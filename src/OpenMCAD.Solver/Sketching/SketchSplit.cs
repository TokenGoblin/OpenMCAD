using System.Collections.Immutable;

using OpenMCAD.Math;

namespace OpenMCAD.Solver.Sketching;

/// <summary>How splitting an entity came to.</summary>
public enum SplitOutcome
{
    /// <summary>The entity was split into two.</summary>
    Resolved,

    /// <summary>There is no such entity in the sketch.</summary>
    EntityNotFound,

    /// <summary>This build does not split this kind of entity.</summary>
    Unsupported,

    /// <summary>The point is at an end already, or off the entity altogether.</summary>
    NotOnEntity,

    /// <summary>
    /// A constraint names a point that belongs to neither resulting piece on its own, so which one
    /// it should follow is not decidable.
    /// </summary>
    ConstraintNotTransferable,
}

/// <summary>What splitting an entity came to.</summary>
/// <param name="Outcome">How it turned out.</param>
/// <param name="Sketch">The result, when resolved.</param>
/// <param name="First">The id of the piece keeping the original start, when resolved.</param>
/// <param name="Second">The id of the new piece, when resolved.</param>
/// <param name="Reason">Why, in words, when it could not be done.</param>
public sealed record SplitResult(
    SplitOutcome Outcome,
    Sketch? Sketch = null,
    SketchEntityId? First = null,
    SketchEntityId? Second = null,
    string? Reason = null)
{
    /// <summary>Gets whether the split produced two pieces.</summary>
    public bool IsResolved => Outcome == SplitOutcome.Resolved;

    /// <summary>Creates a result that produced two pieces.</summary>
    public static SplitResult Found(Sketch sketch, SketchEntityId first, SketchEntityId second)
        => new(SplitOutcome.Resolved, sketch, first, second);

    /// <summary>Creates a result that failed.</summary>
    /// <param name="outcome">How it failed. Must not be <see cref="SplitOutcome.Resolved"/>.</param>
    /// <param name="reason">Why, in words.</param>
    public static SplitResult Failed(SplitOutcome outcome, string reason) => new(outcome, Reason: reason);
}

/// <summary>
/// Breaks an entity into two at a point on it, keeping both pieces (P4-T13).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scoped to line and arc — deliberately not circle.</b> A line and an arc each have two real
/// ends already, so cutting one at an interior point gives each piece one of the original ends and
/// the new point as its other end. A circle has none: cutting a closed loop at a single point does
/// not produce two pieces, it produces one open curve, which is a different operation
/// (<see cref="SketchTrim"/> already covers the case of turning a circle into an arc, at two points
/// rather than one). Reported as <see cref="SplitOutcome.Unsupported"/> rather than attempted badly.
/// </para>
/// <para>
/// <b>Unlike <see cref="SketchTrim"/>, splitting has a decidable rule for where every constraint
/// goes</b> — which is exactly why it exists as its own operation rather than trim producing a
/// second piece itself. Nothing is deleted, so every point of the original entity still belongs to
/// exactly one of the two results: <see cref="EntityPoint.Start"/> keeps the original id and needs
/// no change; <see cref="EntityPoint.End"/> is remapped onto the new piece, because it is no longer
/// where the first piece ends; <see cref="EntityPoint.Self"/> and (for an arc)
/// <see cref="EntityPoint.Centre"/> are facts about the whole original entity that remain true of
/// both halves — a horizontal line split in two is still two horizontal lines, and an arc's centre
/// does not move when a piece of its circumference is cut — so a constraint naming either is kept on
/// the first piece and duplicated onto the second. <see cref="EntityPoint.Middle"/> is the one point
/// with no such rule: the midpoint of the *original* entity is, after the cut, simply some point on
/// whichever piece contains it, not a distinguished point of either — and refusing with
/// <see cref="SplitOutcome.ConstraintNotTransferable"/> is honest about that rather than silently
/// keeping a constraint that now measures to the wrong place.
/// </para>
/// </remarks>
public static class SketchSplit
{
    /// <summary>Splits an entity at a point on it.</summary>
    /// <param name="sketch">The sketch.</param>
    /// <param name="id">Which entity to split.</param>
    /// <param name="at">
    /// Where to split it. Projected onto the entity — the actual cut lands at the nearest point of
    /// the curve to this one, not necessarily exactly here.
    /// </param>
    /// <returns>The result.</returns>
    public static SplitResult Split(Sketch sketch, SketchEntityId id, Vec2d at)
    {
        ArgumentNullException.ThrowIfNull(sketch);

        if (sketch.Entities.Find(id) is not { } entity)
        {
            return SplitResult.Failed(
                SplitOutcome.EntityNotFound, $"There is no entity to split with id {id}.");
        }

        return entity switch
        {
            SketchLine line => SplitLine(sketch, line, at),
            SketchArc arc => SplitArc(sketch, arc, at),
            _ => SplitResult.Failed(SplitOutcome.Unsupported, $"This build cannot split a {entity.Kind}."),
        };
    }

