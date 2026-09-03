using OpenMCAD.Math;

namespace OpenMCAD.Solver.Sketching;

/// <summary>
/// A 2-D similarity transform, with an optional reflection: scale, then an optional flip, then
/// rotation, then translation.
/// </summary>
/// <param name="Translation">Applied last.</param>
/// <param name="Rotation">In radians, applied after scaling and reflecting.</param>
/// <param name="Scale">The uniform scale factor, applied first. Must be positive.</param>
/// <param name="Reflected">
/// Whether a mirror flip is applied between scaling and rotating. Not a negative
/// <see cref="Scale"/>: keeping scale always positive and reflection a separate flag is what makes
/// <see cref="ScaleAbout"/> reject a nonsensical factor rather than accepting one that quietly also
/// mirrors the geometry.
/// </param>
/// <remarks>
/// <para>
/// P4-T13. The sketch-plane counterpart of <see cref="OpenMCAD.Math.Transform"/>, which is 3-D and
/// has no reflection — component placement and occurrence transforms never mirror, but a sketch's
/// own mirror tool has to. <see cref="Apply(Vec2d)"/> is <c>Rotate(Reflect(p * Scale)) + Translation</c>,
/// the same "scale, then rotate, then translate" convention <c>Transform</c> uses, with reflection
/// slotted in before the rotation.
/// </para>
/// <para>
/// <b>Composition is deliberately not offered.</b> Every caller in this build computes one of these
/// directly from what the user actually asked for — move by this vector, rotate about that point by
/// this angle, mirror about that line — and none of them needs to chain two together. Adding
/// <c>operator *</c> before something needs it would be the general mechanism P4-T02's <c>FakeSolver</c>
/// warns against building ahead of a real requirement.
/// </para>
/// </remarks>
public readonly record struct SketchTransform(
    Vec2d Translation, double Rotation = 0, double Scale = 1, bool Reflected = false)
{
    /// <summary>Gets the transform that changes nothing.</summary>
    public static SketchTransform Identity => new(Vec2d.Zero);

    /// <summary>Gets a transform that moves everything by a fixed offset.</summary>
    /// <param name="delta">The offset.</param>
    /// <returns>The transform.</returns>
    public static SketchTransform Translate(Vec2d delta) => new(delta);

    /// <summary>Gets a transform that rotates about a point.</summary>
    /// <param name="centre">The point to rotate about.</param>
    /// <param name="radians">How far, anticlockwise.</param>
    /// <returns>The transform.</returns>
    public static SketchTransform RotateAbout(Vec2d centre, double radians)
        => new(centre - centre.Rotated(radians), radians);

    /// <summary>Gets a transform that scales uniformly about a point.</summary>
    /// <param name="centre">The point to scale about — the one point that does not move.</param>
    /// <param name="factor">The scale factor. Must be positive.</param>
    /// <returns>The transform.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor"/> is not positive.</exception>
    public static SketchTransform ScaleAbout(Vec2d centre, double factor)
    {
        if (!double.IsFinite(factor) || factor <= Tolerance.LinearResolution)
        {
            throw new ArgumentOutOfRangeException(
                nameof(factor), factor, "A sketch scale factor must be a positive, finite number.");
        }

        return new SketchTransform(centre * (1 - factor), Scale: factor);
    }

    /// <summary>Gets a transform that reflects about the infinite line through two points.</summary>
    /// <param name="lineStart">A point on the mirror line.</param>
    /// <param name="lineEnd">Another point on it.</param>
    /// <returns>The transform.</returns>
    /// <exception cref="InvalidOperationException">The two points coincide.</exception>
    /// <remarks>
    /// Reflection about a line through the origin at angle θ is <c>Rotate(2θ) ∘ Flip</c> — a
    /// standard identity, and the reason <see cref="Rotation"/> below is doubled rather than left as
    /// θ. For a line not through the origin, this is that reflection conjugated by a translation to
    /// and from <paramref name="lineStart"/>, folded into one <see cref="Translation"/> so
    /// <see cref="Apply(Vec2d)"/> stays a single scale-flip-rotate-translate rather than needing a
    /// third translation step only mirroring uses.
    /// </remarks>
    public static SketchTransform MirrorAbout(Vec2d lineStart, Vec2d lineEnd)
    {
        double doubled = 2 * (lineEnd - lineStart).Normalized().Angle();
        Vec2d flippedStart = new Vec2d(lineStart.X, -lineStart.Y).Rotated(doubled);

        return new SketchTransform(lineStart - flippedStart, doubled, Reflected: true);
    }

    /// <summary>Maps a point through this transform.</summary>
    /// <param name="point">The point.</param>
    /// <returns>Where it ends up.</returns>
    public Vec2d Apply(Vec2d point)
    {
        Vec2d scaled = point * Scale;
        Vec2d flipped = Reflected ? new Vec2d(scaled.X, -scaled.Y) : scaled;

        return flipped.Rotated(Rotation) + Translation;
    }

    /// <summary>
    /// Maps an angle measured anticlockwise from the local +X axis — an arc's polar angle, an
    /// ellipse's major-axis rotation — through this transform's rotation and reflection.
    /// </summary>
    /// <param name="angle">The angle, in radians.</param>
    /// <returns>The transformed angle.</returns>
    /// <remarks>
    /// Not simply <c>angle + Rotation</c> when <see cref="Reflected"/>: flipping reverses which way
    /// increasing angle turns, the same fact P4-T11's circular-edge projection derives in detail for
    /// exactly this reason. Unreflected, <c>Apply(cos θ, sin θ)</c> works out to
    /// <c>(cos(θ + Rotation), sin(θ + Rotation))</c>; reflected, to
    /// <c>(cos(Rotation − θ), sin(Rotation − θ))</c> — this returns the angle inside those, so a
    /// caller reconstructing a transformed arc or ellipse from it gets the same point
    /// <see cref="Apply(Vec2d)"/> would have produced directly.
    /// </remarks>
    public double ApplyAngle(double angle) => Reflected ? Rotation - angle : Rotation + angle;
}
