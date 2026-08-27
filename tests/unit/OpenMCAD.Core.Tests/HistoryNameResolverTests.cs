using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;
using OpenMCAD.Kernel;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// Tier one of name resolution (P3-T09): replaying what each operation did.
/// </summary>
/// <remarks>
/// The scenarios here are the ones §5.3 names as the categories a naming corpus must cover, in
/// miniature: a dimension change that keeps the topology, a feature inserted above an existing
/// reference, a face split in two, a deletion. The full corpus over real geometry is P3-T13; these
/// are over hand-built history maps, so they test the replay rather than the kernel.
/// </remarks>
public sealed class HistoryNameResolverTests
{
    [Fact]
    public void AGeneratedFaceIsFoundFromTheThingThatGeneratedIt()
    {
        Fixture fixture = new();

        SubEntity line = fixture.Edge(1);
        SubEntity wall = fixture.Face(10);

        fixture.Record(fixture.Extrude, new HistoryMapBuilder()
            .AddGenerated(line, wall, OperationRole.SideWall)
            .Build());

        PersistentName name = fixture.Wall(line);

        fixture.Resolve(name).Entity.Should().Be(wall);
    }

    [Fact]
    public void AReferenceSurvivesTheGeometryChangingUnderIt()
    {
        // The everyday case: a dimension changed, so the kernel issued entirely different tags for
        // the same faces. A reference stored as an index or a position would now be wrong.
        //
        // The name is anchored on the sketch line rather than on a kernel entity, and that is the
        // point rather than a convenience of the test: a kernel tag is a handle into one rebuild
        // and means nothing in the next, so the only thing a durable reference can be tied to is
        // something with an identity of its own. That is where the recursion in a name stops.
        Fixture first = new();

        PersistentName name = PersistentName.Of(new NameSegment(
            first.Extrude,
            ProvenanceKind.Generated,
            [new NameSource.Sketch(first.Sketch, "L3")],
            EntityRole.From(OperationRole.SideWall)));

        first.Record(first.Extrude, new HistoryMapBuilder()
            .AddGenerated(first.Edge(1), first.Face(10), OperationRole.SideWall)
            .Build());

        first.ResolveWithSketch(name, "L3", first.Edge(1)).Entity.Should().Be(first.Face(10));

        // The same model rebuilt after an edit. Not one tag in common with the run above.
        Fixture second = new(first);

        second.Record(second.Extrude, new HistoryMapBuilder()
            .AddGenerated(second.Edge(43), second.Face(77), OperationRole.SideWall)
            .Build());

        second.ResolveWithSketch(name, "L3", second.Edge(43)).Entity.Should().Be(second.Face(77));
    }

    [Fact]
    public void TheRoleIsWhatSeparatesSiblingsFromOneSource()
    {
        Fixture fixture = new();
        SubEntity profile = fixture.Edge(1);

        fixture.Record(fixture.Extrude, new HistoryMapBuilder()
            .AddGenerated(profile, fixture.Face(10), OperationRole.SideWall)
            .AddGenerated(profile, fixture.Face(11), OperationRole.StartCap)
            .AddGenerated(profile, fixture.Face(12), OperationRole.EndCap)
            .Build());

        fixture.Resolve(fixture.Wall(profile)).Entity.Should().Be(fixture.Face(10));

        fixture.Resolve(fixture.Named(fixture.Extrude, EntityRole.EndCap, profile))
            .Entity.Should().Be(fixture.Face(12));
    }

    [Fact]
    public void AFilletBlendIsFoundFromBothFacesItSitsBetween()
    {
        // §5.3's worked example. Either face on its own also produced the blends along every other
        // edge it touches; only the pair identifies this one, which is why sources intersect
        // rather than union.
        Fixture fixture = new();

        SubEntity wall = fixture.Face(10);
        SubEntity cap = fixture.Face(11);
        SubEntity blend = fixture.Face(20);
        SubEntity otherBlend = fixture.Face(21);

        fixture.Record(fixture.Fillet, new HistoryMapBuilder()
            .AddGenerated(wall, blend, OperationRole.BlendFace)
            .AddGenerated(cap, blend, OperationRole.BlendFace)
            .AddGenerated(wall, otherBlend, OperationRole.BlendFace)
            .Build());

        PersistentName name = PersistentName.Of(new NameSegment(
            fixture.Fillet,
            ProvenanceKind.Intersection,
            [
                new NameSource.Entity(fixture.Anchor(wall)),
                new NameSource.Entity(fixture.Anchor(cap)),
            ],
            EntityRole.From(OperationRole.BlendFace)));

        // Which is to say: the blend both faces produced, not the one only the wall did.
        fixture.Resolve(name).Entity.Should().Be(blend);
    }

