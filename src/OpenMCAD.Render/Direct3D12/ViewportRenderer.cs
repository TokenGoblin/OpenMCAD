using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Vortice.Direct3D12;
using Vortice.Mathematics;

namespace OpenMCAD.Render.Direct3D12;

/// <summary>
/// Draws frames into a <see cref="SwapChainTarget"/> (P2-T02, and the frame pacing of P2-T01).
/// </summary>
/// <remarks>
/// <para>
/// At present it clears the back buffer and nothing else. That is deliberately the whole of it:
/// the shaded face pass is P2-T05 and the edge pass is P2-T06. What this establishes is the frame
/// loop those will hang off — allocator recycling, resource barriers, and the fence pacing that
/// keeps the CPU ahead of the GPU without overwriting a buffer still being read.
/// </para>
/// <para>
/// <b>One command allocator per frame in flight.</b> An allocator cannot be reset while the GPU is
/// still executing commands recorded from it, so a single shared one would force a full stall
/// every frame. The fence value each allocator was last used with is remembered, and the loop
/// waits on that one value rather than for the device to go idle.
/// </para>
/// </remarks>
public sealed class ViewportRenderer : IDisposable
{
    private readonly ILogger _logger;
    private readonly D3D12RenderDevice _device;
    private readonly SwapChainTarget _target;
    private readonly ID3D12CommandAllocator[] _allocators;
    private readonly ulong[] _frameFenceValues;
    private readonly ID3D12GraphicsCommandList _commands;
    private readonly ID3D12Fence _fence;
    private readonly AutoResetEvent _fenceReached = new(false);

    private ulong _lastSignalled;
    private bool _disposed;

    /// <summary>Creates the renderer.</summary>
    /// <param name="device">The device to record on.</param>
    /// <param name="target">Where frames go.</param>
    /// <param name="logger">Where to report device loss.</param>
    public ViewportRenderer(D3D12RenderDevice device, SwapChainTarget target, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(target);

        _logger = logger ?? NullLogger.Instance;
        _device = device;
        _target = target;

        _allocators = new ID3D12CommandAllocator[SwapChainTarget.BufferCount];
        _frameFenceValues = new ulong[SwapChainTarget.BufferCount];

        for (int i = 0; i < _allocators.Length; ++i)
        {
            _allocators[i] = device.Device.CreateCommandAllocator(CommandListType.Direct);
            _allocators[i].Name = $"frame allocator {i}";
        }

        _commands = device.Device.CreateCommandList<ID3D12GraphicsCommandList>(
            CommandListType.Direct, _allocators[0]);

        // Created in the recording state and closed immediately, so the loop below can treat every
        // frame identically: reset, record, close.
        _commands.Close();
        _commands.Name = "viewport commands";

        _fence = device.Device.CreateFence(0);
        _fence.Name = "viewport frame fence";
    }

    /// <summary>Gets or sets the colour the viewport is cleared to.</summary>
    /// <remarks>
    /// A mid grey-blue rather than black or white. Shaded parts read badly against both: black
    /// swallows dark faces and hides silhouettes, and white makes every edge look like glare. A
    /// neutral mid-tone is what CAD packages settle on for the same reason.
    /// </remarks>
    public Color4 Background { get; set; } = new(0.22f, 0.24f, 0.27f, 1.0f);

    /// <summary>Gets how many frames have been presented.</summary>
    public long FrameCount { get; private set; }

    /// <summary>
    /// Draws and presents one frame.
    /// </summary>
    /// <param name="verticalSync">Whether to wait for the vertical blank.</param>
    /// <returns>
    /// <see langword="false"/> if the device was lost and everything must be rebuilt.
    /// </returns>
    public bool RenderFrame(bool verticalSync = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int index = _target.CurrentBackBufferIndex;

        // The allocator for this slot may still be in use by the frame that last had it, which is
        // BufferCount frames ago. Waiting on that one fence value is what lets the CPU stay ahead;
        // waiting for idle instead would serialise the two and halve the frame rate.
        WaitForFence(_frameFenceValues[index]);

        _allocators[index].Reset();
        _commands.Reset(_allocators[index]);

        ID3D12Resource backBuffer = _target.BackBuffer(index);

        // Present -> RenderTarget, and back again before presenting. Omitting either barrier is
        // undefined behaviour that happens to work on some drivers, which is the worst kind.
        _commands.ResourceBarrierTransition(
            backBuffer, ResourceStates.Present, ResourceStates.RenderTarget);

        CpuDescriptorHandle view = _target.RenderTargetView(index);
        _commands.OMSetRenderTargets(view);
        _commands.ClearRenderTargetView(view, Background);

        // The face and edge passes go here (P2-T05, P2-T06).

        _commands.ResourceBarrierTransition(
            backBuffer, ResourceStates.RenderTarget, ResourceStates.Present);

        _commands.Close();
        _device.Queue.ExecuteCommandList(_commands);

        if (!_target.Present(verticalSync))
        {
            return false;
        }

        _frameFenceValues[index] = ++_lastSignalled;
        _device.Queue.Signal(_fence, _lastSignalled);

        FrameCount++;
        return true;
    }

    /// <summary>Blocks until the GPU has finished every frame recorded so far.</summary>
    /// <remarks>Needed before a resize, which must release the back buffers.</remarks>
    public void WaitForGpu()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        WaitForFence(_lastSignalled);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Nothing may be released while the GPU is still reading it.
        try
        {
            WaitForFence(_lastSignalled);
        }
        catch (SharpGen.Runtime.SharpGenException exception)
        {
            _logger.LogWarning(exception, "The GPU did not go idle cleanly while closing the viewport");
        }

        _disposed = true;

        _fence.Dispose();
        _fenceReached.Dispose();
        _commands.Dispose();

        foreach (ID3D12CommandAllocator allocator in _allocators)
        {
            allocator.Dispose();
        }
    }

    /// <summary>Waits for a fence value, returning at once if it has already passed.</summary>
    private void WaitForFence(ulong value)
    {
        if (value == 0 || _fence.CompletedValue >= value)
        {
            return;
        }

        _fence.SetEventOnCompletion(value, _fenceReached);
        _fenceReached.WaitOne();
    }
}
