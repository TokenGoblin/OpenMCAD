using System.Collections.Immutable;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;

namespace OpenMCAD.Modeling;

/// <summary>What sort of thing a feature's property holds.</summary>
public enum PropertyKind
{
    /// <summary>A dimension, stored as one of the feature's parameters.</summary>
    /// <remarks>
    /// The only kind that lives in <see cref="Feature.Parameters"/> rather than in its settings,
    /// because a dimension can be driven by an expression and takes part in the parameter graph.
    /// </remarks>
    Quantity,

    /// <summary>A count.</summary>
    Number,

    /// <summary>A switch.</summary>
    Flag,

    /// <summary>Text.</summary>
    Text,

    /// <summary>One of a fixed set of options.</summary>
    Choice,

    /// <summary>Geometry the user picks, stored as one of the feature's entity references.</summary>
    Selection,
}

/// <summary>
/// One thing a feature needs to be told, declared once.
/// </summary>
/// <param name="Name">Its stable name. Appears in files and in scripts, and must never change.</param>
/// <param name="Label">What a person is shown.</param>
/// <param name="Kind">What sort of value it holds.</param>
/// <param name="Group">Which section of the property manager it belongs in.</param>
/// <param name="Description">What it does, for a tooltip and for generated documentation.</param>
/// <param name="Dimension">For a quantity, what it measures.</param>
/// <param name="Choices">For a choice, the options, in the order they should be offered.</param>
/// <param name="Default">What it is when the user has said nothing.</param>
/// <param name="Minimum">For a quantity or a number, the smallest allowed value.</param>
/// <param name="Maximum">For a quantity or a number, the largest allowed value.</param>
/// <param name="Multiplicity">For a selection, how many entities it takes.</param>
/// <param name="VisibleWhen">
/// The property whose value decides whether this one applies at all, and the value that makes it
/// apply. Null when it always applies.
/// </param>
/// <remarks>
/// <para>
/// <b><see cref="Name"/> is a contract and <see cref="Label"/> is not.</b> The name is written into
/// every file and typed into every script, so changing it silently breaks both; the label is shown
/// to a person, is translated, and can change whenever the wording is wrong.
/// </para>
/// <para>
/// A property that is not visible is not merely hidden. An extrude's draft angle when draft is
/// switched off is not a value the user declined to give — it does not apply, and validation must
/// not demand it. That is why <see cref="VisibleWhen"/> is part of the declaration rather than a
/// hint for the UI: three different layers would otherwise each decide for themselves when a
/// property counts, and they would disagree.
/// </para>
/// </remarks>
public sealed record FeatureProperty(
    string Name,
    string Label,
    PropertyKind Kind,
    string Group = "",
    string Description = "",
    Dimension Dimension = Dimension.Dimensionless,
    ImmutableArray<string> Choices = default,
    FeatureValue? Default = null,
    double? Minimum = null,
    double? Maximum = null,
    MultiplicityPolicy Multiplicity = MultiplicityPolicy.ExactlyOne,
    PropertyCondition? VisibleWhen = null)
{
    /// <summary>Gets the options, never a default array.</summary>
    public ImmutableArray<string> Options => Choices.IsDefault ? [] : Choices;

    /// <inheritdoc/>
    public bool Equals(FeatureProperty? other)
        => other is not null
            && Name == other.Name
            && Label == other.Label
            && Kind == other.Kind
            && Group == other.Group
            && Description == other.Description
            && Dimension == other.Dimension
            && Default == other.Default
            && Minimum.Equals(other.Minimum)
            && Maximum.Equals(other.Maximum)
            && Multiplicity == other.Multiplicity
            && VisibleWhen == other.VisibleWhen
            && Options.SequenceEqual(other.Options);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Name, Label, Kind, Group, Options.Length);

    /// <inheritdoc/>
    public override string ToString() => $"{Label} ({Name}, {Kind})";
}

/// <summary>When a property applies.</summary>
/// <param name="Property">The property that decides.</param>
/// <param name="Value">The value of that property which makes this one apply.</param>
public sealed record PropertyCondition(string Property, FeatureValue Value);

