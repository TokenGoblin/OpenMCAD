using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;
using OpenMCAD.Core.Rebuild;
using OpenMCAD.Core.Serialization;
using OpenMCAD.Kernel;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// The two ways a feature points at something already in the document, and how each is stored
/// (P5-T03).
/// </summary>
/// <remarks>
/// <para>
/// Kernel topology is an <see cref="EntityReference"/>, because it has no name of its own and has
/// to be traced through the model's history (§5.3), and because <see cref="FeatureGraph"/> reads
/// the dependency edges from there. Reference geometry is a <see cref="ReferenceValue"/> setting,
/// because it has a name it keeps and is looked up in one step.
/// </para>
/// <para>
/// What is new here is that a reference can say <em>which</em> of its feature's declared inputs it
/// satisfies. Until a feature had more than one, position in the array was the only link — and a
/// link by position stops being one the moment an input can be answered the other way instead.
/// </para>
/// </remarks>
public sealed class FeatureInputTests
{
    [Fact]
    public void AReferenceCanSayWhichInputItSatisfies()
    {
        Feature feature = Feature.Create(Id(1), "Plane1", "DatumPlaneThreePoints") with
        {
            References =
            [
                new EntityReference(Name(2), MultiplicityPolicy.ExactlyOne, "first"),
                new EntityReference(Name(3), MultiplicityPolicy.ExactlyOne, "second"),
                new EntityReference(Name(4), MultiplicityPolicy.ExactlyOne, "third"),
            ],
        };

        feature.FindSelection("first").Should().Be(0);
        feature.FindSelection("second").Should().Be(1);
        feature.FindSelection("third").Should().Be(2);
    }

    [Fact]
    public void AnInputNothingClaimsIsNotFound()
    {
        Feature feature = Feature.Create(Id(1), "Plane1", "DatumPlaneOffset") with
        {
            References = [new EntityReference(Name(2), MultiplicityPolicy.ExactlyOne, "from")],
        };

        // Minus one rather than zero. A feature with one reference would otherwise answer every
        // question about every input with that reference, which is how a plane through three points
        // gets built on the same vertex three times.
        feature.FindSelection("about").Should().Be(-1);
    }

    [Fact]
    public void AReferenceNeedNotSayWhichInputItIs()
    {
        EntityReference anonymous = new(Name(2));

        anonymous.IsNamed.Should().BeFalse();
        anonymous.Property.Should().BeEmpty();
    }

    [Fact]
    public void WhichInputAReferenceSatisfiesSurvivesARoundTrip()
    {
        Document original = WithReferences(
            new EntityReference(Name(2), MultiplicityPolicy.ExactlyOne, "first"),
            new EntityReference(Name(3), MultiplicityPolicy.AllDescendants, "second"));

        Document read = DocumentCodec.Read(DocumentCodec.Write(original));

        read.Features[0].EntityReferences.Should().Equal(original.Features[0].EntityReferences);
        read.Features[0].FindSelection("second").Should().Be(1);
    }

    [Fact]
    public void AReferenceWithNoInputNameWritesNoFieldForIt()
    {
        // Every reference written before this field existed says nothing about which input it is,
        // and a file re-saved by this build must not grow an empty string in every one of them --
        // P3-T18's exit criterion is that a re-save is bit-identical.
        //
        // Asserted on the encoded map rather than by comparing two documents' byte lengths, which
        // is what this test did first and which proved almost nothing: writing the field
        // unconditionally still leaves the anonymous one shorter, because an empty string is
        // shorter than "from". The same trap FeatureSettingTests records for the settings sort.
        Document without = WithReferences(new EntityReference(Name(2)));

        Fields(without).Should().Equal("name", "mult");

        Fields(WithReferences(new EntityReference(Name(2), MultiplicityPolicy.ExactlyOne, "from")))
            .Should().Equal("name", "mult", "prop");

        DocumentCodec.Read(DocumentCodec.Write(without)).Features[0].EntityReferences[0]
            .IsNamed.Should().BeFalse();
    }

