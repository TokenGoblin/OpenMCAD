using System.Globalization;

namespace OpenMCAD.Math;

/// <summary>
/// An oriented plane in 3D, stored as a unit normal and a signed distance from the world origin.
/// </summary>
/// <remarks>
/// <para>
/// The plane is the set of points satisfying <c>Dot(Normal, p) - DistanceFromOrigin == 0</c>.
/// Points on the side the normal points toward have a positive signed distance.
/// </para>
/// <para>
/// This is a bare geometric plane. A <i>sketch plane</i> is more than this: it carries an origin
/// and an in-plane X direction so that 2D sketch coordinates map deterministically into 3D, and
/// it carries a persistent name so it survives rebuild. That type lives in the modelling layer.
/// Use <see cref="CreateFrame"/> when you need a deterministic in-plane basis from a bare plane.
/// </para>
/// </remarks>
public readonly record struct Plane
{
    private Plane(Vec3d normal, double distanceFromOrigin)
    {
        Normal = normal;
        DistanceFromOrigin = distanceFromOrigin;
    }

    /// <summary>Gets the unit normal.</summary>
    public Vec3d Normal { get; }

    /// <summary>
    /// Gets the signed distance from the world origin to the plane, measured along
    /// <see cref="Normal"/>.
    /// </summary>
    public double DistanceFromOrigin { get; }

    /// <summary>Gets the world XY plane, with the normal along +Z.</summary>
    public static Plane XY => new(Vec3d.UnitZ, 0.0);

    /// <summary>Gets the world YZ plane, with the normal along +X.</summary>
    public static Plane YZ => new(Vec3d.UnitX, 0.0);

    /// <summary>Gets the world ZX plane, with the normal along +Y.</summary>
    public static Plane ZX => new(Vec3d.UnitY, 0.0);

    /// <summary>Gets the point on the plane closest to the world origin.</summary>
    public Vec3d Origin => Normal * DistanceFromOrigin;

    /// <summary>Creates a plane from a point on it and a normal.</summary>
    /// <param name="point">Any point on the plane.</param>
    /// <param name="normal">The normal. Need not be unit length, but must be non-degenerate.</param>
    /// <exception cref="InvalidOperationException"><paramref name="normal"/> is degenerate.</exception>
    public static Plane FromPointNormal(Vec3d point, Vec3d normal)
    {
        Vec3d unit = normal.Normalized();
        return new Plane(unit, Vec3d.Dot(unit, point));
    }

    /// <summary>Creates a plane through three points, wound counter-clockwise about the normal.</summary>
    /// <param name="a">The first point.</param>
    /// <param name="b">The second point.</param>
    /// <param name="c">The third point.</param>
    /// <exception cref="InvalidOperationException">The three points are collinear or coincident.</exception>
    public static Plane FromThreePoints(Vec3d a, Vec3d b, Vec3d c)
    {
        Vec3d normal = Vec3d.Cross(b - a, c - a);
        if (normal.IsZeroLength)
        {
            throw new InvalidOperationException(
                "Cannot construct a Plane from collinear or coincident points.");
        }

        return FromPointNormal(a, normal);
    }

    /// <summary>Returns this plane with the normal reversed.</summary>
    public Plane Flipped() => new(-Normal, -DistanceFromOrigin);

    /// <summary>
    /// Returns the signed distance from <paramref name="point"/> to the plane. Positive on the
    /// side <see cref="Normal"/> points toward.
    /// </summary>
    /// <param name="point">The point to measure.</param>
    public double SignedDistanceTo(Vec3d point) => Vec3d.Dot(Normal, point) - DistanceFromOrigin;

    /// <summary>Returns the closest point on the plane to <paramref name="point"/>.</summary>
    /// <param name="point">The point to project.</param>
    public Vec3d Project(Vec3d point) => point - (Normal * SignedDistanceTo(point));

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="point"/> lies on the plane within
    /// <paramref name="tolerance"/>.
    /// </summary>
    /// <param name="point">The point to test.</param>
    /// <param name="tolerance">The non-negative distance tolerance.</param>
    public bool Contains(Vec3d point, double tolerance = Tolerance.Linear)
        => System.Math.Abs(SignedDistanceTo(point)) <= tolerance;

    /// <summary>
    /// Intersects the plane with the infinite line through <paramref name="linePoint"/> along
    /// <paramref name="lineDirection"/>.
    /// </summary>
    /// <param name="linePoint">A point on the line.</param>
    /// <param name="lineDirection">The line direction. Need not be unit length.</param>
    /// <param name="intersection">The intersection point, or <see cref="Vec3d.Zero"/> on failure.</param>
    /// <returns>
    /// <see langword="false"/> when the line is parallel to the plane, whether or not it lies in
    /// it. A line lying in the plane has no single intersection point, so it is reported the same
    /// way; test with <see cref="Contains(Vec3d, double)"/> if you need to distinguish the cases.
    /// </returns>
    public bool TryIntersectLine(Vec3d linePoint, Vec3d lineDirection, out Vec3d intersection)
    {
        double denominator = Vec3d.Dot(Normal, lineDirection);
        if (System.Math.Abs(denominator) <= Tolerance.LinearResolution)
        {
            intersection = Vec3d.Zero;
            return false;
        }

        double t = -SignedDistanceTo(linePoint) / denominator;
        intersection = linePoint + (lineDirection * t);
        return intersection.IsFinite;
    }

    /// <summary>
    /// Produces a deterministic right-handed orthonormal frame whose Z axis is
    /// <see cref="Normal"/>.
    /// </summary>
    /// <param name="xAxis">The in-plane X direction.</param>
    /// <param name="yAxis">The in-plane Y direction.</param>
    /// <remarks>
    /// Determinism matters here: the frame feeds sketch-to-world mapping, and a frame that varied
    /// between rebuilds would move sketch geometry for no reason, violating ADR-0011.
    /// </remarks>
    public void CreateFrame(out Vec3d xAxis, out Vec3d yAxis)
    {
        xAxis = Normal.AnyPerpendicular();
        yAxis = Vec3d.Cross(Normal, xAxis).Normalized();
    }

    /// <summary>
    /// Returns <see langword="true"/> when this plane is geometrically the same as
    /// <paramref name="other"/>, with the same orientation.
    /// </summary>
    /// <param name="other">The plane to compare against.</param>
    /// <param name="linearTolerance">The non-negative distance tolerance.</param>
    /// <param name="angularTolerance">The non-negative angular tolerance, in radians.</param>
    public bool IsNear(
        Plane other,
        double linearTolerance = Tolerance.Linear,
        double angularTolerance = Tolerance.Angular)
        => Normal.AngleTo(other.Normal) <= angularTolerance
        && Tolerance.AreEqual(DistanceFromOrigin, other.DistanceFromOrigin, linearTolerance);

    /// <inheritdoc />
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"Plane(n={Normal}, d={DistanceFromOrigin:G17})");
}
