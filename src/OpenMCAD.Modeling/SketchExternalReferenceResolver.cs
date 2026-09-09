using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;
using OpenMCAD.Kernel;
using OpenMCAD.Math;
using OpenMCAD.Solver.Sketching;

namespace OpenMCAD.Modeling;

/// <summary>How resolving a <see cref="SketchExternalReference"/> came to.</summary>
public enum SketchExternalReferenceResolutionOutcome
{
    /// <summary>The reference produced usable sketch geometry.</summary>
    Resolved,

    /// <summary>Nothing answers to the reference, or it does not name an edge.</summary>
    NotFound,

    /// <summary>The edge traced to more than one candidate and nothing said which was meant.</summary>
    Ambiguous,

    /// <summary>
    /// This build cannot bring this particular edge in for this operation — a curve kind it does
    /// not project, or a circular edge whose plane is not parallel to the sketch plane.
    /// </summary>
    Unsupported,

    /// <summary>
    /// <see cref="SketchExternalReferenceOperation.Convert"/> was asked for an edge that is not
    /// already on the sketch plane.
    /// </summary>
    NotInPlane,

    /// <summary>
    /// <see cref="SketchExternalReferenceOperation.Intersect"/> was asked for an edge whose own
    /// extent does not reach the sketch plane.
    /// </summary>
    NoIntersection,

    /// <summary>
    /// The operation produced geometry that cannot be solved — a line collapsed to a point by a
    /// projection along its own length, for instance.
    /// </summary>
    Degenerate,
}

/// <summary>What resolving a <see cref="SketchExternalReference"/> came to.</summary>
/// <param name="Outcome">How it turned out.</param>
/// <param name="Entity">
/// The sketch entity, when <paramref name="Outcome"/> is
/// <see cref="SketchExternalReferenceResolutionOutcome.Resolved"/>.
/// </param>
/// <param name="Reason">Why, in words, when it could not be resolved.</param>
public sealed record SketchExternalReferenceResolution(
    SketchExternalReferenceResolutionOutcome Outcome,
    SketchEntity? Entity = null,
    string? Reason = null)
{
    /// <summary>Gets whether the reference produced usable geometry.</summary>
    public bool IsResolved => Outcome == SketchExternalReferenceResolutionOutcome.Resolved;

    /// <summary>Creates a resolution that produced geometry.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>The resolution.</returns>
    public static SketchExternalReferenceResolution Found(SketchEntity entity) => new(
        SketchExternalReferenceResolutionOutcome.Resolved, entity);

    /// <summary>Creates a resolution that failed.</summary>
    /// <param name="outcome">
    /// How it failed. Must not be <see cref="SketchExternalReferenceResolutionOutcome.Resolved"/>.
    /// </param>
    /// <param name="reason">Why, in words.</param>
    /// <returns>The resolution.</returns>
    public static SketchExternalReferenceResolution Failed(
        SketchExternalReferenceResolutionOutcome outcome, string reason) => new(outcome, null, reason);
}

