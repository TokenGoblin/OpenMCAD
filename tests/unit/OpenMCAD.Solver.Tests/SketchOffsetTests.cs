using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Solver.Sketching;

using Xunit;

namespace OpenMCAD.Solver.Tests;

/// <summary>Building a parallel copy of a chain of sketch geometry (P4-T13).</summary>
public sealed class SketchOffsetTests
{
    private const double Tol = 1e-9;

    [Fact]
    public void Line_MovesSidewaysToTheLeftOfTheWayItRuns()
    {
        Sketch sketch = Sketch.Empty.With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)));

        OffsetResult result = SketchOffset.Offset(sketch, [Entity(1)], 2);

        result.IsResolved.Should().BeTrue(result.Reason);

        SketchLine offset = (SketchLine)result.Sketch!.Entities.Find(result.Created.Single())!;

        At(offset.Start, 0, 2, "left of a line running along +X is +Y");
        At(offset.End, 10, 2);
    }

    [Fact]
    public void Line_GoesTheOtherWayForANegativeDistance()
    {
        Sketch sketch = Sketch.Empty.With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)));

        OffsetResult result = SketchOffset.Offset(sketch, [Entity(1)], -2);

        SketchLine offset = (SketchLine)result.Sketch!.Entities.Find(result.Created.Single())!;

        At(offset.Start, 0, -2);
    }

    [Fact]
    public void Offset_LeavesTheOriginalGeometryAndItsConstraintsAlone()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)))
            .With(SketchConstraint.Of(ConstraintKind.Horizontal, [new(Entity(1))]));

        OffsetResult result = SketchOffset.Offset(sketch, [Entity(1)], 2);

        SketchLine original = (SketchLine)result.Sketch!.Entities.Find(Entity(1))!;

        original.Start.Should().Be(Vec2d.Zero, "offsetting only ever adds");
        original.End.Should().Be(new Vec2d(10, 0));
        result.Sketch.Constraints.Ordered.Should().ContainSingle(c => c.Kind == ConstraintKind.Horizontal);
    }

    [Fact]
    public void Circle_ShrinksForAPositiveDistanceBecauseItsInsideIsItsLeft()
    {
        Sketch sketch = Sketch.Empty.With(new SketchCircle(Entity(1), new Vec2d(1, 1), 5));

        OffsetResult result = SketchOffset.Offset(sketch, [Entity(1)], 2);

        SketchCircle offset = (SketchCircle)result.Sketch!.Entities.Find(result.Created.Single())!;

        offset.Radius.Should().BeApproximately(3, Tol, "an arc runs anticlockwise, so its left is inward");
        offset.Centre.Should().Be(new Vec2d(1, 1), "an offset circle stays concentric");
    }

    [Fact]
    public void Circle_RefusesToBeOffsetAwayToNothing()
    {
        Sketch sketch = Sketch.Empty.With(new SketchCircle(Entity(1), Vec2d.Zero, 5));

        OffsetResult result = SketchOffset.Offset(sketch, [Entity(1)], 5);

        result.Outcome.Should().Be(OffsetOutcome.DoesNotFit);
    }

    [Fact]
    public void Circle_CannotBeOffsetAsPartOfAChain()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchCircle(Entity(1), Vec2d.Zero, 5))
            .With(new SketchLine(Entity(2), new Vec2d(5, 0), new Vec2d(10, 0)));

        OffsetResult result = SketchOffset.Offset(sketch, [Entity(1), Entity(2)], 1);

        result.Outcome.Should().Be(OffsetOutcome.NotAChain);
    }

    [Fact]
    public void Chain_MitresTheJoinBetweenTwoOffsetLines()
    {
        // An L turning left: offsetting to the left of travel puts both offsets on the inside, where
        // they overshoot one another and have to be cut back to where they cross.
        Sketch sketch = Corner();

        OffsetResult result = SketchOffset.Offset(sketch, [Entity(1), Entity(2)], 2);

        result.IsResolved.Should().BeTrue(result.Reason);
        result.Created.Should().HaveCount(2);

        SketchLine first = (SketchLine)result.Sketch!.Entities.Find(result.Created[0])!;
        SketchLine second = (SketchLine)result.Sketch.Entities.Find(result.Created[1])!;

        At(first.Start, 0, 2);
        At(first.End, 8, 2, "cut back from (10,2) to where the second offset crosses it");
        At(second.Start, 8, 2, "both ends of the join land on the same point");
        At(second.End, 8, 10);
    }

    [Fact]
    public void Chain_MitresOutwardsWhenTheOffsetIsOnTheOtherSide()
    {
        // The same L offset the other way. Now both offsets fall short of one another and the join
        // has to reach out past where either of them ended.
        Sketch sketch = Corner();

        OffsetResult result = SketchOffset.Offset(sketch, [Entity(1), Entity(2)], -2);

        result.IsResolved.Should().BeTrue(result.Reason);

        SketchLine first = (SketchLine)result.Sketch!.Entities.Find(result.Created[0])!;
        SketchLine second = (SketchLine)result.Sketch.Entities.Find(result.Created[1])!;

        At(first.End, 12, -2, "the offset was extended past its own end to reach the join");
        At(second.Start, 12, -2);
    }

    [Fact]
    public void Chain_WorksOutTheOrderForItselfWhateverOrderItIsGiven()
    {
        // Both lines already run the same way round the corner, so naming either first describes
        // the same chain running the same way -- and the offset must not depend on which was named.
        Sketch sketch = Corner();

        OffsetResult forwards = SketchOffset.Offset(sketch, [Entity(1), Entity(2)], 2);
        OffsetResult backwards = SketchOffset.Offset(sketch, [Entity(2), Entity(1)], 2);

        forwards.IsResolved.Should().BeTrue(forwards.Reason);
        backwards.IsResolved.Should().BeTrue(backwards.Reason);

        Corners(forwards).Should().BeEquivalentTo(
            Corners(backwards),
            o => o.Using<double>(c => c.Subject.Should().BeApproximately(c.Expectation, 1e-6))
                .WhenTypeIs<double>());
    }

    [Fact]
    public void Chain_RunsThroughTheEntityNamedFirstInThatEntitysOwnDirection()
    {
        // Two lines pointing *at* their shared corner, so the chain cannot traverse both forwards.
        // Which one is named first therefore decides which way the chain runs, and so which side a
        // positive distance lands on. Nothing else may decide it -- least of all selection order
        // deciding it invisibly.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(10, 10), new Vec2d(10, 0)));

        OffsetResult alongFirst = SketchOffset.Offset(sketch, [Entity(1), Entity(2)], 2);
        OffsetResult alongSecond = SketchOffset.Offset(sketch, [Entity(2), Entity(1)], 2);

        alongFirst.IsResolved.Should().BeTrue(alongFirst.Reason);
        alongSecond.IsResolved.Should().BeTrue(alongSecond.Reason);

        // Created comes back in chain order, so the piece named first is the one built first.
        SketchLine horizontal = (SketchLine)alongFirst.Sketch!.Entities.Find(alongFirst.Created[0])!;
        SketchLine vertical = (SketchLine)alongSecond.Sketch!.Entities.Find(alongSecond.Created[0])!;
        SketchLine alsoHorizontal =
            (SketchLine)alongSecond.Sketch.Entities.Find(alongSecond.Created[1])!;

        // Naming the horizontal line first runs the chain along +X, whose left is +Y.
        At(horizontal.Start, 0, 2);

        // Naming the vertical line first runs the chain from (10,10) down, whose left is +X, and
        // then back along the horizontal line reversed, whose left is now -Y. Same selection, same
        // positive distance, and every piece of it on the opposite side.
        At(vertical.Start, 12, 10);
        At(alsoHorizontal.Start, 0, -2);
    }

    [Fact]
    public void Chain_JoinsEachNewPieceToTheNextWithACoincidence()
    {
        Sketch sketch = Corner();

        OffsetResult result = SketchOffset.Offset(sketch, [Entity(1), Entity(2)], 2);

        SketchConstraint[] added = [.. result.Sketch!.Constraints.Ordered];

        added.Should().ContainSingle().Which.Kind.Should().Be(ConstraintKind.Coincident);
        added[0].On.Select(o => o.Entity).Should().BeEquivalentTo(result.Created);
    }

    [Fact]
    public void Chain_ClosesTheLoopWhenTheGeometryDoes()
    {
        Sketch sketch = Square();

        OffsetResult result = SketchOffset.Offset(sketch, [.. Ids(1, 2, 3, 4)], 1);

        result.IsResolved.Should().BeTrue(result.Reason);
        result.Created.Should().HaveCount(4);
        result.Sketch!.Constraints.Count.Should().Be(
            4, "a closed loop of four has four joins, not three");

        SketchLine[] offsets =
            [.. result.Created.Select(id => (SketchLine)result.Sketch.Entities.Find(id)!)];

        // A square of side 10 offset inwards by 1 is a square of side 8.
        offsets.SelectMany(l => new[] { l.Start, l.End })
            .Should().OnlyContain(p =>
                System.Math.Abs(System.Math.Abs(p.X - 5) - 4) < 1e-6
                && System.Math.Abs(System.Math.Abs(p.Y - 5) - 4) < 1e-6);
    }

    [Fact]
    public void Chain_LeavesATangentJoinExactlyWhereItAlreadyIs()
    {
        // A line running into a quarter arc it is tangent to. Their offsets are tangent too, so
        // they already touch and mitring has nothing to do -- a case that must not be nudged.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), new Vec2d(-10, 0), Vec2d.Zero))
            .With(new SketchArc(Entity(2), new Vec2d(0, 5), 5, -System.Math.PI / 2, 0));

        OffsetResult result = SketchOffset.Offset(sketch, [Entity(1), Entity(2)], 1);

        result.IsResolved.Should().BeTrue(result.Reason);

        SketchLine line = (SketchLine)result.Sketch!.Entities.Find(result.Created[0])!;
        SketchArc arc = (SketchArc)result.Sketch.Entities.Find(result.Created[1])!;

        At(line.End, 0, 1);
        At(arc.PointAt(0), 0, 1, "the tangent join was already met and stayed put");
        arc.Radius.Should().BeApproximately(4, Tol);
        arc.Sweep.Should().BeApproximately(System.Math.PI / 2, Tol, "and its sweep was not disturbed");
    }

    [Fact]
    public void Arc_GrowsWhenTheChainRunsThroughItBackwards()
    {
        // The line is named first and runs down into the corner, so the chain enters the arc at the
        // arc's own End and travels clockwise through it, putting the centre on its right -- which
        // makes a positive offset the larger circle, not the smaller.
        Sketch sketch = Sketch.Empty
            .With(new SketchArc(Entity(2), new Vec2d(0, 5), 5, -System.Math.PI / 2, 0))
            .With(new SketchLine(Entity(1), new Vec2d(5, 20), new Vec2d(5, 5)));

        OffsetResult result = SketchOffset.Offset(sketch, [Entity(1), Entity(2)], 1);

        result.IsResolved.Should().BeTrue(result.Reason);

        SketchArc arc = (SketchArc)result.Sketch!.Entities
            .Find(result.Created.Single(id => result.Sketch.Entities.Find(id) is SketchArc))!;

        arc.Radius.Should().BeApproximately(6, Tol);
    }

    [Fact]
    public void Chain_RefusesADistanceThatConsumesAPieceOfIt()
    {
        // A short middle segment between two corners that both eat into it. Offsetting far enough
        // pushes its two ends past one another, which is a collapse rather than a small piece.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), new Vec2d(0, 10), Vec2d.Zero))
            .With(new SketchLine(Entity(2), Vec2d.Zero, new Vec2d(1, 0)))
            .With(new SketchLine(Entity(3), new Vec2d(1, 0), new Vec2d(1, 10)));

        OffsetResult result = SketchOffset.Offset(sketch, [.. Ids(1, 2, 3)], 5);

        result.Outcome.Should().Be(OffsetOutcome.DoesNotFit);
        result.Sketch.Should().BeNull();
    }

    [Fact]
    public void Chain_RefusesADistanceThatTurnsAnArcInsideOut()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchArc(Entity(1), Vec2d.Zero, 3, 0, System.Math.PI / 2));

        OffsetResult result = SketchOffset.Offset(sketch, [Entity(1)], 3);

        result.Outcome.Should().Be(OffsetOutcome.DoesNotFit);
    }

    [Fact]
    public void Chain_JoinsTwoCollinearPiecesWithoutAskingWhereTheyCross()
    {
        // Two segments in a straight line are a join that turns through nothing, so their offsets
        // are the same infinite line and have no single crossing to be taken out to. The pieces
        // already meet, and noticing that is what keeps a perfectly ordinary chain from failing.
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(5, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(5, 0), new Vec2d(10, 0)));

        OffsetResult result = SketchOffset.Offset(sketch, [Entity(1), Entity(2)], 2);

        result.IsResolved.Should().BeTrue(result.Reason);

        SketchLine first = (SketchLine)result.Sketch!.Entities.Find(result.Created[0])!;
        SketchLine second = (SketchLine)result.Sketch.Entities.Find(result.Created[1])!;

        At(first.Start, 0, 2);
        At(first.End, 5, 2, "the join was already met, so nothing moved it");
        At(second.Start, 5, 2);
        At(second.End, 10, 2);
    }

    [Fact]
    public void Chain_RefusesADistanceThatPushesAnArcsEndsPastOneAnother()
    {
        // A very shallow arc bridging (1,0) to (0,0) between two long uprights, offset far enough
        // that both joins cut past it. What is left reports a sweep of nearly a full turn -- large,
        // positive, and entirely plausible to anything that only looks at the result -- when what
        // actually happened is that its two ends swapped places.
        double startAngle = System.Math.Atan2(20, 0.5);
        double endAngle = System.Math.Atan2(20, -0.5);

        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), new Vec2d(0, 10), Vec2d.Zero))
            .With(new SketchArc(Entity(2), new Vec2d(0.5, -20), System.Math.Sqrt(400.25), startAngle, endAngle))
            .With(new SketchLine(Entity(3), new Vec2d(1, 0), new Vec2d(1, 10)));

        OffsetResult result = SketchOffset.Offset(sketch, [.. Ids(1, 2, 3)], 5);

        result.Outcome.Should().Be(OffsetOutcome.DoesNotFit);
        result.Sketch.Should().BeNull();
    }

    [Fact]
    public void Chain_RefusesABranchThatLeavesNoLooseEndsToNotice()
    {
        // A closed square with a diagonal across it. Every node has an even-looking story -- there
        // are no free ends at all -- so nothing about the shape of the selection gives it away
        // except that two of its corners have three things meeting at them.
        Sketch sketch = Square()
            .With(new SketchLine(Entity(5), Vec2d.Zero, new Vec2d(10, 10)));

        OffsetResult result = SketchOffset.Offset(sketch, [.. Ids(1, 2, 3, 4, 5)], 1);

        result.Outcome.Should().Be(OffsetOutcome.NotAChain);
    }

    [Fact]
    public void Chain_RefusesASelectionThatBranches()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(10, 0), new Vec2d(10, 10)))
            .With(new SketchLine(Entity(3), new Vec2d(10, 0), new Vec2d(10, -10)));

        OffsetResult result = SketchOffset.Offset(sketch, [.. Ids(1, 2, 3)], 1);

        result.Outcome.Should().Be(OffsetOutcome.NotAChain);
    }

    [Fact]
    public void Chain_RefusesTwoRunsThatDoNotTouch()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)))
            .With(new SketchLine(Entity(2), new Vec2d(20, 0), new Vec2d(30, 0)));

        OffsetResult result = SketchOffset.Offset(sketch, [Entity(1), Entity(2)], 1);

        result.Outcome.Should().Be(OffsetOutcome.NotAChain);
    }

    [Fact]
    public void Offset_RefusesAKindItCannotBuildAParallelOf()
    {
        Sketch sketch = Sketch.Empty
            .With(new SketchEllipse(Entity(1), Vec2d.Zero, 5, 3, 0));

        OffsetResult result = SketchOffset.Offset(sketch, [Entity(1)], 1);

        result.Outcome.Should().Be(OffsetOutcome.Unsupported);
    }

    [Fact]
    public void Offset_FailsWhenAnEntityIsMissing()
    {
        OffsetResult result = SketchOffset.Offset(Sketch.Empty, [Entity(1)], 1);

        result.Outcome.Should().Be(OffsetOutcome.EntityNotFound);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(double.NaN)]
    public void Offset_RefusesADistanceThatIsNotAMove(double distance)
    {
        Sketch sketch = Sketch.Empty.With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)));

        OffsetResult result = SketchOffset.Offset(sketch, [Entity(1)], distance);

        result.Outcome.Should().Be(OffsetOutcome.DoesNotFit);
    }

    /// <summary>An L: along +X to (10,0), then up to (10,10).</summary>
    private static Sketch Corner() => Sketch.Empty
        .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)))
        .With(new SketchLine(Entity(2), new Vec2d(10, 0), new Vec2d(10, 10)));

    /// <summary>A closed square from (0,0) to (10,10), walked anticlockwise.</summary>
    private static Sketch Square() => Sketch.Empty
        .With(new SketchLine(Entity(1), Vec2d.Zero, new Vec2d(10, 0)))
        .With(new SketchLine(Entity(2), new Vec2d(10, 0), new Vec2d(10, 10)))
        .With(new SketchLine(Entity(3), new Vec2d(10, 10), new Vec2d(0, 10)))
        .With(new SketchLine(Entity(4), new Vec2d(0, 10), Vec2d.Zero));

    /// <summary>Every endpoint of every piece an offset produced, for comparing two runs.</summary>
    private static ImmutableArray<double> Corners(OffsetResult result) =>
    [
        .. result.Created
            .Select(id => (SketchLine)result.Sketch!.Entities.Find(id)!)
            .SelectMany(l => new[] { l.Start, l.End })
            .SelectMany(p => new[] { p.X, p.Y })
            .Order(),
    ];

    private static void At(Vec2d actual, double x, double y, string because = "")
        => Vec2d.Distance(actual, new Vec2d(x, y)).Should().BeApproximately(0, Tol, because);

    private static ImmutableArray<SketchEntityId> Ids(params int[] ns) => [.. ns.Select(Entity)];

    private static SketchEntityId Entity(int n)
        => new(new Guid($"00000000-0000-0000-0000-{n:D12}"));
}
