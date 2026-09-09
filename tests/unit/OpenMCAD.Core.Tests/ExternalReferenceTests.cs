using System.Collections.Immutable;
using System.Text;

using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Serialization;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// What a document depends on, whether it is current, and the cycles that can form (P5-T11, §5.9).
/// </summary>
/// <remarks>
/// §5.9 calls in-context references a hazardous feature and asks for four specific things: explicit
/// tracking, an out-of-date indicator, lock/break/unlock, and cycle detection at commit. What is
/// asserted below is those four, and mostly the distinctions between states that look alike from
/// outside and mean opposite things to a user.
/// </remarks>
public sealed class ExternalReferenceTests
{
    [Fact]
    public void ADocumentDependsOnNothingUntilItSaysSo()
    {
        Document.Empty().ExternalReferences.Should().BeEmpty();
        Document.Empty().FindExternalReference("anything.omcad").Should().BeNull();
    }

    [Fact]
    public void ALinkedReferenceIsCurrentWhileItsTargetHasNotMoved()
    {
        ExternalReference reference = new("bolt.omcad", "v1");

        reference.Health("v1").Should().Be(ExternalReferenceHealth.UpToDate);
    }

    [Fact]
    public void ALinkedReferenceIsOutOfDateOnceItsTargetMoves()
    {
        // The indicator §5.9 asks for, and the whole reason the stamp is stored: nothing else
        // survives a save to say what the target was when this document last read it.
        ExternalReference reference = new("bolt.omcad", "v1");

        reference.Health("v2").Should().Be(ExternalReferenceHealth.OutOfDate);
    }

    [Fact]
    public void AMissingTargetOutranksEveryOtherAnswer()
    {
        // Reporting a reference as up to date when the file it names has gone would be the worst
        // available lie, so this is checked before the stamps are compared at all.
        foreach (ExternalReferenceState state in Enum.GetValues<ExternalReferenceState>())
        {
            new ExternalReference("bolt.omcad", "v1", state)
                .Health(null).Should().Be(ExternalReferenceHealth.Missing);
        }
    }

    // --- Lock, break, unlock -----------------------------------------------------------------------

    [Fact]
    public void ALockedReferenceDoesNotBecomeOutOfDate()
    {
        ExternalReference held = new ExternalReference("plate.omcad", "v1").Lock("before the release");

        held.State.Should().Be(ExternalReferenceState.Locked);
        held.IsFollowing.Should().BeFalse();
        held.Health("v1").Should().Be(ExternalReferenceHealth.Held);
    }

    [Fact]
    public void ALockedReferenceStillSaysWhenItsTargetHasMovedOn()
    {
        // Not the same as out of date, and the difference is the point: one is a thing to fix, the
        // other a thing the user chose. Collapsing them would either nag about a deliberate freeze
        // or hide that the freeze is now costing something.
        ExternalReference held = new ExternalReference("plate.omcad", "v1").Lock();

        held.Health("v2").Should().Be(ExternalReferenceHealth.HeldBehind);
    }

    [Fact]
    public void UnlockingFollowsAgainAndAdmitsWhatWasMissed()
    {
        ExternalReference again = new ExternalReference("plate.omcad", "v1").Lock().Unlock();

        again.IsFollowing.Should().BeTrue();

        // The stamp is left alone on purpose. Unlocking says "follow this again", not "you are
        // already current" -- so what was missed while frozen turns into work to do, which is the
        // whole reason for unlocking.
        again.Health("v2").Should().Be(ExternalReferenceHealth.OutOfDate);

        // And the stamp really is the old one rather than merely absent. Found by sabotage:
        // clearing it on unlock also reports OutOfDate against a moved target, so only asking
        // about the *unmoved* one tells the two apart.
        again.Stamp.Should().Be("v1");
        again.Health("v1").Should().Be(ExternalReferenceHealth.UpToDate);
    }

    [Fact]
    public void ABrokenReferenceTracksNothingAndSaysWhy()
    {
        ExternalReference cut = new ExternalReference("plate.omcad", "v1")
            .Break("the supplier changed the part");

        cut.Health("v9").Should().Be(ExternalReferenceHealth.Broken);
        cut.Note.Should().Contain("supplier");

        // Kept in the list rather than deleted: a user needs to see that what they are looking at
        // came from somewhere once.
        cut.Target.Should().Be("plate.omcad");
    }

