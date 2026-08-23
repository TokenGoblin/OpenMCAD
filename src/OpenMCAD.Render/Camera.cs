using OpenMCAD.Math;

namespace OpenMCAD.Render;

/// <summary>Whether the camera projects with perspective or orthographically.</summary>
public enum ProjectionMode
{
    /// <summary>Converging rays. What the eye does, and what a rendering looks like.</summary>
    Perspective,

    /// <summary>
    /// Parallel rays. What an engineering drawing does, and the default for measuring against.
    /// </summary>
    Orthographic,
}

/// <summary>The named directions a CAD user expects from a view cube or a keyboard shortcut.</summary>
/// <remarks>
/// Z-up, matching the kernel: a cylinder built with no placement runs along Z, so "top" has to be
/// +Z or every standard view is wrong relative to the geometry.
/// </remarks>
public enum StandardView
{
    /// <summary>Looking along +Y.</summary>
    Front,

    /// <summary>Looking along -Y.</summary>
    Back,

    /// <summary>Looking along +X.</summary>
    Left,

    /// <summary>Looking along -X.</summary>
    Right,

    /// <summary>Looking down, along -Z.</summary>
    Top,

    /// <summary>Looking up, along +Z.</summary>
    Bottom,

    /// <summary>The conventional three-quarter view, from the front-right-top octant.</summary>
    Isometric,
}

/// <summary>
/// An orbiting camera (P2-T08).
/// </summary>
/// <remarks>
/// <para>
/// Modelled as a target, a distance and an orientation, rather than as a free eye position. Every
/// CAD navigation idiom is defined against a pivot — orbit turns about it, zoom moves toward it,
/// zoom-to-fit chooses it — and a camera stored as position-plus-direction has to reconstruct that
/// pivot on every gesture, which is where drift comes from.
/// </para>
/// <para>
/// Mutable, and owned by whatever is handling input. It is not part of a
/// <see cref="DisplaySnapshot"/>: the snapshot is what the rebuild produced and the camera is
/// where the user is looking, and conflating them would make every camera movement invalidate the
/// scene.
/// </para>
/// </remarks>
public sealed class Camera
{
    /// <summary>The default vertical field of view, in radians (about 35 degrees).</summary>
    /// <remarks>
    /// Narrower than a game's sixty or ninety. A wide angle exaggerates perspective, and on
    /// mechanical geometry that reads as parts being subtly the wrong shape — a bore looks
    /// elliptical, a square boss looks tapered.
    /// </remarks>
    public const double DefaultFieldOfView = 0.6108652381980153;

    /// <summary>How much of the viewport a fitted model occupies, leaving a margin.</summary>
    public const double FitMargin = 0.9;

    private double _distance = 1.0;
    private double _fieldOfView = DefaultFieldOfView;
    private double _aspectRatio = 1.0;
    private double _orthographicHeight = 1.0;

    /// <summary>Gets or sets the point the camera orbits and looks at.</summary>
    public Vec3d Target { get; set; } = Vec3d.Zero;

    /// <summary>Gets or sets the camera's orientation.</summary>
    /// <remarks>
    /// The rotation taking the camera's own frame to the world's. Its third column is the
    /// direction from the target back to the eye.
    /// </remarks>
    public Quatd Orientation { get; set; } = Quatd.Identity;

    /// <summary>Gets or sets how far the eye is from the target, in metres.</summary>
    /// <remarks>
    /// Clamped away from zero. A camera at its own target has no view direction, and the
    /// alternative to clamping is a matrix full of NaN and a blank viewport with no explanation.
    /// </remarks>
    public double Distance
    {
        get => _distance;
        set => _distance = System.Math.Max(value, Tolerance.Linear);
    }

    /// <summary>Gets or sets the vertical field of view, in radians.</summary>
    public double FieldOfView
    {
        get => _fieldOfView;
        set => _fieldOfView = System.Math.Clamp(value, 0.01, System.Math.PI - 0.01);
    }

    /// <summary>Gets or sets the viewport's width divided by its height.</summary>
    public double AspectRatio
    {
        get => _aspectRatio;
        set => _aspectRatio = value > 0 && double.IsFinite(value) ? value : 1.0;
    }

    /// <summary>Gets or sets how the camera projects.</summary>
    public ProjectionMode Projection { get; set; } = ProjectionMode.Perspective;

    /// <summary>Gets or sets the height of the orthographic view volume, in metres.</summary>
    /// <remarks>
    /// Orthographic zoom is this, not <see cref="Distance"/>. Moving a parallel projection toward
    /// its subject changes nothing at all about the image, so a camera that dollies in
    /// orthographic mode appears to the user to have stopped responding.
    /// </remarks>
    public double OrthographicHeight
    {
        get => _orthographicHeight;
        set => _orthographicHeight = System.Math.Max(value, Tolerance.Linear);
    }

