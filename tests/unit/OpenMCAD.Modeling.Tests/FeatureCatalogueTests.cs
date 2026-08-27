using FluentAssertions;

using OpenMCAD.Core.Documents;

using Xunit;

namespace OpenMCAD.Modeling.Tests;

/// <summary>
/// The registry that is the API surface and the scripting binding (P3-T21).
/// </summary>
public sealed class FeatureCatalogueTests
{
    private static readonly FeatureCatalogue Catalogue =
        FeatureCatalogue.Of(FeatureSchemaTests.Known);

    [Fact]
    public void ACatalogueSaysWhatFeaturesThereAre()
    {
        Catalogue.Schemas.Select(s => s.FeatureType).Should().Equal("Extrude", "Fillet");
        Catalogue.Find("Extrude")!.Label.Should().Be("Extrude");
        Catalogue.Find("Loft").Should().BeNull();
    }

    [Fact]
    public void FeaturesAreListedInAStableOrder()
    {
        // A generated ribbon, a generated document and a script's listing all read this, and left
        // to a dictionary's enumeration they would agree today and differ after an unrelated
        // change. Asserted as the order itself: building the catalogue twice in different orders
        // and comparing proved nothing, because an ImmutableDictionary enumerates by content
        // rather than by insertion, so both builds enumerated identically either way.
        FeatureCatalogue many = FeatureCatalogue.Of(
        [
            Named("Wrap", "Modify"),
            Named("Extrude", "Create"),
            Named("Shell", "Modify"),
            Named("Chamfer", "Modify"),
            Named("Revolve", "Create"),
            Named("Pattern", "Arrange"),
            Named("Mirror", "Arrange"),
        ]);

        many.Schemas.Select(s => s.FeatureType).Should().Equal(
            "Mirror", "Pattern", "Extrude", "Revolve", "Chamfer", "Shell", "Wrap");
    }

    private static FeatureSchema Named(string featureType, string category)
        => FeatureSchema.Create(featureType, featureType, category, []);

    [Fact]
    public void TwoFeaturesCannotShareAName()
    {
        // Whichever won would depend on load order, which means a document opening differently
        // depending on what else happens to be installed.
        Action clash = () => Catalogue.With(
            FeatureSchema.Create("Extrude", "Someone else's extrude", "Create", []));

        clash.Should().Throw<ArgumentException>().WithMessage("*already the name*");
    }

    [Fact]
    public void ACatalogueChecksAWholeDocument()
    {
        Document document = With(Filled("Extrude", Id(1)), Filled("Fillet", Id(2)));

        Catalogue.Validate(document).Should().BeEmpty();
    }

    [Fact]
    public void AFeatureFromAPluginThatIsNotLoadedIsAWarningAndNotAFailure()
    {
        // An uninstalled plugin costing the user one feature is survivable; costing them the whole
        // file is not. P3-T20 keeps everything that feature held, so reinstalling brings it back.
        Document document = With(Feature.Create(Id(1), "Mystery", "SomeoneElsesFeature"));

        SchemaViolation violation = Catalogue.Validate(document).Should().ContainSingle().Subject;

        violation.Severity.Should().Be(ViolationSeverity.Warning);
        violation.Message.Should().Contain("Nothing here knows");
    }

    [Fact]
    public void EveryFeatureOfADocumentIsChecked()
    {
        Document document = With(
            Feature.Create(Id(1), "Bare", "Extrude"),
            Feature.Create(Id(2), "Also bare", "Fillet"));

        Catalogue.Validate(document).Select(v => v.Feature).Distinct()
            .Should().HaveCount(2, "a report about only the first feature sends the user in circles");
    }

    [Fact]
    public void AnEmptyCatalogueKnowsNothingAndSaysSo()
    {
        FeatureCatalogue.Empty.Schemas.Should().BeEmpty();
        FeatureCatalogue.Empty.Find("Extrude").Should().BeNull();
    }

    /// <summary>Builds a document holding some features, through the public editing path.</summary>
    /// <remarks>
    /// A transaction rather than the document's own mutators, which are internal on purpose: an
    /// edit outside one would not be undoable, and this layer is a consumer of documents like any
    /// other.
    /// </remarks>
    private static Document With(params Feature[] features)
    {
        DocumentSession session = new();

        using (IDocumentTransaction edit = session.BeginTransaction("build"))
        {
            foreach (Feature feature in features)
            {
                edit.AddFeature(feature);
            }

            edit.Commit();
        }

        return session.Current;
    }

    private static FeatureId Id(int n)
        => new(new Guid($"00000000-0000-0000-0000-{n:D12}"));

    private static Feature Filled(string featureType, FeatureId id)
        => Catalogue.Find(featureType)!.WithDefaults(Feature.Create(id, featureType, featureType));
}
