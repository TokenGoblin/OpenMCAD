using System.Collections.Immutable;

namespace OpenMCAD.Kernel;

/// <summary>How serious a kernel diagnostic is.</summary>
public enum DiagnosticSeverity
{
    /// <summary>The operation succeeded; this is context worth recording.</summary>
    Information = 0,

    /// <summary>The operation produced a result, but not the one that was asked for.</summary>
    Warning = 1,

    /// <summary>The operation did not produce a usable result.</summary>
    Error = 2,
}

/// <summary>
/// One thing the kernel has to say about an operation.
/// </summary>
/// <param name="Severity">How serious this is.</param>
/// <param name="Code">
/// A stable identifier for the kind of problem, from <see cref="KernelDiagnosticCodes"/>. Stable
/// because the health metrics in PLAN.md 5.2.4 aggregate over it and tests assert on it; message
/// text is free to be reworded, codes are not.
/// </param>
/// <param name="Message">
/// What to tell the user. Must be actionable and in their terms — see the remarks.
/// </param>
/// <param name="Entities">The specific entities at fault, empty if the problem is not localised.</param>
/// <param name="KernelDetail">
/// The raw text from the underlying kernel, for logs and bug reports. <b>Never shown to a user.</b>
/// </param>
/// <remarks>
/// <para>
/// PLAN.md 6.1: "CAD users forgive failures; they do not forgive failures they cannot understand or
/// work around." The difference between those two outcomes is entirely in <paramref name="Message"/>.
/// </para>
/// <para>
/// The standard the plan sets is a concrete one, worth quoting because it is easy to fall short of:
/// "the fillet radius 8 mm exceeds the available material at edge <i>E</i>; try 5 mm or reorder
/// before the shell". Note what that contains — the offending value, the offending entity, and a
/// specific next action. "BRepFilletAPI_MakeFillet failed" contains none of them and is not an
/// acceptable message, however faithfully it reports what the kernel said. Put that text in
/// <paramref name="KernelDetail"/>, where it belongs.
/// </para>
/// </remarks>
public sealed record KernelDiagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    ImmutableArray<SubEntity> Entities,
    string? KernelDetail = null)
{
    /// <summary>Creates an error diagnostic.</summary>
    /// <param name="code">A code from <see cref="KernelDiagnosticCodes"/>.</param>
    /// <param name="message">A user-actionable message.</param>
    /// <param name="entities">The entities at fault, if the problem is localised.</param>
    /// <param name="kernelDetail">Raw kernel text for logs.</param>
    public static KernelDiagnostic Error(
        string code,
        string message,
        ImmutableArray<SubEntity> entities = default,
        string? kernelDetail = null)
        => new(DiagnosticSeverity.Error, code, message, Normalise(entities), kernelDetail);

    /// <summary>Creates a warning diagnostic.</summary>
    /// <param name="code">A code from <see cref="KernelDiagnosticCodes"/>.</param>
    /// <param name="message">A user-actionable message.</param>
    /// <param name="entities">The entities concerned, if the warning is localised.</param>
    /// <param name="kernelDetail">Raw kernel text for logs.</param>
    public static KernelDiagnostic Warning(
        string code,
        string message,
        ImmutableArray<SubEntity> entities = default,
        string? kernelDetail = null)
        => new(DiagnosticSeverity.Warning, code, message, Normalise(entities), kernelDetail);

    /// <summary>Creates an informational diagnostic.</summary>
    /// <param name="code">A code from <see cref="KernelDiagnosticCodes"/>.</param>
    /// <param name="message">The message.</param>
    public static KernelDiagnostic Information(string code, string message)
        => new(DiagnosticSeverity.Information, code, message, []);

    /// <inheritdoc />
    public override string ToString()
        => Entities.IsDefaultOrEmpty
            ? $"{Severity} {Code}: {Message}"
            : $"{Severity} {Code}: {Message} [{string.Join(", ", Entities)}]";

    private static ImmutableArray<SubEntity> Normalise(ImmutableArray<SubEntity> entities)
        => entities.IsDefault ? [] : entities;
}

