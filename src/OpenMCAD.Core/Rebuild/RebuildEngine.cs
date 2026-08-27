using System.Collections.Immutable;

using OpenMCAD.Core.Documents;
using OpenMCAD.Kernel.Threading;

namespace OpenMCAD.Core.Rebuild;

/// <summary>
/// Turns "these features changed" into "this is what the document now looks like".
/// </summary>
/// <remarks>
/// <para>
/// The engine holds no state about the document. Each rebuild starts from whatever the session has
/// at that moment, works on a private copy, and publishes the result in one transaction at the end.
/// Because a <see cref="Document"/> cannot change, that private copy is safe to read from the
/// kernel thread for as long as the rebuild takes, while the user carries on editing.
/// </para>
/// <para>
/// <b>At most one rebuild in flight.</b> A newer edit makes an older rebuild pointless — it is
/// computing what a document that no longer exists would have looked like — so requesting a rebuild
/// cancels whatever was running and waits for it to unwind before starting. That is what keeps a
/// dimension drag, which can emit an edit per mouse move, from queueing fifty rebuilds and running
/// every one of them (§5.4).
/// </para>
/// <para>
/// <b>Publishing is all-or-nothing.</b> The results are written back in a single transaction after
/// the last feature, not as each one finishes. A rebuild that is cancelled or superseded halfway
/// therefore leaves no trace, rather than leaving the document holding new geometry for the first
/// three features and old geometry for the rest — a state that is not a version of the model at
/// any point in its history and that nothing downstream could interpret.
/// </para>
/// </remarks>
public sealed class RebuildEngine : IDisposable
{
    private readonly DocumentSession _session;
    private readonly KernelDispatcher _dispatcher;
    private readonly IFeatureEvaluator _evaluator;
    private readonly IGeometryCache _cache;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly Lock _gate = new();

    private CancellationTokenSource? _inFlight;
    private DocumentSession? _attached;
    private bool _disposed;

    /// <summary>Creates the engine.</summary>
    /// <param name="session">The document to rebuild.</param>
    /// <param name="dispatcher">The kernel thread every evaluation runs on.</param>
    /// <param name="evaluator">What knows how to evaluate a feature.</param>
    /// <param name="cache">
    /// Where results are remembered. Pass <see cref="NullGeometryCache.Instance"/> for
    /// <c>--no-cache</c>, which runs this same code with a cache that never hits.
    /// </param>
    public RebuildEngine(
        DocumentSession session,
        KernelDispatcher dispatcher,
        IFeatureEvaluator evaluator,
        IGeometryCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(evaluator);

        _session = session;
        _dispatcher = dispatcher;
        _evaluator = evaluator;
        _cache = cache ?? new GeometryCache();
    }

    /// <summary>Gets where results are remembered.</summary>
    public IGeometryCache Cache => _cache;

    /// <summary>Raised when a rebuild finishes, however it finished.</summary>
    public event Action<RebuildResult>? Finished;

