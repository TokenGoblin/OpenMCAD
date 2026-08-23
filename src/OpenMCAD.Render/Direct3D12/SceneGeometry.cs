using System.Runtime.InteropServices;

using OpenMCAD.Math;

using Vortice.Direct3D12;
using Vortice.DXGI;

namespace OpenMCAD.Render.Direct3D12;

/// <summary>
/// The GPU-side buffers for one <see cref="DisplaySnapshot"/> (P2-T05).
/// </summary>
/// <remarks>
/// <para>
/// A snapshot is immutable and versioned, so the buffers for one are too: rather than editing them
/// in place, a new version builds a new set and the old one is released once the GPU has finished
/// with it. That is what makes a rebuild safe to run while frames are still being drawn from the
/// previous snapshot (ADR-0008).
/// </para>
/// <para>
/// <b>Positions and normals go in separate vertex buffers rather than one interleaved stream.</b>
/// Interleaving is marginally friendlier to the vertex cache, but <see cref="DisplayMesh"/> holds
/// them as separate flat arrays precisely so they can be memcpy'd, and repacking every vertex on
/// the CPU to save a few cache lines would cost more on the upload than it recovers on the draw.
/// </para>
/// </remarks>
public sealed class SceneGeometry : IDisposable
{
    /// <summary>Bytes per position or normal element: three floats.</summary>
    public const uint VectorStride = 12;

    /// <summary>Bytes per edge segment: two endpoints, three floats each.</summary>
    public const uint SegmentStride = 24;

    private readonly List<BodyGeometry> _bodies = [];
    private bool _disposed;

    private SceneGeometry(long version, Bounds3d bounds, Vec3d origin)
    {
        Version = version;
        Bounds = bounds;
        Origin = origin;
    }

    /// <summary>Gets the snapshot version these buffers were built from.</summary>
    public long Version { get; }

    /// <summary>Gets the world-space extent of everything, for camera fitting.</summary>
    public Bounds3d Bounds { get; }

    /// <summary>Gets the point the float positions in these buffers are measured from.</summary>
    public Vec3d Origin { get; }

    /// <summary>Gets the bodies, in snapshot order.</summary>
    public IReadOnlyList<BodyGeometry> Bodies => _bodies;

    /// <summary>Gets the total number of triangles across every body.</summary>
    public int TriangleCount
    {
        get
        {
            int total = 0;

            foreach (BodyGeometry body in _bodies)
            {
                total += body.IndexCount / 3;
            }

            return total;
        }
    }

    /// <summary>Gets the total number of edge segments across every body.</summary>
    public int SegmentCount
    {
        get
        {
            int total = 0;

            foreach (BodyGeometry body in _bodies)
            {
                total += body.SegmentCount;
            }

            return total;
        }
    }

    /// <summary>
    /// Uploads a snapshot.
    /// </summary>
    /// <param name="device">The device to allocate on.</param>
    /// <param name="snapshot">What to upload.</param>
    /// <returns>The buffers, which the caller owns.</returns>
    /// <remarks>
    /// Bodies with no triangles are skipped rather than given a zero-length buffer, which D3D12
    /// will not create. A body can legitimately have none: a sketch, or a solid whose tessellation
    /// has not caught up with the last edit.
    /// </remarks>
    public static SceneGeometry Upload(D3D12RenderDevice device, DisplaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(snapshot);

        SceneGeometry scene = new(snapshot.Version, snapshot.Bounds, snapshot.Origin);

        try
        {
            foreach (DisplayBody body in snapshot.Bodies)
            {
                // A body with neither triangles nor edges has nothing to upload, and D3D12 will
                // not create a zero-length buffer. One with edges but no faces is a wireframe and
                // is perfectly legitimate.
                if (body.Mesh.TriangleCount == 0 && body.Edges.PolylineCount == 0)
                {
                    continue;
                }

                scene._bodies.Add(UploadBody(device, body));
            }
        }
        catch
        {
            // A failure halfway through would otherwise leak every buffer uploaded before it.
            scene.Dispose();
            throw;
        }

        return scene;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (BodyGeometry body in _bodies)
        {
            body.Dispose();
        }

        _bodies.Clear();
    }