    /// <summary>Gets the direction from the target towards the eye.</summary>
    public Vec3d Backward => Mat4d.FromRotation(Orientation).TransformDirection(Vec3d.UnitZ);

    /// <summary>Gets the camera's up direction.</summary>
    public Vec3d Up => Mat4d.FromRotation(Orientation).TransformDirection(Vec3d.UnitY);

    /// <summary>Gets the camera's right direction.</summary>
    public Vec3d Right => Mat4d.FromRotation(Orientation).TransformDirection(Vec3d.UnitX);

    /// <summary>Gets where the eye is.</summary>
    public Vec3d Position => Target + (Backward * Distance);

    /// <summary>Builds the world-to-view transform.</summary>
    /// <returns>The view matrix.</returns>
    public Mat4d ViewMatrix() => Mat4d.LookAt(Position, Target, Up);

    /// <summary>
    /// Builds the view-to-clip transform for a scene of the given extent.
    /// </summary>
    /// <param name="sceneBounds">
    /// What has to remain visible. Pass <see cref="Bounds3d.Empty"/> if nothing is loaded.
    /// </param>
    /// <returns>The projection matrix.</returns>
    /// <remarks>
    /// The near and far planes are derived from the scene rather than fixed, because perspective
    /// depth precision depends on their <i>ratio</i>. A constant near plane of a millimetre with a
    /// far plane far enough to hold an assembly throws away most of the depth buffer, and the
    /// symptom is z-fighting between coplanar faces that appears only on large models.
    /// </remarks>
    public Mat4d ProjectionMatrix(Bounds3d sceneBounds)
    {
        (double near, double far) = DepthRange(sceneBounds);

        return Projection == ProjectionMode.Orthographic
            ? Mat4d.Orthographic(OrthographicHeight * AspectRatio, OrthographicHeight, near, far)
            : Mat4d.PerspectiveFieldOfView(FieldOfView, AspectRatio, near, far);
    }

    /// <summary>
    /// Chooses near and far planes that contain the scene.
    /// </summary>
    /// <param name="sceneBounds">The scene's extent.</param>
    /// <returns>The near and far distances.</returns>
    /// <remarks>
    /// <para>
    /// Both are derived from how far the scene actually reaches from the eye. The near plane is
    /// additionally floored at a fraction of the far plane: letting it approach zero as the camera
    /// nears a surface would collapse depth precision exactly when the user has zoomed in to look
    /// closely, which is the worst possible moment for the faces to start fighting.
    /// </para>
    /// <para>
    /// The orthographic case does not clip behind the camera. A parallel projection looks the same
    /// from anywhere along its axis, so a user who has orbited the camera inside a part does not
    /// expect half of it to vanish.
    /// </para>
    /// </remarks>
    public (double Near, double Far) DepthRange(Bounds3d sceneBounds)
    {
        double radius = sceneBounds.IsEmpty
            ? System.Math.Max(Distance, 1.0)
            : System.Math.Max(sceneBounds.DiagonalLength * 0.5, Tolerance.Linear);

        Vec3d centre = sceneBounds.IsEmpty ? Target : sceneBounds.Center;
        double toCentre = (centre - Position).Length;

        double far = toCentre + radius;

        if (Projection == ProjectionMode.Orthographic)
        {
            // Symmetric about the scene, so nothing is clipped from either side.
            return (-(toCentre + radius), far);
        }

        double near = System.Math.Max(toCentre - radius, far / 10_000.0);
        return (System.Math.Max(near, Tolerance.Linear), far);
    }

    /// <summary>
    /// Turns the camera about its target.
    /// </summary>
    /// <param name="yaw">Rotation about the world up axis, in radians.</param>
    /// <param name="pitch">Rotation about the camera's right axis, in radians.</param>
    /// <remarks>
    /// <para>
    /// Yaw is applied about the <b>world</b> up and pitch about the <b>camera's</b> right. Doing
    /// both in camera space lets roll accumulate, and a CAD view that has quietly rolled a few
    /// degrees is disorienting in a way users find hard to name or correct.
    /// </para>
    /// <para>
    /// Pitch is not clamped at the poles. Clamping stops the view dead just as a user tries to
    /// look straight down at a part, which is a common thing to want; the roll-free construction
    /// above is what keeps passing over the pole from tumbling the view.
    /// </para>
    /// </remarks>
    public void Orbit(double yaw, double pitch)
    {
        if (yaw != 0.0)
        {
            Orientation = (Quatd.FromAxisAngle(Vec3d.UnitZ, yaw) * Orientation).Normalized();
        }

        if (pitch != 0.0)
        {
            Orientation = (Quatd.FromAxisAngle(Right, pitch) * Orientation).Normalized();
        }
    }