    [Fact]
    public void RefreshingAReferenceThatIsNotFollowingIsRefused()
    {
        // A rebuild that refreshed a locked reference would defeat the lock, and doing it quietly
        // is how a user finds out their frozen drawing was not frozen after all.
        ExternalReference held = new ExternalReference("plate.omcad", "v1").Lock();

        FluentActions.Invoking(() => held.RefreshedTo("v2"))
            .Should().Throw<InvalidOperationException>();

        new ExternalReference("plate.omcad", "v1").RefreshedTo("v2")
            .Health("v2").Should().Be(ExternalReferenceHealth.UpToDate);
    }

    // --- Recording it on a document ----------------------------------------------------------------

    [Fact]
    public void ADocumentDependsOnAnotherOnceHoweverManyThingsReachAcross()
    {
        DocumentSession session = new();

        Edit(session, t => t.SetExternalReference(new ExternalReference("bolt.omcad", "v1")));
        Edit(session, t => t.SetExternalReference(new ExternalReference("bolt.omcad", "v2")));

        // Add-or-replace on the target. Two entries would be two answers to "is this up to date".
        session.Current.ExternalReferences.Should().ContainSingle();
        session.Current.FindExternalReference("bolt.omcad")!.Stamp.Should().Be("v2");
    }

    [Fact]
    public void LockingIsOneUndoLikeAnyOtherEdit()
    {
        DocumentSession session = new();
        UndoHistory undo = new(session);

        Edit(session, t => t.SetExternalReference(new ExternalReference("bolt.omcad", "v1")));

        Document linked = session.Current;

        Edit(session, t => t.SetExternalReference(
            session.Current.FindExternalReference("bolt.omcad")!.Lock("for the release")));

        session.Current.FindExternalReference("bolt.omcad")!.State
            .Should().Be(ExternalReferenceState.Locked);

        undo.Undo().Should().BeTrue();

        // §5.9 asks for clear UI around a hazardous feature. Being able to take a lock back with
        // the same keystroke as any other mistake is part of that.
        session.Current.Should().BeSameAs(linked);
    }

    [Fact]
    public void ForgettingADependencyIsNotTheSameAsBreakingIt()
    {
        DocumentSession session = new();

        Edit(session, t => t.SetExternalReference(new ExternalReference("bolt.omcad", "v1")));
        Edit(session, t => t.RemoveExternalReference("bolt.omcad"));

        // Broken leaves something the user can see and account for. This leaves nothing, and is for
        // a dependency that genuinely no longer exists.
        session.Current.ExternalReferences.Should().BeEmpty();
    }

    [Fact]
    public void TwoDocumentsDifferingOnlyInWhatTheyDependOnDoNotMatch()
    {
        Document bare = Document.Empty();
        Document depending = bare.WithExternalReference(new ExternalReference("bolt.omcad", "v1"));

        depending.Matches(bare).Should().BeFalse();

        // And in the state, not merely the presence: a locked reference is a different document
        // from a linked one, and an undo has to be able to see that.
        depending.WithExternalReference(new ExternalReference("bolt.omcad", "v1").Lock())
            .Matches(depending).Should().BeFalse();
    }

    // --- The container part -------------------------------------------------------------------------

    [Fact]
    public void WhatADocumentDependsOnSurvivesARoundTrip()
    {
        ImmutableArray<ExternalReference> original =
        [
            new("bolt.omcad", "v1"),
            new("plate.omcad", "v7", ExternalReferenceState.Locked, "before the release"),
            new("gone.omcad", "v3", ExternalReferenceState.Broken, "supplier changed it"),
        ];

        ExternalReferenceFormat.Read(ExternalReferenceFormat.Write(original))
            .Should().Equal(original);
    }

    [Fact]
    public void ADocumentThatDependsOnNothingWritesNoPart()
    {
        // A part in every file written so far, saying nothing, would change all of them -- and §3
        // of persistence.md wants the bytes to be a function of the document.
        ExternalReferenceFormat.Write([]).Should().BeNull();
        ExternalReferenceFormat.Read(null).Should().BeEmpty();
    }

    [Fact]
    public void TheStateIsWrittenByNameSoInsertingOneCannotRewriteOldFiles()
    {
        string json = Encoding.UTF8.GetString(
            ExternalReferenceFormat.Write([new("plate.omcad", "v1", ExternalReferenceState.Locked)])!);

        // A number would mean whatever the declaration order happened to be when the file was
        // written. This part is also the one most likely to be read by something that is not this
        // program, and a name is the only form such a reader can interpret.
        json.Should().Contain("Locked").And.NotContain("\"State\":1");
    }