    /// <summary>
    /// Flattens polylines into independent segments, two endpoints each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Segments rather than strips because the edge pass draws each one as its own quad: a strip
    /// would share vertices between segments that need different screen-space orientations, and
    /// the joins would pinch. The duplication is two floats per interior point, which is nothing
    /// against the clarity of one instance per segment.
    /// </para>
    /// <para>
    /// <b>Malformed input yields fewer segments rather than an exception.</b>
    /// <see cref="DisplayEdges"/> is a public record with no enforced invariants, and it can reach
    /// here from a plugin as easily as from <see cref="SnapshotBuilder"/>. This runs inside the
    /// frame loop, where an <see cref="IndexOutOfRangeException"/> does not report a bad snapshot —
    /// it takes the window down. A polyline that does not lie inside the position array is skipped
    /// on the same reasoning as a polyline of one point.
    /// </para>
    /// </remarks>
    internal static float[] SegmentsOf(DisplayEdges edges)
    {
        ArgumentNullException.ThrowIfNull(edges);

        int pointCount = edges.PointCount;
        int polylines = System.Math.Min(edges.PolylineCount, edges.Lengths.Length);
        int segments = 0;

        for (int i = 0; i < polylines; ++i)
        {
            segments += SpanOf(edges, i, pointCount);
        }

        if (segments == 0)
        {
            return [];
        }

        float[] result = new float[segments * 6];
        int at = 0;

        for (int i = 0; i < polylines; ++i)
        {
            int start = edges.Starts[i];
            int length = SpanOf(edges, i, pointCount) + 1;

            for (int j = 0; j + 1 < length; ++j)
            {
                int a = (start + j) * 3;
                int b = (start + j + 1) * 3;

                result[at++] = edges.Positions[a];
                result[at++] = edges.Positions[a + 1];
                result[at++] = edges.Positions[a + 2];
                result[at++] = edges.Positions[b];
                result[at++] = edges.Positions[b + 1];
                result[at++] = edges.Positions[b + 2];
            }
        }

        return result;
    }

    /// <summary>How many segments polyline <paramref name="index"/> contributes, or zero.</summary>
    /// <remarks>
    /// Zero for a polyline of one point, a negative length, a negative start, or a span running
    /// past the end of the positions. Counted and copied through the same function so the two
    /// passes cannot disagree about how large the buffer should be.
    /// </remarks>
    private static int SpanOf(DisplayEdges edges, int index, int pointCount)
    {
        int start = edges.Starts[index];
        int length = edges.Lengths[index];

        if (start < 0 || length < 2 || start + length > pointCount)
        {
            return 0;
        }

        return length - 1;
    }

    /// <summary>
    /// Uploads one body's buffers, releasing any of them if a later one cannot be allocated.
    /// </summary>
    /// <remarks>
    /// The tidy-up in <see cref="Upload"/> only reaches bodies already added to the list, so a
    /// failure part-way through this method would strand whatever it had already created. That is
    /// most likely to happen exactly when it hurts — out of video memory on a large assembly.
    /// </remarks>
    private static BodyGeometry UploadBody(D3D12RenderDevice device, DisplayBody body)
    {
        DisplayMesh mesh = body.Mesh;
        string name = $"body {body.Id.Value}";

        IGpuBuffer? edgeBuffer = null;
        IGpuBuffer? positions = null;
        IGpuBuffer? normals = null;
        IGpuBuffer? indices = null;

        try
        {
            float[] segments = SegmentsOf(body.Edges);

            if (segments.Length > 0)
            {
                edgeBuffer = device.CreateStaticBuffer(
                    MemoryMarshal.AsBytes(segments.AsSpan()), GpuBufferKind.Vertex, $"{name} edges");
            }

            if (mesh.TriangleCount == 0)
            {
                // Edges only. Nothing for the face pass, so no face buffers are allocated.
                return new BodyGeometry(body.Id, null, null, null, 0, 0, edgeBuffer, body.Bounds);
            }

            positions = device.CreateStaticBuffer(
                MemoryMarshal.AsBytes(mesh.Positions.AsSpan()),
                GpuBufferKind.Vertex,
                $"{name} positions");

            if (mesh.HasNormals)
            {
                normals = device.CreateStaticBuffer(
                    MemoryMarshal.AsBytes(mesh.Normals.AsSpan()),
                    GpuBufferKind.Vertex,
                    $"{name} normals");
            }
            else
            {
                // A zero normal is the signal the pixel shader watches for; it reconstructs the
                // facet normal from screen-space derivatives instead. Uploading zeroes rather than
                // binding nothing keeps one pipeline state and one input layout across both cases.
                float[] zeroes = new float[mesh.VertexCount * 3];

                normals = device.CreateStaticBuffer(
                    MemoryMarshal.AsBytes(zeroes.AsSpan()),
                    GpuBufferKind.Vertex,
                    $"{name} normals (absent)");
            }

            indices = device.CreateStaticBuffer(
                MemoryMarshal.AsBytes(mesh.Indices.AsSpan()),
                GpuBufferKind.Index,
                $"{name} indices");

            return new BodyGeometry(
                body.Id,
                positions,
                normals,
                indices,
                mesh.Indices.Length,
                mesh.VertexCount,
                edgeBuffer,
                body.Bounds);
        }
        catch
        {
            indices?.Dispose();
            normals?.Dispose();
            positions?.Dispose();
            edgeBuffer?.Dispose();
            throw;
        }
    }
}


