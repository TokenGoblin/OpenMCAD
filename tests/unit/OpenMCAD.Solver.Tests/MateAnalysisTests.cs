using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Solver.Assemblies;

using Xunit;

namespace OpenMCAD.Solver.Tests;

/// <summary>
/// The mate model and what can be said about an assembly's freedom without solving it (P5-T05).
/// </summary>
/// <remarks>
/// Two things are asserted below and they are different in kind. The pairing table is a set of
/// decisions — how much freedom each mate removes, and which combinations are refused — and the
/// tests state those decisions so that changing one is a deliberate act. The freedom count is
/// arithmetic over that table, and what matters about it is that it is honest at the edges: a lower
/// bound that says so, rather than a number that is usually right.
/// </remarks>
public sealed class MateAnalysisTests
{
    [Fact]
    public void TwoLooseBodiesHaveTwelveDegreesOfFreedom()
    {
        MateFreedom freedom = MateAnalysis.Freedom(
            [Loose(out MateBodyId first), Loose(out MateBodyId second)], []);

        freedom.AtLeast.Should().Be(12);
        freedom.Total.Should().Be(12);
        freedom.MovableBodies.Should().BeEquivalentTo(ImmutableArray.Create(first, second));
    }

    [Fact]
    public void AGroundedBodyContributesNoFreedom()
    {
        // §5.9 asks for grounded components, and this is what grounding means to a solve: the
        // placement is an input rather than an unknown.
        MateFreedom freedom = MateAnalysis.Freedom(
            [Grounded(out MateBodyId _), Loose(out MateBodyId free)], []);

        freedom.Total.Should().Be(6);
        freedom.MovableBodies.Should().Equal(free);
    }

    [Fact]
    public void AFlushFaceMateLeavesTwoSlidesAndASpin()
    {
        MateBody ground = Grounded(out MateBodyId fixedBody);
        MateBody moving = Loose(out MateBodyId movingBody);

        MateFreedom freedom = MateAnalysis.Freedom(
            [ground, moving],
            [Mate(MateKind.Coincident, fixedBody, SomePlane, movingBody, SomePlane)]);

        // Six, less the three a planar mate removes. What is left is the two directions the face
        // can slide in and the rotation about its normal.
        freedom.AtLeast.Should().Be(3);
    }

    [Fact]
    public void AShaftInAHoleLeavesASlideAndASpin()
    {
        MateBody ground = Grounded(out MateBodyId fixedBody);
        MateBody moving = Loose(out MateBodyId movingBody);

        MateFreedom freedom = MateAnalysis.Freedom(
            [ground, moving],
            [Mate(MateKind.Concentric, fixedBody, SomeAxis, movingBody, SomeAxis)]);

        freedom.AtLeast.Should().Be(2);
    }

    [Fact]
    public void APointToPointMateLeavesEveryRotation()
    {
        MateBody ground = Grounded(out MateBodyId fixedBody);
        MateBody moving = Loose(out MateBodyId movingBody);

        MateFreedom freedom = MateAnalysis.Freedom(
            [ground, moving],
            [Mate(MateKind.Coincident, fixedBody, SomePoint, movingBody, SomePoint)]);

        // Three and not six: a point has no orientation to align, so pinning two together says
        // nothing about which way either body faces.
        freedom.AtLeast.Should().Be(3);
    }

    [Fact]
    public void ADistanceBetweenPointsRemovesOnlyTheLength()
    {
        MateBody ground = Grounded(out MateBodyId fixedBody);
        MateBody moving = Loose(out MateBodyId movingBody);

        MateFreedom freedom = MateAnalysis.Freedom(
            [ground, moving],
            [Mate(MateKind.Distance, fixedBody, SomePoint, movingBody, SomePoint, value: 10)]);

        // A sphere of positions rather than a point, so one degree of freedom goes and five stay.
        freedom.AtLeast.Should().Be(5);
    }

    [Fact]
    public void FreedomIsALowerBoundAndSaysSoWhenMatesRepeatThemselves()
    {
        MateBody ground = Grounded(out MateBodyId fixedBody);
        MateBody moving = Loose(out MateBodyId movingBody);

        // The same thing said three times. Nine removed from six, which is arithmetic rather than
        // a verdict: the honest reading is that a solve is needed, not that the assembly is broken.
        MateFreedom freedom = MateAnalysis.Freedom(
            [ground, moving],
            [
                Mate(MateKind.Coincident, fixedBody, SomePlane, movingBody, SomePlane),
                Mate(MateKind.Coincident, fixedBody, SomePlane, movingBody, SomePlane),
                Mate(MateKind.Coincident, fixedBody, SomePlane, movingBody, SomePlane),
            ]);

        freedom.Removed.Should().Be(9);
        freedom.Total.Should().Be(6);
        freedom.AtLeast.Should().Be(0, "the count floors rather than going negative");
        freedom.MayBeOverConstrained.Should().BeTrue();
    }