    [Fact]
    public void OneSourceAloneWouldNotHaveBeenEnough()
    {
        // The same scene, asked with one source. Two blends came off that wall, so the answer is
        // honestly ambiguous -- which is the point of recording both sources in the first place.
        Fixture fixture = new();

        SubEntity wall = fixture.Face(10);

        fixture.Record(fixture.Fillet, new HistoryMapBuilder()
            .AddGenerated(wall, fixture.Face(20), OperationRole.BlendFace)
            .AddGenerated(wall, fixture.Face(21), OperationRole.BlendFace)
            .Build());

        PersistentName name = PersistentName.Of(new NameSegment(
            fixture.Fillet,
            ProvenanceKind.Generated,
            [new NameSource.Entity(fixture.Anchor(wall))],
            EntityRole.From(OperationRole.BlendFace)));

        NameResolution resolved = fixture.Resolve(name);

        resolved.Outcome.Should().Be(NameResolutionOutcome.Ambiguous);
        resolved.Candidates.Should().HaveCount(2);
    }

    [Fact]
    public void AReferenceSurvivesAFeatureBeingInsertedAboveIt()
    {
        // The commonest edit there is, and the reason resolution has a second walk. The name says
        // nothing about the shell, because the shell did not exist when the name was written.
        Fixture fixture = new();

        SubEntity line = fixture.Edge(1);
        SubEntity wall = fixture.Face(10);
        SubEntity thinnedWall = fixture.Face(30);

        fixture.Record(fixture.Extrude, new HistoryMapBuilder()
            .AddGenerated(line, wall, OperationRole.SideWall)
            .Build());

        PersistentName name = fixture.Wall(line);

        fixture.Record(fixture.Shell, new HistoryMapBuilder()
            .AddModified(wall, thinnedWall, OperationRole.Retained)
            .Build());

        fixture.Resolve(name).Entity.Should().Be(
            thinnedWall, "the face is the same face, and the shell said what became of it");
    }

    [Fact]
    public void AnOperationThatDidNotTouchTheEntityLeavesItAlone()
    {
        Fixture fixture = new();

        SubEntity line = fixture.Edge(1);
        SubEntity wall = fixture.Face(10);

        fixture.Record(fixture.Extrude, new HistoryMapBuilder()
            .AddGenerated(line, wall, OperationRole.SideWall)
            .Build());

        PersistentName name = fixture.Wall(line);

        // A feature that worked on a different body entirely.
        fixture.Record(fixture.Shell, new HistoryMapBuilder()
            .AddGenerated(fixture.Edge(99), fixture.Face(98), OperationRole.SideWall)
            .Build());

        fixture.Resolve(name).Entity.Should().Be(wall);
    }

    [Fact]
    public void AFaceThatHasBeenSplitIsReportedAsAmbiguousRatherThanGuessedAt()
    {
        // §5.3: a wrong-but-plausible resolution silently corrupts design intent and is worse than
        // an error. Picking the first of the two halves would be exactly that.
        Fixture fixture = new();

        SubEntity line = fixture.Edge(1);
        SubEntity wall = fixture.Face(10);

        fixture.Record(fixture.Extrude, new HistoryMapBuilder()
            .AddGenerated(line, wall, OperationRole.SideWall)
            .Build());

        PersistentName name = fixture.Wall(line);

        fixture.Record(fixture.Shell, new HistoryMapBuilder()
            .AddModified(wall, fixture.Face(40), OperationRole.SplitPositive)
            .AddModified(wall, fixture.Face(41), OperationRole.SplitNegative)
            .Build());

        NameResolution resolved = fixture.Resolve(name);

        resolved.Outcome.Should().Be(NameResolutionOutcome.Ambiguous);
        resolved.Candidates.Should().HaveCount(2);
        resolved.Reason.Should().Contain("divided");

        // And the shortlist is what tier two will arbitrate, so it has to come back rather than
        // being thrown away with the failure.
        resolved.Candidates.Should().Contain(fixture.Face(40)).And.Contain(fixture.Face(41));
    }

