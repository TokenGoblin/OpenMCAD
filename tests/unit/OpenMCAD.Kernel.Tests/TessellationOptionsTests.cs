using FluentAssertions;

using OpenMCAD.Math;

using Xunit;

namespace OpenMCAD.Kernel.Tests;

/// <summary>
/// Guards the named tessellation presets against the record-struct default trap.
/// </summary>
/// <remarks>
/// <c>TessellationOptions.Display</c> was once written <c>new()</c>, which on a record struct binds
/// to the implicit parameterless constructor and zeroes every field rather than running the primary
/// constructor with its default arguments. The result was a preset that asked for zero chordal
/// deviation. <c>FakeKernel</c> ignores the deviation entirely, so nothing caught it until OCCT
/// refused the call — which is exactly the kind of gap a preset should never be allowed to have.
/// </remarks>
public sealed class TessellationOptionsTests
{
    [Fact]
    public void Display_AsksForRealTolerances()
    {
        TessellationOptions options = TessellationOptions.Display;

        options.ChordalDeviation.Should().Be(Tolerance.DisplayChordal);
        options.AngularDeviation.Should().BePositive();
        options.ComputeNormals.Should().BeTrue("the display preset is what the viewport shades with");
    }

    [Fact]
    public void Display_IsNotTheDefaultStruct()
    {
        // The distinction this test exists for. If someone writes `new()` again, Display becomes
        // equal to default and this fails.
        TessellationOptions.Display.Should().NotBe(default(TessellationOptions));
    }

    [Theory]
    [InlineData("fine")]
    [InlineData("coarse")]
    public void EveryPreset_AsksForAPositiveDeviation(string name)
    {
        TessellationOptions options = name switch
        {
            "fine" => TessellationOptions.Fine,
            _ => TessellationOptions.Coarse,
        };

        options.ChordalDeviation.Should().BePositive();
        options.AngularDeviation.Should().BePositive();
    }

    [Fact]
    public void Coarse_IsCoarserThanFine()
    {
        TessellationOptions.Coarse.ChordalDeviation
            .Should().BeGreaterThan(TessellationOptions.Fine.ChordalDeviation);
    }
}
