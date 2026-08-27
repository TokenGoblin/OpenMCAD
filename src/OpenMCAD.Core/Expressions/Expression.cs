using System.Collections.Immutable;

using OpenMCAD.Core.Documents;

namespace OpenMCAD.Core.Expressions;

/// <summary>
/// Something wrong with an expression, and where in the text it is.
/// </summary>
/// <param name="Message">
/// What is wrong, addressed to whoever typed it. Never mentions tokens, productions or the parser.
/// </param>
/// <param name="Position">
/// The character it starts at, counting from nought, so an editor can put a caret under it.
/// </param>
/// <param name="Length">How many characters it covers, so an editor can underline them.</param>
/// <remarks>
/// A value rather than an exception, because a half-typed expression is the normal state of an
/// expression box rather than an exceptional one. Throwing would make the common case expensive and
/// would force every caller into a try block to render a red squiggle.
/// </remarks>
public sealed record ExpressionError(string Message, int Position, int Length = 1)
{
    /// <inheritdoc />
    public override string ToString() => $"{Message} (at {Position})";
}

/// <summary>
/// A parsed expression: what it means, or what is wrong with it.
/// </summary>
/// <param name="Root">The expression, or null if it could not be parsed.</param>
/// <param name="Errors">Everything wrong with it, in the order it was found.</param>
/// <param name="Dimension">
/// What the expression evaluates to, when it type-checked. Null when it did not.
/// </param>
public sealed record ParsedExpression(
    Expression? Root,
    ImmutableArray<ExpressionError> Errors,
    Dimension? Dimension)
{
    /// <summary>Gets whether the expression is usable.</summary>
    public bool IsValid => Root is not null && Errors.IsEmpty;

    /// <summary>Gets the first thing wrong with it, which is the one to show.</summary>
    /// <remarks>
    /// Later errors are frequently consequences of the first — a mis-typed operator makes the rest
    /// of the line parse strangely — so leading with the first is nearly always leading with the
    /// cause. The rest are kept for a caller that wants to underline all of them.
    /// </remarks>
    public ExpressionError? FirstError => Errors.IsDefaultOrEmpty ? null : Errors[0];
}

/// <summary>
/// One node of an expression.
/// </summary>
/// <param name="Position">Where it starts in the text it was parsed from.</param>
/// <remarks>
/// <para>
/// A closed hierarchy: an expression is one of exactly these five things, and the compiler can
/// check that every walk over it handles all of them. §5.5 asks for arithmetic, functions,
/// conditionals, parameter references and cross-document references — conditionals are a function
/// here rather than syntax, because <c>if(a, b, c)</c> needs no precedence rules and reads the same
/// as every other call.
/// </para>
/// <para>
/// Positions are carried on every node so that an error can be reported where it is rather than
/// against the whole expression. "Something is wrong with this formula" is not a message anyone
/// can act on.
/// </para>
/// </remarks>
public abstract record Expression(int Position)
{
    /// <summary>A number, with whatever unit was written after it.</summary>
    /// <param name="Position">Where it starts.</param>
    /// <param name="Value">The quantity, already converted to SI base.</param>
    public sealed record Literal(int Position, Quantity Value) : Expression(Position);

    /// <summary>A parameter, in this document or another.</summary>
    /// <param name="Position">Where it starts.</param>
    /// <param name="Document">
    /// The other document's name, or null for a parameter of this one. §5.5's <c>Chassis:Width</c>.
    /// </param>
    /// <param name="Name">The parameter's name.</param>
    public sealed record Reference(int Position, string? Document, string Name)
        : Expression(Position)
    {
        /// <summary>Gets whether this points outside the document holding it.</summary>
        public bool IsCrossDocument => Document is not null;

        /// <inheritdoc />
        public override string ToString() => Document is null ? Name : $"{Document}:{Name}";
    }

    /// <summary>A sign in front of something.</summary>
    /// <param name="Position">Where it starts.</param>
    /// <param name="Operator">The operator, <c>-</c> or <c>+</c>.</param>
    /// <param name="Operand">What it applies to.</param>
    public sealed record Unary(int Position, string Operator, Expression Operand)
        : Expression(Position);

    /// <summary>Two things combined by an operator.</summary>
    /// <param name="Position">Where the operator is.</param>
    /// <param name="Operator">The operator.</param>
    /// <param name="Left">The left operand.</param>
    /// <param name="Right">The right operand.</param>
    public sealed record Binary(int Position, string Operator, Expression Left, Expression Right)
        : Expression(Position);

    /// <summary>A named function applied to arguments.</summary>
    /// <param name="Position">Where the name starts.</param>
    /// <param name="Function">Its name, compared without regard to case.</param>
    /// <param name="Arguments">What it was given.</param>
    public sealed record Invocation(
        int Position, string Function, ImmutableArray<Expression> Arguments)
        : Expression(Position);
}