    [Fact]
    public void ADeletedEntityIsReportedAsDeletedRatherThanMissing()
    {
        // The two lead somewhere different: history saying "it is gone" is a definite answer, and
        // no amount of geometric searching should conjure a replacement for it.
        Fixture fixture = new();

        SubEntity line = fixture.Edge(1);
        SubEntity wall = fixture.Face(10);

        fixture.Record(fixture.Extrude, new HistoryMapBuilder()
            .AddGenerated(line, wall, OperationRole.SideWall)
            .Build());

        PersistentName name = fixture.Wall(line);

        fixture.Record(fixture.Shell, new HistoryMapBuilder()
            .AddDeleted(wall)
            .AddGenerated(fixture.Edge(2), fixture.Face(50), OperationRole.SideWall)
            .Build());

        NameResolution resolved = fixture.Resolve(name);

        resolved.Outcome.Should().Be(NameResolutionOutcome.Deleted);
        resolved.Entity.IsValid.Should().BeFalse();
    }

    [Fact]
    public void AReferenceToAFeatureThatDidNotRunIsNotFound()
    {
        Fixture fixture = new();

        // Nothing recorded at all.
        NameResolution resolved = fixture.Resolve(fixture.Wall(fixture.Edge(1)));

        resolved.Outcome.Should().Be(NameResolutionOutcome.NotFound);
        resolved.Reason.Should().Contain("has not been rebuilt");
    }

    [Fact]
    public void AReferenceToAMissingRoleIsNotFound()
    {
        Fixture fixture = new();
        SubEntity line = fixture.Edge(1);

        fixture.Record(fixture.Extrude, new HistoryMapBuilder()
            .AddGenerated(line, fixture.Face(10), OperationRole.StartCap)
            .Build());

        NameResolution resolved = fixture.Resolve(fixture.Wall(line));

        resolved.Outcome.Should().Be(NameResolutionOutcome.NotFound);
        resolved.Reason.Should().Contain("SideWall");
    }

    [Fact]
    public void ASketchReferenceIsUnsupportedRatherThanMissingWhileThereIsNoSketchLayer()
    {
        // "Cannot answer" and "the answer is no" are different, and conflating them would have
        // Phase 4's arrival look like a pile of newly broken models.
        Fixture fixture = new();

        fixture.Record(fixture.Extrude, new HistoryMapBuilder()
            .AddGenerated(fixture.Edge(1), fixture.Face(10), OperationRole.SideWall)
            .Build());

        PersistentName name = PersistentName.Of(new NameSegment(
            fixture.Extrude,
            ProvenanceKind.Generated,
            [new NameSource.Sketch(fixture.Sketch, "L3")],
            EntityRole.From(OperationRole.SideWall)));

        NameResolution resolved = fixture.Resolve(name);

        resolved.Outcome.Should().Be(NameResolutionOutcome.Unsupported);
        resolved.Reason.Should().Contain("L3");
    }

    [Fact]
    public void ASketchReferenceResolvesOnceSomethingCanLookOneUp()
    {
        Fixture fixture = new();
        SubEntity line = fixture.Edge(1);

        fixture.Record(fixture.Extrude, new HistoryMapBuilder()
            .AddGenerated(line, fixture.Face(10), OperationRole.SideWall)
            .Build());

        PersistentName name = PersistentName.Of(new NameSegment(
            fixture.Extrude,
            ProvenanceKind.Generated,
            [new NameSource.Sketch(fixture.Sketch, "L3")],
            EntityRole.From(OperationRole.SideWall)));

        HistoryNameResolver resolver = new(
            fixture.History, sketch => sketch.EntityId == "L3" ? line : SubEntity.None);

        resolver.Resolve(name, fixture.Consumer).Entity.Should().Be(fixture.Face(10));
    }

    [Fact]
    public void OperationsAfterTheConsumerAreNotApplied()
    {
        // A reference is resolved on behalf of a feature that is about to run, and from its point
        // of view nothing below it in the tree has happened yet. Applying a later feature's map
        // would answer with a face that does not exist at the moment it is being asked for.
        Fixture fixture = new();

        SubEntity line = fixture.Edge(1);
        SubEntity wall = fixture.Face(10);

        fixture.Record(fixture.Extrude, new HistoryMapBuilder()
            .AddGenerated(line, wall, OperationRole.SideWall)
            .Build());

        PersistentName name = fixture.Wall(line);

        fixture.Record(fixture.Fillet, new HistoryMapBuilder()
            .AddModified(wall, fixture.Face(60), OperationRole.Retained)
            .Build());

        fixture.Record(fixture.Shell, new HistoryMapBuilder()
            .AddModified(fixture.Face(60), fixture.Face(70), OperationRole.Retained)
            .Build());

        HistoryNameResolver resolver = new(fixture.History);

        resolver.Resolve(name, fixture.Fillet).Entity.Should().Be(
            wall, "the fillet has not run yet, so nothing has happened to the face");

        resolver.Resolve(name, fixture.Shell).Entity.Should().Be(fixture.Face(60));
        resolver.Resolve(name, fixture.Consumer).Entity.Should().Be(fixture.Face(70));
    }

