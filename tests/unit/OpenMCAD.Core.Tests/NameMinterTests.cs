using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;
using OpenMCAD.Kernel;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// Writing a name for a picked entity (P3-T08..P3-T12, §5.3).
/// </summary>
/// <remarks>
/// The counterpart of <see cref="HistoryNameResolverTests"/>. Those start from a name and ask what
/// it refers to; these start from an entity and ask what name refers to it. The property that
/// matters is the one the two halves make together — a name minted against one rebuild finds the
/// same feature's work in the next, after every kernel tag has changed.
/// </remarks>
public sealed class NameMinterTests
{
    [Fact]
    public void AnEntityGeneratedFromAnotherIsNamedAfterIt()
    {
        // §5.3's provenance vocabulary: Generated is "created by the feature out of one of its
        // inputs", which is what AddGenerated records, and the name must say so.
        Fixture fixture = new();

        SubEntity profile = fixture.Edge(1);
        SubEntity wall = fixture.Face(10);

        fixture.Record(fixture.Base, new HistoryMapBuilder()
            .AddNew(profile, OperationRole.Retained)
            .Build());

        fixture.Record(fixture.Extrude, new HistoryMapBuilder()
            .AddGenerated(profile, wall, OperationRole.SideWall)
            .Build());

        PersistentName name = fixture.Mint(wall).Should().NotBeNull().And.Subject.As<PersistentName>();

        name.Head.Feature.Should().Be(fixture.Extrude);
        name.Head.Provenance.Should().Be(ProvenanceKind.Generated);
        name.Head.Role.Should().Be(EntityRole.From(OperationRole.SideWall));
        name.Head.Sources.Should().ContainSingle().Which.Should().BeOfType<NameSource.Entity>();
    }

    [Fact]
    public void AnEntityAlteredRatherThanCreatedIsNamedModified()
    {
        // The other half of the vocabulary: Modified is "an existing entity the feature altered but
        // did not replace". A minted name that called this Generated would describe the model
        // wrongly even where it still resolved.
        Fixture fixture = new();

        SubEntity before = fixture.Face(1);
        SubEntity after = fixture.Face(2);

        fixture.Record(fixture.Base, new HistoryMapBuilder()
            .AddNew(before, OperationRole.Retained)
            .Build());

        fixture.Record(fixture.Extrude, new HistoryMapBuilder()
            .AddModified(before, after, OperationRole.SideWall)
            .Build());

        fixture.Mint(after)!.Head.Provenance.Should().Be(ProvenanceKind.Modified);
    }

    [Fact]
    public void AnEntityMadeFromNothingIsNamedNewAndHasNoSources()
    {
        // "Created by the feature out of nothing that can be pointed at." There is no source to
        // name, and claiming one would be a reference to something that does not exist.
        Fixture fixture = new();

        SubEntity face = fixture.Face(1);

        fixture.Record(fixture.Base, new HistoryMapBuilder()
            .AddNew(face, OperationRole.Retained)
            .Build());

        PersistentName name = fixture.Mint(face)!;

        name.Head.Provenance.Should().Be(ProvenanceKind.New);
        name.Head.Sources.Should().BeEmpty();
    }

    [Fact]
    public void AUniqueAnswerIsRecordedAsOrdinalZeroRatherThanOne()
    {
        // §5.3: ordinals count from one, and zero means "there was only one of these when this
        // reference was written". The resolver reads a zero facing several candidates as a
        // reference to something that has since become several -- the split its second tier
        // arbitrates. Minting a 1 for a unique answer would throw that distinction away.
        Fixture fixture = new();

        SubEntity profile = fixture.Edge(1);
        SubEntity wall = fixture.Face(10);

        fixture.Record(fixture.Base, new HistoryMapBuilder()
            .AddNew(profile, OperationRole.Retained)
            .Build());

        fixture.Record(fixture.Extrude, new HistoryMapBuilder()
            .AddGenerated(profile, wall, OperationRole.SideWall)
            .AddGenerated(profile, fixture.Face(11), OperationRole.StartCap)
            .Build());

        // The cap comes off the same edge, so narrowing by source alone leaves two. Only the role
        // makes the wall unique, and a count taken before the role is applied would say 1 here --
        // which reads as "the first of several" rather than "the only one".
        fixture.Mint(wall)!.Head.Ordinal.Should().Be(0);
    }