    [Fact]
    public void RemovingSixDegreesIsNotTheSameAsBeingFullyConstrained()
    {
        // The distinction the naming exists to keep. A status bar reading "0 degrees of freedom" on
        // an assembly that can still be dragged is worse than one reading "at least 0", because the
        // first is a claim a user will act on.
        MateBody ground = Grounded(out MateBodyId fixedBody);
        MateBody moving = Loose(out MateBodyId movingBody);

        MateFreedom freedom = MateAnalysis.Freedom(
            [ground, moving],
            [
                Mate(MateKind.Coincident, fixedBody, SomePlane, movingBody, SomePlane),
                Mate(MateKind.Coincident, fixedBody, SomePlane, movingBody, SomePlane),
            ]);

        freedom.AtLeast.Should().Be(0);
        freedom.CouldBeFullyConstrained.Should().BeTrue("which is a necessary condition, not proof");
    }

    [Fact]
    public void AMateBetweenTwoGroundedBodiesRemovesNothing()
    {
        // Otherwise an assembly of fixed components would report as over-constrained the moment
        // anyone mated two of them, which is a message about nothing.
        MateFreedom freedom = MateAnalysis.Freedom(
            [Grounded(out MateBodyId first), Grounded(out MateBodyId second)],
            [Mate(MateKind.Coincident, first, SomePlane, second, SomePlane)]);

        freedom.Removed.Should().Be(0);
        freedom.MayBeOverConstrained.Should().BeFalse();
    }

    [Fact]
    public void AMateFromABodyToItselfRemovesNothing()
    {
        MateBody only = Loose(out MateBodyId body);

        MateFreedom freedom = MateAnalysis.Freedom(
            [only], [Mate(MateKind.Coincident, body, SomePlane, body, SomePlane)]);

        // It is a statement about that body's own geometry and constrains nothing a solve can move.
        freedom.AtLeast.Should().Be(6);
        Mate(MateKind.Coincident, body, SomePlane, body, SomePlane).Bodies.Should().ContainSingle();
    }

    // --- What the table refuses ------------------------------------------------------------------

    [Fact]
    public void ConcentricBetweenTwoPointsIsRefusedRatherThanCountedAsNothing()
    {
        MateBody ground = Grounded(out MateBodyId fixedBody);
        MateBody moving = Loose(out MateBodyId movingBody);

        AssemblyMate nonsense =
            Mate(MateKind.Concentric, fixedBody, SomePoint, movingBody, SomePoint);

        MateFreedom freedom = MateAnalysis.Freedom([ground, moving], [nonsense]);

        // Reported rather than silently removing zero, because the two are different things to
        // tell a user: "this mate does nothing" is a mistake to fix, not a state to live with.
        freedom.Unsupported.Should().ContainSingle().Which.Mate.Should().Be(nonsense.Id);
        freedom.AtLeast.Should().Be(6);
    }

    [Fact]
    public void ADistanceToAnAxisIsRefusedAsAmbiguous()
    {
        MateBody ground = Grounded(out MateBodyId fixedBody);
        MateBody moving = Loose(out MateBodyId movingBody);

        MatePairing pairing = MatePairing.For(
            Mate(MateKind.Distance, fixedBody, SomeAxis, movingBody, SomePlane, value: 5));

        pairing.IsSupported.Should().BeFalse();
        pairing.Reason.Should().Contain("ambiguous");
    }

    [Theory]
    [InlineData(MateKind.Coincident, 3)]
    [InlineData(MateKind.Distance, 3)]
    public void PlaneToPlaneRemovesTheSameFreedomWhetherOrNotItIsOffset(MateKind kind, int removed)
    {
        // A distance mate is a coincident mate with an offset, so the freedoms that survive are the
        // same ones. Stating it here means changing one and not the other has to be deliberate.
        MatePairing pairing = MatePairing.For(
            Mate(kind, MateBodyId.New(), SomePlane, MateBodyId.New(), SomePlane, value: 4));

        pairing.IsSupported.Should().BeTrue();
        pairing.RemovedFreedom.Should().Be(removed);
    }

