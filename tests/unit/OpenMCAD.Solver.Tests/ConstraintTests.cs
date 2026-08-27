using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Solver.Sketching;

using Xunit;

namespace OpenMCAD.Solver.Tests;

/// <summary>
/// The constraint model (P4-T04).
/// </summary>
/// <remarks>
/// §5.6 is blunt that a diagnosis naming no specific constraint is useless to a user. These are the
/// mistakes that can be named without solving anything: a tangency between two points, a radius on
/// a line, a reference to geometry that has been deleted, a constraint that names one thing twice.
/// Everything left over needs the solver, and is P4-T06.
/// </remarks>
public sealed class ConstraintTests
{
    [Fact]
    public void EveryConstraintKindIsDescribed()
    {
        // A kind added without a row in the table has no schema, so nothing could validate it,
        // count its degrees of freedom, or write it to a file. Better to fail here than to have it
        // silently removing zero freedom in a readout that looks authoritative.
        foreach (ConstraintKind kind in Enum.GetValues<ConstraintKind>())
        {
            ConstraintSchema schema = ConstraintSchema.For(kind);

            schema.Kind.Should().Be(kind);
            schema.Label.Should().NotBeNullOrWhiteSpace();
            schema.Equations.Should().BeGreaterThan(0, "a constraint that removes nothing is not one");
            schema.Operands.Should().NotBeEmpty();
        }

        ConstraintSchema.All.Should().HaveCount(Enum.GetValues<ConstraintKind>().Length);
    }

    [Theory]
    [InlineData(ConstraintKind.Coincident, 2)]
    [InlineData(ConstraintKind.Distance, 1)]
    [InlineData(ConstraintKind.Concentric, 2)]
    [InlineData(ConstraintKind.Midpoint, 2)]
    [InlineData(ConstraintKind.Symmetric, 2)]
    [InlineData(ConstraintKind.Parallel, 1)]
    [InlineData(ConstraintKind.Radius, 1)]
    [InlineData(ConstraintKind.Fix, 2)]
    public void AConstraintRemovesTheFreedomItActuallyRemoves(ConstraintKind kind, int expected)
    {
        // Written from what a solver is given, not from what feels right. A coincidence fixes both
        // coordinates and so removes two; a distance fixes only how far apart the points are and
        // leaves the direction free, so it removes one. Getting these wrong does not break a solve
        // -- it breaks the degree-of-freedom readout, which is worse, because the number looks
        // authoritative and is quietly false.
        ConstraintSchema.For(kind).Equations.Should().Be(expected);
    }

    [Fact]
    public void ADimensionalConstraintCarriesAValueAndAGeometricOneDoesNot()
    {
        ConstraintSchema.For(ConstraintKind.Distance).ValueKind.Should().Be(ConstraintValueKind.Length);
        ConstraintSchema.For(ConstraintKind.Angle).ValueKind.Should().Be(ConstraintValueKind.Angle);
        ConstraintSchema.For(ConstraintKind.Parallel).HasValue.Should().BeFalse();
    }

    [Fact]
    public void AReferenceDimensionMeasuresAndRemovesNothing()
    {
        // The whole difference between a driving and a driven dimension, expressed once so the
        // degree-of-freedom count cannot disagree with what the solver was actually given.
        SketchConstraint driving = Constraint(ConstraintKind.Radius, 1, [Whole(1)], 5);

        driving.Removes.Should().Be(1);
        (driving with { IsDriving = false }).Removes.Should().Be(0);
    }

    [Fact]
    public void AWellFormedSketchHasNothingWrongWithIt()
    {
        Sketch sketch = Rectangleish();

        sketch.Problems.Should().BeEmpty();
    }

