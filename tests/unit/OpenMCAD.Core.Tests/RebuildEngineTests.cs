using System.Collections.Concurrent;
using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Rebuild;
using OpenMCAD.Kernel;
using OpenMCAD.Kernel.Threading;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// The rebuild engine (P3-T04): what gets rebuilt, in what order, and what happens when the answer
/// stops being wanted partway through.
/// </summary>
public sealed class RebuildEngineTests
{
    [Fact]
    public async Task OnlyTheDirtySubgraphIsRebuilt()
    {
        using Harness harness = new(NullGeometryCache.Instance);

        FeatureId sketch = harness.Add("Sketch1");
        FeatureId extrude = harness.Add("Extrude1", sketch);
        FeatureId fillet = harness.Add("Fillet1", extrude);
        FeatureId unrelated = harness.Add("Unrelated");

        await harness.Engine.RebuildAllAsync();
        harness.Evaluator.Clear();

        RebuildResult result = await harness.Engine.RebuildAsync([extrude]);

        // Phase 3's second exit criterion, and the reason the graph exists: changing a parameter
        // rebuilds only what depends on it.
        result.Rebuilt.Should().Equal([extrude, fillet]);
        harness.Evaluator.Evaluated.Should().Equal([extrude, fillet]);

        harness.Evaluator.Evaluated.Should().NotContain(
            sketch, "nothing upstream of the edit changed");

        harness.Evaluator.Evaluated.Should().NotContain(
            unrelated, "an independent branch is not affected by an edit elsewhere");
    }

    [Fact]
    public async Task FeaturesAreEvaluatedAfterWhateverTheyConsume()
    {
        using Harness harness = new();

        FeatureId sketch = harness.Add("Sketch1");
        FeatureId extrude = harness.Add("Extrude1", sketch);
        FeatureId fillet = harness.Add("Fillet1", extrude);

        await harness.Engine.RebuildAllAsync();

        harness.Evaluator.Evaluated.Should().Equal([sketch, extrude, fillet]);
    }

    [Fact]
    public async Task AFeatureSeesTheBodiesItsInputsJustProduced()
    {
        using Harness harness = new();

        FeatureId first = harness.Add("First");
        FeatureId second = harness.Add("Second", first);

        await harness.Engine.RebuildAllAsync();

        // Not the bodies from the previous rebuild, and not none: the second feature must see what
        // the first produced during this same rebuild, or a chain of features never composes.
        harness.Evaluator.InputsSeen[second].Should().ContainSingle()
            .Which.Owner.Should().Be(first);
    }

    [Fact]
    public async Task ResultsAreWrittenBackToTheDocument()
    {
        using Harness harness = new();

        FeatureId extrude = harness.Add("Extrude1");

        RebuildResult result = await harness.Engine.RebuildAllAsync();

        result.Outcome.Should().Be(RebuildOutcome.Completed);
        harness.Session.Current.BodiesOf(extrude).Should().ContainSingle();
    }

    [Fact]
    public async Task AFeatureThatNowProducesFewerBodiesLeavesNoneBehind()
    {
        // Without a cache, because the change being made here is to how the evaluator behaves
        // rather than to the feature. Nothing about the feature differs between the two rebuilds,
        // so its key does not either, and a cache would correctly return the first answer -- which
        // would be testing the cache rather than the removal of stale bodies.
        using Harness harness = new(NullGeometryCache.Instance);

        FeatureId id = harness.Add("Splitter");

        harness.Evaluator.BodyCount[id] = 3;
        await harness.Engine.RebuildAllAsync();
        harness.Session.Current.BodiesOf(id).Should().HaveCount(3);

        harness.Evaluator.BodyCount[id] = 1;
        await harness.Engine.RebuildAllAsync();

        // The ids need not match between runs, so anything not re-produced has to go. Left behind,
        // it would be a body whose geometry belongs to a version of the feature that no longer
        // exists, and nothing downstream could tell the difference.
        harness.Session.Current.BodiesOf(id).Should().ContainSingle();
        harness.Session.Current.Bodies.Should().ContainSingle();
    }

    [Fact]
    public async Task AFailingFeatureDoesNotTakeTheRebuildWithIt()
    {
        using Harness harness = new();

        FeatureId good = harness.Add("Good");
        FeatureId bad = harness.Add("Bad");
        FeatureId downstream = harness.Add("Downstream", bad);
        FeatureId alsoGood = harness.Add("AlsoGood");

        harness.Evaluator.Fail(bad);

        RebuildResult result = await harness.Engine.RebuildAllAsync();

        // A feature is arbitrary code -- a plugin's, in the general case. One badly written
        // operation must not make the whole document unrebuildable.
        result.Outcome.Should().Be(RebuildOutcome.Completed);
        result.Failed.Should().Equal([bad]);

        result.Skipped.Should().Equal(
            [downstream], "what depended on the failure cannot be attempted");

        result.Rebuilt.Should().BeEquivalentTo(
            [good, alsoGood], "independent branches carry on");

        result.IsClean.Should().BeFalse();
    }

