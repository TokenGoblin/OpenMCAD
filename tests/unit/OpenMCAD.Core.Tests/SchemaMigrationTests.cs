using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Core.Serialization;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// The schema migration framework (P3-T19).
/// </summary>
/// <remarks>
/// <para>
/// The production registry is empty, because schema 1 is the only version there has ever been and
/// there is nothing to come from. That is why the chain is exercised here with migrations declared
/// for the purpose: the machinery has to be right before the first real migration is written, not
/// after, because by then there will be files on disk that only it can read.
/// </para>
/// <para>
/// What the corpus proves and this does not: that a real file written by a real build still opens.
/// What this proves and the corpus cannot: that a chain of several steps runs in order, that a gap
/// is refused rather than skipped, and that two migrations claiming one version are refused rather
/// than resolved by declaration order.
/// </para>
/// </remarks>
public sealed class SchemaMigrationTests
{
    [Fact]
    public void AChainRunsEveryStepInOrder()
    {
        // Order matters and is easy to get wrong: a set of migrations sorted by nothing, or run
        // backwards, still produces a document, and only the trail each step leaves shows which.
        MessagePackValue document = Map(("schema", 1), ("trail", ""));

        MessagePackValue moved = SchemaMigrator.Migrate(
            document, 1, 4, [Trailing(3, "c"), Trailing(1, "a"), Trailing(2, "b")]);

        Text(moved, "trail").Should().Be("abc");
        Number(moved, "schema").Should().Be(4);
    }

    [Fact]
    public void ADocumentAlreadyAtTheWantedVersionIsUntouched()
    {
        MessagePackValue document = Map(("schema", 4), ("trail", "original"));

        MessagePackValue moved = SchemaMigrator.Migrate(document, 4, 4, [Trailing(4, "x")]);

        moved.Should().Be(document, "there is nothing between a version and itself");
    }

    [Fact]
    public void AMissingStepIsRefusedRatherThanSkipped()
    {
        // Skipping would produce a document that had never been through the step, which is worse
        // than failing: the file opens, looks right, and is wrong in whatever way the missing
        // migration existed to fix.
        Action migrate = () => SchemaMigrator.Migrate(
            Map(("schema", 1)), 1, 4, [Trailing(1, "a"), Trailing(3, "c")]);

        migrate.Should().Throw<DocumentFormatException>()
            .WithMessage("*from 2 to 3*")
            .WithMessage("*not damaged*");
    }

    [Fact]
    public void TwoMigrationsClaimingOneVersionAreRefused()
    {
        // Whichever ran would be an accident of declaration order, and the two produce different
        // documents from the same file.
        Action migrate = () => SchemaMigrator.Migrate(
            Map(("schema", 1)), 1, 2, [Trailing(1, "a"), Trailing(1, "b")]);

        migrate.Should().Throw<DocumentFormatException>().WithMessage("*Only one can be right*");
    }

    [Fact]
    public void ADocumentFromTheFutureIsRefused()
    {
        Action migrate = () => SchemaMigrator.Migrate(Map(("schema", 9)), 9, 4, []);

        migrate.Should().Throw<DocumentFormatException>()
            .WithMessage("*newer version of OpenMCAD*");
    }

    [Fact]
    public void EachStepStampsTheVersionItReached()
    {
        // Left to the migrations themselves this would be forgotten, and the failure would surface
        // as a complaint about the file rather than about the step.
        MessagePackValue moved = SchemaMigrator.Migrate(
            Map(("schema", 1)), 1, 3, [Forgetful(1), Forgetful(2)]);

        Number(moved, "schema").Should().Be(3);
    }

    [Fact]
    public void ThisBuildHasNoMigrationsAndNeedsNone()
    {
        // The registry is empty on purpose. If this fails, someone has raised SchemaVersion or
        // added a migration without the other, and the two only mean anything together.
        SchemaMigrator.Migrations.Should().HaveCount(
            DocumentCodec.SchemaVersion - 1,
            "there is one migration for every version that has been left behind");
    }

