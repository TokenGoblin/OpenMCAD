using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Interaction.Selection;
using OpenMCAD.Kernel;
using OpenMCAD.Math;
using OpenMCAD.Render;

using Xunit;

namespace OpenMCAD.Interaction.Tests;

/// <summary>
/// Selection sets and how they project onto display ids (P2-T09).
/// </summary>
public sealed class SelectionSetTests
{
    private static SubEntity Face(ulong tag) => new(new KernelShape(1), tag, SubEntityKind.Face);

    private static SubEntity Edge(ulong tag) => new(new KernelShape(1), tag, SubEntityKind.Edge);

    /// <summary>A snapshot that knows three faces, with ids 1, 2 and 3.</summary>
    private static DisplaySnapshot Snapshot(long version = 1)
    {
        ImmutableDictionary<DisplayId, SubEntity> entities =
            ImmutableDictionary<DisplayId, SubEntity>.Empty
                .Add(new DisplayId(1), Face(10))
                .Add(new DisplayId(2), Face(20))
                .Add(new DisplayId(3), Edge(30));

        return new DisplaySnapshot(version, Vec3d.Zero, [], entities, default);
    }

    // --- The set ------------------------------------------------------------------------------

    [Fact]
    public void ReplaceSelectsOneThingAndDropsTheRest()
    {
        SelectionSet selection = new();

        selection.Apply(Face(10), SelectionAction.Add);
        selection.Apply(Face(20), SelectionAction.Add);
        selection.Count.Should().Be(2);

        selection.Apply(Face(10), SelectionAction.Replace);

        selection.Count.Should().Be(1);
        selection.Contains(Face(10)).Should().BeTrue();
    }

    [Fact]
    public void ToggleAddsThenRemoves()
    {
        SelectionSet selection = new();

        selection.Apply(Face(10), SelectionAction.Toggle).Should().BeTrue();
        selection.Contains(Face(10)).Should().BeTrue();

        selection.Apply(Face(10), SelectionAction.Toggle).Should().BeTrue();
        selection.Contains(Face(10)).Should().BeFalse();
    }

