using System.Collections.Immutable;
using System.Globalization;

using OpenMCAD.Core.Documents;

namespace OpenMCAD.Core.Expressions;

/// <summary>
/// Turns what a user typed into an expression, or into a complaint they can act on.
/// </summary>
/// <remarks>
/// <para>
/// Recursive descent with precedence climbing. The grammar is small enough that a hand-written
/// parser is shorter than the generator's configuration, and it can produce error messages worded
/// for a person — which a generated one cannot, and which is most of the value here. An expression
/// box is somewhere people make typing mistakes constantly, so the quality of the complaint is the
/// feature.
/// </para>
/// <para>
/// <b>A bare number is a plain number, not a length.</b> Typing <c>Length + 5</c> is refused rather
/// than read as five millimetres. Some modellers do adopt the document's units there, and it makes
/// the common case shorter at the cost of making the expression mean different things in different
/// documents — the same formula, pasted into a part authored in inches, silently becoming a
/// different size. The refusal says what to write instead, which is a smaller cost and a
/// reversible decision.
/// </para>
/// </remarks>
public static class ExpressionParser
{
    /// <summary>Parses an expression and checks that it means something.</summary>
    /// <param name="text">What the user typed.</param>
    /// <param name="dimensionOf">
    /// What a referenced parameter measures, for the type check. Returning null means there is no
    /// such parameter. Pass null to skip the check, which leaves
    /// <see cref="ParsedExpression.Dimension"/> unset — useful for a caller that only wants the
    /// shape, such as one collecting references to build the dependency graph (P3-T16).
    /// </param>
    /// <returns>The expression, or what is wrong with it.</returns>
    public static ParsedExpression Parse(
        string text, Func<Expression.Reference, Dimension?>? dimensionOf = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        Parser parser = new(text);
        Expression? root = parser.ParseExpression();

        if (root is not null && !parser.AtEnd)
        {
            parser.FailAtLeftover();
            root = null;
        }

        ImmutableArray<ExpressionError> errors = parser.Errors;

        if (root is null || !errors.IsEmpty)
        {
            return new ParsedExpression(root, errors, null);
        }

        if (dimensionOf is null)
        {
            return new ParsedExpression(root, [], null);
        }

        // The type check is a separate pass over a complete tree, which is what makes §5.5's
        // "caught before evaluation" achievable: no value is computed, no parameter is read, and
        // the answer is available the moment the text is syntactically whole.
        return ExpressionChecker.Check(root, dimensionOf);
    }

    /// <summary>Collects every parameter an expression refers to.</summary>
    /// <param name="expression">The expression.</param>
    /// <returns>The references, in the order they appear, without duplicates.</returns>
    /// <remarks>
    /// What P3-T16 needs to fold parameters into the rebuild graph, and what a rename needs in
    /// order to know what to rewrite.
    /// </remarks>
    public static ImmutableArray<Expression.Reference> ReferencesIn(Expression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        List<Expression.Reference> found = [];
        HashSet<(string?, string)> seen = [];

        Collect(expression, found, seen);

        return [.. found];
    }

    private static void Collect(
        Expression expression,
        List<Expression.Reference> found,
        HashSet<(string?, string)> seen)
    {
        switch (expression)
        {
            case Expression.Reference reference:
                if (seen.Add((reference.Document, reference.Name)))
                {
                    found.Add(reference);
                }

                break;

            case Expression.Unary unary:
                Collect(unary.Operand, found, seen);
                break;

            case Expression.Binary binary:
                Collect(binary.Left, found, seen);
                Collect(binary.Right, found, seen);
                break;

            case Expression.Invocation call:
                foreach (Expression argument in call.Arguments)
                {
                    Collect(argument, found, seen);
                }

                break;

            default:
                break;
        }
    }

    /// <summary>Walks the text once, left to right.</summary>
    private sealed class Parser(string text)
    {
        private readonly ImmutableArray<ExpressionError>.Builder _errors =
            ImmutableArray.CreateBuilder<ExpressionError>();

        private int _at;