/// <summary>
/// Stable diagnostic codes.
/// </summary>
/// <remarks>
/// <para>
/// Format is <c>OMK</c> plus four digits, banded by cause. Codes are permanent once shipped: the
/// retry-ladder health metrics aggregate over them across releases, and a renumbering would make
/// historical data meaningless.
/// </para>
/// <para>
/// Bands: 1xxx input validation (the caller is wrong), 2xxx operation failure (the kernel could not
/// do it), 3xxx degraded results, 4xxx lifetime and boundary misuse, 9xxx internal faults.
/// </para>
/// </remarks>
public static class KernelDiagnosticCodes
{
    // --- 1xxx: input validation, detected before the kernel is touched -------------------------

    /// <summary>A dimension was zero, negative, or otherwise outside its permitted range.</summary>
    public const string InvalidDimension = "OMK1001";

    /// <summary>A direction or axis vector was degenerate.</summary>
    public const string DegenerateDirection = "OMK1002";

    /// <summary>A profile was self-intersecting, open where it must be closed, or empty.</summary>
    public const string InvalidProfile = "OMK1003";

    /// <summary>An operation was given no entities to act on.</summary>
    public const string EmptySelection = "OMK1004";

    /// <summary>An entity was of the wrong kind for the operation, such as a face where an edge was required.</summary>
    public const string WrongEntityKind = "OMK1005";

    /// <summary>An entity does not belong to the shape the operation is acting on.</summary>
    public const string EntityNotInShape = "OMK1006";

    /// <summary>An angle was zero, or outside the permitted range for the operation.</summary>
    public const string InvalidAngle = "OMK1007";

    // --- 2xxx: the operation failed --------------------------------------------------------------

    /// <summary>A boolean operation failed after every rung of the retry ladder.</summary>
    public const string BooleanFailed = "OMK2001";

    /// <summary>A blend failed on every requested edge.</summary>
    public const string BlendFailed = "OMK2002";

    /// <summary>A blend radius exceeds the material available at an edge.</summary>
    public const string BlendRadiusTooLarge = "OMK2003";

    /// <summary>A sweep or revolve produced a self-intersecting result.</summary>
    public const string SelfIntersecting = "OMK2004";

    /// <summary>The operation produced a shape that failed validity checking.</summary>
    public const string InvalidResult = "OMK2005";

    /// <summary>The operation produced no geometry at all, for example a subtraction that removed everything.</summary>
    public const string EmptyResult = "OMK2006";

    /// <summary>Reading or writing an exchange or B-rep format failed.</summary>
    public const string SerializationFailed = "OMK2007";

    // --- 3xxx: degraded results ------------------------------------------------------------------

    /// <summary>A blend succeeded on some edges and failed on others.</summary>
    public const string BlendPartiallyApplied = "OMK3001";

    /// <summary>The operation succeeded only after input conditioning or a relaxed tolerance.</summary>
    public const string SucceededAfterRetry = "OMK3002";

    /// <summary>The result is valid but has more bodies than expected, for example a subtraction that split the target.</summary>
    public const string ResultSplitIntoMultipleBodies = "OMK3003";

    /// <summary>Mass properties are approximate rather than exact.</summary>
    public const string ApproximateMassProperties = "OMK3004";

    // --- 4xxx: lifetime and boundary misuse --------------------------------------------------------

    /// <summary>A shape or entity tag is unknown, or refers to a released slot.</summary>
    public const string StaleHandle = "OMK4001";

    /// <summary>A shape from a different kernel instance was passed in.</summary>
    public const string ForeignHandle = "OMK4002";

    /// <summary>The kernel has been disposed.</summary>
    public const string KernelDisposed = "OMK4003";

    // --- 9xxx: internal ------------------------------------------------------------------------------

    /// <summary>The operation is not implemented by this kernel.</summary>
    public const string NotImplemented = "OMK9001";

    /// <summary>An unexpected fault inside the kernel or the interop boundary.</summary>
    public const string InternalError = "OMK9002";
}
