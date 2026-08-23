using System.Collections.Immutable;

using OpenMCAD.Kernel;
using OpenMCAD.Math;

namespace OpenMCAD.Render;

/// <summary>
/// Turns kernel tessellation into a <see cref="DisplaySnapshot"/> (P2-T04).
/// </summary>
/// <remarks>
/// <para>
/// One body at a time, then <see cref="Build"/>. The builder is not thread-safe and is not meant
/// to be: a snapshot is produced by one rebuild, and sharing a builder across rebuilds would
/// interleave two scenes.
/// </para>
/// <para>
/// This runs on a worker thread, never on the kernel thread and never on the render thread. It is
/// pure data transformation — no kernel handles are retained and no GPU resources are touched — so
/// it can take as long as it takes without costing anyone a frame.
/// </para>
/// </remarks>
public sealed class SnapshotBuilder
{
    /// <summary>
    /// How coarsely a freshly chosen render origin is rounded, in metres.
    /// </summary>
    /// <remarks>
    /// Moving the origin invalidates every position in the snapshot, so every vertex buffer has to
    /// be re-uploaded. Recomputing it exactly from the scene bounds would move it on almost every
    /// edit. Rounding to a metre keeps it near the geometry — a body within a few metres of the
    /// origin resolves well under a micron, far below the modelling tolerance — while making the
    /// value one of a small set rather than an arbitrary number.
    /// </remarks>
    public const double OriginGrid = 1.0;

    /// <summary>
    /// How far the scene may drift from the current origin before a new one is chosen, in metres.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rounding alone does not achieve what it appears to. A grid still has a boundary at every
    /// line, and a scene centred near one flips between two origins on an edit far smaller than
    /// the grid: a body spanning 10 to 11 metres centres on 10.5, and moving it one millimetre
    /// rounds to 11 instead of 10 and re-uploads everything. A test written to demonstrate the
    /// rounding is what caught it.
    /// </para>
    /// <para>
    /// So the origin is sticky. It is kept until the scene has genuinely moved away from it, which
    /// is the property that was wanted in the first place — rounding only decides where a new one
    /// lands. The threshold exceeds the grid so that no amount of jitter around a boundary can
    /// oscillate it.
    /// </para>
    /// </remarks>
    public const double OriginHysteresis = 2.0 * OriginGrid;

    private readonly List<PendingBody> _bodies = [];
    private readonly ImmutableDictionary<DisplayId, SubEntity>.Builder _entities =
        ImmutableDictionary.CreateBuilder<DisplayId, SubEntity>();

    private Bounds3d _bounds = Bounds3d.Empty;
    private uint _nextId = 1;

    /// <summary>Gets the number of bodies added so far.</summary>
    public int BodyCount => _bodies.Count;

    /// <summary>
    /// Adds a tessellated body.
    /// </summary>
    /// <param name="mesh">The kernel's tessellation of it.</param>
    /// <returns>The id the body will have in the snapshot.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mesh"/> is null.</exception>
    /// <exception cref="ArgumentException">The mesh is internally inconsistent.</exception>
    public DisplayBodyId Add(MeshBuffer mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        // Checked here rather than trusted, because the failure is otherwise invisible: an
        // out-of-range face index produces a pick that resolves to the wrong entity, or to
        // nothing, in a way that looks like a renderer bug and is not.
        if (mesh.TriangleFaces.Length != mesh.TriangleCount)
        {
            throw new ArgumentException(
                $"The mesh has {mesh.TriangleCount} triangles but {mesh.TriangleFaces.Length} "
                + "face attributions. Every triangle must name the face it came from, or picking "
                + "cannot work.",
                nameof(mesh));
        }

        if (!mesh.Normals.IsEmpty && mesh.Normals.Length != mesh.Positions.Length)
        {
            throw new ArgumentException(
                $"The mesh has {mesh.Positions.Length} vertices but {mesh.Normals.Length} normals.",
                nameof(mesh));
        }

        // One display id per face of this body, in the order the kernel enumerated them, so the
        // mapping is as stable as the kernel's canonical ordering is (ADR-0011).
        DisplayId[] faceIds = new DisplayId[mesh.Faces.Length];
        for (int i = 0; i < mesh.Faces.Length; ++i)
        {
            faceIds[i] = new DisplayId(_nextId++);
            _entities[faceIds[i]] = mesh.Faces[i];
        }

        DisplayId[] triangleIds = new DisplayId[mesh.TriangleCount];
        for (int i = 0; i < triangleIds.Length; ++i)
        {
            int face = mesh.TriangleFaces[i];
            if (face < 0 || face >= faceIds.Length)
            {
                throw new ArgumentException(
                    $"Triangle {i} names face {face}, but the mesh has {faceIds.Length} faces.",
                    nameof(mesh));
            }

            triangleIds[i] = faceIds[face];
        }

        DisplayBodyId id = new(_bodies.Count);
        Bounds3d bodyBounds = mesh.Bounds;

        _bodies.Add(new PendingBody(id, mesh, [.. triangleIds], bodyBounds));
        _bounds = _bounds.IsEmpty ? bodyBounds : Bounds3d.Union(_bounds, bodyBounds);

        return id;
    }

