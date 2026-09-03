using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Solver.Sketching;

using Xunit;

namespace OpenMCAD.Solver.Tests;

/// <summary>
/// The sketch-wide editing tools built on <see cref="SketchTransform"/>: move, rotate, scale, copy,
/// mirror, the two patterns, and toggling construction geometry (P4-T13).
/// </summary>
public sealed class SketchEditTests
{
    [Fact]
    public void Transform_MovesTheEntityInPlaceRatherThanCopyingIt()
    {
        SketchEntityId id = SketchEntityId.New();
        Sketch sketch = Sketch.Empty.With(new SketchPoint(id, Vec2d.Zero));

        SketchEditResult result = SketchEdit.Transform(
            sketch, [id], SketchTransform.Translate(new Vec2d(3, 4)));

        result.IsResolved.Should().BeTrue();
        result.Sketch!.Entities.Count.Should().Be(1, "moving is not copying");
        ((SketchPoint)result.Sketch.Entities.Find(id)!).Position.Should().Be(new Vec2d(3, 4));
    }

    [Fact]
    public void Transform_FailsWhenAnEntityIsMissing()
    {
        SketchEditResult result = SketchEdit.Transform(
            Sketch.Empty, [SketchEntityId.New()], SketchTransform.Identity);

        result.Outcome.Should().Be(SketchEditOutcome.EntityNotFound);
    }

    [Fact]
    public void Transform_FailsForAnUnsupportedKindRatherThanSilentlyLeavingItAlone()
    {
        SketchEntityId id = SketchEntityId.New();
        Sketch sketch = Sketch.Empty.With(new SketchEllipse(id, Vec2d.Zero, 5, 3));

        SketchEditResult result = SketchEdit.Transform(sketch, [id], SketchTransform.Identity);

        result.Outcome.Should().Be(SketchEditOutcome.Unsupported);
    }

    [Fact]
    public void Duplicate_AddsANewEntityAndKeepsTheOriginalWhereItWas()
    {
        SketchEntityId id = SketchEntityId.New();
        Sketch sketch = Sketch.Empty.With(new SketchPoint(id, Vec2d.Zero));

        SketchEditResult result = SketchEdit.Duplicate(
            sketch, [id], SketchTransform.Translate(new Vec2d(5, 0)));

        result.IsResolved.Should().BeTrue();
        result.Sketch!.Entities.Count.Should().Be(2);

        SketchPoint original = (SketchPoint)result.Sketch.Entities.Find(id)!;
        original.Position.Should().Be(Vec2d.Zero, "the original does not move when it is copied");

        SketchPoint copy = (SketchPoint)result.Sketch.Entities.Ordered.Single(e => e.Id != id);
        copy.Position.Should().Be(new Vec2d(5, 0));
    }

    [Fact]
    public void Duplicate_RemapsAConstraintWhoseEntitiesAreAllCopied()
    {
        SketchEntityId a = SketchEntityId.New();
        SketchEntityId b = SketchEntityId.New();

        Sketch sketch = Sketch.Empty
            .With(new SketchPoint(a, Vec2d.Zero))
            .With(new SketchPoint(b, new Vec2d(4, 0)))
            .With(SketchConstraint.Of(ConstraintKind.Distance, [new(a), new(b)], 4));

        SketchEditResult result = SketchEdit.Duplicate(
            sketch, [a, b], SketchTransform.Translate(new Vec2d(0, 10)));

        result.IsResolved.Should().BeTrue();
        result.Sketch!.Constraints.Count.Should().Be(
            2, "the copy keeps its own internal Distance, alongside the original's");

        SketchEntityId copyA = result.Sketch.Entities.Ordered.Single(
            e => e.Id != a && e.Id != b && ((SketchPoint)e).Position == new Vec2d(0, 10)).Id;
        SketchEntityId copyB = result.Sketch.Entities.Ordered.Single(
            e => e.Id != a && e.Id != b && ((SketchPoint)e).Position == new Vec2d(4, 10)).Id;

        result.Sketch.Constraints.Ordered.Should().Contain(
            c => c.On.Select(o => o.Entity).OrderBy(x => x.Value)
                .SequenceEqual(new[] { copyA, copyB }.OrderBy(x => x.Value)),
            "the copied pair keeps the Distance between them");
    }

    [Fact]
    public void Duplicate_DropsAConstraintThatReachesOutsideTheCopiedSet()
    {
        // Copying only the moving end of a dimensioned line: duplicating "4 units from the fixed
        // end" onto the copy would point it at the very same fixed point every other copy shares,
        // which contradicts itself the moment there is more than one copy.
        SketchEntityId fixedPoint = SketchEntityId.New();
        SketchEntityId movingPoint = SketchEntityId.New();

        Sketch sketch = Sketch.Empty
            .With(new SketchPoint(fixedPoint, Vec2d.Zero))
            .With(new SketchPoint(movingPoint, new Vec2d(4, 0)))
            .With(SketchConstraint.Of(
                ConstraintKind.Distance, [new(fixedPoint), new(movingPoint)], 4));

        SketchEditResult result = SketchEdit.Duplicate(
            sketch, [movingPoint], SketchTransform.Translate(new Vec2d(0, 10)));

        result.IsResolved.Should().BeTrue();
        result.Sketch!.Constraints.Count.Should().Be(
            1, "only the original Distance -- nothing was duplicated for the copy");
    }

