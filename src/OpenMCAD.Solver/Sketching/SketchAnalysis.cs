using System.Collections.Immutable;

namespace OpenMCAD.Solver.Sketching;

/// <summary>
/// A group of geometry that can be solved without reference to the rest of the sketch.
/// </summary>
/// <param name="Entities">
/// What is in it, in sketch order. Empty for the group holding constraints that act only on
/// ground: those have nothing to move, and still have to be checked, because a distance between
/// two fixed points can be wrong and a group that did not exist would never notice.
/// </param>
/// <param name="Constraints">The constraints acting within it, in the order they were made.</param>
/// <param name="Freedom">How many numbers place its geometry.</param>
/// <param name="Removes">How many of those the constraints take away.</param>
public sealed record Subsystem(
    ImmutableArray<SketchEntityId> Entities,
    ImmutableArray<SketchConstraintId> Constraints,
    int Freedom,
    int Removes)
{
    /// <summary>Gets how much freedom is left, before rank is taken into account.</summary>
    public int RemainingFreedom => Freedom - Removes;

    /// <inheritdoc/>
    public bool Equals(Subsystem? other)
        => other is not null
            && Freedom == other.Freedom
            && Removes == other.Removes
            && Entities.SequenceEqual(other.Entities)
            && Constraints.SequenceEqual(other.Constraints);

    /// <inheritdoc/>
    public override int GetHashCode()
        => HashCode.Combine(Entities.Length, Constraints.Length, Freedom, Removes);

    /// <inheritdoc/>
    public override string ToString()
        => $"{Entities.Length} entities, {Constraints.Length} constraints, "
            + $"{RemainingFreedom} free";
}

/// <summary>
/// What can be worked out about a sketch without solving it.
/// </summary>
/// <remarks>
/// <para>
/// P4-T05. Two things, and they come from the same walk of the constraint graph: which parameters
/// are pinned, and which groups of geometry are independent of each other.
/// </para>
/// <para>
/// The decomposition is what makes §5.6's 16 ms drag budget reachable. A sketch of two hundred
/// entities is almost never one problem — it is a dozen features that happen to share a plane —
/// and dragging a corner of one of them has no business refactorising the whole thing. Solving
/// only the affected group turns the cost from the size of the sketch into the size of the part
/// being touched.
/// </para>
/// <para>
/// Fully fixed geometry is ground and is deliberately left out of the graph. Two otherwise
/// unrelated shapes both dimensioned from the origin are not one problem, and a decomposition that
/// merged them through the origin would report a single subsystem for every sketch anyone ever
/// draws — which is the same as having no decomposition at all.
/// </para>
/// </remarks>
public sealed class SketchAnalysis
{
    private readonly Sketch _sketch;
    private readonly ImmutableDictionary<SketchEntityId, int> _cluster;

    private SketchAnalysis(
        Sketch sketch,
        SketchParameters parameters,
        ImmutableArray<int> frozen,
        ImmutableArray<SketchEntityId> ground,
        ImmutableDictionary<SketchEntityId, int> cluster,
        ImmutableArray<Subsystem> subsystems)
    {
        _sketch = sketch;
        _cluster = cluster;

        Parameters = parameters;
        FrozenParameters = frozen;
        Ground = ground;
        Subsystems = subsystems;
    }

    /// <summary>Gets the sketch this describes.</summary>
    /// <remarks>
    /// Held so a caller that built an analysis from a modified sketch — a drag seeds one before
    /// anything is solved — does not have to carry both and risk passing the wrong one.
    /// </remarks>
    public Sketch Sketch => _sketch;

    /// <summary>Gets the sketch laid out as a vector.</summary>
    public SketchParameters Parameters { get; }

    /// <summary>Gets the indices of the parameters no solver may move.</summary>
    public ImmutableArray<int> FrozenParameters { get; }

    /// <summary>Gets the entities that cannot move at all.</summary>
    public ImmutableArray<SketchEntityId> Ground { get; }

    /// <summary>Gets the independent groups, largest first.</summary>
    /// <remarks>
    /// Largest first because a caller solving them in turn wants the expensive one started first,
    /// and because a stable order at all is required: two runs that decomposed the same sketch
    /// differently would solve it differently, which ADR-0011 does not allow. Ties break on the
    /// first entity's position in the sketch, which is stable across processes where an id is not.
    /// </remarks>
    public ImmutableArray<Subsystem> Subsystems { get; }

    /// <summary>Gets the entities with freedom left, in sketch order.</summary>
    public ImmutableArray<SketchEntityId> FreeEntities =>
    [
        .. _sketch.Entities.Ordered
            .Where(e => !Ground.Contains(e.Id))
            .Select(e => e.Id),
    ];

