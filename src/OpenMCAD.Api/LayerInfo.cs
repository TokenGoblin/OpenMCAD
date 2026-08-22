namespace OpenMCAD.Api;

/// <summary>
/// Marks the <c>OpenMCAD.Api</c> layer.
/// </summary>
/// <remarks>
/// <para>
/// The public plugin API surface. Semver-governed with a checked-in baseline from
/// Phase 2. Plugins never receive raw kernel handles (ADR-0012).
/// </para>
/// <para>
/// This type exists so the assembly is never empty and so tests/arch has a stable anchor to
/// resolve the layer by. It carries no behaviour. Substantive work on this layer begins at
/// P2-T14.
/// </para>
/// </remarks>
public static class LayerInfo
{
    /// <summary>Gets the short name of this layer, as used in PLAN.md 4.1.</summary>
    public static string Name => "Api";
}
