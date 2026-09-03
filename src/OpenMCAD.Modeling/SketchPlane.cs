using OpenMCAD.Math;

namespace OpenMCAD.Modeling;

/// <summary>
/// The 3-D placement a sketch is drawn on, resolved for one rebuild: where its local origin sits
/// in world space, and how its two local axes map into it.
/// </summary>
/// <param name="Origin">Where the sketch's local (0, 0) sits, in world coordinates and metres.</param>
/// <param name="XAxis">The sketch's local X direction, in world coordinates. Unit length.</param>
/// <param name="YAxis">The sketch's local Y direction, in world coordinates. Unit length.</param>
/// <param name="Normal">
/// The plane's normal, <c>Cross(XAxis, YAxis)</c>. Unit length, and stored rather than derived so
/// that a caller asking which side of the sketch something is on does not have to recompute a
/// cross product to find out.
/// </param>
/// <remarks>
/// <para>
/// <b>This is the resolved value, not the durable one.</b> What survives a rebuild is a
/// <see cref="SketchPlaneReference"/> — a name — and this is what that name resolves to for one
/// particular rebuild, via <see cref="SketchPlaneResolver"/>. A feature holding one of these
/// instead of a reference would be holding coordinates rather than intent (§5.3): a sketch on a
/// datum plane that nobody had moved yet would freeze at that plane's current position the moment
/// it was cached, and stop following the datum the first time someone dragged it.
/// </para>
/// <para>
/// <b>Only <see cref="XAxis"/> is new information over <see cref="OpenMCAD.Math.Plane"/>.</b> A
/// bare plane has a normal and a distance from the world origin; it has no rotation about that
/// normal, because nothing about a plane picks one. A sketch needs one regardless — entities are
/// stored as 2-D coordinates (P4-T03), and mapping those into 3-D takes an X direction as much as
/// it takes an origin. <see cref="FromPlane"/> and <see cref="FromNormal"/> are what supply one
/// when nothing else does, via <see cref="Vec3d.AnyPerpendicular"/> — deterministically, so that
/// two rebuilds of the same document place the same sketch geometry at the same 3-D points
/// (ADR-0011).
/// </para>
/// </remarks>
public sealed record SketchPlane(Vec3d Origin, Vec3d XAxis, Vec3d YAxis, Vec3d Normal)
{
    /// <summary>Gets the sketch plane through the world origin with its normal along +Z.</summary>
    /// <remarks>What the standard "Top" datum resolves to, and a convenient default for tests.</remarks>
    public static SketchPlane WorldXY { get; } = FromPlane(Plane.XY);

    /// <summary>Builds a sketch plane from a bare plane, inventing a deterministic X axis for it.</summary>
    /// <param name="plane">The plane to sketch on.</param>
    /// <returns>The sketch plane.</returns>
    public static SketchPlane FromPlane(Plane plane)
    {
        plane.CreateFrame(out Vec3d xAxis, out Vec3d yAxis);
        return new SketchPlane(plane.Origin, xAxis, yAxis, plane.Normal);
    }

    /// <summary>Builds a sketch plane from a point on it and a normal, inventing an X axis.</summary>
    /// <param name="origin">A point on the plane, in world coordinates.</param>
    /// <param name="normal">The normal. Need not be unit length, but must be non-degenerate.</param>
    /// <returns>The sketch plane.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="normal"/> is degenerate.</exception>
    public static SketchPlane FromNormal(Vec3d origin, Vec3d normal)
        => FromPlane(Plane.FromPointNormal(origin, normal));

    /// <summary>Builds a sketch plane from an explicit origin, X axis and normal.</summary>
    /// <param name="origin">Where the sketch's local (0, 0) sits, in world coordinates.</param>
    /// <param name="xAxis">
    /// The desired X direction. Need not already be perpendicular to <paramref name="normal"/> — it
    /// is projected onto the plane and renormalised, the same latitude a custom coordinate system's
    /// own axes are given (P3's <c>ReferenceGeometry.CoordinateSystem</c> does not check its stored
    /// X and Z axes for mutual orthogonality either, and derives its Y axis the same way).
    /// </param>
    /// <param name="normal">The normal. Need not be unit length.</param>
    /// <returns>The sketch plane.</returns>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="normal"/> is degenerate, or <paramref name="xAxis"/> is parallel to it and so
    /// has nothing left once its component along the normal is removed.
    /// </exception>
    public static SketchPlane FromFrame(Vec3d origin, Vec3d xAxis, Vec3d normal)
    {
        Vec3d unitNormal = normal.Normalized();
        Vec3d unitXAxis = xAxis.PerpendicularTo(unitNormal).Normalized();
        Vec3d yAxis = Vec3d.Cross(unitNormal, unitXAxis);

        return new SketchPlane(origin, unitXAxis, yAxis, unitNormal);
    }

    /// <summary>Gets the bare plane this sketch plane sits on.</summary>
    public Plane Plane => Plane.FromPointNormal(Origin, Normal);

    /// <summary>Maps a point in the sketch's local coordinates into world space.</summary>
    /// <param name="local">The local point.</param>
    /// <returns>The world point.</returns>
    public Vec3d ToWorld(Vec2d local) => Origin + (XAxis * local.X) + (YAxis * local.Y);

    /// <summary>
    /// Maps a world point onto the sketch's local coordinates, by projecting it onto the plane.
    /// </summary>
    /// <param name="world">The world point.</param>
    /// <returns>
    /// Its local coordinates. Round-trips through <see cref="ToWorld"/> only for a point already on
    /// the plane — a point off it is silently projected first, since local coordinates have nowhere
    /// to record a distance out of plane.
    /// </returns>
    public Vec2d ToLocal(Vec3d world) => ToLocalDirection(world - Origin);

    /// <summary>
    /// Maps a world <em>direction</em> (not a point) onto the sketch's local coordinates, dropping
    /// its component along <see cref="Normal"/>.
    /// </summary>
    /// <param name="worldDirection">The direction.</param>
    /// <returns>
    /// Its local components. Unlike <see cref="ToLocal"/> this takes no origin — a direction has no
    /// position to be relative to — which is what makes it the right tool for carrying a curve's own
    /// in-plane reference direction (<c>WorldCurve.Circle.XDirection</c>) into the sketch's frame
    /// without also, incorrectly, subtracting the sketch's origin from it.
    /// </returns>
    public Vec2d ToLocalDirection(Vec3d worldDirection)
        => new(Vec3d.Dot(worldDirection, XAxis), Vec3d.Dot(worldDirection, YAxis));

    /// <summary>
    /// Returns <see langword="true"/> when this sketch plane is geometrically the same as
    /// <paramref name="other"/>, with the same orientation.
    /// </summary>
    /// <param name="other">The sketch plane to compare against.</param>
    /// <param name="linearTolerance">The non-negative distance tolerance.</param>
    /// <param name="angularTolerance">The non-negative angular tolerance, in radians.</param>
    /// <remarks>
    /// Compares <see cref="Origin"/>, <see cref="XAxis"/> and <see cref="Normal"/> only.
    /// <see cref="YAxis"/> is redundant with those two for any plane built by this type's own
    /// factories, and this deliberately does not police the invariant of one built by hand.
    /// </remarks>
    public bool IsNear(
        SketchPlane other,
        double linearTolerance = Tolerance.Linear,
        double angularTolerance = Tolerance.Angular)
        => Vec3d.Distance(Origin, other.Origin) <= linearTolerance
        && XAxis.AngleTo(other.XAxis) <= angularTolerance
        && Normal.AngleTo(other.Normal) <= angularTolerance;
}
