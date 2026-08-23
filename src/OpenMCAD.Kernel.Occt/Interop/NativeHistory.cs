using System.Collections.Immutable;

namespace OpenMCAD.Kernel.Occt.Interop;

/// <summary>
/// Reads a native history handle into a <see cref="HistoryMap"/>.
/// </summary>
/// <remarks>
/// <para>
/// The shim reports provenance as bare tags. A <see cref="SubEntity"/> needs the owning shape and
/// the entity kind as well, and neither crosses the boundary with the tag -- so both are recovered
/// here by enumerating the shapes involved once and indexing what comes back.
/// </para>
/// <para>
/// Enumerating up front rather than asking per tag is deliberate. The alternative is calling
/// <c>entity_kind</c> against each candidate shape until one does not throw, which uses exceptions
/// for control flow and is quadratic in the number of tools. Three enumerate calls per shape
/// answers both questions for every tag at once.
/// </para>
/// </remarks>
internal static class NativeHistory
{
    /// <summary>The entity kinds that carry provenance, matching the shim's own list.</summary>
    /// <remarks>
    /// Wires, shells and solids are omitted on purpose: they are containers whose identity follows
    /// from their contents, and no naming reference resolves to one.
    /// </remarks>
    private static readonly SubEntityKind[] MappedKinds =
        [SubEntityKind.Face, SubEntityKind.Edge, SubEntityKind.Vertex];

    /// <summary>
    /// Builds the managed history map for one operation.
    /// </summary>
    /// <param name="history">The native history handle.</param>
    /// <param name="inputs">The shapes the operation consumed. Empty for a primitive.</param>
    /// <param name="output">The shape it produced.</param>
    /// <returns>The map.</returns>
    /// <exception cref="NativeCallException">A native call failed.</exception>
    internal static HistoryMap Read(
        ulong history,
        ReadOnlySpan<KernelShape> inputs,
        KernelShape output)
    {
        Dictionary<ulong, SubEntity> inputEntities = [];
        Dictionary<ulong, int> inputOrder = [];
        foreach (KernelShape shape in inputs)
        {
            Index(shape, inputEntities, inputOrder);
        }

        Dictionary<ulong, SubEntity> outputEntities = [];
        Dictionary<ulong, int> outputOrder = [];
        Index(output, outputEntities, outputOrder);

        HistoryMapBuilder builder = new();

        // Canonical order, not the order the shim happened to list them in. The shim sorts by tag,
        // and a tag carries a generation counter in its high bits, so an entity in a recycled slot
        // sorts nowhere near an equivalent one in a fresh slot. HistoryMap preserves the order it
        // is given, so this is where determinism is either established or lost (ADR-0011).
        foreach (ulong inputTag in Canonical(
            Native.Read<ulong>(
                (Span<ulong> buffer, int capacity, out int required)
                    => OcctBindings.HistoryInputs(history, buffer, capacity, out required),
                nameof(OcctBindings.HistoryInputs)),
            inputOrder))
        {
            if (!inputEntities.TryGetValue(inputTag, out SubEntity input))
            {
                // An input the operation described but that no input shape owns. That is a shim
                // bug rather than a modelling outcome, and it must not be papered over: a history
                // referring to entities nobody can name is exactly what ADR-0005 cannot tolerate.
                throw new NativeCallException(
                    NativeStatus.Internal,
                    nameof(OcctBindings.HistoryInputs),
                    $"The history describes input entity {inputTag}, which belongs to none of the "
                    + $"{inputs.Length} shapes the operation was given.");
            }

            foreach (ulong madeTag in Canonical(
                Native.Read<ulong>(
                    (Span<ulong> buffer, int capacity, out int required)
                        => OcctBindings.HistoryGenerated(history, inputTag, buffer, capacity, out required),
                    nameof(OcctBindings.HistoryGenerated)),
                outputOrder))
            {
                SubEntity made = Resolve(outputEntities, madeTag, nameof(OcctBindings.HistoryGenerated));
                builder.AddGenerated(input, made, RoleOf(history, madeTag));
            }

            foreach (ulong changedTag in Canonical(
                Native.Read<ulong>(
                    (Span<ulong> buffer, int capacity, out int required)
                        => OcctBindings.HistoryModified(history, inputTag, buffer, capacity, out required),
                    nameof(OcctBindings.HistoryModified)),
                outputOrder))
            {
                SubEntity changed =
                    Resolve(outputEntities, changedTag, nameof(OcctBindings.HistoryModified));

                OperationRole role = RoleOf(history, changedTag);

                // The shim records a survivor as modified-with-Retained, because on its side there
                // is one relation. The managed map distinguishes the two, and the distinction
                // matters: a retained entity keeps its name outright, where a modified one has to
                // be re-matched.
                if (role == OperationRole.Retained)
                {
                    builder.AddRetained(input, changed);
                }
                else
                {
                    builder.AddModified(input, changed, role);
                }
            }

            Native.Check(
                OcctBindings.HistoryIsDeleted(history, inputTag, out int deleted),
                nameof(OcctBindings.HistoryIsDeleted));

            if (deleted != 0)
            {
                builder.AddDeleted(input);
            }
        }

        foreach (ulong newTag in Canonical(
            Native.Read<ulong>(
                (Span<ulong> buffer, int capacity, out int required)
                    => OcctBindings.HistoryNewEntities(history, buffer, capacity, out required),
                nameof(OcctBindings.HistoryNewEntities)),
            outputOrder))
        {
            SubEntity made = Resolve(outputEntities, newTag, nameof(OcctBindings.HistoryNewEntities));
            builder.AddNew(made, RoleOf(history, newTag));
        }

        return builder.Build();
    }