    [Fact]
    public void SiblingsAreNumberedFromOneAndEachNameFindsItsOwn()
    {
        // Two walls off one profile edge, both playing the same part. The role cannot separate
        // them, so the ordinal must -- and each minted name must come back to the entity it was
        // minted from rather than to whichever sibling happens to be first.
        Fixture fixture = new();

        SubEntity profile = fixture.Edge(1);
        SubEntity first = fixture.Face(10);
        SubEntity second = fixture.Face(11);

        fixture.Record(fixture.Base, new HistoryMapBuilder()
            .AddNew(profile, OperationRole.Retained)
            .Build());

        fixture.Record(fixture.Extrude, new HistoryMapBuilder()
            .AddGenerated(profile, first, OperationRole.SideWall)
            .AddGenerated(profile, second, OperationRole.SideWall)
            .Build());

        fixture.Mint(first)!.Head.Ordinal.Should().Be(1);
        fixture.Mint(second)!.Head.Ordinal.Should().Be(2);

        fixture.Resolve(fixture.Mint(first)!).Entity.Should().Be(first);
        fixture.Resolve(fixture.Mint(second)!).Entity.Should().Be(second);
    }

    [Fact]
    public void TheOrdinalCountsAmongWhatTheNameItselfWillOffer()
    {
        // The subtle one. This feature makes four walls, two from each of two profile edges. A name
        // carrying its source narrows to that source's two before the ordinal is read, so the
        // second wall of the second edge is ordinal 2 -- not 4, which is its place among all the
        // walls the operation made. Counting over the wider set and resolving against the narrower
        // one picks the wrong sibling, and does it silently, because both sets are self-consistent.
        Fixture fixture = new();

        SubEntity left = fixture.Edge(1);
        SubEntity right = fixture.Edge(2);

        SubEntity fromLeft = fixture.Face(10);
        SubEntity alsoLeft = fixture.Face(11);
        SubEntity fromRight = fixture.Face(12);
        SubEntity alsoRight = fixture.Face(13);

        fixture.Record(fixture.Base, new HistoryMapBuilder()
            .AddNew(left, OperationRole.Retained)
            .AddNew(right, OperationRole.Retained)
            .Build());

        fixture.Record(fixture.Extrude, new HistoryMapBuilder()
            .AddGenerated(left, fromLeft, OperationRole.SideWall)
            .AddGenerated(left, alsoLeft, OperationRole.SideWall)
            .AddGenerated(right, fromRight, OperationRole.SideWall)
            .AddGenerated(right, alsoRight, OperationRole.SideWall)
            .Build());

        fixture.Mint(alsoRight)!.Head.Ordinal.Should().Be(2);

        fixture.Resolve(fixture.Mint(alsoRight)!).Entity.Should().Be(alsoRight);
        fixture.Resolve(fixture.Mint(fromRight)!).Entity.Should().Be(fromRight);
        fixture.Resolve(fixture.Mint(alsoLeft)!).Entity.Should().Be(alsoLeft);
    }

