using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Solver.Sketching;

using Xunit;

namespace OpenMCAD.Solver.Tests;

/// <summary>Shortening an entity to the nearest crossing(s), on the side of a click (P4-T13).</summary>
public sealed class SketchTrimTests
{
    [Fact]
    public void Line_ClickOnTheSegmentBeforeTheCrossingDeletesItAndShortensTheStart()
    {
        // Trim deletes the segment the user clicked on, not the segment nearest to it. Clicking at
        // (2, 0) -- on the piece between the true start and the crossing at x = 6 -- removes that
        // piece, leaving the far side: the line now starts at the crossing.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(6, -5), new Vec2d(6, 5)));

        TrimResult result = SketchTrim.Trim(sketch, Entity(1), new Vec2d(2, 0));

        result.IsResolved.Should().BeTrue();
        SketchLine trimmed = (SketchLine)result.Sketch!.Entities.Find(Entity(1))!;
        trimmed.Start.Should().Be(new Vec2d(6, 0));
        trimmed.End.Should().Be(new Vec2d(10, 0));
    }

    [Fact]
    public void Line_ClickOnTheSegmentAfterTheCrossingDeletesItAndShortensTheEnd()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(6, -5), new Vec2d(6, 5)));

        TrimResult result = SketchTrim.Trim(sketch, Entity(1), new Vec2d(8, 0));

        result.IsResolved.Should().BeTrue();
        SketchLine trimmed = (SketchLine)result.Sketch!.Entities.Find(Entity(1))!;
        trimmed.Start.Should().Be(Vec2d.Zero);
        trimmed.End.Should().Be(new Vec2d(6, 0));
    }

    [Fact]
    public void Line_ClickBetweenTwoCrossingsRefusesRatherThanSplitting()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(3, -5), new Vec2d(3, 5)))
            .With(new SketchLine(Entity(3), new Vec2d(7, -5), new Vec2d(7, 5)));

        TrimResult result = SketchTrim.Trim(sketch, Entity(1), new Vec2d(5, 0));

        result.Outcome.Should().Be(TrimOutcome.WouldSplit);
        result.Sketch.Should().BeNull();
    }

    [Fact]
    public void Line_WithNothingCrossingItRefuses()
    {
        Sketch sketch = Sketch.Empty.With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)));

        TrimResult result = SketchTrim.Trim(sketch, Entity(1), new Vec2d(5, 0));

        result.Outcome.Should().Be(TrimOutcome.NoIntersections);
    }

    [Fact]
    public void Trim_FailsWhenTheEntityIsMissing()
    {
        TrimResult result = SketchTrim.Trim(Sketch.Empty, Entity(1), Vec2d.Zero);

        result.Outcome.Should().Be(TrimOutcome.EntityNotFound);
    }

    [Fact]
    public void Trim_RefusesAnUnsupportedKind()
    {
        Sketch sketch = Sketch.Empty.With(new SketchEllipse(Entity(1), Vec2d.Zero, 5, 3));

        TrimResult result = SketchTrim.Trim(sketch, Entity(1), Vec2d.Zero);

        result.Outcome.Should().Be(TrimOutcome.Unsupported);
    }

    [Fact]
    public void Circle_TwoCrossingsLeavesTheArcOnTheOtherSideFromTheClick()
    {
        // A circle crossed at 0 degrees and 180 degrees. Clicking near the top (90 degrees) deletes
        // the top half; the bottom half survives.
        Sketch sketch = Sketch.Empty
            .With(new SketchCircle(Entity(1), Vec2d.Zero, 5))
            .With(new SketchLine(Entity(2), new Vec2d(5, -5), new Vec2d(5, 5)))
            .With(new SketchLine(Entity(3), new Vec2d(-5, -5), new Vec2d(-5, 5)));

        TrimResult result = SketchTrim.Trim(sketch, Entity(1), new Vec2d(0, 5));

        result.IsResolved.Should().BeTrue();
        SketchArc survivor = (SketchArc)result.Sketch!.Entities.Find(Entity(1))!;

        survivor.Radius.Should().Be(5);
        survivor.Centre.Should().Be(Vec2d.Zero);

        // The surviving arc passes through the bottom (0, -5) and not the top (0, 5).
        bool passesThroughBottom = Enumerable.Range(0, 21)
            .Select(i => survivor.PointAt(i / 20.0))
            .Any(p => (p - new Vec2d(0, -5)).Length < 1e-6);
        bool passesThroughTop = Enumerable.Range(0, 21)
            .Select(i => survivor.PointAt(i / 20.0))
            .Any(p => (p - new Vec2d(0, 5)).Length < 1e-6);

        passesThroughBottom.Should().BeTrue();
        passesThroughTop.Should().BeFalse();
    }

    [Fact]
    public void Circle_WithFewerThanTwoCrossingsRefuses()
    {
        // Tangent to a single line: one crossing only.
        Sketch sketch = Sketch.Empty
            .With(new SketchCircle(Entity(1), Vec2d.Zero, 5))
            .With(new SketchLine(Entity(2), new Vec2d(-5, 5), new Vec2d(5, 5)));

        TrimResult result = SketchTrim.Trim(sketch, Entity(1), new Vec2d(0, 5));

        result.Outcome.Should().Be(TrimOutcome.NoIntersections);
    }

    [Fact]
    public void Arc_ClickBeforeTheCrossingShortensTheStart()
    {
        // A half-circle from 0 to 180 degrees, crossed at 90 degrees by a vertical line.
        Sketch sketch = Sketch.Empty
            .With(new SketchArc(Entity(1), Vec2d.Zero, 5, 0, System.Math.PI))
            .With(new SketchLine(Entity(2), new Vec2d(0, -5), new Vec2d(0, 5)));

        TrimResult result = SketchTrim.Trim(sketch, Entity(1), new Vec2d(4.7, 1.5)); // near 18 degrees

        result.IsResolved.Should().BeTrue();
        SketchArc trimmed = (SketchArc)result.Sketch!.Entities.Find(Entity(1))!;

        trimmed.StartAngle.Should().BeApproximately(System.Math.PI / 2, 1e-9);
        trimmed.EndAngle.Should().BeApproximately(System.Math.PI, 1e-9);
    }

    [Fact]
    public void Arc_ClickAfterTheCrossingShortensTheEnd()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchArc(Entity(1), Vec2d.Zero, 5, 0, System.Math.PI))
            .With(new SketchLine(Entity(2), new Vec2d(0, -5), new Vec2d(0, 5)));

        TrimResult result = SketchTrim.Trim(sketch, Entity(1), new Vec2d(-4.7, 1.5)); // near 162 degrees

        result.IsResolved.Should().BeTrue();
        SketchArc trimmed = (SketchArc)result.Sketch!.Entities.Find(Entity(1))!;

        trimmed.StartAngle.Should().BeApproximately(0, 1e-9);
        trimmed.EndAngle.Should().BeApproximately(System.Math.PI / 2, 1e-9);
    }

    [Fact]
    public void Arc_ClickBetweenTwoCrossingsRefusesRatherThanSplitting()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchArc(Entity(1), Vec2d.Zero, 5, 0, System.Math.PI))
            .With(new SketchLine(Entity(2), new Vec2d(1, -5), new Vec2d(1, 5)))
            .With(new SketchLine(Entity(3), new Vec2d(-1, -5), new Vec2d(-1, 5)));

        TrimResult result = SketchTrim.Trim(sketch, Entity(1), new Vec2d(0, 5));

        result.Outcome.Should().Be(TrimOutcome.WouldSplit);
    }

    private static SketchEntityId Entity(int n)
        => new(new Guid($"00000000-0000-0000-0000-{n:D12}"));
}
