using System.Runtime.InteropServices;

using OpenMCAD.Render.Direct3D12;

using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace OpenMCAD.Render.Tests;

/// <summary>One pixel, as read back from the GPU.</summary>
/// <param name="R">Red, 0-255.</param>
/// <param name="G">Green, 0-255.</param>
/// <param name="B">Blue, 0-255.</param>
/// <param name="A">Alpha, 0-255.</param>
public readonly record struct Pixel(byte R, byte G, byte B, byte A)
{
    /// <summary>Whether this is within a tolerance of another pixel.</summary>
    /// <param name="other">The pixel to compare with.</param>
    /// <param name="tolerance">The permitted difference per channel.</param>
    /// <returns>Whether they match.</returns>
    /// <remarks>
    /// Rasterisation is not bit-exact across drivers, and WARP is not bit-identical to hardware.
    /// Every assertion about colour here is therefore approximate by design; a test demanding an
    /// exact byte would pass on this machine and fail on the next one for no useful reason.
    /// </remarks>
    public bool IsCloseTo(Pixel other, int tolerance = 6)
        => System.Math.Abs(R - other.R) <= tolerance
            && System.Math.Abs(G - other.G) <= tolerance
            && System.Math.Abs(B - other.B) <= tolerance
            && System.Math.Abs(A - other.A) <= tolerance;

    /// <inheritdoc />
    public override string ToString() => $"rgba({R}, {G}, {B}, {A})";
}

/// <summary>
/// A render target with no window behind it, and a way to read the pixels back.
/// </summary>
/// <remarks>
/// <para>
/// The render passes are the one part of this system that cannot be judged by its return values.
/// A pass can bind the wrong buffer, transpose a matrix, cull the wrong way or lose the depth test
/// and still complete every call successfully — the only evidence is what ends up in the pixels.
/// So the tests render for real, on WARP, and read the framebuffer back.
/// </para>
/// <para>
/// Deliberately not built on <see cref="SwapChainTarget"/>: a swap chain needs a window, and a
/// test that needs a window does not run on a build agent.
/// </para>
/// </remarks>
public sealed class OffscreenSurface : IDisposable
{
    /// <summary>The colour format, matching the swap chain so the pipeline states agree.</summary>
    public const Format ColourFormat = SwapChainTarget.BackBufferFormat;

    private readonly ID3D12Resource _colour;
    private readonly ID3D12DescriptorHeap _rtvHeap;
    private readonly DepthBuffer _depth;
    private readonly ID3D12CommandAllocator _allocator;
    private readonly ID3D12GraphicsCommandList _commands;
    private readonly ID3D12Fence _fence;
    private readonly AutoResetEvent _fenceReached = new(false);
    private readonly ID3D12Resource _readback;
    private readonly ID3D12Resource _constants;
    private readonly PlacedSubresourceFootPrint _footprint;

    private ulong _lastSignalled;
    private bool _disposed;

    /// <summary>Creates a surface.</summary>
    /// <param name="device">The device to allocate on.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    public OffscreenSurface(D3D12RenderDevice device, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(device);

        Device = device;
        Width = width;
        Height = height;

        ResourceDescription colourDescription = ResourceDescription.Texture2D(
            ColourFormat,
            (uint)width,
            (uint)height,
            arraySize: 1,
            mipLevels: 1,
            sampleCount: 1,
            sampleQuality: 0,
            flags: ResourceFlags.AllowRenderTarget);

        // Created in the render-target state and returned to it after every readback, so each
        // Render call starts from the same place and needs no knowledge of the last one.
        _colour = device.Device.CreateCommittedResource(
            HeapType.Default,
            HeapFlags.None,
            colourDescription,
            ResourceStates.RenderTarget,
            new ClearValue(ColourFormat, new Color4(0, 0, 0, 1)));

        _colour.Name = "offscreen colour";

