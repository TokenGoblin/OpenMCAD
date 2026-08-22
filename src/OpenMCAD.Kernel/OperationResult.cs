using System.Collections.Immutable;

namespace OpenMCAD.Kernel;

/// <summary>
/// Which rung of the retry ladder produced a result.
/// </summary>
/// <remarks>
/// <para>
/// PLAN.md 5.2.4. OCCT's booleans and blends are the known robustness weak point (ADR-0001), so
/// fragile operations escalate through conditioning and tolerance relaxation rather than failing on
/// the first attempt.
/// </para>
/// <para>
/// This is reported on every result, not just failures, because the <i>distribution</i> of rungs
/// across the regression corpus is a health metric: if the share of operations succeeding at
/// <see cref="ModelTolerance"/> falls between releases, something regressed even though every test
/// still passes.
/// </para>
/// </remarks>
public enum RetryRung
{
    /// <summary>The operation is not fragile and does not use the ladder.</summary>
    NotApplicable = 0,

    /// <summary>Succeeded on the first attempt, at model tolerance. The healthy case.</summary>
    ModelTolerance = 1,

    /// <summary>Succeeded after conditioning the inputs: sewing, removing tiny edges, unifying same-domain faces.</summary>
    Conditioned = 2,

    /// <summary>Succeeded only at a relaxed fuzzy tolerance.</summary>
    FuzzyTolerance = 3,

    /// <summary>Succeeded only by isolating the failing subset, applying the operation edge by edge.</summary>
    PerEntityIsolation = 4,
}

/// <summary>The three ways an operation can end.</summary>
public enum OperationOutcome
{
    /// <summary>The operation did what was asked.</summary>
    Success = 0,

    /// <summary>The operation produced a usable result, but not the one that was asked for.</summary>
    Degraded = 1,

    /// <summary>The operation produced no usable result.</summary>
    Failed = 2,
}

/// <summary>
/// The result of a shape-producing kernel operation.
/// </summary>
/// <remarks>
/// <para>
/// Three cases, per PLAN.md 5.1: <see cref="Success"/>, <see cref="Degraded"/>, and
/// <see cref="Failed"/>. Match on them exhaustively.
/// </para>
/// <para>
/// <b><see cref="Degraded"/> is the case that matters and the one a naive design omits.</b> A
/// fillet asked for twelve edges that succeeds on eleven has not failed — throwing the result away
/// and reporting failure would be strictly worse for the user than production MCAD, which hands
/// back the eleven and says which one it could not do. Callers that treat anything other than
/// <see cref="Success"/> as failure are discarding work the kernel already did.
/// </para>
/// </remarks>
public abstract record OperationResult
{
    private OperationResult()
    {
    }

    /// <summary>Gets which of the three cases this is.</summary>
    public abstract OperationOutcome Outcome { get; }

    /// <summary>Gets the diagnostics attached to this result. Never default.</summary>
    public abstract ImmutableArray<KernelDiagnostic> Diagnostics { get; }

    /// <summary>Gets the rung of the retry ladder that produced this result.</summary>
    public abstract RetryRung Rung { get; }

    /// <summary>Gets a value indicating whether a shape was produced.</summary>
    public bool HasShape => Outcome != OperationOutcome.Failed;

    /// <summary>Gets the produced shape, or throws if the operation failed.</summary>
    /// <param name="shape">The produced shape.</param>
    /// <param name="history">The correspondence between inputs and outputs.</param>
    /// <returns><see langword="true"/> if a shape was produced.</returns>
    public bool TryGetShape(out KernelShapeHandle shape, out HistoryMap history)
    {
        switch (this)
        {
            case Success success:
                shape = success.Shape;
                history = success.History;
                return true;
            case Degraded degraded:
                shape = degraded.Shape;
                history = degraded.History;
                return true;
            default:
                shape = null!;
                history = HistoryMap.Empty;
                return false;
        }
    }

