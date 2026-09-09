using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;
using OpenMCAD.Kernel;
using OpenMCAD.Modeling;

using Xunit;

namespace OpenMCAD.Modeling.Tests;

/// <summary>
/// The datum feature types: what each one declares, and how one is read back into the
/// <see cref="DatumDefinition"/> it describes (P5-T03).
/// </summary>
/// <remarks>
/// The schema and the translation are two descriptions of the same feature, and §5.7's whole
/// argument for a schema is that parallel descriptions drift. Nothing makes them agree by
/// construction, so what is checked here is that they agree: every input the schema declares is one
/// the translation reads, and a feature that passes <see cref="FeatureSchema.Validate"/> always
/// translates.
/// </remarks>
public sealed class DatumFeatureTests
{
    [Fact]
    public void EveryDatumFeatureTypeIsInTheCatalogue()
    {
        DatumFeature.Schemas.Should().HaveCount(13);

        foreach (FeatureSchema schema in DatumFeature.Schemas)
        {
            DatumFeature.Catalogue.Find(schema.FeatureType).Should().Be(schema);
            DatumFeature.Handles(schema.FeatureType).Should().BeTrue();
            schema.Category.Should().Be(DatumFeature.Category);
        }
    }

    [Fact]
    public void EveryInputTheSchemaDeclaresIsOneTheTranslationReads()
    {
        // The drift this catches is silent in both directions: an input the schema declares and the
        // translation ignores is a control the user fills in that does nothing, and one the
        // translation reads and the schema omits is a value the property manager never offers and
        // validation never insists on.
        foreach (FeatureSchema schema in DatumFeature.Schemas)
        {
            foreach (FeatureProperty property in schema.Declared)
            {
                if (property.Kind != PropertyKind.Reference)
                {
                    continue;
                }

                Feature complete = FeatureWith(schema, omitting: null);
                Feature missing = FeatureWith(schema, omitting: property.Name);

                DatumFeature.Translate(complete).IsTranslated.Should().BeTrue(
                    "{0} is complete", schema.FeatureType);

                DatumTranslation without = DatumFeature.Translate(missing);

                without.IsTranslated.Should().BeFalse(
                    "{0} needs '{1}'", schema.FeatureType, property.Name);

                without.Reason.Should().Contain(property.Name);
            }
        }
    }

    [Fact]
    public void AFeatureThatValidatesAlwaysTranslates()
    {
        foreach (FeatureSchema schema in DatumFeature.Schemas)
        {
            Feature feature = FeatureWith(schema, omitting: null);

            schema.Validate(feature).Should().BeEmpty("{0} is complete", schema.FeatureType);
            DatumFeature.Translate(feature).IsTranslated.Should().BeTrue(schema.FeatureType);
        }
    }

    [Fact]
    public void TheDatumIsCalledWhatTheFeatureIsCalled()
    {
        Feature feature = Offset("Offset1", 10);

        DatumDefinition definition = DatumFeature.Translate(feature).Definition!;

        // Document.FindReference looks up (Owner, Name). A second name stored in a setting could
        // disagree with the tree, and then a sketch built on this datum would name something the
        // user cannot see.
        definition.Name.Should().Be("Offset1");
    }

    [Fact]
    public void AnInputCanNameReferenceGeometry()
    {
        Feature feature = Offset("Offset1", 10);

        DatumFeature.InputOf(feature, "from").Should().Be(
            new DatumReference.OnGeometry(FeatureId.None, "Top"));
    }

    [Fact]
    public void AnInputCanNameAFaceInstead()
    {
        PersistentName face = Name(2);

        Feature feature = Feature.Create(Id(1), "Offset1", DatumFeature.PlaneOffset) with
        {
            Parameters = [new Parameter("distance", Unit.Millimetres.Of(10))],
            References = [new EntityReference(face, MultiplicityPolicy.ExactlyOne, "from")],
        };

        // The same declared input, answered the other way. This is why a datum plane offset from a
        // face and one offset from a datum are one feature type rather than two.
        DatumFeature.InputOf(feature, "from").Should().Be(new DatumReference.OnTopology(face));
        DatumFeature.Translate(feature).IsTranslated.Should().BeTrue();
    }

    [Fact]
    public void AnInputNamedBothWaysIsRefusedRatherThanPreferred()
    {
        Feature feature = Offset("Offset1", 10) with
        {
            References =
                [new EntityReference(Name(2), MultiplicityPolicy.ExactlyOne, "from")],
        };

        FeatureSchema schema = DatumFeature.Catalogue.Find(DatumFeature.PlaneOffset)!;

        FeatureSchema.SatisfiedBy(schema.Declared[0], feature)
            .Should().Be(FeatureSchema.InputSource.Both);

        schema.Validate(feature).Should().ContainSingle()
            .Which.Property.Should().Be("from");
    }

