using System.Collections.Immutable;

using OpenMCAD.Math;

namespace OpenMCAD.Solver.Sketching;

/// <summary>
/// A constraint the sketcher thinks the user meant, and what to draw to say so.
/// </summary>
/// <param name="Constraint">What would be added.</param>
/// <param name="Glyph">
/// A stable name for the mark shown beside the cursor. Named rather than drawn here, because what
/// a coincidence looks like is the UI's business and this layer has no idea how big a pixel is.
/// </param>
/// <param name="At">Where in the sketch to show it.</param>
/// <param name="Priority">How confident this is; larger wins.</param>
/// <param name="Reason">What to say if the user asks why.</param>
public sealed record ConstraintProposal(
    SketchConstraint Constraint,
    string Glyph,
    Vec2d At,
    int Priority,
    string Reason)
{
    /// <inheritdoc/>
    public override string ToString() => $"{Constraint.Kind} ({Reason})";
}

/// <summary>
/// How willing the sketcher is to guess.
/// </summary>
/// <param name="Tolerance">
/// How near counts as near, in sketch units. The caller converts from pixels: inference has to feel
/// the same at every zoom, and a fixed model distance would snap to everything when zoomed out and
/// to nothing when zoomed in.
/// </param>
/// <param name="AngularTolerance">How near an angle counts as being at it, in radians.</param>
/// <param name="Suppressed">
/// Whether the user is holding the modifier that means "leave it alone". Nothing is inferred, and
/// nothing is drawn — the escape hatch every sketcher needs for the moment its guess is wrong.
/// </param>
/// <param name="Limit">
/// How many proposals to offer at once. Small on purpose: a user drawing a line wants to be told
/// one thing, and a cloud of eight glyphs is noise they will learn to ignore.
/// </param>
public sealed record InferenceOptions(
    double Tolerance = 1,
    double AngularTolerance = 0.05,
    bool Suppressed = false,
    int Limit = 3)
{
    /// <summary>Gets the settings a sketcher uses when nothing else is said.</summary>
    public static InferenceOptions Default { get; } = new();
}

/// <summary>
/// Guesses what the user meant while they are drawing.
/// </summary>
/// <remarks>
/// <para>
/// P4-T08, and §5.6's "constraint inference while drawing". This is the part of a sketcher that
/// makes it feel like one: a line drawn nearly horizontal should become horizontal, and an endpoint
/// dropped near another should join it, without anyone having to say so afterwards.
/// </para>
/// <para>
/// Nothing here applies anything. It proposes, in a deterministic order, and the caller decides —
/// which is what lets the same code drive the glyphs shown before the click and the constraints
/// added after it, and lets a test check the guess without a UI.
/// </para>
/// <para>
/// Two rules keep it from being annoying. Nothing already true is proposed, because a sketcher
/// offering to make two things coincident that a constraint already holds together is telling the
/// user it has not been paying attention. And at most one direction constraint is offered for an
/// entity: horizontal, vertical, parallel and perpendicular all say where a line points, and
/// offering two of them at once is offering a contradiction.
/// </para>
/// </remarks>
public static class ConstraintInference
{
    /// <summary>Guesses about a point being placed.</summary>
    /// <param name="sketch">The sketch as it stands.</param>
    /// <param name="placing">Which point of which entity is being placed.</param>
    /// <param name="at">Where it is.</param>
    /// <param name="options">How willing to guess.</param>
    /// <returns>The proposals, best first.</returns>
    public static ImmutableArray<ConstraintProposal> ForPoint(
        Sketch sketch, SketchPointRef placing, Vec2d at, InferenceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sketch);

        InferenceOptions settings = options ?? InferenceOptions.Default;

        if (settings.Suppressed)
        {
            return [];
        }

        List<ConstraintProposal> found = [];

