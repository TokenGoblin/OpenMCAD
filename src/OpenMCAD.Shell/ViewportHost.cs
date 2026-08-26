using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

using OpenMCAD.Interaction.Navigation;
using OpenMCAD.Interaction.Selection;
using OpenMCAD.Kernel;

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
    private DisplaySnapshot _snapshot = DisplaySnapshot.Empty;
    private EdgeStyle _edgeStyle = EdgeStyle.Default;
    private NavigationController? _navigation;
    private long _publishedSelection = -1;
    private Camera? _camera;
    private MouseProfile _mouseProfile = MouseProfile.Default;
    private bool _framedOnce;
    private int _deviceLossAttempts;
    private DateTime _lastDeviceLoss = DateTime.MinValue;

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

    /// <summary>Gets or sets what the viewport draws.</summary>
    /// <remarks>
    /// Held here as well as on the renderer because the window may not exist yet: a snapshot can
    /// be produced before WPF has built the HWND, and dropping it would leave a viewport that is
    /// permanently empty for reasons nothing reports.
    /// </remarks>
    public DisplaySnapshot Snapshot
    {
        get => _snapshot;

        set
        {
            _snapshot = value ?? throw new ArgumentNullException(nameof(value));

            if (_renderer is not null)
            {
                _renderer.Snapshot = _snapshot;
            }
        }
    }

    /// <summary>Gets or sets how edges are drawn, before display scaling.</summary>
    /// <remarks>
    /// Held unscaled. The renderer is given this scaled by the current display, and the scaling is
    /// reapplied on every resize — so it has to be reapplied to <i>this</i> rather than to what the
    /// renderer currently holds, or each resize would compound the scale factor, and assigning the
    /// default instead would silently revert whatever the application had chosen.
    /// </remarks>
    public EdgeStyle EdgeStyle
    {
        get => _edgeStyle;

        set
        {
            _edgeStyle = value;
            ApplyEdgeStyle();
        }
    }

    /// <summary>Frames the whole scene.</summary>
    /// <returns>Whether there was anything to frame.</returns>
    public bool ZoomToFit() => _renderer?.ZoomToFit() ?? false;

    /// <summary>Gets the navigation controller, once the window exists.</summary>
    public NavigationController? Navigation => _navigation;

    /// <summary>Gets what the user has selected.</summary>
    public SelectionSet Selection { get; } = new();

    /// <summary>Gets or sets which mouse gestures navigate the view.</summary>
    public MouseProfile MouseProfile
    {
        get => _mouseProfile;

        set
        {
            ArgumentNullException.ThrowIfNull(value);

            // Kept here as well as on the controller, which does not survive a device loss.
            _mouseProfile = value;

            if (_navigation is not null)
            {
                _navigation.Profile = value;
            }
        }
    }

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

        CreateDeviceResources();

        // Driven by WPF's own frame tick rather than a timer. CompositionTarget.Rendering fires
        // once per composition pass on the UI thread, so the viewport redraws in step with the
        // rest of the window instead of fighting it -- and it stops firing when the window is
        // hidden or minimised, which is the occlusion handling this would otherwise need.
        CompositionTarget.Rendering += OnFrame;

        return new HandleRef(this, _handle);
    }

    /// <inheritdoc />
    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        CompositionTarget.Rendering -= OnFrame;

        // Shared with device-loss recovery, so the two cannot disagree about what has to go and in
        // what order. The window can only be destroyed after all of it.
        ReleaseDeviceResources();

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

        // Edge width is specified in physical pixels, so it has to be scaled or a hairline on a
        // 150% display is two thirds the thickness the design intends. Reapplied on every resize
        // because a window dragged to another monitor changes scale without being recreated.
        ApplyEdgeStyle();

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

        // Picks answered since the last frame become hover highlighting on this one.
        UpdateSelection();

        // Vsync off. WPF has already paced this call to the composition rate, so waiting for the
        // vertical blank again would halve the frame rate rather than smooth it.
        if (!_renderer.RenderFrame(verticalSync: false))
        {
            RecoverFromDeviceLoss();
        }
    }

    /// <summary>The viewport's size in physical pixels, at the current DPI.</summary>
    /// <summary>
    /// Builds the device, swapchain and renderer, at start-up and again after a device loss.
    /// </summary>
    /// <remarks>
    /// One path for both, deliberately. A separate recovery routine is a second copy of the set-up
    /// that runs perhaps once a year, on somebody else machine, and it drifts from the real one in
    /// silence: every new piece of viewport state gets wired into start-up and forgotten here.
    /// </remarks>
    private void CreateDeviceResources()
    {
        _device = new D3D12RenderDevice(logger: _logger);

        (int width, int height) = CurrentPixelSize();
        _target = new SwapChainTarget(_device, _handle, width, height, _logger);

        // The camera is carried across rather than rebuilt. Everything else here is a GPU
        // resource; where the user was looking is not, and a view that snapped back to the default
        // on a driver update would be a worse failure than the one being recovered from.
        _renderer = new ViewportRenderer(_device, _target, _logger, _camera)
        {
            Snapshot = _snapshot,
        };

        _camera = _renderer.Camera;
        _navigation = new NavigationController(_camera) { Profile = _mouseProfile };

        // Highlights survive as data but their GPU buffer does not, so the table has to be
        // published to the new renderer.
        _publishedSelection = -1;

        ApplyEdgeStyle();

        // Framed only the first time. Refitting on recovery would throw away the camera that was
        // just carried across for the purpose.
        if (!_framedOnce)
        {
            _renderer.ZoomToFit();
            _framedOnce = true;
        }

        _logger.LogInformation("Viewport created on {Adapter}", _device.Info);
    }

    /// <summary>
    /// Rebuilds everything after the graphics device has gone away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Device loss is ordinary rather than exceptional: a driver update, a GPU reset after a hang,
    /// a laptop switching between integrated and discrete graphics, a remote session changing
    /// adapters. Windows expects an application to rebuild and carry on, and one that stops until
    /// restarted is one users learn to restart pre-emptively.
    /// </para>
    /// <para>
    /// <b>Attempts are counted, and eventually give up.</b> A genuinely broken device fails again
    /// immediately, and retrying inside the frame loop would spin the machine at full speed while
    /// filling the log. After a few failures in quick succession the viewport stops for good and
    /// says why, which is the honest outcome.
    /// </para>
    /// </remarks>
    private void RecoverFromDeviceLoss()
    {
        DateTime now = DateTime.UtcNow;

        // Counted within a window, so a machine that loses its device twice in a year recovers
        // both times rather than being one failure closer to giving up for good.
        if (now - _lastDeviceLoss > TimeSpan.FromMinutes(5))
        {
            _deviceLossAttempts = 0;
        }

        _lastDeviceLoss = now;
        _deviceLossAttempts++;

        if (_deviceLossAttempts > MaxDeviceLossAttempts)
        {
            _logger.LogError(
                "The graphics device has been lost {Count} times in quick succession. The viewport "
                + "has stopped drawing; restart the application.",
                _deviceLossAttempts);

            CompositionTarget.Rendering -= OnFrame;
            ReleaseDeviceResources();
            return;
        }

        _logger.LogWarning(
            "The graphics device was lost. Rebuilding it (attempt {Attempt} of {Max}).",
            _deviceLossAttempts,
            MaxDeviceLossAttempts);

        try
        {
            ReleaseDeviceResources();
            CreateDeviceResources();
            Resize();

            _logger.LogInformation("The viewport recovered onto {Adapter}", _device!.Info);
        }
        catch (Exception exception)
        {
            // Broad on purpose. Anything thrown here leaves the viewport with no device, and
            // letting it escape into WPF rendering event handling takes the application down over
            // a failure it was in the middle of recovering from.
            _logger.LogError(exception, "The graphics device could not be rebuilt");

            ReleaseDeviceResources();
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    /// <summary>Releases the device, swapchain and renderer in dependency order.</summary>
    private void ReleaseDeviceResources()
    {
        // The renderer holds command lists recorded against the swapchain buffers, the swapchain
        // references the queue, and the device waits for the GPU before releasing anything.
        _renderer?.Dispose();
        _renderer = null;

        _target?.Dispose();
        _target = null;

        _device?.Dispose();
        _device = null;

        _navigation = null;
    }

    /// <summary>Hands the renderer the current style, scaled for this display.</summary>
    private void ApplyEdgeStyle()
    {
        if (_renderer is not null)
        {
            _renderer.EdgeStyle = _edgeStyle.AtScale(VisualTreeHelper.GetDpi(this).DpiScaleX);
        }
    }

    private (int Width, int Height) CurrentPixelSize()
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);

        // The arithmetic lives in OpenMCAD.Render so it can be tested: this assembly targets
        // net10.0-windows and, by ADR-0014, nothing else may follow it there -- including a test
        // project.
        return ViewportScaling.ToPhysicalPixels(
            RenderSize.Width, RenderSize.Height, dpi.DpiScaleX, dpi.DpiScaleY);
    }

    /// <summary>
    /// Routes mouse input from the hosted window to the navigation controller (P2-T08).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The messages have to be intercepted here because the hosted child window receives them and
    /// never forwards them to WPF — a plain <c>MouseMove</c> handler on this element sees nothing
    /// at all. <see cref="HwndHost"/> subclasses the child window, so overriding this is the one
    /// place the events are reachable.
    /// </para>
    /// <para>
    /// Coordinates in <c>lParam</c> are already client-relative physical pixels, which is exactly
    /// the space the swapchain, the ID buffer and the navigation rates all work in. Wheel messages
    /// are the exception: they carry <i>screen</i> coordinates, so they are converted, and
    /// forgetting that puts zoom-towards-cursor progressively further out the further the window
    /// is from the top-left of the display.
    /// </para>
    /// </remarks>
    protected override nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (_navigation is null)
        {
            return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
        }

        switch (msg)
        {
            case WmLButtonDown:
            case WmMButtonDown:
            case WmRButtonDown:
                OnButtonDown(ButtonOf(msg), lParam, ref handled);
                break;

            case WmLButtonUp:
            case WmMButtonUp:
            case WmRButtonUp:
                OnButtonUp(ButtonOf(msg), ref handled);
                break;

            case WmMouseMove:
                if (_navigation.IsNavigating)
                {
                    (int width, int height) = CurrentPixelSize();
                    _navigation.PointerMove(LowWord(lParam), HighWord(lParam), width, height);
                    handled = true;
                }
                else
                {
                    // Hover. Asked on every move rather than throttled: a pick costs one extra
                    // pass on a frame that was going to be drawn anyway, and the readback drops
                    // requests when the pipeline is full rather than queueing them, so asking too
                    // often is self-limiting.
                    _renderer?.RequestPick(LowWord(lParam), HighWord(lParam));
                }

                break;

            case WmMouseWheel:
                OnWheel(wParam, lParam, ref handled);
                break;

            case WmCaptureChanged:
                // Capture can be taken away -- a modal dialog, another window, Alt+Tab. The drag
                // is abandoned rather than left running, which would otherwise leave the view
                // spinning under a mouse the user thinks they have let go of.
                _navigation.Cancel();
                break;

            default:
                break;
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private static PointerButton ButtonOf(int message) => message switch
    {
        WmLButtonDown or WmLButtonUp => PointerButton.Left,
        WmMButtonDown or WmMButtonUp => PointerButton.Middle,
        WmRButtonDown or WmRButtonUp => PointerButton.Right,
        _ => PointerButton.None,
    };

    private static int LowWord(nint value) => (short)(value & 0xFFFF);

    private static int HighWord(nint value) => (short)((value >> 16) & 0xFFFF);

    /// <summary>Reads the modifier keys as the navigation layer wants them.</summary>
    /// <remarks>
    /// Shift and Control arrive in the message; Alt never does, because Windows routes it through
    /// the system-key path instead. It has to be asked for separately.
    /// </remarks>
    private static NavigationModifiers CurrentModifiers()
    {
        NavigationModifiers modifiers = NavigationModifiers.None;

        if ((GetKeyState(VkShift) & 0x8000) != 0)
        {
            modifiers |= NavigationModifiers.Shift;
        }

        if ((GetKeyState(VkControl) & 0x8000) != 0)
        {
            modifiers |= NavigationModifiers.Control;
        }

        if ((GetKeyState(VkMenu) & 0x8000) != 0)
        {
            modifiers |= NavigationModifiers.Alt;
        }

        return modifiers;
    }

    private void OnButtonDown(PointerButton button, nint lParam, ref bool handled)
    {
        NavigationModifiers modifiers = CurrentModifiers();

        if (_navigation is null
            || !_navigation.PointerDown(button, modifiers, LowWord(lParam), HighWord(lParam)))
        {
            // Unbound by navigation, so it belongs to selection -- or later, to a sketch tool.
            if (button == PointerButton.Left)
            {
                SelectAtCursor(modifiers);
                handled = true;
            }

            return;
        }

        // Without capture the drag stops the moment the pointer leaves the viewport, which for an
        // orbit is most of the time.
        SetCapture(_handle);
        handled = true;
    }

    private void OnButtonUp(PointerButton button, ref bool handled)
    {
        if (_navigation is null || !_navigation.PointerUp(button))
        {
            return;
        }

        ReleaseCapture();
        handled = true;
    }

    private void OnWheel(nint wParam, nint lParam, ref bool handled)
    {
        if (_navigation is null)
        {
            return;
        }

        // Screen coordinates, unlike every other mouse message.
        Point point = new(LowWord(lParam), HighWord(lParam));

        if (ScreenToClient(_handle, ref point))
        {
            (int width, int height) = CurrentPixelSize();
            double notches = HighWord(wParam) / 120.0;

            _navigation.Wheel(notches, point.X, point.Y, width, height);
            handled = true;
        }
    }

    /// <summary>
    /// Commits whatever the last hover resolved to.
    /// </summary>
    /// <remarks>
    /// It selects what is <i>already known</i> to be under the cursor rather than issuing a fresh
    /// pick and waiting. The readback is deliberately a few frames behind, so waiting would mean a
    /// click that does nothing and then selects a moment later; and because hover has been running
    /// on every mouse move, the answer is already there. It is also the more defensible rule: this
    /// way a click can only ever select the thing the user could see highlighted when they pressed
    /// the button.
    /// </remarks>
    private void SelectAtCursor(NavigationModifiers modifiers)
    {
        SelectionAction action = modifiers switch
        {
            NavigationModifiers.Control => SelectionAction.Toggle,
            NavigationModifiers.Shift => SelectionAction.Add,
            _ => SelectionAction.Replace,
        };

        Selection.Apply(Selection.PreSelected, action);
    }

    /// <summary>Feeds completed picks into the selection, and the selection to the renderer.</summary>
    private void UpdateSelection()
    {
        if (_renderer is null)
        {
            return;
        }

        // Drained rather than taken one at a time. Several picks can retire in one frame during a
        // fast drag, and stopping at the first would let hover lag further behind with every
        // frame instead of catching up.
        while (_renderer.TryTakePick(out PickHit hit))
        {
            Selection.SetPreSelected(hit.Entity);
        }

        if (Selection.Version != _publishedSelection)
        {
            _publishedSelection = Selection.Version;
            _renderer.Highlights = Selection.ToHighlights(_renderer.Snapshot);
        }
    }

    /// <summary>How many device losses in quick succession before giving up.</summary>
    private const int MaxDeviceLossAttempts = 3;

    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmMouseWheel = 0x020A;
    private const int WmCaptureChanged = 0x0215;

    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public Point(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern nint SetCapture(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint window, ref Point point);

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
