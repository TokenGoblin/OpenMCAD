using System.Collections.Immutable;
using OpenMCAD.Math;

namespace OpenMCAD.Kernel.Fake.Geometry;

/// <summary>
/// One face, edge, or vertex of a fake shape.
/// </summary>
/// <param name="Tag">The entity's handle.</param>
/// <param name="Kind">What sort of entity it is.</param>
/// <param name="Role">What the operation that made it says it is.</param>
/// <param name="Ordinal">Its index among siblings with the same role.</param>
/// <param name="Point">A representative point: face centroid, edge midpoint, or vertex position.</param>
/// <param name="Direction">Face normal or edge direction; zero for a vertex.</param>
/// <param name="Measure">Face area, edge length, or zero for a vertex.</param>
/// <remarks>
/// <see cref="Point"/>, <see cref="Direction"/>, and <see cref="Measure"/> are not needed by
/// anything in Phase 1. They are here because they are exactly the <c>GeoHint</c> material that
/// tier-2 name resolution needs in P3-T10, and synthesising them later from a mock that never
/// tracked them would mean rewriting it.
/// </remarks>
internal sealed record FakeEntity(
    ulong Tag,
    SubEntityKind Kind,
    OperationRole Role,
    int Ordinal,
    Vec3d Point,
    Vec3d Direction,
    double Measure)
{
    /// <summary>
    /// Gets the tags of the faces adjacent to this edge, empty for other kinds.
    /// </summary>
    /// <remarks>
    /// A fillet needs to know which two faces an edge separates, because the blend face it creates
    /// is named as the blend <i>between those two faces</i> rather than as an anonymous new face.
    /// That relationship is what lets a fillet survive an edit to the sketch beneath it.
    /// </remarks>
    internal ImmutableArray<ulong> AdjacentFaces { get; init; } = [];
}

/// <summary>
/// The volumetric model behind a fake shape, used for mass properties and tessellation.
/// </summary>
/// <remarks>
/// Topology is stored explicitly on <see cref="FakeShape"/> rather than derived from this, because
/// the two have different needs: topology must be exact and stable for naming, whereas volume only
/// has to be plausible and deterministic. Keeping them apart is what lets booleans have believable
/// topology without anyone having to write a boolean.
/// </remarks>
internal abstract record FakeGeometry
{
    /// <summary>Gets the axis-aligned bound.</summary>
    internal abstract Bounds3d Bounds { get; }

    /// <summary>Gets how trustworthy the computed properties are.</summary>
    internal abstract ResultAccuracy Accuracy { get; }

    /// <summary>Computes mass properties at the given density.</summary>
    /// <param name="density">Density in kilograms per cubic metre.</param>
    internal abstract MassProperties Compute(double density);

    /// <summary>Appends triangles to <paramref name="mesh"/>.</summary>
    /// <param name="mesh">Where to append.</param>
    /// <param name="options">How finely to tessellate.</param>
    /// <param name="faceIndex">The face index to attribute the triangles to.</param>
    internal abstract void Tessellate(MeshAccumulator mesh, TessellationOptions options, int faceIndex);

    /// <summary>
    /// Chooses a segment count for a full circle, from the requested chordal deviation.
    /// </summary>
    /// <param name="radius">The circle radius.</param>
    /// <param name="options">The tessellation settings.</param>
    /// <remarks>
    /// Clamped to a sane range and rounded to an even number so that opposing facets line up. The
    /// result depends only on its arguments, which is what keeps tessellation reproducible.
    /// </remarks>
    private protected static int SegmentsForCircle(double radius, TessellationOptions options)
    {
        double deviation = options.Relative
            ? System.Math.Max(options.ChordalDeviation * radius, Tolerance.LinearResolution)
            : options.ChordalDeviation;

        if (deviation >= radius)
        {
            return 8;
        }

        double angle = 2.0 * System.Math.Acos(Tolerance.Clamp(1.0 - (deviation / radius), -1.0, 1.0));
        int segments = (int)System.Math.Ceiling(2.0 * System.Math.PI / System.Math.Max(angle, 1e-6));
        segments = System.Math.Clamp(segments, 8, 512);
        return segments % 2 == 0 ? segments : segments + 1;
    }
}

/// <summary>Accumulates triangles while tessellating.</summary>
internal sealed class MeshAccumulator
{
    private readonly List<Vec3d> _positions = [];
    private readonly List<Vec3d> _normals = [];
    private readonly List<int> _indices = [];
    private readonly List<int> _triangleFaces = [];

