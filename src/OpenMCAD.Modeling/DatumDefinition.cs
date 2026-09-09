using System.Collections.Immutable;

using OpenMCAD.Core.Documents;

namespace OpenMCAD.Modeling;

/// <summary>
/// How one piece of reference geometry is built: the durable half of a datum (P5-T03).
/// </summary>
/// <param name="Name">
/// What the datum will be called. Part of the definition rather than supplied at rebuild time
/// because it is half of the pair everything else points at a datum by —
/// <see cref="Document.FindReference"/> looks up <c>(Owner, Name)</c>, and a name that came from
/// somewhere other than the file would move the moment that somewhere changed.
/// </param>
/// <remarks>
/// <para>
/// Split from <see cref="ReferenceGeometry"/> the way <see cref="SketchPlaneReference"/> is split
/// from <see cref="SketchPlane"/>, and for the same reason: a definition says <em>offset 10 mm from
/// that face</em> and survives the face moving, while the plane it resolved to last rebuild is a
/// fact about last rebuild. Storing the resolved plane instead would give a datum that stopped
/// following what it was built on the first time anything upstream of it changed — which is what
/// §5.3 exists to prevent one layer up, and what a datum is for in the first place. Store the
/// coordinates and there was no reason to build a datum rather than type the numbers.
/// </para>
/// <para>
/// <b><see cref="ReferenceGeometry.Owner"/> is not here.</b> A definition does not know which
/// feature will host it until it is placed in one, and the same definition placed twice is two
/// datums. The owner is supplied by <see cref="DatumResolver"/>'s caller, which is the feature
/// being evaluated and therefore the one thing that does know.
/// </para>
/// <para>
/// <b>What is deliberately not here.</b> The constructions below are the ones that need nothing of
/// a kernel beyond a plane, a point or a curve — the queries <see cref="SketchPlaneResolver"/> and
/// <see cref="SketchExternalReferenceResolver"/> already take as delegates. A plane tangent to a
/// cylinder, a plane normal to a curve at a point, and an axis down a cylindrical face all need a
/// surface query no layer here has, and are left until there is one rather than approximated from
/// what is available.
/// </para>
/// </remarks>
public abstract record DatumDefinition(string Name)
{
    /// <summary>A plane parallel to another, a signed distance away along its normal.</summary>
    /// <param name="Name">What the datum will be called.</param>
    /// <param name="From">A datum plane, a coordinate system, or a planar face.</param>
    /// <param name="Distance">
    /// How far along <paramref name="From"/>'s normal to move. Signed, so the plane can be put on
    /// either side without asking the user to flip the thing they measured from.
    /// </param>
    public sealed record PlaneOffsetFrom(string Name, DatumReference From, double Distance)
        : DatumDefinition(Name);

    /// <summary>A plane through a point, parallel to another plane.</summary>
    /// <param name="Name">What the datum will be called.</param>
    /// <param name="Through">A datum point, a coordinate system, or a vertex.</param>
    /// <param name="ParallelTo">A datum plane, a coordinate system, or a planar face.</param>
    public sealed record PlaneThroughPoint(
        string Name, DatumReference Through, DatumReference ParallelTo)
        : DatumDefinition(Name);

    /// <summary>A plane rotated from another about an axis lying in it.</summary>
    /// <param name="Name">What the datum will be called.</param>
    /// <param name="From">A datum plane, a coordinate system, or a planar face.</param>
    /// <param name="About">
    /// A datum axis or a straight edge, which must lie in <paramref name="From"/>. Required rather
    /// than merely preferred: rotating about an axis parallel to the plane but off it would
    /// produce, at an angle of zero, an offset copy rather than the plane started from — an answer
    /// that disagrees with itself. The resolver refuses instead.
    /// </param>
    /// <param name="Angle">
    /// How far to rotate, in radians, anticlockwise about <paramref name="About"/>'s direction
    /// (right-hand rule).
    /// </param>
    public sealed record PlaneAtAngle(
        string Name, DatumReference From, DatumReference About, double Angle)
        : DatumDefinition(Name);

