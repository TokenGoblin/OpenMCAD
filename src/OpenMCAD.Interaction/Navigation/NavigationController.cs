using OpenMCAD.Render;

namespace OpenMCAD.Interaction.Navigation;

/// <summary>
/// Turns pointer events into camera movement (P2-T08).
/// </summary>
/// <remarks>
/// <para>
/// The camera itself knows how to orbit, pan and zoom; what lives here is the decision of how far
/// a drag of so many pixels should move it, and which button means which. Keeping the two apart is
/// what lets the camera arithmetic be tested without inventing mouse events, and this be tested
/// without a window.
/// </para>
/// <para>
/// <b>Rates are relative to the viewport, not absolute.</b> A drag across half the viewport turns
/// the model by the same amount whether the window is small or maximised. Fixed pixels-per-degree
/// feels sluggish in a large window and twitchy in a small one, and a user who resizes their window
/// should not have to relearn the gesture.
/// </para>
/// </remarks>
public sealed class NavigationController
{
    private PointerButton _button;
    private double _lastX;
    private double _lastY;

    /// <summary>Creates a controller driving a camera.</summary>
    /// <param name="camera">The camera to move.</param>
    /// <param name="profile">Which bindings to use. Defaults to <see cref="MouseProfile.Default"/>.</param>
    public NavigationController(Camera camera, MouseProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(camera);

        Camera = camera;
        Profile = profile ?? MouseProfile.Default;
    }

    /// <summary>Gets the camera being driven.</summary>
    public Camera Camera { get; }

    /// <summary>Gets or sets which gestures are bound to which buttons.</summary>
    public MouseProfile Profile { get; set; }

    /// <summary>Gets what the current drag is doing.</summary>
    public NavigationGesture ActiveGesture { get; private set; }

    /// <summary>Gets whether a navigation drag is in progress.</summary>
    public bool IsNavigating => ActiveGesture != NavigationGesture.None;

    /// <summary>
    /// Gets or sets how far a full-viewport-height drag turns the model, in radians.
    /// </summary>
    /// <remarks>
    /// A half turn by default. Enough that a model can be spun round without lifting the mouse,
    /// and slow enough to stop on a face. Both axes use the viewport <i>height</i>, so the same
    /// number of pixels turns the same amount in either direction — scaling yaw by the width
    /// instead makes rotation feel faster sideways on a wide monitor.
    /// </remarks>
    public double OrbitRadiansPerViewportHeight { get; set; } = System.Math.PI;

    /// <summary>Gets or sets how far one wheel notch zooms.</summary>
    /// <remarks>
    /// Multiplicative, so each notch covers the same proportion of the remaining distance rather
    /// than a fixed number of millimetres.
    /// </remarks>
    public double ZoomPerWheelNotch { get; set; } = 1.1;

    /// <summary>
    /// Gets or sets how far a full-viewport-height drag zooms, as a multiplier.
    /// </summary>
    public double ZoomPerDragViewportHeight { get; set; } = 4.0;

    /// <summary>Gets or sets whether the wheel direction is reversed.</summary>
    public bool InvertWheel { get; set; }

    /// <summary>
    /// Gets or sets whether the wheel zooms towards the pointer rather than the view centre.
    /// </summary>
    /// <remarks>
    /// On by default. Zooming towards the centre means every approach to a detail is zoom, pan,
    /// zoom, pan; zooming towards the pointer lets a user put the cursor on what they care about
    /// and simply arrive at it. It is the behaviour every modern CAD package and map has, and its
    /// absence is felt immediately even by users who could not say what is wrong.
    /// </remarks>
    public bool ZoomTowardsPointer { get; set; } = true;

    /// <summary>
    /// Begins a drag.
    /// </summary>
    /// <param name="button">Which button went down.</param>
    /// <param name="modifiers">Which modifiers were held.</param>
    /// <param name="x">Pointer column in physical pixels.</param>
    /// <param name="y">Pointer row in physical pixels.</param>
    /// <returns>
    /// Whether navigation claimed the press. <see langword="false"/> leaves it for selection or a
    /// tool.
    /// </returns>
    public bool PointerDown(PointerButton button, NavigationModifiers modifiers, double x, double y)
    {
        NavigationGesture gesture = Profile.Resolve(button, modifiers);

        if (gesture == NavigationGesture.None)
        {
            return false;
        }

        // A second button pressed mid-drag is ignored rather than allowed to switch gesture. A
        // drag that changes meaning under the user's hand is disorienting, and releasing either
        // button would then leave the state machine guessing which drag had ended.
        if (IsNavigating)
        {
            return true;
        }

        ActiveGesture = gesture;
        _button = button;
        _lastX = x;
        _lastY = y;

        return true;
    }

