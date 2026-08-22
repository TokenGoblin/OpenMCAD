using System.Globalization;

namespace OpenMCAD.Math;

/// <summary>
/// A double-precision 3D vector, point, or direction.
/// </summary>
/// <param name="X">The X component.</param>
/// <param name="Y">The Y component.</param>
/// <param name="Z">The Z component.</param>
/// <remarks>
/// <para>
/// This is the fundamental spatial type in OpenMCAD. Coordinates are metres (ADR-0013).
/// </para>
/// <para>
/// The type does not distinguish points from directions at the type level. That distinction is
/// carried by the API that consumes it: <see cref="Transform.TransformPoint"/> applies
/// translation, <see cref="Transform.TransformDirection"/> does not. Getting this wrong is a
/// classic source of transform bugs, so read the method name before you call it.
/// </para>
/// </remarks>
public readonly record struct Vec3d(double X, double Y, double Z)
{
    /// <summary>Gets the zero vector, also the world origin.</summary>
    public static Vec3d Zero => default;

    /// <summary>Gets the vector with all components equal to one.</summary>
    public static Vec3d One => new(1.0, 1.0, 1.0);

    /// <summary>Gets the unit vector along +X.</summary>
    public static Vec3d UnitX => new(1.0, 0.0, 0.0);

    /// <summary>Gets the unit vector along +Y.</summary>
    public static Vec3d UnitY => new(0.0, 1.0, 0.0);

    /// <summary>Gets the unit vector along +Z.</summary>
    public static Vec3d UnitZ => new(0.0, 0.0, 1.0);

    /// <summary>Gets the component at <paramref name="index"/> (0 = X, 1 = Y, 2 = Z).</summary>
    /// <param name="index">The zero-based component index.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="index"/> is not 0, 1, or 2.
    /// </exception>
    public double this[int index] => index switch
    {
        0 => X,
        1 => Y,
        2 => Z,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    /// <summary>
    /// Gets the squared Euclidean length. Prefer this over <see cref="Length"/> for comparisons;
    /// it avoids a square root.
    /// </summary>
    public double LengthSquared => (X * X) + (Y * Y) + (Z * Z);

    /// <summary>Gets the Euclidean length.</summary>
    public double Length => System.Math.Sqrt(LengthSquared);

    /// <summary>
    /// Gets a value indicating whether every component is finite (not NaN or infinity).
    /// </summary>
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);

    /// <summary>
    /// Gets a value indicating whether this vector is shorter than
    /// <see cref="Tolerance.LinearResolution"/>.
    /// </summary>
    public bool IsZeroLength
        => LengthSquared <= Tolerance.LinearResolution * Tolerance.LinearResolution;

    /// <summary>Adds two vectors.</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    public static Vec3d operator +(Vec3d a, Vec3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    /// <summary>Subtracts <paramref name="b"/> from <paramref name="a"/>.</summary>
    /// <param name="a">The minuend.</param>
    /// <param name="b">The subtrahend.</param>
    public static Vec3d operator -(Vec3d a, Vec3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    /// <summary>Negates a vector.</summary>
    /// <param name="v">The vector to negate.</param>
    public static Vec3d operator -(Vec3d v) => new(-v.X, -v.Y, -v.Z);

    /// <summary>Scales a vector.</summary>
    /// <param name="v">The vector.</param>
    /// <param name="s">The scalar.</param>
    public static Vec3d operator *(Vec3d v, double s) => new(v.X * s, v.Y * s, v.Z * s);

    /// <summary>Scales a vector.</summary>
    /// <param name="s">The scalar.</param>
    /// <param name="v">The vector.</param>
    public static Vec3d operator *(double s, Vec3d v) => v * s;

    /// <summary>Divides a vector by a scalar.</summary>
    /// <param name="v">The vector.</param>
    /// <param name="s">The scalar divisor.</param>
    public static Vec3d operator /(Vec3d v, double s) => new(v.X / s, v.Y / s, v.Z / s);

    /// <summary>Named alternative to the addition operator.</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    public static Vec3d Add(Vec3d a, Vec3d b) => a + b;

    /// <summary>Named alternative to the subtraction operator.</summary>
    /// <param name="a">The minuend.</param>
    /// <param name="b">The subtrahend.</param>
    public static Vec3d Subtract(Vec3d a, Vec3d b) => a - b;

    /// <summary>Named alternative to the multiplication operator.</summary>
    /// <param name="v">The vector.</param>
    /// <param name="s">The scalar.</param>
    public static Vec3d Multiply(Vec3d v, double s) => v * s;

    /// <summary>Named alternative to the division operator.</summary>
    /// <param name="v">The vector.</param>
    /// <param name="s">The scalar divisor.</param>
    public static Vec3d Divide(Vec3d v, double s) => v / s;

    /// <summary>Named alternative to the unary negation operator.</summary>
    /// <param name="v">The vector to negate.</param>
    public static Vec3d Negate(Vec3d v) => -v;

    /// <summary>Returns the dot product of two vectors.</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    public static double Dot(Vec3d a, Vec3d b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    /// <summary>Returns the right-handed cross product of two vectors.</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    public static Vec3d Cross(Vec3d a, Vec3d b) => new(
        (a.Y * b.Z) - (a.Z * b.Y),
        (a.Z * b.X) - (a.X * b.Z),
        (a.X * b.Y) - (a.Y * b.X));

    /// <summary>Returns the distance between two points.</summary>
    /// <param name="a">The first point.</param>
    /// <param name="b">The second point.</param>
    public static double Distance(Vec3d a, Vec3d b) => (a - b).Length;

    /// <summary>Returns the squared distance between two points.</summary>
    /// <param name="a">The first point.</param>
    /// <param name="b">The second point.</param>
    public static double DistanceSquared(Vec3d a, Vec3d b) => (a - b).LengthSquared;

    /// <summary>Linearly interpolates between two vectors.</summary>
    /// <param name="a">The value at <paramref name="t"/> equal to zero.</param>
    /// <param name="b">The value at <paramref name="t"/> equal to one.</param>
    /// <param name="t">The interpolation parameter. Not clamped.</param>
    public static Vec3d Lerp(Vec3d a, Vec3d b, double t) => a + ((b - a) * t);

    /// <summary>Returns the component-wise minimum of two vectors.</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    public static Vec3d ComponentMin(Vec3d a, Vec3d b) => new(
        System.Math.Min(a.X, b.X),
        System.Math.Min(a.Y, b.Y),
        System.Math.Min(a.Z, b.Z));

    /// <summary>Returns the component-wise maximum of two vectors.</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    public static Vec3d ComponentMax(Vec3d a, Vec3d b) => new(
        System.Math.Max(a.X, b.X),
        System.Math.Max(a.Y, b.Y),
        System.Math.Max(a.Z, b.Z));

    /// <summary>Returns this vector scaled to unit length.</summary>
    /// <exception cref="InvalidOperationException">The vector is too short to normalise.</exception>
    /// <remarks>
    /// Use <see cref="TryNormalize"/> when a zero-length input is expected and recoverable, which
    /// in modelling code it usually is: a degenerate sketch or a zero-length extrude direction is
    /// a user error to report, not an exception to throw.
    /// </remarks>
    public Vec3d Normalized()
        => TryNormalize(out Vec3d result)
            ? result
            : throw new InvalidOperationException(
                FormattableString.Invariant($"Cannot normalise a Vec3d of length {Length:G17}."));

    /// <summary>Attempts to scale this vector to unit length.</summary>
    /// <param name="result">The normalised vector, or <see cref="Zero"/> on failure.</param>
    /// <returns><see langword="true"/> if the vector was long enough to normalise.</returns>
    public bool TryNormalize(out Vec3d result)
    {
        double lengthSquared = LengthSquared;
        if (!double.IsFinite(lengthSquared)
            || lengthSquared <= Tolerance.LinearResolution * Tolerance.LinearResolution)
        {
            result = Zero;
            return false;
        }

        double inverse = 1.0 / System.Math.Sqrt(lengthSquared);
        result = new Vec3d(X * inverse, Y * inverse, Z * inverse);
        return true;
    }

    /// <summary>
    /// Returns an arbitrary unit vector perpendicular to this one.
    /// </summary>
    /// <exception cref="InvalidOperationException">The vector is too short to work from.</exception>
    /// <remarks>
    /// The choice among the infinitely many perpendiculars is deterministic for a given input,
    /// which matters: rebuild determinism (ADR-0011) forbids anything here that depends on
    /// iteration order or floating-point noise. Used to build a reference frame from a single
    /// direction, for example a sketch plane from a normal.
    /// </remarks>
    public Vec3d AnyPerpendicular()
    {
        if (!TryNormalize(out Vec3d unit))
        {
            throw new InvalidOperationException(
                "Cannot construct a perpendicular to a zero-length Vec3d.");
        }

        // Cross with whichever principal axis this direction is least aligned to. Picking the
        // least-aligned axis keeps the cross product well conditioned.
        double ax = System.Math.Abs(unit.X);
        double ay = System.Math.Abs(unit.Y);
        double az = System.Math.Abs(unit.Z);

        Vec3d axis = ax <= ay && ax <= az ? UnitX
                   : ay <= az ? UnitY
                   : UnitZ;

        return Cross(unit, axis).Normalized();
    }

    /// <summary>
    /// Returns the unsigned angle between this vector and <paramref name="other"/>, in radians,
    /// in the range from zero to pi.
    /// </summary>
    /// <param name="other">The vector to measure to.</param>
    /// <exception cref="InvalidOperationException">Either vector is too short to normalise.</exception>
    /// <remarks>
    /// Uses the atan2 of the cross and dot products rather than acos of the dot product. The acos
    /// form loses catastrophic precision for nearly parallel and nearly antiparallel vectors,
    /// which is exactly where tangency and parallelism tests live.
    /// </remarks>
    public double AngleTo(Vec3d other)
    {
        Vec3d a = Normalized();
        Vec3d b = other.Normalized();
        return System.Math.Atan2(Cross(a, b).Length, Dot(a, b));
    }

    /// <summary>
    /// Returns the signed angle from this vector to <paramref name="other"/> about
    /// <paramref name="axis"/>, in radians, in the range from minus pi to pi.
    /// </summary>
    /// <param name="other">The vector to measure to.</param>
    /// <param name="axis">The rotation axis. Need not be unit length, but must be non-degenerate.</param>
    /// <exception cref="InvalidOperationException">Any argument is too short to normalise.</exception>
    public double SignedAngleTo(Vec3d other, Vec3d axis)
    {
        Vec3d a = Normalized();
        Vec3d b = other.Normalized();
        Vec3d n = axis.Normalized();
        return System.Math.Atan2(Dot(Cross(a, b), n), Dot(a, b));
    }

    /// <summary>
    /// Returns the projection of this vector onto <paramref name="direction"/>.
    /// </summary>
    /// <param name="direction">The direction to project onto. Need not be unit length.</param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="direction"/> is too short to normalise.
    /// </exception>
    public Vec3d ProjectedOnto(Vec3d direction)
    {
        Vec3d unit = direction.Normalized();
        return unit * Dot(this, unit);
    }

    /// <summary>
    /// Returns the component of this vector perpendicular to <paramref name="direction"/>.
    /// </summary>
    /// <param name="direction">The direction to remove. Need not be unit length.</param>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="direction"/> is too short to normalise.
    /// </exception>
    public Vec3d PerpendicularTo(Vec3d direction) => this - ProjectedOnto(direction);

    /// <summary>
    /// Returns <see langword="true"/> when this vector is within <paramref name="tolerance"/> of
    /// <paramref name="other"/> in every component.
    /// </summary>
    /// <param name="other">The vector to compare against.</param>
    /// <param name="tolerance">The non-negative per-component tolerance.</param>
    public bool IsNear(Vec3d other, double tolerance = Tolerance.Linear)
        => Tolerance.AreEqual(X, other.X, tolerance)
        && Tolerance.AreEqual(Y, other.Y, tolerance)
        && Tolerance.AreEqual(Z, other.Z, tolerance);

    /// <summary>
    /// Returns <see langword="true"/> when this direction is parallel or antiparallel to
    /// <paramref name="other"/> within <paramref name="angularTolerance"/>.
    /// </summary>
    /// <param name="other">The direction to compare against.</param>
    /// <param name="angularTolerance">The non-negative angular tolerance, in radians.</param>
    public bool IsParallelTo(Vec3d other, double angularTolerance = Tolerance.Angular)
    {
        if (!TryNormalize(out Vec3d a) || !other.TryNormalize(out Vec3d b))
        {
            return false;
        }

        return Cross(a, b).Length <= System.Math.Sin(angularTolerance) + Tolerance.AngularResolution;
    }

    /// <summary>
    /// Returns <see langword="true"/> when this direction is perpendicular to
    /// <paramref name="other"/> within <paramref name="angularTolerance"/>.
    /// </summary>
    /// <param name="other">The direction to compare against.</param>
    /// <param name="angularTolerance">The non-negative angular tolerance, in radians.</param>
    public bool IsPerpendicularTo(Vec3d other, double angularTolerance = Tolerance.Angular)
    {
        if (!TryNormalize(out Vec3d a) || !other.TryNormalize(out Vec3d b))
        {
            return false;
        }

        return System.Math.Abs(Dot(a, b))
            <= System.Math.Sin(angularTolerance) + Tolerance.AngularResolution;
    }

    /// <inheritdoc />
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"({X:G17}, {Y:G17}, {Z:G17})");
}
