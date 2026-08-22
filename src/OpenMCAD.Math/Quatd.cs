using System.Globalization;

namespace OpenMCAD.Math;

/// <summary>
/// A double-precision quaternion, used to represent 3D rotations.
/// </summary>
/// <param name="X">The X component of the vector part.</param>
/// <param name="Y">The Y component of the vector part.</param>
/// <param name="Z">The Z component of the vector part.</param>
/// <param name="W">The scalar part.</param>
/// <remarks>
/// <para>
/// Rotations are stored as quaternions rather than matrices or Euler angles throughout OpenMCAD.
/// Quaternions compose without accumulating shear, renormalise cheaply, and interpolate
/// correctly. Euler angles appear only at the user-interface boundary, never in storage.
/// </para>
/// <para>
/// Only unit quaternions represent rotations. Methods that assume unit length say so; use
/// <see cref="Normalized"/> after any sequence of operations that could drift.
/// </para>
/// </remarks>
public readonly record struct Quatd(double X, double Y, double Z, double W)
{
    /// <summary>Gets the identity rotation.</summary>
    public static Quatd Identity => new(0.0, 0.0, 0.0, 1.0);

    /// <summary>Gets the squared magnitude of the quaternion.</summary>
    public double LengthSquared => (X * X) + (Y * Y) + (Z * Z) + (W * W);

    /// <summary>Gets the magnitude of the quaternion.</summary>
    public double Length => System.Math.Sqrt(LengthSquared);

    /// <summary>
    /// Gets a value indicating whether every component is finite (not NaN or infinity).
    /// </summary>
    public bool IsFinite
        => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z) && double.IsFinite(W);

    /// <summary>
    /// Gets a value indicating whether this quaternion is within
    /// <see cref="Tolerance.Angular"/> of unit magnitude.
    /// </summary>
    public bool IsUnit => Tolerance.AreEqual(LengthSquared, 1.0, Tolerance.Angular);

    /// <summary>
    /// Composes two rotations. The result applies <paramref name="b"/> first, then
    /// <paramref name="a"/>.
    /// </summary>
    /// <param name="a">The rotation applied second.</param>
    /// <param name="b">The rotation applied first.</param>
    /// <remarks>
    /// This ordering matches matrix composition, so <c>(a * b).Rotate(v)</c> equals
    /// <c>a.Rotate(b.Rotate(v))</c>. Getting the order backwards is the single most common
    /// quaternion bug; the test suite pins it.
    /// </remarks>
    public static Quatd operator *(Quatd a, Quatd b) => new(
        (a.W * b.X) + (a.X * b.W) + (a.Y * b.Z) - (a.Z * b.Y),
        (a.W * b.Y) - (a.X * b.Z) + (a.Y * b.W) + (a.Z * b.X),
        (a.W * b.Z) + (a.X * b.Y) - (a.Y * b.X) + (a.Z * b.W),
        (a.W * b.W) - (a.X * b.X) - (a.Y * b.Y) - (a.Z * b.Z));

    /// <summary>Negates every component. Represents the same rotation.</summary>
    /// <param name="q">The quaternion to negate.</param>
    public static Quatd operator -(Quatd q) => new(-q.X, -q.Y, -q.Z, -q.W);

    /// <summary>Named alternative to the multiplication operator.</summary>
    /// <param name="a">The rotation applied second.</param>
    /// <param name="b">The rotation applied first.</param>
    public static Quatd Multiply(Quatd a, Quatd b) => a * b;

    /// <summary>Named alternative to the unary negation operator.</summary>
    /// <param name="q">The quaternion to negate.</param>
    public static Quatd Negate(Quatd q) => -q;

    /// <summary>Returns the dot product of two quaternions.</summary>
    /// <param name="a">The first quaternion.</param>
    /// <param name="b">The second quaternion.</param>
    public static double Dot(Quatd a, Quatd b)
        => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z) + (a.W * b.W);

    /// <summary>
    /// Creates a rotation of <paramref name="radians"/> about <paramref name="axis"/>, following
    /// the right-hand rule.
    /// </summary>
    /// <param name="axis">The rotation axis. Need not be unit length, but must be non-degenerate.</param>
    /// <param name="radians">The rotation angle in radians.</param>
    /// <exception cref="InvalidOperationException"><paramref name="axis"/> is degenerate.</exception>
    public static Quatd FromAxisAngle(Vec3d axis, double radians)
    {
        Vec3d unit = axis.Normalized();
        double half = radians * 0.5;
        double s = System.Math.Sin(half);
        return new Quatd(unit.X * s, unit.Y * s, unit.Z * s, System.Math.Cos(half));
    }

    /// <summary>
    /// Creates the shortest rotation carrying <paramref name="from"/> onto <paramref name="to"/>.
    /// </summary>
    /// <param name="from">The source direction. Need not be unit length.</param>
    /// <param name="to">The target direction. Need not be unit length.</param>
    /// <exception cref="InvalidOperationException">Either direction is degenerate.</exception>
    /// <remarks>
    /// Handles the antiparallel case, where the rotation is a half turn about an arbitrary
    /// perpendicular, deterministically via <see cref="Vec3d.AnyPerpendicular"/>.
    /// </remarks>
    public static Quatd FromTo(Vec3d from, Vec3d to)
    {
        Vec3d a = from.Normalized();
        Vec3d b = to.Normalized();
        double dot = Vec3d.Dot(a, b);

        if (dot >= 1.0 - Tolerance.Angular)
        {
            return Identity;
        }

        if (dot <= -1.0 + Tolerance.Angular)
        {
            return FromAxisAngle(a.AnyPerpendicular(), System.Math.PI);
        }

        Vec3d axis = Vec3d.Cross(a, b);
        return new Quatd(axis.X, axis.Y, axis.Z, 1.0 + dot).Normalized();
    }

    /// <summary>
    /// Creates a rotation from an orthonormal basis whose columns are the images of +X, +Y, +Z.
    /// </summary>
    /// <param name="xAxis">The image of +X. Must be unit length.</param>
    /// <param name="yAxis">The image of +Y. Must be unit length and perpendicular to the others.</param>
    /// <param name="zAxis">The image of +Z. Must be unit length and perpendicular to the others.</param>
    /// <remarks>
    /// Uses Shepperd's method: pick the largest of the four possible divisors so the square root
    /// is never taken of a near-zero quantity. The naive single-branch formulation is numerically
    /// unusable near 180-degree rotations.
    /// </remarks>
    public static Quatd FromBasis(Vec3d xAxis, Vec3d yAxis, Vec3d zAxis)
    {
        double m00 = xAxis.X, m10 = xAxis.Y, m20 = xAxis.Z;
        double m01 = yAxis.X, m11 = yAxis.Y, m21 = yAxis.Z;
        double m02 = zAxis.X, m12 = zAxis.Y, m22 = zAxis.Z;

        double trace = m00 + m11 + m22;

        if (trace > 0.0)
        {
            double s = System.Math.Sqrt(trace + 1.0) * 2.0;
            return new Quatd((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25 * s)
                .Normalized();
        }

        if (m00 > m11 && m00 > m22)
        {
            double s = System.Math.Sqrt(1.0 + m00 - m11 - m22) * 2.0;
            return new Quatd(0.25 * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s)
                .Normalized();
        }

        if (m11 > m22)
        {
            double s = System.Math.Sqrt(1.0 + m11 - m00 - m22) * 2.0;
            return new Quatd((m01 + m10) / s, 0.25 * s, (m12 + m21) / s, (m02 - m20) / s)
                .Normalized();
        }

        {
            double s = System.Math.Sqrt(1.0 + m22 - m00 - m11) * 2.0;
            return new Quatd((m02 + m20) / s, (m12 + m21) / s, 0.25 * s, (m10 - m01) / s)
                .Normalized();
        }
    }

    /// <summary>Returns this quaternion scaled to unit magnitude.</summary>
    /// <exception cref="InvalidOperationException">The quaternion is degenerate.</exception>
    public Quatd Normalized()
    {
        double lengthSquared = LengthSquared;
        if (!double.IsFinite(lengthSquared) || lengthSquared <= Tolerance.AngularResolution)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"Cannot normalise a Quatd of length {Length:G17}."));
        }

        double inverse = 1.0 / System.Math.Sqrt(lengthSquared);
        return new Quatd(X * inverse, Y * inverse, Z * inverse, W * inverse);
    }

    /// <summary>Returns the conjugate, which for a unit quaternion is the inverse rotation.</summary>
    public Quatd Conjugate() => new(-X, -Y, -Z, W);

    /// <summary>Returns the inverse rotation, valid for any non-degenerate quaternion.</summary>
    /// <exception cref="InvalidOperationException">The quaternion is degenerate.</exception>
    public Quatd Inverse()
    {
        double lengthSquared = LengthSquared;
        if (!double.IsFinite(lengthSquared) || lengthSquared <= Tolerance.AngularResolution)
        {
            throw new InvalidOperationException("Cannot invert a degenerate Quatd.");
        }

        double inverse = 1.0 / lengthSquared;
        return new Quatd(-X * inverse, -Y * inverse, -Z * inverse, W * inverse);
    }

    /// <summary>Rotates <paramref name="v"/> by this rotation.</summary>
    /// <param name="v">The vector to rotate.</param>
    /// <remarks>
    /// Assumes this quaternion is unit length. Uses the standard shuffle that avoids building a
    /// rotation matrix.
    /// </remarks>
    public Vec3d Rotate(Vec3d v)
    {
        Vec3d u = new(X, Y, Z);
        Vec3d t = Vec3d.Cross(u, v) * 2.0;
        return v + (t * W) + Vec3d.Cross(u, t);
    }

    /// <summary>Gets the rotation axis and angle equivalent to this rotation.</summary>
    /// <param name="axis">The unit rotation axis, or +X for the identity rotation.</param>
    /// <param name="radians">The rotation angle in radians, in the range from zero to pi.</param>
    public void ToAxisAngle(out Vec3d axis, out double radians)
    {
        Quatd q = Normalized();

        // Canonicalise to the positive-W hemisphere so the angle lands in [0, pi].
        if (q.W < 0.0)
        {
            q = -q;
        }

        double vectorLength = System.Math.Sqrt((q.X * q.X) + (q.Y * q.Y) + (q.Z * q.Z));
        if (vectorLength <= Tolerance.AngularResolution)
        {
            axis = Vec3d.UnitX;
            radians = 0.0;
            return;
        }

        axis = new Vec3d(q.X / vectorLength, q.Y / vectorLength, q.Z / vectorLength);
        radians = 2.0 * System.Math.Atan2(vectorLength, q.W);
    }

    /// <summary>
    /// Spherically interpolates between two rotations along the shortest arc.
    /// </summary>
    /// <param name="a">The rotation at <paramref name="t"/> equal to zero.</param>
    /// <param name="b">The rotation at <paramref name="t"/> equal to one.</param>
    /// <param name="t">The interpolation parameter, clamped to the range zero to one.</param>
    public static Quatd Slerp(Quatd a, Quatd b, double t)
    {
        t = Tolerance.Clamp(t, 0.0, 1.0);

        Quatd p = a.Normalized();
        Quatd q = b.Normalized();

        double dot = Dot(p, q);
        if (dot < 0.0)
        {
            q = -q;
            dot = -dot;
        }

        // Nearly coincident: fall back to normalised linear interpolation to avoid dividing by a
        // vanishing sin(theta).
        if (dot > 1.0 - 1e-12)
        {
            return new Quatd(
                p.X + ((q.X - p.X) * t),
                p.Y + ((q.Y - p.Y) * t),
                p.Z + ((q.Z - p.Z) * t),
                p.W + ((q.W - p.W) * t)).Normalized();
        }

        double theta = System.Math.Acos(Tolerance.Clamp(dot, -1.0, 1.0));
        double sinTheta = System.Math.Sin(theta);
        double wp = System.Math.Sin((1.0 - t) * theta) / sinTheta;
        double wq = System.Math.Sin(t * theta) / sinTheta;

        return new Quatd(
            (p.X * wp) + (q.X * wq),
            (p.Y * wp) + (q.Y * wq),
            (p.Z * wp) + (q.Z * wq),
            (p.W * wp) + (q.W * wq));
    }

    /// <summary>
    /// Returns <see langword="true"/> when this quaternion represents the same rotation as
    /// <paramref name="other"/> within <paramref name="angularTolerance"/>.
    /// </summary>
    /// <param name="other">The rotation to compare against.</param>
    /// <param name="angularTolerance">The non-negative angular tolerance, in radians.</param>
    /// <remarks>
    /// Compares rotations, not components: q and -q are the same rotation and compare equal here,
    /// while the compiler-generated structural equality reports them as different.
    /// </remarks>
    public bool IsSameRotationAs(Quatd other, double angularTolerance = Tolerance.Angular)
    {
        double dot = System.Math.Abs(Dot(Normalized(), other.Normalized()));
        double angle = 2.0 * System.Math.Acos(Tolerance.Clamp(dot, -1.0, 1.0));
        return angle <= angularTolerance;
    }

    /// <inheritdoc />
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"({X:G17}, {Y:G17}, {Z:G17}, {W:G17})");
}
