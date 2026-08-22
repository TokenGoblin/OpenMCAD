namespace OpenMCAD.Solver.Planegcs;

/// <summary>
/// Marks the <c>OpenMCAD.Solver.Planegcs</c> layer.
/// </summary>
/// <remarks>
/// <para>
/// LibraryImport bindings over openmcad_gcs.dll and the planegcs-backed
/// ISketchSolver implementation (ADR-0006).
/// </para>
/// <para>
/// This type exists so the assembly is never empty and so tests/arch has a stable anchor to
/// resolve the layer by. It carries no behaviour. Substantive work on this layer begins at
/// P4-T01.
/// </para>
/// </remarks>
public static class LayerInfo
{
    /// <summary>Gets the short name of this layer, as used in PLAN.md 4.1.</summary>
    public static string Name => "Solver.Planegcs";
}
