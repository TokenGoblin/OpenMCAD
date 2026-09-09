using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Solver.Sketching;

using Xunit;

namespace OpenMCAD.Solver.Tests;

/// <summary>Replacing the corner between two lines with a fillet or a chamfer (P4-T13).</summary>
public sealed class SketchCornerTests
{
    private const double Tol = 1e-9;

    [Fact]
    public void Fillet_TrimsBothLegsBackToTheirTangentPoints()
    {
        Sketch sketch = RightAngle();

        CornerResult result = SketchCorner.Fillet(sketch, On(1, 5, 0), On(2, 0, 5), 2);

        result.IsResolved.Should().BeTrue(result.Reason);

        SketchLine along = (SketchLine)result.Sketch!.Entities.Find(Entity(1))!;
        SketchLine up = (SketchLine)result.Sketch.Entities.Find(Entity(2))!;

        // r / tan(45 degrees) is r, so both tangent points sit 2 from the corner.
        At(along.Start, 2, 0, "the end at the corner is the one that gives way");
        along.End.Should().Be(new Vec2d(10, 0), "the far end is untouched");
        At(up.Start, 0, 2);
        up.End.Should().Be(new Vec2d(0, 10), "the far end is untouched");
    }

    [Fact]
    public void Fillet_PutsAnArcOfTheGivenRadiusTangentToBothLegs()
    {
        Sketch sketch = RightAngle();

        CornerResult result = SketchCorner.Fillet(sketch, On(1, 5, 0), On(2, 0, 5), 2);

        SketchArc arc = (SketchArc)result.Sketch!.Entities.Find(result.Blend!.Value)!;

        arc.Radius.Should().BeApproximately(2, Tol);
        arc.Centre.X.Should().BeApproximately(2, Tol);
        arc.Centre.Y.Should().BeApproximately(2, Tol);

        // Tangency, said as geometry rather than as a constraint: the centre is exactly a radius
        // from each leg's own line.
        System.Math.Abs(arc.Centre.Y).Should().BeApproximately(2, Tol);
        System.Math.Abs(arc.Centre.X).Should().BeApproximately(2, Tol);
    }

    [Fact]
    public void Fillet_SweepsTheMinorArcBetweenTheTwoTangentPoints()
    {
        Sketch sketch = RightAngle();

        CornerResult result = SketchCorner.Fillet(sketch, On(1, 5, 0), On(2, 0, 5), 2);

        SketchArc arc = (SketchArc)result.Sketch!.Entities.Find(result.Blend!.Value)!;

        arc.Sweep.Should().BeApproximately(
            System.Math.PI / 2, Tol, "the fillet of a right angle sweeps pi minus the corner angle");

        // Anticlockwise from (0,2) round to (2,0), bulging towards the origin -- the major arc
        // would sweep the other three quarters and bulge away from the corner entirely.
        At(arc.PointAt(0), 0, 2);
        At(arc.PointAt(1), 2, 0);
        arc.PointAt(0.5).X.Should().BeLessThan(2);
        arc.PointAt(0.5).Y.Should().BeLessThan(2);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(2)]
    [InlineData(7)]
    public void Fillet_KeepsTheArcTangentAtBothEndsWhateverTheRadius(double radius)
    {
        Sketch sketch = RightAngle();

        CornerResult result = SketchCorner.Fillet(sketch, On(1, 5, 0), On(2, 0, 5), radius);

        result.IsResolved.Should().BeTrue(result.Reason);

        SketchArc arc = (SketchArc)result.Sketch!.Entities.Find(result.Blend!.Value)!;
        SketchLine along = (SketchLine)result.Sketch.Entities.Find(Entity(1))!;
        SketchLine up = (SketchLine)result.Sketch.Entities.Find(Entity(2))!;

        Vec2d.Distance(arc.PointAt(1), along.Start).Should().BeApproximately(0, Tol);
        Vec2d.Distance(arc.PointAt(0), up.Start).Should().BeApproximately(0, Tol);
        arc.Centre.Y.Should().BeApproximately(radius, Tol);
        arc.Centre.X.Should().BeApproximately(radius, Tol);
    }

