using System.Collections.Immutable;

using OpenMCAD.Math;

namespace OpenMCAD.Solver.Sketching;

/// <summary>How extending a line came to.</summary>
public enum ExtendOutcome
{
    /// <summary>The line was lengthened.</summary>
    Resolved,

    /// <summary>There is no such entity in the sketch.</summary>
    EntityNotFound,

    /// <summary>This build does not extend this kind of entity.</summary>
    Unsupported,

    /// <summary>Nothing in the sketch lies ahead of the end being extended.</summary>
    NoIntersections,
}

/// <summary>What extending a line came to.</summary>
/// <param name="Outcome">How it turned out.</param>
/// <param name="Sketch">The result, when resolved.</param>
/// <param name="Reason">Why, in words, when it could not be done.</param>
public sealed record ExtendResult(ExtendOutcome Outcome, Sketch? Sketch = null, string? Reason = null)
{
    /// <summary>Gets whether the extend produced a sketch.</summary>
    public bool IsResolved => Outcome == ExtendOutcome.Resolved;

    /// <summary>Creates a result that produced a sketch.</summary>
    /// <param name="sketch">The result.</param>
    /// <returns>The result.</returns>
    public static ExtendResult Found(Sketch sketch) => new(ExtendOutcome.Resolved, sketch);

    /// <summary>Creates a result that failed.</summary>
    /// <param name="outcome">How it failed. Must not be <see cref="ExtendOutcome.Resolved"/>.</param>
    /// <param name="reason">Why, in words.</param>
    /// <returns>The result.</returns>
    public static ExtendResult Failed(ExtendOutcome outcome, string reason) => new(outcome, null, reason);
}

/// <summary>
/// Lengthens the end of a line nearest a click until it reaches the rest of the sketch (P4-T13).
/// </summary>
/// <remarks>
/// <para>
/// <b>Lines only.</b> <see cref="SketchTrim"/> works on line, circle and arc because shortening any
/// of them is the same question — where does the crossing already on the curve fall relative to the
/// click — answered with the curve's own bounded parameterisation. Extending is a different
/// question: where would a curve <em>not yet there</em> meet something, which for a line is a
/// second, unbounded pass through the same intersection formulas <c>SketchSnapping</c> already
/// proved, and for a circle or an arc is not obviously even the right question — lengthening an arc
/// by growing its sweep changes its radius nowhere, but changes what it looks like everywhere, and
/// nothing here is confident yet about which end a click should be taken to mean when the curve
/// bends back over itself. Left for when that is worked out, and reported as
/// <see cref="ExtendOutcome.Unsupported"/> rather than guessed at.
/// </para>
/// <para>
/// <b>The intersection maths mirrors <c>SketchSnapping</c>'s <c>LineLine</c> and <c>LineCircle</c>
/// deliberately</b> rather than reusing them: those bound <em>both</em> curves to what is actually
/// drawn, which is exactly backwards for this — the whole point of extending is that one of the two
/// curves is not there yet. Rewritten here with only the target bounded, expressed as a signed
/// distance along the extending line's own direction so that every candidate, line or circle or arc,
/// competes on the same scale for "nearest".
/// </para>
/// </remarks>
public static class SketchExtend
{
    /// <summary>Extends a line.</summary>
    /// <param name="sketch">The sketch.</param>
    /// <param name="id">Which line to extend.</param>
    /// <param name="near">
    /// Roughly where the user clicked, which decides which end is extended — whichever end
    /// <paramref name="near"/> is closer to. Need not be exactly on the line.
    /// </param>
    /// <returns>The result.</returns>
    public static ExtendResult Extend(Sketch sketch, SketchEntityId id, Vec2d near)
    {
        ArgumentNullException.ThrowIfNull(sketch);

        if (sketch.Entities.Find(id) is not { } entity)
        {
            return ExtendResult.Failed(
                ExtendOutcome.EntityNotFound, $"There is no entity to extend with id {id}.");
        }

        if (entity is not SketchLine line)
        {
            return ExtendResult.Failed(ExtendOutcome.Unsupported, $"This build cannot extend a {entity.Kind}.");
        }

        if (line.Length <= Tolerance.LinearResolution)
        {
            return ExtendResult.Failed(ExtendOutcome.Unsupported, "This line has no length to extend.");
        }

        bool extendingEnd = Vec2d.Distance(near, line.End) <= Vec2d.Distance(near, line.Start);

        ImmutableArray<double> candidates =
        [
            .. sketch.Entities.Ordered
                .Where(other => other.Id != line.Id)
                .SelectMany(other => Candidates(line, other)),
        ];

        double? reach = extendingEnd
            ? candidates.Where(t => t > line.Length).Cast<double?>().Min()
            : candidates.Where(t => t < 0).Cast<double?>().Max();

        if (reach is not { } t)
        {
            return ExtendResult.Failed(
                ExtendOutcome.NoIntersections,
                $"Nothing in the sketch lies ahead of the {(extendingEnd ? "end" : "start")} of this line.");
        }

        Vec2d reached = line.Start + (line.Direction * t);
        SketchLine extended = extendingEnd ? line with { End = reached } : line with { Start = reached };

        return ExtendResult.Found(sketch.With(extended));
    }

