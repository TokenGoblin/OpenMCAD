using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace OpenMCAD.Render.Direct3D12;

/// <summary>
/// A multisampled colour and depth pair that the scene is drawn into and then resolved (P2-T12).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the whole scene rather than a post-process.</b> The aliasing that matters in CAD is on
/// geometric silhouettes — the outline of a boss against the background, two faces meeting at a
/// shallow angle. A post-process filter such as FXAA works from the finished image and can only
/// guess where an edge was, which softens text and fine detail while never quite fixing the
/// staircase it was aimed at. Multisampling knows where the triangle boundary actually is, because
/// it asks the rasteriser.
/// </para>
/// <para>
/// <b>The ID buffer is deliberately not multisampled.</b> Picking wants the id of one entity at
/// one pixel; resolving several samples would average integers that are indices, producing a
/// number naming a third entity that is under the cursor nowhere. It keeps its own single-sampled
/// target for that reason.
/// </para>
/// <para>
/// The sample count is negotiated rather than assumed. A device that cannot do four falls back
/// through two to one, and one is a legitimate answer — the renderer keeps working, just without
/// smoothing.
/// </para>
/// </remarks>
public sealed class MsaaTarget : IDisposable
{
    /// <summary>The sample count asked for when nothing else is specified.</summary>
    /// <remarks>
    /// Four. Two is visibly better than none but still leaves a staircase on near-horizontal
    /// edges, and eight costs twice the memory and bandwidth of four for a difference most people
    /// cannot see on a part.
    /// </remarks>
    public const int DefaultSampleCount = 4;

    private readonly ID3D12Device _device;
    private readonly ID3D12DescriptorHeap _rtvHeap;
    private readonly ID3D12DescriptorHeap _dsvHeap;

    private Color4 _clearColour;

    private ID3D12Resource? _colour;
    private ID3D12Resource? _depth;
    private bool _disposed;

