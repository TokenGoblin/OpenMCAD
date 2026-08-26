using System.Globalization;

namespace OpenMCAD.Core.Documents;

/// <summary>
/// Identifies a body within a document, for as long as that body keeps its identity.
/// </summary>
/// <param name="Value">The underlying identifier.</param>
/// <remarks>
/// <para>
/// Separate from <see cref="FeatureId"/> because a feature and the body it produces are not the
/// same thing and do not have the same lifetime. One feature can produce several bodies — a split,
/// a pattern, a shell that falls apart — and one body can survive many features acting on it, which
/// is exactly the case a single id would get wrong.
/// </para>
/// <para>
/// <b>What "keeps its identity" means, and why it is not obvious.</b> When a feature consumes a
/// body and emits a modified one, whether that is the *same* body is a policy decision, not a fact
/// about the geometry: a fillet plainly returns the same body, a boolean union of two bodies must
/// pick one identity or mint a new one, and a split has to decide which of its pieces inherits.
/// P3-T12 is where those policies are declared. This type only carries the answer.
/// </para>
/// <para>
/// <b>Never sort by this.</b> The value is random, so ordering by it is stable within one process
/// and meaningless between two. Nor is a total order offered: this deliberately does not
/// implement <see cref="IComparable{T}"/>, so that code wanting a stable order has to reach for one
/// that means something.
/// </para>
/// </remarks>
public readonly record struct BodyId(Guid Value)
{
    /// <summary>Gets the id that denotes no feature.</summary>
    public static BodyId None => default;

    /// <summary>Gets a value indicating whether this denotes a feature.</summary>
    public bool IsValid => Value != Guid.Empty;

    /// <summary>Creates a new, unique id.</summary>
    /// <returns>The id.</returns>
    public static BodyId New() => new(Guid.NewGuid());

    /// <summary>Reads an id back from its round-trip form.</summary>
    /// <param name="text">The text, as produced by <see cref="ToStorageString"/>.</param>
    /// <returns>The id.</returns>
    /// <exception cref="FormatException">The text is not a recognised identifier.</exception>
    public static BodyId Parse(string text) => new(Guid.ParseExact(text, "D"));

    /// <summary>Tries to read an id back from its round-trip form.</summary>
    /// <param name="text">The text.</param>
    /// <param name="id">The id, if it parsed.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(string? text, out BodyId id)
    {
        if (Guid.TryParseExact(text, "D", out Guid value))
        {
            id = new BodyId(value);
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
        ? "body(none)"
        : string.Create(CultureInfo.InvariantCulture, $"body({Value:N})");
}
