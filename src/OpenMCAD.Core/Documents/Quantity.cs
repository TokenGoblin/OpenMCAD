using System.Globalization;

namespace OpenMCAD.Core.Documents;

/// <summary>
/// A number that knows what it measures.
/// </summary>
/// <param name="Value">The magnitude, in the SI base unit for <paramref name="Dimension"/>.</param>
/// <param name="Dimension">What is being measured.</param>
/// <remarks>
/// <para>
/// <b>Why a parameter cannot just hold a double.</b> A bare number carries no unit, so nothing can
/// tell whether 25.4 is a length in millimetres, a length in metres, or an angle. That does not
/// merely make errors possible; it makes them undetectable. <c>4 mm + 3 deg</c> is nonsense that a
/// double addition performs cheerfully, and the result is a model that is wrong in a way no test of
/// the arithmetic can find (§5.5).
/// </para>
/// <para>
/// <b>The value is always SI base.</b> Millimetres and inches exist at the edges of the system —
/// where a person types a number and where one is shown back to them — and nowhere in between.
/// A document authored in inches and one authored in millimetres therefore hold identical values
/// when they describe the same part, which is what lets them be compared, cached and diffed at all.
/// </para>
/// <para>
/// <b>What is deliberately not here.</b> Arithmetic. Adding two quantities, multiplying a length by
/// a length to get an area, and rejecting the combinations that mean nothing are the dimension
/// algebra of P3-T14; parsing <c>25.4mm</c> is P3-T15. This type is the value those will operate on,
/// defined now because building <see cref="Parameter"/> on a double and converting later would mean
/// every caller written in between is written against the wrong type.
/// </para>
/// </remarks>
public readonly record struct Quantity(double Value, Dimension Dimension)
{
    /// <summary>Gets the dimensionless zero.</summary>
    public static Quantity Zero => default;

    /// <summary>Gets a value indicating whether the magnitude is finite.</summary>
    /// <remarks>
    /// Worth asking explicitly. An expression can divide by a parameter that happens to be zero,
    /// and the infinity that results propagates through a rebuild without complaint until it
    /// reaches the kernel, which reports a failure naming the operation rather than the parameter.
    /// </remarks>
    public bool IsFinite => double.IsFinite(Value);

    /// <summary>A length, in metres.</summary>
    /// <param name="metres">The distance.</param>
    /// <returns>The quantity.</returns>
    public static Quantity Metres(double metres) => new(metres, Dimension.Length);

    /// <summary>An angle, in radians.</summary>
    /// <param name="radians">The angle.</param>
    /// <returns>The quantity.</returns>
    public static Quantity Radians(double radians) => new(radians, Dimension.Angle);

    /// <summary>A pure number.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The quantity.</returns>
    public static Quantity Number(double value) => new(value, Dimension.Dimensionless);

    /// <summary>Whether this measures the same kind of thing as another.</summary>
    /// <param name="other">The quantity to compare with.</param>
    /// <returns>Whether the two dimensions agree.</returns>
    public bool IsCompatibleWith(Quantity other) => Dimension == other.Dimension;

    /// <summary>Adds two quantities of the same kind.</summary>
    /// <param name="left">The first.</param>
    /// <param name="right">The second.</param>
    /// <returns>The sum.</returns>
    /// <exception cref="DimensionException">They measure different kinds of thing.</exception>
    public static Quantity operator +(Quantity left, Quantity right)
        => new(left.Value + right.Value, Require(
            Dimensions.Add(left.Dimension, right.Dimension), "added to", left, right));

    /// <summary>Subtracts one quantity from another of the same kind.</summary>
    /// <param name="left">The first.</param>
    /// <param name="right">The second.</param>
    /// <returns>The difference.</returns>
    /// <exception cref="DimensionException">They measure different kinds of thing.</exception>
    public static Quantity operator -(Quantity left, Quantity right)
        => new(left.Value - right.Value, Require(
            Dimensions.Add(left.Dimension, right.Dimension), "subtracted from", right, left));

    /// <summary>Negates a quantity.</summary>
    /// <param name="value">The quantity.</param>
    /// <returns>The negation, measuring the same kind of thing.</returns>
    public static Quantity operator -(Quantity value) => value with { Value = -value.Value };

    /// <summary>Multiplies two quantities.</summary>
    /// <param name="left">The first.</param>
    /// <param name="right">The second.</param>
    /// <returns>The product, which may measure something else entirely.</returns>
    /// <exception cref="DimensionException">The product has no dimension in this system.</exception>
    public static Quantity operator *(Quantity left, Quantity right)
        => new(left.Value * right.Value, Require(
            Dimensions.Multiply(left.Dimension, right.Dimension), "multiplied by", left, right));

    /// <summary>Divides one quantity by another.</summary>
    /// <param name="left">The quantity being divided.</param>
    /// <param name="right">The quantity dividing it.</param>
    /// <returns>The quotient.</returns>
    /// <exception cref="DimensionException">The quotient has no dimension in this system.</exception>
    public static Quantity operator /(Quantity left, Quantity right)
        => new(left.Value / right.Value, Require(
            Dimensions.Divide(left.Dimension, right.Dimension), "divided by", left, right));

    /// <summary>Adds two quantities of the same kind.</summary>
    /// <param name="left">The first.</param>
    /// <param name="right">The second.</param>
    /// <returns>The sum.</returns>
    public static Quantity Add(Quantity left, Quantity right) => left + right;

    /// <summary>Subtracts one quantity from another of the same kind.</summary>
    /// <param name="left">The first.</param>
    /// <param name="right">The second.</param>
    /// <returns>The difference.</returns>
    public static Quantity Subtract(Quantity left, Quantity right) => left - right;

    /// <summary>Multiplies two quantities.</summary>
    /// <param name="left">The first.</param>
    /// <param name="right">The second.</param>
    /// <returns>The product.</returns>
    public static Quantity Multiply(Quantity left, Quantity right) => left * right;

    /// <summary>Divides one quantity by another.</summary>
    /// <param name="left">The quantity being divided.</param>
    /// <param name="right">The quantity dividing it.</param>
    /// <returns>The quotient.</returns>
    public static Quantity Divide(Quantity left, Quantity right) => left / right;

    /// <summary>Negates a quantity.</summary>
    /// <param name="value">The quantity.</param>
    /// <returns>The negation.</returns>
    public static Quantity Negate(Quantity value) => -value;

    /// <summary>Compares two quantities of the same kind.</summary>
    /// <param name="other">The quantity to compare with.</param>
    /// <returns>Less than, equal to, or greater than zero.</returns>
    /// <exception cref="DimensionException">They measure different kinds of thing.</exception>
    /// <remarks>
    /// Refused across dimensions rather than answered false. Asking whether a length exceeds an
    /// angle has no answer, and false is an answer.
    /// </remarks>
    public int CompareTo(Quantity other)
        => Dimensions.CanCompare(Dimension, other.Dimension)
            ? Value.CompareTo(other.Value)
            : throw new DimensionException("compared with", Dimension, other.Dimension);

    /// <summary>The square root of a quantity.</summary>
    /// <returns>The root.</returns>
    /// <exception cref="DimensionException">There is no such dimension in this system.</exception>
    public Quantity SquareRoot()
        => Dimensions.SquareRoot(Dimension) is { } dimension
            ? new Quantity(System.Math.Sqrt(Value), dimension)
            : throw new DimensionException(
                $"There is no dimension for the square root of {Dimension.ToString().ToLowerInvariant()}.");

    /// <summary>Checks that a dimension operation had an answer.</summary>
    private static Dimension Require(
        Dimension? result, string operation, Quantity left, Quantity right)
        => result ?? throw new DimensionException(operation, left.Dimension, right.Dimension);

    /// <inheritdoc />
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Value:0.######} {Dimension}");
}
