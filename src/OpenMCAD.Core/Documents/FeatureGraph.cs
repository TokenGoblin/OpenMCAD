using System.Collections.Immutable;

namespace OpenMCAD.Core.Documents;

/// <summary>
/// A feature that names an input which is not in the document.
/// </summary>
/// <param name="Feature">The feature holding the reference.</param>
/// <param name="MissingInput">What it says it consumes.</param>
/// <remarks>
/// Reported rather than thrown. Deleting a feature that others consume is a normal thing to do,
/// and it is not the delete that is wrong — it is the features left pointing at nothing, which
/// need to be marked in error and shown to the user (P3-T07). Refusing to build the graph at all
/// would leave the document unopenable and give them no way to see what to fix.
/// </remarks>
public readonly record struct DanglingInput(FeatureId Feature, FeatureId MissingInput);

/// <summary>
/// The dependency graph of a document's features: what must be evaluated before what.
/// </summary>
/// <remarks>
/// <para>
/// Built from what each feature declares it consumes, never from the order they appear in the tree
/// (§5.4). The tree is a sequence the user arranged and can rearrange; this is what actually
/// constrains evaluation. Reading the sequence as the graph would make every feature depend on the
/// one above it, so changing the first would rebuild all of them and reordering would never be safe.
/// </para>
/// <para>
/// <b>Deterministic where it has a choice.</b> A topological order is not unique — at any point
/// several features may be ready — and any of them would be correct. Which one is picked is
/// therefore free, and spending that freedom on reproducibility is worth more than spending it on
/// anything else: the same document must rebuild in the same order every time, or a cache key, a
/// regression baseline and a bug report all stop meaning anything. Ties are broken by position in
/// the tree, which is stable, meaningful to the user, and survives a save. Not by id, which is
/// random and would give a different order in the next process (P1-T12's determinism audit found
/// exactly this class of defect elsewhere).
/// </para>
/// <para>
/// A snapshot, not a live view. It describes the document it was built from, and an edited document
/// needs a new one. Building is a walk of the features and their inputs, which is cheap next to
/// anything that would use the result.
/// </para>
/// </remarks>
public sealed class FeatureGraph
{
    private readonly ImmutableDictionary<FeatureId, ImmutableArray<FeatureId>> _dependencies;
    private readonly ImmutableDictionary<FeatureId, ImmutableArray<FeatureId>> _dependents;

    private FeatureGraph(
        ImmutableArray<FeatureId> order,
        ImmutableDictionary<FeatureId, ImmutableArray<FeatureId>> dependencies,
        ImmutableDictionary<FeatureId, ImmutableArray<FeatureId>> dependents,
        ImmutableArray<DanglingInput> dangling)
    {
        EvaluationOrder = order;
        _dependencies = dependencies;
        _dependents = dependents;
        Dangling = dangling;
    }

    /// <summary>
    /// Gets every feature, ordered so that each appears after everything it consumes.
    /// </summary>
    public ImmutableArray<FeatureId> EvaluationOrder { get; }

    /// <summary>Gets the references that point at features the document does not contain.</summary>
    public ImmutableArray<DanglingInput> Dangling { get; }

    /// <summary>Gets whether every declared input resolves to a feature.</summary>
    public bool IsComplete => Dangling.IsEmpty;

    /// <summary>Builds the graph for a document.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The graph.</returns>
    /// <exception cref="FeatureCycleException">
    /// The features depend on one another in a loop. The exception names the loop.
    /// </exception>
    public static FeatureGraph Build(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);

        Dictionary<FeatureId, int> position = [];

        for (int i = 0; i < document.Features.Length; ++i)
        {
            position[document.Features[i].Id] = i;
        }

        ImmutableArray<DanglingInput>.Builder dangling =
            ImmutableArray.CreateBuilder<DanglingInput>();

        Dictionary<FeatureId, ImmutableArray<FeatureId>> dependencies = [];
        Dictionary<FeatureId, List<FeatureId>> dependents = [];

        foreach (Feature feature in document.Features)
        {
            dependents.TryAdd(feature.Id, []);
        }

