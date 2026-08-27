using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;
using OpenMCAD.Kernel;
using OpenMCAD.Math;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// The three tiers together, and tier three itself (P3-T11).
/// </summary>
/// <remarks>
/// §5.3's rule for this tier is one sentence: no silent wrong answer, ever. So the tests are mostly
/// about what happens when the answer is not known — that the refusal is a refusal, that it says
/// something the user can act on, and that nothing anywhere quietly settles for the nearest thing.
/// </remarks>
public sealed class NameResolverTests
{
    [Fact]
    public void HistoryAnswersFirstAndGeometryIsNotConsulted()
    {
        // Order matters, and not only for speed. History is a record of what happened; geometry is
        // a resemblance argument. If a geometric match could override an exact one, an edit that
        // moved a face a long way would silently re-point every reference to it.
        Fixture fixture = new();

        SubEntity line = fixture.Edge(1);
        SubEntity wall = fixture.Face(10);

        fixture.Record(fixture.Extrude, new HistoryMapBuilder()
            .AddGenerated(line, wall, OperationRole.SideWall)
            .Build());

        // A decoy that resembles the reference far more closely than the true answer does.
        fixture.Describe(wall, new GeoHint(GeometryKind.Plane, 99.0, new Vec3d(50, 0, 0), Vec3d.UnitZ, 9));
        fixture.Describe(fixture.Face(11), fixture.RecordedHint);

        NameResolution resolved = fixture.Resolve(fixture.Wall(line, fixture.RecordedHint));

        resolved.Entity.Should().Be(wall, "history knows, so nothing else gets a say");
        resolved.Scores.Should().BeEmpty("tier two was never reached");
    }

    [Fact]
    public void GeometryArbitratesWhatHistoryCouldNotChoose()
    {
        Fixture fixture = new();

        SubEntity line = fixture.Edge(1);
        SubEntity left = fixture.Face(20);
        SubEntity right = fixture.Face(21);

        // Two candidates of the same role from one source: history reports both and cannot choose.
        fixture.Record(fixture.Extrude, new HistoryMapBuilder()
            .AddGenerated(line, left, OperationRole.SideWall)
            .AddGenerated(line, right, OperationRole.SideWall)
            .Build());

        fixture.Describe(left, new GeoHint(GeometryKind.Plane, 0.95, new Vec3d(0.02, 0, 0), Vec3d.UnitZ, 4));
        fixture.Describe(right, new GeoHint(GeometryKind.Plane, 0.05, new Vec3d(8, 0, 0), Vec3d.UnitZ, 4));

        NameResolution resolved = fixture.Resolve(fixture.Wall(line, fixture.RecordedHint));

        resolved.Outcome.Should().Be(NameResolutionOutcome.Resolved);
        resolved.Entity.Should().Be(left);
        resolved.Scores.Should().HaveCount(2, "tier two scored the shortlist history handed it");
    }

    [Fact]
    public void ADeletedEntityIsNeverReplacedByOneThatResemblesIt()
    {
        // The most important refusal here. History says the entity is gone -- a settled question --
        // and the face that most resembles a deleted face is a different face. Adopting it is
        // exactly the silent corruption §5.3 forbids, and it would look entirely reasonable.
        Fixture fixture = new();

        SubEntity line = fixture.Edge(1);
        SubEntity wall = fixture.Face(10);

        fixture.Record(fixture.Extrude, new HistoryMapBuilder()
            .AddGenerated(line, wall, OperationRole.SideWall)
            .Build());

        PersistentName name = fixture.Wall(line, fixture.RecordedHint);

        fixture.Record(fixture.Shell, new HistoryMapBuilder()
            .AddDeleted(wall)
            .AddGenerated(fixture.Edge(2), fixture.Face(50), OperationRole.SideWall)
            .Build());

        // A perfect geometric double of the deleted face is sitting right there.
        fixture.Describe(fixture.Face(50), fixture.RecordedHint);

        NameResolution resolved = fixture.Resolve(name);

        resolved.Outcome.Should().Be(NameResolutionOutcome.Deleted);
        resolved.Entity.IsValid.Should().BeFalse();
        resolved.Scores.Should().BeEmpty("nothing was scored, because nothing should have been");
    }

