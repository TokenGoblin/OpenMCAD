using FluentAssertions;

using OpenMCAD.Core.Assemblies;
using OpenMCAD.Math;
using OpenMCAD.Solver.Assemblies;
using OpenMCAD.Solver.Fake;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// Dragging a component with its mates solving live (P5-T06, §5.9).
/// </summary>
/// <remarks>
/// The three policies are the ones <c>DragSession</c> settled for sketches, plus one an assembly
/// forces: a drag must not solve the whole product. What is asserted below is mostly about which
/// question the solver is asked, because the answers themselves are
/// <c>FakeAssemblySolverTests</c>' business.
/// </remarks>
public sealed class AssemblyDragSessionTests
{
    private static readonly FakeAssemblySolver Solver = new();

    [Fact]
    public void DraggingSomethingThatIsNotThereIsRefusedAtMouseDown()
    {
        Chain scene = new();

        // Sixteen milliseconds later is a worse time to find out.
        FluentActions.Invoking(() => new AssemblyDragSession(
                Solver, scene.Assembly, OccurrencePath.Of(OccurrenceId.New()), Chain.ElementOf))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TheDraggedComponentEndsWhereItWasPut()
    {
        Chain scene = new();
        AssemblyDragSession drag = new(Solver, scene.Assembly, scene.Middle, Chain.ElementOf);

        Transform to = Transform.FromTranslation(new Vec3d(0.4, 0.1, 0));

        drag.MoveTo(to);
        drag.Solve();

        // The assembly follows the hand. A drag that corrected the held component would fight the
        // user for control of it.
        drag.Current.FindOccurrence(scene.Middle.Leaf)!.Placement.Should().Be(to);
    }

    [Fact]
    public void WhatIsMatedToItComesAlong()
    {
        Chain scene = new();
        AssemblyDragSession drag = new(Solver, scene.Assembly, scene.Middle, Chain.ElementOf);

        drag.MoveTo(Transform.FromTranslation(new Vec3d(0.4, 0.1, 0)));
        drag.Solve()!.IsSolved.Should().BeTrue();

        // The far component is mated to the middle one, so it has to follow it.
        drag.Current.FindOccurrence(scene.Far.Leaf)!.Placement
            .TransformPoint(Vec3d.Zero)
            .IsNear(new Vec3d(0.4, 0.1, 0), 1e-6).Should().BeTrue();
    }

    [Fact]
    public void AComponentNothingConnectsToIsNotEvenHandedToTheSolver()
    {
        Chain scene = new();
        AssemblyDragSession drag = new(Solver, scene.Assembly, scene.Middle, Chain.ElementOf);

        Transform before = scene.Assembly.FindOccurrence(scene.Stranger.Leaf)!.Placement;

        drag.MoveTo(Transform.FromTranslation(new Vec3d(0.4, 0.1, 0)));
        drag.Solve();

        // Not merely unchanged. It is never passed to the solver, so no amount of numerical
        // wandering can move it -- which is what makes a ten-thousand-component drag possible.
        drag.SolvedBodies.Should().Be(2);
        drag.Current.FindOccurrence(scene.Stranger.Leaf)!.Placement.Should().Be(before);
    }

    [Fact]
    public void MovementStopsAtAGroundedComponent()
    {
        Anchored scene = new();
        AssemblyDragSession drag = new(Solver, scene.Assembly, scene.Held, Chain.ElementOf);

        Transform before = scene.Assembly.FindOccurrence(scene.Beyond.Leaf)!.Placement;

        drag.MoveTo(Transform.FromTranslation(new Vec3d(0.3, 0, 0)));
        drag.Solve();

        // Held is mated to a fixed base, and the base to another component. The base cannot move,
        // so nothing on its far side can be disturbed by way of it -- but the base itself is in the
        // solve, because it is what the dragged part is positioned against.
        drag.SolvedBodies.Should().Be(2);
        drag.Current.FindOccurrence(scene.Beyond.Leaf)!.Placement.Should().Be(before);
    }

    // --- Coalescing and the baseline ---------------------------------------------------------------

    [Fact]
    public void OnlyTheLatestPositionIsSolved()
    {
        Chain scene = new();
        AssemblyDragSession drag = new(Solver, scene.Assembly, scene.Middle, Chain.ElementOf);

        drag.MoveTo(Transform.FromTranslation(new Vec3d(0.1, 0, 0)));
        drag.MoveTo(Transform.FromTranslation(new Vec3d(0.2, 0, 0)));

        Transform last = Transform.FromTranslation(new Vec3d(0.3, 0, 0));
        drag.MoveTo(last);

        drag.HasWork.Should().BeTrue();
        drag.Solve().Should().NotBeNull();

        // The two it skipped were going to be solved into a position the user had already left.
        drag.Skipped.Should().Be(2);
        drag.Current.FindOccurrence(scene.Middle.Leaf)!.Placement.Should().Be(last);
        drag.HasWork.Should().BeFalse();
    }

    [Fact]
    public void SolvingWithNothingWaitingDoesNothing()
    {
        Chain scene = new();
        AssemblyDragSession drag = new(Solver, scene.Assembly, scene.Middle, Chain.ElementOf);

        drag.Solve().Should().BeNull();
        drag.Current.Should().BeSameAs(scene.Assembly);
    }

    [Fact]
    public void EveryFrameIsSolvedFromWhereTheDragStarted()
    {
        Chain scene = new();
        AssemblyDragSession drag = new(Solver, scene.Assembly, scene.Middle, Chain.ElementOf);

        drag.MoveTo(Transform.FromTranslation(new Vec3d(0.4, 0, 0)));
        drag.Solve();

        Assembly afterDetour = drag.Current;

        drag.MoveTo(Transform.FromTranslation(new Vec3d(-0.4, 0.2, 0.1)));
        drag.Solve();

        drag.MoveTo(scene.MiddleStartedAt);
        drag.Solve();

        // Chaining frames lets components creep: each frame's compromise becomes the next frame's
        // start, and parts the user never touched wander over a few hundred milliseconds. Solving
        // from the baseline makes the drag reversible instead.
        afterDetour.Should().NotBeSameAs(scene.Assembly);

        drag.Current.FindOccurrence(scene.Far.Leaf)!.Placement
            .TransformPoint(Vec3d.Zero)
            .IsNear(scene.MiddleStartedAt.TransformPoint(Vec3d.Zero), 1e-6).Should().BeTrue();
    }

    [Fact]
    public void AFreeDirectionDoesNotDriftOverTheCourseOfADrag()
    {
        // The property the baseline actually protects, which the coincident chain above cannot
        // show: a point pinned to a point is fully determined by where the dragged part is, so it
        // returns to the same place whether frames chain or not. A *distance* mate leaves the
        // direction free, and that is what creeps -- each frame's compromise becoming the next
        // frame's start, until parts the user never touched have wandered.
        Tethered scene = new();
        AssemblyDragSession drag = new(Solver, scene.Assembly, scene.Middle, Chain.ElementOf);

        Transform started = scene.Assembly.FindOccurrence(scene.Tether.Leaf)!.Placement;

        drag.MoveTo(Transform.FromTranslation(new Vec3d(0.6, 0.4, 0.2)));
        drag.Solve();

        drag.MoveTo(Transform.FromTranslation(new Vec3d(-0.5, -0.3, 0.7)));
        drag.Solve();

        drag.MoveTo(scene.MiddleStartedAt);
        drag.Solve();

        // Back where it began, the mate already holds, so nothing needs to move at all.
        drag.Current.FindOccurrence(scene.Tether.Leaf)!.Placement.Should().Be(started);
    }

    [Fact]
    public void DraggingEitherEndOfAMateCarriesTheOther()
    {
        // The adjacency has to go both ways. Dragging the end a mate was written from worked
        // whichever direction was recorded, so only dragging the other end shows it.
        Chain scene = new();
        AssemblyDragSession drag = new(Solver, scene.Assembly, scene.Far, Chain.ElementOf);

        Transform to = Transform.FromTranslation(new Vec3d(0, 0.7, 0));

        drag.MoveTo(to);
        drag.Solve()!.IsSolved.Should().BeTrue();

        drag.SolvedBodies.Should().Be(2);

        drag.Current.FindOccurrence(scene.Middle.Leaf)!.Placement
            .TransformPoint(Vec3d.Zero).IsNear(new Vec3d(0, 0.7, 0), 1e-6).Should().BeTrue();
    }

    [Fact]
    public void CommittingSolvesWhateverIsStillWaiting()
    {
        Chain scene = new();
        AssemblyDragSession drag = new(Solver, scene.Assembly, scene.Middle, Chain.ElementOf);

        Transform last = Transform.FromTranslation(new Vec3d(0.25, 0, 0));
        drag.MoveTo(last);

        // Letting go must leave the assembly where the user last saw it heading, not one frame
        // behind.
        drag.Commit().FindOccurrence(scene.Middle.Leaf)!.Placement.Should().Be(last);
    }

    [Fact]
    public void CancellingPutsEverythingBack()
    {
        Chain scene = new();
        AssemblyDragSession drag = new(Solver, scene.Assembly, scene.Middle, Chain.ElementOf);

        drag.MoveTo(Transform.FromTranslation(new Vec3d(0.4, 0.3, 0)));
        drag.Solve();
        drag.MoveTo(Transform.FromTranslation(new Vec3d(0.9, 0, 0)));

        // Reference-identical, which is what an immutable assembly buys: the thing that was there,
        // not one rebuilt to look like it.
        drag.Cancel().Should().BeSameAs(scene.Assembly);

        // Current as well as the return value. Asserting only what Cancel hands back let the
        // session keep reporting the half-dragged assembly to anyone who asked afterwards.
        drag.Current.Should().BeSameAs(scene.Assembly);
        drag.HasWork.Should().BeFalse();
        drag.Last.Should().BeNull();
    }

    /// <summary>
    /// Three components in a line and one on its own: middle mated to far, stranger mated to
    /// nothing. Nothing is grounded, so a drag is the only thing holding anything still.
    /// </summary>
    private class Chain
    {
        public Chain()
        {
            ComponentDefinition part = new(ComponentDefinitionId.New(), "part.omcad", "Part");

            OccurrenceId middle = OccurrenceId.New();
            OccurrenceId far = OccurrenceId.New();
            OccurrenceId stranger = OccurrenceId.New();

            Middle = OccurrencePath.Of(middle);
            Far = OccurrencePath.Of(far);
            Stranger = OccurrencePath.Of(stranger);
            MiddleStartedAt = Transform.Identity;

            Assembly = Assembly.Empty
                .WithDefinition(part)
                .WithOccurrence(new ComponentOccurrence(middle, part.Id, MiddleStartedAt))
                .WithOccurrence(new ComponentOccurrence(
                    far, part.Id, Transform.FromTranslation(new Vec3d(0.5, 0, 0))))
                .WithOccurrence(new ComponentOccurrence(
                    stranger, part.Id, Transform.FromTranslation(new Vec3d(0, 0.9, 0))))
                .WithMate(Pin(Middle, Far));
        }

        public Assembly Assembly { get; protected init; }

        public OccurrencePath Middle { get; }

        public OccurrencePath Far { get; }

        public OccurrencePath Stranger { get; }

        public Transform MiddleStartedAt { get; }

        /// <summary>Stands in for opening the component and finding what the mate names.</summary>
        public static MateElement? ElementOf(MateEnd end)
        {
            ArgumentNullException.ThrowIfNull(end);

            MateElement? found = end.Element == "Origin" ? new MateElement.Point(Vec3d.Zero) : null;

            return found;
        }

        protected static MateDefinition Pin(OccurrencePath first, OccurrencePath second)
            => new(
                MateId.New(),
                MateKind.Coincident,
                new MateEnd(first, "Origin"),
                new MateEnd(second, "Origin"));
    }

    /// <summary>
    /// Two components held a distance apart, so the direction between them is free to drift.
    /// </summary>
    private sealed class Tethered : Chain
    {
        public Tethered()
        {
            ComponentDefinition part = new(ComponentDefinitionId.New(), "part.omcad", "Part");

            OccurrenceId middle = OccurrenceId.New();
            OccurrenceId tether = OccurrenceId.New();

            Middle = OccurrencePath.Of(middle);
            Tether = OccurrencePath.Of(tether);

            Assembly = Assembly.Empty
                .WithDefinition(part)
                .WithOccurrence(new ComponentOccurrence(middle, part.Id, MiddleStartedAt))
                .WithOccurrence(new ComponentOccurrence(
                    tether, part.Id, Transform.FromTranslation(new Vec3d(0.5, 0, 0))))
                .WithMate(new MateDefinition(
                    MateId.New(),
                    MateKind.Distance,
                    new MateEnd(Middle, "Origin"),
                    new MateEnd(Tether, "Origin"),
                    Value: 0.5));
        }

        public new OccurrencePath Middle { get; }

        public OccurrencePath Tether { get; }
    }

    /// <summary>
    /// Held, mated to a grounded base, which is mated to something beyond it.
    /// </summary>
    private sealed class Anchored : Chain
    {
        public Anchored()
        {
            ComponentDefinition part = new(ComponentDefinitionId.New(), "part.omcad", "Part");

            OccurrenceId held = OccurrenceId.New();
            OccurrenceId anchor = OccurrenceId.New();
            OccurrenceId beyond = OccurrenceId.New();

            Held = OccurrencePath.Of(held);
            Anchor = OccurrencePath.Of(anchor);
            Beyond = OccurrencePath.Of(beyond);

            Assembly = Assembly.Empty
                .WithDefinition(part)
                .WithOccurrence(new ComponentOccurrence(held, part.Id, Transform.Identity))
                .WithOccurrence(new ComponentOccurrence(
                    anchor, part.Id, Transform.Identity, IsGrounded: true))
                .WithOccurrence(new ComponentOccurrence(
                    beyond, part.Id, Transform.FromTranslation(new Vec3d(0, 0.5, 0))))
                .WithMate(Pin(Held, Anchor))
                .WithMate(Pin(Anchor, Beyond));
        }

        public OccurrencePath Held { get; }

        public OccurrencePath Anchor { get; }

        public OccurrencePath Beyond { get; }
    }
}
