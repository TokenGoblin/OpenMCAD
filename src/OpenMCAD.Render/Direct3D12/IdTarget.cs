using Vortice.Direct3D12;
using Vortice.DXGI;

namespace OpenMCAD.Render.Direct3D12;

/// <summary>
/// The off-screen buffer the ID pass writes display ids into (P2-T07).
/// </summary>
/// <remarks>
/// <para>
/// <b>R32_UINT, not a colour format.</b> An id is an index, not a colour: packing one into RGBA8
/// would cap a scene at sixteen million entities and, far worse, would let filtering or blending
/// average two ids into a third that names something else entirely. An integer target cannot be
/// blended at all, which is exactly the guarantee wanted here.
/// </para>
/// <para>
/// It carries its own depth buffer rather than sharing the viewport's. The two passes run at
/// different moments — the visible one every frame, this one only when something asks to pick —
/// so a shared buffer would either force them into lockstep or hand this pass a depth buffer from
/// a camera position that has since moved.
/// </para>
/// </remarks>
public sealed class IdTarget : IDisposable
{
    /// <summary>The format. One 32-bit unsigned integer per pixel.</summary>
    public const Format IdFormat = Format.R32_UInt;

    private readonly ID3D12Device _device;
    private readonly ID3D12DescriptorHeap _rtvHeap;
    private readonly DepthBuffer _depth;

    private ID3D12Resource? _resource;
    private bool _disposed;

    /// <summary>Creates the target.</summary>
    /// <param name="device">The device to allocate on.</param>
    /// <param name="width">Initial width in pixels. May be zero.</param>
    /// <param name="height">Initial height in pixels. May be zero.</param>
    public IdTarget(ID3D12Device device, int width = 0, int height = 0)
    {
        ArgumentNullException.ThrowIfNull(device);

        _device = device;

        _rtvHeap = device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.RenderTargetView, 1, DescriptorHeapFlags.None));

        _rtvHeap.Name = "id render target view heap";
        _depth = new DepthBuffer(device);

        if (width > 0 && height > 0)
        {
            Resize(width, height);
        }
    }

    /// <summary>Gets the current width in pixels, or zero before the first resize.</summary>
    public int Width { get; private set; }

    /// <summary>Gets the current height in pixels, or zero before the first resize.</summary>
    public int Height { get; private set; }

    /// <summary>Gets whether there is a buffer to render into.</summary>
    public bool IsAllocated => _resource is not null;

    /// <summary>Gets the depth buffer this target renders against.</summary>
    public DepthBuffer Depth => _depth;

    /// <summary>Gets the texture, for copying out of.</summary>
    /// <exception cref="InvalidOperationException">Nothing has been allocated yet.</exception>
    public ID3D12Resource Resource
        => _resource ?? throw new InvalidOperationException(
            "The ID target has no size yet. Resize it before using it.");

    /// <summary>Gets the view to bind.</summary>
    /// <exception cref="InvalidOperationException">Nothing has been allocated yet.</exception>
    public CpuDescriptorHandle View
        => _resource is null
            ? throw new InvalidOperationException(
                "The ID target has no size yet. Resize it before binding it.")
            : _rtvHeap.GetCPUDescriptorHandleForHeapStart();

    /// <summary>Gets the state the texture rests in between picks.</summary>
    /// <remarks>
    /// Left as a render target rather than returned to common, so a pick is two barriers rather
    /// than four and every pick starts from the same known state.
    /// </remarks>
    public static ResourceStates RestingState => ResourceStates.RenderTarget;

    /// <summary>Reallocates for a new size, doing nothing if the size has not changed.</summary>
    /// <param name="width">The new width in pixels.</param>
    /// <param name="height">The new height in pixels.</param>
    /// <remarks>The caller must have waited for the GPU; this releases the old texture.</remarks>
    public void Resize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (width == Width && height == Height && _resource is not null)
        {
            return;
        }

        _resource?.Dispose();

        _resource = _device.CreateCommittedResource(
            HeapType.Default,
            HeapFlags.None,
            ResourceDescription.Texture2D(
                IdFormat,
                (uint)width,
                (uint)height,
                arraySize: 1,
                mipLevels: 1,
                sampleCount: 1,
                sampleQuality: 0,
                flags: ResourceFlags.AllowRenderTarget),
            RestingState,
            new ClearValue(IdFormat, new Vortice.Mathematics.Color4(0, 0, 0, 0)));

        _resource.Name = $"id buffer {width}x{height}";

        _device.CreateRenderTargetView(_resource, null, _rtvHeap.GetCPUDescriptorHandleForHeapStart());
        _depth.Resize(width, height);

        Width = width;
        Height = height;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _resource?.Dispose();
        _resource = null;
        _depth.Dispose();
        _rtvHeap.Dispose();
    }
}
