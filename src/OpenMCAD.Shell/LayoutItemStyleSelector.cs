using System.Windows;
using System.Windows.Controls;
using AvalonDock.Controls;

namespace OpenMCAD.Shell;

/// <summary>
/// Chooses the container style for a docking layout item by its kind.
/// </summary>
/// <remarks>
/// AvalonDock applies <c>LayoutItemContainerStyle</c> to documents and anchorables alike, so a
/// single style targeting one of them throws as soon as the other appears. A selector is the
/// supported way to give each kind its own style, and it is needed from the very first window
/// rather than at P6-T02, because the viewport is a document while the tool panels are
/// anchorables.
/// </remarks>
public sealed class LayoutItemStyleSelector : StyleSelector
{
    /// <summary>Gets or sets the style applied to dockable tool panels.</summary>
    public Style? AnchorableStyle { get; set; }

    /// <summary>Gets or sets the style applied to documents, such as the viewport.</summary>
    public Style? DocumentStyle { get; set; }

    /// <inheritdoc />
    /// <remarks>
    /// AvalonDock passes the layout item as the <paramref name="container"/> and the item content
    /// as <paramref name="item"/>, so the kind must be read from the container. Both are checked
    /// because the split is not documented and has changed between releases.
    /// </remarks>
    public override Style? SelectStyle(object item, DependencyObject container)
    {
        if (container is LayoutAnchorableItem || item is LayoutAnchorableItem)
        {
            return AnchorableStyle;
        }

        if (container is LayoutDocumentItem || item is LayoutDocumentItem)
        {
            return DocumentStyle;
        }

        // Unknown kind: apply no style rather than one that will throw on type mismatch.
        return null;
    }
}
