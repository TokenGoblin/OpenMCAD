namespace OpenMCAD.Core.Documents;

/// <summary>
/// The only way to change a document.
/// </summary>
/// <remarks>
/// <para>
/// Open one, make every change that belongs together, then commit it or roll it back (§5.4). The
/// document a transaction is working on is its own; nothing outside sees any of it until commit,
/// so a sequence of edits that fails halfway leaves no trace rather than half a change.
/// </para>
/// <para>
/// <b>Grouping is the point, not ceremony.</b> A single user action is usually several edits — add
/// a feature, set three of its parameters, name it — and the user expects one undo to remove all of
/// it. The transaction is what makes those one thing, which is why it carries a name: the name is
/// what the undo entry is labelled with (P3-T17).
/// </para>
/// <para>
/// Not thread-safe, and not meant to be: a transaction is a unit of work belonging to whoever
/// opened it. What is thread-safe is the handover at commit, which is the <see cref="DocumentSession"/>'s
/// business.
/// </para>
/// </remarks>
public interface IDocumentTransaction : IDisposable
{
    /// <summary>Gets what this edit is called, for the undo entry.</summary>
    string Name { get; }

    /// <summary>Gets the document as this transaction has it so far.</summary>
    /// <remarks>
    /// A working copy, invisible to everything else until commit. Reading it back is how a caller
    /// sees the effect of its own earlier edits within the same transaction.
    /// </remarks>
    Document Document { get; }

    /// <summary>Gets whether this transaction can still be used.</summary>
    bool IsOpen { get; }

    /// <summary>Adds a feature to the end of the tree.</summary>
    /// <param name="feature">The feature.</param>
    /// <exception cref="InvalidOperationException">The transaction is no longer open.</exception>
    /// <exception cref="ArgumentException">A feature with that id is already present.</exception>
    void AddFeature(Feature feature);

    /// <summary>Replaces a feature, keeping its position in the tree.</summary>
    /// <param name="feature">The replacement, carrying the same id.</param>
    /// <exception cref="InvalidOperationException">The transaction is no longer open.</exception>
    /// <exception cref="ArgumentException">No feature with that id is present.</exception>
    void ReplaceFeature(Feature feature);

    /// <summary>Removes a feature and the bodies it produced.</summary>
    /// <param name="id">Which feature.</param>
    /// <exception cref="InvalidOperationException">The transaction is no longer open.</exception>
    void RemoveFeature(FeatureId id);

    /// <summary>Moves a feature to a different position in the tree.</summary>
    /// <param name="id">Which feature.</param>
    /// <param name="index">Where it should end up.</param>
    /// <exception cref="InvalidOperationException">The transaction is no longer open.</exception>
    void MoveFeature(FeatureId id, int index);

    /// <summary>Adds or replaces a parameter.</summary>
    /// <param name="parameter">The parameter.</param>
    /// <exception cref="InvalidOperationException">The transaction is no longer open.</exception>
    void SetParameter(Parameter parameter);

    /// <summary>Removes a parameter.</summary>
    /// <param name="name">What it is called.</param>
    /// <exception cref="InvalidOperationException">The transaction is no longer open.</exception>
    void RemoveParameter(string name);

    /// <summary>Adds or replaces a body.</summary>
    /// <param name="body">The body.</param>
    /// <exception cref="InvalidOperationException">The transaction is no longer open.</exception>
    /// <exception cref="ArgumentException">No feature in the document owns it.</exception>
    void SetBody(Body body);

    /// <summary>Removes a body.</summary>
    /// <param name="id">Which body.</param>
    /// <exception cref="InvalidOperationException">The transaction is no longer open.</exception>
    void RemoveBody(BodyId id);

    /// <summary>Adds a piece of reference geometry.</summary>
    /// <param name="reference">The geometry.</param>
    /// <exception cref="InvalidOperationException">The transaction is no longer open.</exception>
    void AddReference(ReferenceGeometry reference);

    /// <summary>Moves the rollback bar.</summary>
    /// <param name="position">
    /// How many features from the top of the tree stay active, or null to roll forward to the end.
    /// </param>
    /// <exception cref="InvalidOperationException">The transaction is no longer open.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The position is negative, or beyond the end of the tree.
    /// </exception>
    void SetRollbackPosition(int? position);

    /// <summary>Replaces the document's properties.</summary>
    /// <param name="metadata">The new properties.</param>
    /// <exception cref="InvalidOperationException">The transaction is no longer open.</exception>
    void SetMetadata(DocumentMetadata metadata);

    /// <summary>Publishes every change made here, as one edit.</summary>
    /// <returns>What changed.</returns>
    /// <exception cref="InvalidOperationException">The transaction is no longer open.</exception>
    /// <remarks>
    /// Committing a transaction that changed nothing is allowed and does nothing: the session keeps
    /// the document it had, and no undo entry appears. Opening a transaction speculatively and
    /// finding there was nothing to do is normal — a drag that ends where it started, a dialog
    /// dismissed — and making that an error would push the check into every caller.
    /// </remarks>
    DocumentChange Commit();

    /// <summary>Discards every change made here.</summary>
    /// <remarks>
    /// Rolling back a transaction that has already been committed or rolled back does nothing.
    /// Unlike commit, this has to be safe to call from a <see langword="finally"/> block, where the
    /// caller may not know which happened.
    /// </remarks>
    void Rollback();
}
