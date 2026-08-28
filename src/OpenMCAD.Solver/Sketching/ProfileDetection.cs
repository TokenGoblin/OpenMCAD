using System.Collections.Immutable;

using OpenMCAD.Math;

namespace OpenMCAD.Solver.Sketching;

/// <summary>
/// A piece of one curve, between two places where something crosses it.
/// </summary>
/// <param name="Entity">Which curve it came from.</param>
/// <param name="From">Where the piece starts, as a parameter of that curve.</param>
/// <param name="To">Where it ends.</param>
/// <param name="Start">The starting point.</param>
/// <param name="End">The ending point.</param>
/// <remarks>
/// The parameters are kept as well as the points because a profile is going to be handed to a
/// kernel, which needs to know what curve to build and how much of it — not a polyline that happens
/// to pass through the same places.
/// </remarks>
public sealed record ProfileSegment(
    SketchEntityId Entity, double From, double To, Vec2d Start, Vec2d End)
{
    /// <summary>Gets whether the piece runs backwards along its curve.</summary>
    public bool IsReversed => To < From;

    /// <inheritdoc/>
    public override string ToString() => $"{Entity} [{From:0.###}, {To:0.###}]";
}

/// <summary>
/// A closed run of segments.
/// </summary>
/// <param name="Segments">The pieces, in order, each ending where the next begins.</param>
/// <param name="SignedArea">
/// How much it encloses, negative when it runs clockwise. The sign is what tells an outer boundary
/// from a hole, so it is kept rather than thrown away for a magnitude.
/// </param>
public sealed record ProfileLoop(ImmutableArray<ProfileSegment> Segments, double SignedArea)
{
    /// <summary>Gets how much it encloses, without regard to which way it runs.</summary>
    public double Area => System.Math.Abs(SignedArea);

    /// <summary>Gets the points the loop passes through, in order.</summary>
    public ImmutableArray<Vec2d> Corners => [.. Segments.Select(s => s.Start)];

    /// <summary>Gets a point that is definitely inside, for asking what contains what.</summary>
    /// <remarks>
    /// <para>
    /// Not a corner. Two regions that share an edge share its corners, and asking whether a corner
    /// is inside the other region is asking a point-in-polygon test about a point on its own
    /// boundary — which answers yes or no according to rounding. Two overlapping squares came out
    /// with the region they share counted as a hole of one of them, and four units of area
    /// vanished.
    /// </para>
    /// <para>
    /// The middle of the first edge, stepped a little to its left. A loop that runs anticlockwise
    /// has its interior on the left of every edge, so the step lands inside for any shape at all —
    /// convex or not — and the step is scaled to the loop so it works at any size.
    /// </para>
    /// </remarks>
    public Vec2d Somewhere
    {
        get
        {
            if (Segments.IsEmpty)
            {
                return Vec2d.Zero;
            }

            ProfileSegment first = Segments[0];

            Vec2d along = first.End - first.Start;

            if (along.IsZeroLength)
            {
                return first.Start;
            }

            double reach = System.Math.Sqrt(System.Math.Max(Area, 1e-12));
            Vec2d left = new(-along.Y, along.X);

            return ((first.Start + first.End) * 0.5) + (left / along.Length * reach * 1e-3);
        }
    }

    /// <inheritdoc/>
    public bool Equals(ProfileLoop? other)
        => other is not null
            && SignedArea.Equals(other.SignedArea)
            && Segments.SequenceEqual(other.Segments);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Segments.Length, SignedArea);

    /// <inheritdoc/>
    public override string ToString() => $"{Segments.Length} segments, area {Area:0.###}";
}

/// <summary>
/// A region a feature could be built from: an outer boundary, and whatever it has holes for.
/// </summary>
/// <param name="Outer">The boundary.</param>
/// <param name="Holes">The loops inside it that are not part of it.</param>
public sealed record SketchProfile(ProfileLoop Outer, ImmutableArray<ProfileLoop> Holes)
{
    /// <summary>Gets the holes, never a default array.</summary>
    public ImmutableArray<ProfileLoop> Inner => Holes.IsDefault ? [] : Holes;

    /// <summary>Gets how much material the region actually covers.</summary>
    public double Area => Outer.Area - Inner.Sum(h => h.Area);