    /// <summary>Works out what can be worked out.</summary>
    /// <param name="sketch">The sketch.</param>
    /// <returns>The analysis.</returns>
    public static SketchAnalysis Of(Sketch sketch)
    {
        ArgumentNullException.ThrowIfNull(sketch);

        SketchParameters parameters = SketchParameters.Of(sketch);

        ImmutableArray<int> frozen = FrozenBy(sketch, parameters);
        ImmutableArray<SketchEntityId> ground = GroundIn(sketch, parameters, frozen);

        (ImmutableDictionary<SketchEntityId, int> cluster, ImmutableArray<Subsystem> subsystems) =
            Decompose(sketch, ground);

        return new SketchAnalysis(sketch, parameters, frozen, ground, cluster, subsystems);
    }

    /// <summary>Finds the group a piece of geometry belongs to.</summary>
    /// <param name="entity">Which entity.</param>
    /// <returns>Its group, or null if it is ground or is not in the sketch.</returns>
    public Subsystem? Containing(SketchEntityId entity)
        => _cluster.TryGetValue(entity, out int index) ? Subsystems[index] : null;

    /// <summary>Cuts out one group as a sketch that can be solved on its own.</summary>
    /// <param name="subsystem">Which group.</param>
    /// <returns>The sketch.</returns>
    /// <remarks>
    /// The ground the group's constraints refer to comes with it, along with whatever fixes that
    /// ground in place. Leaving it out would give the sub-solve a free point where the whole sketch
    /// had a pinned one, and it would happily move geometry that in the real sketch cannot move.
    /// </remarks>
    public Sketch Restrict(Subsystem subsystem)
    {
        ArgumentNullException.ThrowIfNull(subsystem);

        HashSet<SketchEntityId> wanted = [.. subsystem.Entities];

        // A group with no entities still names geometry through its constraints: that is the whole
        // of what it is. Collecting the operands below is what brings that ground in.

        foreach (SketchConstraintId id in subsystem.Constraints)
        {
            foreach (SketchPointRef operand in _sketch.Constraints.Find(id)?.On ?? [])
            {
                wanted.Add(operand.Entity);
            }
        }

        Sketch part = Sketch.Empty;

        foreach (SketchEntity entity in _sketch.Entities.Ordered)
        {
            if (wanted.Contains(entity.Id))
            {
                part = part.With(entity);
            }
        }

        HashSet<SketchConstraintId> included = [.. subsystem.Constraints];

        foreach (SketchConstraint constraint in _sketch.Constraints.Ordered)
        {
            bool holdsGround = constraint.Kind == ConstraintKind.Fix
                && constraint.On.Length > 0
                && wanted.Contains(constraint.On[0].Entity);

            if (included.Contains(constraint.Id) || holdsGround)
            {
                part = part.With(constraint);
            }
        }

        return part;
    }

    /// <inheritdoc/>
    public override string ToString()
        => $"{Subsystems.Length} subsystems, {Ground.Length} entities fixed";

    /// <summary>Which parameters are pinned by a fix constraint.</summary>
    /// <param name="sketch">The sketch.</param>
    /// <param name="parameters">Its layout.</param>
    /// <returns>The indices, ascending.</returns>
    /// <remarks>
    /// Shared rather than left to each solver, and public so a solver can ask it of a sub-sketch
    /// without paying for a whole decomposition. A solver that worked out "fixed" for itself would
    /// be a second opinion about which numbers may move, and the decomposition depends on the same
    /// answer — two answers would put an entity in a cluster the solver then held still.
    /// </remarks>
    public static ImmutableArray<int> FrozenBy(Sketch sketch, SketchParameters parameters)
    {
        HashSet<int> frozen = [];

        foreach (SketchConstraint constraint in sketch.Constraints.Ordered)
        {
            if (constraint.Kind == ConstraintKind.Fix
                && constraint.IsDriving
                && constraint.On.Length > 0
                && parameters.IndexOf(constraint.On[0]) is { } at)
            {
                frozen.Add(at.X);
                frozen.Add(at.Y);
            }
        }

        return [.. frozen.Order()];
    }

    /// <summary>Which entities have no freedom left at all.</summary>
    private static ImmutableArray<SketchEntityId> GroundIn(
        Sketch sketch, SketchParameters parameters, ImmutableArray<int> frozen)
    {
        ImmutableArray<SketchEntityId>.Builder ground =
            ImmutableArray.CreateBuilder<SketchEntityId>();

        foreach (SketchEntity entity in sketch.Entities.Ordered)
        {
            int at = parameters.OffsetOf(entity.Id);
            int width = SketchParameters.WidthOf(entity);

            if (width > 0 && Enumerable.Range(at, width).All(frozen.Contains))
            {
                ground.Add(entity.Id);
            }
        }

        return ground.ToImmutable();
    }

