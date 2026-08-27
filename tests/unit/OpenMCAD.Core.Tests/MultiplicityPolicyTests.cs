using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;
using OpenMCAD.Core.Rebuild;
using OpenMCAD.Kernel;
using OpenMCAD.Math;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// Split and merge multiplicity policies (P3-T12).
/// </summary>
/// <remarks>
/// §5.3 says this is where most naming bugs live, and the reason is that the same split has three
/// different correct answers depending on what the feature is for. The tests below are the same
/// split three times, so that what changes is only the declaration.
/// </remarks>
public sealed class MultiplicityPolicyTests
{
    [Fact]
    public void ExactlyOneAsksGeometryWhichPieceWasMeant()
    {
        // A split under this policy is a genuine ambiguity, so it goes to tier two -- which is
        // allowed to settle it when one piece clearly matches better. The big piece is where the
        // original was and is nearly its size, so it wins by a clear margin.
        Fixture fixture = new();
        fixture.SplitTheWall();

        ResolvedReference resolved = fixture.Resolve(MultiplicityPolicy.ExactlyOne);

        resolved.IsResolved.Should().BeTrue();
        resolved.Entities.Should().Equal([fixture.Big]);

        // The same entity LargestDescendant would have picked, and for an entirely different
        // reason: this one asked which piece resembles what was recorded, and would have chosen
        // the smaller piece had the original been over there.
        resolved.Scores.Should().HaveCount(2);

        // The default, and deliberately the strictest of the three.
        new EntityReference(fixture.Name).Multiplicity
            .Should().Be(MultiplicityPolicy.ExactlyOne);
    }

    [Fact]
    public void ExactlyOneStopsAndAsksWhenGeometryCannotChooseEither()
    {
        // Both halves the same size, both the same distance from where the original was. History
        // cannot say and resemblance cannot say, so the only honest answer is to put it to the
        // user -- which is the whole point of the strictest policy being the default.
        Fixture fixture = new();
        fixture.SplitTheWall(bigArea: 0.5, smallArea: 0.5, symmetric: true);

        ResolvedReference resolved = fixture.Resolve(MultiplicityPolicy.ExactlyOne);

        resolved.IsResolved.Should().BeFalse();
        resolved.Entities.Should().BeEmpty();

        ReferenceRepair repair = ReferenceRepair.For(
            FeatureId.New(), fixture.Name, new NameResolution(
                resolved.Outcome, SubEntity.None, [fixture.Big, fixture.Small],
                resolved.Reason, resolved.Ranking),
            _ => "Fillet2");

        repair.Action.Should().Contain("Fillet2");
        repair.HasSuggestions.Should().BeTrue("the user is owed the pieces to choose between");
    }

    [Fact]
    public void AllDescendantsTakesEveryPiece()
    {
        Fixture fixture = new();
        fixture.SplitTheWall();

        ResolvedReference resolved = fixture.Resolve(MultiplicityPolicy.AllDescendants);

        // Not an ambiguity for this feature. A shell told to remove a face has to remove all of
        // what that face became, or the part comes out with a wall the user thought they opened.
        resolved.IsResolved.Should().BeTrue();
        resolved.Entities.Should().BeEquivalentTo([fixture.Big, fixture.Small]);
    }

    [Fact]
    public void LargestDescendantTakesTheBiggestPiece()
    {
        Fixture fixture = new();
        fixture.SplitTheWall();

        ResolvedReference resolved = fixture.Resolve(MultiplicityPolicy.LargestDescendant);

        resolved.IsResolved.Should().BeTrue();
        resolved.Entities.Should().Equal([fixture.Big]);
    }

    [Fact]
    public void ThePolicyDecidesWhetherGeometryIsConsultedAtAll()
    {
        // The part worth understanding. Tier two exists to arbitrate an ambiguity, and for two of
        // the three policies an ambiguity is not something to arbitrate -- so it is never asked.
        // Under AllDescendants a split is the answer; under LargestDescendant the tie-break is
        // stated outright and a resemblance argument could only disagree with it.
        Fixture fixture = new();
        fixture.SplitTheWall();

        fixture.Resolve(MultiplicityPolicy.AllDescendants).Scores.Should().BeEmpty();
        fixture.Resolve(MultiplicityPolicy.LargestDescendant).Scores.Should().BeEmpty();

        // Only this one has a question geometry can help with.
        fixture.Resolve(MultiplicityPolicy.ExactlyOne).Scores.Should().NotBeEmpty();
    }

    [Fact]
    public void ASymmetricSplitDefeatsLargestDescendantHonestly()
    {
        // Two pieces of exactly equal size is a real outcome, and "the largest" does not name one
        // of them. Taking whichever the kernel reported first would resolve differently between
        // runs while looking perfectly decisive.
        Fixture fixture = new();
        fixture.SplitTheWall(bigArea: 0.5, smallArea: 0.5);

        ResolvedReference resolved = fixture.Resolve(MultiplicityPolicy.LargestDescendant);

        resolved.IsResolved.Should().BeFalse();
        resolved.Reason.Should().Contain("same size");
    }

