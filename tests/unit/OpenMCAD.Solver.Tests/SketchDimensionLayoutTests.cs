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
    [InlineData(ConstraintKind.Coincident)]
    [InlineData(ConstraintKind.Tangent)]
    [InlineData(ConstraintKind.Fix)]
    public void Resolve_IsUnsupportedForKindsThatAreNotDimensions(ConstraintKind kind)
    {
        // Every dimension type §5.6 names is laid out now. What is left over is the constraints
        // that are not dimensions at all: they carry no number and there is nothing to display.
        Sketch sketch = Sketch.Empty
            .With(new SketchPoint(Entity(1), Vec2d.Zero))
            .With(new SketchPoint(Entity(2), new Vec2d(1, 0)))
            .With(Constraint(1, kind, [Whole(1), Whole(2)]));

        SketchDimension dimension = new(SketchDimensionId.New(), ConstraintId(1), new Vec2d(3, 3));

        DimensionLayout layout = SketchDimensionLayout.Resolve(dimension, sketch);

        layout.Outcome.Should().Be(DimensionLayoutOutcome.Unsupported);
    }

    // --- Point to line ---------------------------------------------------------------------------

    [Fact]
    public void APointToLineDistanceMeasuresToTheFootOfThePerpendicular()
    {
        // The other operand shape Distance accepts. Laid out as an aligned dimension between the
        // point and its foot, because that is what a distance to a line is.
        Sketch sketch = Sketch.Empty
            .With(new SketchPoint(Entity(1), new Vec2d(3, 4)))
            .With(new SketchLine(Entity(2), Vec2d.Zero, new Vec2d(10, 0)));

        DimensionLayout layout = Lay(
            sketch, ConstraintKind.Distance, [Whole(1), Whole(2)], new Vec2d(6, 2));

        layout.IsResolved.Should().BeTrue(layout.Reason);
        layout.Value.Should().BeApproximately(4, Tol, "the perpendicular distance, not to an end");

        // The dimension line runs point-to-foot, offset sideways to where the text was put.
        layout.DimensionLine!.Value.Start.X.Should().BeApproximately(6, Tol);
        layout.DimensionLine.Value.Start.Y.Should().BeApproximately(0, Tol);
        layout.DimensionLine.Value.End.X.Should().BeApproximately(6, Tol);
        layout.DimensionLine.Value.End.Y.Should().BeApproximately(4, Tol);
    }

    [Fact]
    public void APointToLineDistanceUsesTheInfiniteLineRatherThanTheSegment()
    {
        // The foot lands beyond the segment's end. That is still the distance to the line the user
        // named, and refusing it would make the dimension depend on how far the line happens to be
        // drawn rather than on where it points.
        Sketch sketch = Sketch.Empty
            .With(new SketchPoint(Entity(1), new Vec2d(20, 3)))
            .With(new SketchLine(Entity(2), Vec2d.Zero, new Vec2d(10, 0)));

        DimensionLayout layout = Lay(
            sketch, ConstraintKind.Distance, [Whole(1), Whole(2)], new Vec2d(22, 1));

        layout.IsResolved.Should().BeTrue(layout.Reason);
        layout.Value.Should().BeApproximately(3, Tol);
    }

    // --- Angular ---------------------------------------------------------------------------------

    [Fact]
    public void AnAngularDimensionArcsBetweenTheTwoLinesAtTheTextsRadius()
    {
        Sketch sketch = RightAngle();

        DimensionLayout layout = Lay(
            sketch, ConstraintKind.Angle, [Whole(1), Whole(2)], new Vec2d(3, 4));

        layout.IsResolved.Should().BeTrue(layout.Reason);
        layout.DimensionLine.Should().BeNull("an angular dimension's line is an arc");
        layout.Value.Should().BeApproximately(System.Math.PI / 2, Tol);

        (Vec2d centre, double radius, double start, double end) = layout.DimensionArc!.Value;

        centre.Should().Be(Vec2d.Zero, "the lines cross at the origin");
        radius.Should().BeApproximately(5, Tol, "the text is five from the vertex");
        start.Should().BeApproximately(0, Tol);
        end.Should().BeApproximately(System.Math.PI / 2, Tol);
    }

    [Fact]
    public void AnAngularDimensionTakesTheOneOfFourAnglesTheTextSitsIn()
    {
        // Two crossing lines make four angles and the geometry cannot say which was meant. Text in
        // the third quadrant means the third quadrant's angle, not the first's.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), new Vec2d(-10, 0), new Vec2d(10, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(0, -10), new Vec2d(0, 10)));

        DimensionLayout layout = Lay(
            sketch, ConstraintKind.Angle, [Whole(1), Whole(2)], new Vec2d(-3, -4));

        layout.IsResolved.Should().BeTrue(layout.Reason);
        layout.Value.Should().BeApproximately(System.Math.PI / 2, Tol);

        (Vec2d _, double _, double start, double end) = layout.DimensionArc!.Value;

        // Anticlockwise from pointing along -X round to pointing down is the third quadrant. The
        // other ordering describes the same two rays and sweeps the remaining three quarters.
        start.Should().BeApproximately(-System.Math.PI, Tol);
        end.Should().BeApproximately(-System.Math.PI / 2, Tol);
    }

    [Fact]
    public void AnAngularDimensionOrdersItsArcSoTheSweepIsTheAngleItMeasured()
    {
        // The arc runs anticlockwise from start to end, like every other arc here, so when the
        // second line's ray sits clockwise of the first's the two have to be given the other way
        // round. Emitting them in operand order instead names the same pair of rays and sweeps the
        // other three quarters of the turn -- which reads as a plausible arc pointing the wrong way.
        DimensionLayout layout = Lay(
            RightAngle(), ConstraintKind.Angle, [Whole(1), Whole(2)], new Vec2d(3, -4));

        layout.IsResolved.Should().BeTrue(layout.Reason);
        layout.Value.Should().BeApproximately(System.Math.PI / 2, Tol);

        (Vec2d _, double _, double start, double end) = layout.DimensionArc!.Value;

        start.Should().BeApproximately(-System.Math.PI / 2, Tol, "from the ray pointing down");
        end.Should().BeApproximately(0, Tol, "anticlockwise round to the one pointing along +X");
    }

    [Fact]
    public void AnAngularDimensionMeasuresAnObtuseAngleAsObtuse()
    {
        // Text placed in the wide angle has to give the wide angle. Taking the acute one whatever
        // the text said would be the commonest way to get this wrong and would look plausible.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)))
            .With(new SketchLine(Entity(2), Vec2d.Zero, new Vec2d(-10, 10)));

        DimensionLayout layout = Lay(
            sketch, ConstraintKind.Angle, [Whole(1), Whole(2)], new Vec2d(1, 4));

        layout.IsResolved.Should().BeTrue(layout.Reason);
        layout.Value.Should().BeApproximately(3 * System.Math.PI / 4, Tol);
    }

    [Fact]
    public void AnAngularDimensionExtendsALineThatStopsShortOfItsArc()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(2, 0)))
            .With(new SketchLine(Entity(2), Vec2d.Zero, new Vec2d(20, 20)));

        DimensionLayout layout = Lay(
            sketch, ConstraintKind.Angle, [Whole(1), Whole(2)], new Vec2d(7, 7));

        layout.IsResolved.Should().BeTrue(layout.Reason);

        // The short line needs carrying out to the arc; the long one already runs past it.
        layout.Witnesses.Should().ContainSingle();
        layout.Witnesses[0].From.Should().Be(new Vec2d(2, 0));
        layout.Witnesses[0].To.X.Should().BeApproximately(System.Math.Sqrt(98), Tol);
    }

    [Fact]
    public void AnAngularDimensionRefusesTwoParallelLines()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(0, 5), new Vec2d(10, 5)));

        DimensionLayout layout = Lay(
            sketch, ConstraintKind.Angle, [Whole(1), Whole(2)], new Vec2d(5, 2));

        layout.Outcome.Should().Be(DimensionLayoutOutcome.Degenerate);
    }

    [Fact]
    public void AnAngularDimensionRefusesTextOnTheVertex()
    {
        DimensionLayout layout = Lay(
            RightAngle(), ConstraintKind.Angle, [Whole(1), Whole(2)], Vec2d.Zero);

        layout.Outcome.Should().Be(DimensionLayoutOutcome.Degenerate);
    }

    // --- Radial and diametric --------------------------------------------------------------------

    [Fact]
    public void ARadiusInsideTheCircleRunsFromTheCentreToTheRim()
    {
        Sketch sketch = Sketch.Empty.With(new SketchCircle(Entity(1), Vec2d.Zero, 5));

        DimensionLayout layout = Lay(
            sketch, ConstraintKind.Radius, [Whole(1)], new Vec2d(2, 0));

        layout.IsResolved.Should().BeTrue(layout.Reason);
        layout.Value.Should().BeApproximately(5, Tol);
        layout.DimensionLine!.Value.Start.Should().Be(Vec2d.Zero);
        layout.DimensionLine.Value.End.X.Should().BeApproximately(5, Tol);
    }

    [Fact]
    public void ARadiusOutsideTheCircleRunsFromTheRimOutToTheText()
    {
        // Where the text sits is the only thing that can say whether the leader belongs inside the
        // circle or outside it, and dragging it past the rim is how a user says "outside".
        Sketch sketch = Sketch.Empty.With(new SketchCircle(Entity(1), Vec2d.Zero, 5));

        DimensionLayout layout = Lay(
            sketch, ConstraintKind.Radius, [Whole(1)], new Vec2d(9, 0));

        layout.IsResolved.Should().BeTrue(layout.Reason);
        layout.DimensionLine!.Value.Start.X.Should().BeApproximately(5, Tol);
        layout.DimensionLine.Value.End.Should().Be(new Vec2d(9, 0));
    }

    [Fact]
    public void ADiameterRunsRightAcrossTheCircle()
    {
        Sketch sketch = Sketch.Empty.With(new SketchCircle(Entity(1), new Vec2d(1, 1), 5));

        DimensionLayout layout = Lay(
            sketch, ConstraintKind.Diameter, [Whole(1)], new Vec2d(3, 1));

        layout.IsResolved.Should().BeTrue(layout.Reason);
        layout.Value.Should().BeApproximately(10, Tol, "a diameter is twice the radius");
        layout.DimensionLine!.Value.Start.X.Should().BeApproximately(-4, Tol);
        layout.DimensionLine.Value.End.X.Should().BeApproximately(6, Tol);
    }

    [Fact]
    public void ARadiusOnAnArcPointsAtSomewhereTheArcActuallyIs()
    {
        // The leader is aimed by the text, and an arc only exists over its own sweep. Aimed past
        // the end it comes back to the nearer one rather than pointing at empty space.
        Sketch sketch = Sketch.Empty
            .With(new SketchArc(Entity(1), Vec2d.Zero, 5, 0, System.Math.PI / 2));

        DimensionLayout layout = Lay(
            sketch, ConstraintKind.Radius, [Whole(1)], new Vec2d(1, -3));

        layout.IsResolved.Should().BeTrue(layout.Reason);
        layout.Value.Should().BeApproximately(5, Tol);
        layout.DimensionLine!.Value.End.X.Should().BeApproximately(5, Tol, "clamped to the arc's start");
        layout.DimensionLine.Value.End.Y.Should().BeApproximately(0, Tol);
    }

    [Fact]
    public void ARadialDimensionRefusesTextOnTheCentre()
    {
        Sketch sketch = Sketch.Empty.With(new SketchCircle(Entity(1), Vec2d.Zero, 5));

        DimensionLayout layout = Lay(
            sketch, ConstraintKind.Radius, [Whole(1)], Vec2d.Zero);

        layout.Outcome.Should().Be(DimensionLayoutOutcome.Degenerate);
    }

    [Fact]
    public void ARadialDimensionRefusesAKindWithNoRadius()
    {
        Sketch sketch = Sketch.Empty.With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(5, 0)));

        DimensionLayout layout = Lay(
            sketch, ConstraintKind.Radius, [Whole(1)], new Vec2d(2, 2));

        layout.Outcome.Should().Be(DimensionLayoutOutcome.Unsupported);
    }

    private const double Tol = 1e-9;

    private static Sketch RightAngle() => Sketch.Empty
        .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)))
        .With(new SketchLine(Entity(2), Vec2d.Zero, new Vec2d(0, 10)));

    /// <summary>Lays out a dimension of the given kind over a sketch.</summary>
    private static DimensionLayout Lay(
        Sketch sketch,
        ConstraintKind kind,
        System.Collections.Immutable.ImmutableArray<SketchPointRef> operands,
        Vec2d text)
        => SketchDimensionLayout.Resolve(
            new SketchDimension(SketchDimensionId.New(), ConstraintId(99), text),
            sketch.With(Constraint(99, kind, operands, 1)));

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
