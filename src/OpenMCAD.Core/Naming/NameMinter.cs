using System.Collections.Immutable;

using OpenMCAD.Core.Documents;
using OpenMCAD.Kernel;

namespace OpenMCAD.Core.Naming;

/// <summary>
/// Writes a <see cref="PersistentName"/> for an entity the user picked (P3-T08..P3-T12, §5.3).
/// </summary>
/// <param name="history">What each feature of the last rebuild did.</param>
/// <remarks>
/// <para>
/// The other half of <see cref="HistoryNameResolver"/>, and the half that was missing. Resolution
/// has existed since P3-T09; nothing anywhere turned a picked <see cref="SubEntity"/> back into a
/// name, so the only ways to make one were <see cref="PersistentName.Of"/> by hand and the text
/// parser. That is why a selection could not survive a rebuild: a kernel tag is a handle into one
/// rebuild and means nothing in the next, so a selection had to be written down as something with
/// an identity of its own before it could be carried across.
/// </para>
/// <para>
/// <b>A name is minted against the candidate set that will resolve it, not against the whole
/// operation.</b> This is the one thing here that is easy to get subtly wrong. When a segment
/// carries sources, <see cref="HistoryNameResolver"/> narrows to what those sources produced before
/// it applies the role and the ordinal; when it carries none, it starts from every output the
/// operation reported. An ordinal counted over the wider set and then resolved against the narrower
/// one points at the wrong sibling — and it would do so silently, because both sets are internally
/// consistent. So the ordinal is counted over exactly the set the resolver will see.
/// </para>
/// <para>
/// <b>Ordinal zero is not "the first".</b> §5.3's ordinals count from one, and zero means "there
/// was only one of these when this was written". The resolver reads a zero facing several
/// candidates as a reference to something that has since become several, which is the split its
/// second tier exists to arbitrate. Minting a 1 where the answer was unique would throw that
/// distinction away, so a unique answer is recorded as zero.
/// </para>
/// <para>
/// <b>No geometric hint.</b> A <see cref="GeoHint"/> is what tier two matches on when replay fails,
/// and it describes geometry this class has none of — it holds the history of a rebuild, not the
/// shapes it produced. A minted name therefore has tier one and tier three but not tier two, and a
/// name that survives a rebuild here does so by replay alone. Filling the hint in wants the picking
/// code, which is holding the geometry at the moment the user clicks.
/// </para>
/// </remarks>
public sealed class NameMinter(RebuildHistory history)
{
    private readonly RebuildHistory _history = history
        ?? throw new ArgumentNullException(nameof(history));

    /// <summary>Writes a name for an entity that is in the model.</summary>
    /// <param name="entity">What was picked.</param>
    /// <param name="consumer">
    /// The feature that will hold the reference, which fixes how much of the history counts as
    /// having already happened. A feature that has not been evaluated is treated as being at the
    /// end of the queue, which is what a selection made in the current model wants.
    /// </param>
    /// <returns>
    /// A name that resolves to <paramref name="entity"/>, or <see langword="null"/> if the history
    /// does not account for it.
    /// </returns>
    /// <remarks>
    /// <para>
    /// An invalid entity needs no check of its own: it is in no operation's outputs, so the search
    /// below runs off the end and returns null like anything else the history does not hold.
    /// </para>
    /// <para>
    /// Null rather than an exception, and rather than a name that does not work. A picked entity
    /// the history cannot explain is an ordinary thing — geometry from a feature that failed, or
    /// from a build older than the current rebuild — and the caller's answer is to decline to store
    /// the reference. A name that cannot be resolved would be worse than none: it would be written
    /// into a document and fail later, at a distance from whatever caused it.
    /// </para>
    /// </remarks>
    public PersistentName? Mint(SubEntity entity, FeatureId consumer)
    {
        int stop = _history.PositionOf(consumer) is var end && end < 0
            ? _history.Order.Length
            : end;

        // Backwards, because an entity can be an output of several features -- retained or altered
        // by each in turn -- and where it is *now* is where the last of them left it. Naming it at
        // an earlier one would name something a later feature has already replaced.
        for (int i = stop - 1; i >= 0; --i)
        {
            FeatureId feature = _history.Order[i];
            HistoryMap map = _history.For(feature)!;

            if (map.Outputs.Contains(entity))
            {
                return Describe(entity, feature, map);
            }
        }

        return null;
    }

    /// <summary>Writes the segment for an entity known to be an output of one feature.</summary>
    private PersistentName? Describe(SubEntity entity, FeatureId feature, HistoryMap map)
    {
        EntityRole role = EntityRole.From(map.RoleOf(entity));
        SubEntity source = map.SourceOf(entity);

        ProvenanceKind provenance = ProvenanceKind.New;
        ImmutableArray<NameSource> sources = [];
        ImmutableArray<SubEntity> candidates = [.. map.Outputs];

        if (source.IsValid)
        {
            provenance = map.Generated(source).Contains(entity)
                ? ProvenanceKind.Generated
                : ProvenanceKind.Modified;

            // The source is an input to this feature, so it is an output of an earlier one: naming
            // it against this feature searches strictly further back, which is what makes the
            // recursion terminate. It is also exactly what the resolver does with the source it
            // finds here, which is what keeps the two halves saying the same thing.
            if (Mint(source, feature) is { } named)
            {
                sources = [new NameSource.Entity(named)];

                // Narrowed the way FromSources narrows: what the source turned into, back in the
                // operation's own order, because that is the order the ordinal will be read in.
                ImmutableArray<SubEntity> produced =
                    [.. map.Modified(source).Concat(map.Generated(source)).Distinct()];

                candidates = [.. map.Outputs.Where(produced.Contains)];
            }
        }

        ImmutableArray<SubEntity> matching =
            [.. candidates.Where(c => EntityRole.From(map.RoleOf(c)) == role)];

        int at = matching.IndexOf(entity);

        if (at < 0)
        {
            // Unreachable through HistoryMapBuilder, which will not record a source for an entity
            // it did not also record as that source's successor -- so an entity always appears
            // among the candidates its own name offers. Kept for a map that did not come from the
            // builder: the answer to "I cannot write a name that finds this" is no name, and
            // carrying on would mint one that silently points at a sibling.
            return null;
        }

        return PersistentName.Of(new NameSegment(
            feature,
            provenance,
            sources,
            role,
            matching.Length == 1 ? 0 : at + 1));
    }
}
