using System.Text.Json;

using FluentAssertions;

using OpenMCAD.Cli;
using OpenMCAD.Core.Documents;
using OpenMCAD.Modeling;

using Xunit;

namespace OpenMCAD.Cli.Tests;

/// <summary>
/// The headless document API (P3-T22).
/// </summary>
/// <remarks>
/// <para>
/// Called directly rather than by starting <c>omcad.exe</c>. A test that spawned a process to read
/// an exit code would be slow, awkward to debug, and blind to anything the command did not print —
/// and the commands are written to return an exit code and take a writer precisely so that this is
/// possible.
/// </para>
/// <para>
/// What that leaves untested is the argument parsing in <c>Program</c>, which is a declaration
/// rather than logic and which the tool's own <c>--help</c> exercises.
/// </para>
/// </remarks>
public sealed class DocumentCommandTests : IDisposable
{
    private const string Bracket = """
        {
          "title": "Bracket",
          "partNumber": "B-100",
          "revision": "A",
          "parameters": [
            { "name": "Width", "value": 40, "unit": "mm" },
            { "name": "Height", "value": 25, "unit": "mm", "expression": "Width * 0.625" }
          ],
          "features": [
            { "name": "BasePlate", "type": "Sketch" },
            {
              "name": "Body",
              "type": "Extrude",
              "inputs": ["BasePlate"],
              "parameters": [{ "name": "depth", "value": 12, "unit": "mm" }],
              "settings": [{ "name": "end", "choice": "Blind" }, { "name": "merge", "flag": true }]
            },
            { "name": "Corners", "type": "Fillet", "inputs": ["Body"], "suppressed": true }
          ],
          "rollback": 3
        }
        """;

    private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory("omcad-cli-tests");

    /// <inheritdoc/>
    public void Dispose() => _work.Delete(recursive: true);

    [Fact]
    public void ASpecBecomesADocument()
    {
        StringWriter said = new();

        DocumentCommands.Build(Spec(Bracket), Path("bracket.ompart"), false, said)
            .Should().Be(DocumentCommands.Ok);

        said.ToString().Should().Contain("3 features");
        Path("bracket.ompart").Exists.Should().BeTrue();
    }

    [Fact]
    public void BuildingTheSameSpecTwiceProducesTheSameFile()
    {
        // Ids come from feature names and the manifest timestamps are fixed, so a document built by
        // a test can be compared with a stored one. A build stamped with the clock could not be,
        // and every later phase builds its fixtures through this command.
        DocumentCommands.Build(Spec(Bracket), Path("one.ompart"), false, StringWriter.Null);
        DocumentCommands.Build(Spec(Bracket), Path("two.ompart"), false, StringWriter.Null);

        File.ReadAllBytes(Path("one.ompart").FullName)
            .Should().Equal(File.ReadAllBytes(Path("two.ompart").FullName));
    }

    [Fact]
    public void InspectDescribesWhatIsInTheFile()
    {
        Build(Bracket, "bracket.ompart");

        StringWriter said = new();

        DocumentCommands.Inspect(Path("bracket.ompart"), false, said)
            .Should().Be(DocumentCommands.Ok);

        string text = said.ToString();

        text.Should().Contain("Bracket").And.Contain("B-100");
        text.Should().Contain("BasePlate (Sketch)");
        text.Should().Contain("suppressed");
        text.Should().Contain("[Width * 0.625]", "a derived parameter's expression is the point of it");
    }

    [Fact]
    public void InspectCanAnswerInJson()
    {
        // What a later phase's test reads. Asserting on a field rather than on the wording of a
        // sentence means the wording can be improved without breaking anything.
        Build(Bracket, "bracket.ompart");

        StringWriter said = new();
        DocumentCommands.Inspect(Path("bracket.ompart"), true, said);

        JsonElement root = JsonDocument.Parse(said.ToString()).RootElement;

        root.GetProperty("title").GetString().Should().Be("Bracket");
        root.GetProperty("schema").GetInt32().Should().Be(1);
        root.GetProperty("rollback").GetInt32().Should().Be(3);
        root.GetProperty("features").GetArrayLength().Should().Be(3);
        root.GetProperty("features")[2].GetProperty("suppressed").GetBoolean().Should().BeTrue();
        root.GetProperty("parameters")[0].GetProperty("name").GetString().Should().Be("Height");
    }