    [Fact]
    public void AnInputNothingAnswersIsReportedByValidation()
    {
        Feature feature = Feature.Create(Id(1), "Offset1", DatumFeature.PlaneOffset) with
        {
            Parameters = [new Parameter("distance", Unit.Millimetres.Of(10))],
        };

        FeatureSchema schema = DatumFeature.Catalogue.Find(DatumFeature.PlaneOffset)!;

        schema.Validate(feature).Should().ContainSingle()
            .Which.Message.Should().Contain("point at");
    }

    [Fact]
    public void AMissingDimensionIsReportedByValidation()
    {
        Feature feature = Feature.Create(Id(1), "Offset1", DatumFeature.PlaneOffset)
            .WithSetting("from", new ReferenceValue(FeatureId.None, "Top"));

        FeatureSchema schema = DatumFeature.Catalogue.Find(DatumFeature.PlaneOffset)!;

        schema.Validate(feature).Should().ContainSingle()
            .Which.Property.Should().Be("distance");
    }

    [Fact]
    public void AFractionOutsideItsRangeIsReportedByValidation()
    {
        // The schema says 0 to 1 and DatumResolver refuses the same range at rebuild. Both, on
        // purpose: the schema catches it while the user is typing, and the resolver catches a file
        // written by something that never saw the schema.
        Feature feature = Feature.Create(Id(1), "Point1", DatumFeature.PointAlongEdge) with
        {
            Parameters = [new Parameter("fraction", new Quantity(1.5, Dimension.Dimensionless))],
            References = [new EntityReference(Name(2), MultiplicityPolicy.ExactlyOne, "edge")],
        };

        FeatureSchema schema = DatumFeature.Catalogue.Find(DatumFeature.PointAlongEdge)!;

        schema.Validate(feature).Should().ContainSingle()
            .Which.Property.Should().Be("fraction");
    }

    [Fact]
    public void EachFeatureTypeTranslatesToItsOwnConstruction()
    {
        (string FeatureType, Type Construction)[] expected =
        [
            (DatumFeature.PlaneOffset, typeof(DatumDefinition.PlaneOffsetFrom)),
            (DatumFeature.PlaneThroughPoint, typeof(DatumDefinition.PlaneThroughPoint)),
            (DatumFeature.PlaneAtAngle, typeof(DatumDefinition.PlaneAtAngle)),
            (DatumFeature.PlaneThreePoints, typeof(DatumDefinition.PlaneThroughThreePoints)),
            (DatumFeature.PlaneMidway, typeof(DatumDefinition.PlaneMidwayBetween)),
            (DatumFeature.AxisTwoPoints, typeof(DatumDefinition.AxisThroughTwoPoints)),
            (DatumFeature.AxisAlongEdge, typeof(DatumDefinition.AxisAlongEdge)),
            (DatumFeature.AxisPlanesMeet, typeof(DatumDefinition.AxisWherePlanesMeet)),
            (DatumFeature.AxisNormalToPlane, typeof(DatumDefinition.AxisNormalToPlane)),
            (DatumFeature.Point, typeof(DatumDefinition.PointAt)),
            (DatumFeature.PointCentre, typeof(DatumDefinition.PointAtCentreOf)),
            (DatumFeature.PointAxisMeetsPlane, typeof(DatumDefinition.PointWhereAxisMeetsPlane)),
            (DatumFeature.PointAlongEdge, typeof(DatumDefinition.PointAlongEdge)),
        ];

        expected.Should().HaveCount(DatumFeature.Schemas.Length);

        foreach ((string featureType, Type construction) in expected)
        {
            FeatureSchema schema = DatumFeature.Catalogue.Find(featureType)!;

            DatumFeature.Translate(FeatureWith(schema, omitting: null)).Definition
                .Should().BeOfType(construction);
        }
    }

    [Fact]
    public void TheInputsReachTheConstructionInTheOrderTheSchemaDeclaresThem()
    {
        // Three inputs of one kind, so getting two of them the wrong way round produces a plane
        // rather than an error -- which is the failure EntityReference.Property exists to prevent.
        Feature feature = Feature.Create(Id(1), "Plane1", DatumFeature.PlaneThreePoints)
            .WithSetting("first", new ReferenceValue(FeatureId.None, "A"))
            .WithSetting("second", new ReferenceValue(FeatureId.None, "B"))
            .WithSetting("third", new ReferenceValue(FeatureId.None, "C"));

        DatumDefinition.PlaneThroughThreePoints definition =
            (DatumDefinition.PlaneThroughThreePoints)DatumFeature.Translate(feature).Definition!;

        definition.First.Should().Be(new DatumReference.OnGeometry(FeatureId.None, "A"));
        definition.Second.Should().Be(new DatumReference.OnGeometry(FeatureId.None, "B"));
        definition.Third.Should().Be(new DatumReference.OnGeometry(FeatureId.None, "C"));
    }

