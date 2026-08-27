using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Serialization;
using OpenMCAD.Kernel;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>One feature, as a fixture describes it.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Type">What kind of feature it is.</param>
/// <param name="Suppressed">Whether the user had switched it off.</param>
/// <param name="Inputs">How many features it takes as input.</param>
/// <param name="Parameters">Its own parameters, name to value.</param>
public sealed record FeatureExpectation(
    string Name,
    string Type,
    bool Suppressed,
    int Inputs,
    ImmutableDictionary<string, string> Parameters);

/// <summary>One document parameter, as a fixture describes it.</summary>
/// <param name="Value">Its value in SI base units, to full precision.</param>
/// <param name="Expression">What it was entered as, if it is derived.</param>
/// <param name="Description">Whatever was said about it.</param>
public sealed record ParameterExpectation(string Value, string? Expression, string? Description);

/// <summary>What a fixture in the format corpus must still contain when it is opened.</summary>
/// <param name="Schema">The schema version it was written at.</param>
/// <param name="WrittenBy">Which task of the plan produced it, for whoever finds it later.</param>
/// <param name="Title">The document title.</param>
/// <param name="PartNumber">The part number.</param>
/// <param name="Revision">The revision.</param>
/// <param name="Features">Every feature, in tree order.</param>
/// <param name="Parameters">Every document parameter, by name.</param>
/// <param name="Bodies">Every body's name, sorted.</param>
/// <param name="Rollback">Where the rollback bar sits, or null for none.</param>
/// <param name="References">Every piece of reference geometry, by name, sorted.</param>
/// <remarks>
/// <para>
/// Written out beside the fixture rather than expressed in code. The fixture's bytes are frozen the
/// day they are committed, so what they mean has to be frozen with them: an expectation built by
/// calling today's document API would drift with the API and stop describing the file.
/// </para>
/// <para>
/// Every field the schema carries appears here, down to whether a feature was suppressed and what
/// expression a derived parameter was entered as. An expectation that checked only names would let
/// a migration drop everything else without the corpus noticing, which is the one thing it exists
/// to prevent.
/// </para>
/// </remarks>
public sealed record FormatExpectation(
    int Schema,
    string WrittenBy,
    string? Title,
    string? PartNumber,
    string? Revision,
    ImmutableArray<FeatureExpectation> Features,
    ImmutableDictionary<string, ParameterExpectation> Parameters,
    ImmutableArray<string> Bodies,
    int? Rollback,
    ImmutableArray<string> References);

