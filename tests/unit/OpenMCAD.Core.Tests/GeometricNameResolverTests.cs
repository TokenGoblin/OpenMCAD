using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;
using OpenMCAD.Kernel;
using OpenMCAD.Math;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// Tier two of name resolution (P3-T10): choosing by resemblance when history cannot say.
/// </summary>
/// <remarks>
/// This tier is the one most able to be confidently wrong, so most of what follows checks that it
/// refuses rather than that it succeeds. §5.3 is unambiguous about the trade: a wrong-but-plausible
/// resolution silently corrupts downstream design intent, while a refusal stops and asks.
/// </remarks>
public sealed class GeometricNameResolverTests
{
    private static readonly KernelShape Shape = new(1);

    [Fact]
    public void TheHalfOfASplitFaceThatIsWhereTheOriginalWasIsChosen()
    {
        // The case this tier exists for. A face was split in two; history reports both halves and
        // cannot say which was meant. One of them is where the original was and is nearly its size.
        GeoHint original = Face(area: 1.0, at: new Vec3d(0, 0, 0));

        Scene scene = new();
        SubEntity kept = scene.Add(Face(area: 0.9, at: new Vec3d(0.02, 0, 0)));
        SubEntity offcut = scene.Add(Face(area: 0.1, at: new Vec3d(5, 0, 0)));

        NameResolution resolved = scene.Resolve(original);

        resolved.Outcome.Should().Be(NameResolutionOutcome.Resolved);
        resolved.Entity.Should().Be(kept);
        resolved.Scores.Should().HaveCount(2);
        resolved.Scores[0].Entity.Should().Be(kept);
        resolved.Scores[1].Entity.Should().Be(offcut);
    }

    [Fact]
    public void TwoCandidatesThatFitEquallyWellAreRefused()
    {
        // A symmetric split: both halves are the same size and equidistant from where the original
        // was. The geometry genuinely does not say which was meant, and the one that happens to
        // score a thousandth higher is a coin toss dressed as an answer.
        GeoHint original = Face(area: 1.0, at: new Vec3d(0, 0, 0));

        Scene scene = new();
        scene.Add(Face(area: 0.5, at: new Vec3d(-0.25, 0, 0)));
        scene.Add(Face(area: 0.5, at: new Vec3d(0.25, 0, 0)));

        NameResolution resolved = scene.Resolve(original);

        resolved.Outcome.Should().Be(NameResolutionOutcome.Ambiguous);
        resolved.Entity.IsValid.Should().BeFalse();
        resolved.Reason.Should().Contain("almost equally well");
    }

    [Fact]
    public void ACandidateOfTheWrongKindIsNotEvenConsidered()
    {
        // Kind is a gate rather than a term. Were it one score among several, a perfect centroid
        // match could outvote it -- and a plane is never the face a cylinder became.
        GeoHint original = new(GeometryKind.Cylinder, 1.0, Vec3d.Zero, Vec3d.UnitZ, 4);

        Scene scene = new();
        scene.Add(new GeoHint(GeometryKind.Plane, 1.0, Vec3d.Zero, Vec3d.UnitZ, 4));

        NameResolution resolved = scene.Resolve(original);

        resolved.Outcome.Should().Be(NameResolutionOutcome.NotFound);
        resolved.Scores.Should().BeEmpty("a candidate of the wrong kind is not a candidate");
        resolved.Reason.Should().Contain("Cylinder");
    }

    [Fact]
    public void SomethingThatHasSimplyGoneIsRefusedRatherThanReplaced()
    {
        // The least bad of a bad field is not the answer. The confidence threshold is what stops a
        // reference silently reattaching to whatever remains.
        GeoHint original = Face(area: 1.0, at: new Vec3d(0, 0, 0));

        Scene scene = new();
        scene.Add(Face(area: 0.01, at: new Vec3d(50, 40, 30), normal: -Vec3d.UnitZ));

        NameResolution resolved = scene.Resolve(original);

        resolved.Outcome.Should().Be(NameResolutionOutcome.NotFound);
        resolved.Reason.Should().Contain("most likely gone");

        // The score still comes back, because the user is owed something they can act on.
        resolved.Scores.Should().ContainSingle();
        resolved.Scores[0].Score.Should().BeLessThan(GeometricMatchSettings.Default.Confidence);
    }

