using System.Globalization;

namespace OpenMCAD.Core.Assemblies;

/// <summary>
/// Identifies a component definition — <em>what</em> a component is — within an assembly.
/// </summary>
/// <param name="Value">The underlying identifier.</param>
/// <remarks>
/// <para>
/// Separate from <see cref="OccurrenceId"/>, and §5.9 says why in the strongest terms it uses about
/// anything: "never duplicate the definition. This distinction is structural — get it wrong and
/// large assemblies become unusable." One bolt used ten thousand times is one definition and ten
/// thousand occurrences, and the two having one id would make that sentence unsayable.
/// </para>
/// <para>
/// <b>Never sort by this.</b> The value is random, so an ordering by it is stable within one process
/// and meaningless between two — the same reason <see cref="Documents.BodyId"/> offers no total
/// order.
/// </para>
/// </remarks>
public readonly record struct ComponentDefinitionId(Guid Value)
{
    /// <summary>Gets the id that denotes no definition.</summary>
    public static ComponentDefinitionId None => default;

    /// <summary>Gets whether this denotes a definition.</summary>
    public bool IsValid => Value != Guid.Empty;

    /// <summary>Creates a new, unique id.</summary>
    /// <returns>The id.</returns>
    public static ComponentDefinitionId New() => new(Guid.NewGuid());

    /// <summary>Reads an id back from its round-trip form.</summary>
    /// <param name="text">The text, as produced by <see cref="ToStorageString"/>.</param>
    /// <returns>The id.</returns>
    /// <exception cref="FormatException">The text is not a recognised identifier.</exception>
    public static ComponentDefinitionId Parse(string text) => new(Guid.ParseExact(text, "D"));

    /// <summary>Tries to read an id back from its round-trip form.</summary>
    /// <param name="text">The text.</param>
    /// <param name="id">The id, if it parsed.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(string? text, out ComponentDefinitionId id)
    {
        if (Guid.TryParseExact(text, "D", out Guid value))
        {
            id = new ComponentDefinitionId(value);
            return true;
        }

        id = None;
        return false;
    }

    /// <summary>The form written to a document file.</summary>
    /// <returns>The text.</returns>
    public string ToStorageString() => Value.ToString("D", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override string ToString() => Value == Guid.Empty
        ? "definition(none)"
        : string.Create(CultureInfo.InvariantCulture, $"definition({Value:N})");
}

/// <summary>
/// Identifies one placement of a component — <em>where</em> an instance of it is — within the
/// assembly that holds it.
/// </summary>
/// <param name="Value">The underlying identifier.</param>
/// <remarks>
/// <para>
/// Unique within one assembly, not across a whole product. The same sub-assembly placed twice
/// contributes the same child occurrence ids under two different parents, because those children
/// are the sub-assembly's own and are not copied — which is the point. What names one instance in a
/// product is therefore not an id but an <see cref="OccurrencePath"/>.
/// </para>
/// </remarks>
public readonly record struct OccurrenceId(Guid Value)
{
    /// <summary>Gets the id that denotes no occurrence.</summary>
    public static OccurrenceId None => default;

    /// <summary>Gets whether this denotes an occurrence.</summary>
    public bool IsValid => Value != Guid.Empty;

    /// <summary>Creates a new, unique id.</summary>
    /// <returns>The id.</returns>
    public static OccurrenceId New() => new(Guid.NewGuid());

    /// <summary>Reads an id back from its round-trip form.</summary>
    /// <param name="text">The text, as produced by <see cref="ToStorageString"/>.</param>
    /// <returns>The id.</returns>
    /// <exception cref="FormatException">The text is not a recognised identifier.</exception>
    public static OccurrenceId Parse(string text) => new(Guid.ParseExact(text, "D"));

    /// <summary>Tries to read an id back from its round-trip form.</summary>
    /// <param name="text">The text.</param>
    /// <param name="id">The id, if it parsed.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(string? text, out OccurrenceId id)
    {
        if (Guid.TryParseExact(text, "D", out Guid value))
        {
            id = new OccurrenceId(value);
            return true;
        }

        id = None;
        return false;
    }

    /// <summary>The form written to a document file.</summary>
    /// <returns>The text.</returns>
    public string ToStorageString() => Value.ToString("D", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override string ToString() => Value == Guid.Empty
        ? "occurrence(none)"
        : string.Create(CultureInfo.InvariantCulture, $"occurrence({Value:N})");
}
