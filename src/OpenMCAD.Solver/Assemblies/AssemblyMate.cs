using System.Collections.Immutable;

namespace OpenMCAD.Solver.Assemblies;

/// <summary>Identifies one rigid body in a solve.</summary>
/// <param name="Value">The underlying value.</param>
/// <remarks>
/// <para>
/// The solver's own handle, not the document's. An assembly instance is named by an
/// <c>OccurrencePath</c> in <c>OpenMCAD.Core.Assemblies</c>, which is a layer above this one
/// (PLAN.md 4.1) — so the mapping from a path to a body is the modelling layer's, exactly as
/// <c>SketchEntityId</c> is the sketch solver's own and not a document id.
/// </para>
/// <para>
/// That separation is what makes this a rigid-body constraint solver rather than an assembly
/// feature: it knows about bodies, placements and residuals, and nothing about documents, files or
/// what a component is.
/// </para>
/// </remarks>
public readonly record struct MateBodyId(Guid Value)
{
    /// <summary>Gets the id that denotes no body.</summary>
    public static MateBodyId None => default;

    /// <summary>Gets whether this denotes a body.</summary>
    public bool IsValid => Value != Guid.Empty;

    /// <summary>Creates a new, unique id.</summary>
    /// <returns>The id.</returns>
    public static MateBodyId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value == Guid.Empty
        ? "body(none)"
        : $"body({Value:N})";
}

/// <summary>Identifies one mate, so a diagnosis can name it.</summary>
/// <param name="Value">The underlying value.</param>
/// <remarks>
/// §5.6 is blunt that "over-constrained" without a list is useless to a user, because the only
/// thing they can do with it is delete constraints at random until the message goes away. The same
/// applies to mates, and a diagnosis can only name them if they have names.
/// </remarks>
public readonly record struct MateId(Guid Value)
{
    /// <summary>Gets the id that denotes no mate.</summary>
    public static MateId None => default;

    /// <summary>Gets whether this denotes a mate.</summary>
    public bool IsValid => Value != Guid.Empty;

    /// <summary>Creates a new, unique id.</summary>
    /// <returns>The id.</returns>
    public static MateId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => Value == Guid.Empty ? "mate(none)" : $"mate({Value:N})";
}

/// <summary>What a mate says about the two things it joins.</summary>
/// <remarks>
/// The minimal set §5.9 names for P5-T05. The full list — parallel, perpendicular, tangent, angle,
/// lock, width, symmetric, path, gear, and the mechanical joints — is Phase 9, and each of those is
/// a residual and a degree-of-freedom count added here rather than a change of shape.
/// </remarks>
public enum MateKind
{
    /// <summary>The two elements touch: planes flush, points together, or a point on a plane.</summary>
    Coincident,

    /// <summary>Two axes lie on the same line, as a shaft in a hole.</summary>
    Concentric,

    /// <summary>The two elements are held a stated distance apart.</summary>
    Distance,
}

/// <summary>
/// One mate: a statement about two bodies that a solve must make true (P5-T05).
/// </summary>
/// <param name="Id">Identifies this mate, so a diagnosis can name it.</param>
/// <param name="Kind">What it says.</param>
/// <param name="First">Which body, and what on it.</param>
/// <param name="Second">The other body, and what on it.</param>
/// <param name="Value">
/// How far apart, for <see cref="MateKind.Distance"/>. Ignored by the other kinds, which have
/// nothing to measure.
/// </param>
/// <param name="IsFlipped">
/// Whether the mate is satisfied the other way round. Two planes brought together can face each
/// other or face the same way, and both are things a user asks for; a mate that could only mean one
/// of them would send the user to rebuild the component the other way up.
/// </param>
/// <remarks>
/// <para>
/// One record with a kind rather than three records, the same call <c>SketchConstraint</c> made and
/// for the same reason: the solver boundary needs a uniform representation anyway, and a set of
/// sibling types would have to be flattened at the boundary and unflattened for every reader.
/// </para>
/// <para>
/// <b>Which element shapes each kind accepts is not free.</b> A concentric mate between two points
/// says nothing, and a distance between an axis and a plane is ambiguous until somebody decides
/// whether it means the nearest point or the parallel offset. <see cref="MatePairing"/> is where the
/// accepted combinations are declared, so that a mate nobody can satisfy is refused when it is made
/// rather than discovered as a failure to converge.
/// </para>
/// </remarks>
public sealed record AssemblyMate(
    MateId Id,
    MateKind Kind,
    MateAttachment First,
    MateAttachment Second,
    double Value = 0,
    bool IsFlipped = false)
{
    /// <summary>Gets the bodies this mate joins, without duplicates.</summary>
    /// <returns>One body when a mate joins something to itself, two otherwise.</returns>
    /// <remarks>
    /// A mate between two elements of one body is not obviously nonsense — it is a statement about
    /// that body's own geometry — but it constrains nothing a solve can move, so the analysis
    /// treats it as grounded rather than as a degree of freedom removed.
    /// </remarks>
    public ImmutableArray<MateBodyId> Bodies => First.Body == Second.Body
        ? [First.Body]
        : [First.Body, Second.Body];

    /// <inheritdoc />
    public override string ToString() => Kind == MateKind.Distance
        ? $"{Kind} {Value} between {First.Body} and {Second.Body}"
        : $"{Kind} between {First.Body} and {Second.Body}";
}

/// <summary>One end of a mate: which body, and what on it.</summary>
/// <param name="Body">The body.</param>
/// <param name="Element">What the mate attaches to, in that body's frame.</param>
public sealed record MateAttachment(MateBodyId Body, MateElement Element);
