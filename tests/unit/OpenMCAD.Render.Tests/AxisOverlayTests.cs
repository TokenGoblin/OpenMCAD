using System.Numerics;
using System.Runtime.InteropServices;

using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Render.Direct3D12;

using Xunit;

namespace OpenMCAD.Render.Tests;

/// <summary>
/// The origin triad and the corner orientation gizmo (P2-T11, P2-T08).
/// </summary>
/// <remarks>
/// The gizmo's whole job is to report the camera's rotation and nothing else. Every test here is
/// about what it must ignore as much as what it must follow: a gizmo that drifted when the user
/// panned, or grew when they zoomed, would be answering a question nobody asked and would be
/// actively misleading about the one it was put there for.
/// </remarks>
public sealed class AxisOverlayTests
{
    private const int Width = 800;
    private const int Height = 600;

    private static AxisStyle Style => AxisStyle.Default;

    private static Camera Isometric()
    {
        Camera camera = new() { AspectRatio = (double)Width / Height };
        camera.LookFrom(StandardView.Isometric);
        camera.ZoomToFit(new Bounds3d(new Vec3d(-1, -1, -1), new Vec3d(1, 1, 1)));

        return camera;
    }

    /// <summary>Where a direction lands, in pixels from the top-left.</summary>
    private static (double X, double Y) ToPixels(Matrix4x4 transform, Vec3d direction)
    {
        Vector4 point = new((float)direction.X, (float)direction.Y, (float)direction.Z, 1);

        // Row-major, matching the shader's row_major declaration.
        float x = (transform.M11 * point.X) + (transform.M12 * point.Y)
            + (transform.M13 * point.Z) + transform.M14;

        float y = (transform.M21 * point.X) + (transform.M22 * point.Y)
            + (transform.M23 * point.Z) + transform.M24;

        float w = (transform.M41 * point.X) + (transform.M42 * point.Y)
            + (transform.M43 * point.Z) + transform.M44;

        return (((x / w) + 1) * 0.5 * Width, (1 - (y / w)) * 0.5 * Height);
    }

    [Fact]
    public void TheConstantsMatchTheShaderPacking()
    {
        AxisConstants.SizeInBytes.Should().Be(80);

        Marshal.OffsetOf<AxisConstants>(nameof(AxisConstants.ViewportSize)).ToInt32()
            .Should().Be(64);

        Marshal.OffsetOf<AxisConstants>(nameof(AxisConstants.HalfWidthPixels)).ToInt32()
            .Should().Be(72);
    }

    // --- The gizmo ----------------------------------------------------------------------------

    [Fact]
    public void TheGizmoSitsInTheCornerItIsAskedFor()
    {
        foreach ((GizmoCorner corner, bool left, bool top) in new[]
        {
            (GizmoCorner.BottomLeft, true, false),
            (GizmoCorner.BottomRight, false, false),
            (GizmoCorner.TopLeft, true, true),
            (GizmoCorner.TopRight, false, true),
        })
        {
            AxisStyle style = Style with { Corner = corner };

            Matrix4x4 transform = AxisOverlayPass.GizmoTransform(
                Isometric(), style, Width, Height);

            (double x, double y) = ToPixels(transform, Vec3d.Zero);

            (x < Width / 2.0).Should().Be(left, $"{corner} horizontal");
            (y < Height / 2.0).Should().Be(top, $"{corner} vertical");
        }
    }

    [Fact]
    public void TheGizmoIsSquareWhateverShapeTheViewportIs()
    {
        // Scaling both axes by the viewport height, or by the width, would stretch the gizmo with
        // the window -- and a stretched set of axes misreports every angle in it.
        Camera camera = new() { AspectRatio = 3.0 };
        camera.LookFrom(StandardView.Front);
        camera.ZoomToFit(new Bounds3d(new Vec3d(-1, -1, -1), new Vec3d(1, 1, 1)));

        Matrix4x4 transform = AxisOverlayPass.GizmoTransform(camera, Style, 1200, 400);

        // In a front view the camera's right is world X and its up is world Z, so those two axes
        // lie along the screen axes and their lengths are directly comparable.
        Vector4 origin = Transform(transform, Vec3d.Zero);
        Vector4 alongRight = Transform(transform, camera.Right);
        Vector4 alongUp = Transform(transform, camera.Up);

        double horizontalPixels = System.Math.Abs(alongRight.X - origin.X) * 0.5 * 1200;
        double verticalPixels = System.Math.Abs(alongUp.Y - origin.Y) * 0.5 * 400;

        horizontalPixels.Should().BeApproximately(Style.GizmoPixels, 0.5);
        verticalPixels.Should().BeApproximately(Style.GizmoPixels, 0.5);
    }

    [Fact]
    public void TheGizmoIgnoresPanning()
    {
        Camera camera = Isometric();
        Matrix4x4 before = AxisOverlayPass.GizmoTransform(camera, Style, Width, Height);

        camera.Pan(0.4, -0.3);

        Matrix4x4 after = AxisOverlayPass.GizmoTransform(camera, Style, Width, Height);

        after.Should().Be(before, "the gizmo reports orientation, and panning does not change it");
    }

