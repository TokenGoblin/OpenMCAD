using System.Collections.Immutable;

using OpenMCAD.Core.Documents;
using OpenMCAD.Kernel;

namespace OpenMCAD.Core.Naming;

/// <summary>
/// Finds the entity a <see cref="PersistentName"/> refers to by replaying what each operation did.
/// </summary>
/// <remarks>
/// <para>
/// Tier one of §5.3, and the authoritative one. A name records how an entity came about; the
/// kernel's <see cref="HistoryMap"/>s record what each operation actually did to what. Putting the
/// two together answers the question exactly rather than approximately, which is why this runs
/// first and why tier two only sees what this could not settle.
/// </para>
/// <para>
/// <b>Two walks, not one.</b> Resolving the name itself finds the entity as it stood when the
/// feature that produced it finished. Getting from there to now is a second walk: every operation
/// that ran in between may have modified it, split it, or deleted it, and the name says nothing
/// about those because they had not happened when it was written. §5.3 calls this walking the
/// chain forward, and it is the part that makes a reference survive features being inserted above
/// it.
/// </para>
/// <para>
/// <b>Ambiguity is an answer.</b> When history says a name matches two entities, this reports that
/// rather than choosing. A wrong-but-plausible resolution silently corrupts design intent and is
/// worse than an error (§5.3), and the shortlist is exactly what tier two needs to arbitrate.
/// </para>
/// </remarks>
public sealed class HistoryNameResolver
{
    private readonly RebuildHistory _history;
    private readonly Func<NameSource.Sketch, SubEntity>? _sketchEntities;

    /// <summary>Creates a resolver.</summary>
    /// <param name="history">What each feature in the rebuild did.</param>
    /// <param name="sketchEntities">
    /// How to find the kernel entity for a sketch entity, or null while there is no sketch layer to
    /// ask. A name that reaches a sketch source without this resolves as
    /// <see cref="NameResolutionOutcome.Unsupported"/> rather than as not found, because the
    /// difference between "cannot answer" and "the answer is no" matters.
    /// </param>
    public HistoryNameResolver(
        RebuildHistory history, Func<NameSource.Sketch, SubEntity>? sketchEntities = null)
    {
        ArgumentNullException.ThrowIfNull(history);

        _history = history;
        _sketchEntities = sketchEntities;
    }

    /// <summary>Finds what a name refers to, as things stand for a feature about to run.</summary>
    /// <param name="name">The reference.</param>
    /// <param name="consumer">
    /// The feature that wants to use it. Operations that ran at or after this one are not applied,
    /// because from the consumer's point of view they have not happened.
    /// </param>
    /// <returns>What was found.</returns>
    public NameResolution Resolve(PersistentName name, FeatureId consumer)
    {
        ArgumentNullException.ThrowIfNull(name);

        NameResolution current = ResolveSegment(name.Path[0], SubEntity.None);

        for (int i = 1; i < name.Path.Length && current.IsResolved; ++i)
        {
            current = ResolveSegment(name.Path[i], current.Entity);
        }

        return current.IsResolved
            ? CarryForward(current.Entity, name.Head.Feature, consumer)
            : current;
    }

    /// <summary>Works out what one step of a name refers to.</summary>
    /// <param name="segment">The step.</param>
    /// <param name="incoming">
    /// The entity the previous step resolved to, or none for the first step. When present it is
    /// what this feature acted upon, which makes it the only source that matters.
    /// </param>
    private NameResolution ResolveSegment(NameSegment segment, SubEntity incoming)
    {
        HistoryMap? map = _history.For(segment.Feature);

        if (map is null)
        {
            return NameResolution.NotFound(
                "This reference was made by a feature that has not been rebuilt, so there is no "
                + "record of what it produced.");
        }

        ImmutableArray<SubEntity> candidates;

        if (incoming.IsValid)
        {
            if (map.IsDeleted(incoming))
            {
                return NameResolution.Deleted("The entity this refers to was removed.");
            }

            candidates = Successors(map, incoming);
        }
        else if (!segment.Sources.IsEmpty)
        {
            NameResolution sources = FromSources(map, segment);

            if (sources.Outcome != NameResolutionOutcome.Resolved)
            {
                return sources;
            }

            candidates = sources.Candidates;
        }
        else
        {
            // Made from nothing that can be pointed at, so the only handles are the role and the
            // ordinal. Narrower than it sounds: an operation's outputs of any one role are few.
            candidates = [.. map.Outputs];
        }

        return Choose(candidates, map, segment);
    }

