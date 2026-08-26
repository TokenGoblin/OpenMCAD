namespace OpenMCAD.Core.Documents;

/// <summary>What a quantity measures.</summary>
/// <remarks>
/// <para>
/// A closed enumeration rather than a vector of exponents over the seven base units. The general
/// form is the correct one for a physics library and the wrong one here: it makes every dimension
/// representable, including the thousands that no mechanical modeller will ever produce, and buys
/// that generality at the price of never being able to switch on a dimension or name one in a
/// diagnostic. What a CAD document actually holds is this list, and when it needs another the
/// honest change is to add a member.
/// </para>
/// <para>
/// Storage is always SI base — metres, radians, kilograms, seconds — with conversion happening only
/// at the input and display boundary (§5.5). That is not a detail of the units layer; it is why a
/// document authored in inches and one authored in millimetres compare equal when they describe the
/// same part.
/// </para>
/// </remarks>
public enum Dimension
{
    /// <summary>A pure number: a count, a ratio, a scale factor.</summary>
    /// <remarks>
    /// Deliberately first, so it is the value a zeroed <see cref="Quantity"/> carries. A default
    /// quantity is then the dimensionless zero, which is the only default that is not a lie —
    /// a default of "length" would claim that <c>default</c> is a distance of zero metres.
    /// </remarks>
    Dimensionless = 0,

    /// <summary>A distance. Stored in metres.</summary>
    Length,

    /// <summary>An angle. Stored in radians.</summary>
    Angle,

    /// <summary>An area. Stored in square metres.</summary>
    Area,

    /// <summary>A volume. Stored in cubic metres.</summary>
    Volume,

    /// <summary>A mass. Stored in kilograms.</summary>
    Mass,

    /// <summary>A density. Stored in kilograms per cubic metre.</summary>
    Density,

    /// <summary>A duration. Stored in seconds.</summary>
    Time,
}
