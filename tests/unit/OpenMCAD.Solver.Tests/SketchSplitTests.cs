using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Solver.Sketching;

using Xunit;

namespace OpenMCAD.Solver.Tests;

/// <summary>Breaking an entity into two at a point on it, keeping both pieces (P4-T13).</summary>
public sealed class SketchSplitTests
{
    [Fact]
    public void Line_SplitsIntoTwoPiecesMeetingAtThePoint()
    {
        Sketch sketch = Sketch.Empty.With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)));

        SplitResult result = SketchSplit.Split(sketch, Entity(1), new Vec2d(4, 0));

        result.IsResolved.Should().BeTrue();
        result.Sketch!.Entities.Count.Should().Be(2);

        SketchLine first = (SketchLine)result.Sketch.Entities.Find(result.First!.Value)!;
        SketchLine second = (SketchLine)result.Sketch.Entities.Find(result.Second!.Value)!;

        first.Start.Should().Be(Vec2d.Zero);
        first.End.Should().Be(new Vec2d(4, 0));
        second.Start.Should().Be(new Vec2d(4, 0));
        second.End.Should().Be(new Vec2d(10, 0));
    }

    [Fact]
    public void Line_AConstraintOnStartNeedsNoChange()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)))
            .With(SketchConstraint.Of(ConstraintKind.Fix, [new(Entity(1), EntityPoint.Start)]));

        SplitResult result = SketchSplit.Split(sketch, Entity(1), new Vec2d(4, 0));

        result.IsResolved.Should().BeTrue();
        result.Sketch!.Constraints.Count.Should().Be(1);
        result.Sketch.Constraints.Ordered[0].On.Should().Equal(
            new SketchPointRef(result.First!.Value, EntityPoint.Start));
    }

    [Fact]
    public void Line_AConstraintOnEndIsRemappedToTheSecondPiece()
    {
        SketchEntityId anchor = Entity(9);

        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)))
            .With(new SketchPoint(anchor, new Vec2d(20, 5)))
            .With(SketchConstraint.Of(
                ConstraintKind.Distance, [new(anchor), new(Entity(1), EntityPoint.End)], 5));

        SplitResult result = SketchSplit.Split(sketch, Entity(1), new Vec2d(4, 0));

        result.IsResolved.Should().BeTrue();
        result.Sketch!.Constraints.Count.Should().Be(
            1, "the constraint moves with the point it named -- it is not duplicated");
        result.Sketch.Constraints.Ordered[0].On.Should().Contain(
            new SketchPointRef(result.Second!.Value, EntityPoint.End));
        result.Sketch.Constraints.Ordered[0].On.Should().NotContain(
            o => o.Entity == result.First!.Value);
    }

    [Fact]
    public void Line_AWholeEntityConstraintIsKeptAndDuplicatedOntoTheSecondPiece()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)))
            .With(SketchConstraint.Of(ConstraintKind.Horizontal, [new(Entity(1))]));

        SplitResult result = SketchSplit.Split(sketch, Entity(1), new Vec2d(4, 0));

        result.IsResolved.Should().BeTrue();
        result.Sketch!.Constraints.Count.Should().Be(
            2, "both pieces are still horizontal, so both need the constraint");

        result.Sketch.Constraints.Ordered.Select(c => c.On.Single().Entity)
            .Should().BeEquivalentTo([result.First!.Value, result.Second!.Value]);
        result.Sketch.Constraints.Ordered.Should().OnlyContain(c => c.Kind == ConstraintKind.Horizontal);
    }

    [Fact]
    public void Line_AMidpointReferenceRefusesTheWholeSplit()
    {
        SketchEntityId anchor = Entity(9);

        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)))
            .With(new SketchPoint(anchor, new Vec2d(5, 5)))
            .With(SketchConstraint.Of(
                ConstraintKind.Coincident, [new(anchor), new(Entity(1), EntityPoint.Middle)]));

        SplitResult result = SketchSplit.Split(sketch, Entity(1), new Vec2d(4, 0));

        result.Outcome.Should().Be(SplitOutcome.ConstraintNotTransferable);
        result.Sketch.Should().BeNull();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 0)]
    [InlineData(20, 5)]
    public void Line_RefusesAPointAtAnEndOrOffTheLine(double x, double y)
    {
        Sketch sketch = Sketch.Empty.With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)));

        SplitResult result = SketchSplit.Split(sketch, Entity(1), new Vec2d(x, y));

        result.Outcome.Should().Be(SplitOutcome.NotOnEntity);
    }

    [Fact]
    public void Split_FailsWhenTheEntityIsMissing()
    {
        SplitResult result = SketchSplit.Split(Sketch.Empty, Entity(1), Vec2d.Zero);

        result.Outcome.Should().Be(SplitOutcome.EntityNotFound);
    }

    [Fact]
    public void Split_RefusesACircle()
    {
        Sketch sketch = Sketch.Empty.With(new SketchCircle(Entity(1), Vec2d.Zero, 5));

        SplitResult result = SketchSplit.Split(sketch, Entity(1), new Vec2d(5, 0));

        result.Outcome.Should().Be(SplitOutcome.Unsupported);
    }

    [Fact]
    public void Arc_SplitsIntoTwoArcsMeetingAtThePoint()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchArc(Entity(1), Vec2d.Zero, 5, 0, System.Math.PI));

        SplitResult result = SketchSplit.Split(sketch, Entity(1), new Vec2d(0, 5));

        result.IsResolved.Should().BeTrue();
        SketchArc first = (SketchArc)result.Sketch!.Entities.Find(result.First!.Value)!;
        SketchArc second = (SketchArc)result.Sketch.Entities.Find(result.Second!.Value)!;

        first.StartAngle.Should().BeApproximately(0, 1e-9);
        first.EndAngle.Should().BeApproximately(System.Math.PI / 2, 1e-9);
        second.StartAngle.Should().BeApproximately(System.Math.PI / 2, 1e-9);
        second.EndAngle.Should().BeApproximately(System.Math.PI, 1e-9);
    }

    [Fact]
    public void Arc_ACentreReferenceIsKeptAndDuplicatedOntoTheSecondPiece()
    {
        SketchEntityId anchor = Entity(9);

        Sketch sketch = Sketch.Empty
            .With(new SketchArc(Entity(1), Vec2d.Zero, 5, 0, System.Math.PI))
            .With(new SketchPoint(anchor, Vec2d.Zero))
            .With(SketchConstraint.Of(
                ConstraintKind.Coincident, [new(anchor), new(Entity(1), EntityPoint.Centre)]));

        SplitResult result = SketchSplit.Split(sketch, Entity(1), new Vec2d(0, 5));

        result.IsResolved.Should().BeTrue();
        result.Sketch!.Constraints.Count.Should().Be(
            2, "the centre does not move when the circumference is cut, so both pieces still share it");
    }

    private static SketchEntityId Entity(int n)
        => new(new Guid($"00000000-0000-0000-0000-{n:D12}"));
}