        foreach (SketchEntity entity in sketch.Entities.Ordered)
        {
            if (entity.Id == placing.Entity)
            {
                continue;
            }

            foreach (EntityPoint which in entity.Points)
            {
                if (entity.PointOf(which) is not { } where
                    || (where - at).Length > settings.Tolerance)
                {
                    continue;
                }

                // A named point beats the curve it belongs to. Someone aiming at the end of a line
                // wants the end, not a point that happens to lie on the line near it.
                found.Add(new ConstraintProposal(
                    SketchConstraint.Of(
                        ConstraintKind.Coincident, [placing, new SketchPointRef(entity.Id, which)]),
                    which == EntityPoint.Centre ? "coincident-centre" : "coincident",
                    where,
                    which == EntityPoint.Middle ? 90 : 100,
                    $"on the {Describe(which)} of a {entity.Kind}"));
            }

            if (entity is SketchLine line
                && (line.PointAt(0.5) - at).Length <= settings.Tolerance)
            {
                found.Add(new ConstraintProposal(
                    SketchConstraint.Of(
                        ConstraintKind.Midpoint, [placing, new SketchPointRef(entity.Id)]),
                    "midpoint",
                    line.PointAt(0.5),
                    88,
                    "at the middle of a line"));
            }

            if (entity is not SketchPoint && Near(entity, at) is { } touching
                && touching <= settings.Tolerance)
            {
                found.Add(new ConstraintProposal(
                    SketchConstraint.Of(
                        ConstraintKind.PointOnObject, [placing, new SketchPointRef(entity.Id)]),
                    "point-on-object",
                    at,
                    60,
                    $"on a {entity.Kind}"));
            }
        }

