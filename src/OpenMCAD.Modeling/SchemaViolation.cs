using OpenMCAD.Core.Documents;

namespace OpenMCAD.Modeling;

/// <summary>How much a violation matters.</summary>
public enum ViolationSeverity
{
    /// <summary>The feature cannot be built as it stands.</summary>
    Error,

    /// <summary>The feature will build, and something about it is worth saying.</summary>
    Warning,
}

/// <summary>
/// Something wrong with a feature, found before the kernel was touched.
/// </summary>
/// <param name="Feature">Which feature.</param>
/// <param name="Property">Which property of it, by stable name.</param>
/// <param name="Severity">Whether this stops the feature building.</param>
/// <param name="Message">What to tell the user.</param>
/// <remarks>
/// <para>
/// P3-T21, and the pre-flight §5.7 asks for. Deliberately not a
/// <see cref="FeatureDiagnostic"/>: a rebuild diagnostic says what happened when a feature ran, and
/// this says why one should not be run at all. The two are asked at different times and answered
/// against different things — a document here, a built model there — and folding them together
/// would mean a report in which "you have not filled this in" and "the kernel could not do it" look
/// the same to whoever reads them.
/// </para>
/// <para>
/// It names the property. That is the whole value of catching these early: "the depth cannot be
/// less than 0" sends the user to a box on a panel, and the same mistake found inside an operation
/// produces a message about a surface that fails to bound a solid.
/// </para>
/// </remarks>
public sealed record SchemaViolation(
    FeatureId Feature,
    string Property,
    ViolationSeverity Severity,
    string Message)
{
    /// <summary>Gets whether this stops the feature building.</summary>
    public bool IsError => Severity == ViolationSeverity.Error;

    /// <inheritdoc/>
    public override string ToString() => $"{Feature}.{Property}: {Message}";
}
