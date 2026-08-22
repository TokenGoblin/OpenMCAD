namespace OpenMCAD.Render;

/// <summary>
/// Marks the <c>OpenMCAD.Render</c> layer.
/// </summary>
/// <remarks>
/// <para>
/// The D3D12 renderer via Vortice: scene graph, the integer ID pass that makes picking
/// pixel-exact, and the immutable DisplaySnapshot the render thread swaps to (ADR-0008).
/// </para>
/// <para>
/// This type exists so the assembly is never empty and so tests/arch has a stable anchor to
/// resolve the layer by. It carries no behaviour. Substantive work on this layer begins at
/// P2-T01.
/// </para>
/// </remarks>
public static class LayerInfo
{
    /// <summary>Gets the short name of this layer, as used in PLAN.md 4.1.</summary>
    public static string Name => "Render";
}
