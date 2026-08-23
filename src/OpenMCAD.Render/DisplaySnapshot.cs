using System.Collections.Immutable;

using OpenMCAD.Kernel;
using OpenMCAD.Math;

namespace OpenMCAD.Render;

/// <summary>
/// An identifier written into the ID buffer and read back to resolve a pick.
/// </summary>
/// <remarks>
/// <para>
/// Dense and small on purpose. The ID pass renders to <c>R32_UINT</c>, so whatever a pick resolves
/// to has to survive a round trip through a single unsigned integer per pixel — there is no room
/// for a tag, an owner and a kind. The snapshot therefore assigns its own numbering and keeps the
/// mapping back to <see cref="SubEntity"/> beside it.
/// </para>
/// <para>
/// Snapshot-scoped, not durable. These numbers are reassigned on every snapshot and must never be
/// persisted, compared across snapshots, or used as a name — that is what <c>PersistentName</c>
/// exists for (PLAN.md 5.3). A pick resolves an id against the snapshot it was rendered from, and
/// nothing else.
/// </para>
/// </remarks>
/// <param name="Value">The raw value written to the ID buffer.</param>
public readonly record struct DisplayId(uint Value)
{
    /// <summary>Gets the id meaning "nothing here", which is what the ID buffer is cleared to.</summary>
    public static DisplayId None => default;

    /// <summary>Gets a value indicating whether this identifies anything.</summary>
    public bool IsSomething => Value != 0;

    /// <inheritdoc />
    public override string ToString() => Value == 0 ? "(none)" : $"#{Value}";
}

/// <summary>
/// Identifies a body within a snapshot.
/// </summary>
/// <param name="Value">The index of the body.</param>
public readonly record struct DisplayBodyId(int Value)
{
    /// <inheritdoc />
    public override string ToString() => $"body {Value}";
}

/// <summary>
/// The triangles of one body, ready to upload.
/// </summary>
/// <remarks>
/// <para>
/// Positions and normals are <see cref="float"/> and <b>relative to
/// <see cref="DisplaySnapshot.Origin"/></b>, never absolute. This is not a memory optimisation: a
/// single-precision float carries about seven significant decimal digits, so a part sitting a
/// kilometre from the world origin can resolve roughly a tenth of a millimetre — and a modelled
/// feature of ten microns disappears into the rounding. Subtracting a nearby origin first restores
/// the precision, and the origin is carried in <see cref="double"/> so nothing is lost.
/// </para>
/// <para>
/// Flat arrays rather than arrays of vectors, because this is upload-shaped: it is memcpy'd into a
/// vertex buffer, and an array of a struct with padding is not.
/// </para>
/// </remarks>
/// <param name="Positions">Three floats per vertex, relative to the snapshot origin, in metres.</param>
/// <param name="Normals">Three floats per vertex. Empty when the mesh carries none.</param>
/// <param name="Indices">Three indices per triangle.</param>
/// <param name="TriangleIds">The <see cref="DisplayId"/> of the face each triangle belongs to.</param>
public sealed record DisplayMesh(
    ImmutableArray<float> Positions,
    ImmutableArray<float> Normals,
    ImmutableArray<int> Indices,
    ImmutableArray<DisplayId> TriangleIds)
{
    /// <summary>Gets an empty mesh.</summary>
    public static DisplayMesh Empty { get; } = new([], [], [], []);

    /// <summary>Gets the number of vertices.</summary>
    public int VertexCount => Positions.Length / 3;

    /// <summary>Gets the number of triangles.</summary>
    public int TriangleCount => Indices.Length / 3;

    /// <summary>Gets a value indicating whether per-vertex normals are present.</summary>
    public bool HasNormals => !Normals.IsEmpty;
}

