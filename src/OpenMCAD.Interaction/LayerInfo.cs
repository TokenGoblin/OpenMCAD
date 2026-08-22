namespace OpenMCAD.Interaction;

/// <summary>
/// Marks the <c>OpenMCAD.Interaction</c> layer.
/// </summary>
/// <remarks>
/// <para>
/// Tool and gesture state machines, selection, manipulators, snapping, and
/// constraint inference. UI-framework-agnostic.
/// </para>
/// <para>
/// This type exists so the assembly is never empty and so tests/arch has a stable anchor to
/// resolve the layer by. It carries no behaviour. Substantive work on this layer begins at
/// P2-T09.
/// </para>
/// </remarks>
public static class LayerInfo
{
    /// <summary>Gets the short name of this layer, as used in PLAN.md 4.1.</summary>
    public static string Name => "Interaction";
}
