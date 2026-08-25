using OpenMCAD.Kernel;
using OpenMCAD.Render;

namespace OpenMCAD.Interaction.Selection;

/// <summary>What a click does to the existing selection.</summary>
public enum SelectionAction
{
    /// <summary>Clear everything else and select this alone.</summary>
    Replace,

    /// <summary>Add to the selection, leaving what is already there.</summary>
    Add,

    /// <summary>Add if absent, remove if present.</summary>
    Toggle,

    /// <summary>Remove from the selection.</summary>
    Remove,
}

/// <summary>
/// What the user has selected, and what is under the cursor (P2-T09).
/// </summary>
/// <remarks>
/// <para>
/// <b>Holds <see cref="SubEntity"/>, not <see cref="DisplayId"/>.</b> A display id is scoped to
/// one snapshot and means something different in the next — selecting a face and then nudging a
/// dimension would silently move the selection to whatever entity inherited the number. A
/// <see cref="SubEntity"/> names the thing itself.
/// </para>
/// <para>
/// That is still not permanent. Surviving a rebuild that changes the topology needs the persistent
/// naming of PLAN.md 5.3, which does not exist yet; until it does, a selection survives camera
/// movement, tessellation changes and re-picking, and does not survive a modelling operation that
/// renumbers the entity. That is the honest boundary, and the reason selection is stored here
/// rather than being resolved to persistent names it cannot yet produce.
/// </para>
/// <para>
/// Pre-selection — the entity merely under the cursor — is kept separate from the selection
/// proper. Merging them is a common shortcut and it makes hover destroy the user's selection every
/// time the mouse crosses the model on the way to a menu.
/// </para>
/// </remarks>
public sealed class SelectionSet
{
    private readonly HashSet<SubEntity> _selected = [];

    private SubEntity _preSelected = SubEntity.None;
    private long _version;

    /// <summary>Gets a number that changes whenever the selection or pre-selection does.</summary>
    /// <remarks>
    /// What the renderer compares to decide whether to re-upload. It moves on hover as well as on
    /// selection, because hover changes what is drawn.
    /// </remarks>
    public long Version => _version;

    /// <summary>Gets the selected entities.</summary>
    public IReadOnlyCollection<SubEntity> Selected => _selected;

    /// <summary>Gets how many entities are selected.</summary>
    public int Count => _selected.Count;

    /// <summary>Gets whether anything is selected.</summary>
    public bool IsEmpty => _selected.Count == 0;

    /// <summary>Gets the entity under the cursor, or <see cref="SubEntity.None"/>.</summary>
    public SubEntity PreSelected => _preSelected;

    /// <summary>Gets or sets entities to be shown as being in error.</summary>
    /// <remarks>
    /// Set by whatever reported the failure — a rebuild that could not proceed, a reference that
    /// no longer resolves. Kept apart from selection so that clearing one does not clear the
    /// other: a user investigating an error will click around, and having the error markers
    /// disappear as they do is exactly backwards.
    /// </remarks>
    public IReadOnlySet<SubEntity> Faulted { get; private set; } = new HashSet<SubEntity>();

    /// <summary>Whether an entity is selected.</summary>
    /// <param name="entity">The entity to test.</param>
    /// <returns>Whether it is in the selection.</returns>
    public bool Contains(SubEntity entity) => _selected.Contains(entity);

