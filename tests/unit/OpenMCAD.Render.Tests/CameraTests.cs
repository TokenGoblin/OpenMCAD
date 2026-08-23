using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Render;

using Xunit;

namespace OpenMCAD.Render.Tests;

/// <summary>
/// The camera and its navigation (P2-T08).
/// </summary>
/// <remarks>
/// All arithmetic, so all of it is checkable. These are the behaviours that decide whether a
/// viewport feels right, and every one of them has a wrong version that compiles and looks
/// plausible in a screenshot.
/// </remarks>
public sealed class CameraTests
{
    private const double Tight = 1e-9;

    private static Camera Default() => new() { Distance = 10.0, AspectRatio = 16.0 / 9.0 };

    // --- Frame ----------------------------------------------------------------------------------

    [Fact]
    public void TheEyeSitsBehindTheTargetAtTheGivenDistance()
    {
        Camera camera = Default();
        camera.Target = new Vec3d(1, 2, 3);

        (camera.Position - camera.Target).Length.Should().BeApproximately(10.0, Tight);
    }

    [Fact]
    public void TheCameraBasisIsOrthonormal()
    {
        // A basis that has drifted from orthonormal shears the view, which reads as the model
        // being slightly the wrong shape rather than as a camera fault.
        Camera camera = Default();
        camera.Orbit(0.7, -0.4);

        camera.Right.Length.Should().BeApproximately(1.0, 1e-12);
        camera.Up.Length.Should().BeApproximately(1.0, 1e-12);
        camera.Backward.Length.Should().BeApproximately(1.0, 1e-12);

        Vec3d.Dot(camera.Right, camera.Up).Should().BeApproximately(0.0, 1e-12);
        Vec3d.Dot(camera.Right, camera.Backward).Should().BeApproximately(0.0, 1e-12);
        Vec3d.Dot(camera.Up, camera.Backward).Should().BeApproximately(0.0, 1e-12);
    }

    [Fact]
    public void TheViewMatrixPutsTheTargetOnTheNegativeZAxis()
    {
        // The whole point of a view transform: the eye is at the origin looking down -Z.
        Camera camera = Default();
        camera.Target = new Vec3d(4, -2, 7);
        camera.Orbit(1.1, 0.3);

        Vec3d target = camera.ViewMatrix().TransformPoint(camera.Target);

        target.X.Should().BeApproximately(0.0, 1e-9);
        target.Y.Should().BeApproximately(0.0, 1e-9);
        target.Z.Should().BeApproximately(-camera.Distance, 1e-9);
    }

    // --- Orbit ----------------------------------------------------------------------------------

    [Fact]
    public void OrbitingKeepsTheTargetAndDistance()
    {
        Camera camera = Default();
        camera.Target = new Vec3d(5, 5, 5);

        camera.Orbit(0.9, 0.4);

        camera.Target.Should().Be(new Vec3d(5, 5, 5));
        camera.Distance.Should().BeApproximately(10.0, Tight);
    }

    [Fact]
    public void OrbitingDoesNotAccumulateRoll()
    {
        // The failure this guards. Applying yaw in camera space instead of world space lets roll
        // creep in a fraction of a degree at a time; after a minute of orbiting the model sits
        // visibly crooked and the user cannot say why or undo it.
        Camera camera = Default();

        for (int i = 0; i < 200; ++i)
        {
            camera.Orbit(0.11, 0.07);
            camera.Orbit(-0.11, -0.07);
        }

        // Back where it started, and level: the camera's right stays in the world's horizontal
        // plane whenever the view direction is not vertical.
        Vec3d.Dot(camera.Right, Vec3d.UnitZ).Should().BeApproximately(0.0, 1e-9);
    }

    [Fact]
    public void TheRightAxisStaysHorizontalWhileOrbiting()
    {
        Camera camera = Default();

        for (int i = 0; i < 24; ++i)
        {
            camera.Orbit(0.26, 0.13);

            // Not asserted at the poles, where the view direction is vertical and "horizontal
            // right" stops being meaningful.
            if (System.Math.Abs(Vec3d.Dot(camera.Backward, Vec3d.UnitZ)) < 0.99)
            {
                Vec3d.Dot(camera.Right, Vec3d.UnitZ).Should().BeApproximately(
                    0.0, 1e-9, "the horizon must stay level after {0} orbits", i + 1);
            }
        }
    }

    [Fact]
    public void OrbitingOverThePoleDoesNotTumbleTheView()
    {
        // Users look straight down at a part all the time. A camera that clamps stops dead; one
        // that flips is worse.
        Camera camera = Default();
        camera.LookFrom(StandardView.Front);

        for (int i = 0; i < 40; ++i)
        {
            camera.Orbit(0.0, 0.2);

            camera.Up.Length.Should().BeApproximately(1.0, 1e-9);
            camera.Position.Should().NotBe(camera.Target);
        }
    }

    // --- Pan ------------------------------------------------------------------------------------

