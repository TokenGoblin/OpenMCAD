using OpenMCAD.Math;

namespace OpenMCAD.MathTests;

public sealed class QuatdTests
{
    [Fact]
    public void Identity_RotatesNothing()
    {
        Vec3d v = new(1, 2, 3);
        Quatd.Identity.Rotate(v).Should().Be(v);
    }

    [Fact]
    public void FromAxisAngle_RotatesAboutZByRightHandRule()
    {
        Quatd q = Quatd.FromAxisAngle(Vec3d.UnitZ, System.Math.PI / 2);
        q.Rotate(Vec3d.UnitX).IsNear(Vec3d.UnitY, 1e-12).Should().BeTrue();
        q.Rotate(Vec3d.UnitY).IsNear(-Vec3d.UnitX, 1e-12).Should().BeTrue();
        q.Rotate(Vec3d.UnitZ).IsNear(Vec3d.UnitZ, 1e-12).Should().BeTrue();
    }

    [Fact]
    public void Multiplication_AppliesRightOperandFirst()
    {
        // This is the ordering convention documented on the operator. If someone flips it, this
        // test fails rather than a hundred downstream transforms silently going wrong.
        Quatd aboutZ = Quatd.FromAxisAngle(Vec3d.UnitZ, System.Math.PI / 2);
        Quatd aboutX = Quatd.FromAxisAngle(Vec3d.UnitX, System.Math.PI / 2);

        Vec3d v = new(1, 2, 3);
        Vec3d viaComposition = (aboutZ * aboutX).Rotate(v);
        Vec3d viaSequence = aboutZ.Rotate(aboutX.Rotate(v));

        viaComposition.IsNear(viaSequence, 1e-12).Should().BeTrue();
    }

    [Fact]
    public void Rotation_PreservesLength()
    {
        Quatd q = Quatd.FromAxisAngle(new Vec3d(1, 2, 3), 1.234);
        Vec3d v = new(4, -5, 6);

        q.Rotate(v).Length.Should().BeApproximately(v.Length, 1e-12);
    }

    [Fact]
    public void Conjugate_UndoesRotation()
    {
        Quatd q = Quatd.FromAxisAngle(new Vec3d(0.3, -0.7, 0.2), 2.1);
        Vec3d v = new(1, 2, 3);

        q.Conjugate().Rotate(q.Rotate(v)).IsNear(v, 1e-12).Should().BeTrue();
    }

    [Fact]
    public void Inverse_IsTheAlgebraicInverse_EvenWhenNotUnitLength()
    {
        // Note what this does NOT claim. Rotate() assumes a unit quaternion, so composing a
        // non-unit Inverse() with Rotate() is meaningless. Inverse() is the algebraic inverse of
        // the quaternion; to undo a rotation, normalise first (or use Conjugate on a unit value).
        Quatd q = new(0.6, 0.8, 1.2, 2.0);

        Quatd product = q * q.Inverse();

        product.X.Should().BeApproximately(0.0, 1e-12);
        product.Y.Should().BeApproximately(0.0, 1e-12);
        product.Z.Should().BeApproximately(0.0, 1e-12);
        product.W.Should().BeApproximately(1.0, 1e-12);
    }

    [Fact]
    public void Inverse_UndoesRotation_OnceNormalised()
    {
        Quatd q = new Quatd(0.6, 0.8, 1.2, 2.0).Normalized();
        Vec3d v = new(1, 2, 3);

        q.Inverse().Rotate(q.Rotate(v)).IsNear(v, 1e-12).Should().BeTrue();
    }

    [Fact]
    public void FromTo_ProducesTheShortestRotation()
    {
        Vec3d from = new(1, 2, 3);
        Vec3d to = new(-4, 0.5, 2);

        Quatd q = Quatd.FromTo(from, to);
        Vec3d rotated = q.Rotate(from.Normalized());

        rotated.IsNear(to.Normalized(), 1e-12).Should().BeTrue();
    }

    [Fact]
    public void FromTo_HandlesTheAntiparallelCase()
    {
        // The degenerate case that a naive cross-product implementation gets wrong: the cross
        // product vanishes and there is no unique axis.
        Vec3d from = Vec3d.UnitX;
        Vec3d to = -Vec3d.UnitX;

        Quatd q = Quatd.FromTo(from, to);
        q.Rotate(from).IsNear(to, 1e-12).Should().BeTrue();
    }

