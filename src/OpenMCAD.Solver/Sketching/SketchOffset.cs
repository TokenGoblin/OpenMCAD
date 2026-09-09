using System.Collections.Immutable;

using OpenMCAD.Math;

namespace OpenMCAD.Solver.Sketching;

/// <summary>How offsetting came to.</summary>
public enum OffsetOutcome
{
    /// <summary>The offset geometry was built.</summary>
    Resolved,

    /// <summary>There is no such entity in the sketch.</summary>
    EntityNotFound,

    /// <summary>This build does not offset an entity of this kind.</summary>
    Unsupported,

    /// <summary>The selection does not join up end to end into a single chain.</summary>
    NotAChain,

    /// <summary>The distance asked for consumes a piece of the chain, or turns one inside out.</summary>
    DoesNotFit,
}

/// <summary>What offsetting came to.</summary>
/// <param name="Outcome">How it turned out.</param>
/// <param name="Sketch">The result, when resolved.</param>
/// <param name="Pieces">The new entities, in chain order, when resolved.</param>
/// <param name="Reason">Why, in words, when it could not be done.</param>
public sealed record OffsetResult(
    OffsetOutcome Outcome,
    Sketch? Sketch = null,
    ImmutableArray<SketchEntityId> Pieces = default,
    string? Reason = null)
{
    /// <summary>Gets whether the offset was built.</summary>
    public bool IsResolved => Outcome == OffsetOutcome.Resolved;

    /// <summary>Gets the new entities, never a default array.</summary>
    public ImmutableArray<SketchEntityId> Created => Pieces.IsDefault ? [] : Pieces;

    /// <summary>Creates a result that built an offset.</summary>
    /// <param name="sketch">The result.</param>
    /// <param name="pieces">The new entities, in chain order.</param>
    /// <returns>The result.</returns>
    public static OffsetResult Found(Sketch sketch, ImmutableArray<SketchEntityId> pieces)
        => new(OffsetOutcome.Resolved, sketch, pieces);

    /// <summary>Creates a result that failed.</summary>
    /// <param name="outcome">How it failed. Must not be <see cref="OffsetOutcome.Resolved"/>.</param>
    /// <param name="reason">Why, in words.</param>
    /// <returns>The result.</returns>
    public static OffsetResult Failed(OffsetOutcome outcome, string reason)
        => new(outcome, Reason: reason);
}