    [Fact]
    public void APointerAtReferenceGeometrySurvivesARoundTrip()
    {
        FeatureId maker = Id(7);
        Document original = WithSetting("from", new ReferenceValue(maker, "Plane1"));

        Document read = DocumentCodec.Read(DocumentCodec.Write(original));

        read.Features[0].FindSetting("from").Should().Be(new ReferenceValue(maker, "Plane1"));
    }

    [Fact]
    public void APointerAtStandardDatumGeometrySurvivesARoundTrip()
    {
        // FeatureId.None is not a feature and is the commonest owner there is: it owns the three
        // planes and the origin every document starts with, so a datum offset from "Top" holds it.
        Document original = WithSetting("from", new ReferenceValue(FeatureId.None, "Top"));

        Document read = DocumentCodec.Read(DocumentCodec.Write(original));

        read.Features[0].FindSetting("from").Should().Be(new ReferenceValue(FeatureId.None, "Top"));
    }

    [Fact]
    public void TwoPointersAtDifferentGeometryAreDifferent()
    {
        FeatureId maker = Id(7);

        new ReferenceValue(maker, "Plane1").Should().NotBe(new ReferenceValue(maker, "Plane2"));
        new ReferenceValue(maker, "Plane1").Should().NotBe(new ReferenceValue(Id(8), "Plane1"));
        new ReferenceValue(maker, "Plane1").Should().Be(new ReferenceValue(maker, "Plane1"));
    }

    [Fact]
    public void PointingAtDifferentGeometryGivesADifferentRebuildKey()
    {
        // Caught by the first end-to-end rebuild rather than by review: RebuildKey refuses a value
        // kind it does not know, on the grounds that hashing only its type name would make two
        // different values look identical. That refusal is why a datum re-pointed at another plane
        // cannot come back from the cache unchanged.
        Feature top = Offset(new ReferenceValue(FeatureId.None, "Top"));
        Feature front = Offset(new ReferenceValue(FeatureId.None, "Front"));
        Feature owned = Offset(new ReferenceValue(Id(7), "Top"));

        RebuildKey.For(top, []).Should().NotBe(RebuildKey.For(front, []), "the name differs");
        RebuildKey.For(top, []).Should().NotBe(RebuildKey.For(owned, []), "the owner differs");
        RebuildKey.For(top, []).Should().Be(
            RebuildKey.For(Offset(new ReferenceValue(FeatureId.None, "Top")), []));
    }

    /// <summary>The field names one encoded entity reference actually carries.</summary>
    private static IEnumerable<string> Fields(Document document)
    {
        MessagePackMap feature = (MessagePackMap)
            ((MessagePackArray)((MessagePackMap)MessagePackValue.Read(DocumentCodec.Write(document)))
                .Find("features")!).Items[0];

        MessagePackMap reference = (MessagePackMap)((MessagePackArray)feature.Find("refs")!).Items[0];

        return reference.Pairs.Select(pair => ((MessagePackString)pair.Key).Value);
    }

    private static Feature Offset(ReferenceValue from)
        => Feature.Create(Id(1), "Offset1", "DatumPlaneOffset").WithSetting("from", from);

    private static FeatureId Id(int n) => new(new Guid($"00000000-0000-0000-0000-{n:D12}"));

    private static PersistentName Name(int n) => PersistentName.Of(
        NameSegment.Of(Id(n), ProvenanceKind.New, EntityRole.From(OperationRole.SideWall)));

    private static Document WithReferences(params EntityReference[] references)
    {
        DocumentSession session = new();

        using (IDocumentTransaction tx = session.BeginTransaction("Add feature"))
        {
            tx.AddFeature(Feature.Create(Id(1), "One", "DatumPlaneThreePoints") with
            {
                References = [.. references],
            });

            tx.Commit();
        }

        return session.Current;
    }

    private static Document WithSetting(string name, FeatureValue value)
    {
        DocumentSession session = new();

        using (IDocumentTransaction tx = session.BeginTransaction("Add feature"))
        {
            tx.AddFeature(Feature.Create(Id(1), "One", "DatumPlaneOffset") with
            {
                Settings = ImmutableDictionary<string, FeatureValue>.Empty.Add(name, value),
            });

            tx.Commit();
        }

        return session.Current;
    }
}
