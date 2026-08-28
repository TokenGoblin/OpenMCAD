using System.Collections.Immutable;

using OpenMCAD.Math;

namespace OpenMCAD.Solver.Sketching;

/// <summary>What sort of thing the cursor caught on.</summary>
/// <remarks>
/// Ordered by how much it means. A named point of real geometry is what the user almost certainly
/// aimed at; a grid intersection is what they get when they aimed at nothing. The order of this
/// enum is the order of preference, and nothing else decides it.
/// </remarks>
public enum SnapKind
{
    /// <summary>Nothing was found; the cursor is where it was.</summary>
    None,

    /// <summary>The nearest point of a background grid.</summary>
    Grid,

    /// <summary>A line's own direction, or a square to it, through where drawing began.</summary>
    Guide,

    /// <summary>The straight continuation of a line beyond its end.</summary>
    Extension,

    /// <summary>Somewhere along a curve.</summary>
    OnCurve,

    /// <summary>The top, bottom, left or right of a circle or arc.</summary>
    Quadrant,

    /// <summary>The middle of a line.</summary>
    Midpoint,

    /// <summary>Where two curves cross.</summary>
    Intersection,

    /// <summary>A point the geometry actually has: an end, a centre, a point entity.</summary>
    Point,
}

/// <summary>
/// Somewhere the cursor could go, and why.
/// </summary>
/// <param name="At">Where.</param>
/// <param name="Kind">What sort of thing it is.</param>
/// <param name="Glyph">A stable name for the mark to show. The UI decides what it looks like.</param>
/// <param name="Reason">What to say if the user asks.</param>
/// <param name="On">
/// What it caught on, so the caller can turn a snap into a constraint. Empty for a grid point,
/// which belongs to nothing.
/// </param>
public sealed record SnapCandidate(
    Vec2d At,
    SnapKind Kind,
    string Glyph,
    string Reason,
    ImmutableArray<SketchPointRef> On)
{
    /// <summary>Gets what it caught on, never a default array.</summary>
    public ImmutableArray<SketchPointRef> Caught => On.IsDefault ? [] : On;

    /// <inheritdoc/>
    /// <remarks>
    /// Written by hand because a record compares an <see cref="ImmutableArray{T}"/> by its
    /// underlying array reference, so two candidates describing the same catch would be unequal —
    /// and the first thing that notices is a test asking whether the same cursor catches on the
    /// same thing twice, which is the property this type exists to have.
    /// </remarks>
    public bool Equals(SnapCandidate? other)
        => other is not null
            && At == other.At
            && Kind == other.Kind
            && Glyph == other.Glyph
            && Reason == other.Reason
            && Caught.SequenceEqual(other.Caught);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(At, Kind, Glyph, Caught.Length);

    /// <inheritdoc/>
    public override string ToString() => $"{Kind} at {At}";
}

/// <summary>
/// What the cursor is allowed to catch on.
/// </summary>
/// <param name="Tolerance">
/// How near counts as near, in sketch units. The caller converts from pixels, for the same reason
/// inference does: snapping has to feel the same at every zoom.
/// </param>
/// <param name="Grid">How far apart the grid points are, or null for no grid.</param>
/// <param name="Suppressed">Whether the user is holding the modifier that means "leave it alone".</param>
/// <param name="Enabled">Which kinds to look for. Empty means all of them.</param>
public sealed record SnapOptions(
    double Tolerance = 1,
    double? Grid = null,
    bool Suppressed = false,
    ImmutableArray<SnapKind> Enabled = default)
{
    /// <summary>Gets the settings a sketcher uses when nothing else is said.</summary>
    public static SnapOptions Default { get; } = new();

    /// <summary>Whether a kind of snap is being looked for.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>Whether it is wanted.</returns>
    public bool Wants(SnapKind kind)
        => Enabled.IsDefault || Enabled.IsEmpty || Enabled.Contains(kind);
}