    [Fact]
    public void PanningMovesTheSameFractionOfTheScreenAtAnyZoom()
    {
        // Panning in world units feels broken at both extremes: glacial when zoomed out, wild
        // when zoomed in. The same drag must always move the model by the same part of the view.
        Camera near = Default();
        near.Distance = 1.0;

        Camera far = Default();
        far.Distance = 100.0;

        Vec3d nearBefore = near.Target;
        Vec3d farBefore = far.Target;

        near.Pan(0.25, 0.0);
        far.Pan(0.25, 0.0);

        double nearMoved = (near.Target - nearBefore).Length / near.VisibleHeight();
        double farMoved = (far.Target - farBefore).Length / far.VisibleHeight();

        nearMoved.Should().BeApproximately(farMoved, 1e-12);
    }

    [Fact]
    public void PanningMovesAcrossTheViewPlaneOnly()
    {
        Camera camera = Default();
        camera.Orbit(0.6, 0.3);

        Vec3d before = camera.Target;
        camera.Pan(0.3, -0.2);

        // Nothing along the line of sight: panning must not dolly.
        Vec3d.Dot(camera.Target - before, camera.Backward).Should().BeApproximately(0.0, 1e-9);
    }

    // --- Zoom -----------------------------------------------------------------------------------

    [Fact]
    public void ZoomIsMultiplicativeSoEachNotchFeelsTheSame()
    {
        Camera camera = Default();
        camera.Distance = 10.0;

        camera.Zoom(1.1);
        camera.Zoom(1.1);

        camera.Distance.Should().BeApproximately(10.0 * 1.1 * 1.1, 1e-9);
    }

    [Fact]
    public void ZoomingOrthographicallyChangesTheViewVolumeNotTheDistance()
    {
        // Dollying a parallel projection changes nothing on screen. A camera that does it appears
        // to have stopped responding to the wheel.
        Camera camera = Default();
        camera.Projection = ProjectionMode.Orthographic;
        camera.OrthographicHeight = 4.0;
        double distance = camera.Distance;

        camera.Zoom(0.5);

        camera.OrthographicHeight.Should().BeApproximately(2.0, Tight);
        camera.Distance.Should().Be(distance);
    }

