using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Kernel;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// Transactions (P3-T02): grouping edits, publishing them as one, and abandoning them cleanly.
/// </summary>
public sealed class DocumentTransactionTests
{
    [Fact]
    public void NothingIsVisibleUntilCommit()
    {
        DocumentSession session = new();
        Document before = session.Current;

        using IDocumentTransaction transaction = session.BeginTransaction("Add extrude");
        transaction.AddFeature(Feature.Create(FeatureId.New(), "Extrude1", "Extrude"));

        // The transaction can see its own work.
        transaction.Document.Features.Should().ContainSingle();

        // Nobody else can.
        session.Current.Should().BeSameAs(
            before, "an uncommitted edit must not be observable outside the transaction");

        transaction.Commit();

        session.Current.Features.Should().ContainSingle();
    }

    [Fact]
    public void AFailedEditLeavesNoTrace()
    {
        // The reason a transaction works on a private copy. Three edits, the third invalid: the
        // session must be exactly as it was, without the transaction having to know how to reverse
        // the two that succeeded.
        DocumentSession session = new();
        Document before = session.Current;

        FeatureId id = FeatureId.New();

        using (IDocumentTransaction transaction = session.BeginTransaction("Add three"))
        {
            transaction.AddFeature(Feature.Create(id, "Extrude1", "Extrude"));
            transaction.SetParameter(new Parameter("Length", Quantity.Metres(0.1)));

            Action duplicate = () => transaction.AddFeature(
                Feature.Create(id, "Extrude2", "Extrude"));

            duplicate.Should().Throw<ArgumentException>();
        }

        session.Current.Should().BeSameAs(before);
        session.Current.Features.Should().BeEmpty();
        session.Current.FindParameter("Length").Should().BeNull();
    }

    [Fact]
    public void DisposingWithoutCommittingRollsBack()
    {
        DocumentSession session = new();
        Document before = session.Current;

        using (IDocumentTransaction transaction = session.BeginTransaction("Abandoned"))
        {
            transaction.AddFeature(Feature.Create(FeatureId.New(), "Extrude1", "Extrude"));
        }

        // The safe default. A caller that returns early, or throws, has not decided to keep its
        // edits -- and a transaction that published them anyway would turn every unhandled
        // exception into a document change nobody asked for.
        session.Current.Should().BeSameAs(before);
        session.HasOpenTransaction.Should().BeFalse("the abandoned transaction must be released");
    }

    [Fact]
    public void ASecondTransactionIsRefusedWhileOneIsOpen()
    {
        DocumentSession session = new();

        using IDocumentTransaction first = session.BeginTransaction("First");

        Action second = () => session.BeginTransaction("Second");

        // Rejected at open rather than at commit, so it fails where the mistake was made and the
        // caller still has the stack that explains it.
        second.Should().Throw<InvalidOperationException>()
            .WithMessage("*already open*");
    }

    [Fact]
    public void ATransactionCanBeOpenedAgainAfterTheFirstFinishes()
    {
        DocumentSession session = new();

        using (IDocumentTransaction first = session.BeginTransaction("First"))
        {
            first.AddFeature(Feature.Create(FeatureId.New(), "Extrude1", "Extrude"));
            first.Commit();
        }

        using IDocumentTransaction second = session.BeginTransaction("Second");
        second.AddFeature(Feature.Create(FeatureId.New(), "Extrude2", "Extrude"));
        second.Commit();

        session.Current.Features.Should().HaveCount(2);
    }

    [Fact]
    public void ARolledBackTransactionReleasesTheSession()
    {
        DocumentSession session = new();

        IDocumentTransaction transaction = session.BeginTransaction("Abandoned");
        transaction.Rollback();

        session.HasOpenTransaction.Should().BeFalse();

        // And a second rollback is harmless, because this has to be callable from a finally block
        // where the caller may not know which already happened.
        Action again = transaction.Rollback;
        again.Should().NotThrow();
    }

