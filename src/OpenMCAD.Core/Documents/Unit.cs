using System.Collections.Immutable;
using System.Globalization;

namespace OpenMCAD.Core.Documents;

/// <summary>
/// A unit a person types or reads, and how it relates to the SI base the document stores.
/// </summary>
/// <param name="Symbol">How it is written: <c>mm</c>, <c>deg</c>, <c>in</c>.</param>
/// <param name="Dimension">What it measures.</param>
/// <param name="PerBase">
/// How many of these make one of the base unit — a thousand millimetres to the metre. Expressed
/// this way round rather than as a factor to SI because it is the number a person can check by
/// eye, and a unit table nobody can proofread is a unit table with a wrong entry in it.
/// </param>
/// <remarks>
/// <para>
/// §5.5: storage is always SI base, and conversion happens only at the input and display boundary.
/// This type is that boundary and has no other job. Nothing inside the document ever holds
/// millimetres, so nothing inside it can be wrong about which unit a number is in.
/// </para>
/// <para>
/// <b>Round-tripping must not perturb the value.</b> A user who opens a dialog showing 25.4 mm and
/// closes it without typing must not have edited their model. Converting out and back is therefore
/// done by dividing and multiplying by the same constant rather than by a precomputed reciprocal:
/// <c>x * (1/1000)</c> and <c>x / 1000</c> do not agree in floating point, and the second is the
/// one that comes back to where it started.
/// </para>
/// <para>
/// <b>Angles are the exception, and no implementation can fix it.</b> The conversion is a factor of
/// π, which has no exact binary representation, so a degree value can lose an ulp on the way out
/// and back. What matters is that it settles: a second round trip lands exactly where the first
/// did, so a value cannot drift a little further every time a dialog is opened. That is the
/// property the tests assert, because it is the one that protects the model.
/// </para>
/// </remarks>
public sealed record Unit(string Symbol, Dimension Dimension, double PerBase)
{
    /// <summary>Metres, the base unit of length.</summary>
    public static Unit Metres { get; } = new("m", Dimension.Length, 1);

    /// <summary>Millimetres, which most mechanical work is done in.</summary>
    public static Unit Millimetres { get; } = new("mm", Dimension.Length, 1_000);

    /// <summary>Centimetres.</summary>
    public static Unit Centimetres { get; } = new("cm", Dimension.Length, 100);

    /// <summary>Micrometres, for tolerances and surface finish.</summary>
    public static Unit Micrometres { get; } = new("um", Dimension.Length, 1_000_000);

    /// <summary>Inches, defined exactly as 25.4 mm since 1959.</summary>
    public static Unit Inches { get; } = new("in", Dimension.Length, 1 / 0.0254);

    /// <summary>Feet.</summary>
    public static Unit Feet { get; } = new("ft", Dimension.Length, 1 / 0.3048);

    /// <summary>Thousandths of an inch, as used for clearances.</summary>
    public static Unit Thou { get; } = new("thou", Dimension.Length, 1 / 0.0000254);

    /// <summary>Radians, the base unit of angle.</summary>
    public static Unit Radians { get; } = new("rad", Dimension.Angle, 1);

    /// <summary>Degrees, which is what people actually type.</summary>
    public static Unit Degrees { get; } = new("deg", Dimension.Angle, 180 / System.Math.PI);

    /// <summary>Square metres.</summary>
    public static Unit SquareMetres { get; } = new("m2", Dimension.Area, 1);

    /// <summary>Square millimetres.</summary>
    public static Unit SquareMillimetres { get; } = new("mm2", Dimension.Area, 1_000_000);

    /// <summary>Cubic metres.</summary>
    public static Unit CubicMetres { get; } = new("m3", Dimension.Volume, 1);

    /// <summary>Cubic millimetres.</summary>
    public static Unit CubicMillimetres { get; } = new("mm3", Dimension.Volume, 1_000_000_000);

    /// <summary>Kilograms, the base unit of mass.</summary>
    public static Unit Kilograms { get; } = new("kg", Dimension.Mass, 1);

