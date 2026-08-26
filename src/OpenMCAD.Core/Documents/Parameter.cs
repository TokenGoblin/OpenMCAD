namespace OpenMCAD.Core.Documents;

/// <summary>
/// A named value in a document, either given directly or computed from an expression.
/// </summary>
/// <param name="Name">What the parameter is called. Unique within a document.</param>
/// <param name="Value">
/// The current value. When <paramref name="Expression"/> is present this is the result of
/// evaluating it, cached here so that reading a document does not require an evaluator.
/// </param>
/// <param name="Expression">
/// The expression the value came from, or <see langword="null"/> if it was entered directly.
/// </param>
/// <param name="Description">What the parameter is for, shown to whoever edits it later.</param>
/// <remarks>
/// <para>
/// <b>Both the expression and its result are stored.</b> That is a deliberate redundancy, and it
/// costs a rule to maintain: after any edit, the value must be what the expression evaluates to.
/// It buys two things worth more than the rule costs. A document can be opened, inspected and
/// rendered without an expression evaluator, which matters for a viewer, a diff tool and every
/// test that does not care about expressions. And a document whose expression no longer evaluates
/// — a parameter it referenced was deleted — still has a last known good value to show, rather
/// than a hole where a number should be.
/// </para>
/// <para>
/// Evaluation itself is P3-T15, and the dependency edges between parameters that expressions imply
/// join the rebuild graph in P3-T16. Until then an expression is carried but not interpreted.
/// </para>
/// </remarks>
public sealed record Parameter(
    string Name,
    Quantity Value,
    string? Expression = null,
    string? Description = null)
{
    /// <summary>Gets whether the value is computed rather than entered.</summary>
    public bool IsDerived => !string.IsNullOrWhiteSpace(Expression);

    /// <summary>The name, as used for lookup.</summary>
    /// <remarks>
    /// Parameter names are compared without regard to case. Someone who types <c>length</c> in one
    /// expression and <c>Length</c> in another means the same parameter both times, and a modeller
    /// that treated those as two would be reporting an undefined-name error for something plainly
    /// defined. The declared spelling is preserved for display; only comparison ignores case.
    /// </remarks>
    public static StringComparer NameComparer => StringComparer.OrdinalIgnoreCase;

    /// <inheritdoc />
    public override string ToString()
        => IsDerived ? $"{Name} = {Expression} -> {Value}" : $"{Name} = {Value}";
}
