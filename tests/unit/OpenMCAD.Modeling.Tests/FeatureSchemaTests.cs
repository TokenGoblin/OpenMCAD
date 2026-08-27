using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Modeling;

using Xunit;

namespace OpenMCAD.Modeling.Tests;

/// <summary>
/// The one declaration that drives the property manager, serialization, the API and scripting
/// (P3-T21).
/// </summary>
/// <remarks>
/// <para>
/// §5.7: "Adding a feature should mean writing one class and one schema, not editing seven files."
/// The schemas here are a real extrude and a real fillet, declared the way Phase 5 and Phase 7 will
/// declare them, because a schema mechanism proved only against toy declarations is a mechanism
/// nobody has checked is expressive enough.
/// </para>
/// <para>
/// What is deliberately not tested here: the property manager rendering one (P6-T04) and a feature
/// actually running (Phase 5). What is tested is everything those two will rely on being true.
/// </para>
/// </remarks>
public sealed class FeatureSchemaTests
{
    /// <summary>An extrude, near enough to the real one to be worth trusting.</summary>
    private static readonly FeatureSchema Extrude = FeatureSchema.Create(
        "Extrude",
        "Extrude",
        "Create",
        [
            new FeatureProperty(
                "profile",
                "Profile",
                PropertyKind.Selection,
                Group: "Profile",
                Multiplicity: OpenMCAD.Core.Naming.MultiplicityPolicy.ExactlyOne),

            new FeatureProperty(
                "end",
                "End condition",
                PropertyKind.Choice,
                Group: "Direction",
                Choices: ["Blind", "ThroughAll", "UpToSurface", "Midplane"],
                Default: new ChoiceValue("Blind")),

            new FeatureProperty(
                "depth",
                "Depth",
                PropertyKind.Quantity,
                Group: "Direction",
                Dimension: Dimension.Length,
                Default: new QuantityValue(Unit.Millimetres.Of(10)),
                Minimum: 0,
                VisibleWhen: new PropertyCondition("end", new ChoiceValue("Blind"))),

            new FeatureProperty(
                "draft",
                "Draft",
                PropertyKind.Flag,
                Group: "Draft",
                Default: new FlagValue(false)),

            new FeatureProperty(
                "draftAngle",
                "Draft angle",
                PropertyKind.Quantity,
                Group: "Draft",
                Dimension: Dimension.Angle,
                Default: new QuantityValue(Unit.Degrees.Of(3)),
                VisibleWhen: new PropertyCondition("draft", new FlagValue(true))),

            new FeatureProperty(
                "merge",
                "Merge result",
                PropertyKind.Flag,
                Group: "Result",
                Default: new FlagValue(true)),
        ],
        "Sweeps a profile along a straight path.");

    private static readonly FeatureSchema Fillet = FeatureSchema.Create(
        "Fillet",
        "Fillet",
        "Modify",
        [
            new FeatureProperty(
                "edges",
                "Edges",
                PropertyKind.Selection,
                Multiplicity: OpenMCAD.Core.Naming.MultiplicityPolicy.AllDescendants),

            new FeatureProperty(
                "radius",
                "Radius",
                PropertyKind.Quantity,
                Dimension: Dimension.Length,
                Default: new QuantityValue(Unit.Millimetres.Of(1)),
                Minimum: 0),
        ]);

    [Fact]
    public void ASchemaSaysWhatAFeatureTakes()
    {
        // The API surface and the scripting binding are both this question. Neither can hold a
        // compiled reference to a feature class, because a plugin's feature is not compiled into
        // either of them.
        Extrude.Declared.Select(p => p.Name)
            .Should().Equal("profile", "end", "depth", "draft", "draftAngle", "merge");

        Extrude.Find("depth")!.Dimension.Should().Be(Dimension.Length);
        Extrude.Find("end")!.Options.Should().Equal("Blind", "ThroughAll", "UpToSurface", "Midplane");
        Extrude.Find("nothing").Should().BeNull();
    }

    [Fact]
    public void AValueIsFoundWhereItsKindSaysItLives()
    {
        // A dimension is a parameter so an expression can drive it; everything else is a setting.
        // One place knows which, or every caller becomes a fifth description of the feature.
        Feature feature = Extrude.WithDefaults(New("Extrude"));

        feature.FindParameter("depth").Should().NotBeNull("a dimension is a parameter");
        feature.FindSetting("depth").Should().BeNull();

        feature.FindSetting("merge").Should().NotBeNull("a switch is a setting");
        feature.FindParameter("merge").Should().BeNull();
    }

