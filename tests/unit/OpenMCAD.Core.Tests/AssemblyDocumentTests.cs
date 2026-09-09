using FluentAssertions;

using OpenMCAD.Core.Assemblies;
using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Serialization;
using OpenMCAD.Math;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// An assembly as a kind of document: what it may hold, what a part may not, and what survives a
/// save (P5-T04).
/// </summary>
/// <remarks>
/// One document type with a <see cref="DocumentKind"/> rather than three types, because everything
/// the container does — transactions, undo, parameters, unknown-field preservation — is the same
/// for all of them, and only the payload differs. What has to be true for that to be safe is that
/// the kind is recorded rather than guessed, and that a document refuses the payload it is not for.
/// </remarks>
public sealed class AssemblyDocumentTests
{
    [Fact]
    public void ADocumentIsAPartUnlessItSaysOtherwise()
    {
        Document.Empty().Kind.Should().Be(DocumentKind.Part);
        Document.Empty().Assembly.Occurrences.Should().BeEmpty();
    }

    [Fact]
    public void AnAssemblyStartsWithTheStandardDatumsToo()
    {
        Document assembly = Document.Empty(DocumentKind.Assembly);

        // What a first component is placed against, and what a mate to "the front plane" will
        // resolve to. A document whose first action had to be creating somewhere to work is the
        // thing StandardDatums already exists to prevent for parts.
        assembly.Kind.Should().Be(DocumentKind.Assembly);
        assembly.FindReference(FeatureId.None, "Front").Should().NotBeNull();
    }