    private static SplitResult SplitLine(Sketch sketch, SketchLine line, Vec2d at)
    {
        if (line.Length <= Tolerance.LinearResolution)
        {
            return SplitResult.Failed(SplitOutcome.Unsupported, "This line has no length to split.");
        }

        double t = Vec2d.Dot(at - line.Start, line.Direction) / line.Length;

        if (t <= Tolerance.Parametric || t >= 1 - Tolerance.Parametric)
        {
            return SplitResult.Failed(
                SplitOutcome.NotOnEntity, "The split point is at an end of this line, or off it.");
        }

        Vec2d point = line.PointAt(t);
        SketchEntityId secondId = SketchEntityId.New();

        SketchLine first = line with { End = point };
        SketchLine second = new(secondId, point, line.End, line.IsConstruction);

        return Finish(sketch, line.Id, secondId, first, second);
    }

    private static SplitResult SplitArc(Sketch sketch, SketchArc arc, Vec2d at)
    {
        if (arc.Radius <= Tolerance.LinearResolution)
        {
            return SplitResult.Failed(SplitOutcome.Unsupported, "This arc has no radius to split.");
        }

        double offset = SketchTrim.OffsetAlong(arc, at);

        if (offset <= Tolerance.Parametric || offset >= arc.Sweep - Tolerance.Parametric)
        {
            return SplitResult.Failed(
                SplitOutcome.NotOnEntity, "The split point is at an end of this arc, or off it.");
        }

        double splitAngle = arc.StartAngle + offset;
        Vec2d point = arc.PointAt(offset / arc.Sweep);
        SketchEntityId secondId = SketchEntityId.New();

        SketchArc first = arc with { EndAngle = splitAngle };
        SketchArc second = new(secondId, arc.Centre, arc.Radius, splitAngle, arc.EndAngle, arc.IsConstruction);

        return Finish(sketch, arc.Id, secondId, first, second, isArc: true);
    }

    private static SplitResult Finish(
        Sketch sketch,
        SketchEntityId originalId,
        SketchEntityId secondId,
        SketchEntity first,
        SketchEntity second,
        bool isArc = false)
    {
        Sketch split = sketch.With(first).With(second);

        foreach (SketchConstraint constraint in sketch.Constraints.Ordered)
        {
            if (!constraint.On.Any(o => o.Entity == originalId))
            {
                continue;
            }

            if (constraint.On.Any(o => o.Entity == originalId && o.Point == EntityPoint.Middle))
            {
                return SplitResult.Failed(
                    SplitOutcome.ConstraintNotTransferable,
                    $"'{constraint.Kind}' names the midpoint of the entity being split, which is "
                    + "not a distinguished point of either piece afterwards.");
            }

            bool sharedByBoth = constraint.On.Any(o => o.Entity == originalId
                && (o.Point == EntityPoint.Self || (isArc && o.Point == EntityPoint.Centre)));

            if (sharedByBoth)
            {
                ImmutableArray<SketchPointRef> onSecond =
                [
                    .. constraint.On.Select(o => o.Entity == originalId
                        ? new SketchPointRef(secondId, o.Point)
                        : o),
                ];

                split = split.With(constraint with { Id = SketchConstraintId.New(), Operands = onSecond });
                continue;
            }

            bool touchesEnd = constraint.On.Any(o => o.Entity == originalId && o.Point == EntityPoint.End);

            if (touchesEnd)
            {
                ImmutableArray<SketchPointRef> remapped =
                [
                    .. constraint.On.Select(o => o.Entity == originalId && o.Point == EntityPoint.End
                        ? new SketchPointRef(secondId, EntityPoint.End)
                        : o),
                ];

                split = split.With(constraint with { Operands = remapped });
            }

            // A reference to Start needs no change: the first piece kept the original id and Start.
        }

        return SplitResult.Found(split, originalId, secondId);
    }
}
