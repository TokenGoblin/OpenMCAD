using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

using OpenMCAD.Core.Documents;

namespace OpenMCAD.Cli;

/// <summary>One parameter of a document or a feature, as a spec states it.</summary>
/// <param name="Name">What it is called.</param>
/// <param name="Value">Its magnitude, in the unit named below.</param>
/// <param name="Unit">
/// Which unit the magnitude is in: <c>mm</c>, <c>m</c>, <c>in</c>, <c>deg</c>, <c>rad</c>, or
/// nothing for a plain number.
/// </param>
/// <param name="Expression">What it was entered as, if it is derived.</param>
/// <param name="Description">Whatever is worth saying about it.</param>
/// <remarks>
/// The unit is stated rather than assumed. A spec that wrote bare numbers would mean whatever the
/// reader guessed, and every guess about a CAD dimension that has ever been made in millimetres by
/// one side and inches by the other has ended the same way.
/// </remarks>
public sealed record ParameterSpec(
    string Name,
    double Value,
    string? Unit = null,
    string? Expression = null,
    string? Description = null);

/// <summary>One setting of a feature, as a spec states it.</summary>
/// <param name="Name">What it is called.</param>
/// <param name="Flag">A switch, if that is what it is.</param>
/// <param name="Number">A count, if that is what it is.</param>
/// <param name="Text">Text, if that is what it is.</param>
/// <param name="Choice">One of a set of options, if that is what it is.</param>
/// <remarks>
/// Exactly one may be given. A spec naming two is a mistake worth reporting rather than a case
/// where one of them is obviously meant.
/// </remarks>
public sealed record SettingSpec(
    string Name,
    bool? Flag = null,
    long? Number = null,
    string? Text = null,
    string? Choice = null);

/// <summary>One feature, as a spec states it.</summary>
/// <param name="Name">Its display name.</param>
/// <param name="Type">Which kind of operation it is.</param>
/// <param name="Id">Its id. Left out, one is derived from the name so a spec builds the same twice.</param>
/// <param name="Inputs">The names of the features it consumes.</param>
/// <param name="Parameters">Its dimensions.</param>
/// <param name="Settings">What else it has been told.</param>
/// <param name="Suppressed">Whether it is switched off.</param>
public sealed record FeatureSpec(
    string Name,
    string Type,
    string? Id = null,
    ImmutableArray<string> Inputs = default,
    ImmutableArray<ParameterSpec> Parameters = default,
    ImmutableArray<SettingSpec> Settings = default,
    bool Suppressed = false);