    [Fact]
    public void APartRefusesToHoldComponents()
    {
        DocumentSession session = new();

        // Not merely tidiness. If a part could quietly acquire occurrences it would be an assembly
        // nothing had declared, and every reader would decide for itself which it was looking at.
        using IDocumentTransaction tx = session.BeginTransaction("Place a component");

        FluentActions.Invoking(() => tx.SetAssembly(WithOneBolt(out _, out _)))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AnAssemblyKeepsWhatIsPlacedInIt()
    {
        DocumentSession session = new(Document.Empty(DocumentKind.Assembly));
        Assembly structure = WithOneBolt(out ComponentDefinition bolt, out ComponentOccurrence only);

        using (IDocumentTransaction tx = session.BeginTransaction("Place a bolt"))
        {
            tx.SetAssembly(structure);
            tx.Commit();
        }

        session.Current.Assembly.Definitions.Should().ContainSingle().Which.Should().Be(bolt);
        session.Current.Assembly.FindOccurrence(only.Id).Should().Be(only);
    }

    [Fact]
    public void PlacingAComponentIsOneUndo()
    {
        DocumentSession session = new(Document.Empty(DocumentKind.Assembly));
        UndoHistory undo = new(session);
        Document before = session.Current;

        using (IDocumentTransaction tx = session.BeginTransaction("Place a bolt"))
        {
            tx.SetAssembly(WithOneBolt(out _, out _));
            tx.Commit();
        }

        session.Current.Assembly.Occurrences.Should().ContainSingle();

        undo.Undo().Should().BeTrue();

        // Inherited rather than invented: §5.4 already makes a transaction the unit of edit, and
        // an immutable document makes undo a matter of holding an earlier reference -- so this is
        // the document that was there, not one reconstructed to look like it.
        session.Current.Should().BeSameAs(before);
        session.Current.Assembly.Occurrences.Should().BeEmpty();
    }

    [Fact]
    public void TwoDocumentsDifferingOnlyInKindDoNotMatch()
    {
        Document.Empty(DocumentKind.Assembly)
            .Matches(Document.Empty(DocumentKind.Part)).Should().BeFalse();
    }

    [Fact]
    public void TwoDocumentsDifferingOnlyInWhatTheyPlaceDoNotMatch()
    {
        // Matches is what Phase 3's fourth exit criterion rests on -- identical state after a
        // hundred operations -- so a field it did not compare would be a field that could go wrong
        // without the comparison seeing it.
        Document bare = Document.Empty(DocumentKind.Assembly);
        Document placed = bare.WithAssembly(WithOneBolt(out _, out _));

        placed.Matches(bare).Should().BeFalse();
        placed.Matches(placed).Should().BeTrue();
    }

    [Fact]
    public void TwoDocumentsDifferingOnlyInWhatTheyListDoNotMatch()
    {
        // Separate from the test above, which adds a component *and* places it and so cannot tell
        // the two halves of the comparison apart -- found by sabotage: removing either check on its
        // own failed nothing, because the other still saw the difference. A component added to the
        // assembly and not yet placed is a real state and the only one that isolates this half.
        ComponentDefinition bolt = new(ComponentDefinitionId.New(), "bolt.omcad", "Bolt");

        Document bare = Document.Empty(DocumentKind.Assembly);
        Document listed = bare.WithAssembly(Assembly.Empty.WithDefinition(bolt));

        listed.Assembly.Occurrences.Should().BeEmpty("this differs in the definitions alone");
        listed.Matches(bare).Should().BeFalse();
    }

    [Fact]
    public void TwoDocumentsDifferingOnlyInWhereTheyPlaceItDoNotMatch()
    {
        // The other half: the same component listed by both, placed by one.
        ComponentDefinition bolt = new(ComponentDefinitionId.New(), "bolt.omcad", "Bolt");
        Assembly listed = Assembly.Empty.WithDefinition(bolt);

        Assembly placed = listed.WithOccurrence(
            new ComponentOccurrence(OccurrenceId.New(), bolt.Id, Transform.Identity));

        Document without = Document.Empty(DocumentKind.Assembly).WithAssembly(listed);
        Document with = Document.Empty(DocumentKind.Assembly).WithAssembly(placed);

        with.Assembly.Definitions.Should().Equal(without.Assembly.Definitions);
        with.Matches(without).Should().BeFalse();
    }

    [Fact]
    public void AnAssemblySurvivesARoundTrip()
    {
        Assembly structure = WithOneBolt(out ComponentDefinition bolt, out ComponentOccurrence only);
        Document original = Document.Empty(DocumentKind.Assembly).WithAssembly(structure);

        Document read = DocumentCodec.Read(DocumentCodec.Write(original));

        read.Kind.Should().Be(DocumentKind.Assembly);
        read.Assembly.Definitions.Should().Equal(bolt);
        read.Assembly.FindOccurrence(only.Id).Should().Be(only);
    }

    [Fact]
    public void EveryPartOfAPlacementSurvivesARoundTrip()
    {
        ComponentDefinition bracket = new(
            ComponentDefinitionId.New(), "bracket.omcad", "Bracket", ComponentKind.SubAssembly);

        ComponentOccurrence placed = new(
            OccurrenceId.New(),
            bracket.Id,
            new Transform(
                Quatd.FromAxisAngle(new Vec3d(1, 2, 3), 0.7), new Vec3d(4, -5, 6), 1.0),
            IsGrounded: true,
            IsSuppressed: true,
            IsHidden: true,
            Appearance: "Anodised",
            Label: "Left bracket");

        Document original = Document.Empty(DocumentKind.Assembly)
            .WithAssembly(Assembly.Empty.WithDefinition(bracket).WithOccurrence(placed));

        ComponentOccurrence read = DocumentCodec
            .Read(DocumentCodec.Write(original))
            .Assembly.FindOccurrence(placed.Id)!;

        read.Should().Be(placed);
    }

    [Fact]
    public void AnAbsentLabelStaysAbsentRatherThanBecomingEmpty()
    {
        // "No label, so use the component's name" and "a label that happens to be empty" are
        // different states, and collapsing them renames a component on its way through a save.
        Assembly structure = WithOneBolt(out _, out ComponentOccurrence only);
        Document original = Document.Empty(DocumentKind.Assembly).WithAssembly(structure);

        ComponentOccurrence read = DocumentCodec
            .Read(DocumentCodec.Write(original))
            .Assembly.FindOccurrence(only.Id)!;

        read.Label.Should().BeNull();
        read.Appearance.Should().BeNull();
    }

    [Fact]
    public void APartWritesNoAssemblySection()
    {
        // §3 of persistence.md wants the bytes to be a function of the document. An empty section
        // in every part file would change all of them to say nothing.
        MessagePackMap written =
            (MessagePackMap)MessagePackValue.Read(DocumentCodec.Write(Document.Empty()));

        written.Find("assembly").Should().BeNull();
        written.Find("kind").Should().NotBeNull("the kind is always recorded, even for a part");
    }

    [Fact]
    public void AFileWrittenBeforeDocumentsHadAKindReadsAsAPart()
    {
        // Every file already written says nothing about its kind, and a part is what they all are.
        MessagePackMap document =
            (MessagePackMap)MessagePackValue.Read(DocumentCodec.Write(Document.Empty()));

        byte[] older = document.Without("kind").ToBytes();

        DocumentCodec.Read(older).Kind.Should().Be(DocumentKind.Part);
    }

    [Fact]
    public void AnAssemblyPlacingAComponentItDoesNotListIsRefused()
    {
        // The invariant Assembly enforces, restated as a statement about the file. A reader that
        // let it through would hand every later caller a placement whose definition is absent.
        Assembly structure = WithOneBolt(out ComponentDefinition bolt, out _);

        Document original = Document.Empty(DocumentKind.Assembly).WithAssembly(structure);

        MessagePackMap document =
            (MessagePackMap)MessagePackValue.Read(DocumentCodec.Write(original));

        MessagePackMap assembly = (MessagePackMap)document.Find("assembly")!;

        byte[] corrupt = document
            .With("assembly", assembly.With("definitions", new MessagePackArray([])))
            .ToBytes();

        FluentActions.Invoking(() => DocumentCodec.Read(corrupt))
            .Should().Throw<DocumentFormatException>()
            .WithMessage("*does not list*");

        bolt.Should().NotBeNull();
    }

    private static Assembly WithOneBolt(
        out ComponentDefinition definition, out ComponentOccurrence occurrence)
    {
        definition = new ComponentDefinition(
            ComponentDefinitionId.New(), "bolt.omcad", "Bolt");

        occurrence = new ComponentOccurrence(
            OccurrenceId.New(), definition.Id, Transform.FromTranslation(new Vec3d(1, 2, 3)));

        return Assembly.Empty.WithDefinition(definition).WithOccurrence(occurrence);
    }
}
