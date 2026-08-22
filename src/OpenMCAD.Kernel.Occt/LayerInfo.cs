namespace OpenMCAD.Kernel.Occt;

/// <summary>
/// Marks the <c>OpenMCAD.Kernel.Occt</c> layer.
/// </summary>
/// <remarks>
/// <para>
/// LibraryImport bindings over the C ABI shim openmcad_occt.dll, plus the
/// IGeometryKernel implementation. The ONLY assembly permitted to know OCCT exists (ADR-0003).
/// </para>
/// <para>
/// This type exists so the assembly is never empty and so tests/arch has a stable anchor to
/// resolve the layer by. It carries no behaviour. Substantive work on this layer begins at
/// P1-T03.
/// </para>
/// </remarks>
public static class LayerInfo
{
    /// <summary>Gets the short name of this layer, as used in PLAN.md 4.1.</summary>
    public static string Name => "Kernel.Occt";
}
