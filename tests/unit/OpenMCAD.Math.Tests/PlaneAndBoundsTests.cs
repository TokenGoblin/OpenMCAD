using OpenMCAD.Math;

namespace OpenMCAD.MathTests;

public sealed class PlaneTests
{
    [Fact]
    public void WorldPlanes_HaveTheExpectedNormals()
    {
        Plane.XY.Normal.Should().Be(Vec3d.UnitZ);
        Plane.YZ.Normal.Should().Be(Vec3d.UnitX);
        Plane.ZX.Normal.Should().Be(Vec3d.UnitY);

        Plane.XY.DistanceFromOrigin.Should().Be(0.0);
    }

    [Fact]
    public void SignedDistanceTo_IsPositiveOnTheNormalSide()
    {
        Plane p = Plane.FromPointNormal(new Vec3d(0, 0, 2), Vec3d.UnitZ);

        p.SignedDistanceTo(new Vec3d(0, 0, 5)).Should().BeApproximately(3.0, 1e-12);
        p.SignedDistanceTo(new Vec3d(0, 0, -1)).Should().BeApproximately(-3.0, 1e-12);
        p.SignedDistanceTo(new Vec3d(100, -100, 2)).Should().BeApproximately(0.0, 1e-12);
    }

    [Fact]
    public void FromPointNormal_NormalisesTheNormal()
    {
        Plane p = Plane.FromPointNormal(new Vec3d(1, 1, 1), new Vec3d(0, 0, 17));
        p.Normal.Length.Should().BeApproximately(1.0, 1e-15);
    }

    [Fact]
    public void FromThreePoints_WindsCounterClockwiseAboutTheNormal()
    {
        Plane p = Plane.FromThreePoints(Vec3d.Zero, Vec3d.UnitX, Vec3d.UnitY);
        p.Normal.IsNear(Vec3d.UnitZ, 1e-12).Should().BeTrue();
    }

