using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Modeling;

using Xunit;

namespace OpenMCAD.Modeling.Tests;

/// <summary>
/// The 3-D frame a sketch is drawn on (P4-T10): building one, and mapping through it.
/// </summary>
public sealed class SketchPlaneTests
{
    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(1, 1, 1)]
    [InlineData(-3, 7, 0.5)]
    [InlineData(0, 0, -1)]
    public void FromNormal_ProducesARightHandedOrthonormalFrame(double x, double y, double z)
    {
        // The invariant every consumer of a SketchPlane leans on without re-checking it: XAxis,
        // YAxis and Normal are mutually perpendicular, unit length, and Normal is Cross(X, Y) --
        // not its negation, which would flip which side of the sketch is "up".
        SketchPlane plane = SketchPlane.FromNormal(new Vec3d(5, -2, 3), new Vec3d(x, y, z));

        plane.XAxis.Length.Should().BeApproximately(1.0, 1e-12);
        plane.YAxis.Length.Should().BeApproximately(1.0, 1e-12);
        plane.Normal.Length.Should().BeApproximately(1.0, 1e-12);

        Vec3d.Dot(plane.XAxis, plane.YAxis).Should().BeApproximately(0.0, 1e-12);
        Vec3d.Dot(plane.XAxis, plane.Normal).Should().BeApproximately(0.0, 1e-12);
        Vec3d.Dot(plane.YAxis, plane.Normal).Should().BeApproximately(0.0, 1e-12);

        Vec3d.Cross(plane.XAxis, plane.YAxis).IsNear(plane.Normal, 1e-12).Should().BeTrue(
            "Normal is documented as Cross(XAxis, YAxis), and a caller testing which side "
            + "something is on relies on that direction, not its reverse");
    }

    [Fact]
    public void FromNormal_OnTheWorldZAxisMatchesPlaneCreateFrame()
    {
        // SketchPlane invents no basis-selection policy of its own for a bare normal -- it is the
        // one thing OpenMCAD.Math.Plane.CreateFrame already owns and is already tested against
        // (PlaneAndBoundsTests), including the determinism ADR-0011 requires. Re-deriving the
        // exact axes here would be a second, competing description of that policy; this instead
        // pins that FromNormal is nothing more than CreateFrame plus an origin.
        SketchPlane plane = SketchPlane.FromNormal(Vec3d.Zero, Vec3d.UnitZ);

        Plane.XY.CreateFrame(out Vec3d expectedX, out Vec3d expectedY);

        plane.XAxis.Should().Be(expectedX);
        plane.YAxis.Should().Be(expectedY);
        plane.Normal.Should().Be(Vec3d.UnitZ);
    }

    [Fact]
    public void FromNormal_IsDeterministic()
    {
        // ADR-0011: two rebuilds of the same document must place the same sketch geometry at the
        // same 3-D points. The whole reason a canonical basis exists rather than "any perpendicular
        // vector" is this.
        Vec3d normal = new(0.4, -0.6, 0.69282); // not axis-aligned, not normalised
        SketchPlane first = SketchPlane.FromNormal(Vec3d.One, normal);

        for (int i = 0; i < 20; i++)
        {
            SketchPlane.FromNormal(Vec3d.One, normal).Should().Be(first);
        }
    }

    [Fact]
    public void FromNormal_RejectsADegenerateNormal()
    {
        Action act = () => SketchPlane.FromNormal(Vec3d.Zero, Vec3d.Zero);

        act.Should().Throw<InvalidOperationException>(
            "a plane with no normal has no orientation to sketch on, and silently returning some "
            + "arbitrary orientation would place a user's geometry somewhere they never chose");
    }

    [Fact]
    public void FromFrame_KeepsTheGivenXAxisWhenItIsAlreadyPerpendicular()
    {
        SketchPlane plane = SketchPlane.FromFrame(Vec3d.Zero, Vec3d.UnitX, Vec3d.UnitZ);

        plane.XAxis.Should().Be(Vec3d.UnitX);
        plane.YAxis.Should().Be(Vec3d.UnitY);
        plane.Normal.Should().Be(Vec3d.UnitZ);
    }

    [Fact]
    public void FromFrame_ProjectsAnXAxisThatIsNotPerpendicularOntoThePlane()
    {
        // A custom coordinate system's stored X and Z axes are not checked for mutual
        // orthogonality (ReferenceGeometry.CoordinateSystem derives only its Y axis, the same way),
        // so a sketch plane built from one has to cope with an X axis that leans toward the normal.
        SketchPlane plane = SketchPlane.FromFrame(Vec3d.Zero, new Vec3d(1, 0, 1), Vec3d.UnitZ);

        plane.XAxis.Length.Should().BeApproximately(1.0, 1e-12);
        Vec3d.Dot(plane.XAxis, Vec3d.UnitZ).Should().BeApproximately(0.0, 1e-12);

        // Only the component along the normal was removed -- the projection still leans the same
        // way in the plane, toward +X, rather than picking an unrelated direction.
        plane.XAxis.X.Should().BeGreaterThan(0);
        plane.XAxis.Y.Should().BeApproximately(0.0, 1e-12);
    }

    [Fact]
    public void FromFrame_RejectsAnXAxisParallelToTheNormal()
    {
        Action act = () => SketchPlane.FromFrame(Vec3d.Zero, Vec3d.UnitZ, Vec3d.UnitZ);

        act.Should().Throw<InvalidOperationException>(
            "an X axis parallel to the normal has nothing left once its component along the "
            + "normal is removed, so there is no direction to build a frame from");
    }

    [Fact]
    public void FromFrame_RejectsADegenerateNormal()
    {
        Action act = () => SketchPlane.FromFrame(Vec3d.Zero, Vec3d.UnitX, Vec3d.Zero);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void FromPlane_AgreesWithFromNormal()
    {
        // FromPlane is the path a resolved kernel face takes (via OpenMCAD.Math.Plane); FromNormal
        // is the path a datum takes. Both end up at CreateFrame, and this pins that they cannot
        // quietly diverge.
        Vec3d origin = new(1, 2, 3);
        Vec3d normal = new(0.6, 0.8, 0);

        SketchPlane.FromPlane(Plane.FromPointNormal(origin, normal))
            .Should().Be(SketchPlane.FromNormal(origin, normal));
    }

    [Fact]
    public void WorldXY_IsAtTheOriginWithNormalAlongZ()
    {
        // The X and Y axes are whatever Plane.CreateFrame's canonical basis picks for +Z -- see
        // FromNormal_OnTheWorldZAxisMatchesPlaneCreateFrame for why this does not also pin that
        // choice down a second time.
        SketchPlane.WorldXY.Origin.Should().Be(Vec3d.Zero);
        SketchPlane.WorldXY.Normal.Should().Be(Vec3d.UnitZ);
    }

    [Fact]
    public void ToWorld_MapsTheLocalOriginToThePlaneOrigin()
    {
        SketchPlane plane = SketchPlane.FromNormal(new Vec3d(1, 2, 3), Vec3d.UnitY);

        plane.ToWorld(Vec2d.Zero).Should().Be(plane.Origin);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, -2)]
    [InlineData(-1.5, 0.25)]
    public void ToWorldAndToLocal_RoundTripForAPointOnThePlane(double u, double v)
    {
        SketchPlane plane = SketchPlane.FromNormal(new Vec3d(4, -1, 2), new Vec3d(1, 1, 1));
        Vec2d local = new(u, v);

        Vec3d world = plane.ToWorld(local);
        Vec2d back = plane.ToLocal(world);

        back.X.Should().BeApproximately(local.X, 1e-9);
        back.Y.Should().BeApproximately(local.Y, 1e-9);
    }

    [Fact]
    public void ToLocal_ProjectsAPointThatIsOffThePlane()
    {
        // The plane at world Z = 0, normal +Z. A point above it drops straight down onto the
        // plane before its local coordinates are read off -- checked here by comparing against a
        // point already known to be on the plane, rather than against a hand-picked (x, y), which
        // would silently assume WorldXY's axes are world X and Y (they are not; see
        // FromNormal_OnTheWorldZAxisMatchesPlaneCreateFrame).
        SketchPlane plane = SketchPlane.WorldXY;

        Vec2d local = plane.ToLocal(new Vec3d(3, 4, 100));
        Vec2d onThePlane = plane.ToLocal(new Vec3d(3, 4, 0));

        local.Should().Be(onThePlane, "height above the plane carries no local coordinate");
    }

    [Fact]
    public void IsNear_IsFalseWhenOnlyTheNormalDiffers()
    {
        SketchPlane a = SketchPlane.WorldXY;
        SketchPlane b = SketchPlane.FromNormal(Vec3d.Zero, new Vec3d(0, 0, -1));

        a.IsNear(b).Should().BeFalse("a flipped normal is a different plane, not a rounding error");
    }

    [Fact]
    public void IsNear_IsTrueWithinTolerance()
    {
        // The offset has to be along the normal to actually move the reconstructed plane: an
        // offset within the plane changes nothing about which plane it is.
        SketchPlane a = SketchPlane.FromNormal(Vec3d.Zero, Vec3d.UnitZ);
        SketchPlane b = SketchPlane.FromNormal(new Vec3d(0, 0, 1e-10), Vec3d.UnitZ);

        a.Origin.Should().NotBe(b.Origin, "otherwise this is not testing the tolerance at all");
        a.IsNear(b).Should().BeTrue();
    }
}
