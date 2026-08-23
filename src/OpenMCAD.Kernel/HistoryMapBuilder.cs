using System.Collections.Immutable;

namespace OpenMCAD.Kernel;

/// <summary>
/// Builds a <see cref="HistoryMap"/>, enforcing its invariants as it goes.
/// </summary>
/// <remarks>
/// <para>
/// Every operation implementation, in every kernel, records provenance through this type. It is
/// deliberately the only way to make a <see cref="HistoryMap"/>, because the invariants it enforces
/// are ones that are easy to violate by accident and expensive to detect later:
/// </para>
/// <list type="bullet">
/// <item><description>
/// Every output carries a deliberate role. <see cref="Build"/> throws on an
/// <see cref="OperationRole.Unknown"/> role rather than letting an incomplete implementation reach
/// the naming layer, where the symptom would be a model that breaks on edit weeks later.
/// </description></item>
/// <item><description>
/// An entity cannot be both deleted and modified. It may be both deleted and generating, which is
/// the ordinary fillet case and not an error.
/// </description></item>
/// <item><description>
/// Collections come out sorted, so the same operation run twice produces identical output
/// (ADR-0011).
/// </description></item>
/// </list>
/// <para>
/// Not thread-safe, and does not need to be: a builder belongs to one in-flight operation, and all
/// kernel work is serialised onto one thread anyway (ADR-0004).
/// </para>
/// </remarks>
public sealed class HistoryMapBuilder
{
    private readonly Dictionary<SubEntity, HashSet<SubEntity>> _generated = [];
    private readonly Dictionary<SubEntity, HashSet<SubEntity>> _modified = [];
    private readonly HashSet<SubEntity> _deleted = [];
    private readonly HashSet<SubEntity> _new = [];
    private readonly Dictionary<SubEntity, OperationRole> _roles = [];
    private readonly Dictionary<SubEntity, SubEntity> _sources = [];

    /*
     * First-sight order, kept alongside the sets above.
     *
     * The sets answer "is this entity described?"; these answer "in what order did the kernel
     * describe them?". The second question cannot be answered from the first, and it cannot be
     * answered by sorting either: SubEntity sorts by tag, and a tag is a handle carrying a slot
     * index and a generation counter, so recycled and fresh slots interleave unpredictably. The
     * kernel reports in canonical, geometry-derived order, and that is what determinism needs
     * preserved (ADR-0011).
     */
    private readonly List<SubEntity> _outputOrder = [];
    private readonly List<SubEntity> _inputOrder = [];
    private readonly List<SubEntity> _newOrder = [];
    private readonly HashSet<SubEntity> _inputSeen = [];

    /// <summary>
    /// Records that <paramref name="input"/> caused <paramref name="output"/> to come into
    /// existence.
    /// </summary>
    /// <param name="input">The input entity.</param>
    /// <param name="output">The entity it generated.</param>
    /// <param name="role">What the output is. Must not be <see cref="OperationRole.Unknown"/>.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentException">An entity is invalid, or the role is unassigned.</exception>
    public HistoryMapBuilder AddGenerated(SubEntity input, SubEntity output, OperationRole role)
    {
        Require(input, nameof(input));
        Require(output, nameof(output));
        RequireRole(role, output);

        NoteInput(input);
        AddTo(_generated, input, output);
        AssignRole(output, role);
        _sources.TryAdd(output, input);
        return this;
    }

    /// <summary>
    /// Records that <paramref name="output"/> is the altered successor of <paramref name="input"/>.
    /// </summary>
    /// <param name="input">The input entity.</param>
    /// <param name="output">Its successor.</param>
    /// <param name="role">What the output is. Must not be <see cref="OperationRole.Unknown"/>.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentException">An entity is invalid, or the role is unassigned.</exception>
    public HistoryMapBuilder AddModified(SubEntity input, SubEntity output, OperationRole role)
    {
        Require(input, nameof(input));
        Require(output, nameof(output));
        RequireRole(role, output);

        NoteInput(input);
        AddTo(_modified, input, output);
        AssignRole(output, role);
        _sources.TryAdd(output, input);
        return this;
    }

    /// <summary>
    /// Records that <paramref name="input"/> passed through the operation unchanged.
    /// </summary>
    /// <param name="input">The input entity.</param>
    /// <param name="output">The same entity in the output shape.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <remarks>
    /// Worth recording even though nothing happened. The untouched faces are the majority of any
    /// boolean, and a name that cannot resolve straight through an operation that did not affect it
    /// is a name that breaks for no reason.
    /// </remarks>
    public HistoryMapBuilder AddRetained(SubEntity input, SubEntity output)
        => AddModified(input, output, OperationRole.Retained);

    /// <summary>Records that <paramref name="input"/> has no successor in the output.</summary>
    /// <param name="input">The consumed entity.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentException">The entity is invalid.</exception>
    /// <remarks>
    /// Deletion is about <i>succession</i>, not about influence. A deleted entity may still have
    /// generated something — a filleted edge is consumed and yet is the reason the blend face
    /// exists — so <see cref="AddGenerated"/> is legal alongside this. What is not legal is
    /// <see cref="AddModified"/>, which asserts the entity survived in altered form.
    /// </remarks>
    public HistoryMapBuilder AddDeleted(SubEntity input)
    {
        Require(input, nameof(input));
        NoteInput(input);
        _deleted.Add(input);
        return this;
    }

