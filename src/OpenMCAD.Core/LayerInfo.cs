namespace OpenMCAD.Core;

/// <summary>
/// Marks the <c>OpenMCAD.Core</c> layer.
/// </summary>
/// <remarks>
/// <para>
/// The parametric core: document graph, rebuild engine, topological naming,
/// expressions and units, transactions, and persistence. Depends on kernel and solver
/// abstractions only, never on an implementation (ADR-0002, ADR-0005, ADR-0011, ADR-0013).
/// </para>
/// <para>
/// This type exists so the assembly is never empty and so tests/arch has a stable anchor to
/// resolve the layer by. It carries no behaviour. Substantive work on this layer begins at
/// P3-T01.
/// </para>
/// </remarks>
public static class LayerInfo
{
    /// <summary>Gets the short name of this layer, as used in PLAN.md 4.1.</summary>
    public static string Name => "Core";
}
