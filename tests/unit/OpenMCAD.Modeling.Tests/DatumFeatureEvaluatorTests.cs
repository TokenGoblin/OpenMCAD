using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;
using OpenMCAD.Core.Rebuild;
using OpenMCAD.Kernel;
using OpenMCAD.Kernel.Threading;
using OpenMCAD.Math;
using OpenMCAD.Modeling;

using Xunit;

namespace OpenMCAD.Modeling.Tests;

/// <summary>
/// A datum from the feature tree to the document, through the real rebuild engine (P5-T03).
/// </summary>
/// <remarks>
/// <para>
/// The whole path, not a unit of it: a feature is added, the engine sequences and evaluates it, and
/// the reference geometry it produced is published into the document where
/// <see cref="Document.FindReference"/> can find it. Every piece below this has its own tests; what
/// only this can show is that they are actually connected — and that path had never been walked,
/// because <see cref="FeatureOutput.References"/> had no producer until now.
/// </para>
/// <para>
/// No kernel is involved and none is faked. A datum is naming and arithmetic, which is exactly why
/// it can be the first feature to exist end to end.
/// </para>
/// </remarks>
public sealed class DatumFeatureEvaluatorTests
{
    [Fact]
    public async Task ADatumFeaturePutsItsPlaneInTheDocument()
    {
        using Scenario scene = new();

        FeatureId id = scene.AddOffsetPlane("Offset1", "Top", 0.010);

        RebuildResult result = await scene.RebuildAsync();

        result.IsClean.Should().BeTrue();

        ReferenceGeometry.Plane plane =
            scene.Session.Current.FindReference(id, "Offset1").Should()
                .BeOfType<ReferenceGeometry.Plane>().Subject;

        plane.Origin.Z.Should().BeApproximately(0.010, 1e-12);
        plane.Normal.IsParallelTo(Vec3d.UnitZ).Should().BeTrue();
    }

    [Fact]
    public async Task ChangingTheOffsetMovesTheDatumRatherThanAddingOne()
    {
        using Scenario scene = new();

        FeatureId id = scene.AddOffsetPlane("Offset1", "Top", 0.010);
        await scene.RebuildAsync();

        scene.SetDistance(id, 0.025);
        await scene.RebuildAsync();

        scene.DatumsOf(id).Should().ContainSingle();

        ((ReferenceGeometry.Plane)scene.DatumsOf(id)[0]).Origin.Z
            .Should().BeApproximately(0.025, 1e-12);
    }

    [Fact]
    public async Task ADatumBuiltOnAnotherDatumFollowsItWhenItMoves()
    {
        // The reason a datum stores a name and not coordinates. If the second plane had recorded
        // where the first one was, it would stay behind the moment the first one moved.
        using Scenario scene = new();

        FeatureId first = scene.AddOffsetPlane("Offset1", "Top", 0.010);
        FeatureId second = scene.AddOffsetPlane("Offset2", "Offset1", 0.005, of: first);

        await scene.RebuildAsync();

        ((ReferenceGeometry.Plane)scene.DatumsOf(second)[0]).Origin.Z
            .Should().BeApproximately(0.015, 1e-12);

        scene.SetDistance(first, 0.100);
        await scene.RebuildAsync();

        ((ReferenceGeometry.Plane)scene.DatumsOf(second)[0]).Origin.Z
            .Should().BeApproximately(0.105, 1e-12, "the second plane follows the first");
    }

    [Fact]
    public async Task SuppressingADatumTakesItOutOfTheDocument()
    {
        using Scenario scene = new();

        FeatureId id = scene.AddOffsetPlane("Offset1", "Top", 0.010);
        await scene.RebuildAsync();

        scene.Suppress(id);
        await scene.RebuildAsync();

        // A suppressed datum that stayed behind would remain sketchable, and a sketch built on it
        // would go on working while the tree said the feature was switched off.
        scene.DatumsOf(id).Should().BeEmpty();
    }

