namespace OpenMCAD.Kernel;

/// <summary>
/// Marks the <c>OpenMCAD.Kernel</c> layer.
/// </summary>
/// <remarks>
/// <para>
/// The geometry-kernel abstraction: IGeometryKernel, KernelShape, HistoryMap,
/// OperationResult, and the single-threaded KernelDispatcher (ADR-0002, ADR-0004).
/// Knows nothing about OCCT.
/// </para>
/// <para>
/// This type exists so the assembly is never empty and so tests/arch has a stable anchor to
/// resolve the layer by. It carries no behaviour. Substantive work on this layer begins at
/// P1-T01.
/// </para>
/// </remarks>
public static class LayerInfo
{
    /// <summary>Gets the short name of this layer, as used in PLAN.md 4.1.</summary>
    public static string Name => "Kernel";
}