/// <summary>
/// The format-fixture corpus and the gate that opens every fixture in it (P3-T19).
/// </summary>
/// <remarks>
/// <para>
/// §5.8 requires that a file this project has ever written stays openable. Nothing about the
/// migration framework proves that on its own -- a chain of migrations can be perfectly composed
/// and still not produce the document the file described -- so the promise is kept by real files,
/// written by real builds, opened on every run.
/// </para>
/// <para>
/// A fixture is never regenerated. The moment its bytes are rewritten by a later build it stops
/// being evidence about the older one, and the corpus becomes a test that this build can read what
/// this build just wrote, which is a thing every round-trip test already says.
/// </para>
/// </remarks>
public sealed class FormatFixtureTests
{
    private static readonly JsonSerializerOptions ExpectationFormat = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        NewLine = "\n",
    };

    /// <summary>Every fixture in the corpus, as xUnit theory data.</summary>
    public static TheoryData<string> Fixtures()
    {
        TheoryData<string> data = [];

        foreach (FileInfo fixture in FixtureFiles())
        {
            data.Add(fixture.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EveryHistoricalFixtureStillOpens(string name)
    {
        FileInfo fixture = FixtureFiles().Single(f => f.Name == name);

        using FileStream stream = fixture.OpenRead();
        OpenedPackage opened = DocumentPackage.Open(stream);

        FormatExpectation expected = Expectation(fixture);

        opened.Manifest.SchemaVersion.Should().Be(
            expected.Schema, "the fixture is evidence about the version it names");

        Document document = opened.Document;

        document.Metadata.Title.Should().Be(expected.Title);
        document.Metadata.PartNumber.Should().Be(expected.PartNumber);
        document.Metadata.Revision.Should().Be(expected.Revision);
        document.RollbackPosition.Should().Be(expected.Rollback);

        document.Bodies.Select(b => b.Name ?? string.Empty).Order(StringComparer.Ordinal)
            .Should().Equal(expected.Bodies);
        document.References.Select(r => r.Name).Order(StringComparer.Ordinal)
            .Should().Equal(expected.References);

        Describe(document).Features.Should().BeEquivalentTo(
            expected.Features, options => options.WithStrictOrdering());

        Describe(document).Parameters.Should().BeEquivalentTo(expected.Parameters);
    }

    [Fact]
    public void EverySchemaVersionThisBuildCanWriteHasAFixture()
    {
        // The gate that makes the corpus grow. Raising SchemaVersion without leaving behind a file
        // written at the version being abandoned means that version becomes unverifiable the moment
        // the code that wrote it is gone -- and the migration out of it is then a claim nobody can
        // check. Failing here is the only reminder that arrives at the right time.
        ImmutableArray<int> have = [.. FixtureFiles().Select(f => Expectation(f).Schema).Order()];

        ImmutableArray<int> want = [.. Enumerable.Range(1, DocumentCodec.SchemaVersion)];

        if (have.Contains(DocumentCodec.SchemaVersion))
        {
            have.Should().Contain(want, "every version in between has to be represented too");
            return;
        }

        // Written where a failing run can hand it over rather than leaving whoever raised the
        // version to work out how to produce one.
        FileInfo candidate = WriteCandidate();

        Assert.Fail(
            $"The format corpus has no fixture for schema {DocumentCodec.SchemaVersion}. One has "
            + $"been written to {candidate.FullName}; copy it and its .json into "
            + $"{FindCorpus().FullName} and check the expectation describes it.");
    }

    [Fact]
    public void EveryFixtureOlderThanThisBuildHasAChainToReachIt()
    {
        // Opening the fixture proves this too, and only for the versions that happen to be present.
        // Asserted directly so that a missing step is reported as a missing step rather than as a
        // file that would not open.
        foreach (int version in FixtureFiles().Select(f => Expectation(f).Schema).Distinct())
        {
            for (int step = version; step < DocumentCodec.SchemaVersion; ++step)
            {
                SchemaMigrator.Migrations.Should().ContainSingle(
                    m => m.From == step,
                    "a fixture at schema {0} needs a way out of {1}",
                    version,
                    step);
            }
        }
    }

    [Fact]
    public void TheCorpusIsNotEmpty()
    {
        // A corpus that quietly found nothing to run would make every theory above pass by having
        // no cases, which is the failure mode a fixture-driven gate is most prone to.
        FixtureFiles().Should().NotBeEmpty("the corpus is what P3-T19 is for");
    }

    /// <summary>Writes a package at the current schema, for a version that has no fixture yet.</summary>
    /// <returns>Where it was written.</returns>
    /// <remarks>
    /// The document is deliberately varied rather than minimal: a fixture that exercised one field
    /// would let a migration break every other one without the corpus noticing.
    /// </remarks>
    private static FileInfo WriteCandidate()
    {
        Document document = FixtureDocument();

        DocumentManifest manifest = new(
            DocumentManifest.CurrentFormatVersion,
            DocumentCodec.SchemaVersion,
            $"OpenMCAD schema {DocumentCodec.SchemaVersion} fixture",
            DocumentKind.Part,
            new Guid("00000000-0000-0000-000f-000000000001"),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        DirectoryInfo output = new(Path.Combine(AppContext.BaseDirectory, "format-candidate"));
        output.Create();

        string stem = $"schema-{DocumentCodec.SchemaVersion:D3}";
        FileInfo package = new(Path.Combine(output.FullName, stem + ".ompart"));

        using (FileStream stream = package.Create())
        {
            DocumentPackage.Save(stream, document, manifest);
        }

        FormatExpectation expectation = Describe(document);

        File.WriteAllText(
            Path.Combine(output.FullName, stem + ".json"),
            JsonSerializer.Serialize(expectation, ExpectationFormat) + "\n");

        return package;
    }

    /// <summary>The document every fixture holds: one of everything the schema can carry.</summary>
    private static Document FixtureDocument()
    {
        Document document = Document.Empty();

        FeatureId[] ids =
        [
            .. Enumerable.Range(1, 4).Select(
                i => new FeatureId(new Guid($"00000000-0000-0000-00fa-{i:D12}"))),
        ];

        string[] kinds = ["Sketch", "Extrude", "Fillet", "Pattern"];

        for (int i = 0; i < ids.Length; ++i)
        {
            document = document.WithFeatureAdded(
                Feature.Create(ids[i], $"Feature{i}", kinds[i]) with
                {
                    Inputs = i == 0 ? [] : [ids[i - 1]],
                    Parameters = [new Parameter("Depth", Unit.Millimetres.Of(10 + i))],
                    IsSuppressed = i == 3,
                });
        }

        return document
            .WithParameter(new Parameter("Width", Unit.Millimetres.Of(40)))
            .WithParameter(new Parameter("Height", Unit.Millimetres.Of(25), "Width * 0.625", "Tall"))
            .WithParameter(new Parameter("Angle", Unit.Degrees.Of(30)))
            .WithBody(new Body(
                new BodyId(new Guid("00000000-0000-0000-00fb-000000000001")),
                ids[1],
                BodyKind.Solid,
                KernelShape.None,
                "Main"))
            .WithMetadata(DocumentMetadata.Empty with
            {
                Title = "Format fixture",
                PartNumber = "F-1",
                Revision = "A",
            })
            .WithRollbackPosition(3);
    }

    /// <summary>Describes a document the way a fixture's expectation describes one.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The description.</returns>
    /// <remarks>
    /// The one place a document is turned into an expectation, used both to write a candidate and
    /// to check a fixture. Two spellings of the same thing would make fixtures fail for reasons
    /// having nothing to do with the format.
    /// </remarks>
    private static FormatExpectation Describe(Document document) => new(
        DocumentCodec.SchemaVersion,
        "OpenMCAD P3-T19",
        document.Metadata.Title,
        document.Metadata.PartNumber,
        document.Metadata.Revision,
        [
            .. document.Features.Select(f => new FeatureExpectation(
                f.Name,
                f.FeatureType,
                f.IsSuppressed,
                f.Inputs.Length,
                f.Parameters.ToImmutableDictionary(p => p.Name, p => Text(p.Value)))),
        ],
        document.Parameters.ToImmutableDictionary(
            p => p.Name,
            p => new ParameterExpectation(Text(p.Value), p.Expression, p.Description)),
        [.. document.Bodies.Select(b => b.Name ?? string.Empty).Order(StringComparer.Ordinal)],
        document.RollbackPosition,
        [.. document.References.Select(r => r.Name).Order(StringComparer.Ordinal)]);

    /// <summary>How a quantity is written in an expectation, and never anything else.</summary>
    /// <remarks>
    /// Round-trippable rather than <see cref="Quantity.ToString"/>, which rounds to six decimals
    /// for display. Thirty degrees stored in radians is 0.5235987755982988, and an expectation
    /// recording 0.523599 would let a migration move a value by a part in ten thousand and still
    /// pass -- which for a dimension is a real change and not a display detail.
    /// </remarks>
    private static string Text(Quantity value) => string.Create(
        CultureInfo.InvariantCulture,
        $"{value.Value:R} {value.Dimension}");

    private static FormatExpectation Expectation(FileInfo fixture)
    {
        string beside = Path.ChangeExtension(fixture.FullName, ".json");

        File.Exists(beside).Should().BeTrue(
            "every fixture needs a description of what it holds, at {0}", beside);

        return JsonSerializer.Deserialize<FormatExpectation>(
            File.ReadAllText(beside), ExpectationFormat)!;
    }

    private static ImmutableArray<FileInfo> FixtureFiles()
        => [.. FindCorpus().GetFiles("*.ompart").OrderBy(f => f.Name, StringComparer.Ordinal)];

    /// <summary>Finds <c>tests/fixtures/format</c>, walking up from the running assembly.</summary>
    private static DirectoryInfo FindCorpus()
    {
        DirectoryInfo? candidate = new(AppContext.BaseDirectory);

        while (candidate is not null)
        {
            string corpus = Path.Combine(candidate.FullName, "tests", "fixtures", "format");

            if (Directory.Exists(corpus))
            {
                return new DirectoryInfo(corpus);
            }

            candidate = candidate.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find tests/fixtures/format. It is the format corpus and it is not optional.");
    }
}