    /// <summary>
    /// Applies a click to the selection.
    /// </summary>
    /// <param name="entity">What was picked. <see cref="SubEntity.None"/> means empty space.</param>
    /// <param name="action">What the click should do.</param>
    /// <returns>Whether the selection changed.</returns>
    /// <remarks>
    /// Clicking empty space with <see cref="SelectionAction.Replace"/> clears the selection, which
    /// is what every application does and what users expect from a click on the background. The
    /// other actions leave it alone, so a mis-aimed Control-click does not throw away a selection
    /// that took a while to build.
    /// </remarks>
    public bool Apply(SubEntity entity, SelectionAction action)
    {
        if (entity == SubEntity.None)
        {
            return action == SelectionAction.Replace && Clear();
        }

        bool changed = action switch
        {
            SelectionAction.Replace => Replace(entity),
            SelectionAction.Add => _selected.Add(entity),
            SelectionAction.Remove => _selected.Remove(entity),
            // Remove returns whether it removed, Add whether it added -- so one call answers both
            // "did this toggle off" and "did anything change".
            SelectionAction.Toggle => _selected.Remove(entity) || _selected.Add(entity),
            _ => false,
        };

        if (changed)
        {
            _version++;
        }

        return changed;
    }

    /// <summary>Sets what is under the cursor.</summary>
    /// <param name="entity">The entity, or <see cref="SubEntity.None"/>.</param>
    /// <returns>Whether it changed.</returns>
    public bool SetPreSelected(SubEntity entity)
    {
        if (_preSelected == entity)
        {
            return false;
        }

        _preSelected = entity;
        _version++;

        return true;
    }

    /// <summary>Empties the selection, leaving pre-selection and errors alone.</summary>
    /// <returns>Whether anything was removed.</returns>
    public bool Clear()
    {
        if (_selected.Count == 0)
        {
            return false;
        }

        _selected.Clear();
        _version++;

        return true;
    }

    /// <summary>Replaces the set of entities shown as being in error.</summary>
    /// <param name="faulted">The entities to mark, or empty to clear.</param>
    public void SetFaulted(IEnumerable<SubEntity> faulted)
    {
        ArgumentNullException.ThrowIfNull(faulted);

        HashSet<SubEntity> replacement = [.. faulted];

        if (replacement.SetEquals(Faulted))
        {
            return;
        }

        Faulted = replacement;
        _version++;
    }

    /// <summary>
    /// Projects the selection onto the display ids of a snapshot.
    /// </summary>
    /// <param name="snapshot">The snapshot being drawn.</param>
    /// <returns>A table the renderer can upload.</returns>
    /// <remarks>
    /// <para>
    /// The mapping runs the other way from the pick: a pick resolves an id to an entity, and this
    /// has to find every id naming a selected entity. The snapshot's dictionary is
    /// id-to-entity, so this walks it once — an assembly's worth of entities is a few tens of
    /// thousands of dictionary entries, which is nothing beside the rebuild that produced them,
    /// and it happens only when the selection changes rather than per frame.
    /// </para>
    /// <para>
    /// The version combines the selection's with the snapshot's, so that re-selecting the same
    /// entities after a rebuild still counts as a change — the ids will differ even though the
    /// selection did not.
    /// </para>
    /// </remarks>
    public HighlightTable ToHighlights(DisplaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (_selected.Count == 0 && _preSelected == SubEntity.None && Faulted.Count == 0)
        {
            return HighlightTable.Empty;
        }

        List<KeyValuePair<DisplayId, HighlightState>> entries = [];

        foreach ((DisplayId id, SubEntity entity) in snapshot.Entities)
        {
            HighlightState state = StateOf(entity);

            if (state != HighlightState.None)
            {
                entries.Add(new KeyValuePair<DisplayId, HighlightState>(id, state));
            }
        }

        return HighlightTable.Build(entries, HashCode.Combine(_version, snapshot.Version));
    }

    private HighlightState StateOf(SubEntity entity)
    {
        if (Faulted.Contains(entity))
        {
            return HighlightState.Error;
        }

        if (_selected.Contains(entity))
        {
            return HighlightState.Selected;
        }

        return entity == _preSelected ? HighlightState.PreSelected : HighlightState.None;
    }

    private bool Replace(SubEntity entity)
    {
        if (_selected.Count == 1 && _selected.Contains(entity))
        {
            return false;
        }

        _selected.Clear();
        _selected.Add(entity);

        return true;
    }
}
