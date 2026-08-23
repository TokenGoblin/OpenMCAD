using System.Collections.Immutable;

namespace OpenMCAD.Kernel;

/// <summary>
/// The correspondence between an operation's input entities and its output entities.
/// </summary>
/// <remarks>
/// <para>
/// PLAN.md 5.1 calls this "the critical return value", and ADR-0002 makes it non-negotiable in
/// every operation signature. It is the raw material the naming layer (ADR-0005) turns into names
/// that survive a rebuild, and it is the one thing a geometry kernel cannot be asked for after the
/// fact — the correspondence exists only while the operation is running. An operation that returns
/// a shape without a history map has destroyed information that cannot be recovered.
/// </para>
/// <para>
/// Every collection this type returns is sorted, so that two runs of the same operation produce
/// byte-identical output. Determinism is a hard requirement (ADR-0011) and unordered iteration is
/// the most common way to lose it.
/// </para>
/// <para>
/// Build one with <see cref="HistoryMapBuilder"/>, which enforces the invariants — chiefly that
/// every output carries a deliberate <see cref="OperationRole"/>.
/// </para>
/// </remarks>
public sealed class HistoryMap
{
    private readonly ImmutableDictionary<SubEntity, ImmutableArray<SubEntity>> _generated;
    private readonly ImmutableDictionary<SubEntity, ImmutableArray<SubEntity>> _modified;
    private readonly ImmutableHashSet<SubEntity> _deleted;
    private readonly ImmutableDictionary<SubEntity, OperationRole> _roles;
    private readonly ImmutableDictionary<SubEntity, SubEntity> _sources;
    private readonly ImmutableArray<SubEntity> _outputOrder;
    private readonly ImmutableArray<SubEntity> _inputOrder;

    internal HistoryMap(
        ImmutableDictionary<SubEntity, ImmutableArray<SubEntity>> generated,
        ImmutableDictionary<SubEntity, ImmutableArray<SubEntity>> modified,
        ImmutableHashSet<SubEntity> deleted,
        ImmutableArray<SubEntity> newEntities,
        ImmutableDictionary<SubEntity, OperationRole> roles,
        ImmutableDictionary<SubEntity, SubEntity> sources,
        ImmutableArray<SubEntity> outputOrder,
        ImmutableArray<SubEntity> inputOrder)
    {
        _generated = generated;
        _modified = modified;
        _deleted = deleted;
        _roles = roles;
        _sources = sources;
        _outputOrder = outputOrder;
        _inputOrder = inputOrder;
        NewEntities = newEntities;
    }

    /// <summary>
    /// Gets the empty map, for operations with no inputs to correlate and for failed results.
    /// </summary>
    public static HistoryMap Empty { get; } = new(
        ImmutableDictionary<SubEntity, ImmutableArray<SubEntity>>.Empty,
        ImmutableDictionary<SubEntity, ImmutableArray<SubEntity>>.Empty,
        ImmutableHashSet<SubEntity>.Empty,
        [],
        ImmutableDictionary<SubEntity, OperationRole>.Empty,
        ImmutableDictionary<SubEntity, SubEntity>.Empty,
        [],
        []);

    /// <summary>
    /// Gets the entities created from nothing, sorted.
    /// </summary>
    /// <remarks>
    /// These have no input to trace back to — a fillet's blend face is the canonical example. They
    /// are named by their role and by the entities they lie between, not by descent.
    /// </remarks>
    public ImmutableArray<SubEntity> NewEntities { get; }

    /// <summary>Gets every output entity this map describes, in the order the kernel reported them.</summary>
    /// <remarks>
    /// <para>
    /// Reported order, not sorted order, and the distinction is what makes this deterministic.
    /// Sorting means sorting by <see cref="SubEntity.Tag"/>, and a tag is a handle: it carries a
    /// slot index and a generation counter, so an entity in a recycled slot sorts nowhere near an
    /// otherwise identical entity in a fresh one. Two identical models built in the same process
    /// then enumerate their outputs in different orders purely because of what was allocated
    /// before them, which ADR-0011 forbids.
    /// </para>
    /// <para>
    /// The kernel reports outputs in canonical, geometry-derived order (see the shim's
    /// <c>enumerate_canonical</c>), and that order is preserved here rather than discarded and
    /// reconstructed from numbers that mean nothing geometric.
    /// </para>
    /// </remarks>
    public IEnumerable<SubEntity> Outputs => _outputOrder;

