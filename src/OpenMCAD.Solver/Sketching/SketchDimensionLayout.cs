using System.Collections.Immutable;

using OpenMCAD.Math;

namespace OpenMCAD.Solver.Sketching;

/// <summary>How laying out a <see cref="SketchDimension"/> came to.</summary>
public enum DimensionLayoutOutcome
{
    /// <summary>The dimension has geometry to draw.</summary>
    Resolved,

    /// <summary>The dimension's constraint no longer exists.</summary>
    ConstraintNotFound,

    /// <summary>The constraint's geometry no longer exists.</summary>
    GeometryNotFound,

    /// <summary>
    /// This build does not yet lay out this kind of constraint, or this operand shape of it.
    /// </summary>
    Unsupported,

    /// <summary>The two points measured are coincident, so no direction can be laid out from them.</summary>
    Degenerate,
}

/// <summary>
/// The geometry a dimension resolves to for one moment of the sketch: where its witness lines,
/// dimension line and text sit, and what number it currently reads.
/// </summary>
/// <param name="Outcome">How layout went.</param>
/// <param name="DimensionLine">The two ends of the dimension line itself, when resolved.</param>
/// <param name="WitnessLines">
/// One line per measured point, running from the point to the dimension line, when resolved.
/// </param>
/// <param name="TextPosition">Where the text sits, when resolved.</param>
/// <param name="Value">What the dimension currently reads, when resolved.</param>
/// <param name="Reason">Why, in words, when layout could not be resolved.</param>
public sealed record DimensionLayout(
    DimensionLayoutOutcome Outcome,
    (Vec2d Start, Vec2d End)? DimensionLine = null,
    ImmutableArray<(Vec2d From, Vec2d To)> WitnessLines = default,
    Vec2d? TextPosition = null,
    double? Value = null,
    string? Reason = null)
{
    /// <summary>Gets whether this dimension has geometry to draw.</summary>
    public bool IsResolved => Outcome == DimensionLayoutOutcome.Resolved;

    /// <summary>Gets the witness lines, never a default array.</summary>
    public ImmutableArray<(Vec2d From, Vec2d To)> Witnesses
        => WitnessLines.IsDefault ? [] : WitnessLines;

    /// <summary>Creates a resolution that failed.</summary>
    /// <param name="outcome">How it failed. Must not be <see cref="DimensionLayoutOutcome.Resolved"/>.</param>
    /// <param name="reason">Why, in words.</param>
    /// <returns>The layout.</returns>
    public static DimensionLayout Failed(DimensionLayoutOutcome outcome, string reason)
        => new(outcome, Reason: reason);
}

/// <summary>
/// Lays out a <see cref="SketchDimension"/>: witness lines, a dimension line and where its text
/// sits, from the current geometry and the dimension's own placement (P4-T12).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scoped to the point-to-point dimension kinds.</b> <see cref="ConstraintKind.Distance"/>
/// (aligned), <see cref="ConstraintKind.HorizontalDistance"/> and
/// <see cref="ConstraintKind.VerticalDistance"/> (linear — §5.6's "linear" and "aligned" dimension
/// types) are laid out here; <see cref="ConstraintKind.Distance"/>'s point-to-line operand shape,
/// <see cref="ConstraintKind.Angle"/>, <see cref="ConstraintKind.Radius"/> and
/// <see cref="ConstraintKind.Diameter"/> resolve to <see cref="DimensionLayoutOutcome.Unsupported"/>
/// for now. Their geometry is a real piece of work each — an angular dimension needs an arc radius
/// picked from the witness point and two possibly-extended lines to find where the arc meets them; a
/// radial or diametric one needs a leader direction and a decision about whether the leader lands
/// inside or outside the circle — and none of it is needed to give the three point-to-point kinds
/// (which is what a plain length between two points, in any of its three senses, actually is) a
/// complete and correct implementation now rather than three-sevenths of a general one.
/// </para>
/// <para>
/// <b>Ordinate dimensioning is not a fourth layout.</b> §5.6 lists it separately, but the number an
/// ordinate dimension shows against a shared baseline is exactly what
/// <see cref="ConstraintKind.HorizontalDistance"/> or <see cref="ConstraintKind.VerticalDistance"/>
/// already measures from that baseline point — only the presentation differs: one shared baseline
/// extension line and several stacked dimension lines rather than each dimension drawing its own
/// pair of witness lines. That stacking is a layout problem across several dimensions at once (where
/// to offset each one so their text does not collide), which this type's per-dimension signature has
/// nowhere to put; a single <see cref="SketchDimension"/> laid out on its own reads correctly as an
/// ordinate dimension already; only the coordinated stacking is left for when several exist together.
/// </para>
/// </remarks>
public static class SketchDimensionLayout
{
    /// <summary>Lays out a dimension.</summary>
    /// <param name="dimension">The dimension.</param>
    /// <param name="sketch">The sketch it belongs to.</param>
    /// <returns>The layout.</returns>
    public static DimensionLayout Resolve(SketchDimension dimension, Sketch sketch)
    {
        ArgumentNullException.ThrowIfNull(dimension);
        ArgumentNullException.ThrowIfNull(sketch);

        SketchConstraint? constraint = sketch.Constraints.Find(dimension.Constraint);

        if (constraint is null)
        {
            return DimensionLayout.Failed(
                DimensionLayoutOutcome.ConstraintNotFound,
                "The constraint this dimension displays no longer exists.");
        }

        // Dispatched on the constraint's kind before anything about its operands is inspected.
        // Angle, Radius and Diameter name whole entities via EntityPoint.Self (P4-T04), which does
        // not resolve through SketchEntitySet.Locate at all for a line or a circle -- only a point
        // entity has a position at its own Self. Checking "do both operands locate as points"
        // first, before knowing whether this kind even names points, would report those as their
        // geometry having vanished rather than as the unsupported kinds they actually are.
        return constraint.Kind switch
        {
            ConstraintKind.Distance
                => PointPair(constraint, sketch, dimension.TextPosition, Aligned),
            ConstraintKind.HorizontalDistance
                => PointPair(constraint, sketch, dimension.TextPosition, (a, b, t) => Linear(a, b, t, vertical: false)),
            ConstraintKind.VerticalDistance
                => PointPair(constraint, sketch, dimension.TextPosition, (a, b, t) => Linear(a, b, t, vertical: true)),
            _ => DimensionLayout.Failed(
                DimensionLayoutOutcome.Unsupported,
                $"This build does not yet lay out a {constraint.Schema.Label} dimension."),
        };
    }

