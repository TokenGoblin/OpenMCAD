using OpenMCAD.Math;

namespace OpenMCAD.MathTests;

public sealed class Mat4dTests
{
    [Fact]
    public void Identity_IsAMultiplicativeIdentity()
    {
        Mat4d m = Mat4d.FromTranslation(new Vec3d(1, 2, 3))
                * Mat4d.FromRotation(Quatd.FromAxisAngle(Vec3d.UnitY, 0.7));

        (m * Mat4d.Identity).IsNear(m, 1e-15).Should().BeTrue();
        (Mat4d.Identity * m).IsNear(m, 1e-15).Should().BeTrue();
    }

    [Fact]
    public void TranslationLivesInTheFourthColumn()
    {
        Vec3d t = new(4, 5, 6);
        Mat4d m = Mat4d.FromTranslation(t);

        m.M14.Should().Be(4);
        m.M24.Should().Be(5);
        m.M34.Should().Be(6);
        m.Translation.Should().Be(t);
    }

    [Fact]
    public void Multiplication_AppliesRightOperandFirst()
    {
        Mat4d translate = Mat4d.FromTranslation(new Vec3d(10, 0, 0));
        Mat4d rotate = Mat4d.FromRotation(Quatd.FromAxisAngle(Vec3d.UnitZ, System.Math.PI / 2));

        // translate * rotate means: rotate first, then translate.
        Vec3d result = (translate * rotate).TransformPoint(Vec3d.UnitX);
        result.IsNear(new Vec3d(10, 1, 0), 1e-12).Should().BeTrue();

        // rotate * translate means: translate first, then rotate.
        Vec3d other = (rotate * translate).TransformPoint(Vec3d.UnitX);
        other.IsNear(new Vec3d(0, 11, 0), 1e-12).Should().BeTrue();
    }

    [Fact]
    public void TransformDirection_IgnoresTranslation()
    {
        Mat4d m = Mat4d.FromTranslation(new Vec3d(100, 200, 300));
        m.TransformDirection(Vec3d.UnitX).Should().Be(Vec3d.UnitX);
        m.TransformPoint(Vec3d.UnitX).Should().Be(new Vec3d(101, 200, 300));
    }

    [Fact]
    public void Determinant_MatchesKnownValues()
    {
        Mat4d.Identity.Determinant().Should().BeApproximately(1.0, 1e-15);
        Mat4d.FromScale(new Vec3d(2, 3, 4)).Determinant().Should().BeApproximately(24.0, 1e-12);

        // A rotation preserves volume.
        Mat4d rotation = Mat4d.FromRotation(Quatd.FromAxisAngle(new Vec3d(1, 2, 3), 1.1));
        rotation.Determinant().Should().BeApproximately(1.0, 1e-12);
    }

    [Fact]
    public void Inverted_RoundTripsAGeneralAffineMatrix()
    {
        Mat4d m = Mat4d.FromTranslation(new Vec3d(1, -2, 3))
                * Mat4d.FromRotation(Quatd.FromAxisAngle(new Vec3d(0.2, 0.9, -0.3), 1.4))
                * Mat4d.FromScale(new Vec3d(2, 0.5, 3));

        (m * m.Inverted()).IsNear(Mat4d.Identity, 1e-10).Should().BeTrue();
        (m.Inverted() * m).IsNear(Mat4d.Identity, 1e-10).Should().BeTrue();
    }

    [Fact]
    public void TryInvert_ReportsFailureForASingularMatrix()
    {
        Mat4d singular = Mat4d.FromScale(new Vec3d(1, 1, 0));
        singular.TryInvert(out Mat4d result).Should().BeFalse();
        result.Should().Be(Mat4d.Identity);
    }

    [Fact]
    public void TransformNormal_IsCorrectUnderNonUniformScale()
    {
        // A plane at 45 degrees in XY, scaled 2x in X only. The naive direction transform gets
        // this wrong; the inverse transpose does not.
        Mat4d scale = Mat4d.FromScale(new Vec3d(2, 1, 1));
        Vec3d normal = new Vec3d(1, 1, 0).Normalized();

        Vec3d tangent = new Vec3d(-1, 1, 0).Normalized();
        Vec3d transformedTangent = scale.TransformDirection(tangent);
        Vec3d transformedNormal = scale.TransformNormal(normal);

        Vec3d.Dot(transformedNormal, transformedTangent).Should().BeApproximately(0.0, 1e-12);
    }