    /// <summary>A plane through three points.</summary>
    /// <param name="Name">What the datum will be called.</param>
    /// <param name="First">The first point.</param>
    /// <param name="Second">The second point.</param>
    /// <param name="Third">The third point.</param>
    /// <remarks>
    /// Oriented the way <see cref="OpenMCAD.Math.Plane.FromThreePoints"/> orients it: the normal is
    /// the one the three points wind anticlockwise about, so the order they are given in is a
    /// choice about which way the plane faces and not merely a listing.
    /// </remarks>
    public sealed record PlaneThroughThreePoints(
        string Name, DatumReference First, DatumReference Second, DatumReference Third)
        : DatumDefinition(Name);

    /// <summary>The plane halfway between two parallel planes.</summary>
    /// <param name="Name">What the datum will be called.</param>
    /// <param name="First">The first plane, which also decides which way the result faces.</param>
    /// <param name="Second">The second plane.</param>
    /// <remarks>
    /// Two planes that are not parallel have two bisectors, at right angles to each other, and
    /// nothing in the reference says which was meant. The resolver refuses rather than picking one.
    /// </remarks>
    public sealed record PlaneMidwayBetween(
        string Name, DatumReference First, DatumReference Second)
        : DatumDefinition(Name);

    /// <summary>An axis through two points.</summary>
    /// <param name="Name">What the datum will be called.</param>
    /// <param name="From">Where it starts. The axis is infinite; this is the origin recorded.</param>
    /// <param name="To">The second point, which fixes the direction.</param>
    public sealed record AxisThroughTwoPoints(string Name, DatumReference From, DatumReference To)
        : DatumDefinition(Name);

    /// <summary>An axis along a straight edge.</summary>
    /// <param name="Name">What the datum will be called.</param>
    /// <param name="Edge">The edge. Must be straight.</param>
    /// <remarks>
    /// The point of this construction is exactly that an edge is topology and has no name of its
    /// own to be pointed at from elsewhere: it turns one into named, resolvable reference geometry,
    /// which is what P5-T03 asks for. The axis is infinite, so it is the edge's line rather than
    /// the edge's extent.
    /// </remarks>
    public sealed record AxisAlongEdge(string Name, DatumReference Edge) : DatumDefinition(Name);

    /// <summary>The axis where two planes meet.</summary>
    /// <param name="Name">What the datum will be called.</param>
    /// <param name="First">The first plane.</param>
    /// <param name="Second">The second plane.</param>
    public sealed record AxisWherePlanesMeet(
        string Name, DatumReference First, DatumReference Second)
        : DatumDefinition(Name);

    /// <summary>An axis normal to a plane, through a point.</summary>
    /// <param name="Name">What the datum will be called.</param>
    /// <param name="Plane">The plane whose normal the axis runs along.</param>
    /// <param name="Through">
    /// The point the axis passes through. Not required to lie on the plane, and not projected onto
    /// it: an axis normal to a face through a vertex somewhere else is an ordinary thing to want,
    /// and quietly moving it to the foot of the perpendicular would put the axis somewhere the user
    /// did not point at.
    /// </param>
    public sealed record AxisNormalToPlane(
        string Name, DatumReference Plane, DatumReference Through)
        : DatumDefinition(Name);

    /// <summary>A point where something already is.</summary>
    /// <param name="Name">What the datum will be called.</param>
    /// <param name="At">A vertex, a datum point, or a coordinate system.</param>
    /// <remarks>
    /// Trivial-looking and the most useful of the three point constructions: a vertex is topology
    /// with no name of its own, and this is what gives one a name a later feature, a mate or a
    /// dimension can hold on to.
    /// </remarks>
    public sealed record PointAt(string Name, DatumReference At) : DatumDefinition(Name);