/// <summary>
/// Builds a parallel copy of a chain of sketch geometry at a given distance (P4-T13).
/// </summary>
/// <remarks>
/// <para>
/// <b>Offsetting one curve is arithmetic; offsetting a chain is the operation.</b> A line moves
/// sideways and an arc changes radius, and if that were all of it this would be three lines inside
/// <see cref="SketchEdit"/>. It is not: the offsets of two curves that met at a corner do not meet.
/// At a corner turning away from the offset they fall short of one another and at one turning
/// towards it they overshoot, and closing that gap or cutting back that overlap at every join is
/// the whole of the work. Which is why the other ten tools could be built without this one and why
/// it was left until last rather than bundled in with the transforms.
/// </para>
/// <para>
/// <b>The distance is signed, and positive means left of the way the chain runs.</b> Not a click,
/// unlike <see cref="SketchTrim"/> or <see cref="SketchCorner"/>, and deliberately: a click says
/// which side of one curve was meant, but a chain has to be offset to the <em>same</em> side all the
/// way along or the pieces will not join, so the side is a property of the chain rather than of
/// anywhere the cursor happens to be. Converting a cursor position into this sign is one cross
/// product and belongs to whatever is drawing the preview. The convention makes an arc's inside its
/// left, since <see cref="SketchArc"/> runs anticlockwise (P4-T03), so a positive offset of a circle
/// is the smaller one — which falls out of the rule rather than being a rule of its own.
/// </para>
/// <para>
/// <b>The chain is discovered, not asserted.</b> The caller names a set of entities; this orders
/// them by their endpoints and refuses anything that is not a single open chain or a single closed
/// loop — a branch, two disjoint runs, a stray entity. Taking a caller's word for the order would
/// make a wrong selection produce plausible-looking geometry joined in the wrong sequence, which is
/// far worse than a refusal, and the walk that finds the order is also what establishes that the
/// selection is offsettable at all.
/// </para>
/// <para>
/// <b>Which way round the chain runs is pinned to the entity named first, not to whichever loose
/// end the walk reached first.</b> Discovering the order leaves the <em>direction</em> open — a
/// chain can be walked from either end — and since the direction is what "left" means, leaving it
/// to fall out of the walk would let the same selection, listed in a different order, quietly offset
/// to the other side. So the chain is turned to run through the first entity named in that entity's
/// own Start-to-End direction. That is a rule a caller can see the effect of and a UI can use
/// directly: the entity the user clicked first is the one whose direction decides the side.
/// </para>
/// <para>
/// <b>Joins are mitred.</b> Each pair of neighbouring offsets is taken out to where they actually
/// cross. Where a curved pair crosses twice the one nearest the two loose ends is taken, on the
/// grounds that the loose ends are where the offsets really stop and the join belongs between them;
/// in fairness, no case has been found where that differs from taking the crossing nearest the
/// original corner, so it is the better-motivated rule rather than a demonstrably necessary one.
/// Rounding a join with an arc instead is the other standard answer and a real one, but it is a
/// different shape of result — it adds geometry the selection has no counterpart for — and mitring is
/// what makes the offset of a chain of <em>n</em> pieces a chain of <em>n</em> pieces.
/// </para>
/// <para>
/// <b>A join that turns through nothing is left where it is.</b> Two collinear segments, or a curve
/// meeting a line it is tangent to, offset to two pieces that already touch — and asking where they
/// cross is either unanswerable (parallel lines have no crossing) or numerically miserable (tangent
/// circles have one, found by subtracting nearly equal numbers). Noticing that the two ends are
/// already in the same place is what keeps a perfectly ordinary chain from failing on a corner that
/// is not a corner.
/// </para>
/// <para>
/// <b>A piece consumed by its own joins is a refusal, and it is detected by how far the ends
/// moved rather than by how long the piece ended up.</b> Offsetting far enough into a concave run
/// eats the short pieces in it, and the giveaway is an arc whose two ends were pushed past each
/// other — which, measured after the fact, is indistinguishable from an arc that swept nearly the
/// whole way round, because a sweep is only ever reported as a positive angle. Measuring instead how
/// far each end moved, signed and wrapped to half a turn either way, tells the two apart: the new
/// sweep is the old one plus the difference, and a piece survives exactly when that stays positive.
/// This catches a local collapse. An offset that runs into <em>another</em> part of the same chain,
/// which is what makes offsetting genuinely hard, is not caught here and is not claimed to be.
/// </para>
/// <para>
/// <b>Nothing happens to the original geometry or its constraints.</b> Alone among the editing tools
/// this one only adds, so the question that took the most care in <see cref="SketchSplit"/> and
/// <see cref="SketchCorner"/> — which constraints follow what — does not arise. What the new pieces
/// do get is a coincidence at each join, for the same reason a blend does: without them the offset
/// is a row of curves that happen to touch today and comes apart at the first drag.
/// </para>
/// </remarks>
public static class SketchOffset
{
    private const double FullTurn = 2 * System.Math.PI;

    /// <summary>Offsets a chain of lines and arcs, or a single circle.</summary>
    /// <param name="sketch">The sketch.</param>
    /// <param name="entities">
    /// What to offset. Must join end to end into one open chain or one closed loop, in any order —
    /// the order is worked out here — or be a single circle on its own.
    /// </param>
    /// <param name="distance">
    /// How far, and to which side: positive is to the left of the direction the chain runs, so a
    /// positive offset of a circle or an arc is the smaller one.
    /// </param>
    /// <returns>The result.</returns>
    public static OffsetResult Offset(
        Sketch sketch, IEnumerable<SketchEntityId> entities, double distance)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        ArgumentNullException.ThrowIfNull(entities);

