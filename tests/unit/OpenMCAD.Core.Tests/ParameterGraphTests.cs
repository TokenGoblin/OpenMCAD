using FluentAssertions;

using OpenMCAD.Core.Documents;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// The parameter dependency graph, folded into the rebuild DAG (P3-T16).
/// </summary>
/// <remarks>
/// §5.5 puts this graph inside the rebuild DAG rather than beside it, because a parameter change
/// reaches geometry two ways: a parameter defined from another has to be recomputed, and a feature
/// whose own values come out different has to be rebuilt. Both routes are tested here, because
/// either alone leaves a document whose geometry disagrees with its own numbers.
/// </remarks>
public sealed class ParameterGraphTests
{
    [Fact]
    public void AParameterIsRecomputedWhenWhatItIsDefinedFromChanges()
    {
        Session session = new();

        session.SetParameter("Width", Unit.Millimetres.Of(100));
        session.SetFormula("Half", "Width / 2");

        session.Value("Half").Should().BeApproximately(50, 1e-9);

        session.SetParameter("Width", Unit.Millimetres.Of(80));

        session.Value("Half").Should().BeApproximately(
            40, 1e-9, "the stored value has to agree with the formula that produced it");
    }

    [Fact]
    public void AChainOfDefinitionsIsRecomputedInOrder()
    {
        Session session = new();

        session.SetParameter("Width", Unit.Millimetres.Of(100));
        session.SetFormula("Half", "Width / 2");
        session.SetFormula("Quarter", "Half / 2");
        session.SetFormula("Eighth", "Quarter / 2");

        session.SetParameter("Width", Unit.Millimetres.Of(80));

        // If the order were wrong, Eighth would be computed from the previous Quarter and come out
        // stale by exactly one edit -- which looks right until someone checks the arithmetic.
        session.Value("Eighth").Should().BeApproximately(10, 1e-9);
    }

    [Fact]
    public void ChangingAParameterSeedsEverythingDefinedFromIt()
    {
        Session session = new();

        session.SetParameter("Width", Unit.Millimetres.Of(100));
        session.SetFormula("Half", "Width / 2");
        session.SetFormula("Unrelated", "1 + 1");

        DocumentChange change = session.SetParameter("Width", Unit.Millimetres.Of(80));

        change.TouchedParameters.Should().Contain("Width").And.Contain("Half");
        change.TouchedParameters.Should().NotContain("Unrelated");
    }

    [Fact]
    public void AFeatureThatNamesAParameterIsRebuiltWhenItMoves()
    {
        // The second route, and the one that makes this part of the rebuild DAG rather than a
        // thing beside it. A feature whose depth is Thickness * 2 is as dirty when Thickness moves
        // as if the user had edited the feature itself.
        Session session = new();

        session.SetParameter("Thickness", Unit.Millimetres.Of(5));

        FeatureId extrude = session.AddFeature("Extrude1", "Depth", "Thickness * 2");
        FeatureId unrelated = session.AddFeature("Extrude2", "Depth", "10mm");

        DocumentChange change = session.SetParameter("Thickness", Unit.Millimetres.Of(8));

        change.TouchedFeatures.Should().Contain(extrude);
        change.TouchedFeatures.Should().NotContain(unrelated);

        // And its own value was recomputed, not merely marked stale.
        session.FeatureValue(extrude, "Depth").Should().BeApproximately(16, 1e-9);
    }

    [Fact]
    public void AFeatureWhoseValueDidNotMoveIsNotRebuilt()
    {
        // The other half of the rule, and the reason a feature is seeded on its value changing
        // rather than on naming a changed parameter. A depth of min(Thickness, 5mm) does not move
        // when Thickness goes from 8mm to 9mm: the feature's inputs to the kernel are identical
        // and rebuilding it would compute the same solid again.
        Session session = new();

        session.SetParameter("Thickness", Unit.Millimetres.Of(8));

        FeatureId clamped = session.AddFeature("Extrude1", "Depth", "min(Thickness, 5mm)");

        session.FeatureValue(clamped, "Depth").Should().BeApproximately(5, 1e-9);

        DocumentChange change = session.SetParameter("Thickness", Unit.Millimetres.Of(9));

        change.TouchedParameters.Should().Contain("Thickness");
        change.TouchedFeatures.Should().NotContain(clamped);
        session.FeatureValue(clamped, "Depth").Should().BeApproximately(5, 1e-9);
    }

