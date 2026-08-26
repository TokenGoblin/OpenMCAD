using System.Globalization;

namespace OpenMCAD.Core.Documents;

/// <summary>
/// Identifies a feature within a document, for the life of that feature.
/// </summary>
/// <param name="Value">The underlying identifier.</param>
/// <remarks>
/// <para>
/// <b>Why a strong type rather than a <see cref="Guid"/>.</b> Every identifier in this layer is a
/// GUID, so a signature taking two of them accepts them in either order and a caller that swaps a
/// feature for a body compiles perfectly. The compiler can only object once they are different
/// types, and the cost of making them different is one struct each.
/// </para>
/// <para>
/// <b>This identifies the feature, not its result.</b> A feature keeps its id when its parameters
/// change, when it is suppressed, when it fails and when it is rebuilt into completely different
/// geometry. What it produces is identified separately by <see cref="BodyId"/>, and the faces and
/// edges within that by <c>PersistentName</c> (ADR-0005) — three mechanisms because they answer
/// three different questions and conflating any two of them is where topological naming goes wrong.
/// </para>
/// <para>
/// <b>Never sort by this.</b> The value is random, so ordering by it is stable within one process
/// and meaningless between two. The order features appear in is the document's own list, which is
/// user-facing and deliberate; the order they rebuild in comes from the dependency graph. Nor is
/// any total order offered here: this deliberately does not implement
/// <see cref="IComparable{T}"/>, so code wanting a stable order has to reach for one that means
/// something.
/// </para>
/// </remarks>
public readonly record struct FeatureId(Guid Value)
{
    /// <summary>Gets the id that denotes no feature.</summary>
    public static FeatureId None => default;

    /// <summary>Gets a value indicating whether this denotes a feature.</summary>
    public bool IsValid => Value != Guid.Empty;

    /// <summary>Creates a new, unique id.</summary>
    /// <returns>The id.</returns>
    public static FeatureId New() => new(Guid.NewGuid());

    /// <summary>Reads an id back from its round-trip form.</summary>
    /// <param name="text">The text, as produced by <see cref="ToStorageString"/>.</param>
    /// <returns>The id.</returns>
    /// <exception cref="FormatException">The text is not a recognised identifier.</exception>
    public static FeatureId Parse(string text) => new(Guid.ParseExact(text, "D"));

    /// <summary>Tries to read an id back from its round-trip form.</summary>
    /// <param name="text">The text.</param>
    /// <param name="id">The id, if it parsed.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(string? text, out FeatureId id)
    {
        if (Guid.TryParseExact(text, "D", out Guid value))
        {
            id = new FeatureId(value);
            return true;
        }

        id = None;
        return false;
    }

    /// <summary>The form written to a document file.</summary>
    /// <returns>The text.</returns>
    /// <remarks>
    /// Separate from <see cref="ToString"/> deliberately. This one has to round-trip and must never
    /// change; the other is for a person reading a log and is free to be shortened, decorated or
    /// reworded. A single method serving both ends up frozen by the file format.
    /// </remarks>
    public string ToStorageString() => Value.ToString("D", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override string ToString() => Value == Guid.Empty
        ? "feature(none)"
        : string.Create(CultureInfo.InvariantCulture, $"feature({Value:N})");
}
