using System.Runtime.InteropServices;

using OpenMCAD.Render.Direct3D12;

using Vortice.Direct3D12;
using Vortice.Mathematics;

namespace OpenMCAD.Render.Tests;

/// <summary>
/// An <see cref="IdTarget"/> with a way to read the whole buffer back.
/// </summary>
/// <remarks>
/// The production path reads a small window through <see cref="PickReadback"/>, which is what
/// keeps a pick from stalling the pipeline. Tests want the entire buffer and do not care about
/// stalling, so this reads it synchronously — asserting on the whole image is what catches a pass
/// that writes ids in the wrong place, which a fifteen-pixel window would sample right past.
/// </remarks>
public sealed class IdOffscreen : IDisposable
{
    private readonly D3D12RenderDevice _device;
    private readonly IdTarget _target;
    private readonly ID3D12CommandAllocator _allocator;
    private readonly ID3D12GraphicsCommandList _commands;
    private readonly ID3D12Fence _fence;
    private readonly AutoResetEvent _fenceReached = new(false);
    private readonly ID3D12Resource _readback;
    private readonly ID3D12Resource _constants;
    private readonly int _rowPitch;

    private ulong _lastSignalled;
    private bool _disposed;

    /// <summary>Creates the target.</summary>
    /// <param name="device">The device to allocate on.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    public IdOffscreen(D3D12RenderDevice device, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(device);

        _device = device;
        Width = width;
        Height = height;

        _target = new IdTarget(device.Device, width, height);

        int unaligned = width * SceneGeometry.IdStride;
        _rowPitch = (unaligned + 255) & ~255;

        _readback = device.Device.CreateCommittedResource(
            HeapType.Readback,
            HeapFlags.None,
            ResourceDescription.Buffer((ulong)(_rowPitch * height)),
            ResourceStates.CopyDest);

        _constants = device.Device.CreateCommittedResource(
            HeapType.Upload,
            HeapFlags.None,
            ResourceDescription.Buffer(256),
            ResourceStates.GenericRead);

        _allocator = device.Device.CreateCommandAllocator(CommandListType.Direct);

        _commands = device.Device.CreateCommandList<ID3D12GraphicsCommandList>(
            CommandListType.Direct, _allocator);

        _commands.Close();
        _fence = device.Device.CreateFence(0);
    }

    /// <summary>Gets the width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets the underlying target, for tests that drive the pick path directly.</summary>
    public IdTarget Target => _target;

    /// <summary>Gets where the frame constants live on the GPU.</summary>
    public ulong ConstantBufferAddress => _constants.GPUVirtualAddress;

    /// <summary>Writes the frame constants the shaders read.</summary>
    /// <param name="constants">What to write.</param>
    public void SetConstants(FrameConstants constants)
    {
        ReadOnlySpan<FrameConstants> one = new(in constants);
        _constants.SetData(MemoryMarshal.AsBytes(one));
    }

    /// <summary>
    /// Clears the ID buffer, records a pass into it, and reads the whole thing back.
    /// </summary>
    /// <param name="record">What to draw.</param>
    /// <returns>Row-major ids, <see cref="Width"/> per row.</returns>
    public uint[] Render(Action<ID3D12GraphicsCommandList> record)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(record);

        _allocator.Reset();
        _commands.Reset(_allocator);

        CpuDescriptorHandle view = _target.View;

        _commands.OMSetRenderTargets(view, _target.Depth.View);
        _commands.ClearRenderTargetView(view, new Color4(0, 0, 0, 0));
        _commands.ClearDepthStencilView(
            _target.Depth.View, ClearFlags.Depth, DepthBuffer.ClearDepth, 0);

        _commands.RSSetViewport(0, 0, Width, Height);
        _commands.RSSetScissorRect(Width, Height);

        record(_commands);

        _commands.ResourceBarrierTransition(
            _target.Resource, IdTarget.RestingState, ResourceStates.CopySource);

        PlacedSubresourceFootPrint footprint = new()
        {
            Offset = 0,
            Footprint = new SubresourceFootPrint(
                IdTarget.IdFormat, (uint)Width, (uint)Height, 1, (uint)_rowPitch),
        };

        _commands.CopyTextureRegion(
            new TextureCopyLocation(_readback, footprint),
            0,
            0,
            0,
            new TextureCopyLocation(_target.Resource, 0),
            null);

        _commands.ResourceBarrierTransition(
            _target.Resource, ResourceStates.CopySource, IdTarget.RestingState);

        _commands.Close();
        _device.Queue.ExecuteCommandList(_commands);
        WaitForGpu();

        uint[] ids = new uint[Width * Height];
        Span<byte> mapped = _readback.Map<byte>(0, _rowPitch * Height);

        try
        {
            for (int y = 0; y < Height; ++y)
            {
                MemoryMarshal
                    .Cast<byte, uint>(mapped.Slice(y * _rowPitch, Width * SceneGeometry.IdStride))
                    .CopyTo(ids.AsSpan(y * Width, Width));
            }
        }
        finally
        {
            _readback.Unmap(0);
        }

        return ids;
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
        _target.Dispose();
    }

    private void WaitForGpu()
    {
        ulong target = ++_lastSignalled;
        _device.Queue.Signal(_fence, target);

        if (_fence.CompletedValue >= target)
        {
            return;
        }

        _fence.SetEventOnCompletion(target, _fenceReached);
        _fenceReached.WaitOne();
    }
}