    /// <summary>Grams.</summary>
    public static Unit Grams { get; } = new("g", Dimension.Mass, 1_000);

    /// <summary>Pounds.</summary>
    public static Unit Pounds { get; } = new("lb", Dimension.Mass, 1 / 0.45359237);

    /// <summary>Kilograms per cubic metre.</summary>
    public static Unit KilogramsPerCubicMetre { get; } = new("kg/m3", Dimension.Density, 1);

    /// <summary>Grams per cubic centimetre, which is how material tables are written.</summary>
    public static Unit GramsPerCubicCentimetre { get; } = new("g/cm3", Dimension.Density, 0.001);

    /// <summary>Seconds.</summary>
    public static Unit Seconds { get; } = new("s", Dimension.Time, 1);

    /// <summary>A plain number, with no unit at all.</summary>
    public static Unit None { get; } = new(string.Empty, Dimension.Dimensionless, 1);

    /// <summary>Gets every unit this build knows, in the order they are searched.</summary>
    public static ImmutableArray<Unit> All { get; } =
    [
        Metres, Millimetres, Centimetres, Micrometres, Inches, Feet, Thou,
        Radians, Degrees,
        SquareMetres, SquareMillimetres,
        CubicMetres, CubicMillimetres,
        Kilograms, Grams, Pounds,
        KilogramsPerCubicMetre, GramsPerCubicCentimetre,
        Seconds,
        None,
    ];

    /// <summary>Finds a unit by the symbol someone typed.</summary>
    /// <param name="symbol">The symbol, compared without regard to case.</param>
    /// <returns>The unit, or null if this build has no such unit.</returns>
    /// <remarks>
    /// Case-insensitive, which is a small lie worth telling: <c>MM</c> and <c>mm</c> are the same
    /// unit to everyone who is not a metrologist, and refusing the first would be pedantry the user
    /// has to work around. The one real casualty is that <c>M</c> could be metres or a prefix
    /// nobody uses here, and metres wins.
    /// </remarks>
    public static Unit? Find(string? symbol)
    {
        if (symbol is null)
        {
            return null;
        }

        foreach (Unit unit in All)
        {
            if (string.Equals(unit.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
            {
                return unit;
            }
        }

        return null;
    }

    /// <summary>Turns a number the user typed into a stored quantity.</summary>
    /// <param name="value">The number, in this unit.</param>
    /// <returns>The quantity, in SI base.</returns>
    public Quantity Of(double value) => new(value / PerBase, Dimension);

    /// <summary>Turns a stored quantity into the number to show.</summary>
    /// <param name="quantity">The quantity.</param>
    /// <returns>The number, in this unit.</returns>
    /// <exception cref="DimensionException">The quantity measures something else.</exception>
    public double From(Quantity quantity)
        => quantity.Dimension == Dimension
            ? quantity.Value * PerBase
            : throw new DimensionException(
                $"A quantity in {Dimensions.BaseUnitOf(quantity.Dimension)} cannot be shown in "
                + $"{(Symbol.Length == 0 ? "a plain number" : Symbol)}.");

    /// <summary>Writes a quantity for a person to read.</summary>
    /// <param name="quantity">The quantity.</param>
    /// <param name="decimals">How many decimal places, or null to write it exactly.</param>
    /// <returns>The text, with the unit symbol.</returns>
    /// <remarks>
    /// Exact by default. Rounding is a display choice, and a caller that has not made one should
    /// get the number rather than a decision made on its behalf — a default of two places would
    /// silently turn a 0.001 mm tolerance into nothing at all.
    /// </remarks>
    public string Format(Quantity quantity, int? decimals = null)
    {
        double shown = From(quantity);

        string number = decimals is { } places
            ? shown.ToString($"F{places}", CultureInfo.InvariantCulture)
            : shown.ToString("R", CultureInfo.InvariantCulture);

        return Symbol.Length == 0 ? number : $"{number} {Symbol}";
    }

    /// <inheritdoc />
    public override string ToString() => Symbol.Length == 0 ? "(none)" : Symbol;
}
