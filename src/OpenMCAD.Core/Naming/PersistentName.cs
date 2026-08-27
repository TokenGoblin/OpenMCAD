using System.Collections.Immutable;
using System.Text;

using OpenMCAD.Core.Documents;

namespace OpenMCAD.Core.Naming;

/// <summary>
/// A durable way of pointing at one face, edge or vertex — one that survives the model being
/// rebuilt into different geometry.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem this exists for.</b> <c>Fillet(edge)</c> has to find the same edge again after
/// the sketch that produced it changes. Kernel indices change every rebuild, geometry moves, and
/// topology splits and merges. Storing an index would work until the first edit and then quietly
/// fillet a different edge; storing a position would work until the first dimension change (ADR-0005,
/// §5.3).
/// </para>
/// <para>
/// <b>What is stored instead is provenance.</b> Not where the entity is, but how it came to exist:
/// which feature made it, out of what, playing what part. That description stays true when the
/// geometry moves, because it is a description of the model's intent rather than of its current
/// coordinates.
/// </para>
/// <para>
/// <b>This type only records the name.</b> Finding the entity a name refers to after a rebuild is
/// three separate pieces of work, in order of authority: replaying the kernel's history (P3-T09),
/// scoring geometric candidates when history cannot answer (P3-T10), and failing loudly rather than
/// guessing when neither can (P3-T11). Nothing here resolves anything.
/// </para>
/// </remarks>
public sealed record PersistentName
{
    /// <summary>Creates a name.</summary>
    /// <param name="path">
    /// The steps by which the entity came to exist, oldest first. Usually one: an entity created by
    /// a feature and never touched again has a single segment. A second appears when a later
    /// feature modifies the entity rather than replacing it.
    /// </param>
    public PersistentName(ImmutableArray<NameSegment> path)
    {
        if (path.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "A persistent name needs at least one segment. A name with no segments describes "
                + "no entity and could only ever resolve to nothing.",
                nameof(path));
        }

        Path = path;
    }

    /// <summary>Gets the steps by which the entity came to exist, oldest first.</summary>
    public ImmutableArray<NameSegment> Path { get; }

    /// <summary>Gets the step that produced the entity as it now stands.</summary>
    public NameSegment Head => Path[^1];

    /// <summary>Gets the step that first created it.</summary>
    public NameSegment Origin => Path[0];

    /// <summary>Gets the feature responsible for the entity as it now stands.</summary>
    public FeatureId Feature => Head.Feature;

    /// <summary>Creates a name of one segment.</summary>
    /// <param name="segment">The segment.</param>
    /// <returns>The name.</returns>
    public static PersistentName Of(NameSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        return new PersistentName([segment]);
    }

    /// <summary>Gets every feature this name depends on, including through its sources.</summary>
    /// <returns>The features, without duplicates.</returns>
    /// <remarks>
    /// What the rebuild engine needs in order to know that a reference has gone stale. A name that
    /// mentions a feature which has been deleted cannot resolve, and the feature holding that name
    /// is the one to tell the user about (P3-T11).
    /// </remarks>
    public ImmutableArray<FeatureId> ReferencedFeatures()
    {
        HashSet<FeatureId> found = [];

        Collect(this, found);

        return [.. found];
    }

    /// <summary>Renders the name using feature names, as §5.3 shows it.</summary>
    /// <param name="nameOf">
    /// How to turn a feature id into something a person recognises. Returning null for an unknown
    /// id is fine, and renders the id instead.
    /// </param>
    /// <returns>The description.</returns>
    /// <remarks>
    /// For diagnostics, and worth the effort. The error a user is shown when a reference cannot be
    /// resolved is the only part of this machinery they will ever see, and "could not resolve
    /// entity reference" tells them nothing they can act on. §5.3's worked example is legible on
    /// inspection on purpose, and this produces exactly that form.
    /// </remarks>
    public string Describe(Func<FeatureId, string?> nameOf)
    {
        ArgumentNullException.ThrowIfNull(nameOf);

        StringBuilder text = new();

        for (int i = 0; i < Path.Length; ++i)
        {
            if (i > 0)
            {
                // Each later segment is a feature that modified the entity rather than creating it,
                // so the arrow reads as "and then".
                text.Append(" -> ");
            }

            Describe(Path[i], nameOf, text);
        }

        return text.ToString();
    }

    /// <inheritdoc />
    public bool Equals(PersistentName? other)
        => other is not null && Path.SequenceEqual(other.Path);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = default;

        foreach (NameSegment segment in Path)
        {
            hash.Add(segment);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    /// <remarks>Ids rather than names, for a log with no document to hand.</remarks>
    public override string ToString() => Describe(_ => null);

    private static void Describe(
        NameSegment segment, Func<FeatureId, string?> nameOf, StringBuilder text)
    {
        text.Append(nameOf(segment.Feature) ?? segment.Feature.ToString());
        text.Append('/');
        text.Append(segment.Role.Value);

        if (segment.Ordinal != 0)
        {
            text.Append('#').Append(segment.Ordinal);
        }

        if (segment.Sources.IsEmpty)
        {
            return;
        }

        text.Append("/from(");

        for (int i = 0; i < segment.Sources.Length; ++i)
        {
            if (i > 0)
            {
                // The conjunction §5.3 uses: this entity exists because all of these met, not
                // because any one of them did.
                text.Append(" & ");
            }

            switch (segment.Sources[i])
            {
                case NameSource.Entity entity:
                    text.Append(entity.Name.Describe(nameOf));
                    break;

                case NameSource.Sketch sketch:
                    text.Append(nameOf(sketch.Owner) ?? sketch.Owner.ToString())
                        .Append('.')
                        .Append(sketch.EntityId);
                    break;

                default:
                    text.Append('?');
                    break;
            }
        }

        text.Append(')');
    }

    private static void Collect(PersistentName name, HashSet<FeatureId> found)
    {
        foreach (NameSegment segment in name.Path)
        {
            found.Add(segment.Feature);

            foreach (NameSource source in segment.Sources)
            {
                switch (source)
                {
                    case NameSource.Entity entity:
                        Collect(entity.Name, found);
                        break;

                    case NameSource.Sketch sketch:
                        found.Add(sketch.Owner);
                        break;

                    default:
                        break;
                }
            }
        }
    }
}