    /// <summary>Splits the sketch into groups that share no freedom.</summary>
    /// <remarks>
    /// Union-find over the entities, joined by every driving constraint that names more than one of
    /// them. Ground is skipped, so a constraint to a fixed point joins nothing — which is what
    /// makes the decomposition worth having, since dimensioning from the origin is how sketches are
    /// drawn and would otherwise fuse every group into one.
    /// </remarks>
    private static (ImmutableDictionary<SketchEntityId, int>, ImmutableArray<Subsystem>) Decompose(
        Sketch sketch, ImmutableArray<SketchEntityId> ground)
    {
        Dictionary<SketchEntityId, SketchEntityId> parent = [];

        foreach (SketchEntity entity in sketch.Entities.Ordered)
        {
            if (!ground.Contains(entity.Id))
            {
                parent[entity.Id] = entity.Id;
            }
        }

        foreach (SketchConstraint constraint in sketch.Constraints.Ordered)
        {
            if (!constraint.IsDriving)
            {
                continue;
            }

            SketchEntityId? first = null;

            foreach (SketchPointRef operand in constraint.On)
            {
                if (!parent.ContainsKey(operand.Entity))
                {
                    continue;
                }

                if (first is { } anchor)
                {
                    Join(parent, anchor, operand.Entity);
                }
                else
                {
                    first = operand.Entity;
                }
            }
        }

        Dictionary<SketchEntityId, List<SketchEntityId>> groups = [];

        foreach (SketchEntity entity in sketch.Entities.Ordered)
        {
            if (!parent.ContainsKey(entity.Id))
            {
                continue;
            }

            SketchEntityId root = Root(parent, entity.Id);

            if (!groups.TryGetValue(root, out List<SketchEntityId>? members))
            {
                members = [];
                groups[root] = members;
            }

            members.Add(entity.Id);
        }

        ImmutableArray<SketchEntityId> order = [.. sketch.Entities.Ordered.Select(e => e.Id)];

        List<Subsystem> subsystems = [];

        // Constraints that act only on ground have no group of their own to fall into, and
        // dropping them would mean a distance between two fixed points -- which can perfectly well
        // be wrong -- was never evaluated by anybody. They get a group with no entities: nothing to
        // move, everything still checked.
        ImmutableArray<SketchConstraintId> onGroundAlone =
        [
            .. sketch.Constraints.Ordered
                .Where(c => c.IsDriving
                    && c.Kind != ConstraintKind.Fix
                    && c.On.Length > 0
                    && c.On.All(o => !parent.ContainsKey(o.Entity)))
                .Select(c => c.Id),
        ];

        foreach (List<SketchEntityId> members in groups.Values)
        {
            HashSet<SketchEntityId> inside = [.. members];

            ImmutableArray<SketchConstraintId> constraints =
            [
                .. sketch.Constraints.Ordered
                    .Where(c => c.IsDriving && c.On.Any(o => inside.Contains(o.Entity)))
                    .Select(c => c.Id),
            ];

            int freedom = members.Sum(
                id => sketch.Entities.Find(id) is { } entity ? SketchParameters.WidthOf(entity) : 0);

            int removes = constraints.Sum(
                id => sketch.Constraints.Find(id)?.Removes ?? 0);

            subsystems.Add(new Subsystem(
                [.. members.OrderBy(id => order.IndexOf(id))], constraints, freedom, removes));
        }

        if (!onGroundAlone.IsEmpty)
        {
            subsystems.Add(new Subsystem([], onGroundAlone, 0, 0));
        }

        ImmutableArray<Subsystem> ordered =
        [
            .. subsystems
                .OrderByDescending(s => s.Entities.Length)
                .ThenBy(s => s.Entities.IsEmpty ? int.MaxValue : order.IndexOf(s.Entities[0])),
        ];

        ImmutableDictionary<SketchEntityId, int>.Builder cluster =
            ImmutableDictionary.CreateBuilder<SketchEntityId, int>();

        for (int i = 0; i < ordered.Length; ++i)
        {
            foreach (SketchEntityId id in ordered[i].Entities)
            {
                cluster[id] = i;
            }
        }

        return (cluster.ToImmutable(), ordered);
    }

    private static SketchEntityId Root(
        Dictionary<SketchEntityId, SketchEntityId> parent, SketchEntityId id)
    {
        while (!parent[id].Equals(id))
        {
            parent[id] = parent[parent[id]];
            id = parent[id];
        }

        return id;
    }

    private static void Join(
        Dictionary<SketchEntityId, SketchEntityId> parent, SketchEntityId a, SketchEntityId b)
    {
        SketchEntityId rootA = Root(parent, a);
        SketchEntityId rootB = Root(parent, b);

        if (!rootA.Equals(rootB))
        {
            parent[rootB] = rootA;
        }
    }
}
