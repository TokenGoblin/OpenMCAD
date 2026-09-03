using FluentAssertions;

using OpenMCAD.Math;

using Xunit;

namespace OpenMCAD.Math.Tests;

/// <summary>A 3-D curve as a kernel curve query would report one (P4-T11).</summary>
public sealed class WorldCurveTests
{
    [Fact]
    public void Circle_Full_IsRecognisedAsFull()
    {
        // Sweep wraps a full turn to exactly zero -- the same value a zero-length arc would report
        // -- which is exactly why IsFull is not defined in terms of it (see IsFull's own remarks).
        WorldCurve.Circle circle = WorldCurve.Circle.Full(Vec3d.Zero, Vec3d.UnitZ, Vec3d.UnitX, 5);

        circle.Sweep.Should().BeApproximately(0, 1e-12);
        circle.IsFull.Should().BeTrue();
    }

    [Fact]
    public void Circle_ZeroLengthArc_IsNotFull()
    {
        // The other value Sweep reports as zero: a genuine degenerate arc, which must not be
        // mistaken for a full circle just because they share a Sweep of zero.
        WorldCurve.Circle circle = new(Vec3d.Zero, Vec3d.UnitZ, Vec3d.UnitX, 5, 1.0, 1.0);

        circle.Sweep.Should().BeApproximately(0, 1e-12);
        circle.IsFull.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, System.Math.PI / 2)]
    [InlineData(-System.Math.PI / 4, System.Math.PI / 4)]
    public void Circle_PartialSweep_IsNotFull(double start, double end)
    {
        WorldCurve.Circle circle = new(Vec3d.Zero, Vec3d.UnitZ, Vec3d.UnitX, 5, start, end);

        circle.Sweep.Should().BeApproximately(System.Math.PI / 2, 1e-12);
        circle.IsFull.Should().BeFalse();
    }

    [Fact]
    public void Circle_Sweep_WrapsLikeSketchArcs()
    {
        // The same modulo convention SketchArc.Sweep uses (P4-T03), for the same reason: an end
        // angle numerically less than the start still describes a positive sweep, wrapping through
        // zero rather than reporting something negative.
        WorldCurve.Circle circle = new(
            Vec3d.Zero, Vec3d.UnitZ, Vec3d.UnitX, 5, System.Math.PI, System.Math.PI / 2);

        circle.Sweep.Should().BeApproximately((3 * System.Math.PI) / 2, 1e-12);
    }
}
