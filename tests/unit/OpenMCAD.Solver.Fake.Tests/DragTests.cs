using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Solver;
using OpenMCAD.Solver.Fake;
using OpenMCAD.Solver.Sketching;

using Xunit;

namespace OpenMCAD.Solver.Fake.Tests;

/// <summary>
/// Dragging: the minimal-motion objective, the budget, and coalescing (P4-T07).
/// </summary>
/// <remarks>
/// §5.6 asks for minimal motion because a drag on an under-constrained sketch has infinitely many
/// correct answers, and the constraints do not care which is chosen. Every one of them satisfies
/// the constraints; all but one look like the drawing jumping.
/// </remarks>
public sealed class DragTests
{
    private static readonly FakeSolver Solver = new();

    [Fact]
    public void DraggingOneEndOfAChainLeavesTheFarEndWhereItWas()
    {
        // The case the objective exists for. Three lines joined end to end, the first end pinned,
        // and nothing else said about them: dragging the second joint has infinitely many answers,
        // and the ones a solver finds without an objective move geometry the user never touched.
        Sketch sketch = Chain();

        Vec2d farEndBefore = sketch.Entities.Locate(Point(3, EntityPoint.End))!.Value;

        SolveResult result = Solver.Solve(
            sketch, new DragTarget(Point(1, EntityPoint.End), new Vec2d(4, 3)));

        result.Sketch.Entities.Locate(Point(1, EntityPoint.End))!.Value
            .Should().BeApproximately(new Vec2d(4, 3), 1e-6, "the dragged joint followed");

        result.Sketch.Entities.Locate(Point(3, EntityPoint.End))!.Value
            .Should().BeApproximately(
                farEndBefore, 1e-6, "and the far end had no reason to move, so it did not");
    }

    [Fact]
    public void OnlyWhatHasToMoveMoves()
    {
        // Stated as a measurement rather than a guess about any one point: the total displacement
        // of everything but the dragged joint is the objective's whole business.
        Sketch sketch = Chain();

        SolveResult result = Solver.Solve(
            sketch, new DragTarget(Point(1, EntityPoint.End), new Vec2d(4, 3)));

        // Everything except the joint itself, which is line 1's end and -- because they are
        // constrained coincident -- line 2's start. Those two have to move: that is what the
        // coincidence means. The question is whether anything else did.
        SketchPointRef[] shouldNotHaveMoved =
        [
            Point(2, EntityPoint.End), Point(3, EntityPoint.Start), Point(3, EntityPoint.End),
        ];

        double moved = shouldNotHaveMoved.Sum(
            point => (result.Sketch.Entities.Locate(point)!.Value
                - sketch.Entities.Locate(point)!.Value).Length);

        moved.Should().BeLessThan(
            1e-5, "nothing but the joint the user had hold of needed to go anywhere");
    }

    [Fact]
    public void AConstraintStillWinsAgainstTheObjective()
    {
        // The objective breaks ties and must never bend a constraint. A weight large enough to
        // hold geometry against a dimension would make drags quietly wrong in a way that only
        // shows up when someone measures the part.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(4, 0)))
            .With(Fixed(1, Point(1, EntityPoint.Start)))
            .With(Constraint(2, ConstraintKind.Distance,
                [Point(1, EntityPoint.Start), Point(1, EntityPoint.End)], 4));

        SolveResult result = Solver.Solve(
            sketch, new DragTarget(Point(1, EntityPoint.End), new Vec2d(0, 40)));

        SketchLine line = (SketchLine)result.Sketch.Entities.Find(Entity(1))!;

        line.Length.Should().BeApproximately(
            4, 1e-6, "the dimension held, however far the pointer went");

        line.End.X.Should().BeApproximately(0, 1e-5, "and the end went the way it was pulled");
    }

    [Fact]
    public void TheDraggedPointWinsTiesAgainstTheRest()
    {
        // Equal weights would split the difference and the geometry would lag behind the cursor.
        Sketch sketch = Sketch.Empty
            .With(new SketchPoint(Entity(1), Vec2d.Zero))
            .With(new SketchPoint(Entity(2), Vec2d.Zero))
            .With(Constraint(1, ConstraintKind.Coincident, [Whole(1), Whole(2)]));

        SolveResult result = Solver.Solve(
            sketch, new DragTarget(Whole(1), new Vec2d(10, 0)));

        // Coincident and both free: the pair must end up together, and nearer the pointer than the
        // midpoint, because the dragged one is pulled harder.
        Vec2d one = result.Sketch.Entities.Locate(Whole(1))!.Value;
        Vec2d other = result.Sketch.Entities.Locate(Whole(2))!.Value;

        (one - other).Length.Should().BeApproximately(0, 1e-6);
        one.X.Should().BeGreaterThan(5, "the pointer pulled harder than the sketch held back");
    }

    [Fact]
    public void ADragOfSomethingComputedRatherThanStoredMovesNothing()
    {
        // An arc's midpoint is a function of the arc, not a parameter of it: there is nothing for
        // the objective to pull. The drag is a no-op rather than a crash or a sketch flung about
        // by an objective that did not know what it was holding.
        Sketch sketch = Sketch.Empty
            .With(new SketchArc(Entity(1), Vec2d.Zero, 2, 0, 1))
            .With(Constraint(1, ConstraintKind.Radius, [Whole(1)], 2));

        SolveResult result = Solver.Solve(
            sketch, new DragTarget(Point(1, EntityPoint.Middle), new Vec2d(9, 9)));

        result.Sketch.Entities.Find(Entity(1)).Should().Be(
            sketch.Entities.Find(Entity(1)), "there was nothing to move");
    }

