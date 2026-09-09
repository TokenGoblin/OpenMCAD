using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Solver;
using OpenMCAD.Solver.Fake;
using OpenMCAD.Solver.Sketching;
using OpenMCAD.ViewModels;

using Xunit;

namespace OpenMCAD.ViewModels.Tests;

/// <summary>
/// The view model behind a sketch's entity toolbar, constraint palette and DOF readout (P4-T15).
/// </summary>
public sealed class SketchEditorViewModelTests
{
    private static SketchEditorViewModel New() => new(new FakeSolver());

    [Fact]
    public void AddPoint_AddsAndResolvesAsUnderDefined()
    {
        SketchEditorViewModel editor = New();

        SketchEntityId id = editor.AddPoint(Vec2d.Zero);

        editor.Sketch.Entities.Count.Should().Be(1);
        editor.LastSolve.Should().NotBeNull();
        editor.LastSolve!.Diagnosis.Outcome.Should().Be(SolveOutcome.UnderConstrained);
        editor.FreeEntities.Should().Contain(id);
        editor.IsFullyDefined.Should().BeFalse();
        editor.StatusText.Should().Contain("2 degrees of freedom");
    }

    [Fact]
    public void ApplyConstraint_FixingAPointMakesTheSketchFullyDefined()
    {
        SketchEditorViewModel editor = New();
        SketchEntityId id = editor.AddPoint(Vec2d.Zero);

        editor.Selection = [new SketchPointRef(id)];
        string? error = editor.ApplyConstraint(ConstraintKind.Fix);

        error.Should().BeNull();
        editor.Sketch.Constraints.Count.Should().Be(1);
        editor.IsFullyDefined.Should().BeTrue();
        editor.StatusText.Should().Be("Fully defined");
    }

    [Fact]
    public void ApplyConstraint_ReportsAndRollsBackAWrongOperandKind()
    {
        SketchEditorViewModel editor = New();
        SketchEntityId line = editor.AddLine(Vec2d.Zero, new Vec2d(1, 0));

        editor.Selection = [new SketchPointRef(line)];
        string? error = editor.ApplyConstraint(ConstraintKind.Radius, 5);

        error.Should().NotBeNull();
        error.Should().Contain("radius");
        editor.Sketch.Constraints.Count.Should().Be(
            0, "a rejected constraint must not be left dangling in the sketch");
    }

    [Fact]
    public void ApplyConstraint_DetectsAContradictionAndNamesTheLaterConstraint()
    {
        SketchEditorViewModel editor = New();
        SketchEntityId a = editor.AddPoint(Vec2d.Zero);
        SketchEntityId b = editor.AddPoint(new Vec2d(3, 4));

        editor.Selection = [new SketchPointRef(a)];
        editor.ApplyConstraint(ConstraintKind.Fix).Should().BeNull();

        editor.Selection = [new SketchPointRef(a), new SketchPointRef(b)];
        editor.ApplyConstraint(ConstraintKind.Distance, 5).Should().BeNull();

        string? secondError = editor.ApplyConstraint(ConstraintKind.Distance, 7);

        secondError.Should().BeNull("an over-constrained sketch is a diagnosis, not a rejection");
        editor.IsFullyDefined.Should().BeFalse();
        editor.LastSolve!.Diagnosis.Outcome.Should().Be(SolveOutcome.OverConstrained);

        SketchConstraintId theSecondDistance = editor.Sketch.Constraints.Ordered[^1].Id;
        editor.ConflictingConstraints.Should().Contain(theSecondDistance);
        editor.StatusText.Should().Contain("Over-defined");
    }

    [Fact]
    public void ApplyConstraint_ARedundantConstraintStaysFullyDefined()
    {
        SketchEditorViewModel editor = New();
        SketchEntityId a = editor.AddPoint(Vec2d.Zero);
        SketchEntityId b = editor.AddPoint(new Vec2d(3, 4));

        editor.Selection = [new SketchPointRef(a)];
        editor.ApplyConstraint(ConstraintKind.Fix);

        editor.Selection = [new SketchPointRef(a), new SketchPointRef(b)];
        editor.ApplyConstraint(ConstraintKind.Distance, 5);
        editor.ApplyConstraint(ConstraintKind.Distance, 5);

        editor.LastSolve!.Diagnosis.Outcome.Should().Be(SolveOutcome.Redundant);
        editor.IsFullyDefined.Should().BeTrue(
            "a redundant sketch is geometrically as defined as a well-constrained one");
        editor.RedundantConstraints.Should().NotBeEmpty();
    }

    [Fact]
    public void RemoveEntity_TakesItsConstraintsWithItAndDropsItFromTheSelection()
    {
        SketchEditorViewModel editor = New();
        SketchEntityId a = editor.AddPoint(Vec2d.Zero);
        SketchEntityId b = editor.AddPoint(new Vec2d(3, 4));

        editor.Selection = [new SketchPointRef(a), new SketchPointRef(b)];
        editor.ApplyConstraint(ConstraintKind.Distance, 5);

        editor.RemoveEntity(b);

        editor.Sketch.Entities.Count.Should().Be(1);
        editor.Sketch.Constraints.Count.Should().Be(0);
        editor.Selection.Should().NotContain(o => o.Entity == b);
    }

