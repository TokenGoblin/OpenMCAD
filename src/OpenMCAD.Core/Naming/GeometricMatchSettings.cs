namespace OpenMCAD.Core.Naming;

/// <summary>
/// How much each kind of geometric evidence counts, and how sure a match has to be.
/// </summary>
/// <param name="Centroid">
/// How much it counts that the candidate is in the right place. The strongest single signal for
/// the case this exists for — a face that split into two, where both halves are the same kind of
/// surface, face the same way, and differ mainly in where they are.
/// </param>
/// <param name="Direction">How much it counts that it faces the right way.</param>
/// <param name="Measure">How much it counts that it is about the right size.</param>
/// <param name="Adjacency">
/// How much it counts that it touches about the same number of things. Weakest of the four, and
/// worth having anyway: it is the only one that says anything about shape rather than placement,
/// and it survives edits that move everything.
/// </param>
/// <param name="Confidence">
/// How good the best match has to be before it is accepted at all, from nought to one.
/// </param>
/// <param name="Margin">
/// How far ahead of the runner-up the best match has to be.
/// </param>
/// <remarks>
/// <para>
/// <b>The two thresholds do different jobs and both are necessary.</b> Confidence rejects a match
/// that is poor in absolute terms — the entity is simply not there any more, and the least bad of
/// a bad field is not it. The margin rejects a match that is good but not distinctive: two
/// candidates that both fit well means the evidence does not say which, and picking the one that
/// happens to score a thousandth higher is a coin toss dressed as an answer.
/// </para>
/// <para>
/// <b>The defaults are deliberately cautious.</b> §5.3 is unambiguous that a wrong-but-plausible
/// resolution is worse than an error, because it silently corrupts downstream design intent while
/// an error stops and asks. So these are set to refuse in the doubtful cases and let P3-T11 put the
/// question to the user, rather than to maximise the number of references that resolve
/// automatically.
/// </para>
/// </remarks>
public sealed record GeometricMatchSettings(
    double Centroid = 0.45,
    double Direction = 0.25,
    double Measure = 0.20,
    double Adjacency = 0.10,
    double Confidence = 0.60,
    double Margin = 0.15)
{
    /// <summary>Gets the settings used when the caller names none.</summary>
    public static GeometricMatchSettings Default { get; } = new();

    /// <summary>Gets the sum of the weights, for normalising a partial score.</summary>
    internal double TotalWeight => Centroid + Direction + Measure + Adjacency;
}

/// <summary>One candidate and how well it fits.</summary>
/// <param name="Entity">The candidate.</param>
/// <param name="Score">How well it fits, from nought to one.</param>
/// <remarks>
/// Kept and reported even for the candidates that lost. When a reference cannot be resolved the
/// user has to be told something they can act on, and "the closest match scored 0.58, and the next
/// scored 0.55" is a far better start than "could not resolve" — it says the answer was nearly
/// there and nearly ambiguous, which points at what to look for.
/// </remarks>
public readonly record struct ScoredEntity(Kernel.SubEntity Entity, double Score);
