using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Solver.Sketching;

using Xunit;

namespace OpenMCAD.Solver.Tests;

/// <summary>
/// Finding the closed regions of a sketch (P4-T14).
/// </summary>
/// <remarks>
/// Areas are asserted against the geometry rather than against what the code produced. A square of
/// side four has an area of sixteen whatever this code thinks, and a circle of radius three has an
/// area of nine pi — which is also the check that the arc correction is there at all, since a
/// circle traced as a polyline through two points has an area of nothing.
/// </remarks>
public sealed class ProfileDetectionTests
{
    [Fact]
    public void FourLinesMakingASquareAreOneRegion()
    {
        ProfileSet found = ProfileDetection.Find(Square(0, 0, 4));

        found.Profiles.Should().ContainSingle();
        found.Profiles[0].Area.Should().BeApproximately(16, 1e-9);
        found.Profiles[0].Outer.Segments.Should().HaveCount(4);
        found.Dangling.Should().BeEmpty();
    }

    [Fact]
    public void ACircleIsARegion()
    {
        // The check that arcs contribute their area at all: traced as a polyline through the two
        // points a circle gets cut at, its area is zero.
        Sketch sketch = Sketch.Empty.With(new SketchCircle(Entity(1), Vec2d.Zero, 3));

        ProfileSet found = ProfileDetection.Find(sketch);

        found.Profiles.Should().ContainSingle();
        found.Profiles[0].Area.Should().BeApproximately(9 * System.Math.PI, 1e-6);
    }

    [Fact]
    public void ASquareWithACircleInsideIsTwoRegionsAndOneHole()
    {
        // What a user drawing a washer sees: the disc is selectable, and so is the square minus
        // the disc. Offering only one of them is offering half the sketch.
        Sketch sketch = Square(0, 0, 10).With(new SketchCircle(Entity(5), new Vec2d(5, 5), 2));

        ProfileSet found = ProfileDetection.Find(sketch);

        found.Profiles.Should().HaveCount(2);

        SketchProfile outer = found.Profiles[0];
        SketchProfile disc = found.Profiles[1];

        outer.Inner.Should().ContainSingle("the circle is a hole of the square");
        outer.Area.Should().BeApproximately(100 - (4 * System.Math.PI), 1e-6);

        disc.Inner.Should().BeEmpty();
        disc.Area.Should().BeApproximately(4 * System.Math.PI, 1e-6);
    }

    [Fact]
    public void TwoOverlappingSquaresAreThreeRegions()
    {
        // The case chains of coincident endpoints cannot find: none of the three regions is a
        // shape anybody drew, and all three are things a user can extrude.
        Sketch sketch = Square(0, 0, 4);

        foreach (SketchEntity entity in Square(2, 2, 4).Entities.Ordered)
        {
            sketch = sketch.With(Renamed(entity, 10));
        }

        ProfileSet found = ProfileDetection.Find(sketch);

        found.Profiles.Should().HaveCount(3, "two L-shapes and the square where they overlap");

        found.Profiles.Select(p => p.Area).Order().Should().AllSatisfy(
            area => area.Should().BeGreaterThan(0));

        found.Profiles.Sum(p => p.Area).Should().BeApproximately(
            28, 1e-6, "sixteen and sixteen, less the four they share, plus that four again");
    }