    /// <inheritdoc/>
    public bool Equals(SketchProfile? other)
        => other is not null && Outer == other.Outer && Inner.SequenceEqual(other.Inner);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Outer, Inner.Length);

    /// <inheritdoc/>
    public override string ToString() => Inner.IsEmpty
        ? $"region of {Outer.Area:0.###}"
        : $"region of {Area:0.###} with {Inner.Length} holes";
}

/// <summary>
/// What the sketch offers to build from, and what it does not.
/// </summary>
/// <param name="Profiles">The regions, largest first.</param>
/// <param name="Dangling">
/// Geometry that is in the sketch and in no region: an open chain, a line to nowhere, a curve kind
/// this build cannot yet trace. Reported rather than ignored, because "why is my extrude not
/// offering this" is the commonest question a sketcher has to answer.
/// </param>
public sealed record ProfileSet(
    ImmutableArray<SketchProfile> Profiles, ImmutableArray<SketchEntityId> Dangling)
{
    /// <summary>Gets whether anything can be built from this sketch.</summary>
    public bool IsEmpty => Profiles.IsEmpty;

    /// <inheritdoc/>
    public bool Equals(ProfileSet? other)
        => other is not null
            && Profiles.SequenceEqual(other.Profiles)
            && Dangling.SequenceEqual(other.Dangling);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Profiles.Length, Dangling.Length);

    /// <inheritdoc/>
    public override string ToString()
        => $"{Profiles.Length} regions, {Dangling.Length} entities in none of them";
}

/// <summary>
/// Finds the closed regions of a sketch.
/// </summary>
/// <remarks>
/// <para>
/// P4-T14. What an extrude is offered when the user picks a sketch. The sketch is treated as a
/// planar arrangement: every curve is cut where anything crosses it, the pieces become the edges of
/// a graph, and the bounded faces of that graph are the regions. That is more work than following
/// chains of coincident endpoints, and it is the only way to get the case users actually draw —
/// two overlapping rectangles, where the regions are not any of the shapes anyone drew.
/// </para>
/// <para>
/// Construction geometry is left out. It exists to constrain, never to be built from, and a
/// sketcher that offered a region bounded by a construction line would be offering something the
/// user deliberately said was scaffolding.
/// </para>
/// <para>
/// Lines, circles and arcs only. Splines and conics can bound a region in principle and cutting
/// them at their crossings needs a numerical intersector that P4-T09 does not have; they are
/// reported as dangling rather than silently dropped, so the sketcher can say why.
/// </para>
/// </remarks>
public static class ProfileDetection
{
    /// <summary>Finds what can be built from a sketch.</summary>
    /// <param name="sketch">The sketch.</param>
    /// <param name="tolerance">How near two points have to be to count as the same one.</param>
    /// <returns>The regions, and whatever is in none of them.</returns>
    public static ProfileSet Find(Sketch sketch, double tolerance = 1e-7)
    {
        ArgumentNullException.ThrowIfNull(sketch);

        ImmutableArray<SketchEntity> usable =
        [
            .. sketch.Entities.Ordered.Where(e => !e.IsConstruction && CanTrace(e)),
        ];

        ImmutableArray<SketchEntityId> unusable =
        [
            .. sketch.Entities.Ordered
                .Where(e => !e.IsConstruction && !CanTrace(e))
                .Select(e => e.Id),
        ];

        List<ProfileSegment> segments = [];

        foreach (SketchEntity entity in usable)
        {
            segments.AddRange(Cut(entity, usable, tolerance));
        }

        List<Vec2d> vertices = [];
        List<HalfEdge> edges = [];

        foreach (ProfileSegment segment in segments)
        {
            int from = Vertex(vertices, segment.Start, tolerance);
            int to = Vertex(vertices, segment.End, tolerance);

            if (from == to)
            {
                // A piece that begins and ends at the same vertex is a whole closed curve, and a
                // graph edge cannot be a loop. It is split in half so it becomes two edges, which
                // is what lets a lone circle be a region at all.
                continue;
            }

            edges.Add(new HalfEdge(segment, from, to, false));
            edges.Add(new HalfEdge(Reversed(segment), to, from, true));
        }

        ImmutableArray<ProfileLoop> loops = Walk(vertices, edges, usable);

        return Assemble(loops, segments, usable, unusable);
    }

