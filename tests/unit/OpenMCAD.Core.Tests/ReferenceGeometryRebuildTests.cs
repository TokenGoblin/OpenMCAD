using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Rebuild;
using OpenMCAD.Kernel;
using OpenMCAD.Kernel.Threading;
using OpenMCAD.Math;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// What happens to the reference geometry a feature produces when it is rebuilt, suppressed or
/// rolled back (P3-T04, P3-T06, P3-T07).
/// </summary>
/// <remarks>
/// <para>
/// This path existed from P3-T04 and had never had a producer: <c>FeatureOutput.References</c> is
/// documented as carrying "any reference geometry it created, such as a datum plane", and until
/// P5-T03 nothing created one. So the rebuild replaced a feature's bodies and merely accumulated
/// its references, and nothing noticed.
/// </para>
/// <para>
/// The distinction that makes this matter is identity. A body carries a generated <c>BodyId</c> and
/// is held by it, so re-adding replaces. A datum is identified by its owner and its name, and the
/// collection holding it only ever grew — so the second rebuild produced a second plane with the
/// same name, and a sketch resolving that name would then be choosing between two.
/// </para>
/// </remarks>
public sealed class ReferenceGeometryRebuildTests
{
    [Fact]
    public async Task ARebuiltFeatureReplacesItsDatumRatherThanAddingAnother()
    {
        using Scenario scene = new();

        FeatureId datum = scene.AddDatum("Plane1", 0.010);
        await scene.RebuildAsync();

        scene.DatumsOf(datum).Should().ContainSingle();

        scene.SetOffset(datum, 0.020);
        await scene.RebuildAsync();

        scene.DatumsOf(datum).Should().ContainSingle("a rebuild replaces what a feature produced");

        ((ReferenceGeometry.Plane)scene.DatumsOf(datum)[0]).Origin.Z
            .Should().BeApproximately(0.020, 1e-9, "and it is the new one, not the old");
    }

    [Fact]
    public async Task RebuildingTenTimesLeavesOneDatum()
    {
        // The failure this prevents is cumulative and silent: nothing errors, the document simply
        // grows a duplicate datum per rebuild until the feature tree is unreadable.
        using Scenario scene = new();

        FeatureId datum = scene.AddDatum("Plane1", 0.010);

        for (int i = 1; i <= 10; ++i)
        {
            scene.SetOffset(datum, 0.001 * i);
            await scene.RebuildAsync();
        }

        scene.DatumsOf(datum).Should().ContainSingle();
    }

    [Fact]
    public async Task AFeatureProducingFewerDatumsThanBeforeLeavesNoneBehind()
    {
        // The case replacing-by-name cannot cover, and the exact counterpart of the comment on the
        // body handling: "a feature that produced two bodies last time and one now must not leave
        // the second behind". Without the per-owner clear, the second plane outlives the edit that
        // stopped producing it -- and it is still sketchable.
        using Scenario scene = new();

        FeatureId datum = scene.AddDatum("Plane1", 0.010);
        scene.SetCount(datum, 3);
        await scene.RebuildAsync();

        scene.DatumsOf(datum).Should().HaveCount(3);

        scene.SetCount(datum, 1);
        await scene.RebuildAsync();

        scene.DatumsOf(datum).Should().ContainSingle();
    }

    [Fact]
    public async Task SuppressingAFeatureTakesItsDatumWithIt()
    {
        // A datum belonging to a suppressed feature is not there any more. Leaving it would let a
        // sketch be placed on a plane the model does not currently produce.
        using Scenario scene = new();

        FeatureId datum = scene.AddDatum("Plane1", 0.010);
        await scene.RebuildAsync();

        scene.Suppress(datum);
        await scene.RebuildAsync();

        scene.DatumsOf(datum).Should().BeEmpty();
    }

    [Fact]
    public async Task UnsuppressingBringsItBackOnce()
    {
        using Scenario scene = new();

        FeatureId datum = scene.AddDatum("Plane1", 0.010);
        await scene.RebuildAsync();

        scene.Suppress(datum);
        await scene.RebuildAsync();

        scene.Unsuppress(datum);
        await scene.RebuildAsync();

        scene.DatumsOf(datum).Should().ContainSingle();
    }

