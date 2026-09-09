using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Solver.Assemblies;
using OpenMCAD.Solver.Fake;

using Xunit;

namespace OpenMCAD.Solver.Fake.Tests;

/// <summary>
/// Placing an assembly's bodies so its mates hold (P5-T05).
/// </summary>
/// <remarks>
/// <para>
/// What is asserted is that <em>the mates hold</em>, not where a body ended up. An under-constrained
/// assembly has infinitely many right answers and the one this solver finds is a property of the
/// minimisation rather than of the problem — asserting a coordinate would be asserting the
/// implementation, and would have to be rewritten the first time the damping changed.
/// </para>
/// <para>
/// The residuals are the definition of what a mate means, so "the mate holds" is checked by asking
/// <see cref="MateResiduals"/> against the solved placements. That is not circular: the solver
/// drives residuals to zero through a Jacobian and a damped step, and a bug in any of that shows up
/// as residuals that are not zero.
/// </para>
/// </remarks>
public sealed class FakeAssemblySolverTests
{
    private static readonly FakeAssemblySolver Solver = new();

    [Fact]
    public void AFaceIsBroughtFlushAgainstAFixedOne()
    {
        MateBody ground = Grounded(out MateBodyId fixedBody, Transform.Identity);

        MateBody moving = Loose(
            out MateBodyId movingBody, Transform.FromTranslation(new Vec3d(0.05, 0.02, 0.3)));

        // The fixed body's face looks up; the moving body's looks up too, so satisfying the mate
        // means turning it over as well as bringing it down.
        AssemblyMate mate = Mate(
            MateKind.Coincident,
            fixedBody, new MateElement.Plane(Vec3d.Zero, Vec3d.UnitZ),
            movingBody, new MateElement.Plane(Vec3d.Zero, Vec3d.UnitZ));

        MateSolveResult result = Solver.Solve([ground, moving], [mate]);

        result.IsSolved.Should().BeTrue(result.Diagnosis.Message);
        Holds(mate, result).Should().BeTrue();
    }

    [Fact]
    public void AShaftIsBroughtIntoLineWithAHole()
    {
        MateBody ground = Grounded(out MateBodyId hole, Transform.Identity);

        MateBody shaft = Loose(
            out MateBodyId shaftBody,
            new Transform(
                Quatd.FromAxisAngle(new Vec3d(1, 1, 0), 0.4), new Vec3d(0.1, -0.05, 0.2), 1.0));

        AssemblyMate mate = Mate(
            MateKind.Concentric,
            hole, new MateElement.Axis(Vec3d.Zero, Vec3d.UnitZ),
            shaftBody, new MateElement.Axis(Vec3d.Zero, Vec3d.UnitZ));

        MateSolveResult result = Solver.Solve([ground, shaft], [mate]);

        result.IsSolved.Should().BeTrue(result.Diagnosis.Message);
        Holds(mate, result).Should().BeTrue();

        // Two degrees of freedom survive: sliding along the axis and spinning about it.
        result.Diagnosis.RemainingFreedom.Should().Be(2);
    }

    [Fact]
    public void ADistanceMateHoldsThePlanesApartByTheValueAsked()
    {
        MateBody ground = Grounded(out MateBodyId fixedBody, Transform.Identity);
        MateBody moving = Loose(out MateBodyId movingBody, Transform.Identity);

        AssemblyMate mate = Mate(
            MateKind.Distance,
            fixedBody, new MateElement.Plane(Vec3d.Zero, Vec3d.UnitZ),
            movingBody, new MateElement.Plane(Vec3d.Zero, Vec3d.UnitZ),
            value: 0.025);

        MateSolveResult result = Solver.Solve([ground, moving], [mate]);

        result.IsSolved.Should().BeTrue(result.Diagnosis.Message);

        // Asserted through the geometry rather than through the residual, so this would catch a
        // residual that was self-consistently wrong about which way the offset runs.
        Transform placed = result.Placements[movingBody];
        Vec3d origin = placed.TransformPoint(Vec3d.Zero);

        Vec3d.Dot(Vec3d.UnitZ, origin).Should().BeApproximately(0.025, 1e-6);
    }