    /// <summary>
    /// Resolves a two-point constraint's operands to positions and hands them to a layout function,
    /// telling apart geometry that no longer exists from an operand shape this build does not lay
    /// out -- <see cref="ConstraintKind.Distance"/> also accepts a point and a line, which
    /// <see cref="SketchEntitySet.Locate"/> cannot resolve to a position (a line has no <c>Self</c>
    /// point) even though the line itself is present and well-formed.
    /// </summary>
    private static DimensionLayout PointPair(
        SketchConstraint constraint,
        Sketch sketch,
        Vec2d text,
        Func<Vec2d, Vec2d, Vec2d, DimensionLayout> layout)
    {
        if (constraint.On.Length != 2)
        {
            return DimensionLayout.Failed(
                DimensionLayoutOutcome.Unsupported,
                "This build only lays out a dimension between two points.");
        }

        SketchPointRef first = constraint.On[0];
        SketchPointRef second = constraint.On[1];

        if (sketch.Entities.Find(first.Entity) is null || sketch.Entities.Find(second.Entity) is null)
        {
            return DimensionLayout.Failed(
                DimensionLayoutOutcome.GeometryNotFound,
                "The geometry this dimension measures no longer exists.");
        }

        return sketch.Entities.Locate(first) is not { } a || sketch.Entities.Locate(second) is not { } b
            ? DimensionLayout.Failed(
                DimensionLayoutOutcome.Unsupported,
                "This build only lays out a dimension between two points.")
            : layout(a, b, text);
    }

    private static DimensionLayout Aligned(Vec2d a, Vec2d b, Vec2d text)
    {
        Vec2d delta = b - a;
        double length = delta.Length;

        if (length <= Tolerance.LinearResolution)
        {
            return DimensionLayout.Failed(
                DimensionLayoutOutcome.Degenerate,
                "The two points this dimension measures are in the same place, so there is no "
                + "direction to lay the dimension line along.");
        }

        Vec2d perpendicular = (delta / length).Perpendicular();
        double offset = Vec2d.Dot(text - a, perpendicular);
        Vec2d onLine = a + (perpendicular * offset);
        Vec2d otherOnLine = b + (perpendicular * offset);

        return new DimensionLayout(
            DimensionLayoutOutcome.Resolved,
            (onLine, otherOnLine),
            [(a, onLine), (b, otherOnLine)],
            text,
            length);
    }

    private static DimensionLayout Linear(Vec2d a, Vec2d b, Vec2d text, bool vertical)
    {
        // The dimension line runs along the axis being measured, at the offset the witness point
        // set on the other axis -- horizontal for a vertical dimension, vertical for a horizontal
        // one -- regardless of where the two points actually are relative to one another. That is
        // the entire difference from Aligned: no direction is derived from the geometry at all, so
        // there is nothing here that can be degenerate.
        Vec2d onLine = vertical ? new Vec2d(text.X, a.Y) : new Vec2d(a.X, text.Y);
        Vec2d otherOnLine = vertical ? new Vec2d(text.X, b.Y) : new Vec2d(b.X, text.Y);

        double value = System.Math.Abs(vertical ? b.Y - a.Y : b.X - a.X);

        return new DimensionLayout(
            DimensionLayoutOutcome.Resolved,
            (onLine, otherOnLine),
            [(a, onLine), (b, otherOnLine)],
            text,
            value);
    }
}