    [Fact]
    public void ABrokenChainIsSearchedForButNotSettledFor()
    {
        // Nothing was recorded at all, so there is no shortlist and the model is searched. What is
        // there resembles the reference only vaguely, and vaguely is not an answer.
        Fixture fixture = new();

        fixture.Describe(fixture.Face(60), new GeoHint(GeometryKind.Plane, 0.01, new Vec3d(40, 40, 40), -Vec3d.UnitZ, 9));

        NameResolution resolved = fixture.Resolve(fixture.Wall(fixture.Edge(1), fixture.RecordedHint));

        resolved.IsResolved.Should().BeFalse();
        resolved.Scores.Should().ContainSingle("the search happened, and reported what it found");
    }

    [Fact]
    public void WithoutAGeometricTierAnAmbiguousReferenceSimplyFails()
    {
        // A legitimate configuration: a batch tool that will not be repairing anything gains
        // nothing from a tier that can only ever produce a maybe.
        Fixture fixture = new();

        SubEntity line = fixture.Edge(1);

        fixture.Record(fixture.Extrude, new HistoryMapBuilder()
            .AddGenerated(line, fixture.Face(20), OperationRole.SideWall)
            .AddGenerated(line, fixture.Face(21), OperationRole.SideWall)
            .Build());

        NameResolution resolved = fixture.ResolveHistoryOnly(fixture.Wall(line, fixture.RecordedHint));

        resolved.Outcome.Should().Be(NameResolutionOutcome.Ambiguous);
        resolved.Scores.Should().BeEmpty();
    }

    [Fact]
    public void AFailureBecomesSomethingTheUserCanActOn()
    {
        Fixture fixture = new();

        PersistentName name = fixture.Wall(fixture.Edge(1), fixture.RecordedHint);
        NameResolution resolved = fixture.Resolve(name);

        ReferenceRepair? repair = NameResolver.Repair(
            fixture.Fillet, name, resolved, id => id == fixture.Fillet ? "Fillet2" : null);

        repair.Should().NotBeNull();

        // §5.3's own example sentence: a verb, the thing, the feature.
        repair!.Action.Should().Be("Reselect the missing face for Fillet2.");
        repair.Problem.Should().Contain("Fillet2").And.Contain("face");

        // It names the feature the user knows, not an id, and describes their model rather than
        // the resolver's internals.
        repair.Problem.Should().NotContain("tier").And.NotContain("threshold");
        repair.Feature.Should().Be(fixture.Fillet);
        repair.Reference.Should().Be(name);
    }

    [Theory]
    [InlineData(GeometryKind.Plane, "face")]
    [InlineData(GeometryKind.Cylinder, "face")]
    [InlineData(GeometryKind.Line, "edge")]
    [InlineData(GeometryKind.Circle, "edge")]
    [InlineData(GeometryKind.Point, "vertex")]
    [InlineData(GeometryKind.Unknown, "entity")]
    public void TheRepairCallsTheThingWhatTheUserCallsIt(GeometryKind kind, string noun)
    {
        // "Reselect the missing edge" is a sentence someone can act on. "Reselect the missing
        // entity" is not, so the general word is the fallback rather than the default.
        Fixture fixture = new();

        PersistentName name = fixture.Wall(fixture.Edge(1), GeoHint.Of(kind));

        ReferenceRepair repair = ReferenceRepair.For(
            fixture.Fillet, name, fixture.Resolve(name), _ => "Fillet2");

        repair.Action.Should().Contain(noun);
    }

    [Fact]
    public void ARepairOffersWhatWasConsidered()
    {
        // The information exists only at the moment resolution fails. Reducing the failure to a
        // boolean throws away which candidates were weighed and how closely each fitted, and
        // nothing downstream can recover it.
        Fixture fixture = new();

        SubEntity line = fixture.Edge(1);
        SubEntity left = fixture.Face(20);
        SubEntity right = fixture.Face(21);

        fixture.Record(fixture.Extrude, new HistoryMapBuilder()
            .AddGenerated(line, left, OperationRole.SideWall)
            .AddGenerated(line, right, OperationRole.SideWall)
            .Build());

        // Equally good, so tier two refuses too.
        fixture.Describe(left, new GeoHint(GeometryKind.Plane, 0.5, new Vec3d(-0.25, 0, 0), Vec3d.UnitZ, 4));
        fixture.Describe(right, new GeoHint(GeometryKind.Plane, 0.5, new Vec3d(0.25, 0, 0), Vec3d.UnitZ, 4));

        PersistentName name = fixture.Wall(line, fixture.RecordedHint);
        NameResolution resolved = fixture.Resolve(name);

        ReferenceRepair repair = ReferenceRepair.For(fixture.Fillet, name, resolved, _ => "Fillet2");

        repair.Outcome.Should().Be(NameResolutionOutcome.Ambiguous);
        repair.HasSuggestions.Should().BeTrue();
        repair.Suggestions.Should().HaveCount(2);
        repair.Action.Should().Be("Choose which face Fillet2 should use.");
    }

