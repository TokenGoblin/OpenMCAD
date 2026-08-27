namespace OpenMCAD.Solver.Sketching;

/// <summary>
/// Identifies one piece of sketch geometry.
/// </summary>
/// <param name="Value">The underlying value.</param>
/// <remarks>
/// <para>
/// GUID-backed and strongly typed, for the same reasons as the document's ids: an index would
/// change meaning the moment an entity was deleted, and a bare <see cref="Guid"/> would let a
/// constraint be pointed at a feature by a compiler that saw nothing wrong.
/// </para>
/// <para>
/// It does not implement <see cref="IComparable{T}"/>, deliberately. The values are random, so any
/// ordering by them is stable within one process and meaningless between two — and a solver whose
/// entity order came from that would converge differently on two machines.
/// </para>
/// </remarks>
public readonly record struct SketchEntityId(Guid Value)
{
    /// <summary>Gets the id that denotes no entity.</summary>
    public static SketchEntityId None => default;

    /// <summary>Gets a value indicating whether this denotes an entity.</summary>
    public bool IsValid => Value != Guid.Empty;

    /// <summary>Creates a new, unique id.</summary>
    /// <returns>The id.</returns>
    public static SketchEntityId New() => new(Guid.NewGuid());

    /// <summary>Reads an id back from its round-trip form.</summary>
    /// <param name="text">The text, as produced by <see cref="ToStorageString"/>.</param>
    /// <returns>The id.</returns>
    /// <exception cref="FormatException">The text is not a recognised identifier.</exception>
    public static SketchEntityId Parse(string text) => new(Guid.ParseExact(text, "D"));

    /// <summary>Tries to read an id back from its round-trip form.</summary>
    /// <param name="text">The text.</param>
    /// <param name="id">The id, if it parsed.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(string? text, out SketchEntityId id)
    {
        if (Guid.TryParseExact(text, "D", out Guid value))
        {
            id = new SketchEntityId(value);
            return true;
        }

        id = None;
        return false;
    }

    /// <summary>Writes the id in the form a file holds.</summary>
    /// <returns>The text.</returns>
    public string ToStorageString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    public override string ToString() => IsValid ? ToStorageString() : "(none)";
}

/// <summary>
/// Which named point of an entity a constraint is attached to.
/// </summary>
/// <remarks>
/// <para>
/// A constraint almost never attaches to an entity as a whole. "This line's end meets that arc's
/// centre" is two point references, and a model in which a constraint named only the two entities
/// could not express it.
/// </para>
/// <para>
/// Named rather than indexed. An index into "the points of an entity" means something different
/// for a line and for an ellipse, and the first time an entity kind gains a point every stored
/// index after it silently moves.
/// </para>
/// </remarks>
public enum EntityPoint
{
    /// <summary>The entity itself, where it is a point.</summary>
    Self,

    /// <summary>Where the curve begins.</summary>
    Start,

    /// <summary>Where the curve ends.</summary>
    End,

    /// <summary>The centre of a circle, arc, ellipse or hyperbola.</summary>
    Centre,

    /// <summary>The first focus of a conic.</summary>
    Focus,

    /// <summary>The second focus of an ellipse or hyperbola.</summary>
    SecondFocus,

    /// <summary>The midpoint of the curve, by parameter.</summary>
    Middle,
}

/// <summary>
/// A particular point of a particular entity.
/// </summary>
/// <param name="Entity">Which entity.</param>
/// <param name="Point">Which of its points.</param>
public readonly record struct SketchPointRef(SketchEntityId Entity, EntityPoint Point = EntityPoint.Self)
{
    /// <inheritdoc/>
    public override string ToString()
        => Point == EntityPoint.Self ? Entity.ToString() : $"{Entity}.{Point}";
}