    [Fact]
    public void WithNothingToMeasureLargestDescendantRefusesRatherThanPicks()
    {
        Fixture fixture = new();
        fixture.SplitTheWall();

        ResolvedReference resolved = fixture.ResolveWithoutMeasuring(MultiplicityPolicy.LargestDescendant);

        resolved.IsResolved.Should().BeFalse();
        resolved.Reason.Should().Contain("no way to measure");
    }

    [Fact]
    public void AReferenceThatDidNotSplitResolvesTheSameWhateverThePolicySays()
    {
        // The policy only says what to do about a split. With one answer there is nothing for it
        // to decide, and all three have to agree -- otherwise the declaration would be changing
        // behaviour in the ordinary case, which is not what it is for.
        Fixture fixture = new();
        fixture.KeepTheWallWhole();

        foreach (MultiplicityPolicy policy in Enum.GetValues<MultiplicityPolicy>())
        {
            ResolvedReference resolved = fixture.Resolve(policy);

            resolved.IsResolved.Should().BeTrue($"{policy} should resolve an unsplit reference");
            resolved.Entities.Should().Equal([fixture.Whole]);
            resolved.OnlyEntity.Should().Be(fixture.Whole);
        }
    }

    [Fact]
    public void AskingForOneEntityWhenThereAreSeveralIsRefusedRatherThanTruncated()
    {
        Fixture fixture = new();
        fixture.SplitTheWall();

        ResolvedReference resolved = fixture.Resolve(MultiplicityPolicy.AllDescendants);

        Action single = () => _ = resolved.OnlyEntity;

        single.Should().Throw<InvalidOperationException>(
            "silently taking the first of a set is how a shell ends up removing one of two faces");
    }

    [Fact]
    public void AFeatureDependsOnWhateverItsReferencesPointInto()
    {
        // A feature that points at a face of Extrude1 depends on Extrude1 whether or not it also
        // declared it as an input. A graph that missed that would order it before the thing it is
        // built on, and it would build against geometry that did not exist yet.
        FeatureId extrude = FeatureId.New();
        FeatureId fillet = FeatureId.New();

        PersistentName wall = PersistentName.Of(
            NameSegment.Of(extrude, ProvenanceKind.Generated, EntityRole.SideWall));

        Document document = Document.Empty()
            .WithFeatureAdded(Feature.Create(fillet, "Fillet1", "Fillet") with
            {
                References = [new EntityReference(wall)],
            })
            .WithFeatureAdded(Feature.Create(extrude, "Extrude1", "Extrude"));

        FeatureGraph graph = FeatureGraph.Build(document);

        graph.DependenciesOf(fillet).Should().Equal([extrude]);

        // And so the fillet is ordered after the extrude, despite coming first in the tree.
        graph.EvaluationOrder.Should().Equal([extrude, fillet]);
    }

    [Fact]
    public void AReferenceIntoAFeaturesOwnOutputIsNotASelfDependency()
    {
        // A later segment of a name legitimately points into what the feature itself produced.
        // Counting that as an edge would make the feature depend on itself, which is a cycle --
        // and one that is an artefact of how the name is written rather than anything the user did.
        FeatureId fillet = FeatureId.New();

        PersistentName ownFace = PersistentName.Of(
            NameSegment.Of(fillet, ProvenanceKind.Generated, EntityRole.BlendFace));

        Document document = Document.Empty()
            .WithFeatureAdded(Feature.Create(fillet, "Fillet1", "Fillet") with
            {
                References = [new EntityReference(ownFace)],
            });

        Action build = () => FeatureGraph.Build(document);

        build.Should().NotThrow<FeatureCycleException>();
        FeatureGraph.Build(document).DependenciesOf(fillet).Should().BeEmpty();
    }

    [Fact]
    public void RePointingAReferenceChangesTheCacheKey()
    {
        // The one case where a stale cached answer is guaranteed to be wrong: the user has just
        // repaired a reference, which means they have said in as many words that the old geometry
        // was not what they meant.
        FeatureId id = FeatureId.New();
        FeatureId extrude = FeatureId.New();

        Feature before = Feature.Create(id, "Fillet1", "Fillet") with
        {
            References =
            [
                new EntityReference(PersistentName.Of(
                    NameSegment.Of(extrude, ProvenanceKind.Generated, EntityRole.SideWall))),
            ],
        };

        Feature after = before with
        {
            References =
            [
                new EntityReference(PersistentName.Of(
                    NameSegment.Of(extrude, ProvenanceKind.Generated, EntityRole.EndCap))),
            ],
        };

        RebuildKey.For(before, []).Should().NotBe(RebuildKey.For(after, []));
    }

