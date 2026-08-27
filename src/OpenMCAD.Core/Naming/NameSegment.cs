using System.Collections.Immutable;
using System.Globalization;

using OpenMCAD.Core.Documents;
using OpenMCAD.Math;

namespace OpenMCAD.Core.Naming;

/// <summary>Where an entity came from.</summary>
public enum ProvenanceKind
{
    /// <summary>Created by the feature out of one of its inputs, such as a wall swept from a line.</summary>
    Generated,

    /// <summary>An existing entity the feature altered but did not replace.</summary>
    Modified,

    /// <summary>Created where two existing entities meet, such as a blend along an edge.</summary>
    Intersection,

    /// <summary>Created by the feature out of nothing that can be pointed at.</summary>
    New,

    /// <summary>Came in from a file, and has whatever identity that file gave it.</summary>
    Imported,
}

/// <summary>What part an entity plays in the feature that produced it.</summary>
/// <param name="Value">The role's name.</param>
/// <remarks>
/// <para>
/// Deliberately not an enum. §5.3 lists <c>SideWall</c>, <c>StartCap</c>, <c>EndCap</c>,
/// <c>BlendFace</c>, <c>DraftFace</c>, <c>SplitLeft</c> and then an ellipsis, and the ellipsis is
/// the important part: every feature type added in a later phase brings its own roles, and a plugin
/// can add feature types this build has never heard of (§5.12). A closed enum would mean a plugin's
/// entities either have no role or borrow an unrelated one, and it would make the file format
/// unreadable by any build that predates the plugin.
/// </para>
/// <para>
/// The well-known values below are constants rather than the whole set, so that comparing against
/// one is spelled the same way whether or not this build knows what it means.
/// </para>
/// </remarks>
public readonly record struct EntityRole(string Value)
{
    /// <summary>Gets the role of an entity whose part is not recorded.</summary>
    public static EntityRole Unknown => new("Unknown");

    /// <summary>Gets the role of a wall swept from a profile edge.</summary>
    public static EntityRole SideWall => new("SideWall");

    /// <summary>Gets the role of the face at the start of a sweep.</summary>
    public static EntityRole StartCap => new("StartCap");

    /// <summary>Gets the role of the face at the end of a sweep.</summary>
    public static EntityRole EndCap => new("EndCap");

    /// <summary>Gets the role of the curved face of a fillet or chamfer.</summary>
    public static EntityRole BlendFace => new("BlendFace");

    /// <summary>Gets the role of a face produced by drafting.</summary>
    public static EntityRole DraftFace => new("DraftFace");

    /// <summary>Gets the role of the first piece of something split in two.</summary>
    public static EntityRole SplitLeft => new("SplitLeft");

    /// <summary>Gets the role of the second piece of something split in two.</summary>
    public static EntityRole SplitRight => new("SplitRight");

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>What kind of geometry an entity is.</summary>
/// <remarks>
/// The first thing a geometric match compares, and the only part of a <see cref="GeoHint"/> that is
/// required to agree exactly (§5.3): a plane is never the face a cylinder became. Everything else
/// in the hint is a similarity score, and this is a gate.
/// </remarks>
public enum GeometryKind
{
    /// <summary>Not recorded.</summary>
    Unknown,

    /// <summary>A flat face.</summary>
    Plane,

    /// <summary>A cylindrical face.</summary>
    Cylinder,

    /// <summary>A conical face.</summary>
    Cone,

    /// <summary>A spherical face.</summary>
    Sphere,

    /// <summary>A toroidal face.</summary>
    Torus,

    /// <summary>A face of no simple analytic kind.</summary>
    FreeformSurface,

    /// <summary>A straight edge.</summary>
    Line,

    /// <summary>A circular edge.</summary>
    Circle,

    /// <summary>An elliptical edge.</summary>
    Ellipse,

    /// <summary>An edge of no simple analytic kind.</summary>
    FreeformCurve,

    /// <summary>A vertex.</summary>
    Point,
}

/// <summary>
/// A cheap geometric signature of an entity, recorded when its name was.
/// </summary>
/// <param name="Kind">What sort of geometry it is.</param>
/// <param name="Measure">
/// Its area if it is a face, its length if it is an edge, and zero for a vertex. Stored raw and
/// bucketed only when compared, so that the bucketing can be tuned without invalidating every name
/// ever written.
/// </param>
/// <param name="Centroid">
/// Where it is, in coordinates local to the feature that produced it. Local rather than global
/// deliberately: moving the whole part, or the sketch the feature is built on, must not stop its
/// faces being recognised.
/// </param>
/// <param name="Direction">
/// Its normal if it is a face, its tangent if it is an edge, and zero if neither applies.
/// </param>
/// <param name="AdjacencyDegree">
/// How many entities it touches. A crude shape descriptor that survives the things geometry does
/// not: a face keeps its four neighbours when it moves, stretches, or changes area.
/// </param>
/// <remarks>
/// <para>
/// This is the evidence tier two of resolution scores against (P3-T10), and it is deliberately
/// cheap to compute and cheap to store. It is not a fingerprint and cannot be: an exact geometric
/// signature would fail the moment a dimension changed, which is precisely the case naming exists
/// to survive.
/// </para>
/// <para>
/// Nothing here is authoritative. A hint that disagrees with history loses to history every time,
/// and a hint alone can only produce an answer that is accepted above a confidence threshold with
/// a clear margin over the runner-up — anything less is an error rather than a guess (§5.3).
/// </para>
/// </remarks>
public sealed record GeoHint(
    GeometryKind Kind,
    double Measure,
    Vec3d Centroid,
    Vec3d Direction,
    int AdjacencyDegree)
{
    /// <summary>Gets a hint that says only what kind of thing the entity is.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The hint.</returns>
    public static GeoHint Of(GeometryKind kind) => new(kind, 0, Vec3d.Zero, Vec3d.Zero, 0);

    /// <summary>Gets the measure, quantised so that small changes do not move it.</summary>
    /// <remarks>
    /// Logarithmic, because what matters is proportion rather than difference: a face growing from
    /// one square millimetre to two is a large change and one growing from a hundred to a hundred
    /// and one is not, and a linear bucket treats those the same.
    /// </remarks>
    public int MeasureBucket => Measure <= 0
        ? 0
        : (int)System.Math.Round(System.Math.Log(Measure, 2) * 4);

    /// <inheritdoc />
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Kind} m={Measure:0.####} at {Centroid} deg={AdjacencyDegree}");
}

/// <summary>What a segment was made from.</summary>
/// <remarks>
/// Either another named entity, which makes the structure recursive, or a sketch entity, which is
/// where the recursion stops. §5.3's worked example ends at <c>Sketch1.L3</c> for exactly this
/// reason: a sketch line has an identity of its own that does not need deriving from anything.
/// </remarks>
public abstract record NameSource
{
    private NameSource()
    {
    }

    /// <summary>Another named entity.</summary>
    /// <param name="Name">Its name.</param>
    public sealed record Entity(PersistentName Name) : NameSource;

    /// <summary>An entity belonging to a sketch.</summary>
    /// <param name="Owner">The feature that owns the sketch.</param>
    /// <param name="EntityId">Which entity within it.</param>
    public sealed record Sketch(FeatureId Owner, string EntityId) : NameSource;
}

/// <summary>
/// One step in an entity's provenance: which feature produced it, out of what, playing what part.
/// </summary>
/// <param name="Feature">The feature that produced it.</param>
/// <param name="Provenance">How it came about.</param>
/// <param name="Sources">
/// What it was made from, in a fixed order. More than one is normal rather than exceptional: the
/// blend face of a fillet exists because two faces meet, and naming it after either one alone
/// would not distinguish it from the blend on the next edge along.
/// </param>
/// <param name="Role">What part it plays in that feature.</param>
/// <param name="Ordinal">
/// Which of several otherwise identical siblings this is. Zero when there is only one, so that the
/// common case does not carry a number that means nothing.
/// </param>
/// <param name="Hint">A cheap geometric signature, for when history cannot answer.</param>
public sealed record NameSegment(
    FeatureId Feature,
    ProvenanceKind Provenance,
    ImmutableArray<NameSource> Sources,
    EntityRole Role,
    int Ordinal = 0,
    GeoHint? Hint = null)
{
    /// <summary>Creates a segment with no sources.</summary>
    /// <param name="feature">The feature that produced the entity.</param>
    /// <param name="provenance">How it came about.</param>
    /// <param name="role">What part it plays.</param>
    /// <param name="ordinal">Which sibling it is.</param>
    /// <returns>The segment.</returns>
    public static NameSegment Of(
        FeatureId feature, ProvenanceKind provenance, EntityRole role, int ordinal = 0)
        => new(feature, provenance, [], role, ordinal);

    /// <inheritdoc />
    /// <remarks>
    /// Records do not compare <see cref="ImmutableArray{T}"/> element by element — the generated
    /// comparison uses the array's own equality, which is by underlying reference. Two names built
    /// the same way from different array instances would then be unequal, and since names are
    /// compared constantly during resolution that would not fail loudly; it would just never match.
    /// </remarks>
    public bool Equals(NameSegment? other)
        => other is not null
            && Feature == other.Feature
            && Provenance == other.Provenance
            && Role == other.Role
            && Ordinal == other.Ordinal
            && Hint == other.Hint
            && Sources.SequenceEqual(other.Sources);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = default;

        hash.Add(Feature);
        hash.Add(Provenance);
        hash.Add(Role);
        hash.Add(Ordinal);
        hash.Add(Hint);

        foreach (NameSource source in Sources)
        {
            hash.Add(source);
        }

        return hash.ToHashCode();
    }
}
