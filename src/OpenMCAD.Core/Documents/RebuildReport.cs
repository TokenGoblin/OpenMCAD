using System.Collections.Immutable;

namespace OpenMCAD.Core.Documents;

/// <summary>How a feature stands after the last rebuild.</summary>
/// <remarks>
/// Eight states rather than "built" and "didn't", because the difference between them is the only
/// thing the user actually needs. Three are problems they have to solve, two are consequences of
/// those, and two are what they asked for. Telling them apart is the difference between a tree
/// that explains itself and twenty red marks with one cause.
/// </remarks>
public enum FeatureState
{
    /// <summary>Evaluated, and produced what it was asked for.</summary>
    Ok,

    /// <summary>Its own evaluation failed. This is the one to go and look at.</summary>
    Failed,

    /// <summary>
    /// Could not be attempted because something it consumes failed.
    /// </summary>
    /// <remarks>
    /// The <c>suppressed-by-error</c> state of §5.4. Not a problem in its own right: fixing the
    /// feature that failed fixes every one of these at once, and presenting them as equals sends
    /// the user to look at whichever happens to be at the top of the list.
    /// </remarks>
    SuppressedByError,

    /// <summary>The user switched it off.</summary>
    Suppressed,

    /// <summary>It is behind the rollback bar.</summary>
    RolledBack,

    /// <summary>
    /// It consumes something that is suppressed or rolled back, so there was nothing to build from.
    /// </summary>
    /// <remarks>
    /// Deliberately not an error. Nothing has gone wrong — the user asked for the thing this
    /// depends on to be absent, and this is the consequence they asked for.
    /// </remarks>
    Blocked,

    /// <summary>
    /// One of the entities it was built on could not be found in the rebuilt model.
    /// </summary>
    /// <remarks>
    /// Tier three of §5.3. Kept apart from <see cref="MissingInput"/> because the two break at
    /// different grain and are repaired differently: a missing input is a whole feature that is
    /// gone and the fix is to re-point or remove this one, whereas this is a particular face or
    /// edge that can no longer be identified and the fix is to reselect it. The
    /// <see cref="FeatureDiagnostic.Repair"/> attached says which, and offers what was considered.
    /// </remarks>
    UnresolvedReference,

    /// <summary>It declares an input that the document does not contain.</summary>
    /// <remarks>
    /// What P3-T03 reports as a dangling reference. Normally the result of deleting a feature that
    /// something else consumed, which is a reasonable thing to have done and leaves a reasonable
    /// question behind: this feature now has to be repointed or removed.
    /// </remarks>
    MissingInput,
}

/// <summary>What happened to one feature.</summary>
/// <param name="Feature">Which feature.</param>
/// <param name="State">How it stands.</param>
/// <param name="Message">
/// What to tell the user, for the states where there is anything to say. Null when the state says
/// it all.
/// </param>
/// <param name="Cause">
/// For <see cref="FeatureState.SuppressedByError"/> and <see cref="FeatureState.Blocked"/>, the
/// feature responsible. This is what lets the tree say "fix this one" rather than marking a dozen
/// features and leaving the user to work out which is the cause and which are the symptoms.
/// </param>
/// <param name="Repair">
/// For <see cref="FeatureState.UnresolvedReference"/>, what the user has to do about it and what
/// they might do it with. Carried rather than rebuilt on demand because the information exists
/// only at the moment resolution fails: which candidates were considered, and how closely each
/// one fitted, cannot be recovered afterwards.
/// </param>
public sealed record FeatureDiagnostic(
    FeatureId Feature,
    FeatureState State,
    string? Message = null,
    FeatureId Cause = default,
    Naming.ReferenceRepair? Repair = null)
{
    /// <summary>Gets whether this represents something the user has to fix.</summary>
    public bool IsError => State
        is FeatureState.Failed
        or FeatureState.MissingInput
        or FeatureState.UnresolvedReference;

    /// <inheritdoc />
    public override string ToString()
        => Message is null ? $"{Feature}: {State}" : $"{Feature}: {State} — {Message}";
}

