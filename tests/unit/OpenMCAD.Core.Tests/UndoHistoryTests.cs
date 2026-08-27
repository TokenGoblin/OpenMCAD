using System.Globalization;

using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Kernel;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// Undo and redo (P3-T17), including Phase 3's fourth exit criterion.
/// </summary>
/// <remarks>
/// The criterion is a hundred mixed operations undone and redone, with the result asserted by full
/// graph comparison. That test is at the bottom; the ones above it are the behaviours it depends on
/// and would not distinguish between if any of them were wrong.
/// </remarks>
public sealed class UndoHistoryTests
{
    [Fact]
    public void UndoingPutsTheDocumentBackExactly()
    {
        Harness harness = new();

        Document before = harness.Session.Current;
        harness.AddFeature("Extrude1");

        harness.Session.Current.Should().NotBeSameAs(before);

        harness.Undo.Undo().Should().BeTrue();

        // Reference-identical, not merely equivalent. Undo restores the document that was there
        // rather than reconstructing one like it, which is what immutability bought.
        harness.Session.Current.Should().BeSameAs(before);
    }

    [Fact]
    public void OneTransactionIsOneUndo()
    {
        // §5.4 already makes a transaction the unit of edit, so this inherits the grouping rather
        // than inventing it. A user action that adds a feature, sets three parameters and names it
        // is one undo because it was one transaction.
        Harness harness = new();

        FeatureId id = FeatureId.New();

        using (IDocumentTransaction transaction = harness.Session.BeginTransaction("Add extrude"))
        {
            transaction.AddFeature(Feature.Create(id, "Extrude1", "Extrude"));
            transaction.SetParameter(new Parameter("Depth", Unit.Millimetres.Of(10)));
            transaction.SetParameter(new Parameter("Width", Unit.Millimetres.Of(20)));
            transaction.Commit();
        }

        harness.Undo.History.Should().ContainSingle();

        harness.Undo.Undo();

        harness.Session.Current.Features.Should().BeEmpty();
        harness.Session.Current.Parameters.Should().BeEmpty();
    }

    [Fact]
    public void TheEntriesAreNamedAfterWhatTheUserDid()
    {
        Harness harness = new();

        harness.AddFeature("Extrude1", "Add extrude");
        harness.AddFeature("Fillet1", "Add fillet");

        harness.Undo.UndoName.Should().Be("Add fillet");
        harness.Undo.RedoName.Should().BeNull();

        harness.Undo.Undo();

        harness.Undo.UndoName.Should().Be("Add extrude");
        harness.Undo.RedoName.Should().Be("Add fillet");
    }

    [Fact]
    public void RedoingPutsItBack()
    {
        Harness harness = new();

        harness.AddFeature("Extrude1");
        Document after = harness.Session.Current;

        harness.Undo.Undo();
        harness.Undo.Redo().Should().BeTrue();

        harness.Session.Current.Should().BeSameAs(after);
    }

    [Fact]
    public void ANewEditAfterAnUndoMakesTheRedoUnreachable()
    {
        // Keeping it would offer a redo that jumps to a document with no path from the one on
        // screen -- the branch the user abandoned when they typed something else.
        Harness harness = new();

        harness.AddFeature("Extrude1");
        harness.Undo.Undo();

        harness.Undo.CanRedo.Should().BeTrue();

        harness.AddFeature("Revolve1");

        harness.Undo.CanRedo.Should().BeFalse();
        harness.Undo.Redo().Should().BeFalse();
    }

    [Fact]
    public void UndoingWithNothingToUndoDoesNothing()
    {
        Harness harness = new();

        harness.Undo.CanUndo.Should().BeFalse();
        harness.Undo.Undo().Should().BeFalse();
        harness.Session.Current.Features.Should().BeEmpty();
    }

    [Fact]
    public void AnUndoneEditIsNotRecordedAsANewOne()
    {
        // The restore arrives back through the same event a commit does. Recording it would put
        // the undone change straight back on the stack, and undo would be a no-op that looked
        // like it had worked.
        Harness harness = new();

        harness.AddFeature("Extrude1");
        harness.AddFeature("Fillet1");

        harness.Undo.History.Should().HaveCount(2);

        harness.Undo.Undo();

        harness.Undo.History.Should().HaveCount(1);
        harness.Undo.CanRedo.Should().BeTrue();
    }