    [Fact]
    public void ADragSessionSolvesTheLatestPositionAndSkipsTheRest()
    {
        // Every position but the newest is stale by the time it could be worked on. A queue would
        // make the geometry lag further behind the cursor the longer the drag went on.
        DragSession session = new(Solver, Chain(), Point(1, EntityPoint.End));

        session.MoveTo(new Vec2d(1, 1));
        session.MoveTo(new Vec2d(2, 2));
        session.MoveTo(new Vec2d(4, 3));

        session.Skipped.Should().Be(2);

        SolveResult result = session.Solve()!;

        result.Sketch.Entities.Locate(Point(1, EntityPoint.End))!.Value
            .Should().BeApproximately(new Vec2d(4, 3), 1e-6, "the newest position, not the first");

        session.HasWork.Should().BeFalse();
        session.Solve().Should().BeNull("there is nothing left waiting");
    }

    [Fact]
    public void EveryFrameOfADragIsMeasuredFromWhereTheSketchStarted()
    {
        // Chaining frames lets a slow drag creep: each frame's small compromise becomes the next
        // frame's baseline, and geometry the user never touched drifts over a few hundred
        // milliseconds. Moving the pointer back where it began has to give back what was there.
        Sketch start = Chain();

        DragSession session = new(Solver, start, Point(1, EntityPoint.End));

        Vec2d origin = start.Entities.Locate(Point(1, EntityPoint.End))!.Value;

        foreach (Vec2d step in new[]
        {
            new Vec2d(3, 1), new Vec2d(4, 2), new Vec2d(5, 3), new Vec2d(4, 2), origin,
        })
        {
            session.MoveTo(step);
            session.Solve();
        }

        session.Current.Entities.Ordered.Should().HaveCount(start.Entities.Count);

        foreach (SketchEntity entity in start.Entities.Ordered)
        {
            SketchLine was = (SketchLine)entity;
            SketchLine now = (SketchLine)session.Current.Entities.Find(entity.Id)!;

            (now.Start - was.Start).Length.Should().BeLessThan(1e-5, "no creep in {0}", entity.Id);
            (now.End - was.End).Length.Should().BeLessThan(1e-5, "no creep in {0}", entity.Id);
        }
    }

    [Fact]
    public void ACancelledDragPutsEverythingBack()
    {
        Sketch start = Chain();

        DragSession session = new(Solver, start, Point(1, EntityPoint.End));

        session.MoveTo(new Vec2d(9, 9));
        session.Solve();

        session.Current.Should().NotBe(start, "something moved");
        session.Cancel().Should().Be(start);
        session.Current.Should().Be(start);
        session.Last.Should().BeNull();
    }

    [Fact]
    public void CommittingSolvesWhateverWasStillWaiting()
    {
        // Letting go where the pointer actually is has to leave the sketch where the user last saw
        // it heading, not one frame behind.
        DragSession session = new(Solver, Chain(), Point(1, EntityPoint.End));

        session.MoveTo(new Vec2d(4, 3));

        Sketch committed = session.Commit();

        committed.Entities.Locate(Point(1, EntityPoint.End))!.Value
            .Should().BeApproximately(new Vec2d(4, 3), 1e-6);
    }

    [Fact]
    public void ADragSessionWithNothingWaitingSolvesNothing()
    {
        DragSession session = new(Solver, Chain(), Point(1, EntityPoint.End));

        session.HasWork.Should().BeFalse();
        session.Solve().Should().BeNull();
        session.Skipped.Should().Be(0);
    }

    [Fact]
    public void ADragUsesTheDragBudgetByDefault()
    {
        DragSession session = new(Solver, Chain(), Point(1, EntityPoint.End));

        session.MoveTo(new Vec2d(4, 3));

        SolveResult result = session.Solve()!;

        result.Iterations.Should().BeLessThanOrEqualTo(
            SolverOptions.ForDrag.MaximumIterations,
            "a drag is budgeted, and the session applies that without being asked");
    }

    /// <summary>Three lines joined end to end, the first end pinned and nothing else said.</summary>
    private static Sketch Chain() => Sketch.Empty
        .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(3, 0)))
        .With(new SketchLine(Entity(2), new Vec2d(3, 0), new Vec2d(6, 0)))
        .With(new SketchLine(Entity(3), new Vec2d(6, 0), new Vec2d(9, 0)))
        .With(Fixed(1, Point(1, EntityPoint.Start)))
        .With(Constraint(2, ConstraintKind.Coincident,
            [Point(1, EntityPoint.End), Point(2, EntityPoint.Start)]))
        .With(Constraint(3, ConstraintKind.Coincident,
            [Point(2, EntityPoint.End), Point(3, EntityPoint.Start)]));

    private static SketchConstraint Fixed(int id, SketchPointRef point)
        => Constraint(id, ConstraintKind.Fix, [point]);

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
