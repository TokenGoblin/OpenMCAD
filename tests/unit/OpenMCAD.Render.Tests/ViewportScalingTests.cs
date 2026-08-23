using FluentAssertions;

using OpenMCAD.Render;

using Xunit;

namespace OpenMCAD.Render.Tests;

/// <summary>
/// Per-monitor DPI, reduced to the arithmetic that decides the back-buffer size (P2-T02).
/// </summary>
public sealed class ViewportScalingTests
{
    [Theory]
    [InlineData(800, 600, 1.0, 1.0, 800, 600)]     // 96 DPI
    [InlineData(800, 600, 1.25, 1.25, 1000, 750)]  // 120 DPI
    [InlineData(800, 600, 1.5, 1.5, 1200, 900)]    // 144 DPI
    [InlineData(800, 600, 2.0, 2.0, 1600, 1200)]   // 192 DPI
    public void AViewportIsSizedInRealPixels(
        double width, double height, double scaleX, double scaleY, int expectedWidth, int expectedHeight)
    {
        // Sizing the buffer in layout units and letting the compositor stretch is the usual
        // mistake. It costs sharpness on exactly the thing CAD draws: thin lines.
        ViewportScaling.ToPhysicalPixels(width, height, scaleX, scaleY)
            .Should().Be((expectedWidth, expectedHeight));
    }

    [Fact]
    public void MonitorsMayScaleDifferentlyInEachDirection()
    {
        // Rare but real, and assuming a single scale silently stretches the image on such a display.
        ViewportScaling.ToPhysicalPixels(100, 100, 1.5, 2.0).Should().Be((150, 200));
    }

    [Fact]
    public void AFractionalSizeRoundsUpRatherThanLeavingASeam()
    {
        // 100 * 1.25 is exact; 101 * 1.25 is 126.25. Rounding down leaves a quarter-pixel column
        // at the right edge that nothing ever paints.
        ViewportScaling.ToPhysicalPixels(101, 101, 1.25, 1.25).Should().Be((127, 127));
    }

    [Fact]
    public void AMinimisedWindowStillYieldsACreatableSize()
    {
        // Minimising lays out at zero, and DXGI refuses a zero-sized buffer.
        ViewportScaling.ToPhysicalPixels(0, 0, 1.5, 1.5).Should().Be((1, 1));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ANonsenseScaleFallsBackToOneHundredPercent(double scale)
    {
        // A degenerate scale can only come from a monitor query that failed. Guessing 100% gives a
        // viewport that is the wrong size; propagating the nonsense gives no viewport at all.
        ViewportScaling.ToPhysicalPixels(800, 600, scale, scale).Should().Be((800, 600));
    }

    [Fact]
    public void ANonsenseSizeDoesNotProduceANegativeBuffer()
    {
        ViewportScaling.ToPhysicalPixels(double.NaN, double.NaN, 1.0, 1.0).Should().Be((1, 1));
    }
}
