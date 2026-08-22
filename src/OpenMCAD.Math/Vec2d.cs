using System.Globalization;

namespace OpenMCAD.Math;

/// <summary>
/// A double-precision 2D vector or point.
/// </summary>
/// <param name="X">The X component.</param>
/// <param name="Y">The Y component.</param>
/// <remarks>
/// Used for sketch geometry and parameter-space work. <c>System.Numerics</c> is deliberately not
/// used anywhere in OpenMCAD geometry: it is single-precision, and CAD needs double throughout
/// (PLAN.md 4.4).
/// </remarks>
public readonly record struct Vec2d(double X, double Y)
{
    /// <summary>Gets the zero vector.</summary>
    public static Vec2d Zero => default;

    /// <summary>Gets the vector with both components equal to one.</summary>
    public static Vec2d One => new(1.0, 1.0);

    /// <summary>Gets the unit vector along +X.</summary>
    public static Vec2d UnitX => new(1.0, 0.0);

    /// <summary>Gets the unit vector along +Y.</summary>
    public static Vec2d UnitY => new(0.0, 1.0);

    /// <summary>Gets the component at <paramref name="index"/> (0 = X, 1 = Y).</summary>
    /// <param name="index">The zero-based component index.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="index"/> is not 0 or 1.
    /// </exception>
    public double this[int index] => index switch
    {
        0 => X,
        1 => Y,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    /// <summary>
    /// Gets the squared Euclidean length. Prefer this over <see cref="Length"/> for comparisons.
    /// </summary>
    public double LengthSquared => (X * X) + (Y * Y);

    /// <summary>Gets the Euclidean length.</summary>
    public double Length => System.Math.Sqrt(LengthSquared);

    /// <summary>
    /// Gets a value indicating whether every component is finite (not NaN or infinity).
    /// </summary>
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);

    /// <summary>
    /// Gets a value indicating whether this vector is shorter than
    /// <see cref="Tolerance.LinearResolution"/>.
    /// </summary>
    public bool IsZeroLength
        => LengthSquared <= Tolerance.LinearResolution * Tolerance.LinearResolution;

    /// <summary>Adds two vectors.</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    public static Vec2d operator +(Vec2d a, Vec2d b) => new(a.X + b.X, a.Y + b.Y);

    /// <summary>Subtracts <paramref name="b"/> from <paramref name="a"/>.</summary>
    /// <param name="a">The minuend.</param>
    /// <param name="b">The subtrahend.</param>
    public static Vec2d operator -(Vec2d a, Vec2d b) => new(a.X - b.X, a.Y - b.Y);

    /// <summary>Negates a vector.</summary>
    /// <param name="v">The vector to negate.</param>
    public static Vec2d operator -(Vec2d v) => new(-v.X, -v.Y);

    /// <summary>Scales a vector.</summary>
    /// <param name="v">The vector.</param>
    /// <param name="s">The scalar.</param>
    public static Vec2d operator *(Vec2d v, double s) => new(v.X * s, v.Y * s);

    /// <summary>Scales a vector.</summary>
    /// <param name="s">The scalar.</param>
    /// <param name="v">The vector.</param>
    public static Vec2d operator *(double s, Vec2d v) => v * s;

    /// <summary>Divides a vector by a scalar.</summary>
    /// <param name="v">The vector.</param>
    /// <param name="s">The scalar divisor.</param>
    public static Vec2d operator /(Vec2d v, double s) => new(v.X / s, v.Y / s);

    /// <summary>Named alternative to the addition operator.</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    public static Vec2d Add(Vec2d a, Vec2d b) => a + b;

    /// <summary>Named alternative to the subtraction operator.</summary>
    /// <param name="a">The minuend.</param>
    /// <param name="b">The subtrahend.</param>
    public static Vec2d Subtract(Vec2d a, Vec2d b) => a - b;

    /// <summary>Named alternative to the multiplication operator.</summary>
    /// <param name="v">The vector.</param>
    /// <param name="s">The scalar.</param>
    public static Vec2d Multiply(Vec2d v, double s) => v * s;

    /// <summary>Named alternative to the division operator.</summary>
    /// <param name="v">The vector.</param>
    /// <param name="s">The scalar divisor.</param>
    public static Vec2d Divide(Vec2d v, double s) => v / s;

    /// <summary>Named alternative to the unary negation operator.</summary>
    /// <param name="v">The vector to negate.</param>
    public static Vec2d Negate(Vec2d v) => -v;

    /// <summary>Returns the dot product of two vectors.</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    public static double Dot(Vec2d a, Vec2d b) => (a.X * b.X) + (a.Y * b.Y);

    /// <summary>
    /// Returns the scalar cross product, that is the Z component of the equivalent 3D cross
    /// product.
    /// </summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    /// <remarks>
    /// Positive when <paramref name="b"/> is counter-clockwise from <paramref name="a"/>. This is
    /// the signed area of the parallelogram they span, and it is the workhorse of 2D orientation
    /// tests in the sketcher.
    /// </remarks>
    public static double Cross(Vec2d a, Vec2d b) => (a.X * b.Y) - (a.Y * b.X);

    /// <summary>Returns the distance between two points.</summary>
    /// <param name="a">The first point.</param>
    /// <param name="b">The second point.</param>
    public static double Distance(Vec2d a, Vec2d b) => (a - b).Length;

    /// <summary>Returns the squared distance between two points.</summary>
    /// <param name="a">The first point.</param>
    /// <param name="b">The second point.</param>
    public static double DistanceSquared(Vec2d a, Vec2d b) => (a - b).LengthSquared;

    /// <summary>Linearly interpolates between two vectors.</summary>
    /// <param name="a">The value at <paramref name="t"/> equal to zero.</param>
    /// <param name="b">The value at <paramref name="t"/> equal to one.</param>
    /// <param name="t">The interpolation parameter. Not clamped.</param>
    public static Vec2d Lerp(Vec2d a, Vec2d b, double t) => a + ((b - a) * t);

    /// <summary>Returns the component-wise minimum of two vectors.</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    public static Vec2d ComponentMin(Vec2d a, Vec2d b)
        => new(System.Math.Min(a.X, b.X), System.Math.Min(a.Y, b.Y));

    /// <summary>Returns the component-wise maximum of two vectors.</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    public static Vec2d ComponentMax(Vec2d a, Vec2d b)
        => new(System.Math.Max(a.X, b.X), System.Math.Max(a.Y, b.Y));

    /// <summary>Returns this vector scaled to unit length.</summary>
    /// <exception cref="InvalidOperationException">The vector is too short to normalise.</exception>
    /// <remarks>
    /// Use <see cref="TryNormalize"/> when a zero-length input is expected and recoverable.
    /// </remarks>
    public Vec2d Normalized()
        => TryNormalize(out Vec2d result)
            ? result
            : throw new InvalidOperationException(
                FormattableString.Invariant($"Cannot normalise a Vec2d of length {Length:G17}."));

    /// <summary>Attempts to scale this vector to unit length.</summary>
    /// <param name="result">The normalised vector, or <see cref="Zero"/> on failure.</param>
    /// <returns><see langword="true"/> if the vector was long enough to normalise.</returns>
    public bool TryNormalize(out Vec2d result)
    {
        double lengthSquared = LengthSquared;
        if (!double.IsFinite(lengthSquared)
            || lengthSquared <= Tolerance.LinearResolution * Tolerance.LinearResolution)
        {
            result = Zero;
            return false;
        }

        double inverse = 1.0 / System.Math.Sqrt(lengthSquared);
        result = new Vec2d(X * inverse, Y * inverse);
        return true;
    }

    /// <summary>Returns this vector rotated 90 degrees counter-clockwise.</summary>
    public Vec2d Perpendicular() => new(-Y, X);

    /// <summary>Returns this vector rotated counter-clockwise by <paramref name="radians"/>.</summary>
    /// <param name="radians">The rotation angle in radians.</param>
    public Vec2d Rotated(double radians)
    {
        double c = System.Math.Cos(radians);
        double s = System.Math.Sin(radians);
        return new Vec2d((X * c) - (Y * s), (X * s) + (Y * c));
    }

    /// <summary>
    /// Returns the counter-clockwise angle from this vector to <paramref name="other"/>, in
    /// radians, in the range from minus pi to pi.
    /// </summary>
    /// <param name="other">The vector to measure to.</param>
    public double SignedAngleTo(Vec2d other)
        => System.Math.Atan2(Cross(this, other), Dot(this, other));

    /// <summary>
    /// Returns the angle of this vector measured from +X, in radians, in the range from minus pi
    /// to pi.
    /// </summary>
    public double Angle() => System.Math.Atan2(Y, X);

    /// <summary>
    /// Returns <see langword="true"/> when this vector is within <paramref name="tolerance"/> of
    /// <paramref name="other"/> in every component.
    /// </summary>
    /// <param name="other">The vector to compare against.</param>
    /// <param name="tolerance">The non-negative per-component tolerance.</param>
    public bool IsNear(Vec2d other, double tolerance = Tolerance.Linear)
        => Tolerance.AreEqual(X, other.X, tolerance)
        && Tolerance.AreEqual(Y, other.Y, tolerance);

    /// <inheritdoc />
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"({X:G17}, {Y:G17})");
}