    [Fact]
    public async Task SuppressedFeaturesAndTheirDependentsAreSkipped()
    {
        using Harness harness = new();

        FeatureId off = harness.Add("Suppressed");
        FeatureId downstream = harness.Add("Downstream", off);

        harness.Suppress(off);

        RebuildResult result = await harness.Engine.RebuildAllAsync();

        result.Skipped.Should().BeEquivalentTo([off, downstream]);
        harness.Evaluator.Evaluated.Should().BeEmpty();
    }

    [Fact]
    public async Task ANewerRequestSupersedesTheOneRunning()
    {
        using Harness harness = new();

        FeatureId first = harness.Add("First");
        FeatureId second = harness.Add("Second");

        using ManualResetEventSlim reached = new(false);
        using ManualResetEventSlim release = new(false);

        harness.Evaluator.Block(first, reached, release);

        Task<RebuildResult> running = harness.Engine.RebuildAsync([first, second]);

        reached.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        // The second request cancels the first, which is what stops a dimension drag from queueing
        // one rebuild per mouse move and running every one of them.
        Task<RebuildResult> superseding = harness.Engine.RebuildAsync([second]);

        release.Set();

        RebuildResult superseded = await running;
        RebuildResult winner = await superseding;

        superseded.Outcome.Should().Be(RebuildOutcome.Superseded);
        winner.Outcome.Should().Be(RebuildOutcome.Completed);
    }

    [Fact]
    public async Task ASupersededRebuildPublishesNothing()
    {
        using Harness harness = new();

        FeatureId first = harness.Add("First");
        FeatureId second = harness.Add("Second");

        using ManualResetEventSlim reached = new(false);
        using ManualResetEventSlim release = new(false);

        harness.Evaluator.Block(second, reached, release);

        Task<RebuildResult> running = harness.Engine.RebuildAsync([first, second]);
        reached.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        Task<RebuildResult> superseding = harness.Engine.RebuildAsync([]);
        release.Set();

        RebuildResult superseded = await running;
        await superseding;

        superseded.Outcome.Should().Be(RebuildOutcome.Superseded);

        // The first feature had already been evaluated when the rebuild was cancelled. Publishing
        // that alone would leave the document holding new geometry for one feature and old for the
        // rest -- a state the model was never in and nothing downstream could interpret.
        harness.Session.Current.Bodies.Should().BeEmpty();
    }

    [Fact]
    public async Task CancellingIsReportedAsCancelledRatherThanSuperseded()
    {
        using Harness harness = new();

        FeatureId first = harness.Add("First");
        harness.Add("Second");

        using ManualResetEventSlim reached = new(false);
        using ManualResetEventSlim release = new(false);
        using CancellationTokenSource cancel = new();

        harness.Evaluator.Block(first, reached, release);

        Task<RebuildResult> running = harness.Engine.RebuildAsync(
            harness.AllFeatures, KernelPriority.Rebuild, cancel.Token);

        reached.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        await cancel.CancelAsync();
        release.Set();

        RebuildResult result = await running;

        // The two are told apart because they mean different things to the user: one is a rebuild
        // that stopped because they asked, the other is one that stopped because they carried on.
        result.Outcome.Should().Be(RebuildOutcome.Cancelled);
    }

    [Fact]
    public async Task RebuildingWithNothingDirtyDoesNothing()
    {
        using Harness harness = new();
        harness.Add("Extrude1");

        RebuildResult result = await harness.Engine.RebuildAsync([]);

        result.Outcome.Should().Be(RebuildOutcome.NothingToDo);
        result.IsClean.Should().BeTrue();
        harness.Evaluator.Evaluated.Should().BeEmpty();
    }

    [Fact]
    public async Task EveryEvaluationRunsOnTheKernelThread()
    {
        // ADR-0004: all kernel calls are marshalled onto one thread. The engine is what does the
        // marshalling for a rebuild, and an evaluator called on the caller's thread would be
        // making native calls from wherever the edit happened to come from.
        using Harness harness = new();

        harness.Add("First");
        harness.Add("Second");
        harness.Add("Third");

        await harness.Engine.RebuildAllAsync();

        harness.Evaluator.Threads.Should().HaveCount(
            1, "every evaluation belongs on the one kernel thread");

        harness.Evaluator.Threads.Single().Should().NotBe(Environment.CurrentManagedThreadId);
    }

