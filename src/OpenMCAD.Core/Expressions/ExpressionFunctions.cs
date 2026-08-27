using System.Collections.Immutable;

using OpenMCAD.Core.Documents;

namespace OpenMCAD.Core.Expressions;

/// <summary>
/// The functions an expression may call, and what each does to a dimension.
/// </summary>
/// <remarks>
/// <para>
/// Each function declares its dimension rule and its arithmetic in one place, deliberately. They
/// have to agree — a type check that says <c>sqrt</c> of an area is a length, beside an evaluation
/// that returns an area, is a bug that only shows up in the value — and keeping them apart is how
/// they drift.
/// </para>
/// <para>
/// The rules are stricter than a calculator's, in the two places where being permissive would
/// silently do the wrong thing. <c>sin</c> takes an angle rather than any number, so
/// <c>sin(Length)</c> is refused instead of interpreting a length as radians. And <c>round</c>,
/// <c>floor</c> and <c>ceil</c> take a plain number only: rounding is done in whatever unit the
/// value is stored in, which is metres, so <c>round(Length)</c> would quietly round a part to the
/// nearest metre. Somebody wanting the nearest millimetre writes <c>round(L / 1mm) * 1mm</c>, which
/// says what it does.
/// </para>
/// </remarks>
internal static class ExpressionFunctions
{
    private static readonly ImmutableDictionary<string, FunctionDefinition> Known =
        Build().ToImmutableDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Finds a function by the name that was typed.</summary>
    /// <param name="name">The name, compared without regard to case.</param>
    /// <returns>The function, or null if there is no such function.</returns>
    public static FunctionDefinition? Find(string name)
        => Known.TryGetValue(name, out FunctionDefinition? found) ? found : null;

    /// <summary>Gets every function's name, for a diagnostic that lists them.</summary>
    public static ImmutableArray<string> Names => [.. Known.Keys.OrderBy(n => n, StringComparer.Ordinal)];

    private static ImmutableArray<FunctionDefinition> Build() =>
    [
        new("abs", 1, 1,
            d => d[0],
            a => a[0] with { Value = System.Math.Abs(a[0].Value) }),

        new("min", 2, 2,
            d => Dimensions.Add(d[0], d[1]),
            a => a[0].Value <= a[1].Value ? a[0] : a[1]),

        new("max", 2, 2,
            d => Dimensions.Add(d[0], d[1]),
            a => a[0].Value >= a[1].Value ? a[0] : a[1]),

        new("sqrt", 1, 1,
            d => Dimensions.SquareRoot(d[0]),
            a => a[0].SquareRoot()),

        // Trigonometry takes an angle and gives a plain number. Refusing a plain number here is
        // the point: sin(0.5) reads as "the sine of a half turn?" to a user and as radians to a
        // calculator, and the two disagree.
        new("sin", 1, 1, Trigonometric, a => Quantity.Number(System.Math.Sin(a[0].Value))),
        new("cos", 1, 1, Trigonometric, a => Quantity.Number(System.Math.Cos(a[0].Value))),
        new("tan", 1, 1, Trigonometric, a => Quantity.Number(System.Math.Tan(a[0].Value))),

        new("asin", 1, 1, InverseTrigonometric, a => Quantity.Radians(System.Math.Asin(a[0].Value))),
        new("acos", 1, 1, InverseTrigonometric, a => Quantity.Radians(System.Math.Acos(a[0].Value))),
        new("atan", 1, 1, InverseTrigonometric, a => Quantity.Radians(System.Math.Atan(a[0].Value))),

        // Two arguments of any one dimension, because it is a ratio: atan2(rise, run) is the same
        // angle whether both are millimetres or both are inches.
        new("atan2", 2, 2,
            d => Dimensions.Add(d[0], d[1]) is null ? null : Dimension.Angle,
            a => Quantity.Radians(System.Math.Atan2(a[0].Value, a[1].Value))),

        new("floor", 1, 1, PlainNumber, a => Quantity.Number(System.Math.Floor(a[0].Value))),
        new("ceil", 1, 1, PlainNumber, a => Quantity.Number(System.Math.Ceiling(a[0].Value))),

        // Away from zero at a half, which is what a person means by rounding and what every
        // engineering drawing assumes. Banker's rounding is the .NET default and would surprise.
        new("round", 1, 1, PlainNumber,
            a => Quantity.Number(System.Math.Round(a[0].Value, MidpointRounding.AwayFromZero))),

        // A function rather than syntax: it needs no precedence rules and reads like every other
        // call. The two outcomes must measure the same kind of thing, or the expression's own
        // dimension would depend on a value and could not be checked before evaluating.
        new("if", 3, 3,
            d => d[0] != Dimension.Dimensionless ? null : Dimensions.Add(d[1], d[2]),
            a => a[0].Value != 0 ? a[1] : a[2]),
    ];

    private static Dimension? Trigonometric(ImmutableArray<Dimension> arguments)
        => arguments[0] == Dimension.Angle ? Dimension.Dimensionless : null;

    private static Dimension? InverseTrigonometric(ImmutableArray<Dimension> arguments)
        => arguments[0] == Dimension.Dimensionless ? Dimension.Angle : null;

    private static Dimension? PlainNumber(ImmutableArray<Dimension> arguments)
        => arguments[0] == Dimension.Dimensionless ? Dimension.Dimensionless : null;
}

/// <summary>One function: what it is called, what it accepts, and what it does.</summary>
/// <param name="Name">Its name.</param>
/// <param name="MinimumArguments">The fewest arguments it takes.</param>
/// <param name="MaximumArguments">The most.</param>
/// <param name="ResultDimension">
/// What it produces, given what it was passed, or null if that combination has no meaning.
/// </param>
/// <param name="Apply">What it computes. Only called once the dimensions have been agreed.</param>
internal sealed record FunctionDefinition(
    string Name,
    int MinimumArguments,
    int MaximumArguments,
    Func<ImmutableArray<Dimension>, Dimension?> ResultDimension,
    Func<ImmutableArray<Quantity>, Quantity> Apply);