    [Fact]
    public void TheStackIsBounded()
    {
        Harness harness = new(depth: 5);

        for (int i = 0; i < 20; ++i)
        {
            harness.AddFeature($"Extrude{i}");
        }

        harness.Undo.History.Should().HaveCount(5);
        harness.Undo.History.Last().Should().Contain("Extrude19");
    }

    [Fact]
    public void ADocumentCannotBePutBackUnderAnOpenTransaction()
    {
        Harness harness = new();
        harness.AddFeature("Extrude1");

        using IDocumentTransaction transaction = harness.Session.BeginTransaction("Editing");

        Action undo = () => harness.Undo.Undo();

        undo.Should().Throw<InvalidOperationException>()
            .WithMessage("*transaction*is open*");
    }

    [Fact]
    public void ComparingDocumentsLooksAtEverythingThatDescribesTheModel()
    {
        // The comparison the exit criterion rests on. Each of these is a difference, and a
        // comparison that missed one would be unable to see the thing that had gone wrong.
        Harness harness = new();

        FeatureId first = harness.AddFeature("Extrude1");
        FeatureId second = harness.AddFeature("Extrude2");

        Document baseline = harness.Session.Current;

        baseline.Matches(baseline).Should().BeTrue();
        baseline.Matches(null).Should().BeFalse();

        baseline.Matches(baseline.WithFeatureRemoved(second)).Should().BeFalse("a feature is gone");

        baseline.Matches(baseline.WithFeatureMoved(second, 0))
            .Should().BeFalse("the order the user arranged is part of the model");

        baseline.Matches(baseline.WithParameter(new Parameter("W", Unit.Millimetres.Of(1))))
            .Should().BeFalse("a parameter was added");

        baseline.Matches(baseline.WithRollbackPosition(1))
            .Should().BeFalse("the rollback bar is part of what is on screen");

        baseline.Matches(baseline.WithMetadata(DocumentMetadata.Empty with { PartNumber = "A-1" }))
            .Should().BeFalse("the properties are part of the document");

        baseline.Matches(baseline.WithBody(
                new Body(BodyId.New(), first, BodyKind.Solid, new KernelShape(1))))
            .Should().BeFalse("geometry is part of it too");

        baseline.Matches(baseline.WithFeatureReplaced(
                baseline.FindFeature(first)! with { IsSuppressed = true }))
            .Should().BeFalse("suppression changes what is built");
    }

    [Fact]
    public void TheVersionIsNotPartOfWhetherTwoDocumentsAreTheSameModel()
    {
        // Undoing three edits and redoing them returns the same model at a higher version, and
        // that is not a difference anybody means.
        Harness harness = new();

        harness.AddFeature("Extrude1");
        Document before = harness.Session.Current;

        harness.AddFeature("Extrude2");
        harness.Undo.Undo();

        Document after = harness.Session.Current;

        after.Should().BeSameAs(before, "this particular undo restores the reference");

        // And a document rebuilt to the same state at a different version still matches.
        Document later = before.WithParameter(new Parameter("W", Unit.Millimetres.Of(1)))
            .WithParameterRemoved("W");

        later.Version.Should().BeGreaterThan(before.Version);
        later.Matches(before).Should().BeTrue();
    }

    [Fact]
    public void TwoIdenticallyBuiltFeaturesCompareEqual()
    {
        // The trap underneath the comparison: a record compares an ImmutableArray by reference,
        // and a Feature holds three of them. Without hand-written equality every document would
        // report as different from itself the moment any feature had an input or a parameter.
        FeatureId id = FeatureId.New();
        FeatureId input = FeatureId.New();

        Feature first = Feature.Create(id, "Extrude1", "Extrude") with
        {
            Inputs = [input],
            Parameters = [new Parameter("Depth", Unit.Millimetres.Of(10))],
        };

        Feature second = Feature.Create(id, "Extrude1", "Extrude") with
        {
            Inputs = [input],
            Parameters = [new Parameter("Depth", Unit.Millimetres.Of(10))],
        };

        first.Should().Be(second);
        first.GetHashCode().Should().Be(second.GetHashCode());

        (first with { Inputs = [] }).Should().NotBe(second);
    }

    [Fact]
    public void MetadataComparesByItsPropertiesRatherThanItsDictionary()
    {
        DocumentMetadata first = DocumentMetadata.Empty.WithProperty("Finish", "Anodised");
        DocumentMetadata second = DocumentMetadata.Empty.WithProperty("Finish", "Anodised");

        first.Should().Be(second);
        first.GetHashCode().Should().Be(second.GetHashCode());

        first.Should().NotBe(DocumentMetadata.Empty.WithProperty("Finish", "Painted"));
    }