    [Fact]
    public void Mirror_AddsAReflectedCopy()
    {
        SketchEntityId id = SketchEntityId.New();
        Sketch sketch = Sketch.Empty.With(new SketchPoint(id, new Vec2d(3, 4)));

        SketchEditResult result = SketchEdit.Mirror(sketch, [id], Vec2d.Zero, Vec2d.UnitX);

        result.IsResolved.Should().BeTrue();
        SketchPoint copy = (SketchPoint)result.Sketch!.Entities.Ordered.Single(e => e.Id != id);
        copy.Position.Should().Be(new Vec2d(3, -4));
    }

    [Fact]
    public void LinearPattern_OneInstanceLeavesTheSelectionUntouched()
    {
        SketchEntityId id = SketchEntityId.New();
        Sketch sketch = Sketch.Empty.With(new SketchPoint(id, Vec2d.Zero));

        SketchEditResult result = SketchEdit.LinearPattern(sketch, [id], new Vec2d(1, 0), 1);

        result.IsResolved.Should().BeTrue();
        result.Sketch!.Entities.Count.Should().Be(1);
    }

    [Fact]
    public void LinearPattern_ProducesTheRequestedTotalNumberOfInstances()
    {
        SketchEntityId id = SketchEntityId.New();
        Sketch sketch = Sketch.Empty.With(new SketchPoint(id, Vec2d.Zero));

        SketchEditResult result = SketchEdit.LinearPattern(sketch, [id], new Vec2d(2, 0), 3);

        result.IsResolved.Should().BeTrue();
        result.Sketch!.Entities.Count.Should().Be(3);

        IEnumerable<Vec2d> positions = result.Sketch.Entities.Ordered.Select(e => ((SketchPoint)e).Position);
        positions.Should().BeEquivalentTo([Vec2d.Zero, new Vec2d(2, 0), new Vec2d(4, 0)]);
    }

    [Fact]
    public void CircularPattern_FourInstancesRoundAFullCircleAreNinetyDegreesApart()
    {
        SketchEntityId id = SketchEntityId.New();
        Sketch sketch = Sketch.Empty.With(new SketchPoint(id, new Vec2d(10, 0)));

        SketchEditResult result = SketchEdit.CircularPattern(
            sketch, [id], Vec2d.Zero, 2 * System.Math.PI, 4);

        result.IsResolved.Should().BeTrue();
        result.Sketch!.Entities.Count.Should().Be(4);

        List<Vec2d> positions =
            [.. result.Sketch.Entities.Ordered.Select(e => ((SketchPoint)e).Position)];

        positions.Should().Contain(p => Near(p, new Vec2d(10, 0)));
        positions.Should().Contain(p => Near(p, new Vec2d(0, 10)));
        positions.Should().Contain(p => Near(p, new Vec2d(-10, 0)));
        positions.Should().Contain(p => Near(p, new Vec2d(0, -10)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Patterns_RejectFewerThanOneInstance(int count)
    {
        SketchEntityId id = SketchEntityId.New();
        Sketch sketch = Sketch.Empty.With(new SketchPoint(id, Vec2d.Zero));

        Action linear = () => SketchEdit.LinearPattern(sketch, [id], Vec2d.UnitX, count);
        Action circular = () => SketchEdit.CircularPattern(sketch, [id], Vec2d.Zero, 1, count);

        linear.Should().Throw<ArgumentOutOfRangeException>();
        circular.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void SetConstruction_TogglesTheFlagWithoutChangingTheGeometry()
    {
        SketchEntityId id = SketchEntityId.New();
        Sketch sketch = Sketch.Empty.With(new SketchLine(id, Vec2d.Zero, new Vec2d(4, 0)));

        SketchEditResult result = SketchEdit.SetConstruction(sketch, [id], true);

        result.IsResolved.Should().BeTrue();
        SketchLine line = (SketchLine)result.Sketch!.Entities.Find(id)!;
        line.IsConstruction.Should().BeTrue();
        line.Start.Should().Be(Vec2d.Zero);
        line.End.Should().Be(new Vec2d(4, 0));
    }

    [Fact]
    public void SetConstruction_FailsWhenAnEntityIsMissing()
    {
        SketchEditResult result = SketchEdit.SetConstruction(Sketch.Empty, [SketchEntityId.New()], true);

        result.Outcome.Should().Be(SketchEditOutcome.EntityNotFound);
    }

    private static bool Near(Vec2d a, Vec2d b) => (a - b).Length <= 1e-9;
}