    [Fact]
    public void ADamagedListIsRefusedRatherThanGuessedAt()
    {
        // Unlike a cache this cannot be regenerated: the stamps are the only record of what the
        // targets were when they were last read, and inventing them would report a stale document
        // as current.
        FluentActions.Invoking(() => ExternalReferenceFormat.Read(Encoding.UTF8.GetBytes("{ not json")))
            .Should().Throw<DocumentFormatException>();
    }

    [Fact]
    public void ADependencyWithNoTargetIsRefused()
    {
        FluentActions.Invoking(() => ExternalReferenceFormat.Read(
                Encoding.UTF8.GetBytes("""[{"Stamp":"v1","State":"Linked"}]""")))
            .Should().Throw<DocumentFormatException>();
    }

    [Fact]
    public void ThePartSurvivesTheContainer()
    {
        Document document = Document.Empty(DocumentKind.Assembly)
            .WithExternalReference(new ExternalReference("bolt.omcad", "v1"));

        using MemoryStream stream = new();

        // Nothing is composed by hand. The part is written from the document and folded back into
        // it on the way in, so a caller cannot forget it -- which is the whole reason this one part
        // is not opaque like the thumbnail beside it.
        DocumentPackage.Save(
            stream,
            document,
            DocumentManifest.ForNewDocument("tests", DocumentKind.Assembly, DateTimeOffset.UnixEpoch));

        stream.Position = 0;

        OpenedPackage read = DocumentPackage.Open(stream);

        read.Document.ExternalReferences.Should().Equal(document.ExternalReferences);
        read.Contents.ExternalReferences.Should().NotBeNull("the part is on disk as well");
    }

    // --- Dirty tracking ---------------------------------------------------------------------------

    [Fact]
    public void AFreshlyOpenedDocumentIsNotDirty()
    {
        // What a user expects of a title bar: the first edit is what makes it dirty, not the act
        // of opening the file.
        new DocumentSession().IsDirty.Should().BeFalse();
        new DocumentSession(Document.Empty(DocumentKind.Assembly)).IsDirty.Should().BeFalse();
    }

