using System.Collections.Immutable;

using OpenMCAD.Kernel;

namespace OpenMCAD.Core.Naming;

/// <summary>How a reference turned out.</summary>
public enum NameResolutionOutcome
{
    /// <summary>Exactly one entity answers to the name.</summary>
    Resolved,

    /// <summary>
    /// Several entities answer to it, and nothing in the name says which was meant.
    /// </summary>
    /// <remarks>
    /// Usually a split: the face that was referred to is now two faces. This is not a failure of
    /// tier one — it is tier one reporting truthfully that history has more than one answer, which
    /// is what tier two exists to arbitrate (P3-T10) and what a multiplicity policy exists to
    /// declare in advance (P3-T12).
    /// </remarks>
    Ambiguous,

    /// <summary>The entity existed and an operation removed it.</summary>
    /// <remarks>
    /// Told apart from <see cref="NotFound"/> because the two lead somewhere different. A deleted
    /// entity is a definite answer — history says it is gone — and no amount of geometric searching
    /// should conjure a replacement. Something merely not found may still be found by tier two.
    /// </remarks>
    Deleted,

    /// <summary>Nothing in the recorded history answers to the name.</summary>
    NotFound,

    /// <summary>The name depends on something this build cannot resolve yet.</summary>
    /// <remarks>
    /// A sketch entity, until the sketch layer exists (Phase 4). Kept apart from
    /// <see cref="NotFound"/> so that "we cannot answer this" is never mistaken for "the answer is
    /// no" — the first is a gap in the program and the second is a fact about the model.
    /// </remarks>
    Unsupported,
}

/// <summary>
/// What resolving a reference came to, from whichever tier answered.
/// </summary>
/// <param name="Outcome">How it turned out.</param>
/// <param name="Entity">The entity, when exactly one was found.</param>
/// <param name="Candidates">
/// Everything that answered to the name. One entry when resolved, several when ambiguous, none
/// otherwise — so that tier two has the shortlist rather than having to search from nothing.
/// </param>
/// <param name="Reason">
/// Why, in words, for the states where a person needs telling. This is what reaches the user
/// through P3-T11, so it says what happened to their model rather than what happened in the code.
/// </param>
/// <param name="Ranking">
/// How well each candidate fitted, best first, when geometry was the one doing the deciding.
/// Empty from tier one, which does not score anything — it either knows or it does not.
/// </param>
public sealed record NameResolution(
    NameResolutionOutcome Outcome,
    SubEntity Entity,
    ImmutableArray<SubEntity> Candidates,
    string? Reason = null,
    ImmutableArray<ScoredEntity> Ranking = default)
{
    /// <summary>Gets whether exactly one entity was found.</summary>
    public bool IsResolved => Outcome == NameResolutionOutcome.Resolved;

    /// <summary>Gets the scores, never a default array.</summary>
    public ImmutableArray<ScoredEntity> Scores
        => Ranking.IsDefault ? [] : Ranking;

    /// <summary>A resolution that found one entity.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>The resolution.</returns>
    public static NameResolution Found(SubEntity entity)
        => new(NameResolutionOutcome.Resolved, entity, [entity]);

    /// <summary>A resolution that found several.</summary>
    /// <param name="candidates">The entities.</param>
    /// <param name="reason">What happened.</param>
    /// <returns>The resolution.</returns>
    public static NameResolution Ambiguous(
        ImmutableArray<SubEntity> candidates, string reason)
        => new(NameResolutionOutcome.Ambiguous, SubEntity.None, candidates, reason);

    /// <summary>A resolution that found nothing.</summary>
    /// <param name="reason">What happened.</param>
    /// <returns>The resolution.</returns>
    public static NameResolution NotFound(string reason)
        => new(NameResolutionOutcome.NotFound, SubEntity.None, [], reason);

    /// <summary>A resolution that found the entity gone.</summary>
    /// <param name="reason">What happened.</param>
    /// <returns>The resolution.</returns>
    public static NameResolution Deleted(string reason)
        => new(NameResolutionOutcome.Deleted, SubEntity.None, [], reason);

    /// <summary>A resolution this build cannot make.</summary>
    /// <param name="reason">What is missing.</param>
    /// <returns>The resolution.</returns>
    public static NameResolution Unsupported(string reason)
        => new(NameResolutionOutcome.Unsupported, SubEntity.None, [], reason);

    /// <inheritdoc />
    public override string ToString()
        => Reason is null ? $"{Outcome} {Entity}" : $"{Outcome}: {Reason}";
}