/// <summary>
/// Works out where the cursor really means to be.
/// </summary>
/// <remarks>
/// <para>
/// P4-T09. Snapping and inference (P4-T08) are two answers to the same proximity search and are
/// deliberately separate: snapping moves the cursor, inference proposes a constraint. A user
/// dropping a point on a line wants it <em>on</em> the line whether or not a constraint follows,
/// and a sketcher that only offered the constraint would leave the geometry visibly off by however
/// far the cursor missed.
/// </para>
/// <para>
/// One candidate comes back, not a list. A cursor is in one place, and a caller given several
/// would have to choose — which is this code's job, done once, rather than every caller's, done
/// differently.
/// </para>
/// </remarks>
public static class SketchSnapping
{
    /// <summary>Finds where the cursor should go.</summary>
    /// <param name="sketch">The sketch as it stands.</param>
    /// <param name="at">Where the cursor is.</param>
    /// <param name="options">What it is allowed to catch on.</param>
    /// <param name="from">
    /// Where the current drawing began, for the guides that run through it. Null when nothing is
    /// being drawn, and then no guides are offered — a guide from nowhere is a line through the
    /// whole sketch that catches the cursor at random.
    /// </param>
    /// <param name="ignore">Geometry to leave out, normally what is being drawn or dragged.</param>
    /// <returns>Where to go and why, or null if nothing was near enough.</returns>
    public static SnapCandidate? Snap(
        Sketch sketch,
        Vec2d at,
        SnapOptions? options = null,
        Vec2d? from = null,
        SketchEntityId ignore = default)
    {
        ArgumentNullException.ThrowIfNull(sketch);

        SnapOptions settings = options ?? SnapOptions.Default;

        if (settings.Suppressed)
        {
            return null;
        }

        List<SnapCandidate> found = [];

        ImmutableArray<SketchEntity> visible =
            [.. sketch.Entities.Ordered.Where(e => e.Id != ignore)];

        Points(visible, at, settings, found);
        Quadrants(visible, at, settings, found);
        Curves(visible, at, settings, found);
        Crossings(visible, at, settings, found);
        Extensions(visible, at, settings, found);
        Guides(visible, at, settings, from, found);
        Grid(at, settings, found);

        // Best by kind, then by nearness, then by where the geometry sits in the sketch. Never by
        // an id: two runs would catch on different things and a sketcher nobody can predict is one
        // nobody can aim with.
        ImmutableArray<SketchEntityId> order = [.. sketch.Entities.Ordered.Select(e => e.Id)];

        return found
            .OrderByDescending(c => c.Kind)
            .ThenBy(c => (c.At - at).Length)
            .ThenBy(c => c.Caught.Select(o => order.IndexOf(o.Entity)).DefaultIfEmpty(0).Min())
            .FirstOrDefault();
    }

    /// <summary>Every place two curves cross.</summary>
    /// <param name="first">One curve.</param>
    /// <param name="second">The other.</param>
    /// <returns>Where they cross, empty if they do not.</returns>
    /// <remarks>
    /// Public because it is worth having on its own: trimming (P4-T13) and profile detection
    /// (P4-T14) both need to know where things cross, and three answers to that question would
    /// eventually be three different answers.
    /// </remarks>
    public static ImmutableArray<Vec2d> Crossings(SketchEntity first, SketchEntity second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        return (first, second) switch
        {
            (SketchLine a, SketchLine b) => LineLine(a, b),
            (SketchLine a, _) when Round(second) is { } circle => LineCircle(a, circle, second),
            (_, SketchLine b) when Round(first) is { } circle => LineCircle(b, circle, first),
            _ when Round(first) is { } one && Round(second) is { } other
                => CircleCircle(one, other, first, second),
            _ => [],
        };
    }

    private static void Points(
        ImmutableArray<SketchEntity> visible,
        Vec2d at,
        SnapOptions settings,
        List<SnapCandidate> found)
    {
        if (!settings.Wants(SnapKind.Point) && !settings.Wants(SnapKind.Midpoint))
        {
            return;
        }

        foreach (SketchEntity entity in visible)
        {
            foreach (EntityPoint which in entity.Points)
            {
                if (entity.PointOf(which) is not { } where
                    || (where - at).Length > settings.Tolerance)
                {
                    continue;
                }

                bool middle = which == EntityPoint.Middle;
                SnapKind kind = middle ? SnapKind.Midpoint : SnapKind.Point;

                if (!settings.Wants(kind))
                {
                    continue;
                }

                found.Add(new SnapCandidate(
                    where,
                    kind,
                    middle ? "midpoint" : "endpoint",
                    middle ? $"the middle of a {entity.Kind}" : $"a point of a {entity.Kind}",
                    [new SketchPointRef(entity.Id, which)]));
            }
        }
    }

    private static void Quadrants(
        ImmutableArray<SketchEntity> visible,
        Vec2d at,
        SnapOptions settings,
        List<SnapCandidate> found)
    {
        if (!settings.Wants(SnapKind.Quadrant))
        {
            return;
        }

        foreach (SketchEntity entity in visible)
        {
            if (Round(entity) is not { } circle)
            {
                continue;
            }

            foreach (Vec2d spoke in new[]
            {
                Vec2d.UnitX, Vec2d.UnitY, -Vec2d.UnitX, -Vec2d.UnitY,
            })
            {
                Vec2d where = circle.Centre + (spoke * circle.Radius);

                if ((where - at).Length <= settings.Tolerance && OnArc(entity, where))
                {
                    found.Add(new SnapCandidate(
                        where,
                        SnapKind.Quadrant,
                        "quadrant",
                        $"a quarter point of a {entity.Kind}",
                        [new SketchPointRef(entity.Id)]));
                }
            }
        }
    }

