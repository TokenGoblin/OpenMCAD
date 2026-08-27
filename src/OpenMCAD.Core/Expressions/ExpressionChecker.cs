using System.Collections.Immutable;

using OpenMCAD.Core.Documents;

namespace OpenMCAD.Core.Expressions;

/// <summary>
/// Works out what an expression measures, without computing anything.
/// </summary>
/// <remarks>
/// <para>
/// This is what §5.5 means by catching <c>4 mm + 3 deg</c> before evaluation. It reads no parameter
/// values, calls no functions and produces no numbers — it walks the tree carrying only dimensions,
/// so it can answer the moment the text is syntactically whole and while the user is still looking
/// at what they typed.
/// </para>
/// <para>
/// It reports every independent problem rather than stopping at the first, but a node whose
/// children failed is not reported again: one mistake should produce one complaint, and a badly
/// typed sub-expression otherwise produces one for every operator above it.
/// </para>
/// </remarks>
internal static class ExpressionChecker
{
    public static ParsedExpression Check(
        Expression root, Func<Expression.Reference, Dimension?> dimensionOf)
    {
        ImmutableArray<ExpressionError>.Builder errors =
            ImmutableArray.CreateBuilder<ExpressionError>();

        Dimension? dimension = Walk(root, dimensionOf, errors);

        return new ParsedExpression(root, errors.ToImmutable(), dimension);
    }

    private static Dimension? Walk(
        Expression expression,
        Func<Expression.Reference, Dimension?> dimensionOf,
        ImmutableArray<ExpressionError>.Builder errors)
        => expression switch
        {
            Expression.Literal literal => literal.Value.Dimension,
            Expression.Reference reference => CheckReference(reference, dimensionOf, errors),
            Expression.Unary unary => Walk(unary.Operand, dimensionOf, errors),
            Expression.Binary binary => CheckBinary(binary, dimensionOf, errors),
            Expression.Invocation call => CheckCall(call, dimensionOf, errors),
            _ => null,
        };

    private static Dimension? CheckReference(
        Expression.Reference reference,
        Func<Expression.Reference, Dimension?> dimensionOf,
        ImmutableArray<ExpressionError>.Builder errors)
    {
        if (dimensionOf(reference) is { } dimension)
        {
            return dimension;
        }

        errors.Add(new ExpressionError(
            reference.IsCrossDocument
                ? $"'{reference}' does not name a parameter — '{reference.Document}' may not be "
                    + "open, or may not have a parameter called that."
                : $"There is no parameter called '{reference.Name}'.",
            reference.Position,
            reference.ToString().Length));

        return null;
    }

    private static Dimension? CheckBinary(
        Expression.Binary binary,
        Func<Expression.Reference, Dimension?> dimensionOf,
        ImmutableArray<ExpressionError>.Builder errors)
    {
        Dimension? left = Walk(binary.Left, dimensionOf, errors);
        Dimension? right = Walk(binary.Right, dimensionOf, errors);

        // Something below already failed and has already been complained about. Adding "and
        // therefore this operator does not work either" helps nobody.
        if (left is not { } a || right is not { } b)
        {
            return null;
        }

        Dimension? result = binary.Operator switch
        {
            "+" or "-" => Dimensions.Add(a, b),
            "*" => Dimensions.Multiply(a, b),
            "/" => Dimensions.Divide(a, b),
            _ => Dimensions.CanCompare(a, b) ? Dimension.Dimensionless : null,
        };

        if (result is not null)
        {
            return result;
        }

        errors.Add(new ExpressionError(
            Explain(binary.Operator, a, b), binary.Position, binary.Operator.Length));

        return null;
    }

