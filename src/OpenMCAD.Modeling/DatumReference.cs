using System.Collections.Immutable;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;

namespace OpenMCAD.Modeling;

/// <summary>
/// One input to a datum construction: a name, not coordinates.
/// </summary>
/// <remarks>
/// <para>
/// P5-T03. Every datum is built from something else — a plane to offset from, two points to run an
/// axis through, a face to sit parallel to — and each of those inputs is named the same two ways
/// <see cref="SketchPlaneReference"/> already names its three sources, because there are only two
/// kinds of thing in a document that can be named at all. Reference geometry carries its own
/// <c>(Owner, Name)</c> pair, the one <see cref="Document.FindReference"/> looks up by. Kernel
/// topology carries none, and is addressed through a <see cref="PersistentName"/> (ADR-0005, §5.3).
/// </para>
/// <para>
/// <b>Why one reference type rather than a source list per construction.</b>
/// <see cref="SketchPlaneReference"/> enumerates its three sources as cases because a sketch plane
/// has exactly one input and each source resolves to a plane a different way. A datum has up to
/// three inputs, in three different geometric roles — a plane, a point, an axis — and the same
/// input kind serves different roles in different constructions: a face is a plane to offset from
/// and a vertex is a point to run an axis through. Enumerating source-by-role would be a
/// combinatorial table of cases that resolve identically. Naming <em>what is pointed at</em> here
/// and asking <em>what role it must play</em> at resolution time keeps one table instead of nine,
/// and it is the resolver that reports a face named where a point was wanted.
/// </para>
/// </remarks>
public abstract record DatumReference
{
    private DatumReference()
    {
    }

    /// <summary>Reference geometry, by the name it carries.</summary>
    /// <param name="Owner">
    /// The feature that created it, or <see cref="FeatureId.None"/> for the origin geometry every
    /// document starts with.
    /// </param>
    /// <param name="Name">What it is called.</param>
    public sealed record OnGeometry(FeatureId Owner, string Name) : DatumReference;

    /// <summary>A face, edge or vertex of a body.</summary>
    /// <param name="Entity">Its persistent name.</param>
    /// <remarks>
    /// Always exactly one entity. A datum plane built on "every piece of a split face" has no
    /// meaning, so this deliberately does not offer <see cref="MultiplicityPolicy"/> the way
    /// <see cref="EntityReference"/> does for the consumers of kernel topology that can act on a
    /// set — the same call <see cref="SketchPlaneReference.OnFace"/> already made.
    /// </remarks>
    public sealed record OnTopology(PersistentName Entity) : DatumReference;

    /// <summary>Gets every feature this reference depends on.</summary>
    /// <returns>The features, without duplicates.</returns>
    /// <remarks>
    /// What the rebuild engine needs in order to know a datum has gone stale (§5.4). Empty for a
    /// reference to one of the standard datums, which no feature owns and which nothing can delete.
    /// </remarks>
    public ImmutableArray<FeatureId> ReferencedFeatures() => this switch
    {
        OnGeometry(var owner, _) => owner.IsValid ? [owner] : [],
        OnTopology(var entity) => entity.ReferencedFeatures(),
        _ => [],
    };
}