        _rtvHeap = device.Device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.RenderTargetView, 1, DescriptorHeapFlags.None));

        device.Device.CreateRenderTargetView(_colour, null, RenderTargetView);

        _depth = new DepthBuffer(device.Device, width, height);

        // A texture's rows are padded to 256 bytes when copied to a buffer, so the readback is not
        // simply width*4*height and the rows are not contiguous. Asking the device for the layout
        // rather than computing it is the difference between reading pixels and reading a sheared
        // image that looks plausible enough to be believed.
        PlacedSubresourceFootPrint[] layouts = new PlacedSubresourceFootPrint[1];

        device.Device.GetCopyableFootprints(
            colourDescription, 0, 1, 0, layouts, null!, null!, out ulong totalBytes);

        _footprint = layouts[0];

        _readback = device.Device.CreateCommittedResource(
            HeapType.Readback,
            HeapFlags.None,
            ResourceDescription.Buffer(totalBytes),
            ResourceStates.CopyDest);

        _readback.Name = "offscreen readback";

        _constants = device.Device.CreateCommittedResource(
            HeapType.Upload,
            HeapFlags.None,
            ResourceDescription.Buffer(256),
            ResourceStates.GenericRead);

        _constants.Name = "offscreen frame constants";

        _allocator = device.Device.CreateCommandAllocator(CommandListType.Direct);

        _commands = device.Device.CreateCommandList<ID3D12GraphicsCommandList>(
            CommandListType.Direct, _allocator);

        _commands.Close();

        _fence = device.Device.CreateFence(0);
    }

    /// <summary>Gets the device.</summary>
    public D3D12RenderDevice Device { get; }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets where the frame constants live on the GPU.</summary>
    public ulong ConstantBufferAddress => _constants.GPUVirtualAddress;

    private CpuDescriptorHandle RenderTargetView => _rtvHeap.GetCPUDescriptorHandleForHeapStart();

    /// <summary>Writes the frame constants the surface shader will read.</summary>
    /// <param name="constants">What to write.</param>
    public void SetConstants(FrameConstants constants)
    {
        ReadOnlySpan<FrameConstants> one = new(in constants);
        _constants.SetData(MemoryMarshal.AsBytes(one));
    }

    /// <summary>
    /// Clears, records, submits and waits.
    /// </summary>
    /// <param name="clear">The background colour.</param>
    /// <param name="record">
    /// What to draw. The render target and depth buffer are already bound, and the viewport and
    /// scissor already cover the surface.
    /// </param>
    public void Render(Color4 clear, Action<ID3D12GraphicsCommandList> record)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(record);

        _allocator.Reset();
        _commands.Reset(_allocator);

        CpuDescriptorHandle view = RenderTargetView;

        _commands.OMSetRenderTargets(view, _depth.View);
        _commands.ClearRenderTargetView(view, clear);
        _commands.ClearDepthStencilView(_depth.View, ClearFlags.Depth, DepthBuffer.ClearDepth, 0);
        _commands.RSSetViewport(0, 0, Width, Height);
        _commands.RSSetScissorRect(Width, Height);

        record(_commands);

        _commands.ResourceBarrierTransition(
            _colour, ResourceStates.RenderTarget, ResourceStates.CopySource);

        _commands.CopyTextureRegion(
            new TextureCopyLocation(_readback, _footprint),
            0,
            0,
            0,
            new TextureCopyLocation(_colour, 0),
            null);

        _commands.ResourceBarrierTransition(
            _colour, ResourceStates.CopySource, ResourceStates.RenderTarget);

        _commands.Close();
        Device.Queue.ExecuteCommandList(_commands);

        WaitForGpu();
    }

    /// <summary>
    /// Clears and records into a multisampled target, resolves into this surface, and reads back.
    /// </summary>
    /// <param name="msaa">The multisampled target to draw into.</param>
    /// <param name="clear">The background colour.</param>
    /// <param name="record">What to draw.</param>
    /// <remarks>
    /// The same shape as <see cref="Render"/>, but through the resolve the viewport actually uses.
    /// Testing the passes against a single-sampled target and the resolve separately would leave
    /// the combination — pipeline states whose sample count must match the target they are used
    /// with — covered by nothing.
    /// </remarks>
    public void RenderInto(MsaaTarget msaa, Color4 clear, Action<ID3D12GraphicsCommandList> record)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(msaa);
        ArgumentNullException.ThrowIfNull(record);

        _allocator.Reset();
        _commands.Reset(_allocator);

        _commands.OMSetRenderTargets(msaa.RenderTargetView, msaa.DepthStencilView);
        _commands.ClearRenderTargetView(msaa.RenderTargetView, clear);

        _commands.ClearDepthStencilView(
            msaa.DepthStencilView, ClearFlags.Depth, DepthBuffer.ClearDepth, 0);

        _commands.RSSetViewport(0, 0, Width, Height);
        _commands.RSSetScissorRect(Width, Height);

        record(_commands);

        msaa.ResolveTo(_commands, _colour, ResourceStates.RenderTarget);

        _commands.ResourceBarrierTransition(
            _colour, ResourceStates.RenderTarget, ResourceStates.CopySource);

        PlacedSubresourceFootPrint footprint = new()
        {
            Offset = 0,
            Footprint = new SubresourceFootPrint(
                ColourFormat, (uint)Width, (uint)Height, 1, (uint)_footprint.Footprint.RowPitch),
        };

        _commands.CopyTextureRegion(
            new TextureCopyLocation(_readback, footprint),
            0,
            0,
            0,
            new TextureCopyLocation(_colour, 0),
            null);

        _commands.ResourceBarrierTransition(
            _colour, ResourceStates.CopySource, ResourceStates.RenderTarget);

        _commands.Close();
        Device.Queue.ExecuteCommandList(_commands);

        WaitForGpu();
    }

    /// <summary>Reads one pixel out of the last rendered frame.</summary>
    /// <param name="x">Column, from the left.</param>
    /// <param name="y">Row, from the top.</param>
    /// <returns>The pixel.</returns>
    public Pixel At(int x, int y)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, Width);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);

        int rowPitch = (int)_footprint.Footprint.RowPitch;
        int length = rowPitch * Height;

        Span<byte> mapped = _readback.Map<byte>(0, length);

        try
        {
            int offset = (y * rowPitch) + (x * 4);
            return new Pixel(mapped[offset], mapped[offset + 1], mapped[offset + 2], mapped[offset + 3]);
        }
        finally
        {
            _readback.Unmap(0);
        }
    }

    /// <summary>Reads the pixel at the centre.</summary>
    /// <returns>The pixel.</returns>
    public Pixel Centre() => At(Width / 2, Height / 2);

    /// <summary>Counts how many sampled pixels differ from a given colour.</summary>
    /// <param name="background">The colour to ignore.</param>
    /// <param name="tolerance">How close counts as the same.</param>
    /// <returns>How many of the surface's pixels are something else.</returns>
    public int CountDifferingFrom(Pixel background, int tolerance = 6)
    {
        int rowPitch = (int)_footprint.Footprint.RowPitch;
        Span<byte> mapped = _readback.Map<byte>(0, rowPitch * Height);

        try
        {
            int count = 0;

            for (int y = 0; y < Height; ++y)
            {
                for (int x = 0; x < Width; ++x)
                {
                    int offset = (y * rowPitch) + (x * 4);

                    Pixel pixel = new(
                        mapped[offset], mapped[offset + 1], mapped[offset + 2], mapped[offset + 3]);

                    if (!pixel.IsCloseTo(background, tolerance))
                    {
                        count++;
                    }
                }
            }

            return count;
        }
        finally
        {
            _readback.Unmap(0);
        }
    }

    /// <summary>Counts the distinct colours present, quantised to reduce rasterisation noise.</summary>
    /// <param name="ignore">A colour not to count, normally the background.</param>
    /// <param name="bucket">How coarsely to quantise each channel.</param>
    /// <returns>How many distinct colours were found.</returns>
    /// <remarks>
    /// Quantised because anti-aliased edges and interpolated normals produce a continuum of near
    /// colours; the question a test wants to ask is "are these faces shaded differently", not "how
    /// many byte values occur".
    /// </remarks>
    public int DistinctColours(Pixel ignore, int bucket = 16)
    {
        int rowPitch = (int)_footprint.Footprint.RowPitch;
        Span<byte> mapped = _readback.Map<byte>(0, rowPitch * Height);

        try
        {
            HashSet<int> seen = [];

            for (int y = 0; y < Height; ++y)
            {
                for (int x = 0; x < Width; ++x)
                {
                    int offset = (y * rowPitch) + (x * 4);

                    Pixel pixel = new(
                        mapped[offset], mapped[offset + 1], mapped[offset + 2], mapped[offset + 3]);

                    if (pixel.IsCloseTo(ignore))
                    {
                        continue;
                    }

                    seen.Add(((pixel.R / bucket) << 16) | ((pixel.G / bucket) << 8) | (pixel.B / bucket));
                }
            }

            return seen.Count;
        }
        finally
        {
            _readback.Unmap(0);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        WaitForGpu();
        _disposed = true;

        _constants.Dispose();
        _readback.Dispose();
        _fence.Dispose();
        _fenceReached.Dispose();
        _commands.Dispose();
        _allocator.Dispose();
        _depth.Dispose();
        _rtvHeap.Dispose();
        _colour.Dispose();
    }

    private void WaitForGpu()
    {
        ulong target = ++_lastSignalled;
        Device.Queue.Signal(_fence, target);

        if (_fence.CompletedValue >= target)
        {
            return;
        }

        _fence.SetEventOnCompletion(target, _fenceReached);
        _fenceReached.WaitOne();
    }
}
