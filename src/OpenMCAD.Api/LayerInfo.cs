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
/// Internal here, unlike in the other layers. This assembly's public surface is the plugin
/// contract (PLAN.md 5.12), and a marker type that exists only to describe the layer would be a
/// permanent, meaningless member of it -- baselined, versioned, and impossible to remove later
/// without a major version. The assembly is no longer empty in any case. Substantive work on this layer begins at
/// P2-T14.
/// </para>
/// </remarks>
internal static class LayerInfo
{
    /// <summary>Gets the short name of this layer, as used in PLAN.md 4.1.</summary>
    internal static string Name => "Api";
}
