using System.Collections.Immutable;

using OpenMCAD.Core.Expressions;

namespace OpenMCAD.Core.Documents;

/// <summary>
/// Thrown when parameters are defined in terms of one another in a loop.
/// </summary>
/// <remarks>
/// §5.5 asks for this to be rejected at commit with the cycle named. A loop has no value to
/// compute — every member is waiting for another — and the only useful thing to say is which
/// parameters are in it, because one of those definitions has to change and the user is the only
/// one who knows which.
/// </remarks>
public sealed class ParameterCycleException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="cycle">
    /// The parameters in the loop, in dependency order. The first is not repeated at the end.
    /// </param>
    public ParameterCycleException(ImmutableArray<string> cycle)
        : base(Describe(cycle))
        => Cycle = cycle;

    /// <summary>Creates the exception with a plain message.</summary>
    /// <param name="message">The message.</param>
    public ParameterCycleException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a plain message and an inner cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ParameterCycleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with nothing to say.</summary>
    public ParameterCycleException()
        : base("These parameters are defined in terms of one another.")
    {
    }

    /// <summary>Gets the parameters in the loop, in dependency order.</summary>
    public ImmutableArray<string> Cycle { get; }

    private static string Describe(ImmutableArray<string> cycle)
    {
        if (cycle.IsDefaultOrEmpty)
        {
            return "These parameters are defined in terms of one another.";
        }

        string path = string.Join(" -> ", cycle.Append(cycle[0]));

        return $"These parameters are defined in terms of one another: {path}. One of these "
            + "definitions has to change.";
    }
}

/// <summary>
/// What a parameter re-evaluation did.
/// </summary>
/// <param name="Document">The document, with every computed value brought up to date.</param>
/// <param name="Changed">
/// The document parameters whose value is now different. These are the seeds a rebuild propagates
/// from.
/// </param>
/// <param name="ChangedFeatures">
/// The features whose own parameters were recomputed and came out different.
/// </param>
/// <param name="Unresolved">
/// Parameters whose expression could not be evaluated, with the reason. They keep their last known
/// value rather than losing it, which is why a <see cref="Parameter"/> stores both.
/// </param>
public sealed record ParameterEvaluation(
    Document Document,
    ImmutableArray<string> Changed,
    ImmutableArray<FeatureId> ChangedFeatures,
    ImmutableArray<(string Name, string Reason)> Unresolved);

/// <summary>
/// Which parameters are defined in terms of which, and in what order they can be worked out.
/// </summary>
/// <remarks>
/// <para>
/// §5.5 puts this graph inside the rebuild DAG rather than beside it, and the reason is that a
/// parameter change reaches geometry through two different routes. A parameter defined in terms of
/// another has to be recomputed before anything reads it; and a feature whose own values are
/// computed from parameters has to rebuild when one of them comes out different. Treating those as
/// one graph is what makes a single edit propagate correctly through both.
/// </para>
/// <para>
/// <b>Order is alphabetical where the graph does not constrain it.</b> A document's parameters are
/// held in a dictionary, which has no order to inherit, so something has to be chosen — and the
/// same reasoning applies as for the feature graph (P3-T03): a rebuild that evaluates in a
/// different order each run makes a cache key and a bug report stop meaning anything. Names are
/// unique, stable, and the user chose them.
/// </para>
/// </remarks>
public sealed class ParameterGraph
{
    private readonly ImmutableDictionary<string, ImmutableArray<string>> _dependencies;
    private readonly ImmutableDictionary<string, ImmutableArray<string>> _dependents;

    private ParameterGraph(
        ImmutableArray<string> order,
        ImmutableDictionary<string, ImmutableArray<string>> dependencies,
        ImmutableDictionary<string, ImmutableArray<string>> dependents)
    {
        EvaluationOrder = order;
        _dependencies = dependencies;
        _dependents = dependents;
    }

    /// <summary>Gets every parameter, ordered so each comes after what it is defined from.</summary>
    public ImmutableArray<string> EvaluationOrder { get; }

    /// <summary>Builds the graph for a document.</summary>
    /// <param name="document">The document.</param>
    /// <returns>The graph.</returns>
    /// <exception cref="ParameterCycleException">
    /// Parameters are defined in terms of one another. The exception names the loop.
    /// </exception>
    public static ParameterGraph Build(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);

        ImmutableArray<string> names =
            [.. document.Parameters.Select(p => p.Name).Order(StringComparer.Ordinal)];

        HashSet<string> known = new(names, Parameter.NameComparer);

        Dictionary<string, ImmutableArray<string>> dependencies = new(Parameter.NameComparer);
        Dictionary<string, List<string>> dependents = new(Parameter.NameComparer);

        foreach (string name in names)
        {
            dependents[name] = [];
        }

        foreach (string name in names)
        {
            dependencies[name] = ReferencesOf(document.FindParameter(name), known);
        }

        foreach (string name in names)
        {
            foreach (string dependency in dependencies[name])
            {
                dependents[dependency].Add(name);
            }
        }

