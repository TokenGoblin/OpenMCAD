namespace OpenMCAD.Modeling;

/// <summary>
/// Marks the <c>OpenMCAD.Modeling</c> layer.
/// </summary>
/// <remarks>
/// <para>
/// Feature semantics: parts, sketches, assemblies, drawings, sheet metal, surfacing.
/// Every feature must be creatable, rebuildable, serialisable and validatable headlessly.
/// </para>
/// <para>
/// This type exists so the assembly is never empty and so tests/arch has a stable anchor to
/// resolve the layer by. It carries no behaviour. Substantive work on this layer begins at
/// P5-T01.
/// </para>
/// </remarks>
public static class LayerInfo
{
    /// <summary>Gets the short name of this layer, as used in PLAN.md 4.1.</summary>
    public static string Name => "Modeling";
}