    [Fact]
    public void ARepairIsNeverBuiltForSomethingThatWorked()
    {
        Fixture fixture = new();

        SubEntity line = fixture.Edge(1);

        fixture.Record(fixture.Extrude, new HistoryMapBuilder()
            .AddGenerated(line, fixture.Face(10), OperationRole.SideWall)
            .Build());

        PersistentName name = fixture.Wall(line, fixture.RecordedHint);
        NameResolution resolved = fixture.Resolve(name);

        NameResolver.Repair(fixture.Fillet, name, resolved, _ => "Fillet2").Should().BeNull();

        Action forced = () => ReferenceRepair.For(fixture.Fillet, name, resolved, _ => "Fillet2");

        forced.Should().Throw<ArgumentException>(
            "putting a question to the user about something that is not wrong is its own defect");
    }

    [Fact]
    public void AnUnresolvedReferenceIsAnErrorTheTreeShows()
    {
        Fixture fixture = new();

        PersistentName name = fixture.Wall(fixture.Edge(1), fixture.RecordedHint);
        NameResolution resolved = fixture.Resolve(name);

        ReferenceRepair repair = ReferenceRepair.For(fixture.Fillet, name, resolved, _ => "Fillet2");

        RebuildReport.Builder builder = new();

        builder.Add(new FeatureDiagnostic(
            fixture.Fillet, FeatureState.UnresolvedReference, repair.Problem, Repair: repair));

        RebuildReport report = builder.Build();

        report.HasErrors.Should().BeTrue();
        report.Errors.Should().ContainSingle().Which.Feature.Should().Be(fixture.Fillet);

        // And it reaches the repair UI's list, which is separate from the error list because not
        // every error has anything to reselect.
        report.Repairs.Should().ContainSingle().Which.Action.Should().Contain("Fillet2");
    }

    [Fact]
    public void AFeatureThatSimplyFailedOffersNoRepair()
    {
        RebuildReport.Builder builder = new();

        builder.Add(new FeatureDiagnostic(FeatureId.New(), FeatureState.Failed, "cannot build"));

        RebuildReport report = builder.Build();

        report.HasErrors.Should().BeTrue();
        report.Repairs.Should().BeEmpty(
            "there is no reference to re-point, so offering a reselect button would be a lie");
    }

    private sealed class Fixture
    {
        private readonly RebuildHistory.Builder _history = new();
        private readonly Dictionary<SubEntity, GeoHint> _hints = [];

        public KernelShape Shape { get; } = new(1);

        public FeatureId Extrude { get; } = FeatureId.New();

        public FeatureId Shell { get; } = FeatureId.New();

        public FeatureId Fillet { get; } = FeatureId.New();

        /// <summary>What the reference recorded about itself when it was written.</summary>
        public GeoHint RecordedHint { get; } =
            new(GeometryKind.Plane, 1.0, Vec3d.Zero, Vec3d.UnitZ, 4);

        public SubEntity Face(ulong tag) => new(Shape, tag, SubEntityKind.Face);

        public SubEntity Edge(ulong tag) => new(Shape, tag, SubEntityKind.Edge);

        public void Record(FeatureId feature, HistoryMap map) => _history.Add(feature, map);

        public void Describe(SubEntity entity, GeoHint hint) => _hints[entity] = hint;

        public PersistentName Wall(SubEntity source, GeoHint hint)
        {
            FeatureId anchor = FeatureId.New();

            _history.Add(anchor, new HistoryMapBuilder()
                .AddNew(source, OperationRole.Retained)
                .Build());

            PersistentName origin = PersistentName.Of(
                NameSegment.Of(anchor, ProvenanceKind.New, EntityRole.From(OperationRole.Retained)));

            return PersistentName.Of(new NameSegment(
                Extrude,
                ProvenanceKind.Generated,
                [new NameSource.Entity(origin)],
                EntityRole.From(OperationRole.SideWall),
                0,
                hint));
        }

        public NameResolution Resolve(PersistentName name) => new NameResolver(
            _history.Build(),
            entity => _hints.TryGetValue(entity, out GeoHint? hint) ? hint : null,
            _ => _hints.Keys).Resolve(name, Fillet);

        public NameResolution ResolveHistoryOnly(PersistentName name)
            => new NameResolver(_history.Build()).Resolve(name, Fillet);
    }
}
