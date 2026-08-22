using System.Globalization;

namespace OpenMCAD.Math;

/// <summary>
/// A double-precision 4x4 matrix.
/// </summary>
/// <remarks>
/// <para>
/// <b>Convention.</b> Column-vector, so a point is transformed as <c>v' = M * v</c> and a
/// composition <c>A * B</c> applies <c>B</c> first. Element <c>Mrc</c> is row <c>r</c>, column
/// <c>c</c>, one-based, matching standard linear-algebra notation. Translation therefore lives in
/// the fourth <i>column</i> (<see cref="M14"/>, <see cref="M24"/>, <see cref="M34"/>).
/// </para>
/// <para>
/// Most modelling code should use <see cref="Transform"/> instead, which is a rigid transform
/// plus uniform scale and cannot represent shear. Reach for <see cref="Mat4d"/> only for
/// projections, non-uniform scaling, and interop with graphics APIs.
/// </para>
/// </remarks>
public readonly record struct Mat4d
{
    /// <summary>Initialises a matrix from its sixteen elements in row-major order.</summary>
    /// <param name="m11">Row 1, column 1.</param>
    /// <param name="m12">Row 1, column 2.</param>
    /// <param name="m13">Row 1, column 3.</param>
    /// <param name="m14">Row 1, column 4.</param>
    /// <param name="m21">Row 2, column 1.</param>
    /// <param name="m22">Row 2, column 2.</param>
    /// <param name="m23">Row 2, column 3.</param>
    /// <param name="m24">Row 2, column 4.</param>
    /// <param name="m31">Row 3, column 1.</param>
    /// <param name="m32">Row 3, column 2.</param>
    /// <param name="m33">Row 3, column 3.</param>
    /// <param name="m34">Row 3, column 4.</param>
    /// <param name="m41">Row 4, column 1.</param>
    /// <param name="m42">Row 4, column 2.</param>
    /// <param name="m43">Row 4, column 3.</param>
    /// <param name="m44">Row 4, column 4.</param>
    public Mat4d(
        double m11, double m12, double m13, double m14,
        double m21, double m22, double m23, double m24,
        double m31, double m32, double m33, double m34,
        double m41, double m42, double m43, double m44)
    {
        M11 = m11; M12 = m12; M13 = m13; M14 = m14;
        M21 = m21; M22 = m22; M23 = m23; M24 = m24;
        M31 = m31; M32 = m32; M33 = m33; M34 = m34;
        M41 = m41; M42 = m42; M43 = m43; M44 = m44;
    }

    /// <summary>Gets the element at row 1, column 1.</summary>
    public double M11 { get; init; }

    /// <summary>Gets the element at row 1, column 2.</summary>
    public double M12 { get; init; }

    /// <summary>Gets the element at row 1, column 3.</summary>
    public double M13 { get; init; }

    /// <summary>Gets the element at row 1, column 4. Part of the translation.</summary>
    public double M14 { get; init; }

    /// <summary>Gets the element at row 2, column 1.</summary>
    public double M21 { get; init; }

    /// <summary>Gets the element at row 2, column 2.</summary>
    public double M22 { get; init; }

    /// <summary>Gets the element at row 2, column 3.</summary>
    public double M23 { get; init; }

    /// <summary>Gets the element at row 2, column 4. Part of the translation.</summary>
    public double M24 { get; init; }

    /// <summary>Gets the element at row 3, column 1.</summary>
    public double M31 { get; init; }

    /// <summary>Gets the element at row 3, column 2.</summary>
    public double M32 { get; init; }

    /// <summary>Gets the element at row 3, column 3.</summary>
    public double M33 { get; init; }

    /// <summary>Gets the element at row 3, column 4. Part of the translation.</summary>
    public double M34 { get; init; }

    /// <summary>Gets the element at row 4, column 1.</summary>
    public double M41 { get; init; }

    /// <summary>Gets the element at row 4, column 2.</summary>
    public double M42 { get; init; }

    /// <summary>Gets the element at row 4, column 3.</summary>
    public double M43 { get; init; }

    /// <summary>Gets the element at row 4, column 4.</summary>
    public double M44 { get; init; }

    /// <summary>Gets the identity matrix.</summary>
    public static Mat4d Identity => new(
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1);

    /// <summary>Gets the element at the given one-based row and column.</summary>
    /// <param name="row">The one-based row index, from 1 to 4.</param>
    /// <param name="column">The one-based column index, from 1 to 4.</param>
    /// <exception cref="ArgumentOutOfRangeException">An index is outside the range 1 to 4.</exception>
    public double this[int row, int column] => (row, column) switch
    {
        (1, 1) => M11, (1, 2) => M12, (1, 3) => M13, (1, 4) => M14,
        (2, 1) => M21, (2, 2) => M22, (2, 3) => M23, (2, 4) => M24,
        (3, 1) => M31, (3, 2) => M32, (3, 3) => M33, (3, 4) => M34,
        (4, 1) => M41, (4, 2) => M42, (4, 3) => M43, (4, 4) => M44,
        _ => throw new ArgumentOutOfRangeException(
            row is < 1 or > 4 ? nameof(row) : nameof(column)),
    };

    /// <summary>
    /// Gets a value indicating whether every element is finite (not NaN or infinity).
    /// </summary>
    public bool IsFinite
        => double.IsFinite(M11) && double.IsFinite(M12) && double.IsFinite(M13) && double.IsFinite(M14)
        && double.IsFinite(M21) && double.IsFinite(M22) && double.IsFinite(M23) && double.IsFinite(M24)
        && double.IsFinite(M31) && double.IsFinite(M32) && double.IsFinite(M33) && double.IsFinite(M34)
        && double.IsFinite(M41) && double.IsFinite(M42) && double.IsFinite(M43) && double.IsFinite(M44);

    /// <summary>Gets the translation held in the fourth column.</summary>
    public Vec3d Translation => new(M14, M24, M34);

    /// <summary>
    /// Composes two matrices. The result applies <paramref name="b"/> first, then
    /// <paramref name="a"/>.
    /// </summary>
    /// <param name="a">The transform applied second.</param>
    /// <param name="b">The transform applied first.</param>
    public static Mat4d operator *(Mat4d a, Mat4d b) => new(
        (a.M11 * b.M11) + (a.M12 * b.M21) + (a.M13 * b.M31) + (a.M14 * b.M41),
        (a.M11 * b.M12) + (a.M12 * b.M22) + (a.M13 * b.M32) + (a.M14 * b.M42),
        (a.M11 * b.M13) + (a.M12 * b.M23) + (a.M13 * b.M33) + (a.M14 * b.M43),
        (a.M11 * b.M14) + (a.M12 * b.M24) + (a.M13 * b.M34) + (a.M14 * b.M44),

        (a.M21 * b.M11) + (a.M22 * b.M21) + (a.M23 * b.M31) + (a.M24 * b.M41),
        (a.M21 * b.M12) + (a.M22 * b.M22) + (a.M23 * b.M32) + (a.M24 * b.M42),
        (a.M21 * b.M13) + (a.M22 * b.M23) + (a.M23 * b.M33) + (a.M24 * b.M43),
        (a.M21 * b.M14) + (a.M22 * b.M24) + (a.M23 * b.M34) + (a.M24 * b.M44),

        (a.M31 * b.M11) + (a.M32 * b.M21) + (a.M33 * b.M31) + (a.M34 * b.M41),
        (a.M31 * b.M12) + (a.M32 * b.M22) + (a.M33 * b.M32) + (a.M34 * b.M42),
        (a.M31 * b.M13) + (a.M32 * b.M23) + (a.M33 * b.M33) + (a.M34 * b.M43),
        (a.M31 * b.M14) + (a.M32 * b.M24) + (a.M33 * b.M34) + (a.M34 * b.M44),

        (a.M41 * b.M11) + (a.M42 * b.M21) + (a.M43 * b.M31) + (a.M44 * b.M41),
        (a.M41 * b.M12) + (a.M42 * b.M22) + (a.M43 * b.M32) + (a.M44 * b.M42),
        (a.M41 * b.M13) + (a.M42 * b.M23) + (a.M43 * b.M33) + (a.M44 * b.M43),
        (a.M41 * b.M14) + (a.M42 * b.M24) + (a.M43 * b.M34) + (a.M44 * b.M44));

    /// <summary>Named alternative to the multiplication operator.</summary>
    /// <param name="a">The transform applied second.</param>
    /// <param name="b">The transform applied first.</param>
    public static Mat4d Multiply(Mat4d a, Mat4d b) => a * b;

    /// <summary>Creates a pure translation matrix.</summary>
    /// <param name="translation">The translation vector.</param>
    public static Mat4d FromTranslation(Vec3d translation) => new(
        1, 0, 0, translation.X,
        0, 1, 0, translation.Y,
        0, 0, 1, translation.Z,
        0, 0, 0, 1);

    /// <summary>Creates a non-uniform scaling matrix about the origin.</summary>
    /// <param name="scale">The per-axis scale factors.</param>
    public static Mat4d FromScale(Vec3d scale) => new(
        scale.X, 0, 0, 0,
        0, scale.Y, 0, 0,
        0, 0, scale.Z, 0,
        0, 0, 0, 1);

    /// <summary>Creates a uniform scaling matrix about the origin.</summary>
    /// <param name="scale">The scale factor.</param>
    public static Mat4d FromScale(double scale) => FromScale(new Vec3d(scale, scale, scale));

    /// <summary>Creates a rotation matrix from a unit quaternion.</summary>
    /// <param name="rotation">The rotation. Normalised internally.</param>
    /// <exception cref="InvalidOperationException"><paramref name="rotation"/> is degenerate.</exception>
    public static Mat4d FromRotation(Quatd rotation)
    {
        Quatd q = rotation.Normalized();
        double xx = q.X * q.X, yy = q.Y * q.Y, zz = q.Z * q.Z;
        double xy = q.X * q.Y, xz = q.X * q.Z, yz = q.Y * q.Z;
        double wx = q.W * q.X, wy = q.W * q.Y, wz = q.W * q.Z;

        return new Mat4d(
            1.0 - (2.0 * (yy + zz)), 2.0 * (xy - wz), 2.0 * (xz + wy), 0,
            2.0 * (xy + wz), 1.0 - (2.0 * (xx + zz)), 2.0 * (yz - wx), 0,
            2.0 * (xz - wy), 2.0 * (yz + wx), 1.0 - (2.0 * (xx + yy)), 0,
            0, 0, 0, 1);
    }

    /// <summary>Returns the transpose of this matrix.</summary>
    public Mat4d Transposed() => new(
        M11, M21, M31, M41,
        M12, M22, M32, M42,
        M13, M23, M33, M43,
        M14, M24, M34, M44);

    /// <summary>Returns the determinant of this matrix.</summary>
    public double Determinant()
    {
        double s0 = (M11 * M22) - (M21 * M12);
        double s1 = (M11 * M23) - (M21 * M13);
        double s2 = (M11 * M24) - (M21 * M14);
        double s3 = (M12 * M23) - (M22 * M13);
        double s4 = (M12 * M24) - (M22 * M14);
        double s5 = (M13 * M24) - (M23 * M14);

        double c5 = (M33 * M44) - (M43 * M34);
        double c4 = (M32 * M44) - (M42 * M34);
        double c3 = (M32 * M43) - (M42 * M33);
        double c2 = (M31 * M44) - (M41 * M34);
        double c1 = (M31 * M43) - (M41 * M33);
        double c0 = (M31 * M42) - (M41 * M32);

        return (s0 * c5) - (s1 * c4) + (s2 * c3) + (s3 * c2) - (s4 * c1) + (s5 * c0);
    }

    /// <summary>Attempts to invert this matrix.</summary>
    /// <param name="result">The inverse, or <see cref="Identity"/> if the matrix is singular.</param>
    /// <returns><see langword="true"/> if the matrix was invertible.</returns>
    public bool TryInvert(out Mat4d result)
    {
        double s0 = (M11 * M22) - (M21 * M12);
        double s1 = (M11 * M23) - (M21 * M13);
        double s2 = (M11 * M24) - (M21 * M14);
        double s3 = (M12 * M23) - (M22 * M13);
        double s4 = (M12 * M24) - (M22 * M14);
        double s5 = (M13 * M24) - (M23 * M14);

        double c5 = (M33 * M44) - (M43 * M34);
        double c4 = (M32 * M44) - (M42 * M34);
        double c3 = (M32 * M43) - (M42 * M33);
        double c2 = (M31 * M44) - (M41 * M34);
        double c1 = (M31 * M43) - (M41 * M33);
        double c0 = (M31 * M42) - (M41 * M32);

        double det = (s0 * c5) - (s1 * c4) + (s2 * c3) + (s3 * c2) - (s4 * c1) + (s5 * c0);

        if (!double.IsFinite(det) || System.Math.Abs(det) <= double.Epsilon)
        {
            result = Identity;
            return false;
        }

        double d = 1.0 / det;

        result = new Mat4d(
            ((M22 * c5) - (M23 * c4) + (M24 * c3)) * d,
            ((-M12 * c5) + (M13 * c4) - (M14 * c3)) * d,
            ((M42 * s5) - (M43 * s4) + (M44 * s3)) * d,
            ((-M32 * s5) + (M33 * s4) - (M34 * s3)) * d,

            ((-M21 * c5) + (M23 * c2) - (M24 * c1)) * d,
            ((M11 * c5) - (M13 * c2) + (M14 * c1)) * d,
            ((-M41 * s5) + (M43 * s2) - (M44 * s1)) * d,
            ((M31 * s5) - (M33 * s2) + (M34 * s1)) * d,

            ((M21 * c4) - (M22 * c2) + (M24 * c0)) * d,
            ((-M11 * c4) + (M12 * c2) - (M14 * c0)) * d,
            ((M41 * s4) - (M42 * s2) + (M44 * s0)) * d,
            ((-M31 * s4) + (M32 * s2) - (M34 * s0)) * d,

            ((-M21 * c3) + (M22 * c1) - (M23 * c0)) * d,
            ((M11 * c3) - (M12 * c1) + (M13 * c0)) * d,
            ((-M41 * s3) + (M42 * s1) - (M43 * s0)) * d,
            ((M31 * s3) - (M32 * s1) + (M33 * s0)) * d);

        return result.IsFinite;
    }

    /// <summary>Returns the inverse of this matrix.</summary>
    /// <exception cref="InvalidOperationException">The matrix is singular.</exception>
    public Mat4d Inverted()
        => TryInvert(out Mat4d result)
            ? result
            : throw new InvalidOperationException("Cannot invert a singular Mat4d.");

    /// <summary>
    /// Transforms <paramref name="point"/> as a position, applying translation and performing the
    /// perspective divide when the matrix is not affine.
    /// </summary>
    /// <param name="point">The point to transform.</param>
    public Vec3d TransformPoint(Vec3d point)
    {
        double x = (M11 * point.X) + (M12 * point.Y) + (M13 * point.Z) + M14;
        double y = (M21 * point.X) + (M22 * point.Y) + (M23 * point.Z) + M24;
        double z = (M31 * point.X) + (M32 * point.Y) + (M33 * point.Z) + M34;
        double w = (M41 * point.X) + (M42 * point.Y) + (M43 * point.Z) + M44;

        if (w != 1.0 && w != 0.0)
        {
            double inverse = 1.0 / w;
            return new Vec3d(x * inverse, y * inverse, z * inverse);
        }

        return new Vec3d(x, y, z);
    }

    /// <summary>
    /// Transforms <paramref name="direction"/> as a direction, ignoring translation.
    /// </summary>
    /// <param name="direction">The direction to transform.</param>
    /// <remarks>
    /// This is not the correct transform for surface normals under non-uniform scale; use
    /// <see cref="TransformNormal"/> for those.
    /// </remarks>
    public Vec3d TransformDirection(Vec3d direction) => new(
        (M11 * direction.X) + (M12 * direction.Y) + (M13 * direction.Z),
        (M21 * direction.X) + (M22 * direction.Y) + (M23 * direction.Z),
        (M31 * direction.X) + (M32 * direction.Y) + (M33 * direction.Z));

    /// <summary>
    /// Transforms <paramref name="normal"/> using the inverse transpose, which is the correct
    /// transform for surface normals under non-uniform scale.
    /// </summary>
    /// <param name="normal">The normal to transform. Not renormalised.</param>
    /// <exception cref="InvalidOperationException">The matrix is singular.</exception>
    public Vec3d TransformNormal(Vec3d normal)
        => Inverted().Transposed().TransformDirection(normal);

    /// <summary>
    /// Returns <see langword="true"/> when every element is within <paramref name="tolerance"/>
    /// of the corresponding element of <paramref name="other"/>.
    /// </summary>
    /// <param name="other">The matrix to compare against.</param>
    /// <param name="tolerance">The non-negative per-element tolerance.</param>
    public bool IsNear(Mat4d other, double tolerance = Tolerance.Linear)
    {
        for (int row = 1; row <= 4; row++)
        {
            for (int column = 1; column <= 4; column++)
            {
                if (!Tolerance.AreEqual(this[row, column], other[row, column], tolerance))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"[[{M11:G17}, {M12:G17}, {M13:G17}, {M14:G17}], " +
        $"[{M21:G17}, {M22:G17}, {M23:G17}, {M24:G17}], " +
        $"[{M31:G17}, {M32:G17}, {M33:G17}, {M34:G17}], " +
        $"[{M41:G17}, {M42:G17}, {M43:G17}, {M44:G17}]]");
}
