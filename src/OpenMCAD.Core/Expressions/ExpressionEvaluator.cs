using System.Collections.Immutable;

using OpenMCAD.Core.Documents;

namespace OpenMCAD.Core.Expressions;

/// <summary>
/// Computes what an expression comes to.
/// </summary>
/// <remarks>
/// <para>
/// Runs only on an expression that has already type-checked, and says so by throwing rather than
/// reporting if it meets a dimension it cannot handle. Every user-facing problem — a missing
/// parameter, a length added to an angle, a function that does not exist — is found by
/// <see cref="ExpressionParser"/> before anything gets here, so a failure at this point is a defect
/// in the program rather than in what the user typed.
/// </para>
/// <para>
/// Division by zero is deliberately not an error. It produces an infinity, which
/// <see cref="Quantity.IsFinite"/> reports and the caller decides about: a parameter that has gone
/// infinite is a problem for whoever is about to build geometry from it, and stopping the whole
/// evaluation here would deny them the chance to say so in terms of their own model.
/// </para>
/// </remarks>
public static class ExpressionEvaluator
{
    /// <summary>Evaluates an expression.</summary>
    /// <param name="expression">The expression, already checked.</param>
    /// <param name="valueOf">
    /// What a referenced parameter is worth. Must answer for every reference the check accepted.
    /// </param>
    /// <returns>The value.</returns>
    /// <exception cref="DimensionException">
    /// The expression was not checked, and does not mean anything.
    /// </exception>
    /// <exception cref="InvalidOperationException">A reference has no value.</exception>
    public static Quantity Evaluate(
        Expression expression, Func<Expression.Reference, Quantity?> valueOf)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(valueOf);

        switch (expression)
        {
            case Expression.Literal literal:
                return literal.Value;

            case Expression.Reference reference:
                return valueOf(reference) ?? throw new InvalidOperationException(
                    $"'{reference}' has no value. An expression should be checked before it is "
                    + "evaluated, and the check is what reports a missing parameter to the user.");

            case Expression.Unary unary:
                Quantity operand = Evaluate(unary.Operand, valueOf);
                return unary.Operator == "-" ? -operand : operand;

            case Expression.Binary binary:
                return Apply(
                    binary.Operator,
                    Evaluate(binary.Left, valueOf),
                    Evaluate(binary.Right, valueOf));

            case Expression.Invocation call:
                return Call(call, valueOf);

            default:
                throw new InvalidOperationException(
                    $"There is no way to evaluate a {expression.GetType().Name}.");
        }
    }

    /// <summary>Parses, checks and evaluates in one go.</summary>
    /// <param name="text">What the user typed.</param>
    /// <param name="valueOf">What a referenced parameter is worth.</param>
    /// <returns>The value, or what is wrong with the expression.</returns>
    /// <remarks>
    /// For a caller with nothing to do between the two steps. The dimension check uses the same
    /// lookup, so a parameter that exists is one whose dimension is known — which is why this can
    /// be one call rather than two.
    /// </remarks>
    public static (Quantity? Value, ImmutableArray<ExpressionError> Errors) Evaluate(
        string text, Func<Expression.Reference, Quantity?> valueOf)
    {
        ArgumentNullException.ThrowIfNull(valueOf);

        ParsedExpression parsed = ExpressionParser.Parse(
            text, reference => valueOf(reference)?.Dimension);

        return parsed is { IsValid: true, Root: { } root }
            ? (Evaluate(root, valueOf), [])
            : (null, parsed.Errors);
    }

    private static Quantity Apply(string op, Quantity left, Quantity right) => op switch
    {
        "+" => left + right,
        "-" => left - right,
        "*" => left * right,
        "/" => left / right,
        "<" => Truth(left.CompareTo(right) < 0),
        "<=" => Truth(left.CompareTo(right) <= 0),
        ">" => Truth(left.CompareTo(right) > 0),
        ">=" => Truth(left.CompareTo(right) >= 0),
        "==" => Truth(left.CompareTo(right) == 0),
        "!=" => Truth(left.CompareTo(right) != 0),
        _ => throw new InvalidOperationException($"There is no operator '{op}'."),
    };

    /// <summary>
    /// A comparison's answer, as a plain number.
    /// </summary>
    /// <remarks>
    /// One and nought rather than a type of their own. A boolean would be a second kind of value
    /// running through every part of this — a dimension that is not a dimension, an arithmetic that
    /// does not apply — for the sake of one function taking one argument. The cost is that
    /// <c>if(1, a, b)</c> is accepted, which is odd and harmless.
    /// </remarks>
    private static Quantity Truth(bool value) => Quantity.Number(value ? 1 : 0);

    private static Quantity Call(
        Expression.Invocation call, Func<Expression.Reference, Quantity?> valueOf)
    {
        FunctionDefinition function = ExpressionFunctions.Find(call.Function)
            ?? throw new InvalidOperationException(
                $"There is no function called '{call.Function}'.");

        // 'if' evaluates its test and then only the branch it needs. That is not merely faster:
        // the branch not taken is allowed to be something that would fail, which is what makes
        // if(x != 0, y / x, 0) a sensible thing to write.
        if (string.Equals(call.Function, "if", StringComparison.OrdinalIgnoreCase)
            && call.Arguments.Length == 3)
        {
            return Evaluate(call.Arguments[0], valueOf).Value != 0
                ? Evaluate(call.Arguments[1], valueOf)
                : Evaluate(call.Arguments[2], valueOf);
        }

        ImmutableArray<Quantity>.Builder arguments =
            ImmutableArray.CreateBuilder<Quantity>(call.Arguments.Length);

        foreach (Expression argument in call.Arguments)
        {
            arguments.Add(Evaluate(argument, valueOf));
        }

        return function.Apply(arguments.ToImmutable());
    }
}
