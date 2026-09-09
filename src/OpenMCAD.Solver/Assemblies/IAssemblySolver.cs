using System.Collections.Immutable;

using OpenMCAD.Math;

namespace OpenMCAD.Solver.Assemblies;

/// <summary>What a mate solve found out about an assembly.</summary>
/// <remarks>
/// The same five situations <c>SolveOutcome</c> names for a sketch, deliberately. A user who has
/// learned what "under-constrained" means in the sketcher has learned what it means in an assembly,
/// and a second vocabulary for the same five facts would be a second thing to explain. They are
/// separate enums rather than one shared because the sets that come with them differ — a sketch
/// names entities, an assembly names bodies — and merging them would put sketch ids in an assembly
/// diagnosis.
/// </remarks>
public enum MateSolveOutcome
{
    /// <summary>Every body is pinned down. There is one answer and it was found.</summary>
    WellConstrained,

    /// <summary>Some freedom is left. The assembly solves, and components can still be dragged.</summary>
    UnderConstrained,

    /// <summary>The mates contradict each other. No placement satisfies them all.</summary>
    OverConstrained,

    /// <summary>Some mates say what others already said. The assembly solves and the extra ones do nothing.</summary>
    Redundant,

    /// <summary>The mates are consistent and the numbers did not converge.</summary>
    Failed,

    /// <summary>A mate names a combination of elements this build does not solve.</summary>
    /// <remarks>
    /// Not one of the sketch outcomes, because a sketch constraint cannot be malformed in this way:
    /// its operands are checked when it is built. A mate's ends are whatever geometry the modelling
    /// layer reduced a user's selection to, so "concentric between two points" is a thing a caller
    /// can hand over, and telling that apart from a genuine failure to converge is the difference
    /// between "reselect" and "your assembly is impossible".
    /// </remarks>
    Unsupported,
}

/// <summary>
/// What a mate solve found out, beyond the placements.
/// </summary>
/// <param name="Outcome">Which situation the assembly is in.</param>
/// <param name="RemainingFreedom">How many degrees of freedom are left.</param>
/// <param name="FreeBodies">Which bodies can still move.</param>
/// <param name="Conflicting">The mates that contradict each other, when over-constrained.</param>
/// <param name="Redundant">The mates that say nothing new, when redundant.</param>
/// <param name="Unsupported">The mates this build cannot solve, with the reason on each.</param>
/// <param name="Message">What to tell the user.</param>
/// <remarks>
/// The sets are the point, for the reason §5.6 gives about sketches: a diagnosis without a list
/// leaves the user deleting mates at random until the message goes away. An assembly makes that
/// worse than a sketch does, because the mates are spread across a tree the user has to expand to
/// see.
/// </remarks>
public sealed record MateDiagnosis(
    MateSolveOutcome Outcome,
    int RemainingFreedom = 0,
    ImmutableArray<MateBodyId> FreeBodies = default,
    ImmutableArray<MateId> Conflicting = default,
    ImmutableArray<MateId> Redundant = default,
    ImmutableArray<UnsupportedMate> Unsupported = default,
    string? Message = null)
{
    /// <summary>Gets whether the assembly is solved and cannot move.</summary>
    /// <remarks>
    /// True for <see cref="MateSolveOutcome.Redundant"/> as well as
    /// <see cref="MateSolveOutcome.WellConstrained"/>, the same call the sketcher makes: an assembly
    /// with a mate that says nothing is exactly as pinned down as one without it.
    /// </remarks>
    public bool IsFullyConstrained
        => Outcome is MateSolveOutcome.WellConstrained or MateSolveOutcome.Redundant;

    /// <summary>Gets the free bodies, never a default array.</summary>
    public ImmutableArray<MateBodyId> Free => FreeBodies.IsDefault ? [] : FreeBodies;

    /// <summary>Gets the conflicting mates, never a default array.</summary>
    public ImmutableArray<MateId> InConflict => Conflicting.IsDefault ? [] : Conflicting;

    /// <summary>Gets the redundant mates, never a default array.</summary>
    public ImmutableArray<MateId> SayingNothingNew => Redundant.IsDefault ? [] : Redundant;

    /// <summary>Gets the unsupported mates, never a default array.</summary>
    public ImmutableArray<UnsupportedMate> Unsolvable => Unsupported.IsDefault ? [] : Unsupported;
}