    /// <summary>
    /// Continues a drag.
    /// </summary>
    /// <param name="x">Pointer column in physical pixels.</param>
    /// <param name="y">Pointer row in physical pixels.</param>
    /// <param name="viewportWidth">Viewport width in physical pixels.</param>
    /// <param name="viewportHeight">Viewport height in physical pixels.</param>
    /// <returns>Whether the camera moved.</returns>
    public bool PointerMove(double x, double y, int viewportWidth, int viewportHeight)
    {
        if (!IsNavigating || viewportHeight <= 0 || viewportWidth <= 0)
        {
            return false;
        }

        double dx = x - _lastX;
        double dy = y - _lastY;

        _lastX = x;
        _lastY = y;

        if (dx == 0 && dy == 0)
        {
            return false;
        }

        // Screen y grows downwards and the camera's up axis grows upwards, so every vertical term
        // below is negated. Forgetting this is the classic inverted-drag bug.
        double acrossHeight = dx / viewportHeight;
        double downHeight = dy / viewportHeight;

        switch (ActiveGesture)
        {
            case NavigationGesture.Orbit:
                Camera.Orbit(
                    -acrossHeight * OrbitRadiansPerViewportHeight,
                    -downHeight * OrbitRadiansPerViewportHeight);

                return true;

            case NavigationGesture.Pan:
                Camera.Pan(acrossHeight, -downHeight);
                return true;

            case NavigationGesture.Zoom:
                // Dragging down zooms out, matching the wheel convention below and the direction
                // every package agrees on: pull towards you to back away.
                Camera.Zoom(System.Math.Pow(ZoomPerDragViewportHeight, downHeight));
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Ends a drag.
    /// </summary>
    /// <param name="button">Which button came up.</param>
    /// <returns>Whether this ended the drag.</returns>
    public bool PointerUp(PointerButton button)
    {
        if (!IsNavigating || button != _button)
        {
            return false;
        }

        Cancel();
        return true;
    }

    /// <summary>
    /// Zooms by the wheel.
    /// </summary>
    /// <param name="notches">Wheel notches. Positive is conventionally away from the user.</param>
    /// <param name="x">Pointer column in physical pixels.</param>
    /// <param name="y">Pointer row in physical pixels.</param>
    /// <param name="viewportWidth">Viewport width in physical pixels.</param>
    /// <param name="viewportHeight">Viewport height in physical pixels.</param>
    /// <returns>Whether the camera moved.</returns>
    public bool Wheel(double notches, double x, double y, int viewportWidth, int viewportHeight)
    {
        if (notches == 0 || !double.IsFinite(notches))
        {
            return false;
        }

        double direction = InvertWheel ? -notches : notches;

        // Away from the user zooms in, so the factor is below one.
        double factor = System.Math.Pow(ZoomPerWheelNotch, -direction);

        if (!ZoomTowardsPointer || viewportWidth <= 0 || viewportHeight <= 0)
        {
            Camera.Zoom(factor);
            return true;
        }

        ZoomAbout(factor, x, y, viewportWidth, viewportHeight);
        return true;
    }

    /// <summary>Abandons any drag in progress, leaving the camera where it is.</summary>
    /// <remarks>For a lost mouse capture, or a window deactivated mid-drag.</remarks>
    public void Cancel()
    {
        ActiveGesture = NavigationGesture.None;
        _button = PointerButton.None;
    }

    /// <summary>
    /// Zooms while holding the world point under the pointer still.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zooming scales the visible height by the same factor, so a point at a fixed screen offset
    /// from the centre moves outwards. Panning by the difference puts it back.
    /// </para>
    /// <para>
    /// The offset is measured in view heights, so the correction is
    /// <c>offset * (factor - 1) / factor</c> — expressed against the height <i>after</i> the zoom,
    /// which is what <see cref="Camera.Pan"/> works in once the zoom has been applied. Both
    /// projections scale their visible height by exactly the zoom factor, so the same correction
    /// serves perspective and orthographic alike.
    /// </para>
    /// <para>
    /// It holds the point on the <i>target plane</i>, not the surface actually under the cursor —
    /// that would need the depth buffer, and a readback per wheel notch. In a viewport framed on
    /// the model the two are close enough that nobody notices.
    /// </para>
    /// </remarks>
    private void ZoomAbout(double factor, double x, double y, int viewportWidth, int viewportHeight)
    {
        double offsetRight = (x - (viewportWidth * 0.5)) / viewportHeight;
        double offsetUp = -(y - (viewportHeight * 0.5)) / viewportHeight;

        double before = Camera.VisibleHeight();
        Camera.Zoom(factor);
        double after = Camera.VisibleHeight();

        if (before <= 0 || after <= 0 || !double.IsFinite(after))
        {
            return;
        }

        // Derived from the heights actually achieved rather than from the requested factor, so a
        // zoom the camera clamped or refused does not drag the model sideways.
        double correction = (after - before) / after;

        Camera.Pan(offsetRight * correction, offsetUp * correction);
    }
}
