using System.Collections.Immutable;

namespace OpenMCAD.Solver.Assemblies;

/// <summary>
/// What can be said about an assembly's freedom without solving it (P5-T05, §5.9).
/// </summary>
/// <remarks>
/// <para>
/// Separate from the solve, the way <c>SketchAnalysis</c> is separate from <c>ISketchSolver</c>.
/// A degree-of-freedom readout is wanted while the user is still adding mates and long before
/// anything converges, and it has to survive a solve that failed — an assembly whose mates
/// contradict each other still has a sensible answer to "which components are not pinned down".
/// </para>
/// <para>
/// <b>What this counts, and what it cannot.</b> Six degrees of freedom for every body that is not
/// grounded, less what each mate removes on its own. That is exact when no two mates say the same
/// thing, and optimistic when they do: two mates repeating each other subtract twice while removing
/// freedom once. Telling those apart needs the rank of the constraint Jacobian, which needs the
/// numerical solve — so <see cref="Freedom"/> is reported as a <em>lower bound</em> and named as
/// one, rather than as a number that is usually right.
/// </para>
/// <para>
/// That honesty costs nothing here and would cost a great deal later. A status bar reading "0
/// degrees of freedom" on an assembly that can still be dragged is worse than one reading "at least
/// 0", because the first is a claim the user will trust and act on.
/// </para>
/// </remarks>
public static class MateAnalysis
{
    /// <summary>How much freedom an assembly has, and which bodies still hold it.</summary>
    /// <param name="bodies">The bodies, and which of them are fixed.</param>
    /// <param name="mates">What must be true of them.</param>
    /// <returns>What can be said without solving.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static MateFreedom Freedom(
        IReadOnlyCollection<MateBody> bodies, IReadOnlyCollection<AssemblyMate> mates)
    {
        ArgumentNullException.ThrowIfNull(bodies);
        ArgumentNullException.ThrowIfNull(mates);

        HashSet<MateBodyId> movable = [.. bodies.Where(b => !b.IsGrounded).Select(b => b.Id)];

        int total = movable.Count * 6;
        int removed = 0;

        ImmutableArray<UnsupportedMate>.Builder unsupported =
            ImmutableArray.CreateBuilder<UnsupportedMate>();

        foreach (AssemblyMate mate in mates)
        {
            MatePairing pairing = MatePairing.For(mate);

            if (!pairing.IsSupported)
            {
                unsupported.Add(new UnsupportedMate(mate.Id, pairing.Reason!));
                continue;
            }

            // A mate between two grounded bodies, or between a body and itself, removes nothing
            // that was ever free. Counting the first would report an assembly of fixed components
            // as over-constrained the moment anyone mated two of them; counting the second would
            // subtract freedom for a statement no rigid placement can change, which is what
            // AssemblyMate.Bodies collapsing to one entry is saying.
            if (mate.Bodies.Length != 2 || !mate.Bodies.Any(movable.Contains))
            {
                continue;
            }

            removed += pairing.RemovedFreedom;
        }

        return new MateFreedom(
            System.Math.Max(0, total - removed),
            total,
            removed,
            [.. movable.OrderBy(id => id.Value)],
            unsupported.ToImmutable());
    }

    /// <summary>Groups bodies that are joined by mates, directly or through others.</summary>
    /// <param name="bodies">The bodies.</param>
    /// <param name="mates">What joins them.</param>
    /// <returns>The groups, each a set of bodies that must be solved together.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// <para>
    /// The same union-find the sketch solver uses for subsystem decomposition (P4-T05), and for the
    /// same payoff: dragging one component should solve the mates that reach it and leave the rest
    /// of the product alone. An assembly makes this matter more than a sketch does, because the
    /// thing not being re-solved may be ten thousand other components.
    /// </para>
    /// <para>
    /// A grounded body joins no group. It is an anchor rather than an unknown, so two components
    /// mated to the same fixed base are not thereby made to move together — treating the base as a
    /// connection would merge every group in the assembly into one and give back exactly the
    /// whole-product solve this exists to avoid.
    /// </para>
    /// </remarks>
    public static ImmutableArray<ImmutableArray<MateBodyId>> Groups(
        IReadOnlyCollection<MateBody> bodies, IReadOnlyCollection<AssemblyMate> mates)
    {
        ArgumentNullException.ThrowIfNull(bodies);
        ArgumentNullException.ThrowIfNull(mates);

        Dictionary<MateBodyId, MateBodyId> parent = [];

        foreach (MateBody body in bodies.Where(b => !b.IsGrounded))
        {
            parent[body.Id] = body.Id;
        }

        foreach (AssemblyMate mate in mates)
        {
            if (!MatePairing.For(mate).IsSupported)
            {
                continue;
            }

            ImmutableArray<MateBodyId> joined = mate.Bodies;

            if (joined.Length != 2 || !parent.ContainsKey(joined[0]) || !parent.ContainsKey(joined[1]))
            {
                continue;
            }

            Union(parent, joined[0], joined[1]);
        }

        Dictionary<MateBodyId, List<MateBodyId>> groups = [];

        foreach (MateBodyId id in parent.Keys)
        {
            MateBodyId root = Find(parent, id);

            if (!groups.TryGetValue(root, out List<MateBodyId>? members))
            {
                groups[root] = members = [];
            }

            members.Add(id);
        }

        // Ordered by id within a group and by first member between groups, so that two runs on two
        // machines report the same decomposition -- the determinism ADR-0011 asks of everything.
        return
        [
            .. groups.Values
                .Select(members => members.OrderBy(id => id.Value).ToImmutableArray())
                .OrderBy(group => group[0].Value),
        ];
    }

    /// <summary>Which bodies moving one of them can disturb.</summary>
    /// <param name="bodies">The bodies, and which of them are fixed.</param>
    /// <param name="mates">What joins them.</param>
    /// <param name="from">The body being moved.</param>
    /// <returns>
    /// <paramref name="from"/> and everything a mate can carry the movement to, including the
    /// grounded bodies that stop it. Empty when <paramref name="from"/> is not one of the bodies.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// <para>
    /// A different question from <see cref="Groups"/>, and it took a drag to notice. Groups answers
    /// "which bodies must be solved together", and deliberately leaves grounded bodies out because
    /// they are anchors rather than unknowns. A drag asks "what does moving <em>this</em> disturb",
    /// and the answer has to start at a body the drag has just grounded — so the one Groups would
    /// exclude is the one this must begin from.
    /// </para>
    /// <para>
    /// <b>Movement stops at a grounded body rather than passing through it.</b> Drag a bracket
    /// mated to a fixed base which is mated to a cover, and the cover does not move: the base
    /// cannot, so nothing beyond it can be disturbed by way of it. The grounded body is still
    /// returned, because the solve needs it — it is what the dragged part is being positioned
    /// against — but the search does not expand from it. Traversing through would drag the whole
    /// product every time, which is the answer this exists to avoid.
    /// </para>
    /// </remarks>
    public static ImmutableArray<MateBodyId> Reaching(
        IReadOnlyCollection<MateBody> bodies,
        IReadOnlyCollection<AssemblyMate> mates,
        MateBodyId from)
    {
        ArgumentNullException.ThrowIfNull(bodies);
        ArgumentNullException.ThrowIfNull(mates);

        Dictionary<MateBodyId, MateBody> byId = bodies.ToDictionary(b => b.Id);

        if (!byId.ContainsKey(from))
        {
            return [];
        }

        Dictionary<MateBodyId, List<MateBodyId>> neighbours = [];

        foreach (AssemblyMate mate in mates)
        {
            if (!MatePairing.For(mate).IsSupported || mate.Bodies.Length != 2)
            {
                continue;
            }

            MateBodyId a = mate.Bodies[0];
            MateBodyId b = mate.Bodies[1];

            if (!byId.ContainsKey(a) || !byId.ContainsKey(b))
            {
                continue;
            }

            Add(neighbours, a, b);
            Add(neighbours, b, a);
        }

        HashSet<MateBodyId> found = [from];
        Queue<MateBodyId> pending = new();
        pending.Enqueue(from);

        while (pending.Count > 0)
        {
            MateBodyId at = pending.Dequeue();

            // The body the drag holds is grounded too, and expanding from it is the whole point --
            // so the check is on the ones reached rather than on the one started from.
            if (at != from && byId[at].IsGrounded)
            {
                continue;
            }

            if (!neighbours.TryGetValue(at, out List<MateBodyId>? next))
            {
                continue;
            }

            foreach (MateBodyId neighbour in next)
            {
                if (found.Add(neighbour))
                {
                    pending.Enqueue(neighbour);
                }
            }
        }

        // Ordered, so that two runs hand the solver the same sub-problem (ADR-0011).
        return [.. found.OrderBy(id => id.Value)];
    }

    private static void Add(
        Dictionary<MateBodyId, List<MateBodyId>> neighbours, MateBodyId from, MateBodyId to)
    {
        if (!neighbours.TryGetValue(from, out List<MateBodyId>? next))
        {
            neighbours[from] = next = [];
        }

        next.Add(to);
    }

    private static MateBodyId Find(Dictionary<MateBodyId, MateBodyId> parent, MateBodyId id)
    {
        while (parent[id] != id)
        {
            parent[id] = parent[parent[id]];
            id = parent[id];
        }

        return id;
    }

    private static void Union(
        Dictionary<MateBodyId, MateBodyId> parent, MateBodyId first, MateBodyId second)
    {
        MateBodyId a = Find(parent, first);
        MateBodyId b = Find(parent, second);

        if (a != b)
        {
            parent[a] = b;
        }
    }
}