    [Fact]
    public void EveryPairingIsEitherSupportedOrGivesAReason()
    {
        // A pairing that was refused with nothing to say would reach the user as a mate that does
        // not work and does not explain itself, which §5.6 is explicit is worse than useless.
        MateElement[] shapes = [SomePlane, SomeAxis, SomePoint];

        foreach (MateKind kind in Enum.GetValues<MateKind>())
        {
            foreach (MateElement first in shapes)
            {
                foreach (MateElement second in shapes)
                {
                    MatePairing pairing = MatePairing.For(
                        Mate(kind, MateBodyId.New(), first, MateBodyId.New(), second));

                    if (pairing.IsSupported)
                    {
                        pairing.RemovedFreedom.Should().BeInRange(1, 6);
                        pairing.Reason.Should().BeNull();
                    }
                    else
                    {
                        pairing.Reason.Should().NotBeNullOrWhiteSpace();
                        pairing.RemovedFreedom.Should().Be(0);
                    }
                }
            }
        }
    }

    // --- Grouping ---------------------------------------------------------------------------------

    [Fact]
    public void BodiesJoinedByMatesAreSolvedTogether()
    {
        MateBody a = Loose(out MateBodyId first);
        MateBody b = Loose(out MateBodyId second);
        MateBody c = Loose(out MateBodyId third);

        ImmutableArray<ImmutableArray<MateBodyId>> groups = MateAnalysis.Groups(
            [a, b, c],
            [
                Mate(MateKind.Coincident, first, SomePlane, second, SomePlane),
                Mate(MateKind.Concentric, second, SomeAxis, third, SomeAxis),
            ]);

        groups.Should().ContainSingle();
        groups[0].Should().BeEquivalentTo(ImmutableArray.Create(first, second, third));
    }

    [Fact]
    public void BodiesThatShareNoMateAreSolvedApart()
    {
        MateBody a = Loose(out MateBodyId first);
        MateBody b = Loose(out MateBodyId second);
        MateBody c = Loose(out MateBodyId third);

        // The payoff: dragging one of these should not re-solve the others.
        ImmutableArray<ImmutableArray<MateBodyId>> groups = MateAnalysis.Groups(
            [a, b, c], [Mate(MateKind.Coincident, first, SomePlane, second, SomePlane)]);

        groups.Should().HaveCount(2);
        groups.Should().ContainSingle(g => g.Length == 2);
        groups.Should().ContainSingle(g => g.Length == 1 && g[0] == third);
    }

    [Fact]
    public void TwoBodiesMatedToTheSameGroundedBaseAreNotTherebyJoined()
    {
        MateBody ground = Grounded(out MateBodyId baseBody);
        MateBody a = Loose(out MateBodyId first);
        MateBody b = Loose(out MateBodyId second);

        ImmutableArray<ImmutableArray<MateBodyId>> groups = MateAnalysis.Groups(
            [ground, a, b],
            [
                Mate(MateKind.Coincident, baseBody, SomePlane, first, SomePlane),
                Mate(MateKind.Coincident, baseBody, SomePlane, second, SomePlane),
            ]);

        // Treating the base as a connection would merge every group in the assembly into one and
        // give back the whole-product solve the decomposition exists to avoid.
        groups.Should().HaveCount(2);
        groups.Should().NotContain(g => g.Length == 2);
    }

    [Fact]
    public void AGroundedBodyIsInNoGroup()
    {
        ImmutableArray<ImmutableArray<MateBodyId>> groups =
            MateAnalysis.Groups([Grounded(out MateBodyId _)], []);

        groups.Should().BeEmpty();
    }

    [Fact]
    public void AnUnsupportedMateJoinsNothing()
    {
        MateBody a = Loose(out MateBodyId first);
        MateBody b = Loose(out MateBodyId second);

        // It cannot be solved, so it cannot be a reason to solve two bodies together.
        ImmutableArray<ImmutableArray<MateBodyId>> groups = MateAnalysis.Groups(
            [a, b], [Mate(MateKind.Concentric, first, SomePoint, second, SomePoint)]);

        groups.Should().HaveCount(2);
    }

