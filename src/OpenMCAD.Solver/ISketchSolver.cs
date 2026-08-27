using System.Collections.Immutable;

using OpenMCAD.Math;
using OpenMCAD.Solver.Sketching;

namespace OpenMCAD.Solver;

/// <summary>
/// Where the user is dragging, and what they have hold of.
/// </summary>
/// <param name="Point">Which point of which entity is being moved.</param>
/// <param name="To">Where the pointer is now.</param>
/// <remarks>
/// A drag is not the same problem as a solve. Any of the infinitely many configurations satisfying
/// the constraints would be a correct answer, and all but one of them look like the sketch jumping.
/// §5.6 asks for a minimal-motion objective: of the valid answers, the one nearest where things
/// already were. P4-T07 does that properly; the interface takes the target now so it can.
/// </remarks>
public sealed record DragTarget(SketchPointRef Point, Vec2d To);

/// <summary>
/// How hard to try.
/// </summary>
/// <param name="MaximumIterations">How many steps before giving up.</param>
/// <param name="Tolerance">How small the residual has to get to count as solved.</param>
/// <param name="TimeBudget">
/// How long the solve may take. §5.6 requires a 200-entity sketch to drag in under 16 ms, which
/// means a solve that is going badly has to be abandoned rather than finished.
/// </param>
/// <remarks>
/// A budget rather than a promise. A solver that ran to convergence however long it took would
/// make the drag experience depend on the hardest frame, and a dropped frame is worse than a solve
/// that stops one iteration early and says so.
/// </remarks>
public sealed record SolverOptions(
    int MaximumIterations = 100,
    double Tolerance = 1e-10,
    TimeSpan? TimeBudget = null)
{
    /// <summary>Gets the settings an ordinary solve uses.</summary>
    public static SolverOptions Default { get; } = new();

    /// <summary>Gets the settings a drag uses.</summary>
    public static SolverOptions ForDrag { get; } =
        new(MaximumIterations: 25, Tolerance: 1e-8, TimeBudget: TimeSpan.FromMilliseconds(12));
}

/// <summary>How a sketch stands after a solve.</summary>
/// <remarks>
/// §5.6: the diagnosis is as important as the solution. A user whose sketch will not solve needs to
/// know which of these four situations they are in, because the remedy differs completely: add a
/// constraint, delete one, delete a duplicate, or move the geometry closer to where it should be.
/// </remarks>
public enum SolveOutcome
{
    /// <summary>Exactly determined. There is one answer and it was found.</summary>
    WellConstrained,

    /// <summary>Some freedom is left. The sketch solves, and can still be moved.</summary>
    UnderConstrained,

    /// <summary>The constraints contradict each other. No configuration satisfies them all.</summary>
    OverConstrained,

    /// <summary>
    /// Some constraints say what others already said. The sketch solves and the extra ones are
    /// doing nothing.
    /// </summary>
    Redundant,

    /// <summary>The constraints are consistent and the numbers did not converge.</summary>
    Failed,
}

