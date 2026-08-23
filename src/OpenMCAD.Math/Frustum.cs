namespace OpenMCAD.Math;

/// <summary>Which side of a frustum a volume falls on.</summary>
public enum FrustumPlacement
{
    /// <summary>Entirely beyond at least one plane. Nothing of it is visible.</summary>
    Outside,

    /// <summary>Crossing at least one plane. Partly visible.</summary>
    Intersecting,

    /// <summary>Entirely within every plane. Wholly visible.</summary>
    Inside,
}

/// <summary>
/// The six planes bounding what a camera can see.
/// </summary>
/// <remarks>
/// <para>
/// Used to skip bodies the camera cannot see (P2-T05). In a mechanical assembly this is worth far
/// more than it is in a game: a user zoomed in on one bracket of a thousand-part machine is looking
/// at a fraction of a per cent of the triangles, and drawing the rest costs the frame budget for
/// nothing.
/// </para>
/// <para>
/// <b>Extraction is the Gribb–Hartmann method</b>: each plane is a sum or difference of two rows of
/// the view-projection matrix. It is exact rather than approximate, and it works unchanged for
/// perspective and orthographic projections — which matters here, because a CAD viewport switches
/// between them constantly and a culler that quietly mis-handled one of them would drop geometry
/// in only one mode.
/// </para>
/// <para>
/// The planes face inwards, so a point is visible when its signed distance is non-negative against
/// all six.
/// </para>
/// </remarks>
public readonly struct Frustum : IEquatable<Frustum>
{
    private readonly Plane _left;
    private readonly Plane _right;
    private readonly Plane _bottom;
    private readonly Plane _top;
    private readonly Plane _near;
    private readonly Plane _far;

    /// <summary>Creates a frustum from six inward-facing planes.</summary>
    /// <param name="left">The left plane.</param>
    /// <param name="right">The right plane.</param>
    /// <param name="bottom">The bottom plane.</param>
    /// <param name="top">The top plane.</param>
    /// <param name="near">The near plane.</param>
    /// <param name="far">The far plane.</param>
    public Frustum(Plane left, Plane right, Plane bottom, Plane top, Plane near, Plane far)
    {
        _left = left;
        _right = right;
        _bottom = bottom;
        _top = top;
        _near = near;
        _far = far;
    }

    /// <summary>Gets the left plane.</summary>
    public Plane Left => _left;

    /// <summary>Gets the right plane.</summary>
    public Plane Right => _right;

    /// <summary>Gets the bottom plane.</summary>
    public Plane Bottom => _bottom;

    /// <summary>Gets the top plane.</summary>
    public Plane Top => _top;

    /// <summary>Gets the near plane.</summary>
    public Plane Near => _near;

    /// <summary>Gets the far plane.</summary>
    public Plane Far => _far;

    /// <summary>Compares two frustums.</summary>
    /// <param name="left">The first.</param>
    /// <param name="right">The second.</param>
    /// <returns>Whether they are equal.</returns>
    public static bool operator ==(Frustum left, Frustum right) => left.Equals(right);

    /// <summary>Compares two frustums.</summary>
    /// <param name="left">The first.</param>
    /// <param name="right">The second.</param>
    /// <returns>Whether they differ.</returns>
    public static bool operator !=(Frustum left, Frustum right) => !left.Equals(right);

    /// <summary>
    /// Extracts the six planes from a combined view-projection matrix.
    /// </summary>
    /// <param name="viewProjection">Projection multiplied by view.</param>
    /// <returns>The frustum that matrix describes.</returns>
    /// <remarks>
    /// The depth convention is D3D's, where the near plane maps to z = 0 rather than OpenGL's
    /// z = -1. That is why the near plane is row four alone rather than row four plus row three,
    /// and getting it wrong costs exactly the geometry closest to the camera.
    /// </remarks>
    public static Frustum FromViewProjection(Mat4d viewProjection)
    {
        // Row four plus or minus row n. Written out rather than looped: the asymmetry of the near
        // plane is the whole subtlety here, and a loop would hide it.
        return new Frustum(
            PlaneFrom(
                viewProjection.M41 + viewProjection.M11,
                viewProjection.M42 + viewProjection.M12,
                viewProjection.M43 + viewProjection.M13,
                viewProjection.M44 + viewProjection.M14),
            PlaneFrom(
                viewProjection.M41 - viewProjection.M11,
                viewProjection.M42 - viewProjection.M12,
                viewProjection.M43 - viewProjection.M13,
                viewProjection.M44 - viewProjection.M14),
            PlaneFrom(
                viewProjection.M41 + viewProjection.M21,
                viewProjection.M42 + viewProjection.M22,
                viewProjection.M43 + viewProjection.M23,
                viewProjection.M44 + viewProjection.M24),
            PlaneFrom(
                viewProjection.M41 - viewProjection.M21,
                viewProjection.M42 - viewProjection.M22,
                viewProjection.M43 - viewProjection.M23,
                viewProjection.M44 - viewProjection.M24),
            PlaneFrom(
                viewProjection.M31,
                viewProjection.M32,
                viewProjection.M33,
                viewProjection.M34),
            PlaneFrom(
                viewProjection.M41 - viewProjection.M31,
                viewProjection.M42 - viewProjection.M32,
                viewProjection.M43 - viewProjection.M33,
                viewProjection.M44 - viewProjection.M34));
    }

    /// <summary>
    /// Tests an axis-aligned box against the frustum.
    /// </summary>
    /// <param name="bounds">The box. An empty one is <see cref="FrustumPlacement.Outside"/>.</param>
    /// <returns>Where the box sits.</returns>
    /// <remarks>
    /// <para>
    /// The n-vertex test: for each plane, only the box corner furthest along the plane normal
    /// decides whether the box is wholly beyond it. That is one dot product per plane instead of
    /// eight, and it needs no corner array.
    /// </para>
    /// <para>
    /// This can report <see cref="FrustumPlacement.Intersecting"/> for a box that is in fact
    /// outside, where the box straddles two planes without entering the frustum between them. The
    /// consequence is drawing something invisible, occasionally; the alternative costs more than
    /// the draw it saves.
    /// </para>
    /// </remarks>
    public FrustumPlacement Classify(Bounds3d bounds)
    {
        if (bounds.IsEmpty)
        {
            return FrustumPlacement.Outside;
        }

        Vec3d min = bounds.Min;
        Vec3d max = bounds.Max;
        bool intersecting = false;

        // Written out rather than looped over an array of the six planes: this runs once per body
        // per frame, and allocating a six-element array each time would put the culler that exists
        // to save work onto the garbage collector's critical path.
        if (Beyond(_left, min, max, ref intersecting)
            || Beyond(_right, min, max, ref intersecting)
            || Beyond(_bottom, min, max, ref intersecting)
            || Beyond(_top, min, max, ref intersecting)
            || Beyond(_near, min, max, ref intersecting)
            || Beyond(_far, min, max, ref intersecting))
        {
            return FrustumPlacement.Outside;
        }

        return intersecting ? FrustumPlacement.Intersecting : FrustumPlacement.Inside;
    }

    /// <summary>Whether any part of a box may be visible.</summary>
    /// <param name="bounds">The box.</param>
    /// <returns>Whether it should be drawn.</returns>
    public bool Intersects(Bounds3d bounds) => Classify(bounds) != FrustumPlacement.Outside;

    /// <summary>Whether a point is inside every plane.</summary>
    /// <param name="point">The point.</param>
    /// <returns>Whether it is visible.</returns>
    public bool Contains(Vec3d point)
        => _left.SignedDistanceTo(point) >= 0
            && _right.SignedDistanceTo(point) >= 0
            && _bottom.SignedDistanceTo(point) >= 0
            && _top.SignedDistanceTo(point) >= 0
            && _near.SignedDistanceTo(point) >= 0
            && _far.SignedDistanceTo(point) >= 0;

    /// <inheritdoc />
    public bool Equals(Frustum other)
        => _left.Equals(other._left)
            && _right.Equals(other._right)
            && _bottom.Equals(other._bottom)
            && _top.Equals(other._top)
            && _near.Equals(other._near)
            && _far.Equals(other._far);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Frustum other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(_left, _right, _bottom, _top, _near, _far);

    /// <summary>
    /// Whether a box lies wholly beyond one plane, noting in passing whether it straddles it.
    /// </summary>
    /// <remarks>
    /// Only the box corner furthest along the plane normal can decide that the box is wholly
    /// outside, and only the corner furthest against it can decide the box is wholly inside. That
    /// is two dot products per plane rather than eight, with no corner array to build.
    /// </remarks>
    private static bool Beyond(Plane plane, Vec3d min, Vec3d max, ref bool intersecting)
    {
        Vec3d normal = plane.Normal;

        Vec3d positive = new(
            normal.X >= 0 ? max.X : min.X,
            normal.Y >= 0 ? max.Y : min.Y,
            normal.Z >= 0 ? max.Z : min.Z);

        if (plane.SignedDistanceTo(positive) < 0)
        {
            return true;
        }

        Vec3d negative = new(
            normal.X >= 0 ? min.X : max.X,
            normal.Y >= 0 ? min.Y : max.Y,
            normal.Z >= 0 ? min.Z : max.Z);

        if (plane.SignedDistanceTo(negative) < 0)
        {
            intersecting = true;
        }

        return false;
    }

    /// <summary>
    /// Builds a plane from the raw coefficients of ax + by + cz + d = 0, normalised.
    /// </summary>
    /// <remarks>
    /// <see cref="Plane"/> stores distance from the origin along the normal, which is the negation
    /// of the d in that equation, so the point it is anchored at is the normal scaled by that.
    /// Normalising is what makes <see cref="Plane.SignedDistanceTo"/> return a true distance rather
    /// than a scaled one — the sign would be right either way, so an unnormalised plane passes
    /// every inside/outside test and then quietly breaks the moment anything wants a distance.
    /// </remarks>
    private static Plane PlaneFrom(double a, double b, double c, double d)
    {
        Vec3d normal = new(a, b, c);
        double length = normal.Length;

        if (length < Tolerance.Linear)
        {
            // A degenerate matrix — a zero far plane, a collapsed projection. Returning a valid
            // plane rather than throwing keeps a mis-set camera from taking the frame loop down
            // with it; the frustum is nonsense either way, and nonsense that draws too much is a
            // great deal easier to diagnose than an exception from inside the renderer.
            return Plane.XY;
        }

        Vec3d unit = normal / length;

        return Plane.FromPointNormal(unit * (-d / length), unit);
    }
}