    /// <summary>Rebuilds whenever the session commits an edit.</summary>
    /// <remarks>
    /// <para>
    /// Opt-in rather than automatic, because whether an edit should rebuild immediately is a policy
    /// question — a batch script wants to make fifty edits and rebuild once — and policy does not
    /// belong to the engine.
    /// </para>
    /// <para>
    /// Safe against its own writes: publishing a rebuild's results is itself a commit, and it
    /// arrives here like any other. It seeds nothing, because P3-T02 deliberately does not record a
    /// body as a dirty feature — a body is the result of a rebuild, not a cause of one — so the
    /// rebuild it would trigger has nothing to do and stops.
    /// </para>
    /// </remarks>
    public void RebuildOnCommit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_attached is not null)
        {
            return;
        }

        _attached = _session;
        _session.Committed += OnCommitted;
    }

    /// <summary>Rebuilds everything the given edits made stale.</summary>
    /// <param name="seeds">The features that changed.</param>
    /// <param name="priority">How urgent this is.</param>
    /// <param name="cancellationToken">Cancels this rebuild.</param>
    /// <returns>What the rebuild did.</returns>
    public async Task<RebuildResult> RebuildAsync(
        IEnumerable<FeatureId> seeds,
        KernelPriority priority = KernelPriority.Rebuild,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(seeds);

        ImmutableArray<FeatureId> requested = [.. seeds];

        CancellationTokenSource mine = Supersede(cancellationToken);
        bool acquired = false;

        try
        {
            // Cancelling the previous rebuild happened above, before this wait. Asking for the slot
            // first would mean queueing behind a rebuild already known to be pointless and waiting
            // for it to finish on its own.
            await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;

            RebuildResult result = await RunAsync(requested, priority, mine.Token, cancellationToken)
                .ConfigureAwait(false);

            Finished?.Invoke(result);
            return result;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_inFlight, mine))
                {
                    _inFlight = null;
                }
            }

            mine.Dispose();

            // Only if it was taken. Releasing a semaphore that was never acquired -- which is what
            // happens when the wait itself is cancelled -- raises its count and lets two rebuilds
            // run at once from then on.
            if (acquired)
            {
                _oneAtATime.Release();
            }
        }
    }

    /// <summary>Rebuilds every feature in the document, from the beginning.</summary>
    /// <param name="priority">How urgent this is.</param>
    /// <param name="cancellationToken">Cancels this rebuild.</param>
    /// <returns>What the rebuild did.</returns>
    /// <remarks>
    /// What <c>--no-cache</c> and "force rebuild" mean, and what the regression corpus runs. Seeded
    /// with every feature rather than by clearing a cache, so it goes through exactly the same code
    /// path as an ordinary rebuild — a forced rebuild that took a different route would not be
    /// evidence about the ordinary one.
    /// </remarks>
    public Task<RebuildResult> RebuildAllAsync(
        KernelPriority priority = KernelPriority.Rebuild,
        CancellationToken cancellationToken = default)
        => RebuildAsync(
            _session.Current.Features.Select(feature => feature.Id), priority, cancellationToken);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_attached is not null)
        {
            _attached.Committed -= OnCommitted;
            _attached = null;
        }

        lock (_gate)
        {
            _inFlight?.Cancel();
        }

        _oneAtATime.Dispose();
    }

    private async Task<RebuildResult> RunAsync(
        ImmutableArray<FeatureId> seeds,
        KernelPriority priority,
        CancellationToken token,
        CancellationToken callerToken)
    {
        // The document this rebuild is about. Everything below reasons about this one value, so
        // an edit arriving mid-rebuild cannot change what is being computed -- only whether the
        // answer is still wanted, which is settled once, at the end.
        Document start = _session.Current;

        FeatureGraph graph = FeatureGraph.Build(start);
        ImmutableArray<FeatureId> order = graph.AffectedBy(seeds);

        if (order.IsEmpty)
        {
            return new RebuildResult(RebuildOutcome.NothingToDo, [], [], [], [], start);
        }

        Document working = start;

        ImmutableArray<FeatureId>.Builder rebuilt = ImmutableArray.CreateBuilder<FeatureId>();
        ImmutableArray<FeatureId>.Builder failed = ImmutableArray.CreateBuilder<FeatureId>();
        ImmutableArray<FeatureId>.Builder skipped = ImmutableArray.CreateBuilder<FeatureId>();
        ImmutableArray<FeatureId>.Builder hits = ImmutableArray.CreateBuilder<FeatureId>();

        HashSet<FeatureId> unusable = [];

        // Keys are chained: a feature key folds in the keys of what it consumes, so they have to be
        // computed in evaluation order -- which is the order this loop already runs in.
        //
        // Seeded with the keys of everything *not* being rebuilt, so a partial rebuild produces the
        // same keys a full one would. Without that, rebuilding a subgraph would compute different
        // keys for its features than rebuilding the whole document did, and nothing downstream of
        // an untouched feature would ever hit.
        Dictionary<FeatureId, RebuildKey> keys = KeysFor(graph, working, order);

        foreach (FeatureId id in order)
        {
            // The operation boundary. A rebuild that has been superseded stops here rather than
            // partway through a kernel call, because a native operation cannot be interrupted
            // safely and the dispatcher will not abandon one it has started.
            if (token.IsCancellationRequested)
            {
                return new RebuildResult(
                    callerToken.IsCancellationRequested
                        ? RebuildOutcome.Cancelled
                        : RebuildOutcome.Superseded,
                    rebuilt.ToImmutable(),
                    failed.ToImmutable(),
                    skipped.ToImmutable(),
                    hits.ToImmutable(),
                    start);
            }

            Feature? feature = working.FindFeature(id);

            if (feature is null)
            {
                // A seed for a feature that was deleted. It has nothing to evaluate; its former
                // consumers are in this list too and are what the seed was really for.
                continue;
            }

            if (feature.IsSuppressed || DependsOnSomethingUnusable(graph, id, unusable))
            {
                // Independent branches carry on. A failure contains itself to what actually
                // depended on it, which is the difference between one broken feature and a
                // document that will not rebuild.
                unusable.Add(id);
                skipped.Add(id);
                continue;
            }

            ImmutableArray<Body> inputs = InputsFor(graph, working, id);
            RebuildKey key = RebuildKey.For(feature, InputKeys(graph, keys, id));

            keys[id] = key;

            if (_cache.TryGet(key, out FeatureOutput cached))
            {
                // The whole point: an identical situation skips the kernel entirely. Undo and
                // rollback-bar scrubbing return the document to states it has already been in, so
                // every key is one already computed and every feature takes this branch.
                working = Apply(working, id, cached);

                rebuilt.Add(id);
                hits.Add(id);

                continue;
            }

            try
            {
                FeatureEvaluation evaluation = new(working, feature, inputs);

                FeatureOutput output = await _dispatcher.RunAsync(
                    $"rebuild {feature.FeatureType} '{feature.Name}'",
                    () => _evaluator.Evaluate(evaluation, token),
                    priority,
                    token).ConfigureAwait(false);

                _cache.Store(key, output);

                working = Apply(working, id, output);
                rebuilt.Add(id);
            }
            catch (OperationCanceledException)
            {
                return new RebuildResult(
                    callerToken.IsCancellationRequested
                        ? RebuildOutcome.Cancelled
                        : RebuildOutcome.Superseded,
                    rebuilt.ToImmutable(),
                    failed.ToImmutable(),
                    skipped.ToImmutable(),
                    hits.ToImmutable(),
                    start);
            }
#pragma warning disable CA1031 // A failing feature must not take the rebuild with it.
            catch (Exception)
#pragma warning restore CA1031
            {
                // Deliberately every exception. A feature is arbitrary code -- a plugin's, in the
                // general case -- and the one thing that must not happen is one badly written
                // operation making the whole document unrebuildable. P3-T07 records what went
                // wrong and shows it; this only guarantees the rebuild survives it.
                unusable.Add(id);
                failed.Add(id);
            }
        }

        Document published = Publish(start, working, out bool superseded);

        return new RebuildResult(
            superseded ? RebuildOutcome.Superseded : RebuildOutcome.Completed,
            rebuilt.ToImmutable(),
            failed.ToImmutable(),
            skipped.ToImmutable(),
            hits.ToImmutable(),
            published);
    }

    /// <summary>Writes the results back, unless the document has moved on.</summary>
    private Document Publish(Document start, Document working, out bool superseded)
    {
        if (ReferenceEquals(start, working))
        {
            superseded = false;
            return start;
        }

        using IDocumentTransaction? transaction = _session.TryBeginTransaction("Rebuild");

        if (transaction is null)
        {
            // Somebody is editing. Whatever they are about to commit supersedes this rebuild by
            // definition, so there is nothing here worth waiting for a turn to publish.
            superseded = true;
            return _session.Current;
        }

        // Checked with the transaction open rather than before it. Only one transaction may exist
        // at a time, so nothing can slip in between this check and the commit -- whereas checking
        // beforehand and then opening leaves a gap exactly wide enough for the edit that matters.
        if (transaction.Document.Version != start.Version)
        {
            superseded = true;
            transaction.Rollback();

            return _session.Current;
        }

        foreach (Body body in working.Bodies)
        {
            transaction.SetBody(body);
        }

        foreach (Body body in start.Bodies)
        {
            if (working.FindBody(body.Id) is null)
            {
                transaction.RemoveBody(body.Id);
            }
        }

        transaction.Commit();

        superseded = false;
        return _session.Current;
    }

    /// <summary>Replaces what a feature owned with what it has just produced.</summary>
    private static Document Apply(Document document, FeatureId id, FeatureOutput output)
    {
        // Removed first. A feature that produced two bodies last time and one now must not leave
        // the second behind, and the ids need not match between runs.
        foreach (Body existing in document.BodiesOf(id))
        {
            document = document.WithBodyRemoved(existing.Id);
        }

        foreach (Body body in output.Bodies)
        {
            document = document.WithBody(body);
        }

        foreach (ReferenceGeometry reference in output.References)
        {
            document = document.WithReference(reference);
        }

        return document;
    }

    /// <summary>Computes the keys of every feature the rebuild will not visit.</summary>
    /// <remarks>
    /// A key folds in the keys of what a feature consumes, so a feature being rebuilt needs the
    /// keys of its inputs even when those inputs are not themselves being rebuilt. Walking the full
    /// evaluation order and filling in everything ahead of the affected set is what makes a partial
    /// rebuild produce the same keys a complete one would -- and identical keys are the only reason
    /// a cache entry written by one rebuild is ever found by another.
    /// </remarks>
    private static Dictionary<FeatureId, RebuildKey> KeysFor(
        FeatureGraph graph, Document document, ImmutableArray<FeatureId> affected)
    {
        Dictionary<FeatureId, RebuildKey> keys = [];
        HashSet<FeatureId> pending = [.. affected];

        foreach (FeatureId id in graph.EvaluationOrder)
        {
            if (pending.Contains(id))
            {
                continue;
            }

            if (document.FindFeature(id) is { } feature)
            {
                keys[id] = RebuildKey.For(feature, InputKeys(graph, keys, id));
            }
        }

        return keys;
    }

    private static ImmutableArray<RebuildKey> InputKeys(
        FeatureGraph graph, Dictionary<FeatureId, RebuildKey> keys, FeatureId id)
    {
        ImmutableArray<RebuildKey>.Builder inputs = ImmutableArray.CreateBuilder<RebuildKey>();

        foreach (FeatureId dependency in graph.DependenciesOf(id))
        {
            // A dependency with no key is one that was suppressed, failed, or has been removed.
            // Folding in None rather than skipping it keeps the arity of the key stable, so a
            // feature with one broken input does not key the same as one with no inputs at all.
            inputs.Add(keys.TryGetValue(dependency, out RebuildKey key) ? key : RebuildKey.None);
        }

        return inputs.ToImmutable();
    }

    private static bool DependsOnSomethingUnusable(
        FeatureGraph graph, FeatureId id, HashSet<FeatureId> unusable)
    {
        foreach (FeatureId input in graph.DependenciesOf(id))
        {
            if (unusable.Contains(input))
            {
                return true;
            }
        }

        return false;
    }

    private static ImmutableArray<Body> InputsFor(
        FeatureGraph graph, Document document, FeatureId id)
    {
        ImmutableArray<Body>.Builder inputs = ImmutableArray.CreateBuilder<Body>();

        foreach (FeatureId dependency in graph.DependenciesOf(id))
        {
            inputs.AddRange(document.BodiesOf(dependency));
        }

        return inputs.ToImmutable();
    }

    private CancellationTokenSource Supersede(CancellationToken callerToken)
    {
        CancellationTokenSource mine =
            CancellationTokenSource.CreateLinkedTokenSource(callerToken);

        CancellationTokenSource? previous;

        lock (_gate)
        {
            previous = _inFlight;
            _inFlight = mine;
        }

        // Outside the lock: cancelling runs continuations, and one of those may be the superseded
        // rebuild finishing, which takes this same lock to clear itself.
        previous?.Cancel();

        return mine;
    }

    private void OnCommitted(DocumentChange change)
    {
        if (change.TouchedFeatures.IsEmpty)
        {
            return;
        }

        // Fire and forget, deliberately. The commit that raised this is a user edit, and making it
        // wait for a rebuild would freeze whoever made it for as long as the kernel takes -- which
        // is the freeze the whole snapshot architecture exists to avoid. Whoever wants to know how
        // it went subscribes to Finished.
        _ = RebuildAsync(change.TouchedFeatures);
    }
}