    [Fact]
    public void TheGizmoIgnoresZooming()
    {
        Camera camera = Isometric();
        Matrix4x4 before = AxisOverlayPass.GizmoTransform(camera, Style, Width, Height);

        camera.Zoom(0.25);

        Matrix4x4 after = AxisOverlayPass.GizmoTransform(camera, Style, Width, Height);

        after.Should().Be(before, "a gizmo that grew as the user zoomed would be reporting scale");
    }

    [Fact]
    public void TheGizmoFollowsOrbiting()
    {
        // The one thing it must track.
        Camera camera = Isometric();
        Matrix4x4 before = AxisOverlayPass.GizmoTransform(camera, Style, Width, Height);

        camera.Orbit(0.6, 0.2);

        Matrix4x4 after = AxisOverlayPass.GizmoTransform(camera, Style, Width, Height);

        after.Should().NotBe(before);
    }

    [Fact]
    public void TheGizmoAxesPointTheRightWayInAFrontView()
    {
        // Looking along +Y with Z up: world X runs right across the screen and world Z runs up.
        // If these were swapped or flipped the gizmo would be confidently wrong, which is worse
        // than absent.
        Camera camera = new() { AspectRatio = (double)Width / Height };
        camera.LookFrom(StandardView.Front);
        camera.ZoomToFit(new Bounds3d(new Vec3d(-1, -1, -1), new Vec3d(1, 1, 1)));

        Matrix4x4 transform = AxisOverlayPass.GizmoTransform(camera, Style, Width, Height);

        (double originX, double originY) = ToPixels(transform, Vec3d.Zero);
        (double xTipX, double xTipY) = ToPixels(transform, Vec3d.UnitX);
        (double zTipX, double zTipY) = ToPixels(transform, Vec3d.UnitZ);

        xTipX.Should().BeGreaterThan(originX, "world X should run right on screen");
        System.Math.Abs(xTipY - originY).Should().BeLessThan(1.0, "and should be level");

        zTipY.Should().BeLessThan(originY, "world Z should run up, which is towards y = 0");
        System.Math.Abs(zTipX - originX).Should().BeLessThan(1.0, "and should be vertical");
    }

    [Fact]
    public void AZeroSizedViewportYieldsSomethingHarmless()
    {
        // A window can be measured at zero during layout, and dividing by it would take the
        // renderer down over a resize.
        Matrix4x4 transform = AxisOverlayPass.GizmoTransform(Isometric(), Style, 0, 0);

        transform.Should().Be(Matrix4x4.Identity);
    }

    // --- The triad ----------------------------------------------------------------------------

    [Fact]
    public void TheTriadSitsAtTheWorldOriginEvenWhenTheSnapshotIsShifted()
    {
        // Positions are relative to the snapshot origin, so a triad drawn at zero in that frame
        // would appear wherever the model happens to be rather than at the world origin.
        Camera camera = Isometric();
        Bounds3d bounds = new(new Vec3d(-1, -1, -1), new Vec3d(1, 1, 1));

        Vec3d shift = new(1000, -2000, 500);

        Matrix4x4 unshifted = AxisOverlayPass.TriadTransform(camera, bounds, Vec3d.Zero, 1.0);
        Matrix4x4 shifted = AxisOverlayPass.TriadTransform(camera, bounds, shift, 1.0);

        (double plainX, double plainY) = ToPixels(unshifted, Vec3d.Zero);
        (double shiftedX, double shiftedY) = ToPixels(shifted, Vec3d.Zero);

        shiftedX.Should().BeApproximately(plainX, 0.5);
        shiftedY.Should().BeApproximately(plainY, 0.5);
    }

    [Fact]
    public void TheTriadArmsGrowWithTheLengthAsked()
    {
        Camera camera = Isometric();
        Bounds3d bounds = new(new Vec3d(-1, -1, -1), new Vec3d(1, 1, 1));

        (double originX, _) = ToPixels(
            AxisOverlayPass.TriadTransform(camera, bounds, Vec3d.Zero, 1.0), Vec3d.Zero);

        (double shortX, double shortY) = ToPixels(
            AxisOverlayPass.TriadTransform(camera, bounds, Vec3d.Zero, 0.2), Vec3d.UnitX);

        (double longX, double longY) = ToPixels(
            AxisOverlayPass.TriadTransform(camera, bounds, Vec3d.Zero, 1.0), Vec3d.UnitX);

        double shortReach = System.Math.Abs(shortX - originX);
        double longReach = System.Math.Abs(longX - originX);

        longReach.Should().BeGreaterThan(shortReach * 3, "a five-times-longer arm should reach much further");
    }

    private static Vector4 Transform(Matrix4x4 m, Vec3d v)
    {
        Vector4 p = new((float)v.X, (float)v.Y, (float)v.Z, 1);

        return new Vector4(
            (m.M11 * p.X) + (m.M12 * p.Y) + (m.M13 * p.Z) + m.M14,
            (m.M21 * p.X) + (m.M22 * p.Y) + (m.M23 * p.Z) + m.M24,
            (m.M31 * p.X) + (m.M32 * p.Y) + (m.M33 * p.Z) + m.M34,
            (m.M41 * p.X) + (m.M42 * p.Y) + (m.M43 * p.Z) + m.M44);
    }
}
