namespace OpenMCAD.App;

/// <summary>
/// Marks the <c>OpenMCAD.App</c> layer.
/// </summary>
/// <remarks>
/// <para>
/// Application services: the command registry, the undo stack over document parameter
/// state, document sessions, settings, and the AssemblyLoadContext plugin loader
/// (ADR-0011, ADR-0012).
/// </para>
/// <para>
/// This type exists so the assembly is never empty and so tests/arch has a stable anchor to
/// resolve the layer by. It carries no behaviour. Substantive work on this layer begins at
/// P0-T09.
/// </para>
/// </remarks>
public static class LayerInfo
{
    /// <summary>Gets the short name of this layer, as used in PLAN.md 4.1.</summary>
    public static string Name => "App";
}