    [Fact]
    public void ASelectionSurvivesARebuildThatChangedEveryTag()
    {
        // The whole point of the exercise, and the thing that could not be done before: a name is
        // minted against one rebuild and resolved against the next, in which the kernel issued
        // entirely different tags for the same faces. Nothing the name holds is a tag.
        Fixture first = new();

        SubEntity profile = first.Edge(1);
        SubEntity wall = first.Face(10);

        first.Record(first.Base, new HistoryMapBuilder()
            .AddNew(profile, OperationRole.Retained)
            .Build());

        first.Record(first.Extrude, new HistoryMapBuilder()
            .AddGenerated(profile, wall, OperationRole.SideWall)
            .Build());

        PersistentName name = first.Mint(wall)!;

        Fixture again = new(first);

        SubEntity movedProfile = again.Edge(41);
        SubEntity movedWall = again.Face(50);

        again.Record(again.Base, new HistoryMapBuilder()
            .AddNew(movedProfile, OperationRole.Retained)
            .Build());

        again.Record(again.Extrude, new HistoryMapBuilder()
            .AddGenerated(movedProfile, movedWall, OperationRole.SideWall)
            .Build());

        again.Resolve(name).Entity.Should().Be(movedWall);
    }

    [Fact]
    public void ANameFollowsTheEntityThroughAFeatureAddedAfterIt()
    {
        // Inserting a feature above an existing reference is the commonest edit there is. The name
        // says nothing about the new feature -- it had not run when the name was written -- so the
        // resolver carries the entity through it.
        Fixture first = new();

        SubEntity face = first.Face(1);

        first.Record(first.Base, new HistoryMapBuilder()
            .AddNew(face, OperationRole.Retained)
            .Build());

        PersistentName name = first.Mint(face)!;

        Fixture again = new(first);
        SubEntity shelled = again.Face(2);

        again.Record(again.Base, new HistoryMapBuilder()
            .AddNew(face, OperationRole.Retained)
            .Build());

        again.Record(again.Shell, new HistoryMapBuilder()
            .AddModified(face, shelled, OperationRole.Retained)
            .Build());

        again.Resolve(name).Entity.Should().Be(shelled);
    }

    [Fact]
    public void AnEntityIsNamedWhereItWasLastLeftRatherThanWhereItBegan()
    {
        // An entity can be an output of several features in turn. Naming it at the first would name
        // something a later feature has already replaced, so the last one wins.
        Fixture fixture = new();

        SubEntity before = fixture.Face(1);
        SubEntity after = fixture.Face(2);

        fixture.Record(fixture.Base, new HistoryMapBuilder()
            .AddNew(before, OperationRole.Retained)
            .Build());

        fixture.Record(fixture.Shell, new HistoryMapBuilder()
            .AddModified(before, after, OperationRole.BlendFace)
            .Build());

        fixture.Mint(after)!.Head.Feature.Should().Be(fixture.Shell);
    }

    [Fact]
    public void AHalfOfASplitFaceIsNamedAtTheFeatureThatSplitIt()
    {
        // §5.3's split category, and the case that makes naming an entity where it was *last* left
        // a matter of correctness rather than taste. One half keeps the original's identity, which
        // is what a kernel usually does -- so that half is an output of two features, and there is
        // a real choice about which to name it at.
        //
        // Name it at the feature that made the original and resolution finds one face there, then
        // carries it forward into the feature that turned it into two: ambiguous, and rightly so,
        // because at that point the reference genuinely does not say which half was meant. Named at
        // the split itself, the halves are siblings the role and the ordinal separate.
        Fixture fixture = new();

        SubEntity whole = fixture.Face(1);
        SubEntity other = fixture.Face(3);

        fixture.Record(fixture.Base, new HistoryMapBuilder()
            .AddNew(whole, OperationRole.Retained)
            .Build());

        fixture.Record(fixture.Shell, new HistoryMapBuilder()
            .AddModified(whole, whole, OperationRole.SplitPositive)
            .AddModified(whole, other, OperationRole.SplitNegative)
            .Build());

        fixture.Mint(whole)!.Head.Feature.Should().Be(fixture.Shell);

        fixture.Resolve(fixture.Mint(whole)!).Entity.Should().Be(whole);
        fixture.Resolve(fixture.Mint(other)!).Entity.Should().Be(other);
    }

