using System.Runtime.InteropServices;

using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Render.Direct3D12;

using SharpGen.Runtime;

using Vortice.Direct3D12;
using Vortice.Mathematics;

using Xunit;

namespace OpenMCAD.Render.Tests;

/// <summary>
/// Reference plane display (P2-T11).
/// </summary>
/// <remarks>
/// The pixel tests here render a single plane square-on and read the middle back. That is a weak
/// picture of a renderer and a strong one of this pass: what can go wrong is that nothing is drawn
/// at all, that it is drawn opaque, or that it is drawn where the geometry is not — and reading one
/// pixel in the middle and one outside the square separates all three.
/// </remarks>
public sealed class ReferencePlanePassTests
{
    private const int Size = 192;

    private static Bounds3d Metre =>
        new(new Vec3d(-0.5, -0.5, -0.5), new Vec3d(0.5, 0.5, 0.5));

    // --- Sizing ------------------------------------------------------------------------------

    [Theory]
    [InlineData(2.0)]
    [InlineData(200.0)]
    [InlineData(0.02)]
    public void TheSquareIsSizedFromTheSceneRatherThanFixed(double extent)
    {
        // The decision this pass turns on. A fixed size is wrong at nearly every scale a CAD user
        // works at, and a size taken from the camera would hold the planes still on screen while
        // the model grew and shrank against them.
        double half = extent / (2 * System.Math.Sqrt(3));
        Bounds3d bounds = new(new Vec3d(-half, -half, -half), new Vec3d(half, half, half));

        float size = ReferencePlanePass.HalfSizeFor(bounds, PlaneStyle.Default);

        size.Should().BeApproximately(
            (float)(extent * PlaneStyle.Default.SizeFraction / 2), (float)(extent * 0.001));
    }

    [Fact]
    public void AnEmptySceneStillGetsAUsableSize()
    {
        // A brand-new document has no bodies and is exactly when the datums matter most, having
        // nothing else to sketch on. Sizing them to nothing would show the user an empty void.
        ReferencePlanePass.HalfSizeFor(Bounds3d.Empty, PlaneStyle.Default)
            .Should().BeGreaterThan(0);
    }

    [Fact]
    public void ADegenerateSceneDoesNotCollapseTheSquare()
    {
        Bounds3d point = new(Vec3d.Zero, Vec3d.Zero);

        ReferencePlanePass.HalfSizeFor(point, PlaneStyle.Default).Should().BeGreaterThan(0);
    }

    // --- Uploading ----------------------------------------------------------------------------

    [Fact]
    public void MorePlanesThanTheBufferHoldsAreDroppedRatherThanOverrunning()
    {
        using Fixture fixture = Fixture.Create(Size);
        Assert.SkipWhen(fixture.Skipped is not null, fixture.Skipped ?? string.Empty);

        DisplayPlane[] many =
        [
            .. Enumerable.Range(0, ReferencePlanePass.MaximumPlanes + 20)
                .Select(_ => new DisplayPlane(Vec3d.Zero, Vec3d.UnitX, Vec3d.UnitZ)),
        ];

        fixture.Pass.Upload(many, Vec3d.Zero);

        fixture.Pass.PlanesUploaded.Should().Be(ReferencePlanePass.MaximumPlanes);
    }

    // --- Drawing ------------------------------------------------------------------------------

    [Fact]
    public void APlaneFacingTheCameraIsDrawnWhereItIs()
    {
        using Fixture fixture = Fixture.Create(Size);
        Assert.SkipWhen(fixture.Skipped is not null, fixture.Skipped ?? string.Empty);

        fixture.Render(new DisplayPlane(Vec3d.Zero, Vec3d.UnitX, Vec3d.UnitZ));

        // Cleared to magenta, so a plane that failed to draw shows as magenta rather than as a
        // plausible blue-grey.
        fixture.Surface.Centre().Should().NotBe(
            fixture.Clear, "the plane covers the middle of the view");
    }

    [Fact]
    public void APlaneIsTranslucentRatherThanSolid()
    {
        // The whole point of a datum plane is that the model in front of it stays readable. A pass
        // that drew it opaque would pass "something was drawn" and be useless.
        using Fixture fixture = Fixture.Create(Size);
        Assert.SkipWhen(fixture.Skipped is not null, fixture.Skipped ?? string.Empty);

        fixture.Render(new DisplayPlane(Vec3d.Zero, Vec3d.UnitX, Vec3d.UnitZ));

        Pixel middle = fixture.Surface.Centre();
        Color4 tint = PlaneStyle.Default.Colour;

        // Between the background and the plane's own colour: blended, not replaced. Magenta has no
        // green in it and the tint has plenty, so green rising off the floor without reaching the
        // tint's own value is exactly what blending looks like here.
        middle.G.Should().BeGreaterThan(0, "the plane's colour reached the pixel");
        middle.G.Should().BeLessThan(
            (byte)(tint.G * 255), "it was blended with the background rather than replacing it");
    }