    [Fact]
    public async Task TheStandardDatumsSurviveEveryRebuild()
    {
        // They are owned by FeatureId.None, which no feature has, so nothing that works feature by
        // feature can reach them. Asserted because the cost of being wrong is a document whose
        // origin planes silently vanish and whose every sketch loses its plane.
        using Scenario scene = new();

        FeatureId datum = scene.AddDatum("Plane1", 0.010);
        await scene.RebuildAsync();

        scene.Suppress(datum);
        await scene.RebuildAsync();

        scene.Session.Current.References.Where(r => r.Owner == FeatureId.None)
            .Select(r => r.Name)
            .Should().BeEquivalentTo(["Origin", "Front", "Top", "Right"]);
    }

    /// <summary>A document, an engine, and an evaluator that produces a datum plane.</summary>
    private sealed class Scenario : IDisposable
    {
        private readonly KernelDispatcher _dispatcher = new("reference geometry kernel");
        private readonly DatumEvaluator _evaluator = new();

        public Scenario()
        {
            Session = new DocumentSession();
            Engine = new RebuildEngine(Session, _dispatcher, _evaluator, new GeometryCache());
        }

        public DocumentSession Session { get; }

        public RebuildEngine Engine { get; }

        public FeatureId AddDatum(string name, double offset)
        {
            FeatureId id = FeatureId.New();

            Edit($"Add {name}", t => t.AddFeature(
                Feature.Create(id, name, "DatumPlane") with
                {
                    Parameters = [new Parameter("Offset", Quantity.Metres(offset))],
                }));

            return id;
        }

        public Task<RebuildResult> RebuildAsync() => Engine.RebuildAllAsync();

        public ImmutableArray<ReferenceGeometry> DatumsOf(FeatureId id)
            => [.. Session.Current.References.Where(r => r.Owner == id)];

        public void SetOffset(FeatureId id, double offset) => Edit(
            "Change offset",
            t => t.ReplaceFeature(Session.Current.FindFeature(id)! with
            {
                Parameters = [new Parameter("Offset", Quantity.Metres(offset))],
            }));

        public void SetCount(FeatureId id, int count) => Edit(
            "Change count",
            t => t.ReplaceFeature(Session.Current.FindFeature(id)! with
            {
                Settings = ImmutableDictionary<string, FeatureValue>.Empty
                    .Add("Count", new NumberValue(count)),
            }));

        public void Suppress(FeatureId id) => Edit(
            "Suppress",
            t => t.ReplaceFeature(Session.Current.FindFeature(id)! with { IsSuppressed = true }));

        public void Unsuppress(FeatureId id) => Edit(
            "Unsuppress",
            t => t.ReplaceFeature(Session.Current.FindFeature(id)! with { IsSuppressed = false }));

        public void Dispose()
        {
            Engine.Dispose();
            _dispatcher.Dispose();
        }

        private void Edit(string name, Action<IDocumentTransaction> change)
        {
            using IDocumentTransaction transaction = Session.BeginTransaction(name);
            change(transaction);
            transaction.Commit();
        }
    }

    /// <summary>Produces one datum plane per feature, and no bodies at all.</summary>
    /// <remarks>
    /// Deliberately produces nothing else. A datum feature is the case this path was written for
    /// and never received, and mixing bodies in would let the body handling — which was already
    /// correct — carry the test.
    /// </remarks>
    private sealed class DatumEvaluator : IFeatureEvaluator
    {
        public FeatureOutput Evaluate(
            FeatureEvaluation evaluation, CancellationToken cancellationToken)
        {
            Feature feature = evaluation.Feature;
            double offset = feature.Parameters.FirstOrDefault()?.Value.Value ?? 0;

            // A count rather than always one, so a feature can be made to produce fewer datums than
            // it did last time -- which is the case the per-owner clear exists for and the one that
            // replacing by name cannot cover.
            int count = (int)(feature.SettingValues.TryGetValue("Count", out FeatureValue? value)
                && value is NumberValue number ? number.Value : 1);

            return new FeatureOutput(
                [],
                [
                    .. Enumerable.Range(0, count).Select(i => new ReferenceGeometry.Plane(
                        feature.Id,
                        i == 0 ? feature.Name : $"{feature.Name}.{i}",
                        new Vec3d(0, 0, offset + i),
                        Vec3d.UnitZ)),
                ],
                HistoryMap.Empty);
        }
    }
}