    [Fact]
    public void AFeatureValueIsComputedWhenTheFeatureIsAdded()
    {
        Session session = new();

        session.SetParameter("Thickness", Unit.Millimetres.Of(5));

        FeatureId id = session.AddFeature("Extrude1", "Depth", "Thickness * 3");

        session.FeatureValue(id, "Depth").Should().BeApproximately(
            15, 1e-9, "a formula typed into a new feature has to be worth something immediately");
    }

    [Fact]
    public void ALoopIsRejectedAtCommitAndNamed()
    {
        // §5.5 asks for exactly this: rejected at commit, with the cycle named. A loop has no value
        // to compute, and the only useful thing to say is which definitions are in it.
        Session session = new();

        session.SetFormula("Width", "1mm");
        session.SetFormula("Height", "Width * 2");

        Action loop = () => session.SetFormula("Width", "Height / 2");

        ParameterCycleException thrown = loop.Should().Throw<ParameterCycleException>().Which;

        thrown.Cycle.Should().BeEquivalentTo(["Width", "Height"]);
        thrown.Message.Should().Contain("Width").And.Contain("Height");
        thrown.Message.Should().MatchRegex(@"(Width|Height).*->.*->.*\1");
    }

    [Fact]
    public void AParameterDefinedFromItselfIsALoopOfOne()
    {
        Session session = new();
        session.SetParameter("Width", Unit.Millimetres.Of(10));

        Action loop = () => session.SetFormula("Width", "Width + 1mm");

        loop.Should().Throw<ParameterCycleException>()
            .Which.Cycle.Should().Equal(["Width"]);
    }

    [Fact]
    public void ARejectedLoopLeavesTheDocumentAsItWas()
    {
        // The commit did not happen, so nothing about it did. A document that had been half
        // updated by a rejected edit would be worse than one that refused the edit.
        Session session = new();

        session.SetFormula("Width", "1mm");
        session.SetFormula("Height", "Width * 2");

        double before = session.Value("Height");

        Action loop = () => session.SetFormula("Width", "Height / 2");
        loop.Should().Throw<ParameterCycleException>();

        session.Value("Height").Should().Be(before);
        session.Value("Width").Should().BeApproximately(1, 1e-9);
    }

    [Fact]
    public void OnlyTheLoopIsNamedAndNotEverythingItSpoiled()
    {
        Session session = new();

        session.SetFormula("A", "1mm");
        session.SetFormula("B", "A * 2");

        for (int i = 0; i < 6; ++i)
        {
            session.SetFormula($"Downstream{i}", "A + 1mm");
        }

        Action loop = () => session.SetFormula("A", "B / 2");

        loop.Should().Throw<ParameterCycleException>()
            .Which.Cycle.Should().HaveCount(2, "only the loop itself is the loop");
    }

    [Fact]
    public void TheOrderIsTheSameOnEveryRun()
    {
        // A document holds its parameters in a dictionary, which has no order to inherit, so
        // something has to be chosen. A rebuild that evaluated them differently each run would
        // make a cache key and a bug report stop meaning anything.
        Session session = new();

        foreach (string name in new[] { "Zulu", "Alpha", "Mike", "Bravo", "Yankee" })
        {
            session.SetParameter(name, Unit.Millimetres.Of(1));
        }

        ParameterGraph graph = ParameterGraph.Build(session.Document);

        graph.EvaluationOrder.Should().Equal(["Alpha", "Bravo", "Mike", "Yankee", "Zulu"]);

        for (int i = 0; i < 10; ++i)
        {
            ParameterGraph.Build(session.Document).EvaluationOrder
                .Should().Equal(graph.EvaluationOrder);
        }
    }

    [Fact]
    public void ADefinitionIsAlwaysOrderedAfterWhatItIsDefinedFrom()
    {
        Session session = new();

        // Named so that alphabetical order and dependency order disagree.
        session.SetParameter("Zebra", Unit.Millimetres.Of(100));
        session.SetFormula("Apple", "Zebra / 2");

        ParameterGraph graph = ParameterGraph.Build(session.Document);

        graph.EvaluationOrder.Should().Equal(["Zebra", "Apple"]);
        graph.DependenciesOf("Apple").Should().Equal(["Zebra"]);
        graph.DependentsOf("Zebra").Should().Equal(["Apple"]);
    }

