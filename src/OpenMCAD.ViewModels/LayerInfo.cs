namespace OpenMCAD.ViewModels;

/// <summary>
/// Marks the <c>OpenMCAD.ViewModels</c> layer.
/// </summary>
/// <remarks>
/// <para>
/// The MVVM layer. No System.Windows.* type may ever appear here; the rule is
/// enforced by tests/arch, not by discipline (ADR-0007).
/// </para>
/// <para>
/// This type exists so the assembly is never empty and so tests/arch has a stable anchor to
/// resolve the layer by. It carries no behaviour. Substantive work on this layer begins at
/// P6-T01.
/// </para>
/// </remarks>
public static class LayerInfo
{
    /// <summary>Gets the short name of this layer, as used in PLAN.md 4.1.</summary>
    public static string Name => "ViewModels";
}