    /// <summary>The centre of a circular edge.</summary>
    /// <param name="Name">What the datum will be called.</param>
    /// <param name="Edge">The edge. Must be circular; an arc of one will do.</param>
    /// <remarks>
    /// Scoped to circular edges on purpose. "The centre of" a straight edge is not the same request
    /// as its midpoint — a circle's centre is not on the curve at all — and folding the two into
    /// one construction would make the name mean two different things depending on what it was
    /// pointed at. <see cref="PointAlongEdge"/> at a fraction of one half is the midpoint.
    /// </remarks>
    public sealed record PointAtCentreOf(string Name, DatumReference Edge) : DatumDefinition(Name);

    /// <summary>The point where an axis crosses a plane.</summary>
    /// <param name="Name">What the datum will be called.</param>
    /// <param name="Axis">A datum axis or a straight edge. Used as an infinite line.</param>
    /// <param name="Plane">The plane it crosses.</param>
    /// <remarks>
    /// The axis is infinite here even when it came from an edge, which is what distinguishes this
    /// from a sketch's <see cref="SketchExternalReferenceOperation.Intersect"/>: that one asks
    /// where an edge actually reaches the sketch plane and reports nothing when the edge stops
    /// short, because it is bringing a piece of real geometry into a profile. A datum is a
    /// construction, and an axis is by definition unbounded.
    /// </remarks>
    public sealed record PointWhereAxisMeetsPlane(
        string Name, DatumReference Axis, DatumReference Plane)
        : DatumDefinition(Name);

    /// <summary>A point a fraction of the way along an edge.</summary>
    /// <param name="Name">What the datum will be called.</param>
    /// <param name="Edge">The edge. Straight or circular.</param>
    /// <param name="Fraction">
    /// How far along, from 0 at the edge's start to 1 at its end. Outside that range is refused
    /// rather than extrapolated: continuing past a line's end is a different construction from
    /// picking a point on an edge, and an arc has nowhere to continue to.
    /// </param>
    public sealed record PointAlongEdge(string Name, DatumReference Edge, double Fraction)
        : DatumDefinition(Name);

    /// <summary>Gets the inputs this definition is built from, in declaration order.</summary>
    /// <returns>The references.</returns>
    /// <remarks>
    /// One list rather than a property per case, because every caller that wants them —
    /// <see cref="ReferencedFeatures"/> below, and a rebuild collecting what to resolve — wants all
    /// of them and none cares which slot a given one came from.
    /// </remarks>
    public ImmutableArray<DatumReference> Inputs() => this switch
    {
        PlaneOffsetFrom(_, var from, _) => [from],
        PlaneThroughPoint(_, var through, var parallelTo) => [through, parallelTo],
        PlaneAtAngle(_, var from, var about, _) => [from, about],
        PlaneThroughThreePoints(_, var first, var second, var third) => [first, second, third],
        PlaneMidwayBetween(_, var first, var second) => [first, second],
        AxisThroughTwoPoints(_, var from, var to) => [from, to],
        AxisAlongEdge(_, var edge) => [edge],
        AxisWherePlanesMeet(_, var first, var second) => [first, second],
        AxisNormalToPlane(_, var plane, var through) => [plane, through],
        PointAt(_, var at) => [at],
        PointAtCentreOf(_, var edge) => [edge],
        PointWhereAxisMeetsPlane(_, var axis, var plane) => [axis, plane],
        PointAlongEdge(_, var edge, _) => [edge],
        _ => [],
    };

    /// <summary>Gets every feature this definition depends on.</summary>
    /// <returns>The features, without duplicates.</returns>
    /// <remarks>
    /// The edge set a datum contributes to the dependency graph (§5.4). Deduplicated here rather
    /// than left to the caller because two inputs naming entities of the same body is the ordinary
    /// case, not an unusual one — a plane through three vertices of one extrude names it three
    /// times.
    /// </remarks>
    public ImmutableArray<FeatureId> ReferencedFeatures()
    {
        HashSet<FeatureId> seen = [];
        ImmutableArray<FeatureId>.Builder features = ImmutableArray.CreateBuilder<FeatureId>();

        foreach (DatumReference input in Inputs())
        {
            foreach (FeatureId feature in input.ReferencedFeatures())
            {
                if (seen.Add(feature))
                {
                    features.Add(feature);
                }
            }
        }

        return features.ToImmutable();
    }
}