    [Fact]
    public void SetSelectionConstruction_TogglesTheFlagOnEverySelectedEntity()
    {
        SketchEditorViewModel editor = New();
        SketchEntityId line = editor.AddLine(Vec2d.Zero, new Vec2d(4, 0));

        editor.Selection = [new SketchPointRef(line)];
        string? error = editor.SetSelectionConstruction(true);

        error.Should().BeNull();
        editor.Sketch.Entities.Find(line)!.IsConstruction.Should().BeTrue();
    }

    [Fact]
    public void PropertyChanged_FiresWhenTheStatusTextChanges()
    {
        SketchEditorViewModel editor = New();
        List<string> changed = [];
        editor.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        editor.AddPoint(Vec2d.Zero);

        changed.Should().Contain(nameof(SketchEditorViewModel.StatusText));
        changed.Should().Contain(nameof(SketchEditorViewModel.Sketch));
    }
    // --- Editing tools ---------------------------------------------------------------------------

    [Fact]
    public void Trim_ShortensTheEntityAndReSolves()
    {
        SketchEditorViewModel editor = New();

        SketchEntityId line = editor.AddLine(Vec2d.Zero, new Vec2d(10, 0));
        editor.AddLine(new Vec2d(4, -5), new Vec2d(4, 5));

        SolveResult before = editor.LastSolve!;

        string? failure = editor.Trim(line, new Vec2d(8, 0));

        failure.Should().BeNull();
        ((SketchLine)editor.Sketch.Entities.Find(line)!).End.X.Should().BeApproximately(4, 1e-9);

        // A fresh solve, not the one the last Add left behind. Asserting only that LastSolve is
        // non-null passes without the tool having re-solved at all, which is the readout going
        // stale under the user -- the exact failure this view model exists to prevent.
        editor.LastSolve.Should().NotBeSameAs(before, "every mutation re-solves");
    }

    [Fact]
    public void Trim_ReportsWhyAndLeavesTheSketchAloneWhenItCannot()
    {
        SketchEditorViewModel editor = New();

        SketchEntityId line = editor.AddLine(Vec2d.Zero, new Vec2d(10, 0));
        Sketch before = editor.Sketch;

        string? failure = editor.Trim(line, new Vec2d(5, 0));

        failure.Should().NotBeNullOrWhiteSpace("nothing crosses it, and the user has to be told why");
        editor.Sketch.Should().BeSameAs(before, "a refused tool changes nothing");
    }

    [Fact]
    public void Extend_LengthensTheLineToWhatItReaches()
    {
        SketchEditorViewModel editor = New();

        SketchEntityId line = editor.AddLine(Vec2d.Zero, new Vec2d(4, 0));
        editor.AddLine(new Vec2d(9, -5), new Vec2d(9, 5));

        editor.Extend(line, new Vec2d(4, 0)).Should().BeNull();

        ((SketchLine)editor.Sketch.Entities.Find(line)!).End.X.Should().BeApproximately(9, 1e-9);
    }

    [Fact]
    public void Split_LeavesTwoEntitiesWhereThereWasOne()
    {
        SketchEditorViewModel editor = New();

        SketchEntityId line = editor.AddLine(Vec2d.Zero, new Vec2d(10, 0));

        editor.Split(line, new Vec2d(4, 0)).Should().BeNull();

        editor.Sketch.Entities.Count.Should().Be(2);
    }

    [Fact]
    public void Fillet_ReplacesTheCornerWithAnArcAndItsConstraints()
    {
        SketchEditorViewModel editor = New();

        SketchEntityId along = editor.AddLine(Vec2d.Zero, new Vec2d(10, 0));
        SketchEntityId up = editor.AddLine(Vec2d.Zero, new Vec2d(0, 10));

        string? failure = editor.Fillet(
            new CornerPick(along, new Vec2d(5, 0)), new CornerPick(up, new Vec2d(0, 5)), 2);

        failure.Should().BeNull();
        editor.Sketch.Entities.Ordered.Should().ContainSingle(e => e is SketchArc);
        editor.Sketch.Constraints.Count.Should().Be(4, "two coincidences and two tangencies");
    }

    [Fact]
    public void Chamfer_CutsTheCornerOffWithALine()
    {
        SketchEditorViewModel editor = New();

        SketchEntityId along = editor.AddLine(Vec2d.Zero, new Vec2d(10, 0));
        SketchEntityId up = editor.AddLine(Vec2d.Zero, new Vec2d(0, 10));

        editor.Chamfer(
            new CornerPick(along, new Vec2d(5, 0)), new CornerPick(up, new Vec2d(0, 5)), 3)
            .Should().BeNull();

        editor.Sketch.Entities.Count.Should().Be(3);
        editor.Sketch.Entities.Ordered.Should().NotContain(e => e is SketchArc);
    }

