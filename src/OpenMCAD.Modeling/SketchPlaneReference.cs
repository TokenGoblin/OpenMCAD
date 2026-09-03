using System.Collections.Immutable;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;

namespace OpenMCAD.Modeling;

/// <summary>
/// What a sketch is placed on: a name, not coordinates.
/// </summary>
/// <remarks>
/// <para>
/// P4-T10. §5.4 lists a plane reference as part of what a sketch holds, and this is that
/// reference — the durable half. <see cref="SketchPlaneResolver"/> is the other half, turning one
/// of these plus a <see cref="Document"/> into a <see cref="SketchPlane"/> for one rebuild.
/// Splitting the two is the same reason <see cref="PersistentName"/> is split from
/// <c>NameResolver</c>: a name is what a file holds and what survives an edit elsewhere in the
/// tree, and resolving it needs a document in hand that a record sitting in isolation does not
/// have.
/// </para>
/// <para>
/// <b>Three sources, three different ways of being named.</b> A datum plane and a custom
/// coordinate system are both <see cref="ReferenceGeometry"/> — identified by which feature made
/// them (or <see cref="FeatureId.None"/> for the origin geometry every document starts with) and
/// what they are called, the same pair <see cref="Document.FindReference"/> looks up by. A planar
/// face is kernel topology and gets no such name of its own; it is addressed the way every other
/// feature addresses kernel topology, through a <see cref="PersistentName"/> (ADR-0005, §5.3).
/// Always exactly one face — a sketch plane naming "every piece of a split face" has no meaning,
/// so this deliberately does not offer <see cref="MultiplicityPolicy"/> the way
/// <see cref="EntityReference"/> does for other consumers of kernel topology.
/// </para>
/// </remarks>
public abstract record SketchPlaneReference
{
    private SketchPlaneReference()
    {
    }

    /// <summary>A named datum plane.</summary>
    /// <param name="Owner">
    /// The feature that created it, or <see cref="FeatureId.None"/> for one of the three standard
    /// datums every document starts with.
    /// </param>
    /// <param name="Name">What it is called.</param>
    public sealed record OnDatumPlane(FeatureId Owner, string Name) : SketchPlaneReference;

    /// <summary>A named custom coordinate system, sketched on its XY plane.</summary>
    /// <param name="Owner">The feature that created it, or <see cref="FeatureId.None"/>.</param>
    /// <param name="Name">What it is called.</param>
    public sealed record OnCoordinateSystem(FeatureId Owner, string Name) : SketchPlaneReference;

    /// <summary>A planar face.</summary>
    /// <param name="Face">Its persistent name.</param>
    public sealed record OnFace(PersistentName Face) : SketchPlaneReference;

    /// <summary>Gets every feature this reference depends on.</summary>
    /// <returns>The features, without duplicates.</returns>
    /// <remarks>
    /// What the rebuild engine needs to know a sketch plane has gone stale (§5.4): if any of these
    /// is deleted, resolving this reference can no longer succeed. Empty for a reference to one of
    /// the standard datums, which no feature owns and which nothing can delete.
    /// </remarks>
    public ImmutableArray<FeatureId> ReferencedFeatures() => this switch
    {
        OnDatumPlane(var owner, _) => OneOrNone(owner),
        OnCoordinateSystem(var owner, _) => OneOrNone(owner),
        OnFace(var face) => face.ReferencedFeatures(),
        _ => [],
    };

    private static ImmutableArray<FeatureId> OneOrNone(FeatureId owner)
        => owner.IsValid ? [owner] : [];
}
