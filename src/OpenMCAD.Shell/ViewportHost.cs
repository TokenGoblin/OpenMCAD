using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using OpenMCAD.Render;
using OpenMCAD.Render.Direct3D12;

namespace OpenMCAD.Shell;

/// <summary>
/// Hosts the D3D12 viewport inside the WPF tree (P2-T02, ADR-0008).
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="HwndHost"/> with a plain child window, presented into by a swapchain. WPF's own
/// D3DImage path is the alternative and is rejected: it copies every frame through a shared
/// surface, is capped by WPF's own composition rate, and does not support the flip model. A CAD
/// viewport that must hold 60 fps on two million triangles cannot pay for a copy per frame.
/// </para>
/// <para>
/// The cost of an airspace child window is that WPF cannot draw over it — no adorner, no popup, no
/// transparency above the viewport. Overlays therefore belong in the viewport's own rendering
/// rather than in XAML on top of it, and that is a constraint on every later phase, not a detail
/// of this one.
/// </para>
/// <para>
/// <b>Verification.</b> Everything below the window handle — swapchain creation, resize, present,
/// device loss — is covered by <c>SwapChainTests</c> against a real off-screen window. This class
/// itself is not: it needs a WPF message loop, a monitor, and someone to look at it. It compiles
/// and its arithmetic is tested; that a viewport appears in the right place is not established
/// here.
/// </para>
/// </remarks>
public sealed class ViewportHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;

    // Repainting is entirely the swapchain's job. Without these the window is erased and redrawn
    // by USER32 underneath the presented frame, which shows as flicker while resizing.
    private const int CsVerticalRedraw = 0x0001;
    private const int CsHorizontalRedraw = 0x0002;

    private readonly ILogger _logger;

    private D3D12RenderDevice? _device;
    private SwapChainTarget? _target;
    private ViewportRenderer? _renderer;
    private nint _handle;
    private bool _reportedSize;

    /// <summary>
    /// Where a XAML-constructed viewport gets its logger from.
    /// </summary>
    /// <remarks>
    /// A static hook rather than injection, because XAML constructs controls itself and the
    /// container is not involved. The alternative was for the viewport to log nothing at all,
    /// which loses the one line that matters when a user reports slowness: which adapter it
    /// actually ended up on, and whether that was the WARP software fallback.
    /// </remarks>
    public static ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>Creates the host with no logging.</summary>
    /// <remarks>
    /// Explicitly parameterless, and not merged into the overload below with an optional argument.
    /// XAML instantiates a control through a genuinely parameterless constructor; a constructor
    /// whose every parameter happens to be optional does not satisfy it, and the failure is a
    /// <see cref="NullReferenceException"/> from deep inside the XAML object writer that names
    /// neither this type nor the reason.
    /// </remarks>
    public ViewportHost()
        : this(LoggerFactory?.CreateLogger<ViewportHost>())
    {
    }

    /// <summary>Creates the host.</summary>
    /// <param name="logger">Where to record device creation, resize and device loss.</param>
    public ViewportHost(ILogger<ViewportHost>? logger)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Gets what the renderer ended up running on, once the window exists.</summary>
    public string AdapterDescription => _device?.Info.ToString() ?? "(not yet created)";

    /// <summary>Gets the swapchain, once the window exists.</summary>
    public SwapChainTarget? Target => _target;

    /// <inheritdoc />
    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _handle = CreateWindowExW(
            0,
            "STATIC",
            string.Empty,
            WsChild | WsVisible | CsVerticalRedraw | CsHorizontalRedraw,
            0,
            0,
            1,
            1,
            hwndParent.Handle,
            0,
            0,
            0);

        if (_handle == 0)
        {
            throw new InvalidOperationException(
                $"The viewport window could not be created (Win32 error "
                + $"{Marshal.GetLastWin32Error()}).");
        }

        _device = new D3D12RenderDevice(logger: _logger);

        (int width, int height) = CurrentPixelSize();
        _target = new SwapChainTarget(_device, _handle, width, height, _logger);
        _renderer = new ViewportRenderer(_device, _target, _logger);

        // Driven by WPF's own frame tick rather than a timer. CompositionTarget.Rendering fires
        // once per composition pass on the UI thread, so the viewport redraws in step with the
        // rest of the window instead of fighting it -- and it stops firing when the window is
        // hidden or minimised, which is the occlusion handling this would otherwise need.
        CompositionTarget.Rendering += OnFrame;

        _logger.LogInformation("Viewport created on {Adapter}", _device.Info);

        return new HandleRef(this, _handle);
    }

    /// <inheritdoc />
    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        CompositionTarget.Rendering -= OnFrame;

        // Order matters. The renderer holds command lists recorded against the swapchain's
        // buffers, the swapchain references the queue, and the device waits for the GPU before
        // releasing anything -- so the window can only go last.
        _renderer?.Dispose();
        _renderer = null;

        _target?.Dispose();
        _target = null;

        _device?.Dispose();
        _device = null;

        if (_handle != 0)
        {
            DestroyWindow(_handle);
            _handle = 0;
        }
    }

    /// <inheritdoc />
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        if (_target is null)
        {
            return;
        }

        Resize();
    }

    /// <inheritdoc />
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);

        // A monitor change alters the pixel size without altering the layout size, so
        // OnRenderSizeChanged does not fire. Missing this is what leaves a viewport rendering at
        // the old monitor's resolution after the window is dragged to a second screen.
        if (_target is null)
        {
            return;
        }

        Resize();

        _logger.LogDebug(
            "Viewport DPI changed from {Old} to {New}; swapchain is now {Width}x{Height}",
            oldDpi.PixelsPerDip,
            newDpi.PixelsPerDip,
            _target.Width,
            _target.Height);
    }

    /// <summary>Resizes the swapchain to the current pixel size.</summary>
    private void Resize()
    {
        if (_target is null)
        {
            return;
        }

        // The GPU may still be reading the buffers a frame or two back, and resizing releases
        // them. The renderer knows which fence to wait on; the swapchain does not, so it is told
        // not to wait again.
        _renderer?.WaitForGpu();

        (int width, int height) = CurrentPixelSize();
        _target.Resize(width, height, waitForIdle: _renderer is null);

        // The first real size is worth a line. BuildWindowCore runs before WPF has measured
        // anything, so the swapchain is necessarily created at 1x1 and only reaches its true size
        // once layout has run -- and "created at 1x1" on its own reads like a bug in a support
        // log. Only the first, because a window drag would otherwise fill the log.
        if (!_reportedSize && width > 1 && height > 1)
        {
            _reportedSize = true;
            _logger.LogInformation("Viewport sized to {Width}x{Height} physical pixels", width, height);
        }
    }

    /// <summary>Draws one frame, in step with WPF's composition.</summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        if (_renderer is null)
        {
            return;
        }

        // Vsync off. WPF has already paced this call to the composition rate, so waiting for the
        // vertical blank again would halve the frame rate rather than smooth it.
        if (!_renderer.RenderFrame(verticalSync: false))
        {
            // Device loss is normal -- a driver update, a GPU reset, a laptop switching adapters.
            // Recreating the device is P2-T02's remaining work; until then, stop drawing rather
            // than spin on a dead device, and say so once.
            _logger.LogError("The graphics device was lost. The viewport has stopped drawing.");
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    /// <summary>The viewport's size in physical pixels, at the current DPI.</summary>
    private (int Width, int Height) CurrentPixelSize()
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);

        // The arithmetic lives in OpenMCAD.Render so it can be tested: this assembly targets
        // net10.0-windows and, by ADR-0014, nothing else may follow it there -- including a test
        // project.
        return ViewportScaling.ToPhysicalPixels(
            RenderSize.Width, RenderSize.Height, dpi.DpiScaleX, dpi.DpiScaleY);
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
    private static extern nint CreateWindowExW(
        int exStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}
