using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Core.Assemblies;
using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Serialization;
using OpenMCAD.Math;
using OpenMCAD.Solver.Assemblies;
using OpenMCAD.Solver.Fake;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// Mates as a document holds them, and the crossing into the solver's vocabulary (P5-T06).
/// </summary>
/// <remarks>
/// This is the only place the two halves of an assembly meet. Above it there are components,
/// occurrences and names; below it there are rigid bodies and residuals, and <c>OpenMCAD.Solver</c>
/// has never heard of a document. What is checked here is that the crossing keeps its promises in
/// both directions — that a solve is asked the question the document meant, and that the answer
/// lands back on the right instances.
/// </remarks>
public sealed class MateSystemTests
{
    private static readonly FakeAssemblySolver Solver = new();

    [Fact]
    public void AMateIsRefusedWhenItNamesAPlacementTheAssemblyDoesNotHave()
    {
        Scene scene = new();

        MateDefinition stray = Coincident(
            OccurrencePath.Of(OccurrenceId.New()), "Front", scene.BoltAt, "Front");

        // The invariant everything that reads a mate is written against.
        FluentActions.Invoking(() => scene.Assembly.WithMate(stray))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DeletingAPlacementDeletesTheMatesThatNamedIt()
    {
        Scene scene = new();
        Assembly mated = scene.Assembly.WithMate(scene.Mate);

        Assembly without = mated.WithoutOccurrence(scene.BoltAt.Leaf);

        // A mate to something that is gone cannot be solved and cannot be repaired -- there is
        // nothing to re-point it at -- so leaving it would leave a permanent error behind.
        without.Mates.Should().BeEmpty();
        without.Occurrences.Should().ContainSingle();
    }

    [Fact]
    public void AMateJoiningAPlacementToItselfIsReportedAsAProblem()
    {
        Scene scene = new();

        Assembly odd = scene.Assembly.WithMate(
            Coincident(scene.BoltAt, "Front", scene.BoltAt, "Top"));

        odd.Problems().Should().ContainSingle().Which.Should().Contain("itself");
    }

    // --- Crossing into the solver ------------------------------------------------------------------

    [Fact]
    public void EachActivePlacementBecomesOneBody()
    {
        Scene scene = new();
        MateSystem system = MateSystem.For(scene.Assembly.WithMate(scene.Mate), Scene.ElementOf);

        system.Bodies.Should().HaveCount(2);
        system.Mates.Should().ContainSingle();
        system.Unresolved.Should().BeEmpty();

        // The path is what names an instance, which is the whole reason it exists: the same
        // component placed twice is two bodies, and an id would name both.
        system.Instances.Values.Should().BeEquivalentTo(
            ImmutableArray.Create(scene.BaseAt, scene.BoltAt));
    }

    [Fact]
    public void AGroundedPlacementIsGroundedInTheSolve()
    {
        Scene scene = new();
        MateSystem system = MateSystem.For(scene.Assembly.WithMate(scene.Mate), Scene.ElementOf);

        MateBodyId baseBody = system.Instances.First(pair => pair.Value.Equals(scene.BaseAt)).Key;

        system.Bodies.Single(b => b.Id == baseBody).IsGrounded.Should().BeTrue();
    }

    [Fact]
    public void ASuppressedPlacementIsNotInTheSolveAndNorAreItsMates()
    {
        Scene scene = new();

        Assembly mated = scene.Assembly.WithMate(scene.Mate);
        ComponentOccurrence bolt = mated.FindOccurrence(scene.BoltAt.Leaf)!;
        Assembly without = mated.ReplaceOccurrence(bolt with { IsSuppressed = true });

        MateSystem system = MateSystem.For(without, Scene.ElementOf);

        // A suppressed component is not in the product, so a mate to it has nothing to hold.
        // Handing the solver a body that is not there would let it go on constraining the ones
        // that are.
        system.Bodies.Should().ContainSingle();
        system.Mates.Should().BeEmpty();

        UnresolvedMate why = system.Unresolved.Should().ContainSingle().Subject;

        why.Mate.Should().Be(scene.Mate.Id);

        // The reason as well as the id. The two ways a mate fails to translate need different
        // repairs -- a missing component has to be deleted, a missing face can be re-pointed -- and
        // only asserting the id let a sabotage swap the messages without failing anything.
        why.Reason.Should().Contain("suppressed");
    }

    [Fact]
    public void ASuppressedMateIsLeftOutWithoutBeingCalledUnresolved()
    {
        Scene scene = new();

        Assembly mated = scene.Assembly.WithMate(scene.Mate with { IsSuppressed = true });
        MateSystem system = MateSystem.For(mated, Scene.ElementOf);

        // Switched off is not the same as broken: nothing is wrong with it, so nothing is reported.
        system.Mates.Should().BeEmpty();
        system.Unresolved.Should().BeEmpty();
    }

    [Fact]
    public void AMateWhoseGeometryCannotBeFoundIsReportedRatherThanDropped()
    {
        Scene scene = new();

        Assembly mated = scene.Assembly.WithMate(
            Coincident(scene.BaseAt, "NoSuchPlane", scene.BoltAt, "Front"));

        MateSystem system = MateSystem.For(mated, Scene.ElementOf);

        system.Mates.Should().BeEmpty();

        // Told apart from a missing component, because the repair differs: this one can be
        // re-pointed at another face, and that one has to be deleted.
        system.Unresolved.Should().ContainSingle()
            .Which.Reason.Should().Contain("attaches to");
    }

    // --- Solving, and coming back ------------------------------------------------------------------

    [Fact]
    public void SolvingMovesThePlacementTheMateWasAbout()
    {
        Scene scene = new();
        Assembly mated = scene.Assembly.WithMate(scene.Mate);

        MateSystem system = MateSystem.For(mated, Scene.ElementOf);
        MateSolveResult result = Solver.Solve(system.Bodies, system.Mates);

        result.IsSolved.Should().BeTrue(result.Diagnosis.Message);

        Assembly moved = system.ApplyTo(mated, result);

        // The bolt's own origin is mated to the base's, and the base is grounded at the world
        // origin, so the bolt has to arrive there.
        moved.FindOccurrence(scene.BoltAt.Leaf)!.Placement
            .TransformPoint(Vec3d.Zero).IsNear(Vec3d.Zero, 1e-6).Should().BeTrue();
    }

    [Fact]
    public void SolvingDoesNotMoveAGroundedPlacement()
    {
        Scene scene = new();
        Assembly mated = scene.Assembly.WithMate(scene.Mate);

        MateSystem system = MateSystem.For(mated, Scene.ElementOf);
        MateSolveResult result = Solver.Solve(system.Bodies, system.Mates);

        Assembly moved = system.ApplyTo(mated, result);

        moved.FindOccurrence(scene.BaseAt.Leaf)!.Placement
            .Should().Be(mated.FindOccurrence(scene.BaseAt.Leaf)!.Placement);
    }

    [Fact]
    public void AnAssemblyAlreadySatisfiedComesBackUnchanged()
    {
        Scene scene = new();

        // Put the bolt where the mate already wants it, so the solve has nothing to do.
        ComponentOccurrence bolt = scene.Assembly.FindOccurrence(scene.BoltAt.Leaf)!;

        Assembly settled = scene.Assembly
            .ReplaceOccurrence(bolt with { Placement = Transform.Identity })
            .WithMate(scene.Mate);

        MateSystem system = MateSystem.For(settled, Scene.ElementOf);
        MateSolveResult result = Solver.Solve(system.Bodies, system.Mates);

        // Reference-identical. A placement rewritten with the same numbers would make every rebuild
        // look like an edit, and a document that reports itself dirty after opening is one nobody
        // can trust about anything else.
        system.ApplyTo(settled, result).Should().BeSameAs(settled);
    }

    // --- Drag ---------------------------------------------------------------------------------------

    [Fact]
    public void TheComponentBeingDraggedIsTheOneThatDoesNotMove()
    {
        Scene scene = new();

        // Neither component grounded, so without a drag either could move to satisfy the mate.
        ComponentOccurrence baseAt = scene.Assembly.FindOccurrence(scene.BaseAt.Leaf)!;

        Assembly loose = scene.Assembly
            .ReplaceOccurrence(baseAt with { IsGrounded = false })
            .WithMate(scene.Mate);

        Transform held = loose.FindOccurrence(scene.BoltAt.Leaf)!.Placement;

        MateSystem system = MateSystem.For(loose, Scene.ElementOf, moving: scene.BoltAt);
        MateSolveResult result = Solver.Solve(system.Bodies, system.Mates);

        Assembly moved = system.ApplyTo(loose, result);

        // §5.9's live mate solving means the assembly follows the hand, not that the hand is
        // corrected by the assembly. The dragged component is the fixed point of its own drag.
        moved.FindOccurrence(scene.BoltAt.Leaf)!.Placement.Should().Be(held);

        moved.FindOccurrence(scene.BaseAt.Leaf)!.Placement
            .TransformPoint(Vec3d.Zero).IsNear(held.TransformPoint(Vec3d.Zero), 1e-6)
            .Should().BeTrue("the rest of the assembly came to it");
    }

    [Fact]
    public void DraggingAGroundedComponentStillLeavesItWhereItIs()
    {
        Scene scene = new();
        Assembly mated = scene.Assembly.WithMate(scene.Mate);

        MateSystem system = MateSystem.For(mated, Scene.ElementOf, moving: scene.BaseAt);

        // Already grounded, and being dragged. Both say the same thing, and asking twice must not
        // make it any less true.
        MateBodyId baseBody = system.Instances.First(pair => pair.Value.Equals(scene.BaseAt)).Key;
        system.Bodies.Single(b => b.Id == baseBody).IsGrounded.Should().BeTrue();
    }

    // --- Persistence ---------------------------------------------------------------------------------

    [Fact]
    public void AMateSurvivesARoundTrip()
    {
        Scene scene = new();

        MateDefinition mate = scene.Mate with
        {
            Kind = MateKind.Distance,
            Value = 0.015,
            IsFlipped = true,
            IsSuppressed = true,
        };

        Document original = Document.Empty(DocumentKind.Assembly)
            .WithAssembly(scene.Assembly.WithMate(mate));

        Document read = DocumentCodec.Read(DocumentCodec.Write(original));

        read.Assembly.Mates.Should().ContainSingle().Which.Should().Be(mate);
    }

    [Fact]
    public void AnAssemblyWithNoMatesWritesNoMateSection()
    {
        Document original = Document.Empty(DocumentKind.Assembly)
            .WithAssembly(new Scene().Assembly);

        MessagePackMap document =
            (MessagePackMap)MessagePackValue.Read(DocumentCodec.Write(original));

        MessagePackMap assembly = (MessagePackMap)document.Find("assembly")!;

        assembly.Find("mates").Should().BeNull();
    }

    [Fact]
    public void AFileMatingSomethingItDoesNotPlaceIsRefused()
    {
        Scene scene = new();

        Document original = Document.Empty(DocumentKind.Assembly)
            .WithAssembly(scene.Assembly.WithMate(scene.Mate));

        MessagePackMap document =
            (MessagePackMap)MessagePackValue.Read(DocumentCodec.Write(original));

        MessagePackMap assembly = (MessagePackMap)document.Find("assembly")!;

        byte[] corrupt = document
            .With("assembly", assembly.With("occurrences", new MessagePackArray([])))
            .ToBytes();

        // The message as well as the type. Read already converts an ArgumentException into a
        // DocumentFormatException, so asserting only the type would pass with the specific message
        // gone -- which is the whole of what catching it here buys.
        FluentActions.Invoking(() => DocumentCodec.Read(corrupt))
            .Should().Throw<DocumentFormatException>()
            .WithMessage("*does not place*");
    }

    private static MateDefinition Coincident(
        OccurrencePath first, string firstElement, OccurrencePath second, string secondElement)
        => new(
            MateId.New(),
            MateKind.Coincident,
            new MateEnd(first, firstElement),
            new MateEnd(second, secondElement));

    /// <summary>
    /// A base and a bolt, the base grounded, mated origin to origin.
    /// </summary>
    /// <remarks>
    /// Points rather than planes, because what is under test here is the crossing rather than the
    /// arithmetic: a point-to-point mate has one obvious right answer and
    /// <c>FakeAssemblySolverTests</c> already covers what the residuals mean.
    /// </remarks>
    private sealed class Scene
    {
        public Scene()
        {
            ComponentDefinition plate = new(ComponentDefinitionId.New(), "plate.omcad", "Plate");
            ComponentDefinition bolt = new(ComponentDefinitionId.New(), "bolt.omcad", "Bolt");

            OccurrenceId baseId = OccurrenceId.New();
            OccurrenceId boltId = OccurrenceId.New();

            BaseAt = OccurrencePath.Of(baseId);
            BoltAt = OccurrencePath.Of(boltId);

            Assembly = Assembly.Empty
                .WithDefinition(plate)
                .WithDefinition(bolt)
                .WithOccurrence(new ComponentOccurrence(
                    baseId, plate.Id, Transform.Identity, IsGrounded: true))
                .WithOccurrence(new ComponentOccurrence(
                    boltId, bolt.Id, Transform.FromTranslation(new Vec3d(0.2, 0.1, 0))));

            Mate = Coincident(BaseAt, "Origin", BoltAt, "Origin");
        }

        public Assembly Assembly { get; }

        public OccurrencePath BaseAt { get; }

        public OccurrencePath BoltAt { get; }

        public MateDefinition Mate { get; }

        /// <summary>Stands in for opening the component and finding what the mate names.</summary>
        /// <remarks>
        /// Static, and returning the base type: the delegate MateSystem takes is
        /// <c>Func&lt;MateEnd, MateElement?&gt;</c>, so narrowing the return here would only move
        /// the conversion.
        /// </remarks>
        public static MateElement? ElementOf(MateEnd end)
        {
            ArgumentNullException.ThrowIfNull(end);

            MateElement? found = end.Element == "Origin"
                ? new MateElement.Point(Vec3d.Zero)
                : null;

            return found;
        }
    }
}