    [Fact]
    public void AHundredMixedOperationsUndoAndRedoToAnIdenticalState()
    {
        // Phase 3's fourth exit criterion, asserted by full graph comparison.
        //
        // Worth being straight about what this does and does not exercise. Because undo restores
        // the earlier document rather than reconstructing one like it, Matches short-circuits on
        // reference identity here -- so the criterion is met more strongly than it asks for, and
        // the deep comparison is not what is being stressed. Matches is tested field by field in
        // ComparingDocumentsLooksAtEverythingThatDescribesTheModel, which is where a comparison
        // that missed something would show up.
        Harness harness = new(depth: 200);

        Document start = harness.Session.Current;

        List<Document> states = [start];
        List<FeatureId> features = [];

        for (int i = 0; i < 100; ++i)
        {
            harness.MixedOperation(i, features);
            states.Add(harness.Session.Current);
        }

        Document end = harness.Session.Current;

        harness.Undo.History.Should().HaveCount(100);

        // Back to the beginning, checking every intermediate state on the way rather than only
        // the endpoint: an undo that skipped one and an undo that mis-restored two would both
        // land correctly at the start.
        for (int i = 99; i >= 0; --i)
        {
            harness.Undo.Undo().Should().BeTrue();

            harness.Session.Current.Matches(states[i]).Should().BeTrue(
                $"undoing back to operation {i} should give exactly that state");
        }

        harness.Session.Current.Matches(start).Should().BeTrue();
        harness.Undo.CanUndo.Should().BeFalse();

        // And forward again.
        for (int i = 0; i < 100; ++i)
        {
            harness.Undo.Redo().Should().BeTrue();

            harness.Session.Current.Matches(states[i + 1]).Should().BeTrue(
                $"redoing to operation {i} should give exactly that state");
        }

        harness.Session.Current.Matches(end).Should().BeTrue();
        harness.Undo.CanRedo.Should().BeFalse();
    }

    private sealed class Harness
    {
        public Harness(int depth = UndoHistory.DefaultDepth)
        {
            Session = new DocumentSession();
            Undo = new UndoHistory(Session, depth);
        }

        public DocumentSession Session { get; }

        public UndoHistory Undo { get; }

        public FeatureId AddFeature(string name, string? edit = null)
        {
            FeatureId id = FeatureId.New();

            Edit(edit ?? $"Add {name}",
                t => t.AddFeature(Feature.Create(id, name, "Test")));

            return id;
        }

        /// <summary>One of every kind of edit, cycled, so the run covers them all.</summary>
        public void MixedOperation(int step, List<FeatureId> features)
        {
            switch (step % 8)
            {
                case 0:
                case 1:
                    features.Add(AddFeature($"Feature{step}"));
                    break;

                case 2:
                    Edit("Set parameter", t => t.SetParameter(
                        new Parameter($"P{step}", Unit.Millimetres.Of(step))));
                    break;

                case 3 when features.Count > 0:
                    Edit("Rename", t => t.ReplaceFeature(
                        Session.Current.FindFeature(features[^1])! with { Name = $"Renamed{step}" }));
                    break;

                case 4 when features.Count > 1:
                    Edit("Reorder", t => t.MoveFeature(features[^1], 0));
                    break;

                case 5 when features.Count > 0:
                    Edit("Suppress", t => t.ReplaceFeature(
                        Session.Current.FindFeature(features[0])! with { IsSuppressed = true }));
                    break;

                case 6 when features.Count > 0:
                    Edit("Add body", t => t.SetBody(new Body(
                        BodyId.New(), features[^1], BodyKind.Solid, new KernelShape((ulong)step + 1))));
                    break;

                case 7 when features.Count > 2:
                    FeatureId gone = features[0];
                    features.RemoveAt(0);

                    Edit("Delete", t => t.RemoveFeature(gone));
                    break;

                default:
                    Edit("Set property", t => t.SetMetadata(
                        Session.Current.Metadata.WithProperty("Step", step.ToString(CultureInfo.InvariantCulture))));
                    break;
            }
        }

        private void Edit(string name, Action<IDocumentTransaction> change)
        {
            using IDocumentTransaction transaction = Session.BeginTransaction(name);
            change(transaction);
            transaction.Commit();
        }
    }
}
