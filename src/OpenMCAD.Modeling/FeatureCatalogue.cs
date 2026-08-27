using System.Collections.Immutable;

using OpenMCAD.Core.Documents;

namespace OpenMCAD.Modeling;

/// <summary>
/// Every kind of feature this build knows, by the name its files use.
/// </summary>
/// <remarks>
/// <para>
/// P3-T21. The public API surface and the scripting binding §5.7 asks for are both "ask what
/// features there are, and what each one takes" — neither can be a compiled reference to a feature
/// class, because a plugin's feature is not compiled into either of them. A registry of schemas is
/// that surface, and it is the same registry the property manager reads.
/// </para>
/// <para>
/// Immutable, and built rather than mutated. A catalogue that could change under a caller would
/// mean a script enumerating features while a plugin loaded got a different answer halfway
/// through — and the answer it acted on would not be an answer the catalogue ever gave.
/// </para>
/// </remarks>
public sealed class FeatureCatalogue
{
    private readonly ImmutableDictionary<string, FeatureSchema> _byType;

    private FeatureCatalogue(ImmutableDictionary<string, FeatureSchema> byType)
        => _byType = byType;

    /// <summary>Gets a catalogue with nothing in it.</summary>
    public static FeatureCatalogue Empty { get; } =
        new(ImmutableDictionary.Create<string, FeatureSchema>(StringComparer.Ordinal));

    /// <summary>Gets every schema, ordered by category and then by label.</summary>
    /// <remarks>
    /// Ordered so that a generated ribbon, a generated document and a script's listing all present
    /// features the same way, and so that two runs of anything that enumerates them agree.
    /// </remarks>
    public ImmutableArray<FeatureSchema> Schemas =>
    [
        .. _byType.Values
            .OrderBy(s => s.Category, StringComparer.Ordinal)
            .ThenBy(s => s.Label, StringComparer.Ordinal)
            .ThenBy(s => s.FeatureType, StringComparer.Ordinal),
    ];

    /// <summary>Builds a catalogue from a set of schemas.</summary>
    /// <param name="schemas">The schemas.</param>
    /// <returns>The catalogue.</returns>
    /// <exception cref="ArgumentException">Two schemas claim the same feature type.</exception>
    public static FeatureCatalogue Of(IEnumerable<FeatureSchema> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);

        FeatureCatalogue catalogue = Empty;

        foreach (FeatureSchema schema in schemas)
        {
            catalogue = catalogue.With(schema);
        }

        return catalogue;
    }

    /// <summary>Adds a schema.</summary>
    /// <param name="schema">The schema.</param>
    /// <returns>A catalogue containing it.</returns>
    /// <exception cref="ArgumentException">Something already claims that feature type.</exception>
    public FeatureCatalogue With(FeatureSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        if (_byType.TryGetValue(schema.FeatureType, out FeatureSchema? existing))
        {
            // Refused rather than replaced. Two plugins each claiming "Extrude" is not a case where
            // one of them is obviously right, and whichever won would depend on load order --
            // which means a document opening differently depending on what else is installed.
            throw new ArgumentException(
                $"'{schema.FeatureType}' is already the name of a feature kind ({existing.Label}), "
                + $"so {schema.Label} cannot have it too. Every feature in a file is found by this "
                + "name.",
                nameof(schema));
        }

        return new FeatureCatalogue(_byType.Add(schema.FeatureType, schema));
    }

    /// <summary>Finds the schema for a kind of feature.</summary>
    /// <param name="featureType">The name its files use.</param>
    /// <returns>The schema, or <see langword="null"/> if this build has no such feature.</returns>
    public FeatureSchema? Find(string featureType)
        => _byType.TryGetValue(featureType, out FeatureSchema? schema) ? schema : null;

    /// <summary>Finds the schema for a feature.</summary>
    /// <param name="feature">The feature.</param>
    /// <returns>The schema, or <see langword="null"/> if this build has no such feature.</returns>
    public FeatureSchema? SchemaOf(Feature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);

        return Find(feature.FeatureType);
    }

    /// <summary>Checks every feature of a document against its schema.</summary>
    /// <param name="document">The document.</param>
    /// <returns>What is wrong with it, empty if nothing is.</returns>
    /// <remarks>
    /// A feature whose kind is unknown is a warning rather than a failure. It came from a plugin
    /// that is not loaded, and refusing the document would mean an uninstalled plugin costing the
    /// user the whole file rather than one feature — while P3-T20 keeps everything that feature
    /// held, so reinstalling the plugin brings it back intact.
    /// </remarks>
    public ImmutableArray<SchemaViolation> Validate(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);

        ImmutableArray<SchemaViolation>.Builder found =
            ImmutableArray.CreateBuilder<SchemaViolation>();

        foreach (Feature feature in document.Features)
        {
            FeatureSchema? schema = SchemaOf(feature);

            if (schema is null)
            {
                found.Add(new SchemaViolation(
                    feature.Id,
                    feature.FeatureType,
                    ViolationSeverity.Warning,
                    $"Nothing here knows what a '{feature.FeatureType}' is. It is being left "
                    + "exactly as it was found, and a build that does know will read it."));

                continue;
            }

            found.AddRange(schema.Validate(feature));
        }

        return found.ToImmutable();
    }

    /// <inheritdoc/>
    public override string ToString() => $"{_byType.Count} kinds of feature";
}