    [Fact]
    public void ClickingEmptySpaceClearsButOnlyWhenReplacing()
    {
        // A click on the background clears, as every application does. A mis-aimed Control-click
        // must not throw away a selection that took a while to build.
        SelectionSet selection = new();
        selection.Apply(Face(10), SelectionAction.Add);

        selection.Apply(SubEntity.None, SelectionAction.Add).Should().BeFalse();
        selection.Count.Should().Be(1);

        selection.Apply(SubEntity.None, SelectionAction.Replace).Should().BeTrue();
        selection.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void ReselectingTheSameSingleThingIsNotAChange()
    {
        // Otherwise the version would move on every click and the renderer would re-upload the
        // highlight table for a selection that did not change.
        SelectionSet selection = new();
        selection.Apply(Face(10), SelectionAction.Replace);

        long version = selection.Version;

        selection.Apply(Face(10), SelectionAction.Replace).Should().BeFalse();
        selection.Version.Should().Be(version);
    }

    [Fact]
    public void HoverDoesNotDisturbTheSelection()
    {
        // Merging pre-selection into selection is a common shortcut, and it destroys the user's
        // selection every time the mouse crosses the model on the way to a menu.
        SelectionSet selection = new();
        selection.Apply(Face(10), SelectionAction.Replace);

        selection.SetPreSelected(Face(20));

        selection.Contains(Face(10)).Should().BeTrue();
        selection.Contains(Face(20)).Should().BeFalse();
        selection.PreSelected.Should().Be(Face(20));
    }

    [Fact]
    public void HoverMovingChangesTheVersionSoTheViewRedraws()
    {
        SelectionSet selection = new();
        long before = selection.Version;

        selection.SetPreSelected(Face(10)).Should().BeTrue();
        selection.Version.Should().NotBe(before);

        // Staying on the same entity is not a change, so a mouse moving across one large face
        // does not re-upload anything.
        long settled = selection.Version;
        selection.SetPreSelected(Face(10)).Should().BeFalse();
        selection.Version.Should().Be(settled);
    }

    [Fact]
    public void ClearingTheSelectionLeavesErrorsAlone()
    {
        // A user investigating a failure clicks around. Having the error markers vanish as they do
        // is exactly backwards.
        SelectionSet selection = new();
        selection.SetFaulted([Face(20)]);
        selection.Apply(Face(10), SelectionAction.Replace);

        selection.Clear();

        selection.IsEmpty.Should().BeTrue();
        selection.Faulted.Should().Contain(Face(20));
    }

    // --- Projection onto display ids ----------------------------------------------------------

    [Fact]
    public void SelectedEntitiesBecomeSelectedIds()
    {
        SelectionSet selection = new();
        selection.Apply(Face(20), SelectionAction.Replace);

        HighlightTable table = selection.ToHighlights(Snapshot());

        table[new DisplayId(2)].Should().Be(HighlightState.Selected);
        table[new DisplayId(1)].Should().Be(HighlightState.None);
    }

    [Fact]
    public void ErrorOutranksSelection()
    {
        // A user being told the face they have selected is why their model will not rebuild needs
        // to see the error, not the selection.
        SelectionSet selection = new();
        selection.Apply(Face(10), SelectionAction.Replace);
        selection.SetFaulted([Face(10)]);

        selection.ToHighlights(Snapshot())[new DisplayId(1)]
            .Should().Be(HighlightState.Error);
    }

    [Fact]
    public void SelectionOutranksHover()
    {
        SelectionSet selection = new();
        selection.Apply(Face(10), SelectionAction.Replace);
        selection.SetPreSelected(Face(10));

        selection.ToHighlights(Snapshot())[new DisplayId(1)]
            .Should().Be(HighlightState.Selected);
    }

    [Fact]
    public void NothingSelectedProducesTheEmptyTable()
    {
        // The empty table is what tells the renderer to bind nothing, so the shader takes its
        // cheap path and every entity reads as unhighlighted.
        new SelectionSet().ToHighlights(Snapshot()).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void TheVersionChangesWhenTheSnapshotDoesEvenIfTheSelectionDidNot()
    {
        // Ids are snapshot-scoped. The same selection maps to different numbers after a rebuild,
        // so a table built from it is genuinely different even though nothing was clicked.
        SelectionSet selection = new();
        selection.Apply(Face(10), SelectionAction.Replace);

        long first = selection.ToHighlights(Snapshot(version: 1)).Version;
        long second = selection.ToHighlights(Snapshot(version: 2)).Version;

        second.Should().NotBe(first);
    }

    [Fact]
    public void AnEntityTheSnapshotDoesNotContainIsSimplyNotHighlighted()
    {
        // Selection outlives the snapshot it was made against, so this is routine rather than
        // exceptional -- and it must not throw on the render path.
        SelectionSet selection = new();
        selection.Apply(Face(999), SelectionAction.Replace);

        HighlightTable table = selection.ToHighlights(Snapshot());

        table.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void EdgesHighlightJustAsFacesDo()
    {
        SelectionSet selection = new();
        selection.Apply(Edge(30), SelectionAction.Replace);

        selection.ToHighlights(Snapshot())[new DisplayId(3)]
            .Should().Be(HighlightState.Selected);
    }

    // --- The table ----------------------------------------------------------------------------

    [Fact]
    public void TheStrongerStateWinsWhateverOrderItArrivesIn()
    {
        // Depending on enumeration order here would make a face flicker between two colours as an
        // unordered set was iterated.
        HighlightTable ascending = HighlightTable.Build(
            [
                new(new DisplayId(1), HighlightState.PreSelected),
                new(new DisplayId(1), HighlightState.Selected),
            ],
            1);

        HighlightTable descending = HighlightTable.Build(
            [
                new(new DisplayId(1), HighlightState.Selected),
                new(new DisplayId(1), HighlightState.PreSelected),
            ],
            1);

        ascending[new DisplayId(1)].Should().Be(HighlightState.Selected);
        descending[new DisplayId(1)].Should().Be(HighlightState.Selected);
    }

    [Fact]
    public void AnIdBeyondTheTableReadsAsUnhighlighted()
    {
        HighlightTable table = HighlightTable.Build(
            [new(new DisplayId(2), HighlightState.Selected)], 1);

        table[new DisplayId(9999)].Should().Be(HighlightState.None);
    }
}
