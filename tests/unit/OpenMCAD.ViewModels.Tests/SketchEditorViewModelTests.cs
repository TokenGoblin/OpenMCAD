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
}
