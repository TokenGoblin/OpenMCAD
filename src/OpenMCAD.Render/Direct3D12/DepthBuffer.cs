using Vortice.Direct3D12;
using Vortice.DXGI;

namespace OpenMCAD.Render.Direct3D12;

/// <summary>
/// The depth buffer the face pass writes through (P2-T05).
/// </summary>
/// <remarks>
/// <para>
/// Kept apart from <see cref="SwapChainTarget"/> deliberately. The swap chain is about
/// presentation and is owned by the window; depth is a property of a pass and is not presented at
/// all. Offscreen rendering — the ID pass of P2-T07, and the headless tests — needs depth without
/// a swap chain anywhere in sight.
/// </para>
/// <para>
/// <b>The optimised clear value must match what the pass actually clears to.</b> A mismatch is not
/// an error; it is a silent loss of the fast-clear path, and on tiled hardware that is a
/// measurable cost for no reason. Both live on this type so they cannot drift apart.
/// </para>
/// </remarks>
public sealed class DepthBuffer : IDisposable
{
    /// <summary>The format. 32-bit float, no stencil.</summary>
    /// <remarks>
    /// A CAD scene spans a far wider depth range than a game level, and 24-bit depth runs out of
    /// resolution across it — the symptom is coplanar faces flickering against one another as the
    /// camera moves. Nothing here needs stencil, and a D24_UNorm_S8 buffer would spend a byte per
    /// pixel on it.
    /// </remarks>
    public const Format DepthFormat = Format.D32_Float;

    /// <summary>The value a cleared depth buffer holds: the far plane.</summary>
    public const float ClearDepth = 1.0f;

    private readonly ID3D12Device _device;
    private readonly ID3D12DescriptorHeap _heap;

    private ID3D12Resource? _resource;
    private bool _disposed;

    /// <summary>Creates a depth buffer.</summary>
    /// <param name="device">The device to allocate on.</param>
    /// <param name="width">Initial width in pixels. May be zero.</param>
    /// <param name="height">Initial height in pixels. May be zero.</param>
    public DepthBuffer(ID3D12Device device, int width = 0, int height = 0)
    {
        ArgumentNullException.ThrowIfNull(device);

        _device = device;

        _heap = device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.DepthStencilView, 1, DescriptorHeapFlags.None));

        _heap.Name = "depth stencil view heap";

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

    /// <summary>Gets the view to bind.</summary>
    /// <exception cref="InvalidOperationException">Nothing has been allocated yet.</exception>
    public CpuDescriptorHandle View
        => _resource is null
            ? throw new InvalidOperationException(
                "The depth buffer has no size yet. Resize it before binding it.")
            : _heap.GetCPUDescriptorHandleForHeapStart();

    /// <summary>
    /// Reallocates for a new size, doing nothing if the size has not changed.
    /// </summary>
    /// <param name="width">The new width in pixels.</param>
    /// <param name="height">The new height in pixels.</param>
    /// <remarks>
    /// The caller must have waited for the GPU first. This releases the old texture, and releasing
    /// one the GPU is still reading is a use-after-free that reappears later as a device-removed
    /// error with no connection to the resize that caused it.
    /// </remarks>
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
                DepthFormat,
                (uint)width,
                (uint)height,
                arraySize: 1,
                mipLevels: 1,
                sampleCount: 1,
                sampleQuality: 0,
                flags: ResourceFlags.AllowDepthStencil),
            ResourceStates.DepthWrite,
            new ClearValue(DepthFormat, ClearDepth, 0));

        _resource.Name = $"depth {width}x{height}";

        _device.CreateDepthStencilView(
            _resource, null, _heap.GetCPUDescriptorHandleForHeapStart());

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
        _heap.Dispose();
    }
}
