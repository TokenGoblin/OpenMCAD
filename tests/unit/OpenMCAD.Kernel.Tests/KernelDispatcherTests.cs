using System.Collections.Concurrent;
using OpenMCAD.Kernel;
using OpenMCAD.Kernel.Threading;

namespace OpenMCAD.KernelTests;

public sealed class KernelDispatcherTests
{
    private static Task AsTask(ValueTask task) => task.AsTask();

    [Fact]
    public async Task Work_RunsOnTheKernelThread()
    {
        await using KernelDispatcher dispatcher = new("test-kernel");

        int callerThread = Environment.CurrentManagedThreadId;
        (int Thread, bool IsKernel) observed = await dispatcher.RunAsync(
            "probe",
            () => (Environment.CurrentManagedThreadId, KernelThreadGuard.IsKernelThread));

        observed.IsKernel.Should().BeTrue();
        observed.Thread.Should().NotBe(callerThread);
    }

    [Fact]
    public async Task AllWork_RunsOnTheSameThread()
    {
        // ADR-0004's whole point. If this ever produces two thread ids, OCCT state is being
        // touched from more than one thread and the resulting corruption will not reproduce.
        await using KernelDispatcher dispatcher = new("test-kernel");

        ConcurrentBag<int> threads = [];
        await Task.WhenAll(Enumerable.Range(0, 50).Select(async i =>
        {
            int thread = await dispatcher.RunAsync($"probe{i}", () => Environment.CurrentManagedThreadId);
            threads.Add(thread);
        }));

        threads.Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task HigherPriorityWork_OvertakesQueuedWork()
    {
        await using KernelDispatcher dispatcher = new("test-kernel");

        List<string> order = [];
        using ManualResetEventSlim gate = new(false);

        // Occupy the thread so everything after this queues up behind it.
        Task blocker = AsTask(dispatcher.RunAsync("blocker", () => { _ = gate.Wait(TimeSpan.FromSeconds(10)); }));

        // Give the blocker time to be dequeued and start running.
        while (dispatcher.CompletedCount == 0 && dispatcher.QueueDepth > 0)
        {
            await Task.Delay(1);
        }

        await Task.Delay(20);

        Task background = AsTask(dispatcher.RunAsync(
            "background", () => order.Add("background"), KernelPriority.Background));
        Task rebuild = AsTask(dispatcher.RunAsync(
            "rebuild", () => order.Add("rebuild"), KernelPriority.Rebuild));
        Task interactive = AsTask(dispatcher.RunAsync(
            "interactive", () => order.Add("interactive"), KernelPriority.Interactive));

        gate.Set();
        await blocker;
        await background;
        await rebuild;
        await interactive;

        // Submitted background, rebuild, interactive; must run in priority order regardless.
        order.Should().Equal("interactive", "rebuild", "background");
    }

    [Fact]
    public async Task EqualPriorityWork_RunsInSubmissionOrder()
    {
        // Determinism (ADR-0011) requires a total order, not just a priority order. Two runs that
        // submit the same work must execute it identically.
        await using KernelDispatcher dispatcher = new("test-kernel");

        List<int> order = [];
        using ManualResetEventSlim gate = new(false);

        Task blocker = AsTask(dispatcher.RunAsync("blocker", () => { _ = gate.Wait(TimeSpan.FromSeconds(10)); }));
        await Task.Delay(20);

        List<Task> queued = [];
        for (int i = 0; i < 20; i++)
        {
            int captured = i;
            queued.Add(AsTask(dispatcher.RunAsync($"work{i}", () => order.Add(captured))));
        }

        gate.Set();
        await blocker;
        await Task.WhenAll(queued);

        order.Should().Equal(Enumerable.Range(0, 20));
    }

    [Fact]
    public async Task QueuedWork_CancelsBeforeItStarts()
    {
        await using KernelDispatcher dispatcher = new("test-kernel");

        bool ran = false;
        using ManualResetEventSlim gate = new(false);
        using CancellationTokenSource cancellation = new();

        Task blocker = AsTask(dispatcher.RunAsync("blocker", () => { _ = gate.Wait(TimeSpan.FromSeconds(10)); }));
        await Task.Delay(20);

        Task<int> cancelled = dispatcher.RunAsync(
            "cancelled",
            () =>
            {
                ran = true;
                return 1;
            },
            KernelPriority.Rebuild,
            cancellation.Token).AsTask();

        await cancellation.CancelAsync();
        gate.Set();
        await blocker;

        Func<Task> act = async () => await cancelled;
        await act.Should().ThrowAsync<OperationCanceledException>();

        ran.Should().BeFalse("cancellation happens at the operation boundary, before work starts");
    }

    [Fact]
    public async Task WorkAlreadyRunning_IsNotInterrupted()
    {
        // The other half of the contract: a superseded rebuild stops between operations, it does
        // not tear one out of the kernel partway through.
        await using KernelDispatcher dispatcher = new("test-kernel");
        using CancellationTokenSource cancellation = new();

        bool completed = false;
        Task<int> running = dispatcher.RunAsync(
            "long",
            () =>
            {
                Thread.Sleep(100);
                completed = true;
                return 42;
            },
            KernelPriority.Rebuild,
            cancellation.Token).AsTask();

        await Task.Delay(20);
        await cancellation.CancelAsync();

        (await running).Should().Be(42);
        completed.Should().BeTrue();
    }

    [Fact]
    public async Task ExceptionsPropagateToTheCaller()
    {
        await using KernelDispatcher dispatcher = new("test-kernel");

        Func<Task> act = async () => await dispatcher.RunAsync<int>(
            "throws", () => throw new InvalidOperationException("boom"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }

    [Fact]
    public async Task ExceptionInOneOperation_DoesNotStopTheDispatcher()
    {
        await using KernelDispatcher dispatcher = new("test-kernel");

        try
        {
            await dispatcher.RunAsync<int>("throws", () => throw new InvalidOperationException());
        }
        catch (InvalidOperationException)
        {
            // Expected.
        }

        (await dispatcher.RunAsync("after", () => 7)).Should().Be(7);
    }

    [Fact]
    public async Task Metrics_SeparateQueueTimeFromExecutionTime()
    {
        List<KernelCallMetrics> metrics = [];
        await using KernelDispatcher dispatcher = new(
            "test-kernel", onMetrics: m => metrics.Add(m));

        await dispatcher.RunAsync("measured", () => Thread.Sleep(30));

        metrics.Should().ContainSingle();
        metrics[0].Operation.Should().Be("measured");
        metrics[0].Executed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(20));
        metrics[0].Faulted.Should().BeFalse();
    }

    [Fact]
    public async Task Post_NeverThrows_EvenAfterDisposal()
    {
        // Post is called from SafeHandle.ReleaseHandle on the finalizer thread. An exception there
        // terminates the process, so it must swallow everything including a disposal race.
        KernelDispatcher dispatcher = new("test-kernel");
        await dispatcher.DisposeAsync();

        Action act = () => dispatcher.Post("release", () => throw new InvalidOperationException());
        act.Should().NotThrow();
    }

    [Fact]
    public async Task Disposal_CancelsPendingWorkRatherThanLeavingItHanging()
    {
        KernelDispatcher dispatcher = new("test-kernel");

        using ManualResetEventSlim occupied = new(false);
        using ManualResetEventSlim release = new(false);

        Task blocker = AsTask(dispatcher.RunAsync("blocker", () =>
        {
            occupied.Set();
            _ = release.Wait(TimeSpan.FromSeconds(5));
        }));

        // Wait for the blocker to actually be running rather than sleeping and hoping. A fixed
        // delay here was a guess about scheduling, and on a loaded machine it is the wrong guess.
        occupied.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the kernel thread should have started the blocker");

        Task<int> pending = dispatcher.RunAsync("pending", () => 1).AsTask();

        // Shutdown must be requested before the kernel thread can reach "pending", or the thread
        // completes it and there is nothing to cancel. That ordering used to be left to luck --
        // the blocker was released first and the test passed only because shutdown usually won --
        // and it cannot be arranged from outside, because DisposeAsync requests cancellation and
        // then blocks in a join with no observable moment between the two. So disposal runs on
        // another thread and the blocker is held until the flag actually flips.
        Task disposal = Task.Run(async () => await dispatcher.DisposeAsync());

        SpinWait.SpinUntil(() => dispatcher.IsShuttingDown, TimeSpan.FromSeconds(5))
            .Should().BeTrue("disposal should have requested cancellation");

        release.Set();
        await disposal;

        // An await that never completes on shutdown looks exactly like a deadlock.
        Func<Task> act = async () => await pending;
        await act.Should().ThrowAsync<OperationCanceledException>();

        await blocker;
    }

    [Fact]
    public async Task QueueingFromTheKernelThread_IsRejectedInDebugBuilds()
    {
        await using KernelDispatcher dispatcher = new("test-kernel");

        // Re-entrant queueing on a single-threaded actor deadlocks: the inner item cannot start
        // until the outer one finishes. The guard turns that into an immediate, legible failure.
        Exception? captured = null;
        await dispatcher.RunAsync("outer", () =>
        {
            try
            {
                KernelThreadGuard.AssertNotOnKernelThread("inner");
            }
            catch (InvalidOperationException exception)
            {
                captured = exception;
            }
        });

#if DEBUG
        captured.Should().NotBeNull();
        captured!.Message.Should().Contain("deadlock");
#else
        captured.Should().BeNull("the guard is compiled out of release builds");
#endif
    }
}

public sealed class KernelThreadGuardTests
{
    [Fact]
    public void AssertOnKernelThread_ThrowsOffThreadInDebugBuilds()
    {
        Action act = () => KernelThreadGuard.AssertOnKernelThread("Fillet");

#if DEBUG
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not a kernel thread*");
#else
        act.Should().NotThrow();
#endif
    }

    [Fact]
    public void OrdinaryThreads_AreNotKernelThreads()
    {
        KernelThreadGuard.IsKernelThread.Should().BeFalse();
    }
}

public sealed class KernelShapeHandleTests
{
    private sealed class RecordingReleaser : IKernelShapeReleaser
    {
        public List<KernelShape> Released { get; } = [];

        public void EnqueueRelease(KernelShape shape) => Released.Add(shape);
    }

    [Fact]
    public void Dispose_ReleasesExactlyOnce()
    {
        RecordingReleaser releaser = new();
        KernelShapeHandle handle = new(new KernelShape(0x1234), releaser);

        handle.Dispose();
        handle.Dispose();

        releaser.Released.Should().ContainSingle();
        releaser.Released[0].Tag.Should().Be(0x1234UL);
    }

    [Fact]
    public void Tag_SurvivesTheFullUnsignedRange()
    {
        // The generation counter lives in the high bits, so a valid tag can have its top bit set.
        // A checked conversion would throw on it; this pins the reinterpretation.
        RecordingReleaser releaser = new();
        const ulong HighTag = 0xFFFF_FFFF_FFFF_FFF0UL;

        using KernelShapeHandle handle = new(new KernelShape(HighTag), releaser);

        handle.Shape.Tag.Should().Be(HighTag);
    }

    [Fact]
    public void AccessingAReleasedHandle_Throws()
    {
        RecordingReleaser releaser = new();
        KernelShapeHandle handle = new(new KernelShape(1), releaser);
        handle.Dispose();

        Action act = () => _ = handle.Shape;
        act.Should().Throw<ObjectDisposedException>();

        // The non-throwing accessor exists for logging on a possibly-dead handle.
        handle.ShapeOrNone.Should().Be(KernelShape.None);
    }

    [Fact]
    public void AnInvalidShape_CannotBeOwned()
    {
        RecordingReleaser releaser = new();

        Action act = () => _ = new KernelShapeHandle(KernelShape.None, releaser);
        act.Should().Throw<ArgumentException>();
    }
}
