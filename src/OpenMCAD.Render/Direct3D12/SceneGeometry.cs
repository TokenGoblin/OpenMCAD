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
                if (body.Mesh.TriangleCount == 0)
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

    private static BodyGeometry UploadBody(D3D12RenderDevice device, DisplayBody body)
    {
        DisplayMesh mesh = body.Mesh;
        string name = $"body {body.Id.Value}";

        IGpuBuffer positions = device.CreateStaticBuffer(
            MemoryMarshal.AsBytes(mesh.Positions.AsSpan()),
            GpuBufferKind.Vertex,
            $"{name} positions");

        IGpuBuffer normals;

        if (mesh.HasNormals)
        {
            normals = device.CreateStaticBuffer(
                MemoryMarshal.AsBytes(mesh.Normals.AsSpan()),
                GpuBufferKind.Vertex,
                $"{name} normals");
        }
        else
        {
            // A zero normal is the signal the pixel shader watches for; it reconstructs the facet
            // normal from screen-space derivatives instead. Uploading zeroes rather than binding
            // nothing keeps one pipeline state and one input layout across both cases.
            float[] zeroes = new float[mesh.VertexCount * 3];

            normals = device.CreateStaticBuffer(
                MemoryMarshal.AsBytes(zeroes.AsSpan()),
                GpuBufferKind.Vertex,
                $"{name} normals (absent)");
        }

        IGpuBuffer indices = device.CreateStaticBuffer(
            MemoryMarshal.AsBytes(mesh.Indices.AsSpan()),
            GpuBufferKind.Index,
            $"{name} indices");

        return new BodyGeometry(
            body.Id, positions, normals, indices, mesh.Indices.Length, mesh.VertexCount, body.Bounds);
    }
}

/// <summary>One body's buffers.</summary>
/// <remarks>
/// The views are computed once at construction rather than per frame. A vertex buffer view is only
/// three integers, but building three of them per body per frame across a large assembly is
/// avoidable work in the hottest loop there is.
/// </remarks>
public sealed class BodyGeometry : IDisposable
{
    private readonly IGpuBuffer _positions;
    private readonly IGpuBuffer _normals;
    private readonly IGpuBuffer _indices;

    private bool _disposed;

    internal BodyGeometry(
        DisplayBodyId id,
        IGpuBuffer positions,
        IGpuBuffer normals,
        IGpuBuffer indices,
        int indexCount,
        int vertexCount,
        Bounds3d bounds)
    {
        _positions = positions;
        _normals = normals;
        _indices = indices;

        Id = id;
        IndexCount = indexCount;
        VertexCount = vertexCount;
        Bounds = bounds;

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

    /// <summary>Gets which body this is.</summary>
    public DisplayBodyId Id { get; }

    /// <summary>Gets how many indices to draw.</summary>
    public int IndexCount { get; }

    /// <summary>Gets how many vertices the buffers hold.</summary>
    public int VertexCount { get; }

    /// <summary>Gets the body's world-space extent, for culling.</summary>
    public Bounds3d Bounds { get; }

    /// <summary>Gets the view binding positions to slot 0.</summary>
    public VertexBufferView PositionView { get; }

    /// <summary>Gets the view binding normals to slot 1.</summary>
    public VertexBufferView NormalView { get; }

    /// <summary>Gets the index buffer view.</summary>
    public IndexBufferView IndexView { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _positions.Dispose();
        _normals.Dispose();
        _indices.Dispose();
    }
}
