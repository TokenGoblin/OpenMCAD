using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;
using OpenMCAD.Interaction.Selection;
using OpenMCAD.Kernel;

using Xunit;

namespace OpenMCAD.Interaction.Tests;

/// <summary>
/// Carrying a selection through a rebuild (P2-T09, §5.3).
/// </summary>
/// <remarks>
/// P2-T09 could only promise that a selection survived camera movement and re-picking; surviving a
/// modelling operation needed naming that did not exist. It does now, and these are the tests for
/// the boundary where the two meet.
/// </remarks>
public sealed class SelectionAcrossRebuildTests
{
    [Fact]
    public void ASelectionSurvivesARebuildThatRenumberedEverything()
    {
        // The promise P2-T09 could not make. Every tag differs between the two rebuilds, and the
        // selection still points at the same wall.
        Scene before = new();
        SelectionSet selection = new();

        selection.Apply(before.Wall, SelectionAction.Add).Should().BeTrue();

        var remembered = SelectionAcrossRebuild.Remember(selection, before.History, FeatureId.None);

        Scene after = new(before, tagOffset: 100);

        SelectionCarry carried =
            SelectionAcrossRebuild.Restore(selection, remembered, after.History, FeatureId.None);

        carried.Should().Be(new SelectionCarry(Restored: 1, Lost: 0));
        carried.IsComplete.Should().BeTrue();

        selection.Selected.Should().ContainSingle().Which.Should().Be(after.Wall);
        selection.Contains(before.Wall).Should().BeFalse("that tag belongs to the previous rebuild");
    }

