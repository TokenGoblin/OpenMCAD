using System.Collections.Immutable;

using OpenMCAD.Math;

namespace OpenMCAD.Solver.Sketching;

/// <summary>How blending a corner came to.</summary>
public enum CornerOutcome
{
    /// <summary>The corner was replaced by a blend.</summary>
    Resolved,

    /// <summary>There is no such entity in the sketch.</summary>
    EntityNotFound,

    /// <summary>This build does not blend a corner between entities of this kind.</summary>
    Unsupported,

    /// <summary>The two entities do not meet at an angle, so there is no corner to blend.</summary>
    NotACorner,

    /// <summary>The blend asked for is bigger than one of the two legs can give up.</summary>
    DoesNotFit,

    /// <summary>
    /// A constraint names one of the corner points in a way that blending would quietly falsify.
    /// </summary>
    ConstraintNotTransferable,
}

/// <summary>What blending a corner came to.</summary>
/// <param name="Outcome">How it turned out.</param>
/// <param name="Sketch">The result, when resolved.</param>
/// <param name="Blend">The id of the arc or line put in the corner, when resolved.</param>
/// <param name="Reason">Why, in words, when it could not be done.</param>
public sealed record CornerResult(
    CornerOutcome Outcome,
    Sketch? Sketch = null,
    SketchEntityId? Blend = null,
    string? Reason = null)
{
    /// <summary>Gets whether the corner was blended.</summary>
    public bool IsResolved => Outcome == CornerOutcome.Resolved;

    /// <summary>Creates a result that blended a corner.</summary>
    /// <param name="sketch">The result.</param>
    /// <param name="blend">The arc or line put in the corner.</param>
    /// <returns>The result.</returns>
    public static CornerResult Found(Sketch sketch, SketchEntityId blend)
        => new(CornerOutcome.Resolved, sketch, blend);

    /// <summary>Creates a result that failed.</summary>
    /// <param name="outcome">How it failed. Must not be <see cref="CornerOutcome.Resolved"/>.</param>
    /// <param name="reason">Why, in words.</param>
    /// <returns>The result.</returns>
    public static CornerResult Failed(CornerOutcome outcome, string reason) => new(outcome, Reason: reason);
}

/// <summary>Which entity a corner tool was pointed at, and roughly where.</summary>
/// <param name="Entity">Which entity.</param>
/// <param name="Near">
/// Roughly where the user clicked on it. Decides which side of the corner survives, which is a real
/// question rather than a nicety: two lines that cross rather than merely meet make four corners,
/// and only the click says which one was meant. Need not be exactly on the entity.
/// </param>
public readonly record struct CornerPick(SketchEntityId Entity, Vec2d Near);

/// <summary>
/// Replaces the corner between two lines with a blend: an arc tangent to both (fillet) or a
/// straight cut across it (chamfer) (P4-T13).
/// </summary>
/// <remarks>
/// <para>
/// <b>Fillet and chamfer are one operation with two blends.</b> Everything hard about either is
/// finding the corner — where the two lines' infinite extensions meet, which side of that point
/// each click asked to keep, which endpoint therefore moves, and whether the leg is long enough to
/// give up what the blend needs. Only the last step differs: an arc of the given radius tangent to
/// both legs, or a line straight across at the given setback. Writing them as two tools that share
/// nothing would be two chances to disagree about which corner the user pointed at.
/// </para>
/// <para>
/// <b>Lines only.</b> The line–line case is exact and closed-form: the tangent points sit at
/// <c>r / tan(θ/2)</c> from the corner along each leg and the centre at <c>r / sin(θ/2)</c> along
/// the bisector, with no case analysis at all once the corner is known. A leg that is an arc is a
/// different problem — the blend's centre is then an intersection of <em>offset</em> curves, which
/// exists in up to four places depending on whether the blend is meant to sit inside or outside
/// each arc, and choosing among them is a judgement about what was meant that a click alone does
/// not settle. That is real work with its own decisions to make, reported as
/// <see cref="CornerOutcome.Unsupported"/> rather than guessed at — the same choice
/// <see cref="SketchExtend"/> made about arcs, and for the same reason.
/// </para>
/// <para>
/// <b>The corner coincidence is removed, and that is the operation rather than a side effect.</b> A
/// corner drawn the usual way carries a <see cref="ConstraintKind.Coincident"/> saying the two ends
/// are in the same place. After a blend they demonstrably are not: the blend is exactly what now
/// lies between them. Keeping it would leave a sketch that contradicts itself the next time it is
/// solved, so it goes, and the two coincidences onto the blend's own ends replace it — which is what
/// keeps the corner a corner when something is dragged, rather than three pieces of geometry that
/// merely happen to touch today.
/// </para>
/// <para>
/// <b>Any other constraint on a moving end refuses the whole thing.</b> A <c>Fix</c> on the corner
/// point, or a distance measured to it, is a statement about a point the blend is about to move
/// somewhere nobody asked for. <see cref="CornerOutcome.ConstraintNotTransferable"/> says so, on the
/// same principle as <see cref="SketchSplit"/>'s refusal over a midpoint: a constraint that quietly
/// starts measuring to the wrong place is worse than an operation that declines. Constraints naming
/// a whole leg — that it is horizontal, or parallel to something — are untouched and stay true, a
/// shortened line being no less horizontal than a long one.
/// </para>
/// <para>
/// <b>A fillet adds both tangencies, and they are not redundant.</b> Naive counting looks alarming —
/// an arc brings five parameters and the four new constraints remove six — but the two coincidences
/// pin only the arc's ends, and a circle through two given points is still a one-parameter family;
/// one tangency uses that up and the other is a fact about the far end that nothing has yet said.
/// The equations are independent wherever the geometry is not already degenerate, which is what
/// P4-T06's rank analysis is there to establish rather than something to take on trust from a count.
/// </para>
/// </remarks>
public static class SketchCorner
{
    private const double FullTurn = 2 * System.Math.PI;