/// <summary>One body's buffers.</summary>
/// <remarks>
/// The views are computed once at construction rather than per frame. A vertex buffer view is only
/// three integers, but building them per body per frame across a large assembly is avoidable work
/// in the hottest loop there is.
/// </remarks>
public sealed class BodyGeometry : IDisposable
{
    private readonly IGpuBuffer? _positions;
    private readonly IGpuBuffer? _normals;
    private readonly IGpuBuffer? _indices;
    private readonly IGpuBuffer? _edges;

    private bool _disposed;

    internal BodyGeometry(
        DisplayBodyId id,
        IGpuBuffer? positions,
        IGpuBuffer? normals,
        IGpuBuffer? indices,
        int indexCount,
        int vertexCount,
        IGpuBuffer? edges,
        Bounds3d bounds)
    {
        _positions = positions;
        _normals = normals;
        _indices = indices;
        _edges = edges;

        Id = id;
        IndexCount = indexCount;
        VertexCount = vertexCount;
        Bounds = bounds;

        if (positions is not null && normals is not null && indices is not null)
        {
            PositionView = new VertexBufferView(
                D3D12RenderDevice.ResourceOf(positions).GPUVirtualAddress,
                (uint)positions.ByteLength,
                SceneGeometry.VectorStride);

            NormalView = new VertexBufferView(
                D3D12RenderDevice.ResourceOf(normals).GPUVirtualAddress,
                (uint)normals.ByteLength,
                SceneGeometry.VectorStride);

            IndexView = new IndexBufferView(
                D3D12RenderDevice.ResourceOf(indices).GPUVirtualAddress,
                (uint)indices.ByteLength,
                Format.R32_UInt);
        }

        if (edges is not null)
        {
            SegmentCount = edges.ByteLength / (int)SceneGeometry.SegmentStride;

            EdgeSegmentView = new VertexBufferView(
                D3D12RenderDevice.ResourceOf(edges).GPUVirtualAddress,
                (uint)edges.ByteLength,
                SceneGeometry.SegmentStride);
        }
    }

    /// <summary>Gets which body this is.</summary>
    public DisplayBodyId Id { get; }

    /// <summary>Gets how many indices to draw, or zero for an edges-only body.</summary>
    public int IndexCount { get; }

    /// <summary>Gets how many vertices the face buffers hold.</summary>
    public int VertexCount { get; }

    /// <summary>Gets how many edge segments to draw.</summary>
    public int SegmentCount { get; }

    /// <summary>Gets whether there are triangles to draw.</summary>
    public bool HasFaces => IndexCount > 0;

    /// <summary>Gets the body's world-space extent, for culling.</summary>
    public Bounds3d Bounds { get; }

    /// <summary>Gets the view binding positions to slot 0.</summary>
    public VertexBufferView PositionView { get; }

    /// <summary>Gets the view binding normals to slot 1.</summary>
    public VertexBufferView NormalView { get; }

    /// <summary>Gets the index buffer view.</summary>
    public IndexBufferView IndexView { get; }

    /// <summary>Gets the view binding edge segments as per-instance data.</summary>
    public VertexBufferView EdgeSegmentView { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _positions?.Dispose();
        _normals?.Dispose();
        _indices?.Dispose();
        _edges?.Dispose();
    }
}
