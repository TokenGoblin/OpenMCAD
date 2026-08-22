using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenMCAD.Kernel.Threading;

/// <summary>
/// Records what one dispatched operation cost.
/// </summary>
/// <param name="Operation">The operation name.</param>
/// <param name="Priority">The priority it ran at.</param>
/// <param name="Queued">How long it waited before starting.</param>
/// <param name="Executed">How long it took to run.</param>
/// <param name="Faulted">Whether it threw.</param>
/// <remarks>
/// PLAN.md 5.1 requires the dispatcher to instrument every call. Queue time and execution time are
/// kept apart deliberately: they mean different things and have different fixes. Long execution is
/// a slow operation; long queueing is a starved priority, and the two get confused constantly when
/// only total latency is measured.
/// </remarks>
public readonly record struct KernelCallMetrics(
    string Operation,
    KernelPriority Priority,
    TimeSpan Queued,
    TimeSpan Executed,
    bool Faulted);

/// <summary>
/// The single-threaded actor that owns all kernel state.
/// </summary>
/// <remarks>
/// <para>
/// P1-T08, implementing ADR-0004. Every kernel call is marshalled onto one dedicated thread. The
/// decision buys correctness at the cost of throughput: a class of unreproducible corruption is
/// eliminated by construction rather than by hoping OCCT's undocumented re-entrancy guarantees hold.
/// </para>
/// <para>
/// Rebuild parallelism therefore happens <i>above</i> this: independent branches of the feature DAG
/// prepare concurrently and queue work here, which then executes serially. That is still a large
/// win, because expression evaluation, name resolution, and validation — a substantial share of
/// rebuild time — parallelise freely.
/// </para>
/// <para>
/// <b>Ordering is deterministic.</b> Work is ordered by priority and then by submission sequence,
/// never by thread-arrival races. Two runs that submit the same work in the same order execute it
/// in the same order, which ADR-0011 requires and which a plain concurrent queue would not give.
/// </para>
/// <para>
/// <b>Cancellation happens at operation boundaries.</b> A superseded rebuild stops before its next
/// operation starts; it does not interrupt an operation already inside the kernel. Interrupting
/// OCCT mid-call is not something the library supports, and pretending otherwise would trade a slow
/// cancel for a corrupt one.
/// </para>
/// </remarks>
public sealed class KernelDispatcher : IAsyncDisposable, IDisposable
{
    private readonly PriorityQueue<WorkItem, WorkKey> _queue = new();
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Thread _thread;
    private readonly ILogger _logger;
    private readonly Action<KernelCallMetrics>? _onMetrics;

    private long _sequence;
    private bool _disposed;

    /// <summary>Creates a dispatcher and starts its thread.</summary>
    /// <param name="name">A name for the thread, shown in debuggers and assertion messages.</param>
    /// <param name="logger">Where to log slow and failed operations.</param>
    /// <param name="onMetrics">
    /// Called on the kernel thread after each operation. Keep it cheap: it runs inline and delays
    /// the next operation. Intended for feeding the performance corpus, not for doing work.
    /// </param>
    public KernelDispatcher(
        string name = "OpenMCAD Kernel",
        ILogger<KernelDispatcher>? logger = null,
        Action<KernelCallMetrics>? onMetrics = null)
    {
        _logger = logger ?? NullLogger<KernelDispatcher>.Instance;
        _onMetrics = onMetrics;

        _thread = new Thread(() => Run(name))
        {
            Name = name,
            IsBackground = true,

            // The kernel thread does long numerical work and must not be starved by UI work, but
            // must also not outrank it: raising this above Normal makes a heavy rebuild freeze the
            // interface, which is the exact failure the snapshot rendering design exists to avoid.
            Priority = ThreadPriority.Normal,
        };

        _thread.Start();
    }

    /// <summary>Gets the number of items waiting to run.</summary>
    public int QueueDepth
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>Gets the total number of operations that have completed.</summary>
    public long CompletedCount { get; private set; }

    /// <summary>
    /// Runs <paramref name="work"/> on the kernel thread and returns its result.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="operation">
    /// The operation name, for logging and metrics. Use a stable identifier, not a formatted string.
    /// </param>
    /// <param name="work">
    /// The work to perform. Runs on the kernel thread; must not block on other kernel work.
    /// </param>
    /// <param name="priority">How urgent it is.</param>
    /// <param name="cancellationToken">
    /// Cancels the work if it has not yet started. Once started it runs to completion.
    /// </param>
    /// <exception cref="ObjectDisposedException">The dispatcher has been disposed.</exception>
    /// <exception cref="OperationCanceledException">The work was cancelled before it started.</exception>
    public ValueTask<T> RunAsync<T>(
        string operation,
        Func<T> work,
        KernelPriority priority = KernelPriority.Rebuild,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(work);
        KernelThreadGuard.AssertNotOnKernelThread(operation);

        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        WorkItem item = new(
            operation,
            priority,
            Stopwatch.GetTimestamp(),
            () =>
            {
                try
                {
                    completion.TrySetResult(work());
                    return false;
                }
                catch (OperationCanceledException)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return true;
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                    return true;
                }
            },
            () => completion.TrySetCanceled(cancellationToken),
            cancellationToken);

