namespace OpenMCAD.Core.Documents;

/// <summary>
/// Thrown when an expression asks for something that has no meaning.
/// </summary>
/// <remarks>
/// Adding a length to an angle is not a value that happens to be wrong; it is a question with no
/// answer. §5.5 wants those caught before evaluation, so most of them are found by
/// <see cref="Dimensions"/> during a type check and never reach a value. This exists for the ones
/// that reach arithmetic anyway — a caller that skipped the check, or a plugin computing directly.
/// </remarks>
public sealed class DimensionException : InvalidOperationException
{
    /// <summary>Creates the exception for an operation between two dimensions.</summary>
    /// <param name="operation">What was attempted, in words the user would recognise.</param>
    /// <param name="left">The dimension on the left.</param>
    /// <param name="right">The dimension on the right.</param>
    public DimensionException(string operation, Dimension left, Dimension right)
        : base($"{Describe(left)} cannot be {operation} {Describe(right)}.")
    {
        Left = left;
        Right = right;
    }

    /// <summary>Creates the exception with a plain message.</summary>
    /// <param name="message">The message.</param>
    public DimensionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a plain message and an inner cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public DimensionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with nothing to say.</summary>
    public DimensionException()
        : base("These quantities measure different kinds of thing.")
    {
    }

    /// <summary>Gets the dimension on the left of the operation.</summary>
    public Dimension Left { get; }

    /// <summary>Gets the dimension on the right.</summary>
    public Dimension Right { get; }

    /// <summary>Names a dimension the way a person would.</summary>
    private static string Describe(Dimension dimension) => dimension switch
    {
        Dimension.Dimensionless => "A plain number",
        Dimension.Length => "A length",
        Dimension.Angle => "An angle",
        Dimension.Area => "An area",
        Dimension.Volume => "A volume",
        Dimension.Mass => "A mass",
        Dimension.Density => "A density",
        Dimension.Time => "A duration",
        _ => "A quantity",
    };
}

/// <summary>
/// What combining two dimensions produces, or that it produces nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dimensions only, never values.</b> §5.5 requires <c>4 mm + 3 deg</c> to be caught before
/// evaluation, and that is only possible if the check needs nothing but the dimensions — an
/// expression tree can then be type-checked as it is parsed, and the user is told about the
/// mistake while they are still looking at it rather than when a rebuild fails an hour later.
/// </para>
/// <para>
/// <b>A table rather than exponent arithmetic.</b> The general form — a vector of exponents over
/// the base units — is right for a physics library and wrong here. It makes every dimension
/// representable, including the thousands no mechanical modeller will produce, and in exchange it
/// can never name one in a diagnostic or switch on one. <see cref="Dimension"/> is the closed list
/// a CAD document actually holds, so this is a closed table over it, and a combination that falls
/// outside is rejected rather than invented.
/// </para>
/// <para>
/// <b>Angle is its own dimension, which physics would disagree with.</b> An angle in radians is
/// dimensionless in SI, so a strict treatment would let <c>4 mm + 3 deg</c> through as a number
/// plus a number. That is precisely the error §5.5 names, so angle is kept separate here and the
/// physics is the thing that gives way.
/// </para>
/// </remarks>
public static class Dimensions
{
    /// <summary>What adding or subtracting two dimensions produces.</summary>
    /// <param name="left">The dimension on the left.</param>
    /// <param name="right">The dimension on the right.</param>
    /// <returns>The result, or null if the two cannot be added.</returns>
    /// <remarks>
    /// Only like to like. This is the whole of the rule, and it is the one that catches the error
    /// §5.5 opens with.
    /// </remarks>
    public static Dimension? Add(Dimension left, Dimension right)
        => left == right ? left : null;