    [Fact]
    public void Fillet_OfAnObtuseCornerSetsBackFurtherThanItsRadius()
    {
        // A 135 degree corner: the tangent length r / tan(67.5 degrees) is less than r, and for a
        // 45 degree one it is more. Getting tan and its reciprocal the wrong way round passes on a
        // right angle, where they happen to agree, and fails everywhere else.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)))
            .With(new SketchLine(Entity(2), Vec2d.Zero, new Vec2d(-10, 10)));

        CornerResult result = SketchCorner.Fillet(sketch, On(1, 5, 0), On(2, -5, 5), 2);

        result.IsResolved.Should().BeTrue(result.Reason);

        SketchLine along = (SketchLine)result.Sketch!.Entities.Find(Entity(1))!;
        double expected = 2 / System.Math.Tan(3 * System.Math.PI / 8);

        expected.Should().BeLessThan(2);
        along.Start.X.Should().BeApproximately(expected, Tol);
    }

    [Fact]
    public void Fillet_ExtendsALegThatStopsShortOfTheCorner()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), new Vec2d(3, 0), new Vec2d(10, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(0, 4), new Vec2d(0, 10)));

        CornerResult result = SketchCorner.Fillet(sketch, On(1, 5, 0), On(2, 0, 6), 2);

        result.IsResolved.Should().BeTrue(result.Reason);

        SketchLine along = (SketchLine)result.Sketch!.Entities.Find(Entity(1))!;
        SketchLine up = (SketchLine)result.Sketch.Entities.Find(Entity(2))!;

        At(along.Start, 2, 0, "the leg grew back towards the corner it never reached");
        At(up.Start, 0, 2);
    }

    [Fact]
    public void Fillet_TrimsAwayTheOverhangWhenTheLegsCrossRatherThanMeet()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), new Vec2d(-5, 0), new Vec2d(10, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(0, -5), new Vec2d(0, 10)));

        CornerResult result = SketchCorner.Fillet(sketch, On(1, 5, 0), On(2, 0, 5), 2);

        result.IsResolved.Should().BeTrue(result.Reason);

        SketchLine along = (SketchLine)result.Sketch!.Entities.Find(Entity(1))!;

        At(along.Start, 2, 0, "the stub on the far side of the corner goes");
        along.End.Should().Be(new Vec2d(10, 0), "the far end is untouched");
    }

    [Fact]
    public void Fillet_FindsACornerThatIsNeitherLegsOwnStart()
    {
        // Both legs run away from the corner rather than towards it, and neither begins there, so
        // the intersection is a real solve rather than something a degenerate case hands back for
        // free -- and it is the second leg's End, not its Start, that gives way.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), new Vec2d(2, 1), new Vec2d(12, 1)))
            .With(new SketchLine(Entity(2), new Vec2d(5, 10), new Vec2d(5, 3)));

        CornerResult result = SketchCorner.Fillet(sketch, On(1, 9, 1), On(2, 5, 7), 1);

        result.IsResolved.Should().BeTrue(result.Reason);

        SketchLine along = (SketchLine)result.Sketch!.Entities.Find(Entity(1))!;
        SketchLine down = (SketchLine)result.Sketch.Entities.Find(Entity(2))!;
        SketchArc arc = (SketchArc)result.Sketch.Entities.Find(result.Blend!.Value)!;

        At(along.Start, 6, 1, "the corner is at (5,1), a setback of 1 along the surviving arm");
        along.End.Should().Be(new Vec2d(12, 1), "the far end is untouched");
        down.Start.Should().Be(new Vec2d(5, 10), "the far end is untouched");
        At(down.End, 5, 2, "this leg meets the corner at its End, not its Start");
        At(arc.Centre, 6, 2);
    }

    [Fact]
    public void Fillet_TakesTheCornerTheClicksPointAt()
    {
        // The same crossing lines, clicked on the opposite two arms. The corner in the third
        // quadrant is as real as the one in the first, and only the clicks tell them apart.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), new Vec2d(-10, 0), new Vec2d(10, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(0, -10), new Vec2d(0, 10)));

        CornerResult result = SketchCorner.Fillet(sketch, On(1, -5, 0), On(2, 0, -5), 2);

        result.IsResolved.Should().BeTrue(result.Reason);

        SketchArc arc = (SketchArc)result.Sketch!.Entities.Find(result.Blend!.Value)!;

        arc.Centre.X.Should().BeApproximately(-2, Tol);
        arc.Centre.Y.Should().BeApproximately(-2, Tol);
    }

    [Fact]
    public void Fillet_ReplacesTheCornerCoincidenceWithOneOntoEachEndOfTheArc()
    {
        Sketch sketch = RightAngle().With(SketchConstraint.Of(
            ConstraintKind.Coincident,
            [new(Entity(1), EntityPoint.Start), new(Entity(2), EntityPoint.Start)]));

        CornerResult result = SketchCorner.Fillet(sketch, On(1, 5, 0), On(2, 0, 5), 2);

        result.IsResolved.Should().BeTrue(result.Reason);

        SketchConstraint[] coincidences =
            [.. result.Sketch!.Constraints.Ordered.Where(c => c.Kind == ConstraintKind.Coincident)];

        coincidences.Should().HaveCount(2, "the corner's own coincidence is now false and goes");
        coincidences.Should().OnlyContain(c => c.On.Any(o => o.Entity == result.Blend!.Value));
        coincidences.Should().NotContain(c =>
            c.On.Any(o => o.Entity == Entity(1)) && c.On.Any(o => o.Entity == Entity(2)));
    }

    [Fact]
    public void Fillet_AddsATangencyToEachLeg()
    {
        Sketch sketch = RightAngle();

        CornerResult result = SketchCorner.Fillet(sketch, On(1, 5, 0), On(2, 0, 5), 2);

        SketchConstraint[] tangencies =
            [.. result.Sketch!.Constraints.Ordered.Where(c => c.Kind == ConstraintKind.Tangent)];

        tangencies.Should().HaveCount(2);
        tangencies.SelectMany(c => c.On).Select(o => o.Entity)
            .Should().Contain([Entity(1), Entity(2), result.Blend!.Value]);
    }

    [Fact]
    public void Fillet_LeavesAConstraintOnAWholeLegAlone()
    {
        Sketch sketch = RightAngle()
            .With(SketchConstraint.Of(ConstraintKind.Horizontal, [new(Entity(1))]));

        CornerResult result = SketchCorner.Fillet(sketch, On(1, 5, 0), On(2, 0, 5), 2);

        result.IsResolved.Should().BeTrue(result.Reason);
        result.Sketch!.Constraints.Ordered.Should().ContainSingle(c =>
            c.Kind == ConstraintKind.Horizontal,
            "a shortened line is no less horizontal than a long one");
    }

    [Fact]
    public void Fillet_RefusesWhenSomethingElseNamesAMovingCornerPoint()
    {
        Sketch sketch = RightAngle()
            .With(SketchConstraint.Of(ConstraintKind.Fix, [new(Entity(1), EntityPoint.Start)]));

        CornerResult result = SketchCorner.Fillet(sketch, On(1, 5, 0), On(2, 0, 5), 2);

        result.Outcome.Should().Be(CornerOutcome.ConstraintNotTransferable);
        result.Sketch.Should().BeNull();
    }

    [Fact]
    public void Fillet_RefusesARadiusBiggerThanALegCanGiveUp()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(3, 0)))
            .With(new SketchLine(Entity(2), Vec2d.Zero, new Vec2d(0, 10)));

        CornerResult result = SketchCorner.Fillet(sketch, On(1, 2, 0), On(2, 0, 5), 5);

        result.Outcome.Should().Be(CornerOutcome.DoesNotFit);
    }

    [Fact]
    public void Fillet_RefusesTwoParallelLines()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(0, 5), new Vec2d(10, 5)));

        CornerResult result = SketchCorner.Fillet(sketch, On(1, 5, 0), On(2, 5, 5), 2);

        result.Outcome.Should().Be(CornerOutcome.NotACorner);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(double.NaN)]
    public void Fillet_RefusesARadiusThatIsNotPositiveAndFinite(double radius)
    {
        CornerResult result = SketchCorner.Fillet(RightAngle(), On(1, 5, 0), On(2, 0, 5), radius);

        result.Outcome.Should().Be(CornerOutcome.NotACorner);
    }

    [Fact]
    public void Fillet_RefusesOneLineAgainstItself()
    {
        CornerResult result = SketchCorner.Fillet(RightAngle(), On(1, 5, 0), On(1, 2, 0), 2);

        result.Outcome.Should().Be(CornerOutcome.NotACorner);
    }

    [Fact]
    public void Fillet_FailsWhenALegIsMissing()
    {
        CornerResult result = SketchCorner.Fillet(RightAngle(), On(1, 5, 0), On(9, 0, 5), 2);

        result.Outcome.Should().Be(CornerOutcome.EntityNotFound);
    }

    [Fact]
    public void Fillet_RefusesALegThatIsNotALine()
    {
        Sketch sketch = RightAngle().With(new SketchCircle(Entity(3), Vec2d.Zero, 5));

        CornerResult result = SketchCorner.Fillet(sketch, On(1, 5, 0), On(3, 0, 5), 2);

        result.Outcome.Should().Be(CornerOutcome.Unsupported);
    }

    [Fact]
    public void Chamfer_CutsStraightAcrossBetweenTheTwoSetbackPoints()
    {
        Sketch sketch = RightAngle();

        CornerResult result = SketchCorner.Chamfer(sketch, On(1, 5, 0), On(2, 0, 5), 3);

        result.IsResolved.Should().BeTrue(result.Reason);

        SketchLine cut = (SketchLine)result.Sketch!.Entities.Find(result.Blend!.Value)!;
        SketchLine along = (SketchLine)result.Sketch.Entities.Find(Entity(1))!;
        SketchLine up = (SketchLine)result.Sketch.Entities.Find(Entity(2))!;

        cut.Start.Should().Be(new Vec2d(3, 0));
        cut.End.Should().Be(new Vec2d(0, 3));
        along.Start.Should().Be(new Vec2d(3, 0));
        up.Start.Should().Be(new Vec2d(0, 3));
    }

    [Fact]
    public void Chamfer_SetsBothLegsBackByTheSameDistanceWhateverTheAngle()
    {
        // The setback is the distance itself, not a tangent length: unlike a fillet it does not
        // depend on the corner angle at all.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)))
            .With(new SketchLine(Entity(2), Vec2d.Zero, new Vec2d(-10, 10)));

        CornerResult result = SketchCorner.Chamfer(sketch, On(1, 5, 0), On(2, -5, 5), 3);

        result.IsResolved.Should().BeTrue(result.Reason);

        SketchLine along = (SketchLine)result.Sketch!.Entities.Find(Entity(1))!;
        SketchLine up = (SketchLine)result.Sketch.Entities.Find(Entity(2))!;

        Vec2d.Distance(along.Start, Vec2d.Zero).Should().BeApproximately(3, Tol);
        Vec2d.Distance(up.Start, Vec2d.Zero).Should().BeApproximately(3, Tol);
    }

    [Fact]
    public void Chamfer_JoinsBothLegsToTheCutAndClaimsNoTangency()
    {
        Sketch sketch = RightAngle().With(SketchConstraint.Of(
            ConstraintKind.Coincident,
            [new(Entity(1), EntityPoint.Start), new(Entity(2), EntityPoint.Start)]));

        CornerResult result = SketchCorner.Chamfer(sketch, On(1, 5, 0), On(2, 0, 5), 3);

        result.IsResolved.Should().BeTrue(result.Reason);
        result.Sketch!.Constraints.Ordered.Where(c => c.Kind == ConstraintKind.Coincident)
            .Should().HaveCount(2);
        result.Sketch.Constraints.Ordered.Should().NotContain(
            c => c.Kind == ConstraintKind.Tangent, "a chamfer meets its legs at an angle");
    }

    [Fact]
    public void Chamfer_RefusesASetbackBiggerThanALegCanGiveUp()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(3, 0)))
            .With(new SketchLine(Entity(2), Vec2d.Zero, new Vec2d(0, 10)));

        CornerResult result = SketchCorner.Chamfer(sketch, On(1, 2, 0), On(2, 0, 5), 4);

        result.Outcome.Should().Be(CornerOutcome.DoesNotFit);
    }

    [Fact]
    public void Blend_IsConstructionOnlyWhenBothLegsAre()
    {
        Sketch mixed = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0), IsConstruction: true))
            .With(new SketchLine(Entity(2), Vec2d.Zero, new Vec2d(0, 10)));

        Sketch both = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0), IsConstruction: true))
            .With(new SketchLine(Entity(2), Vec2d.Zero, new Vec2d(0, 10), IsConstruction: true));

        CornerResult one = SketchCorner.Chamfer(mixed, On(1, 5, 0), On(2, 0, 5), 3);
        CornerResult two = SketchCorner.Chamfer(both, On(1, 5, 0), On(2, 0, 5), 3);

        one.Sketch!.Entities.Find(one.Blend!.Value)!.IsConstruction.Should().BeFalse();
        two.Sketch!.Entities.Find(two.Blend!.Value)!.IsConstruction.Should().BeTrue();
    }

    /// <summary>Asserts where a point ended up, to within what a tangent length can be got to.</summary>
    private static void At(Vec2d actual, double x, double y, string because = "")
        => Vec2d.Distance(actual, new Vec2d(x, y)).Should()
            .BeApproximately(0, Tol, because);

    private static Sketch RightAngle() => Sketch.Empty
        .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)))
        .With(new SketchLine(Entity(2), Vec2d.Zero, new Vec2d(0, 10)));

    private static CornerPick On(int entity, double x, double y) => new(Entity(entity), new Vec2d(x, y));

    private static SketchEntityId Entity(int n)
        => new(new Guid($"00000000-0000-0000-0000-{n:D12}"));
}
