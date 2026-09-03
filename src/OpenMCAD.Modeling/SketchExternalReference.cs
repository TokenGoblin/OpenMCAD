using System.Collections.Immutable;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;
using OpenMCAD.Solver.Sketching;

namespace OpenMCAD.Modeling;

/// <summary>How a 3-D edge is brought into a sketch.</summary>
/// <remarks>
/// §5.6: "external references (project/convert/intersect edges from 3D, with a live parametric
/// link)". The three differ in what they demand of the edge and what they produce, and
/// <see cref="SketchExternalReferenceResolver"/> is where each is defined; this only names which
/// one a given reference asked for.
/// </remarks>
public enum SketchExternalReferenceOperation
{
    /// <summary>
    /// Drops the edge onto the sketch plane along the plane's normal, wherever the edge actually is.
    /// </summary>
    Project,

    /// <summary>
    /// Brings the edge in unchanged. Refuses rather than distorts when the edge is not already on
    /// the sketch plane — that is what <see cref="Project"/> is for.
    /// </summary>
    Convert,

    /// <summary>Finds where the edge crosses the sketch plane, as a point.</summary>
    Intersect,
}

/// <summary>
/// A live link from one sketch entity to a 3-D edge: what a sketch's "project", "convert" and
/// "intersect" tools each leave behind (P4-T11).
/// </summary>
/// <param name="Produces">
/// Which sketch entity this reference is responsible for. Assigned once, when the reference is
/// created, and never afterwards — the entity's geometry is replaced on every rebuild, but a
/// constraint attached to it, or a later feature naming it, has to keep pointing at the same one.
/// </param>
/// <param name="Source">The 3-D edge, as a persistent name (ADR-0005, §5.3).</param>
/// <param name="Operation">Which of the three operations produced it.</param>
/// <param name="IsConstruction">
/// Whether the resulting entity is construction geometry. An external reference is not swept or
/// extruded any more sensibly than hand-drawn construction geometry is, so this is offered rather
/// than always assumed — a converted edge that is meant to become part of the profile needs to say so.
/// </param>
/// <remarks>
/// The durable half, in the same shape as <see cref="SketchPlaneReference"/> and for the same
/// reason: a name, not coordinates. Storing the projected geometry instead would mean a sketch that
/// no longer followed the edge it was built from the moment the part it came from moved — exactly
/// the failure §5.3 exists to prevent one layer up. <see cref="SketchExternalReferenceResolver"/> is
/// what turns one of these into fresh <see cref="SketchEntity"/> geometry for a single rebuild.
/// </remarks>
public sealed record SketchExternalReference(
    SketchEntityId Produces,
    PersistentName Source,
    SketchExternalReferenceOperation Operation,
    bool IsConstruction = false)
{
    /// <summary>Gets every feature this reference depends on.</summary>
    /// <returns>The features, without duplicates.</returns>
    /// <remarks>
    /// Delegated to <see cref="PersistentName.ReferencedFeatures"/> rather than recomputed: a face
    /// modified after it was created can span several features, and a second, independent walk of
    /// that chain here would be a second place for it to drift from the one <see cref="PersistentName"/>
    /// already does.
    /// </remarks>
    public ImmutableArray<FeatureId> ReferencedFeatures() => Source.ReferencedFeatures();
}
