using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;
using OpenMCAD.Modeling;
using OpenMCAD.Solver.Sketching;

using Xunit;

namespace OpenMCAD.Modeling.Tests;

/// <summary>The durable half of P4-T11: what an external reference depends on.</summary>
public sealed class SketchExternalReferenceTests
{
    [Fact]
    public void ReferencedFeatures_DelegatesToTheSourcesPersistentName()
    {
        // Same reasoning as SketchPlaneReference.OnFace: PersistentName.ReferencedFeatures already
        // walks a chain that can span several features, and a second, independent walk here would
        // be a second place for that to drift from it.
        FeatureId extrude = FeatureId.New();
        FeatureId fillet = FeatureId.New();

        PersistentName source = PersistentName.Of(new NameSegment(
            fillet,
            ProvenanceKind.Modified,
            [new NameSource.Entity(PersistentName.Of(
                NameSegment.Of(extrude, ProvenanceKind.Generated, EntityRole.SideWall)))],
            EntityRole.BlendFace));

        SketchExternalReference reference = new(
            SketchEntityId.New(), source, SketchExternalReferenceOperation.Project);

        reference.ReferencedFeatures().Should().Equal(source.ReferencedFeatures());
        reference.ReferencedFeatures().Should().Equal(fillet, extrude);
    }
}