    [Fact]
    public async Task ADatumThatCannotBeBuiltFailsOnlyItsOwnFeature()
    {
        using Scenario scene = new();

        FeatureId good = scene.AddOffsetPlane("Offset1", "Top", 0.010);
        FeatureId bad = scene.AddOffsetPlane("Offset2", "NoSuchPlane", 0.010);

        RebuildResult result = await scene.RebuildAsync();

        // §5.4: one bad feature must not take the document with it. The evaluator throws and the
        // engine contains it, which is why a refusal that DatumResolver reports as data becomes an
        // exception at this boundary and nowhere else.
        result.Failed.Should().Equal(bad);
        scene.Session.Current.Report.StateOf(bad).Should().Be(FeatureState.Failed);
        scene.Session.Current.Report.StateOf(good).Should().Be(FeatureState.Ok);

        scene.DatumsOf(good).Should().ContainSingle();
        scene.DatumsOf(bad).Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedDatumSaysWhatWasWrongWithIt()
    {
        using Scenario scene = new();

        FeatureId bad = scene.AddOffsetPlane("Offset2", "NoSuchPlane", 0.010);

        await scene.RebuildAsync();

        scene.Session.Current.Report.For(bad)!.Message.Should().Contain("NoSuchPlane");
    }

    [Fact]
    public async Task ADatumOnAFaceUsesWhatTheEngineAlreadyResolved()
    {
        using Scenario scene = new();

        FeatureId id = scene.AddPlaneOnFace("Offset1", 0.004);

        RebuildResult result = await scene.RebuildAsync();

        result.Failed.Should().BeEmpty();
        scene.Session.Current.Report.StateOf(id).Should().Be(FeatureState.Ok);

        // The face's plane came from the evaluator's planeOf delegate, and the face itself from
        // FeatureEvaluation.Resolved -- not from a second walk of the naming tiers, which could
        // score candidates against a model in a different state and answer differently.
        ((ReferenceGeometry.Plane)scene.DatumsOf(id)[0]).Origin.Z
            .Should().BeApproximately(0.007, 1e-12);
    }

    private sealed class Scenario : IDisposable
    {
        /// <summary>Where the one face this scene has actually is.</summary>
        private static readonly Plane FacePlane =
            Plane.FromPointNormal(new Vec3d(0, 0, 0.003), Vec3d.UnitZ);

        private readonly KernelDispatcher _dispatcher = new("datum feature kernel");
        private FeatureId _slab = FeatureId.None;

        public Scenario()
        {
            Session = new DocumentSession();
            Engine = new RebuildEngine(
                Session,
                _dispatcher,
                new Evaluator(FacePlane),
                new GeometryCache());
        }

        public DocumentSession Session { get; }

        public RebuildEngine Engine { get; }

        public FeatureId AddOffsetPlane(
            string name, string from, double metres, FeatureId? of = null)
        {
            FeatureId id = FeatureId.New();

            Edit($"Add {name}", t => t.AddFeature(
                Feature.Create(id, name, DatumFeature.PlaneOffset) with
                {
                    // Declared as an input as well as named in the setting: FeatureGraph reads its
                    // edges from Inputs and EntityReferences, and a datum's settings are not
                    // somewhere it looks, so this is what sequences the two rebuilds in order.
                    Inputs = of is { } upstream ? [upstream] : [],
                    Parameters = [new Parameter("distance", Quantity.Metres(metres))],
                    Settings = ImmutableDictionary<string, FeatureValue>.Empty
                        .Add("from", new ReferenceValue(of ?? FeatureId.None, from)),
                }));

            return id;
        }

        public FeatureId AddPlaneOnFace(string name, double metres)
        {
            FeatureId id = FeatureId.New();

            // Outside the transaction below, because adding the slab opens one of its own and a
            // session deliberately refuses two at once.
            PersistentName face = FaceName();

            Edit($"Add {name}", t => t.AddFeature(
                Feature.Create(id, name, DatumFeature.PlaneOffset) with
                {
                    Parameters = [new Parameter("distance", Quantity.Metres(metres))],
                    References =
                    [
                        new EntityReference(face, MultiplicityPolicy.ExactlyOne, "from"),
                    ],
                }));

            return id;
        }

        public Task<RebuildResult> RebuildAsync() => Engine.RebuildAllAsync();

        public ImmutableArray<ReferenceGeometry> DatumsOf(FeatureId id)
            => [.. Session.Current.References.Where(r => r.Owner == id)];

        public void SetDistance(FeatureId id, double metres) => Edit(
            "Change distance",
            t => t.ReplaceFeature(Session.Current.FindFeature(id)! with
            {
                Parameters = [new Parameter("distance", Quantity.Metres(metres))],
            }));

        public void Suppress(FeatureId id) => Edit(
            "Suppress",
            t => t.ReplaceFeature(Session.Current.FindFeature(id)! with { IsSuppressed = true }));

        public void Dispose()
        {
            Engine.Dispose();
            _dispatcher.Dispose();
        }

        /// <summary>
        /// A face made by a feature of its own, earlier in the same rebuild, so that a name for it
        /// resolves.
        /// </summary>
        /// <remarks>
        /// It has to be a real feature rather than a history handed to the engine: the engine
        /// builds its history as the rebuild goes, in evaluation order, because that is the order
        /// name resolution replays it in (P3-T09).
        /// </remarks>
        private PersistentName FaceName()
        {
            if (_slab.IsValid)
            {
                return Evaluator.FaceOf(_slab);
            }

            _slab = FeatureId.New();

            FeatureId id = _slab;
            Edit("Add Slab", t => t.AddFeature(Feature.Create(id, "Slab", Evaluator.MakesAFace)));

            return Evaluator.FaceOf(_slab);
        }

        private void Edit(string name, Action<IDocumentTransaction> change)
        {
            using IDocumentTransaction transaction = Session.BeginTransaction(name);
            change(transaction);
            transaction.Commit();
        }
    }

    /// <summary>
    /// The datum evaluator, plus the one thing it cannot do: make a face for a datum to be built on.
    /// </summary>
    /// <remarks>
    /// A dispatching evaluator is what a real application has — <see cref="IFeatureEvaluator"/> is
    /// one seam for every feature type, and <c>OpenMCAD.Modeling</c> will grow a registry that picks
    /// between them. Here it is two cases, because the point is only that a datum can be built on
    /// topology a feature above it produced in the same rebuild.
    /// </remarks>
    private sealed class Evaluator(Plane facePlane) : IFeatureEvaluator
    {
        public const string MakesAFace = "TestSlab";

        private static readonly KernelShape Shape = new(1);

        private readonly DatumFeatureEvaluator _datums = new(planeOf: _ => facePlane);

        public static SubEntity Face => new(Shape, 1, SubEntityKind.Face);

        public static PersistentName FaceOf(FeatureId maker) => PersistentName.Of(
            NameSegment.Of(maker, ProvenanceKind.New, EntityRole.From(OperationRole.SideWall)));

        public FeatureOutput Evaluate(
            FeatureEvaluation evaluation, CancellationToken cancellationToken)
        {
            if (evaluation.Feature.FeatureType != MakesAFace)
            {
                return _datums.Evaluate(evaluation, cancellationToken);
            }

            return new FeatureOutput(
                [new Body(BodyId.New(), evaluation.Feature.Id, BodyKind.Solid, Shape)],
                [],
                new HistoryMapBuilder().AddNew(Face, OperationRole.SideWall).Build());
        }
    }
}