    [Fact]
    public async Task CommittingAnEditRebuildsWhatItMadeStale()
    {
        using Harness harness = new();

        FeatureId extrude = harness.Add("Extrude1");

        using ManualResetEventSlim rebuilt = new(false);
        harness.Engine.Finished += _ => rebuilt.Set();
        harness.Engine.RebuildOnCommit();

        using (IDocumentTransaction transaction = harness.Session.BeginTransaction("Edit"))
        {
            transaction.ReplaceFeature(
                harness.Session.Current.FindFeature(extrude)! with { Name = "Renamed" });

            transaction.Commit();
        }

        rebuilt.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        harness.Evaluator.Evaluated.Should().Contain(extrude);
    }

    [Fact]
    public async Task PublishingARebuildDoesNotTriggerAnotherOne()
    {
        // The engine's own writes come back to it through the same event. They must not start a
        // rebuild, or the two would feed each other indefinitely. What stops it is that P3-T02
        // does not record a body as a dirty feature.
        using Harness harness = new();

        harness.Add("Extrude1");
        harness.Engine.RebuildOnCommit();

        int finishes = 0;
        using ManualResetEventSlim first = new(false);

        harness.Engine.Finished += _ =>
        {
            Interlocked.Increment(ref finishes);
            first.Set();
        };

        await harness.Engine.RebuildAllAsync();

        first.Wait(TimeSpan.FromSeconds(5));
        await Task.Delay(250);

        finishes.Should().Be(1, "the rebuild's own commit must not start a second rebuild");
    }

    [Fact]
    public async Task RebuildingAnUnchangedDocumentReachesTheKernelOnce()
    {
        using Harness harness = new();

        harness.Add("Sketch1");
        harness.Add("Extrude1");

        RebuildResult first = await harness.Engine.RebuildAllAsync();
        RebuildResult second = await harness.Engine.RebuildAllAsync();

        first.FromCache.Should().BeEmpty("nothing was remembered yet");
        first.Evaluated.Should().Be(2);

        // The second rebuild still reports both features as rebuilt -- from the document's point of
        // view they were -- but neither reached the kernel. This is the instrumentation Phase 3's
        // second exit criterion asks for.
        second.Rebuilt.Should().HaveCount(2);
        second.FromCache.Should().HaveCount(2);
        second.Evaluated.Should().Be(0);

        harness.Evaluator.Evaluated.Should().HaveCount(2, "the second rebuild called nothing");
    }

    [Fact]
    public async Task ChangingAParameterMissesForThatFeatureAndEverythingBelowIt()
    {
        using Harness harness = new();

        FeatureId sketch = harness.Add("Sketch1");
        FeatureId extrude = harness.Add("Extrude1", sketch);
        FeatureId fillet = harness.Add("Fillet1", extrude);

        await harness.Engine.RebuildAllAsync();

        harness.SetParameter(extrude, new Parameter("Depth", Quantity.Metres(0.02)));

        RebuildResult result = await harness.Engine.RebuildAllAsync();

        // The sketch is untouched and hits. The extrude changed, so it misses -- and the fillet
        // misses too, without anything having changed about the fillet itself, because its key
        // folds in what the extrude produced. That chaining is the whole reason the key is a
        // Merkle chain rather than a hash of the feature alone.
        result.FromCache.Should().Equal([sketch]);
        result.Evaluated.Should().Be(2);

        harness.Evaluator.Evaluated.Should().Equal(
            [sketch, extrude, fillet, extrude, fillet],
            "three the first time, then the two that depend on the change");
    }

    [Fact]
    public async Task UndoingAChangeCostsNothing()
    {
        // What the cache is for. Undo returns the document to a state it has already been in, so
        // every key is one already computed and every feature hits -- which is why undo is instant
        // rather than a full rebuild in reverse.
        using Harness harness = new();

        FeatureId extrude = harness.Add("Extrude1");

        harness.SetParameter(extrude, new Parameter("Depth", Quantity.Metres(0.01)));
        await harness.Engine.RebuildAllAsync();

        harness.SetParameter(extrude, new Parameter("Depth", Quantity.Metres(0.02)));
        await harness.Engine.RebuildAllAsync();

        harness.SetParameter(extrude, new Parameter("Depth", Quantity.Metres(0.01)));
        RebuildResult undone = await harness.Engine.RebuildAllAsync();

        undone.FromCache.Should().Equal([extrude]);
        undone.Evaluated.Should().Be(0);
    }

    [Fact]
    public async Task WithoutACacheEveryRebuildReachesTheKernel()
    {
        // Phase 3's fifth exit criterion is that --no-cache produces identical results. The mode is
        // the same engine with a cache that never hits, so that the comparison is evidence about
        // the ordinary path rather than about a second implementation of it.
        using Harness harness = new(NullGeometryCache.Instance);

        harness.Add("Sketch1");
        harness.Add("Extrude1");

        await harness.Engine.RebuildAllAsync();
        RebuildResult second = await harness.Engine.RebuildAllAsync();

        second.FromCache.Should().BeEmpty();
        second.Evaluated.Should().Be(2);
        harness.Evaluator.Evaluated.Should().HaveCount(4);
    }