    /// <summary>Gets every input entity this map describes, in the order the kernel reported them.</summary>
    /// <remarks>See <see cref="Outputs"/> for why this is not sorted.</remarks>
    public IEnumerable<SubEntity> Inputs => _inputOrder;

    /// <summary>Gets a value indicating whether this map describes nothing.</summary>
    public bool IsEmpty => _roles.IsEmpty && _deleted.IsEmpty;

    /// <summary>
    /// Returns the entities that <paramref name="input"/> caused to come into existence, sorted.
    /// </summary>
    /// <param name="input">An input entity.</param>
    /// <remarks>
    /// Generation is the "swept from" relationship: a profile edge generates a side wall, a profile
    /// vertex generates a side edge. The output is a different kind of thing than the input, made
    /// because the input was there.
    /// </remarks>
    public ImmutableArray<SubEntity> Generated(SubEntity input)
        => _generated.TryGetValue(input, out ImmutableArray<SubEntity> result) ? result : [];

    /// <summary>
    /// Returns the successors of <paramref name="input"/> — the same entity, altered — sorted.
    /// </summary>
    /// <param name="input">An input entity.</param>
    /// <remarks>
    /// <para>
    /// Modification is the "still the same face, but changed" relationship: a face trimmed by a
    /// boolean, or split by one.
    /// </para>
    /// <para>
    /// <b>The result is a list, not a single entity, and that is the whole difficulty.</b> A face
    /// cut in two has two successors, and the feature that referenced it must say which it meant.
    /// PLAN.md 5.3 requires dependent features to declare a multiplicity policy for exactly this
    /// case; it is where most naming bugs live.
    /// </para>
    /// </remarks>
    public ImmutableArray<SubEntity> Modified(SubEntity input)
        => _modified.TryGetValue(input, out ImmutableArray<SubEntity> result) ? result : [];

    /// <summary>
    /// Returns whether <paramref name="input"/> has no successor in the output.
    /// </summary>
    /// <param name="input">An input entity.</param>
    /// <remarks>
    /// A reference to a deleted entity is not an error to paper over. It means a downstream feature
    /// has lost what it pointed at, and the correct response is to mark that feature in error
    /// (PLAN.md 5.3 tier 3) rather than to guess at a substitute.
    /// </remarks>
    public bool IsDeleted(SubEntity input) => _deleted.Contains(input);

    /// <summary>
    /// Returns what <paramref name="output"/> is, in the operation's own terms.
    /// </summary>
    /// <param name="output">An output entity.</param>
    /// <returns>
    /// The role, or <see cref="OperationRole.Unknown"/> if the entity is not one this map describes.
    /// </returns>
    public OperationRole RoleOf(SubEntity output)
        => _roles.TryGetValue(output, out OperationRole role) ? role : OperationRole.Unknown;

    /// <summary>
    /// Returns the input entity that <paramref name="output"/> came from, if any.
    /// </summary>
    /// <param name="output">An output entity.</param>
    /// <returns>The source entity, or <see cref="SubEntity.None"/> for entities created from nothing.</returns>
    /// <remarks>
    /// The reverse of <see cref="Generated"/> and <see cref="Modified"/>. Name resolution walks
    /// forward from inputs, but diagnostics and the repair UI need to walk backward from a result
    /// the user is looking at.
    /// </remarks>
    public SubEntity SourceOf(SubEntity output)
        => _sources.TryGetValue(output, out SubEntity source) ? source : SubEntity.None;

    /// <summary>
    /// Returns every output whose role is <paramref name="role"/>, in the order the kernel
    /// reported them.
    /// </summary>
    /// <param name="role">The role to select.</param>
    /// <remarks>
    /// Filtered from <see cref="Outputs"/> rather than selected from the role dictionary and
    /// sorted. See <see cref="Outputs"/> for why sorting is the wrong instrument here: it would
    /// sort by tag, and a tag is a handle rather than a description of geometry.
    /// </remarks>
    public IEnumerable<SubEntity> WithRole(OperationRole role)
        => _outputOrder.Where(output => RoleOf(output) == role);

    /// <summary>
    /// Returns the outputs that carry no deliberate role, which should always be empty.
    /// </summary>
    /// <remarks>
    /// PLAN.md 5.1: an operation returning unrolled outputs is an incomplete implementation and
    /// fails review. <see cref="HistoryMapBuilder"/> refuses to produce such a map, so a non-empty
    /// result here means something bypassed the builder.
    /// </remarks>
    public IEnumerable<SubEntity> UnrolledOutputs => WithRole(OperationRole.Unknown);
}
