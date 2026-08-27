using System.Collections.Immutable;

using OpenMCAD.Kernel;

namespace OpenMCAD.Core.Naming;

/// <summary>
/// What a feature means when the thing it points at has become several things.
/// </summary>
/// <remarks>
/// <para>
/// §5.3: a named face that splits becomes a set, and this is where most naming bugs live. The
/// remedy it prescribes is to make the policy explicit when the reference is declared rather than
/// inferring it at resolution time — because the right answer depends on what the feature is for,
/// and nothing about the geometry says which.
/// </para>
/// <para>
/// The same split, three features, three correct answers. A fillet on one edge of a face that has
/// been divided wants the one edge it was put on. A shell removing a face wants every piece of it,
/// or the part comes out with a wall the user thought they had opened. A draft applied to the main
/// wall of a moulding wants the piece that is still the wall, not the sliver a later pocket cut off
/// it. Guessing between those is guessing at intent.
/// </para>
/// </remarks>
public enum MultiplicityPolicy
{
    /// <summary>
    /// The reference means one entity, and a split is a problem for the user to resolve.
    /// </summary>
    /// <remarks>
    /// The default, and deliberately the strictest. A feature that has not thought about splitting
    /// is a feature that will be wrong when one happens, and stopping to ask is the failure mode
    /// §5.3 prefers.
    /// </remarks>
    ExactlyOne,

    /// <summary>Every piece the original became.</summary>
    /// <remarks>
    /// What an operation acting on a region wants: shelling, removing a face, applying a finish.
    /// A split is not an ambiguity for these — it is simply a set with more members than last time.
    /// </remarks>
    AllDescendants,

    /// <summary>The biggest piece, by area for a face and by length for an edge.</summary>
    /// <remarks>
    /// For features whose intent is "the main one" — a draft on a wall that a later pocket has
    /// nicked a corner from. Crude and honest: it does not pretend to know which piece carries the
    /// design intent, only which is the largest, and it says so in its name.
    /// </remarks>
    LargestDescendant,
}

/// <summary>
/// A feature's pointer at a face, edge or vertex, and what it means if that becomes several.
/// </summary>
/// <param name="Name">Which entity.</param>
/// <param name="Multiplicity">What to do when it has become more than one.</param>
/// <remarks>
/// The unit a feature actually declares. §5.3 asks for the policy to be recorded per reference
/// rather than per feature, because one feature can hold references meaning different things: a
/// boolean cut wants exactly one tool body, and the faces it is aligned against may well be a
/// region.
/// </remarks>
public sealed record EntityReference(
    PersistentName Name,
    MultiplicityPolicy Multiplicity = MultiplicityPolicy.ExactlyOne)
{
    /// <summary>Gets whether this reference expects to yield a set rather than a single entity.</summary>
    public bool IsSet => Multiplicity == MultiplicityPolicy.AllDescendants;

    /// <inheritdoc />
    public override string ToString()
        => Multiplicity == MultiplicityPolicy.ExactlyOne
            ? Name.ToString()
            : $"{Name} [{Multiplicity}]";
}

/// <summary>
/// What a reference came to, once its multiplicity policy has been applied.
/// </summary>
/// <param name="Outcome">How it turned out.</param>
/// <param name="Entities">
/// What it points at now. One entity for most references; several when the policy asked for every
/// descendant; none when it could not be resolved.
/// </param>
/// <param name="Reason">Why, in words, when it could not be resolved.</param>
/// <param name="Ranking">How well each candidate fitted, when geometry did the deciding.</param>
/// <remarks>
/// Separate from <see cref="NameResolution"/>, which answers about a name and yields at most one
/// entity. A reference is a name plus an intention, and under
/// <see cref="MultiplicityPolicy.AllDescendants"/> the correct answer is genuinely a set — so
/// folding the two together would either lose that or force every caller to handle a collection
/// for the common case where there is exactly one.
/// </remarks>
public sealed record ResolvedReference(
    NameResolutionOutcome Outcome,
    ImmutableArray<SubEntity> Entities,
    string? Reason = null,
    ImmutableArray<ScoredEntity> Ranking = default)
{
    /// <summary>Gets whether the reference points at something.</summary>
    public bool IsResolved => Outcome == NameResolutionOutcome.Resolved;

    /// <summary>Gets the one entity, for the common case of a reference to a single thing.</summary>
    /// <exception cref="InvalidOperationException">
    /// It did not resolve, or it resolved to a set.
    /// </exception>
    public SubEntity OnlyEntity => Outcome == NameResolutionOutcome.Resolved && Entities.Length == 1
        ? Entities[0]
        : throw new InvalidOperationException(
            $"This reference resolved to {Entities.Length} entities, not one. A caller that can "
            + "only use a single entity should declare ExactlyOne, so that the disagreement is "
            + "reported against the reference rather than thrown here.");

    /// <summary>Gets the scores, never a default array.</summary>
    public ImmutableArray<ScoredEntity> Scores => Ranking.IsDefault ? [] : Ranking;
}