    [Fact]
    public void APointIsPinnedToAPoint()
    {
        MateBody ground = Grounded(out MateBodyId fixedBody, Transform.Identity);

        MateBody moving = Loose(
            out MateBodyId movingBody, Transform.FromTranslation(new Vec3d(1, 1, 1)));

        AssemblyMate mate = Mate(
            MateKind.Coincident,
            fixedBody, new MateElement.Point(new Vec3d(0.01, 0.02, 0.03)),
            movingBody, new MateElement.Point(Vec3d.Zero));

        MateSolveResult result = Solver.Solve([ground, moving], [mate]);

        result.IsSolved.Should().BeTrue(result.Diagnosis.Message);

        result.Placements[movingBody].TransformPoint(Vec3d.Zero)
            .IsNear(new Vec3d(0.01, 0.02, 0.03), 1e-6).Should().BeTrue();

        // Every rotation survives: a point has no orientation to align.
        result.Diagnosis.RemainingFreedom.Should().Be(3);
    }

    [Fact]
    public void AChainOfMatesSolvesTogether()
    {
        // Three bodies, the middle one mated to both. Nothing here is solvable a body at a time.
        MateBody ground = Grounded(out MateBodyId baseBody, Transform.Identity);
        MateBody middle = Loose(out MateBodyId middleBody, Transform.FromTranslation(new Vec3d(0.2, 0, 0)));
        MateBody top = Loose(out MateBodyId topBody, Transform.FromTranslation(new Vec3d(0, 0.2, 0)));

        AssemblyMate lower = Mate(
            MateKind.Coincident,
            baseBody, new MateElement.Point(Vec3d.Zero),
            middleBody, new MateElement.Point(new Vec3d(0.05, 0, 0)));

        AssemblyMate upper = Mate(
            MateKind.Coincident,
            middleBody, new MateElement.Point(new Vec3d(0, 0.05, 0)),
            topBody, new MateElement.Point(Vec3d.Zero));

        MateSolveResult result = Solver.Solve([ground, middle, top], [lower, upper]);

        result.IsSolved.Should().BeTrue(result.Diagnosis.Message);
        Holds(lower, result).Should().BeTrue();
        Holds(upper, result).Should().BeTrue();
    }

    [Fact]
    public void AnAssemblyAlreadyInPlaceIsLeftWhereItIs()
    {
        MateBody ground = Grounded(out MateBodyId fixedBody, Transform.Identity);
        MateBody moving = Loose(out MateBodyId movingBody, Transform.Identity);

        AssemblyMate mate = Mate(
            MateKind.Coincident,
            fixedBody, new MateElement.Point(Vec3d.Zero),
            movingBody, new MateElement.Point(Vec3d.Zero));

        MateSolveResult result = Solver.Solve([ground, moving], [mate]);

        // A solve that moved a satisfied assembly would make every rebuild a change, and a document
        // that reports itself dirty after opening is one nobody can trust about anything else.
        result.Placements[movingBody].Translation.IsNear(Vec3d.Zero, 1e-9).Should().BeTrue();
        result.Iterations.Should().Be(0);
    }

    [Fact]
    public void AGroundedBodyIsNotMoved()
    {
        Transform where = new(Quatd.FromAxisAngle(Vec3d.UnitY, 0.3), new Vec3d(1, 2, 3), 1.0);
        MateBody ground = Grounded(out MateBodyId fixedBody, where);
        MateBody moving = Loose(out MateBodyId movingBody, Transform.Identity);

        MateSolveResult result = Solver.Solve(
            [ground, moving],
            [
                Mate(
                    MateKind.Coincident,
                    fixedBody, new MateElement.Point(Vec3d.Zero),
                    movingBody, new MateElement.Point(Vec3d.Zero)),
            ]);

        // §5.9's grounded components: the placement is an input, so the moving body has to come to
        // it rather than the two meeting in the middle.
        result.Placements[fixedBody].Should().Be(where);
    }

    [Fact]
    public void ABodyKeepsItsScaleThroughASolve()
    {
        // Mates say where a component sits, not how big it is. A solver that let scale drift would
        // resize parts to satisfy geometry, which is never what a mate meant.
        MateBody ground = Grounded(out MateBodyId fixedBody, Transform.Identity);

        MateBody moving = Loose(
            out MateBodyId movingBody,
            new Transform(Quatd.Identity, new Vec3d(0.4, 0, 0), 2.0));

        MateSolveResult result = Solver.Solve(
            [ground, moving],
            [
                Mate(
                    MateKind.Coincident,
                    fixedBody, new MateElement.Point(Vec3d.Zero),
                    movingBody, new MateElement.Point(Vec3d.Zero)),
            ]);

        result.Placements[movingBody].Scale.Should().Be(2.0);
    }

