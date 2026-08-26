using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Core.Documents;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// The dependency graph (P3-T03): what must be evaluated before what, and what to say when that
/// question has no answer.
/// </summary>
public sealed class FeatureGraphTests
{
    [Fact]
    public void TheGraphComesFromDeclaredInputsAndNotFromTreeOrder()
    {
        // Three features in the tree, none of which consumes another. Reading the tree as the graph
        // would make each depend on the one above it, so a change to the first would rebuild all
        // three and reordering would never be safe.
        Builder builder = new();

        FeatureId first = builder.Add("First");
        FeatureId second = builder.Add("Second");
        FeatureId third = builder.Add("Third");

        FeatureGraph graph = FeatureGraph.Build(builder.Document);

        graph.DependenciesOf(second).Should().BeEmpty();
        graph.DependenciesOf(third).Should().BeEmpty();

        graph.AffectedBy([first]).Should().Equal(
            [first], "nothing consumes the first feature, so nothing else needs rebuilding");
    }

    [Fact]
    public void EverythingAppearsAfterWhatItConsumes()
    {
        Builder builder = new();

        FeatureId sketch = builder.Add("Sketch1");
        FeatureId extrude = builder.Add("Extrude1", sketch);
        FeatureId fillet = builder.Add("Fillet1", extrude);

        FeatureGraph graph = FeatureGraph.Build(builder.Document);

        graph.EvaluationOrder.Should().Equal([sketch, extrude, fillet]);
    }

    [Fact]
    public void AFeatureIsOrderedAfterItsInputsEvenWhenItComesFirstInTheTree()
    {
        // The case that proves the order is not the tree's. The fillet is added first and consumes
        // the extrude, which is added second, so the tree order and the evaluation order disagree.
        Builder builder = new();

        FeatureId fillet = builder.Add("Fillet1");
        FeatureId extrude = builder.Add("Extrude1");

        builder.SetInputs(fillet, extrude);

        FeatureGraph graph = FeatureGraph.Build(builder.Document);

        graph.EvaluationOrder.Should().Equal(
            [extrude, fillet], "an input has to be evaluated before what consumes it");
    }

    [Fact]
    public void IndependentFeaturesAreOrderedByTheirPlaceInTheTree()
    {
        // A topological order is not unique: all three of these are ready immediately and any
        // order would be correct. Which one is picked is free, and spending that freedom on
        // reproducibility is what keeps a cache key, a regression baseline and a bug report
        // meaningful. Tree position is stable and survives a save; the id is random and does not.
        //
        // Eight rather than three, so the assertion discriminates. An implementation that broke
        // ties by id would be sorting by a random value, and with three features it would land on
        // tree order once in six runs by luck alone -- a test that passes a sixth of the time
        // against a broken implementation is not far off one that always does.
        Builder builder = new();

        ImmutableArray<FeatureId> added =
        [
            .. Enumerable.Range(0, 8).Select(i => builder.Add($"Feature{i}")),
        ];

        FeatureGraph.Build(builder.Document).EvaluationOrder.Should().Equal(added);
    }

    [Fact]
    public void TheSameDocumentAlwaysOrdersTheSameWay()
    {
        Builder builder = new();

        FeatureId sketch = builder.Add("Sketch1");
        FeatureId left = builder.Add("Left", sketch);
        FeatureId right = builder.Add("Right", sketch);
        FeatureId join = builder.Add("Join", left, right);

        ImmutableArray<FeatureId> first = FeatureGraph.Build(builder.Document).EvaluationOrder;

        for (int i = 0; i < 20; ++i)
        {
            FeatureGraph.Build(builder.Document).EvaluationOrder.Should().Equal(
                first, "the same document must rebuild in the same order every time");
        }

        first.Should().Equal([sketch, left, right, join]);
    }

