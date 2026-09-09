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
/// <param name="Value">
/// What the dimension currently reads, when resolved: a length in the sketch's units, or an angle
/// in radians for <see cref="ConstraintKind.Angle"/>.
/// </param>
/// <param name="Reason">Why, in words, when layout could not be resolved.</param>
/// <param name="DimensionArc">
/// The dimension line, when it is an arc rather than a segment — which is the case for an angular
/// dimension and no other. Anticlockwise from <c>StartAngle</c> to <c>EndAngle</c>, the same
/// convention <see cref="SketchArc"/> uses, so a renderer has one rule for arcs rather than two.
/// </param>
/// <remarks>
/// Exactly one of <paramref name="DimensionLine"/> and <paramref name="DimensionArc"/> is set when
/// resolved. Two fields rather than one, because forcing an arc into a pair of points loses the
/// bulge that is the whole shape of an angular dimension, and making every consumer handle a
/// general polyline would charge the seven kinds that are a straight line for the one that is not.
/// </remarks>
public sealed record DimensionLayout(
    DimensionLayoutOutcome Outcome,
    (Vec2d Start, Vec2d End)? DimensionLine = null,
    ImmutableArray<(Vec2d From, Vec2d To)> WitnessLines = default,
    Vec2d? TextPosition = null,
    double? Value = null,
    string? Reason = null,
    (Vec2d Centre, double Radius, double StartAngle, double EndAngle)? DimensionArc = null)
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
/// <b>Every dimension type §5.6 names is laid out here.</b> Aligned
/// (<see cref="ConstraintKind.Distance"/> between two points), linear
/// (<see cref="ConstraintKind.HorizontalDistance"/> and <see cref="ConstraintKind.VerticalDistance"/>),
/// point-to-line (<see cref="ConstraintKind.Distance"/>'s other operand shape), angular
/// (<see cref="ConstraintKind.Angle"/>), radial (<see cref="ConstraintKind.Radius"/>) and diametric
/// (<see cref="ConstraintKind.Diameter"/>).
/// </para>
/// <para>
/// <b>The witness point decides every choice that is otherwise arbitrary,</b> and there is one such
/// choice per kind. It is the offset of the dimension line for a length; the radius of the arc and
/// <em>which of the four angles</em> for an angular dimension; the direction of the leader and
/// whether it sits inside or outside the circle for a radial or diametric one. Nothing here guesses
/// from the geometry alone, because all of those have several defensible answers and only the user
/// knows which they meant — they have already said, by dragging the text somewhere.
/// </para>
/// <para>
/// <b>A point-to-line distance is an aligned dimension to the foot of the perpendicular.</b> Once
/// the foot is found, "the distance from this point to that line" is the distance between two
/// points, laid out by the same code, and giving it its own would have been a second opinion about
/// where a dimension line goes.
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
                => Length(constraint, sketch, dimension.TextPosition),
            ConstraintKind.HorizontalDistance
                => PointPair(constraint, sketch, dimension.TextPosition, (a, b, t) => Linear(a, b, t, vertical: false)),
            ConstraintKind.VerticalDistance
                => PointPair(constraint, sketch, dimension.TextPosition, (a, b, t) => Linear(a, b, t, vertical: true)),
            ConstraintKind.Angle
                => Angular(constraint, sketch, dimension.TextPosition),
            ConstraintKind.Radius
                => Radial(constraint, sketch, dimension.TextPosition, across: false),
            ConstraintKind.Diameter
                => Radial(constraint, sketch, dimension.TextPosition, across: true),
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

    /// <summary>
    /// A <see cref="ConstraintKind.Distance"/>, which measures either between two points or from a
    /// point to a line, and is told which by what its operands resolve to.
    /// </summary>
    private static DimensionLayout Length(SketchConstraint constraint, Sketch sketch, Vec2d text)
    {
        if (constraint.On.Length != 2)
        {
            return Unsupported("A distance dimension measures between two things.");
        }

        if (sketch.Entities.Find(constraint.On[0].Entity) is null
            || sketch.Entities.Find(constraint.On[1].Entity) is not { } second)
        {
            return Missing();
        }

        if (sketch.Entities.Locate(constraint.On[0]) is not { } point)
        {
            return Unsupported(
                "The first operand of a distance dimension has to be a point this build can place.");
        }

        // Two points is the aligned dimension. A point and a line is the same dimension to the foot
        // of the perpendicular, which is the only place a distance to a line is ever measured from.
        if (sketch.Entities.Locate(constraint.On[1]) is { } other)
        {
            return Aligned(point, other, text);
        }

        if (second is not SketchLine line)
        {
            return Unsupported(
                $"This build does not lay out a distance from a point to a {second.Kind}.");
        }

        if (line.Length <= Tolerance.LinearResolution)
        {
            return Degenerate(
                "The line this dimension measures to has no length, so it has no direction.");
        }

        Vec2d foot = line.Start + (line.Direction * Vec2d.Dot(point - line.Start, line.Direction));

        return Aligned(foot, point, text);
    }

    /// <summary>An angular dimension: an arc between two lines, centred where they cross.</summary>
    private static DimensionLayout Angular(SketchConstraint constraint, Sketch sketch, Vec2d text)
    {
        if (constraint.On.Length != 2)
        {
            return Unsupported("An angular dimension is between two lines.");
        }

        if (sketch.Entities.Find(constraint.On[0].Entity) is not { } firstEntity
            || sketch.Entities.Find(constraint.On[1].Entity) is not { } secondEntity)
        {
            return Missing();
        }

        if (firstEntity is not SketchLine first || secondEntity is not SketchLine second)
        {
            return Unsupported("This build lays out an angle between two lines only.");
        }

        if (first.Length <= Tolerance.LinearResolution || second.Length <= Tolerance.LinearResolution)
        {
            return Degenerate("One of the lines has no length, so it points nowhere.");
        }

        double denominator = Vec2d.Cross(first.Direction, second.Direction);

        if (System.Math.Abs(denominator) <= Tolerance.Linear)
        {
            return Degenerate(
                "These lines are parallel, so they meet nowhere and make no angle to place an arc in.");
        }

        Vec2d vertex = first.Start
            + (first.Direction
                * (Vec2d.Cross(second.Start - first.Start, second.Direction) / denominator));

        Vec2d spoke = text - vertex;
        double radius = spoke.Length;

        if (radius <= Tolerance.LinearResolution)
        {
            return Degenerate(
                "The text sits exactly where the lines cross, which says nothing about which of the "
                + "four angles was meant, nor how big to draw it.");
        }

        // Four angles meet at a crossing and the geometry alone cannot say which was meant. The one
        // the text sits in is the one, so each line contributes whichever of its two rays makes a
        // sector containing it.
        if (Sector(first.Direction, second.Direction, spoke.Angle()) is not { } chosen)
        {
            return Degenerate(
                "The text lies along one of the lines rather than between them, so there is no "
                + "sector to draw the arc in.");
        }

        (Vec2d fromRay, Vec2d toRay, double sweep) = chosen;

        return new DimensionLayout(
            DimensionLayoutOutcome.Resolved,
            null,
            [
                .. Extension(first, vertex, fromRay, radius),
                .. Extension(second, vertex, toRay, radius),
            ],
            text,
            System.Math.Abs(sweep),
            null,
            DimensionArc: (
                vertex,
                radius,
                sweep > 0 ? fromRay.Angle() : toRay.Angle(),
                sweep > 0 ? toRay.Angle() : fromRay.Angle()));
    }

    /// <summary>A radial or diametric dimension: a leader out along the direction of the text.</summary>
    private static DimensionLayout Radial(
        SketchConstraint constraint, Sketch sketch, Vec2d text, bool across)
    {
        if (constraint.On.Length != 1)
        {
            return Unsupported("A radial dimension names one circle or arc.");
        }

        if (sketch.Entities.Find(constraint.On[0].Entity) is not { } entity)
        {
            return Missing();
        }

        if (entity is not (SketchCircle or SketchArc))
        {
            return Unsupported($"This build does not lay out a radial dimension on a {entity.Kind}.");
        }

        SketchArc? arc = entity as SketchArc;
        Vec2d centre = entity is SketchCircle circle ? circle.Centre : arc!.Centre;
        double radius = entity is SketchCircle round ? round.Radius : arc!.Radius;

        if (radius <= Tolerance.LinearResolution)
        {
            return Degenerate($"This {entity.Kind} has no radius to dimension.");
        }

        Vec2d spoke = text - centre;

        if (spoke.Length <= Tolerance.LinearResolution)
        {
            return Degenerate(
                "The text sits on the centre, which gives the leader no direction to run in.");
        }

        // An arc exists only over its own sweep, so a leader aimed past either end would point at
        // nothing. Brought back to the nearer end rather than refused: the user asked for this
        // arc's radius, and the nearest place the arc actually is remains a true answer to that.
        double angle = arc is null ? spoke.Angle() : OnSweep(arc, spoke.Angle());
        Vec2d direction = new(System.Math.Cos(angle), System.Math.Sin(angle));
        Vec2d touch = centre + (direction * radius);

        // Inside or outside is the other choice only the user can make, and dragging the text past
        // the rim is how they make it: the leader then runs from the rim out to the text rather
        // than from the centre to the rim.
        bool outside = spoke.Length > radius;

        (Vec2d start, Vec2d end) = across
            ? (centre - (direction * radius), touch)
            : outside ? (touch, text) : (centre, touch);

        return new DimensionLayout(
            DimensionLayoutOutcome.Resolved,
            (start, end),
            across && outside ? [(touch, text)] : [],
            text,
            across ? radius * 2 : radius);
    }

    /// <summary>
    /// Which pair of rays makes the sector the text sits in, and how far it sweeps, signed.
    /// </summary>
    private static (Vec2d From, Vec2d To, double Sweep)? Sector(
        Vec2d first, Vec2d second, double toText)
    {
        foreach (double firstSign in (ReadOnlySpan<double>)[1, -1])
        {
            foreach (double secondSign in (ReadOnlySpan<double>)[1, -1])
            {
                Vec2d from = first * firstSign;
                Vec2d to = second * secondSign;

                double sweep = Signed(to.Angle() - from.Angle());
                double toTheText = Signed(toText - from.Angle());

                // On the same side of the first ray, and not past the second. A zero sweep cannot
                // arise here: the lines were established as non-parallel before this was asked.
                if (toTheText * sweep >= 0
                    && System.Math.Abs(toTheText) <= System.Math.Abs(sweep))
                {
                    return (from, to, sweep);
                }
            }
        }

        return null;
    }

    /// <summary>A witness line from where a line stops to where the arc needs it to reach.</summary>
    private static ImmutableArray<(Vec2d From, Vec2d To)> Extension(
        SketchLine line, Vec2d vertex, Vec2d ray, double radius)
    {
        double reach = System.Math.Max(
            Vec2d.Dot(line.Start - vertex, ray), Vec2d.Dot(line.End - vertex, ray));

        // Only when the line stops short of the arc. A line already running past it needs no
        // extension, and drawing a zero-length one would put a stray tick in the picture.
        return reach >= radius - Tolerance.Linear
            ? []
            : [(vertex + (ray * System.Math.Max(reach, 0)), vertex + (ray * radius))];
    }

    /// <summary>An angle brought onto an arc's own sweep, by the nearer end when it is outside.</summary>
    private static double OnSweep(SketchArc arc, double angle)
    {
        double wrapped = (angle - arc.StartAngle) % (2 * System.Math.PI);

        if (wrapped < 0)
        {
            wrapped += 2 * System.Math.PI;
        }

        if (wrapped <= arc.Sweep)
        {
            return angle;
        }

        // Past the end. Whichever end of the sweep it is nearer to, measured the short way round.
        return wrapped - arc.Sweep <= (2 * System.Math.PI) - wrapped
            ? arc.EndAngle
            : arc.StartAngle;
    }

    private static double Signed(double angle)
    {
        double wrapped = angle % (2 * System.Math.PI);

        return wrapped > System.Math.PI
            ? wrapped - (2 * System.Math.PI)
            : wrapped <= -System.Math.PI ? wrapped + (2 * System.Math.PI) : wrapped;
    }

    private static DimensionLayout Unsupported(string reason)
        => DimensionLayout.Failed(DimensionLayoutOutcome.Unsupported, reason);

    private static DimensionLayout Degenerate(string reason)
        => DimensionLayout.Failed(DimensionLayoutOutcome.Degenerate, reason);

    private static DimensionLayout Missing()
        => DimensionLayout.Failed(
            DimensionLayoutOutcome.GeometryNotFound,
            "The geometry this dimension measures no longer exists.");

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