    /// <summary>Intersects what each of a segment's sources produced.</summary>
    /// <remarks>
    /// Intersection rather than union, and that is the point of recording several sources. §5.3's
    /// worked example names a fillet's blend face after the two faces whose edge it replaces:
    /// either face alone also produced the blends on every other edge it touches, and only the
    /// pair identifies this one.
    /// </remarks>
    private NameResolution FromSources(HistoryMap map, NameSegment segment)
    {
        HashSet<SubEntity>? shared = null;

        foreach (NameSource source in segment.Sources)
        {
            NameResolution resolved = ResolveSource(source, segment.Feature);

            if (!resolved.IsResolved)
            {
                return resolved;
            }

            if (map.IsDeleted(resolved.Entity))
            {
                return NameResolution.Deleted(
                    "Something this reference was built from was removed.");
            }

            ImmutableArray<SubEntity> produced = Successors(map, resolved.Entity);

            if (shared is null)
            {
                shared = [.. produced];
            }
            else
            {
                shared.IntersectWith(produced);
            }

            if (shared.Count == 0)
            {
                return NameResolution.NotFound(
                    "Nothing in the rebuilt model came from all the things this reference was "
                    + "built from.");
            }
        }

        // Back into the operation's own order. A HashSet has none, and picking by ordinal out of an
        // unordered set would resolve differently between runs -- which is the determinism failure
        // that is hardest to notice, because each run is individually self-consistent.
        return new NameResolution(
            NameResolutionOutcome.Resolved,
            SubEntity.None,
            [.. map.Outputs.Where(shared!.Contains)]);
    }

    private NameResolution ResolveSource(NameSource source, FeatureId consumer) => source switch
    {
        NameSource.Entity entity => Resolve(entity.Name, consumer),

        NameSource.Sketch sketch when _sketchEntities is not null
            => _sketchEntities(sketch) is { IsValid: true } found
                ? NameResolution.Found(found)
                : NameResolution.NotFound(
                    $"Sketch entity '{sketch.EntityId}' is no longer in that sketch."),

        NameSource.Sketch sketch => NameResolution.Unsupported(
            $"This reference depends on sketch entity '{sketch.EntityId}', and this build has no "
            + "way to look one up yet."),

        _ => NameResolution.Unsupported(
            $"There is no way to resolve a {source.GetType().Name} source."),
    };

    /// <summary>Narrows candidates by role, then by ordinal.</summary>
    private static NameResolution Choose(
        ImmutableArray<SubEntity> candidates, HistoryMap map, NameSegment segment)
    {
        ImmutableArray<SubEntity> matching =
            [.. candidates.Where(c => EntityRole.From(map.RoleOf(c)) == segment.Role)];

        if (matching.IsEmpty)
        {
            return NameResolution.NotFound(
                $"Nothing the rebuilt model produced plays the part of {segment.Role} here.");
        }

        if (matching.Length == 1)
        {
            // One answer, whatever the ordinal said. An ordinal disambiguates siblings, and there
            // are none to disambiguate.
            return NameResolution.Found(matching[0]);
        }

        // Ordinals count from one, and zero means "there was only one of these when this reference
        // was written". So a zero ordinal facing several candidates is not a reference to the first
        // of them -- it is a reference to something that has since become several, which is exactly
        // the split that tier two exists to arbitrate.
        if (segment.Ordinal >= 1 && segment.Ordinal <= matching.Length)
        {
            return NameResolution.Found(matching[segment.Ordinal - 1]);
        }

        return NameResolution.Ambiguous(
            matching,
            segment.Ordinal == 0
                ? $"What this refers to is now {matching.Length} separate entities, and the "
                    + "reference does not say which was meant."
                : $"This refers to entity {segment.Ordinal} of a set that now has "
                    + $"{matching.Length}.");
    }

    /// <summary>Carries an entity through every operation that ran after it was named.</summary>
    /// <remarks>
    /// The name says nothing about these features, because they had not run when it was written --
    /// and inserting a feature above an existing one is the commonest edit there is. An operation
    /// that did not touch the entity reports no successors and leaves it alone.
    /// </remarks>
    private NameResolution CarryForward(SubEntity entity, FeatureId from, FeatureId consumer)
    {
        int start = _history.PositionOf(from);
        int end = _history.PositionOf(consumer);

        if (start < 0)
        {
            return NameResolution.Found(entity);
        }

        // A consumer that has not run yet is at the end of the queue by definition: everything
        // recorded so far ran before it.
        int stop = end < 0 ? _history.Order.Length : end;

        for (int i = start + 1; i < stop; ++i)
        {
            HistoryMap map = _history.For(_history.Order[i])!;

            if (map.IsDeleted(entity))
            {
                return NameResolution.Deleted(
                    "A later feature removed the entity this refers to.");
            }

            ImmutableArray<SubEntity> successors = map.Modified(entity);

            if (successors.Length == 1)
            {
                entity = successors[0];
            }
            else if (successors.Length > 1)
            {
                return NameResolution.Ambiguous(
                    successors,
                    $"A later feature divided what this refers to into {successors.Length} "
                    + "separate entities.");
            }
        }

        return NameResolution.Found(entity);
    }

    /// <summary>
    /// What an input turned into: the things it became, and the things it brought into being.
    /// </summary>
    /// <remarks>
    /// Both together, because a segment's provenance says which of the two the author meant and
    /// the kernel is the one that decides which it recorded. Taking only the matching one would
    /// make a reference fail whenever a kernel classified an output as generated where the naming
    /// layer expected modified, which is a disagreement about vocabulary rather than about the
    /// model. The role filter that follows is what actually narrows this.
    /// </remarks>
    private static ImmutableArray<SubEntity> Successors(HistoryMap map, SubEntity input)
        => [.. map.Modified(input).Concat(map.Generated(input)).Distinct()];
}