    /// <summary>Rounds a corner off with an arc tangent to both legs.</summary>
    /// <param name="sketch">The sketch.</param>
    /// <param name="first">One leg, and where it was clicked.</param>
    /// <param name="second">The other leg, and where it was clicked.</param>
    /// <param name="radius">How big the fillet is. Must be positive.</param>
    /// <returns>The result.</returns>
    public static CornerResult Fillet(Sketch sketch, CornerPick first, CornerPick second, double radius)
    {
        ArgumentNullException.ThrowIfNull(sketch);

        if (!double.IsFinite(radius) || radius <= Tolerance.LinearResolution)
        {
            return CornerResult.Failed(CornerOutcome.NotACorner, "A fillet needs a positive radius.");
        }

        if (Prepare(sketch, first, second) is not { } corner)
        {
            return Refuse(sketch, first, second);
        }

        // The tangent length: how far from the corner an inscribed circle of this radius touches
        // each leg. Standard, and the whole of the line--line case's geometry.
        double setback = radius / System.Math.Tan(corner.Angle / 2);

        if (Fit(corner, setback) is { } tooBig)
        {
            return tooBig;
        }

        (Vec2d onFirst, Vec2d onSecond) = corner.Touch(setback);
        Vec2d bisector = (corner.First.Direction + corner.Second.Direction).Normalized();
        Vec2d centre = corner.Point + (bisector * (radius / System.Math.Sin(corner.Angle / 2)));

        double angleToFirst = (onFirst - centre).Angle();
        double angleToSecond = (onSecond - centre).Angle();

        // A fillet sweeps exactly pi - theta, so it is always the minor arc. Whichever of the two
        // orderings sweeps less than half a turn anticlockwise is therefore the right one, and
        // SketchArc admits no other direction in which to say it (P4-T03).
        bool firstLeads = Wrap(angleToSecond - angleToFirst) < System.Math.PI;

        SketchEntityId id = SketchEntityId.New();
        SketchArc arc = firstLeads
            ? new SketchArc(id, centre, radius, angleToFirst, angleToSecond, corner.IsConstruction)
            : new SketchArc(id, centre, radius, angleToSecond, angleToFirst, corner.IsConstruction);

        SketchPointRef metFirst = new(id, firstLeads ? EntityPoint.Start : EntityPoint.End);
        SketchPointRef metSecond = new(id, firstLeads ? EntityPoint.End : EntityPoint.Start);

        return Rebuild(sketch, corner, onFirst, onSecond, arc, metFirst, metSecond, tangent: true);
    }