    [Fact]
    public void AFaceThatMovedALittleIsStillTheSameFace()
    {
        GeoHint original = Face(area: 1.0, at: new Vec3d(0, 0, 0));

        Scene scene = new();
        SubEntity moved = scene.Add(Face(area: 1.0, at: new Vec3d(0.05, 0, 0)));

        scene.Resolve(original).Entity.Should().Be(moved);
    }

    [Fact]
    public void DistanceIsJudgedAgainstTheEntitysOwnSize()
    {
        // CAD spans watch parts to airframes, so an absolute tolerance has to be wrong at one end.
        // The same proportional displacement should score the same at any scale.
        Scene small = new();
        small.Add(Face(area: 1e-6, at: new Vec3d(1e-4, 0, 0)));

        Scene large = new();
        large.Add(Face(area: 1e6, at: new Vec3d(1e2, 0, 0)));

        double smallScore = small.Resolve(Face(area: 1e-6, at: Vec3d.Zero)).Scores[0].Score;
        double largeScore = large.Resolve(Face(area: 1e6, at: Vec3d.Zero)).Scores[0].Score;

        smallScore.Should().BeApproximately(largeScore, 1e-9);
    }

    [Fact]
    public void TheFaceFacingTheRightWayWinsOverTheOneBehindIt()
    {
        // The two sides of a thin plate. Their centroids differ by the thickness, which is small
        // next to the width, so placement barely separates them and the normal is the only thing
        // that does. Direction is mapped from [-1, 1] rather than clamped at zero so that pointing
        // the opposite way actually costs something.
        GeoHint original = Face(area: 1.0, at: Vec3d.Zero, normal: Vec3d.UnitZ);

        Scene scene = new();
        SubEntity front = scene.Add(Face(area: 1.0, at: new Vec3d(0, 0, 0.01), normal: Vec3d.UnitZ));
        SubEntity back = scene.Add(Face(area: 1.0, at: new Vec3d(0, 0, -0.01), normal: -Vec3d.UnitZ));

        NameResolution resolved = scene.Resolve(original);

        resolved.Entity.Should().Be(front);
        resolved.Scores.Single(s => s.Entity == front).Score
            .Should().BeGreaterThan(resolved.Scores.Single(s => s.Entity == back).Score);
    }

    [Fact]
    public void AReversedNormalIsNotOnItsOwnDisqualifying()
    {
        // Deliberately not treated as fatal. A boolean subtract routinely hands back the same face
        // with its orientation reversed, and a matcher that refused on that alone would reject
        // references to faces that are perfectly intact. It costs the candidate a quarter of its
        // score, which is enough to lose to the right face when the right face is there, and not
        // enough to fail when it is the only one.
        GeoHint original = Face(area: 1.0, at: Vec3d.Zero, normal: Vec3d.UnitZ);

        Scene scene = new();
        SubEntity reversed = scene.Add(Face(area: 1.0, at: Vec3d.Zero, normal: -Vec3d.UnitZ));

        NameResolution resolved = scene.Resolve(original);

        resolved.Entity.Should().Be(reversed);
        resolved.Scores[0].Score.Should().BeApproximately(0.75, 0.001);
    }

    [Fact]
    public void MissingEvidenceIsNotEvidenceAgainst()
    {
        // A reference written before some part of the hint was recorded is not a reference to
        // something that is wrong in that respect. Counting absent terms as zero would push every
        // older reference below the threshold at once.
        GeoHint sparse = new(GeometryKind.Plane, 1.0, Vec3d.Zero, Vec3d.Zero, 0);

        Scene scene = new();
        SubEntity match = scene.Add(new GeoHint(GeometryKind.Plane, 1.0, Vec3d.Zero, Vec3d.Zero, 0));

        NameResolution resolved = scene.Resolve(sparse);

        resolved.Outcome.Should().Be(NameResolutionOutcome.Resolved);
        resolved.Entity.Should().Be(match);
    }

