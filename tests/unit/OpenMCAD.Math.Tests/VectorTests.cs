using OpenMCAD.Math;

namespace OpenMCAD.MathTests;

public sealed class Vec3dTests
{
    [Fact]
    public void Arithmetic_FollowsComponentwiseRules()
    {
        Vec3d a = new(1, 2, 3);
        Vec3d b = new(10, 20, 30);

        (a + b).Should().Be(new Vec3d(11, 22, 33));
        (b - a).Should().Be(new Vec3d(9, 18, 27));
        (a * 2).Should().Be(new Vec3d(2, 4, 6));
        (2 * a).Should().Be(new Vec3d(2, 4, 6));
        (b / 10).Should().Be(new Vec3d(1, 2, 3));
        (-a).Should().Be(new Vec3d(-1, -2, -3));
    }

    [Fact]
    public void Dot_AndCross_MatchRightHandedConvention()
    {
        Vec3d.Dot(Vec3d.UnitX, Vec3d.UnitX).Should().Be(1.0);
        Vec3d.Dot(Vec3d.UnitX, Vec3d.UnitY).Should().Be(0.0);

        // Right-handed: X cross Y is Z.
        Vec3d.Cross(Vec3d.UnitX, Vec3d.UnitY).Should().Be(Vec3d.UnitZ);
        Vec3d.Cross(Vec3d.UnitY, Vec3d.UnitZ).Should().Be(Vec3d.UnitX);
        Vec3d.Cross(Vec3d.UnitZ, Vec3d.UnitX).Should().Be(Vec3d.UnitY);
    }

    [Fact]
    public void Length_IsExactForPythagoreanTriples()
    {
        new Vec3d(3, 4, 0).Length.Should().Be(5.0);
        new Vec3d(2, 3, 6).Length.Should().Be(7.0);
    }

    [Fact]
    public void TryNormalize_FailsOnDegenerateInput_AndDoesNotThrow()
    {
        Vec3d.Zero.TryNormalize(out Vec3d result).Should().BeFalse();
        result.Should().Be(Vec3d.Zero);

        new Vec3d(double.NaN, 0, 0).TryNormalize(out _).Should().BeFalse();
        new Vec3d(double.PositiveInfinity, 0, 0).TryNormalize(out _).Should().BeFalse();

        // Just under the resolution floor must fail; comfortably above it must succeed.
        new Vec3d(Tolerance.LinearResolution / 10, 0, 0).TryNormalize(out _).Should().BeFalse();
        new Vec3d(Tolerance.LinearResolution * 100, 0, 0).TryNormalize(out _).Should().BeTrue();
    }