    /// <summary>Adds every nameable entity of one shape to <paramref name="into"/>.</summary>
    /// <param name="shape">The shape to enumerate.</param>
    /// <param name="into">The tag-to-entity index being built.</param>
    /// <param name="order">The tag-to-canonical-position index being built alongside it.</param>
    private static void Index(
        KernelShape shape, Dictionary<ulong, SubEntity> into, Dictionary<ulong, int> order)
    {
        if (!shape.IsValid)
        {
            return;
        }

        foreach (SubEntityKind kind in MappedKinds)
        {
            int nativeKind = (int)kind;

            foreach (ulong tag in Native.Read<ulong>(
                (Span<ulong> buffer, int capacity, out int required)
                    => OcctBindings.Enumerate(shape.Tag, nativeKind, buffer, capacity, out required),
                nameof(OcctBindings.Enumerate)))
            {
                into[tag] = new SubEntity(shape, tag, kind);

                // Enumerate returns canonical order, and MappedKinds fixes the order of the kinds
                // themselves, so a running counter across the whole walk is a total canonical
                // ordering over every nameable entity of every shape involved.
                order.TryAdd(tag, order.Count);
            }
        }
    }

    /// <summary>Puts tags into canonical order.</summary>
    /// <param name="tags">The tags, in whatever order the shim listed them.</param>
    /// <param name="order">The canonical position of each tag, from <see cref="Index"/>.</param>
    /// <returns>The tags, canonically ordered.</returns>
    /// <remarks>
    /// A tag with no canonical position sorts last rather than throwing. The callers all resolve
    /// their tags immediately afterwards and raise a precise error there; failing here would
    /// report "could not order" when the real problem is "the result does not contain it".
    /// </remarks>
    private static IEnumerable<ulong> Canonical(ulong[] tags, Dictionary<ulong, int> order)
        => tags.OrderBy(tag => order.TryGetValue(tag, out int position) ? position : int.MaxValue);

    /// <summary>Looks up an output tag, failing loudly rather than inventing an entity.</summary>
    /// <param name="entities">The output index.</param>
    /// <param name="tag">The tag to resolve.</param>
    /// <param name="operation">The native call that produced the tag.</param>
    /// <returns>The entity.</returns>
    private static SubEntity Resolve(
        Dictionary<ulong, SubEntity> entities, ulong tag, string operation)
    {
        if (entities.TryGetValue(tag, out SubEntity entity))
        {
            return entity;
        }

        throw new NativeCallException(
            NativeStatus.Internal,
            operation,
            $"The history names output entity {tag}, which the result shape does not contain.");
    }

    /// <summary>Reads the role the shim assigned to an output entity.</summary>
    /// <param name="history">The history handle.</param>
    /// <param name="tag">The output entity.</param>
    /// <returns>The role, or <see cref="OperationRole.Unknown"/> if the shim assigned none.</returns>
    private static OperationRole RoleOf(ulong history, ulong tag)
    {
        Native.Check(
            OcctBindings.HistoryRoleOf(history, tag, out int role),
            nameof(OcctBindings.HistoryRoleOf));

        // Cast rather than parse: the shim's enum is generated from OperationRole, so an unknown
        // value here means the two have drifted, and the arch test in P1-T16 exists to catch that.
        return (OperationRole)role;
    }

    /// <summary>Reads a shape's entities of one kind, in the shim's canonical order.</summary>
    /// <param name="shape">The shape.</param>
    /// <param name="kind">The kind to enumerate.</param>
    /// <returns>The entities.</returns>
    internal static ImmutableArray<SubEntity> Enumerate(KernelShape shape, SubEntityKind kind)
    {
        int nativeKind = (int)kind;

        ulong[] tags = Native.Read<ulong>(
            (Span<ulong> buffer, int capacity, out int required)
                => OcctBindings.Enumerate(shape.Tag, nativeKind, buffer, capacity, out required),
            nameof(OcctBindings.Enumerate));

        ImmutableArray<SubEntity>.Builder entities =
            ImmutableArray.CreateBuilder<SubEntity>(tags.Length);

        foreach (ulong tag in tags)
        {
            entities.Add(new SubEntity(shape, tag, kind));
        }

        return entities.MoveToImmutable();
    }
}