        if (!double.IsFinite(distance) || System.Math.Abs(distance) <= Tolerance.LinearResolution)
        {
            return OffsetResult.Failed(
                OffsetOutcome.DoesNotFit, "An offset needs a distance that is not zero.");
        }

        ImmutableArray<SketchEntity> selected = ImmutableArray<SketchEntity>.Empty;

        foreach (SketchEntityId id in entities.Distinct())
        {
            if (sketch.Entities.Find(id) is not { } entity)
            {
                return OffsetResult.Failed(
                    OffsetOutcome.EntityNotFound, $"There is no entity to offset with id {id}.");
            }

            if (entity is not (SketchLine or SketchArc or SketchCircle))
            {
                return OffsetResult.Failed(
                    OffsetOutcome.Unsupported, $"This build cannot offset a {entity.Kind}.");
            }

            selected = selected.Add(entity);
        }

        if (selected.IsEmpty)
        {
            return OffsetResult.Failed(OffsetOutcome.NotAChain, "There is nothing to offset.");
        }

        // A circle has no ends, so it can neither continue a chain nor be continued. On its own it
        // is the simplest offset there is; with anything else it is a selection that cannot mean
        // what it looks like it means.
        if (selected.Any(e => e is SketchCircle))
        {
            return selected.Length == 1
                ? OffsetCircle(sketch, (SketchCircle)selected[0], distance)
                : OffsetResult.Failed(
                    OffsetOutcome.NotAChain,
                    "A circle is closed already, so it cannot be part of a chain with anything else.");
        }

        if (Order(selected) is not { } chain)
        {
            return OffsetResult.Failed(
                OffsetOutcome.NotAChain,
                "These entities do not join end to end into a single chain — they branch, or fall "
                + "into more than one run, or one of them touches nothing.");
        }

