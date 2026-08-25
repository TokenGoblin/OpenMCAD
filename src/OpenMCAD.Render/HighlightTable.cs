namespace OpenMCAD.Render;

/// <summary>How an entity is being called out in the viewport.</summary>
/// <remarks>
/// The numeric values are written into a GPU buffer and switched on in the shaders, so they are
/// part of the contract with <c>Surface.hlsl</c> and <c>Edges.hlsl</c>, not an implementation
/// detail. Ordered by precedence: where two states could apply, the higher wins.
/// </remarks>
public enum HighlightState : uint
{
    /// <summary>Drawn normally.</summary>
    None = 0,

    /// <summary>Under the cursor but not yet chosen. Called pre-selection in most CAD packages.</summary>
    PreSelected = 1,

    /// <summary>Chosen by the user.</summary>
    Selected = 2,

    /// <summary>Implicated in a failure — a feature that could not rebuild, a bad reference.</summary>
    /// <remarks>
    /// Ranks above selection deliberately. A user who has selected a face and is being told that
    /// same face is the reason their model will not rebuild needs to see the error, not the
    /// selection.
    /// </remarks>
    Error = 3,
}

/// <summary>
/// Which entities are highlighted, indexed by <see cref="DisplayId"/> (P2-T09).
/// </summary>
/// <remarks>
/// <para>
/// A dense array rather than a dictionary, because it is uploaded to the GPU as-is and read in the
/// pixel shader by the same id the ID pass writes. Ids are handed out sequentially from one within
/// a snapshot, so the array has no holes worth caring about — an assembly with a hundred thousand
/// entities costs four hundred kilobytes, which is less than one of its meshes.
/// </para>
/// <para>
/// <b>Immutable and versioned</b>, like <see cref="DisplaySnapshot"/> and for the same reason: the
/// render thread compares versions and re-uploads only when the number changes, and the table it
/// is reading cannot be edited underneath it while it does.
/// </para>
/// <para>
/// Keyed by display id rather than by kernel entity, because that is what the shader has. Turning
/// a selection of <c>SubEntity</c> values into this is the job of whatever owns the selection —
/// which lives a layer above and knows about both.
/// </para>
/// </remarks>
public sealed class HighlightTable
{
    private readonly uint[] _states;

    private HighlightTable(uint[] states, long version)
    {
        _states = states;
        Version = version;
    }

    /// <summary>Gets the table in which nothing is highlighted.</summary>
    public static HighlightTable Empty { get; } = new([], 0);

    /// <summary>Gets a number that changes whenever the contents do.</summary>
    public long Version { get; }

    /// <summary>Gets how many entries there are, including the unhighlighted ones between them.</summary>
    public int Length => _states.Length;

    /// <summary>Gets whether anything at all is highlighted.</summary>
    public bool IsEmpty => _states.Length == 0;

    /// <summary>Gets the raw states, for upload.</summary>
    public ReadOnlySpan<uint> Raw => _states;

    /// <summary>Gets the state of one entity.</summary>
    /// <param name="id">The entity to ask about.</param>
    /// <returns>Its state, or <see cref="HighlightState.None"/> if it has none.</returns>
    public HighlightState this[DisplayId id]
        => id.Value < (uint)_states.Length
            ? (HighlightState)_states[id.Value]
            : HighlightState.None;

    /// <summary>
    /// Builds a table from a set of highlighted entities.
    /// </summary>
    /// <param name="entries">What to highlight. Later entries do not overwrite stronger ones.</param>
    /// <param name="version">
    /// A number that must increase whenever the contents change, so the renderer knows to
    /// re-upload.
    /// </param>
    /// <returns>The table.</returns>
    /// <remarks>
    /// Where an entity appears twice — selected and also under the cursor, or selected and also in
    /// error — the higher state wins rather than the last one written. Depending on enumeration
    /// order for that would make a face flicker between two colours as an unordered set was
    /// iterated.
    /// </remarks>
    public static HighlightTable Build(
        IEnumerable<KeyValuePair<DisplayId, HighlightState>> entries, long version)
    {
        ArgumentNullException.ThrowIfNull(entries);

        List<KeyValuePair<DisplayId, HighlightState>> materialised = [.. entries];

        if (materialised.Count == 0)
        {
            return version == 0 ? Empty : new HighlightTable([], version);
        }

        uint highest = 0;

        foreach ((DisplayId id, HighlightState _) in materialised)
        {
            highest = System.Math.Max(highest, id.Value);
        }

        uint[] states = new uint[highest + 1];

        foreach ((DisplayId id, HighlightState state) in materialised)
        {
            if ((uint)state > states[id.Value])
            {
                states[id.Value] = (uint)state;
            }
        }

        return new HighlightTable(states, version);
    }
}