    [Fact]
    public void DefaultsFillInWhatAnOlderFileNeverSaid()
    {
        // What makes adding a property to an existing feature kind safe. A file written before the
        // property existed says nothing about it; refusing that file, or running the feature with
        // a hole in it, are both worse than using the value the schema already declares.
        Feature bare = New("Extrude");

        Feature filled = Extrude.WithDefaults(bare);

        FeatureSchema.ValueOf(Extrude.Find("end")!, filled).Should().Be(new ChoiceValue("Blind"));
        FeatureSchema.ValueOf(Extrude.Find("merge")!, filled).Should().Be(new FlagValue(true));
        FeatureSchema.ValueOf(Extrude.Find("depth")!, filled)
            .Should().Be(new QuantityValue(Unit.Millimetres.Of(10)));
    }

    [Fact]
    public void DefaultsDoNotOverwriteWhatTheUserSaid()
    {
        Feature chosen = New("Extrude").WithSetting("end", new ChoiceValue("ThroughAll"));

        FeatureSchema.ValueOf(Extrude.Find("end")!, Extrude.WithDefaults(chosen))
            .Should().Be(new ChoiceValue("ThroughAll"));
    }

    [Fact]
    public void APropertyThatDoesNotApplyIsNotDemanded()
    {
        // A blind extrude's depth applies and a through-all extrude's does not. Insisting on it
        // anyway would report an error about a box the user was never shown.
        Feature through = New("Extrude").WithSetting("end", new ChoiceValue("ThroughAll"));

        Extrude.Applies(Extrude.Find("depth")!, through).Should().BeFalse();

        Extrude.Validate(through).Select(v => v.Property)
            .Should().NotContain("depth")
            .And.Contain("merge", "the properties that do apply are still required");
    }

    [Fact]
    public void APropertyThatDoesApplyIsDemanded()
    {
        Feature blind = New("Extrude").WithSetting("end", new ChoiceValue("Blind"));

        Extrude.Validate(blind).Should().Contain(v => v.Property == "depth" && v.IsError);
    }

    [Fact]
    public void APropertyBehindAPropertyThatDoesNotApplyDoesNotApplyEither()
    {
        // draftAngle applies when draft is on. If draft itself were hidden, insisting on the angle
        // would be insisting on the consequence of a choice nobody was offered.
        FeatureSchema chained = FeatureSchema.Create(
            "Chained",
            "Chained",
            "Test",
            [
                new FeatureProperty(
                    "mode", "Mode", PropertyKind.Choice,
                    Choices: ["Simple", "Advanced"], Default: new ChoiceValue("Simple")),

                new FeatureProperty(
                    "extra", "Extra", PropertyKind.Flag, Default: new FlagValue(true),
                    VisibleWhen: new PropertyCondition("mode", new ChoiceValue("Advanced"))),

                new FeatureProperty(
                    "beyond", "Beyond", PropertyKind.Text,
                    VisibleWhen: new PropertyCondition("extra", new FlagValue(true))),
            ]);

        Feature simple = chained.WithDefaults(New("Chained"));

        chained.Applies(chained.Find("beyond")!, simple).Should().BeFalse(
            "the property it depends on is itself not in play");

        chained.Validate(simple).Should().BeEmpty();
    }

    [Fact]
    public void AValueOfTheWrongKindIsRefused()
    {
        Feature confused = Extrude.WithDefaults(New("Extrude"))
            .WithSetting("merge", new TextValue("yes please"));

        Extrude.Validate(confused).Should().ContainSingle()
            .Which.Message.Should().Contain("on or off").And.Contain("text");
    }

    [Fact]
    public void ADimensionMeasuringTheWrongThingIsRefused()
    {
        // 4mm where an angle was wanted. The dimension table (P3-T14) makes this expressible; the
        // schema is what makes it catchable before an extrude tries to draft by four millimetres.
        Feature confused = Extrude.WithDefaults(New("Extrude"))
            .WithSetting("draft", new FlagValue(true)) with
        {
            Parameters =
            [
                new Parameter("depth", Unit.Millimetres.Of(10)),
                new Parameter("draftAngle", Unit.Millimetres.Of(4)),
            ],
        };

        Extrude.Validate(confused).Should().ContainSingle()
            .Which.Message.Should().Contain("Angle").And.Contain("Length");
    }

    [Fact]
    public void AValueOutOfRangeIsRefused()
    {
        Feature backwards = Fillet.WithDefaults(New("Fillet")) with
        {
            Parameters = [new Parameter("radius", Unit.Millimetres.Of(-5))],
        };

        Fillet.Validate(backwards).Should().ContainSingle()
            .Which.Message.Should().Contain("cannot be less than 0");
    }