    [Fact]
    public void ACycleIsReportedByName()
    {
        Builder builder = new();

        FeatureId a = builder.Add("Extrude1");
        FeatureId b = builder.Add("Fillet1");

        builder.SetInputs(a, b);
        builder.SetInputs(b, a);

        Action build = () => FeatureGraph.Build(builder.Document);

        FeatureCycleException thrown = build.Should().Throw<FeatureCycleException>().Which;

        // The names, not the ids. The user chose the names; the ids mean nothing to them, and
        // "this document contains a circular dependency" tells them they are stuck and nothing
        // about where.
        thrown.Names.Should().BeEquivalentTo(["Extrude1", "Fillet1"]);
        thrown.Cycle.Should().BeEquivalentTo([a, b]);

        thrown.Message.Should().Contain("Extrude1").And.Contain("Fillet1");

        // Closed back to the start, so it reads as a loop.
        thrown.Message.Should().MatchRegex(@"(Extrude1|Fillet1).*->.*->.*\1");
    }

    [Fact]
    public void ALongerCycleIsReportedInFull()
    {
        Builder builder = new();

        FeatureId a = builder.Add("A");
        FeatureId b = builder.Add("B");
        FeatureId c = builder.Add("C");

        builder.SetInputs(a, c);
        builder.SetInputs(b, a);
        builder.SetInputs(c, b);

        FeatureCycleException thrown = ((Action)(() => FeatureGraph.Build(builder.Document)))
            .Should().Throw<FeatureCycleException>().Which;

        thrown.Cycle.Should().HaveCount(3);
        thrown.Names.Should().BeEquivalentTo(["A", "B", "C"]);
    }

    [Fact]
    public void OnlyTheLoopIsNamedAndNotEverythingItSpoiled()
    {
        // Everything downstream of a loop is also unorderable, so the obvious implementation
        // reports all of it. That buries the two features the user has to look at under a list of
        // the twenty they do not.
        Builder builder = new();

        FeatureId a = builder.Add("A");
        FeatureId b = builder.Add("B");

        builder.SetInputs(a, b);
        builder.SetInputs(b, a);

        for (int i = 0; i < 8; ++i)
        {
            builder.Add($"Downstream{i}", a);
        }

        FeatureCycleException thrown = ((Action)(() => FeatureGraph.Build(builder.Document)))
            .Should().Throw<FeatureCycleException>().Which;

        thrown.Cycle.Should().HaveCount(2, "only the loop itself is the loop");
        thrown.Names.Should().BeEquivalentTo(["A", "B"]);
    }

    [Fact]
    public void AFeatureThatConsumesItselfIsACycle()
    {
        Builder builder = new();
        FeatureId self = builder.Add("Extrude1");

        builder.SetInputs(self, self);

        FeatureCycleException thrown = ((Action)(() => FeatureGraph.Build(builder.Document)))
            .Should().Throw<FeatureCycleException>().Which;

        thrown.Cycle.Should().Equal([self]);
        thrown.Names.Should().Equal(["Extrude1"]);
    }

    [Fact]
    public void AnInputThatIsNotThereIsReportedRatherThanThrown()
    {
        // Deleting a feature that others consume is a normal thing to do, and it is not the delete
        // that is wrong -- it is what is left pointing at nothing. Refusing to build the graph
        // would leave the document unopenable and give the user no way to see what to fix.
        Builder builder = new();

        FeatureId extrude = builder.Add("Extrude1");
        FeatureId missing = FeatureId.New();
        FeatureId fillet = builder.Add("Fillet1", extrude, missing);

        FeatureGraph graph = FeatureGraph.Build(builder.Document);

        graph.IsComplete.Should().BeFalse();
        graph.Dangling.Should().ContainSingle().Which.Should().Be(new DanglingInput(fillet, missing));

        // And the rest of the graph is still usable.
        graph.EvaluationOrder.Should().Equal([extrude, fillet]);
        graph.DependenciesOf(fillet).Should().Equal([extrude]);
    }

    [Fact]
    public void NamingOneInputTwiceIsNotTwoEdges()
    {
        // An in-degree counted twice for one edge never reaches zero, so the feature is never
        // ready, and the graph reports a cycle that does not exist.
        Builder builder = new();

        FeatureId extrude = builder.Add("Extrude1");
        FeatureId pattern = builder.Add("Pattern1", extrude, extrude);

        FeatureGraph graph = FeatureGraph.Build(builder.Document);

        graph.EvaluationOrder.Should().Equal([extrude, pattern]);
        graph.DependenciesOf(pattern).Should().Equal([extrude]);
        graph.DependentsOf(extrude).Should().Equal([pattern]);
    }

