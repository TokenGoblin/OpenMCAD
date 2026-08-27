using System.Collections.Immutable;

namespace OpenMCAD.Core.Serialization;

/// <summary>
/// A field of a document that this build has no name for, kept exactly as it arrived.
/// </summary>
/// <param name="Owner">What the field belongs to. Empty for the document itself.</param>
/// <param name="Field">Its key.</param>
/// <param name="Value">Its encoded value, marker and all.</param>
/// <remarks>
/// <para>
/// P3-T20. A newer build's document is not only a matter of the schema number: it may carry fields
/// this one has never heard of, and the only responsible thing to do with them is give them back
/// untouched. Dropping them means that opening a colleague's file to look at it, and saving out of
/// habit, silently deletes work — the kind of data loss nobody notices until much later, and which
/// no error message ever appeared for.
/// </para>
/// <para>
/// The value is stored encoded rather than interpreted. Anything else would mean guessing at the
/// meaning of a field whose meaning is by definition unavailable, and a guess that re-encoded it
/// differently would corrupt it while appearing to preserve it.
/// </para>
/// </remarks>
internal sealed record UnknownField(string Owner, string Field, ImmutableArray<byte> Value)
{
    /// <summary>The owner of a field belonging to the document itself.</summary>
    public const string Root = "";

    /// <summary>The owner of a field belonging to the document's properties.</summary>
    public const string Metadata = "metadata";

    /// <summary>Names the owner of a field belonging to a feature.</summary>
    /// <param name="id">The feature's id, as stored.</param>
    /// <returns>The owner.</returns>
    public static string Feature(string id) => $"feature:{id}";

    /// <summary>Names the owner of a field belonging to a parameter.</summary>
    /// <param name="name">The parameter's name.</param>
    /// <returns>The owner.</returns>
    public static string Parameter(string name) => $"parameter:{name}";

    /// <summary>Names the owner of a field belonging to a body.</summary>
    /// <param name="id">The body's id, as stored.</param>
    /// <returns>The owner.</returns>
    public static string Body(string id) => $"body:{id}";

    /// <summary>Names the owner of a field belonging to a piece of reference geometry.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>The owner.</returns>
    /// <remarks>
    /// By name, because reference geometry has no id of its own. Renaming a datum therefore loses
    /// whatever a newer build had attached to it, which is a real limitation and the honest one:
    /// the alternative is to attach the fields to a position in a list, and then inserting a datum
    /// moves someone else's data onto the wrong object.
    /// </remarks>
    public static string Reference(string name) => $"reference:{name}";

    /// <summary>Puts one owner inside another.</summary>
    /// <param name="outer">The containing owner.</param>
    /// <param name="inner">The contained owner, empty for the container's own fields.</param>
    /// <returns>The combined owner.</returns>
    /// <remarks>
    /// Nesting matters because a field is read before the thing that owns it necessarily has a
    /// name. A parameter's unknown field is collected while the parameter is still being read, and
    /// only once its name is known — and then only once the enclosing feature's id is known — can
    /// it be said what it belongs to.
    /// </remarks>
    public static string Nest(string outer, string inner)
        => inner.Length == 0 ? outer : outer.Length == 0 ? inner : $"{outer}/{inner}";

    /// <inheritdoc/>
    public bool Equals(UnknownField? other)
        => other is not null
            && Owner == other.Owner
            && Field == other.Field
            && Value.SequenceEqual(other.Value);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Owner, Field, Value.Length);

    /// <inheritdoc/>
    public override string ToString() => $"{Owner}/{Field} ({Value.Length} bytes)";
}