/// <summary>
/// The edges of one body, as polylines ready to upload.
/// </summary>
/// <remarks>
/// Edges are drawn as their own geometry rather than derived from the triangle mesh. A CAD edge is
/// a modelled entity that a user selects and dimensions to, and the boundary between two coplanar
/// triangles is not one — deriving edges from the mesh would both invent edges that do not exist
/// and lose the ones that do (PLAN.md 5.6).
/// </remarks>
/// <param name="Positions">Three floats per point, relative to the snapshot origin, in metres.</param>
/// <param name="Starts">The index into <paramref name="Positions"/> at which each polyline starts.</param>
/// <param name="Lengths">How many points each polyline has.</param>
/// <param name="Ids">The <see cref="DisplayId"/> of the edge each polyline represents.</param>
public sealed record DisplayEdges(
    ImmutableArray<float> Positions,
    ImmutableArray<int> Starts,
    ImmutableArray<int> Lengths,
    ImmutableArray<DisplayId> Ids)
{
    /// <summary>Gets an empty edge set.</summary>
    public static DisplayEdges Empty { get; } = new([], [], [], []);

    /// <summary>Gets the number of polylines.</summary>
    public int PolylineCount => Starts.Length;

    /// <summary>Gets the total number of points across all polylines.</summary>
    public int PointCount => Positions.Length / 3;
}

/// <summary>
/// One body as the renderer sees it.
/// </summary>
/// <param name="Id">Which body this is within the snapshot.</param>
/// <param name="Mesh">Its triangles.</param>
/// <param name="Edges">Its edges.</param>
/// <param name="Bounds">Its extent in world space, in metres, for culling and zoom-to-fit.</param>
public sealed record DisplayBody(
    DisplayBodyId Id,
    DisplayMesh Mesh,
    DisplayEdges Edges,
    Bounds3d Bounds);

/// <summary>
/// An immutable, versioned picture of everything the viewport should draw.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0008 and PLAN.md 4.2. The rebuild produces one of these; the render thread swaps to the
/// newest at frame start. <b>There is no lock between the two — only a reference swap</b>, and that
/// is the whole reason the viewport keeps its frame budget while a large rebuild runs. It works
/// only because everything reachable from here is immutable, so the render thread can read a
/// snapshot for as long as it likes while the next one is being built beside it.
/// </para>
/// <para>
/// Nothing here refers to a <see cref="KernelShape"/> or anything else the kernel owns. The
/// snapshot is a copy, deliberately: the render thread must never touch kernel state, and a
/// snapshot that held a handle would keep kernel memory alive for as long as a frame took to draw.
/// </para>
/// </remarks>
/// <param name="Version">
/// A monotonically increasing number. The render thread uses it to reject a snapshot older than
/// the one it already has, which can arrive when two rebuilds finish out of order.
/// </param>
/// <param name="Origin">
/// The point that float positions in this snapshot are measured from. See <see cref="DisplayMesh"/>
/// for why this exists.
/// </param>
/// <param name="Bodies">The bodies to draw.</param>
/// <param name="Entities">What each <see cref="DisplayId"/> resolves to.</param>
/// <param name="Bounds">The extent of everything, in world space and metres.</param>
public sealed record DisplaySnapshot(
    long Version,
    Vec3d Origin,
    ImmutableArray<DisplayBody> Bodies,
    ImmutableDictionary<DisplayId, SubEntity> Entities,
    Bounds3d Bounds)
{
    /// <summary>Gets the snapshot for an empty scene.</summary>
    /// <remarks>
    /// Version zero, so that any real snapshot supersedes it. The viewport starts here rather than
    /// with a null, which removes a branch from the frame loop and a whole class of first-frame bug.
    /// </remarks>
    public static DisplaySnapshot Empty { get; } = new(
        0,
        Vec3d.Zero,
        [],
        ImmutableDictionary<DisplayId, SubEntity>.Empty,
        default);

    /// <summary>Gets the total number of triangles across every body.</summary>
    public int TriangleCount
    {
        get
        {
            int total = 0;
            foreach (DisplayBody body in Bodies)
            {
                total += body.Mesh.TriangleCount;
            }

            return total;
        }
    }

    /// <summary>Resolves a picked id to the entity it names.</summary>
    /// <param name="id">The id read out of the ID buffer.</param>
    /// <returns>The entity, or <see cref="SubEntity.None"/> if the id names nothing.</returns>
    /// <remarks>
    /// An unknown id returns nothing rather than throwing. The ID buffer is read back
    /// asynchronously (P2-T07), so a pick can legitimately resolve against a snapshot that has
    /// since been replaced — a stale pick should select nothing, not fail.
    /// </remarks>
    public SubEntity Resolve(DisplayId id)
        => Entities.TryGetValue(id, out SubEntity entity) ? entity : SubEntity.None;

    /// <inheritdoc />
    public override string ToString()
        => $"snapshot {Version}: {Bodies.Length} bodies, {TriangleCount} triangles";
}