    [Fact]
    public void ChangingOnlyTheMultiplicityPolicyChangesTheCacheKey()
    {
        // It changes what the feature is built on -- one face or all of them -- so it changes what
        // the feature produces, and a key that ignored it would serve the answer for the other.
        FeatureId id = FeatureId.New();

        PersistentName name = PersistentName.Of(
            NameSegment.Of(FeatureId.New(), ProvenanceKind.Generated, EntityRole.SideWall));

        Feature one = Feature.Create(id, "Shell1", "Shell") with
        {
            References = [new EntityReference(name, MultiplicityPolicy.ExactlyOne)],
        };

        Feature all = one with
        {
            References = [new EntityReference(name, MultiplicityPolicy.AllDescendants)],
        };

        RebuildKey.For(one, []).Should().NotBe(RebuildKey.For(all, []));
    }

    /// <summary>One wall, optionally divided in two.</summary>
    private sealed class Fixture
    {
        private readonly RebuildHistory.Builder _history = new();
        private readonly Dictionary<SubEntity, GeoHint> _hints = [];
        private readonly FeatureId _extrude = FeatureId.New();
        private readonly FeatureId _pocket = FeatureId.New();
        private readonly FeatureId _consumer = FeatureId.New();
        private readonly SubEntity _line;

        public Fixture()
        {
            KernelShape shape = new(1);

            _line = new SubEntity(shape, 1, SubEntityKind.Edge);
            Whole = new SubEntity(shape, 10, SubEntityKind.Face);
            Big = new SubEntity(shape, 20, SubEntityKind.Face);
            Small = new SubEntity(shape, 21, SubEntityKind.Face);

            _history.Add(_extrude, new HistoryMapBuilder()
                .AddGenerated(_line, Whole, OperationRole.SideWall)
                .Build());

            Name = PersistentName.Of(new NameSegment(
                _extrude,
                ProvenanceKind.Generated,
                [new NameSource.Entity(Anchor(_line))],
                EntityRole.From(OperationRole.SideWall),
                0,
                new GeoHint(GeometryKind.Plane, 1.0, Vec3d.Zero, Vec3d.UnitZ, 4)));
        }

        public SubEntity Whole { get; }

        public SubEntity Big { get; }

        public SubEntity Small { get; }

        public PersistentName Name { get; }

        /// <summary>A later feature divides the wall in two.</summary>
        /// <param name="bigArea">The area of the first piece.</param>
        /// <param name="smallArea">The area of the second.</param>
        /// <param name="symmetric">
        /// Whether to place the pieces equidistant from where the original was, so that neither
        /// resembles it more closely than the other.
        /// </param>
        public void SplitTheWall(
            double bigArea = 0.8, double smallArea = 0.2, bool symmetric = false)
        {
            _history.Add(_pocket, new HistoryMapBuilder()
                .AddModified(Whole, Big, OperationRole.SplitPositive)
                .AddModified(Whole, Small, OperationRole.SplitNegative)
                .Build());

            Vec3d bigAt = symmetric ? new Vec3d(0.25, 0, 0) : new Vec3d(0.1, 0, 0);
            Vec3d smallAt = symmetric ? new Vec3d(-0.25, 0, 0) : new Vec3d(-0.4, 0, 0);

            _hints[Big] = new GeoHint(GeometryKind.Plane, bigArea, bigAt, Vec3d.UnitZ, 4);
            _hints[Small] = new GeoHint(GeometryKind.Plane, smallArea, smallAt, Vec3d.UnitZ, 4);
        }

        /// <summary>A later feature leaves the wall alone.</summary>
        public void KeepTheWallWhole()
            => _hints[Whole] = new GeoHint(GeometryKind.Plane, 1.0, Vec3d.Zero, Vec3d.UnitZ, 4);

        public ResolvedReference Resolve(MultiplicityPolicy policy) => new NameResolver(
            _history.Build(),
            entity => _hints.TryGetValue(entity, out GeoHint? hint) ? hint : null,
            _ => _hints.Keys).Resolve(new EntityReference(Name, policy), _consumer);

        public ResolvedReference ResolveWithoutMeasuring(MultiplicityPolicy policy)
            => new NameResolver(_history.Build())
                .Resolve(new EntityReference(Name, policy), _consumer);

        private PersistentName Anchor(SubEntity entity)
        {
            FeatureId anchor = FeatureId.New();

            _history.Add(anchor, new HistoryMapBuilder()
                .AddNew(entity, OperationRole.Retained)
                .Build());

            return PersistentName.Of(
                NameSegment.Of(anchor, ProvenanceKind.New, EntityRole.From(OperationRole.Retained)));
        }
    }
}