    [Fact]
    public void ChangingAFeatureAffectsEverythingDownstreamOfIt()
    {
        Builder builder = new();

        FeatureId sketch = builder.Add("Sketch1");
        FeatureId extrude = builder.Add("Extrude1", sketch);
        FeatureId fillet = builder.Add("Fillet1", extrude);
        FeatureId unrelated = builder.Add("Unrelated");

        FeatureGraph graph = FeatureGraph.Build(builder.Document);

        // The seed is included: a feature whose parameters changed has to be rebuilt itself, not
        // merely have its consumers rebuilt.
        graph.AffectedBy([sketch]).Should().Equal([sketch, extrude, fillet]);

        graph.AffectedBy([extrude]).Should().Equal([extrude, fillet]);
        graph.AffectedBy([fillet]).Should().Equal([fillet]);

        graph.AffectedBy([unrelated]).Should().Equal(
            [unrelated], "an independent branch is not rebuilt because something else changed");
    }

    [Fact]
    public void WhatIsAffectedComesBackInEvaluationOrder()
    {
        // A caller that received a set would have to sort it against this same graph to execute it,
        // so returning one would just move the work.
        Builder builder = new();

        FeatureId sketch = builder.Add("Sketch1");
        FeatureId left = builder.Add("Left", sketch);
        FeatureId right = builder.Add("Right", sketch);
        FeatureId join = builder.Add("Join", left, right);

        FeatureGraph graph = FeatureGraph.Build(builder.Document);

        ImmutableArray<FeatureId> affected = graph.AffectedBy([sketch]);

        affected.Should().Equal([sketch, left, right, join]);

        // Which is to say: a subsequence of the full order, not merely the same set.
        affected.Should().BeSubsetOf(graph.EvaluationOrder);

        int previous = -1;

        foreach (FeatureId id in affected)
        {
            int index = graph.EvaluationOrder.IndexOf(id);
            index.Should().BeGreaterThan(previous);
            previous = index;
        }
    }

    [Fact]
    public void ADiamondRebuildsTheJoinOnceAndNotTwice()
    {
        Builder builder = new();

        FeatureId sketch = builder.Add("Sketch1");
        FeatureId left = builder.Add("Left", sketch);
        FeatureId right = builder.Add("Right", sketch);
        FeatureId join = builder.Add("Join", left, right);

        FeatureGraph graph = FeatureGraph.Build(builder.Document);

        ImmutableArray<FeatureId> affected = graph.AffectedBy([sketch]);

        affected.Count(id => id == join).Should().Be(
            1, "the join is reachable down both branches but is still one feature");
    }

    [Fact]
    public void ARemovedFeatureIsStillAUsefulSeed()
    {
        // The rebuild engine is told a feature was deleted. That feature has no node to walk from,
        // and rejecting the seed would throw away the only reason its former consumers are dirty.
        Builder builder = new();

        FeatureId extrude = builder.Add("Extrude1");

        FeatureGraph graph = FeatureGraph.Build(builder.Document);

        Action affected = () => graph.AffectedBy([FeatureId.New(), extrude]);

        affected.Should().NotThrow();
        graph.AffectedBy([FeatureId.New(), extrude]).Should().Equal([extrude]);
    }

    [Fact]
    public void AnEmptyDocumentHasAnEmptyGraph()
    {
        FeatureGraph graph = FeatureGraph.Build(Document.Empty());

        graph.EvaluationOrder.Should().BeEmpty();
        graph.IsComplete.Should().BeTrue();
        graph.AffectedBy([]).Should().BeEmpty();
    }

    /// <summary>Builds documents without the ceremony of transactions, which are tested elsewhere.</summary>
    private sealed class Builder
    {
        public Document Document { get; private set; } = Document.Empty();

        public FeatureId Add(string name, params FeatureId[] inputs)
        {
            FeatureId id = FeatureId.New();

            Document = Document.WithFeatureAdded(
                Feature.Create(id, name, "Test") with { Inputs = [.. inputs] });

            return id;
        }

        public void SetInputs(FeatureId id, params FeatureId[] inputs)
            => Document = Document.WithFeatureReplaced(
                Document.FindFeature(id)! with { Inputs = [.. inputs] });
    }
}