    [Fact]
    public void ADocumentAtAnOlderSchemaWithNoMigrationSaysSoPlainly()
    {
        // What a real user would hit opening a file from a version whose migration was lost. The
        // message has to distinguish that from a corrupt file, because the two need opposite
        // responses -- one is a bug report, the other is a restore from backup.
        byte[] encoded = DocumentCodec.Write(OpenMCAD.Core.Documents.Document.Empty());
        byte[] older = WithSchema(encoded, 0);

        Action read = () => DocumentCodec.Read(older);

        read.Should().Throw<DocumentFormatException>()
            .WithMessage("*missing a migration*");
    }

    [Fact]
    public void ADocumentWithNoSchemaFieldIsReadAsCurrent()
    {
        // A guess either way, and this is the one that changes nothing: every file this project
        // writes states its version, so one that does not was made by something else, and running
        // migrations over it would rewrite a document that never asked to be rewritten.
        byte[] encoded = DocumentCodec.Write(OpenMCAD.Core.Documents.Document.Empty());
        MessagePackMap map = (MessagePackMap)MessagePackValue.Read(encoded);

        Action read = () => DocumentCodec.Read(map.Without("schema").ToBytes());

        read.Should().NotThrow();
    }

    /// <summary>A migration that appends a letter to a trail, so its running is visible.</summary>
    private static TestMigration Trailing(int from, string mark) => new(
        from,
        $"appends {mark}",
        value => ((MessagePackMap)value).With(
            "trail",
            new MessagePackString(Text(value, "trail") + mark)));

    /// <summary>A migration that changes nothing, including the version it claims to have reached.</summary>
    private static TestMigration Forgetful(int from)
        => new(from, "forgets to stamp", value => value);

    private static MessagePackMap Map(params (string Key, object Value)[] fields)
        => new(
        [
            .. fields.Select(f => new MessagePackPair(
                new MessagePackString(f.Key),
                f.Value is int number
                    ? new MessagePackInteger(number)
                    : new MessagePackString((string)f.Value))),
        ]);

    private static string Text(MessagePackValue value, string key)
        => ((MessagePackMap)value).Find(key) is MessagePackString text ? text.Value : string.Empty;

    private static long Number(MessagePackValue value, string key)
        => ((MessagePackMap)value).Find(key) is MessagePackInteger number ? number.Value : -1;

    private static byte[] WithSchema(byte[] encoded, int version)
        => ((MessagePackMap)MessagePackValue.Read(encoded))
            .With("schema", new MessagePackInteger(version))
            .ToBytes();

    private sealed class TestMigration(
        int from, string summary, Func<MessagePackValue, MessagePackValue> apply) : ISchemaMigration
    {
        public int From => from;

        public string Summary => summary;

        public MessagePackValue Apply(MessagePackValue document) => apply(document);
    }
}

/// <summary>
/// The MessagePack value tree the migrations work on (P3-T19).
/// </summary>
public sealed class MessagePackValueTests
{
    [Fact]
    public void ADocumentSurvivesBeingTakenApartAndPutBack()
    {
        // The property everything else depends on. A tree that did not re-encode exactly would
        // make every migration a rewrite of the whole file, and P3-T18's bit-identical re-save
        // would hold only for documents nothing had ever migrated.
        byte[] encoded = DocumentCodec.Write(OpenMCAD.Core.Documents.Document.Empty());

        MessagePackValue.Read(encoded).ToBytes().Should().Equal(encoded);
    }

    [Fact]
    public void AMapKeepsItsOrder()
    {
        MessagePackMap map = new(
        [
            new MessagePackPair(new MessagePackString("b"), new MessagePackInteger(1)),
            new MessagePackPair(new MessagePackString("a"), new MessagePackInteger(2)),
        ]);

        MessagePackMap read = (MessagePackMap)MessagePackValue.Read(map.ToBytes());

        read.Pairs.Select(p => ((MessagePackString)p.Key).Value).Should().Equal("b", "a");
    }

    [Fact]
    public void SettingAKeyThatIsThereLeavesItWhereItIs()
    {
        // Removing and re-adding is the obvious implementation and it moves the field to the end,
        // which changes the bytes of every document a migration touches for no reason anyone
        // reading the diff could explain.
        MessagePackMap map = new(
        [
            new MessagePackPair(new MessagePackString("first"), new MessagePackInteger(1)),
            new MessagePackPair(new MessagePackString("second"), new MessagePackInteger(2)),
        ]);

        MessagePackMap changed = map.With("first", new MessagePackInteger(9));

        changed.Pairs.Select(p => ((MessagePackString)p.Key).Value).Should().Equal("first", "second");
        ((MessagePackInteger)changed.Pairs[0].Value).Value.Should().Be(9);
    }