    /// <summary>
    /// Returns a one-line summary suitable for a rebuild report or a log entry.
    /// </summary>
    public string Describe()
    {
        string rung = Rung == RetryRung.NotApplicable ? string.Empty : $" (rung {(int)Rung})";
        string diagnostics = Diagnostics.IsEmpty
            ? string.Empty
            : " — " + string.Join("; ", Diagnostics.Select(d => d.Message));
        return $"{Outcome}{rung}{diagnostics}";
    }

    /// <summary>The operation did what was asked.</summary>
    /// <param name="Shape">The resulting shape. The caller owns it and must dispose it.</param>
    /// <param name="History">The correspondence between the operation's inputs and its outputs.</param>
    /// <param name="Rung">Which rung of the retry ladder succeeded.</param>
    /// <param name="Diagnostics">Informational notes, usually empty.</param>
    public sealed record Success(
        KernelShapeHandle Shape,
        HistoryMap History,
        RetryRung Rung = RetryRung.NotApplicable,
        ImmutableArray<KernelDiagnostic> Diagnostics = default) : OperationResult
    {
        /// <inheritdoc />
        public override OperationOutcome Outcome => OperationOutcome.Success;

        /// <inheritdoc />
        public override ImmutableArray<KernelDiagnostic> Diagnostics { get; }
            = Diagnostics.IsDefault ? [] : Diagnostics;

        /// <inheritdoc />
        public override RetryRung Rung { get; } = Rung;
    }

    /// <summary>
    /// The operation produced a usable shape, but not the one that was asked for.
    /// </summary>
    /// <param name="Shape">The resulting shape. The caller owns it and must dispose it.</param>
    /// <param name="History">The correspondence between the operation's inputs and its outputs.</param>
    /// <param name="Warnings">What was not achieved, and why. Must be non-empty.</param>
    /// <param name="Rung">Which rung of the retry ladder produced this.</param>
    public sealed record Degraded(
        KernelShapeHandle Shape,
        HistoryMap History,
        ImmutableArray<KernelDiagnostic> Warnings,
        RetryRung Rung = RetryRung.NotApplicable) : OperationResult
    {
        /// <inheritdoc />
        public override OperationOutcome Outcome => OperationOutcome.Degraded;

        /// <inheritdoc />
        public override ImmutableArray<KernelDiagnostic> Diagnostics { get; }
            = Warnings.IsDefaultOrEmpty
                ? throw new ArgumentException(
                    "A degraded result must say what was not achieved. If there is nothing to "
                    + "report, the result is a Success.",
                    nameof(Warnings))
                : Warnings;

        /// <inheritdoc />
        public override RetryRung Rung { get; } = Rung;
    }

    /// <summary>The operation produced no usable shape.</summary>
    /// <param name="Errors">Why it failed. Must be non-empty and user-actionable.</param>
    /// <param name="Rung">The last rung attempted before giving up.</param>
    public sealed record Failed(
        ImmutableArray<KernelDiagnostic> Errors,
        RetryRung Rung = RetryRung.NotApplicable) : OperationResult
    {
        /// <inheritdoc />
        public override OperationOutcome Outcome => OperationOutcome.Failed;

        /// <inheritdoc />
        public override ImmutableArray<KernelDiagnostic> Diagnostics { get; }
            = Errors.IsDefaultOrEmpty
                ? throw new ArgumentException(
                    "A failed result must say why. A failure the user cannot understand or work "
                    + "around is the outcome PLAN.md 6.1 exists to prevent.",
                    nameof(Errors))
                : Errors;

        /// <inheritdoc />
        public override RetryRung Rung { get; } = Rung;

        /// <summary>Creates a failure carrying a single diagnostic.</summary>
        /// <param name="code">A code from <see cref="KernelDiagnosticCodes"/>.</param>
        /// <param name="message">A user-actionable message.</param>
        /// <param name="entities">The entities at fault, if localised.</param>
        /// <param name="kernelDetail">Raw kernel text for logs.</param>
        public static Failed From(
            string code,
            string message,
            ImmutableArray<SubEntity> entities = default,
            string? kernelDetail = null)
            => new([KernelDiagnostic.Error(code, message, entities, kernelDetail)]);
    }
}
