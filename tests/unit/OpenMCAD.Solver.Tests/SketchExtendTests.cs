using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Solver.Sketching;

using Xunit;

namespace OpenMCAD.Solver.Tests;

/// <summary>Lengthening the end of a line nearest a click until it reaches the sketch (P4-T13).</summary>
public sealed class SketchExtendTests
{
    [Fact]
    public void ClickNearTheEndExtendsTheEndToTheNearestLine()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(4, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(10, -5), new Vec2d(10, 5)));

        ExtendResult result = SketchExtend.Extend(sketch, Entity(1), new Vec2d(4, 0));

        result.IsResolved.Should().BeTrue();
        SketchLine extended = (SketchLine)result.Sketch!.Entities.Find(Entity(1))!;
        extended.Start.Should().Be(Vec2d.Zero);
        extended.End.Should().Be(new Vec2d(10, 0));
    }

    [Fact]
    public void ClickNearTheStartExtendsTheStartBackward()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), new Vec2d(10, 0), new Vec2d(14, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(6, -5), new Vec2d(6, 5)));

        ExtendResult result = SketchExtend.Extend(sketch, Entity(1), new Vec2d(10, 0));

        result.IsResolved.Should().BeTrue();
        SketchLine extended = (SketchLine)result.Sketch!.Entities.Find(Entity(1))!;
        extended.Start.Should().Be(new Vec2d(6, 0));
        extended.End.Should().Be(new Vec2d(14, 0));
    }

    [Fact]
    public void ExtendingToACirclePicksTheNearerOfTwoIntersections()
    {
        // The X axis meets a circle centred at (10, 0) with radius 3 at x = 7 and x = 13. Extending
        // forward from (4, 0), the nearer one -- x = 7 -- is what a real extend reaches first.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(4, 0)))
            .With(new SketchCircle(Entity(2), new Vec2d(10, 0), 3));

        ExtendResult result = SketchExtend.Extend(sketch, Entity(1), new Vec2d(4, 0));

        result.IsResolved.Should().BeTrue();
        SketchLine extended = (SketchLine)result.Sketch!.Entities.Find(Entity(1))!;
        extended.End.X.Should().BeApproximately(7, 1e-9);
        extended.End.Y.Should().BeApproximately(0, 1e-9);
    }

    [Fact]
    public void ExtendingToAnArcSkipsAnIntersectionOutsideItsSweep()
    {
        // A vertical line from (0, 0) to (0, 4), extended upward. The full circle centred at
        // (0, 10) with radius 3 would be met at y = 7 and y = 13, but the arc here covers only the
        // point at y = 13 (angle 90 degrees from its centre); the y = 7 point (angle -90 degrees) is
        // outside its sweep and must not be reached instead, even though it is nearer.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(0, 4)))
            .With(new SketchArc(
                Entity(2), new Vec2d(0, 10), 3, System.Math.PI / 4, 3 * System.Math.PI / 4));

        ExtendResult result = SketchExtend.Extend(sketch, Entity(1), new Vec2d(0, 4));

        result.IsResolved.Should().BeTrue();
        SketchLine extended = (SketchLine)result.Sketch!.Entities.Find(Entity(1))!;
        extended.End.X.Should().BeApproximately(0, 1e-9);
        extended.End.Y.Should().BeApproximately(13, 1e-9);
    }

    [Fact]
    public void NothingAheadOfTheEndRefuses()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(4, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(-10, -5), new Vec2d(-10, 5)));

        ExtendResult result = SketchExtend.Extend(sketch, Entity(1), new Vec2d(4, 0));

        result.Outcome.Should().Be(ExtendOutcome.NoIntersections);
    }

    [Fact]
    public void Extend_FailsWhenTheEntityIsMissing()
    {
        ExtendResult result = SketchExtend.Extend(Sketch.Empty, Entity(1), Vec2d.Zero);

        result.Outcome.Should().Be(ExtendOutcome.EntityNotFound);
    }

    [Fact]
    public void Extend_RefusesAnUnsupportedKind()
    {
        Sketch sketch = Sketch.Empty.With(new SketchCircle(Entity(1), Vec2d.Zero, 5));

        ExtendResult result = SketchExtend.Extend(sketch, Entity(1), Vec2d.Zero);

        result.Outcome.Should().Be(ExtendOutcome.Unsupported);
    }

    private static SketchEntityId Entity(int n)
        => new(new Guid($"00000000-0000-0000-0000-{n:D12}"));
}