    /// <summary>Creates the target, negotiating the sample count with the device.</summary>
    /// <param name="device">The device to allocate on.</param>
    /// <param name="clearColour">
    /// The colour this is cleared to. Recorded so the optimised clear value matches what the pass
    /// actually clears to; a mismatch silently loses the fast-clear path.
    /// </param>
    /// <param name="requestedSamples">How many samples to ask for.</param>
    public MsaaTarget(
        ID3D12Device device,
        Color4 clearColour,
        int requestedSamples = DefaultSampleCount)
    {
        ArgumentNullException.ThrowIfNull(device);

        _device = device;
        _clearColour = clearColour;
        SampleCount = Negotiate(device, requestedSamples);

        // Both created before either is assigned. Assigning the first and then throwing on the
        // second would strand its COM reference for the life of the process: the constructor never
        // completes, so Dispose is never called and there is no finalizer to catch it.
        ID3D12DescriptorHeap rtv = device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.RenderTargetView, 1, DescriptorHeapFlags.None));

        ID3D12DescriptorHeap dsv;

        try
        {
            dsv = device.CreateDescriptorHeap(new DescriptorHeapDescription(
                DescriptorHeapType.DepthStencilView, 1, DescriptorHeapFlags.None));
        }
        catch
        {
            rtv.Dispose();
            throw;
        }

        _rtvHeap = rtv;
        _dsvHeap = dsv;
        _rtvHeap.Name = "msaa render target view heap";
        _dsvHeap.Name = "msaa depth stencil view heap";
    }

    /// <summary>Gets or sets the colour this target is cleared to.</summary>
    /// <remarks>
    /// Changing it discards the textures so they can be recreated with a matching optimised clear
    /// value. A committed resource's clear value is fixed when it is created, so a target whose
    /// colour was changed underneath it would be cleared to one value and optimised for another —
    /// which is not an error, just a silent loss of the fast-clear path and a debug-layer warning
    /// about a mismatch. The viewport's background is a legitimate thing for an application to
    /// change, so it cannot simply be made read-only.
    /// </remarks>
    public Color4 ClearColour
    {
        get => _clearColour;

        set
        {
            if (_clearColour == value)
            {
                return;
            }

            _clearColour = value;
            Invalidate();
        }
    }

    /// <summary>Gets how many samples per pixel this actually has.</summary>
    /// <remarks>One means the device refused everything higher and there is no smoothing.</remarks>
    public int SampleCount { get; }

    /// <summary>Gets whether more than one sample is in use.</summary>
    public bool IsMultisampled => SampleCount > 1;

    /// <summary>Gets the current width in pixels, or zero before the first resize.</summary>
    public int Width { get; private set; }

    /// <summary>Gets the current height in pixels, or zero before the first resize.</summary>
    public int Height { get; private set; }

    /// <summary>Gets whether there is a buffer to render into.</summary>
    public bool IsAllocated => _colour is not null;

    /// <summary>Gets the multisampled colour texture, for resolving out of.</summary>
    /// <exception cref="InvalidOperationException">Nothing has been allocated yet.</exception>
    public ID3D12Resource Colour
        => _colour ?? throw new InvalidOperationException(
            "The multisample target has no size yet. Resize it before using it.");

    /// <summary>Gets the render target view to bind.</summary>
    /// <exception cref="InvalidOperationException">Nothing has been allocated yet.</exception>
    /// <remarks>
    /// Guarded, because the views are only written inside <see cref="Resize"/>. Before that the
    /// heap holds uninitialised memory, and binding it is a GPU fault or a removed device rather
    /// than anything that names the mistake.
    /// </remarks>
    public CpuDescriptorHandle RenderTargetView
        => _colour is null
            ? throw new InvalidOperationException(
                "The multisample target has no size yet. Resize it before binding it.")
            : _rtvHeap.GetCPUDescriptorHandleForHeapStart();

    /// <summary>Gets the depth stencil view to bind.</summary>
    /// <exception cref="InvalidOperationException">Nothing has been allocated yet.</exception>
    public CpuDescriptorHandle DepthStencilView
        => _depth is null
            ? throw new InvalidOperationException(
                "The multisample target has no size yet. Resize it before binding it.")
            : _dsvHeap.GetCPUDescriptorHandleForHeapStart();

    /// <summary>Gets the state the colour texture rests in between frames.</summary>
    public static ResourceStates RestingState => ResourceStates.RenderTarget;

    /// <summary>Reallocates for a new size, doing nothing if the size has not changed.</summary>
    /// <param name="width">The new width in pixels.</param>
    /// <param name="height">The new height in pixels.</param>
    /// <remarks>The caller must have waited for the GPU; this releases the old textures.</remarks>
    public void Resize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (width == Width && height == Height && _colour is not null)
        {
            return;
        }

        // Released and forgotten *before* anything is allocated. Disposing without nulling means
        // a failure part-way -- running out of video memory on a large resize is the realistic
        // case, at roughly 130 MB per attachment for 4K at four samples -- leaves the fields
        // pointing at released textures while Width and Height still describe the old size. The
        // next frame's Resize would then take the unchanged-size early return and bind them.
        Invalidate();

        _colour = _device.CreateCommittedResource(
            HeapType.Default,
            HeapFlags.None,
            ResourceDescription.Texture2D(
                SwapChainTarget.BackBufferFormat,
                (uint)width,
                (uint)height,
                arraySize: 1,
                mipLevels: 1,
                sampleCount: (uint)SampleCount,
                sampleQuality: 0,
                flags: ResourceFlags.AllowRenderTarget),
            RestingState,
            new ClearValue(SwapChainTarget.BackBufferFormat, _clearColour));

        _colour.Name = $"msaa colour {width}x{height} x{SampleCount}";

        _depth = _device.CreateCommittedResource(
            HeapType.Default,
            HeapFlags.None,
            ResourceDescription.Texture2D(
                DepthBuffer.DepthFormat,
                (uint)width,
                (uint)height,
                arraySize: 1,
                mipLevels: 1,
                sampleCount: (uint)SampleCount,
                sampleQuality: 0,
                flags: ResourceFlags.AllowDepthStencil),
            ResourceStates.DepthWrite,
            new ClearValue(DepthBuffer.DepthFormat, DepthBuffer.ClearDepth, 0));

        _depth.Name = $"msaa depth {width}x{height} x{SampleCount}";

        _device.CreateRenderTargetView(_colour, null, RenderTargetView);
        _device.CreateDepthStencilView(_depth, null, DepthStencilView);

        Width = width;
        Height = height;
    }

    /// <summary>
    /// Records the resolve from the multisampled colour into a single-sampled destination.
    /// </summary>
    /// <param name="commands">An open command list.</param>
    /// <param name="destination">Where to resolve to, normally a swapchain back buffer.</param>
    /// <param name="destinationState">
    /// The state <paramref name="destination"/> is in, and will be returned to.
    /// </param>
    /// <remarks>
    /// <para>
    /// Both resources have to be transitioned into the transfer states and back again. Leaving the
    /// destination in <see cref="ResourceStates.ResolveDest"/> would fail validation at present
    /// time, which is a message about the swapchain rather than about the resolve that caused it.
    /// </para>
    /// <para>
    /// <b>With one sample this copies instead of resolving.</b> <c>ResolveSubresource</c> requires
    /// a multisampled source and is rejected outright otherwise — the command list simply refuses
    /// to close, which surfaces as a failure with no mention of the resolve that caused it. That
    /// is exactly the path a device offering no multisampling takes, so the fallback the sample
    /// negotiation exists to provide was broken until this was exercised.
    /// </para>
    /// </remarks>
    public void ResolveTo(
        ID3D12GraphicsCommandList commands,
        ID3D12Resource destination,
        ResourceStates destinationState)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(destination);

        ResourceStates transfer = IsMultisampled
            ? ResourceStates.ResolveDest
            : ResourceStates.CopyDest;

        ResourceStates source = IsMultisampled
            ? ResourceStates.ResolveSource
            : ResourceStates.CopySource;

        commands.ResourceBarrierTransition(Colour, RestingState, source);
        commands.ResourceBarrierTransition(destination, destinationState, transfer);

        if (IsMultisampled)
        {
            commands.ResolveSubresource(destination, 0, Colour, 0, SwapChainTarget.BackBufferFormat);
        }
        else
        {
            commands.CopyResource(destination, Colour);
        }

        commands.ResourceBarrierTransition(destination, transfer, destinationState);
        commands.ResourceBarrierTransition(Colour, source, RestingState);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Invalidate();
        _dsvHeap.Dispose();
        _rtvHeap.Dispose();
    }

    /// <summary>Releases the textures and forgets the size, so the next resize rebuilds.</summary>
    private void Invalidate()
    {
        _colour?.Dispose();
        _depth?.Dispose();
        _colour = null;
        _depth = null;

        Width = 0;
        Height = 0;
    }

    /// <summary>
    /// Finds the highest supported sample count at or below what was asked for.
    /// </summary>
    /// <remarks>
    /// Both formats have to support it, and they are checked separately: a device may offer four
    /// samples on the colour format and not on the depth format, and creating a mismatched pair is
    /// a validation error rather than a graceful degradation.
    /// </remarks>
    private static int Negotiate(ID3D12Device device, int requested)
    {
        // Rounded down to a power of two first. Halving from a count that is not one walks through
        // more counts that are not one and falls out at the bottom: asking for six checked six and
        // three, found neither supported, and returned one on a device offering both four and two.
        int start = System.Math.Clamp(requested, 1, 16);

        while (start > 1 && (start & (start - 1)) != 0)
        {
            start &= start - 1;
        }

        for (int samples = start; samples > 1; samples >>= 1)
        {
            uint colour = device.CheckMultisampleQualityLevels(
                SwapChainTarget.BackBufferFormat, (uint)samples, MultisampleQualityLevelFlags.None);

            uint depth = device.CheckMultisampleQualityLevels(
                DepthBuffer.DepthFormat, (uint)samples, MultisampleQualityLevelFlags.None);

            if (colour > 0 && depth > 0)
            {
                return samples;
            }
        }

        return 1;
    }
}