    [Fact]
    public void RenamingAKeyKeepsItsPlaceAndItsValue()
    {
        MessagePackMap map = new(
        [
            new MessagePackPair(new MessagePackString("old"), new MessagePackInteger(7)),
            new MessagePackPair(new MessagePackString("other"), new MessagePackInteger(8)),
        ]);

        MessagePackMap renamed = map.Renamed("old", "new");

        renamed.Pairs.Select(p => ((MessagePackString)p.Key).Value).Should().Equal("new", "other");
        ((MessagePackInteger)renamed.Pairs[0].Value).Value.Should().Be(7);
    }

    [Fact]
    public void AValueThisBuildCannotInterpretIsKeptExactly()
    {
        // The extension family. Re-encoding one would mean guessing at its meaning, and a tree that
        // guessed would corrupt a document the moment any migration ran over it.
        byte[] data = Convert.FromHexString("82A165D6FF00000001A16E07");

        MessagePackValue.Read(data).ToBytes().Should().Equal(data);
    }

    [Fact]
    public void TwoTreesWithTheSameContentAreEqual()
    {
        // Records compare collections by reference unless told otherwise, and this project has been
        // caught by that three times. A migration test comparing trees would silently always fail.
        byte[] encoded = DocumentCodec.Write(OpenMCAD.Core.Documents.Document.Empty());

        MessagePackValue.Read(encoded).Should().Be(MessagePackValue.Read(encoded));
    }

    [Fact]
    public void TwoTreesWithDifferentContentAreNotEqual()
    {
        MessagePackMap map = new(
            [new MessagePackPair(new MessagePackString("a"), new MessagePackInteger(1))]);

        map.Should().NotBe(map.With("a", new MessagePackInteger(2)));
    }

    [Fact]
    public void EveryKindOfValueSurvivesTheTree()
    {
        MessagePackWriter writer = new();
        writer.WriteArrayHeader(7);
        writer.WriteNil();
        writer.Write(true);
        writer.Write(-42);
        writer.Write(1.5);
        writer.Write("text");
        writer.WriteBinary([1, 2, 3]);
        writer.WriteMapHeader(1);
        writer.Write("k");
        writer.Write(0);

        byte[] encoded = writer.ToArray();

        MessagePackArray tree = (MessagePackArray)MessagePackValue.Read(encoded);

        tree.Items.Select(i => i.GetType().Name).Should().Equal(
            nameof(MessagePackNil),
            nameof(MessagePackBoolean),
            nameof(MessagePackInteger),
            nameof(MessagePackFloat),
            nameof(MessagePackString),
            nameof(MessagePackBinary),
            nameof(MessagePackMap));

        tree.ToBytes().Should().Equal(encoded);
    }

    [Fact]
    public void EveryElementOfAnArrayCanBeChangedAtOnce()
    {
        MessagePackArray array = new(
            [new MessagePackInteger(1), new MessagePackInteger(2)]);

        MessagePackArray doubled = array.Select(
            v => new MessagePackInteger(((MessagePackInteger)v).Value * 2));

        doubled.Items.Select(i => ((MessagePackInteger)i).Value).Should().Equal(2, 4);
    }

    [Fact]
    public void RemovingAKeyThatIsNotThereChangesNothing()
    {
        MessagePackMap map = new(
            [new MessagePackPair(new MessagePackString("a"), new MessagePackInteger(1))]);

        map.Without("b").Should().BeSameAs(map);
        map.Renamed("b", "c").Should().BeSameAs(map);
    }

    [Fact]
    public void AnUnreadableValueIsRefusedAsADocumentProblem()
    {
        // 0xC1 is the one byte MessagePack leaves permanently unassigned.
        Action read = () => MessagePackValue.Read(new byte[] { 0xC1 });

        read.Should().Throw<DocumentFormatException>().WithMessage("*not the start of any*");
    }

}
