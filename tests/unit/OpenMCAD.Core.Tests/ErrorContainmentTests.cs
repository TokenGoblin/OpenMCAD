using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Rebuild;
using OpenMCAD.Kernel;
using OpenMCAD.Kernel.Threading;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// Error containment (P3-T07): a failed feature is marked, its consequences are marked as
/// consequences, and everything else carries on.
/// </summary>
/// <remarks>
/// The substance here is the distinctions. "Did not build" is easy and useless: a document where a
/// sketch failed can leave twenty features unbuilt, and presenting them as twenty equal problems
/// sends the user to look at whichever is nearest the top. One of them is the problem and the rest
/// are its shadow, and the tests below are mostly about keeping those apart.
/// </remarks>
public sealed class ErrorContainmentTests
{
    [Fact]
    public async Task AFailedFeatureIsMarkedAndItsConsequencesAreMarkedAsConsequences()
    {
        using Harness harness = new();

        FeatureId sketch = harness.Add("Sketch1");
        FeatureId extrude = harness.Add("Extrude1", sketch);
        FeatureId fillet = harness.Add("Fillet1", extrude);
        FeatureId unrelated = harness.Add("Unrelated");

        harness.Evaluator.Fail(extrude, "the profile is not closed");

        await harness.Engine.RebuildAllAsync();

        RebuildReport report = harness.Session.Current.Report;

        report.StateOf(extrude).Should().Be(FeatureState.Failed);
        report.For(extrude)!.Message.Should().Contain("profile is not closed");

        report.StateOf(fillet).Should().Be(FeatureState.SuppressedByError);
        report.StateOf(sketch).Should().Be(FeatureState.Ok);
        report.StateOf(unrelated).Should().Be(FeatureState.Ok);

        // One problem, not two. The fillet is the same failure counted again, and listing it
        // alongside the extrude is what makes a tree of red marks with a single cause.
        report.Errors.Should().ContainSingle().Which.Feature.Should().Be(extrude);
        report.HasErrors.Should().BeTrue();
    }

    [Fact]
    public async Task AConsequenceNamesTheFeatureThatActuallyFailed()
    {
        // Down a chain of five, every one of which is unbuilt. The user needs to be sent to the
        // one at the top, not to whichever of the four they happen to click on.
        using Harness harness = new();

        FeatureId first = harness.Add("First");
        FeatureId second = harness.Add("Second", first);
        FeatureId third = harness.Add("Third", second);
        FeatureId fourth = harness.Add("Fourth", third);

        harness.Evaluator.Fail(first, "no");

        await harness.Engine.RebuildAllAsync();

        RebuildReport report = harness.Session.Current.Report;

        report.For(second)!.Cause.Should().Be(first);
        report.For(third)!.Cause.Should().Be(first, "the cause is carried through, not restated");
        report.For(fourth)!.Cause.Should().Be(first);
    }