    /// <summary>What multiplying two dimensions produces.</summary>
    /// <param name="left">The dimension on the left.</param>
    /// <param name="right">The dimension on the right.</param>
    /// <returns>The result, or null if the two cannot be multiplied.</returns>
    public static Dimension? Multiply(Dimension left, Dimension right)
    {
        // Scaling by a plain number never changes what a thing measures, which is by far the
        // commonest case: Length * 2, Thickness * Count.
        if (left == Dimension.Dimensionless)
        {
            return right;
        }

        if (right == Dimension.Dimensionless)
        {
            return left;
        }

        return (left, right) switch
        {
            (Dimension.Length, Dimension.Length) => Dimension.Area,
            (Dimension.Length, Dimension.Area) or (Dimension.Area, Dimension.Length)
                => Dimension.Volume,
            (Dimension.Density, Dimension.Volume) or (Dimension.Volume, Dimension.Density)
                => Dimension.Mass,

            // Everything else falls outside the closed list. Length * Mass is a real physical
            // quantity and is not one a part document has any use for, so it is refused rather
            // than quietly collapsed to something that is nearly right.
            _ => null,
        };
    }

    /// <summary>What dividing one dimension by another produces.</summary>
    /// <param name="left">The dimension being divided.</param>
    /// <param name="right">The dimension dividing it.</param>
    /// <returns>The result, or null if the two cannot be divided.</returns>
    public static Dimension? Divide(Dimension left, Dimension right)
    {
        if (right == Dimension.Dimensionless)
        {
            return left;
        }

        // Like over like is a ratio, whatever the like was. This is how a scale factor or an
        // aspect ratio comes about, and it is the only route from a measured thing back to a
        // plain number without an explicit conversion.
        if (left == right)
        {
            return Dimension.Dimensionless;
        }

        return (left, right) switch
        {
            (Dimension.Area, Dimension.Length) => Dimension.Length,
            (Dimension.Volume, Dimension.Length) => Dimension.Area,
            (Dimension.Volume, Dimension.Area) => Dimension.Length,
            (Dimension.Mass, Dimension.Volume) => Dimension.Density,
            (Dimension.Mass, Dimension.Density) => Dimension.Volume,
            _ => null,
        };
    }

    /// <summary>What taking a square root produces.</summary>
    /// <param name="dimension">What is being rooted.</param>
    /// <returns>The result, or null if there is no such dimension.</returns>
    /// <remarks>
    /// The root of a volume is not in the list — it would be a length to the power of three
    /// halves — so it is refused. That is a real limitation of a closed table and the honest
    /// place to meet it is here, with a message, rather than in a result that is subtly wrong.
    /// </remarks>
    public static Dimension? SquareRoot(Dimension dimension) => dimension switch
    {
        Dimension.Dimensionless => Dimension.Dimensionless,
        Dimension.Area => Dimension.Length,
        _ => null,
    };

    /// <summary>Whether two dimensions can be compared with one another.</summary>
    /// <param name="left">The dimension on the left.</param>
    /// <param name="right">The dimension on the right.</param>
    /// <returns>Whether the comparison means anything.</returns>
    /// <remarks>
    /// The same rule as addition, and for the same reason: asking whether a length exceeds an
    /// angle has no answer, and returning false would be an answer.
    /// </remarks>
    public static bool CanCompare(Dimension left, Dimension right) => left == right;

    /// <summary>The SI unit a dimension is stored in.</summary>
    /// <param name="dimension">The dimension.</param>
    /// <returns>The symbol, for a diagnostic.</returns>
    /// <remarks>
    /// §5.5: storage is always SI base, and conversion happens only at the input and display
    /// boundary. This says what that base is, so a message can name it.
    /// </remarks>
    public static string BaseUnitOf(Dimension dimension) => dimension switch
    {
        Dimension.Length => "m",
        Dimension.Angle => "rad",
        Dimension.Area => "m²",
        Dimension.Volume => "m³",
        Dimension.Mass => "kg",
        Dimension.Density => "kg/m³",
        Dimension.Time => "s",
        _ => string.Empty,
    };
}
