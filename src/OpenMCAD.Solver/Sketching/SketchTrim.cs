using System.Collections.Immutable;

using OpenMCAD.Math;

namespace OpenMCAD.Solver.Sketching;

/// <summary>How trimming an entity came to.</summary>
public enum TrimOutcome
{
    /// <summary>The entity was shortened.</summary>
    Resolved,

    /// <summary>There is no such entity in the sketch.</summary>
    EntityNotFound,

    /// <summary>This build does not trim this kind of entity.</summary>
    Unsupported,

    /// <summary>Nothing else in the sketch crosses this entity, so there is nothing to trim to.</summary>
    NoIntersections,

    /// <summary>
    /// The click falls between two crossings with material on both sides — trimming it would split
    /// the entity into two separate pieces, which this build does not yet do.
    /// </summary>
    WouldSplit,
}

/// <summary>What trimming an entity came to.</summary>
/// <param name="Outcome">How it turned out.</param>
/// <param name="Sketch">The result, when resolved.</param>
/// <param name="Reason">Why, in words, when it could not be done.</param>
public sealed record TrimResult(TrimOutcome Outcome, Sketch? Sketch = null, string? Reason = null)
{
    /// <summary>Gets whether the trim produced a sketch.</summary>
    public bool IsResolved => Outcome == TrimOutcome.Resolved;

    /// <summary>Creates a result that produced a sketch.</summary>
    /// <param name="sketch">The result.</param>
    /// <returns>The result.</returns>
    public static TrimResult Found(Sketch sketch) => new(TrimOutcome.Resolved, sketch);

    /// <summary>Creates a result that failed.</summary>
    /// <param name="outcome">How it failed. Must not be <see cref="TrimOutcome.Resolved"/>.</param>
    /// <param name="reason">Why, in words.</param>
    /// <returns>The result.</returns>
    public static TrimResult Failed(TrimOutcome outcome, string reason) => new(outcome, null, reason);
}

/// <summary>
/// Shortens an entity to the nearest crossing(s) with the rest of the sketch, on the side of a
/// click (P4-T13).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scoped to line, circle and arc</b> — the same boundary <see cref="SketchGeometryTransform"/>
/// and <c>WorldCurve</c> (P4-T11) already draw, for the same reason: the others each need their own
/// parameterisation worked out with the same care, and a genuinely incomplete case reports
/// <see cref="TrimOutcome.Unsupported"/> rather than silently doing nothing or the wrong thing.
/// </para>
/// <para>
/// <b>Only ever shortens one end (or, for a circle, keeps one arc) — it never splits an entity into
/// two.</b> A real trim tool deletes the segment nearest the click and keeps whatever remains, which
/// is two separate pieces when there is a crossing on both sides of the click. Producing that second
/// piece with a fresh id is not the hard part; deciding which of the original entity's constraints
/// travel with which piece is (the same question P4-T13's <see cref="SketchEdit.Duplicate"/> answers
/// for a copy by keeping only a constraint whose entities are <em>all</em> copied together — but a
/// split has no such rule waiting, since the two pieces are not a copied set, they are the same
/// entity torn in two, and a constraint on its far end plainly belongs with only one of them).
/// Reporting <see cref="TrimOutcome.WouldSplit"/> and refusing is honest about that being unresolved;
/// the common case — cutting back an overshoot to the nearest intersection — has only one side to
/// keep and works today.
/// </para>
/// <para>
/// <b>A circle never needs to split, and this is not a special case bolted on — it falls out of the
/// geometry.</b> A line or an arc has two real ends, so a crossing on both sides of the click leaves
/// two genuinely separate pieces. A circle has none: removing one arc from a closed loop always
/// leaves exactly one connected piece, however many other crossings sit on it, so trimming a circle
/// with two or more crossings resolves every time and becomes a <see cref="SketchArc"/> — the one
/// arc that was not clicked on.
/// </para>
/// </remarks>
public static class SketchTrim
{
    private const double FullTurn = 2 * System.Math.PI;

    /// <summary>Trims an entity.</summary>
    /// <param name="sketch">The sketch.</param>
    /// <param name="id">Which entity to trim.</param>
    /// <param name="near">
    /// Roughly where the user clicked, which decides which side of a crossing is kept and, for a
    /// closed circle, which arc of it survives. Need not be exactly on the entity.
    /// </param>
    /// <returns>The result.</returns>
    public static TrimResult Trim(Sketch sketch, SketchEntityId id, Vec2d near)
    {
        ArgumentNullException.ThrowIfNull(sketch);

        if (sketch.Entities.Find(id) is not { } entity)
        {
            return TrimResult.Failed(
                TrimOutcome.EntityNotFound, $"There is no entity to trim with id {id}.");
        }

        return entity switch
        {
            SketchLine line => TrimLine(sketch, line, near),
            SketchCircle circle => TrimCircle(sketch, circle, near),
            SketchArc arc => TrimArc(sketch, arc, near),
            _ => TrimResult.Failed(TrimOutcome.Unsupported, $"This build cannot trim a {entity.Kind}."),
        };
    }