    /// <summary>
    /// Where the infinite line through <paramref name="extending"/> meets <paramref name="target"/>,
    /// as signed distances from <paramref name="extending"/>'s own start along its own direction.
    /// </summary>
    private static ImmutableArray<double> Candidates(SketchLine extending, SketchEntity target)
        => target switch
        {
            SketchLine other => LineLine(extending, other),
            SketchCircle circle => LineCircle(extending, circle.Centre, circle.Radius),
            SketchArc arc => [.. LineCircle(extending, arc.Centre, arc.Radius).Where(t => OnArc(extending, arc, t))],
            _ => [],
        };

    private static ImmutableArray<double> LineLine(SketchLine extending, SketchLine target)
    {
        Vec2d p = extending.Direction;
        Vec2d q = target.End - target.Start;
        double denominator = Vec2d.Cross(p, q);

        if (System.Math.Abs(denominator) <= Tolerance.LinearResolution || target.Length <= Tolerance.LinearResolution)
        {
            return [];
        }

        Vec2d toTarget = target.Start - extending.Start;
        double t = Vec2d.Cross(toTarget, q) / denominator;
        double u = Vec2d.Cross(toTarget, p) / denominator;

        // Bounded on the target's side only -- the target is the real geometry being reached for,
        // and the extending line is exactly the part that is not real yet.
        return u < 0 || u > 1 ? [] : [t];
    }

    private static ImmutableArray<double> LineCircle(SketchLine extending, Vec2d centre, double radius)
    {
        double along = Vec2d.Dot(centre - extending.Start, extending.Direction);
        Vec2d closest = extending.Start + (extending.Direction * along);
        double away = (closest - centre).Length;

        if (away > radius)
        {
            return [];
        }

        double half = System.Math.Sqrt(System.Math.Max(0, (radius * radius) - (away * away)));

        return half <= Tolerance.LinearResolution ? [along] : [along - half, along + half];
    }

    /// <summary>Whether the point an extending line reaches at distance <paramref name="t"/> falls
    /// within an arc's sweep rather than merely on the circle it came from.</summary>
    private static bool OnArc(SketchLine extending, SketchArc arc, double t)
    {
        Vec2d point = extending.Start + (extending.Direction * t);
        double angle = System.Math.Atan2(point.Y - arc.Centre.Y, point.X - arc.Centre.X) - arc.StartAngle;
        double wrapped = angle % (2 * System.Math.PI);

        if (wrapped < 0)
        {
            wrapped += 2 * System.Math.PI;
        }

        return wrapped <= arc.Sweep + Tolerance.AngularResolution;
    }
}