    [Fact]
    public void AnEditMakesADocumentDirtyAndSavingClearsIt()
    {
        DocumentSession session = new();

        Edit(session, t => t.SetExternalReference(new ExternalReference("bolt.omcad", "v1")));
        session.IsDirty.Should().BeTrue();

        session.MarkSaved().Should().BeSameAs(session.Current);
        session.IsDirty.Should().BeFalse();

        Edit(session, t => t.SetExternalReference(new ExternalReference("bolt.omcad", "v2")));
        session.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void UndoingBackToWhatWasSavedIsNotDirty()
    {
        // The property immutability buys, and the reason this compares by reference: undo hands
        // back the very document that was saved, so asking the user to save a file that cannot
        // have changed would be asking them to save nothing.
        DocumentSession session = new();
        UndoHistory undo = new(session);

        Edit(session, t => t.SetExternalReference(new ExternalReference("bolt.omcad", "v1")));
        session.MarkSaved();

        Edit(session, t => t.SetExternalReference(new ExternalReference("plate.omcad", "v1")));
        session.IsDirty.Should().BeTrue();

        undo.Undo().Should().BeTrue();
        session.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void EditingBackToAnEqualStateStillCountsAsDirty()
    {
        // The one case where comparing by reference and comparing by value disagree, and the
        // choice is deliberate: this document equals the saved one and is not the saved one, and
        // reporting it clean would mean deciding on the user's behalf that their round trip
        // changed nothing. Offering to save something that need not be saved costs a keystroke;
        // the other way costs the work.
        DocumentSession session = new();
        Document saved = session.MarkSaved();

        Edit(session, t => t.SetExternalReference(new ExternalReference("bolt.omcad", "v1")));
        Edit(session, t => t.RemoveExternalReference("bolt.omcad"));

        session.Current.Matches(saved).Should().BeTrue("it is an equal document");
        session.IsDirty.Should().BeTrue("but it is not the same one");
    }

    [Fact]
    public void ReadingWhatADocumentDependsOnReplacesRatherThanMerges()
    {
        // What a file says it depends on *is* what it depends on. Folding a file's list into an
        // existing one would let an entry that has been deleted survive a reload -- and the
        // surviving entry would carry a stamp, so it would go on claiming to be up to date.
        Document had = Document.Empty()
            .WithExternalReference(new ExternalReference("gone.omcad", "v1"));

        Document reloaded = had.WithExternalReferences(
            [new ExternalReference("bolt.omcad", "v2")]);

        reloaded.ExternalReferences.Should().ContainSingle();
        reloaded.FindExternalReference("gone.omcad").Should().BeNull();
    }

    // --- Cycles ---------------------------------------------------------------------------------------

    [Fact]
    public void ADocumentThatDependsOnNobodyIsInNoCycle()
    {
        ExternalReferenceGraph.FindCycle("a.omcad", _ => []).Should().BeEmpty();
    }

    [Fact]
    public void ACycleIsReportedAsThePathAroundIt()
    {
        Dictionary<string, string[]> product = new()
        {
            ["a.omcad"] = ["b.omcad"],
            ["b.omcad"] = ["c.omcad"],
            ["c.omcad"] = ["a.omcad"],
        };

        ImmutableArray<string> cycle = ExternalReferenceGraph.FindCycle(
            "a.omcad", d => product.TryGetValue(d, out string[]? next) ? next : []);

        // The path rather than a yes or no: a refusal the user cannot act on is barely better than
        // no refusal, and this tells them which link to cut.
        cycle.Should().Equal("a.omcad", "b.omcad", "c.omcad", "a.omcad");
    }

    [Fact]
    public void ACycleIsReportedWithoutTheRoadThatReachedIt()
    {
        // The run-up is not part of the loop, and a user asked to cut a link needs the links that
        // form the ring rather than the ones leading to it.
        Dictionary<string, string[]> product = new()
        {
            ["start.omcad"] = ["b.omcad"],
            ["b.omcad"] = ["c.omcad"],
            ["c.omcad"] = ["b.omcad"],
        };

        ImmutableArray<string> cycle = ExternalReferenceGraph.FindCycle(
            "start.omcad", d => product.TryGetValue(d, out string[]? next) ? next : []);

        cycle.Should().Equal("b.omcad", "c.omcad", "b.omcad");
        cycle.Should().NotContain("start.omcad");
    }

    [Fact]
    public void ADocumentDependingOnItselfIsACycle()
    {
        ExternalReferenceGraph.FindCycle("a.omcad", _ => ["a.omcad"])
            .Should().Equal("a.omcad", "a.omcad");
    }

    [Fact]
    public void ADiamondIsNotACycle()
    {
        // Two routes to one document is the ordinary shape of a product that uses a common library,
        // and reporting it as a cycle would refuse every real assembly.
        Dictionary<string, string[]> product = new()
        {
            ["top.omcad"] = ["left.omcad", "right.omcad"],
            ["left.omcad"] = ["shared.omcad"],
            ["right.omcad"] = ["shared.omcad"],
            ["shared.omcad"] = [],
        };

        ExternalReferenceGraph.FindCycle(
            "top.omcad", d => product.TryGetValue(d, out string[]? next) ? next : [])
            .Should().BeEmpty();
    }

    [Fact]
    public void ADocumentIsAskedAboutOnceHoweverManyRoutesReachIt()
    {
        // Pins the memo, which is otherwise invisible: without it the search is exponential in a
        // product where parts share a library, and every answer is still correct, so no assertion
        // about cycles can see the difference. Counting the questions asked can.
        Dictionary<string, string[]> product = new()
        {
            ["a"] = ["b", "c"],
            ["b"] = ["d", "e"],
            ["c"] = ["d", "e"],
            ["d"] = ["f"],
            ["e"] = ["f"],
            ["f"] = [],
        };

        Dictionary<string, int> asked = [];

        ExternalReferenceGraph.FindCycle("a", d =>
        {
            asked[d] = asked.GetValueOrDefault(d) + 1;
            return product.TryGetValue(d, out string[]? next) ? next : [];
        }).Should().BeEmpty();

        asked.Values.Should().OnlyContain(times => times == 1);
    }

    [Fact]
    public void ADocumentThatCannotBeOpenedIsNotEvidenceOfACycle()
    {
        // Refusing an edit because a file could not be read would blame a document that might be
        // innocent, and the user has no way to prove otherwise.
        ExternalReferenceGraph.FindCycle("a.omcad", d => d == "a.omcad" ? ["locked.omcad"] : [])
            .Should().BeEmpty();
    }

    private static void Edit(DocumentSession session, Action<IDocumentTransaction> change)
    {
        using IDocumentTransaction transaction = session.BeginTransaction("Edit");
        change(transaction);
        transaction.Commit();
    }
}
