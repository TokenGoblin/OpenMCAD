using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using SharpGen.Runtime;

using Vortice.Direct3D12;
using Vortice.DXGI;

namespace OpenMCAD.Render.Direct3D12;

/// <summary>
/// A swapchain bound to a window, and the render targets behind it (P2-T02).
/// </summary>
/// <remarks>
/// <para>
/// Takes a bare <c>HWND</c> and knows nothing about WPF. That is what keeps it in this assembly:
/// ADR-0014 confines the Windows-specific target framework to the shell, so the
/// <c>HwndHost</c>-derived control lives there and hands its child window down to this. The split
/// also means resize and device-loss can be tested against a plain Win32 window, with no WPF
/// dispatcher and no message pump.
/// </para>
/// <para>
/// Flip-model, which is not optional on Windows 10 and later: the older blit models are
/// deprecated, cost an extra copy, and disable the composition path that makes a windowed
/// application tear-free. <see cref="SwapEffect.FlipDiscard"/> with three buffers is the
/// configuration that lets the CPU run two frames ahead without the GPU stalling on a buffer the
/// display still owns.
/// </para>
/// </remarks>
public sealed class SwapChainTarget : IDisposable
{
    /// <summary>
    /// How many back buffers the chain holds.
    /// </summary>
    /// <remarks>
    /// Three, not two. Two means the CPU can only be one frame ahead, so any frame that overruns
    /// its budget stalls the next one immediately; three absorbs a spike without a visible hitch.
    /// The cost is one buffer's worth of memory, which at any realistic resolution is a rounding
    /// error against the geometry.
    /// </remarks>
    public const int BufferCount = 3;

    /// <summary>
    /// The back buffer format.
    /// </summary>
    /// <remarks>
    /// Straight sRGB rather than a _SRGB-suffixed format. The flip model refuses an sRGB backbuffer
    /// format outright; the conversion is done by the render-target view instead, which is the
    /// documented way round it.
    /// </remarks>
    public const Format BackBufferFormat = Format.R8G8B8A8_UNorm;

    private readonly ILogger _logger;
    private readonly D3D12RenderDevice _owner;
    private readonly ID3D12Device _device;
    private readonly IDXGISwapChain3 _swapChain;
    private readonly DescriptorHeapAllocator _renderTargetViews;
    private readonly ID3D12Resource?[] _buffers = new ID3D12Resource?[BufferCount];
    private readonly int[] _views = new int[BufferCount];
    private readonly bool _allowTearing;

    private bool _disposed;

    /// <summary>Creates the swapchain.</summary>
    /// <param name="device">The device whose queue will present.</param>
    /// <param name="windowHandle">The window to present into.</param>
    /// <param name="width">Initial width in physical pixels.</param>
    /// <param name="height">Initial height in physical pixels.</param>
    /// <param name="logger">Where to record resizes and device loss.</param>
    /// <exception cref="ArgumentException">The window handle is null.</exception>
    public SwapChainTarget(
        D3D12RenderDevice device,
        nint windowHandle,
        int width,
        int height,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (windowHandle == 0)
        {
            throw new ArgumentException("A swapchain needs a window to present into.", nameof(windowHandle));
        }

        _logger = logger ?? NullLogger.Instance;
        _owner = device;
        _device = device.Device;

        using IDXGIFactory5 factory = DXGI.CreateDXGIFactory1<IDXGIFactory5>();

        // Tearing has to be supported by the whole chain -- adapter, driver, and OS -- and asking
        // for it where it is not is a creation failure rather than a graceful downgrade. It is
        // what lets an unlocked frame rate actually exceed the refresh rate on a variable-refresh
        // display, which matters while dragging.
        _allowTearing = factory.PresentAllowTearing;

        SwapChainDescription1 description = new()
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = BackBufferFormat,
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = BufferCount,
            SwapEffect = SwapEffect.FlipDiscard,
            SampleDescription = new SampleDescription(1, 0),
            AlphaMode = AlphaMode.Ignore,
            Scaling = Scaling.None,
            Flags = _allowTearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None,
        };

        using IDXGISwapChain1 created = factory.CreateSwapChainForHwnd(
            device.Queue, windowHandle, description);

        // Alt+Enter fullscreen is DXGI's, not ours: it resizes the window behind WPF's back and
        // leaves the layout inconsistent. A CAD application provides its own full-screen mode.
        factory.MakeWindowAssociation(windowHandle, WindowAssociationFlags.IgnoreAltEnter);

        _swapChain = created.QueryInterface<IDXGISwapChain3>();

        _renderTargetViews = new DescriptorHeapAllocator(
            _device, DescriptorHeapType.RenderTargetView, BufferCount, shaderVisible: false,
            "swapchain RTVs");

        for (int i = 0; i < BufferCount; ++i)
        {
            _views[i] = _renderTargetViews.Allocate();
        }

        Width = width;
        Height = height;

        AcquireBuffers();