    /// <summary>Cuts a corner off with a straight line across it.</summary>
    /// <param name="sketch">The sketch.</param>
    /// <param name="first">One leg, and where it was clicked.</param>
    /// <param name="second">The other leg, and where it was clicked.</param>
    /// <param name="setback">
    /// How far back from the corner the cut starts, along each leg. Must be positive.
    /// </param>
    /// <returns>The result.</returns>
    /// <remarks>
    /// An equal-leg chamfer, which is what a chamfer tool offers first and what nearly every one
    /// actually drawn is. The distance–distance and distance–angle variants are two more numbers
    /// about the same corner rather than a different operation, and are left until something asks
    /// for them.
    /// </remarks>
    public static CornerResult Chamfer(Sketch sketch, CornerPick first, CornerPick second, double setback)
    {
        ArgumentNullException.ThrowIfNull(sketch);

        if (!double.IsFinite(setback) || setback <= Tolerance.LinearResolution)
        {
            return CornerResult.Failed(CornerOutcome.NotACorner, "A chamfer needs a positive setback.");
        }

        if (Prepare(sketch, first, second) is not { } corner)
        {
            return Refuse(sketch, first, second);
        }

        if (Fit(corner, setback) is { } tooBig)
        {
            return tooBig;
        }

        (Vec2d onFirst, Vec2d onSecond) = corner.Touch(setback);

        SketchEntityId id = SketchEntityId.New();
        SketchLine cut = new(id, onFirst, onSecond, corner.IsConstruction);

        return Rebuild(
            sketch,
            corner,
            onFirst,
            onSecond,
            cut,
            new SketchPointRef(id, EntityPoint.Start),
            new SketchPointRef(id, EntityPoint.End),
            tangent: false);
    }

    /// <summary>One leg of a corner: the line, which way it survives, and which end gives way.</summary>
    /// <param name="Line">The leg.</param>
    /// <param name="Direction">
    /// Unit, pointing from the corner along the half of the leg the click asked to keep.
    /// </param>
    /// <param name="Moving">Which of the leg's ends the blend takes over.</param>
    /// <param name="Reach">
    /// How far the surviving end sits from the corner along <see cref="Direction"/>. Signed:
    /// negative means the whole leg lies on the discarded side, which no blend can fit into.
    /// </param>
    private readonly record struct Leg(
        SketchLine Line, Vec2d Direction, EntityPoint Moving, double Reach)
    {
        /// <summary>Gets the point of the leg that the blend takes over.</summary>
        public SketchPointRef MovingPoint => new(Line.Id, Moving);

        /// <summary>The leg with its moving end brought to where the blend meets it.</summary>
        public SketchLine MovedTo(Vec2d point) => Moving == EntityPoint.Start
            ? Line with { Start = point }
            : Line with { End = point };
    }

    /// <summary>A corner, once both legs and the point they make have been worked out.</summary>
    private readonly record struct Corner(Leg First, Leg Second, Vec2d Point, double Angle)
    {
        /// <summary>Gets whether the blend is scaffolding, which it is only if both legs are.</summary>
        public bool IsConstruction => First.Line.IsConstruction && Second.Line.IsConstruction;

        /// <summary>Where a blend of this setback touches each leg.</summary>
        public (Vec2d OnFirst, Vec2d OnSecond) Touch(double setback)
            => (Point + (First.Direction * setback), Point + (Second.Direction * setback));
    }

    private static Corner? Prepare(Sketch sketch, CornerPick first, CornerPick second)
    {
        if (first.Entity == second.Entity
            || sketch.Entities.Find(first.Entity) is not SketchLine a
            || sketch.Entities.Find(second.Entity) is not SketchLine b
            || a.Length <= Tolerance.LinearResolution
            || b.Length <= Tolerance.LinearResolution)
        {
            return null;
        }

        double denominator = Vec2d.Cross(a.Direction, b.Direction);

        // Parallel, or antiparallel, and either way there is nowhere the two meet. This one test
        // covers both, which is why the angle below can be taken as a genuine interior angle
        // without a second guard against it being zero or straight.
        if (System.Math.Abs(denominator) <= Tolerance.Linear)
        {
            return null;
        }

        Vec2d point = a.Start + (a.Direction * (Vec2d.Cross(b.Start - a.Start, b.Direction) / denominator));

        Leg legA = LegOf(a, point, first.Near);
        Leg legB = LegOf(b, point, second.Near);
        double angle = System.Math.Acos(
            Tolerance.Clamp(Vec2d.Dot(legA.Direction, legB.Direction), -1, 1));

        return new Corner(legA, legB, point, angle);
    }