    [Fact]
    public void AFormulaThatCannotBeEvaluatedKeepsItsLastKnownValue()
    {
        // Why a Parameter stores both the formula and the result. A cross-document reference
        // cannot be resolved while nothing can open the other document, and losing the number
        // would leave a hole where a dimension should be.
        Session session = new();

        session.SetParameter("Width", Unit.Millimetres.Of(100));

        ParameterEvaluation evaluated = ParameterGraph.Reevaluate(
            session.Document.WithParameter(
                new Parameter("Outer", Unit.Millimetres.Of(42), "Chassis:Width + 2mm")));

        evaluated.Unresolved.Should().ContainSingle().Which.Name.Should().Be("Outer");

        evaluated.Document.FindParameter("Outer")!.Value.Value
            .Should().Be(Unit.Millimetres.Of(42).Value);
    }

    [Fact]
    public void ACrossDocumentReferenceIsNotAnEdgeInThisGraph()
    {
        // Whatever it names lives in another document's graph and cannot take part in a loop here.
        Session session = new();

        session.SetParameter("Width", Unit.Millimetres.Of(100));

        Document document = session.Document.WithParameter(
            new Parameter("Outer", Unit.Millimetres.Of(42), "Chassis:Width + 2mm"));

        ParameterGraph graph = ParameterGraph.Build(document);

        graph.DependenciesOf("Outer").Should().BeEmpty();
        graph.EvaluationOrder.Should().HaveCount(2);
    }

    [Fact]
    public void AParameterWithNoFormulaIsLeftAlone()
    {
        Session session = new();

        session.SetParameter("Width", Unit.Millimetres.Of(100));
        session.SetParameter("Height", Unit.Millimetres.Of(60));

        ParameterEvaluation evaluated = ParameterGraph.Reevaluate(session.Document);

        evaluated.Changed.Should().BeEmpty();
        evaluated.Unresolved.Should().BeEmpty();
        evaluated.Document.Should().BeSameAs(session.Document);
    }

    [Fact]
    public void ABadlyTypedFormulaDoesNotStopTheDocumentBeingOrdered()
    {
        // The formula is still wrong and the evaluator still says so. But a typo in one definition
        // must not make the whole document unorderable, or one mistake would hide every other.
        Session session = new();

        session.SetParameter("Width", Unit.Millimetres.Of(100));

        Document document = session.Document.WithParameter(
            new Parameter("Broken", Quantity.Zero, "Width +"));

        Action build = () => ParameterGraph.Build(document);
        build.Should().NotThrow();

        ParameterEvaluation evaluated = ParameterGraph.Reevaluate(document);

        evaluated.Unresolved.Should().ContainSingle().Which.Name.Should().Be("Broken");
    }

    /// <summary>A document session with the ceremony of transactions hidden.</summary>
    private sealed class Session
    {
        private readonly DocumentSession _session = new();

        public Document Document => _session.Current;

        public DocumentChange SetParameter(string name, Quantity value) => Edit(
            $"Set {name}", t => t.SetParameter(new Parameter(name, value)));

        public DocumentChange SetFormula(string name, string formula) => Edit(
            $"Set {name}",
            t => t.SetParameter(new Parameter(name, Quantity.Zero, formula)));

        public FeatureId AddFeature(string name, string parameter, string formula)
        {
            FeatureId id = FeatureId.New();

            Edit($"Add {name}", t => t.AddFeature(
                Feature.Create(id, name, "Test") with
                {
                    Parameters = [new Parameter(parameter, Quantity.Zero, formula)],
                }));

            return id;
        }

        public double Value(string name)
            => Unit.Millimetres.From(Document.FindParameter(name)!.Value);

        public double FeatureValue(FeatureId id, string name)
            => Unit.Millimetres.From(Document.FindFeature(id)!.FindParameter(name)!.Value);

        private DocumentChange Edit(string name, Action<IDocumentTransaction> change)
        {
            using IDocumentTransaction transaction = _session.BeginTransaction(name);
            change(transaction);

            return transaction.Commit();
        }
    }
}
