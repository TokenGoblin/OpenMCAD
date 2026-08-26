using System.Collections.Immutable;

namespace OpenMCAD.Core.Documents;

/// <summary>
/// What one committed transaction did.
/// </summary>
/// <param name="Name">
/// What the edit was called. This is what an undo entry is labelled with, so it should read as
/// something a person did — "Add extrude", "Change length" — rather than as what the code did.
/// </param>
/// <param name="Before">The document as it was.</param>
/// <param name="After">The document as it now is.</param>
/// <param name="TouchedFeatures">
/// The features this edit changed directly. These are the seeds of the dirty set, not the dirty set
/// itself: what else must be rebuilt because it depends on one of these is a question for the
/// dependency graph, and propagating through it is P3-T04.
/// </param>
/// <param name="TouchedParameters">
/// The parameters this edit changed directly. Separate from the features because a parameter change
/// reaches features through the expression graph rather than through the feature graph (P3-T16),
/// and collapsing the two would mean the rebuild engine could not tell which question to ask.
/// </param>
/// <remarks>
/// Carrying both documents rather than a description of the difference between them. They share
/// almost all of their structure, so holding both costs the spine that changed; and an undo that
/// restores a reference cannot fail, whereas one that replays an inverse description can be wrong
/// in ways that only show up much later.
/// </remarks>
public sealed record DocumentChange(
    string Name,
    Document Before,
    Document After,
    ImmutableArray<FeatureId> TouchedFeatures,
    ImmutableArray<string> TouchedParameters)
{
    /// <summary>Gets whether this change altered anything at all.</summary>
    public bool IsEmpty => ReferenceEquals(Before, After);

    /// <inheritdoc />
    public override string ToString()
        => $"'{Name}': v{Before.Version} -> v{After.Version}, "
            + $"{TouchedFeatures.Length} features, {TouchedParameters.Length} parameters";
}
