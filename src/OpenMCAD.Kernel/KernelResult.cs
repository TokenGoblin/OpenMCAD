using System.Collections.Immutable;

namespace OpenMCAD.Kernel;

/// <summary>
/// The result of a kernel query — an operation that answers a question rather than producing a
/// shape.
/// </summary>
/// <typeparam name="T">The answer type.</typeparam>
/// <remarks>
/// <para>
/// Separate from <see cref="OperationResult"/> because the two differ in what they hand back and in
/// what a caller does with them: a query has no shape to own and no history to record, and merging
/// them would force every call site to reason about members that cannot apply.
/// </para>
/// <para>
/// Queries can be <see cref="Degraded"/> too. Mass properties computed from a tessellation rather
/// than from exact integration are a real answer with a real caveat, and silently returning them as
/// exact is how a mass figure ends up on a drawing with more confidence than it deserves.
/// </para>
/// </remarks>
/// <param name="Diagnostics">Anything the kernel has to say. Never default.</param>
public abstract record KernelResult<T>(ImmutableArray<KernelDiagnostic> Diagnostics)
{
    /// <summary>Gets a value indicating whether an answer is available.</summary>
    public bool HasValue => this is not Failed;

    /// <summary>Gets the answer, or throws if the query failed.</summary>
    /// <exception cref="InvalidOperationException">The query failed.</exception>
    public T Value => this switch
    {
        Success success => success.Result,
        Degraded degraded => degraded.Result,
        _ => throw new InvalidOperationException(
            "The query failed and has no value: "
            + string.Join("; ", Diagnostics.Select(d => d.Message))),
    };

    /// <summary>Gets the answer if there is one.</summary>
    /// <param name="value">The answer, or <see langword="default"/>.</param>
    /// <returns><see langword="true"/> if an answer is available.</returns>
    public bool TryGetValue(out T value)
    {
        switch (this)
        {
            case Success success:
                value = success.Result;
                return true;
            case Degraded degraded:
                value = degraded.Result;
                return true;
            default:
                value = default!;
                return false;
        }
    }

    /// <summary>The query produced an answer.</summary>
    /// <param name="Result">The answer.</param>
    /// <param name="Diagnostics">Informational notes, usually empty.</param>
    public sealed record Success(T Result, ImmutableArray<KernelDiagnostic> Diagnostics)
        : KernelResult<T>(Diagnostics.IsDefault ? [] : Diagnostics);

    /// <summary>The query produced an answer with caveats.</summary>
    /// <param name="Result">The answer.</param>
    /// <param name="Warnings">The caveats. Must be non-empty.</param>
    public sealed record Degraded(T Result, ImmutableArray<KernelDiagnostic> Warnings)
        : KernelResult<T>(Warnings.IsDefaultOrEmpty
            ? throw new ArgumentException(
                "A degraded query result must state its caveats; without them it is a Success.",
                nameof(Warnings))
            : Warnings);

    /// <summary>The query produced no answer.</summary>
    /// <param name="Errors">Why. Must be non-empty.</param>
    public sealed record Failed(ImmutableArray<KernelDiagnostic> Errors)
        : KernelResult<T>(Errors.IsDefaultOrEmpty
            ? throw new ArgumentException(
                "A failed query must say why.",
                nameof(Errors))
            : Errors);
}

/// <summary>
/// Factory methods for <see cref="KernelResult{T}"/>.
/// </summary>
/// <remarks>
/// Non-generic so the factories are not static members of a generic type, which would force every
/// call site to restate the type argument it already knows from context.
/// </remarks>
public static class KernelResult
{
    /// <summary>Creates a successful result.</summary>
    /// <typeparam name="T">The answer type, usually inferred.</typeparam>
    /// <param name="value">The answer.</param>
    public static KernelResult<T> Ok<T>(T value) => new KernelResult<T>.Success(value, []);

    /// <summary>Creates a result with caveats.</summary>
    /// <typeparam name="T">The answer type, usually inferred.</typeparam>
    /// <param name="value">The answer.</param>
    /// <param name="warnings">The caveats. Must be non-empty.</param>
    public static KernelResult<T> Degraded<T>(T value, ImmutableArray<KernelDiagnostic> warnings)
        => new KernelResult<T>.Degraded(value, warnings);

    /// <summary>Creates a failed result carrying a single diagnostic.</summary>
    /// <typeparam name="T">The answer type.</typeparam>
    /// <param name="code">A code from <see cref="KernelDiagnosticCodes"/>.</param>
    /// <param name="message">A user-actionable message.</param>
    /// <param name="entities">The entities at fault, if localised.</param>
    /// <param name="kernelDetail">Raw kernel text for logs.</param>
    public static KernelResult<T> Fail<T>(
        string code,
        string message,
        ImmutableArray<SubEntity> entities = default,
        string? kernelDetail = null)
        => new KernelResult<T>.Failed([KernelDiagnostic.Error(code, message, entities, kernelDetail)]);

    /// <summary>Creates a failed result from existing diagnostics.</summary>
    /// <typeparam name="T">The answer type.</typeparam>
    /// <param name="errors">Why it failed. Must be non-empty.</param>
    public static KernelResult<T> Fail<T>(ImmutableArray<KernelDiagnostic> errors)
        => new KernelResult<T>.Failed(errors);
}
