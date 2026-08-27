using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Solver;
using OpenMCAD.Solver.Fake;
using OpenMCAD.Solver.Sketching;

using Xunit;

namespace OpenMCAD.Solver.Fake.Tests;

/// <summary>
/// A working sketch solver (P4-T02).
/// </summary>
/// <remarks>
/// <para>
/// Every case here is checked by measuring the solved geometry, not by trusting the diagnosis. A
/// solver that reported <c>WellConstrained</c> and moved nothing would satisfy any test that only
/// read the outcome, and that is exactly the failure a fake is prone to.
/// </para>
/// <para>
/// The diagnosis cases are the point of the whole task. §5.6: "over-constrained" without a list is
/// useless to a user, so the tests below check which constraints are named, not merely that
/// something was.
/// </para>
/// </remarks>
public sealed class FakeSolverTests
{
    private static readonly FakeSolver Solver = new();

    [Fact]
    public void ACoincidenceBringsTwoPointsTogether()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchPoint(Entity(1), Vec2d.Zero))
            .With(new SketchPoint(Entity(2), new Vec2d(3, 4)))
            .With(Fixed(1, Whole(1)))
            .With(Constraint(2, ConstraintKind.Coincident, [Whole(1), Whole(2)]));

        SolveResult result = Solver.Solve(sketch);

        result.IsUsable.Should().BeTrue();
        Where(result, 2).Should().BeApproximately(Vec2d.Zero, 1e-7);
        Where(result, 1).Should().BeApproximately(Vec2d.Zero, 1e-12, "a fixed point does not move");
    }

    [Fact]
    public void ADistanceIsMet()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchPoint(Entity(1), Vec2d.Zero))
            .With(new SketchPoint(Entity(2), new Vec2d(3, 0)))
            .With(Fixed(1, Whole(1)))
            .With(Constraint(2, ConstraintKind.Distance, [Whole(1), Whole(2)], 10));

        SolveResult result = Solver.Solve(sketch);

        (Where(result, 2) - Where(result, 1)).Length.Should().BeApproximately(10, 1e-7);
    }

    [Fact]
    public void AHorizontalLineEndsUpHorizontal()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(5, 2)))
            .With(Constraint(1, ConstraintKind.Horizontal, [Whole(1)]));

        SketchLine solved = Line(Solver.Solve(sketch), 1);

        (solved.End.Y - solved.Start.Y).Should().BeApproximately(0, 1e-7);
        solved.Length.Should().BeGreaterThan(1, "the line was straightened, not collapsed");
    }

    [Fact]
    public void APerpendicularPairMeetsAtARightAngle()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(4, 0)))
            .With(new SketchLine(Entity(2), Vec2d.Zero, new Vec2d(3, 1)))
            .With(Constraint(1, ConstraintKind.Perpendicular, [Whole(1), Whole(2)]));

        SolveResult result = Solver.Solve(sketch);

        Vec2d.Dot(Line(result, 1).Direction, Line(result, 2).Direction)
            .Should().BeApproximately(0, 1e-7);
    }

    [Fact]
    public void TwoLinesEndUpParallel()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(4, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(0, 5), new Vec2d(3, 6)))
            .With(Fixed(1, Point(1, EntityPoint.Start)))
            .With(Fixed(2, Point(1, EntityPoint.End)))
            .With(Constraint(3, ConstraintKind.Parallel, [Whole(1), Whole(2)]));

        SolveResult result = Solver.Solve(sketch);

        Vec2d.Cross(Line(result, 1).Direction, Line(result, 2).Direction)
            .Should().BeApproximately(0, 1e-7);

        Line(result, 2).Length.Should().BeGreaterThan(1, "turned, not collapsed");
    }

    [Fact]
    public void GeometryAtVeryDifferentScalesSolvesTogether()
    {
        // Why residuals are scaled to a length. The obvious form of "parallel" is the cross product
        // of the two direction vectors, which grows with both lengths: a metre-long pair then has a
        // residual a million times larger than a millimetre-long pair, a least-squares step spends
        // everything on the long one, and the small one is left visibly unsolved.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(1000, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(0, 50), new Vec2d(900, 130)))
            .With(new SketchLine(Entity(3), new Vec2d(0, -1), new Vec2d(0.001, -0.9997)))
            .With(Fixed(1, Point(1, EntityPoint.Start)))
            .With(Fixed(2, Point(1, EntityPoint.End)))
            .With(Constraint(3, ConstraintKind.Parallel, [Whole(1), Whole(2)]))
            .With(Constraint(4, ConstraintKind.Parallel, [Whole(1), Whole(3)]));

        SolveResult result = Solver.Solve(sketch);

        Vec2d.Cross(Line(result, 1).Direction, Line(result, 2).Direction)
            .Should().BeApproximately(0, 1e-7, "the long line is parallel");

        Vec2d.Cross(Line(result, 1).Direction, Line(result, 3).Direction)
            .Should().BeApproximately(0, 1e-7, "and so is the one a million times smaller");

        // And it is reported as solved. "Solved" is a comparison of the residual against a fixed
        // tolerance, which means the same thing at every scale only if the residuals do: unscaled,
        // a thousand-unit sketch that is perfectly parallel still carries a residual a thousand
        // times larger and is declared a failure.
        result.Diagnosis.Outcome.Should().NotBe(SolveOutcome.Failed);
        result.IsUsable.Should().BeTrue();
    }

    [Fact]
    public void AnAngleAcrossTheBranchCutIsAlreadyNearlySatisfied()
    {
        // An angle and that angle plus a full turn are the same angle. Here the measured angle sits
        // a hair above minus half a turn and the target a hair below plus half a turn -- the same
        // angle, on opposite sides of where atan2 wraps. Unwrapped, the residual is very nearly a
        // full revolution and the solver swings the line right round to fix a sketch that was
        // already right.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(4, 0)))
            .With(new SketchLine(Entity(2), Vec2d.Zero, new Vec2d(-4, -0.004)))
            .With(Fixed(1, Point(1, EntityPoint.Start)))
            .With(Fixed(2, Point(1, EntityPoint.End)))
            .With(Constraint(3, ConstraintKind.Angle,
                [Whole(1), Whole(2)], System.Math.PI - 0.002));

        SolveResult result = Solver.Solve(sketch);

        result.Residual.Should().BeLessThan(1e-6);

        // Asserted on the work, not the geometry. Unwrapped, the solver rotates the line a full
        // revolution and arrives back where it started, so the final geometry is identical either
        // way and only the cost differs -- measured at three iterations against nine. A drag has
        // 16 ms (§5.6), so three times the work for a sketch that was already right is exactly the
        // kind of waste that shows up as a dropped frame rather than as a wrong answer.
        result.Iterations.Should().BeLessThan(6, "the sketch was a thousandth of a radian out");
    }

    [Theory]
    [InlineData(3.0)]
    [InlineData(-3.0)]
    public void APointIsPulledOntoALineFromEitherSide(double startsAt)
    {
        // The distance from a point to a line is signed on purpose. An absolute distance has a kink
        // at zero, which is exactly where the solver is trying to get to, and a derivative that
        // flips sign there stalls the iteration from one side.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)))
            .With(new SketchPoint(Entity(2), new Vec2d(4, startsAt)))
            .With(Fixed(1, Point(1, EntityPoint.Start)))
            .With(Fixed(2, Point(1, EntityPoint.End)))
            .With(Constraint(3, ConstraintKind.PointOnObject, [Whole(2), Whole(1)]));

        SolveResult result = Solver.Solve(sketch);

        Where(result, 2).Y.Should().BeApproximately(0, 1e-7);
    }

    [Fact]
    public void APoorStartingGuessStillConverges()
    {
        // Every step has to earn its place: a Levenberg-Marquardt iteration that took whatever the
        // linear solve suggested, improvement or not, wanders off a badly-conditioned start rather
        // than raising the damping and trying a shorter step.
        Sketch sketch = Sketch.Empty
            .With(new SketchCircle(Entity(1), new Vec2d(0.001, 0.002), 0.003))
            .With(new SketchLine(Entity(2), new Vec2d(-0.002, 0.001), new Vec2d(0.004, 0.0005)))
            .With(Constraint(1, ConstraintKind.Radius, [Whole(1)], 25))
            .With(Constraint(2, ConstraintKind.Tangent, [Whole(1), Whole(2)]))
            .With(Constraint(3, ConstraintKind.Distance,
                [Point(2, EntityPoint.Start), Point(2, EntityPoint.End)], 80))
            .With(Constraint(4, ConstraintKind.Horizontal, [Whole(2)]));

        SolveResult result = Solver.Solve(sketch);

        result.Residual.Should().BeLessThan(1e-6);
        ((SketchCircle)result.Sketch.Entities.Find(Entity(1))!).Radius
            .Should().BeApproximately(25, 1e-6);
        Line(result, 2).Length.Should().BeApproximately(80, 1e-6);
    }

    [Fact]
    public void ATangentLineTouchesTheCircle()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchCircle(Entity(1), Vec2d.Zero, 2))
            .With(new SketchLine(Entity(2), new Vec2d(-5, 3), new Vec2d(5, 3)))
            .With(Constraint(1, ConstraintKind.Radius, [Whole(1)], 2))
            .With(Constraint(2, ConstraintKind.Tangent, [Whole(1), Whole(2)]));

        SolveResult result = Solver.Solve(sketch);

        SketchLine line = Line(result, 2);
        SketchCircle circle = (SketchCircle)result.Sketch.Entities.Find(Entity(1))!;

        Vec2d along = line.Direction;
        double distance = System.Math.Abs(
            Vec2d.Cross(along, circle.Centre - line.Start));

        distance.Should().BeApproximately(circle.Radius, 1e-6);
    }

    [Fact]
    public void ARadiusIsMet()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchCircle(Entity(1), Vec2d.Zero, 1))
            .With(Constraint(1, ConstraintKind.Radius, [Whole(1)], 7));

        SolveResult result = Solver.Solve(sketch);

        ((SketchCircle)result.Sketch.Entities.Find(Entity(1))!).Radius
            .Should().BeApproximately(7, 1e-7);
    }

    [Fact]
    public void ADiameterIsHalfARadius()
    {
        // The one place a sign or a factor of two goes unnoticed, because both constraints solve.
        Sketch sketch = Sketch.Empty
            .With(new SketchCircle(Entity(1), Vec2d.Zero, 1))
            .With(Constraint(1, ConstraintKind.Diameter, [Whole(1)], 10));

        ((SketchCircle)Solver.Solve(sketch).Sketch.Entities.Find(Entity(1))!).Radius
            .Should().BeApproximately(5, 1e-7);
    }

    [Fact]
    public void AMidpointEndsUpInTheMiddle()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)))
            .With(new SketchPoint(Entity(2), new Vec2d(1, 4)))
            .With(Fixed(1, Point(1, EntityPoint.Start)))
            .With(Fixed(2, Point(1, EntityPoint.End)))
            .With(Constraint(3, ConstraintKind.Midpoint, [Whole(2), Whole(1)]));

        Where(Solver.Solve(sketch), 2).Should().BeApproximately(new Vec2d(5, 0), 1e-7);
    }

    [Fact]
    public void ASymmetricPairIsAReflection()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(0, 10)))
            .With(new SketchPoint(Entity(2), new Vec2d(-3, 5)))
            .With(new SketchPoint(Entity(3), new Vec2d(4, 6)))
            .With(Fixed(1, Point(1, EntityPoint.Start)))
            .With(Fixed(2, Point(1, EntityPoint.End)))
            .With(Fixed(3, Whole(2)))
            .With(Constraint(4, ConstraintKind.Symmetric, [Whole(2), Whole(3), Whole(1)]));

        SolveResult result = Solver.Solve(sketch);

        // Mirrored in the Y axis: same height, opposite side.
        Where(result, 3).Should().BeApproximately(new Vec2d(3, 5), 1e-6);
    }

    [Fact]
    public void AnAngleIsMet()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(4, 0)))
            .With(new SketchLine(Entity(2), Vec2d.Zero, new Vec2d(4, 1)))
            .With(Fixed(1, Point(1, EntityPoint.Start)))
            .With(Fixed(2, Point(1, EntityPoint.End)))
            .With(Constraint(3, ConstraintKind.Angle,
                [Whole(1), Whole(2)], System.Math.PI / 4));

        SolveResult result = Solver.Solve(sketch);

        double between = System.Math.Atan2(
            Vec2d.Cross(Line(result, 1).Direction, Line(result, 2).Direction),
            Vec2d.Dot(Line(result, 1).Direction, Line(result, 2).Direction));

        System.Math.Abs(between).Should().BeApproximately(System.Math.PI / 4, 1e-6);
    }

    [Fact]
    public void AChainOfConstraintsSolvesTogether()
    {
        // Three lines into a right triangle: the interesting case, because no constraint can be
        // satisfied on its own without disturbing another.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(4.2, 0.3)))
            .With(new SketchLine(Entity(2), new Vec2d(4.2, 0.3), new Vec2d(4, 3.1)))
            .With(new SketchLine(Entity(3), new Vec2d(4, 3.1), new Vec2d(0.1, -0.2)))
            .With(Fixed(1, Point(1, EntityPoint.Start)))
            .With(Constraint(2, ConstraintKind.Coincident,
                [Point(1, EntityPoint.End), Point(2, EntityPoint.Start)]))
            .With(Constraint(3, ConstraintKind.Coincident,
                [Point(2, EntityPoint.End), Point(3, EntityPoint.Start)]))
            .With(Constraint(4, ConstraintKind.Coincident,
                [Point(3, EntityPoint.End), Point(1, EntityPoint.Start)]))
            .With(Constraint(5, ConstraintKind.Horizontal, [Whole(1)]))
            .With(Constraint(6, ConstraintKind.Vertical, [Whole(2)]))
            .With(Constraint(7, ConstraintKind.Distance,
                [Point(1, EntityPoint.Start), Point(1, EntityPoint.End)], 4))
            .With(Constraint(8, ConstraintKind.Distance,
                [Point(2, EntityPoint.Start), Point(2, EntityPoint.End)], 3));

        SolveResult result = Solver.Solve(sketch);

        result.Residual.Should().BeLessThan(1e-7);

        Line(result, 1).Start.Should().BeApproximately(Vec2d.Zero, 1e-9);
        Line(result, 1).Length.Should().BeApproximately(4, 1e-6);
        Line(result, 2).Length.Should().BeApproximately(3, 1e-6);
        (Line(result, 1).End.Y - Line(result, 1).Start.Y).Should().BeApproximately(0, 1e-6);
        (Line(result, 2).End.X - Line(result, 2).Start.X).Should().BeApproximately(0, 1e-6);
    }

    [Fact]
    public void AnUnderConstrainedSketchSaysHowMuchFreedomIsLeft()
    {
        // A sketch being drawn is under-constrained almost all the time. Reporting that as a
        // failure would put an error against every sketch in progress.
        Sketch sketch = Sketch.Empty.With(new SketchPoint(Entity(1), Vec2d.Zero));

        SolveResult result = Solver.Solve(sketch);

        result.Diagnosis.Outcome.Should().Be(SolveOutcome.UnderConstrained);
        result.Diagnosis.RemainingFreedom.Should().Be(2);
        result.IsUsable.Should().BeTrue();
    }

    [Fact]
    public void AFullyDefinedSketchSaysSo()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchPoint(Entity(1), new Vec2d(3, 4)))
            .With(Fixed(1, Whole(1)));

        Solver.Solve(sketch).Diagnosis.Outcome.Should().Be(SolveOutcome.WellConstrained);
    }

    [Fact]
    public void ARedundantConstraintIsNamed()
    {
        // Two constraints saying the same true thing. The sketch solves, and one of them is doing
        // nothing -- which is a different situation from a contradiction and needs a different fix.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(5, 0.1)))
            .With(Fixed(1, Point(1, EntityPoint.Start)))
            .With(Constraint(2, ConstraintKind.Horizontal, [Whole(1)]))
            .With(Constraint(3, ConstraintKind.Horizontal, [Whole(1)]));

        SolveResult result = Solver.Solve(sketch);

        result.Diagnosis.Outcome.Should().Be(SolveOutcome.Redundant);
        result.Diagnosis.Surplus.Should().Contain(ConstraintId(3), "the later of the pair");
        result.IsUsable.Should().BeTrue("a redundant sketch still solves");
    }

    [Fact]
    public void AContradictionIsNamedRatherThanLeftAsAFailureToConverge()
    {
        // Two lengths for one line. Nothing satisfies both, and the user needs to know which two
        // constraints are arguing rather than that the numbers did not settle.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(5, 0)))
            .With(Fixed(1, Point(1, EntityPoint.Start)))
            .With(Constraint(2, ConstraintKind.Horizontal, [Whole(1)]))
            .With(Constraint(3, ConstraintKind.Distance,
                [Point(1, EntityPoint.Start), Point(1, EntityPoint.End)], 4))
            .With(Constraint(4, ConstraintKind.Distance,
                [Point(1, EntityPoint.Start), Point(1, EntityPoint.End)], 9));

        SolveResult result = Solver.Solve(sketch);

        result.Diagnosis.Outcome.Should().Be(SolveOutcome.OverConstrained);
        result.Diagnosis.Conflicts.Should().NotBeEmpty("a list is the whole value of the message");
        result.Diagnosis.Message.Should().Contain("contradict");
        result.IsUsable.Should().BeFalse();
    }

    [Fact]
    public void AReferenceDimensionDoesNotMoveAnything()
    {
        // It measures. A solver that acted on one would move geometry to satisfy a number the user
        // explicitly said was only being displayed.
        Sketch sketch = Sketch.Empty
            .With(new SketchCircle(Entity(1), Vec2d.Zero, 3))
            .With(Constraint(1, ConstraintKind.Radius, [Whole(1)], 99) with { IsDriving = false });

        ((SketchCircle)Solver.Solve(sketch).Sketch.Entities.Find(Entity(1))!).Radius
            .Should().Be(3);
    }

    [Fact]
    public void ADragMovesWhatIsHeldAndPullsTheRestAlong()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(4, 0)))
            .With(Fixed(1, Point(1, EntityPoint.Start)))
            .With(Constraint(2, ConstraintKind.Distance,
                [Point(1, EntityPoint.Start), Point(1, EntityPoint.End)], 4));

        SolveResult result = Solver.Solve(
            sketch,
            new DragTarget(Point(1, EntityPoint.End), new Vec2d(0, 9)),
            SolverOptions.ForDrag);

        SketchLine line = Line(result, 1);

        line.Start.Should().BeApproximately(Vec2d.Zero, 1e-9, "the fixed end stayed put");
        line.Length.Should().BeApproximately(
            4, 1e-6, "the length constraint still holds during a drag");
        line.End.Should().BeApproximately(
            new Vec2d(0, 4), 1e-6, "so the end slid up the circle towards the pointer");
    }

    [Fact]
    public void ASolveNeverThrowsForASketchItCannotSolve()
    {
        // The sketcher has to draw the result either way, and a user mid-drag with a momentarily
        // impossible sketch is the ordinary case rather than an exceptional one.
        Sketch nonsense = Sketch.Empty
            .With(new SketchCircle(Entity(1), Vec2d.Zero, 0))
            .With(Constraint(1, ConstraintKind.Radius, [Whole(9)], 5));

        Action solve = () => Solver.Solve(nonsense);

        solve.Should().NotThrow();
    }

    [Fact]
    public void ASolveIsAbandonedWhenItsBudgetRunsOut()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchPoint(Entity(1), Vec2d.Zero))
            .With(new SketchPoint(Entity(2), new Vec2d(1, 1)))
            .With(Constraint(1, ConstraintKind.Distance, [Whole(1), Whole(2)], 5));

        SolveResult result = Solver.Solve(
            sketch, options: new SolverOptions(MaximumIterations: 1, Tolerance: 1e-15));

        result.Iterations.Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public void ACancelledSolveStopsRatherThanFinishing()
    {
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        SolveResult result = Solver.Solve(
            Sketch.Empty.With(new SketchPoint(Entity(1), Vec2d.Zero)),
            cancellationToken: cancelled.Token);

        result.Iterations.Should().Be(0);
    }

    [Fact]
    public void SolvingTheSameSketchTwiceGivesTheSameAnswer()
    {
        // ADR-0011. A solver whose answer depended on anything but its input would make every
        // regression fixture in Phase 4 meaningless.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(4.2, 0.3)))
            .With(new SketchLine(Entity(2), new Vec2d(4.2, 0.3), new Vec2d(4, 3.1)))
            .With(Constraint(1, ConstraintKind.Coincident,
                [Point(1, EntityPoint.End), Point(2, EntityPoint.Start)]))
            .With(Constraint(2, ConstraintKind.Perpendicular, [Whole(1), Whole(2)]));

        SolveResult first = Solver.Solve(sketch);
        SolveResult second = Solver.Solve(sketch);

        first.Sketch.Should().Be(second.Sketch);
        first.Iterations.Should().Be(second.Iterations);
    }

    private static Vec2d Where(SolveResult result, int entity)
        => result.Sketch.Entities.Locate(Whole(entity))!.Value;

    private static SketchLine Line(SolveResult result, int entity)
        => (SketchLine)result.Sketch.Entities.Find(Entity(entity))!;

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

/// <summary>Comparison helpers that read well in a geometric assertion.</summary>
internal static class VectorAssertions
{
    /// <summary>Asserts that a point is where it should be.</summary>
    public static void BeApproximately(
        this FluentAssertions.Primitives.ObjectAssertions assertions,
        Vec2d expected,
        double tolerance,
        string because = "")
    {
        Vec2d actual = (Vec2d)assertions.Subject;

        actual.X.Should().BeApproximately(expected.X, tolerance, because);
        actual.Y.Should().BeApproximately(expected.Y, tolerance, because);
    }
}
