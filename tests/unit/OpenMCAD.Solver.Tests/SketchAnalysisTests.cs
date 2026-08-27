using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Solver.Sketching;

using Xunit;

namespace OpenMCAD.Solver.Tests;

/// <summary>
/// Degree-of-freedom analysis and subsystem decomposition (P4-T05).
/// </summary>
/// <remarks>
/// The decomposition is what makes §5.6's 16 ms drag budget reachable: a sketch of two hundred
/// entities is almost never one problem, and dragging a corner of one feature has no business
/// refactorising the other eleven.
/// </remarks>
public sealed class SketchAnalysisTests
{
    [Fact]
    public void UnrelatedGeometryFallsIntoSeparateGroups()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(1, 0)))
            .With(new SketchLine(Entity(2), Vec2d.Zero, new Vec2d(0, 1)))
            .With(new SketchCircle(Entity(3), new Vec2d(9, 9), 1))
            .With(Constraint(1, ConstraintKind.Perpendicular, [Whole(1), Whole(2)]));

        SketchAnalysis analysis = SketchAnalysis.Of(sketch);

        analysis.Subsystems.Should().HaveCount(2);
        analysis.Subsystems[0].Entities.Should().Equal(Entity(1), Entity(2));
        analysis.Subsystems[1].Entities.Should().Equal(Entity(3));
    }

    [Fact]
    public void ConstrainingTwoShapesToTheSameGroundDoesNotMergeThem()
    {
        // The decision the whole decomposition rests on. Dimensioning from the origin is how
        // sketches are drawn, and a graph that joined groups through ground would report one
        // subsystem for every sketch anyone ever made -- which is the same as having none.
        Sketch sketch = Sketch.Empty
            .With(new SketchPoint(Entity(1), Vec2d.Zero))
            .With(new SketchLine(Entity(2), Vec2d.Zero, new Vec2d(4, 0)))
            .With(new SketchLine(Entity(3), new Vec2d(0, 9), new Vec2d(4, 9)))
            .With(Constraint(1, ConstraintKind.Fix, [Whole(1)]))
            .With(Constraint(2, ConstraintKind.Distance,
                [Whole(1), Point(2, EntityPoint.Start)], 0))
            .With(Constraint(3, ConstraintKind.Distance,
                [Whole(1), Point(3, EntityPoint.Start)], 9));

        SketchAnalysis analysis = SketchAnalysis.Of(sketch);

        analysis.Ground.Should().Equal(Entity(1));
        analysis.Subsystems.Should().HaveCount(2, "the two lines share nothing that can move");
    }

    [Fact]
    public void AFullyFixedEntityIsGroundAndIsInNoGroup()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchPoint(Entity(1), Vec2d.Zero))
            .With(new SketchPoint(Entity(2), Vec2d.One))
            .With(Constraint(1, ConstraintKind.Fix, [Whole(1)]));

        SketchAnalysis analysis = SketchAnalysis.Of(sketch);

        analysis.Ground.Should().Equal(Entity(1));
        analysis.Containing(Entity(1)).Should().BeNull();
        analysis.Containing(Entity(2)).Should().NotBeNull();
        analysis.FreeEntities.Should().Equal(Entity(2));
    }

    [Fact]
    public void APartlyFixedEntityIsNotGround()
    {
        // A line with one end pinned still has an end that moves, and putting it in no group would
        // leave that end unsolvable.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, Vec2d.One))
            .With(Constraint(1, ConstraintKind.Fix, [Point(1, EntityPoint.Start)]));

        SketchAnalysis analysis = SketchAnalysis.Of(sketch);

        analysis.Ground.Should().BeEmpty();
        analysis.FrozenParameters.Should().Equal(
            [0, 1], "the start's two numbers, and no more");
        analysis.Containing(Entity(1)).Should().NotBeNull();
    }

    [Fact]
    public void AChainOfConstraintsIsOneGroup()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(4, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(4, 0), new Vec2d(4, 3)))
            .With(new SketchLine(Entity(3), new Vec2d(4, 3), Vec2d.Zero))
            .With(Constraint(1, ConstraintKind.Coincident,
                [Point(1, EntityPoint.End), Point(2, EntityPoint.Start)]))
            .With(Constraint(2, ConstraintKind.Coincident,
                [Point(2, EntityPoint.End), Point(3, EntityPoint.Start)]))
            .With(Constraint(3, ConstraintKind.Coincident,
                [Point(3, EntityPoint.End), Point(1, EntityPoint.Start)]));

        SketchAnalysis analysis = SketchAnalysis.Of(sketch);

        analysis.Subsystems.Should().ContainSingle()
            .Which.Entities.Should().Equal(Entity(1), Entity(2), Entity(3));
    }

    [Fact]
    public void AReferenceDimensionJoinsNothing()
    {
        // It measures. Letting it merge two groups would make a drag re-solve geometry that a
        // driving constraint never connected.
        Sketch sketch = Sketch.Empty
            .With(new SketchPoint(Entity(1), Vec2d.Zero))
            .With(new SketchPoint(Entity(2), Vec2d.One))
            .With(Constraint(1, ConstraintKind.Distance, [Whole(1), Whole(2)], 1) with
            { IsDriving = false });

        SketchAnalysis.Of(sketch).Subsystems.Should().HaveCount(2);
    }

    [Fact]
    public void GroupsComeBackInAStableOrder()
    {
        // Two runs that decomposed the same sketch differently would solve it differently, which
        // ADR-0011 does not allow. Largest first, ties broken on position in the sketch -- never on
        // an id, whose value is random and orders differently in the next process.
        Sketch sketch = Sketch.Empty
            .With(new SketchPoint(Entity(5), Vec2d.Zero))
            .With(new SketchLine(Entity(1), Vec2d.Zero, Vec2d.One))
            .With(new SketchLine(Entity(2), Vec2d.Zero, Vec2d.One))
            .With(new SketchPoint(Entity(9), Vec2d.One))
            .With(Constraint(1, ConstraintKind.Parallel, [Whole(1), Whole(2)]));

        ImmutableArray<Subsystem> groups = SketchAnalysis.Of(sketch).Subsystems;

        groups.Select(g => g.Entities.Length).Should().Equal(2, 1, 1);
        groups[0].Entities.Should().Equal(Entity(1), Entity(2));
        groups[1].Entities.Should().Equal(
            [Entity(5)], "it comes first in the sketch");
        groups[2].Entities.Should().Equal(Entity(9));
    }

    [Fact]
    public void AGroupCountsItsOwnFreedom()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(4, 0)))
            .With(new SketchLine(Entity(2), Vec2d.Zero, new Vec2d(0, 4)))
            .With(Constraint(1, ConstraintKind.Perpendicular, [Whole(1), Whole(2)]));

        Subsystem group = SketchAnalysis.Of(sketch).Subsystems[0];

        group.Freedom.Should().Be(8, "two lines");
        group.Removes.Should().Be(1);
        group.RemainingFreedom.Should().Be(7);
    }

    [Fact]
    public void ARestrictedGroupBringsItsGroundWithIt()
    {
        // Leaving the ground out would give the sub-solve a free point where the whole sketch has a
        // pinned one, and it would move geometry that in the real sketch cannot move.
        Sketch sketch = Sketch.Empty
            .With(new SketchPoint(Entity(1), Vec2d.Zero))
            .With(new SketchPoint(Entity(2), new Vec2d(5, 0)))
            .With(new SketchCircle(Entity(3), new Vec2d(9, 9), 1))
            .With(Constraint(1, ConstraintKind.Fix, [Whole(1)]))
            .With(Constraint(2, ConstraintKind.Distance, [Whole(1), Whole(2)], 5));

        SketchAnalysis analysis = SketchAnalysis.Of(sketch);
        Sketch part = analysis.Restrict(analysis.Containing(Entity(2))!);

        part.Entities.Ordered.Select(e => e.Id).Should().Equal(Entity(1), Entity(2));
        part.Constraints.Ordered.Select(c => c.Kind)
            .Should().Contain(ConstraintKind.Fix, "or the ground would not be held");

        part.Entities.Find(Entity(3)).Should().BeNull("the other group is not in this problem");
    }

    [Fact]
    public void ARestrictedGroupIsSmallerThanTheSketch()
    {
        // The point of the whole exercise, stated as a measurement: a drag on one feature of a
        // busy sketch is a small problem, not a large one.
        Sketch sketch = Sketch.Empty;

        for (int i = 1; i <= 40; ++i)
        {
            sketch = sketch
                .With(new SketchLine(Entity(i * 2), Vec2d.Zero, new Vec2d(1, i)))
                .With(new SketchLine(Entity((i * 2) + 1), Vec2d.Zero, new Vec2d(i, 1)))
                .With(Constraint(i, ConstraintKind.Perpendicular,
                    [Whole(i * 2), Whole((i * 2) + 1)]));
        }

        SketchAnalysis analysis = SketchAnalysis.Of(sketch);

        analysis.Subsystems.Should().HaveCount(40);
        analysis.Containing(Entity(2))!.Entities.Should().HaveCount(2);
        analysis.Restrict(analysis.Containing(Entity(2))!).Entities.Count
            .Should().Be(2, "out of eighty");
    }

    [Fact]
    public void AnEmptySketchHasNoGroups()
    {
        SketchAnalysis analysis = SketchAnalysis.Of(Sketch.Empty);

        analysis.Subsystems.Should().BeEmpty();
        analysis.FreeEntities.Should().BeEmpty();
        analysis.Containing(Entity(1)).Should().BeNull();
    }

    [Fact]
    public void UnconstrainedGeometryIsOneGroupEach()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchPoint(Entity(1), Vec2d.Zero))
            .With(new SketchPoint(Entity(2), Vec2d.One));

        SketchAnalysis.Of(sketch).Subsystems.Should().HaveCount(2);
    }

    private static SketchConstraint Constraint(
        int id, ConstraintKind kind, ImmutableArray<SketchPointRef> on, double? value = null)
        => new(ConstraintId(id), kind, on, value);

    private static SketchPointRef Whole(int entity) => new(Entity(entity));

    private static SketchPointRef Point(int entity, EntityPoint point)
        => new(Entity(entity), point);

    private static SketchEntityId Entity(int n)
        => new(new Guid($"00000000-0000-0000-0000-{n:D12}"));

    private static SketchConstraintId ConstraintId(int n)
        => new(new Guid($"00000000-0000-0000-0001-{n:D12}"));
}
