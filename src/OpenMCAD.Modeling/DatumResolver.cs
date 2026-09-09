using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;
using OpenMCAD.Kernel;
using OpenMCAD.Math;

namespace OpenMCAD.Modeling;

/// <summary>How resolving a <see cref="DatumDefinition"/> came to.</summary>
/// <remarks>
/// Four ways to fail rather than one, split along the line that decides what the user has to do
/// about it. The first three all mean <em>the reference is wrong</em> and are repaired by pointing
/// the datum at something else; <see cref="Degenerate"/> means the references were all fine and the
/// <em>geometry</em> has no answer, which is repaired by changing the model or the construction.
/// Splitting them any finer than that would name distinctions the person reading the message cannot
/// act on differently, which is what <see cref="DatumResolution.Reason"/> is for instead.
/// </remarks>
public enum DatumResolutionOutcome
{
    /// <summary>The definition produced reference geometry.</summary>
    Resolved,

    /// <summary>
    /// Nothing answers to one of the references, or it names a kind of thing the construction
    /// cannot use at all — an axis where a plane was wanted.
    /// </summary>
    NotFound,

    /// <summary>
    /// A topology reference traced to more than one candidate and nothing said which was meant.
    /// </summary>
    /// <remarks>
    /// Bubbled up from <see cref="NameResolutionOutcome.Ambiguous"/>. A datum input is always
    /// exactly one entity (see <see cref="DatumReference.OnTopology"/>), so unlike
    /// <see cref="EntityReference"/>'s <see cref="MultiplicityPolicy"/> there is no policy here that
    /// could turn a split into an answer — it is always a refusal.
    /// </remarks>
    Ambiguous,

    /// <summary>
    /// A reference resolved to exactly what it names, and that thing cannot play the part the
    /// construction needs: a face that is not flat, an edge that is not straight where an axis was
    /// asked for, or an edge this build cannot describe as a curve at all.
    /// </summary>
    NotUsable,

    /// <summary>
    /// Every reference resolved and served its role, and the construction still has no answer:
    /// three collinear points, two parallel planes asked where they meet, two planes that are not
    /// parallel asked for a midplane.
    /// </summary>
    Degenerate,
}

/// <summary>What resolving a <see cref="DatumDefinition"/> came to.</summary>
/// <param name="Outcome">How it turned out.</param>
/// <param name="Geometry">
/// The reference geometry, when <paramref name="Outcome"/> is
/// <see cref="DatumResolutionOutcome.Resolved"/>.
/// </param>
/// <param name="Reason">Why, in words, when it could not be resolved.</param>
public sealed record DatumResolution(
    DatumResolutionOutcome Outcome,
    ReferenceGeometry? Geometry = null,
    string? Reason = null)
{
    /// <summary>Gets whether the definition produced reference geometry.</summary>
    public bool IsResolved => Outcome == DatumResolutionOutcome.Resolved;

    /// <summary>Creates a resolution that produced geometry.</summary>
    /// <param name="geometry">The geometry.</param>
    /// <returns>The resolution.</returns>
    public static DatumResolution Found(ReferenceGeometry geometry)
        => new(DatumResolutionOutcome.Resolved, geometry);

    /// <summary>Creates a resolution that failed.</summary>
    /// <param name="outcome">
    /// How it failed. Must not be <see cref="DatumResolutionOutcome.Resolved"/>.
    /// </param>
    /// <param name="reason">Why, in words.</param>
    /// <returns>The resolution.</returns>
    public static DatumResolution Failed(DatumResolutionOutcome outcome, string reason)
        => new(outcome, null, reason);
}