    private static void Curves(
        ImmutableArray<SketchEntity> visible,
        Vec2d at,
        SnapOptions settings,
        List<SnapCandidate> found)
    {
        if (!settings.Wants(SnapKind.OnCurve))
        {
            return;
        }

        foreach (SketchEntity entity in visible)
        {
            if (Nearest(entity, at) is not { } where
                || (where - at).Length > settings.Tolerance)
            {
                continue;
            }

            found.Add(new SnapCandidate(
                where,
                SnapKind.OnCurve,
                "on-curve",
                $"on a {entity.Kind}",
                [new SketchPointRef(entity.Id)]));
        }
    }

    private static void Crossings(
        ImmutableArray<SketchEntity> visible,
        Vec2d at,
        SnapOptions settings,
        List<SnapCandidate> found)
    {
        if (!settings.Wants(SnapKind.Intersection))
        {
            return;
        }

        for (int i = 0; i < visible.Length; ++i)
        {
            for (int j = i + 1; j < visible.Length; ++j)
            {
                foreach (Vec2d where in Crossings(visible[i], visible[j]))
                {
                    if ((where - at).Length <= settings.Tolerance)
                    {
                        found.Add(new SnapCandidate(
                            where,
                            SnapKind.Intersection,
                            "intersection",
                            $"where a {visible[i].Kind} crosses a {visible[j].Kind}",
                            [
                                new SketchPointRef(visible[i].Id),
                                new SketchPointRef(visible[j].Id),
                            ]));
                    }
                }
            }
        }
    }

    private static void Extensions(
        ImmutableArray<SketchEntity> visible,
        Vec2d at,
        SnapOptions settings,
        List<SnapCandidate> found)
    {
        if (!settings.Wants(SnapKind.Extension))
        {
            return;
        }

        foreach (SketchEntity entity in visible)
        {
            if (entity is not SketchLine line || line.Length <= Tolerance.LinearResolution)
            {
                continue;
            }

            double along = Vec2d.Dot(at - line.Start, line.Direction);

            // Beyond an end, not between them: between them is the line itself, which is a
            // different and better snap.
            if (along > 0 && along < line.Length)
            {
                continue;
            }

            Vec2d where = line.Start + (line.Direction * along);

            if ((where - at).Length <= settings.Tolerance)
            {
                found.Add(new SnapCandidate(
                    where,
                    SnapKind.Extension,
                    "extension",
                    "in line with a line",
                    [new SketchPointRef(entity.Id)]));
            }
        }
    }

    private static void Guides(
        ImmutableArray<SketchEntity> visible,
        Vec2d at,
        SnapOptions settings,
        Vec2d? from,
        List<SnapCandidate> found)
    {
        if (!settings.Wants(SnapKind.Guide) || from is not { } anchor)
        {
            return;
        }

        foreach (SketchEntity entity in visible)
        {
            if (entity is not SketchLine line || line.Length <= Tolerance.LinearResolution)
            {
                continue;
            }

            foreach ((Vec2d direction, string what) in new[]
            {
                (line.Direction, "parallel to a line"),
                (new Vec2d(-line.Direction.Y, line.Direction.X), "square to a line"),
            })
            {
                Vec2d where = anchor + (direction * Vec2d.Dot(at - anchor, direction));

                if ((where - at).Length <= settings.Tolerance)
                {
                    found.Add(new SnapCandidate(
                        where,
                        SnapKind.Guide,
                        what.StartsWith("parallel", StringComparison.Ordinal)
                            ? "guide-parallel"
                            : "guide-perpendicular",
                        what,
                        [new SketchPointRef(entity.Id)]));
                }
            }
        }
    }

    private static void Grid(Vec2d at, SnapOptions settings, List<SnapCandidate> found)
    {
        if (!settings.Wants(SnapKind.Grid) || settings.Grid is not { } spacing || spacing <= 0)
        {
            return;
        }

        // Always a candidate, and always the weakest. A grid is what the cursor catches on when it
        // caught on nothing else, so it rounds rather than needing to be within a tolerance --
        // otherwise a user with a grid on would find it working only sometimes.
        Vec2d where = new(
            System.Math.Round(at.X / spacing) * spacing,
            System.Math.Round(at.Y / spacing) * spacing);

        found.Add(new SnapCandidate(where, SnapKind.Grid, "grid", "a grid point", []));
    }

