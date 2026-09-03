using OpenMCAD.Math;

namespace OpenMCAD.Solver.Sketching;

/// <summary>
/// Identifies one dimension annotation.
/// </summary>
/// <param name="Value">The underlying value.</param>
/// <remarks>Same shape as <see cref="SketchConstraintId"/>, and for the same reasons.</remarks>
public readonly record struct SketchDimensionId(Guid Value)
{
    /// <summary>Gets the id that denotes no dimension.</summary>
    public static SketchDimensionId None => default;

    /// <summary>Gets a value indicating whether this denotes a dimension.</summary>
    public bool IsValid => Value != Guid.Empty;

    /// <summary>Creates a new, unique id.</summary>
    /// <returns>The id.</returns>
    public static SketchDimensionId New() => new(Guid.NewGuid());

    /// <inheritdoc/>
    public override string ToString() => IsValid
        ? Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture)
        : "(none)";
}

/// <summary>
/// Where a dimension is drawn: a witness point that decides the dimension line's offset from the
/// geometry it measures.
/// </summary>
/// <param name="Id">Which dimension this is.</param>
/// <param name="Constraint">Which constraint this displays and, if driving, edits.</param>
/// <param name="TextPosition">
/// Where the user has put the dimension, in the sketch's own coordinates. Decides the dimension
/// line's offset from the geometry (see <see cref="SketchDimensionLayout"/>) and is, for now, also
/// where the text itself sits.
/// </param>
/// <remarks>
/// <para>
/// P4-T12. The durable half, the same split as <c>SketchPlaneReference</c>/<c>SketchPlane</c> in
/// <c>OpenMCAD.Modeling</c>: a placement choice the user made, kept apart from
/// <see cref="SketchDimensionLayout"/>'s answer to "so where does the line actually go right now",
/// which changes every time the geometry does and has no business being stored.
/// </para>
/// <para>
/// <b>This is not the number.</b> The value a dimension shows is <see cref="SketchConstraint.Value"/>
/// for a driving dimension and the constraint's own live measurement for a reference one — either
/// way, <see cref="SketchDimensionLayout"/> reads it fresh from the geometry every time rather than
/// trusting <see cref="SketchConstraint.Value"/> to already agree with it, which is exactly true for
/// a driving dimension only once the solver has actually run since the value last changed.
/// </para>
/// <para>
/// <b>"Editing" a driving dimension is not a new operation.</b> §5.6 asks for placement, display and
/// editing; the first two are what this type and <see cref="SketchDimensionLayout"/> are for, and
/// editing is simply replacing <see cref="SketchConstraint.Value"/> on the constraint this points
/// at — <see cref="ConstraintSet.With"/> (P4-T04) already does that, and a value edit is a rebuild
/// like any other, not a separate mechanism.
/// </para>
/// </remarks>
public sealed record SketchDimension(
    SketchDimensionId Id, SketchConstraintId Constraint, Vec2d TextPosition);
