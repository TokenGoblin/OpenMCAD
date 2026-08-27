using System.Collections.Immutable;

namespace OpenMCAD.Core.Documents;

/// <summary>
/// A unit of work against a document. See <see cref="IDocumentTransaction"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every edit is applied to a private working document. Because a <see cref="Document"/> is
/// immutable, "applying" it means replacing the working reference, and abandoning the transaction
/// means dropping that reference — so rollback has nothing to undo and cannot itself fail. A
/// sequence of edits that throws halfway leaves the session exactly as it was, without the
/// transaction having to know how to reverse the ones that had already succeeded.
/// </para>
/// <para>
/// What is recorded alongside is which features and parameters were touched, which is the seed of
/// the dirty set. Recorded as each edit happens rather than worked out at commit by comparing two
/// documents: the comparison would have to guess intent — a feature that was removed and re-added
/// looks identical to one that was never touched — and it would cost a walk of the whole graph for
/// something already known exactly.
/// </para>
/// </remarks>
internal sealed class DocumentTransaction : IDocumentTransaction
{
    private readonly DocumentSession _session;
    private readonly HashSet<FeatureId> _touchedFeatures = [];
    private readonly HashSet<string> _touchedParameters;

    private Document _working;
    private bool _finished;

    internal DocumentTransaction(DocumentSession session, string name, Document document)
    {
        _session = session;
        Name = name;
        _working = document;
        _touchedParameters = new HashSet<string>(Parameter.NameComparer);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public Document Document
    {
        get
        {
            EnsureOpen();
            return _working;
        }
    }

    /// <inheritdoc />
    public bool IsOpen => !_finished;

    /// <inheritdoc />
    public void AddFeature(Feature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        EnsureOpen();

        _working = _working.WithFeatureAdded(feature);
        _touchedFeatures.Add(feature.Id);
    }

    /// <inheritdoc />
    public void ReplaceFeature(Feature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        EnsureOpen();

        _working = _working.WithFeatureReplaced(feature);
        _touchedFeatures.Add(feature.Id);
    }

    /// <inheritdoc />
    public void RemoveFeature(FeatureId id)
    {
        EnsureOpen();

        // Recorded before the removal, because afterwards there is nothing to ask. Whatever
        // depended on this feature is now dangling and has to be reconsidered, which is exactly
        // what a dirty seed is for -- so a removal is the one edit where the seed matters most.
        _touchedFeatures.Add(id);

        _working = _working.WithFeatureRemoved(id);
    }

    /// <inheritdoc />
    public void MoveFeature(FeatureId id, int index)
    {
        EnsureOpen();

        _working = _working.WithFeatureMoved(id, index);

        // Deliberately not recorded as touched. Moving a feature changes the order the user sees,
        // not what anything consumes or produces, so nothing needs rebuilding. Whether the move is
        // legal against the dependency graph is a separate question, asked at commit once P3-T03
        // exists to answer it.
    }

    /// <inheritdoc />
    public void SetParameter(Parameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        EnsureOpen();

        _working = _working.WithParameter(parameter);
        _touchedParameters.Add(parameter.Name);
    }

    /// <inheritdoc />
    public void RemoveParameter(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        EnsureOpen();

        _working = _working.WithParameterRemoved(name);
        _touchedParameters.Add(name);
    }

    /// <inheritdoc />
    public void SetBody(Body body)
    {
        ArgumentNullException.ThrowIfNull(body);
        EnsureOpen();

        _working = _working.WithBody(body);

        // Not a dirty seed. A body appearing is the *result* of a rebuild, not a cause of one, and
        // recording it here would mean every rebuild dirtied the features it had just rebuilt.
    }

    /// <inheritdoc />
    public void RemoveBody(BodyId id)
    {
        EnsureOpen();

        _working = _working.WithBodyRemoved(id);
    }

    /// <inheritdoc />
    public void AddReference(ReferenceGeometry reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        EnsureOpen();

        _working = _working.WithReference(reference);

        if (reference.Owner.IsValid)
        {
            _touchedFeatures.Add(reference.Owner);
        }
    }

    /// <inheritdoc />
    public void SetRollbackPosition(int? position)
    {
        EnsureOpen();

        int before = _working.ActiveFeatureCount;

        _working = _working.WithRollbackPosition(position);

        int after = _working.ActiveFeatureCount;

        // Every feature that crossed the bar is a dirty seed, in whichever direction it crossed:
        // one that became active has to be built, and one that became inactive has to give up its
        // geometry. Seeding only the newly active ones would leave the rolled-back part of the
        // model still on screen.
        for (int i = System.Math.Min(before, after); i < System.Math.Max(before, after); ++i)
        {
            _touchedFeatures.Add(_working.Features[i].Id);
        }
    }

    /// <inheritdoc />
    public void SetReport(RebuildReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        EnsureOpen();

        _working = _working.WithReport(report);

        // Not a dirty seed, for the same reason a body is not: this records what a rebuild found,
        // and treating it as a change would have every rebuild dirty what it had just finished.
    }

    /// <inheritdoc />
    public void SetMetadata(DocumentMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        EnsureOpen();

        _working = _working.WithMetadata(metadata);
    }

    /// <inheritdoc />
    public DocumentChange Commit()
    {
        EnsureOpen();

        // Marked finished before the session is told, not after. If the commit throws, this
        // transaction is spent either way, and one that could be retried after a failed commit
        // would be applying its edits to a document that had moved on.
        _finished = true;

        DocumentChange? change = _session.Commit(
            this,
            _working,
            [.. _touchedFeatures],
            [.. _touchedParameters]);

        // A transaction that changed nothing still has to return something describing that. The
        // alternative is a null return that every caller has to check, for a case that is normal.
        return change ?? new DocumentChange(Name, _working, _working, [], []);
    }

    /// <inheritdoc />
    public void Rollback()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _session.Rollback(this);
    }

    /// <inheritdoc />
    public void Dispose() => Rollback();

    private void EnsureOpen()
    {
        if (_finished)
        {
            throw new InvalidOperationException(
                $"The transaction '{Name}' has already been committed or rolled back. Using one "
                + "again would apply edits to a document that is no longer the session's, and they "
                + "would be lost at best.");
        }
    }
}