    [Fact]
    public void Indexer_ExposesElementsByOneBasedRowAndColumn()
    {
        Mat4d m = new(
            11, 12, 13, 14,
            21, 22, 23, 24,
            31, 32, 33, 34,
            41, 42, 43, 44);

        m[1, 1].Should().Be(11);
        m[2, 3].Should().Be(23);
        m[4, 4].Should().Be(44);

        Action act = () => _ = m[0, 1];
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

public sealed class TransformTests
{
    [Fact]
    public void Identity_LeavesGeometryAlone()
    {
        Vec3d v = new(1, 2, 3);
        Transform.Identity.TransformPoint(v).Should().Be(v);
        Transform.Identity.TransformDirection(v).Should().Be(v);
        Transform.Identity.IsRigid.Should().BeTrue();
    }

    [Fact]
    public void Composition_AppliesRightOperandFirst()
    {
        Transform translate = Transform.FromTranslation(new Vec3d(10, 0, 0));
        Transform rotate = Transform.FromRotation(
            Quatd.FromAxisAngle(Vec3d.UnitZ, System.Math.PI / 2));

        (translate * rotate).TransformPoint(Vec3d.UnitX)
            .IsNear(new Vec3d(10, 1, 0), 1e-12).Should().BeTrue();

        (rotate * translate).TransformPoint(Vec3d.UnitX)
            .IsNear(new Vec3d(0, 11, 0), 1e-12).Should().BeTrue();
    }

    [Fact]
    public void Composition_IsAssociative()
    {
        Transform a = Transform.FromTranslation(new Vec3d(1, 2, 3));
        Transform b = Transform.FromRotation(Quatd.FromAxisAngle(new Vec3d(1, 1, 0), 0.9));
        Transform c = Transform.FromScale(2.5);

        Vec3d v = new(0.3, -4, 7);
        Vec3d left = ((a * b) * c).TransformPoint(v);
        Vec3d right = (a * (b * c)).TransformPoint(v);

        left.IsNear(right, 1e-10).Should().BeTrue();
    }

    [Fact]
    public void Inverted_RoundTripsPointsExactly()
    {
        Transform t = Transform.FromTranslation(new Vec3d(3, -1, 0.5))
                    * Transform.FromRotation(Quatd.FromAxisAngle(new Vec3d(0.1, 0.9, 0.4), 2.2))
                    * Transform.FromScale(3.0);

        Vec3d v = new(1.25, -8, 0.03);
        t.Inverted().TransformPoint(t.TransformPoint(v)).IsNear(v, 1e-10).Should().BeTrue();
        (t * t.Inverted()).IsNear(Transform.Identity, 10.0, 1e-10).Should().BeTrue();
    }

    [Fact]
    public void Inverted_ThrowsOnADegenerateTransform()
    {
        Transform degenerate = new(Quatd.Identity, Vec3d.Zero, 0.0);
        degenerate.IsValid.Should().BeFalse();

        Action act = () => degenerate.Inverted();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void FromScale_RejectsNonPositiveScale()
    {
        Action zero = () => Transform.FromScale(0.0);
        Action negative = () => Transform.FromScale(-1.0);

        zero.Should().Throw<ArgumentOutOfRangeException>();
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void FromAxisRotation_LeavesTheAxisLineFixed()
    {
        Vec3d axisPoint = new(5, 5, 0);
        Vec3d axisDirection = Vec3d.UnitZ;
        Transform t = Transform.FromAxisRotation(axisPoint, axisDirection, 1.234);

        t.TransformPoint(axisPoint).IsNear(axisPoint, 1e-12).Should().BeTrue();
        t.TransformPoint(axisPoint + axisDirection)
            .IsNear(axisPoint + axisDirection, 1e-12).Should().BeTrue();
    }

    [Fact]
    public void TransformNormal_KeepsUnitNormalsUnit_UnderScale()
    {
        Transform t = Transform.FromScale(1000.0)
                    * Transform.FromRotation(Quatd.FromAxisAngle(Vec3d.UnitX, 0.6));

        Vec3d normal = new Vec3d(1, 2, 3).Normalized();
        t.TransformNormal(normal).Length.Should().BeApproximately(1.0, 1e-12);
    }

    [Fact]
    public void ToMat4d_AgreesWithDirectApplication()
    {
        Transform t = Transform.FromTranslation(new Vec3d(-2, 7, 1))
                    * Transform.FromRotation(Quatd.FromAxisAngle(new Vec3d(3, -1, 2), 0.77))
                    * Transform.FromScale(1.75);

        Mat4d m = t.ToMat4d();
        Vec3d v = new(0.4, 5, -6);

        m.TransformPoint(v).IsNear(t.TransformPoint(v), 1e-10).Should().BeTrue();
        m.TransformDirection(v).IsNear(t.TransformDirection(v), 1e-10).Should().BeTrue();
    }

    [Fact]
    public void FromFrame_MapsWorldAxesOntoTheFrame()
    {
        Vec3d origin = new(1, 2, 3);
        Vec3d x = new Vec3d(0, 1, 0).Normalized();
        Vec3d y = new Vec3d(0, 0, 1).Normalized();
        Vec3d z = Vec3d.Cross(x, y);

        Transform t = Transform.FromFrame(origin, x, y, z);

        t.TransformPoint(Vec3d.Zero).IsNear(origin, 1e-12).Should().BeTrue();
        t.TransformDirection(Vec3d.UnitX).IsNear(x, 1e-12).Should().BeTrue();
        t.TransformDirection(Vec3d.UnitY).IsNear(y, 1e-12).Should().BeTrue();
        t.TransformDirection(Vec3d.UnitZ).IsNear(z, 1e-12).Should().BeTrue();
    }

    [Fact]
    public void TransformPlane_MovesThePlaneCoherently()
    {
        Plane plane = Plane.FromPointNormal(new Vec3d(0, 0, 5), Vec3d.UnitZ);
        Transform t = Transform.FromAxisRotation(Vec3d.Zero, Vec3d.UnitX, System.Math.PI / 2);

        Plane moved = t.TransformPlane(plane);

        moved.Normal.IsNear(-Vec3d.UnitY, 1e-12).Should().BeTrue();
        moved.Contains(t.TransformPoint(new Vec3d(0, 0, 5)), 1e-12).Should().BeTrue();
    }
}