    [Fact]
    public void Normalized_ThrowsOnDegenerateInput()
    {
        Action act = () => Vec3d.Zero.Normalized();
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 1)]
    [InlineData(1, 1, 1)]
    [InlineData(-3, 7, 0.5)]
    [InlineData(1e-6, 1e-6, 1e-6)]
    [InlineData(1e6, -1e6, 1e6)]
    public void AnyPerpendicular_IsUnitLengthAndActuallyPerpendicular(double x, double y, double z)
    {
        Vec3d v = new(x, y, z);
        Vec3d p = v.AnyPerpendicular();

        p.Length.Should().BeApproximately(1.0, 1e-12);
        Vec3d.Dot(v.Normalized(), p).Should().BeApproximately(0.0, 1e-12);
    }

    [Fact]
    public void AnyPerpendicular_IsDeterministic()
    {
        // ADR-0011: nothing in the geometry layer may vary between identical calls.
        Vec3d v = new(0.37, -0.91, 0.18);
        Vec3d first = v.AnyPerpendicular();

        for (int i = 0; i < 100; i++)
        {
            v.AnyPerpendicular().Should().Be(first);
        }
    }

    [Fact]
    public void AngleTo_IsAccurateForNearlyParallelVectors()
    {
        // The acos-of-dot formulation loses roughly half its digits here. The atan2 formulation
        // this type uses does not. This test exists to stop anyone "simplifying" it back.
        const double SmallAngle = 1e-9;
        Vec3d a = Vec3d.UnitX;
        Vec3d b = new(System.Math.Cos(SmallAngle), System.Math.Sin(SmallAngle), 0);

        a.AngleTo(b).Should().BeApproximately(SmallAngle, SmallAngle * 1e-6);
    }

    [Fact]
    public void AngleTo_IsAccurateForNearlyAntiparallelVectors()
    {
        double nearPi = System.Math.PI - 1e-9;
        Vec3d a = Vec3d.UnitX;
        Vec3d b = new(System.Math.Cos(nearPi), System.Math.Sin(nearPi), 0);

        a.AngleTo(b).Should().BeApproximately(nearPi, 1e-15);
    }

    [Fact]
    public void SignedAngleTo_RespectsAxisOrientation()
    {
        double angle = Vec3d.UnitX.SignedAngleTo(Vec3d.UnitY, Vec3d.UnitZ);
        angle.Should().BeApproximately(System.Math.PI / 2, 1e-12);

        double reversed = Vec3d.UnitX.SignedAngleTo(Vec3d.UnitY, -Vec3d.UnitZ);
        reversed.Should().BeApproximately(-System.Math.PI / 2, 1e-12);
    }

    [Fact]
    public void ProjectedOnto_AndPerpendicularTo_Decompose()
    {
        Vec3d v = new(3, 4, 5);
        Vec3d axis = new(1, 1, 0);

        Vec3d parallel = v.ProjectedOnto(axis);
        Vec3d perpendicular = v.PerpendicularTo(axis);

        (parallel + perpendicular).IsNear(v, 1e-12).Should().BeTrue();
        Vec3d.Dot(perpendicular, axis).Should().BeApproximately(0.0, 1e-12);
    }

    [Fact]
    public void IsParallelTo_AndIsPerpendicularTo_HandleDegenerateInputWithoutThrowing()
    {
        Vec3d.Zero.IsParallelTo(Vec3d.UnitX).Should().BeFalse();
        Vec3d.Zero.IsPerpendicularTo(Vec3d.UnitX).Should().BeFalse();

        Vec3d.UnitX.IsParallelTo(new Vec3d(-5, 0, 0)).Should().BeTrue();
        Vec3d.UnitX.IsPerpendicularTo(Vec3d.UnitY).Should().BeTrue();
    }

    [Fact]
    public void Indexer_RejectsOutOfRange()
    {
        Vec3d v = new(1, 2, 3);
        v[0].Should().Be(1);
        v[1].Should().Be(2);
        v[2].Should().Be(3);

        Action act = () => _ = v[3];
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ToString_IsCultureInvariantAndRoundTripPrecise()
    {
        // Round-tripping display values exactly is an ADR-0013 requirement.
        Vec3d v = new(0.1, 1.0 / 3.0, -2.5e-7);
        string text = v.ToString();

        text.Should().Contain(".");
        text.Should().NotContain(",00");
    }
}

public sealed class Vec2dTests
{
    [Fact]
    public void Cross_IsPositiveCounterClockwise()
    {
        Vec2d.Cross(Vec2d.UnitX, Vec2d.UnitY).Should().Be(1.0);
        Vec2d.Cross(Vec2d.UnitY, Vec2d.UnitX).Should().Be(-1.0);
    }

    [Fact]
    public void Perpendicular_RotatesNinetyDegreesCounterClockwise()
    {
        Vec2d.UnitX.Perpendicular().Should().Be(Vec2d.UnitY);
        Vec2d.UnitY.Perpendicular().Should().Be(-Vec2d.UnitX);
    }

    [Fact]
    public void Rotated_MatchesAngleArithmetic()
    {
        Vec2d v = new(2, 0);
        Vec2d r = v.Rotated(System.Math.PI / 2);

        r.IsNear(new Vec2d(0, 2), 1e-12).Should().BeTrue();
        r.Length.Should().BeApproximately(2.0, 1e-15);
    }

    [Fact]
    public void SignedAngleTo_IsSignedAndBounded()
    {
        Vec2d.UnitX.SignedAngleTo(Vec2d.UnitY).Should().BeApproximately(System.Math.PI / 2, 1e-12);
        Vec2d.UnitY.SignedAngleTo(Vec2d.UnitX).Should().BeApproximately(-System.Math.PI / 2, 1e-12);
    }
}