    [Fact]
    public void NumberedSiblingsAreCountedFromOne()
    {
        // Zero means "there was only one of these when this was written", so a zero ordinal facing
        // several candidates is a split rather than a reference to the first of them.
        Fixture fixture = new();

        SubEntity profile = fixture.Edge(1);

        fixture.Record(fixture.Extrude, new HistoryMapBuilder()
            .AddGenerated(profile, fixture.Face(10), OperationRole.SideWall)
            .AddGenerated(profile, fixture.Face(11), OperationRole.SideWall)
            .AddGenerated(profile, fixture.Face(12), OperationRole.SideWall)
            .Build());

        fixture.Resolve(fixture.Wall(profile, ordinal: 1)).Entity.Should().Be(fixture.Face(10));
        fixture.Resolve(fixture.Wall(profile, ordinal: 3)).Entity.Should().Be(fixture.Face(12));

        fixture.Resolve(fixture.Wall(profile)).Outcome.Should().Be(
            NameResolutionOutcome.Ambiguous, "zero says there was one, and now there are three");

        fixture.Resolve(fixture.Wall(profile, ordinal: 9)).Outcome.Should().Be(
            NameResolutionOutcome.Ambiguous, "there is no ninth");
    }

    /// <summary>Hand-built histories, so these test the replay and not the kernel.</summary>
    private sealed class Fixture
    {
        private readonly RebuildHistory.Builder _builder = new();

        public Fixture()
        {
        }

        /// <summary>Continues with the same feature ids and a fresh history.</summary>
        public Fixture(Fixture other)
        {
            Sketch = other.Sketch;
            Extrude = other.Extrude;
            Fillet = other.Fillet;
            Shell = other.Shell;
            Consumer = other.Consumer;
        }

        public FeatureId Sketch { get; } = FeatureId.New();

        public FeatureId Extrude { get; } = FeatureId.New();

        public FeatureId Fillet { get; } = FeatureId.New();

        public FeatureId Shell { get; } = FeatureId.New();

        public FeatureId Consumer { get; } = FeatureId.New();

        public RebuildHistory History => _builder.Build();

        /// <summary>The body these entities belong to.</summary>
        /// <remarks>
        /// Held per fixture rather than shared, because a SubEntity is identified by its owner as
        /// well as its tag -- two fixtures using one shape would have entities that compare equal
        /// across tests that are supposed to be independent.
        /// </remarks>
        public KernelShape Shape { get; } = new(1);

        public SubEntity Face(ulong tag) => new(Shape, tag, SubEntityKind.Face);

        public SubEntity Edge(ulong tag) => new(Shape, tag, SubEntityKind.Edge);

        public void Record(FeatureId feature, HistoryMap map) => _builder.Add(feature, map);

        /// <summary>A name for a side wall generated by something.</summary>
        public PersistentName Wall(SubEntity source, int ordinal = 0)
            => Named(Extrude, EntityRole.From(OperationRole.SideWall), source, ordinal);

        public PersistentName Named(
            FeatureId feature, EntityRole role, SubEntity source, int ordinal = 0)
            => PersistentName.Of(new NameSegment(
                feature,
                ProvenanceKind.Generated,
                [new NameSource.Entity(Anchor(source))],
                role,
                ordinal));

        /// <summary>
        /// A name that resolves straight to a known entity, standing in for whatever chain would
        /// really have produced it.
        /// </summary>
        /// <remarks>
        /// The sources in these tests are entities the fixture already holds, and what is being
        /// tested is what the feature under test did with them rather than how they came to be
        /// named. This gives one a history entry of its own so that a name can point at it.
        /// </remarks>
        public PersistentName Anchor(SubEntity entity)
        {
            FeatureId anchor = FeatureId.New();

            _builder.Add(anchor, new HistoryMapBuilder()
                .AddNew(entity, OperationRole.Retained)
                .Build());

            return PersistentName.Of(
                NameSegment.Of(anchor, ProvenanceKind.New, EntityRole.From(OperationRole.Retained)));
        }

        public NameResolution Resolve(PersistentName name)
            => new HistoryNameResolver(History).Resolve(name, Consumer);

        /// <summary>Resolves with a sketch layer that knows where one line ended up.</summary>
        public NameResolution ResolveWithSketch(PersistentName name, string entityId, SubEntity entity)
            => new HistoryNameResolver(
                History, sketch => sketch.EntityId == entityId ? entity : SubEntity.None)
                .Resolve(name, Consumer);
    }
}
