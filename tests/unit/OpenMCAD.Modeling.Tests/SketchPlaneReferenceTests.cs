using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;
using OpenMCAD.Modeling;

using Xunit;

namespace OpenMCAD.Modeling.Tests;

/// <summary>
/// The durable half of P4-T10: what a sketch plane reference depends on.
/// </summary>
public sealed class SketchPlaneReferenceTests
{
    [Fact]
    public void OnDatumPlane_OwnedByNoFeatureHasNoDependency()
    {
        // A standard datum (FeatureId.None) can never be deleted, so nothing needs to be in the
        // rebuild's dependency graph on its account.
        SketchPlaneReference reference = new SketchPlaneReference.OnDatumPlane(FeatureId.None, "Front");

        reference.ReferencedFeatures().Should().BeEmpty();
    }

    [Fact]
    public void OnDatumPlane_OwnedByAFeatureDependsOnIt()
    {
        FeatureId owner = FeatureId.New();
        SketchPlaneReference reference = new SketchPlaneReference.OnDatumPlane(owner, "Plane1");

        reference.ReferencedFeatures().Should().Equal(owner);
    }

    [Fact]
    public void OnCoordinateSystem_OwnedByNoFeatureHasNoDependency()
    {
        SketchPlaneReference reference =
            new SketchPlaneReference.OnCoordinateSystem(FeatureId.None, "Origin");

        reference.ReferencedFeatures().Should().BeEmpty();
    }

    [Fact]
    public void OnCoordinateSystem_OwnedByAFeatureDependsOnIt()
    {
        FeatureId owner = FeatureId.New();
        SketchPlaneReference reference = new SketchPlaneReference.OnCoordinateSystem(owner, "CS1");

        reference.ReferencedFeatures().Should().Equal(owner);
    }

    [Fact]
    public void OnFace_DependsOnEveryFeatureItsPersistentNameDoes()
    {
        // Delegated rather than recomputed: PersistentName.ReferencedFeatures already walks a
        // chain that can span several features (a face modified after it was created), and a
        // second, independent walk here would be a second place for that logic to drift from it.
        FeatureId extrude = FeatureId.New();
        FeatureId fillet = FeatureId.New();

        PersistentName face = PersistentName.Of(new NameSegment(
            fillet,
            ProvenanceKind.Modified,
            [new NameSource.Entity(PersistentName.Of(
                NameSegment.Of(extrude, ProvenanceKind.Generated, EntityRole.SideWall)))],
            EntityRole.BlendFace));

        SketchPlaneReference reference = new SketchPlaneReference.OnFace(face);

        reference.ReferencedFeatures().Should().Equal(face.ReferencedFeatures());
        reference.ReferencedFeatures().Should().Equal(fillet, extrude);
    }

    [Fact]
    public void ReferenceGeometryStandardDatums_AreAllOwnedByNoFeature()
    {
        // The premise OnDatumPlane_OwnedByNoFeatureHasNoDependency rests on: every datum a document
        // starts with really is owned by FeatureId.None, so a sketch on "Front" out of the box never
        // enters the dependency graph at all.
        ReferenceGeometry[] datums = ReferenceGeometry.StandardDatums();

        datums.Should().NotBeEmpty();
        datums.Should().OnlyContain(d => d.Owner == FeatureId.None);
    }
}
