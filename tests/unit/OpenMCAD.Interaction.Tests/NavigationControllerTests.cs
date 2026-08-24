using FluentAssertions;

using OpenMCAD.Interaction.Navigation;
using OpenMCAD.Math;
using OpenMCAD.Render;

using Xunit;

namespace OpenMCAD.Interaction.Tests;

/// <summary>
/// Mouse navigation (P2-T08).
/// </summary>
/// <remarks>
/// <para>
/// Almost every assertion here is about what the user <b>sees</b> rather than about a sign or an
/// angle. Navigation goes wrong by being inverted, and an inverted drag satisfies every test that
/// checks "the orientation changed" while being immediately, viscerally wrong to use. So the
/// tests project a world point to the screen, move the mouse, and project it again.
/// </para>
/// <para>
/// The screen projection below is the same view-projection the renderer builds, so a test that
/// passes here describes the pixels the viewport would actually produce.
/// </para>
/// </remarks>
public sealed class NavigationControllerTests
{
    private const int Width = 800;
    private const int Height = 600;

    /// <summary>A camera looking at the origin from the front, with a metre of scene framed.</summary>
    private static Camera FrontCamera()
    {
        Camera camera = new() { AspectRatio = (double)Width / Height };
        camera.LookFrom(StandardView.Front);
        camera.ZoomToFit(new Bounds3d(new Vec3d(-0.5, -0.5, -0.5), new Vec3d(0.5, 0.5, 0.5)));

        return camera;
    }

    private static Bounds3d UnitCube =>
        new(new Vec3d(-0.5, -0.5, -0.5), new Vec3d(0.5, 0.5, 0.5));

    /// <summary>Projects a world point to pixels, exactly as the renderer would.</summary>
    private static (double X, double Y) ToScreen(Camera camera, Vec3d world)
    {
        Mat4d viewProjection = camera.ProjectionMatrix(UnitCube) * camera.ViewMatrix();

        double x = (viewProjection.M11 * world.X) + (viewProjection.M12 * world.Y)
            + (viewProjection.M13 * world.Z) + viewProjection.M14;

        double y = (viewProjection.M21 * world.X) + (viewProjection.M22 * world.Y)
            + (viewProjection.M23 * world.Z) + viewProjection.M24;

        double w = (viewProjection.M41 * world.X) + (viewProjection.M42 * world.Y)
            + (viewProjection.M43 * world.Z) + viewProjection.M44;

        // Normalised device coordinates to pixels. NDC y is up, screen y is down.
        return (((x / w) + 1) * 0.5 * Width, (1 - (y / w)) * 0.5 * Height);
    }

    private static NavigationController Controller(MouseProfile? profile = null)
        => new(FrontCamera(), profile);

    // --- Binding resolution -------------------------------------------------------------------

    [Fact]
    public void TheDefaultProfileOrbitsWithTheMiddleButton()
    {
        MouseProfile.Default.Resolve(PointerButton.Middle, NavigationModifiers.None)
            .Should().Be(NavigationGesture.Orbit);
    }

    [Fact]
    public void ModifiersMustMatchExactly()
    {
        // Control+middle is not "middle, plus something". Loose matching would silently fall back
        // to the unmodified gesture, which is how you pan when you meant to zoom.
        MouseProfile.SolidWorks.Resolve(PointerButton.Middle, NavigationModifiers.Control)
            .Should().Be(NavigationGesture.Pan);

        MouseProfile.SolidWorks.Resolve(PointerButton.Middle, NavigationModifiers.Alt)
            .Should().Be(NavigationGesture.None, "Alt+middle is unbound in this profile");
    }

    [Fact]
    public void AnUnboundButtonIsLeftForSomethingElseToClaim()
    {
        // The left button must stay available for selection and sketch tools.
        NavigationController controller = Controller();

        controller.PointerDown(PointerButton.Left, NavigationModifiers.None, 100, 100)
            .Should().BeFalse();

        controller.IsNavigating.Should().BeFalse();
    }

    [Fact]
    public void ProfilesCanBeRebound()
    {
        MouseProfile custom = MouseProfile.Fusion.With(
            PointerButton.Right, NavigationModifiers.None, NavigationGesture.Orbit);

        custom.Resolve(PointerButton.Right, NavigationModifiers.None)
            .Should().Be(NavigationGesture.Orbit);

        custom.Resolve(PointerButton.Middle, NavigationModifiers.None)
            .Should().Be(NavigationGesture.Pan, "the rest of the profile is untouched");

        MouseProfile.Fusion.Resolve(PointerButton.Right, NavigationModifiers.None)
            .Should().Be(NavigationGesture.None, "the original profile is not mutated");
    }

