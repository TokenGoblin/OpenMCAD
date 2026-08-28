using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Solver.Sketching;

using Xunit;

namespace OpenMCAD.Solver.Tests;

/// <summary>
/// Working out where the cursor really means to be (P4-T09).
/// </summary>
/// <remarks>
/// Snapping and inference are two answers to one proximity search, deliberately kept apart:
/// snapping moves the cursor, inference proposes a constraint. A user dropping a point on a line
/// wants it on the line whether or not a constraint follows.
/// </remarks>
public sealed class SketchSnappingTests
{
    [Fact]
    public void TheCursorCatchesOnAnEndpoint()
    {
        Sketch sketch = Sketch.Empty.With(
            new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(5, 0)));

        SnapCandidate caught = SketchSnapping.Snap(sketch, new Vec2d(5.2, 0.1))!;

        caught.Kind.Should().Be(SnapKind.Point);
        caught.At.Should().Be(new Vec2d(5, 0));
        caught.Caught.Should().Contain(Point(1, EntityPoint.End));
    }

    [Fact]
    public void TheCursorCatchesOnAMidpoint()
    {
        Sketch sketch = Sketch.Empty.With(
            new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)));

        SketchSnapping.Snap(sketch, new Vec2d(5.1, 0.1))!.Kind.Should().Be(SnapKind.Midpoint);
    }

    [Fact]
    public void TheCursorCatchesOnACurve()
    {
        Sketch sketch = Sketch.Empty.With(
            new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)));

        SnapCandidate caught = SketchSnapping.Snap(sketch, new Vec2d(3, 0.2))!;

        caught.Kind.Should().Be(SnapKind.OnCurve);
        caught.At.Should().Be(new Vec2d(3, 0), "projected onto the line, not left beside it");
    }

    [Fact]
    public void TheCursorCatchesOnAQuadrant()
    {
        Sketch sketch = Sketch.Empty.With(new SketchCircle(Entity(1), Vec2d.Zero, 5));

        SnapCandidate caught = SketchSnapping.Snap(sketch, new Vec2d(0.1, 5.1))!;

        caught.Kind.Should().Be(SnapKind.Quadrant);
        caught.At.Should().BeApproximately(new Vec2d(0, 5), 1e-9);
    }

    [Fact]
    public void TheCursorCatchesWhereTwoLinesCross()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), new Vec2d(-5, 0), new Vec2d(5, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(2, -5), new Vec2d(2, 5)));

        SnapCandidate caught = SketchSnapping.Snap(sketch, new Vec2d(2.1, 0.1))!;

        caught.Kind.Should().Be(SnapKind.Intersection);
        caught.At.Should().BeApproximately(new Vec2d(2, 0), 1e-9);
        caught.Caught.Should().HaveCount(2, "a crossing belongs to both curves");
    }

    [Fact]
    public void TwoLinesThatWouldCrossIfTheyWereLongerDoNotCross()
    {
        // A sketcher that said they did would put a point where there is nothing to see.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), new Vec2d(-5, 0), new Vec2d(-1, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(2, -5), new Vec2d(2, 5)));

        SketchSnapping.Crossings(
            sketch.Entities.Find(Entity(1))!, sketch.Entities.Find(Entity(2))!)
            .Should().BeEmpty();
    }

    [Fact]
    public void ALineCrossingACircleCrossesItTwice()
    {
        SketchLine line = new(Entity(1), new Vec2d(-10, 0), new Vec2d(10, 0));
        SketchCircle circle = new(Entity(2), Vec2d.Zero, 5);

        ImmutableArray<Vec2d> crossings = SketchSnapping.Crossings(line, circle);

        crossings.Should().HaveCount(2);
        crossings.Select(c => c.X).Order().Should().Equal([-5, 5]);
    }

    [Fact]
    public void TwoCirclesCrossWhereTheGeometrySaysTheyDo()
    {
        // Radius 5 about the origin and radius 5 about (8, 0): the classic 3-4-5, crossing at
        // x = 4 and y = plus or minus 3.
        SketchCircle one = new(Entity(1), Vec2d.Zero, 5);
        SketchCircle other = new(Entity(2), new Vec2d(8, 0), 5);

        ImmutableArray<Vec2d> crossings = SketchSnapping.Crossings(one, other);

        crossings.Should().HaveCount(2);
        crossings.Should().OnlyContain(c => System.Math.Abs(c.X - 4) < 1e-9);
        crossings.Select(c => c.Y).Order().Should().Equal([-3, 3]);
    }

    [Fact]
    public void CirclesTooFarApartDoNotCross()
    {
        SketchSnapping.Crossings(
            new SketchCircle(Entity(1), Vec2d.Zero, 1),
            new SketchCircle(Entity(2), new Vec2d(50, 0), 1))
            .Should().BeEmpty();
    }

    [Fact]
    public void ACrossingOutsideAnArcsSweepIsNotACrossing()
    {
        // The line crosses the circle the arc came from, on the half the arc does not cover.
        SketchLine line = new(Entity(1), new Vec2d(-10, 0), new Vec2d(10, 0));
        SketchArc arc = new(Entity(2), Vec2d.Zero, 5, 0, System.Math.PI / 2);

        ImmutableArray<Vec2d> crossings = SketchSnapping.Crossings(line, arc);

        crossings.Should().ContainSingle()
            .Which.X.Should().BeApproximately(5, 1e-9, "only the end inside the sweep");
    }

    [Fact]
    public void TheCursorCatchesOnTheContinuationOfALine()
    {
        Sketch sketch = Sketch.Empty.With(
            new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(5, 0)));

        SnapCandidate caught = SketchSnapping.Snap(sketch, new Vec2d(9, 0.2))!;

        caught.Kind.Should().Be(SnapKind.Extension);
        caught.At.Should().BeApproximately(new Vec2d(9, 0), 1e-9);
    }

    [Fact]
    public void BetweenTheEndsIsTheLineItselfAndNotItsExtension()
    {
        Sketch sketch = Sketch.Empty.With(
            new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)));

        SketchSnapping.Snap(sketch, new Vec2d(3, 0.2))!.Kind
            .Should().Be(SnapKind.OnCurve, "which is a better answer than 'in line with'");
    }

    [Fact]
    public void TheCursorCatchesOnAGuideThroughWhereDrawingBegan()
    {
        // Drawing from (0, 4), with a horizontal line elsewhere in the sketch: the cursor should
        // catch on the horizontal through the anchor, and on the vertical square to it.
        Sketch sketch = Sketch.Empty.With(
            new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(5, 0)));

        SnapCandidate parallel = SketchSnapping.Snap(
            sketch, new Vec2d(9, 4.2), SnapOptions.Default, from: new Vec2d(0, 4))!;

        parallel.Kind.Should().Be(SnapKind.Guide);
        parallel.At.Should().BeApproximately(new Vec2d(9, 4), 1e-9);
        parallel.Glyph.Should().Be("guide-parallel");

        SnapCandidate square = SketchSnapping.Snap(
            sketch, new Vec2d(0.2, 9), SnapOptions.Default, from: new Vec2d(0, 4))!;

        square.Glyph.Should().Be("guide-perpendicular");
    }

    [Fact]
    public void NoGuidesAreOfferedWhenNothingIsBeingDrawn()
    {
        // A guide from nowhere is a line across the whole sketch that catches the cursor at random.
        // The line is deliberately not through the origin: a guide taken from a default anchor of
        // nowhere would run along the axis and catch this cursor, while the line's own extension
        // is four units away and does not.
        Sketch sketch = Sketch.Empty.With(
            new SketchLine(Entity(1), new Vec2d(0, 4), new Vec2d(5, 4)));

        SketchSnapping.Snap(sketch, new Vec2d(30, 0.2)).Should().BeNull(
            "nothing is being drawn, so there is nowhere for a guide to run through");
    }

    [Fact]
    public void TheExtensionOfALineDoesNotCoverTheLine()
    {
        // Between the ends is the line itself. With the curve snap turned off, the extension must
        // still decline: "in line with" is a statement about being past an end.
        Sketch sketch = Sketch.Empty.With(
            new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)));

        SketchSnapping.Snap(sketch, new Vec2d(5, 0.1),
            SnapOptions.Default with { Enabled = [SnapKind.Extension] })
            .Should().BeNull();
    }

    [Fact]
    public void ACircleInsideAnotherDoesNotCrossIt()
    {
        // Too far apart is the obvious case; wholly contained is the one that is easy to leave out,
        // and leaving it out invents a crossing at a place neither circle passes through.
        SketchSnapping.Crossings(
            new SketchCircle(Entity(1), new Vec2d(1, 0), 1),
            new SketchCircle(Entity(2), Vec2d.Zero, 5))
            .Should().BeEmpty();
    }

    [Fact]
    public void TheCursorFallsBackToTheGrid()
    {
        SnapCandidate caught = SketchSnapping.Snap(
            Sketch.Empty, new Vec2d(4.4, 7.6), SnapOptions.Default with { Grid = 1 })!;

        caught.Kind.Should().Be(SnapKind.Grid);
        caught.At.Should().Be(new Vec2d(4, 8));
        caught.Caught.Should().BeEmpty("a grid point belongs to nothing");
    }

    [Fact]
    public void RealGeometryAlwaysBeatsTheGrid()
    {
        // A grid is what the cursor catches on when it caught on nothing else.
        Sketch sketch = Sketch.Empty.With(
            new SketchLine(Entity(1), new Vec2d(0.4, 0), new Vec2d(5.4, 0)));

        SnapCandidate caught = SketchSnapping.Snap(
            sketch, new Vec2d(0.45, 0.05), SnapOptions.Default with { Grid = 1 })!;

        caught.Kind.Should().Be(SnapKind.Point);
        caught.At.Should().Be(new Vec2d(0.4, 0));
    }

    [Fact]
    public void TheGridSnapsHoweverFarAwayTheCursorIs()
    {
        // It rounds rather than needing to be within a tolerance, or a user with the grid on would
        // find it working only sometimes.
        // Four units from the nearest grid point, with a tolerance of one. Rounding regardless is
        // the point: a grid that only worked when the cursor was already nearly on it would be a
        // grid that worked sometimes.
        SketchSnapping.Snap(
            Sketch.Empty, new Vec2d(404, 704), SnapOptions.Default with { Grid = 10 })!
            .At.Should().Be(new Vec2d(400, 700));
    }

    [Theory]
    [InlineData(SnapKind.Point, SnapKind.Intersection)]
    [InlineData(SnapKind.Intersection, SnapKind.Midpoint)]
    [InlineData(SnapKind.Midpoint, SnapKind.Quadrant)]
    [InlineData(SnapKind.Quadrant, SnapKind.OnCurve)]
    [InlineData(SnapKind.OnCurve, SnapKind.Extension)]
    [InlineData(SnapKind.Extension, SnapKind.Guide)]
    [InlineData(SnapKind.Guide, SnapKind.Grid)]
    public void WhatTheCursorCatchesOnIsOrderedByHowMuchItMeans(SnapKind better, SnapKind worse)
    {
        // A named point of real geometry is what the user almost certainly aimed at; a grid
        // intersection is what they get when they aimed at nothing.
        ((int)better).Should().BeGreaterThan((int)worse);
    }

    [Fact]
    public void NothingIsCaughtWhileTheModifierIsHeld()
    {
        Sketch sketch = Sketch.Empty.With(
            new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(5, 0)));

        SketchSnapping.Snap(
            sketch,
            new Vec2d(5, 0),
            SnapOptions.Default with { Suppressed = true, Grid = 1 })
            .Should().BeNull();
    }

    [Fact]
    public void OnlyTheKindsAskedForAreLookedFor()
    {
        Sketch sketch = Sketch.Empty.With(
            new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(5, 0)));

        SnapOptions onlyCurves = SnapOptions.Default with { Enabled = [SnapKind.OnCurve] };

        SketchSnapping.Snap(sketch, new Vec2d(0.05, 0.05), onlyCurves)!.Kind
            .Should().Be(SnapKind.OnCurve, "the endpoint was not being looked for");
    }

    [Fact]
    public void WhatIsBeingDrawnIsNotSomethingToCatchOn()
    {
        // A line snapping to its own endpoint would collapse the moment it was drawn.
        Sketch sketch = Sketch.Empty.With(
            new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(5, 0)));

        SketchSnapping.Snap(
            sketch, new Vec2d(5, 0), SnapOptions.Default, from: null, ignore: Entity(1))
            .Should().BeNull();
    }

    [Fact]
    public void HowNearCountsAsNearIsTheCallersToSet()
    {
        Sketch sketch = Sketch.Empty.With(
            new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(5, 0)));

        // Off the line's axis as well as past its end, or the extension would catch it whatever
        // the tolerance -- which it should, and which is a different test.
        SketchSnapping.Snap(sketch, new Vec2d(5.5, 0.5),
            SnapOptions.Default with { Tolerance = 0.1 }).Should().BeNull();

        SketchSnapping.Snap(sketch, new Vec2d(5.5, 0.5),
            SnapOptions.Default with { Tolerance = 2 })!.Kind.Should().Be(SnapKind.Point);
    }

    [Fact]
    public void TheNearestOfTwoEqualCandidatesWins()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(5, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(5.4, 0), new Vec2d(9, 0)));

        SketchSnapping.Snap(sketch, new Vec2d(5.3, 0))!
            .At.Should().Be(new Vec2d(5.4, 0), "a tenth away beats three tenths");
    }

    [Fact]
    public void TheSameCursorAlwaysCatchesOnTheSameThing()
    {
        // A sketcher nobody can predict is one nobody can aim with.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(9), Vec2d.Zero, new Vec2d(5, 0)))
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(0, 5)));

        SnapCandidate once = SketchSnapping.Snap(sketch, new Vec2d(0.05, 0.05))!;
        SnapCandidate twice = SketchSnapping.Snap(sketch, new Vec2d(0.05, 0.05))!;

        once.Should().Be(twice);
        once.Caught[0].Entity.Should().Be(
            Entity(9), "the first line in the sketch, whatever its id sorts like");
    }

    [Fact]
    public void APointOnACircleOutsideAnArcsSweepIsNotOnTheArc()
    {
        Sketch sketch = Sketch.Empty.With(
            new SketchArc(Entity(1), Vec2d.Zero, 5, 0, System.Math.PI / 2));

        SketchSnapping.Snap(sketch, new Vec2d(-5, 0.1),
            SnapOptions.Default with { Tolerance = 0.5 })
            .Should().BeNull("that is a quarter turn outside the sweep");
    }

    private static SketchPointRef Point(int entity, EntityPoint point)
        => new(Entity(entity), point);

    private static SketchEntityId Entity(int n)
        => new(new Guid($"00000000-0000-0000-0000-{n:D12}"));
}

/// <summary>Point comparison that reads well in a geometric assertion.</summary>
internal static class SnapAssertions
{
    /// <summary>Asserts that a point is where it should be.</summary>
    public static void BeApproximately(
        this FluentAssertions.Primitives.ObjectAssertions assertions,
        Vec2d expected,
        double tolerance,
        string because = "")
    {
        Vec2d actual = (Vec2d)assertions.Subject;

        actual.X.Should().BeApproximately(expected.X, tolerance, because);
        actual.Y.Should().BeApproximately(expected.Y, tolerance, because);
    }
}