    /// <summary>Whether a point is inside a loop.</summary>
    /// <param name="loop">The loop.</param>
    /// <param name="at">The point.</param>
    /// <returns>Whether it is inside.</returns>
    /// <remarks>
    /// A crossing count along a ray, over the loop's corners. Approximating an arc by its chord is
    /// wrong in general and right here, because the only points ever asked about come from
    /// <see cref="ProfileLoop.Somewhere"/> on loops of the same arrangement — and a loop that
    /// crosses another has already been cut at the crossing, so nothing sits between another's
    /// chord and its arc.
    /// </remarks>
    public static bool Contains(ProfileLoop loop, Vec2d at)
    {
        ArgumentNullException.ThrowIfNull(loop);

        ImmutableArray<Vec2d> corners = loop.Corners;
        bool inside = false;

        for (int i = 0, j = corners.Length - 1; i < corners.Length; j = i++)
        {
            if (corners[i].Y > at.Y != corners[j].Y > at.Y
                && at.X < ((corners[j].X - corners[i].X) * (at.Y - corners[i].Y)
                    / (corners[j].Y - corners[i].Y)) + corners[i].X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static bool CanTrace(SketchEntity entity)
        => entity is SketchLine or SketchCircle or SketchArc;

    /// <summary>Cuts one curve wherever anything crosses it.</summary>
    private static ImmutableArray<ProfileSegment> Cut(
        SketchEntity entity, ImmutableArray<SketchEntity> others, double tolerance)
    {
        List<double> at = [0, 1];

        foreach (SketchEntity other in others)
        {
            if (other.Id == entity.Id)
            {
                continue;
            }

            foreach (Vec2d where in SketchSnapping.Crossings(entity, other))
            {
                if (ParameterOf(entity, where) is { } t && t > tolerance && t < 1 - tolerance)
                {
                    at.Add(t);
                }
            }
        }

        // A closed curve with nothing crossing it still has to be cut, or it becomes a graph edge
        // from a vertex to itself, which no traversal can walk.
        if (entity.IsClosed && at.Count == 2)
        {
            at.Add(0.5);
        }

        at.Sort();

        ImmutableArray<ProfileSegment>.Builder found =
            ImmutableArray.CreateBuilder<ProfileSegment>();

        for (int i = 0; i < at.Count - 1; ++i)
        {
            if (at[i + 1] - at[i] <= tolerance)
            {
                continue;
            }

            found.Add(new ProfileSegment(
                entity.Id, at[i], at[i + 1], entity.PointAt(at[i]), entity.PointAt(at[i + 1])));
        }

        return found.ToImmutable();
    }

    private static ProfileSegment Reversed(ProfileSegment segment)
        => new(segment.Entity, segment.To, segment.From, segment.End, segment.Start);

    private static int Vertex(List<Vec2d> vertices, Vec2d at, double tolerance)
    {
        for (int i = 0; i < vertices.Count; ++i)
        {
            if ((vertices[i] - at).Length <= System.Math.Max(tolerance, 1e-9))
            {
                return i;
            }
        }

        vertices.Add(at);

        return vertices.Count - 1;
    }

    /// <summary>Walks the faces of the arrangement.</summary>
    /// <remarks>
    /// The standard rule: arriving at a vertex, leave by the edge that turns most sharply
    /// clockwise from the way you came. Following that consistently traces one face per circuit,
    /// and every half-edge belongs to exactly one — so the whole arrangement is covered by walking
    /// each unused half-edge once.
    /// </remarks>
    private static ImmutableArray<ProfileLoop> Walk(
        List<Vec2d> vertices, List<HalfEdge> edges, ImmutableArray<SketchEntity> entities)
    {
        Dictionary<int, List<int>> leaving = [];

        for (int i = 0; i < edges.Count; ++i)
        {
            if (!leaving.TryGetValue(edges[i].From, out List<int>? here))
            {
                here = [];
                leaving[edges[i].From] = here;
            }

            here.Add(i);
        }

        bool[] used = new bool[edges.Count];
        ImmutableArray<ProfileLoop>.Builder loops = ImmutableArray.CreateBuilder<ProfileLoop>();

        for (int start = 0; start < edges.Count; ++start)
        {
            if (used[start])
            {
                continue;
            }

            List<ProfileSegment> run = [];
            int at = start;

            while (!used[at])
            {
                used[at] = true;
                run.Add(edges[at].Segment);

                double arriving = Angle(entities, edges[at], atEnd: true);
                int next = -1;
                double best = double.MaxValue;

                foreach (int candidate in leaving.GetValueOrDefault(edges[at].To, []))
                {
                    double turn = Turn(arriving + System.Math.PI, Angle(entities, edges[candidate], atEnd: false));

                    if (turn < best)
                    {
                        best = turn;
                        next = candidate;
                    }
                }

                if (next < 0)
                {
                    break;
                }

                at = next;
            }

            if (run.Count > 0 && (run[^1].End - run[0].Start).Length <= 1e-6)
            {
                loops.Add(new ProfileLoop([.. run], SignedArea(run, entities)));
            }
        }

        _ = vertices;

        return loops.ToImmutable();
    }

    /// <summary>How far clockwise one direction is from another, in [0, 2π).</summary>
    private static double Turn(double from, double to)
    {
        double turn = (from - to) % (2 * System.Math.PI);

        return turn <= 0 ? turn + (2 * System.Math.PI) : turn;
    }

    /// <summary>Which way a half-edge points, at whichever of its ends is asked about.</summary>
    private static double Angle(
        ImmutableArray<SketchEntity> entities, HalfEdge edge, bool atEnd)
    {
        SketchEntity? entity = entities.FirstOrDefault(e => e.Id == edge.Segment.Entity);

        Vec2d direction = entity is null
            ? edge.Segment.End - edge.Segment.Start
            : Tangent(entity, edge.Segment, atEnd);

        return System.Math.Atan2(direction.Y, direction.X);
    }

    /// <summary>The direction of travel along a piece of a curve, at one of its ends.</summary>
    /// <remarks>
    /// Straight for a line, and square to the radius for a circle or an arc, turned to face the way
    /// the piece is being travelled. A chord would do for the ordering almost always and would be
    /// wrong exactly where two curves meet tangentially — which is where a fillet meets the line it
    /// was made from, and so is the commonest join in a real sketch.
    /// </remarks>
    private static Vec2d Tangent(SketchEntity entity, ProfileSegment segment, bool atEnd)
    {
        double t = atEnd ? segment.To : segment.From;

        if (entity is SketchLine line)
        {
            return segment.IsReversed ? -line.Direction : line.Direction;
        }

        Vec2d centre = entity.PointOf(EntityPoint.Centre) ?? Vec2d.Zero;
        Vec2d out_ = entity.PointAt(t) - centre;

        if (out_.IsZeroLength)
        {
            return segment.End - segment.Start;
        }

        Vec2d round = new(-out_.Y, out_.X);

        return segment.IsReversed ? -round : round;
    }

    /// <summary>How much a run of segments encloses, negative when it runs clockwise.</summary>
    /// <remarks>
    /// The shoelace over the corners, plus the sliver each arc cuts off its own chord. Without that
    /// correction a circle has an area of zero, because its corners are two points on a line.
    /// </remarks>
    private static double SignedArea(
        List<ProfileSegment> run, ImmutableArray<SketchEntity> entities)
    {
        double area = 0;

        for (int i = 0; i < run.Count; ++i)
        {
            Vec2d a = run[i].Start;
            Vec2d b = run[i].End;

            area += (a.X * b.Y) - (b.X * a.Y);
        }

        area /= 2;

        foreach (ProfileSegment segment in run)
        {
            SketchEntity? entity = entities.FirstOrDefault(e => e.Id == segment.Entity);

            if (entity is SketchLine or null)
            {
                continue;
            }

            double radius = entity switch
            {
                SketchCircle circle => circle.Radius,
                SketchArc arc => arc.Radius,
                _ => 0,
            };

            if (radius <= 0)
            {
                continue;
            }

            double chord = (segment.End - segment.Start).Length;
            double half = System.Math.Min(1, chord / (2 * radius));
            double swept = 2 * System.Math.Asin(half);

            // Which side of the chord the arc bulges to decides the sign, and how much of the
            // circle the piece covers decides whether the sliver is the small piece or the rest.
            double covered = System.Math.Abs(segment.To - segment.From)
                * (entity.IsClosed ? 2 * System.Math.PI : Sweep(entity));

            if (covered > System.Math.PI)
            {
                swept = (2 * System.Math.PI) - swept;
            }

            double sliver = (radius * radius / 2) * (swept - System.Math.Sin(swept));

            area += segment.IsReversed ? -sliver : sliver;
        }

        return area;
    }

    private static double Sweep(SketchEntity entity) => entity switch
    {
        SketchArc arc => arc.Sweep,
        _ => 2 * System.Math.PI,
    };

    /// <summary>Sorts the loops into regions, and says what was left over.</summary>
    /// <remarks>
    /// Every circuit of the arrangement comes back, including the one that runs round the outside
    /// of everything — which is the loop that turns clockwise and encloses the whole. It is dropped:
    /// nobody can extrude the outside of a drawing.
    /// </remarks>
    private static ProfileSet Assemble(
        ImmutableArray<ProfileLoop> loops,
        List<ProfileSegment> segments,
        ImmutableArray<SketchEntity> usable,
        ImmutableArray<SketchEntityId> unusable)
    {
        ImmutableArray<ProfileLoop> bounded = [.. loops.Where(l => l.SignedArea > 0)];

        // Largest first, then by area, then by where the first segment's curve sits in the sketch.
        // Never by an id: two runs would offer the user regions in a different order.
        ImmutableArray<SketchEntityId> order = [.. usable.Select(e => e.Id)];

        ImmutableArray<ProfileLoop> sorted =
        [
            .. bounded
                .OrderByDescending(l => l.Area)
                .ThenBy(l => l.Segments.IsEmpty ? 0 : order.IndexOf(l.Segments[0].Entity)),
        ];

        ImmutableArray<SketchProfile>.Builder profiles =
            ImmutableArray.CreateBuilder<SketchProfile>();

        foreach (ProfileLoop loop in sorted)
        {
            // A loop wholly inside another is a hole of it -- but only of the smallest one that
            // holds it, or a circle inside a square inside a rectangle would be a hole of both.
            ImmutableArray<ProfileLoop> holes =
            [
                .. sorted.Where(other => other != loop
                    && other.Area < loop.Area
                    && Contains(loop, other.Somewhere)
                    && !sorted.Any(between => between != loop
                        && between != other
                        && between.Area < loop.Area
                        && between.Area > other.Area
                        && Contains(between, other.Somewhere))),
            ];

            profiles.Add(new SketchProfile(loop, holes));
        }

        HashSet<SketchEntityId> used =
        [
            .. sorted.SelectMany(l => l.Segments).Select(s => s.Entity),
        ];

        ImmutableArray<SketchEntityId> dangling =
        [
            .. unusable,
            .. segments.Select(s => s.Entity).Distinct().Where(id => !used.Contains(id)),
        ];

        return new ProfileSet(
            profiles.ToImmutable(),
            [.. dangling.Distinct().OrderBy(id => order.IndexOf(id))]);
    }

    /// <summary>Where a point sits along a curve, as a parameter, or null if it is not on it.</summary>
    private static double? ParameterOf(SketchEntity entity, Vec2d at)
    {
        switch (entity)
        {
            case SketchLine line when line.Length > Tolerance.LinearResolution:
                return Vec2d.Dot(at - line.Start, line.Direction) / line.Length;

            case SketchCircle circle:
                Vec2d round = at - circle.Centre;

                return round.IsZeroLength
                    ? null
                    : Wrapped(System.Math.Atan2(round.Y, round.X)) / (2 * System.Math.PI);

            case SketchArc arc:
                Vec2d out_ = at - arc.Centre;

                if (out_.IsZeroLength || arc.Sweep <= Tolerance.AngularResolution)
                {
                    return null;
                }

                return Wrapped(System.Math.Atan2(out_.Y, out_.X) - arc.StartAngle) / arc.Sweep;

            default:
                return null;
        }
    }

    private static double Wrapped(double angle)
    {
        while (angle < 0)
        {
            angle += 2 * System.Math.PI;
        }

        return angle;
    }

    private readonly record struct HalfEdge(
        ProfileSegment Segment, int From, int To, bool IsReversed);
}