        public ImmutableArray<ExpressionError> Errors => _errors.ToImmutable();

        public bool AtEnd
        {
            get
            {
                SkipSpace();
                return _at >= text.Length;
            }
        }

        public void Fail(string message, int? position = null, int length = 1)
            => _errors.Add(new ExpressionError(message, position ?? _at, length));

        /// <summary>Complains about text after a complete expression.</summary>
        /// <remarks>
        /// Two different mistakes end up here and they deserve different words. A character that
        /// could never appear in an expression is best named outright -- the user has typed
        /// something they did not mean to. Something that could start an expression means they have
        /// written two where one was expected, most often by leaving out an operator.
        /// </remarks>
        public void FailAtLeftover()
        {
            SkipSpace();

            char next = _at < text.Length ? text[_at] : ' ';

            Fail(char.IsLetterOrDigit(next) || next is '(' or '_' or '.'
                ? "There is something left over after the end of this expression. An operator may "
                    + "be missing."
                : $"'{next}' does not belong here.");
        }

        /// <summary>Comparisons, which bind least tightly of all.</summary>
        public Expression? ParseExpression()
        {
            Expression? left = ParseSum();

            while (left is not null && TryTakeComparison(out string op, out int at))
            {
                Expression? right = ParseSum();

                if (right is null)
                {
                    return null;
                }

                left = new Expression.Binary(at, op, left, right);
            }

            return left;
        }

        private Expression? ParseSum()
        {
            Expression? left = ParseProduct();

            while (left is not null && TryTakeOneOf("+-", out char op, out int at))
            {
                Expression? right = ParseProduct();

                if (right is null)
                {
                    return null;
                }

                left = new Expression.Binary(at, op.ToString(), left, right);
            }

            return left;
        }

        private Expression? ParseProduct()
        {
            Expression? left = ParseUnary();

            while (left is not null && TryTakeOneOf("*/", out char op, out int at))
            {
                Expression? right = ParseUnary();

                if (right is null)
                {
                    return null;
                }

                left = new Expression.Binary(at, op.ToString(), left, right);
            }

            return left;
        }

        private Expression? ParseUnary()
        {
            SkipSpace();

            if (_at < text.Length && (text[_at] == '-' || text[_at] == '+'))
            {
                int at = _at;
                char op = text[_at++];

                Expression? operand = ParseUnary();

                return operand is null ? null : new Expression.Unary(at, op.ToString(), operand);
            }

            return ParsePrimary();
        }

        private Expression? ParsePrimary()
        {
            SkipSpace();

            if (_at >= text.Length)
            {
                Fail(text.Length == 0
                    ? "This expression is empty."
                    : "This expression stops before it is finished.");

                return null;
            }

            char c = text[_at];

            if (c == '(')
            {
                int open = _at++;
                Expression? inner = ParseExpression();

                if (inner is null)
                {
                    return null;
                }

                SkipSpace();

                if (_at >= text.Length || text[_at] != ')')
                {
                    Fail("This bracket is never closed.", open);
                    return null;
                }

                _at++;
                return inner;
            }

            if (char.IsAsciiDigit(c) || c == '.')
            {
                return ParseNumber();
            }

            if (char.IsLetter(c) || c == '_')
            {
                return ParseNameOrCall();
            }

            Fail($"'{c}' does not belong here.");
            return null;
        }

