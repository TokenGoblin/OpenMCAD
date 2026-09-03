using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Solver.Sketching;

using Xunit;

namespace OpenMCAD.Solver.Tests;

/// <summary>The 2-D similarity transform behind every sketch editing tool (P4-T13).</summary>
public sealed class SketchTransformTests
{
    [Fact]
    public void Translate_MovesByTheOffset()
    {
        SketchTransform transform = SketchTransform.Translate(new Vec2d(3, -2));

        transform.Apply(new Vec2d(1, 1)).Should().Be(new Vec2d(4, -1));
    }

    [Fact]
    public void RotateAbout_LeavesTheCentreWhereItWas()
    {
        SketchTransform transform = SketchTransform.RotateAbout(new Vec2d(5, 5), System.Math.PI / 2);

        transform.Apply(new Vec2d(5, 5)).X.Should().BeApproximately(5, 1e-12);
        transform.Apply(new Vec2d(5, 5)).Y.Should().BeApproximately(5, 1e-12);
    }

    [Fact]
    public void RotateAbout_TurnsAPointTheRightWay()
    {
        SketchTransform transform = SketchTransform.RotateAbout(Vec2d.Zero, System.Math.PI / 2);
        Vec2d rotated = transform.Apply(new Vec2d(1, 0));

        rotated.X.Should().BeApproximately(0, 1e-12);
        rotated.Y.Should().BeApproximately(1, 1e-12);
    }

    [Fact]
    public void ScaleAbout_LeavesTheCentreWhereItWasAndScalesEverythingElse()
    {
        SketchTransform transform = SketchTransform.ScaleAbout(new Vec2d(10, 0), 2);

        transform.Apply(new Vec2d(10, 0)).Should().Be(new Vec2d(10, 0));
        transform.Apply(new Vec2d(12, 0)).Should().Be(new Vec2d(14, 0), "two units out becomes four");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void ScaleAbout_RejectsANonPositiveFactor(double factor)
    {
        Action act = () => SketchTransform.ScaleAbout(Vec2d.Zero, factor);

        act.Should().Throw<ArgumentOutOfRangeException>(
            "a zero or negative scale is not a shrink or an enlargement, and a mirror has its own "
            + "tool rather than being smuggled in as a negative one");
    }

    [Fact]
    public void MirrorAbout_TheXAxisNegatesY()
    {
        SketchTransform transform = SketchTransform.MirrorAbout(Vec2d.Zero, Vec2d.UnitX);
        Vec2d mirrored = transform.Apply(new Vec2d(3, 4));

        mirrored.X.Should().BeApproximately(3, 1e-12);
        mirrored.Y.Should().BeApproximately(-4, 1e-12);
    }

    [Fact]
    public void MirrorAbout_TheYAxisNegatesX()
    {
        SketchTransform transform = SketchTransform.MirrorAbout(Vec2d.Zero, Vec2d.UnitY);
        Vec2d mirrored = transform.Apply(new Vec2d(3, 4));

        // A doubled angle of pi, reached via Rotated(pi) rather than a literal -1, carries the
        // usual few ULPs of floating-point noise -- an exact Be here would be pinning that noise
        // rather than the geometry.
        mirrored.X.Should().BeApproximately(-3, 1e-12);
        mirrored.Y.Should().BeApproximately(4, 1e-12);
    }

    [Fact]
    public void MirrorAbout_TheLineYEqualsXSwapsTheCoordinates()
    {
        SketchTransform transform = SketchTransform.MirrorAbout(Vec2d.Zero, Vec2d.One);

        Vec2d mirrored = transform.Apply(new Vec2d(3, 4));

        mirrored.X.Should().BeApproximately(4, 1e-12);
        mirrored.Y.Should().BeApproximately(3, 1e-12);
    }

    [Fact]
    public void MirrorAbout_APointOnTheLineDoesNotMove()
    {
        SketchTransform transform = SketchTransform.MirrorAbout(new Vec2d(1, 2), new Vec2d(5, 6));

        Vec2d onLine = new(3, 4);

        transform.Apply(onLine).X.Should().BeApproximately(3, 1e-9);
        transform.Apply(onLine).Y.Should().BeApproximately(4, 1e-9);
    }

    [Fact]
    public void MirrorAbout_RejectsTwoCoincidentPoints()
    {
        Action act = () => SketchTransform.MirrorAbout(new Vec2d(1, 1), new Vec2d(1, 1));

        act.Should().Throw<InvalidOperationException>(
            "two coincident points name no line, so there is no direction to mirror about");
    }

    [Fact]
    public void ApplyAngle_AddsTheRotationWhenNotReflected()
    {
        SketchTransform transform = SketchTransform.RotateAbout(Vec2d.Zero, System.Math.PI / 4);

        transform.ApplyAngle(System.Math.PI / 4).Should().BeApproximately(System.Math.PI / 2, 1e-12);
    }

    [Fact]
    public void ApplyAngle_AgreesWithApplyForAPointOnAUnitCircle()
    {
        // The property ApplyAngle exists to guarantee: reconstructing a point from the transformed
        // angle must land exactly where transforming the point directly would.
        SketchTransform transform = SketchTransform.MirrorAbout(Vec2d.Zero, new Vec2d(1, 1));
        double angle = 0.7;

        Vec2d direct = transform.Apply(new Vec2d(System.Math.Cos(angle), System.Math.Sin(angle)));
        double mapped = transform.ApplyAngle(angle);
        Vec2d reconstructed = new(System.Math.Cos(mapped), System.Math.Sin(mapped));

        reconstructed.X.Should().BeApproximately(direct.X, 1e-9);
        reconstructed.Y.Should().BeApproximately(direct.Y, 1e-9);
    }
}