    [Fact]
    public void WhatTheRebuildRemovedIsDroppedAndCounted()
    {
        // A selection quietly shrinking is indistinguishable to a user from one they mis-clicked.
        // The count is what lets the caller tell them which happened.
        Scene before = new();
        SelectionSet selection = new();

        selection.Apply(before.Wall, SelectionAction.Add);

        var remembered = SelectionAcrossRebuild.Remember(selection, before.History, FeatureId.None);

        RebuildHistory.Builder gone = new();
        gone.Add(before.Base, new HistoryMapBuilder()
            .AddNew(before.Profile, OperationRole.Retained)
            .Build());

        // The extrude ran and made nothing: the wall this selection named is not there.
        gone.Add(before.Extrude, HistoryMap.Empty);

        SelectionCarry carried =
            SelectionAcrossRebuild.Restore(selection, remembered, gone.Build(), FeatureId.None);

        carried.Should().Be(new SelectionCarry(Restored: 0, Lost: 1));
        carried.IsComplete.Should().BeFalse();
        selection.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void OneUnnameableEntityDoesNotCostTheRestOfTheSelection()
    {
        // Geometry the history cannot account for -- from a feature that failed, or an older
        // build. It cannot be carried, but the other selections are unaffected by its presence.
        Scene before = new();
        SelectionSet selection = new();

        selection.Apply(before.Wall, SelectionAction.Add);
        selection.Apply(before.Face(999), SelectionAction.Add);

        var remembered = SelectionAcrossRebuild.Remember(selection, before.History, FeatureId.None);

        remembered.Should().ContainSingle("only the wall can be named");

        Scene after = new(before, tagOffset: 100);

        SelectionAcrossRebuild.Restore(selection, remembered, after.History, FeatureId.None)
            .Should().Be(new SelectionCarry(Restored: 1, Lost: 0));

        selection.Selected.Should().ContainSingle().Which.Should().Be(after.Wall);
    }

    [Fact]
    public void RestoringReplacesTheSelectionRatherThanAddingToIt()
    {
        // What is in the set before a restore are handles from the previous rebuild -- the stale
        // things this exists to replace. Leaving them would hide them among the ones that resolved.
        Scene before = new();
        SelectionSet selection = new();

        selection.Apply(before.Wall, SelectionAction.Add);

        var remembered = SelectionAcrossRebuild.Remember(selection, before.History, FeatureId.None);

        Scene after = new(before, tagOffset: 100);

        SelectionAcrossRebuild.Restore(selection, remembered, after.History, FeatureId.None);

        selection.Count.Should().Be(1, "the stale entity must not still be there beside the new one");
    }

    [Fact]
    public void ARebuildThatSplitWhatWasSelectedLosesItRatherThanGuessing()
    {
        // §5.3's third tier: an ambiguous reference is not answered by a guess. Selecting both
        // halves would be one, and made silently -- the user would go on to act on something they
        // never chose.
        Scene before = new();
        SelectionSet selection = new();

        selection.Apply(before.Wall, SelectionAction.Add);

        var remembered = SelectionAcrossRebuild.Remember(selection, before.History, FeatureId.None);

        RebuildHistory.Builder split = new();
        split.Add(before.Base, new HistoryMapBuilder()
            .AddNew(before.Profile, OperationRole.Retained)
            .Build());

        split.Add(before.Extrude, new HistoryMapBuilder()
            .AddGenerated(before.Profile, before.Face(20), OperationRole.SideWall)
            .AddGenerated(before.Profile, before.Face(21), OperationRole.SideWall)
            .Build());

        SelectionCarry carried =
            SelectionAcrossRebuild.Restore(selection, remembered, split.Build(), FeatureId.None);

        carried.Lost.Should().Be(1);
        selection.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void TwoSelectionsMergedIntoOneAreBothFoundRatherThanOneLost()
    {
        // A boolean that unites two faces. Both names resolve, to the same face, so the selection
        // is smaller than it was -- but nothing was lost, and saying otherwise would tell the user
        // to go looking for something that is right in front of them.
        //
        // This is why the count is taken from the resolution and not from whether the set grew:
        // the second Apply reports no change, which is true of the set and false about the name.
        Scene before = new();
        SelectionSet selection = new();

        SubEntity second = before.Face(11);

        before.AlsoGenerated(second, OperationRole.SideWall);

        selection.Apply(before.Wall, SelectionAction.Add);
        selection.Apply(second, SelectionAction.Add);

        var remembered = SelectionAcrossRebuild.Remember(selection, before.History, FeatureId.None);

        remembered.Length.Should().Be(2);

        RebuildHistory.Builder merged = new();
        merged.Add(before.Base, new HistoryMapBuilder()
            .AddNew(before.Profile, OperationRole.Retained)
            .Build());

        // One wall where there were two, and it is the only thing with that role, so both
        // ordinals land on it.
        merged.Add(before.Extrude, new HistoryMapBuilder()
            .AddGenerated(before.Profile, before.Face(30), OperationRole.SideWall)
            .Build());

        SelectionCarry carried =
            SelectionAcrossRebuild.Restore(selection, remembered, merged.Build(), FeatureId.None);

        carried.Lost.Should().Be(0, "both names found a face");
        carried.Restored.Should().Be(2);
        selection.Count.Should().Be(1, "they found the same face");
    }

    [Fact]
    public void AnEmptySelectionCarriesAcrossAsNothing()
    {
        Scene before = new();
        SelectionSet selection = new();

        var remembered = SelectionAcrossRebuild.Remember(selection, before.History, FeatureId.None);

        remembered.Should().BeEmpty();

        SelectionAcrossRebuild.Restore(selection, remembered, before.History, FeatureId.None)
            .Should().Be(new SelectionCarry(Restored: 0, Lost: 0));
    }

    /// <summary>A profile edge and the wall extruded from it, at whatever tags this rebuild used.</summary>
    private sealed class Scene
    {
        private readonly RebuildHistory.Builder _builder = new();

        public Scene()
        {
            Shape = new KernelShape(1);
            Profile = Edge(1);
            Wall = Face(10);
            Record();
        }

        /// <summary>The same model rebuilt, with every tag different.</summary>
        public Scene(Scene other, ulong tagOffset)
        {
            Base = other.Base;
            Extrude = other.Extrude;

            // A different shape as well as different tags: a SubEntity is identified by its owner
            // too, so reusing the shape would leave half the identity accidentally unchanged.
            Shape = new KernelShape(other.Shape.Tag + 1);
            Profile = Edge(1 + tagOffset);
            Wall = Face(10 + tagOffset);
            Record();
        }

        public FeatureId Base { get; } = FeatureId.New();

        public FeatureId Extrude { get; } = FeatureId.New();

        public KernelShape Shape { get; }

        public SubEntity Profile { get; }

        public SubEntity Wall { get; }

        public RebuildHistory History => _builder.Build();

        public SubEntity Face(ulong tag) => new(Shape, tag, SubEntityKind.Face);

        public SubEntity Edge(ulong tag) => new(Shape, tag, SubEntityKind.Edge);

        /// <summary>Adds a second output of the extrude, so the scene has siblings to separate.</summary>
        public void AlsoGenerated(SubEntity output, OperationRole role)
        {
            _builder.Add(Extrude, new HistoryMapBuilder()
                .AddGenerated(Profile, Wall, OperationRole.SideWall)
                .AddGenerated(Profile, output, role)
                .Build());
        }

        private void Record()
        {
            _builder.Add(Base, new HistoryMapBuilder()
                .AddNew(Profile, OperationRole.Retained)
                .Build());

            _builder.Add(Extrude, new HistoryMapBuilder()
                .AddGenerated(Profile, Wall, OperationRole.SideWall)
                .Build());
        }
    }
}
