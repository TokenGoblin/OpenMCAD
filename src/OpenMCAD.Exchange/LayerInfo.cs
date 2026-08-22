namespace OpenMCAD.Exchange;

/// <summary>
/// Marks the <c>OpenMCAD.Exchange</c> layer.
/// </summary>
/// <remarks>
/// <para>
/// Import and export: STEP AP242, IGES, DXF/DWG, 3MF/STL, glTF, and PMI where the
/// format supports it. Every importer is an attack surface and is fuzzed as untrusted input.
/// </para>
/// <para>
/// This type exists so the assembly is never empty and so tests/arch has a stable anchor to
/// resolve the layer by. It carries no behaviour. Substantive work on this layer begins at
/// P5-T10.
/// </para>
/// </remarks>
public static class LayerInfo
{
    /// <summary>Gets the short name of this layer, as used in PLAN.md 4.1.</summary>
    public static string Name => "Exchange";
}