/// <summary>
/// Everything about a kind of feature that is not its behaviour, declared once.
/// </summary>
/// <param name="FeatureType">
/// The stable name of the feature kind, as it appears in files and scripts.
/// </param>
/// <param name="Label">What a person is shown.</param>
/// <param name="Category">Which group of the ribbon and the catalogue it belongs to.</param>
/// <param name="Properties">Everything the feature needs to be told, in the order to offer it.</param>
/// <param name="Description">What the feature does.</param>
/// <remarks>
/// <para>
/// §5.7: "Adding a feature should mean writing one class and one schema, not editing seven files."
/// This is that schema. It drives the property manager (P6-T04 renders it), the serialization
/// contract (<see cref="Validate"/> says what a file may contain and <see cref="WithDefaults"/>
/// fills in what an older file left out), the public API surface (a schema is enumerable, so a
/// caller can ask what a feature takes without a compiled reference to it), and the scripting
/// binding (a script sets properties by name and gets a real error when it uses the wrong kind).
/// </para>
/// <para>
/// The alternative — a property-manager layout, a serializer, an API model and a script binding all
/// written by hand — is four descriptions of one thing, and they drift. The one that drifts
/// silently is serialization, because nothing shows it is wrong until a file will not open.
/// </para>
/// <para>
/// A schema checks itself when it is built. A malformed one is a programming mistake that would
/// otherwise surface as a property that never appears in the UI, or a default no file can hold, and
/// finding it at construction means finding it the first time the feature is registered rather than
/// the first time a user opens that panel.
/// </para>
/// </remarks>
public sealed record FeatureSchema(
    string FeatureType,
    string Label,
    string Category,
    ImmutableArray<FeatureProperty> Properties,
    string Description = "")
{
    /// <summary>Gets the properties, never a default array.</summary>
    public ImmutableArray<FeatureProperty> Declared => Properties.IsDefault ? [] : Properties;

    /// <summary>Builds a schema, checking that it is well formed.</summary>
    /// <param name="featureType">The stable name of the feature kind.</param>
    /// <param name="label">What a person is shown.</param>
    /// <param name="category">Which group it belongs to.</param>
    /// <param name="properties">Everything the feature needs to be told.</param>
    /// <param name="description">What the feature does.</param>
    /// <returns>The schema.</returns>
    /// <exception cref="ArgumentException">The declaration contradicts itself.</exception>
    public static FeatureSchema Create(
        string featureType,
        string label,
        string category,
        IEnumerable<FeatureProperty> properties,
        string description = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureType);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(properties);

        FeatureSchema schema = new(featureType, label, category, [.. properties], description);

        schema.CheckWellFormed();

        return schema;
    }

    /// <summary>Finds a property by its stable name.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The property, or <see langword="null"/> if this schema declares none by that name.</returns>
    public FeatureProperty? Find(string name)
        => Declared.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    /// <summary>Whether a property applies to a feature as it currently stands.</summary>
    /// <param name="property">The property.</param>
    /// <param name="feature">The feature.</param>
    /// <returns>Whether it applies.</returns>
    /// <remarks>
    /// The same question the property manager asks to decide whether to show a control and
    /// validation asks to decide whether to insist on a value. One answer, so the two cannot
    /// disagree about whether the user was ever given the chance to say.
    /// </remarks>
    public bool Applies(FeatureProperty property, Feature feature)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(feature);

        if (property.VisibleWhen is not { } condition)
        {
            return true;
        }

        FeatureProperty? decider = Find(condition.Property);

        return decider is not null
            && ValueOf(decider, feature) == condition.Value
            && Applies(decider, feature);
    }

    /// <summary>Reads what a feature currently says a property is.</summary>
    /// <param name="property">The property.</param>
    /// <param name="feature">The feature.</param>
    /// <returns>The value, or <see langword="null"/> if the feature has not been told.</returns>
    /// <remarks>
    /// Where a value lives depends on its kind, and this is the only place that knows: a dimension
    /// is a parameter so that an expression can drive it, a selection is an entity reference so
    /// that persistent naming can repair it, and everything else is a setting. A caller that had to
    /// know which would be a fifth description of the feature.
    /// </remarks>
    public static FeatureValue? ValueOf(FeatureProperty property, Feature feature)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(feature);

        if (property.Kind == PropertyKind.Quantity)
        {
            return feature.FindParameter(property.Name) is { } parameter
                ? new QuantityValue(parameter.Value)
                : null;
        }

        return property.Kind == PropertyKind.Selection ? null : feature.FindSetting(property.Name);
    }

    /// <summary>Fills in whatever a feature has not been told and the schema has a default for.</summary>
    /// <param name="feature">The feature.</param>
    /// <returns>The feature, with defaults applied.</returns>
    /// <remarks>
    /// What makes adding a property to an existing feature kind safe. A file written before the
    /// property existed says nothing about it, and the alternatives are to refuse the file, or to
    /// let the feature run with a missing value and fail somewhere unrelated. Neither is what the
    /// person who wrote the file meant, and the schema already says what the value should be when
    /// nobody has said otherwise.
    /// </remarks>
    public Feature WithDefaults(Feature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);

        Feature filled = feature;

        foreach (FeatureProperty property in Declared)
        {
            if (property.Default is not { } fallback || ValueOf(property, filled) is not null)
            {
                continue;
            }

            filled = property.Kind == PropertyKind.Quantity
                ? fallback is QuantityValue quantity
                    ? filled with
                    {
                        Parameters = filled.Parameters.Add(
                            new Parameter(property.Name, quantity.Value, null, property.Description)),
                    }
                    : filled
                : filled.WithSetting(property.Name, fallback);
        }

        return filled;
    }

    /// <summary>Checks a feature against this schema.</summary>
    /// <param name="feature">The feature.</param>
    /// <returns>What is wrong with it, empty if nothing is.</returns>
    /// <remarks>
    /// <para>
    /// The pre-flight §5.7 asks for, before the kernel is touched. A missing value or one out of
    /// range is worth a sentence naming the property; the same mistake found by the kernel is worth
    /// a failure deep inside an operation, with a message about a surface.
    /// </para>
    /// <para>
    /// Reported as <see cref="SchemaViolation"/> rather than as a
    /// <see cref="FeatureDiagnostic"/>. A rebuild diagnostic says what happened when a feature ran;
    /// this says why one should not be run at all, which is a different question asked at a
    /// different time, and it can name the property at fault where a rebuild state cannot.
    /// </para>
    /// </remarks>
    public ImmutableArray<SchemaViolation> Validate(Feature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);

        ImmutableArray<SchemaViolation>.Builder found =
            ImmutableArray.CreateBuilder<SchemaViolation>();

        foreach (FeatureProperty property in Declared)
        {
            if (!Applies(property, feature))
            {
                continue;
            }

            FeatureValue? value = ValueOf(property, feature);

            if (value is null)
            {
                // A selection is not asked for here. Whether the geometry it names still exists is
                // persistent naming's question (§5.3), asked at rebuild against a model that has
                // been built -- and answering it here, against a document, would be guessing.
                if (property.Kind != PropertyKind.Selection)
                {
                    found.Add(new SchemaViolation(
                        feature.Id,
                        property.Name,
                        ViolationSeverity.Error,
                        $"'{property.Label}' has not been given a value."));
                }

                continue;
            }

            if (Check(property, value) is { } complaint)
            {
                found.Add(new SchemaViolation(
                    feature.Id, property.Name, ViolationSeverity.Error, complaint));
            }
        }

        foreach (string name in feature.SettingValues.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            if (Find(name) is null)
            {
                // Reported rather than refused. It may be a setting from a newer build, which
                // P3-T20 keeps and which this build has no business deleting -- but a user whose
                // feature is not behaving should be told the file says something nothing here reads.
                found.Add(new SchemaViolation(
                    feature.Id,
                    name,
                    ViolationSeverity.Warning,
                    $"'{name}' is not something a {Label} understands, and is being ignored."));
            }
        }

        return found.ToImmutable();
    }

    /// <inheritdoc/>
    public bool Equals(FeatureSchema? other)
        => other is not null
            && FeatureType == other.FeatureType
            && Label == other.Label
            && Category == other.Category
            && Description == other.Description
            && Declared.SequenceEqual(other.Declared);

    /// <inheritdoc/>
    public override int GetHashCode()
        => HashCode.Combine(FeatureType, Label, Category, Declared.Length);

    /// <inheritdoc/>
    public override string ToString() => $"{Label} ({FeatureType}, {Declared.Length} properties)";

    /// <summary>What is wrong with a value, or null if nothing is.</summary>
    private static string? Check(FeatureProperty property, FeatureValue value)
    {
        switch (property.Kind, value)
        {
            case (PropertyKind.Quantity, QuantityValue quantity):
                return quantity.Value.Dimension != property.Dimension
                    ? $"'{property.Label}' measures {property.Dimension} and was given "
                        + $"{quantity.Value.Dimension}."
                    : Range(property, quantity.Value.Value);

            case (PropertyKind.Number, NumberValue number):
                return Range(property, number.Value);

            case (PropertyKind.Flag, FlagValue):
            case (PropertyKind.Text, TextValue):
                return null;

            case (PropertyKind.Choice, ChoiceValue choice):
                return property.Options.Contains(choice.Value, StringComparer.Ordinal)
                    ? null
                    : $"'{choice.Value}' is not one of the options for '{property.Label}' "
                        + $"({string.Join(", ", property.Options)}).";

            default:
                return $"'{property.Label}' takes {Wanted(property.Kind)} and was given "
                    + $"{value.Kind}.";
        }
    }

    private static string? Range(FeatureProperty property, double value)
    {
        if (property.Minimum is { } least && value < least)
        {
            return $"'{property.Label}' is {value} and cannot be less than {least}.";
        }

        return property.Maximum is { } most && value > most
            ? $"'{property.Label}' is {value} and cannot be more than {most}."
            : null;
    }

    private static string Wanted(PropertyKind kind) => kind switch
    {
        PropertyKind.Quantity => "a dimension",
        PropertyKind.Number => "a whole number",
        PropertyKind.Flag => "on or off",
        PropertyKind.Text => "text",
        PropertyKind.Choice => "one of a set of options",
        _ => "geometry",
    };

    /// <summary>Refuses a declaration that contradicts itself.</summary>
    private void CheckWellFormed()
    {
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (FeatureProperty property in Declared)
        {
            if (string.IsNullOrWhiteSpace(property.Name))
            {
                throw new ArgumentException(
                    $"A property of {FeatureType} has no name, so nothing could refer to it.",
                    nameof(Properties));
            }

            if (!seen.Add(property.Name))
            {
                throw new ArgumentException(
                    $"{FeatureType} declares '{property.Name}' twice, so a file holding it would "
                    + "mean two things at once.",
                    nameof(Properties));
            }

            if (property.Kind == PropertyKind.Choice && property.Options.IsEmpty)
            {
                throw new ArgumentException(
                    $"'{property.Name}' of {FeatureType} is a choice with nothing to choose from.",
                    nameof(Properties));
            }

            if (property.Default is { } fallback && Check(property, fallback) is { } wrong)
            {
                throw new ArgumentException(
                    $"The default for '{property.Name}' of {FeatureType} is not a value it can "
                    + $"hold: {wrong}",
                    nameof(Properties));
            }
        }

        foreach (FeatureProperty property in Declared)
        {
            if (property.VisibleWhen is not { } condition)
            {
                continue;
            }

            FeatureProperty decider = Find(condition.Property)
                ?? throw new ArgumentException(
                    $"'{property.Name}' of {FeatureType} applies only when '{condition.Property}' "
                    + "has a value, and there is no such property.",
                    nameof(Properties));

            if (Check(decider, condition.Value) is { } mismatch)
            {
                throw new ArgumentException(
                    $"'{property.Name}' of {FeatureType} applies when '{condition.Property}' has a "
                    + $"value it cannot have: {mismatch}",
                    nameof(Properties));
            }

            if (ReferenceEquals(decider, property))
            {
                throw new ArgumentException(
                    $"'{property.Name}' of {FeatureType} applies only when it already has a value.",
                    nameof(Properties));
            }
        }
    }
}
