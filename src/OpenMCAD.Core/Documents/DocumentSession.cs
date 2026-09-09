using System.Collections.Immutable;

namespace OpenMCAD.Core.Documents;

/// <summary>
/// Holds the current state of one open document, and is the only place a new state can come from.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="Document"/> is a value: it cannot change, and passing one around is safe from any
/// thread. What has to be shared and does change is <em>which</em> document is current, and that is
/// this type's whole job. Keeping the two apart is what lets a rebuild read a document for as long
/// as it likes while the user carries on editing — the rebuild holds a value that nobody can alter,
/// not a lock on the thing everyone needs.
/// </para>
/// <para>
/// <b>One transaction at a time.</b> Two open transactions on one document would each start from
/// the same state and each believe theirs was the result, so committing both would silently discard
/// one. Merging them is not possible in general — two edits to the same parameter have no correct
/// combination — so the honest options are to reject the second transaction or to reject the second
/// commit. Rejecting at open is better: it fails at the point the mistake was made, while the
/// caller still has the stack that explains it.
/// </para>
/// </remarks>
public sealed class DocumentSession
{
    private readonly Lock _gate = new();

    private Document _current;
    private Document _saved;
    private DocumentTransaction? _open;

    /// <summary>Creates a session over a document.</summary>
    /// <param name="document">The starting state, or null for an empty document.</param>
    /// <remarks>
    /// The starting state counts as saved. A session opened on a document read from disk is not
    /// dirty, and a new empty one has nothing worth keeping — so in both cases the first edit is
    /// what makes it dirty, which is what a user expects of a title bar.
    /// </remarks>
    public DocumentSession(Document? document = null)
        => _saved = _current = document ?? Document.Empty();

    /// <summary>Raised after a transaction commits something.</summary>
    /// <remarks>
    /// <para>
    /// The seam the rest of the phase attaches to. The rebuild engine (P3-T04) subscribes to learn
    /// what went dirty, and the undo stack (P3-T17) to record the entry. Neither exists yet, and
    /// neither is invented here: an event carrying what changed is what they will both need, and
    /// guessing at their interfaces now would be designing against code that has not been written.
    /// </para>
    /// <para>
    /// Raised outside the lock, and after the new document is already current. A handler that reads
    /// <see cref="Current"/> therefore sees the state the event is telling it about, and one that
    /// opens a transaction of its own does not deadlock.
    /// </para>
    /// </remarks>
    public event Action<DocumentChange>? Committed;

    /// <summary>Gets the document as it currently stands.</summary>
    public Document Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Gets whether there are changes that have not been written to disk.</summary>
    /// <remarks>
    /// <para>
    /// By reference, not by comparison. An immutable document makes undo a matter of holding an
    /// earlier reference (§5.4), so a user who edits and then undoes back to where they started is
    /// holding the very document that was saved — and reporting that as dirty would ask them to
    /// save a file that cannot have changed. A deep comparison would reach the same answer here and
    /// would also call two separately-built identical documents the same, which is a different
    /// claim and a slower one.
    /// </para>
    /// <para>
    /// The counterpart is that this can say "dirty" for a document that has been edited into an
    /// equal state by a different route. That is the safe direction to be wrong in: offering to
    /// save something that need not be saved costs a keystroke, and the other way costs the work.
    /// </para>
    /// </remarks>
    public bool IsDirty
    {
        get
        {
            lock (_gate)
            {
                return !ReferenceEquals(_current, _saved);
            }
        }
    }

    /// <summary>Records that the document as it now stands has been written to disk.</summary>
    /// <returns>The document that was marked, so a caller can be sure what it saved.</returns>
    /// <remarks>
    /// Returns what it marked rather than nothing, because a save is not instantaneous: a caller
    /// that wrote <see cref="Current"/> to a file and then called this could be marking a later
    /// document if an edit landed in between. Marking and reporting in one locked step means the
    /// caller can compare what it wrote against what it marked and notice.
    /// </remarks>
    public Document MarkSaved()
    {
        lock (_gate)
        {
            return _saved = _current;
        }
    }

    /// <summary>Gets whether a transaction is open on this session.</summary>
    public bool HasOpenTransaction
    {
        get
        {
            lock (_gate)
            {
                return _open is not null;
            }
        }
    }

