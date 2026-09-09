using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;
using OpenMCAD.Kernel;
using OpenMCAD.Modeling;

using Xunit;

namespace OpenMCAD.Modeling.Tests;

/// <summary>
/// The durable half of a datum: what it says it is built from, and which features that makes it
/// depend on (P5-T03).
/// </summary>
public sealed class DatumDefinitionTests
{
    [Fact]
    public void ReferenceToStandardDatumGeometryDependsOnNoFeature()
    {
        DatumReference reference = new DatumReference.OnGeometry(FeatureId.None, "Top");

        reference.ReferencedFeatures().Should().BeEmpty();
    }

    [Fact]
    public void ReferenceToOwnedGeometryDependsOnItsOwner()
    {
        FeatureId owner = FeatureId.New();
        DatumReference reference = new DatumReference.OnGeometry(owner, "Offset1");

        reference.ReferencedFeatures().Should().Equal(owner);
    }

    [Fact]
    public void ReferenceToTopologyDependsOnWhatTheNameDoes()
    {
        FeatureId extrude = FeatureId.New();
        PersistentName name = NameFrom(extrude);
        DatumReference reference = new DatumReference.OnTopology(name);

        // Delegated rather than recomputed: a name that spans several features is the naming
        // layer's business, and a second walk of it here would be a second thing to drift.
        reference.ReferencedFeatures().Should().Equal(name.ReferencedFeatures());
        reference.ReferencedFeatures().Should().Equal(extrude);
    }

    [Fact]
    public void InputsAreTheReferencesInDeclarationOrder()
    {
        DatumReference first = new DatumReference.OnGeometry(FeatureId.None, "Front");
        DatumReference second = new DatumReference.OnGeometry(FeatureId.None, "Top");
        DatumReference third = new DatumReference.OnGeometry(FeatureId.None, "Right");

        DatumDefinition definition =
            new DatumDefinition.PlaneThroughThreePoints("Plane1", first, second, third);

        definition.Inputs().Should().Equal(first, second, third);
    }

    [Fact]
    public void EveryConstructionReportsItsOwnInputs()
    {
        // A construction whose Inputs() forgot one of its references would contribute a missing
        // edge to the dependency graph, and the datum would go on resolving against a feature the
        // rebuild no longer thought it depended on. Nothing else notices, so this checks the count
        // against the arity each case was declared with.
        DatumReference a = new DatumReference.OnGeometry(FeatureId.None, "A");
        DatumReference b = new DatumReference.OnGeometry(FeatureId.None, "B");
        DatumReference c = new DatumReference.OnGeometry(FeatureId.None, "C");

        (DatumDefinition Definition, int Arity)[] cases =
        [
            (new DatumDefinition.PlaneOffsetFrom("d", a, 1), 1),
            (new DatumDefinition.PlaneThroughPoint("d", a, b), 2),
            (new DatumDefinition.PlaneAtAngle("d", a, b, 1), 2),
            (new DatumDefinition.PlaneThroughThreePoints("d", a, b, c), 3),
            (new DatumDefinition.PlaneMidwayBetween("d", a, b), 2),
            (new DatumDefinition.AxisThroughTwoPoints("d", a, b), 2),
            (new DatumDefinition.AxisAlongEdge("d", a), 1),
            (new DatumDefinition.AxisWherePlanesMeet("d", a, b), 2),
            (new DatumDefinition.AxisNormalToPlane("d", a, b), 2),
            (new DatumDefinition.PointAt("d", a), 1),
            (new DatumDefinition.PointAtCentreOf("d", a), 1),
            (new DatumDefinition.PointWhereAxisMeetsPlane("d", a, b), 2),
            (new DatumDefinition.PointAlongEdge("d", a, 0.5), 1),
        ];

        foreach ((DatumDefinition definition, int arity) in cases)
        {
            definition.Inputs().Length.Should().Be(arity, "{0} declares {1} references", definition, arity);
        }
    }

    [Fact]
    public void ReferencedFeaturesDoesNotRepeatOneNamedTwice()
    {
        // The ordinary case, not an unusual one: a plane through three vertices of a single
        // extrude names it three times.
        FeatureId extrude = FeatureId.New();
        DatumReference first = new DatumReference.OnTopology(NameFrom(extrude));
        DatumReference second = new DatumReference.OnTopology(NameFrom(extrude));
        DatumReference third = new DatumReference.OnTopology(NameFrom(extrude));

        DatumDefinition definition =
            new DatumDefinition.PlaneThroughThreePoints("Plane1", first, second, third);

        definition.ReferencedFeatures().Should().Equal(extrude);
    }

    [Fact]
    public void ReferencedFeaturesKeepsEveryDistinctFeature()
    {
        FeatureId first = FeatureId.New();
        FeatureId second = FeatureId.New();

        DatumDefinition definition = new DatumDefinition.AxisThroughTwoPoints(
            "Axis1",
            new DatumReference.OnTopology(NameFrom(first)),
            new DatumReference.OnTopology(NameFrom(second)));

        definition.ReferencedFeatures().Should().BeEquivalentTo(
            ImmutableArray.Create(first, second));
    }

    private static PersistentName NameFrom(FeatureId feature) => PersistentName.Of(
        NameSegment.Of(feature, ProvenanceKind.New, EntityRole.From(OperationRole.SideWall)));
}
