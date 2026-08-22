using OpenMCAD.Kernel.Threading;
using OpenMCAD.Math;

namespace OpenMCAD.Kernel;

/// <summary>
/// The per-call options that apply to every kernel operation.
/// </summary>
/// <param name="Priority">How urgent this call is.</param>
/// <param name="LinearTolerance">
/// Model tolerance in metres, or <see langword="null"/> for <see cref="Tolerance.Linear"/>. This is
/// the tolerance rung 1 of the retry ladder uses; the ladder relaxes it on its own if it must.
/// </param>
/// <param name="CorrelationId">
/// Ties this call to the feature being rebuilt. Flows into logs, metrics, and repro bundles so a
/// kernel failure can be traced back to the feature that caused it without guesswork.
/// </param>
/// <remarks>
/// <para>
/// One options record threaded through every call, rather than a growing tail of optional
/// parameters on twenty methods. Adding an option later then costs one field instead of twenty
/// signature changes and a compatibility break in <c>OpenMCAD.Api</c>.
/// </para>
/// <para>
/// The default is a rebuild-priority call at model tolerance, which is what the overwhelming
/// majority of calls want.
/// </para>
/// </remarks>
public readonly record struct KernelRequest(
    KernelPriority Priority = KernelPriority.Rebuild,
    double? LinearTolerance = null,
    string? CorrelationId = null)
{
    /// <summary>Gets options for work the user is waiting on right now, such as a drag preview.</summary>
    public static KernelRequest Interactive => new(KernelPriority.Interactive);

    /// <summary>Gets options for feature rebuild. The default.</summary>
    public static KernelRequest Rebuild => new(KernelPriority.Rebuild);

    /// <summary>Gets options for work nobody is waiting on.</summary>
    public static KernelRequest Background => new(KernelPriority.Background);

    /// <summary>Gets the tolerance this call should use.</summary>
    public double EffectiveTolerance => LinearTolerance ?? Tolerance.Linear;

    /// <summary>Returns these options with a correlation identifier attached.</summary>
    /// <param name="correlationId">The identifier, usually a feature id.</param>
    public KernelRequest For(string correlationId) => this with { CorrelationId = correlationId };
}