    [Fact]
    public async Task ASuppressedFeatureIsNotAnError()
    {
        using Harness harness = new();

        FeatureId off = harness.Add("Suppressed");
        FeatureId downstream = harness.Add("Downstream", off);

        harness.Suppress(off);

        await harness.Engine.RebuildAllAsync();

        RebuildReport report = harness.Session.Current.Report;

        report.StateOf(off).Should().Be(FeatureState.Suppressed);

        // The consequence of a deliberate choice, not of a failure. Nothing has gone wrong: the
        // user asked for the thing this depends on to be absent, and this is what they asked for.
        report.StateOf(downstream).Should().Be(FeatureState.Blocked);

        report.HasErrors.Should().BeFalse("switching a feature off is not a problem to be fixed");
        report.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task ARolledBackFeatureIsNotAnError()
    {
        using Harness harness = new();

        FeatureId first = harness.Add("First");
        FeatureId second = harness.Add("Second", first);

        harness.RollBackTo(1);

        await harness.Engine.RebuildAllAsync();

        RebuildReport report = harness.Session.Current.Report;

        report.StateOf(second).Should().Be(FeatureState.RolledBack);
        report.HasErrors.Should().BeFalse();
    }

    [Fact]
    public async Task ADanglingReferenceIsAnErrorAgainstTheFeatureHoldingIt()
    {
        // The other half of P3-T03's dangling report, which until now was computed and discarded.
        // Deleting a feature that something else consumed is a reasonable thing to have done, and
        // it leaves a reasonable question behind for the feature left pointing at nothing.
        using Harness harness = new();

        FeatureId sketch = harness.Add("Sketch1");
        FeatureId extrude = harness.Add("Extrude1", sketch);

        harness.Remove(sketch);

        await harness.Engine.RebuildAllAsync();

        RebuildReport report = harness.Session.Current.Report;

        report.StateOf(extrude).Should().Be(FeatureState.MissingInput);
        report.For(extrude)!.Message.Should().Contain("no longer in the document");
        report.Errors.Should().ContainSingle().Which.Feature.Should().Be(extrude);
    }

    [Fact]
    public async Task AFailedFeatureDoesNotLeaveItsOldGeometryOnScreen()
    {
        using Harness harness = new();

        FeatureId id = harness.Add("Extrude1");

        await harness.Engine.RebuildAllAsync();
        harness.Session.Current.BodiesOf(id).Should().ContainSingle();

        harness.Evaluator.Fail(id, "cannot build");
        harness.SetParameter(id, new Parameter("Depth", Quantity.Metres(0.02)));

        await harness.Engine.RebuildAllAsync();

        // Otherwise the user is looking at a solid the current parameters do not produce, marked
        // with an error they may well not look at.
        harness.Session.Current.BodiesOf(id).Should().BeEmpty();
        harness.Session.Current.Report.StateOf(id).Should().Be(FeatureState.Failed);
    }

    [Fact]
    public async Task FixingTheCauseClearsTheConsequences()
    {
        using Harness harness = new();

        FeatureId extrude = harness.Add("Extrude1");
        FeatureId fillet = harness.Add("Fillet1", extrude);

        harness.Evaluator.Fail(extrude, "cannot build");
        await harness.Engine.RebuildAllAsync();

        harness.Session.Current.Report.HasErrors.Should().BeTrue();

        harness.Evaluator.Succeed(extrude);
        harness.SetParameter(extrude, new Parameter("Depth", Quantity.Metres(0.05)));

        await harness.Engine.RebuildAllAsync();

        RebuildReport report = harness.Session.Current.Report;

        report.StateOf(extrude).Should().Be(FeatureState.Ok);
        report.StateOf(fillet).Should().Be(FeatureState.Ok, "one fix clears the whole shadow");
        report.HasErrors.Should().BeFalse();
    }

    [Fact]
    public async Task EditingSomethingElseDoesNotClearAnExistingError()
    {
        // A partial rebuild says nothing about features outside its dirty subgraph, and "nothing"
        // is not "fine". Dropping their diagnostics would wipe the error marks off still-broken
        // features every time the user touched an unrelated branch.
        using Harness harness = new();

        FeatureId broken = harness.Add("Broken");
        FeatureId elsewhere = harness.Add("Elsewhere");

        harness.Evaluator.Fail(broken, "cannot build");
        await harness.Engine.RebuildAllAsync();

        harness.Session.Current.Report.StateOf(broken).Should().Be(FeatureState.Failed);

        harness.SetParameter(elsewhere, new Parameter("Depth", Quantity.Metres(0.03)));
        await harness.Engine.RebuildAsync([elsewhere]);

        harness.Session.Current.Report.StateOf(broken).Should().Be(
            FeatureState.Failed, "it is still broken, and nothing about it was re-examined");

        harness.Session.Current.Report.StateOf(elsewhere).Should().Be(FeatureState.Ok);
    }

    [Fact]
    public async Task UndoingRestoresTheReportThatBelongsToTheStateItRestored()
    {
        // Why the report lives on the document rather than beside it. Undo is a matter of holding
        // an earlier reference, and an error list kept somewhere else would still be describing a
        // version of the model that no longer exists.
        using Harness harness = new();

        FeatureId id = harness.Add("Extrude1");

        await harness.Engine.RebuildAllAsync();
        Document good = harness.Session.Current;

        harness.Evaluator.Fail(id, "cannot build");
        harness.SetParameter(id, new Parameter("Depth", Quantity.Metres(0.02)));
        await harness.Engine.RebuildAllAsync();

        harness.Session.Current.Report.HasErrors.Should().BeTrue();

        // The document from before the failure carries the report from before the failure.
        good.Report.HasErrors.Should().BeFalse();
        good.Report.StateOf(id).Should().Be(FeatureState.Ok);
    }

    [Fact]
    public async Task IndependentBranchesStillBuild()
    {
        using Harness harness = new();

        FeatureId bad = harness.Add("Bad");
        harness.Add("AlsoBad", bad);

        FeatureId good = harness.Add("Good");
        FeatureId alsoGood = harness.Add("AlsoGood", good);

        harness.Evaluator.Fail(bad, "cannot build");

        await harness.Engine.RebuildAllAsync();

        // §5.4: a failed feature does not abort the rebuild. The difference between one broken
        // feature and a document that will not open.
        harness.Session.Current.BodiesOf(good).Should().ContainSingle();
        harness.Session.Current.BodiesOf(alsoGood).Should().ContainSingle();
        harness.Session.Current.Report.StateOf(alsoGood).Should().Be(FeatureState.Ok);
    }

    [Fact]
    public void ADocumentThatHasNeverBeenRebuiltReportsNothing()
    {
        Document document = Document.Empty();

        document.Report.Should().BeSameAs(RebuildReport.Empty);
        document.Report.HasErrors.Should().BeFalse();
        document.Report.StateOf(FeatureId.New()).Should().Be(FeatureState.Ok);
    }

    private sealed class Harness : IDisposable
    {
        private readonly KernelDispatcher _dispatcher = new("error test kernel");

        public Harness()
        {
            Session = new DocumentSession();
            Evaluator = new FailingEvaluator();
            Engine = new RebuildEngine(Session, _dispatcher, Evaluator);
        }

        public DocumentSession Session { get; }

        public FailingEvaluator Evaluator { get; }

        public RebuildEngine Engine { get; }

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

        public void Suppress(FeatureId id)
        {
            using IDocumentTransaction transaction = Session.BeginTransaction("Suppress");
            transaction.ReplaceFeature(Session.Current.FindFeature(id)! with { IsSuppressed = true });
            transaction.Commit();
        }

        public void RollBackTo(int? position)
        {
            using IDocumentTransaction transaction = Session.BeginTransaction("Roll back");
            transaction.SetRollbackPosition(position);
            transaction.Commit();
        }

        public void SetParameter(FeatureId id, Parameter parameter)
        {
            using IDocumentTransaction transaction = Session.BeginTransaction("Set parameter");
            transaction.ReplaceFeature(
                Session.Current.FindFeature(id)! with { Parameters = [parameter] });
            transaction.Commit();
        }

        public void Dispose()
        {
            Engine.Dispose();
            _dispatcher.Dispose();
        }
    }

    private sealed class FailingEvaluator : IFeatureEvaluator
    {
        private readonly Dictionary<FeatureId, string> _failures = [];

        public void Fail(FeatureId id, string why) => _failures[id] = why;

        public void Succeed(FeatureId id) => _failures.Remove(id);

        public FeatureOutput Evaluate(
            FeatureEvaluation evaluation, CancellationToken cancellationToken)
        {
            if (_failures.TryGetValue(evaluation.Feature.Id, out string? why))
            {
                throw new InvalidOperationException(why);
            }

            return FeatureOutput.Of(new Body(
                BodyId.New(), evaluation.Feature.Id, BodyKind.Solid, new KernelShape(1)));
        }
    }
}