/// <summary>
/// How every feature stands after the last rebuild.
/// </summary>
/// <remarks>
/// <para>
/// <b>Carried by the document rather than returned from the rebuild.</b> A rebuild result describes
/// one rebuild and is gone once its caller has read it; the tree has to keep showing which features
/// are in error until something changes. Holding it on the document also means undo restores the
/// report that belongs to the state it restored, for free — the alternative is a tree still marked
/// with errors from a version of the model that no longer exists.
/// </para>
/// <para>
/// <b>A report, not a dialog.</b> §5.4 is explicit that a failed feature does not abort the
/// rebuild: it is marked, its dependents are marked as consequences, independent branches carry on,
/// and the user is shown what happened rather than being interrupted by it.
/// </para>
/// </remarks>
public sealed class RebuildReport
{
    private readonly ImmutableDictionary<FeatureId, FeatureDiagnostic> _byFeature;

    private RebuildReport(ImmutableDictionary<FeatureId, FeatureDiagnostic> byFeature)
        => _byFeature = byFeature;

    /// <summary>Gets a report saying nothing about anything.</summary>
    public static RebuildReport Empty { get; } =
        new(ImmutableDictionary<FeatureId, FeatureDiagnostic>.Empty);

    /// <summary>Gets every diagnostic, in no particular order.</summary>
    public IReadOnlyCollection<FeatureDiagnostic> Diagnostics => _byFeature.Values.ToImmutableArray();

    /// <summary>Gets the repairs the user is being offered, if any.</summary>
    /// <remarks>
    /// What a repair UI binds to (Phase 6). Separate from <see cref="Errors"/> because not every
    /// error is repairable this way — a feature whose operation simply failed has no reference to
    /// re-point — and a list that mixed the two would offer a "reselect" button for something with
    /// nothing to reselect.
    /// </remarks>
    public ImmutableArray<Naming.ReferenceRepair> Repairs
        => [.. _byFeature.Values.Select(d => d.Repair).OfType<Naming.ReferenceRepair>()];

    /// <summary>Gets the features the user has to do something about.</summary>
    /// <remarks>
    /// Failures, missing inputs and unresolved references only. The features that could not be
    /// attempted as a consequence of one of those are deliberately absent: they are the same
    /// problem counted again.
    /// </remarks>
    public ImmutableArray<FeatureDiagnostic> Errors
        => [.. _byFeature.Values.Where(d => d.IsError)];

    /// <summary>Gets whether anything needs attention.</summary>
    public bool HasErrors => _byFeature.Values.Any(d => d.IsError);

    /// <summary>Gets how many features this says anything about.</summary>
    public int Count => _byFeature.Count;

    /// <summary>Looks up how one feature stands.</summary>
    /// <param name="id">Which feature.</param>
    /// <returns>
    /// Its diagnostic, or null if the last rebuild had nothing to say about it — which for a
    /// feature that is present means it built.
    /// </returns>
    public FeatureDiagnostic? For(FeatureId id)
        => _byFeature.TryGetValue(id, out FeatureDiagnostic? found) ? found : null;

    /// <summary>Gets how one feature stands, treating silence as success.</summary>
    /// <param name="id">Which feature.</param>
    /// <returns>Its state.</returns>
    public FeatureState StateOf(FeatureId id) => For(id)?.State ?? FeatureState.Ok;

    /// <summary>Builds a report as a rebuild proceeds.</summary>
    public sealed class Builder
    {
        private readonly ImmutableDictionary<FeatureId, FeatureDiagnostic>.Builder _entries =
            ImmutableDictionary.CreateBuilder<FeatureId, FeatureDiagnostic>();

        /// <summary>Records how a feature stands.</summary>
        /// <param name="diagnostic">What happened to it.</param>
        public void Add(FeatureDiagnostic diagnostic)
        {
            ArgumentNullException.ThrowIfNull(diagnostic);

            _entries[diagnostic.Feature] = diagnostic;
        }

        /// <summary>Carries forward what was said about a feature this rebuild did not visit.</summary>
        /// <param name="previous">The report from before.</param>
        /// <param name="id">Which feature.</param>
        /// <remarks>
        /// A partial rebuild says nothing about the features outside its dirty subgraph, and
        /// "nothing" is not the same as "fine". Dropping their diagnostics would clear the error
        /// marks off features that are still broken, every time the user edited something else.
        /// </remarks>
        public void CarryForward(RebuildReport previous, FeatureId id)
        {
            ArgumentNullException.ThrowIfNull(previous);

            if (previous.For(id) is { } existing)
            {
                _entries[id] = existing;
            }
        }

        /// <summary>Produces the report.</summary>
        /// <returns>The report.</returns>
        public RebuildReport Build() => new(_entries.ToImmutable());
    }
}
