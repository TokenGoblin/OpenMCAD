using System.Windows;

using Fluent;

using OpenMCAD.ViewModels;

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

    /// <summary>Runs a plugin's command when its ribbon button is pressed.</summary>
    /// <remarks>
    /// The one thing in this code-behind, and it is here because a <c>Click</c> handler is the
    /// only way to reach a <c>DataTemplate</c>'s data item without introducing a command
    /// abstraction that would put an <c>ICommand</c> — and therefore <c>System.Windows.Input</c> —
    /// into the view models, which ADR-0007 forbids and <c>tests/arch</c> enforces.
    ///
    /// It contains no logic: it finds the item and invokes it. The error handling that matters
    /// lives around the delegate itself, in App, where a throwing plugin can be named.
    /// </remarks>
    private void OnPluginCommandClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PluginCommandItem item })
        {
            item.Invoke();
        }
    }
}
