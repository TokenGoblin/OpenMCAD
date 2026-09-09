namespace OpenMCAD.Solver.Assemblies;

/// <summary>Whether a mate's two ends fit together, and what it costs if they do.</summary>
/// <param name="IsSupported">Whether this build can solve that combination.</param>
/// <param name="RemovedFreedom">
/// How many of the six degrees of freedom between two bodies the mate takes away, when it is
/// supported.
/// </param>
/// <param name="Reason">Why not, when it is not supported.</param>
public readonly record struct MatePairing(bool IsSupported, int RemovedFreedom, string? Reason)
{
    /// <summary>A combination this build solves.</summary>
    /// <param name="removedFreedom">How much freedom it takes away.</param>
    /// <returns>The pairing.</returns>
    public static MatePairing Supported(int removedFreedom) => new(true, removedFreedom, null);

    /// <summary>A combination this build does not solve.</summary>
    /// <param name="reason">Why not, in words.</param>
    /// <returns>The pairing.</returns>
    public static MatePairing Rejected(string reason) => new(false, 0, reason);

    /// <summary>Says whether a mate's ends fit together, and what it costs.</summary>
    /// <param name="mate">The mate.</param>
    /// <returns>The pairing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mate"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// <b>The freedom counts are the standard ones, and worth stating rather than deriving.</b> Two
    /// unconstrained rigid bodies have six degrees of freedom between them, three translational and
    /// three rotational. A planar mate leaves the two bodies able to slide in two directions and
    /// spin about the shared normal, so it removes three. A concentric mate leaves a slide along the
    /// axis and a spin about it, so it removes four. Point to point pins the position and leaves all
    /// three rotations, so it removes three. A distance between two points fixes only the length of
    /// the vector between them, so it removes one.
    /// </para>
    /// <para>
    /// <b>What this count is not.</b> It is what each mate removes <em>on its own</em>. Two mates
    /// can say the same thing, in which case the second removes nothing and a sum over mates
    /// overstates how constrained the assembly is. Finding that out needs the rank of the Jacobian,
    /// which needs the numerical solve — so <see cref="MateAnalysis"/> reports this sum as a lower
    /// bound on the freedom remaining and says so, rather than pretending to a figure it cannot
    /// justify yet.
    /// </para>
    /// <para>
    /// <b>Combinations are refused rather than approximated.</b> A concentric mate between two
    /// points says nothing; a distance between an axis and a plane is ambiguous until somebody
    /// decides whether it means the nearest approach or the parallel offset, and inventing an answer
    /// there would be the kind of plausible-but-wrong result §5.3 would rather refuse. The same call
    /// <c>DatumResolver</c> makes about the constructions it will not attempt.
    /// </para>
    /// </remarks>
    public static MatePairing For(AssemblyMate mate)
    {
        ArgumentNullException.ThrowIfNull(mate);

        return (mate.Kind, mate.First.Element, mate.Second.Element) switch
        {
            // Faces brought flush. Two translations in the plane and one rotation about the normal
            // survive.
            (MateKind.Coincident, MateElement.Plane, MateElement.Plane) => Supported(3),

            // A point pinned to another. Every rotation survives, which is why this is three and not
            // six -- a point has no orientation to align.
            (MateKind.Coincident, MateElement.Point, MateElement.Point) => Supported(3),

            // A point held on a plane. Only the approach along the normal is removed.
            (MateKind.Coincident, MateElement.Point, MateElement.Plane) => Supported(1),
            (MateKind.Coincident, MateElement.Plane, MateElement.Point) => Supported(1),

            // A shaft in a hole: slide along the axis and spin about it survive.
            (MateKind.Concentric, MateElement.Axis, MateElement.Axis) => Supported(4),

            // A point on a line, which is what a concentric mate between an axis and a point means:
            // the two translations across the axis go, the slide along it stays.
            (MateKind.Concentric, MateElement.Axis, MateElement.Point) => Supported(2),
            (MateKind.Concentric, MateElement.Point, MateElement.Axis) => Supported(2),

            // Parallel planes held apart. The same freedoms survive as for coincident faces, which
            // is why the count matches: a distance mate is a coincident mate with an offset.
            (MateKind.Distance, MateElement.Plane, MateElement.Plane) => Supported(3),

            // A point held at a distance from a plane -- the offset version of point-on-plane.
            (MateKind.Distance, MateElement.Point, MateElement.Plane) => Supported(1),
            (MateKind.Distance, MateElement.Plane, MateElement.Point) => Supported(1),

            // Two points a fixed distance apart. This is a sphere of positions, not a point, so it
            // removes one degree of freedom rather than three.
            (MateKind.Distance, MateElement.Point, MateElement.Point) => Supported(1),

            (MateKind.Concentric, MateElement.Point, MateElement.Point) => Rejected(
                "Two points are concentric wherever they are, so the mate would say nothing."),

            (MateKind.Distance, MateElement.Axis, _) or (MateKind.Distance, _, MateElement.Axis)
                => Rejected(
                    "A distance to an axis is ambiguous: it could mean the nearest approach or the "
                    + "offset between parallel lines, and this build does not guess."),

            _ => Rejected(
                $"A {mate.Kind} mate between {mate.First.Element.Shape} and "
                + $"{mate.Second.Element.Shape} is not something this build solves."),
        };
    }
}