    [Fact]
    public void SavingADocumentAgainProducesTheSameBytes()
    {
        // §5.8's first exit criterion, checkable from a shell rather than only from inside a test.
        Build(Bracket, "bracket.ompart");

        StringWriter said = new();

        DocumentCommands.Save(Path("bracket.ompart"), Path("again.ompart"), true, said)
            .Should().Be(DocumentCommands.Ok);

        JsonDocument.Parse(said.ToString()).RootElement
            .GetProperty("identical").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void TwoBuildsOfOneSpecAreTheSameDocument()
    {
        Build(Bracket, "one.ompart");
        Build(Bracket, "two.ompart");

        StringWriter said = new();

        DocumentCommands.Diff(Path("one.ompart"), Path("two.ompart"), false, said)
            .Should().Be(DocumentCommands.Ok);

        said.ToString().Should().Contain("the same model");
    }

    [Fact]
    public void DiffSaysWhatIsDifferentAndExitsNonZero()
    {
        Build(Bracket, "one.ompart");
        Build(Bracket.Replace("\"value\": 40", "\"value\": 45", StringComparison.Ordinal), "two.ompart");

        StringWriter said = new();

        DocumentCommands.Diff(Path("one.ompart"), Path("two.ompart"), false, said)
            .Should().Be(DocumentCommands.Negative, "a shell reads the exit code, not the words");

        said.ToString().Should().Contain("parameter 'Width'");
    }

    [Fact]
    public void DiffNoticesFeaturesInADifferentOrder()
    {
        // The tree order is what the user arranged and what a rebuild follows. A diff that compared
        // the two as sets would call a reorder no difference at all.
        const string Forwards = """
            { "features": [
                { "name": "A", "type": "Sketch" },
                { "name": "B", "type": "Sketch" },
                { "name": "C", "type": "Sketch" } ] }
            """;

        const string Backwards = """
            { "features": [
                { "name": "C", "type": "Sketch" },
                { "name": "B", "type": "Sketch" },
                { "name": "A", "type": "Sketch" } ] }
            """;

        DocumentCommands.Differences(
            DocumentSpec.Parse(Forwards).Build(), DocumentSpec.Parse(Backwards).Build())
            .Should().ContainSingle().Which.Should().Contain("different order");
    }

    [Fact]
    public void DiffComparesDocumentsRatherThanBytes()
    {
        // Two files can differ byte for byte and describe the same model -- a different manifest
        // application string is enough -- and a diff that reported those would be useless.
        Build(Bracket, "one.ompart");
        DocumentCommands.Build(Spec(Bracket, "other-name.json"), Path("two.ompart"), false, StringWriter.Null);

        File.ReadAllBytes(Path("one.ompart").FullName)
            .Should().NotEqual(File.ReadAllBytes(Path("two.ompart").FullName));

        DocumentCommands.Diff(Path("one.ompart"), Path("two.ompart"), false, StringWriter.Null)
            .Should().Be(DocumentCommands.Ok);
    }

    [Fact]
    public void RebuildSaysNothingIsWrongWhenNothingIs()
    {
        Build(Bracket, "bracket.ompart");

        FeatureCatalogue catalogue = FeatureCatalogue.Of(
        [
            FeatureSchema.Create("Sketch", "Sketch", "Create", []),
            FeatureSchema.Create("Extrude", "Extrude", "Create", []),
            FeatureSchema.Create("Fillet", "Fillet", "Modify", []),
        ]);

        StringWriter said = new();

        DocumentCommands.Rebuild(Path("bracket.ompart"), true, said, catalogue)
            .Should().Be(DocumentCommands.Ok);

        JsonElement root = JsonDocument.Parse(said.ToString()).RootElement;

        root.GetProperty("errors").GetInt32().Should().Be(0);
        root.GetProperty("warnings").GetInt32().Should().Be(
            2, "the extrude has two settings nothing in that catalogue declares");
    }

    [Fact]
    public void RebuildExitsNonZeroWhenAFeatureCannotBeBuilt()
    {
        Build(Bracket, "bracket.ompart");

        FeatureCatalogue catalogue = FeatureCatalogue.Of(
        [
            FeatureSchema.Create("Sketch", "Sketch", "Create", []),
            FeatureSchema.Create(
                "Extrude",
                "Extrude",
                "Create",
                [new FeatureProperty("angle", "Angle", PropertyKind.Quantity, Dimension: Dimension.Angle)]),
            FeatureSchema.Create("Fillet", "Fillet", "Modify", []),
        ]);

        StringWriter said = new();

        DocumentCommands.Rebuild(Path("bracket.ompart"), false, said, catalogue)
            .Should().Be(DocumentCommands.Negative);

        said.ToString().Should().Contain("error").And.Contain("Angle");
    }

    [Fact]
    public void AFeatureNobodyKnowsIsOnlyAWarning()
    {
        // An uninstalled plugin costing the user one feature is survivable; costing them the file
        // is not, so the empty catalogue this build ships with does not fail every document.
        Build(Bracket, "bracket.ompart");

        DocumentCommands.Rebuild(Path("bracket.ompart"), false, StringWriter.Null)
            .Should().Be(DocumentCommands.Ok);
    }

    [Fact]
    public void InspectSaysWhenAFileCarriesFieldsFromANewerBuild()
    {
        // Someone about to edit and save a colleague's file is entitled to know that this build
        // cannot read all of it. P3-T20 keeps those fields; this is the only place a person is
        // told they exist.
        Build(Bracket, "bracket.ompart");
        AddAFieldFromTheFuture(Path("bracket.ompart"));

        StringWriter said = new();
        DocumentCommands.Inspect(Path("bracket.ompart"), true, said);

        JsonDocument.Parse(said.ToString()).RootElement
            .GetProperty("carried").GetInt32().Should().Be(1);

        StringWriter aloud = new();
        DocumentCommands.Inspect(Path("bracket.ompart"), false, aloud);

        aloud.ToString().Should().Contain("from a newer version");
    }

    /// <summary>Adds a top-level field to a package's document, as a newer build would have.</summary>
    /// <remarks>
    /// Written as bytes rather than through the codec, because the codec by definition cannot write
    /// a field it does not know. The document is a MessagePack map: raising the header's count by
    /// one and appending a key and a value is exactly what a build with one more field would emit.
    /// </remarks>
    private static void AddAFieldFromTheFuture(FileInfo package)
    {
        using System.IO.Compression.ZipArchive archive = System.IO.Compression.ZipFile.Open(
            package.FullName, System.IO.Compression.ZipArchiveMode.Update);

        System.IO.Compression.ZipArchiveEntry entry = archive.GetEntry("document.msgpack")!;

        byte[] payload;

        using (Stream reading = entry.Open())
        using (MemoryStream copy = new())
        {
            reading.CopyTo(copy);
            payload = copy.ToArray();
        }

        payload[0].Should().Be(0x87, "the document is a seven-field map");
        payload[0] = 0x88;

        byte[] appended =
        [
            .. payload,
            0xA6, (byte)'w', (byte)'i', (byte)'b', (byte)'b', (byte)'l', (byte)'e',
            0xC3,
        ];

        entry.Delete();

        using Stream writing = archive.CreateEntry("document.msgpack").Open();
        writing.Write(appended);
    }

    [Theory]
    [InlineData("{ \"features\": [ { \"name\": \"A\", \"type\": \"X\" }, { \"name\": \"A\", \"type\": \"Y\" } ] }",
        "Two features are called")]
    [InlineData("{ \"features\": [ { \"name\": \"A\", \"type\": \"X\", \"inputs\": [\"Nowhere\"] } ] }",
        "not a feature declared before it")]
    [InlineData("{ \"parameters\": [ { \"name\": \"W\", \"value\": 1, \"unit\": \"furlong\" } ] }",
        "not a unit this build knows")]
    [InlineData("{ \"features\": [], \"rollback\": 4 }", "rollback bar is at 4")]
    [InlineData("{ \"features\": [ { \"name\": \"A\", \"type\": \"X\", \"settings\": [ { \"name\": \"s\", \"flag\": true, \"number\": 2 } ] } ] }",
        "can only have one")]
    [InlineData("{ \"features\": [ { \"name\": \"A\", \"type\": \"X\", \"settings\": [ { \"name\": \"s\" } ] } ] }",
        "has no value")]
    [InlineData("not json at all", "not a document spec")]
    public void ASpecThatCannotBeBuiltIsRefusedWithAReason(string spec, string complaint)
    {
        // Exit code 2, not 1. A script that treated a malformed spec the same as a document that
        // legitimately differs would report success when nothing had been built at all.
        StringWriter said = new();

        DocumentCommands.Build(Spec(spec), Path("out.ompart"), false, said)
            .Should().Be(DocumentCommands.Failed);

        said.ToString().Should().Contain(complaint);
        said.ToString().Should().NotContain("   at ", "a stack trace is never what a user wants");
    }

    [Fact]
    public void AMissingFileIsRefusedRatherThanReportedAsADifference()
    {
        DocumentCommands.Inspect(Path("nothing.ompart"), false, StringWriter.Null)
            .Should().Be(DocumentCommands.Failed);

        DocumentCommands.Diff(Path("nothing.ompart"), Path("nothing.ompart"), false, StringWriter.Null)
            .Should().Be(DocumentCommands.Failed, "there is nothing to compare, which is not 'they differ'");
    }

    [Fact]
    public void AFileThatIsNotADocumentIsRefusedWithAReason()
    {
        File.WriteAllText(Path("rubbish.ompart").FullName, "this is not a zip");

        StringWriter said = new();

        DocumentCommands.Inspect(Path("rubbish.ompart"), false, said)
            .Should().Be(DocumentCommands.Failed);

        said.ToString().Should().NotBeEmpty();
    }

    [Fact]
    public void AnErrorCanBeReadByAScriptToo()
    {
        StringWriter said = new();

        DocumentCommands.Inspect(Path("nothing.ompart"), true, said);

        JsonDocument.Parse(said.ToString()).RootElement
            .GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void UnitsAreConvertedRatherThanAssumed()
    {
        Document document = DocumentSpec.Parse("""
            {
              "parameters": [
                { "name": "Metric", "value": 25.4, "unit": "mm" },
                { "name": "Imperial", "value": 1, "unit": "in" },
                { "name": "Turn", "value": 180, "unit": "deg" },
                { "name": "Count", "value": 7 }
              ]
            }
            """).Build();

        document.FindParameter("Metric")!.Value.Value
            .Should().BeApproximately(document.FindParameter("Imperial")!.Value.Value, 1e-12);

        document.FindParameter("Turn")!.Value.Value
            .Should().BeApproximately(System.Math.PI, 1e-12);

        document.FindParameter("Count")!.Value.Dimension.Should().Be(Dimension.Dimensionless);
    }

    private void Build(string spec, string output)
        => DocumentCommands.Build(Spec(spec), Path(output), false, StringWriter.Null)
            .Should().Be(DocumentCommands.Ok, "the test needs a document to work on");

    private FileInfo Spec(string json, string name = "spec.json")
    {
        FileInfo file = Path(name);
        File.WriteAllText(file.FullName, json);

        return file;
    }

    private FileInfo Path(string name)
        => new(System.IO.Path.Combine(_work.FullName, name));
}