        foreach (Feature feature in document.Features)
        {
            ImmutableArray<FeatureId>.Builder resolved = ImmutableArray.CreateBuilder<FeatureId>();

            foreach (FeatureId input in feature.Inputs)
            {
                if (!position.ContainsKey(input))
                {
                    dangling.Add(new DanglingInput(feature.Id, input));
                    continue;
                }

                // A feature may legitimately name the same input twice — a boolean of a body with
                // itself is nonsense, but a pattern seeded from one feature and bounded by the same
                // one is not. The edge is recorded once, so the in-degree counting below stays
                // correct; recording it twice would leave a count that never reaches zero.
                if (!resolved.Contains(input))
                {
                    resolved.Add(input);
                    dependents[input].Add(feature.Id);
                }
            }

            dependencies[feature.Id] = resolved.ToImmutable();
        }

        ImmutableArray<FeatureId> order = Order(document, dependencies, dependents, position);

        return new FeatureGraph(
            order,
            dependencies.ToImmutableDictionary(),
            dependents.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToImmutableArray()),
            dangling.ToImmutable());
    }

    /// <summary>Gets what a feature directly consumes.</summary>
    /// <param name="id">Which feature.</param>
    /// <returns>Its inputs that resolve, in declaration order, without duplicates.</returns>
    public ImmutableArray<FeatureId> DependenciesOf(FeatureId id)
        => _dependencies.TryGetValue(id, out ImmutableArray<FeatureId> found) ? found : [];

    /// <summary>Gets what directly consumes a feature.</summary>
    /// <param name="id">Which feature.</param>
    /// <returns>The features naming it as an input, in tree order.</returns>
    public ImmutableArray<FeatureId> DependentsOf(FeatureId id)
        => _dependents.TryGetValue(id, out ImmutableArray<FeatureId> found) ? found : [];

    /// <summary>
    /// Gets everything that has to be rebuilt because one of the given features changed.
    /// </summary>
    /// <param name="seeds">The features that were edited.</param>
    /// <returns>
    /// The seeds and everything reachable from them, in evaluation order.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The seeds are included. A feature whose parameters changed has to be rebuilt itself, not
    /// merely have its consumers rebuilt — and a caller that wanted only the consumers can ask for
    /// the dependents directly.
    /// </para>
    /// <para>
    /// Returned in evaluation order rather than in discovery order, because that is the order the
    /// result has to be executed in, and a caller that received a set would have to sort it against
    /// this same graph to get anywhere.
    /// </para>
    /// </remarks>
    public ImmutableArray<FeatureId> AffectedBy(IEnumerable<FeatureId> seeds)
    {
        ArgumentNullException.ThrowIfNull(seeds);

        HashSet<FeatureId> affected = [];
        Queue<FeatureId> pending = new();

        foreach (FeatureId seed in seeds)
        {
            // Seeds that are not in the document are skipped rather than rejected. A removed
            // feature is a perfectly ordinary seed — it is why its former consumers are dirty —
            // and it has no node of its own to walk from.
            if (affected.Add(seed) && _dependents.ContainsKey(seed))
            {
                pending.Enqueue(seed);
            }
        }

        while (pending.Count > 0)
        {
            foreach (FeatureId dependent in DependentsOf(pending.Dequeue()))
            {
                if (affected.Add(dependent))
                {
                    pending.Enqueue(dependent);
                }
            }
        }

        ImmutableArray<FeatureId>.Builder ordered = ImmutableArray.CreateBuilder<FeatureId>();

        foreach (FeatureId id in EvaluationOrder)
        {
            if (affected.Contains(id))
            {
                ordered.Add(id);
            }
        }

        return ordered.ToImmutable();
    }

    /// <summary>Kahn's algorithm, with ties broken by position in the tree.</summary>
    private static ImmutableArray<FeatureId> Order(
        Document document,
        Dictionary<FeatureId, ImmutableArray<FeatureId>> dependencies,
        Dictionary<FeatureId, List<FeatureId>> dependents,
        Dictionary<FeatureId, int> position)
    {
        Dictionary<FeatureId, int> remaining = [];

        foreach ((FeatureId id, ImmutableArray<FeatureId> inputs) in dependencies)
        {
            remaining[id] = inputs.Length;
        }

        // A priority queue keyed by tree position, so that when several features are ready the one
        // highest in the tree goes first. A plain queue would order by the accident of which
        // dependency finished last, which is stable within a run and not between two.
        PriorityQueue<FeatureId, int> ready = new();

        foreach (Feature feature in document.Features)
        {
            if (remaining[feature.Id] == 0)
            {
                ready.Enqueue(feature.Id, position[feature.Id]);
            }
        }

        ImmutableArray<FeatureId>.Builder order = ImmutableArray.CreateBuilder<FeatureId>();

        while (ready.TryDequeue(out FeatureId id, out _))
        {
            order.Add(id);

            foreach (FeatureId dependent in dependents[id])
            {
                if (--remaining[dependent] == 0)
                {
                    ready.Enqueue(dependent, position[dependent]);
                }
            }
        }

        if (order.Count != document.Features.Length)
        {
            // Whatever never reached an in-degree of zero is in a cycle or downstream of one.
            HashSet<FeatureId> stuck = [];

            foreach ((FeatureId id, int count) in remaining)
            {
                if (count > 0)
                {
                    stuck.Add(id);
                }
            }

            throw Cycle(document, dependencies, stuck);
        }

        return order.ToImmutable();
    }

    /// <summary>Finds one actual loop among the features that could not be ordered.</summary>
    /// <remarks>
    /// Kahn's algorithm reports that a cycle exists but not what is in it: everything downstream of
    /// a loop is stuck too, so the leftovers are a superset. A depth-first walk of just those finds
    /// a back edge, and the section of the stack from that edge onwards is a real loop — which is
    /// what the user needs, rather than a list of everything the loop spoiled.
    /// </remarks>
    private static FeatureCycleException Cycle(
        Document document,
        Dictionary<FeatureId, ImmutableArray<FeatureId>> dependencies,
        HashSet<FeatureId> stuck)
    {
        HashSet<FeatureId> finished = [];
        List<FeatureId> path = [];
        HashSet<FeatureId> onPath = [];

        foreach (Feature feature in document.Features)
        {
            if (stuck.Contains(feature.Id)
                && !finished.Contains(feature.Id)
                && Walk(feature.Id, dependencies, stuck, finished, path, onPath) is { } loop)
            {
                ImmutableArray<string> names = [.. loop.Select(
                    id => document.FindFeature(id)?.Name ?? id.ToString())];

                return new FeatureCycleException(loop, names);
            }
        }

        // Unreachable while Kahn and this walk agree about what an edge is. Kept because a
        // wrong-looking exception is a better failure than a null one.
        return new FeatureCycleException(
            "These features depend on one another in a loop, but the loop could not be traced.");
    }

    private static ImmutableArray<FeatureId>? Walk(
        FeatureId id,
        Dictionary<FeatureId, ImmutableArray<FeatureId>> dependencies,
        HashSet<FeatureId> stuck,
        HashSet<FeatureId> finished,
        List<FeatureId> path,
        HashSet<FeatureId> onPath)
    {
        path.Add(id);
        onPath.Add(id);

        foreach (FeatureId input in dependencies[id])
        {
            if (!stuck.Contains(input))
            {
                continue;
            }

            if (onPath.Contains(input))
            {
                // Back edge. The loop is the tail of the path from that feature onwards, reversed
                // so it reads in dependency order rather than in the order it was walked.
                int start = path.IndexOf(input);
                ImmutableArray<FeatureId> loop = [.. path.Skip(start).Reverse()];

                return loop;
            }

            if (!finished.Contains(input)
                && Walk(input, dependencies, stuck, finished, path, onPath) is { } found)
            {
                return found;
            }
        }

        path.RemoveAt(path.Count - 1);
        onPath.Remove(id);
        finished.Add(id);

        return null;
    }
}