    [Fact]
    public void SelectionsReachTheConstructionByTheInputTheyClaimAndNotByPosition()
    {
        PersistentName a = Name(2);
        PersistentName b = Name(3);
        PersistentName c = Name(4);

        // Declared in the reverse of the schema's order. Position would put them back to front.
        Feature feature = Feature.Create(Id(1), "Plane1", DatumFeature.PlaneThreePoints) with
        {
            References =
            [
                new EntityReference(c, MultiplicityPolicy.ExactlyOne, "third"),
                new EntityReference(b, MultiplicityPolicy.ExactlyOne, "second"),
                new EntityReference(a, MultiplicityPolicy.ExactlyOne, "first"),
            ],
        };

        DatumDefinition.PlaneThroughThreePoints definition =
            (DatumDefinition.PlaneThroughThreePoints)DatumFeature.Translate(feature).Definition!;

        definition.First.Should().Be(new DatumReference.OnTopology(a));
        definition.Second.Should().Be(new DatumReference.OnTopology(b));
        definition.Third.Should().Be(new DatumReference.OnTopology(c));
    }

    [Fact]
    public void AMissingDimensionStopsTheTranslationRatherThanDefaultingIt()
    {
        // The last line of defence, and it is a real one: FeatureSchema lives in this layer and the
        // rebuild engine does not run it, so a feature reaching an evaluator has not necessarily
        // been validated. Substituting a zero here would build a plane coincident with the one it
        // was meant to be offset from, and report success.
        Feature feature = Feature.Create(Id(1), "Offset1", DatumFeature.PlaneOffset)
            .WithSetting("from", new ReferenceValue(FeatureId.None, "Top"));

        DatumTranslation result = DatumFeature.Translate(feature);

        result.IsTranslated.Should().BeFalse();
        result.Reason.Should().Contain("distance");
    }

    [Fact]
    public void AnInputCanBeDeclaredWithADefaultAndOnlyAPointerWillDo()
    {
        // Nothing declares one today. The check exists because a schema that did would otherwise
        // have its default accepted whatever kind it was, and FeatureSchema.Create is where a
        // malformed declaration is meant to be caught -- at registration rather than at the first
        // time a user opens that panel.
        FeatureProperty sound = new(
            "from", "From", PropertyKind.Reference,
            Default: new ReferenceValue(FeatureId.None, "Top"));

        FeatureProperty wrong = new(
            "from", "From", PropertyKind.Reference, Default: new TextValue("Top"));

        FluentActions.Invoking(() => FeatureSchema.Create("T", "T", "T", [sound]))
            .Should().NotThrow();

        FluentActions.Invoking(() => FeatureSchema.Create("T", "T", "T", [wrong]))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AFeatureOfAnotherKindDoesNotTranslate()
    {
        DatumTranslation result = DatumFeature.Translate(
            Feature.Create(Id(1), "Boss", "Extrude"));

        result.IsTranslated.Should().BeFalse();
        result.Reason.Should().Contain("Extrude");
        DatumFeature.Handles("Extrude").Should().BeFalse();
    }

    /// <summary>A feature answering every input its schema declares, except one.</summary>
    private static Feature FeatureWith(FeatureSchema schema, string? omitting)
    {
        Feature feature = Feature.Create(Id(1), "Datum1", schema.FeatureType);

        foreach (FeatureProperty property in schema.Declared)
        {
            if (string.Equals(property.Name, omitting, StringComparison.Ordinal))
            {
                continue;
            }

            feature = property.Kind switch
            {
                PropertyKind.Reference => feature.WithSetting(
                    property.Name, new ReferenceValue(FeatureId.None, property.Name)),

                // Half, so that a fraction is inside its declared range and an angle and a
                // distance are values a plane could actually be built at.
                PropertyKind.Quantity => feature with
                {
                    Parameters = feature.Parameters.Add(
                        new Parameter(property.Name, new Quantity(0.5, property.Dimension))),
                },

                _ => feature,
            };
        }

        return feature;
    }

    private static Feature Offset(string name, double millimetres)
        => Feature.Create(Id(1), name, DatumFeature.PlaneOffset) with
        {
            Parameters = [new Parameter("distance", Unit.Millimetres.Of(millimetres))],
            Settings = ImmutableDictionary<string, FeatureValue>.Empty
                .Add("from", new ReferenceValue(FeatureId.None, "Top")),
        };

    private static FeatureId Id(int n) => new(new Guid($"00000000-0000-0000-0000-{n:D12}"));

    private static PersistentName Name(int n) => PersistentName.Of(
        NameSegment.Of(Id(n), ProvenanceKind.New, EntityRole.From(OperationRole.SideWall)));
}