    private static Leg LegOf(SketchLine line, Vec2d corner, Vec2d near)
    {
        double clicked = Vec2d.Dot(near - corner, line.Direction);
        double atStart = Vec2d.Dot(line.Start - corner, line.Direction);
        double atEnd = Vec2d.Dot(line.End - corner, line.Direction);

        // A click on the corner itself says nothing about which side was meant, so the leg answers
        // for itself: the side with more of the line on it.
        double side = System.Math.Abs(clicked) > Tolerance.Linear
            ? clicked
            : System.Math.Abs(atEnd) >= System.Math.Abs(atStart) ? atEnd : atStart;

        Vec2d direction = side >= 0 ? line.Direction : -line.Direction;
        double fromStart = side >= 0 ? atStart : -atStart;
        double fromEnd = side >= 0 ? atEnd : -atEnd;

        // The end nearer the corner along the surviving direction is the one the blend takes over;
        // the other is what is left to blend into. Nothing here requires the leg to reach the
        // corner at all — a leg that stops short is extended out to its tangent point, which is
        // what a corner tool is expected to do with two lines that nearly meet.
        return fromStart <= fromEnd
            ? new Leg(line, direction, EntityPoint.Start, fromEnd)
            : new Leg(line, direction, EntityPoint.End, fromStart);
    }

    private static CornerResult? Fit(Corner corner, double setback)
    {
        if (corner.First.Reach > setback + Tolerance.LinearResolution
            && corner.Second.Reach > setback + Tolerance.LinearResolution)
        {
            return null;
        }

        return CornerResult.Failed(
            CornerOutcome.DoesNotFit,
            $"A blend set back {setback} from this corner needs more line than one of the two legs "
            + "has, so it would consume the whole of it.");
    }

    private static CornerResult Rebuild(
        Sketch sketch,
        Corner corner,
        Vec2d onFirst,
        Vec2d onSecond,
        SketchEntity blend,
        SketchPointRef metFirst,
        SketchPointRef metSecond,
        bool tangent)
    {
        SketchPointRef movingFirst = corner.First.MovingPoint;
        SketchPointRef movingSecond = corner.Second.MovingPoint;

        ImmutableArray<SketchConstraintId> removing = [];

        foreach (SketchConstraint constraint in sketch.Constraints.Ordered)
        {
            if (!constraint.On.Any(o => o == movingFirst || o == movingSecond))
            {
                continue;
            }

            bool isTheCorner = constraint.Kind == ConstraintKind.Coincident
                && constraint.On.Length == 2
                && constraint.On.Contains(movingFirst)
                && constraint.On.Contains(movingSecond);

            if (!isTheCorner)
            {
                return CornerResult.Failed(
                    CornerOutcome.ConstraintNotTransferable,
                    $"'{constraint.Kind}' names a corner point that blending moves, and where it "
                    + "should follow to is not decidable.");
            }

            removing = removing.Add(constraint.Id);
        }

        Sketch blended = sketch
            .With(corner.First.MovedTo(onFirst))
            .With(corner.Second.MovedTo(onSecond))
            .With(blend);

        blended = removing.Aggregate(blended, (s, id) => s.Without(id));

        blended = blended
            .With(SketchConstraint.Of(ConstraintKind.Coincident, [movingFirst, metFirst]))
            .With(SketchConstraint.Of(ConstraintKind.Coincident, [movingSecond, metSecond]));

        if (tangent)
        {
            blended = blended
                .With(SketchConstraint.Of(
                    ConstraintKind.Tangent, [new SketchPointRef(corner.First.Line.Id), new(blend.Id)]))
                .With(SketchConstraint.Of(
                    ConstraintKind.Tangent, [new SketchPointRef(corner.Second.Line.Id), new(blend.Id)]));
        }

        return CornerResult.Found(blended, blend.Id);
    }

    /// <summary>Why <see cref="Prepare"/> gave up, said in the caller's vocabulary.</summary>
    private static CornerResult Refuse(Sketch sketch, CornerPick first, CornerPick second)
    {
        foreach (CornerPick pick in new[] { first, second })
        {
            if (sketch.Entities.Find(pick.Entity) is not { } entity)
            {
                return CornerResult.Failed(
                    CornerOutcome.EntityNotFound, $"There is no entity to blend with id {pick.Entity}.");
            }

            if (entity is not SketchLine)
            {
                return CornerResult.Failed(
                    CornerOutcome.Unsupported,
                    $"This build cannot blend a corner against a {entity.Kind}.");
            }
        }

        return first.Entity == second.Entity
            ? CornerResult.Failed(CornerOutcome.NotACorner, "An entity makes no corner with itself.")
            : CornerResult.Failed(
                CornerOutcome.NotACorner,
                "These two lines are parallel, or one of them has no length, so they make no corner.");
    }

    private static double Wrap(double angle)
    {
        double wrapped = angle % FullTurn;

        return wrapped < 0 ? wrapped + FullTurn : wrapped;
    }
}