/// <summary>
/// What a solve found out, beyond the numbers.
/// </summary>
/// <param name="Outcome">Which of the four situations the sketch is in.</param>
/// <param name="RemainingFreedom">How many degrees of freedom are left, when it is under-constrained.</param>
/// <param name="FreeEntities">Which entities can still move.</param>
/// <param name="Conflicting">
/// The constraints that contradict each other, when it is over-constrained.
/// </param>
/// <param name="Redundant">The constraints that say nothing new, when it is redundant.</param>
/// <param name="Message">What to tell the user.</param>
/// <remarks>
/// The sets are the whole point. §5.6 is blunt about it: "over-constrained" without a list is
/// useless to a user, because the only thing they can do with it is delete constraints at random
/// until the message goes away.
/// </remarks>
public sealed record SolveDiagnosis(
    SolveOutcome Outcome,
    int RemainingFreedom = 0,
    ImmutableArray<SketchEntityId> FreeEntities = default,
    ImmutableArray<SketchConstraintId> Conflicting = default,
    ImmutableArray<SketchConstraintId> Redundant = default,
    string Message = "")
{
    /// <summary>Gets whether the sketch is in a state the user can work in.</summary>
    /// <remarks>
    /// Under-constrained counts. A sketch being drawn is under-constrained almost all the time, and
    /// a solver that treated that as a failure would be reporting an error against every sketch in
    /// progress.
    /// </remarks>
    public bool IsUsable => Outcome
        is SolveOutcome.WellConstrained
        or SolveOutcome.UnderConstrained
        or SolveOutcome.Redundant;

    /// <summary>Gets the entities that can still move, never a default array.</summary>
    public ImmutableArray<SketchEntityId> Free => FreeEntities.IsDefault ? [] : FreeEntities;

    /// <summary>Gets the contradicting constraints, never a default array.</summary>
    public ImmutableArray<SketchConstraintId> Conflicts
        => Conflicting.IsDefault ? [] : Conflicting;

    /// <summary>Gets the constraints that say nothing new, never a default array.</summary>
    public ImmutableArray<SketchConstraintId> Surplus => Redundant.IsDefault ? [] : Redundant;

    /// <inheritdoc/>
    public bool Equals(SolveDiagnosis? other)
        => other is not null
            && Outcome == other.Outcome
            && RemainingFreedom == other.RemainingFreedom
            && Message == other.Message
            && Free.SequenceEqual(other.Free)
            && Conflicts.SequenceEqual(other.Conflicts)
            && Surplus.SequenceEqual(other.Surplus);

    /// <inheritdoc/>
    public override int GetHashCode()
        => HashCode.Combine(Outcome, RemainingFreedom, Free.Length, Conflicts.Length, Surplus.Length);

    /// <inheritdoc/>
    public override string ToString()
        => Message.Length > 0 ? $"{Outcome}: {Message}" : Outcome.ToString();
}

/// <summary>
/// What a solve produced.
/// </summary>
/// <param name="Sketch">The sketch, with its geometry moved to satisfy the constraints.</param>
/// <param name="Diagnosis">What the solver found out about it.</param>
/// <param name="Iterations">How many steps it took.</param>
/// <param name="Residual">How far from satisfied the constraints ended up.</param>
/// <remarks>
/// A whole sketch comes back rather than a parameter vector. The vector is the solver's business;
/// a caller that had to scatter it back into entities would be doing the solver's bookkeeping, and
/// two callers doing it would eventually do it differently.
/// </remarks>
public sealed record SolveResult(
    Sketch Sketch,
    SolveDiagnosis Diagnosis,
    int Iterations = 0,
    double Residual = 0)
{
    /// <summary>Gets whether the sketch came back in a state the user can work in.</summary>
    public bool IsUsable => Diagnosis.IsUsable;

    /// <inheritdoc/>
    public override string ToString()
        => $"{Diagnosis} after {Iterations} iterations, residual {Residual:0.###e+0}";
}

/// <summary>
/// A constraint solver for one sketch.
/// </summary>
/// <remarks>
/// <para>
/// P4-T02, and §5.6's signature. The interface is deliberately narrow — geometry in, geometry and a
/// diagnosis out — because that narrowness is what makes ADR-0006's contingency real: replacing
/// planegcs with a managed rewrite or a commercial licence is a contained swap only while nothing
/// above here knows how the solve is done.
/// </para>
/// <para>
/// Nothing about parameter vectors appears here. planegcs wants one and gets one, built by
/// <see cref="Sketching.SketchParameters"/> at the boundary; a caller that had to flatten its own
/// sketch would be doing the solver's bookkeeping, and two callers would eventually do it
/// differently.
/// </para>
/// </remarks>
public interface ISketchSolver
{
    /// <summary>Gets what to call this solver, for logs and for a bug report.</summary>
    string Name { get; }

    /// <summary>Solves a sketch.</summary>
    /// <param name="sketch">The geometry and the constraints on it.</param>
    /// <param name="drag">
    /// Where the user is dragging, or null when this is not a drag. A drag asks for the answer
    /// nearest where things already were, rather than any answer.
    /// </param>
    /// <param name="options">How hard to try.</param>
    /// <param name="cancellationToken">Abandons the solve.</param>
    /// <returns>The moved sketch, and what was found out about it.</returns>
    /// <remarks>
    /// Never throws for a sketch it cannot solve. Not solving is a diagnosis, and the sketcher has
    /// to draw the result either way — a user mid-drag with a momentarily impossible sketch is the
    /// ordinary case, not an exceptional one.
    /// </remarks>
    SolveResult Solve(
        Sketch sketch,
        DragTarget? drag = null,
        SolverOptions? options = null,
        CancellationToken cancellationToken = default);
}