    private static TrimResult TrimLine(Sketch sketch, SketchLine line, Vec2d near)
    {
        if (line.Length <= Tolerance.LinearResolution)
        {
            return TrimResult.Failed(TrimOutcome.Unsupported, "This line has no length to trim.");
        }

        ImmutableArray<double> parameters =
        [
            .. OtherEntities(sketch, line.Id)
                .SelectMany(other => SketchSnapping.Crossings(line, other))
                .Select(p => Vec2d.Dot(p - line.Start, line.Direction) / line.Length),
        ];

        if (parameters.IsEmpty)
        {
            return NoCrossings("line");
        }

        double nearParameter = Vec2d.Dot(near - line.Start, line.Direction) / line.Length;

        return Bracket(parameters, nearParameter) switch
        {
            (null, null) => NoCrossings("line"),

            ({ } before, null) => TrimResult.Found(sketch.With(line with { End = line.PointAt(before) })),

            (null, { } after) => TrimResult.Found(sketch.With(line with { Start = line.PointAt(after) })),

            _ => WouldSplit("line"),
        };
    }

    private static TrimResult TrimArc(Sketch sketch, SketchArc arc, Vec2d near)
    {
        if (arc.Radius <= Tolerance.LinearResolution)
        {
            return TrimResult.Failed(TrimOutcome.Unsupported, "This arc has no radius to trim.");
        }

        ImmutableArray<double> offsets =
        [
            .. OtherEntities(sketch, arc.Id)
                .SelectMany(other => SketchSnapping.Crossings(arc, other))
                .Select(p => OffsetAlong(arc, p)),
        ];

        if (offsets.IsEmpty)
        {
            return NoCrossings("arc");
        }

        double nearOffset = OffsetAlong(arc, near);

        return Bracket(offsets, nearOffset) switch
        {
            (null, null) => NoCrossings("arc"),

            ({ } before, null) => TrimResult.Found(
                sketch.With(arc with { EndAngle = arc.StartAngle + before })),

            (null, { } after) => TrimResult.Found(
                sketch.With(arc with { StartAngle = arc.StartAngle + after })),

            _ => WouldSplit("arc"),
        };
    }

    private static TrimResult TrimCircle(Sketch sketch, SketchCircle circle, Vec2d near)
    {
        if (circle.Radius <= Tolerance.LinearResolution)
        {
            return TrimResult.Failed(TrimOutcome.Unsupported, "This circle has no radius to trim.");
        }

        ImmutableArray<double> angles =
        [
            .. OtherEntities(sketch, circle.Id)
                .SelectMany(other => SketchSnapping.Crossings(circle, other))
                .Select(p => (p - circle.Centre).Angle()),
        ];

        // A single crossing brackets no more than a sliver of nothing to delete either side of it --
        // there is no "other side" on a closed curve the way a line has a second, untouched end.
        if (angles.Distinct().Count() < 2)
        {
            return TrimResult.Failed(
                TrimOutcome.NoIntersections,
                "This circle needs at least two crossings with the rest of the sketch to trim.");
        }

        double nearAngle = (near - circle.Centre).Angle();

        // Unlike a line or an arc, a circle has no boundary for a search to run off the end of --
        // wrapping the long way round is what "before" and "after" mean here.
        double after = angles.Select(a => a > nearAngle ? a : a + FullTurn).Min();
        double before = angles.Select(a => a < nearAngle ? a : a - FullTurn).Max();

        SketchArc survivor = new(
            circle.Id, circle.Centre, circle.Radius, after, before + FullTurn, circle.IsConstruction);

        return TrimResult.Found(sketch.With(survivor));
    }

    /// <summary>How far a point sits from an arc's own start, anticlockwise, in radians.</summary>
    private static double OffsetAlong(SketchArc arc, Vec2d point)
    {
        double raw = (point - arc.Centre).Angle() - arc.StartAngle;
        double wrapped = raw % FullTurn;

        return wrapped < 0 ? wrapped + FullTurn : wrapped;
    }

    /// <summary>The candidate nearest below <paramref name="value"/>, and the one nearest above it.</summary>
    private static (double? Before, double? After) Bracket(
        IEnumerable<double> candidates, double value)
    {
        double? before = candidates.Where(c => c < value).Cast<double?>().Max();
        double? after = candidates.Where(c => c > value).Cast<double?>().Min();

        return (before, after);
    }

    private static IEnumerable<SketchEntity> OtherEntities(Sketch sketch, SketchEntityId excluding)
        => sketch.Entities.Ordered.Where(e => e.Id != excluding);

    private static TrimResult NoCrossings(string kind) => TrimResult.Failed(
        TrimOutcome.NoIntersections, $"Nothing else in the sketch crosses this {kind}.");

    private static TrimResult WouldSplit(string kind) => TrimResult.Failed(
        TrimOutcome.WouldSplit,
        $"The click falls between two crossings with material on both sides of it, and trimming "
        + $"there would split this {kind} into two pieces.");
}