    [Fact]
    public void ASolvedPlacementIsARigidTransform()
    {
        // The quaternion is seven numbers holding three degrees of freedom, and nothing but the
        // normalisation residual and the read-back keeps it on the unit sphere. A placement built
        // from a long quaternion would scale everything it moves.
        MateBody ground = Grounded(out MateBodyId fixedBody, Transform.Identity);

        MateBody moving = Loose(
            out MateBodyId movingBody,
            new Transform(Quatd.FromAxisAngle(Vec3d.UnitX, 1.1), new Vec3d(0.3, 0.4, 0.5), 1.0));

        MateSolveResult result = Solver.Solve(
            [ground, moving],
            [
                Mate(
                    MateKind.Coincident,
                    fixedBody, new MateElement.Plane(Vec3d.Zero, Vec3d.UnitZ),
                    movingBody, new MateElement.Plane(Vec3d.Zero, Vec3d.UnitZ)),
            ]);

        result.Placements[movingBody].IsRigid.Should().BeTrue();
    }

    [Fact]
    public void AFlushMateLeavesTheFacesLookingAtEachOther()
    {
        // Found by sabotage: every other plane test checked the mate through MateResiduals, which
        // is the same function the solver minimises, so reversing the convention reversed both and
        // nothing noticed. "Flush" means the normals oppose, and that has to be asserted against
        // the geometry rather than against the residual.
        MateBody ground = Grounded(out MateBodyId fixedBody, Transform.Identity);
        MateBody moving = Loose(out MateBodyId movingBody, Transform.FromTranslation(new Vec3d(0, 0, 0.2)));

        MateSolveResult result = Solver.Solve(
            [ground, moving],
            [
                Mate(
                    MateKind.Coincident,
                    fixedBody, new MateElement.Plane(Vec3d.Zero, Vec3d.UnitZ),
                    movingBody, new MateElement.Plane(Vec3d.Zero, Vec3d.UnitZ)),
            ]);

        result.IsSolved.Should().BeTrue(result.Diagnosis.Message);

        Vec3d normal = result.Placements[movingBody].TransformNormal(Vec3d.UnitZ);

        normal.IsNear(-Vec3d.UnitZ, 1e-6).Should().BeTrue("two faces brought together face opposite ways");
    }

    [Fact]
    public void AFlippedMateLeavesTheFacesLookingTheSameWay()
    {
        // The other half, and the reason the flag exists: a user should not have to rebuild a
        // component the other way up to mate it the way they meant.
        MateBody ground = Grounded(out MateBodyId fixedBody, Transform.Identity);
        MateBody moving = Loose(out MateBodyId movingBody, Transform.FromTranslation(new Vec3d(0, 0, 0.2)));

        AssemblyMate flipped = new(
            MateId.New(),
            MateKind.Coincident,
            new MateAttachment(fixedBody, new MateElement.Plane(Vec3d.Zero, Vec3d.UnitZ)),
            new MateAttachment(movingBody, new MateElement.Plane(Vec3d.Zero, Vec3d.UnitZ)),
            IsFlipped: true);

        MateSolveResult result = Solver.Solve([ground, moving], [flipped]);

        result.IsSolved.Should().BeTrue(result.Diagnosis.Message);

        result.Placements[movingBody].TransformNormal(Vec3d.UnitZ)
            .IsNear(Vec3d.UnitZ, 1e-6).Should().BeTrue();
    }

    [Fact]
    public void APlacementGivenWithALongQuaternionComesBackRigid()
    {
        // The read-back normalisation, which nothing else reaches: every path inside the solve
        // already keeps the quaternion on the unit sphere, so the only way to hand it a long one
        // is to pass it in, and a caller building a Transform by hand can.
        //
        // Asserted on the quaternion rather than through Transform.IsRigid, which was the first
        // attempt and proved nothing: IsRigid checks only that Scale is one and says nothing about
        // the rotation's length, so it calls a transform rigid that is not. Quatd.Rotate documents
        // that it assumes unit length, so a long quaternion silently scales everything it moves.
        //
        // A mate is present because without one the solve returns early and never reads a placement
        // back at all -- which is how the first version of this test passed either way.
        MateBody ground = Grounded(out MateBodyId fixedBody, Transform.Identity);

        MateBody moving = new(
            MateBodyId.New(),
            new Transform(new Quatd(0, 0, 0, 3), new Vec3d(0.2, 0, 0), 1.0));

        MateSolveResult result = Solver.Solve(
            [ground, moving], [Pin(fixedBody, moving.Id, Vec3d.Zero)]);

        result.Placements[moving.Id].Rotation.LengthSquared.Should().BeApproximately(1, 1e-9);
    }

