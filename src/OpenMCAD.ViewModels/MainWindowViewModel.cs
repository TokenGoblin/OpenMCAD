using System.Collections.ObjectModel;
using OpenMCAD.App;

namespace OpenMCAD.ViewModels;

/// <summary>
/// A dockable panel in the shell.
/// </summary>
/// <param name="ContentId">
/// A stable identifier used to persist and restore the docking layout. It must never change once
/// shipped, or saved layouts stop resolving.
/// </param>
/// <param name="Title">The panel title shown on its tab.</param>
public sealed record ToolPanel(string ContentId, string Title)
{
    /// <summary>Gets or sets placeholder body text shown until the panel is implemented.</summary>
    /// <remarks>Replaced by real content in Phase 6; see the task noted on each panel.</remarks>
    public string Placeholder { get; init; } = string.Empty;
}

/// <summary>
/// The root view model for the main window.
/// </summary>
/// <remarks>
/// <para>
/// P0-T10. Deliberately contains no <c>System.Windows</c> type of any kind. ADR-0007 makes that a
/// hard rule and <c>tests/arch</c> enforces it, because it is the only thing that turns a future
/// WPF replacement into a reskin rather than a rewrite. The rule is easy to keep now and
/// impossible to adopt at 500k lines, which is exactly why it starts in Phase 0.
/// </para>
/// <para>
/// The panel set here is the Phase 6 docking layout (P6-T02) stubbed out, so the layout, its
/// persistence identifiers, and the window chrome are exercised from the first commit.
/// </para>
/// </remarks>
public sealed class MainWindowViewModel : ObservableObject
{
    private string _statusText = "Ready";
    private string _documentName = string.Empty;

    /// <summary>Initialises the view model with its default panel set.</summary>
    public MainWindowViewModel()
    {
        Panels =
        [
            new ToolPanel("FeatureTree", "Feature Tree")
            {
                Placeholder = "The feature tree lands in P6-T03: virtualised, drag-to-reorder "
                    + "with dependency validation, rollback bar, and error badges.",
            },
            new ToolPanel("PropertyManager", "Property Manager")
            {
                Placeholder = "The property manager lands in P6-T04, generated from FeatureSchema "
                    + "(P3-T21) so a feature is one class and one schema, not seven files.",
            },
            new ToolPanel("Output", "Output")
            {
                Placeholder = "The rebuild report lands in P3-T07. Rebuild failures are reported "
                    + "here, never in a modal dialog.",
            },
        ];
    }

    /// <summary>
    /// Gets or sets the name of the active document, shown in the title bar.
    /// </summary>
    /// <remarks>Empty until document sessions land in P5-T12.</remarks>
    public string DocumentName
    {
        get => _documentName;
        set
        {
            if (SetProperty(ref _documentName, value))
            {
                OnPropertyChanged(nameof(Title));
            }
        }
    }

    /// <summary>Gets the window title, which follows the active document.</summary>
    public string Title => string.IsNullOrEmpty(_documentName)
        ? $"{AppInfo.ProductName} {AppInfo.Version}"
        : $"{_documentName} — {AppInfo.ProductName} {AppInfo.Version}";

    /// <summary>Gets the dockable panels shown around the viewport.</summary>
    public ObservableCollection<ToolPanel> Panels { get; }

    /// <summary>Gets or sets the status bar text.</summary>
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>
    /// Gets or sets the message shown in the viewport until the D3D12 renderer replaces it.
    /// </summary>
    /// <remarks>
    /// Also the surface for reporting a device-lost or unsupported-hardware condition once the
    /// renderer exists (P2-T02), so it stays settable rather than becoming a constant.
    /// </remarks>
    public string ViewportPlaceholder { get; set; } =
        "Viewport — Direct3D 12 via Vortice, hosted in an HwndHost (ADR-0008), lands in P2-T01/P2-T02.";
}
