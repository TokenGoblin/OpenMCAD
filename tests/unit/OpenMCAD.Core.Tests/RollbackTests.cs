using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Rebuild;
using OpenMCAD.Kernel;
using OpenMCAD.Kernel.Threading;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// The rollback bar (P3-T06).
/// </summary>
/// <remarks>
/// §5.4 predicted this would fall out of the design for free provided it was not special-cased, and
/// it did: being behind the bar is one more reason a feature is not evaluated, alongside being
/// suppressed and depending on something that failed. The propagation, the ordering and the skipping
/// were all already there. What it did need was for a feature that is not evaluated to give up its
/// geometry, which nothing had required until now — and which suppression had been getting wrong.
/// </remarks>
public sealed class RollbackTests
{
    [Fact]
    public void ANewDocumentIsNotRolledBack()
    {
        Document document = Document.Empty();

        document.RollbackPosition.Should().BeNull();
        document.IsRolledBack.Should().BeFalse();
    }

    [Fact]
    public void RollingBackHidesTheEndOfTheTree()
    {
        Harness harness = new();

        FeatureId first = harness.Add("First");
        FeatureId second = harness.Add("Second");
        FeatureId third = harness.Add("Third");

        harness.RollBackTo(2);

        harness.Session.Current.ActiveFeatures.Select(f => f.Name).Should().Equal(["First", "Second"]);

        harness.Session.Current.IsActive(first).Should().BeTrue();
        harness.Session.Current.IsActive(second).Should().BeTrue();
        harness.Session.Current.IsActive(third).Should().BeFalse();
    }

    [Fact]
    public void RollingForwardAgainRestoresEverything()
    {
        Harness harness = new();

        harness.Add("First");
        harness.Add("Second");

        harness.RollBackTo(1);
        harness.RollBackTo(null);

        harness.Session.Current.IsRolledBack.Should().BeFalse();
        harness.Session.Current.ActiveFeatures.Should().HaveCount(2);
    }

    [Fact]
    public void AddingAFeatureToADocumentThatWasNeverRolledBackDoesNotHideIt()
    {
        // Why the position is nullable rather than defaulting to the feature count. Stored as a
        // number, "not rolled back" would be whatever the length was when it was last written, and
        // the next feature added would appear behind the bar and be invisible.
        Harness harness = new();

        harness.Add("First");
        harness.Add("Second");

        harness.Session.Current.ActiveFeatures.Should().HaveCount(2);
        harness.Session.Current.IsRolledBack.Should().BeFalse();
    }

    [Fact]
    public void DeletingAFeatureAboveTheBarKeepsTheSameOnesActive()
    {
        // The bar is a position, so removing a feature above it shifts everything below up by one.
        // A bar left where it was would quietly roll back a feature that was visible a moment ago.
        Harness harness = new();

        FeatureId first = harness.Add("First");
        harness.Add("Second");
        harness.Add("Third");
        harness.Add("Fourth");

        harness.RollBackTo(3);
        harness.Session.Current.ActiveFeatures.Select(f => f.Name).Should().Equal(
            ["First", "Second", "Third"]);

        harness.Remove(first);

        harness.Session.Current.ActiveFeatures.Select(f => f.Name).Should().Equal(
            ["Second", "Third"], "the same features stay active as before the deletion");

        harness.Session.Current.RollbackPosition.Should().Be(2);
    }