    /// <summary>Opens a transaction.</summary>
    /// <param name="name">
    /// What this edit is called, as it should appear in the undo list. Something a person did.
    /// </param>
    /// <returns>The transaction. Dispose it; disposing without committing rolls it back.</returns>
    /// <exception cref="InvalidOperationException">A transaction is already open.</exception>
    public IDocumentTransaction BeginTransaction(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_gate)
        {
            if (_open is not null)
            {
                throw new InvalidOperationException(
                    $"A transaction ('{_open.Name}') is already open on this document. Two "
                    + "transactions starting from the same state would each believe theirs was the "
                    + "result, and committing both would discard one of them without saying so.");
            }

            DocumentTransaction transaction = new(this, name, _current);
            _open = transaction;

            return transaction;
        }
    }

    /// <summary>Opens a transaction, or returns null if one is already open.</summary>
    /// <param name="name">What this edit is called.</param>
    /// <returns>The transaction, or null.</returns>
    /// <remarks>
    /// For callers to whom "someone else is editing right now" is an ordinary answer rather than a
    /// mistake. A rebuild finishing at the moment the user opens a dialog is exactly that: its
    /// results describe a document that is about to change anyway, so there is nothing to report
    /// and nothing to retry. <see cref="BeginTransaction"/> stays the right call for an edit that
    /// was asked for and must therefore either happen or say why not.
    /// </remarks>
    public IDocumentTransaction? TryBeginTransaction(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_gate)
        {
            if (_open is not null)
            {
                return null;
            }

            DocumentTransaction transaction = new(this, name, _current);
            _open = transaction;

            return transaction;
        }
    }

    /// <summary>Puts the session back to a document it held before.</summary>
    /// <param name="document">The document to restore.</param>
    /// <param name="name">What to call the change, for anyone listening.</param>
    /// <returns>What changed, or null if that document is already current.</returns>
    /// <remarks>
    /// <para>
    /// The one way in that is not a transaction, and it is not an exception to the rule so much as
    /// the rule not applying: the document being restored was produced by a transaction and
    /// validated then. Re-validating it would be asking whether a state the document has already
    /// been in is allowed.
    /// </para>
    /// <para>
    /// Internal, so that undo is the only thing that can do this. A caller able to set the document
    /// to anything at all would be able to set it to something that never passed a commit.
    /// </para>
    /// </remarks>
    internal DocumentChange? Restore(Document document, string name)
    {
        ArgumentNullException.ThrowIfNull(document);

        DocumentChange change;

        lock (_gate)
        {
            if (_open is not null)
            {
                throw new InvalidOperationException(
                    $"A transaction ('{_open.Name}') is open on this document, so it cannot be "
                    + "put back to an earlier state underneath it.");
            }

            if (ReferenceEquals(_current, document))
            {
                return null;
            }

            // Every feature is a seed. Undo can change anything -- a parameter, the tree, the
            // rollback bar -- and working out precisely what moved would mean diffing two
            // documents, which is the guessing P3-T02 avoids by recording edits as they happen.
            // There is nothing to record here, so the honest answer is to mark everything and let
            // the geometry cache make it cheap, which after an undo it always can.
            change = new DocumentChange(
                name,
                _current,
                document,
                [.. document.Features.Select(f => f.Id)],
                [.. document.Parameters.Select(p => p.Name)]);

            _current = document;
        }

        Committed?.Invoke(change);

        return change;
    }

    /// <summary>Replaces the current document, from a committing transaction.</summary>
    /// <returns>What changed, or null if the transaction changed nothing.</returns>
    /// <remarks>
    /// The base version is checked even though only one transaction may be open at a time. That
    /// invariant is enforced a few lines above by a different mechanism, and a rule protected in
    /// one place only is a rule that ends at the first refactor. The check costs a comparison.
    /// </remarks>
    internal DocumentChange? Commit(
        DocumentTransaction transaction,
        Document result,
        ImmutableArray<FeatureId> touchedFeatures,
        ImmutableArray<string> touchedParameters)
    {
        DocumentChange change;

        lock (_gate)
        {
            if (!ReferenceEquals(_open, transaction))
            {
                throw new InvalidOperationException(
                    "This transaction is not the one open on its document, so committing it would "
                    + "overwrite whatever replaced it.");
            }

            if (ReferenceEquals(result, _current))
            {
                _open = null;
                return null;
            }

            change = new DocumentChange(
                transaction.Name, _current, result, touchedFeatures, touchedParameters);

            _current = result;
            _open = null;
        }

        // Outside the lock. A handler is free to read the session, and the rebuild engine that will
        // subscribe to this does real work -- holding the lock across it would make every reader of
        // Current wait for a rebuild, which is precisely the freeze this design exists to avoid.
        Committed?.Invoke(change);

        return change;
    }

    /// <summary>Releases the open transaction without changing anything.</summary>
    internal void Rollback(DocumentTransaction transaction)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_open, transaction))
            {
                _open = null;
            }
        }
    }
}