        Enqueue(item);
        return new ValueTask<T>(completion.Task);
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the kernel thread.
    /// </summary>
    /// <param name="operation">The operation name, for logging and metrics.</param>
    /// <param name="work">The work to perform.</param>
    /// <param name="priority">How urgent it is.</param>
    /// <param name="cancellationToken">Cancels the work if it has not yet started.</param>
    public ValueTask RunAsync(
        string operation,
        Action work,
        KernelPriority priority = KernelPriority.Rebuild,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        return new ValueTask(RunAsync<object?>(
            operation,
            () =>
            {
                work();
                return null;
            },
            priority,
            cancellationToken).AsTask());
    }

    /// <summary>
    /// Queues work whose completion nobody awaits, such as releasing a dropped shape.
    /// </summary>
    /// <param name="operation">The operation name.</param>
    /// <param name="work">The work to perform. Exceptions are logged and swallowed.</param>
    /// <remarks>
    /// Safe to call from a finalizer: it never blocks, never throws, and never allocates a task.
    /// After disposal it silently does nothing, because a shape released during process shutdown
    /// has nowhere to go and the kernel is about to be torn down anyway.
    /// </remarks>
    public void Post(string operation, Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (_disposed)
        {
            return;
        }

        WorkItem item = new(
            operation,
            KernelPriority.Background,
            Stopwatch.GetTimestamp(),
            () =>
            {
                try
                {
                    work();
                    return false;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Posted kernel work {Operation} failed", operation);
                    return true;
                }
            },
            static () => { },
            CancellationToken.None);

        try
        {
            Enqueue(item);
        }
        catch (ObjectDisposedException)
        {
            // Raced with shutdown. Dropping the work is correct: the kernel is going away.
        }
    }

    private void Enqueue(WorkItem item)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _queue.Enqueue(item, new WorkKey(item.Priority, ++_sequence));
        }

        _signal.Release();
    }

    private void Run(string name)
    {
        using KernelThreadGuard.Scope scope = KernelThreadGuard.Enter(name);

        while (true)
        {
            try
            {
                _signal.Wait(_shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                DrainOnShutdown();
                return;
            }

            WorkItem item;
            lock (_gate)
            {
                if (!_queue.TryDequeue(out item!, out _))
                {
                    continue;
                }
            }

            Execute(item);
        }
    }

    private void Execute(WorkItem item)
    {
        long startedAt = Stopwatch.GetTimestamp();
        TimeSpan queued = Stopwatch.GetElapsedTime(item.EnqueuedAt, startedAt);

        // The cancellation boundary. A rebuild superseded while this item sat in the queue stops
        // here, before touching the kernel.
        if (item.CancellationToken.IsCancellationRequested)
        {
            item.Cancel();
            _logger.LogTrace(
                "Kernel operation {Operation} cancelled after {QueuedMs:F1} ms in queue",
                item.Operation,
                queued.TotalMilliseconds);
            return;
        }

        bool faulted = item.Execute();
        TimeSpan executed = Stopwatch.GetElapsedTime(startedAt);
        CompletedCount++;

        if (executed > TimeSpan.FromSeconds(1))
        {
            _logger.LogInformation(
                "Kernel operation {Operation} took {ExecutedMs:F0} ms ({QueuedMs:F0} ms queued)",
                item.Operation,
                executed.TotalMilliseconds,
                queued.TotalMilliseconds);
        }

        if (_onMetrics is not null)
        {
            try
            {
                _onMetrics(new KernelCallMetrics(
                    item.Operation, item.Priority, queued, executed, faulted));
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Kernel metrics callback threw and was ignored");
            }
        }
    }

    private void DrainOnShutdown()
    {
        // Cancel everything still queued so nobody is left awaiting a task that will never
        // complete. A hung await on shutdown looks exactly like a deadlock and wastes a day.
        lock (_gate)
        {
            while (_queue.TryDequeue(out WorkItem? item, out _))
            {
                item.Cancel();
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);

        // The kernel thread is a background thread, so a hang here cannot keep the process alive.
        // Wait anyway, bounded, so an orderly shutdown gets a chance to release native memory.
        if (!_thread.Join(TimeSpan.FromSeconds(5)))
        {
            _logger.LogWarning("Kernel thread did not stop within 5 seconds; abandoning it");
        }

        _shutdown.Dispose();
        _signal.Dispose();
    }

    /// <inheritdoc />
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private readonly record struct WorkKey(KernelPriority Priority, long Sequence)
        : IComparable<WorkKey>
    {
        public int CompareTo(WorkKey other)
        {
            int byPriority = ((int)Priority).CompareTo((int)other.Priority);
            return byPriority != 0 ? byPriority : Sequence.CompareTo(other.Sequence);
        }
    }

    private sealed record WorkItem(
        string Operation,
        KernelPriority Priority,
        long EnqueuedAt,
        Func<bool> Execute,
        Action Cancel,
        CancellationToken CancellationToken);
}