        return new ParameterGraph(
            Order(names, dependencies, dependents),
            dependencies.ToImmutableDictionary(Parameter.NameComparer),
            dependents.ToImmutableDictionary(
                pair => pair.Key, pair => pair.Value.ToImmutableArray(), Parameter.NameComparer));
    }

    /// <summary>Gets what a parameter is defined from.</summary>
    /// <param name="name">Which parameter.</param>
    /// <returns>The parameters it names.</returns>
    public ImmutableArray<string> DependenciesOf(string name)
        => _dependencies.TryGetValue(name, out ImmutableArray<string> found) ? found : [];

    /// <summary>Gets what is defined from a parameter.</summary>
    /// <param name="name">Which parameter.</param>
    /// <returns>The parameters that name it.</returns>
    public ImmutableArray<string> DependentsOf(string name)
        => _dependents.TryGetValue(name, out ImmutableArray<string> found) ? found : [];

    /// <summary>Gets every parameter that has to be recomputed because some changed.</summary>
    /// <param name="seeds">The parameters that were edited.</param>
    /// <returns>The seeds and everything defined from them, in evaluation order.</returns>
    public ImmutableArray<string> AffectedBy(IEnumerable<string> seeds)
    {
        ArgumentNullException.ThrowIfNull(seeds);

        HashSet<string> affected = new(Parameter.NameComparer);
        Queue<string> pending = new();

        foreach (string seed in seeds)
        {
            if (affected.Add(seed))
            {
                pending.Enqueue(seed);
            }
        }

        while (pending.Count > 0)
        {
            foreach (string dependent in DependentsOf(pending.Dequeue()))
            {
                if (affected.Add(dependent))
                {
                    pending.Enqueue(dependent);
                }
            }
        }

        return [.. EvaluationOrder.Where(affected.Contains)];
    }

    /// <summary>Brings every computed value in a document up to date.</summary>
    /// <param name="document">The document.</param>
    /// <param name="external">
    /// How to resolve a reference to another document's parameter, or null while nothing can. §5.5
    /// allows <c>Chassis:Width</c>; until something can open Chassis, such a parameter keeps the
    /// value it last had rather than losing it.
    /// </param>
    /// <returns>What changed.</returns>
    /// <exception cref="ParameterCycleException">Parameters are defined in a loop.</exception>
    public static ParameterEvaluation Reevaluate(
        Document document, Func<Expression.Reference, Quantity?>? external = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        ParameterGraph graph = Build(document);

        Dictionary<string, Quantity> values = new(Parameter.NameComparer);

        foreach (Parameter parameter in document.Parameters)
        {
            values[parameter.Name] = parameter.Value;
        }

        ImmutableArray<string>.Builder changed = ImmutableArray.CreateBuilder<string>();

        ImmutableArray<(string, string)>.Builder unresolved =
            ImmutableArray.CreateBuilder<(string, string)>();

        Document working = document;

        foreach (string name in graph.EvaluationOrder)
        {
            Parameter parameter = working.FindParameter(name)!;

            if (!parameter.IsDerived)
            {
                continue;
            }

            (Quantity? value, string? reason) = TryEvaluate(parameter, values, external);

            if (value is not { } computed)
            {
                unresolved.Add((name, reason!));
                continue;
            }

            values[name] = computed;

            if (computed != parameter.Value)
            {
                changed.Add(name);
                working = working.WithParameter(parameter with { Value = computed });
            }
        }

        // Feature values come last, and are never referred to by anything: a feature's parameters
        // belong to it and are not in scope elsewhere, so they are leaves that read the document's
        // parameters and are read by nobody.
        ImmutableArray<FeatureId>.Builder changedFeatures =
            ImmutableArray.CreateBuilder<FeatureId>();

        foreach (Feature feature in document.Features)
        {
            if (Recompute(feature, values, external, unresolved) is { } updated)
            {
                changedFeatures.Add(feature.Id);
                working = working.WithFeatureReplaced(updated);
            }
        }

        return new ParameterEvaluation(
            working, changed.ToImmutable(), changedFeatures.ToImmutable(), unresolved.ToImmutable());
    }

    /// <summary>Recomputes a feature's own values, or returns null if none moved.</summary>
    private static Feature? Recompute(
        Feature feature,
        Dictionary<string, Quantity> values,
        Func<Expression.Reference, Quantity?>? external,
        ImmutableArray<(string, string)>.Builder unresolved)
    {
        ImmutableArray<Parameter>.Builder updated =
            ImmutableArray.CreateBuilder<Parameter>(feature.Parameters.Length);

        bool moved = false;

        foreach (Parameter parameter in feature.Parameters)
        {
            if (!parameter.IsDerived)
            {
                updated.Add(parameter);
                continue;
            }

            (Quantity? value, string? reason) = TryEvaluate(parameter, values, external);

            if (value is not { } computed)
            {
                unresolved.Add(($"{feature.Name}.{parameter.Name}", reason!));
                updated.Add(parameter);

                continue;
            }

            moved |= computed != parameter.Value;
            updated.Add(parameter with { Value = computed });
        }

        return moved ? feature with { Parameters = updated.ToImmutable() } : null;
    }

    private static (Quantity? Value, string? Reason) TryEvaluate(
        Parameter parameter,
        Dictionary<string, Quantity> values,
        Func<Expression.Reference, Quantity?>? external)
    {
        (Quantity? value, ImmutableArray<ExpressionError> errors) = ExpressionEvaluator.Evaluate(
            parameter.Expression!,
            reference => reference.IsCrossDocument
                ? external?.Invoke(reference)
                : values.TryGetValue(reference.Name, out Quantity found) ? found : null);

        return value is not null
            ? (value, null)
            : (null, errors.IsDefaultOrEmpty ? "It could not be evaluated." : errors[0].Message);
    }

    /// <summary>The parameters an expression names, ignoring anything that is not one.</summary>
    private static ImmutableArray<string> ReferencesOf(Parameter? parameter, HashSet<string> known)
    {
        if (parameter is not { IsDerived: true })
        {
            return [];
        }

        ParsedExpression parsed = ExpressionParser.Parse(parameter.Expression!);

        if (parsed.Root is null)
        {
            // Unparseable, so it names nothing this graph can see. The expression is still wrong,
            // and saying so is the evaluator's job -- a badly typed formula must not also stop the
            // document being ordered.
            return [];
        }

        ImmutableArray<string>.Builder found = ImmutableArray.CreateBuilder<string>();

        foreach (Expression.Reference reference in ExpressionParser.ReferencesIn(parsed.Root))
        {
            // A cross-document reference creates no edge here: whatever it names lives in another
            // document's graph, and cannot take part in a loop inside this one.
            if (!reference.IsCrossDocument && known.Contains(reference.Name))
            {
                found.Add(reference.Name);
            }
        }

        return found.ToImmutable();
    }

    /// <summary>Kahn's algorithm, with ties broken alphabetically.</summary>
    private static ImmutableArray<string> Order(
        ImmutableArray<string> names,
        Dictionary<string, ImmutableArray<string>> dependencies,
        Dictionary<string, List<string>> dependents)
    {
        Dictionary<string, int> remaining = new(Parameter.NameComparer);

        foreach (string name in names)
        {
            remaining[name] = dependencies[name].Length;
        }

        PriorityQueue<string, string> ready = new(StringComparer.Ordinal);

        foreach (string name in names)
        {
            if (remaining[name] == 0)
            {
                ready.Enqueue(name, name);
            }
        }

        ImmutableArray<string>.Builder order = ImmutableArray.CreateBuilder<string>();

        while (ready.TryDequeue(out string? name, out _))
        {
            order.Add(name);

            foreach (string dependent in dependents[name])
            {
                if (--remaining[dependent] == 0)
                {
                    ready.Enqueue(dependent, dependent);
                }
            }
        }

        if (order.Count != names.Length)
        {
            HashSet<string> stuck = new(
                names.Where(n => remaining[n] > 0), Parameter.NameComparer);

            throw new ParameterCycleException(Cycle(names, dependencies, stuck));
        }

        return order.ToImmutable();
    }

    /// <summary>Finds one real loop among the parameters that could not be ordered.</summary>
    /// <remarks>
    /// Everything downstream of a loop is stuck too, so the leftovers are a superset. The same
    /// reasoning as the feature graph: naming all of them buries the two definitions the user has
    /// to look at under the ten they do not.
    /// </remarks>
    private static ImmutableArray<string> Cycle(
        ImmutableArray<string> names,
        Dictionary<string, ImmutableArray<string>> dependencies,
        HashSet<string> stuck)
    {
        HashSet<string> finished = new(Parameter.NameComparer);
        List<string> path = [];
        HashSet<string> onPath = new(Parameter.NameComparer);

        foreach (string name in names)
        {
            if (stuck.Contains(name)
                && !finished.Contains(name)
                && Walk(name, dependencies, stuck, finished, path, onPath) is { } loop)
            {
                return loop;
            }
        }

        return [];
    }

    private static ImmutableArray<string>? Walk(
        string name,
        Dictionary<string, ImmutableArray<string>> dependencies,
        HashSet<string> stuck,
        HashSet<string> finished,
        List<string> path,
        HashSet<string> onPath)
    {
        path.Add(name);
        onPath.Add(name);

        foreach (string dependency in dependencies[name])
        {
            if (!stuck.Contains(dependency))
            {
                continue;
            }

            if (onPath.Contains(dependency))
            {
                int start = path.FindIndex(p => Parameter.NameComparer.Equals(p, dependency));

                return [.. path.Skip(start).Reverse()];
            }

            if (!finished.Contains(dependency)
                && Walk(dependency, dependencies, stuck, finished, path, onPath) is { } found)
            {
                return found;
            }
        }

        path.RemoveAt(path.Count - 1);
        onPath.Remove(name);
        finished.Add(name);

        return null;
    }
}
