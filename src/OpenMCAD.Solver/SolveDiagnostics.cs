using System.Collections.Immutable;

using OpenMCAD.Solver.Sketching;

namespace OpenMCAD.Solver;

/// <summary>
/// What a solver found out about one group, in the terms every solver can express.
/// </summary>
/// <param name="Residual">How far from satisfied the constraints ended up.</param>
/// <param name="Rank">
/// How many of the equations were independent. The one number that cannot be inferred from the
/// others, and the one that separates redundancy from contradiction.
/// </param>
/// <param name="Equations">How many equations the driving constraints produced.</param>
/// <param name="FreeParameters">How many numbers the solver was allowed to move.</param>
/// <param name="Dependent">
/// The constraints whose equations added nothing to the rank, in the order the user made them.
/// </param>
/// <param name="Free">The entities that can still move.</param>
/// <param name="Tolerance">How small the residual had to get to count as solved.</param>
/// <param name="Converged">
/// Whether the solver ran to completion rather than stopping on an iteration limit, a time budget
/// or a cancellation. A solver that stopped early has not shown the sketch is unsatisfiable.
/// </param>
/// <remarks>
/// P4-T06. Deliberately not "planegcs's output" or "the fake's output" but the intersection: a
/// residual, a rank, two counts and two lists. Every solver worth using can produce all six —
/// planegcs reports dependent and conflicting constraint groups directly, and a least-squares
/// implementation gets them from eliminating its own Jacobian.
/// </remarks>
public readonly record struct SolveEvidence(
    double Residual,
    int Rank,
    int Equations,
    int FreeParameters,
    ImmutableArray<SketchConstraintId> Dependent,
    ImmutableArray<SketchEntityId> Free,
    double Tolerance,
    bool Converged = true)
{
    /// <summary>Gets the dependent constraints, never a default array.</summary>
    public ImmutableArray<SketchConstraintId> Implied => Dependent.IsDefault ? [] : Dependent;

    /// <summary>Gets the entities that can move, never a default array.</summary>
    public ImmutableArray<SketchEntityId> Movable => Free.IsDefault ? [] : Free;
}

/// <summary>
/// Turns what a solver found into what a user is told.
/// </summary>
/// <remarks>
/// <para>
/// P4-T06, and §5.6's five cases. Shared rather than written inside each solver, because the
/// classification is a statement about sketches and not about numerical methods: two solvers that
/// each decided for themselves when a sketch was "redundant" rather than "over-constrained" would
/// give a user two different diagnoses of one drawing, and only one of them could be right.
/// </para>
/// <para>
/// It is also the only part of the solver stack that can be tested without solving anything, which
/// matters: the classification rules are where the subtlety is, and reaching a particular rank and
/// residual through an actual solve is a slow and indirect way to check one.
/// </para>
/// </remarks>
public static class SolveDiagnostics
{
    /// <summary>The smallest residual anyone should call "not solved".</summary>
    /// <remarks>
    /// A floor under whatever tolerance was asked for. A drag asks for 1e-8 and a careful solve for
    /// 1e-10, and a sketch that reached 1e-9 is solved in both cases — a classification that said
    /// otherwise would report a perfectly good sketch as failed for having been solved slightly
    /// less hard than it might have been.
    /// </remarks>
    public const double SolvedWithin = 1e-7;

