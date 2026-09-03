using System.Collections.Immutable;

using OpenMCAD.Math;

namespace OpenMCAD.Solver.Sketching;

/// <summary>How a bulk sketch edit came to.</summary>
public enum SketchEditOutcome
{
    /// <summary>The edit produced a new sketch.</summary>
    Resolved,

    /// <summary>One of the named entities is not in the sketch.</summary>
    EntityNotFound,

    /// <summary>One of the named entities is a kind this build does not yet edit this way.</summary>
    Unsupported,
}

/// <summary>What a bulk sketch edit came to.</summary>
/// <param name="Outcome">How it turned out.</param>
/// <param name="Sketch">The result, when resolved.</param>
/// <param name="Reason">Why, in words, when it could not be done.</param>
public sealed record SketchEditResult(
    SketchEditOutcome Outcome, Sketch? Sketch = null, string? Reason = null)
{
    /// <summary>Gets whether the edit produced a sketch.</summary>
    public bool IsResolved => Outcome == SketchEditOutcome.Resolved;

    /// <summary>Creates a result that produced a sketch.</summary>
    /// <param name="sketch">The result.</param>
    /// <returns>The result.</returns>
    public static SketchEditResult Found(Sketch sketch) => new(SketchEditOutcome.Resolved, sketch);

    /// <summary>Creates a result that failed.</summary>
    /// <param name="outcome">How it failed. Must not be <see cref="SketchEditOutcome.Resolved"/>.</param>
    /// <param name="reason">Why, in words.</param>
    /// <returns>The result.</returns>
    public static SketchEditResult Failed(SketchEditOutcome outcome, string reason)
        => new(outcome, null, reason);
}

/// <summary>
/// The sketch-wide editing tools that are all, underneath, a <see cref="SketchTransform"/> applied
/// to a selection: move, rotate, scale, copy, mirror, and the linear and circular patterns (P4-T13).
/// </summary>
/// <remarks>
/// <para>
/// <b>Move, rotate and scale edit in place; copy, mirror and patterns duplicate.</b> That split is
/// not incidental to which tool is which — a mirror or a pattern is meant to leave the original
/// geometry standing next to its copies, the way a real sketcher's tools work, while dragging a
/// selection is meant to move the very entities that were selected. One transform-application
/// primitive (<see cref="SketchGeometryTransform"/>) serves both; only what happens to
/// <see cref="SketchEntity.Id"/> and to constraints differs.
/// </para>
/// <para>
/// <b><see cref="Duplicate"/> keeps a copied selection's own internal relationships and drops
/// everything else.</b> A constraint duplicates, with its operands remapped onto the new entities,
/// only when every entity it names is in the copied set — a coincidence between two copied points
/// travels with them, because it is a fact about the shape being duplicated. A constraint reaching
/// outside the set is left on the original alone: duplicating "concentric with that fixed hole"
/// onto every pattern instance would point them all at the same one fixed hole, which is a
/// contradiction the first time there is more than one instance, not a pattern.
/// </para>
/// <para>
/// <b>Trim, extend, offset, split, fillet and chamfer are not here.</b> Each is a curve-topology
/// operation — cutting an entity at an intersection, building a new curve at a distance and
/// re-trimming it against its neighbours at each corner, inserting a blend between two curves whose
/// endpoints move to meet it — and none of them is "apply a transform to a selection" underneath.
/// They are real, separate work, left for when it is their turn rather than forced into this shape.
/// </para>
/// </remarks>
public static class SketchEdit
{
    /// <summary>Moves, rotates or scales a selection in place.</summary>
    /// <param name="sketch">The sketch.</param>
    /// <param name="entities">Which entities to transform.</param>
    /// <param name="transform">The transform.</param>
    /// <returns>The result.</returns>
    public static SketchEditResult Transform(
        Sketch sketch, IEnumerable<SketchEntityId> entities, SketchTransform transform)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        ArgumentNullException.ThrowIfNull(entities);

        Sketch result = sketch;

        foreach (SketchEntityId id in entities)
        {
            if (sketch.Entities.Find(id) is not { } entity)
            {
                return SketchEditResult.Failed(
                    SketchEditOutcome.EntityNotFound, $"There is no entity to move with id {id}.");
            }

            if (SketchGeometryTransform.Apply(transform, entity) is not { } transformed)
            {
                return SketchEditResult.Failed(
                    SketchEditOutcome.Unsupported,
                    $"This build cannot move, rotate or scale a {entity.Kind}.");
            }

            result = result.With(transformed);
        }

