using System.Collections.Immutable;

namespace OpenMCAD.Core.Documents;

/// <summary>
/// What a document records about itself rather than about its geometry.
/// </summary>
/// <param name="Title">What the part is called, which need not be the file name.</param>
/// <param name="PartNumber">The identifier it is known by outside this program.</param>
/// <param name="Revision">
/// The engineering revision. Deliberately a string: revisions are <c>A</c>, <c>B</c>, <c>01</c>,
/// <c>C.2</c> and whatever else a given organisation has settled on, and a modeller that imposed
/// its own scheme would simply be wrong somewhere.
/// </param>
/// <param name="Material">
/// What it is made of, by name. Density and the rest belong to a material library, which is a later
/// phase; the name is what a drawing needs and is worth carrying from the start so that documents
/// authored now do not lose it.
/// </param>
/// <param name="Description">A free-text description.</param>
/// <param name="CustomProperties">
/// Anything else, by name. Every organisation has properties nobody else has — a cost code, a
/// finish specification, a supplier — and the alternative to carrying them generically is losing
/// them on every round-trip.
/// </param>
/// <remarks>
/// Kept out of <see cref="Document"/>'s own surface so that changing a part number is visibly a
/// change to the document's properties rather than to its model, and so the whole block can be
/// replaced in one operation.
/// </remarks>
public sealed record DocumentMetadata(
    string? Title = null,
    string? PartNumber = null,
    string? Revision = null,
    string? Material = null,
    string? Description = null,
    ImmutableDictionary<string, string>? CustomProperties = null)
{
    /// <summary>Gets metadata with nothing filled in.</summary>
    public static DocumentMetadata Empty { get; } = new();

    /// <summary>Gets the custom properties, never null.</summary>
    public ImmutableDictionary<string, string> Properties
        => CustomProperties ?? ImmutableDictionary<string, string>.Empty;

    /// <summary>The same metadata with one custom property set.</summary>
    /// <param name="name">The property name.</param>
    /// <param name="value">Its value.</param>
    /// <returns>The metadata.</returns>
    public DocumentMetadata WithProperty(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        return this with { CustomProperties = Properties.SetItem(name, value) };
    }
}
