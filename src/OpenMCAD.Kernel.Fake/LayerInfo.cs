namespace OpenMCAD.Kernel.Fake;

/// <summary>
/// Marks the <c>OpenMCAD.Kernel.Fake</c> layer.
/// </summary>
/// <remarks>
/// <para>
/// A deterministic mock kernel with simple analytic geometry and a synthetic but
/// realistic history map. A first-class deliverable, not a stub: it is what makes the unit suite
/// run in seconds (ADR-0002).
/// </para>
/// <para>
/// This type exists so the assembly is never empty and so tests/arch has a stable anchor to
/// resolve the layer by. It carries no behaviour. Substantive work on this layer begins at
/// P1-T09.
/// </para>
/// </remarks>
public static class LayerInfo
{
    /// <summary>Gets the short name of this layer, as used in PLAN.md 4.1.</summary>
    public static string Name => "Kernel.Fake";
}