    [Fact]
    public void FromThreePoints_RejectsCollinearInput()
    {
        Action act = () => Plane.FromThreePoints(
            Vec3d.Zero, new Vec3d(1, 0, 0), new Vec3d(2, 0, 0));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Project_LandsOnThePlaneAndIsIdempotent()
    {
        Plane p = Plane.FromPointNormal(new Vec3d(1, 2, 3), new Vec3d(1, 1, 1));
        Vec3d v = new(9, -4, 0.5);

        Vec3d projected = p.Project(v);
        p.Contains(projected, 1e-12).Should().BeTrue();
        p.Project(projected).IsNear(projected, 1e-12).Should().BeTrue();
    }

    [Fact]
    public void Flipped_ReversesOrientationButNotGeometry()
    {
        Plane p = Plane.FromPointNormal(new Vec3d(0, 0, 4), Vec3d.UnitZ);
        Plane f = p.Flipped();

        f.Normal.Should().Be(-Vec3d.UnitZ);
        f.Contains(new Vec3d(7, 7, 4), 1e-12).Should().BeTrue();
        f.SignedDistanceTo(new Vec3d(0, 0, 6)).Should().BeApproximately(-2.0, 1e-12);
    }

    [Fact]
    public void TryIntersectLine_FindsTheCrossingPoint()
    {
        Plane p = Plane.XY;
        p.TryIntersectLine(new Vec3d(1, 2, 5), -Vec3d.UnitZ, out Vec3d hit).Should().BeTrue();
        hit.IsNear(new Vec3d(1, 2, 0), 1e-12).Should().BeTrue();
    }

    [Fact]
    public void TryIntersectLine_ReportsFailureForParallelLines()
    {
        Plane p = Plane.XY;

        p.TryIntersectLine(new Vec3d(0, 0, 5), Vec3d.UnitX, out Vec3d off).Should().BeFalse();
        off.Should().Be(Vec3d.Zero);

        // A line lying in the plane is reported the same way: no single intersection point.
        p.TryIntersectLine(Vec3d.Zero, Vec3d.UnitX, out _).Should().BeFalse();
    }

    [Fact]
    public void CreateFrame_ProducesARightHandedOrthonormalBasis()
    {
        Plane p = Plane.FromPointNormal(new Vec3d(1, 2, 3), new Vec3d(2, -5, 1));
        p.CreateFrame(out Vec3d x, out Vec3d y);

        x.Length.Should().BeApproximately(1.0, 1e-12);
        y.Length.Should().BeApproximately(1.0, 1e-12);
        Vec3d.Dot(x, y).Should().BeApproximately(0.0, 1e-12);
        Vec3d.Cross(x, y).IsNear(p.Normal, 1e-12).Should().BeTrue();
    }

    [Fact]
    public void CreateFrame_IsDeterministic()
    {
        Plane p = Plane.FromPointNormal(new Vec3d(1, 2, 3), new Vec3d(2, -5, 1));
        p.CreateFrame(out Vec3d firstX, out Vec3d firstY);

        for (int i = 0; i < 50; i++)
        {
            p.CreateFrame(out Vec3d x, out Vec3d y);
            x.Should().Be(firstX);
            y.Should().Be(firstY);
        }
    }
}

public sealed class Bounds3dTests
{
    [Fact]
    public void Default_IsEmpty()
    {
        default(Bounds3d).IsEmpty.Should().BeTrue();
        Bounds3d.Empty.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void UnionWithAPoint_GrowsAnEmptyBoxToADegenerateBox()
    {
        Vec3d p = new(1, 2, 3);
        Bounds3d b = Bounds3d.Union(Bounds3d.Empty, p);

        b.IsEmpty.Should().BeFalse();
        b.Min.Should().Be(p);
        b.Max.Should().Be(p);
        b.Volume.Should().Be(0.0);
    }

    [Fact]
    public void FromPoints_BoundsEveryInput()
    {
        Vec3d[] points =
        [
            new(1, 2, 3),
            new(-4, 0, 8),
            new(0, 9, -2),
        ];

        Bounds3d b = Bounds3d.FromPoints(points);

        b.Min.Should().Be(new Vec3d(-4, 0, -2));
        b.Max.Should().Be(new Vec3d(1, 9, 8));
        foreach (Vec3d p in points)
        {
            b.Contains(p).Should().BeTrue();
        }
    }

    [Fact]
    public void FromPoints_OfNothing_IsEmpty()
    {
        Bounds3d.FromPoints([]).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void FromPoints_RejectsNull()
    {
        Action act = () => Bounds3d.FromPoints(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void SizeCenterAndVolume_AreCorrect()
    {
        Bounds3d b = new(new Vec3d(0, 0, 0), new Vec3d(2, 4, 8));

        b.Size.Should().Be(new Vec3d(2, 4, 8));
        b.Center.Should().Be(new Vec3d(1, 2, 4));
        b.Volume.Should().Be(64.0);
        b.DiagonalLength.Should().BeApproximately(System.Math.Sqrt(84.0), 1e-12);
    }

    [Fact]
    public void EmptyBox_HasNoCentreAndNoCorners()
    {
        Action centre = () => _ = Bounds3d.Empty.Center;
        Action corners = () => Bounds3d.Empty.Corners();

        centre.Should().Throw<InvalidOperationException>();
        corners.Should().Throw<InvalidOperationException>();

        Bounds3d.Empty.Size.Should().Be(Vec3d.Zero);
        Bounds3d.Empty.Volume.Should().Be(0.0);
    }

    [Fact]
    public void Intersection_OfDisjointBoxes_IsEmpty()
    {
        Bounds3d a = new(Vec3d.Zero, Vec3d.One);
        Bounds3d b = new(new Vec3d(5, 5, 5), new Vec3d(6, 6, 6));

        Bounds3d.Intersection(a, b).IsEmpty.Should().BeTrue();
        a.Intersects(b).Should().BeFalse();
    }

    [Fact]
    public void Intersects_RespectsTolerance()
    {
        Bounds3d a = new(Vec3d.Zero, Vec3d.One);
        Bounds3d b = new(new Vec3d(1.001, 0, 0), new Vec3d(2, 1, 1));

        a.Intersects(b).Should().BeFalse();
        a.Intersects(b, 0.01).Should().BeTrue();
    }

    [Fact]
    public void Contains_HandlesBoxContainment()
    {
        Bounds3d outer = new(Vec3d.Zero, new Vec3d(10, 10, 10));
        Bounds3d inner = new(new Vec3d(1, 1, 1), new Vec3d(2, 2, 2));

        outer.Contains(inner).Should().BeTrue();
        inner.Contains(outer).Should().BeFalse();
        outer.Contains(Bounds3d.Empty).Should().BeFalse();
    }

    [Fact]
    public void Expanded_GrowsAndShrinks()
    {
        Bounds3d b = new(Vec3d.Zero, new Vec3d(10, 10, 10));

        b.Expanded(1).Min.Should().Be(new Vec3d(-1, -1, -1));
        b.Expanded(1).Max.Should().Be(new Vec3d(11, 11, 11));
        b.Expanded(-1).Size.Should().Be(new Vec3d(8, 8, 8));

        Bounds3d.Empty.Expanded(5).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Corners_UseTheDocumentedBitOrder()
    {
        Bounds3d b = new(Vec3d.Zero, new Vec3d(1, 2, 4));
        Vec3d[] corners = b.Corners();

        corners.Should().HaveCount(8);
        corners[0].Should().Be(new Vec3d(0, 0, 0));
        corners[1].Should().Be(new Vec3d(1, 0, 0));
        corners[2].Should().Be(new Vec3d(0, 2, 0));
        corners[4].Should().Be(new Vec3d(0, 0, 4));
        corners[7].Should().Be(new Vec3d(1, 2, 4));
    }

    [Fact]
    public void Transformed_BoundsTheTransformedCorners()
    {
        Bounds3d b = new(Vec3d.Zero, new Vec3d(1, 1, 1));
        Transform t = Transform.FromAxisRotation(Vec3d.Zero, Vec3d.UnitZ, System.Math.PI / 4);

        Bounds3d moved = b.Transformed(t);

        foreach (Vec3d corner in b.Corners())
        {
            moved.Contains(t.TransformPoint(corner), 1e-12).Should().BeTrue();
        }

        // A rotated unit cube needs a wider axis-aligned box than the original.
        moved.Size.X.Should().BeGreaterThan(1.0);
    }

    [Fact]
    public void Transformed_OfEmpty_IsEmpty()
    {
        Bounds3d.Empty.Transformed(Transform.FromTranslation(Vec3d.One)).IsEmpty.Should().BeTrue();
    }
}

public sealed class ToleranceTests
{
    [Fact]
    public void AreRelativelyEqual_ScalesWithMagnitude()
    {
        // An absolute tolerance is useless for a quantity like a moment of inertia.
        Tolerance.AreRelativelyEqual(1e9, 1e9 + 1.0, 1e-9).Should().BeTrue();
        Tolerance.AreRelativelyEqual(1e9, 1.1e9, 1e-9).Should().BeFalse();

        // Near zero it falls back to an absolute comparison.
        Tolerance.AreRelativelyEqual(0.0, 1e-12, 1e-9).Should().BeTrue();
        Tolerance.AreRelativelyEqual(0.0, 1e-6, 1e-9).Should().BeFalse();
    }

    [Fact]
    public void Clamp_BoundsBothWays()
    {
        Tolerance.Clamp(-5, 0, 1).Should().Be(0);
        Tolerance.Clamp(5, 0, 1).Should().Be(1);
        Tolerance.Clamp(0.5, 0, 1).Should().Be(0.5);
    }

    [Fact]
    public void Constants_AreOrderedSensibly()
    {
        Tolerance.LinearResolution.Should().BeLessThan(Tolerance.Linear);
        Tolerance.AngularResolution.Should().BeLessThan(Tolerance.Angular);
        Tolerance.Linear.Should().BeLessThan(Tolerance.DisplayChordal);
    }
}