    /// <summary>
    /// Slides the camera and its target across the view plane.
    /// </summary>
    /// <param name="right">How far to move in the camera's right direction, in view heights.</param>
    /// <param name="up">How far to move in the camera's up direction, in view heights.</param>
    /// <remarks>
    /// Measured in fractions of the visible height rather than in metres, so that dragging by the
    /// same number of pixels moves the model by the same fraction of the screen no matter how far
    /// the camera has zoomed. Panning at a fixed rate in world units feels broken at both extremes.
    /// </remarks>
    public void Pan(double right, double up)
    {
        double height = VisibleHeight();
        Vec3d offset = (Right * (-right * height)) + (Up * (-up * height));
        Target += offset;
    }

    /// <summary>
    /// Zooms by a multiplicative factor.
    /// </summary>
    /// <param name="factor">
    /// Greater than one zooms out, less than one zooms in. A wheel notch is conventionally about
    /// 1.1.
    /// </param>
    /// <remarks>
    /// Multiplicative rather than additive, so that each wheel notch covers the same proportion of
    /// the remaining distance. Additive zoom crawls when far away and overshoots into the model
    /// when close.
    /// </remarks>
    public void Zoom(double factor)
    {
        if (factor <= 0 || !double.IsFinite(factor))
        {
            return;
        }

        if (Projection == ProjectionMode.Orthographic)
        {
            OrthographicHeight *= factor;
        }
        else
        {
            Distance *= factor;
        }
    }

    /// <summary>Frames a volume so it fills the viewport with a margin.</summary>
    /// <param name="bounds">What to frame. An empty volume is ignored.</param>
    /// <remarks>
    /// <para>
    /// Fitted against both axes. Fitting to the vertical field of view alone is the common
    /// shortcut and it clips a wide model on a wide viewport, which is the usual case — a long
    /// bracket viewed on a 16:9 screen.
    /// </para>
    /// <para>
    /// The bounding sphere is used rather than the box, so that the framing does not change as the
    /// model is orbited. Fitting the box makes the model appear to breathe in and out while the
    /// user turns it.
    /// </para>
    /// </remarks>
    public void ZoomToFit(Bounds3d bounds)
    {
        if (bounds.IsEmpty)
        {
            return;
        }

        Target = bounds.Center;

        double radius = System.Math.Max(bounds.DiagonalLength * 0.5, Tolerance.Linear);
        double height = radius * 2.0 / FitMargin;

        OrthographicHeight = height;

        // The horizontal field of view is the vertical one widened by the aspect ratio, so on a
        // viewport narrower than it is tall the horizontal one is the binding constraint.
        double halfVertical = FieldOfView * 0.5;
        double halfHorizontal = System.Math.Atan(System.Math.Tan(halfVertical) * AspectRatio);
        double limiting = System.Math.Min(halfVertical, halfHorizontal);

        Distance = radius / System.Math.Sin(limiting) / FitMargin;
    }

    /// <summary>Points the camera along a named axis, keeping the current target and distance.</summary>
    /// <param name="view">Which view.</param>
    public void LookFrom(StandardView view)
    {
        // The camera looks along its own -Z, so the orientation's Z axis is the direction from the
        // target back towards the eye -- the opposite of the direction of sight.
        (Vec3d backward, Vec3d up) = view switch
        {
            StandardView.Front => (-Vec3d.UnitY, Vec3d.UnitZ),
            StandardView.Back => (Vec3d.UnitY, Vec3d.UnitZ),
            StandardView.Left => (-Vec3d.UnitX, Vec3d.UnitZ),
            StandardView.Right => (Vec3d.UnitX, Vec3d.UnitZ),

            // Looking straight down, world up is along the line of sight and cannot also be the
            // camera's up. Front-facing-up is the convention every CAD package uses for a top view.
            StandardView.Top => (Vec3d.UnitZ, Vec3d.UnitY),
            StandardView.Bottom => (-Vec3d.UnitZ, -Vec3d.UnitY),

            _ => (new Vec3d(1.0, -1.0, 1.0).Normalized(), Vec3d.UnitZ),
        };

        Vec3d right = Vec3d.Cross(up, backward).Normalized();
        Vec3d trueUp = Vec3d.Cross(backward, right);

        Orientation = (Quatd.FromBasis(right, trueUp, backward)).Normalized();
    }

    /// <summary>How much of the world is visible vertically at the target, in metres.</summary>
    /// <returns>The visible height.</returns>
    public double VisibleHeight()
        => Projection == ProjectionMode.Orthographic
            ? OrthographicHeight
            : 2.0 * Distance * System.Math.Tan(FieldOfView * 0.5);
}