    [Fact]
    public void TheDecompositionIsTheSameEveryTime()
    {
        // ADR-0011. A decomposition that depended on dictionary order would solve differently on
        // two machines, and the corpus would disagree with itself.
        MateBody a = Loose(out MateBodyId first);
        MateBody b = Loose(out MateBodyId second);
        MateBody c = Loose(out MateBodyId third);

        AssemblyMate[] mates = [Mate(MateKind.Coincident, first, SomePlane, third, SomePlane)];

        ImmutableArray<ImmutableArray<MateBodyId>> once = MateAnalysis.Groups([a, b, c], mates);
        ImmutableArray<ImmutableArray<MateBodyId>> again = MateAnalysis.Groups([c, b, a], mates);

        once.Select(g => g.ToArray()).Should().BeEquivalentTo(
            again.Select(g => g.ToArray()), options => options.WithStrictOrdering());
    }

    // --- Placing an element ------------------------------------------------------------------------

    [Fact]
    public void PlacingAnElementMovesItsPointAndTurnsItsDirection()
    {
        MateElement.Plane local = new(new Vec3d(0, 0, 1), Vec3d.UnitZ);

        Transform placement = new(
            Quatd.FromAxisAngle(Vec3d.UnitX, System.Math.PI / 2), new Vec3d(10, 0, 0), 1.0);

        MateElement.Plane placed = (MateElement.Plane)local.PlacedBy(placement);

        // A quarter turn about +X carries +Z onto -Y, and the origin rides along with the body.
        placed.Normal.IsNear(-Vec3d.UnitY).Should().BeTrue();
        placed.Origin.IsNear(new Vec3d(10, -1, 0)).Should().BeTrue();
    }

    [Fact]
    public void ScalingABodyDoesNotScaleItsNormals()
    {
        // The residuals compare directions by angle. A scaled normal is still the same direction,
        // and one that came back longer would make a comparison against it wrong by the scale.
        MateElement.Plane local = new(new Vec3d(1, 0, 0), Vec3d.UnitZ);
        Transform doubled = new(Quatd.Identity, Vec3d.Zero, 2.0);

        MateElement.Plane placed = (MateElement.Plane)local.PlacedBy(doubled);

        placed.Normal.Length.Should().BeApproximately(1, 1e-12);
        placed.Origin.Should().Be(new Vec3d(2, 0, 0), "the point does scale");
    }

    [Fact]
    public void EveryShapeOfElementIsCarriedByItsBody()
    {
        // Found by sabotage: the placement tests above both used a plane, so breaking the point or
        // the axis case failed nothing. Every shape has to ride along, because a mate compares
        // elements in the world and an element left behind at the origin would be satisfied by
        // putting the body there.
        Transform placement = new(
            Quatd.FromAxisAngle(Vec3d.UnitZ, System.Math.PI / 2), new Vec3d(0, 0, 5), 1.0);

        MateElement.Point point =
            (MateElement.Point)new MateElement.Point(new Vec3d(2, 0, 0)).PlacedBy(placement);

        MateElement.Axis axis =
            (MateElement.Axis)new MateElement.Axis(new Vec3d(2, 0, 0), Vec3d.UnitX)
                .PlacedBy(placement);

        // A quarter turn about +Z carries +X onto +Y, and both origins rise with the body.
        point.Position.IsNear(new Vec3d(0, 2, 5)).Should().BeTrue();
        axis.Origin.IsNear(new Vec3d(0, 2, 5)).Should().BeTrue();
        axis.Direction.IsNear(Vec3d.UnitY).Should().BeTrue();
    }

    [Fact]
    public void AMateBetweenTwoFixedBodiesIsNotCountedEvenThoughItJoinsTwo()
    {
        // The other half of the same guard as the self-mate test, and separate from it: one half
        // is about a mate naming one body, this one about a mate naming two that cannot move.
        MateFreedom freedom = MateAnalysis.Freedom(
            [Grounded(out MateBodyId first), Grounded(out MateBodyId second), Loose(out _)],
            [Mate(MateKind.Concentric, first, SomeAxis, second, SomeAxis)]);

        freedom.Total.Should().Be(6, "one body is loose");
        freedom.Removed.Should().Be(0);
        freedom.AtLeast.Should().Be(6);
    }

    // --- Helpers -----------------------------------------------------------------------------------

    private static MateElement SomePlane => new MateElement.Plane(Vec3d.Zero, Vec3d.UnitZ);

    private static MateElement SomeAxis => new MateElement.Axis(Vec3d.Zero, Vec3d.UnitX);

    private static MateElement SomePoint => new MateElement.Point(Vec3d.Zero);

    private static MateBody Loose(out MateBodyId id)
    {
        id = MateBodyId.New();
        return new MateBody(id, Transform.Identity);
    }

    private static MateBody Grounded(out MateBodyId id)
    {
        id = MateBodyId.New();
        return new MateBody(id, Transform.Identity, IsGrounded: true);
    }

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
