namespace OpenMCAD.Kernel.Threading;

/// <summary>
/// How urgent a piece of kernel work is.
/// </summary>
/// <remarks>
/// <para>
/// PLAN.md 5.1 specifies the ordering: interactive beats rebuild beats background. The kernel is a
/// single serial resource (ADR-0004), so this is the only lever available for keeping the
/// application responsive while it is busy.
/// </para>
/// <para>
/// The ordering matters most during a drag. A drag preview issued while a background tessellation
/// of some unrelated body is queued must not wait behind it, or the drag stutters in a way the user
/// reads as the application being slow — even though total throughput is unchanged.
/// </para>
/// <para>
/// Lower values run first.
/// </para>
/// </remarks>
public enum KernelPriority
{
    /// <summary>
    /// Work the user is waiting on right now: drag previews, hover evaluation, measurement.
    /// Budgeted in milliseconds.
    /// </summary>
    Interactive = 0,

    /// <summary>
    /// Feature rebuild. The user is waiting, but is expecting to.
    /// </summary>
    Rebuild = 1,

    /// <summary>
    /// Work nobody is waiting on: tessellation refinement, thumbnails, cache warming, autosave.
    /// </summary>
    Background = 2,
}
