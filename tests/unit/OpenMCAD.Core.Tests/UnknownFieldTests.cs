using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Serialization;
using OpenMCAD.Kernel;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// Unknown-field preservation on round-trip (P3-T20).
/// </summary>
/// <remarks>
/// <para>
/// The case that matters is not a corrupt file or a hostile one. It is a colleague on a newer build
/// sending a part, someone here opening it to look at a dimension, and saving out of habit. A
/// reader that dropped what it could not read would delete that colleague's work with no error, no
/// warning, and nothing to notice until much later.
/// </para>
/// <para>
/// Every test here writes the newer build's file by hand, because there is no newer build to write
/// one. The tree from P3-T19 is what makes that possible: a field can be added to a real encoded
/// document at any level without a parser having to be written for the occasion.
/// </para>
/// </remarks>
public sealed class UnknownFieldTests
{
    [Fact]
    public void AFieldTheDocumentDoesNotUnderstandSurvivesBeingOpenedAndSaved()
    {
        byte[] newer = WithField(Encoded(), UnknownField.Root, "wibble", Text("kept"));

        byte[] resaved = DocumentCodec.Write(DocumentCodec.Read(newer));

        FieldOf(resaved, UnknownField.Root, "wibble").Should().Equal(Text("kept"));
    }

    [Fact]
    public void AFieldOfAFeatureSurvives()
    {
        // The likeliest schema change of all: a new build gives features a property. Preserving
        // only the document's own fields would keep the version number and lose the feature data.
        byte[] newer = WithField(Encoded(), Owns(0), "draft", Text("3deg"));

        byte[] resaved = DocumentCodec.Write(DocumentCodec.Read(newer));

        FieldOf(resaved, Owns(0), "draft").Should().Equal(Text("3deg"));
    }

    [Theory]
    [InlineData("parameter:Width")]
    [InlineData("body:00000000-0000-0000-0001-000000000001")]
    [InlineData("reference:Front")]
    [InlineData("metadata")]
    public void AFieldOfAnythingTheSchemaCarriesSurvives(string owner)
    {
        byte[] newer = WithField(Encoded(), owner, "extra", Text("kept"));

        byte[] resaved = DocumentCodec.Write(DocumentCodec.Read(newer));

        FieldOf(resaved, owner, "extra").Should().Equal(Text("kept"));
    }

    [Fact]
    public void AFieldOfAParameterInsideAFeatureSurvives()
    {
        // Two levels down, and the reason the owner of a kept field is built up as the reader
        // returns rather than known when it is read: the parameter's name and the feature's id may
        // both come after the field itself.
        // Spelled out rather than built with UnknownField.Nest. A test that computes its
        // expectation with the code under test agrees with that code however wrong it is: nesting
        // could collapse entirely and both sides would move together.
        string owner = "feature:00000000-0000-0000-0000-000000000001/parameter:Depth";

        byte[] newer = WithField(Encoded(), owner, "tolerance", Text("h7"));

        byte[] resaved = DocumentCodec.Write(DocumentCodec.Read(newer));

        FieldOf(resaved, owner, "tolerance").Should().Equal(Text("h7"));

        // Once, and on the parameter rather than on the feature around it. Filing a nested field
        // under its container writes it out at both levels, which preserves the data and quietly
        // doubles it -- and the next build to read the file finds the same field in two places
        // with no way to tell which one it meant.
        Occurrences(resaved, "tolerance").Should().Be(1);
    }

    /// <summary>How many times a key appears in an encoded document.</summary>
    private static int Occurrences(byte[] encoded, string key)
    {
        ReadOnlySpan<byte> needle = System.Text.Encoding.UTF8.GetBytes(key);
        ReadOnlySpan<byte> rest = encoded;

        int count = 0;

        while (true)
        {
            int at = rest.IndexOf(needle);

            if (at < 0)
            {
                return count;
            }

            ++count;
            rest = rest[(at + needle.Length)..];
        }
    }

    [Fact]
    public void AKeptFieldSurvivesEditingTheDocument()
    {
        // The whole point. Held at the file boundary rather than on the document, a kept field
        // would be dropped by the first edit -- and editing is what someone opening the file does.
        byte[] newer = WithField(Encoded(), UnknownField.Root, "wibble", Text("kept"));

        Document opened = DocumentCodec.Read(newer);
        Document edited = opened
            .WithParameter(new Parameter("Width", Unit.Millimetres.Of(99)))
            .WithRollbackPosition(1)
            .WithMetadata(opened.Metadata with { Title = "Renamed" });

        FieldOf(DocumentCodec.Write(edited), UnknownField.Root, "wibble").Should().Equal(Text("kept"));
    }