    /// <summary>A session, a real dispatcher and a recording evaluator.</summary>
    private sealed class Harness : IDisposable
    {
        /// <summary>Creates the harness.</summary>
        /// <param name="cache">
        /// Where results are remembered. Several tests below pass the null cache, and not to keep
        /// things simple: they assert on what the evaluator was asked to do, and a cache hit means
        /// it is asked to do nothing. Those tests are about ordering and propagation, so they run
        /// the engine in the mode where every feature reaches the evaluator -- which is also the
        /// mode P3-T05 has to provide anyway.
        /// </param>
        public Harness(IGeometryCache? cache = null)
        {
            Dispatcher = new KernelDispatcher("rebuild test kernel");
            Session = new DocumentSession();
            Evaluator = new RecordingEvaluator();
            Engine = new RebuildEngine(Session, Dispatcher, Evaluator, cache ?? new GeometryCache());
        }

        public DocumentSession Session { get; }

        public KernelDispatcher Dispatcher { get; }

        public RecordingEvaluator Evaluator { get; }

        public RebuildEngine Engine { get; }

        public ImmutableArray<FeatureId> AllFeatures
            => [.. Session.Current.Features.Select(f => f.Id)];

        public FeatureId Add(string name, params FeatureId[] inputs)
        {
            FeatureId id = FeatureId.New();

            using IDocumentTransaction transaction = Session.BeginTransaction($"Add {name}");

            transaction.AddFeature(
                Feature.Create(id, name, "Test") with { Inputs = [.. inputs] });

            transaction.Commit();

            return id;
        }

        public void SetParameter(FeatureId id, Parameter parameter)
        {
            using IDocumentTransaction transaction = Session.BeginTransaction("Set parameter");

            transaction.ReplaceFeature(
                Session.Current.FindFeature(id)! with { Parameters = [parameter] });

            transaction.Commit();
        }

        public void Suppress(FeatureId id)
        {
            using IDocumentTransaction transaction = Session.BeginTransaction("Suppress");

            transaction.ReplaceFeature(
                Session.Current.FindFeature(id)! with { IsSuppressed = true });

            transaction.Commit();
        }

        public void Dispose()
        {
            Engine.Dispose();
            Dispatcher.Dispose();
        }
    }

    /// <summary>Stands in for OpenMCAD.Modeling, which does not exist yet.</summary>
    private sealed class RecordingEvaluator : IFeatureEvaluator
    {
        private readonly ConcurrentQueue<FeatureId> _evaluated = new();
        private readonly HashSet<FeatureId> _failing = [];
        private readonly Dictionary<FeatureId, (ManualResetEventSlim Reached, ManualResetEventSlim Release)> _blocks = [];

        public ImmutableArray<FeatureId> Evaluated => [.. _evaluated];

        public Dictionary<FeatureId, ImmutableArray<Body>> InputsSeen { get; } = [];

        public Dictionary<FeatureId, int> BodyCount { get; } = [];

        public HashSet<int> Threads { get; } = [];

        public void Clear()
        {
            _evaluated.Clear();
            InputsSeen.Clear();
        }

        public void Fail(FeatureId id) => _failing.Add(id);

        public void Block(FeatureId id, ManualResetEventSlim reached, ManualResetEventSlim release)
            => _blocks[id] = (reached, release);

        public FeatureOutput Evaluate(
            FeatureEvaluation evaluation, CancellationToken cancellationToken)
        {
            FeatureId id = evaluation.Feature.Id;

            _evaluated.Enqueue(id);
            InputsSeen[id] = evaluation.Inputs;

            lock (Threads)
            {
                Threads.Add(Environment.CurrentManagedThreadId);
            }

            if (_blocks.TryGetValue(id, out var block))
            {
                block.Reached.Set();

                // Deliberately not passed the token. A native kernel operation cannot be
                // interrupted partway through, so this models one that runs to completion and
                // only then finds that its result is no longer wanted -- which is what "cancels
                // at the next operation boundary" actually means.
                block.Release.Wait(TimeSpan.FromSeconds(30), CancellationToken.None);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (_failing.Contains(id))
            {
                throw new InvalidOperationException($"'{evaluation.Feature.Name}' cannot build.");
            }

            int count = BodyCount.TryGetValue(id, out int wanted) ? wanted : 1;

            return new FeatureOutput(
                [.. Enumerable.Range(0, count).Select(
                    i => new Body(BodyId.New(), id, BodyKind.Solid, new KernelShape((ulong)(i + 1))))],
                []);
        }
    }
}
