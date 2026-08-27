using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Solver.Sketching;

using Xunit;

namespace OpenMCAD.Solver.Tests;

/// <summary>
/// The geometry of one sketch (P4-T03).
/// </summary>
public sealed class SketchEntitySetTests
{
    [Fact]
    public void EntitiesKeepTheOrderTheyWereDrawnIn()
    {
        // A solver reads the entities in some order. If that order came from a dictionary the same
        // sketch would converge differently on two machines, which ADR-0011 does not allow.
        SketchEntitySet set = SketchEntitySet.Of(
        [
            new SketchPoint(Id(3), Vec2d.Zero),
            new SketchPoint(Id(1), Vec2d.One),
            new SketchPoint(Id(2), new Vec2d(2, 2)),
        ]);

        set.Ordered.Select(e => e.Id).Should().Equal(Id(3), Id(1), Id(2));
    }

    [Fact]
    public void ReplacingAnEntityLeavesItWhereItWas()
    {
        // Every solve writes back every point. If that reordered the set, one drag would change the
        // order the next solve read them in, and the sketch would drift from its own history for no
        // reason a user could see.
        SketchEntitySet set = SketchEntitySet.Of(
        [
            new SketchPoint(Id(1), Vec2d.Zero),
            new SketchPoint(Id(2), Vec2d.Zero),
            new SketchPoint(Id(3), Vec2d.Zero),
        ]);

        SketchEntitySet moved = set.With(new SketchPoint(Id(1), new Vec2d(9, 9)));

        moved.Ordered.Select(e => e.Id).Should().Equal(Id(1), Id(2), Id(3));
        moved.Find(Id(1)).Should().BeOfType<SketchPoint>()
            .Which.Position.Should().Be(new Vec2d(9, 9));
    }

    [Fact]
    public void AnEntityWithNoIdIsRefused()
    {
        Action add = () => SketchEntitySet.Empty.With(new SketchPoint(SketchEntityId.None, Vec2d.Zero));

        add.Should().Throw<ArgumentException>().WithMessage("*nothing could constrain it*");
    }

    [Fact]
    public void ARemovedEntityIsGoneFromBothTheOrderAndTheLookup()
    {
        // Two collections holding the same thing is two chances to disagree, and a lookup that
        // still answered for a deleted entity would let a stale constraint resolve.
        SketchEntitySet set = SketchEntitySet
            .Of([new SketchPoint(Id(1), Vec2d.Zero), new SketchPoint(Id(2), Vec2d.One)])
            .Without(Id(1));

        set.Count.Should().Be(1);
        set.Find(Id(1)).Should().BeNull();
        set.Ordered.Should().NotContain(e => e.Id == Id(1));
    }

    [Fact]
    public void RemovingSomethingThatIsNotThereChangesNothing()
    {
        SketchEntitySet set = SketchEntitySet.Of([new SketchPoint(Id(1), Vec2d.Zero)]);

        set.Without(Id(9)).Should().BeSameAs(set);
    }

    [Fact]
    public void APointReferenceBecomesACoordinateInOnePlace()
    {
        // Constraint evaluation, inference, snapping and the drag objective all ask this. Four
        // answers would be four chances to disagree about where the middle of an arc is.
        SketchEntitySet set = SketchEntitySet.Of(
            [new SketchArc(Id(1), Vec2d.Zero, 2, 0, System.Math.PI)]);

        set.Locate(new SketchPointRef(Id(1), EntityPoint.Start))!.Value.X
            .Should().BeApproximately(2, 1e-9);

        set.Locate(new SketchPointRef(Id(1), EntityPoint.Centre)).Should().Be(Vec2d.Zero);
    }

    [Fact]
    public void APointReferenceToSomethingAbsentResolvesToNothing()
    {
        SketchEntitySet set = SketchEntitySet.Of([new SketchCircle(Id(1), Vec2d.Zero, 1)]);

        set.Locate(new SketchPointRef(Id(9))).Should().BeNull("there is no such entity");
        set.Locate(new SketchPointRef(Id(1), EntityPoint.Start))
            .Should().BeNull("a full circle has no start");
    }

    [Fact]
    public void ConstructionAndProfileGeometryAreTheSameSetSeenTwoWays()
    {
        SketchEntitySet set = SketchEntitySet.Of(
        [
            new SketchLine(Id(1), Vec2d.Zero, Vec2d.One),
            new SketchLine(Id(2), Vec2d.Zero, Vec2d.One, IsConstruction: true),
        ]);

        set.Profile.Select(e => e.Id).Should().Equal(Id(1));
        set.Construction.Select(e => e.Id).Should().Equal(Id(2));
        set.Count.Should().Be(2, "construction geometry is in the sketch, not beside it");
    }

    [Fact]
    public void ASketchReportsEveryDegenerateEntityRatherThanTheFirst()
    {
        // A user fixing one and finding another has been told half the truth twice.
        SketchEntitySet set = SketchEntitySet.Of(
        [
            new SketchLine(Id(1), Vec2d.Zero, Vec2d.Zero),
            new SketchCircle(Id(2), Vec2d.Zero, 1),
            new SketchCircle(Id(3), Vec2d.Zero, 0),
        ]);

        set.Degeneracies.Should().HaveCount(2);
    }

    [Fact]
    public void ASoundSketchHasNothingToReport()
    {
        SketchEntitySet set = SketchEntitySet.Of(
        [
            new SketchLine(Id(1), Vec2d.Zero, Vec2d.One),
            new SketchCircle(Id(2), Vec2d.Zero, 1),
        ]);

        set.Degeneracies.Should().BeEmpty();
    }

    private static SketchEntityId Id(int n)
        => new(new Guid($"00000000-0000-0000-0000-{n:D12}"));
}