    [Fact]
    public void ZoomingCannotCollapseTheCameraOntoItsTarget()
    {
        Camera camera = Default();

        for (int i = 0; i < 500; ++i)
        {
            camera.Zoom(0.5);
        }

        camera.Distance.Should().BePositive();
        FluentActions.Invoking(() => camera.ViewMatrix()).Should().NotThrow(
            "a collapsed camera would produce a matrix of NaN and a blank viewport with no clue why");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void ANonsenseZoomIsIgnored(double factor)
    {
        Camera camera = Default();
        double before = camera.Distance;

        camera.Zoom(factor);

        camera.Distance.Should().Be(before);
    }

    // --- Zoom to fit ------------------------------------------------------------------------------

    [Fact]
    public void FittingCentresOnTheModel()
    {
        Camera camera = Default();
        Bounds3d bounds = new(new Vec3d(10, 20, 30), new Vec3d(12, 24, 36));

        camera.ZoomToFit(bounds);

        camera.Target.Should().Be(bounds.Center);
    }

    [Theory]
    [InlineData(16.0 / 9.0)]
    [InlineData(1.0)]
    [InlineData(0.5)]
    public void AFittedModelIsInsideTheFrustumOnBothAxes(double aspect)
    {
        // Fitting to the vertical field of view alone is the usual shortcut, and it clips a wide
        // model on a wide viewport -- a long bracket on a 16:9 screen, which is the common case.
        Camera camera = new() { AspectRatio = aspect };
        Bounds3d bounds = new(new Vec3d(-3, -1, -0.5), new Vec3d(3, 1, 0.5));

        camera.ZoomToFit(bounds);
        camera.LookFrom(StandardView.Isometric);
        camera.ZoomToFit(bounds);

        Mat4d viewProjection = camera.ProjectionMatrix(bounds) * camera.ViewMatrix();

        foreach (Vec3d corner in Corners(bounds))
        {
            Vec3d clip = viewProjection.TransformPoint(corner);

            clip.X.Should().BeInRange(-1.0, 1.0, "corner {0} must be within the frustum", corner);
            clip.Y.Should().BeInRange(-1.0, 1.0, "corner {0} must be within the frustum", corner);
            clip.Z.Should().BeInRange(0.0, 1.0, "corner {0} must be within the depth range", corner);
        }
    }

    [Fact]
    public void FittingUsesTheBoundingSphereSoOrbitingDoesNotResizeTheModel()
    {
        // Fitting the box makes the model breathe in and out as it is turned. Fitting the sphere
        // it sits in does not.
        Bounds3d bounds = new(new Vec3d(-2, -1, -0.5), new Vec3d(2, 1, 0.5));

        Camera front = new() { AspectRatio = 1.5 };
        front.LookFrom(StandardView.Front);
        front.ZoomToFit(bounds);

        Camera corner = new() { AspectRatio = 1.5 };
        corner.LookFrom(StandardView.Isometric);
        corner.ZoomToFit(bounds);

        corner.Distance.Should().BeApproximately(front.Distance, 1e-9);
    }

    [Fact]
    public void FittingNothingLeavesTheCameraAlone()
    {
        Camera camera = Default();
        Vec3d target = camera.Target;
        double distance = camera.Distance;

        camera.ZoomToFit(Bounds3d.Empty);

        camera.Target.Should().Be(target);
        camera.Distance.Should().Be(distance);
    }

    // --- Standard views ---------------------------------------------------------------------------

    [Theory]
    [InlineData(StandardView.Front, 0.0, -1.0, 0.0)]
    [InlineData(StandardView.Back, 0.0, 1.0, 0.0)]
    [InlineData(StandardView.Left, -1.0, 0.0, 0.0)]
    [InlineData(StandardView.Right, 1.0, 0.0, 0.0)]
    [InlineData(StandardView.Top, 0.0, 0.0, 1.0)]
    [InlineData(StandardView.Bottom, 0.0, 0.0, -1.0)]
    public void EachStandardViewLooksAlongItsAxis(StandardView view, double x, double y, double z)
    {
        Camera camera = Default();
        camera.LookFrom(view);

        Vec3d backward = camera.Backward;

        backward.X.Should().BeApproximately(x, 1e-9);
        backward.Y.Should().BeApproximately(y, 1e-9);
        backward.Z.Should().BeApproximately(z, 1e-9);
    }

    [Theory]
    [InlineData(StandardView.Front)]
    [InlineData(StandardView.Back)]
    [InlineData(StandardView.Left)]
    [InlineData(StandardView.Right)]
    [InlineData(StandardView.Isometric)]
    public void TheSideViewsPutWorldUpAtTheTopOfTheScreen(StandardView view)
    {
        // Z is up in the kernel, so it has to be up on screen. A front view showing a part on its
        // side is the sort of thing that survives review because nobody checks the obvious.
        Camera camera = Default();
        camera.LookFrom(view);

        Vec3d.Dot(camera.Up, Vec3d.UnitZ).Should().BePositive();
    }

    [Fact]
    public void EveryStandardViewProducesAUsableBasis()
    {
        foreach (StandardView view in Enum.GetValues<StandardView>())
        {
            Camera camera = Default();
            camera.LookFrom(view);

            camera.Right.Length.Should().BeApproximately(1.0, 1e-9, "{0}", view);
            camera.Up.Length.Should().BeApproximately(1.0, 1e-9, "{0}", view);
            Vec3d.Dot(camera.Right, camera.Up).Should().BeApproximately(0.0, 1e-9, "{0}", view);

            FluentActions.Invoking(() => camera.ViewMatrix()).Should().NotThrow("{0}", view);
        }
    }

    // --- Depth range --------------------------------------------------------------------------------

    [Fact]
    public void TheNearPlaneNeverCollapsesAgainstTheFar()
    {
        // Perspective depth precision is governed by the far-to-near ratio. Letting the near plane
        // approach zero as the camera nears a surface destroys precision exactly when the user has
        // zoomed in to look closely.
        Camera camera = Default();
        Bounds3d bounds = new(new Vec3d(-1, -1, -1), new Vec3d(1, 1, 1));

        camera.Distance = 1.0001;
        (double near, double far) = camera.DepthRange(bounds);

        near.Should().BePositive();
        (far / near).Should().BeLessThan(
            100_000.0, "an extreme depth ratio is what makes coplanar faces fight");
    }

    [Fact]
    public void TheSceneIsInsideTheDepthRange()
    {
        Camera camera = Default();
        Bounds3d bounds = new(new Vec3d(-5, -5, -5), new Vec3d(5, 5, 5));
        camera.ZoomToFit(bounds);

        (double near, double far) = camera.DepthRange(bounds);

        foreach (Vec3d corner in Corners(bounds))
        {
            double depth = -camera.ViewMatrix().TransformPoint(corner).Z;
            depth.Should().BeInRange(near, far, "corner {0} must be between the planes", corner);
        }
    }

    [Fact]
    public void AnOrthographicViewDoesNotClipBehindTheCamera()
    {
        // A parallel projection looks identical from anywhere along its axis, so a user who has
        // orbited into a part does not expect half of it to disappear.
        Camera camera = Default();
        camera.Projection = ProjectionMode.Orthographic;

        Bounds3d bounds = new(new Vec3d(-1, -1, -1), new Vec3d(1, 1, 1));
        (double near, _) = camera.DepthRange(bounds);

        near.Should().BeNegative();
    }

    [Fact]
    public void AnEmptySceneStillProducesAUsableProjection()
    {
        Camera camera = Default();

        FluentActions.Invoking(() => camera.ProjectionMatrix(Bounds3d.Empty)).Should().NotThrow();
    }

    private static IEnumerable<Vec3d> Corners(Bounds3d bounds)
    {
        for (int i = 0; i < 8; ++i)
        {
            yield return new Vec3d(
                (i & 1) == 0 ? bounds.Min.X : bounds.Max.X,
                (i & 2) == 0 ? bounds.Min.Y : bounds.Max.Y,
                (i & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);
        }
    }
}