    /// <summary>The nearest point of a curve, as drawn.</summary>
    private static Vec2d? Nearest(SketchEntity entity, Vec2d at)
    {
        switch (entity)
        {
            case SketchLine line when line.Length > Tolerance.LinearResolution:
                double along = Vec2d.Dot(at - line.Start, line.Direction);

                return along < 0 || along > line.Length
                    ? null
                    : line.Start + (line.Direction * along);

            case SketchCircle or SketchArc when Round(entity) is { } circle:
                Vec2d out_ = at - circle.Centre;

                if (out_.IsZeroLength)
                {
                    return null;
                }

                Vec2d where = circle.Centre + (out_ / out_.Length * circle.Radius);

                return OnArc(entity, where) ? where : null;

            default:
                return null;
        }
    }

    /// <summary>Whether a point of a circle is within an arc's sweep.</summary>
    /// <remarks>
    /// A full circle is all of it. An arc is not: a point on the circle the arc came from is not on
    /// the arc, and snapping to it would put the cursor somewhere the user cannot see any geometry.
    /// </remarks>
    private static bool OnArc(SketchEntity entity, Vec2d where)
    {
        if (entity is not SketchArc arc)
        {
            return true;
        }

        double angle = System.Math.Atan2(
            where.Y - arc.Centre.Y, where.X - arc.Centre.X) - arc.StartAngle;

        while (angle < 0)
        {
            angle += 2 * System.Math.PI;
        }

        return angle <= arc.Sweep + Tolerance.AngularResolution;
    }

    private static (Vec2d Centre, double Radius)? Round(SketchEntity entity) => entity switch
    {
        SketchCircle circle => (circle.Centre, circle.Radius),
        SketchArc arc => (arc.Centre, arc.Radius),
        _ => null,
    };

    private static ImmutableArray<Vec2d> LineLine(SketchLine a, SketchLine b)
    {
        Vec2d p = a.End - a.Start;
        Vec2d q = b.End - b.Start;

        double denominator = Vec2d.Cross(p, q);

        if (System.Math.Abs(denominator) <= Tolerance.LinearResolution)
        {
            return [];
        }

        double t = Vec2d.Cross(b.Start - a.Start, q) / denominator;
        double u = Vec2d.Cross(b.Start - a.Start, p) / denominator;

        // Within both segments. Two lines that would cross if they were longer do not cross, and a
        // sketcher that said they did would put a point where there is nothing to see.
        return t < 0 || t > 1 || u < 0 || u > 1 ? [] : [a.Start + (p * t)];
    }

    private static ImmutableArray<Vec2d> LineCircle(
        SketchLine line, (Vec2d Centre, double Radius) circle, SketchEntity arc)
    {
        if (line.Length <= Tolerance.LinearResolution)
        {
            return [];
        }

        double along = Vec2d.Dot(circle.Centre - line.Start, line.Direction);
        Vec2d closest = line.Start + (line.Direction * along);

        double away = (closest - circle.Centre).Length;

        if (away > circle.Radius)
        {
            return [];
        }

        double half = System.Math.Sqrt(
            System.Math.Max(0, (circle.Radius * circle.Radius) - (away * away)));

        ImmutableArray<Vec2d>.Builder found = ImmutableArray.CreateBuilder<Vec2d>();

        foreach (double offset in half <= Tolerance.LinearResolution
            ? [0d]
            : new[] { -half, half })
        {
            double distance = along + offset;

            if (distance < 0 || distance > line.Length)
            {
                continue;
            }

            Vec2d where = line.Start + (line.Direction * distance);

            if (OnArc(arc, where))
            {
                found.Add(where);
            }
        }

        return found.ToImmutable();
    }

    private static ImmutableArray<Vec2d> CircleCircle(
        (Vec2d Centre, double Radius) one,
        (Vec2d Centre, double Radius) other,
        SketchEntity first,
        SketchEntity second)
    {
        Vec2d between = other.Centre - one.Centre;
        double apart = between.Length;

        if (apart <= Tolerance.LinearResolution
            || apart > one.Radius + other.Radius
            || apart < System.Math.Abs(one.Radius - other.Radius))
        {
            return [];
        }

        double along = ((apart * apart) + (one.Radius * one.Radius) - (other.Radius * other.Radius))
            / (2 * apart);

        double across = System.Math.Sqrt(
            System.Math.Max(0, (one.Radius * one.Radius) - (along * along)));

        Vec2d direction = between / apart;
        Vec2d square = new(-direction.Y, direction.X);
        Vec2d middle = one.Centre + (direction * along);

        ImmutableArray<Vec2d>.Builder found = ImmutableArray.CreateBuilder<Vec2d>();

        foreach (double offset in across <= Tolerance.LinearResolution
            ? [0d]
            : new[] { -across, across })
        {
            Vec2d where = middle + (square * offset);

            if (OnArc(first, where) && OnArc(second, where))
            {
                found.Add(where);
            }
        }

        return found.ToImmutable();
    }
}
