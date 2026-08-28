using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Solver.Sketching;

using Xunit;

namespace OpenMCAD.Solver.Tests;

/// <summary>
/// Guessing what the user meant while they are drawing (P4-T08).
/// </summary>
/// <remarks>
/// This is the part of a sketcher that makes it feel like one, and the part most easily made
/// annoying. The tests below are as much about what is <em>not</em> offered — things already true,
/// two contradicting guesses at once, anything at all while the modifier is held — as about what is.
/// </remarks>
public sealed class ConstraintInferenceTests
{
    [Fact]
    public void AnEndpointDroppedNearAnotherIsOfferedACoincidence()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(5, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(9, 9), new Vec2d(8, 8)));

        ConstraintProposal best = ConstraintInference
            .ForPoint(sketch, Point(2, EntityPoint.End), new Vec2d(5.2, 0.1))
            .First();

        best.Constraint.Kind.Should().Be(ConstraintKind.Coincident);
        best.Constraint.On.Should().Contain(Point(1, EntityPoint.End));
        best.Glyph.Should().Be("coincident");
        best.At.Should().Be(new Vec2d(5, 0), "the glyph belongs on the thing being snapped to");
    }

    [Fact]
    public void ANamedPointBeatsTheCurveItBelongsTo()
    {
        // Someone aiming at the end of a line wants the end, not a point that happens to lie on
        // the line near it. Both are true; only one is what they meant.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(5, 0)))
            .With(new SketchPoint(Entity(2), new Vec2d(9, 9)));

        ImmutableArray<ConstraintProposal> offered = ConstraintInference.ForPoint(
            sketch, Whole(2), new Vec2d(4.9, 0.05));

        offered[0].Constraint.Kind.Should().Be(ConstraintKind.Coincident);

        offered.Select(p => p.Constraint.Kind)
            .Should().Contain(ConstraintKind.PointOnObject, "which is still true, and second");
    }

    [Fact]
    public void APointDroppedOnALineAwayFromItsEndsIsOfferedPointOnObject()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)))
            .With(new SketchPoint(Entity(2), new Vec2d(9, 9)));

        ImmutableArray<ConstraintProposal> offered = ConstraintInference.ForPoint(
            sketch, Whole(2), new Vec2d(3, 0.1));

        offered.Should().ContainSingle()
            .Which.Constraint.Kind.Should().Be(ConstraintKind.PointOnObject);
    }

    [Fact]
    public void APointDroppedAtTheMiddleOfALineIsOfferedAMidpoint()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)))
            .With(new SketchPoint(Entity(2), new Vec2d(9, 9)));

        ConstraintInference.ForPoint(sketch, Whole(2), new Vec2d(5.1, 0.1))
            .Select(p => p.Constraint.Kind)
            .Should().Contain(ConstraintKind.Midpoint);
    }

    [Theory]
    [InlineData(0.02, "horizontal")]
    [InlineData(-0.02, "horizontal")]
    public void ALineDrawnNearlyHorizontalIsOfferedHorizontal(double rise, string glyph)
    {
        Sketch sketch = Sketch.Empty.With(
            new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(5, rise)));

        ConstraintProposal best = ConstraintInference.ForEntity(sketch, Entity(1)).First();

        best.Constraint.Kind.Should().Be(ConstraintKind.Horizontal);
        best.Glyph.Should().Be(glyph);
    }

    [Fact]
    public void ALineDrawnNearlyVerticalIsOfferedVertical()
    {
        Sketch sketch = Sketch.Empty.With(
            new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(0.02, 5)));

        ConstraintInference.ForEntity(sketch, Entity(1)).First()
            .Constraint.Kind.Should().Be(ConstraintKind.Vertical);
    }

    [Fact]
    public void ALineDrawnWellOffAnAxisIsOfferedNothingAboutAxes()
    {
        Sketch sketch = Sketch.Empty.With(
            new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(5, 5)));

        ConstraintInference.ForEntity(sketch, Entity(1)).Select(p => p.Constraint.Kind)
            .Should().NotContain(ConstraintKind.Horizontal)
            .And.NotContain(ConstraintKind.Vertical);
    }

    [Fact]
    public void OnlyOneThingIsOfferedAboutWhereALinePoints()
    {
        // Horizontal, parallel and perpendicular all say where a line points. A line drawn nearly
        // horizontal beside another horizontal line is all three at once, and offering two of them
        // is offering a contradiction.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(5, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(0, 3), new Vec2d(5, 3.01)));

        ImmutableArray<ConstraintProposal> offered =
            ConstraintInference.ForEntity(sketch, Entity(2));

        offered.Count(p => p.Constraint.Kind
            is ConstraintKind.Horizontal
            or ConstraintKind.Vertical
            or ConstraintKind.Parallel
            or ConstraintKind.Perpendicular)
            .Should().Be(1);

        offered[0].Constraint.Kind.Should().Be(
            ConstraintKind.Horizontal, "the more confident of the two");
    }

    [Fact]
    public void ALineDrawnNearlyParallelToAnotherIsOfferedParallel()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(3, 4)))
            .With(new SketchLine(Entity(2), new Vec2d(0, 9), new Vec2d(3.02, 13)));

        ConstraintInference.ForEntity(sketch, Entity(2)).Select(p => p.Constraint.Kind)
            .Should().Contain(ConstraintKind.Parallel);
    }

    [Fact]
    public void ALineDrawnNearlySquareToAnotherIsOfferedPerpendicular()
    {
        // Both lines are well off an axis, or "vertical" would win the direction slot and be right
        // to: an axis is a more confident guess than a relationship.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(4, 3)))
            .With(new SketchLine(Entity(2), new Vec2d(9, 0), new Vec2d(5.98, 4.02)));

        ConstraintInference.ForEntity(sketch, Entity(2)).Select(p => p.Constraint.Kind)
            .Should().Contain(ConstraintKind.Perpendicular);
    }

    [Fact]
    public void ALineDrawnNearlyTouchingACircleIsOfferedTangent()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchCircle(Entity(1), Vec2d.Zero, 2))
            .With(new SketchLine(Entity(2), new Vec2d(-5, 2.1), new Vec2d(5, 2.1)));

        ConstraintInference.ForEntity(sketch, Entity(2)).Select(p => p.Constraint.Kind)
            .Should().Contain(ConstraintKind.Tangent);
    }

    [Fact]
    public void ACircleDrawnNearlyOnAnothersCentreIsOfferedConcentric()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchCircle(Entity(1), Vec2d.Zero, 5))
            .With(new SketchCircle(Entity(2), new Vec2d(0.1, 0.1), 2));

        ConstraintInference.ForEntity(sketch, Entity(2)).First()
            .Constraint.Kind.Should().Be(ConstraintKind.Concentric);
    }

    [Fact]
    public void SomethingDrawnNearlyTheSameSizeAsAnotherIsOfferedEqual()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchCircle(Entity(1), Vec2d.Zero, 5))
            .With(new SketchCircle(Entity(2), new Vec2d(20, 20), 5.1));

        ConstraintInference.ForEntity(sketch, Entity(2)).Select(p => p.Constraint.Kind)
            .Should().Contain(ConstraintKind.Equal);
    }

    [Fact]
    public void EqualIsOfferedLastBecauseItIsTheWeakestGuess()
    {
        // Two circles the same size and nearly concentric: both are true, and only one is what
        // someone drawing a bolt circle almost certainly meant.
        Sketch sketch = Sketch.Empty
            .With(new SketchCircle(Entity(1), Vec2d.Zero, 5))
            .With(new SketchCircle(Entity(2), new Vec2d(0.1, 0), 5.05));

        ImmutableArray<ConstraintProposal> offered =
            ConstraintInference.ForEntity(sketch, Entity(2));

        offered[0].Constraint.Kind.Should().Be(ConstraintKind.Concentric);
        offered.Last().Constraint.Kind.Should().Be(ConstraintKind.Equal);
    }

    [Fact]
    public void NothingAlreadyTrueIsOffered()
    {
        // A sketcher offering to make two things coincident that a constraint already holds
        // together is telling the user it has not been paying attention.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(5, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(0, 3), new Vec2d(5, 3.01)))
            .With(new SketchConstraint(
                ConstraintId(1), ConstraintKind.Horizontal, [Whole(2)]));

        ConstraintInference.ForEntity(sketch, Entity(2)).Select(p => p.Constraint.Kind)
            .Should().NotContain(ConstraintKind.Horizontal);
    }

    [Fact]
    public void SomethingAlreadyTrueTheOtherWayRoundIsStillAlreadyTrue()
    {
        // "A is parallel to B" and "B is parallel to A" are the same sentence.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(3, 4)))
            .With(new SketchLine(Entity(2), new Vec2d(0, 9), new Vec2d(3.02, 13)))
            .With(new SketchConstraint(
                ConstraintId(1), ConstraintKind.Parallel, [Whole(1), Whole(2)]));

        ConstraintInference.ForEntity(sketch, Entity(2)).Select(p => p.Constraint.Kind)
            .Should().NotContain(ConstraintKind.Parallel);
    }

    [Fact]
    public void NothingIsOfferedWhileTheModifierIsHeld()
    {
        // The escape hatch every sketcher needs for the moment its guess is wrong.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(5, 0)))
            .With(new SketchPoint(Entity(2), new Vec2d(9, 9)));

        InferenceOptions held = InferenceOptions.Default with { Suppressed = true };

        ConstraintInference.ForPoint(sketch, Whole(2), new Vec2d(5, 0), held).Should().BeEmpty();
        ConstraintInference.ForEntity(sketch, Entity(1), held).Should().BeEmpty();
    }

    [Fact]
    public void APointBeyondTheEndOfALineIsNotOnIt()
    {
        // It sits on the infinite line through the segment at a distance of nothing, and a
        // sketcher offering "on this line" for a point plainly past its end is offering something
        // the user can see is wrong.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(5, 0)))
            .With(new SketchPoint(Entity(2), new Vec2d(9, 9)));

        ConstraintInference.ForPoint(sketch, Whole(2), new Vec2d(20, 0),
            InferenceOptions.Default with { Tolerance = 0.1 })
            .Should().BeEmpty();
    }

    [Fact]
    public void APointOutsideAnArcsSweepIsNotOnIt()
    {
        // The same story in angle. A point on the circle the arc came from is not on the arc.
        Sketch sketch = Sketch.Empty
            .With(new SketchArc(Entity(1), Vec2d.Zero, 5, 0, System.Math.PI / 2))
            .With(new SketchPoint(Entity(2), new Vec2d(9, 9)));

        ConstraintInference.ForPoint(sketch, Whole(2), new Vec2d(-5, 0),
            InferenceOptions.Default with { Tolerance = 0.1 })
            .Should().BeEmpty("that is a quarter turn outside the sweep");

        ConstraintInference.ForPoint(sketch, Whole(2), new Vec2d(3.53, 3.53),
            InferenceOptions.Default with { Tolerance = 0.1 })
            .Should().NotBeEmpty("and this is inside it");
    }

    [Fact]
    public void HowNearCountsAsNearIsTheCallersToSet()
    {
        // Inference has to feel the same at every zoom, so the tolerance is a model distance the
        // caller works out from pixels. A fixed one would snap to everything when zoomed out and
        // to nothing when zoomed in.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(5, 0)))
            .With(new SketchPoint(Entity(2), new Vec2d(9, 9)));

        Vec2d nearby = new(5.5, 0);

        ConstraintInference.ForPoint(sketch, Whole(2), nearby,
            InferenceOptions.Default with { Tolerance = 0.1 }).Should().BeEmpty();

        ConstraintInference.ForPoint(sketch, Whole(2), nearby,
            InferenceOptions.Default with { Tolerance = 2 }).Should().NotBeEmpty();
    }

    [Fact]
    public void OnlyAFewThingsAreOfferedAtOnce()
    {
        // A cloud of glyphs is noise a user will learn to ignore.
        Sketch sketch = Sketch.Empty;

        for (int i = 1; i <= 8; ++i)
        {
            sketch = sketch.With(new SketchLine(Entity(i), new Vec2d(0, i), new Vec2d(5, i)));
        }

        sketch = sketch.With(new SketchLine(Entity(9), new Vec2d(0, 20), new Vec2d(5, 20.01)));

        ConstraintInference.ForEntity(sketch, Entity(9))
            .Should().HaveCountLessThanOrEqualTo(InferenceOptions.Default.Limit);
    }

    [Fact]
    public void TheSameSketchIsAlwaysGuessedTheSameWay()
    {
        // A sketcher that guessed differently each run would be one nobody could learn. Ties break
        // on position in the sketch, never on an id, whose value is random.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(7), Vec2d.Zero, new Vec2d(5, 0)))
            .With(new SketchLine(Entity(3), new Vec2d(0, 2), new Vec2d(5, 2)))
            .With(new SketchLine(Entity(5), new Vec2d(0, 4), new Vec2d(5, 4.01)));

        ImmutableArray<ConstraintProposal> once = ConstraintInference.ForEntity(sketch, Entity(5));
        ImmutableArray<ConstraintProposal> twice = ConstraintInference.ForEntity(sketch, Entity(5));

        once.Select(p => p.Constraint.Kind).Should().Equal(twice.Select(p => p.Constraint.Kind));
        once.Select(p => p.Glyph).Should().Equal(twice.Select(p => p.Glyph));
    }

    [Fact]
    public void TiesBreakOnPositionInTheSketchAndNotOnAnId()
    {
        // Two equally good guesses, and the entity that comes first in the sketch has the larger
        // id. Ordering by id would offer the other one -- and would offer a different one again in
        // the next process, because a guid's value is random.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(9), Vec2d.Zero, new Vec2d(4, 3)))
            .With(new SketchLine(Entity(1), new Vec2d(0, 9), new Vec2d(4, 12)))
            .With(new SketchLine(Entity(5), new Vec2d(0, 20), new Vec2d(4.01, 23)));

        ImmutableArray<ConstraintProposal> offered =
            ConstraintInference.ForEntity(sketch, Entity(5));

        offered[0].Constraint.Kind.Should().Be(ConstraintKind.Parallel);

        offered[0].Constraint.On.Should().Contain(
            Whole(9), "the first line in the sketch, whatever its id sorts like");
    }

    [Fact]
    public void NothingIsOfferedAboutAnEntityAgainstItself()
    {
        Sketch sketch = Sketch.Empty.With(
            new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(5, 0)));

        ConstraintInference.ForEntity(sketch, Entity(1))
            .Should().OnlyContain(p => p.Constraint.On.Length == 1,
                "a line can be horizontal on its own, and cannot be parallel to itself");
    }

    [Fact]
    public void AnEntityThatIsNotThereIsGuessedAboutNotAtAll()
    {
        ConstraintInference.ForEntity(Sketch.Empty, Entity(1)).Should().BeEmpty();
    }

    [Fact]
    public void EveryProposalCarriesSomethingToDrawAndSomethingToSay()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchCircle(Entity(1), Vec2d.Zero, 5))
            .With(new SketchCircle(Entity(2), new Vec2d(0.1, 0), 5.05));

        ConstraintInference.ForEntity(sketch, Entity(2)).Should().OnlyContain(
            p => p.Glyph.Length > 0 && p.Reason.Length > 0);
    }

    private static SketchPointRef Whole(int entity) => new(Entity(entity));

    private static SketchPointRef Point(int entity, EntityPoint point)
        => new(Entity(entity), point);

    private static SketchEntityId Entity(int n)
        => new(new Guid($"00000000-0000-0000-0000-{n:D12}"));

    private static SketchConstraintId ConstraintId(int n)
        => new(new Guid($"00000000-0000-0000-0001-{n:D12}"));
}