    private static Dimension? CheckCall(
        Expression.Invocation call,
        Func<Expression.Reference, Dimension?> dimensionOf,
        ImmutableArray<ExpressionError>.Builder errors)
    {
        ImmutableArray<Dimension>.Builder arguments =
            ImmutableArray.CreateBuilder<Dimension>(call.Arguments.Length);

        bool sound = true;

        foreach (Expression argument in call.Arguments)
        {
            if (Walk(argument, dimensionOf, errors) is { } dimension)
            {
                arguments.Add(dimension);
            }
            else
            {
                sound = false;
            }
        }

        if (ExpressionFunctions.Find(call.Function) is not { } function)
        {
            errors.Add(new ExpressionError(
                $"There is no function called '{call.Function}'. The ones there are: "
                + string.Join(", ", ExpressionFunctions.Names) + ".",
                call.Position,
                call.Function.Length));

            return null;
        }

        if (call.Arguments.Length < function.MinimumArguments
            || call.Arguments.Length > function.MaximumArguments)
        {
            errors.Add(new ExpressionError(
                function.MinimumArguments == function.MaximumArguments
                    ? $"'{function.Name}' takes {function.MinimumArguments} "
                        + $"{Things(function.MinimumArguments)}, and was given {call.Arguments.Length}."
                    : $"'{function.Name}' takes between {function.MinimumArguments} and "
                        + $"{function.MaximumArguments} arguments, and was given {call.Arguments.Length}.",
                call.Position,
                call.Function.Length));

            return null;
        }

        if (!sound)
        {
            return null;
        }

        if (function.ResultDimension(arguments.ToImmutable()) is { } result)
        {
            return result;
        }

        errors.Add(new ExpressionError(
            ExplainCall(function.Name, arguments), call.Position, call.Function.Length));

        return null;
    }

    private static string Things(int count) => count == 1 ? "argument" : "arguments";

    /// <summary>Says why an operator will not do, in terms of the model rather than the code.</summary>
    private static string Explain(string op, Dimension left, Dimension right)
    {
        string a = Name(left);
        string b = Name(right);

        string hint = left != right && (left == Dimension.Dimensionless || right == Dimension.Dimensionless)
            ? " If you meant a measurement, write the unit after the number — 5mm rather than 5."
            : string.Empty;

        return op switch
        {
            "+" => $"{a} and {b} cannot be added together.{hint}",
            "-" => $"{b} cannot be subtracted from {a}.{hint}",
            "*" => $"There is no such thing as {a} times {b}.",
            "/" => $"There is no such thing as {a} divided by {b}.",
            _ => $"{a} and {b} cannot be compared with one another.{hint}",
        };
    }

    private static string ExplainCall(string name, ImmutableArray<Dimension>.Builder arguments)
    {
        string given = string.Join(" and ", arguments.Select(Name));

        return name.ToLowerInvariant() switch
        {
            "sin" or "cos" or "tan" =>
                $"'{name}' needs an angle, and was given {given}. Write the unit — 45deg or 0.5rad.",

            "asin" or "acos" or "atan" =>
                $"'{name}' needs a plain number, and was given {given}.",

            "floor" or "ceil" or "round" =>
                $"'{name}' needs a plain number, and was given {given}. Values are stored in metres, "
                + $"so rounding a measurement directly would round it to the nearest metre — divide "
                + $"by the unit you want first, as in round(x / 1mm) * 1mm.",

            "if" when arguments.Count == 3 && arguments[0] != Dimension.Dimensionless =>
                $"The test in 'if' has to be a comparison, and this one is {Name(arguments[0])}.",

            "if" => $"The two outcomes of 'if' have to measure the same kind of thing, and these are "
                + $"{given}.",

            _ => $"'{name}' cannot be applied to {given}.",
        };
    }

    private static string Name(Dimension dimension) => dimension switch
    {
        Dimension.Dimensionless => "a plain number",
        Dimension.Length => "a length",
        Dimension.Angle => "an angle",
        Dimension.Area => "an area",
        Dimension.Volume => "a volume",
        Dimension.Mass => "a mass",
        Dimension.Density => "a density",
        Dimension.Time => "a duration",
        _ => "a quantity",
    };
}