    [Fact]
    public void AFinishedTransactionCannotBeUsedAgain()
    {
        DocumentSession session = new();

        IDocumentTransaction transaction = session.BeginTransaction("Add extrude");
        transaction.AddFeature(Feature.Create(FeatureId.New(), "Extrude1", "Extrude"));
        transaction.Commit();

        Action edit = () => transaction.AddFeature(
            Feature.Create(FeatureId.New(), "Extrude2", "Extrude"));

        Action commitAgain = () => transaction.Commit();
        Action read = () => _ = transaction.Document;

        edit.Should().Throw<InvalidOperationException>();
        commitAgain.Should().Throw<InvalidOperationException>();
        read.Should().Throw<InvalidOperationException>();

        transaction.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void CommitReportsWhichFeaturesAndParametersWereTouched()
    {
        DocumentSession session = new();

        FeatureId first = FeatureId.New();
        FeatureId second = FeatureId.New();

        using IDocumentTransaction transaction = session.BeginTransaction("Edit");

        transaction.AddFeature(Feature.Create(first, "Extrude1", "Extrude"));
        transaction.AddFeature(Feature.Create(second, "Fillet1", "Fillet"));
        transaction.SetParameter(new Parameter("Length", Quantity.Metres(0.1)));

        DocumentChange change = transaction.Commit();

        change.TouchedFeatures.Should().BeEquivalentTo([first, second]);
        change.TouchedParameters.Should().BeEquivalentTo(["Length"]);
        change.Name.Should().Be("Edit");
        change.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void EditingOneFeatureTwiceReportsItOnce()
    {
        DocumentSession session = new();
        FeatureId id = FeatureId.New();

        using IDocumentTransaction transaction = session.BeginTransaction("Edit");

        transaction.AddFeature(Feature.Create(id, "Extrude1", "Extrude"));
        transaction.ReplaceFeature(transaction.Document.FindFeature(id)! with { Name = "Renamed" });
        transaction.ReplaceFeature(transaction.Document.FindFeature(id)! with { Name = "Again" });

        DocumentChange change = transaction.Commit();

        // A dirty seed is a set, not a log. Reporting the same feature three times would have the
        // rebuild engine consider it three times, or force it to deduplicate what was already known.
        change.TouchedFeatures.Should().ContainSingle().Which.Should().Be(id);
    }

    [Fact]
    public void RemovingAFeatureStillReportsIt()
    {
        DocumentSession session = new();
        FeatureId id = FeatureId.New();

        using (IDocumentTransaction setup = session.BeginTransaction("Setup"))
        {
            setup.AddFeature(Feature.Create(id, "Extrude1", "Extrude"));
            setup.Commit();
        }

        using IDocumentTransaction transaction = session.BeginTransaction("Delete");
        transaction.RemoveFeature(id);

        DocumentChange change = transaction.Commit();

        // The removal is the edit that most needs a seed: whatever depended on this feature is now
        // dangling and has to be reconsidered. Recording it after the fact would be impossible,
        // because by then the document has no such feature to name.
        change.TouchedFeatures.Should().ContainSingle().Which.Should().Be(id);
    }

    [Fact]
    public void ProducingABodyIsNotAnEditThatDirtiesAnything()
    {
        DocumentSession session = new();
        FeatureId id = FeatureId.New();

        using (IDocumentTransaction setup = session.BeginTransaction("Setup"))
        {
            setup.AddFeature(Feature.Create(id, "Extrude1", "Extrude"));
            setup.Commit();
        }

        using IDocumentTransaction rebuild = session.BeginTransaction("Rebuild");
        rebuild.SetBody(new Body(BodyId.New(), id, BodyKind.Solid, new KernelShape(1)));

        DocumentChange change = rebuild.Commit();

        // A body appearing is the result of a rebuild, not a cause of one. Seeding on it would
        // have every rebuild dirty the features it had just finished rebuilding, which does not
        // terminate.
        change.TouchedFeatures.Should().BeEmpty();
        session.Current.BodiesOf(id).Should().ContainSingle();
    }

    [Fact]
    public void MovingAFeatureDirtiesNothing()
    {
        DocumentSession session = new();
        FeatureId first = FeatureId.New();
        FeatureId second = FeatureId.New();

        using (IDocumentTransaction setup = session.BeginTransaction("Setup"))
        {
            setup.AddFeature(Feature.Create(first, "First", "Extrude"));
            setup.AddFeature(Feature.Create(second, "Second", "Extrude"));
            setup.Commit();
        }

        using IDocumentTransaction transaction = session.BeginTransaction("Reorder");
        transaction.MoveFeature(second, 0);

        DocumentChange change = transaction.Commit();

        // Order is what the user sees, not what anything consumes. Nothing needs rebuilding.
        change.TouchedFeatures.Should().BeEmpty();
        session.Current.Features[0].Name.Should().Be("Second");
    }

    [Fact]
    public void CommittingNothingChangesNothingAndAnnouncesNothing()
    {
        DocumentSession session = new();
        Document before = session.Current;

        int announcements = 0;
        session.Committed += _ => announcements++;

        using IDocumentTransaction transaction = session.BeginTransaction("Nothing happened");
        DocumentChange change = transaction.Commit();

        // Opening a transaction and finding there was nothing to do is normal -- a drag that ends
        // where it started, a dialog dismissed. It must not appear in the undo list.
        change.IsEmpty.Should().BeTrue();
        announcements.Should().Be(0, "an empty commit is not an edit");
        session.Current.Should().BeSameAs(before);
        session.HasOpenTransaction.Should().BeFalse();
    }

    [Fact]
    public void CommitAnnouncesTheChangeWithTheNewDocumentAlreadyCurrent()
    {
        DocumentSession session = new();

        DocumentChange? announced = null;
        Document? currentWhenAnnounced = null;

        session.Committed += change =>
        {
            announced = change;
            currentWhenAnnounced = session.Current;
        };

        using IDocumentTransaction transaction = session.BeginTransaction("Add extrude");
        transaction.AddFeature(Feature.Create(FeatureId.New(), "Extrude1", "Extrude"));
        transaction.Commit();

        announced.Should().NotBeNull();

        // A handler that reads the session sees the state the event is telling it about. Raising
        // before the swap, or while holding the lock, would give it the previous document or a
        // deadlock -- and the rebuild engine that will subscribe to this reads the session.
        currentWhenAnnounced.Should().BeSameAs(announced!.After);
        announced.Before.Features.Should().BeEmpty();
        announced.After.Features.Should().ContainSingle();
    }

    [Fact]
    public void AHandlerMayOpenItsOwnTransaction()
    {
        // The rebuild engine will do exactly this: hear that something changed, then write the
        // bodies it produced back into the document. It must not deadlock, and the transaction it
        // opens must be allowed -- the committing one has already been released by this point.
        DocumentSession session = new();
        FeatureId id = FeatureId.New();

        session.Committed += change =>
        {
            if (change.TouchedFeatures.IsEmpty)
            {
                return;
            }

            using IDocumentTransaction inner = session.BeginTransaction("Rebuild");
            inner.SetBody(new Body(BodyId.New(), change.TouchedFeatures[0], BodyKind.Solid, new KernelShape(7)));
            inner.Commit();
        };

        using (IDocumentTransaction transaction = session.BeginTransaction("Add extrude"))
        {
            transaction.AddFeature(Feature.Create(id, "Extrude1", "Extrude"));
            transaction.Commit();
        }

        session.Current.BodiesOf(id).Should().ContainSingle();
        session.HasOpenTransaction.Should().BeFalse();
    }

    [Fact]
    public void ASessionCanBeStartedFromAnExistingDocument()
    {
        Document document = Document.Empty()
            .WithFeatureAdded(Feature.Create(FeatureId.New(), "Extrude1", "Extrude"));

        DocumentSession session = new(document);

        session.Current.Should().BeSameAs(document, "opening a file does not re-edit it");
        session.Current.Features.Should().ContainSingle();
    }
}