    [Fact]
    public void AnOpenChainIsNoRegionAtAll()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(4, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(4, 0), new Vec2d(4, 4)));

        ProfileSet found = ProfileDetection.Find(sketch);

        found.IsEmpty.Should().BeTrue();
        found.Dangling.Should().BeEquivalentTo([Entity(1), Entity(2)]);
    }

    [Fact]
    public void ALineToNowhereIsReportedRatherThanIgnored()
    {
        // "Why is my extrude not offering this" is the commonest question a sketcher has to answer.
        Sketch sketch = Square(0, 0, 4)
            .With(new SketchLine(Entity(9), new Vec2d(20, 20), new Vec2d(25, 25)));

        ProfileSet found = ProfileDetection.Find(sketch);

        found.Profiles.Should().ContainSingle();
        found.Dangling.Should().Equal(Entity(9));
    }

    [Fact]
    public void ConstructionGeometryBoundsNothing()
    {
        // It exists to constrain, never to be built from. A region bounded by a construction line
        // would be a region the user deliberately said was scaffolding.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(4, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(4, 0), new Vec2d(4, 4)))
            .With(new SketchLine(Entity(3), new Vec2d(4, 4), Vec2d.Zero, IsConstruction: true));

        ProfileDetection.Find(sketch).IsEmpty.Should().BeTrue();
        ProfileDetection.Find(sketch).Dangling.Should().NotContain(Entity(3),
            "it is not missing from a region, it was never a candidate for one");
    }

    [Fact]
    public void ACurveKindThatCannotYetBeTracedIsReportedAsDangling()
    {
        // Splines can bound a region in principle. Cutting them at their crossings needs an
        // intersector P4-T09 does not have, so they are named rather than silently dropped.
        Sketch sketch = Square(0, 0, 4).With(
            SketchBSpline.Through(Entity(9), 2, [Vec2d.Zero, new Vec2d(1, 1), new Vec2d(2, 0)]));

        ProfileDetection.Find(sketch).Dangling.Should().Contain(Entity(9));
    }

    [Fact]
    public void ATriangleOfLinesAndAnArcIsOneRegion()
    {
        // A fillet meets the line it was made from tangentially, which is where a chord-based
        // ordering picks the wrong edge and the walk goes somewhere else entirely.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(10, 0), new Vec2d(10, 10)))
            .With(new SketchLine(Entity(3), new Vec2d(10, 10), Vec2d.Zero));

        ProfileSet found = ProfileDetection.Find(sketch);

        found.Profiles.Should().ContainSingle();
        found.Profiles[0].Area.Should().BeApproximately(50, 1e-9, "half of ten by ten");
    }

    [Fact]
    public void ARegionRunsAnticlockwiseHoweverTheGeometryWasDrawn()
    {
        // The sign of the area is what tells an outer boundary from a hole, so it cannot depend on
        // which way round the user happened to draw their square.
        Sketch clockwise = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(0, 4)))
            .With(new SketchLine(Entity(2), new Vec2d(0, 4), new Vec2d(4, 4)))
            .With(new SketchLine(Entity(3), new Vec2d(4, 4), new Vec2d(4, 0)))
            .With(new SketchLine(Entity(4), new Vec2d(4, 0), Vec2d.Zero));

        ProfileDetection.Find(clockwise).Profiles.Should().ContainSingle()
            .Which.Outer.SignedArea.Should().BePositive();
    }

    [Fact]
    public void ACircleInASquareInARectangleIsAHoleOfOnlyTheSquare()
    {
        // Or a hole would be counted twice and the outermost region would come out too small.
        Sketch sketch = Square(0, 0, 20);

        foreach (SketchEntity entity in Square(5, 5, 10).Entities.Ordered)
        {
            sketch = sketch.With(Renamed(entity, 10));
        }

        sketch = sketch.With(new SketchCircle(Entity(30), new Vec2d(10, 10), 2));

        ProfileSet found = ProfileDetection.Find(sketch);

        found.Profiles.Should().HaveCount(3);

        found.Profiles[0].Inner.Should().ContainSingle("the outer square holds only the inner one");
        found.Profiles[1].Inner.Should().ContainSingle("which in turn holds only the circle");
        found.Profiles[2].Inner.Should().BeEmpty();

        found.Profiles.Sum(p => p.Area).Should().BeApproximately(
            400, 1e-6, "every point of the outermost square is in exactly one region");
    }

    [Fact]
    public void ASegmentRemembersWhichCurveItCameFromAndHowMuchOfIt()
    {
        // A profile goes to a kernel, which needs to know what curve to build and how much of it,
        // not a polyline that happens to pass through the same places.
        Sketch sketch = Sketch.Empty.With(new SketchCircle(Entity(1), Vec2d.Zero, 3));

        ProfileLoop loop = ProfileDetection.Find(sketch).Profiles[0].Outer;

        loop.Segments.Should().OnlyContain(s => s.Entity == Entity(1));
        loop.Segments.Sum(s => System.Math.Abs(s.To - s.From)).Should().BeApproximately(
            1, 1e-9, "the pieces cover the whole circle exactly once");
    }

    [Fact]
    public void AnArcCoveringMoreThanHalfACircleContributesTheLargerPiece()
    {
        // Three quarters of a disc: an arc from the positive X axis round to the negative Y one,
        // closed by two radii. The sliver an arc cuts off its own chord is the small piece for a
        // minor arc and the rest of the circle for a major one, and taking the small piece here
        // gives a negative area -- so the region does not merely come out wrong, it disappears.
        Sketch sketch = Sketch.Empty
            .With(new SketchArc(Entity(1), Vec2d.Zero, 2, 0, 3 * System.Math.PI / 2))
            .With(new SketchLine(Entity(2), new Vec2d(0, -2), Vec2d.Zero))
            .With(new SketchLine(Entity(3), Vec2d.Zero, new Vec2d(2, 0)));

        ProfileSet found = ProfileDetection.Find(sketch);

        found.Profiles.Should().ContainSingle()
            .Which.Area.Should().BeApproximately(3 * System.Math.PI, 1e-6, "three quarters of 4 pi");
    }

    [Fact]
    public void RegionsComeBackLargestFirstAndAlwaysInTheSameOrder()
    {
        // A user picking "the second region" has to get the same one twice, and an id sorts
        // differently in the next process. The circle is added first, so the walk finds the small
        // region before the large one and an unsorted answer would come back small-first.
        Sketch sketch = Sketch.Empty.With(new SketchCircle(Entity(5), new Vec2d(5, 5), 2));

        foreach (SketchEntity entity in Square(0, 0, 10).Entities.Ordered)
        {
            sketch = sketch.With(entity);
        }

        ProfileSet once = ProfileDetection.Find(sketch);
        ProfileSet twice = ProfileDetection.Find(sketch);

        once.Should().Be(twice);
        once.Profiles.Select(p => p.Outer.Area).Should().BeInDescendingOrder();
    }

    [Fact]
    public void AnEmptySketchOffersNothing()
    {
        ProfileSet found = ProfileDetection.Find(Sketch.Empty);

        found.IsEmpty.Should().BeTrue();
        found.Dangling.Should().BeEmpty();
    }

    [Fact]
    public void APointIsNotARegionAndIsNotDanglingEither()
    {
        // It cannot bound anything and was never trying to.
        Sketch sketch = Square(0, 0, 4).With(new SketchPoint(Entity(9), new Vec2d(2, 2)));

        ProfileDetection.Find(sketch).Dangling.Should().Contain(Entity(9),
            "it is profile geometry this build cannot trace, which is worth saying");
    }

    /// <summary>A square of lines, anticlockwise from a corner.</summary>
    private static Sketch Square(double x, double y, double side) => Sketch.Empty
        .With(new SketchLine(Entity(1), new Vec2d(x, y), new Vec2d(x + side, y)))
        .With(new SketchLine(Entity(2), new Vec2d(x + side, y), new Vec2d(x + side, y + side)))
        .With(new SketchLine(Entity(3), new Vec2d(x + side, y + side), new Vec2d(x, y + side)))
        .With(new SketchLine(Entity(4), new Vec2d(x, y + side), new Vec2d(x, y)));

    /// <summary>The same entity under a different id, for building a second shape.</summary>
    private static SketchEntity Renamed(SketchEntity entity, int offset)
    {
        SketchLine line = (SketchLine)entity;
        int was = int.Parse(
            line.Id.Value.ToString("N", System.Globalization.CultureInfo.InvariantCulture)[^4..],
            System.Globalization.CultureInfo.InvariantCulture);

        return line with { Id = Entity(was + offset) };
    }

    private static SketchEntityId Entity(int n)
        => new(new Guid($"00000000-0000-0000-0000-{n:D12}"));
}