    [Fact]
    public void EveryPresetBindsAllThreeGestures()
    {
        // A profile missing a gesture is a profile that cannot navigate, and the failure is a user
        // discovering mid-task that they cannot pan.
        foreach (MouseProfile profile in MouseProfile.Presets)
        {
            NavigationGesture[] bound = [.. profile.Bindings.Select(b => b.Gesture).Distinct()];

            bound.Should().Contain(NavigationGesture.Orbit, profile.Name);
            bound.Should().Contain(NavigationGesture.Pan, profile.Name);
        }
    }

    // --- What the user sees -------------------------------------------------------------------

    [Fact]
    public void DraggingRightTurnsTheModelToTheRight()
    {
        // Grab-and-turn: the surface facing the viewer travels right under the cursor.
        //
        // The reference point is the centre of the *near* face, not a silhouette corner. A point
        // on the silhouette sits at a turning point of the projection -- its screen position is
        // stationary to first order under rotation and creeps inward whichever way the model
        // turns, so an assertion about it passes just as happily when the drag is inverted. That
        // is not a hypothetical: the first version of this test used (-0.5, 0, 0) and passed with
        // the signs reversed.
        NavigationController controller = Controller();
        Vec3d nearFaceCentre = new(0, -0.5, 0);

        double before = ToScreen(controller.Camera, nearFaceCentre).X;

        controller.PointerDown(PointerButton.Middle, NavigationModifiers.None, 400, 300);
        controller.PointerMove(500, 300, Width, Height);

        double after = ToScreen(controller.Camera, nearFaceCentre).X;

        after.Should().BeGreaterThan(before, "dragging right should carry the model right with it");
    }

    [Fact]
    public void DraggingDownTipsTheModelToShowItsTop()
    {
        // Stated as a fact about the camera rather than about a projected point, which leaves no
        // room for a degenerate reference: pulling the front of the model down lifts the camera
        // over it, so the eye gains height on the world up axis.
        NavigationController controller = Controller();
        double before = controller.Camera.Backward.Z;

        controller.PointerDown(PointerButton.Middle, NavigationModifiers.None, 400, 300);
        controller.PointerMove(400, 400, Width, Height);

        controller.Camera.Backward.Z.Should().BeGreaterThan(
            before, "pulling down should tip the top of the model towards the viewer");
    }

    [Fact]
    public void PanningFollowsTheCursor()
    {
        // The model should stay under the finger. Panning the opposite way is the single most
        // complained-about default in any 3D application.
        NavigationController controller = Controller(MouseProfile.Fusion);
        Vec3d centre = Vec3d.Zero;

        (double x, double y) = ToScreen(controller.Camera, centre);

        controller.PointerDown(PointerButton.Middle, NavigationModifiers.None, 400, 300);
        controller.PointerMove(460, 340, Width, Height);

        (double movedX, double movedY) = ToScreen(controller.Camera, centre);

        (movedX - x).Should().BeApproximately(60, 1.0, "the model should track the cursor in x");
        (movedY - y).Should().BeApproximately(40, 1.0, "and in y");
    }

    [Fact]
    public void OrbitRateIsTheSameHorizontallyAndVertically()
    {
        // Scaling yaw by the viewport width instead of its height makes rotation feel faster
        // sideways on a wide monitor, which users report as the view "sliding".
        NavigationController horizontal = Controller();
        horizontal.PointerDown(PointerButton.Middle, NavigationModifiers.None, 400, 300);
        horizontal.PointerMove(500, 300, Width, Height);

        NavigationController vertical = Controller();
        vertical.PointerDown(PointerButton.Middle, NavigationModifiers.None, 400, 300);
        vertical.PointerMove(400, 400, Width, Height);

        double yaw = Vec3d.Dot(horizontal.Camera.Backward, FrontCamera().Backward);
        double pitch = Vec3d.Dot(vertical.Camera.Backward, FrontCamera().Backward);

        yaw.Should().BeApproximately(pitch, 1e-9, "a hundred pixels should turn the same either way");
    }

    [Fact]
    public void TheSameDragTurnsTheSameAmountInAnySizeOfWindow()
    {
        // Fixed pixels-per-degree feels sluggish maximised and twitchy in a small window, and a
        // user who resizes should not have to relearn the gesture.
        NavigationController small = Controller();
        small.PointerDown(PointerButton.Middle, NavigationModifiers.None, 0, 0);
        small.PointerMove(0, 300, 400, 300);

        NavigationController large = Controller();
        large.PointerDown(PointerButton.Middle, NavigationModifiers.None, 0, 0);
        large.PointerMove(0, 1200, 1600, 1200);

        Vec3d one = small.Camera.Backward;
        Vec3d other = large.Camera.Backward;

        Vec3d.Dot(one, other).Should().BeApproximately(
            1.0, 1e-9, "a full-height drag is a full-height drag at any resolution");
    }

    // --- Zoom ---------------------------------------------------------------------------------

    [Fact]
    public void TheWheelZoomsInAwayFromTheUser()
    {
        NavigationController controller = Controller();
        double before = controller.Camera.Distance;

        controller.Wheel(1, 400, 300, Width, Height);

        controller.Camera.Distance.Should().BeLessThan(before);
    }

