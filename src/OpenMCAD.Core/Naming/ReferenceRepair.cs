using System.Collections.Immutable;

using OpenMCAD.Core.Documents;

namespace OpenMCAD.Core.Naming;

/// <summary>
/// What the user has to be told, and offered, when a reference cannot be resolved.
/// </summary>
/// <param name="Feature">The feature holding the broken reference.</param>
/// <param name="Reference">The reference itself, for the UI to re-point once they choose.</param>
/// <param name="Outcome">Why resolution failed, for a caller that wants to branch on it.</param>
/// <param name="Problem">
/// What went wrong, in the user's terms. Names the feature and says what happened to the thing it
/// pointed at — never what happened inside the resolver.
/// </param>
/// <param name="Action">
/// What they can do about it, as an instruction. §5.3's example is "Reselect the missing edge for
/// Fillet2", and the shape of that sentence is the specification: a verb, the thing, the feature.
/// </param>
/// <param name="Suggestions">
/// What was considered and how well each fitted, best first. Empty when nothing plausible was
/// found. This is what lets the repair offer "did you mean this one?" rather than sending the user
/// to hunt through the model.
/// </param>
/// <remarks>
/// <para>
/// The contract between naming and the repair UI, which lands in Phase 6. It exists now, and is
/// produced now, because the alternative is that tier three throws away everything it knew about
/// the failure and the UI has to rediscover it months later — by which time the information is
/// gone. A resolver that knows a face split into two, and which two, and how closely each matched,
/// should hand that on rather than reduce it to a boolean.
/// </para>
/// <para>
/// <b>Every field is for the user rather than the log.</b> A message that says "tier two scored
/// 0.58 against a confidence threshold of 0.60" describes the program; one that says "the face
/// Fillet2 was built on is no longer there" describes their model. Only the second is actionable,
/// and being actionable is the whole reason §5.3 prefers an error to a guess.
/// </para>
/// </remarks>
public sealed record ReferenceRepair(
    FeatureId Feature,
    PersistentName Reference,
    NameResolutionOutcome Outcome,
    string Problem,
    string Action,
    ImmutableArray<ScoredEntity> Suggestions)
{
    /// <summary>Gets whether there is anything sensible to offer the user as a replacement.</summary>
    public bool HasSuggestions => !Suggestions.IsDefaultOrEmpty;

    /// <summary>Builds the repair for a failed resolution.</summary>
    /// <param name="feature">The feature holding the reference.</param>
    /// <param name="reference">The reference.</param>
    /// <param name="resolution">What resolution came to.</param>
    /// <param name="nameOf">How to turn a feature id into the name the user gave it.</param>
    /// <returns>The repair.</returns>
    /// <exception cref="ArgumentException">The resolution succeeded, so there is nothing to repair.</exception>
    public static ReferenceRepair For(
        FeatureId feature,
        PersistentName reference,
        NameResolution resolution,
        Func<FeatureId, string?> nameOf)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(nameOf);

        if (resolution.IsResolved)
        {
            throw new ArgumentException(
                "This reference resolved, so there is nothing to repair. Building a repair for a "
                + "success would put a question to the user about something that is not wrong.",
                nameof(resolution));
        }

        string name = nameOf(feature) ?? feature.ToString();
        string noun = NounFor(reference);

        (string problem, string action) = resolution.Outcome switch
        {
            NameResolutionOutcome.Deleted =>
                ($"The {noun} that {name} was built on has been removed by a later feature.",
                 $"Choose another {noun} for {name}, or delete {name}."),

            NameResolutionOutcome.Ambiguous =>
                ($"The {noun} that {name} was built on has become "
                    + $"{System.Math.Max(2, resolution.Candidates.Length)} separate ones, and "
                    + "which of them was meant is not recorded.",
                 $"Choose which {noun} {name} should use."),

            NameResolutionOutcome.Unsupported =>
                ($"{name} refers to something this build cannot look up.",
                 $"Reselect the {noun} for {name}."),

            _ => ($"The {noun} that {name} was built on can no longer be found.",
                  $"Reselect the missing {noun} for {name}."),
        };

        return new ReferenceRepair(
            feature, reference, resolution.Outcome, problem, action, resolution.Scores);
    }

    /// <summary>What to call the thing, so the message reads like the model rather than the code.</summary>
    /// <remarks>
    /// Taken from the recorded geometry when there is any, because "reselect the missing edge" is
    /// a sentence a user can act on and "reselect the missing entity" is not. Falls back to the
    /// general word rather than guessing: being vague is a smaller failure than being wrong about
    /// what the user is looking for.
    /// </remarks>
    private static string NounFor(PersistentName reference) => reference.Head.Hint?.Kind switch
    {
        GeometryKind.Plane or GeometryKind.Cylinder or GeometryKind.Cone or GeometryKind.Sphere
            or GeometryKind.Torus or GeometryKind.FreeformSurface => "face",

        GeometryKind.Line or GeometryKind.Circle or GeometryKind.Ellipse
            or GeometryKind.FreeformCurve => "edge",

        GeometryKind.Point => "vertex",

        _ => "entity",
    };

    /// <inheritdoc />
    public override string ToString() => $"{Problem} {Action}";
}
