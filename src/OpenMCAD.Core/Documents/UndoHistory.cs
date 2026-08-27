using System.Collections.Immutable;

namespace OpenMCAD.Core.Documents;

/// <summary>
/// What the user can undo and redo, and the names to put on the menu items.
/// </summary>
/// <remarks>
/// <para>
/// <b>A stack of documents rather than a log of inverse commands.</b> The plan's phrasing for this
/// task is a command log over parameter state, which is what a mutable document forces: to undo an
/// edit you must know how to reverse it, every kind of edit needs its own inverse, and an inverse
/// that is subtly wrong corrupts the model in a way that only appears later. P3-T01 made
/// <see cref="Document"/> immutable, and undo became holding an earlier reference — which cannot be
/// subtly wrong, because it is not a computation.
/// </para>
/// <para>
/// The saving is not only in the code that is absent. Restoring a reference restores the bodies,
/// the rebuild report and the rollback bar with it, all exactly as they were, so there is no
/// question of the geometry and the tree disagreeing after an undo. An inverse-command scheme has
/// to get every one of those right separately.
/// </para>
/// <para>
/// <b>Grouping comes from the transaction.</b> §5.4 already makes a transaction the unit of edit,
/// and this records one entry per commit — so a user action that added a feature, set three of its
/// parameters and named it is one undo, because it was one transaction. Nothing here needs to know
/// how edits group; it inherits the answer.
/// </para>
/// </remarks>
public sealed class UndoHistory : IDisposable
{
    /// <summary>How many edits are remembered when no other depth is asked for.</summary>
    public const int DefaultDepth = 100;

    private readonly DocumentSession _session;
    private readonly int _depth;
    private readonly List<DocumentChange> _done = [];
    private readonly List<DocumentChange> _undone = [];

    private bool _restoring;
    private bool _disposed;

    /// <summary>Starts remembering what happens to a document.</summary>
    /// <param name="session">The session to watch.</param>
    /// <param name="depth">How many edits to remember.</param>
    public UndoHistory(DocumentSession session, int depth = DefaultDepth)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);

        _session = session;
        _depth = depth;

        _session.Committed += OnCommitted;
    }

    /// <summary>Raised when what can be undone or redone changes.</summary>
    public event Action? Changed;

    /// <summary>Gets whether there is anything to undo.</summary>
    public bool CanUndo => _done.Count > 0;

    /// <summary>Gets whether there is anything to redo.</summary>
    public bool CanRedo => _undone.Count > 0;

    /// <summary>Gets what undoing would undo, for the menu item.</summary>
    public string? UndoName => CanUndo ? _done[^1].Name : null;

    /// <summary>Gets what redoing would redo.</summary>
    public string? RedoName => CanRedo ? _undone[^1].Name : null;

    /// <summary>Gets the edits that can be undone, most recent last.</summary>
    public ImmutableArray<string> History => [.. _done.Select(c => c.Name)];

    /// <summary>Puts the document back to before the last edit.</summary>
    /// <returns>Whether there was anything to undo.</returns>
    public bool Undo() => Step(_done, _undone, change => change.Before);

    /// <summary>Puts back an edit that was undone.</summary>
    /// <returns>Whether there was anything to redo.</returns>
    public bool Redo() => Step(_undone, _done, change => change.After);

    /// <summary>Forgets everything, without touching the document.</summary>
    /// <remarks>
    /// What saving a file does, if the application chooses to: the edits are still real, but the
    /// point they would return to is no longer interesting.
    /// </remarks>
    public void Clear()
    {
        if (_done.Count == 0 && _undone.Count == 0)
        {
            return;
        }

        _done.Clear();
        _undone.Clear();

        Changed?.Invoke();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Committed -= OnCommitted;
    }

    private bool Step(
        List<DocumentChange> from, List<DocumentChange> to, Func<DocumentChange, Document> pick)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (from.Count == 0)
        {
            return false;
        }

        DocumentChange change = from[^1];
        from.RemoveAt(from.Count - 1);

        // The flag is what stops the restore being recorded as a new edit, which would put the
        // undone change straight back on the stack and make undo a no-op that looked like it had
        // worked.
        _restoring = true;

        try
        {
            _session.Restore(pick(change), change.Name);
        }
        finally
        {
            _restoring = false;
        }

        to.Add(change);
        Changed?.Invoke();

        return true;
    }

    private void OnCommitted(DocumentChange change)
    {
        if (_restoring)
        {
            return;
        }

        _done.Add(change);

        // A new edit after an undo makes the undone ones unreachable. Keeping them would offer a
        // redo that jumps to a document with no path from the one on screen.
        _undone.Clear();

        while (_done.Count > _depth)
        {
            // The oldest goes. Its Before document is then referred to by nothing and is collected
            // with everything only it held -- which is what bounds the memory a long session uses,
            // since the documents in between share almost all of their structure.
            _done.RemoveAt(0);
        }

        Changed?.Invoke();
    }
}