/// <summary>A document, as a spec states it.</summary>
/// <param name="Title">The document title.</param>
/// <param name="PartNumber">The part number.</param>
/// <param name="Revision">The revision.</param>
/// <param name="Description">What the part is.</param>
/// <param name="Parameters">The document's named values.</param>
/// <param name="Features">The features, in tree order.</param>
/// <param name="Rollback">Where the rollback bar sits, or null for none.</param>
/// <remarks>
/// <para>
/// P3-T22. What <c>omcad build</c> takes. Every later phase tests through the headless tool, and a
/// test that had to construct a document by calling an API would be a test of that API's C#
/// surface rather than of the thing being tested — and could not be written by anyone working from
/// outside the repository.
/// </para>
/// <para>
/// Deliberately not the regression corpus's fixture format, which describes kernel operations. This
/// describes a document: features, parameters and a rollback bar, with no geometry in it at all.
/// The two are different layers and one file trying to be both would serve neither.
/// </para>
/// </remarks>
public sealed record DocumentSpec(
    string? Title = null,
    string? PartNumber = null,
    string? Revision = null,
    string? Description = null,
    ImmutableArray<ParameterSpec> Parameters = default,
    ImmutableArray<FeatureSpec> Features = default,
    int? Rollback = null)
{
    /// <summary>How a spec is read and written.</summary>
    public static JsonSerializerOptions Format { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        NewLine = "\n",
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Reads a spec.</summary>
    /// <param name="json">The text.</param>
    /// <returns>The spec.</returns>
    /// <exception cref="SpecException">The text is not a spec.</exception>
    public static DocumentSpec Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<DocumentSpec>(json, Format)
                ?? throw new SpecException("That file is empty.");
        }
        catch (JsonException failure)
        {
            throw new SpecException($"That file is not a document spec: {failure.Message}", failure);
        }
    }

    /// <summary>Builds the document this spec describes.</summary>
    /// <returns>The document.</returns>
    /// <exception cref="SpecException">The spec contradicts itself.</exception>
    /// <remarks>
    /// Through a transaction, like every other edit. A document assembled behind the session's back
    /// would be one no undo could reach, and the headless tool is where later phases will build the
    /// documents their tests then edit.
    /// </remarks>
    public Document Build()
    {
        DocumentSession session = new();

        Dictionary<string, FeatureId> byName = new(StringComparer.Ordinal);

        using (IDocumentTransaction edit = session.BeginTransaction("build"))
        {
            foreach (ParameterSpec parameter in Parameters.IsDefault ? [] : Parameters)
            {
                edit.SetParameter(new Parameter(
                    parameter.Name,
                    QuantityOf(parameter),
                    parameter.Expression,
                    parameter.Description));
            }

            foreach (FeatureSpec feature in Features.IsDefault ? [] : Features)
            {
                if (byName.ContainsKey(feature.Name))
                {
                    throw new SpecException(
                        $"Two features are called '{feature.Name}'. Later features refer to earlier "
                        + "ones by name, so the reference would be ambiguous.");
                }

                FeatureId id = IdOf(feature);
                byName[feature.Name] = id;

                edit.AddFeature(new Feature(
                    id,
                    feature.Name,
                    feature.Type,
                    [.. InputsOf(feature, byName)],
                    [.. (feature.Parameters.IsDefault ? [] : feature.Parameters)
                        .Select(p => new Parameter(
                            p.Name, QuantityOf(p), p.Expression, p.Description))],
                    [],
                    feature.Suppressed,
                    SettingsOf(feature)));
            }

            if (Rollback is { } bar)
            {
                int features = Features.IsDefault ? 0 : Features.Length;

                if (bar < 0 || bar > features)
                {
                    throw new SpecException(
                        $"The rollback bar is at {bar} and there are {features} features.");
                }

                edit.SetRollbackPosition(bar);
            }

            edit.SetMetadata(DocumentMetadata.Empty with
            {
                Title = Title,
                PartNumber = PartNumber,
                Revision = Revision,
                Description = Description,
            });

            edit.Commit();
        }

        return session.Current;
    }

    /// <summary>Derives a feature's id from what the spec says.</summary>
    /// <remarks>
    /// A spec without ids builds the same document every time it is run, because the id comes from
    /// the name rather than from a clock or a counter. That is what lets the same spec be used as a
    /// fixture: a document whose ids changed on every build could never be compared with a stored
    /// one.
    /// </remarks>
    private static FeatureId IdOf(FeatureSpec feature)
    {
        if (feature.Id is { } stated)
        {
            return Guid.TryParse(stated, out Guid parsed)
                ? new FeatureId(parsed)
                : throw new SpecException($"'{stated}' is not an id.");
        }

        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"feature:{feature.Name}"));

        return new FeatureId(new Guid(hash.AsSpan(0, 16)));
    }

    private static IEnumerable<FeatureId> InputsOf(
        FeatureSpec feature, Dictionary<string, FeatureId> byName)
    {
        foreach (string input in feature.Inputs.IsDefault ? [] : feature.Inputs)
        {
            if (!byName.TryGetValue(input, out FeatureId id))
            {
                throw new SpecException(
                    $"'{feature.Name}' consumes '{input}', which is not a feature declared before "
                    + "it. A feature can only consume something already in the tree.");
            }

            yield return id;
        }
    }

    private static ImmutableDictionary<string, FeatureValue>? SettingsOf(FeatureSpec feature)
    {
        if (feature.Settings.IsDefault || feature.Settings.IsEmpty)
        {
            return null;
        }

        ImmutableDictionary<string, FeatureValue>.Builder found =
            ImmutableDictionary.CreateBuilder<string, FeatureValue>(StringComparer.Ordinal);

        foreach (SettingSpec setting in feature.Settings)
        {
            FeatureValue[] given =
            [
                .. new FeatureValue?[]
                {
                    setting.Flag is { } flag ? new FlagValue(flag) : null,
                    setting.Number is { } number ? new NumberValue(number) : null,
                    setting.Text is { } text ? new TextValue(text) : null,
                    setting.Choice is { } choice ? new ChoiceValue(choice) : null,
                }.OfType<FeatureValue>(),
            ];

            found[setting.Name] = given.Length == 1
                ? given[0]
                : throw new SpecException(
                    given.Length == 0
                        ? $"Setting '{setting.Name}' of '{feature.Name}' has no value."
                        : $"Setting '{setting.Name}' of '{feature.Name}' has {given.Length} values, "
                            + "and can only have one.");
        }

        return found.ToImmutable();
    }

    private static Quantity QuantityOf(ParameterSpec parameter) => parameter.Unit?.ToLowerInvariant() switch
    {
        null or "" => new Quantity(parameter.Value, Dimension.Dimensionless),
        "mm" => Core.Documents.Unit.Millimetres.Of(parameter.Value),
        "m" => Core.Documents.Unit.Metres.Of(parameter.Value),
        "in" => Core.Documents.Unit.Inches.Of(parameter.Value),
        "deg" or "°" => Core.Documents.Unit.Degrees.Of(parameter.Value),
        "rad" => Core.Documents.Unit.Radians.Of(parameter.Value),
        _ => throw new SpecException(
            $"'{parameter.Unit}' is not a unit this build knows, in parameter "
            + $"'{parameter.Name}'."),
    };
}

/// <summary>Thrown when a spec says something that cannot be built.</summary>
public sealed class SpecException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong.</param>
    public SpecException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    /// <param name="message">What is wrong.</param>
    /// <param name="innerException">The cause.</param>
    public SpecException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with nothing to say.</summary>
    public SpecException()
        : base("That is not a document spec.")
    {
    }
}