/// <summary>
/// Turns a <see cref="SketchExternalReference"/> into the sketch geometry it names, against an
/// already-resolved <see cref="SketchPlane"/> (P4-T11).
/// </summary>
/// <remarks>
/// <para>
/// Takes a <see cref="SketchPlane"/> rather than a <see cref="Document"/> and re-deriving one: this
/// runs once per external reference and a sketch typically has several, so resolving the plane
/// itself is the caller's job, done once with <see cref="SketchPlaneResolver"/>, not this type's.
/// </para>
/// <para>
/// <b>Only lines project in full generality.</b> A line orthogonally projects onto any plane as a
/// line, degenerating only when it runs perpendicular to it. A circle does not: projected onto a
/// plane it is not parallel to, it becomes an ellipse in general, which is a real and useful
/// operation but a second one, not attempted here. What this build accepts for
/// <see cref="SketchExternalReferenceOperation.Project"/> and <see cref="SketchExternalReferenceOperation.Convert"/>
/// is a circular or arc edge whose own plane <em>is</em> parallel to the sketch plane — the common
/// case of bringing in a circular edge from a face parallel to the sketch — and anything else
/// resolves to <see cref="SketchExternalReferenceResolutionOutcome.Unsupported"/> rather than a
/// silently distorted ellipse nobody asked for.
/// </para>
/// <para>
/// <b><see cref="SketchExternalReferenceOperation.Intersect"/> is scoped to straight edges.</b> A
/// line crosses a plane at zero or one point, which is unambiguous. A circular edge can cross at
/// zero, one (tangent) or two, and "one external reference names one sketch entity" — deliberately,
/// the same choice <see cref="SketchExternalReference.Produces"/> makes for the same reason
/// <see cref="EntityReference"/>'s <see cref="MultiplicityPolicy"/> exists for kernel topology — has
/// nowhere to put a second point without inventing that policy here too. Left for when it is needed.
/// </para>
/// </remarks>
public static class SketchExternalReferenceResolver
{
    /// <summary>Resolves an external reference.</summary>
    /// <param name="reference">The reference.</param>
    /// <param name="plane">The sketch plane, already resolved.</param>
    /// <param name="consumer">The feature holding the reference, for the edge's history search.</param>
    /// <param name="edgeResolver">
    /// How to resolve the edge reference through the naming tiers (§5.3), or <see langword="null"/>
    /// if this configuration cannot — the reference then fails with
    /// <see cref="SketchExternalReferenceResolutionOutcome.NotFound"/> rather than throwing.
    /// </param>
    /// <param name="curveOf">
    /// How to get the world-space curve a resolved edge is, or <see langword="null"/> to the same
    /// effect.
    /// </param>
    /// <returns>The resolution.</returns>
    public static SketchExternalReferenceResolution Resolve(
        SketchExternalReference reference,
        SketchPlane plane,
        FeatureId consumer,
        NameResolver? edgeResolver = null,
        Func<SubEntity, WorldCurve?>? curveOf = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(plane);

        if (edgeResolver is null)
        {
            return SketchExternalReferenceResolution.Failed(
                SketchExternalReferenceResolutionOutcome.NotFound,
                "No way to resolve a 3-D edge reference is available in this configuration.");
        }

        NameResolution resolution = edgeResolver.Resolve(reference.Source, consumer);

        if (resolution.Outcome == NameResolutionOutcome.Ambiguous)
        {
            return SketchExternalReferenceResolution.Failed(
                SketchExternalReferenceResolutionOutcome.Ambiguous,
                resolution.Reason ?? "More than one edge answers to this reference.");
        }

        if (!resolution.IsResolved)
        {
            return SketchExternalReferenceResolution.Failed(
                SketchExternalReferenceResolutionOutcome.NotFound,
                resolution.Reason ?? "The referenced edge could not be resolved.");
        }

        if (resolution.Entity.Kind != SubEntityKind.Edge)
        {
            return SketchExternalReferenceResolution.Failed(
                SketchExternalReferenceResolutionOutcome.NotFound, "The reference does not name an edge.");
        }

        WorldCurve? curve = curveOf?.Invoke(resolution.Entity);

        if (curve is null)
        {
            return SketchExternalReferenceResolution.Failed(
                SketchExternalReferenceResolutionOutcome.Unsupported,
                "This build has no geometry for the referenced edge.");
        }

        return reference.Operation switch
        {
            SketchExternalReferenceOperation.Project => Project(reference, plane, curve),
            SketchExternalReferenceOperation.Convert => Convert(reference, plane, curve),
            SketchExternalReferenceOperation.Intersect => Intersect(reference, plane, curve),
            _ => throw new ArgumentOutOfRangeException(
                nameof(reference), reference.Operation, "Unknown external reference operation."),
        };
    }

    private static SketchExternalReferenceResolution Project(
        SketchExternalReference reference, SketchPlane plane, WorldCurve curve) => curve switch
        {
            WorldCurve.Line line => Finish(ProjectLine(reference, plane, line)),
            WorldCurve.Circle circle => ProjectCircle(reference, plane, circle),
            _ => SketchExternalReferenceResolution.Failed(
                SketchExternalReferenceResolutionOutcome.Unsupported,
                "This build does not project this kind of edge."),
        };

    private static SketchExternalReferenceResolution Convert(
        SketchExternalReference reference, SketchPlane plane, WorldCurve curve)
    {
        if (!LiesOnPlane(curve, plane))
        {
            return SketchExternalReferenceResolution.Failed(
                SketchExternalReferenceResolutionOutcome.NotInPlane,
                "This edge is not already on the sketch plane. Use Project instead.");
        }

        return Project(reference, plane, curve);
    }

    private static SketchExternalReferenceResolution Intersect(
        SketchExternalReference reference, SketchPlane plane, WorldCurve curve)
    {
        if (curve is not WorldCurve.Line line)
        {
            return SketchExternalReferenceResolution.Failed(
                SketchExternalReferenceResolutionOutcome.Unsupported,
                "This build can only intersect a straight edge with the sketch plane.");
        }

        Vec3d direction = line.End - line.Start;

        if (!plane.Plane.TryIntersectLine(line.Start, direction, out Vec3d hit)
            || !WithinSegment(line, direction, hit))
        {
            return SketchExternalReferenceResolution.Failed(
                SketchExternalReferenceResolutionOutcome.NoIntersection,
                "This edge does not cross the sketch plane within its own extent.");
        }

        return Finish(new SketchPoint(reference.Produces, plane.ToLocal(hit), reference.IsConstruction));
    }