    [Fact]
    public void AnOptionThatIsNotOnTheListIsRefused()
    {
        Feature invented = Extrude.WithDefaults(New("Extrude"))
            .WithSetting("end", new ChoiceValue("Sideways"));

        Extrude.Validate(invented).Should().Contain(
            v => v.Property == "end" && v.Message.Contains("not one of the options"));
    }

    [Fact]
    public void ASettingNothingUnderstandsIsReportedAndNotRefused()
    {
        // It may be from a newer build, which P3-T20 keeps and this build has no business deleting.
        // Saying nothing at all would leave a user wondering why a switch in the file does nothing.
        Feature odd = Extrude.WithDefaults(New("Extrude"))
            .WithSetting("wibble", new FlagValue(true));

        SchemaViolation violation = Extrude.Validate(odd).Should().ContainSingle().Subject;

        violation.Severity.Should().Be(ViolationSeverity.Warning);
        violation.IsError.Should().BeFalse();
        violation.Message.Should().Contain("being ignored");
    }

    [Fact]
    public void AWellFilledFeatureHasNothingWrongWithIt()
    {
        Extrude.Validate(Extrude.WithDefaults(New("Extrude"))).Should().BeEmpty();
        Fillet.Validate(Fillet.WithDefaults(New("Fillet"))).Should().BeEmpty();
    }

    [Fact]
    public void ASelectionIsNotSomethingTheSchemaCanCheck()
    {
        // Whether the geometry a feature names still exists is persistent naming's question (§5.3),
        // asked at rebuild against a model that has been built. Answering it here would be guessing,
        // and guessing wrong would mark every feature invalid the moment a document was loaded.
        Feature unpicked = Extrude.WithDefaults(New("Extrude"));

        unpicked.EntityReferences.Should().BeEmpty();
        Extrude.Validate(unpicked).Should().BeEmpty();
    }

    [Theory]
    [InlineData("two properties with one name")]
    [InlineData("a choice with nothing to choose from")]
    [InlineData("a default of the wrong kind")]
    [InlineData("a condition on a property that does not exist")]
    [InlineData("a condition on a value that property cannot have")]
    public void ASchemaThatContradictsItselfIsRefusedWhenItIsBuilt(string fault)
    {
        // A programming mistake, caught the first time the feature is registered rather than the
        // first time a user opens that panel and finds a control missing.
        ImmutableArray<FeatureProperty> properties = fault switch
        {
            "two properties with one name" =>
            [
                new FeatureProperty("depth", "Depth", PropertyKind.Quantity),
                new FeatureProperty("depth", "Also depth", PropertyKind.Flag),
            ],

            "a choice with nothing to choose from" =>
                [new FeatureProperty("end", "End", PropertyKind.Choice)],

            "a default of the wrong kind" =>
            [
                new FeatureProperty(
                    "merge", "Merge", PropertyKind.Flag, Default: new TextValue("yes")),
            ],

            "a condition on a property that does not exist" =>
            [
                new FeatureProperty(
                    "depth", "Depth", PropertyKind.Quantity,
                    VisibleWhen: new PropertyCondition("nothing", new FlagValue(true))),
            ],

            _ =>
            [
                new FeatureProperty(
                    "end", "End", PropertyKind.Choice, Choices: ["Blind"],
                    Default: new ChoiceValue("Blind")),
                new FeatureProperty(
                    "depth", "Depth", PropertyKind.Quantity,
                    VisibleWhen: new PropertyCondition("end", new ChoiceValue("Sideways"))),
            ],
        };

        Action build = () => FeatureSchema.Create("Broken", "Broken", "Test", properties);

        build.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TwoSchemasBuiltTheSameWayAreEqual()
    {
        // Records compare collections by reference unless told otherwise, and a schema holds an
        // array of properties each holding an array of options. This project has been caught by
        // that four times.
        FeatureSchema again = FeatureSchema.Create(
            "Fillet",
            "Fillet",
            "Modify",
            [
                new FeatureProperty(
                    "edges", "Edges", PropertyKind.Selection,
                    Multiplicity: OpenMCAD.Core.Naming.MultiplicityPolicy.AllDescendants),

                new FeatureProperty(
                    "radius", "Radius", PropertyKind.Quantity,
                    Dimension: Dimension.Length,
                    Default: new QuantityValue(Unit.Millimetres.Of(1)),
                    Minimum: 0),
            ]);

        again.Should().Be(Fillet);
        again.Should().NotBe(Extrude);
    }

    /// <summary>Gets the schemas these tests declare, for the catalogue tests to use.</summary>
    internal static ImmutableArray<FeatureSchema> Known => [Extrude, Fillet];

    private static Feature New(string featureType) => Feature.Create(
        new FeatureId(Guid.Parse("00000000-0000-0000-0000-000000000001")), "One", featureType);
}
