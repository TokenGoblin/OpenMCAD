using OpenMCAD.Core.Documents;
using OpenMCAD.Kernel;

namespace OpenMCAD.Core.Naming;

/// <summary>
/// Resolves a reference through all three tiers of §5.3, and fails loudly when none of them can.
/// </summary>
/// <remarks>
/// <para>
/// History first, because it is a record of what actually happened. Geometry second, because it is
/// a resemblance argument and can be wrong. Nothing third — no fallback, no nearest match, no
/// "probably this one". §5.3 is unambiguous: a wrong-but-plausible resolution silently corrupts
/// downstream design intent, while an error stops and asks, so the third tier is a refusal with
/// enough detail attached to be repaired.
/// </para>
/// <para>
/// <b>Not every failure goes on to tier two.</b> An entity history reports as deleted is a settled
/// question, and searching for something that resembles it would be looking for a replacement
/// nobody asked for — the face that best resembles a deleted one is a different face, and adopting
/// it is precisely the silent corruption being avoided. Ambiguity and a broken chain do go on,
/// because there the question is open rather than answered.
/// </para>
/// </remarks>
public sealed class NameResolver
{
    private readonly HistoryNameResolver _history;
    private readonly GeometricNameResolver? _geometry;
    private readonly Func<FeatureId, IEnumerable<SubEntity>>? _searchPool;

    /// <summary>Creates a resolver.</summary>
    /// <param name="history">What each feature in the rebuild did.</param>
    /// <param name="hintOf">
    /// How to measure a candidate as things now stand, or null to run tier one alone. Without it
    /// an ambiguous or broken reference fails immediately rather than being scored, which is a
    /// legitimate configuration: a batch tool that will not be repairing anything gains nothing
    /// from a tier that can only ever produce a maybe.
    /// </param>
    /// <param name="searchPool">
    /// What to search when history is broken altogether, given the consuming feature. Null means
    /// tier two only ever arbitrates a shortlist history produced, and never goes looking.
    /// </param>
    /// <param name="settings">How much each kind of geometric evidence counts.</param>
    /// <param name="sketchEntities">How to find the kernel entity for a sketch entity.</param>
    public NameResolver(
        RebuildHistory history,
        Func<SubEntity, GeoHint?>? hintOf = null,
        Func<FeatureId, IEnumerable<SubEntity>>? searchPool = null,
        GeometricMatchSettings? settings = null,
        Func<NameSource.Sketch, SubEntity>? sketchEntities = null)
    {
        ArgumentNullException.ThrowIfNull(history);

        _history = new HistoryNameResolver(history, sketchEntities);
        _geometry = hintOf is null ? null : new GeometricNameResolver(hintOf, settings);
        _searchPool = searchPool;
    }

    /// <summary>Finds what a reference points at, or says why it cannot.</summary>
    /// <param name="name">The reference.</param>
    /// <param name="consumer">The feature that wants to use it.</param>
    /// <returns>
    /// The resolution. Anything other than <see cref="NameResolutionOutcome.Resolved"/> is a
    /// refusal, and <see cref="Repair"/> turns it into something the user can act on.
    /// </returns>
    public NameResolution Resolve(PersistentName name, FeatureId consumer)
    {
        ArgumentNullException.ThrowIfNull(name);

        NameResolution byHistory = _history.Resolve(name, consumer);

        if (byHistory.IsResolved || _geometry is null)
        {
            return byHistory;
        }

        switch (byHistory.Outcome)
        {
            case NameResolutionOutcome.Ambiguous:
                // History narrowed it and could not choose. Geometry arbitrates that shortlist and
                // nothing else: the answer is one of these, so widening the search could only
                // introduce candidates history has already ruled out.
                return Prefer(_geometry.Resolve(name, byHistory.Candidates), byHistory);

            case NameResolutionOutcome.NotFound when _searchPool is not null:
                // The chain is broken -- a feature was reordered or deleted -- so there is no
                // shortlist and the whole model has to be searched.
                return Prefer(_geometry.Resolve(name, _searchPool(consumer)), byHistory);

            default:
                // Deleted, unsupported, or nothing to search. All settled questions.
                return byHistory;
        }
    }

    /// <summary>Turns a refusal into something the user can act on.</summary>
    /// <param name="feature">The feature holding the reference.</param>
    /// <param name="name">The reference.</param>
    /// <param name="resolution">What resolution came to.</param>
    /// <param name="nameOf">How to turn a feature id into the name the user gave it.</param>
    /// <returns>The repair, or null if the reference resolved and there is nothing to repair.</returns>
    public static ReferenceRepair? Repair(
        FeatureId feature,
        PersistentName name,
        NameResolution resolution,
        Func<FeatureId, string?> nameOf)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        return resolution.IsResolved
            ? null
            : ReferenceRepair.For(feature, name, resolution, nameOf);
    }

    /// <summary>Keeps whichever answer is more useful to the user.</summary>
    /// <remarks>
    /// Tier two's verdict wins when it resolved. When it did not, its reason is usually the better
    /// one to show — it has scores and knows how close the field was — but its candidate list is
    /// worth keeping from whichever tier had one, so that a repair can still offer choices.
    /// </remarks>
    private static NameResolution Prefer(NameResolution byGeometry, NameResolution byHistory)
        => byGeometry.IsResolved || !byGeometry.Scores.IsEmpty
            ? byGeometry
            : byGeometry with { Candidates = byHistory.Candidates };
}
