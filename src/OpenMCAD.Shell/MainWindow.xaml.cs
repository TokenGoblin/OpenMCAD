using Fluent;

namespace OpenMCAD.Shell;

/// <summary>
/// The main application window.
/// </summary>
/// <remarks>
/// P0-T10. The code-behind is empty on purpose and should stay that way: everything the window
/// shows comes from <c>OpenMCAD.ViewModels.MainWindowViewModel</c> through binding. Logic that
/// creeps into code-behind is logic that cannot be unit-tested and cannot move to another UI
/// framework, which is the failure mode ADR-0007 exists to prevent.
/// </remarks>
public partial class MainWindow : RibbonWindow
{
    /// <summary>Initialises the window.</summary>
    public MainWindow() => InitializeComponent();
}