    [Fact]
    public void AnEntityIsNamedByWhateverProducedItRatherThanByTheLatestFeature()
    {
        // A later feature that had nothing to do with this entity must not end up in its name.
        // Searching back from the end has to keep going until it finds the feature that actually
        // reported the entity, not stop at the first one it looks at.
        Fixture fixture = new();

        SubEntity face = fixture.Face(1);

        fixture.Record(fixture.Base, new HistoryMapBuilder()
            .AddNew(face, OperationRole.Retained)
            .Build());

        fixture.Record(fixture.Extrude, new HistoryMapBuilder()
            .AddNew(fixture.Face(2), OperationRole.Retained)
            .Build());

        fixture.Mint(face)!.Head.Feature.Should().Be(fixture.Base);
    }

    [Fact]
    public void AnEntityTheHistoryDoesNotAccountForCannotBeNamed()
    {
        // Geometry from a feature that failed, or from a build older than this rebuild. Declining
        // is the answer: a name that cannot be resolved would be written into a document and fail
        // later, at a distance from whatever caused it.
        Fixture fixture = new();

        fixture.Record(fixture.Base, new HistoryMapBuilder()
            .AddNew(fixture.Face(1), OperationRole.Retained)
            .Build());

        fixture.Mint(fixture.Face(99)).Should().BeNull();
    }

    [Fact]
    public void NothingIsNotAnEntityAndCannotBeNamed()
    {
        Fixture fixture = new();

        fixture.Record(fixture.Base, new HistoryMapBuilder()
            .AddNew(fixture.Face(1), OperationRole.Retained)
            .Build());

        fixture.Mint(SubEntity.None).Should().BeNull();
    }

    [Fact]
    public void OnlyWhatHadRunWhenTheReferenceWasMadeCanBeNamed()
    {
        // A feature may only refer to what came before it. Naming an entity produced later would
        // be a reference backwards in time, and the rebuild order is what makes that meaningless.
        Fixture fixture = new();

        SubEntity early = fixture.Face(1);
        SubEntity late = fixture.Face(2);

        fixture.Record(fixture.Base, new HistoryMapBuilder()
            .AddNew(early, OperationRole.Retained)
            .Build());

        fixture.Record(fixture.Extrude, new HistoryMapBuilder()
            .AddNew(late, OperationRole.Retained)
            .Build());

        fixture.Mint(early, fixture.Extrude).Should().NotBeNull();
        fixture.Mint(late, fixture.Extrude).Should().BeNull("the extrude cannot refer to its own output");
    }

    private sealed class Fixture
    {
        private readonly RebuildHistory.Builder _builder = new();

        public Fixture()
        {
        }

        /// <summary>Continues with the same feature ids and a fresh history.</summary>
        public Fixture(Fixture other)
        {
            Base = other.Base;
            Extrude = other.Extrude;
            Shell = other.Shell;
            Consumer = other.Consumer;
        }

        public FeatureId Base { get; } = FeatureId.New();

        public FeatureId Extrude { get; } = FeatureId.New();

        public FeatureId Shell { get; } = FeatureId.New();

        public FeatureId Consumer { get; } = FeatureId.New();

        public RebuildHistory History => _builder.Build();

        /// <remarks>
        /// Per fixture rather than shared: a SubEntity is identified by its owner as well as its
        /// tag, so two fixtures using one shape would have entities comparing equal across tests
        /// that are meant to be independent. The rebuild pair deliberately uses two shapes, which
        /// is what makes "every tag changed" true of the owner as well as the number.
        /// </remarks>
        public KernelShape Shape { get; } = new(1);

        public SubEntity Face(ulong tag) => new(Shape, tag, SubEntityKind.Face);

        public SubEntity Edge(ulong tag) => new(Shape, tag, SubEntityKind.Edge);

        public void Record(FeatureId feature, HistoryMap map) => _builder.Add(feature, map);

        public PersistentName? Mint(SubEntity entity) => Mint(entity, Consumer);

        public PersistentName? Mint(SubEntity entity, FeatureId consumer)
            => new NameMinter(History).Mint(entity, consumer);

        public NameResolution Resolve(PersistentName name)
            => new HistoryNameResolver(History).Resolve(name, Consumer);
    }
}
