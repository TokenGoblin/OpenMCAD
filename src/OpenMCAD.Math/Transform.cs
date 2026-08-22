using System.Globalization;

namespace OpenMCAD.Math;

/// <summary>
/// A similarity transform: uniform scale, then rotation, then translation.
/// </summary>
/// <param name="Rotation">The rotation, applied after scaling.</param>
/// <param name="Translation">The translation, applied last.</param>
/// <param name="Scale">The uniform scale factor, applied first. Must be positive.</param>
/// <remarks>
/// <para>
/// This, not <see cref="Mat4d"/>, is the transform type for component placement, occurrence
/// transforms, sketch planes, and coordinate systems. The restriction to uniform scale is
/// deliberate: it makes the transform exactly invertible, keeps normals valid under
/// <see cref="TransformDirection"/> without an inverse transpose, keeps composition free of
/// accumulated shear, and interpolates sensibly for animation and drag. Non-uniform scale and
/// projection need <see cref="Mat4d"/>.
/// </para>
/// <para>
/// A point is transformed as <c>Rotation.Rotate(p * Scale) + Translation</c>. Composition follows
/// the same convention as matrices: in <c>a * b</c>, <c>b</c> is applied first.
/// </para>
/// </remarks>
public readonly record struct Transform(Quatd Rotation, Vec3d Translation, double Scale)
{
    /// <summary>Gets the identity transform.</summary>
    public static Transform Identity => new(Quatd.Identity, Vec3d.Zero, 1.0);

    /// <summary>
    /// Gets a value indicating whether the transform is finite, non-degenerate, and therefore
    /// safe to apply and invert.
    /// </summary>
    public bool IsValid
        => Rotation.IsFinite
        && Translation.IsFinite
        && double.IsFinite(Scale)
        && Scale > Tolerance.LinearResolution;

    /// <summary>
    /// Gets a value indicating whether this transform is rigid, that is, whether it preserves
    /// distances.
    /// </summary>
    public bool IsRigid => IsValid && Tolerance.AreEqual(Scale, 1.0, Tolerance.Angular);

    /// <summary>Creates a pure translation.</summary>
    /// <param name="translation">The translation vector.</param>
    public static Transform FromTranslation(Vec3d translation)
        => new(Quatd.Identity, translation, 1.0);

    /// <summary>Creates a pure rotation about the world origin.</summary>
    /// <param name="rotation">The rotation.</param>
    public static Transform FromRotation(Quatd rotation) => new(rotation, Vec3d.Zero, 1.0);

    /// <summary>Creates a rotation about an arbitrary axis line.</summary>
    /// <param name="axisPoint">A point on the axis.</param>
    /// <param name="axisDirection">The axis direction. Need not be unit length.</param>
    /// <param name="radians">The rotation angle in radians, following the right-hand rule.</param>
    /// <exception cref="InvalidOperationException"><paramref name="axisDirection"/> is degenerate.</exception>
    public static Transform FromAxisRotation(Vec3d axisPoint, Vec3d axisDirection, double radians)
    {
        Quatd rotation = Quatd.FromAxisAngle(axisDirection, radians);
        return new Transform(rotation, axisPoint - rotation.Rotate(axisPoint), 1.0);
    }

    /// <summary>Creates a uniform scaling about the world origin.</summary>
    /// <param name="scale">The positive scale factor.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scale"/> is not positive.</exception>
    public static Transform FromScale(double scale)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);
        return new Transform(Quatd.Identity, Vec3d.Zero, scale);
    }

    /// <summary>
    /// Creates the transform mapping the world axes onto the given orthonormal frame placed at
    /// <paramref name="origin"/>.
    /// </summary>
    /// <param name="origin">The frame origin in world coordinates.</param>
    /// <param name="xAxis">The image of +X. Must be unit length.</param>
    /// <param name="yAxis">The image of +Y. Must be unit length and perpendicular to the others.</param>
    /// <param name="zAxis">The image of +Z. Must be unit length and perpendicular to the others.</param>
    /// <remarks>
    /// This is the local-to-world transform of a coordinate system. Invert it to map world
    /// coordinates into the frame.
    /// </remarks>
    public static Transform FromFrame(Vec3d origin, Vec3d xAxis, Vec3d yAxis, Vec3d zAxis)
        => new(Quatd.FromBasis(xAxis, yAxis, zAxis), origin, 1.0);

    /// <summary>
    /// Composes two transforms. The result applies <paramref name="b"/> first, then
    /// <paramref name="a"/>.
    /// </summary>
    /// <param name="a">The transform applied second.</param>
    /// <param name="b">The transform applied first.</param>
    public static Transform operator *(Transform a, Transform b) => new(
        a.Rotation * b.Rotation,
        a.TransformPoint(b.Translation),
        a.Scale * b.Scale);

    /// <summary>Named alternative to the multiplication operator.</summary>
    /// <param name="a">The transform applied second.</param>
    /// <param name="b">The transform applied first.</param>
    public static Transform Multiply(Transform a, Transform b) => a * b;

    /// <summary>Transforms <paramref name="point"/> as a position.</summary>
    /// <param name="point">The point to transform.</param>
    public Vec3d TransformPoint(Vec3d point) => Rotation.Rotate(point * Scale) + Translation;

    /// <summary>
    /// Transforms <paramref name="direction"/> as a direction, applying rotation and scale but
    /// not translation.
    /// </summary>
    /// <param name="direction">The direction to transform.</param>
    public Vec3d TransformDirection(Vec3d direction) => Rotation.Rotate(direction * Scale);

    /// <summary>
    /// Rotates <paramref name="normal"/> without scaling it, which keeps a unit normal unit.
    /// </summary>
    /// <param name="normal">The normal to transform.</param>
    /// <remarks>
    /// Because the scale is uniform and positive, rotating alone is the correct normal transform;
    /// no inverse transpose is needed. This is one of the concrete payoffs of forbidding
    /// non-uniform scale in this type.
    /// </remarks>
    public Vec3d TransformNormal(Vec3d normal) => Rotation.Rotate(normal);

    /// <summary>Transforms a plane.</summary>
    /// <param name="plane">The plane to transform.</param>
    public Plane TransformPlane(Plane plane)
        => Plane.FromPointNormal(TransformPoint(plane.Origin), TransformNormal(plane.Normal));

    /// <summary>Returns the inverse transform.</summary>
    /// <exception cref="InvalidOperationException">The transform is degenerate.</exception>
    public Transform Inverted()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"Cannot invert a degenerate Transform (scale {Scale:G17})."));
        }

        Quatd inverseRotation = Rotation.Conjugate();
        double inverseScale = 1.0 / Scale;
        return new Transform(
            inverseRotation,
            inverseRotation.Rotate(-Translation) * inverseScale,
            inverseScale);
    }

    /// <summary>Converts this transform to an equivalent 4x4 matrix.</summary>
    /// <exception cref="InvalidOperationException">The transform is degenerate.</exception>
    public Mat4d ToMat4d()
    {
        Mat4d rotation = Mat4d.FromRotation(Rotation);
        return new Mat4d(
            rotation.M11 * Scale, rotation.M12 * Scale, rotation.M13 * Scale, Translation.X,
            rotation.M21 * Scale, rotation.M22 * Scale, rotation.M23 * Scale, Translation.Y,
            rotation.M31 * Scale, rotation.M32 * Scale, rotation.M33 * Scale, Translation.Z,
            0, 0, 0, 1);
    }

    /// <summary>
    /// Returns <see langword="true"/> when this transform maps every point to within
    /// <paramref name="linearTolerance"/> of where <paramref name="other"/> maps it, over a
    /// region of radius <paramref name="characteristicRadius"/> about the origin.
    /// </summary>
    /// <param name="other">The transform to compare against.</param>
    /// <param name="characteristicRadius">
    /// The radius of the region of interest, in metres. Rotation differences produce positional
    /// differences proportional to distance from the origin, so comparing transforms without a
    /// length scale is meaningless.
    /// </param>
    /// <param name="linearTolerance">The non-negative positional tolerance, in metres.</param>
    public bool IsNear(
        Transform other,
        double characteristicRadius = 1.0,
        double linearTolerance = Tolerance.Linear)
    {
        if (!Translation.IsNear(other.Translation, linearTolerance))
        {
            return false;
        }

        if (!Tolerance.AreEqual(Scale, other.Scale, linearTolerance))
        {
            return false;
        }

        // Convert the rotation difference into the arc length it induces at the given radius.
        double dot = System.Math.Abs(Quatd.Dot(Rotation.Normalized(), other.Rotation.Normalized()));
        double angle = 2.0 * System.Math.Acos(Tolerance.Clamp(dot, -1.0, 1.0));
        return angle * System.Math.Abs(characteristicRadius) <= linearTolerance;
    }

    /// <inheritdoc />
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"Transform(r={Rotation}, t={Translation}, s={Scale:G17})");
}
