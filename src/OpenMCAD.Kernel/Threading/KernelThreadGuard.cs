using System.Diagnostics;

namespace OpenMCAD.Kernel.Threading;

/// <summary>
/// Marks the kernel thread and asserts that kernel work happens only there.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0004: OCCT is not thread-safe, so every kernel call is marshalled onto one dedicated thread.
/// This type is how that rule is checked rather than merely intended. PLAN.md 12 lists "calling the
/// kernel off the dispatcher thread" first among the things that are always wrong.
/// </para>
/// <para>
/// The marker is thread-static rather than a comparison against a single known thread, so the
/// Phase 15 worker pool (P15-T03) — several kernel threads, each owning an isolated shape universe
/// — needs no change here.
/// </para>
/// <para>
/// The assertion is debug-only. A release build cannot afford a check on every P/Invoke, and by
/// then the violation would have been caught: the whole point of asserting is that the failure is
/// loud in development instead of being a heisenbug in production.
/// </para>
/// </remarks>
public static class KernelThreadGuard
{
    [ThreadStatic]
    private static bool _isKernelThread;

    [ThreadStatic]
    private static string? _threadName;

    /// <summary>Gets a value indicating whether the calling thread is a kernel thread.</summary>
    public static bool IsKernelThread => _isKernelThread;

    /// <summary>
    /// Marks the calling thread as a kernel thread. Called by the dispatcher on its own thread.
    /// </summary>
    /// <param name="name">A name for the thread, used in assertion messages.</param>
    /// <returns>A scope that unmarks the thread when disposed.</returns>
    public static Scope Enter(string name)
    {
        if (_isKernelThread)
        {
            throw new InvalidOperationException(
                $"Thread '{_threadName}' is already marked as a kernel thread. Nesting kernel "
                + "threads means two dispatchers believe they own the same OCCT state.");
        }

        _isKernelThread = true;
        _threadName = name;
        return default;
    }

    /// <summary>
    /// Throws if the calling thread is not a kernel thread. Compiled out of release builds.
    /// </summary>
    /// <param name="operation">The operation being attempted, for the message.</param>
    /// <exception cref="InvalidOperationException">The caller is not on a kernel thread.</exception>
    [Conditional("DEBUG")]
    public static void AssertOnKernelThread(string operation)
    {
        if (!_isKernelThread)
        {
            throw new InvalidOperationException(
                $"'{operation}' was called on thread "
                + $"{Environment.CurrentManagedThreadId} ('{Thread.CurrentThread.Name ?? "unnamed"}'), "
                + "which is not a kernel thread. Every kernel call must be marshalled through "
                + "KernelDispatcher (ADR-0004); OCCT is not thread-safe and the resulting "
                + "corruption is not reproducible.");
        }
    }

    /// <summary>
    /// Throws if the calling thread <i>is</i> a kernel thread. Compiled out of release builds.
    /// </summary>
    /// <param name="operation">The operation being attempted, for the message.</param>
    /// <exception cref="InvalidOperationException">The caller is on a kernel thread.</exception>
    /// <remarks>
    /// The other direction, and a real hazard: awaiting a kernel operation from inside a kernel
    /// operation deadlocks, because the work being awaited is queued behind the work doing the
    /// awaiting on a single-threaded actor.
    /// </remarks>
    [Conditional("DEBUG")]
    public static void AssertNotOnKernelThread(string operation)
    {
        if (_isKernelThread)
        {
            throw new InvalidOperationException(
                $"'{operation}' was called from the kernel thread. Queueing kernel work from "
                + "inside kernel work deadlocks: the dispatcher is single-threaded, so the "
                + "awaited item cannot start until the awaiting item finishes.");
        }
    }

    /// <summary>Unmarks a kernel thread when disposed.</summary>
    public readonly struct Scope : IDisposable
    {
        /// <summary>Unmarks the calling thread.</summary>
        public void Dispose()
        {
            _isKernelThread = false;
            _threadName = null;
        }
    }
}