    [Fact]
    public void AKeptFieldOfAFeatureThatIsDeletedIsNotWrittenBack()
    {
        // A field belongs to something. When the user deletes that something the field described a
        // feature that is gone, and writing it back would leave a file describing a feature it does
        // not contain. It stays on the document in memory, so undoing the deletion brings it back
        // with the feature -- it is dropped by not being visited on the way out, not by being
        // deleted on the way in.
        byte[] newer = WithField(Encoded(), Owns(0), "draft", Text("3deg"));

        Document opened = DocumentCodec.Read(newer);
        Document trimmed = opened.WithFeatureRemoved(opened.Features[0].Id);

        Holds(Encoded(opened), "draft").Should().BeTrue("the field is there to begin with");
        Holds(Encoded(trimmed), "draft").Should().BeFalse();
    }

    /// <summary>Whether an encoded document contains a run of bytes spelling a key.</summary>
    /// <remarks>
    /// A subsequence search, not <c>Should().Contain</c> on the bytes: that checks each element
    /// separately, so every letter of "draft" appearing anywhere in the file would satisfy it -- as
    /// they all do, in "Front" and "Right".
    /// </remarks>
    private static bool Holds(byte[] encoded, string key)
        => encoded.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(key).AsSpan()) >= 0;

    [Fact]
    public void KeptFieldsAreWrittenBackDeterministically()
    {
        // Two saves of the same document have to agree, or P3-T18's first exit criterion holds only
        // for documents that came from this build.
        byte[] newer = WithField(
            WithField(Encoded(), UnknownField.Root, "b", Text("two")),
            UnknownField.Root,
            "a",
            Text("one"));

        Document opened = DocumentCodec.Read(newer);

        DocumentCodec.Write(opened).Should().Equal(DocumentCodec.Write(opened));
        DocumentCodec.Write(DocumentCodec.Read(DocumentCodec.Write(opened)))
            .Should().Equal(DocumentCodec.Write(opened), "and a second trip changes nothing either");
    }

    [Fact]
    public void KeptFieldsKeepTheirOrderRelativeToEachOther()
    {
        byte[] newer = WithField(
            WithField(Encoded(), UnknownField.Root, "second", Text("2")),
            UnknownField.Root,
            "first",
            Text("1"));

        Document opened = DocumentCodec.Read(newer);

        opened.UnknownFields.Select(f => f.Field).Should().Equal("second", "first");
    }

    [Fact]
    public void ADocumentWithNothingUnknownWritesExactlyWhatItAlwaysDid()
    {
        // The preservation machinery must be invisible to an ordinary document, or every fixture in
        // the format corpus stops matching for a reason that has nothing to do with the corpus.
        Document plain = Build();

        DocumentCodec.Read(DocumentCodec.Write(plain)).UnknownFields.Should().BeEmpty();
        DocumentCodec.Write(DocumentCodec.Read(DocumentCodec.Write(plain)))
            .Should().Equal(DocumentCodec.Write(plain));
    }

    [Fact]
    public void AValueOfAnyShapeIsKeptWhole()
    {
        // A newer build's field is as likely to be a map of arrays as a string, and a reader that
        // kept only scalars would land in the middle of a structure it had half-consumed.
        MessagePackWriter nested = new();
        nested.WriteMapHeader(1);
        nested.Write("inner");
        nested.WriteArrayHeader(2);
        nested.Write(1);
        nested.Write("two");

        byte[] value = nested.ToArray();

        byte[] newer = WithField(Encoded(), Owns(1), "structure", value);

        FieldOf(DocumentCodec.Write(DocumentCodec.Read(newer)), Owns(1), "structure")
            .Should().Equal(value);
    }

    [Fact]
    public void TwoDocumentsDifferingOnlyInWhatTheyKeepAreNotTheSame()
    {
        // Matches compares everything else the document carries, and a comparison that skipped
        // these would report an undo as having restored a state it had not restored.
        Document plain = DocumentCodec.Read(Encoded());
        Document carrying = DocumentCodec.Read(
            WithField(Encoded(), UnknownField.Root, "wibble", Text("kept")));

        plain.Matches(carrying).Should().BeFalse();
    }

    /// <summary>The owner key of the nth feature of the test document.</summary>
    private static string Owns(int index)
        => UnknownField.Feature($"00000000-0000-0000-0000-{index + 1:D12}");

    private static byte[] Text(string value)
    {
        MessagePackWriter writer = new();
        writer.Write(value);

        return writer.ToArray();
    }

    /// <summary>Adds a field to an encoded document, the way a newer build would have written it.</summary>
    /// <param name="encoded">The document.</param>
    /// <param name="owner">What to attach it to, in the same form the reader files things under.</param>
    /// <param name="field">The key.</param>
    /// <param name="value">The encoded value.</param>
    /// <returns>The document, with the field in it.</returns>
    private static byte[] WithField(byte[] encoded, string owner, string field, byte[] value)
    {
        MessagePackValue added = MessagePackValue.Read(value);
        MessagePackMap document = (MessagePackMap)MessagePackValue.Read(encoded);

        return Insert(document, owner, field, added).ToBytes();
    }

    private static MessagePackMap Insert(
        MessagePackMap map, string owner, string field, MessagePackValue value)
    {
        if (owner.Length == 0)
        {
            return map.With(field, value);
        }

        int slash = owner.IndexOf('/', StringComparison.Ordinal);
        string head = slash < 0 ? owner : owner[..slash];
        string rest = slash < 0 ? string.Empty : owner[(slash + 1)..];

        if (head == "metadata")
        {
            return map.With(
                "metadata", Insert((MessagePackMap)map.Find("metadata")!, rest, field, value));
        }

        (string collection, string key, string identifier) = head switch
        {
            _ when head.StartsWith("feature:", StringComparison.Ordinal)
                => ("features", "id", head["feature:".Length..]),
            _ when head.StartsWith("parameter:", StringComparison.Ordinal)
                => ("parameters", "name", head["parameter:".Length..]),
            _ when head.StartsWith("body:", StringComparison.Ordinal)
                => ("bodies", "id", head["body:".Length..]),
            _ when head.StartsWith("reference:", StringComparison.Ordinal)
                => ("references", "name", head["reference:".Length..]),
            _ => throw new ArgumentException($"No idea what {head} is.", nameof(owner)),
        };

        MessagePackArray items = (MessagePackArray)map.Find(collection)!;

        return map.With(
            collection,
            items.Select(item => Names((MessagePackMap)item, key) == identifier
                ? Insert((MessagePackMap)item, rest, field, value)
                : item));
    }

    private static string Names(MessagePackMap map, string key)
        => map.Find(key) is MessagePackString text ? text.Value : string.Empty;

    /// <summary>Reads a field back out of an encoded document, wherever it is.</summary>
    private static ImmutableArray<byte> FieldOf(byte[] encoded, string owner, string field)
    {
        MessagePackMap map = (MessagePackMap)MessagePackValue.Read(encoded);

        MessagePackValue? found = Locate(map, owner)?.Find(field);

        found.Should().NotBeNull("the field has to still be there, under {0}", owner);

        return [.. found!.ToBytes()];
    }

    private static MessagePackMap? Locate(MessagePackMap map, string owner)
    {
        if (owner.Length == 0)
        {
            return map;
        }

        int slash = owner.IndexOf('/', StringComparison.Ordinal);
        string head = slash < 0 ? owner : owner[..slash];
        string rest = slash < 0 ? string.Empty : owner[(slash + 1)..];

        if (head == "metadata")
        {
            return Locate((MessagePackMap)map.Find("metadata")!, rest);
        }

        (string collection, string key, string identifier) = head switch
        {
            _ when head.StartsWith("feature:", StringComparison.Ordinal)
                => ("features", "id", head["feature:".Length..]),
            _ when head.StartsWith("parameter:", StringComparison.Ordinal)
                => ("parameters", "name", head["parameter:".Length..]),
            _ when head.StartsWith("body:", StringComparison.Ordinal)
                => ("bodies", "id", head["body:".Length..]),
            _ when head.StartsWith("reference:", StringComparison.Ordinal)
                => ("references", "name", head["reference:".Length..]),
            _ => throw new ArgumentException($"No idea what {head} is.", nameof(owner)),
        };

        MessagePackArray items = (MessagePackArray)map.Find(collection)!;

        MessagePackMap? match = items.Items
            .Cast<MessagePackMap>()
            .FirstOrDefault(item => Names(item, key) == identifier);

        return match is null ? null : Locate(match, rest);
    }

    private static byte[] Encoded() => Encoded(Build());

    private static byte[] Encoded(Document document) => DocumentCodec.Write(document);

    /// <summary>A document with a feature chain, parameters, a body and datums.</summary>
    private static Document Build()
    {
        Document document = Document.Empty();

        FeatureId[] ids =
        [
            .. Enumerable.Range(1, 3).Select(
                i => new FeatureId(new Guid($"00000000-0000-0000-0000-{i:D12}"))),
        ];

        for (int i = 0; i < ids.Length; ++i)
        {
            document = document.WithFeatureAdded(
                Feature.Create(ids[i], $"Feature{i}", i == 0 ? "Sketch" : "Extrude") with
                {
                    Inputs = i == 0 ? [] : [ids[i - 1]],
                    Parameters = [new Parameter("Depth", Unit.Millimetres.Of(10 + i))],
                });
        }

        return document
            .WithParameter(new Parameter("Width", Unit.Millimetres.Of(40)))
            .WithBody(new Body(
                new BodyId(new Guid("00000000-0000-0000-0001-000000000001")),
                ids[0],
                BodyKind.Solid,
                KernelShape.None,
                "Main"))
            .WithMetadata(DocumentMetadata.Empty with { Title = "Carrier" });
    }
}