    [Fact]
    public void NothingIsDrawnBeyondTheSquare()
    {
        // Sized to a tenth of the scene, the square covers the middle and nothing near the corner.
        // Without this the pass could be drawing a full-screen quad and the test above would not
        // know.
        using Fixture fixture = Fixture.Create(Size);
        Assert.SkipWhen(fixture.Skipped is not null, fixture.Skipped ?? string.Empty);

        fixture.Render(
            new DisplayPlane(Vec3d.Zero, Vec3d.UnitX, Vec3d.UnitZ),
            PlaneStyle.Default with { SizeFraction = 0.1f });

        fixture.Surface.At(4, 4).Should().Be(fixture.Clear, "the corner is outside the square");
        fixture.Surface.Centre().Should().NotBe(fixture.Clear, "the middle is inside it");
    }

    [Fact]
    public void APlaneEdgeOnToTheCameraCoversAlmostNothing()
    {
        // A plane seen edge-on is a line. This is the cheapest check that the square is built from
        // the frame it was given rather than always facing the viewer -- a billboarded quad would
        // fill the view here. The front view looks along -Y, so XY is the edge-on plane and XZ is
        // the one square-on to the camera.
        using Fixture fixture = Fixture.Create(Size);
        Assert.SkipWhen(fixture.Skipped is not null, fixture.Skipped ?? string.Empty);

        int covered = fixture.CoveredPixels(
            new DisplayPlane(Vec3d.Zero, Vec3d.UnitX, Vec3d.UnitY));

        int facing = fixture.CoveredPixels(
            new DisplayPlane(Vec3d.Zero, Vec3d.UnitX, Vec3d.UnitZ));

        covered.Should().BeLessThan(
            facing / 4, "seen edge-on it is a line across the view, not a square filling it");
    }

    [Fact]
    public void NoPlanesMeansNoDraw()
    {
        using Fixture fixture = Fixture.Create(Size);
        Assert.SkipWhen(fixture.Skipped is not null, fixture.Skipped ?? string.Empty);

        fixture.Pass.Upload([], Vec3d.Zero);
        fixture.RenderUploaded();

        fixture.Surface.Centre().Should().Be(
            fixture.Clear, "an empty snapshot leaves the view exactly as it was");
    }

    /// <summary>A device, a surface and the pass, or a reason there is none.</summary>
    private sealed class Fixture : IDisposable
    {
        private ID3D12Resource _constants = null!;

        private Fixture(string skipped) => Skipped = skipped;

        private Fixture(D3D12RenderDevice device, OffscreenSurface surface, ReferencePlanePass pass)
        {
            Device = device;
            Surface = surface;
            Pass = pass;
        }

        public string? Skipped { get; }

        public D3D12RenderDevice Device { get; } = null!;

        public OffscreenSurface Surface { get; } = null!;

        public ReferencePlanePass Pass { get; } = null!;

        public Camera Camera { get; } = new();

        public Color4 ClearColour { get; } = new(1, 0, 1, 1);

        /// <summary>The clear colour as it comes back out of the framebuffer.</summary>
        public Pixel Clear => new(
            (byte)System.Math.Round(ClearColour.R * 255),
            (byte)System.Math.Round(ClearColour.G * 255),
            (byte)System.Math.Round(ClearColour.B * 255),
            (byte)System.Math.Round(ClearColour.A * 255));

        public static Fixture Create(int size)
        {
            if (TestDevices.Shared is not { } device)
            {
                return new Fixture(TestDevices.Unavailable!);
            }

            OffscreenSurface? surface = null;
            ReferencePlanePass? pass = null;

            try
            {
                surface = new OffscreenSurface(device, size, size);
                pass = new ReferencePlanePass(
                    device.Device, OffscreenSurface.ColourFormat, optimiseShaders: false);

                Fixture fixture = new(device, surface, pass);

                fixture._constants = device.Device.CreateCommittedResource(
                    HeapType.Upload,
                    HeapFlags.None,
                    ResourceDescription.Buffer(256),
                    ResourceStates.GenericRead);

                fixture.Camera.AspectRatio = 1.0;
                fixture.Camera.LookFrom(StandardView.Front);
                fixture.Camera.ZoomToFit(Metre);

                return fixture;
            }
            catch (Exception exception)
                when (exception is RenderDeviceUnavailableException or SharpGenException)
            {
                pass?.Dispose();
                surface?.Dispose();

                return new Fixture($"This device could not build what the test needs: {exception.Message}");
            }
        }

        public void Render(DisplayPlane plane, PlaneStyle? style = null)
        {
            Pass.Upload([plane], Vec3d.Zero);
            RenderUploaded(style);
        }

        public void RenderUploaded(PlaneStyle? style = null)
        {
            PlaneConstants constants = ReferencePlanePass.ConstantsFor(
                Camera, Metre, Vec3d.Zero, style ?? PlaneStyle.Default);

            _constants.SetData(MemoryMarshal.AsBytes(new[] { constants }.AsSpan()));

            Surface.Render(
                ClearColour,
                commands => Pass.Draw(commands, _constants.GPUVirtualAddress));
        }

        /// <summary>How many pixels the plane covered, sampled on a grid.</summary>
        public int CoveredPixels(DisplayPlane plane)
        {
            Render(plane);

            int covered = 0;

            for (int y = 2; y < Surface.Height; y += 4)
            {
                for (int x = 2; x < Surface.Width; x += 4)
                {
                    if (!Surface.At(x, y).IsCloseTo(Clear))
                    {
                        covered++;
                    }
                }
            }

            return covered;
        }

        public void Dispose()
        {
            _constants?.Dispose();
            Pass?.Dispose();
            Surface?.Dispose();
        }
    }
}