        private Expression.Literal? ParseNumber()
        {
            int start = _at;

            while (_at < text.Length && char.IsAsciiDigit(text[_at]))
            {
                _at++;
            }

            if (_at < text.Length && text[_at] == '.')
            {
                _at++;

                while (_at < text.Length && char.IsAsciiDigit(text[_at]))
                {
                    _at++;
                }
            }

            // An exponent, but only when it is really one: the 'e' in '2e' is the start of a unit
            // or a mistake, not an exponent with a missing power.
            if (_at < text.Length && (text[_at] == 'e' || text[_at] == 'E'))
            {
                int mark = _at;
                int after = _at + 1;

                if (after < text.Length && (text[after] == '+' || text[after] == '-'))
                {
                    after++;
                }

                if (after < text.Length && char.IsAsciiDigit(text[after]))
                {
                    _at = after;

                    while (_at < text.Length && char.IsAsciiDigit(text[_at]))
                    {
                        _at++;
                    }
                }
                else
                {
                    _at = mark;
                }
            }

            if (!double.TryParse(
                text.AsSpan(start, _at - start),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double value))
            {
                Fail($"'{text[start.._at]}' is not a number.", start, _at - start);
                return null;
            }

            // A unit written straight after the digits, with no space: 25.4mm, 45deg, 1in.
            int unitStart = _at;

            while (_at < text.Length && (char.IsLetter(text[_at]) || char.IsAsciiDigit(text[_at])))
            {
                _at++;
            }

            if (unitStart == _at)
            {
                return new Expression.Literal(start, Quantity.Number(value));
            }

            string symbol = text[unitStart.._at];
            Unit? unit = Unit.Find(symbol);

            if (unit is null)
            {
                Fail($"'{symbol}' is not a unit this build knows.", unitStart, _at - unitStart);
                return null;
            }

            return new Expression.Literal(start, unit.Of(value));
        }

        private Expression? ParseNameOrCall()
        {
            int start = _at;
            string first = TakeIdentifier();

            SkipSpace();

            // A colon means the name before it was a document: Chassis:Width.
            if (_at < text.Length && text[_at] == ':')
            {
                _at++;
                SkipSpace();

                if (_at >= text.Length || !(char.IsLetter(text[_at]) || text[_at] == '_'))
                {
                    Fail($"'{first}:' has no parameter name after it.", start, _at - start);
                    return null;
                }

                return new Expression.Reference(start, first, TakeIdentifier());
            }

            if (_at >= text.Length || text[_at] != '(')
            {
                return new Expression.Reference(start, null, first);
            }

            _at++;

            ImmutableArray<Expression>.Builder arguments =
                ImmutableArray.CreateBuilder<Expression>();

            SkipSpace();

            if (_at < text.Length && text[_at] == ')')
            {
                _at++;
                return new Expression.Invocation(start, first, arguments.ToImmutable());
            }

            while (true)
            {
                Expression? argument = ParseExpression();

                if (argument is null)
                {
                    return null;
                }

                arguments.Add(argument);
                SkipSpace();

                if (_at < text.Length && text[_at] == ',')
                {
                    _at++;
                    continue;
                }

                if (_at < text.Length && text[_at] == ')')
                {
                    _at++;
                    return new Expression.Invocation(start, first, arguments.ToImmutable());
                }

                Fail($"'{first}(' is never closed.", start, first.Length + 1);
                return null;
            }
        }

        private string TakeIdentifier()
        {
            int start = _at;

            while (_at < text.Length && (char.IsLetterOrDigit(text[_at]) || text[_at] == '_'))
            {
                _at++;
            }

            return text[start.._at];
        }

        private bool TryTakeComparison(out string op, out int at)
        {
            SkipSpace();

            op = string.Empty;
            at = _at;

            if (_at >= text.Length)
            {
                return false;
            }

            // Two characters first, so that '<=' is never read as '<' followed by a stray '='.
            if (_at + 1 < text.Length)
            {
                string two = text.Substring(_at, 2);

                if (two is "<=" or ">=" or "==" or "!=")
                {
                    op = two;
                    _at += 2;

                    return true;
                }
            }

            if (text[_at] is '<' or '>')
            {
                op = text[_at].ToString();
                _at++;

                return true;
            }

            return false;
        }

        private bool TryTakeOneOf(string operators, out char op, out int at)
        {
            SkipSpace();

            op = default;
            at = _at;

            if (_at >= text.Length || !operators.Contains(text[_at], StringComparison.Ordinal))
            {
                return false;
            }

            op = text[_at];
            _at++;

            return true;
        }

        private void SkipSpace()
        {
            while (_at < text.Length && char.IsWhiteSpace(text[_at]))
            {
                _at++;
            }
        }
    }
}