    [Fact]
    public void FromTo_HandlesTheIdenticalCase()
    {
        Quatd q = Quatd.FromTo(Vec3d.UnitY, Vec3d.UnitY * 3);
        q.IsSameRotationAs(Quatd.Identity).Should().BeTrue();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(1.0)]
    [InlineData(3.0)]
    [InlineData(3.14159265)]
    public void ToAxisAngle_RoundTripsThroughFromAxisAngle(double angle)
    {
        Vec3d axis = new Vec3d(0.3, 0.5, -0.81).Normalized();
        Quatd q = Quatd.FromAxisAngle(axis, angle);

        q.ToAxisAngle(out Vec3d recoveredAxis, out double recoveredAngle);

        recoveredAngle.Should().BeApproximately(angle, 1e-9);
        if (angle > 1e-6)
        {
            recoveredAxis.IsNear(axis, 1e-9).Should().BeTrue();
        }
    }

    [Fact]
    public void FromBasis_RoundTripsThroughRotation_IncludingNearHalfTurns()
    {
        // Shepperd's method exists to survive this case; the naive trace-only formulation divides
        // by something near zero here.
        foreach (double angle in new[] { 0.0, 0.5, System.Math.PI - 1e-7, System.Math.PI })
        {
            Quatd expected = Quatd.FromAxisAngle(new Vec3d(1, 1, 1), angle);

            Vec3d x = expected.Rotate(Vec3d.UnitX);
            Vec3d y = expected.Rotate(Vec3d.UnitY);
            Vec3d z = expected.Rotate(Vec3d.UnitZ);

            Quatd actual = Quatd.FromBasis(x, y, z);

            actual.IsSameRotationAs(expected, 1e-7).Should().BeTrue(
                $"basis round-trip should hold at angle {angle}");
        }
    }

    [Fact]
    public void IsSameRotationAs_TreatsNegatedQuaternionsAsEqual()
    {
        Quatd q = Quatd.FromAxisAngle(Vec3d.UnitZ, 1.0);
        Quatd negated = Quatd.Negate(q);

        // Structural equality says different...
        q.Equals(negated).Should().BeFalse();
        // ...but they are the same rotation, and that is what geometry code must ask.
        q.IsSameRotationAs(negated).Should().BeTrue();
    }

    [Fact]
    public void Slerp_HitsTheEndpointsAndStaysUnit()
    {
        Quatd a = Quatd.FromAxisAngle(Vec3d.UnitZ, 0.0);
        Quatd b = Quatd.FromAxisAngle(Vec3d.UnitZ, 2.0);

        Quatd.Slerp(a, b, 0.0).IsSameRotationAs(a).Should().BeTrue();
        Quatd.Slerp(a, b, 1.0).IsSameRotationAs(b).Should().BeTrue();

        for (int i = 0; i <= 10; i++)
        {
            Quatd mid = Quatd.Slerp(a, b, i / 10.0);
            mid.Length.Should().BeApproximately(1.0, 1e-12);
        }
    }

    [Fact]
    public void Slerp_TakesTheShortArcAcrossTheHemisphereBoundary()
    {
        Quatd a = Quatd.FromAxisAngle(Vec3d.UnitZ, 0.1);
        Quatd b = Quatd.Negate(Quatd.FromAxisAngle(Vec3d.UnitZ, 0.3));

        Quatd mid = Quatd.Slerp(a, b, 0.5);
        Quatd expected = Quatd.FromAxisAngle(Vec3d.UnitZ, 0.2);

        mid.IsSameRotationAs(expected, 1e-9).Should().BeTrue();
    }

    [Fact]
    public void Slerp_ClampsTheParameter()
    {
        Quatd a = Quatd.Identity;
        Quatd b = Quatd.FromAxisAngle(Vec3d.UnitZ, 1.0);

        Quatd.Slerp(a, b, -5.0).IsSameRotationAs(a).Should().BeTrue();
        Quatd.Slerp(a, b, 5.0).IsSameRotationAs(b).Should().BeTrue();
    }

    [Fact]
    public void Normalized_ThrowsOnDegenerateInput()
    {
        Action act = () => new Quatd(0, 0, 0, 0).Normalized();
        act.Should().Throw<InvalidOperationException>();
    }
}
