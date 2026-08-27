using System.Collections.Immutable;
using System.Text;

using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;
using OpenMCAD.Core.Serialization;
using OpenMCAD.Kernel;
using OpenMCAD.Math;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// The document schema and the container (P3-T18).
/// </summary>
/// <remarks>
/// Phase 3's first exit criterion is that a document built from a chain of ten features rebuilds,
/// saves, reopens and is bit-identical on re-save. The last clause is the demanding one: it means
/// the bytes have to be a function of the document alone, which rules out a wall-clock timestamp
/// anywhere in the file and any collection written in whatever order a dictionary happened to
/// enumerate.
/// </remarks>
public sealed class DocumentPackageTests
{
    private static readonly DateTimeOffset When = new(2026, 3, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ADocumentSurvivesBeingWrittenAndReadBack()
    {
        Document original = Build();

        Document read = DocumentCodec.Read(DocumentCodec.Write(original));

        read.Matches(original).Should().BeTrue(
            "everything the file carries has to come back the way it went in");
    }

    [Fact]
    public void TheSameDocumentAlwaysWritesTheSameBytes()
    {
        Document first = Build();
        Document second = Build(reverseParameterOrder: true);

        first.Matches(second).Should().BeTrue("the two are the same document, built differently");

        Convert.ToHexString(DocumentCodec.Write(first))
            .Should().Be(Convert.ToHexString(DocumentCodec.Write(second)));
    }

    [Fact]
    public void CollectionsWithNoOrderOfTheirOwnAreWrittenSorted()
    {
        // Asserted on the bytes rather than by building the document two ways and comparing, which
        // is what this test did first and which proved nothing: an ImmutableDictionary enumerates
        // by content rather than by insertion order, so both builds enumerated identically and the
        // sort could be removed without the comparison noticing.
        //
        // The guarantee is that the written order is a function of the content, so that is what is
        // checked -- directly, by reading the names back out of the encoded form.
        Document document = Build(reverseParameterOrder: true);

        ReadParameterNames(DocumentCodec.Write(document))
            .Should().Equal(["Depth", "Height", "Width"], "parameters are written in name order");
    }

    [Fact]
    public void EveryEntryCarriesTheSameFixedTimestamp()
    {
        // Also asserted directly, and for a similar reason: comparing the bytes of two saves does
        // not catch a wall-clock timestamp, because a Zip records DOS time to a two-second
        // resolution and two saves in one test land in the same tick more often than not.
        using MemoryStream stream = new(Save(Build(), Manifest()));
        using System.IO.Compression.ZipArchive archive = new(stream);

        archive.Entries.Should().NotBeEmpty();

        foreach (System.IO.Compression.ZipArchiveEntry entry in archive.Entries)
        {
            // The wall-clock value rather than the instant. A Zip stores DOS time with no
            // timezone, so it reads back with whatever offset the reading machine is in -- which
            // means the same file compares differently in two places if the instant is compared.
            entry.LastWriteTime.DateTime.Should().Be(
                new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
                $"'{entry.FullName}' must not record when it happened to be written");
        }
    }

    [Fact]
    public void ReadingAndWritingAgainGivesTheSameBytes()
    {
        // The exit criterion, at the level of the graph: whatever the reader produces has to
        // encode back to what it was read from, or a document would drift a little every time
        // somebody opened and saved it.
        byte[] once = DocumentCodec.Write(Build());
        byte[] twice = DocumentCodec.Write(DocumentCodec.Read(once));

        Convert.ToHexString(twice).Should().Be(Convert.ToHexString(once));
    }

    [Fact]
    public void APackageIsBitIdenticalWhenNothingHasChanged()
    {
        // The exit criterion at the level of the file. A Zip records the wall clock on every entry
        // unless told otherwise, which would make two saves of an unchanged document differ.
        Document document = Build();
        DocumentManifest manifest = Manifest();

        Convert.ToHexString(Save(document, manifest))
            .Should().Be(Convert.ToHexString(Save(document, manifest)));
    }

    [Fact]
    public void APackageOpensToWhatWasSaved()
    {
        Document document = Build();
        DocumentManifest manifest = Manifest();

        OpenedPackage opened = Open(Save(document, manifest));

        opened.Document.Matches(document).Should().BeTrue();
        opened.Manifest.Should().Be(manifest);
    }

    [Fact]
    public void TheManifestIsReadableWithoutThisProgram()
    {
        // It is the part a person reads when a file will not open, and the part another tool reads
        // to decide whether it wants to. A binary outermost layer costs every diagnosis after the
        // first.
        using MemoryStream stream = new(Save(Build(), Manifest()));
        using System.IO.Compression.ZipArchive archive = new(stream);

        System.IO.Compression.ZipArchiveEntry entry =
            archive.GetEntry(DocumentPackage.ManifestEntry)!;

        using StreamReader reader = new(entry.Open());
        string json = reader.ReadToEnd();

        json.Should().Contain("\"FormatVersion\"").And.Contain("\"SchemaVersion\"");
        json.Should().Contain("\"Kind\": \"Part\"", "an enum written as a number is not legible");
    }

    [Fact]
    public void TheCachesComeBackAndCanBeIgnored()
    {
        // §5.8: the caches are always regenerable and never the source of truth, so there has to
        // be a way to open without them and compare -- which is what --no-cache is for.
        PackageContents contents = new(
            ImmutableDictionary<string, byte[]>.Empty.Add("feature-1", [1, 2, 3]),
            ImmutableDictionary<string, byte[]>.Empty.Add("body-1", [4, 5, 6]));

        byte[] saved = Save(Build(), Manifest(), contents);

        OpenedPackage withCaches = Open(saved);

        withCaches.Contents.Geometry.Should().ContainKey("feature-1");
        withCaches.Contents.Tessellation.Should().ContainKey("body-1");

        OpenedPackage without = Open(saved, useCaches: false);

        without.Contents.Geometry.Should().BeEmpty();
        without.Contents.Tessellation.Should().BeEmpty();

        // And the document is the same either way, which is the point of the mode existing.
        without.Document.Matches(withCaches.Document).Should().BeTrue();
    }

    [Fact]
    public void TheOtherPartsOfTheLayoutAreKept()
    {
        PackageContents contents = new(
            ImmutableDictionary<string, byte[]>.Empty,
            ImmutableDictionary<string, byte[]>.Empty,
            Thumbnail: Encoding.UTF8.GetBytes("not really a png"),
            Previews: ImmutableDictionary<string, byte[]>.Empty.Add("default", [7]),
            ExternalReferences: Encoding.UTF8.GetBytes("{}"),
            Custom: ImmutableDictionary<string, byte[]>.Empty.Add("plugin/settings.json", [8]));

        OpenedPackage opened = Open(Save(Build(), Manifest(), contents));

        opened.Contents.Thumbnail.Should().NotBeNull();
        opened.Contents.Previews!.Should().ContainKey("default");
        opened.Contents.ExternalReferences.Should().NotBeNull();

        // Custom parts keep their extension, because this build has no idea what any of it is and
        // a plugin that wrote settings.json expects settings.json back.
        opened.Contents.Custom!.Should().ContainKey("plugin/settings.json");
    }

    [Fact]
    public void ABodyKeepsItsIdentityButNotItsHandle()
    {
        // A KernelShape is a handle into a running kernel's table and means nothing in the next
        // process. Writing it would be recording something the file cannot know.
        Document document = Build(liveShape: true);

        Body before = document.Bodies.First();
        before.Shape.IsValid.Should().BeTrue();

        Body after = DocumentCodec.Read(DocumentCodec.Write(document)).FindBody(before.Id)!;

        after.Id.Should().Be(before.Id);
        after.Owner.Should().Be(before.Owner);
        after.Kind.Should().Be(before.Kind);
        after.HasGeometry.Should().BeFalse("the geometry comes back when the document rebuilds");
    }

    [Fact]
    public void ADocumentReadBackHasTheFilesDatumsAndNotAFreshSetToo()
    {
        // An empty document starts with the standard datums, which is right for a new part and
        // wrong for one being read: the file carries its own, and adding both would give the
        // document two of each after every open.
        Document document = Build();

        int before = document.References.Length;

        DocumentCodec.Read(DocumentCodec.Write(document)).References
            .Should().HaveCount(before);
    }

    [Fact]
    public void EveryKindOfReferenceGeometrySurvives()
    {
        FeatureId owner = FeatureId.New();

        Document document = Document.Empty()
            .WithFeatureAdded(Feature.Create(owner, "Datum1", "Datum"))
            .WithReference(new ReferenceGeometry.Axis(
                owner, "Axis1", new Vec3d(1, 2, 3), Vec3d.UnitX))
            .WithReference(new ReferenceGeometry.CoordinateSystem(
                owner, "Frame1", Vec3d.Zero, Vec3d.UnitX, Vec3d.UnitZ));

        Document read = DocumentCodec.Read(DocumentCodec.Write(document));

        read.Matches(document).Should().BeTrue();
        read.References.OfType<ReferenceGeometry.CoordinateSystem>().Should().ContainSingle();
    }

    [Fact]
    public void EntityReferencesAndTheirPoliciesSurvive()
    {
        FeatureId extrude = FeatureId.New();
        FeatureId shell = FeatureId.New();

        PersistentName wall = PersistentName.Of(new NameSegment(
            extrude,
            ProvenanceKind.Generated,
            [],
            EntityRole.SideWall,
            0,
            new GeoHint(GeometryKind.Plane, 1.0, new Vec3d(1, 2, 3), Vec3d.UnitZ, 4)));

        Document document = Document.Empty()
            .WithFeatureAdded(Feature.Create(extrude, "Extrude1", "Extrude"))
            .WithFeatureAdded(Feature.Create(shell, "Shell1", "Shell") with
            {
                Inputs = [extrude],
                References = [new EntityReference(wall, MultiplicityPolicy.AllDescendants)],
            });

        Document read = DocumentCodec.Read(DocumentCodec.Write(document));

        EntityReference reference = read.FindFeature(shell)!.EntityReferences.Single();

        reference.Multiplicity.Should().Be(MultiplicityPolicy.AllDescendants);
        reference.Name.Should().Be(wall);
        reference.Name.Head.Hint.Should().Be(wall.Head.Hint, "the hint has to come back exactly");
    }

    [Fact]
    public void AFileFromANewerBuildIsRefusedRatherThanPartlyRead()
    {
        // Opening it would silently drop whatever the newer version added, and saving would then
        // write that loss back to disk.
        byte[] written = DocumentCodec.Write(Build());

        // The schema number is the first value after the map header and the "schema" key.
        int at = Array.IndexOf(written, (byte)DocumentCodec.SchemaVersion, 8);
        written[at] = 99;

        Action open = () => DocumentCodec.Read(written);

        open.Should().Throw<DocumentFormatException>().WithMessage("*newer version*");
    }

    [Fact]
    public void SomethingThatIsNotADocumentIsRefusedClearly()
    {
        using MemoryStream empty = new();

        using (System.IO.Compression.ZipArchive archive =
            new(empty, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("readme.txt");
        }

        empty.Position = 0;

        Action open = () => DocumentPackage.Open(empty);

        open.Should().Throw<DocumentFormatException>().WithMessage("*not an OpenMCAD document*");
    }

    [Fact]
    public void APackageMissingItsGraphSaysSo()
    {
        using MemoryStream stream = new();

        using (System.IO.Compression.ZipArchive archive =
            new(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using Stream entry = archive.CreateEntry(DocumentPackage.ManifestEntry).Open();
            entry.Write(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Manifest()));
        }

        stream.Position = 0;

        Action open = () => DocumentPackage.Open(stream);

        open.Should().Throw<DocumentFormatException>().WithMessage("*holds no document*");
    }

    /// <summary>A document with one of everything, and a chain of ten features.</summary>
    /// <param name="reverseParameterOrder">
    /// Whether to add the parameters the other way round, so that two builds of the same document
    /// populate their dictionaries differently.
    /// </param>
    /// <param name="liveShape">
    /// Whether the body carries a kernel handle. Off by default, because a handle is deliberately
    /// not written to the file -- so a document holding one can never equal what comes back, and a
    /// round-trip test that used one would be asserting the codec is broken. The dropping itself is
    /// tested by <see cref="ABodyKeepsItsIdentityButNotItsHandle"/>.
    /// </param>
    private static Document Build(bool reverseParameterOrder = false, bool liveShape = false)
    {
        Document document = Document.Empty();

        // Fixed ids, so that two builds of "the same document" really are the same one. Random
        // ids would make the byte comparison below compare two different documents.
        FeatureId[] ids =
        [
            .. Enumerable.Range(1, 10).Select(
                i => new FeatureId(new Guid($"00000000-0000-0000-0000-{i:D12}"))),
        ];

        for (int i = 0; i < ids.Length; ++i)
        {
            document = document.WithFeatureAdded(
                Feature.Create(ids[i], $"Feature{i}", i == 0 ? "Sketch" : "Extrude") with
                {
                    Inputs = i == 0 ? [] : [ids[i - 1]],
                    Parameters = [new Parameter("Depth", Unit.Millimetres.Of(10 + i))],
                    IsSuppressed = i == 7,
                });
        }

        string[] names = ["Width", "Height", "Depth"];

        foreach (string name in reverseParameterOrder ? names.Reverse() : names)
        {
            document = document.WithParameter(
                new Parameter(name, Unit.Millimetres.Of(name.Length), $"{name.Length}mm", "A size"));
        }

        document = document
            .WithBody(new Body(
                new BodyId(new Guid("00000000-0000-0000-0001-000000000001")),
                ids[0],
                BodyKind.Solid,
                liveShape ? new KernelShape(7) : KernelShape.None,
                "Main"))
            .WithMetadata(DocumentMetadata.Empty with
            {
                Title = "Bracket",
                PartNumber = "A-1",
                Revision = "B",
            })
            .WithRollbackPosition(9);

        return document;
    }

    private static DocumentManifest Manifest() => new(
        DocumentManifest.CurrentFormatVersion,
        DocumentCodec.SchemaVersion,
        "OpenMCAD tests",
        DocumentKind.Part,
        new Guid("00000000-0000-0000-0002-000000000001"),
        When,
        When);

    private static byte[] Save(
        Document document, DocumentManifest manifest, PackageContents? contents = null)
    {
        using MemoryStream stream = new();

        DocumentPackage.Save(stream, document, manifest, contents);

        return stream.ToArray();
    }

    /// <summary>Reads the parameter names straight out of the encoded document.</summary>
    private static ImmutableArray<string> ReadParameterNames(byte[] encoded)
    {
        MessagePackReader reader = new(encoded);

        int fields = reader.ReadMapHeader();

        for (int i = 0; i < fields; ++i)
        {
            if (reader.ReadString() != "parameters")
            {
                reader.Skip();
                continue;
            }

            int count = reader.ReadArrayHeader();
            ImmutableArray<string>.Builder names = ImmutableArray.CreateBuilder<string>(count);

            for (int p = 0; p < count; ++p)
            {
                int properties = reader.ReadMapHeader();

                for (int f = 0; f < properties; ++f)
                {
                    if (reader.ReadString() == "name")
                    {
                        names.Add(reader.ReadString());
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
            }

            return names.ToImmutable();
        }

        return [];
    }

    private static OpenedPackage Open(byte[] bytes, bool useCaches = true)
    {
        using MemoryStream stream = new(bytes);

        return DocumentPackage.Open(stream, useCaches);
    }
}