    /// <summary>Says which of the five situations a group is in.</summary>
    /// <param name="evidence">What the solver found.</param>
    /// <returns>The diagnosis.</returns>
    /// <remarks>
    /// <para>
    /// The order of the questions is the whole design. Rank is asked first, because a rank short of
    /// the equation count means some constraint is implied by the others and that is true whether
    /// or not the sketch happens to be satisfied. Only then does the residual decide which kind of
    /// implied it is: a duplicate that agrees is redundant, and one that disagrees is a
    /// contradiction.
    /// </para>
    /// <para>
    /// Counting equations against unknowns instead would call a sketch with two identical
    /// constraints fully determined, and would have nothing to say about which constraints were at
    /// fault — which §5.6 is blunt about being the only part a user can act on.
    /// </para>
    /// </remarks>
    public static SolveDiagnosis From(SolveEvidence evidence)
    {
        bool solved = evidence.Residual <= System.Math.Max(evidence.Tolerance, SolvedWithin);

        if (evidence.Equations == 0)
        {
            return evidence.FreeParameters > 0
                ? Loose(evidence.FreeParameters, evidence.Movable)
                : new SolveDiagnosis(
                    SolveOutcome.WellConstrained, Message: "Nothing can move.");
        }

        if (evidence.Rank < evidence.Equations)
        {
            return solved
                ? new SolveDiagnosis(
                    SolveOutcome.Redundant,
                    System.Math.Max(0, evidence.FreeParameters - evidence.Rank),
                    evidence.Movable,
                    Redundant: evidence.Implied,
                    Message: evidence.Implied.Length == 1
                        ? "One constraint says what the others already said."
                        : $"{evidence.Implied.Length} constraints say what the others already said.")
                : new SolveDiagnosis(
                    SolveOutcome.OverConstrained,
                    Conflicting: evidence.Implied,
                    Message: "These constraints contradict each other, so no arrangement of the "
                        + "geometry satisfies them all.");
        }

        if (!solved)
        {
            // Independent equations that are not satisfied. Nothing here is contradictory, so
            // either the numbers did not settle or the solver was stopped before they could --
            // and telling the user to move the geometry is only good advice in the first case.
            return new SolveDiagnosis(
                SolveOutcome.Failed,
                Message: evidence.Converged
                    ? "The constraints do not contradict each other and the solve did not "
                        + "converge. Moving the geometry nearer to where it should be usually helps."
                    : "The solve was stopped before it finished, so this is what it had reached "
                        + "rather than what it would have found.");
        }

        return evidence.FreeParameters > evidence.Rank
            ? Loose(evidence.FreeParameters - evidence.Rank, evidence.Movable)
            : new SolveDiagnosis(SolveOutcome.WellConstrained, Message: "Fully defined.");
    }

    /// <summary>Rolls several groups' verdicts into one for the sketch.</summary>
    /// <param name="verdicts">What each group came to.</param>
    /// <returns>The verdict for the sketch.</returns>
    /// <remarks>
    /// <para>
    /// The worst outcome wins. A sketch with one contradicting group is a contradicting sketch
    /// however well the others solved, because the user cannot proceed — and reporting the best of
    /// them would say "fully defined" about a sketch that is not.
    /// </para>
    /// <para>
    /// Freedom and the free-entity list are carried only when the answer is that the sketch is
    /// loose. "Conflicting, four degrees of freedom left" is two answers to two different questions
    /// presented as one, and the fields' own contract says which question they answer.
    /// </para>
    /// </remarks>
    public static SolveDiagnosis Combine(IEnumerable<SolveDiagnosis> verdicts)
    {
        ArgumentNullException.ThrowIfNull(verdicts);

        ImmutableArray<SolveDiagnosis> all = [.. verdicts];

        if (all.IsEmpty)
        {
            return new SolveDiagnosis(SolveOutcome.WellConstrained, Message: "Nothing can move.");
        }

        if (all.Length == 1)
        {
            return all[0];
        }

        SolveOutcome worst = all.Select(v => v.Outcome).OrderByDescending(Severity).First();
        bool loose = worst == SolveOutcome.UnderConstrained;

        return new SolveDiagnosis(
            worst,
            loose ? all.Sum(v => v.RemainingFreedom) : 0,
            loose ? [.. all.SelectMany(v => v.Free).Distinct()] : [],
            [.. all.SelectMany(v => v.Conflicts).Distinct()],
            [.. all.SelectMany(v => v.Surplus).Distinct()],
            all.First(v => v.Outcome == worst).Message);
    }

    /// <summary>How badly one outcome stops the user, for choosing between several.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <returns>How severe it is; larger is worse.</returns>
    /// <remarks>
    /// A contradiction outranks a failure to converge because it is a statement about the sketch
    /// rather than about the attempt, and both outrank redundancy, which the user can ignore.
    /// Under-constrained is barely a complaint at all: a sketch being drawn is under-constrained
    /// almost all the time.
    /// </remarks>
    public static int Severity(SolveOutcome outcome) => outcome switch
    {
        SolveOutcome.OverConstrained => 4,
        SolveOutcome.Failed => 3,
        SolveOutcome.Redundant => 2,
        SolveOutcome.UnderConstrained => 1,
        _ => 0,
    };

    private static SolveDiagnosis Loose(int remaining, ImmutableArray<SketchEntityId> free) => new(
        SolveOutcome.UnderConstrained,
        remaining,
        free,
        Message: remaining == 1
            ? "One degree of freedom is left."
            : $"{remaining} degrees of freedom are left.");
}