/// <summary>How much an assembly can still move.</summary>
/// <param name="AtLeast">
/// A lower bound on the degrees of freedom left. Exact when no two mates say the same thing, and
/// too low when they do — see <see cref="MateAnalysis"/>.
/// </param>
/// <param name="Total">How many degrees of freedom the ungrounded bodies started with.</param>
/// <param name="Removed">How many the mates take away, counted one mate at a time.</param>
/// <param name="MovableBodies">The bodies that are not grounded.</param>
/// <param name="Unsupported">The mates this build cannot solve, which remove nothing.</param>
public sealed record MateFreedom(
    int AtLeast,
    int Total,
    int Removed,
    ImmutableArray<MateBodyId> MovableBodies,
    ImmutableArray<UnsupportedMate> Unsupported)
{
    /// <summary>Gets whether the mates take away at least as much freedom as there was.</summary>
    /// <remarks>
    /// Not the same as "fully constrained", and the difference is why this is named for what it
    /// measures. Mates can remove six degrees of freedom from a body and still leave it able to
    /// move, if two of them said the same thing — so this is a necessary condition for a solved
    /// assembly rather than a sufficient one, and only the solve can tell which.
    /// </remarks>
    public bool CouldBeFullyConstrained => AtLeast == 0;

    /// <summary>Gets whether more freedom has been removed than the bodies ever had.</summary>
    /// <remarks>
    /// A hint, not a verdict. It is the ordinary state of an assembly mated the way a person mates
    /// one — a bracket to a base, then a bolt to the bracket and to the base — where the last mate
    /// repeats what the first two implied. Reporting it as over-constrained would cry wolf; what it
    /// is good for is deciding that a solve is worth running before trusting a "fully constrained"
    /// readout.
    /// </remarks>
    public bool MayBeOverConstrained => Removed > Total;
}