    private static SketchLine ProjectLine(
        SketchExternalReference reference, SketchPlane plane, WorldCurve.Line line)
        => new SketchLine(
            reference.Produces, plane.ToLocal(line.Start), plane.ToLocal(line.End), reference.IsConstruction);

    private static SketchExternalReferenceResolution ProjectCircle(
        SketchExternalReference reference, SketchPlane plane, WorldCurve.Circle circle)
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
            // The edge itself does not describe a circle: no usable normal, or a reference
            // direction parallel to it. Degenerate rather than unsupported -- there is nothing
            // here this build is declining to handle.
            return SketchExternalReferenceResolution.Failed(
                SketchExternalReferenceResolutionOutcome.Degenerate,
                "This circular edge has no usable plane of its own, so it cannot be projected.");
        }

        double angleToSketch = normal.AngleTo(plane.Normal);
        bool antiparallel = angleToSketch >= System.Math.PI - Tolerance.Angular;

        if (angleToSketch > Tolerance.Angular && !antiparallel)
        {
            // Edge-on: the two planes are perpendicular, so the circle projects to a straight line
            // and there is no ellipse to build. Decided by the angle, alongside the parallel test
            // it belongs with, rather than by measuring the minor axis afterwards -- a length
            // threshold on a radius is wrong at one end of the range CAD spans, refusing a
            // perfectly reasonable aspect ratio for a millimetre-scale hole.
            //
            // SketchEllipse.Degeneracy would catch the resulting geometry regardless. This exists
            // so the reason names the view rather than the ellipse: "seen edge-on" is something the
            // user can act on, and "this ellipse has no minor radius" is about an entity they never
            // asked for.
            if (System.Math.Abs(angleToSketch - (System.Math.PI / 2)) <= Tolerance.Angular)
            {
                return SketchExternalReferenceResolution.Failed(
                    SketchExternalReferenceResolutionOutcome.Degenerate,
                    "This circular edge is seen exactly edge-on from the sketch plane, so it "
                    + "projects to a straight line rather than to an ellipse.");
            }

            return ProjectTransverseCircle(reference, plane, circle, normal, xDirection);
        }

        int sign = antiparallel ? -1 : 1;
        Vec2d localCentre = plane.ToLocal(circle.Centre);

        if (circle.IsFull)
        {
            return Finish(
                new SketchCircle(reference.Produces, localCentre, circle.Radius, reference.IsConstruction));
        }

        Vec2d localXDirection = plane.ToLocalDirection(xDirection);
        double rotationOffset = System.Math.Atan2(localXDirection.Y, localXDirection.X);

        // Antiparallel means the sketch views this circle's own plane from the opposite side to
        // the one its angles were measured from -- reversing which rotational sense looks
        // anticlockwise. SketchArc's convention is always-anticlockwise-from-start (P4-T03), so
        // representing the same physical arc means swapping which end is "start" as well as
        // negating the angle, not negating the angle alone: negating alone would keep the original
        // start point as the label "start" but describe the sketch sweeping the long way round to
        // reach the original end, which is a different arc from the one this edge actually is.
        double startAngle3D = sign > 0 ? circle.StartAngle : circle.EndAngle;
        double endAngle3D = sign > 0 ? circle.EndAngle : circle.StartAngle;

        return Finish(new SketchArc(
            reference.Produces,
            localCentre,
            circle.Radius,
            rotationOffset + (sign * startAngle3D),
            rotationOffset + (sign * endAngle3D),
            reference.IsConstruction));
    }

    /// <summary>
    /// Projects a circle whose own plane is at an angle to the sketch's, which is an ellipse.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Orthographic projection is linear, so a circle written as
    /// <c>C + r·cos θ·u + r·sin θ·v</c> lands as <c>c + cos θ·A + sin θ·B</c> with
    /// <c>A</c> and <c>B</c> the projections of its own two radius vectors. That is already an
    /// ellipse; what it is not is an ellipse in the form <see cref="SketchEllipse"/> stores, because
    /// <c>A</c> and <c>B</c> are in general neither perpendicular to one another nor of equal
    /// length. Finding the axes is one closed-form step: <c>|p(θ) − c|²</c> is a sinusoid in
    /// <c>2θ</c>, so its extremes are half a turn apart in <c>2θ</c> and therefore a quarter turn
    /// apart in <c>θ</c> — which is why one <c>atan2</c> gives both axes rather than only the
    /// longer.
    /// </para>
    /// <para>
    /// <b>The eccentric angle falls out for free, and that is the reason for doing it this way.</b>
    /// Because <c>A cos θ + B sin θ</c> is identically <c>M cos(θ − θ₀) + N sin(θ − θ₀)</c> for the
    /// axes found at <c>θ₀</c>, a point at circle-angle <c>θ</c> is at eccentric angle
    /// <c>θ − θ₀</c> exactly. <see cref="SketchEllipticalArc"/> stores eccentric angles (P4-T03)
    /// precisely so that no transcendental solve is needed to evaluate a point, and computing them
    /// here from the projected geometry rather than from the parameterisation would put that solve
    /// straight back in.
    /// </para>
    /// <para>
    /// <b>A basis that comes out clockwise has to be turned round, not merely negated.</b>
    /// <see cref="SketchEllipticalArc"/> sweeps anticlockwise from start to end, and its minor axis
    /// is a quarter turn anticlockwise from its major, so a projected frame with the opposite
    /// handedness describes the same physical arc traversed the other way. Negating the eccentric
    /// angles alone would keep the original start labelled "start" while describing a sweep the long
    /// way round to reach the original end — a different arc. The ends swap too, which is the same
    /// correction an antiparallel circular edge needs, arrived at independently and for the same
    /// underlying reason.
    /// </para>
    /// <para>
    /// Edge-on is refused before this is reached, by the caller, on the angle between the two
    /// planes — see there for why the test is an angle rather than a measurement of what came out.
    /// </para>
    /// </remarks>
    private static SketchExternalReferenceResolution ProjectTransverseCircle(
        SketchExternalReference reference,
        SketchPlane plane,
        WorldCurve.Circle circle,
        Vec3d normal,
        Vec3d xDirection)
    {
        // The circle's own frame. Angles on it are measured anticlockwise about its normal from
        // xDirection, so this is the right-handed pair that parameterisation assumes.
        Vec2d a = plane.ToLocalDirection(xDirection) * circle.Radius;
        Vec2d b = plane.ToLocalDirection(Vec3d.Cross(normal, xDirection)) * circle.Radius;

        double turn = System.Math.Atan2(
            2 * Vec2d.Dot(a, b), a.LengthSquared - b.LengthSquared) / 2;

        // `turn` is the maximum rather than either extreme: |p(θ) − c|² is
        // K + R·cos(2θ − ψ) with ψ the atan2 above, so the angle it names is where that cosine is
        // +1. The axis found there is therefore always the longer, and the swap this originally
        // carried -- to put the longer one in `major` -- could never run. Removed rather than kept
        // as insurance, because a branch no input can reach is a branch nothing can check.
        Vec2d major = (a * System.Math.Cos(turn)) + (b * System.Math.Sin(turn));
        Vec2d minor = (b * System.Math.Cos(turn)) - (a * System.Math.Sin(turn));

        // A left-handed projected frame means the sketch sees this circle turning the other way.
        bool reversed = Vec2d.Cross(major, minor) < 0;
        Vec2d centre = plane.ToLocal(circle.Centre);
        double rotation = major.Angle();

        if (circle.IsFull)
        {
            return Finish(new SketchEllipse(
                reference.Produces, centre, major.Length, minor.Length, rotation,
                reference.IsConstruction));
        }

        double start = circle.StartAngle - turn;
        double end = circle.EndAngle - turn;

        return Finish(new SketchEllipticalArc(
            reference.Produces,
            centre,
            major.Length,
            minor.Length,
            rotation,
            reversed ? -end : start,
            reversed ? -start : end,
            reference.IsConstruction));
    }

    private static bool LiesOnPlane(WorldCurve curve, SketchPlane plane) => curve switch
    {
        WorldCurve.Line line => plane.Plane.Contains(line.Start) && plane.Plane.Contains(line.End),
        WorldCurve.Circle circle => plane.Plane.Contains(circle.Centre),
        _ => false,
    };

    /// <summary>Whether a point already known to lie on the infinite line also lies on the segment.</summary>
    private static bool WithinSegment(WorldCurve.Line line, Vec3d direction, Vec3d point)
    {
        double lengthSquared = direction.LengthSquared;

        if (lengthSquared <= Tolerance.LinearResolution * Tolerance.LinearResolution)
        {
            return false;
        }

        double t = Vec3d.Dot(point - line.Start, direction) / lengthSquared;

        return t >= -Tolerance.Parametric && t <= 1 + Tolerance.Parametric;
    }

    /// <summary>Hands back a produced entity, unless the geometry it describes is degenerate.</summary>
    /// <remarks>
    /// Takes a non-null entity: every path that can fail now says why itself, in terms of what it
    /// was actually trying to do. It used to take a nullable one and explain every failure as
    /// "only a parallel circle can be brought in", which stopped being true the moment a transverse
    /// one could be projected and would have gone on being reported for a degenerate edge as well.
    /// </remarks>
    private static SketchExternalReferenceResolution Finish(SketchEntity entity)
    {
        return entity.Degeneracy is { } problem
            ? SketchExternalReferenceResolution.Failed(
                SketchExternalReferenceResolutionOutcome.Degenerate, problem)
            : SketchExternalReferenceResolution.Found(entity);
    }
}