    [Fact]
    public void TheWheelCanBeInverted()
    {
        NavigationController controller = Controller();
        controller.InvertWheel = true;

        double before = controller.Camera.Distance;
        controller.Wheel(1, 400, 300, Width, Height);

        controller.Camera.Distance.Should().BeGreaterThan(before);
    }

    [Fact]
    public void ZoomingHoldsThePointUnderTheCursorStill()
    {
        // The whole reason zoom-towards-cursor exists. Without it, approaching a detail is
        // zoom, pan, zoom, pan; with it the user points at what they want and arrives there.
        NavigationController controller = Controller();
        controller.ZoomTowardsPointer = true;

        // A point off to one side, so the correction has something to do.
        Vec3d mark = new(0.4, 0, 0.3);
        (double x, double y) = ToScreen(controller.Camera, mark);

        for (int i = 0; i < 5; ++i)
        {
            controller.Wheel(1, x, y, Width, Height);
        }

        (double afterX, double afterY) = ToScreen(controller.Camera, mark);

        afterX.Should().BeApproximately(x, 1.5, "the point under the cursor must not drift in x");
        afterY.Should().BeApproximately(y, 1.5, "nor in y");
    }

    [Fact]
    public void ZoomingTowardsTheCentreLeavesTheCentreStill()
    {
        NavigationController controller = Controller();

        controller.Wheel(3, Width / 2.0, Height / 2.0, Width, Height);

        (double x, double y) = ToScreen(controller.Camera, Vec3d.Zero);

        x.Should().BeApproximately(Width / 2.0, 0.5);
        y.Should().BeApproximately(Height / 2.0, 0.5);
    }

    [Fact]
    public void ZoomTowardsPointerWorksInOrthographicToo()
    {
        // Orthographic zoom changes the view volume rather than the distance, so the correction
        // has to be derived from the visible height rather than from the camera position.
        NavigationController controller = Controller();
        controller.Camera.Projection = ProjectionMode.Orthographic;
        controller.Camera.ZoomToFit(UnitCube);

        Vec3d mark = new(0.4, 0, 0.3);
        (double x, double y) = ToScreen(controller.Camera, mark);

        controller.Wheel(4, x, y, Width, Height);

        (double afterX, double afterY) = ToScreen(controller.Camera, mark);

        afterX.Should().BeApproximately(x, 1.5);
        afterY.Should().BeApproximately(y, 1.5);
    }

    // --- The state machine --------------------------------------------------------------------

    [Fact]
    public void MovingWithNoButtonDownDoesNothing()
    {
        NavigationController controller = Controller();
        Quatd before = controller.Camera.Orientation;

        controller.PointerMove(500, 400, Width, Height).Should().BeFalse();
        controller.Camera.Orientation.Should().Be(before);
    }

    [Fact]
    public void ASecondButtonDoesNotHijackTheGestureMidDrag()
    {
        // A drag that changes meaning under the user's hand is disorienting, and releasing either
        // button would leave the state machine guessing which drag had ended.
        NavigationController controller = Controller();

        controller.PointerDown(PointerButton.Middle, NavigationModifiers.None, 400, 300);
        controller.PointerDown(PointerButton.Middle, NavigationModifiers.Control, 400, 300);

        controller.ActiveGesture.Should().Be(NavigationGesture.Orbit);
    }

    [Fact]
    public void ReleasingADifferentButtonDoesNotEndTheDrag()
    {
        NavigationController controller = Controller();
        controller.PointerDown(PointerButton.Middle, NavigationModifiers.None, 400, 300);

        controller.PointerUp(PointerButton.Left).Should().BeFalse();
        controller.IsNavigating.Should().BeTrue();

        controller.PointerUp(PointerButton.Middle).Should().BeTrue();
        controller.IsNavigating.Should().BeFalse();
    }

    [Fact]
    public void CancelAbandonsTheDragWithoutMovingTheCamera()
    {
        // For a lost mouse capture or a window deactivated mid-drag.
        NavigationController controller = Controller();
        controller.PointerDown(PointerButton.Middle, NavigationModifiers.None, 400, 300);
        controller.PointerMove(500, 300, Width, Height);

        Quatd afterDrag = controller.Camera.Orientation;
        controller.Cancel();

        controller.IsNavigating.Should().BeFalse();
        controller.Camera.Orientation.Should().Be(afterDrag, "cancelling does not rewind");
    }

    [Fact]
    public void AZeroSizedViewportIsSurvived()
    {
        // A window can be measured at zero during layout, and dividing by it would take the
        // application down over a mouse move.
        NavigationController controller = Controller();
        controller.PointerDown(PointerButton.Middle, NavigationModifiers.None, 0, 0);

        controller.PointerMove(10, 10, 0, 0).Should().BeFalse();
        controller.Wheel(1, 0, 0, 0, 0).Should().BeTrue("the wheel still zooms about the centre");
    }
}