    /// <summary>
    /// Produces the snapshot.
    /// </summary>
    /// <param name="version">
    /// The version to stamp it with. Must increase between snapshots of the same scene, or
    /// <see cref="SnapshotHolder.Publish"/> will discard it as stale.
    /// </param>
    /// <returns>The snapshot.</returns>
    /// <param name="previousOrigin">
    /// The origin of the snapshot this one replaces, if there is one. Passing it lets the origin
    /// stay put across an edit, which is what avoids re-uploading every vertex buffer; passing
    /// nothing chooses a fresh one.
    /// </param>
    /// <remarks>
    /// The origin is chosen here rather than per body, because a single origin for the whole
    /// snapshot is what lets the renderer use one view matrix. Bodies far apart in a large
    /// assembly therefore share it, and precision degrades with distance from it — an assembly
    /// spanning kilometres would need per-body origins, which is a Phase 6 problem and is called
    /// out in the Phase 2 notes rather than pre-solved here.
    /// </remarks>
    public DisplaySnapshot Build(long version, Vec3d? previousOrigin = null)
    {
        Vec3d origin = ChooseOrigin(_bounds, previousOrigin);

        ImmutableArray<DisplayBody>.Builder bodies =
            ImmutableArray.CreateBuilder<DisplayBody>(_bodies.Count);

        foreach (PendingBody pending in _bodies)
        {
            bodies.Add(new DisplayBody(
                pending.Id,
                new DisplayMesh(
                    Relative(pending.Mesh.Positions, origin),
                    Direction(pending.Mesh.Normals),
                    pending.Mesh.Indices,
                    pending.TriangleIds),

                // Edges need a kernel operation that does not exist yet: `triangulate` returns
                // faces only, and a CAD edge is a modelled entity rather than something to be
                // recovered from the triangle mesh (see DisplayEdges). Left empty rather than
                // approximated, because an approximated edge is one a user can select and
                // dimension to, and it would be wrong.
                DisplayEdges.Empty,
                pending.Bounds));
        }

        return new DisplaySnapshot(
            version,
            origin,
            bodies.MoveToImmutable(),
            _entities.ToImmutable(),
            _bounds);
    }

    /// <summary>
    /// Picks the point that float positions are measured from, keeping the previous one where it
    /// still serves.
    /// </summary>
    /// <param name="bounds">The extent of the scene.</param>
    /// <param name="previous">The origin currently in use, if any.</param>
    /// <returns>The origin.</returns>
    internal static Vec3d ChooseOrigin(Bounds3d bounds, Vec3d? previous = null)
    {
        if (bounds.IsEmpty)
        {
            // Nothing to be near. Keeping the previous origin would be equally valid and would
            // avoid a needless move when the last body is deleted and another is added back.
            return previous ?? Vec3d.Zero;
        }

        Vec3d centre = bounds.Center;

        if (previous is Vec3d held && (centre - held).Length <= OriginHysteresis)
        {
            return held;
        }

        return new Vec3d(
            System.Math.Round(centre.X / OriginGrid) * OriginGrid,
            System.Math.Round(centre.Y / OriginGrid) * OriginGrid,
            System.Math.Round(centre.Z / OriginGrid) * OriginGrid);
    }

    /// <summary>Flattens positions to floats measured from the origin.</summary>
    /// <param name="points">The world positions, in metres.</param>
    /// <param name="origin">The point to measure from.</param>
    /// <returns>Three floats per point.</returns>
    /// <remarks>
    /// The subtraction happens in <see cref="double"/> and only the result is narrowed. Doing it
    /// the other way round — narrowing first and subtracting in float — would discard exactly the
    /// precision this exists to keep.
    /// </remarks>
    private static ImmutableArray<float> Relative(ImmutableArray<Vec3d> points, Vec3d origin)
    {
        ImmutableArray<float>.Builder values =
            ImmutableArray.CreateBuilder<float>(points.Length * 3);

        foreach (Vec3d point in points)
        {
            values.Add((float)(point.X - origin.X));
            values.Add((float)(point.Y - origin.Y));
            values.Add((float)(point.Z - origin.Z));
        }

        return values.MoveToImmutable();
    }

    /// <summary>Flattens directions to floats.</summary>
    /// <param name="directions">The directions.</param>
    /// <returns>Three floats per direction.</returns>
    /// <remarks>
    /// No origin: a direction is a difference already, so translating it would be wrong. This is
    /// the reason normals get their own method rather than sharing the one above with a zero
    /// origin — the distinction is easy to lose and produces lighting that is subtly wrong
    /// everywhere rather than obviously wrong somewhere.
    /// </remarks>
    private static ImmutableArray<float> Direction(ImmutableArray<Vec3d> directions)
    {
        if (directions.IsEmpty)
        {
            return [];
        }

        ImmutableArray<float>.Builder values =
            ImmutableArray.CreateBuilder<float>(directions.Length * 3);

        foreach (Vec3d direction in directions)
        {
            values.Add((float)direction.X);
            values.Add((float)direction.Y);
            values.Add((float)direction.Z);
        }

        return values.MoveToImmutable();
    }

    private sealed record PendingBody(
        DisplayBodyId Id,
        MeshBuffer Mesh,
        ImmutableArray<DisplayId> TriangleIds,
        Bounds3d Bounds);
}