    [Fact]
    public void TheBarCannotBeSetBeyondTheTree()
    {
        Harness harness = new();
        harness.Add("Only");

        Action tooFar = () => harness.RollBackTo(5);
        Action negative = () => harness.RollBackTo(-1);

        tooFar.Should().Throw<ArgumentOutOfRangeException>();
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ARolledBackFeatureIsNotEvaluated()
    {
        using RebuildHarness harness = new();

        FeatureId first = harness.Add("First");
        FeatureId second = harness.Add("Second");

        harness.RollBackTo(1);

        RebuildResult result = await harness.Engine.RebuildAllAsync();

        result.Rebuilt.Should().Equal([first]);
        result.Skipped.Should().Equal([second]);
        harness.Evaluator.Evaluated.Should().Equal([first]);
    }

    [Fact]
    public async Task ARolledBackFeatureGivesUpItsGeometry()
    {
        // The part the design did not already provide. Dragging the bar up the tree is how the user
        // looks at the part half-built, and a rolled-back extrude still showing its solid would make
        // that gesture show them nothing.
        using RebuildHarness harness = new();

        FeatureId first = harness.Add("First");
        FeatureId second = harness.Add("Second");

        await harness.Engine.RebuildAllAsync();
        harness.Session.Current.Bodies.Should().HaveCount(2);

        harness.RollBackTo(1);
        await harness.Engine.RebuildAllAsync();

        harness.Session.Current.BodiesOf(first).Should().ContainSingle();
        harness.Session.Current.BodiesOf(second).Should().BeEmpty();
        harness.Session.Current.Bodies.Should().ContainSingle();
    }

    [Fact]
    public async Task SuppressingAFeatureAlsoTakesItsGeometryAway()
    {
        // Found while writing the rollback tests: the same rule had to hold for suppression, and
        // did not. A suppressed feature was skipped but kept last time's bodies, so switching a
        // feature off left its solid on screen.
        using RebuildHarness harness = new();

        FeatureId id = harness.Add("Extrude1");

        await harness.Engine.RebuildAllAsync();
        harness.Session.Current.BodiesOf(id).Should().ContainSingle();

        harness.Suppress(id);
        await harness.Engine.RebuildAllAsync();

        harness.Session.Current.BodiesOf(id).Should().BeEmpty();
    }

    [Fact]
    public async Task WhatDependsOnARolledBackFeatureIsRolledBackToo()
    {
        // Not because the bar says so -- the dependent may be anywhere -- but because it consumes
        // something that is not there. The engine already had that rule for failures.
        using RebuildHarness harness = new();

        FeatureId sketch = harness.Add("Sketch1");
        FeatureId extrude = harness.Add("Extrude1", sketch);

        harness.Move(extrude, 0);
        harness.RollBackTo(1);

        RebuildResult result = await harness.Engine.RebuildAllAsync();

        // The extrude is now first in the tree and so is nominally active, but the sketch it
        // consumes is behind the bar.
        result.Skipped.Should().Contain(extrude);
        harness.Evaluator.Evaluated.Should().NotContain(extrude);
    }

    [Fact]
    public async Task MovingTheBarBackAndForwardCostsNothingTheSecondTime()
    {
        // What the geometry cache is for, and the reason scrubbing the bar feels instant: every
        // position the user drags back to is one the document has already been in, so every key is
        // one already computed.
        using RebuildHarness harness = new();

        harness.Add("First");
        harness.Add("Second");
        harness.Add("Third");

        await harness.Engine.RebuildAllAsync();

        harness.RollBackTo(1);
        await harness.Engine.RebuildAllAsync();

        harness.RollBackTo(null);
        RebuildResult forward = await harness.Engine.RebuildAllAsync();

        forward.Evaluated.Should().Be(
            0, "every feature has been built at this position before");

        forward.FromCache.Should().HaveCount(3);
    }

    /// <summary>A session, without the rebuild machinery.</summary>
    private sealed class Harness
    {
        public DocumentSession Session { get; } = new();

        public FeatureId Add(string name, params FeatureId[] inputs)
        {
            FeatureId id = FeatureId.New();

            using IDocumentTransaction transaction = Session.BeginTransaction($"Add {name}");
            transaction.AddFeature(Feature.Create(id, name, "Test") with { Inputs = [.. inputs] });
            transaction.Commit();

            return id;
        }

        public void Remove(FeatureId id)
        {
            using IDocumentTransaction transaction = Session.BeginTransaction("Delete");
            transaction.RemoveFeature(id);
            transaction.Commit();
        }

        public void RollBackTo(int? position)
        {
            using IDocumentTransaction transaction = Session.BeginTransaction("Roll back");
            transaction.SetRollbackPosition(position);
            transaction.Commit();
        }
    }

    /// <summary>The same, with a dispatcher, an evaluator and an engine.</summary>
    private sealed class RebuildHarness : IDisposable
    {
        private readonly KernelDispatcher _dispatcher = new("rollback test kernel");

        public RebuildHarness()
        {
            Session = new DocumentSession();
            Evaluator = new CountingEvaluator();
            Engine = new RebuildEngine(Session, _dispatcher, Evaluator);
        }

        public DocumentSession Session { get; }

        public CountingEvaluator Evaluator { get; }

        public RebuildEngine Engine { get; }

        public FeatureId Add(string name, params FeatureId[] inputs)
        {
            FeatureId id = FeatureId.New();

            using IDocumentTransaction transaction = Session.BeginTransaction($"Add {name}");
            transaction.AddFeature(Feature.Create(id, name, "Test") with { Inputs = [.. inputs] });
            transaction.Commit();

            return id;
        }

        public void RollBackTo(int? position)
        {
            using IDocumentTransaction transaction = Session.BeginTransaction("Roll back");
            transaction.SetRollbackPosition(position);
            transaction.Commit();
        }

        public void Suppress(FeatureId id)
        {
            using IDocumentTransaction transaction = Session.BeginTransaction("Suppress");
            transaction.ReplaceFeature(Session.Current.FindFeature(id)! with { IsSuppressed = true });
            transaction.Commit();
        }

        public void Move(FeatureId id, int index)
        {
            using IDocumentTransaction transaction = Session.BeginTransaction("Reorder");
            transaction.MoveFeature(id, index);
            transaction.Commit();
        }

        public void Dispose()
        {
            Engine.Dispose();
            _dispatcher.Dispose();
        }
    }

    private sealed class CountingEvaluator : IFeatureEvaluator
    {
        private readonly List<FeatureId> _evaluated = [];

        public IReadOnlyList<FeatureId> Evaluated
        {
            get
            {
                lock (_evaluated)
                {
                    return [.. _evaluated];
                }
            }
        }

        public FeatureOutput Evaluate(
            FeatureEvaluation evaluation, CancellationToken cancellationToken)
        {
            lock (_evaluated)
            {
                _evaluated.Add(evaluation.Feature.Id);
            }

            return FeatureOutput.Of(new Body(
                BodyId.New(), evaluation.Feature.Id, BodyKind.Solid, new KernelShape(1)));
        }
    }
}