    [Fact]
    public void AnUnderDeterminedSolveIsTheSameWhicheverOrderTheBodiesArriveIn()
    {
        // Stronger than the well-determined case, which converges to the one right answer whatever
        // the column order. Here two free bodies are pinned only to each other, so there are
        // infinitely many answers and which one comes out depends on the order the unknowns sit in
        // -- which is why that order is taken from the body ids and not from the caller's list.
        MateBody a = Loose(out MateBodyId first, Transform.FromTranslation(new Vec3d(0.3, 0.1, 0)));
        MateBody b = Loose(out MateBodyId second, Transform.FromTranslation(new Vec3d(-0.1, 0.2, 0.4)));

        AssemblyMate[] mates = [Pin(first, second, Vec3d.Zero)];

        MateSolveResult once = Solver.Solve([a, b], mates);
        MateSolveResult again = Solver.Solve([b, a], mates);

        once.Diagnosis.Outcome.Should().Be(MateSolveOutcome.UnderConstrained);
        once.Placements[first].IsNear(again.Placements[first], 1e-9).Should().BeTrue();
        once.Placements[second].IsNear(again.Placements[second], 1e-9).Should().BeTrue();
    }

    // --- Diagnosis --------------------------------------------------------------------------------

    [Fact]
    public void AnUnmatedBodyHasSixDegreesOfFreedom()
    {
        MateBody only = Loose(out MateBodyId body, Transform.Identity);

        MateSolveResult result = Solver.Solve([only], []);

        result.Diagnosis.Outcome.Should().Be(MateSolveOutcome.UnderConstrained);
        result.Diagnosis.RemainingFreedom.Should().Be(6);
        result.Diagnosis.Free.Should().Equal(body);
    }

    [Fact]
    public void AFullyMatedBodyIsWellConstrained()
    {
        // Three point-to-point mates at three places that are not collinear pin a body completely:
        // nine numbers for six degrees of freedom, and the rank is what tells the difference.
        MateBody ground = Grounded(out MateBodyId fixedBody, Transform.Identity);
        MateBody moving = Loose(out MateBodyId movingBody, Transform.FromTranslation(new Vec3d(0.01, 0, 0)));

        MateSolveResult result = Solver.Solve(
            [ground, moving],
            [
                Pin(fixedBody, movingBody, Vec3d.Zero),
                Pin(fixedBody, movingBody, new Vec3d(0.1, 0, 0)),
                Pin(fixedBody, movingBody, new Vec3d(0, 0.1, 0)),
            ]);

        result.IsSolved.Should().BeTrue(result.Diagnosis.Message);
        result.Diagnosis.RemainingFreedom.Should().Be(0);
        result.Diagnosis.Outcome.Should().Be(MateSolveOutcome.WellConstrained);
        result.Diagnosis.IsFullyConstrained.Should().BeTrue();
    }

    [Fact]
    public void AMateThatSaysWhatAnotherAlreadySaidIsReportedAsRedundant()
    {
        // The thing MateAnalysis.Freedom explicitly cannot tell: the same mate twice removes
        // freedom once, and only the rank of the Jacobian knows it.
        MateBody ground = Grounded(out MateBodyId fixedBody, Transform.Identity);
        MateBody moving = Loose(out MateBodyId movingBody, Transform.Identity);

        AssemblyMate first = Pin(fixedBody, movingBody, Vec3d.Zero);
        AssemblyMate second = Pin(fixedBody, movingBody, Vec3d.Zero);

        MateSolveResult result = Solver.Solve(
            [ground, moving],
            [
                first,
                second,
                Pin(fixedBody, movingBody, new Vec3d(0.1, 0, 0)),
                Pin(fixedBody, movingBody, new Vec3d(0, 0.1, 0)),
            ]);

        result.IsSolved.Should().BeTrue(result.Diagnosis.Message);
        result.Diagnosis.Outcome.Should().Be(MateSolveOutcome.Redundant);
        result.Diagnosis.IsFullyConstrained.Should().BeTrue("a redundant mate removes no freedom");
        result.Diagnosis.SayingNothingNew.Should().NotBeEmpty();
    }