    [Fact]
    public void OffsetSelection_TakesItsDirectionFromTheEntitySelectedFirst()
    {
        // The rule SketchOffset defines, and the reason this passes the selection in order rather
        // than as a set: which entity the user picked first decides which side a positive distance
        // lands on, which is what lets a preview follow the cursor without this layer knowing where
        // the cursor is.
        SketchEditorViewModel editor = New();

        SketchEntityId along = editor.AddLine(Vec2d.Zero, new Vec2d(10, 0));
        SketchEntityId back = editor.AddLine(new Vec2d(10, 10), new Vec2d(10, 0));

        editor.Selection = [new(along), new(back)];
        editor.OffsetSelection(2).Should().BeNull();

        editor.Sketch.Entities.Count.Should().Be(4, "two new pieces beside the two originals");

        // The chain runs through the first-named entity in its own direction, so along +X. Left of
        // that is +Y, which puts its offset above it -- and would put it below if the order, or the
        // rule, went the other way.
        SketchLine[] added =
        [
            .. editor.Sketch.Entities.Ordered
                .OfType<SketchLine>()
                .Where(l => l.Id != along && l.Id != back),
        ];

        added.Should().ContainSingle(l =>
            System.Math.Abs(l.Start.Y - 2) < 1e-9 && System.Math.Abs(l.End.Y - 2) < 1e-9);

        added.Should().NotContain(l => l.Start.Y < 0 || l.End.Y < 0);
    }

    [Fact]
    public void OffsetSelection_ReportsASelectionThatIsNotAChain()
    {
        SketchEditorViewModel editor = New();

        SketchEntityId one = editor.AddLine(Vec2d.Zero, new Vec2d(10, 0));
        SketchEntityId other = editor.AddLine(new Vec2d(20, 0), new Vec2d(30, 0));

        editor.Selection = [new(one), new(other)];

        editor.OffsetSelection(2).Should().NotBeNullOrWhiteSpace();
        editor.Sketch.Entities.Count.Should().Be(2, "nothing was added");
    }

    [Fact]
    public void TransformSelection_MovesTheSelectedEntitiesInPlace()
    {
        SketchEditorViewModel editor = New();

        SketchEntityId point = editor.AddPoint(Vec2d.Zero);
        editor.Selection = [new(point)];

        editor.TransformSelection(SketchTransform.Translate(new Vec2d(3, 4))).Should().BeNull();

        editor.Sketch.Entities.Count.Should().Be(1, "a move does not add anything");
        ((SketchPoint)editor.Sketch.Entities.Find(point)!).Position.Should().Be(new Vec2d(3, 4));
    }

    [Fact]
    public void CopySelection_KeepsTheOriginalAndAddsTheCopy()
    {
        SketchEditorViewModel editor = New();

        SketchEntityId point = editor.AddPoint(Vec2d.Zero);
        editor.Selection = [new(point)];

        editor.CopySelection(SketchTransform.Translate(new Vec2d(3, 0))).Should().BeNull();

        editor.Sketch.Entities.Count.Should().Be(2);
        ((SketchPoint)editor.Sketch.Entities.Find(point)!).Position.Should().Be(Vec2d.Zero);
    }

    [Fact]
    public void LinearPatternSelection_CountsTheOriginalAmongTheInstances()
    {
        SketchEditorViewModel editor = New();

        SketchEntityId point = editor.AddPoint(Vec2d.Zero);
        editor.Selection = [new(point)];

        editor.LinearPatternSelection(new Vec2d(2, 0), 4).Should().BeNull();

        editor.Sketch.Entities.Count.Should().Be(4, "four in total, not four more");
    }

    [Fact]
    public void MirrorSelection_AddsTheReflectionAndKeepsTheOriginal()
    {
        SketchEditorViewModel editor = New();

        SketchEntityId point = editor.AddPoint(new Vec2d(0, 3));
        editor.Selection = [new(point)];

        editor.MirrorSelection(Vec2d.Zero, new Vec2d(1, 0)).Should().BeNull();

        editor.Sketch.Entities.Count.Should().Be(2);
        editor.Sketch.Entities.Ordered.Select(e => ((SketchPoint)e).Position.Y)
            .Should().BeEquivalentTo(new[] { 3.0, -3.0 });
    }

    [Fact]
    public void CircularPatternSelection_SpacesInstancesOverTheWholeAngle()
    {
        SketchEditorViewModel editor = New();

        SketchEntityId point = editor.AddPoint(new Vec2d(5, 0));
        editor.Selection = [new(point)];

        editor.CircularPatternSelection(Vec2d.Zero, 2 * System.Math.PI, 4).Should().BeNull();

        editor.Sketch.Entities.Count.Should().Be(4);
    }

    [Fact]
    public void ATool_ThatSelectsNothingSaysSoRatherThanDoingNothingQuietly()
    {
        SketchEditorViewModel editor = New();

        editor.AddLine(Vec2d.Zero, new Vec2d(10, 0));

        // Nothing selected. An offset of nothing is not an offset, and a toolbar button that
        // appears to work while doing nothing is worse than one that explains itself.
        editor.OffsetSelection(2).Should().NotBeNullOrWhiteSpace();
    }

}