        _logger.LogInformation(
            "Swapchain created at {Width}x{Height}, tearing {Tearing}",
            width,
            height,
            _allowTearing ? "supported" : "unsupported");
    }

    /// <summary>Gets the current width in physical pixels.</summary>
    public int Width { get; private set; }

    /// <summary>Gets the current height in physical pixels.</summary>
    public int Height { get; private set; }

    /// <summary>Gets which back buffer the next frame should draw into.</summary>
    public int CurrentBackBufferIndex => (int)_swapChain.CurrentBackBufferIndex;

    /// <summary>Gets whether the display chain supports tearing.</summary>
    public bool AllowsTearing => _allowTearing;

    /// <summary>Gets the resource for a back buffer.</summary>
    /// <param name="index">Which one.</param>
    /// <returns>The resource.</returns>
    public ID3D12Resource BackBuffer(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, BufferCount);

        return _buffers[index]!;
    }

    /// <summary>Gets the render-target view for a back buffer.</summary>
    /// <param name="index">Which one.</param>
    /// <returns>Its descriptor handle.</returns>
    public CpuDescriptorHandle RenderTargetView(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, BufferCount);

        return _renderTargetViews.CpuHandle(_views[index]);
    }

    /// <summary>
    /// Resizes the back buffers.
    /// </summary>
    /// <param name="width">New width in physical pixels.</param>
    /// <param name="height">New height in physical pixels.</param>
    /// <param name="waitForIdle">
    /// Releases the GPU's references to the old buffers before replacing them. Required, and a
    /// parameter only so a caller that has just waited does not wait twice.
    /// </param>
    /// <remarks>
    /// <para>
    /// A no-op when the size has not actually changed. WM_SIZE arrives for moves between monitors
    /// and for restores from minimised, and rebuilding the chain each time would drop frames
    /// during a window drag for no reason.
    /// </para>
    /// <para>
    /// A zero dimension is ignored rather than rejected. Minimising a window reports a client area
    /// of zero by zero, which is not an error and not resizable -- DXGI refuses it -- so the old
    /// buffers are kept until the window comes back.
    /// </para>
    /// </remarks>
    public void Resize(int width, int height, bool waitForIdle = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (width <= 0 || height <= 0)
        {
            _logger.LogDebug("Ignoring a resize to {Width}x{Height}; the window is minimised", width, height);
            return;
        }

        if (width == Width && height == Height)
        {
            return;
        }

        if (waitForIdle)
        {
            // Every back buffer has to be released before ResizeBuffers, and the GPU must not
            // still be reading one. Skipping this is the classic source of "the swapchain could
            // not be resized" reported as a device-removed error much later.
            _owner.WaitForIdle();
        }

        ReleaseBuffers();

        _swapChain.ResizeBuffers(
            BufferCount,
            (uint)width,
            (uint)height,
            BackBufferFormat,
            _allowTearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None).CheckError();

        Width = width;
        Height = height;

        AcquireBuffers();

        _logger.LogDebug("Swapchain resized to {Width}x{Height}", width, height);
    }

    /// <summary>Presents the current back buffer.</summary>
    /// <param name="verticalSync">Whether to wait for the vertical blank.</param>
    /// <returns>
    /// <see langword="true"/> if the frame was presented; <see langword="false"/> if the device was
    /// lost and everything above must be rebuilt.
    /// </returns>
    /// <remarks>
    /// Device loss is returned rather than thrown. It is a normal event — a driver update, a GPU
    /// reset, a laptop switching adapters — and the application is expected to recover by
    /// recreating its device rather than to treat it as a fault.
    /// </remarks>
    public bool Present(bool verticalSync = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Tearing and vsync are mutually exclusive: the flag is only legal on an unsynchronised
        // present, and DXGI rejects the combination outright.
        PresentFlags flags = !verticalSync && _allowTearing ? PresentFlags.AllowTearing : PresentFlags.None;

        Result result = _swapChain.Present(verticalSync ? 1u : 0u, flags);

        if (result.Success)
        {
            return true;
        }

        if (result == Vortice.DXGI.ResultCode.DeviceRemoved || result == Vortice.DXGI.ResultCode.DeviceReset)
        {
            _logger.LogWarning(
                "The graphics device was lost while presenting ({Reason}). The renderer must be "
                + "recreated.",
                _device.DeviceRemovedReason);

            return false;
        }

        result.CheckError();
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        ReleaseBuffers();
        _renderTargetViews.Dispose();
        _swapChain.Dispose();
    }

    /// <summary>Takes the back buffers and builds their views.</summary>
    private void AcquireBuffers()
    {
        for (int i = 0; i < BufferCount; ++i)
        {
            _buffers[i] = _swapChain.GetBuffer<ID3D12Resource>((uint)i);
            _buffers[i]!.Name = $"backbuffer {i}";

            _device.CreateRenderTargetView(
                _buffers[i], null, _renderTargetViews.CpuHandle(_views[i]));
        }
    }

    /// <summary>Releases the back buffers, which resizing requires.</summary>
    private void ReleaseBuffers()
    {
        for (int i = 0; i < BufferCount; ++i)
        {
            _buffers[i]?.Dispose();
            _buffers[i] = null;
        }
    }
}