    [Fact]
    public void MatesThatContradictEachOtherAreReportedWithTheOnesInConflict()
    {
        // One point cannot be in two places. §5.6 is blunt that a diagnosis without a list leaves
        // the user deleting mates at random, so the set is the point of the answer.
        MateBody ground = Grounded(out MateBodyId fixedBody, Transform.Identity);
        MateBody moving = Loose(out MateBodyId movingBody, Transform.Identity);

        MateSolveResult result = Solver.Solve(
            [ground, moving],
            [
                Mate(
                    MateKind.Distance,
                    fixedBody, new MateElement.Point(Vec3d.Zero),
                    movingBody, new MateElement.Point(Vec3d.Zero),
                    value: 0.1),
                Mate(
                    MateKind.Distance,
                    fixedBody, new MateElement.Point(Vec3d.Zero),
                    movingBody, new MateElement.Point(Vec3d.Zero),
                    value: 0.2),
            ]);

        result.IsSolved.Should().BeFalse();
        result.Diagnosis.Outcome.Should().Be(MateSolveOutcome.OverConstrained);
        result.Diagnosis.InConflict.Should().NotBeEmpty();
    }

    [Fact]
    public void AMateThisBuildCannotSolveIsNamedRatherThanIgnored()
    {
        MateBody ground = Grounded(out MateBodyId fixedBody, Transform.Identity);
        MateBody moving = Loose(out MateBodyId movingBody, Transform.Identity);

        AssemblyMate nonsense = Mate(
            MateKind.Concentric,
            fixedBody, new MateElement.Point(Vec3d.Zero),
            movingBody, new MateElement.Point(Vec3d.Zero));

        MateSolveResult result = Solver.Solve([ground, moving], [nonsense]);

        result.Diagnosis.Unsolvable.Should().ContainSingle()
            .Which.Mate.Should().Be(nonsense.Id);

        // It constrains nothing, so what is left is everything.
        result.Diagnosis.RemainingFreedom.Should().Be(6);
    }

    [Fact]
    public void ASolveIsTheSameEveryTime()
    {
        // ADR-0011. The column order comes from the body ids rather than from a dictionary, so two
        // runs that list the bodies differently must still converge to the same placement.
        MateBody ground = Grounded(out MateBodyId fixedBody, Transform.Identity);
        MateBody a = Loose(out MateBodyId first, Transform.FromTranslation(new Vec3d(0.1, 0.2, 0.3)));
        MateBody b = Loose(out MateBodyId second, Transform.FromTranslation(new Vec3d(-0.2, 0.1, 0)));

        AssemblyMate[] mates =
        [
            Pin(fixedBody, first, Vec3d.Zero),
            Pin(first, second, new Vec3d(0.05, 0, 0)),
        ];

        MateSolveResult once = Solver.Solve([ground, a, b], mates);
        MateSolveResult again = Solver.Solve([b, a, ground], mates);

        once.Placements[first].IsNear(again.Placements[first], 1e-9).Should().BeTrue();
        once.Placements[second].IsNear(again.Placements[second], 1e-9).Should().BeTrue();
    }

    [Fact]
    public void ASolveCanBeAbandoned()
    {
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        MateBody ground = Grounded(out MateBodyId fixedBody, Transform.Identity);
        MateBody moving = Loose(out MateBodyId movingBody, Transform.FromTranslation(new Vec3d(9, 9, 9)));

        FluentActions.Invoking(() => Solver.Solve(
                [ground, moving],
                [Pin(fixedBody, movingBody, Vec3d.Zero)],
                cancellationToken: cancelled.Token))
            .Should().Throw<OperationCanceledException>();
    }

    // --- Helpers ----------------------------------------------------------------------------------

    /// <summary>Whether a mate actually holds at the placements a solve produced.</summary>
    private static bool Holds(AssemblyMate mate, MateSolveResult result)
        => MateResiduals
            .Of(mate, result.Placements[mate.First.Body], result.Placements[mate.Second.Body])
            .All(r => System.Math.Abs(r) <= 1e-6);

    private static MateBody Loose(out MateBodyId id, Transform placement)
    {
        id = MateBodyId.New();
        return new MateBody(id, placement);
    }

    private static MateBody Grounded(out MateBodyId id, Transform placement)
    {
        id = MateBodyId.New();
        return new MateBody(id, placement, IsGrounded: true);
    }

    private static AssemblyMate Pin(MateBodyId first, MateBodyId second, Vec3d at)
        => Mate(
            MateKind.Coincident,
            first, new MateElement.Point(at),
            second, new MateElement.Point(at));

    private static AssemblyMate Mate(
        MateKind kind,
        MateBodyId first,
        MateElement firstElement,
        MateBodyId second,
        MateElement secondElement,
        double value = 0)
        => new(
            MateId.New(),
            kind,
            new MateAttachment(first, firstElement),
            new MateAttachment(second, secondElement),
            value);
}
