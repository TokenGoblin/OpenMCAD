using OpenMCAD.Math;

namespace OpenMCAD.Solver.Assemblies;

/// <summary>
/// What a mate measures: how far from satisfied it is, as numbers a least-squares step can drive to
/// zero (P5-T05).
/// </summary>
/// <remarks>
/// <para>
/// Separate from any solver, the way <c>ConstraintResiduals</c> is for sketches. The residuals are
/// the definition of what each mate means; a solver is one way of making them zero, and a second
/// solver must agree with the first about what it is solving.
/// </para>
/// <para>
/// <b>More residuals than degrees of freedom, on purpose.</b> Two planes are flush when their unit
/// normals sum to zero, which is three numbers describing a two-dimensional condition. The
/// alternative — picking two independent directions and measuring against those — needs a basis
/// chosen from the geometry, and any such choice degenerates for some orientation of the bodies.
/// Writing the redundant form and letting the rank come out at two is well conditioned everywhere,
/// and it is the rank of the assembled Jacobian that tells a redundant mate from a constraining one.
/// <see cref="Count"/> is deliberately not equal to <see cref="MatePairing.RemovedFreedom"/>: the
/// first is how many numbers the mate contributes to the system, the second how much freedom it
/// actually removes, and only a rank taken at the solution reconciles them.
/// </para>
/// </remarks>
public static class MateResiduals
{
    /// <summary>Measures how far a mate is from being satisfied.</summary>
    /// <param name="mate">The mate.</param>
    /// <param name="first">Where the body carrying the first element sits.</param>
    /// <param name="second">Where the body carrying the second element sits.</param>
    /// <returns>
    /// The residuals, all zero when the mate holds. Empty when this build cannot solve the
    /// combination, which <see cref="MatePairing"/> is what reports.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="mate"/> is null.</exception>
    public static double[] Of(AssemblyMate mate, Transform first, Transform second)
    {
        ArgumentNullException.ThrowIfNull(mate);

        if (!MatePairing.For(mate).IsSupported)
        {
            return [];
        }

        MateElement a = mate.First.Element.PlacedBy(first);
        MateElement b = mate.Second.Element.PlacedBy(second);

        return (mate.Kind, a, b) switch
        {
            (MateKind.Coincident, MateElement.Plane p, MateElement.Plane q)
                => Planes(p, q, 0, mate.IsFlipped),

            (MateKind.Distance, MateElement.Plane p, MateElement.Plane q)
                => Planes(p, q, mate.Value, mate.IsFlipped),

            (MateKind.Coincident, MateElement.Point p, MateElement.Point q)
                => [q.Position.X - p.Position.X, q.Position.Y - p.Position.Y, q.Position.Z - p.Position.Z],

            (MateKind.Distance, MateElement.Point p, MateElement.Point q)
                => [Vec3d.Distance(p.Position, q.Position) - mate.Value],

            (MateKind.Coincident, MateElement.Point p, MateElement.Plane q) => [Gap(q, p, 0)],
            (MateKind.Coincident, MateElement.Plane p, MateElement.Point q) => [Gap(p, q, 0)],

            (MateKind.Distance, MateElement.Point p, MateElement.Plane q) => [Gap(q, p, mate.Value)],
            (MateKind.Distance, MateElement.Plane p, MateElement.Point q) => [Gap(p, q, mate.Value)],

            (MateKind.Concentric, MateElement.Axis p, MateElement.Axis q) => Axes(p, q),

            (MateKind.Concentric, MateElement.Axis p, MateElement.Point q) => OffAxis(p, q.Position),
            (MateKind.Concentric, MateElement.Point p, MateElement.Axis q) => OffAxis(q, p.Position),

            _ => [],
        };
    }

    /// <summary>How many numbers a mate contributes to the system.</summary>
    /// <param name="mate">The mate.</param>
    /// <returns>The count, zero for a combination this build cannot solve.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mate"/> is null.</exception>
    /// <remarks>
    /// Measured rather than tabulated, by asking for the residuals of two identity placements. A
    /// second table would be a second thing to keep in step with <see cref="Of"/>, and the one that
    /// drifted would produce a Jacobian whose rows did not line up with its residuals.
    /// </remarks>
    public static int Count(AssemblyMate mate)
        => Of(mate, Transform.Identity, Transform.Identity).Length;

    /// <summary>Two planes flush, or held a distance apart.</summary>
    /// <remarks>
    /// Four numbers for three degrees of freedom. The first three say the normals oppose — or agree,
    /// when the mate is flipped, which is what lets a user mate two faces without rebuilding one of
    /// the components the other way up. The fourth is the gap along the normal, which is where a
    /// distance mate differs from a coincident one and nowhere else: a distance mate <em>is</em> a
    /// coincident mate with an offset, which is why they share a residual and a freedom count.
    /// </remarks>
    private static double[] Planes(
        MateElement.Plane first, MateElement.Plane second, double offset, bool flipped)
    {
        if (!first.Normal.TryNormalize(out Vec3d u) || !second.Normal.TryNormalize(out Vec3d v))
        {
            // A degenerate normal cannot be driven anywhere useful. Zero residuals say "this mate
            // asks nothing", which is honest: there is nothing here to solve towards.
            return [0, 0, 0, 0];
        }

        Vec3d wanted = flipped ? u : -u;
        Vec3d error = v - wanted;

        return [error.X, error.Y, error.Z, Vec3d.Dot(u, second.Origin - first.Origin) - offset];
    }

    /// <summary>Two axes on the same line.</summary>
    /// <remarks>
    /// Six numbers for four degrees of freedom. The cross product is zero when the directions are
    /// parallel <em>or</em> anti-parallel, which is deliberate: a shaft is in a hole whichever way
    /// round it went in, and a mate that cared would be §5.9's parallel mate rather than this one.
    /// The rest is the offset between the lines, measured across the first axis so that sliding
    /// along it is free.
    /// </remarks>
    private static double[] Axes(MateElement.Axis first, MateElement.Axis second)
    {
        if (!first.Direction.TryNormalize(out Vec3d u) || !second.Direction.TryNormalize(out Vec3d v))
        {
            return [0, 0, 0, 0, 0, 0];
        }

        Vec3d turn = Vec3d.Cross(u, v);
        Vec3d across = (second.Origin - first.Origin).PerpendicularTo(u);

        return [turn.X, turn.Y, turn.Z, across.X, across.Y, across.Z];
    }

    /// <summary>A point on a line.</summary>
    private static double[] OffAxis(MateElement.Axis axis, Vec3d point)
    {
        if (!axis.Direction.TryNormalize(out Vec3d u))
        {
            return [0, 0, 0];
        }

        Vec3d across = (point - axis.Origin).PerpendicularTo(u);

        return [across.X, across.Y, across.Z];
    }

    /// <summary>How far a point is from a plane, along its normal.</summary>
    private static double Gap(MateElement.Plane plane, MateElement.Point point, double offset)
        => plane.Normal.TryNormalize(out Vec3d n)
            ? Vec3d.Dot(n, point.Position - plane.Origin) - offset
            : 0;
}
