using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Serialization;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// What a feature is told that is not a dimension, and how it is stored (P3-T21).
/// </summary>
/// <remarks>
/// This layer stores settings without an opinion about what they mean. What the names mean is the
/// business of the feature's schema, which lives in <c>OpenMCAD.Modeling</c> — so a document holding
/// a feature from a plugin nobody has installed still round-trips exactly.
/// </remarks>
public sealed class FeatureSettingTests
{
    [Fact]
    public void EveryKindOfSettingSurvivesARoundTrip()
    {
        Document original = With(
            ("choice", new ChoiceValue("ThroughAll")),
            ("count", new NumberValue(12)),
            ("merge", new FlagValue(true)),
            ("note", new TextValue("as machined")),
            ("offset", new QuantityValue(Unit.Millimetres.Of(2.5))));

        Document read = DocumentCodec.Read(DocumentCodec.Write(original));

        read.Matches(original).Should().BeTrue();

        read.Features[0].SettingValues.Should().BeEquivalentTo(
            original.Features[0].SettingValues);
    }

    [Fact]
    public void SettingsAreWrittenSorted()
    {
        // Asserted on the bytes rather than by building the document two ways and comparing, which
        // is what this test did first and which proved nothing: an ImmutableDictionary enumerates
        // by content rather than by insertion order, so both builds enumerated identically and the
        // sort could be removed without the comparison noticing. The same trap caught P3-T18.
        //
        // The bytes have to be a function of the document, because P3-T18's first exit criterion
        // is that a re-save is bit-identical.
        Document document = With(
            ("zebra", new FlagValue(true)),
            ("aardvark", new FlagValue(false)),
            ("mongoose", new NumberValue(3)),
            ("badger", new TextValue("stripey")),
            ("yak", new FlagValue(true)),
            ("civet", new NumberValue(9)),
            ("okapi", new TextValue("shy")));

        MessagePackMap feature = (MessagePackMap)
            ((MessagePackArray)((MessagePackMap)MessagePackValue.Read(DocumentCodec.Write(document)))
                .Find("features")!).Items[0];

        MessagePackMap settings = (MessagePackMap)feature.Find("settings")!;

        settings.Pairs.Select(pair => ((MessagePackString)pair.Key).Value).Should().Equal(
            "aardvark", "badger", "civet", "mongoose", "okapi", "yak", "zebra");
    }

    [Fact]
    public void ASettingOfAKindThisBuildDoesNotKnowDoesNotStopTheFileOpening()
    {
        // A tag from a newer version or from a plugin. One unrecognised switch making a document
        // unopenable would be a poor trade for the strictness it buys.
        byte[] encoded = DocumentCodec.Write(With(("merge", new FlagValue(true))));

        MessagePackMap document = (MessagePackMap)MessagePackValue.Read(encoded);
        MessagePackArray features = (MessagePackArray)document.Find("features")!;
        MessagePackMap feature = (MessagePackMap)features.Items[0];
        MessagePackMap settings = (MessagePackMap)feature.Find("settings")!;

        MessagePackArray future = new(
            [new MessagePackInteger(99), new MessagePackString("something new")]);

        byte[] newer = document.With(
            "features",
            features.Select(_ => feature.With("settings", settings.With("newKind", future))))
            .ToBytes();

        Action read = () => DocumentCodec.Read(newer);

        read.Should().NotThrow();
        DocumentCodec.Read(newer).Features[0].FindSetting("newKind").Should().BeNull(
            "it is skipped here rather than guessed at");
    }

    [Fact]
    public void AFeatureWithNoSettingsIsUnchangedByHavingNone()
    {
        Feature bare = Feature.Create(Id(1), "One", "Extrude");

        bare.SettingValues.Should().BeEmpty();
        bare.FindSetting("anything").Should().BeNull();
    }

    [Fact]
    public void TwoFeaturesToldTheSameThingsAreEqual()
    {
        // A record compares an ImmutableDictionary by reference like every other collection it
        // holds. Feature's equality is written out by hand for exactly this reason, and the
        // settings are the newest place for the trap to be sprung.
        Feature one = Feature.Create(Id(1), "One", "Extrude")
            .WithSetting("merge", new FlagValue(true));

        Feature other = Feature.Create(Id(1), "One", "Extrude")
            .WithSetting("merge", new FlagValue(true));

        one.Should().Be(other);
        one.WithSetting("merge", new FlagValue(false)).Should().NotBe(other);
        one.WithSetting("extra", new FlagValue(true)).Should().NotBe(other);
    }

    [Fact]
    public void ASettingCanBeChangedWithoutDisturbingTheRest()
    {
        Feature feature = Feature.Create(Id(1), "One", "Extrude")
            .WithSetting("merge", new FlagValue(true))
            .WithSetting("count", new NumberValue(3));

        Feature changed = feature.WithSetting("count", new NumberValue(4));

        changed.FindSetting("merge").Should().Be(new FlagValue(true));
        changed.FindSetting("count").Should().Be(new NumberValue(4));
        feature.FindSetting("count").Should().Be(new NumberValue(3), "the original is immutable");
    }

    private static FeatureId Id(int n) => new(new Guid($"00000000-0000-0000-0000-{n:D12}"));

    private static Document With(params (string Name, FeatureValue Value)[] settings)
    {
        ImmutableDictionary<string, FeatureValue>.Builder values =
            ImmutableDictionary.CreateBuilder<string, FeatureValue>(StringComparer.Ordinal);

        foreach ((string name, FeatureValue value) in settings)
        {
            values[name] = value;
        }

        DocumentSession session = new();

        using (IDocumentTransaction edit = session.BeginTransaction("build"))
        {
            edit.AddFeature(
                Feature.Create(Id(1), "One", "Extrude") with { Settings = values.ToImmutable() });

            edit.Commit();
        }

        return session.Current;
    }
}