/// <summary>A mate this build cannot solve, and why.</summary>
/// <param name="Mate">Which mate.</param>
/// <param name="Reason">Why, in words the user can act on.</param>
public sealed record UnsupportedMate(MateId Mate, string Reason);

/// <summary>Where every body ended up, and what was found out.</summary>
/// <param name="Placements">Where each body sits now.</param>
/// <param name="Diagnosis">What the solve found out.</param>
/// <param name="Iterations">How many steps it took, for a performance budget.</param>
public sealed record MateSolveResult(
    ImmutableDictionary<MateBodyId, Transform> Placements,
    MateDiagnosis Diagnosis,
    int Iterations = 0)
{
    /// <summary>Gets whether the mates were satisfied.</summary>
    public bool IsSolved => Diagnosis.Outcome
        is MateSolveOutcome.WellConstrained
        or MateSolveOutcome.UnderConstrained
        or MateSolveOutcome.Redundant;
}

/// <summary>One body as the solver sees it: where it is, and whether it may move.</summary>
/// <param name="Id">Which body.</param>
/// <param name="Placement">Where it sits.</param>
/// <param name="IsGrounded">
/// Whether it is fixed. §5.9 asks for grounded components, and this is where that lives in the
/// solve: a grounded body's placement is an input rather than an unknown.
/// </param>
public sealed record MateBody(MateBodyId Id, Transform Placement, bool IsGrounded = false);

/// <summary>
/// Places the bodies of an assembly so that its mates hold (P5-T05, §5.9).
/// </summary>
/// <remarks>
/// <para>
/// The same shape as <c>ISketchSolver</c> and for the same reasons: an interface so that the
/// implementation can be replaced without the layers above noticing (ADR-0006), and a solve that
/// reports rather than throws, because an assembly the user is halfway through mating is the
/// ordinary case and not an exceptional one.
/// </para>
/// <para>
/// <b>Bodies and placements, not components and documents.</b> This layer is below
/// <c>OpenMCAD.Core</c> (PLAN.md 4.1), so it knows nothing of occurrences, definitions or files.
/// Mapping an occurrence path onto a <see cref="MateBodyId"/>, and a user's face selection onto a
/// <see cref="MateElement"/>, is the modelling layer's job — which is what lets the same solver be
/// handed a sub-assembly, a flattened product, or a test fixture with three cubes.
/// </para>
/// </remarks>
public interface IAssemblySolver
{
    /// <summary>Gets what to call this solver, for logs and for a bug report.</summary>
    string Name { get; }

    /// <summary>Places the bodies so the mates hold.</summary>
    /// <param name="bodies">The bodies, where they are now, and which of them are fixed.</param>
    /// <param name="mates">What must be true of them.</param>
    /// <param name="options">How hard to try.</param>
    /// <param name="cancellationToken">Abandons the solve.</param>
    /// <returns>Where everything ended up, and what was found out.</returns>
    /// <remarks>
    /// Takes the bodies as an argument rather than reading them from an assembly, so that a drag
    /// can hand over the subset it is moving. Which subset that is belongs to the caller: the
    /// assembly knows which components a mate touches, and this does not.
    /// </remarks>
    MateSolveResult Solve(
        IReadOnlyCollection<MateBody> bodies,
        IReadOnlyCollection<AssemblyMate> mates,
        MateSolverOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>How hard a mate solve should try.</summary>
/// <param name="MaximumIterations">How many steps before giving up.</param>
/// <param name="Tolerance">How close a residual has to be to zero to count as satisfied.</param>
public sealed record MateSolverOptions(
    int MaximumIterations = 100,
    double Tolerance = Tolerance.Linear)
{
    /// <summary>Gets the ordinary settings.</summary>
    public static MateSolverOptions Default { get; } = new();
}