    [Fact]
    public void AConstraintOnGeometryThatIsNotThereIsNamed()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, Vec2d.One))
            .With(Constraint(ConstraintKind.Parallel, 1, [Whole(1), Whole(9)]));

        sketch.Problems.Should().ContainSingle()
            .Which.Should().Contain("no such entity");
    }

    [Theory]
    [InlineData(ConstraintKind.Radius, "has no radius")]
    [InlineData(ConstraintKind.Diameter, "has no radius")]
    [InlineData(ConstraintKind.Concentric, "has no radius")]
    public void ADimensionOfTheWrongSortOfThingIsNamed(ConstraintKind kind, string complaint)
    {
        // A radius on a line. The solver would report a non-convergence somewhere unrelated.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, Vec2d.One))
            .With(new SketchLine(Entity(2), Vec2d.Zero, Vec2d.One))
            .With(Constraint(
                kind,
                1,
                ConstraintSchema.For(kind).Operands.Length == 1 ? [Whole(1)] : [Whole(1), Whole(2)],
                ConstraintSchema.For(kind).HasValue ? 5 : null));

        sketch.Problems.Should().ContainSingle().Which.Should().Contain(complaint);
    }

    [Fact]
    public void AConstraintWantingAWholeEntityRefusesOneOfItsPoints()
    {
        // "This line's end is parallel to that line" is not a sentence, and a model that quietly
        // accepted it would resolve the operand to a coordinate and constrain something nobody
        // asked about.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, Vec2d.One))
            .With(new SketchLine(Entity(2), Vec2d.Zero, new Vec2d(1, 0)))
            .With(Constraint(
                ConstraintKind.Parallel, 1, [Point(1, EntityPoint.End), Whole(2)]));

        sketch.Problems.Should().ContainSingle().Which.Should().Contain("whole line is wanted");
    }

    [Fact]
    public void AConstraintWantingAPointRefusesOneTheEntityDoesNotHave()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchCircle(Entity(1), Vec2d.Zero, 1))
            .With(new SketchPoint(Entity(2), Vec2d.One))
            .With(Constraint(
                ConstraintKind.Coincident, 1, [Point(1, EntityPoint.Start), Whole(2)]));

        sketch.Problems.Should().ContainSingle().Which.Should().Contain("no start point");
    }

    [Fact]
    public void AConstraintThatNamesOneThingTwiceIsRefused()
    {
        // A line parallel to itself, a point coincident with itself. Always true, removes nothing,
        // and the solver reports it as redundancy far from the cause.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, Vec2d.One))
            .With(Constraint(ConstraintKind.Parallel, 1, [Whole(1), Whole(1)]));

        sketch.Problems.Should().ContainSingle().Which.Should().Contain("constrains nothing");
    }

    [Fact]
    public void AConstraintWithTheWrongNumberOfOperandsIsRefused()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, Vec2d.One))
            .With(Constraint(ConstraintKind.Parallel, 1, [Whole(1)]));

        sketch.Problems.Should().ContainSingle().Which.Should().Contain("takes 2 operands");
    }

    [Fact]
    public void ADimensionWithNoValueIsRefusedAndAGeometricOneWithAValueToo()
    {
        Sketch bare = Sketch.Empty
            .With(new SketchCircle(Entity(1), Vec2d.Zero, 1))
            .With(Constraint(ConstraintKind.Radius, 1, [Whole(1)]));

        bare.Problems.Should().ContainSingle().Which.Should().Contain("needs a value");

        Sketch surplus = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, Vec2d.One))
            .With(new SketchLine(Entity(2), Vec2d.Zero, new Vec2d(1, 0)))
            .With(Constraint(ConstraintKind.Parallel, 1, [Whole(1), Whole(2)], 4));

        surplus.Problems.Should().ContainSingle().Which.Should().Contain("no use for one");
    }

    [Fact]
    public void AKindWithMoreThanOneShapeAcceptsBoth()
    {
        // Horizontal takes one line or two points, and both say the same thing to a user. A
        // validator that knew only the declared shape would refuse half of what the UI offers.
        Sketch asLine = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, Vec2d.One))
            .With(Constraint(ConstraintKind.Horizontal, 1, [Whole(1)]));

        Sketch asPoints = Sketch.Empty
            .With(new SketchPoint(Entity(1), Vec2d.Zero))
            .With(new SketchPoint(Entity(2), Vec2d.One))
            .With(Constraint(ConstraintKind.Horizontal, 1, [Whole(1), Whole(2)]));

        asLine.Problems.Should().BeEmpty();
        asPoints.Problems.Should().BeEmpty();
    }

    [Fact]
    public void TheComplaintComesFromTheShapeWithTheRightNumberOfOperands()
    {
        // Horizontal given one bad operand should not be told it is not two points, which is true
        // and unhelpful.
        Sketch sketch = Sketch.Empty
            .With(new SketchCircle(Entity(1), Vec2d.Zero, 1))
            .With(Constraint(ConstraintKind.Horizontal, 1, [Whole(1)]));

        sketch.Problems.Should().ContainSingle().Which.Should().Contain("not a line");
    }

    [Fact]
    public void DeletingGeometryTakesItsConstraintsWithIt()
    {
        // A constraint left pointing at deleted geometry names a coordinate nobody will write, and
        // the failure surfaces as a solve that does not converge for no visible reason.
        Sketch sketch = Rectangleish().Without(Entity(1));

        sketch.Constraints.Should().NotContain(c => c.On.Any(o => o.Entity == Entity(1)));
        sketch.Problems.Should().BeEmpty();
    }

    [Fact]
    public void DegreesOfFreedomAreCountedFromTheGeometryAndTheConstraints()
    {
        // Two free points is four degrees of freedom; making them coincident removes two.
        Sketch sketch = Sketch.Empty
            .With(new SketchPoint(Entity(1), Vec2d.Zero))
            .With(new SketchPoint(Entity(2), Vec2d.One));

        sketch.Freedom.Should().Be(4);
        sketch.RemainingFreedom.Should().Be(4);

        Sketch joined = sketch.With(
            Constraint(ConstraintKind.Coincident, 1, [Whole(1), Whole(2)]));

        joined.RemainingFreedom.Should().Be(2);
    }

    [Fact]
    public void ConstructionGeometryCountsTowardsFreedomLikeEverythingElse()
    {
        // It is solved like everything else, which is what it is for.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, Vec2d.One, IsConstruction: true));

        sketch.Freedom.Should().Be(4);
    }

    [Fact]
    public void AFullyFixedPointHasNoFreedomLeft()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchPoint(Entity(1), Vec2d.Zero))
            .With(Constraint(ConstraintKind.Fix, 1, [Whole(1)]));

        sketch.RemainingFreedom.Should().Be(0);
    }

    [Fact]
    public void ASketchReportsEveryProblemRatherThanTheFirst()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchCircle(Entity(1), Vec2d.Zero, 0))
            .With(new SketchLine(Entity(2), Vec2d.Zero, Vec2d.One))
            .With(Constraint(ConstraintKind.Radius, 1, [Whole(2)], 5));

        sketch.Problems.Should().HaveCount(2, "one degenerate circle and one radius on a line");
    }

    [Fact]
    public void ReplacingAConstraintLeavesItWhereItWas()
    {
        ConstraintSet set = ConstraintSet.Of(
        [
            Constraint(ConstraintKind.Horizontal, 1, [Whole(1)]),
            Constraint(ConstraintKind.Vertical, 2, [Whole(2)]),
        ]);

        ConstraintSet changed = set.With(
            Constraint(ConstraintKind.Horizontal, 1, [Whole(3)]));

        changed.Ordered.Select(c => c.Id).Should().Equal(ConstraintId(1), ConstraintId(2));
        changed.Find(ConstraintId(1))!.On[0].Entity.Should().Be(Entity(3));
    }

    [Fact]
    public void AConstraintWithNoIdIsRefused()
    {
        Action add = () => ConstraintSet.Empty.With(new SketchConstraint(
            SketchConstraintId.None, ConstraintKind.Horizontal, [Whole(1)]));

        add.Should().Throw<ArgumentException>().WithMessage("*could remove it again*");
    }

    /// <summary>A sketch a person might actually draw: three lines, joined and constrained.</summary>
    internal static Sketch Rectangleish()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(4, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(4, 0), new Vec2d(4, 3)))
            .With(new SketchLine(Entity(3), new Vec2d(4, 3), Vec2d.Zero));

        return sketch
            .With(Constraint(
                ConstraintKind.Coincident, 1,
                [Point(1, EntityPoint.End), Point(2, EntityPoint.Start)]))
            .With(Constraint(
                ConstraintKind.Coincident, 2,
                [Point(2, EntityPoint.End), Point(3, EntityPoint.Start)]))
            .With(Constraint(ConstraintKind.Horizontal, 3, [Whole(1)]))
            .With(Constraint(ConstraintKind.Vertical, 4, [Whole(2)]))
            .With(Constraint(ConstraintKind.Distance, 5,
                [Point(1, EntityPoint.Start), Point(1, EntityPoint.End)], 4));
    }

    internal static SketchConstraint Constraint(
        ConstraintKind kind, int id, ImmutableArray<SketchPointRef> on, double? value = null)
        => new(ConstraintId(id), kind, on, value);

    internal static SketchPointRef Whole(int entity) => new(Entity(entity));

    internal static SketchPointRef Point(int entity, EntityPoint point)
        => new(Entity(entity), point);

    internal static SketchEntityId Entity(int n)
        => new(new Guid($"00000000-0000-0000-0000-{n:D12}"));

    internal static SketchConstraintId ConstraintId(int n)
        => new(new Guid($"00000000-0000-0000-0001-{n:D12}"));
}