    /// <summary>Adds a vertex and returns its index.</summary>
    /// <param name="position">The position.</param>
    /// <param name="normal">The normal.</param>
    internal int AddVertex(Vec3d position, Vec3d normal)
    {
        _positions.Add(position);
        _normals.Add(normal);
        return _positions.Count - 1;
    }

    /// <summary>Adds a triangle by vertex index.</summary>
    /// <param name="a">First vertex index.</param>
    /// <param name="b">Second vertex index.</param>
    /// <param name="c">Third vertex index.</param>
    /// <param name="faceIndex">The face to attribute it to.</param>
    internal void AddTriangle(int a, int b, int c, int faceIndex)
    {
        _indices.Add(a);
        _indices.Add(b);
        _indices.Add(c);
        _triangleFaces.Add(faceIndex);
    }

    /// <summary>Builds the immutable mesh.</summary>
    /// <param name="faces">The faces the triangle attribution indexes into.</param>
    internal MeshBuffer Build(ImmutableArray<SubEntity> faces) => new(
        [.. _positions],
        [.. _normals],
        [.. _indices],
        [.. _triangleFaces],
        faces);
}

/// <summary>A shape inside <see cref="FakeKernel"/>.</summary>
/// <remarks>
/// Immutable. Operations produce new shapes rather than mutating existing ones, which is what makes
/// the geometry cache in P3-T05 safe and makes a shape freely shareable across the rebuild.
/// </remarks>
internal sealed class FakeShape
{
    internal FakeShape(
        ulong tag,
        bool isSolid,
        FakeGeometry geometry,
        ImmutableArray<FakeEntity> faces,
        ImmutableArray<FakeEntity> edges,
        ImmutableArray<FakeEntity> vertices)
    {
        Tag = tag;
        IsSolid = isSolid;
        Geometry = geometry;
        Faces = faces;
        Edges = edges;
        Vertices = vertices;
    }

    /// <summary>Gets this shape's handle.</summary>
    internal ulong Tag { get; }

    /// <summary>Gets a value indicating whether this is a solid rather than a profile.</summary>
    internal bool IsSolid { get; }

    /// <summary>Gets the volumetric model.</summary>
    internal FakeGeometry Geometry { get; }

    /// <summary>Gets the faces, in canonical order.</summary>
    internal ImmutableArray<FakeEntity> Faces { get; }

    /// <summary>Gets the edges, in canonical order.</summary>
    internal ImmutableArray<FakeEntity> Edges { get; }

    /// <summary>Gets the vertices, in canonical order.</summary>
    internal ImmutableArray<FakeEntity> Vertices { get; }

    /// <summary>Gets the shape reference for this shape.</summary>
    internal KernelShape Reference => new(Tag);

    /// <summary>Gets entities of one kind.</summary>
    /// <param name="kind">The kind to get.</param>
    internal ImmutableArray<FakeEntity> EntitiesOf(SubEntityKind kind) => kind switch
    {
        SubEntityKind.Face => Faces,
        SubEntityKind.Edge => Edges,
        SubEntityKind.Vertex => Vertices,
        _ => [],
    };

    /// <summary>Converts an internal entity to the public reference form.</summary>
    /// <param name="entity">The entity.</param>
    internal SubEntity Reference2(FakeEntity entity) => new(Reference, entity.Tag, entity.Kind);

    /// <summary>Finds an entity by tag.</summary>
    /// <param name="tag">The entity tag.</param>
    /// <returns>The entity, or <see langword="null"/> if this shape has no such entity.</returns>
    internal FakeEntity? Find(ulong tag)
    {
        foreach (FakeEntity face in Faces)
        {
            if (face.Tag == tag)
            {
                return face;
            }
        }

        foreach (FakeEntity edge in Edges)
        {
            if (edge.Tag == tag)
            {
                return edge;
            }
        }

        foreach (FakeEntity vertex in Vertices)
        {
            if (vertex.Tag == tag)
            {
                return vertex;
            }
        }

        return null;
    }

    /// <summary>Gets the topology counts.</summary>
    internal TopologyCounts Counts => new(
        Solids: IsSolid ? 1 : 0,
        Shells: IsSolid ? 1 : 0,
        Faces: Faces.Length,
        Wires: Faces.Length,
        Edges: Edges.Length,
        Vertices: Vertices.Length);
}
