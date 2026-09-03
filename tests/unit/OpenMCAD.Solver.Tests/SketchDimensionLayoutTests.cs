using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Solver.Sketching;

using Xunit;

namespace OpenMCAD.Solver.Tests;

/// <summary>
/// Laying out a dimension's witness lines, dimension line and text against the current geometry
/// (P4-T12).
/// </summary>
public sealed class SketchDimensionLayoutTests
{
    [Fact]
    public void Aligned_OffsetsTheDimensionLineToTheWitnessPoint()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchPoint(Entity(1), Vec2d.Zero))
            .With(new SketchPoint(Entity(2), new Vec2d(4, 0)))
            .With(Constraint(1, ConstraintKind.Distance, [Whole(1), Whole(2)], 4));

        SketchDimension dimension = new(SketchDimensionId.New(), ConstraintId(1), new Vec2d(2, 3));

        DimensionLayout layout = SketchDimensionLayout.Resolve(dimension, sketch);

        layout.IsResolved.Should().BeTrue();
        layout.Value.Should().Be(4);
        layout.DimensionLine!.Value.Start.Should().Be(new Vec2d(0, 3));
        layout.DimensionLine!.Value.End.Should().Be(new Vec2d(4, 3));
        layout.Witnesses.Should().Equal(
            (Vec2d.Zero, new Vec2d(0, 3)), (new Vec2d(4, 0), new Vec2d(4, 3)));
    }

    [Fact]
    public void Aligned_OffsetsToTheOtherSideWhenTheWitnessPointIsThere()
    {
        // The dimension line follows whichever side of the measured line the user put the text on
        // -- not a fixed convention -- so dragging the dimension across the line has to flip it.
        Sketch sketch = Sketch.Empty
            .With(new SketchPoint(Entity(1), Vec2d.Zero))
            .With(new SketchPoint(Entity(2), new Vec2d(4, 0)))
            .With(Constraint(1, ConstraintKind.Distance, [Whole(1), Whole(2)], 4));

        SketchDimension dimension = new(SketchDimensionId.New(), ConstraintId(1), new Vec2d(2, -3));

        DimensionLayout layout = SketchDimensionLayout.Resolve(dimension, sketch);

        layout.DimensionLine!.Value.Start.Should().Be(new Vec2d(0, -3));
        layout.DimensionLine!.Value.End.Should().Be(new Vec2d(4, -3));
    }

    [Fact]
    public void Aligned_IsDegenerateWhenThePointsCoincide()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchPoint(Entity(1), Vec2d.Zero))
            .With(new SketchPoint(Entity(2), Vec2d.Zero))
            .With(Constraint(1, ConstraintKind.Distance, [Whole(1), Whole(2)], 0));

        SketchDimension dimension = new(SketchDimensionId.New(), ConstraintId(1), new Vec2d(1, 1));

        DimensionLayout layout = SketchDimensionLayout.Resolve(dimension, sketch);

        layout.Outcome.Should().Be(DimensionLayoutOutcome.Degenerate);
    }

    [Fact]
    public void Linear_HorizontalDistance_RunsHorizontallyAtTheWitnessHeight()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchPoint(Entity(1), Vec2d.Zero))
            .With(new SketchPoint(Entity(2), new Vec2d(3, 4)))
            .With(Constraint(1, ConstraintKind.HorizontalDistance, [Whole(1), Whole(2)], 3));

        SketchDimension dimension = new(SketchDimensionId.New(), ConstraintId(1), new Vec2d(1, 5));

        DimensionLayout layout = SketchDimensionLayout.Resolve(dimension, sketch);

        layout.IsResolved.Should().BeTrue();
        layout.Value.Should().Be(3, "the vertical separation plays no part in a horizontal dimension");
        layout.DimensionLine!.Value.Start.Should().Be(new Vec2d(0, 5));
        layout.DimensionLine!.Value.End.Should().Be(new Vec2d(3, 5));
        layout.Witnesses.Should().Equal(
            (Vec2d.Zero, new Vec2d(0, 5)), (new Vec2d(3, 4), new Vec2d(3, 5)));
    }

    [Fact]
    public void Linear_VerticalDistance_RunsVerticallyAtTheWitnessOffset()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchPoint(Entity(1), Vec2d.Zero))
            .With(new SketchPoint(Entity(2), new Vec2d(3, 4)))
            .With(Constraint(1, ConstraintKind.VerticalDistance, [Whole(1), Whole(2)], 4));

        SketchDimension dimension = new(SketchDimensionId.New(), ConstraintId(1), new Vec2d(6, 1));

        DimensionLayout layout = SketchDimensionLayout.Resolve(dimension, sketch);

        layout.IsResolved.Should().BeTrue();
        layout.Value.Should().Be(4, "the horizontal separation plays no part in a vertical dimension");
        layout.DimensionLine!.Value.Start.Should().Be(new Vec2d(6, 0));
        layout.DimensionLine!.Value.End.Should().Be(new Vec2d(6, 4));
    }

    [Fact]
    public void TheReadingComesFromTheCurrentGeometryRatherThanTheStoredValue()
    {
        // A stale or reference constraint value must never be trusted over what the geometry
        // actually says -- that is the entire reason this reads Value fresh rather than returning
        // constraint.Value, and this is what would fail if that changed back.
        Sketch sketch = Sketch.Empty
            .With(new SketchPoint(Entity(1), Vec2d.Zero))
            .With(new SketchPoint(Entity(2), new Vec2d(4, 0)))
            .With(Constraint(1, ConstraintKind.Distance, [Whole(1), Whole(2)], 999));

        SketchDimension dimension = new(SketchDimensionId.New(), ConstraintId(1), new Vec2d(2, 1));

        DimensionLayout layout = SketchDimensionLayout.Resolve(dimension, sketch);

        layout.Value.Should().Be(4);
    }

    [Fact]
    public void Resolve_FailsWhenTheConstraintNoLongerExists()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchPoint(Entity(1), Vec2d.Zero))
            .With(new SketchPoint(Entity(2), new Vec2d(4, 0)));

        SketchDimension dimension = new(SketchDimensionId.New(), ConstraintId(1), Vec2d.Zero);

        DimensionLayout layout = SketchDimensionLayout.Resolve(dimension, sketch);

        layout.Outcome.Should().Be(DimensionLayoutOutcome.ConstraintNotFound);
    }

    [Fact]
    public void Resolve_FailsWhenTheGeometryNoLongerExists()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchPoint(Entity(1), Vec2d.Zero))
            .With(Constraint(1, ConstraintKind.Distance, [Whole(1), Whole(2)], 4));

        SketchDimension dimension = new(SketchDimensionId.New(), ConstraintId(1), Vec2d.Zero);

        DimensionLayout layout = SketchDimensionLayout.Resolve(dimension, sketch);

        layout.Outcome.Should().Be(DimensionLayoutOutcome.GeometryNotFound);
    }

    [Theory]
    [InlineData(ConstraintKind.Angle)]
    [InlineData(ConstraintKind.Radius)]
    public void Resolve_IsUnsupportedForKindsNotYetLaidOut(ConstraintKind kind)
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(1, 0)))
            .With(new SketchLine(Entity(2), Vec2d.Zero, new Vec2d(0, 1)))
            .With(new SketchCircle(Entity(3), Vec2d.Zero, 2))
            .With(kind == ConstraintKind.Angle
                ? Constraint(1, kind, [Whole(1), Whole(2)], System.Math.PI / 2)
                : Constraint(1, kind, [Whole(3)], 2));

        SketchDimension dimension = new(SketchDimensionId.New(), ConstraintId(1), new Vec2d(3, 3));

        DimensionLayout layout = SketchDimensionLayout.Resolve(dimension, sketch);

        layout.Outcome.Should().Be(DimensionLayoutOutcome.Unsupported);
    }

    [Fact]
    public void Resolve_IsUnsupportedForThePointToLineShapeOfDistance()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchPoint(Entity(1), new Vec2d(0, 2)))
            .With(new SketchLine(Entity(2), Vec2d.Zero, new Vec2d(4, 0)))
            .With(Constraint(
                1, ConstraintKind.Distance, [Point(1, EntityPoint.Self), Point(2, EntityPoint.Self)], 2));

        SketchDimension dimension = new(SketchDimensionId.New(), ConstraintId(1), new Vec2d(2, 1));

        DimensionLayout layout = SketchDimensionLayout.Resolve(dimension, sketch);

        layout.Outcome.Should().Be(DimensionLayoutOutcome.Unsupported);
    }

    private static SketchConstraint Constraint(
        int id, ConstraintKind kind, System.Collections.Immutable.ImmutableArray<SketchPointRef> on,
        double? value = null)
        => new(ConstraintId(id), kind, on, value);

    private static SketchPointRef Whole(int entity) => new(Entity(entity));

    private static SketchPointRef Point(int entity, EntityPoint point) => new(Entity(entity), point);

    private static SketchEntityId Entity(int n)
        => new(new Guid($"00000000-0000-0000-0000-{n:D12}"));

    private static SketchConstraintId ConstraintId(int n)
        => new(new Guid($"00000000-0000-0000-0001-{n:D12}"));
}