    [Fact]
    public void AHintWithNothingButItsKindIsNotEnoughToAcceptOn()
    {
        // The gate has been passed and there is no further evidence either way. Half is the honest
        // score, and half is below the confidence threshold -- as it should be.
        GeoHint bare = GeoHint.Of(GeometryKind.Plane);

        Scene scene = new();
        scene.Add(GeoHint.Of(GeometryKind.Plane));

        NameResolution resolved = scene.Resolve(bare);

        resolved.Scores[0].Score.Should().Be(0.5);
        resolved.Outcome.Should().Be(NameResolutionOutcome.NotFound);
    }

    [Fact]
    public void AReferenceWithNoRecordedGeometryCannotBeMatchedAtAll()
    {
        Scene scene = new();
        scene.Add(Face(area: 1.0, at: Vec3d.Zero));

        NameResolution resolved = scene.Resolve(recorded: null);

        resolved.Outcome.Should().Be(NameResolutionOutcome.NotFound);
        resolved.Reason.Should().Contain("no record of what its geometry looked like");
    }

    [Fact]
    public void NothingToChooseFromIsReportedRatherThanCrashing()
    {
        Scene scene = new();

        scene.Resolve(Face(area: 1.0, at: Vec3d.Zero))
            .Outcome.Should().Be(NameResolutionOutcome.NotFound);
    }

    [Fact]
    public void TheRankingIsTheSameOnEveryRun()
    {
        // A diagnostic that changes when nothing changed is one nobody trusts, and the runner-up is
        // part of what the user is shown. Equal scores are ordered by the entity itself rather than
        // left to the sort.
        GeoHint original = Face(area: 1.0, at: Vec3d.Zero);

        ImmutableArray<SubEntity> first = [];

        for (int run = 0; run < 20; ++run)
        {
            Scene scene = new();

            scene.Add(Face(area: 0.5, at: new Vec3d(-0.25, 0, 0)), tag: 30);
            scene.Add(Face(area: 0.5, at: new Vec3d(0.25, 0, 0)), tag: 20);
            scene.Add(Face(area: 0.5, at: new Vec3d(0, 0.25, 0)), tag: 10);

            ImmutableArray<SubEntity> ranking =
                [.. scene.Resolve(original).Scores.Select(s => s.Entity)];

            if (run == 0)
            {
                first = ranking;
            }

            ranking.Should().Equal(first);
        }
    }

    [Fact]
    public void TheThresholdsCanBeLoosenedForCallersThatWantThat()
    {
        // The defaults refuse in the doubtful cases on purpose. A caller with a repair UI in front
        // of the user, or a batch tool with different priorities, can trade differently -- but has
        // to say so rather than getting it by accident.
        GeoHint original = Face(area: 1.0, at: Vec3d.Zero);

        Scene scene = new();
        SubEntity distant = scene.Add(Face(area: 0.2, at: new Vec3d(3, 0, 0)));

        scene.Resolve(original).Outcome.Should().Be(NameResolutionOutcome.NotFound);

        NameResolution loose = scene.Resolve(
            original, new GeometricMatchSettings(Confidence: 0.2, Margin: 0.05));

        loose.Outcome.Should().Be(NameResolutionOutcome.Resolved);
        loose.Entity.Should().Be(distant);
    }

    private static GeoHint Face(double area, Vec3d at, Vec3d? normal = null)
        => new(GeometryKind.Plane, area, at, normal ?? Vec3d.UnitZ, 4);

    /// <summary>A pool of candidates with known geometry.</summary>
    private sealed class Scene
    {
        private readonly Dictionary<SubEntity, GeoHint> _hints = [];
        private ulong _next = 100;

        public SubEntity Add(GeoHint hint, ulong? tag = null)
        {
            SubEntity entity = new(Shape, tag ?? _next++, SubEntityKind.Face);
            _hints[entity] = hint;

            return entity;
        }

        public NameResolution Resolve(GeoHint? recorded, GeometricMatchSettings? settings = null)
        {
            PersistentName name = PersistentName.Of(new NameSegment(
                FeatureId.New(),
                ProvenanceKind.Generated,
                [],
                EntityRole.SideWall,
                0,
                recorded));

            GeometricNameResolver resolver = new(
                entity => _hints.TryGetValue(entity, out GeoHint? hint) ? hint : null, settings);

            return resolver.Resolve(name, _hints.Keys);
        }
    }
}
