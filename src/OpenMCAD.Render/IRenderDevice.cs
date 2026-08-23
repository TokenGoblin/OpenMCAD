namespace OpenMCAD.Render;

/// <summary>What a GPU buffer is going to be used for.</summary>
/// <remarks>
/// The renderer's intent, not the backend's vocabulary. D3D12 barely distinguishes these — a
/// buffer is a buffer and the view decides — but D3D11 and every other API do, and ADR-0008 asks
/// for an abstraction thin enough that a D3D11 fallback stays possible.
/// </remarks>
public enum GpuBufferKind
{
    /// <summary>Vertex data.</summary>
    Vertex,

    /// <summary>Index data.</summary>
    Index,

    /// <summary>Constants, read by shaders as a block.</summary>
    Constant,
}

/// <summary>A buffer living in GPU memory.</summary>
/// <remarks>
/// Opaque on purpose. Nothing above the render layer should be able to reach a native resource,
/// for the same reason nothing above the kernel layer can reach a <c>TopoDS_Shape</c>: a leaked
/// handle makes the abstraction unswappable.
/// </remarks>
public interface IGpuBuffer : IDisposable
{
    /// <summary>Gets what the buffer holds.</summary>
    GpuBufferKind Kind { get; }

    /// <summary>Gets its size in bytes.</summary>
    int ByteLength { get; }

    /// <summary>Gets a debug name, as it appears in a graphics debugger.</summary>
    string Name { get; }
}

/// <summary>Which adapter the renderer ended up on, and what it can do.</summary>
/// <param name="AdapterName">The adapter's description, for logs and support bundles.</param>
/// <param name="IsSoftware">
/// Whether this is a software rasteriser. True means WARP: correct, and far too slow for the
/// frame budgets in PLAN.md 7. Worth surfacing to the user rather than letting them conclude the
/// application is simply slow.
/// </param>
/// <param name="DedicatedVideoMemory">Dedicated video memory in bytes, zero for software.</param>
/// <param name="FeatureLevel">The Direct3D feature level, as a string for logging.</param>
public readonly record struct RenderDeviceInfo(
    string AdapterName,
    bool IsSoftware,
    long DedicatedVideoMemory,
    string FeatureLevel)
{
    /// <inheritdoc />
    public override string ToString()
        => IsSoftware
            ? $"{AdapterName} (software, {FeatureLevel})"
            : $"{AdapterName} ({FeatureLevel}, {DedicatedVideoMemory / (1024 * 1024)} MB)";
}

/// <summary>
/// The graphics device, as the rest of the renderer sees it (P2-T01).
/// </summary>
/// <remarks>
/// <para>
/// ADR-0008: "Abstract the RHI thinly (<c>IRenderDevice</c>) so a D3D11 fallback path is possible
/// for old hardware if telemetry ever demands it." Thin is the operative word. Descriptor heaps,
/// upload rings and fences are D3D12 concepts and are deliberately absent here — they are how the
/// D3D12 backend meets this contract, not part of the contract.
/// </para>
/// <para>
/// It is also small because only what exists is declared. Swapchains and frame submission arrive
/// with the viewport in P2-T02, when there is a window to present to and something to draw;
/// declaring them now would be designing against an imagined implementation, which is the same
/// mistake avoided in the plugin API (PLAN.md 5.12).
/// </para>
/// </remarks>
public interface IRenderDevice : IDisposable
{
    /// <summary>Gets what the renderer is running on.</summary>
    RenderDeviceInfo Info { get; }

    /// <summary>
    /// Creates a buffer in GPU memory and fills it.
    /// </summary>
    /// <param name="data">The contents.</param>
    /// <param name="kind">What it is for.</param>
    /// <param name="name">A debug name.</param>
    /// <returns>The buffer, owned by the caller.</returns>
    /// <remarks>
    /// Static: the contents do not change after creation. That is what a tessellated body is —
    /// the snapshot that produced it is immutable (ADR-0008), so a body that changes produces a
    /// new buffer rather than an updated one, and the old one is released when the frames still
    /// referencing it have finished.
    /// </remarks>
    IGpuBuffer CreateStaticBuffer(ReadOnlySpan<byte> data, GpuBufferKind kind, string name);

    /// <summary>Blocks until the GPU has finished everything submitted so far.</summary>
    /// <remarks>
    /// For shutdown and for resizing, not for frames. Calling this per frame would serialise the
    /// CPU and GPU and halve the frame rate, which is the mistake fences exist to avoid.
    /// </remarks>
    void WaitForIdle();
}
