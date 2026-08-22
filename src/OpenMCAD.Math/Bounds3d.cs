using System.Globalization;

namespace OpenMCAD.Math;

/// <summary>
/// An axis-aligned bounding box in 3D, which may be empty.
/// </summary>
/// <remarks>
/// <para>
/// Emptiness is tracked explicitly, so <c>default(Bounds3d)</c> is the empty box rather than a
/// degenerate box at the origin. That matters: an uninitialised bound that silently claimed to
/// contain the origin would quietly corrupt every accumulation it took part in.
/// </para>
/// <para>
/// An empty box reports <see cref="Min"/> as positive infinity and <see cref="Max"/> as negative
/// infinity. Those sentinels are chosen so that <see cref="Union(Bounds3d, Vec3d)"/> and
/// <see cref="Union(Bounds3d, Bounds3d)"/> need no special case: folding the first point into an
/// empty box yields a degenerate box at that point, which is correct.
/// </para>
/// <para>
/// A degenerate box — zero extent on one or more axes — is <i>not</i> empty. The bound of a
/// planar face legitimately has zero thickness, and code that conflates the two cases will
/// mishandle every planar body in the model.
/// </para>
/// </remarks>
public readonly record struct Bounds3d
{
    private readonly Vec3d _min;
    private readonly Vec3d _max;
    private readonly bool _isNotEmpty;

    /// <summary>Initialises a box from its corners.</summary>
    /// <param name="min">The minimum corner.</param>
    /// <param name="max">The maximum corner.</param>
    /// <remarks>
    /// No ordering is enforced on the caller. If <paramref name="min"/> exceeds
    /// <paramref name="max"/> on any axis, or either corner is not finite, the result is the
    /// empty box. This is deliberate: it makes an inverted range a representable, harmless
    /// "nothing" rather than a box with negative extent.
    /// </remarks>
    public Bounds3d(Vec3d min, Vec3d max)
    {
        if (min.IsFinite && max.IsFinite && min.X <= max.X && min.Y <= max.Y && min.Z <= max.Z)
        {
            _min = min;
            _max = max;
            _isNotEmpty = true;
        }
        else
        {
            // Normalise every empty box to the same representation so that all empty boxes
            // compare equal under the compiler-generated structural equality.
            _min = default;
            _max = default;
            _isNotEmpty = false;
        }
    }

    /// <summary>
    /// Gets the minimum corner, or positive infinity on every axis when the box is empty.
    /// </summary>
    public Vec3d Min => _isNotEmpty
        ? _min
        : new Vec3d(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);

    /// <summary>
    /// Gets the maximum corner, or negative infinity on every axis when the box is empty.
    /// </summary>
    public Vec3d Max => _isNotEmpty
        ? _max
        : new Vec3d(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity);

    /// <summary>Gets the empty box, which is also the default value of this type.</summary>
    public static Bounds3d Empty => default;

    /// <summary>Gets a value indicating whether the box contains no points.</summary>
    public bool IsEmpty => !_isNotEmpty;

    /// <summary>Gets the centre of the box.</summary>
    /// <exception cref="InvalidOperationException">The box is empty.</exception>
    public Vec3d Center => _isNotEmpty
        ? (_min + _max) * 0.5
        : throw new InvalidOperationException("An empty Bounds3d has no centre.");

    /// <summary>Gets the extent of the box along each axis, or zero when empty.</summary>
    public Vec3d Size => _isNotEmpty ? _max - _min : Vec3d.Zero;

    /// <summary>Gets the length of the box diagonal, or zero when empty.</summary>
    public double DiagonalLength => Size.Length;

    /// <summary>Gets the volume of the box, or zero when empty or degenerate.</summary>
    public double Volume
    {
        get
        {
            Vec3d size = Size;
            return size.X * size.Y * size.Z;
        }
    }

    /// <summary>Creates the smallest box containing a single point.</summary>
    /// <param name="point">The point.</param>
    public static Bounds3d FromPoint(Vec3d point) => new(point, point);

    /// <summary>Creates the smallest box containing every point in <paramref name="points"/>.</summary>
    /// <param name="points">The points to bound. May be empty.</param>
    /// <exception cref="ArgumentNullException"><paramref name="points"/> is <see langword="null"/>.</exception>
    public static Bounds3d FromPoints(IEnumerable<Vec3d> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        Bounds3d result = Empty;
        foreach (Vec3d point in points)
        {
            result = Union(result, point);
        }

        return result;
    }

    /// <summary>Returns the smallest box containing both inputs.</summary>
    /// <param name="a">The first box.</param>
    /// <param name="b">The second box.</param>
    public static Bounds3d Union(Bounds3d a, Bounds3d b) => new(
        Vec3d.ComponentMin(a.Min, b.Min),
        Vec3d.ComponentMax(a.Max, b.Max));

    /// <summary>Returns the smallest box containing <paramref name="box"/> and <paramref name="point"/>.</summary>
    /// <param name="box">The box.</param>
    /// <param name="point">The point to include.</param>
    public static Bounds3d Union(Bounds3d box, Vec3d point) => new(
        Vec3d.ComponentMin(box.Min, point),
        Vec3d.ComponentMax(box.Max, point));

    /// <summary>Returns the overlap of two boxes, which is empty when they do not overlap.</summary>
    /// <param name="a">The first box.</param>
    /// <param name="b">The second box.</param>
    public static Bounds3d Intersection(Bounds3d a, Bounds3d b)
    {
        if (a.IsEmpty || b.IsEmpty)
        {
            return Empty;
        }

        return new Bounds3d(
            Vec3d.ComponentMax(a.Min, b.Min),
            Vec3d.ComponentMin(a.Max, b.Max));
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="point"/> lies inside the box, grown by
    /// <paramref name="tolerance"/> on every side.
    /// </summary>
    /// <param name="point">The point to test.</param>
    /// <param name="tolerance">The non-negative tolerance by which to grow the box.</param>
    public bool Contains(Vec3d point, double tolerance = 0.0)
        => _isNotEmpty
        && point.X >= _min.X - tolerance && point.X <= _max.X + tolerance
        && point.Y >= _min.Y - tolerance && point.Y <= _max.Y + tolerance
        && point.Z >= _min.Z - tolerance && point.Z <= _max.Z + tolerance;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="other"/> lies entirely inside this box,
    /// grown by <paramref name="tolerance"/> on every side.
    /// </summary>
    /// <param name="other">The box to test.</param>
    /// <param name="tolerance">The non-negative tolerance by which to grow this box.</param>
    public bool Contains(Bounds3d other, double tolerance = 0.0)
        => _isNotEmpty
        && !other.IsEmpty
        && Contains(other.Min, tolerance)
        && Contains(other.Max, tolerance);

    /// <summary>
    /// Returns <see langword="true"/> when this box overlaps <paramref name="other"/>, with both
    /// grown by <paramref name="tolerance"/>.
    /// </summary>
    /// <param name="other">The box to test.</param>
    /// <param name="tolerance">The non-negative tolerance by which to grow both boxes.</param>
    public bool Intersects(Bounds3d other, double tolerance = 0.0)
        => _isNotEmpty
        && !other.IsEmpty
        && _min.X - tolerance <= other._max.X && _max.X + tolerance >= other._min.X
        && _min.Y - tolerance <= other._max.Y && _max.Y + tolerance >= other._min.Y
        && _min.Z - tolerance <= other._max.Z && _max.Z + tolerance >= other._min.Z;

    /// <summary>Returns this box grown by <paramref name="amount"/> on every side.</summary>
    /// <param name="amount">
    /// The amount to grow by. Negative values shrink the box, and shrinking past zero extent on
    /// any axis produces the empty box.
    /// </param>
    public Bounds3d Expanded(double amount)
    {
        if (!_isNotEmpty)
        {
            return Empty;
        }

        Vec3d delta = new(amount, amount, amount);
        return new Bounds3d(_min - delta, _max + delta);
    }

    /// <summary>Returns the eight corners of the box.</summary>
    /// <exception cref="InvalidOperationException">The box is empty.</exception>
    /// <remarks>
    /// Corner order is fixed and documented so callers can rely on it: the index bits select the
    /// maximum corner on X (bit 0), Y (bit 1), and Z (bit 2).
    /// </remarks>
    public Vec3d[] Corners()
    {
        if (!_isNotEmpty)
        {
            throw new InvalidOperationException("An empty Bounds3d has no corners.");
        }

        Vec3d[] corners = new Vec3d[8];
        for (int i = 0; i < 8; i++)
        {
            corners[i] = new Vec3d(
                (i & 1) == 0 ? _min.X : _max.X,
                (i & 2) == 0 ? _min.Y : _max.Y,
                (i & 4) == 0 ? _min.Z : _max.Z);
        }

        return corners;
    }

    /// <summary>
    /// Returns the axis-aligned box bounding this box after <paramref name="transform"/> is
    /// applied to it.
    /// </summary>
    /// <param name="transform">The transform to apply.</param>
    /// <remarks>
    /// The result bounds the transformed corners, so under rotation it is generally larger than
    /// the tight bound of the transformed contents. That is correct and expected for an
    /// axis-aligned box, but it means repeatedly transforming a bound inflates it. Re-derive the
    /// bound from geometry rather than chaining transforms on the bound itself.
    /// </remarks>
    public Bounds3d Transformed(Transform transform)
        => _isNotEmpty ? FromPoints(Corners().Select(transform.TransformPoint)) : Empty;

    /// <inheritdoc />
    public override string ToString() => _isNotEmpty
        ? string.Create(CultureInfo.InvariantCulture, $"Bounds3d(min={_min}, max={_max})")
        : "Bounds3d(empty)";
}
