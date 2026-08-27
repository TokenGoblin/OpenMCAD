namespace OpenMCAD.Core.Documents;

/// <summary>
/// A value a feature holds that is not a dimension.
/// </summary>
/// <remarks>
/// <para>
/// P3-T21. A feature's dimensions live in <see cref="Feature.Parameters"/>, because a dimension is
/// a <see cref="Quantity"/> that can be driven by an expression and takes part in the parameter
/// graph. Everything else a feature needs to be told — which way to go, how many instances, whether
/// to merge the result, which end condition to use — is none of those things, and forcing it into a
/// quantity would mean pretending a boolean has units.
/// </para>
/// <para>
/// Deliberately small. These are the kinds a property manager can render, a script can set, and the
/// codec can write without knowing what feature they belong to. Anything more elaborate is a
/// selection, which is an entity reference and is stored as one.
/// </para>
/// </remarks>
public abstract record FeatureValue
{
    /// <summary>Gets a short description of what kind of value this is, for messages.</summary>
    public abstract string Kind { get; }
}

/// <summary>A dimension.</summary>
/// <param name="Value">The quantity.</param>
/// <remarks>
/// Not how a feature stores its dimensions — those are parameters, so they can carry an expression.
/// This exists so a schema can declare what a dimension's default is.
/// </remarks>
public sealed record QuantityValue(Quantity Value) : FeatureValue
{
    /// <inheritdoc/>
    public override string Kind => "a dimension";

    /// <inheritdoc/>
    public override string ToString() => Value.ToString();
}

/// <summary>A count.</summary>
/// <param name="Value">The number.</param>
/// <remarks>
/// Whole numbers only, and separate from <see cref="QuantityValue"/> on purpose: the number of
/// instances in a pattern is not a length, has no units, and rounding it is not a display choice.
/// </remarks>
public sealed record NumberValue(long Value) : FeatureValue
{
    /// <inheritdoc/>
    public override string Kind => "a whole number";

    /// <inheritdoc/>
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A switch.</summary>
/// <param name="Value">Whether it is on.</param>
public sealed record FlagValue(bool Value) : FeatureValue
{
    /// <inheritdoc/>
    public override string Kind => "on or off";

    /// <inheritdoc/>
    public override string ToString() => Value ? "on" : "off";
}

/// <summary>Text.</summary>
/// <param name="Value">The text.</param>
public sealed record TextValue(string Value) : FeatureValue
{
    /// <inheritdoc/>
    public override string Kind => "text";

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>One of a fixed set of options.</summary>
/// <param name="Value">Which one, by its stable name.</param>
/// <remarks>
/// Stored by name rather than by ordinal. An ordinal is a number whose meaning depends on the order
/// the options happened to be declared in, so inserting an option in the middle silently changes
/// what every file already written means.
/// </remarks>
public sealed record ChoiceValue(string Value) : FeatureValue
{
    /// <inheritdoc/>
    public override string Kind => "one of a set of options";

    /// <inheritdoc/>
    public override string ToString() => Value;
}