        return OffsetChain(sketch, chain, distance);
    }

    /// <summary>One entity of a chain, and which way the walk went through it.</summary>
    /// <param name="Source">The entity being offset.</param>
    /// <param name="Reversed">
    /// Whether the chain runs through it from its End to its Start. Nothing is flipped to suit the
    /// walk — the offset keeps the source's own Start and End — so this is the only record of which
    /// way "left" points, and every use of it is a use of that.
    /// </param>
    private readonly record struct Step(SketchEntity Source, bool Reversed)
    {
        /// <summary>Gets which of the offset's own points the chain arrives at.</summary>
        public EntityPoint Entry => Reversed ? EntityPoint.End : EntityPoint.Start;

        /// <summary>Gets which of the offset's own points the chain leaves by.</summary>
        public EntityPoint Exit => Reversed ? EntityPoint.Start : EntityPoint.End;
    }

    private static OffsetResult OffsetCircle(Sketch sketch, SketchCircle circle, double distance)
    {
        double radius = circle.Radius - distance;

        if (radius <= Tolerance.LinearResolution)
        {
            return OffsetResult.Failed(
                OffsetOutcome.DoesNotFit,
                $"Offsetting this circle by {distance} leaves it no radius at all.");
        }

        SketchEntityId id = SketchEntityId.New();
        SketchCircle offset = new(id, circle.Centre, radius, circle.IsConstruction);

        return OffsetResult.Found(sketch.With(offset), [id]);
    }

    private static OffsetResult OffsetChain(
        Sketch sketch, ImmutableArray<Step> chain, double distance)
    {
        SketchEntity[] offsets = new SketchEntity[chain.Length];

        for (int i = 0; i < chain.Length; i++)
        {
            if (Parallel(chain[i], distance, SketchEntityId.New()) is not { } offset)
            {
                return OffsetResult.Failed(
                    OffsetOutcome.DoesNotFit,
                    $"Offsetting this {chain[i].Source.Kind} by {distance} leaves it no radius at all.");
            }

            offsets[i] = offset;
        }

        bool closed = chain.Length > 1 && Meets(Exit(chain[^1]), Entry(chain[0]));
        int joins = closed ? chain.Length : chain.Length - 1;

        for (int i = 0; i < joins; i++)
        {
            int next = (i + 1) % chain.Length;

            if (Join(chain[i], offsets[i], chain[next], offsets[next]) is not { } joined)
            {
                return OffsetResult.Failed(
                    OffsetOutcome.DoesNotFit,
                    $"The offsets of the {chain[i].Source.Kind} and the {chain[next].Source.Kind} "
                    + "that meet here never cross, so there is no join to make between them.");
            }

            offsets[i] = joined.Leaving;
            offsets[next] = joined.Arriving;
        }

        for (int i = 0; i < offsets.Length; i++)
        {
            if (Survives(chain[i], offsets[i]))
            {
                continue;
            }

            return OffsetResult.Failed(
                OffsetOutcome.DoesNotFit,
                $"An offset of {distance} consumes the {chain[i].Source.Kind} in this chain "
                + "entirely — its two ends were pushed past one another.");
        }

        Sketch built = sketch;
        ImmutableArray<SketchEntityId> pieces = [];

        foreach (SketchEntity offset in offsets)
        {
            built = built.With(offset);
            pieces = pieces.Add(offset.Id);
        }

        for (int i = 0; i < joins; i++)
        {
            int next = (i + 1) % chain.Length;

            built = built.With(SketchConstraint.Of(
                ConstraintKind.Coincident,
                [
                    new SketchPointRef(offsets[i].Id, chain[i].Exit),
                    new SketchPointRef(offsets[next].Id, chain[next].Entry),
                ]));
        }

        return OffsetResult.Found(built, pieces);
    }

    /// <summary>The parallel of one entity, before its neighbours have had their say.</summary>
    private static SketchEntity? Parallel(Step step, double distance, SketchEntityId id)
    {
        switch (step.Source)
        {
            case SketchLine line:
            {
                Vec2d travel = step.Reversed ? -line.Direction : line.Direction;
                Vec2d sideways = travel.Perpendicular() * distance;

                return new SketchLine(id, line.Start + sideways, line.End + sideways, line.IsConstruction);
            }

            case SketchArc arc:
            {
                // Anticlockwise is the only direction an arc runs, so travelling forwards puts the
                // centre on the left and travelling backwards puts it on the right.
                double radius = step.Reversed ? arc.Radius + distance : arc.Radius - distance;

                return radius <= Tolerance.LinearResolution
                    ? null
                    : new SketchArc(
                        id, arc.Centre, radius, arc.StartAngle, arc.EndAngle, arc.IsConstruction);
            }

            default:
                return null;
        }
    }

    /// <summary>Takes two neighbouring offsets out to where they cross.</summary>
    private static (SketchEntity Leaving, SketchEntity Arriving)? Join(
        Step leavingStep, SketchEntity leaving, Step arrivingStep, SketchEntity arriving)
    {
        Vec2d loose = At(leaving, leavingStep.Exit);
        Vec2d met = At(arriving, arrivingStep.Entry);

        // A tangent join offsets to a tangent join: the two ends are already in the same place, and
        // asking two curves that touch where they cross is a question with no stable answer.
        if (Meets(loose, met))
        {
            return (leaving, arriving);
        }

        ImmutableArray<Vec2d> crossings = Crossings(leaving, arriving);

        if (crossings.IsEmpty)
        {
            return null;
        }

        Vec2d between = Vec2d.Lerp(loose, met, 0.5);
        Vec2d at = crossings.OrderBy(c => Vec2d.DistanceSquared(c, between)).First();

        return (Moved(leaving, leavingStep.Exit, at), Moved(arriving, arrivingStep.Entry, at));
    }

    /// <summary>
    /// Whether a piece is still there once both its joins have moved its ends.
    /// </summary>
    /// <remarks>
    /// Measured as how far each end moved rather than as what is left, because what is left cannot
    /// tell an arc that collapsed from one that swept almost the whole way round: both report a
    /// positive sweep, and only the signed movement of the ends says which happened.
    /// </remarks>
    private static bool Survives(Step step, SketchEntity offset)
    {
        switch (offset)
        {
            case SketchLine line when step.Source is SketchLine source:

                // A translation does not turn a line round, so the offset started out running the
                // way its source does. Still doing so, with length left, is the whole test.
                return Vec2d.Dot(line.End - line.Start, source.Direction) > Tolerance.LinearResolution;

            case SketchArc arc when step.Source is SketchArc source:
            {
                double atStart = Signed(arc.StartAngle - source.StartAngle);
                double atEnd = Signed(arc.EndAngle - source.EndAngle);
                double sweep = source.Sweep + atEnd - atStart;

                return sweep > Tolerance.AngularResolution && sweep < FullTurn;
            }

            default:
                return false;
        }
    }

    /// <summary>Orders a selection into one chain, or reports that it is not one.</summary>
    private static ImmutableArray<Step>? Order(ImmutableArray<SketchEntity> entities)
    {
        (Vec2d Start, Vec2d End)[] ends =
            [.. entities.Select(e => (e.PointAt(0), e.PointAt(1)))];

        // Nodes are endpoint positions that coincide. Quadratic in the selection, which is the
        // right trade at the size a person selects by hand and keeps the rule -- "these two ends
        // are in the same place" -- the same one every other tool here uses.
        List<Vec2d> nodes = [];
        int[,] node = new int[entities.Length, 2];

        for (int i = 0; i < entities.Length; i++)
        {
            node[i, 0] = NodeFor(nodes, ends[i].Start);
            node[i, 1] = NodeFor(nodes, ends[i].End);
        }

        int[] degree = new int[nodes.Count];

        for (int i = 0; i < entities.Length; i++)
        {
            degree[node[i, 0]]++;
            degree[node[i, 1]]++;
        }

        if (degree.Any(d => d > 2))
        {
            return null;
        }

        int[] loose = [.. Enumerable.Range(0, nodes.Count).Where(n => degree[n] == 1)];

        if (loose.Length is not (0 or 2))
        {
            return null;
        }

        int at = loose.Length == 2 ? loose[0] : node[0, 0];
        bool[] used = new bool[entities.Length];
        ImmutableArray<Step> chain = [];

        for (int step = 0; step < entities.Length; step++)
        {
            int next = -1;
            bool reversed = false;

            for (int i = 0; i < entities.Length && next < 0; i++)
            {
                if (used[i])
                {
                    continue;
                }

                if (node[i, 0] == at)
                {
                    (next, reversed) = (i, false);
                }
                else if (node[i, 1] == at)
                {
                    (next, reversed) = (i, true);
                }
            }

            if (next < 0)
            {
                return null;
            }

            used[next] = true;
            chain = chain.Add(new Step(entities[next], reversed));
            at = reversed ? node[next, 0] : node[next, 1];
        }

        // Which loose end the walk happened to start from is an accident of the order the caller
        // listed things in, and letting that decide the chain's direction would let it decide which
        // side a positive distance lands on -- the same selection, reordered, silently offsetting
        // the other way. So the direction is pinned to something the caller can see: the chain runs
        // through the entity named first in its own Start-to-End direction.
        return chain.First(s => s.Source.Id == entities[0].Id).Reversed
            ? [.. chain.Reverse().Select(s => s with { Reversed = !s.Reversed })]
            : chain;
    }

    private static int NodeFor(List<Vec2d> nodes, Vec2d point)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            if (Meets(nodes[i], point))
            {
                return i;
            }
        }

        nodes.Add(point);

        return nodes.Count - 1;
    }

    private static ImmutableArray<Vec2d> Crossings(SketchEntity first, SketchEntity second)
        => (first, second) switch
        {
            (SketchLine a, SketchLine b) => LineLine(a, b),
            (SketchLine a, SketchArc b) => LineCircle(a, b.Centre, b.Radius),
            (SketchArc a, SketchLine b) => LineCircle(b, a.Centre, a.Radius),
            (SketchArc a, SketchArc b) => CircleCircle(a.Centre, a.Radius, b.Centre, b.Radius),
            _ => [],
        };

    /// <summary>
    /// Where two offsets cross, both taken as unbounded.
    /// </summary>
    /// <remarks>
    /// Unbounded on both sides, unlike <see cref="SketchExtend"/>, which bounds the curve it is
    /// reaching for: here neither piece is finished yet, and the join is precisely the place where
    /// neither of them currently reaches.
    /// </remarks>
    private static ImmutableArray<Vec2d> LineLine(SketchLine first, SketchLine second)
    {
        double denominator = Vec2d.Cross(first.Direction, second.Direction);

        if (System.Math.Abs(denominator) <= Tolerance.Linear)
        {
            return [];
        }

        double t = Vec2d.Cross(second.Start - first.Start, second.Direction) / denominator;

        return [first.Start + (first.Direction * t)];
    }

    private static ImmutableArray<Vec2d> LineCircle(SketchLine line, Vec2d centre, double radius)
    {
        double along = Vec2d.Dot(centre - line.Start, line.Direction);
        Vec2d closest = line.Start + (line.Direction * along);
        double away = (closest - centre).Length;

        if (away > radius + Tolerance.Linear)
        {
            return [];
        }

        double half = System.Math.Sqrt(System.Math.Max(0, (radius * radius) - (away * away)));

        return half <= Tolerance.LinearResolution
            ? [closest]
            : [closest - (line.Direction * half), closest + (line.Direction * half)];
    }

    private static ImmutableArray<Vec2d> CircleCircle(
        Vec2d first, double firstRadius, Vec2d second, double secondRadius)
    {
        Vec2d between = second - first;
        double apart = between.Length;

        if (apart <= Tolerance.Linear
            || apart > firstRadius + secondRadius + Tolerance.Linear
            || apart < System.Math.Abs(firstRadius - secondRadius) - Tolerance.Linear)
        {
            return [];
        }

        double along = ((firstRadius * firstRadius) - (secondRadius * secondRadius) + (apart * apart))
            / (2 * apart);
        double across = System.Math.Sqrt(
            System.Math.Max(0, (firstRadius * firstRadius) - (along * along)));
        Vec2d foot = first + (between * (along / apart));

        if (across <= Tolerance.LinearResolution)
        {
            return [foot];
        }

        Vec2d sideways = between.Perpendicular() * (across / apart);

        return [foot - sideways, foot + sideways];
    }

    private static Vec2d At(SketchEntity entity, EntityPoint point) => entity switch
    {
        SketchLine line => point == EntityPoint.Start ? line.Start : line.End,
        SketchArc arc => point == EntityPoint.Start ? arc.PointAt(0) : arc.PointAt(1),
        _ => Vec2d.Zero,
    };

    private static SketchEntity Moved(SketchEntity entity, EntityPoint point, Vec2d to) => entity switch
    {
        SketchLine line => point == EntityPoint.Start
            ? line with { Start = to }
            : line with { End = to },

        SketchArc arc => point == EntityPoint.Start
            ? arc with { StartAngle = (to - arc.Centre).Angle() }
            : arc with { EndAngle = (to - arc.Centre).Angle() },

        _ => entity,
    };

    private static bool Meets(Vec2d first, Vec2d second)
        => Vec2d.DistanceSquared(first, second) <= Tolerance.Linear * Tolerance.Linear;

    private static Vec2d Exit(Step step)
        => step.Reversed ? step.Source.PointAt(0) : step.Source.PointAt(1);

    private static Vec2d Entry(Step step)
        => step.Reversed ? step.Source.PointAt(1) : step.Source.PointAt(0);

    /// <summary>An angle brought into the half turn either side of zero.</summary>
    private static double Signed(double angle)
    {
        double wrapped = angle % FullTurn;

        return wrapped > System.Math.PI
            ? wrapped - FullTurn
            : wrapped <= -System.Math.PI ? wrapped + FullTurn : wrapped;
    }
}