/// <summary>
/// Turns a <see cref="DatumDefinition"/> into the <see cref="ReferenceGeometry"/> it describes, for
/// one rebuild (P5-T03).
/// </summary>
/// <remarks>
/// <para>
/// The other half of the split <see cref="DatumDefinition"/> describes: a definition is what a file
/// holds and what survives an edit upstream, and turning one into coordinates needs a document in
/// hand and, for anything built on topology, the naming tiers of §5.3 walked. A static class rather
/// than an instance for the same reason <see cref="SketchPlaneResolver"/> is one — there is no
/// multi-call state worth bundling.
/// </para>
/// <para>
/// <b>Three geometry queries, supplied by the caller.</b> A face's plane, a vertex's position and
/// an edge's curve are all facts only a kernel can report, and nothing in
/// <see cref="OpenMCAD.Kernel"/> exposes any of the three yet. They arrive as delegates, exactly as
/// <see cref="SketchPlaneResolver"/>'s <c>planeOf</c> and
/// <see cref="SketchExternalReferenceResolver"/>'s <c>curveOf</c> already do, and for the same
/// reason those do rather than reading <c>GeoHint</c>: the naming layer's hints are deliberately in
/// coordinates local to the feature that produced the entity, so that moving a part does not stop
/// its faces being recognised, and a datum needs the opposite — genuine world-space coordinates to
/// place geometry at. A hint would put the datum in the wrong place the first time the feature
/// under it moved.
/// </para>
/// <para>
/// <b>Nothing here throws for a bad model.</b> A rebuild resolves every reference of every feature
/// in the dirty set (§5.4); one corrupt datum throwing would take down features that have nothing
/// to do with it. Degenerate inputs are reported as data, the same call
/// <see cref="SketchPlaneResolver"/> made.
/// </para>
/// </remarks>
public static class DatumResolver
{
    /// <summary>Resolves a datum definition.</summary>
    /// <param name="definition">What to build.</param>
    /// <param name="owner">
    /// The feature that will own the result — half of the pair everything else points at a datum
    /// by. Supplied here rather than stored on the definition, which does not know which feature
    /// hosts it.
    /// </param>
    /// <param name="document">The document to resolve reference geometry against.</param>
    /// <param name="entityOf">
    /// What a topology reference already came to, or <see langword="null"/> if this configuration
    /// has no way to say — any <see cref="DatumReference.OnTopology"/> then fails with
    /// <see cref="DatumResolutionOutcome.NotFound"/> rather than throwing. Use
    /// <see cref="Through"/> to build one from a <see cref="NameResolver"/>.
    /// </param>
    /// <param name="planeOf">
    /// How to get the world-space plane a resolved face lies on, or <see langword="null"/> to the
    /// same effect. Returning <see langword="null"/> for one face is a legitimate answer — the face
    /// is not flat — and gives <see cref="DatumResolutionOutcome.NotUsable"/>.
    /// </param>
    /// <param name="pointOf">
    /// How to get the world-space position of a resolved vertex, or <see langword="null"/> to the
    /// same effect.
    /// </param>
    /// <param name="curveOf">
    /// How to get the world-space curve a resolved edge is, or <see langword="null"/> to the same
    /// effect.
    /// </param>
    /// <returns>The resolution.</returns>
    public static DatumResolution Resolve(
        DatumDefinition definition,
        FeatureId owner,
        Document document,
        Func<PersistentName, ResolvedReference>? entityOf = null,
        Func<SubEntity, Plane?>? planeOf = null,
        Func<SubEntity, Vec3d?>? pointOf = null,
        Func<SubEntity, WorldCurve?>? curveOf = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(document);

        Lookup lookup = new(document, entityOf, planeOf, pointOf, curveOf);

        return definition switch
        {
            DatumDefinition.PlaneOffsetFrom offset => OffsetPlane(offset, owner, lookup),
            DatumDefinition.PlaneThroughPoint through => PlaneThroughPoint(through, owner, lookup),
            DatumDefinition.PlaneAtAngle angled => PlaneAtAngle(angled, owner, lookup),
            DatumDefinition.PlaneThroughThreePoints three => PlaneThroughThreePoints(three, owner, lookup),
            DatumDefinition.PlaneMidwayBetween midway => MidPlane(midway, owner, lookup),
            DatumDefinition.AxisThroughTwoPoints axis => AxisThroughTwoPoints(axis, owner, lookup),
            DatumDefinition.AxisAlongEdge along => AxisAlongEdge(along, owner, lookup),
            DatumDefinition.AxisWherePlanesMeet meet => AxisWherePlanesMeet(meet, owner, lookup),
            DatumDefinition.AxisNormalToPlane normal => AxisNormalToPlane(normal, owner, lookup),
            DatumDefinition.PointAt at => PointAt(at, owner, lookup),
            DatumDefinition.PointAtCentreOf centre => PointAtCentreOf(centre, owner, lookup),
            DatumDefinition.PointWhereAxisMeetsPlane cross => PointWhereAxisMeetsPlane(cross, owner, lookup),
            DatumDefinition.PointAlongEdge alongEdge => PointAlongEdge(alongEdge, owner, lookup),
            _ => throw new ArgumentOutOfRangeException(
                nameof(definition), definition, "Unknown datum definition kind."),
        };
    }