    /// <summary>
    /// Records an entity created from nothing, with no input to trace it to.
    /// </summary>
    /// <param name="output">The new entity.</param>
    /// <param name="role">What it is. Must not be <see cref="OperationRole.Unknown"/>.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentException">The entity is invalid, or the role is unassigned.</exception>
    public HistoryMapBuilder AddNew(SubEntity output, OperationRole role)
    {
        Require(output, nameof(output));
        RequireRole(role, output);

        if (_new.Add(output))
        {
            _newOrder.Add(output);
        }

        AssignRole(output, role);
        return this;
    }

    /// <summary>
    /// Records an entity created from nothing but lying between two known inputs.
    /// </summary>
    /// <param name="output">The new entity.</param>
    /// <param name="role">What it is.</param>
    /// <param name="between">The inputs it was created between.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <remarks>
    /// The important case for naming. A fillet's blend face is created from nothing, but it is not
    /// anonymous: it is the blend between <i>these two specific faces</i>, and that is what makes it
    /// nameable at all. Recording the relationship as generation from each contributing input is
    /// what lets a name for it survive a rebuild.
    /// </remarks>
    public HistoryMapBuilder AddNewBetween(
        SubEntity output,
        OperationRole role,
        params ReadOnlySpan<SubEntity> between)
    {
        Require(output, nameof(output));
        RequireRole(role, output);

        if (_new.Add(output))
        {
            _newOrder.Add(output);
        }

        AssignRole(output, role);

        foreach (SubEntity input in between)
        {
            Require(input, nameof(between));
            NoteInput(input);
            AddTo(_generated, input, output);
        }

        // Deliberately no single source: the entity has several contributing inputs and picking
        // one arbitrarily would make SourceOf lie about a symmetric relationship.
        return this;
    }

    /// <summary>Builds the map.</summary>
    /// <exception cref="InvalidOperationException">
    /// An output carries no deliberate role, or an entity is both deleted and modified.
    /// </exception>
    public HistoryMap Build()
    {
        List<SubEntity> unrolled = [.. _roles.Where(p => p.Value == OperationRole.Unknown).Select(p => p.Key)];
        if (unrolled.Count > 0)
        {
            throw new InvalidOperationException(
                $"{unrolled.Count} output entities have no OperationRole. Every operation must "
                + "assign roles deliberately (PLAN.md 5.1); an unrolled output is an incomplete "
                + $"implementation. First: {unrolled[0]}.");
        }

        // Deleted conflicts with Modified, but NOT with Generated. The distinction is the whole
        // fillet case: a filleted edge is consumed -- it has no successor -- and it is also the
        // cause of the blend face that replaced it. Forbidding that combination made the single
        // most common blend impossible to record faithfully, and the OCCT spike confirmed the
        // kernel reports exactly it: Generated(edge) yields the blend face, and IsDeleted(edge)
        // is true.
        List<SubEntity> contradictory = [.. _deleted.Where(_modified.ContainsKey)];

        if (contradictory.Count > 0)
        {
            throw new InvalidOperationException(
                $"Entity {contradictory[0]} is recorded as deleted but also has a modified "
                + "successor. Modified means the same entity, altered, so it survives; an entity "
                + "cannot both survive and be gone. (Generated is different and is allowed "
                + "alongside deletion: a consumed edge may still have created a blend face.)");
        }

        return new HistoryMap(
            Freeze(_generated),
            Freeze(_modified),
            [.. _deleted],
            [.. _newOrder],
            _roles.ToImmutableDictionary(),
            _sources.ToImmutableDictionary(),
            [.. _outputOrder],
            [.. _inputOrder]);
    }

    private static void AddTo(
        Dictionary<SubEntity, HashSet<SubEntity>> map,
        SubEntity key,
        SubEntity value)
    {
        if (!map.TryGetValue(key, out HashSet<SubEntity>? set))
        {
            set = [];
            map[key] = set;
        }

        set.Add(value);
    }

    /// <summary>Records an input the first time it is described.</summary>
    /// <param name="input">The input entity.</param>
    private void NoteInput(SubEntity input)
    {
        if (_inputSeen.Add(input))
        {
            _inputOrder.Add(input);
        }
    }

    private void AssignRole(SubEntity output, OperationRole role)
    {
        if (!_roles.ContainsKey(output))
        {
            _outputOrder.Add(output);
        }

        if (_roles.TryGetValue(output, out OperationRole existing) && existing != role)
        {
            throw new InvalidOperationException(
                $"Entity {output} was already assigned role {existing} and cannot also be {role}. "
                + "An output has exactly one role; if it genuinely serves two purposes, the role "
                + "enum is missing a value for what it actually is.");
        }

        _roles[output] = role;
    }

    private static ImmutableDictionary<SubEntity, ImmutableArray<SubEntity>> Freeze(
        Dictionary<SubEntity, HashSet<SubEntity>> map)
        => map.ToImmutableDictionary(
            pair => pair.Key,
            pair => ImmutableArray.CreateRange(pair.Value.Order()));

    private static void Require(SubEntity entity, string parameterName)
    {
        if (!entity.IsValid)
        {
            throw new ArgumentException("The entity is not valid.", parameterName);
        }
    }

    private static void RequireRole(OperationRole role, SubEntity output)
    {
        if (role == OperationRole.Unknown)
        {
            throw new ArgumentException(
                $"Output {output} must be given a deliberate OperationRole. Unknown exists so that "
                + "omission is detectable, not so that it can be used (PLAN.md 5.1).",
                nameof(role));
        }
    }
}