        return Best(sketch, found, settings);
    }

    /// <summary>Guesses about an entity that has just been drawn.</summary>
    /// <param name="sketch">The sketch as it stands, including the new entity.</param>
    /// <param name="drawn">Which entity was drawn.</param>
    /// <param name="options">How willing to guess.</param>
    /// <returns>The proposals, best first.</returns>
    public static ImmutableArray<ConstraintProposal> ForEntity(
        Sketch sketch, SketchEntityId drawn, InferenceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sketch);

        InferenceOptions settings = options ?? InferenceOptions.Default;

        if (settings.Suppressed || sketch.Entities.Find(drawn) is not { } entity)
        {
            return [];
        }

        List<ConstraintProposal> found = [];

        if (entity is SketchLine line && line.Length > Tolerance.LinearResolution)
        {
            Axis(line, drawn, settings, found);
            Against(sketch, line, drawn, settings, found);
        }

        if (entity.PointOf(EntityPoint.Centre) is { } centre && RadiusOf(entity) is { } radius)
        {
            Round(sketch, entity, drawn, centre, radius, settings, found);
        }

        return Best(sketch, found, settings);
    }

    /// <summary>Whether a constraint would say something the sketch already says.</summary>
    /// <param name="sketch">The sketch.</param>
    /// <param name="proposal">What is being proposed.</param>
    /// <returns>Whether it is already there.</returns>
    /// <remarks>
    /// Compared as an unordered pair, because "A is parallel to B" and "B is parallel to A" are the
    /// same sentence and a sketcher that offered the second because it had only looked for the
    /// first would be visibly not paying attention.
    /// </remarks>
    public static bool AlreadySaid(Sketch sketch, SketchConstraint proposal)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        ArgumentNullException.ThrowIfNull(proposal);

        foreach (SketchConstraint existing in sketch.Constraints.Ordered)
        {
            if (existing.Kind == proposal.Kind
                && existing.On.Length == proposal.On.Length
                && existing.On.All(proposal.On.Contains))
            {
                return true;
            }
        }

        return false;
    }

    private static void Axis(
        SketchLine line,
        SketchEntityId drawn,
        InferenceOptions settings,
        List<ConstraintProposal> found)
    {
        Vec2d along = line.Direction;

        if (System.Math.Abs(along.Y) <= System.Math.Sin(settings.AngularTolerance))
        {
            found.Add(new ConstraintProposal(
                SketchConstraint.Of(ConstraintKind.Horizontal, [new SketchPointRef(drawn)]),
                "horizontal",
                line.PointAt(0.5),
                95,
                "nearly horizontal"));
        }

        if (System.Math.Abs(along.X) <= System.Math.Sin(settings.AngularTolerance))
        {
            found.Add(new ConstraintProposal(
                SketchConstraint.Of(ConstraintKind.Vertical, [new SketchPointRef(drawn)]),
                "vertical",
                line.PointAt(0.5),
                95,
                "nearly vertical"));
        }
    }

    private static void Against(
        Sketch sketch,
        SketchLine line,
        SketchEntityId drawn,
        InferenceOptions settings,
        List<ConstraintProposal> found)
    {
        foreach (SketchEntity other in sketch.Entities.Ordered)
        {
            if (other.Id == drawn
                || other is not SketchLine second
                || second.Length <= Tolerance.LinearResolution)
            {
                continue;
            }

            double cross = System.Math.Abs(Vec2d.Cross(line.Direction, second.Direction));
            double dot = System.Math.Abs(Vec2d.Dot(line.Direction, second.Direction));
            double near = System.Math.Sin(settings.AngularTolerance);

            if (cross <= near)
            {
                found.Add(new ConstraintProposal(
                    SketchConstraint.Of(
                        ConstraintKind.Parallel,
                        [new SketchPointRef(drawn), new SketchPointRef(other.Id)]),
                    "parallel",
                    line.PointAt(0.5),
                    70,
                    "nearly parallel to another line"));
            }
            else if (dot <= near)
            {
                found.Add(new ConstraintProposal(
                    SketchConstraint.Of(
                        ConstraintKind.Perpendicular,
                        [new SketchPointRef(drawn), new SketchPointRef(other.Id)]),
                    "perpendicular",
                    line.PointAt(0.5),
                    72,
                    "nearly square to another line"));
            }

            if (System.Math.Abs(line.Length - second.Length) <= settings.Tolerance)
            {
                found.Add(new ConstraintProposal(
                    SketchConstraint.Of(
                        ConstraintKind.Equal,
                        [new SketchPointRef(drawn), new SketchPointRef(other.Id)]),
                    "equal",
                    line.PointAt(0.5),
                    40,
                    "nearly the same length as another line"));
            }
        }

        foreach (SketchEntity other in sketch.Entities.Ordered)
        {
            if (other.Id == drawn
                || other.PointOf(EntityPoint.Centre) is not { } centre
                || RadiusOf(other) is not { } radius)
            {
                continue;
            }

            double away = System.Math.Abs(
                Vec2d.Cross(line.Direction, centre - line.Start));

            if (System.Math.Abs(away - radius) <= settings.Tolerance)
            {
                found.Add(new ConstraintProposal(
                    SketchConstraint.Of(
                        ConstraintKind.Tangent,
                        [new SketchPointRef(drawn), new SketchPointRef(other.Id)]),
                    "tangent",
                    line.PointAt(0.5),
                    65,
                    $"nearly touching a {other.Kind}"));
            }
        }
    }

    private static void Round(
        Sketch sketch,
        SketchEntity entity,
        SketchEntityId drawn,
        Vec2d centre,
        double radius,
        InferenceOptions settings,
        List<ConstraintProposal> found)
    {
        foreach (SketchEntity other in sketch.Entities.Ordered)
        {
            if (other.Id == drawn
                || other.PointOf(EntityPoint.Centre) is not { } theirs
                || RadiusOf(other) is not { } theirRadius)
            {
                continue;
            }

            if ((centre - theirs).Length <= settings.Tolerance)
            {
                found.Add(new ConstraintProposal(
                    SketchConstraint.Of(
                        ConstraintKind.Concentric,
                        [new SketchPointRef(drawn), new SketchPointRef(other.Id)]),
                    "concentric",
                    centre,
                    85,
                    $"nearly sharing a centre with a {other.Kind}"));
            }

            if (System.Math.Abs(radius - theirRadius) <= settings.Tolerance)
            {
                found.Add(new ConstraintProposal(
                    SketchConstraint.Of(
                        ConstraintKind.Equal,
                        [new SketchPointRef(drawn), new SketchPointRef(other.Id)]),
                    "equal",
                    centre,
                    40,
                    $"nearly the same size as a {other.Kind}"));
            }
        }

        _ = entity;
    }

    /// <summary>Picks the proposals worth showing, in a fixed order.</summary>
    /// <remarks>
    /// Sorted by confidence, and ties broken by where the earliest entity mentioned sits in the
    /// sketch — never by an id, whose value is random and would offer the same user a different
    /// guess on the next run. A sketcher that guessed differently each time would be one nobody
    /// could learn.
    /// </remarks>
    /// <remarks>
    /// The earliest and not the latest: the entity just drawn is by definition last in the sketch,
    /// so a tie-break on the latest is the same number for every proposal about it and breaks
    /// nothing at all.
    /// </remarks>
    private static ImmutableArray<ConstraintProposal> Best(
        Sketch sketch, List<ConstraintProposal> found, InferenceOptions settings)
    {
        ImmutableArray<SketchEntityId> order =
            [.. sketch.Entities.Ordered.Select(e => e.Id)];

        List<ConstraintProposal> kept = [];
        bool tookDirection = false;

        foreach (ConstraintProposal proposal in found
            .Where(p => !AlreadySaid(sketch, p.Constraint))
            .OrderByDescending(p => p.Priority)
            .ThenBy(p => p.Constraint.On.Select(o => order.IndexOf(o.Entity))
                .DefaultIfEmpty(0).Min())
            .ThenBy(p => p.Constraint.Kind))
        {
            // Horizontal, vertical, parallel and perpendicular all say where a line points, and
            // offering two at once is offering a contradiction.
            bool direction = proposal.Constraint.Kind
                is ConstraintKind.Horizontal
                or ConstraintKind.Vertical
                or ConstraintKind.Parallel
                or ConstraintKind.Perpendicular;

            if (direction && tookDirection)
            {
                continue;
            }

            tookDirection |= direction;
            kept.Add(proposal);

            if (kept.Count >= settings.Limit)
            {
                break;
            }
        }

        return [.. kept];
    }

    /// <summary>How far a position is from a curve, or null if the curve has no distance to give.</summary>
    /// <remarks>
    /// Measured to the curve as drawn, not to the infinite one it lies on. A point dropped past the
    /// end of a segment sits on the line through it at a distance of zero, and a sketcher that
    /// offered "on this line" for a point plainly beyond its end would be offering something the
    /// user can see is wrong. Arcs are the same story in angle: a point on the circle but outside
    /// the sweep is not on the arc.
    /// </remarks>
    private static double? Near(SketchEntity entity, Vec2d at)
    {
        switch (entity)
        {
            case SketchLine line when line.Length > Tolerance.LinearResolution:
                double along = Vec2d.Dot(at - line.Start, line.Direction);

                return along < 0 || along > line.Length
                    ? null
                    : System.Math.Abs(Vec2d.Cross(line.Direction, at - line.Start));

            case SketchCircle circle:
                return System.Math.Abs((at - circle.Centre).Length - circle.Radius);

            case SketchArc arc:
                Vec2d out_ = at - arc.Centre;

                if (out_.IsZeroLength)
                {
                    return null;
                }

                double angle = System.Math.Atan2(out_.Y, out_.X) - arc.StartAngle;

                while (angle < 0)
                {
                    angle += 2 * System.Math.PI;
                }

                return angle > arc.Sweep
                    ? null
                    : System.Math.Abs(out_.Length - arc.Radius);

            default:
                return null;
        }
    }

    private static double? RadiusOf(SketchEntity entity) => entity switch
    {
        SketchCircle circle => circle.Radius,
        SketchArc arc => arc.Radius,
        _ => null,
    };

    private static string Describe(EntityPoint point) => point switch
    {
        EntityPoint.Self => "position",
        EntityPoint.Start => "start",
        EntityPoint.End => "end",
        EntityPoint.Centre => "centre",
        EntityPoint.Focus => "focus",
        EntityPoint.SecondFocus => "second focus",
        _ => "middle",
    };
}