        return SketchEditResult.Found(result);
    }

    /// <summary>Adds a transformed copy of a selection, keeping the original.</summary>
    /// <param name="sketch">The sketch.</param>
    /// <param name="entities">Which entities to copy — copy, mirror and each pattern instance all
    /// call this once per instance with the transform that instance needs.</param>
    /// <param name="transform">The transform the copy is placed by.</param>
    /// <returns>The result.</returns>
    public static SketchEditResult Duplicate(
        Sketch sketch, IEnumerable<SketchEntityId> entities, SketchTransform transform)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        ArgumentNullException.ThrowIfNull(entities);

        // Encounter order, kept explicitly and deduplicated. Iterating the caller's own selection
        // order rather than, say, a HashSet's is what keeps two calls with the same arguments
        // producing the same sketch (ADR-0011) — the new ids are random regardless, but nothing
        // about which entity is processed when should be.
        List<SketchEntityId> ordered = [];
        HashSet<SketchEntityId> seen = [];

        foreach (SketchEntityId id in entities)
        {
            if (seen.Add(id))
            {
                ordered.Add(id);
            }
        }

        Dictionary<SketchEntityId, SketchEntityId> remap = [];
        Sketch result = sketch;

        foreach (SketchEntityId id in ordered)
        {
            if (sketch.Entities.Find(id) is not { } entity)
            {
                return SketchEditResult.Failed(
                    SketchEditOutcome.EntityNotFound, $"There is no entity to copy with id {id}.");
            }

            if (SketchGeometryTransform.Apply(transform, entity) is not { } transformed)
            {
                return SketchEditResult.Failed(
                    SketchEditOutcome.Unsupported, $"This build cannot copy a {entity.Kind}.");
            }

            SketchEntityId copyId = SketchEntityId.New();
            remap[id] = copyId;
            result = result.With(transformed with { Id = copyId });
        }

        foreach (SketchConstraint constraint in sketch.Constraints.Ordered)
        {
            if (constraint.On.IsEmpty || !constraint.On.All(o => remap.ContainsKey(o.Entity)))
            {
                continue;
            }

            ImmutableArray<SketchPointRef> remapped =
                [.. constraint.On.Select(o => new SketchPointRef(remap[o.Entity], o.Point))];

            result = result.With(constraint with { Id = SketchConstraintId.New(), Operands = remapped });
        }

        return SketchEditResult.Found(result);
    }

    /// <summary>Mirrors a selection about a line, keeping the original.</summary>
    /// <param name="sketch">The sketch.</param>
    /// <param name="entities">Which entities to mirror.</param>
    /// <param name="lineStart">A point on the mirror line.</param>
    /// <param name="lineEnd">Another point on it.</param>
    /// <returns>The result.</returns>
    public static SketchEditResult Mirror(
        Sketch sketch, IEnumerable<SketchEntityId> entities, Vec2d lineStart, Vec2d lineEnd)
        => Duplicate(sketch, entities, SketchTransform.MirrorAbout(lineStart, lineEnd));

    /// <summary>
    /// Repeats a selection along a straight line, evenly spaced.
    /// </summary>
    /// <param name="sketch">The sketch.</param>
    /// <param name="entities">Which entities to repeat.</param>
    /// <param name="step">The offset from one instance to the next.</param>
    /// <param name="count">
    /// How many instances the pattern has in total, counting the original — one leaves the
    /// selection untouched, the way asking for a pattern of a single instance should.
    /// </param>
    /// <returns>The result.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is less than one.</exception>
    public static SketchEditResult LinearPattern(
        Sketch sketch, IEnumerable<SketchEntityId> entities, Vec2d step, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        ImmutableArray<SketchEntityId> selection = [.. entities];
        Sketch result = sketch;

        for (int i = 1; i < count; i++)
        {
            SketchEditResult copy = Duplicate(result, selection, SketchTransform.Translate(step * i));

            if (!copy.IsResolved)
            {
                return copy;
            }

            result = copy.Sketch!;
        }

        return SketchEditResult.Found(result);
    }

    /// <summary>
    /// Repeats a selection about a centre, evenly spaced across a total angle.
    /// </summary>
    /// <param name="sketch">The sketch.</param>
    /// <param name="entities">Which entities to repeat.</param>
    /// <param name="centre">The point to rotate about.</param>
    /// <param name="totalAngle">
    /// How far apart the first and the one-past-the-last instance would be, in radians —
    /// <c>2·π</c> for a full circle. Instances are spaced at <c>totalAngle / count</c>, not
    /// <c>totalAngle / (count − 1)</c>: for a full circle those agree only by coincidence when the
    /// pattern also happens to close exactly on itself, and dividing by <paramref name="count"/> is
    /// what makes four instances round a bolt circle land at 0°, 90°, 180° and 270° rather than
    /// leaving a 90° gap where a fifth, unrequested instance would have closed the loop.
    /// </param>
    /// <param name="count">How many instances the pattern has in total, counting the original.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is less than one.</exception>
    public static SketchEditResult CircularPattern(
        Sketch sketch, IEnumerable<SketchEntityId> entities, Vec2d centre, double totalAngle, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        ImmutableArray<SketchEntityId> selection = [.. entities];
        double step = totalAngle / count;
        Sketch result = sketch;

        for (int i = 1; i < count; i++)
        {
            SketchEditResult copy = Duplicate(
                result, selection, SketchTransform.RotateAbout(centre, step * i));

            if (!copy.IsResolved)
            {
                return copy;
            }

            result = copy.Sketch!;
        }

        return SketchEditResult.Found(result);
    }

    /// <summary>Marks a selection as construction geometry, or as profile geometry again.</summary>
    /// <param name="sketch">The sketch.</param>
    /// <param name="entities">Which entities to change.</param>
    /// <param name="isConstruction">Whether they become construction geometry.</param>
    /// <returns>The result.</returns>
    public static SketchEditResult SetConstruction(
        Sketch sketch, IEnumerable<SketchEntityId> entities, bool isConstruction)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        ArgumentNullException.ThrowIfNull(entities);

        Sketch result = sketch;

        foreach (SketchEntityId id in entities)
        {
            if (sketch.Entities.Find(id) is not { } entity)
            {
                return SketchEditResult.Failed(
                    SketchEditOutcome.EntityNotFound, $"There is no entity with id {id}.");
            }

            result = result.With(entity with { IsConstruction = isConstruction });
        }

        return SketchEditResult.Found(result);
    }
}
