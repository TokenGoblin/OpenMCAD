using FluentAssertions;

using OpenMCAD.Math;

using Xunit;

namespace OpenMCAD.Math.Tests;

/// <summary>
/// Frustum extraction and box classification, which the face pass culls with.
/// </summary>
/// <remarks>
/// A culler that is wrong in the safe direction draws too much and nobody notices until a
/// profiler does; wrong in the unsafe direction and geometry vanishes from the viewport for
/// reasons no user could describe. Both are tested here against matrices whose answers are known
/// by construction rather than by rendering.
/// </remarks>
public sealed class FrustumTests
{
    /// <summary>A camera at the origin looking down +Y, with Z up.</summary>
    private static Mat4d View => Mat4d.LookAt(Vec3d.Zero, Vec3d.UnitY, Vec3d.UnitZ);

    private static Mat4d Perspective(double near = 1, double far = 100)
        => Mat4d.PerspectiveFieldOfView(0.8, 1.0, near, far);

    private static Frustum Standard() => Frustum.FromViewProjection(Perspective() * View);

    // --- Extraction ---------------------------------------------------------------------------

    [Fact]
    public void EveryExtractedPlaneIsNormalised()
    {
        // Unnormalised planes still give the right sign, so every inside/outside test passes and
        // the error only appears when something asks for an actual distance. Checking here is far
        // cheaper than discovering it from a near-plane bias that is wrong by a scale factor.
        Frustum frustum = Standard();

        foreach (Plane plane in new[]
        {
            frustum.Left, frustum.Right, frustum.Bottom,
            frustum.Top, frustum.Near, frustum.Far,
        })
        {
            plane.Normal.Length.Should().BeApproximately(1.0, 1e-9);
        }
    }

    [Fact]
    public void ThePlanesFaceInwards()
    {
        // A point well inside must be on the positive side of all six. If any plane were flipped,
        // everything would be culled and the viewport would simply be empty.
        Standard().Contains(new Vec3d(0, 10, 0)).Should().BeTrue();
    }

    // --- Points -------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 10, 0, true, "straight ahead")]
    [InlineData(0, -10, 0, false, "directly behind the camera")]
    [InlineData(0, 0.5, 0, false, "closer than the near plane")]
    [InlineData(0, 200, 0, false, "beyond the far plane")]
    [InlineData(100, 10, 0, false, "far off to the right")]
    [InlineData(0, 10, 100, false, "far above")]
    public void PointsAreClassifiedAgainstEveryPlane(
        double x, double y, double z, bool expected, string because)
    {
        Standard().Contains(new Vec3d(x, y, z)).Should().Be(expected, because);
    }

    [Fact]
    public void TheNearPlaneIsNotConfusedWithTheFarOne()
    {
        // The one asymmetry in the extraction: with D3D's depth convention the near plane is row
        // four alone, not row four plus row three. Getting it wrong costs exactly the geometry
        // nearest the camera, which is the geometry the user is looking at.
        Frustum frustum = Frustum.FromViewProjection(Perspective(near: 5, far: 50) * View);

        frustum.Contains(new Vec3d(0, 4, 0)).Should().BeFalse("it is inside the near plane");
        frustum.Contains(new Vec3d(0, 6, 0)).Should().BeTrue();
        frustum.Contains(new Vec3d(0, 49, 0)).Should().BeTrue();
        frustum.Contains(new Vec3d(0, 51, 0)).Should().BeFalse("it is beyond the far plane");
    }

    // --- Boxes --------------------------------------------------------------------------------

    [Fact]
    public void ABoxWhollyInsideIsInside()
    {
        Bounds3d box = new(new Vec3d(-1, 9, -1), new Vec3d(1, 11, 1));

        Standard().Classify(box).Should().Be(FrustumPlacement.Inside);
    }

    [Fact]
    public void ABoxWhollyBehindIsOutside()
    {
        Bounds3d box = new(new Vec3d(-1, -20, -1), new Vec3d(1, -10, 1));

        Standard().Classify(box).Should().Be(FrustumPlacement.Outside);
        Standard().Intersects(box).Should().BeFalse();
    }

    [Fact]
    public void ABoxStraddlingTheFarPlaneIntersects()
    {
        Bounds3d box = new(new Vec3d(-1, 90, -1), new Vec3d(1, 110, 1));

        Standard().Classify(box).Should().Be(FrustumPlacement.Intersecting);
        Standard().Intersects(box).Should().BeTrue("part of it is visible and must still be drawn");
    }

    [Fact]
    public void AnEmptyBoxIsOutsideRatherThanEverywhere()
    {
        // Bounds3d.Empty has an inverted min and max. Treated naively it satisfies every plane at
        // once and is drawn always, which is the opposite of what an empty body should cost.
        Standard().Classify(Bounds3d.Empty).Should().Be(FrustumPlacement.Outside);
    }

    [Fact]
    public void AHugeBoxContainingTheCameraIntersects()
    {
        // The camera inside the geometry: a section view, or simply a zoomed-in one. Culling this
        // would empty the viewport at exactly the moment the user is closest to their model.
        Bounds3d box = new(new Vec3d(-1000, -1000, -1000), new Vec3d(1000, 1000, 1000));

        Standard().Intersects(box).Should().BeTrue();
    }

    // --- Orthographic -------------------------------------------------------------------------

    [Fact]
    public void AnOrthographicFrustumCullsToTheSameBoxItProjects()
    {
        // A CAD viewport switches projection constantly, and the extraction has to hold for both.
        // With an orthographic projection the side planes are parallel, so a point outside the
        // half-width is outside at every depth -- unlike perspective, where it depends on range.
        Mat4d projection = Mat4d.Orthographic(20, 20, 1, 100);
        Frustum frustum = Frustum.FromViewProjection(projection * View);

        frustum.Contains(new Vec3d(0, 50, 0)).Should().BeTrue();
        frustum.Contains(new Vec3d(9, 50, 0)).Should().BeTrue("it is within the 20-wide extent");
        frustum.Contains(new Vec3d(11, 50, 0)).Should().BeFalse();
        frustum.Contains(new Vec3d(9, 5, 0)).Should().BeTrue("width does not narrow with depth");
    }

    // --- Degenerate ---------------------------------------------------------------------------

    [Fact]
    public void ADegenerateMatrixYieldsAFrustumRatherThanAnException()
    {
        // A mis-set camera should not take the frame loop down with it. The frustum is nonsense
        // either way, and nonsense that draws too much is far easier to diagnose than a crash
        // from inside the renderer.
        Action act = () => Frustum.FromViewProjection(default);

        act.Should().NotThrow();
    }

    [Fact]
    public void TwoFrustumsFromTheSameMatrixAreEqual()
    {
        Frustum first = Standard();
        Frustum second = Standard();

        first.Should().Be(second);
        (first == second).Should().BeTrue();
        first.GetHashCode().Should().Be(second.GetHashCode());
    }
}