    /// <summary>Builds an entity source that resolves names afresh through the naming tiers.</summary>
    /// <param name="resolver">The resolver.</param>
    /// <param name="consumer">The feature holding the definition, for the history search.</param>
    /// <returns>The source.</returns>
    /// <remarks>
    /// For a caller that has a name and no answer yet — a tool building a datum interactively, or a
    /// test. A rebuild is not that caller: the engine has already resolved every declared reference
    /// by the time an evaluator runs, and resolving them a second time here would be work done
    /// twice that could come out differently, since the geometric tier scores candidates against a
    /// model the second pass would be reading in a different state.
    /// </remarks>
    public static Func<PersistentName, ResolvedReference> Through(
        NameResolver resolver, FeatureId consumer)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        return name =>
        {
            NameResolution resolution = resolver.Resolve(name, consumer);

            return new ResolvedReference(
                resolution.Outcome,
                resolution.IsResolved ? [resolution.Entity] : [],
                resolution.Reason);
        };
    }

    private static DatumResolution OffsetPlane(
        DatumDefinition.PlaneOffsetFrom definition, FeatureId owner, Lookup lookup)
    {
        Input<Plane> from = lookup.PlaneOf(definition.From);
        if (from.Failure is { } failure)
        {
            return failure;
        }

        if (!double.IsFinite(definition.Distance))
        {
            return NotANumber("offset distance");
        }

        Plane source = from.Value;

        return DatumResolution.Found(new ReferenceGeometry.Plane(
            owner,
            definition.Name,
            source.Origin + (source.Normal * definition.Distance),
            source.Normal));
    }

    private static DatumResolution PlaneThroughPoint(
        DatumDefinition.PlaneThroughPoint definition, FeatureId owner, Lookup lookup)
    {
        Input<Vec3d> through = lookup.PointOf(definition.Through);
        if (through.Failure is { } pointFailure)
        {
            return pointFailure;
        }

        Input<Plane> parallelTo = lookup.PlaneOf(definition.ParallelTo);
        if (parallelTo.Failure is { } planeFailure)
        {
            return planeFailure;
        }

        return DatumResolution.Found(new ReferenceGeometry.Plane(
            owner, definition.Name, through.Value, parallelTo.Value.Normal));
    }

    private static DatumResolution PlaneAtAngle(
        DatumDefinition.PlaneAtAngle definition, FeatureId owner, Lookup lookup)
    {
        Input<Plane> from = lookup.PlaneOf(definition.From);
        if (from.Failure is { } planeFailure)
        {
            return planeFailure;
        }

        Input<Line> about = lookup.AxisOf(definition.About);
        if (about.Failure is { } axisFailure)
        {
            return axisFailure;
        }

        if (!double.IsFinite(definition.Angle))
        {
            return NotANumber("angle");
        }

        Plane source = from.Value;
        Line axis = about.Value;

        // Both halves of "lies in the plane", and both are needed. Perpendicular to the normal
        // alone allows an axis parallel to the plane but off it, which rotates to an offset copy
        // at an angle of zero rather than to the plane it started from.
        if (!axis.Direction.IsPerpendicularTo(source.Normal))
        {
            return DatumResolution.Failed(
                DatumResolutionOutcome.Degenerate,
                "The axis is not parallel to the plane, so rotating the plane about it would not "
                    + "leave the axis in it.");
        }

        if (!source.Contains(axis.Origin))
        {
            return DatumResolution.Failed(
                DatumResolutionOutcome.Degenerate,
                "The axis is parallel to the plane but does not lie in it, so there is no angle at "
                    + "which the result would be the plane itself.");
        }

        Vec3d rotated = Quatd.FromAxisAngle(axis.Direction, definition.Angle).Rotate(source.Normal);

        return DatumResolution.Found(
            new ReferenceGeometry.Plane(owner, definition.Name, axis.Origin, rotated));
    }

    private static DatumResolution PlaneThroughThreePoints(
        DatumDefinition.PlaneThroughThreePoints definition, FeatureId owner, Lookup lookup)
    {
        Input<Vec3d> first = lookup.PointOf(definition.First);
        if (first.Failure is { } firstFailure)
        {
            return firstFailure;
        }

        Input<Vec3d> second = lookup.PointOf(definition.Second);
        if (second.Failure is { } secondFailure)
        {
            return secondFailure;
        }

        Input<Vec3d> third = lookup.PointOf(definition.Third);
        if (third.Failure is { } thirdFailure)
        {
            return thirdFailure;
        }

        try
        {
            Plane plane = Plane.FromThreePoints(first.Value, second.Value, third.Value);

            return DatumResolution.Found(new ReferenceGeometry.Plane(
                owner, definition.Name, plane.Origin, plane.Normal));
        }
        catch (InvalidOperationException)
        {
            return DatumResolution.Failed(
                DatumResolutionOutcome.Degenerate,
                "The three points are collinear or coincident, so they do not pick out one plane.");
        }
    }

    private static DatumResolution MidPlane(
        DatumDefinition.PlaneMidwayBetween definition, FeatureId owner, Lookup lookup)
    {
        Input<Plane> first = lookup.PlaneOf(definition.First);
        if (first.Failure is { } firstFailure)
        {
            return firstFailure;
        }

        Input<Plane> second = lookup.PlaneOf(definition.Second);
        if (second.Failure is { } secondFailure)
        {
            return secondFailure;
        }

        Plane a = first.Value;
        Plane b = second.Value;

        if (!a.Normal.IsParallelTo(b.Normal))
        {
            return DatumResolution.Failed(
                DatumResolutionOutcome.Degenerate,
                "The two planes are not parallel, and a pair that meet has two bisecting planes at "
                    + "right angles to each other rather than one midplane.");
        }

        // Two planes that face opposite ways are still parallel, and their midplane is the same
        // one either way round. Flipping the second into the first's orientation is what makes
        // averaging the signed distances mean the same thing for both.
        Plane aligned = Vec3d.Dot(a.Normal, b.Normal) < 0 ? b.Flipped() : b;
        double distance = (a.DistanceFromOrigin + aligned.DistanceFromOrigin) / 2;

        return DatumResolution.Found(new ReferenceGeometry.Plane(
            owner, definition.Name, a.Normal * distance, a.Normal));
    }

    private static DatumResolution AxisThroughTwoPoints(
        DatumDefinition.AxisThroughTwoPoints definition, FeatureId owner, Lookup lookup)
    {
        Input<Vec3d> from = lookup.PointOf(definition.From);
        if (from.Failure is { } fromFailure)
        {
            return fromFailure;
        }

        Input<Vec3d> to = lookup.PointOf(definition.To);
        if (to.Failure is { } toFailure)
        {
            return toFailure;
        }

        Vec3d direction = to.Value - from.Value;

        if (direction.IsZeroLength)
        {
            return DatumResolution.Failed(
                DatumResolutionOutcome.Degenerate,
                "The two points are in the same place, so they do not pick out a direction.");
        }

        return DatumResolution.Found(
            new ReferenceGeometry.Axis(owner, definition.Name, from.Value, direction));
    }

    private static DatumResolution AxisAlongEdge(
        DatumDefinition.AxisAlongEdge definition, FeatureId owner, Lookup lookup)
    {
        Input<Line> axis = lookup.AxisOf(definition.Edge);

        return axis.Failure ?? DatumResolution.Found(new ReferenceGeometry.Axis(
            owner, definition.Name, axis.Value.Origin, axis.Value.Direction));
    }

    private static DatumResolution AxisWherePlanesMeet(
        DatumDefinition.AxisWherePlanesMeet definition, FeatureId owner, Lookup lookup)
    {
        Input<Plane> first = lookup.PlaneOf(definition.First);
        if (first.Failure is { } firstFailure)
        {
            return firstFailure;
        }

        Input<Plane> second = lookup.PlaneOf(definition.Second);
        if (second.Failure is { } secondFailure)
        {
            return secondFailure;
        }

        Plane a = first.Value;
        Plane b = second.Value;
        Vec3d direction = Vec3d.Cross(a.Normal, b.Normal);

        if (direction.IsZeroLength)
        {
            return DatumResolution.Failed(
                DatumResolutionOutcome.Degenerate,
                "The two planes are parallel, so they either never meet or meet everywhere.");
        }

        // The point of the line closest to the world origin, which is the one in the plane the two
        // normals span: solving n1.p = d1 and n2.p = d2 for p = a*n1 + b*n2 gives this pair, whose
        // shared denominator is |n1 x n2|^2. Any point of the line would describe the same axis,
        // but this one is a function of the two planes alone, so a rebuild that changes nothing
        // reports the same origin -- which ADR-0011 asks of everything here.
        double cosine = Vec3d.Dot(a.Normal, b.Normal);
        double denominator = direction.LengthSquared;
        double alongFirst = (a.DistanceFromOrigin - (b.DistanceFromOrigin * cosine)) / denominator;
        double alongSecond = (b.DistanceFromOrigin - (a.DistanceFromOrigin * cosine)) / denominator;
        Vec3d origin = (a.Normal * alongFirst) + (b.Normal * alongSecond);

        return DatumResolution.Found(
            new ReferenceGeometry.Axis(owner, definition.Name, origin, direction));
    }

    private static DatumResolution AxisNormalToPlane(
        DatumDefinition.AxisNormalToPlane definition, FeatureId owner, Lookup lookup)
    {
        Input<Plane> plane = lookup.PlaneOf(definition.Plane);
        if (plane.Failure is { } planeFailure)
        {
            return planeFailure;
        }

        Input<Vec3d> through = lookup.PointOf(definition.Through);
        if (through.Failure is { } pointFailure)
        {
            return pointFailure;
        }

        return DatumResolution.Found(new ReferenceGeometry.Axis(
            owner, definition.Name, through.Value, plane.Value.Normal));
    }

    private static DatumResolution PointAt(
        DatumDefinition.PointAt definition, FeatureId owner, Lookup lookup)
    {
        Input<Vec3d> at = lookup.PointOf(definition.At);

        return at.Failure
            ?? DatumResolution.Found(new ReferenceGeometry.Point(owner, definition.Name, at.Value));
    }

    private static DatumResolution PointAtCentreOf(
        DatumDefinition.PointAtCentreOf definition, FeatureId owner, Lookup lookup)
    {
        Input<WorldCurve> curve = lookup.CurveOf(definition.Edge);
        if (curve.Failure is { } failure)
        {
            return failure;
        }

        if (curve.Value is not WorldCurve.Circle circle)
        {
            return DatumResolution.Failed(
                DatumResolutionOutcome.NotUsable,
                "That edge is straight, and a straight edge has no centre. Use a point along the "
                    + "edge at a fraction of one half for its midpoint.");
        }

        return DatumResolution.Found(
            new ReferenceGeometry.Point(owner, definition.Name, circle.Centre));
    }

    private static DatumResolution PointWhereAxisMeetsPlane(
        DatumDefinition.PointWhereAxisMeetsPlane definition, FeatureId owner, Lookup lookup)
    {
        Input<Line> axis = lookup.AxisOf(definition.Axis);
        if (axis.Failure is { } axisFailure)
        {
            return axisFailure;
        }

        Input<Plane> plane = lookup.PlaneOf(definition.Plane);
        if (plane.Failure is { } planeFailure)
        {
            return planeFailure;
        }

        if (!plane.Value.TryIntersectLine(axis.Value.Origin, axis.Value.Direction, out Vec3d point))
        {
            // Reported the same way whether the axis misses the plane or lies in it: an axis in
            // the plane crosses it everywhere, which is as much "no single point" as never
            // crossing at all.
            return DatumResolution.Failed(
                DatumResolutionOutcome.Degenerate,
                "The axis is parallel to the plane, so there is no one point where they meet.");
        }

        return DatumResolution.Found(new ReferenceGeometry.Point(owner, definition.Name, point));
    }

    private static DatumResolution PointAlongEdge(
        DatumDefinition.PointAlongEdge definition, FeatureId owner, Lookup lookup)
    {
        Input<WorldCurve> curve = lookup.CurveOf(definition.Edge);
        if (curve.Failure is { } failure)
        {
            return failure;
        }

        double fraction = definition.Fraction;

        // Written as a positive test so that a fraction of NaN is refused rather than falling
        // through every comparison a negated one would make.
        if (!(fraction >= 0 && fraction <= 1))
        {
            return DatumResolution.Failed(
                DatumResolutionOutcome.Degenerate,
                "A point along an edge needs a fraction from 0 to 1; the edge has no geometry "
                    + "outside its own extent.");
        }

        switch (curve.Value)
        {
            case WorldCurve.Line line:
                return DatumResolution.Found(new ReferenceGeometry.Point(
                    owner, definition.Name, Vec3d.Lerp(line.Start, line.End, fraction)));

            case WorldCurve.Circle circle:
                return PointAlongArc(definition, owner, circle, fraction);

            default:
                // WorldCurve is closed around those two, so this is a build that has grown a third
                // and not updated here -- the same call Resolve makes about a definition kind it
                // does not know.
                throw new ArgumentOutOfRangeException(
                    nameof(definition), curve.Value, "Unknown curve kind.");
        }
    }

    private static DatumResolution PointAlongArc(
        DatumDefinition.PointAlongEdge definition, FeatureId owner, WorldCurve.Circle circle, double fraction)
    {
        Vec3d normal;
        Vec3d xDirection;

        try
        {
            normal = circle.Normal.Normalized();
            xDirection = circle.XDirection.PerpendicularTo(normal).Normalized();
        }
        catch (InvalidOperationException)
        {
            // The same call SketchExternalReferenceResolver makes on the same shape of input:
            // degenerate rather than unusable, because the edge does not describe a circle at all
            // and there is nothing here this build is declining to handle.
            return DatumResolution.Failed(
                DatumResolutionOutcome.Degenerate,
                "That circular edge has no usable plane of its own.");
        }

        if (!(circle.Radius > 0) || !double.IsFinite(circle.Radius))
        {
            return DatumResolution.Failed(
                DatumResolutionOutcome.Degenerate, "That circular edge has no usable radius.");
        }

        // A full circle wraps to a sweep of exactly zero (WorldCurve.Circle.Sweep), which would put
        // every fraction at the start angle. IsFull is what tells the two apart.
        double span = circle.IsFull ? 2 * System.Math.PI : circle.Sweep;
        double angle = circle.StartAngle + (fraction * span);
        Vec3d yDirection = Vec3d.Cross(normal, xDirection);
        Vec3d offset = (xDirection * System.Math.Cos(angle)) + (yDirection * System.Math.Sin(angle));

        return DatumResolution.Found(new ReferenceGeometry.Point(
            owner, definition.Name, circle.Centre + (offset * circle.Radius)));
    }

    private static DatumResolution NotANumber(string what) => DatumResolution.Failed(
        DatumResolutionOutcome.Degenerate, $"The {what} is not a finite number.");

    /// <summary>An infinite line, as the axis-shaped inputs resolve to before becoming a datum.</summary>
    /// <param name="Origin">A point on it.</param>
    /// <param name="Direction">Which way it runs. Always unit length once resolved.</param>
    private readonly record struct Line(Vec3d Origin, Vec3d Direction);

    /// <summary>
    /// One input resolved into the geometric role a construction needed, or why it could not be.
    /// </summary>
    /// <typeparam name="T">What the role resolves to.</typeparam>
    /// <param name="Value">The resolved value, meaningful only when <paramref name="Failure"/> is null.</param>
    /// <param name="Failure">Why it could not be resolved, or null if it was.</param>
    private readonly record struct Input<T>(T Value, DatumResolution? Failure)
    {
        public static Input<T> Of(T value) => new(value, null);

        public static Input<T> From(DatumResolution failure) => new(default!, failure);

        public static Input<T> Failed(DatumResolutionOutcome outcome, string reason)
            => new(default!, DatumResolution.Failed(outcome, reason));
    }

    /// <summary>
    /// What every construction resolves its inputs through: the document, the naming tiers, and the
    /// three geometry queries.
    /// </summary>
    /// <remarks>
    /// Bundled rather than threaded through thirteen constructions as six parameters each. The
    /// roles below are the whole reason <see cref="DatumReference"/> names what is pointed at
    /// instead of what it is for: each asks the same reference a different question, and a face
    /// answers "what plane" while refusing "what point".
    /// </remarks>
    private sealed class Lookup(
        Document document,
        Func<PersistentName, ResolvedReference>? entityOf,
        Func<SubEntity, Plane?>? planeOf,
        Func<SubEntity, Vec3d?>? pointOf,
        Func<SubEntity, WorldCurve?>? curveOf)
    {
        /// <summary>Resolves an input to the plane it stands for.</summary>
        /// <param name="reference">The reference.</param>
        /// <returns>The plane, or why not.</returns>
        public Input<Plane> PlaneOf(DatumReference reference) => reference switch
        {
            DatumReference.OnTopology topology => FaceAsPlane(topology),
            DatumReference.OnGeometry named => GeometryAsPlane(named),
            _ => throw Unknown(reference),
        };

        /// <summary>Resolves an input to the point it stands for.</summary>
        /// <param name="reference">The reference.</param>
        /// <returns>The point, or why not.</returns>
        public Input<Vec3d> PointOf(DatumReference reference) => reference switch
        {
            DatumReference.OnTopology topology => VertexAsPoint(topology),
            DatumReference.OnGeometry named => GeometryAsPoint(named),
            _ => throw Unknown(reference),
        };

        /// <summary>Resolves an input to the infinite line it stands for.</summary>
        /// <param name="reference">The reference.</param>
        /// <returns>The line, or why not.</returns>
        public Input<Line> AxisOf(DatumReference reference) => reference switch
        {
            DatumReference.OnTopology topology => EdgeAsAxis(topology),
            DatumReference.OnGeometry named => GeometryAsAxis(named),
            _ => throw Unknown(reference),
        };

        /// <summary>Resolves an input to the curve of the edge it names.</summary>
        /// <param name="reference">The reference.</param>
        /// <returns>The curve, or why not.</returns>
        /// <remarks>
        /// The one role reference geometry can never fill. A datum axis is an infinite line and a
        /// datum plane is an infinite plane; neither has the extent that "the centre of" or "a
        /// fraction along" is asking about, so those constructions want an edge specifically rather
        /// than anything that happens to be line-shaped.
        /// </remarks>
        public Input<WorldCurve> CurveOf(DatumReference reference)
        {
            if (reference is not DatumReference.OnTopology topology)
            {
                return Input<WorldCurve>.Failed(
                    DatumResolutionOutcome.NotFound,
                    "This construction needs an edge, and reference geometry is not one.");
            }

            Input<SubEntity> edge = Entity(topology, SubEntityKind.Edge, "an edge");
            if (edge.Failure is { } failure)
            {
                return Input<WorldCurve>.From(failure);
            }

            WorldCurve? found = curveOf?.Invoke(edge.Value);

            return found is { } curve
                ? Input<WorldCurve>.Of(curve)
                : Input<WorldCurve>.Failed(
                    DatumResolutionOutcome.NotUsable,
                    "This build cannot describe that edge as a curve.");
        }

        private static ArgumentOutOfRangeException Unknown(DatumReference reference)
            => new(nameof(reference), reference, "Unknown datum reference kind.");

        private static Input<Line> Normalise(Vec3d origin, Vec3d direction, string complaint)
        {
            try
            {
                return Input<Line>.Of(new Line(origin, direction.Normalized()));
            }
            catch (InvalidOperationException)
            {
                return Input<Line>.Failed(
                    DatumResolutionOutcome.Degenerate,
                    $"{complaint}, so it does not describe an axis.");
            }
        }

        private static DatumResolution WrongKind(ReferenceGeometry found, string wanted)
            => DatumResolution.Failed(
                DatumResolutionOutcome.NotFound,
                $"'{found.Name}' is {Describe(found)}, not {wanted}.");

        private static string Describe(ReferenceGeometry geometry) => geometry switch
        {
            ReferenceGeometry.Plane => "a plane",
            ReferenceGeometry.Axis => "an axis",
            ReferenceGeometry.Point => "a point",
            ReferenceGeometry.CoordinateSystem => "a coordinate system",
            _ => "something this build does not recognise",
        };

        private Input<Plane> FaceAsPlane(DatumReference.OnTopology reference)
        {
            Input<SubEntity> face = Entity(reference, SubEntityKind.Face, "a face");
            if (face.Failure is { } failure)
            {
                return Input<Plane>.From(failure);
            }

            Plane? found = planeOf?.Invoke(face.Value);

            return found is { } plane
                ? Input<Plane>.Of(plane)
                : Input<Plane>.Failed(DatumResolutionOutcome.NotUsable, "That face is not flat.");
        }

        private Input<Plane> GeometryAsPlane(DatumReference.OnGeometry reference)
        {
            Input<ReferenceGeometry> geometry = Geometry(reference);
            if (geometry.Failure is { } failure)
            {
                return Input<Plane>.From(failure);
            }

            // A coordinate system is a located frame, and the plane it stands for is its XY plane
            // -- the same one SketchPlaneResolver already sketches on.
            (Vec3d origin, Vec3d normal) = geometry.Value switch
            {
                ReferenceGeometry.Plane plane => (plane.Origin, plane.Normal),
                ReferenceGeometry.CoordinateSystem frame => (frame.Origin, frame.ZAxis),
                _ => (Vec3d.Zero, Vec3d.Zero),
            };

            if (geometry.Value is not (ReferenceGeometry.Plane or ReferenceGeometry.CoordinateSystem))
            {
                return new Input<Plane>(default, WrongKind(geometry.Value, "a plane"));
            }

            try
            {
                return Input<Plane>.Of(Plane.FromPointNormal(origin, normal));
            }
            catch (InvalidOperationException)
            {
                return Input<Plane>.Failed(
                    DatumResolutionOutcome.Degenerate,
                    $"'{geometry.Value.Name}' has a degenerate normal and does not describe a plane.");
            }
        }

        private Input<Vec3d> VertexAsPoint(DatumReference.OnTopology reference)
        {
            Input<SubEntity> vertex = Entity(reference, SubEntityKind.Vertex, "a vertex");
            if (vertex.Failure is { } failure)
            {
                return Input<Vec3d>.From(failure);
            }

            Vec3d? found = pointOf?.Invoke(vertex.Value);

            return found is { } point
                ? Input<Vec3d>.Of(point)
                : Input<Vec3d>.Failed(
                    DatumResolutionOutcome.NotUsable, "That vertex has no position.");
        }

        private Input<Vec3d> GeometryAsPoint(DatumReference.OnGeometry reference)
        {
            Input<ReferenceGeometry> geometry = Geometry(reference);
            if (geometry.Failure is { } failure)
            {
                return Input<Vec3d>.From(failure);
            }

            return geometry.Value switch
            {
                ReferenceGeometry.Point point => Input<Vec3d>.Of(point.Position),

                // A coordinate system is a point as well as a frame: it is somewhere, and that
                // somewhere is its origin. Offered rather than refused because someone who built a
                // coordinate system to place something has already named the point they mean, and
                // making them build a second datum on top of it to say so is busywork.
                ReferenceGeometry.CoordinateSystem frame => Input<Vec3d>.Of(frame.Origin),
                _ => new Input<Vec3d>(default, WrongKind(geometry.Value, "a point")),
            };
        }

        private Input<Line> EdgeAsAxis(DatumReference.OnTopology reference)
        {
            Input<WorldCurve> curve = CurveOf(reference);
            if (curve.Failure is { } failure)
            {
                return Input<Line>.From(failure);
            }

            if (curve.Value is not WorldCurve.Line line)
            {
                return Input<Line>.Failed(
                    DatumResolutionOutcome.NotUsable,
                    "That edge is not straight, so it does not describe an axis.");
            }

            return Normalise(line.Start, line.End - line.Start, "That edge has no length");
        }

        private Input<Line> GeometryAsAxis(DatumReference.OnGeometry reference)
        {
            Input<ReferenceGeometry> geometry = Geometry(reference);
            if (geometry.Failure is { } failure)
            {
                return Input<Line>.From(failure);
            }

            return geometry.Value is ReferenceGeometry.Axis axis
                ? Normalise(axis.Origin, axis.Direction, $"'{axis.Name}' has no direction")
                : new Input<Line>(default, WrongKind(geometry.Value, "an axis"));
        }

        private Input<ReferenceGeometry> Geometry(DatumReference.OnGeometry reference)
        {
            ReferenceGeometry? found = document.FindReference(reference.Owner, reference.Name);

            return found is null
                ? Input<ReferenceGeometry>.Failed(
                    DatumResolutionOutcome.NotFound,
                    $"There is no reference geometry named '{reference.Name}'.")
                : Input<ReferenceGeometry>.Of(found);
        }

        private Input<SubEntity> Entity(
            DatumReference.OnTopology reference, SubEntityKind wanted, string description)
        {
            if (entityOf is null)
            {
                return Input<SubEntity>.Failed(
                    DatumResolutionOutcome.NotFound,
                    "No way to resolve a topology reference is available in this configuration.");
            }

            ResolvedReference resolution = entityOf(reference.Entity);

            if (resolution.Outcome == NameResolutionOutcome.Ambiguous)
            {
                return Input<SubEntity>.Failed(
                    DatumResolutionOutcome.Ambiguous,
                    resolution.Reason ?? "More than one entity answers to this reference.");
            }

            if (!resolution.IsResolved)
            {
                return Input<SubEntity>.Failed(
                    DatumResolutionOutcome.NotFound,
                    resolution.Reason ?? "The referenced entity could not be resolved.");
            }

            if (resolution.Entities.Length != 1)
            {
                // A reference declared AllDescendants and the entity it named has split. Reported
                // as ambiguous rather than taking the first: a datum input is one entity, and which
                // of the pieces was meant is exactly what nothing here knows.
                return Input<SubEntity>.Failed(
                    DatumResolutionOutcome.Ambiguous,
                    $"That reference came to {resolution.Entities.Length} entities, and a datum is "
                        + "built on one.");
            }

            SubEntity entity = resolution.Entities[0];

            // Naming an entity of the wrong kind is a mistake by whoever built the reference rather
            // than a fact about the model, but it is still reported as data: a rebuild that throws
            // on one bad reference takes the whole document with it (§5.4).
            return entity.Kind == wanted
                ? Input<SubEntity>.Of(entity)
                : Input<SubEntity>.Failed(
                    DatumResolutionOutcome.NotFound, $"The reference does not name {description}.");
        }
    }
}
