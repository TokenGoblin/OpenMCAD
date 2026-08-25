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
/// The background gradient and ground grid (P2-T11).
/// </summary>
public sealed class EnvironmentPassTests
{
    private const int Size = 192;

    private static RenderDeviceOptions Software => new(EnableDebugLayer: true, ForceSoftware: true);

    private static Bounds3d Metre =>
        new(new Vec3d(-0.5, -0.5, -0.5), new Vec3d(0.5, 0.5, 0.5));

    // --- Choosing a spacing -------------------------------------------------------------------

    [Theory]
    [InlineData(0.02, 0.001)]
    [InlineData(0.2, 0.01)]
    [InlineData(2.0, 0.1)]
    [InlineData(20.0, 1.0)]
    [InlineData(200.0, 10.0)]
    public void SpacingLandsOnARoundNumberForTheSceneSize(double extent, double expected)
    {
        // A fixed spacing is wrong at nearly every scale a CAD user works at: ten millimetres is
        // right for a bracket and invisible for a building. Snapping to a power of ten also means
        // the lines land on numbers a user can count in.
        double half = extent / (2 * System.Math.Sqrt(3));
        Bounds3d bounds = new(new Vec3d(-half, -half, -half), new Vec3d(half, half, half));

        EnvironmentStyle style = EnvironmentStyle.Default.ForScene(bounds);

        style.Spacing.Should().BeApproximately((float)expected, (float)(expected * 0.001));
    }

    [Fact]
    public void AnEmptySceneLeavesTheStyleAlone()
    {
        // Nothing to scale to, and a grid that vanished the moment a document was emptied would
        // read as the viewport failing.
        EnvironmentStyle style = EnvironmentStyle.Default.ForScene(Bounds3d.Empty);

        style.Should().Be(EnvironmentStyle.Default);
    }

    [Fact]
    public void ADegenerateSceneDoesNotProduceAnUnusableSpacing()
    {
        // A single point has no extent, and log10(0) is negative infinity.
        Bounds3d point = Bounds3d.FromPoint(new Vec3d(1, 2, 3));

        EnvironmentStyle style = EnvironmentStyle.Default.ForScene(point);

        style.Spacing.Should().BeGreaterThan(0);
        float.IsFinite(style.Spacing).Should().BeTrue();
        float.IsFinite(style.FadeDistance).Should().BeTrue();
    }

    [Fact]
    public void TheConstantsMatchTheShaderPacking()
    {
        EnvironmentConstants.SizeInBytes.Should().Be(176);

        Marshal.OffsetOf<EnvironmentConstants>(nameof(EnvironmentConstants.CameraPosition))
            .ToInt32().Should().Be(64);

        Marshal.OffsetOf<EnvironmentConstants>(nameof(EnvironmentConstants.TopColour))
            .ToInt32().Should().Be(80);

        Marshal.OffsetOf<EnvironmentConstants>(nameof(EnvironmentConstants.GridSpacing))
            .ToInt32().Should().Be(160);
    }

    // --- Rendering ----------------------------------------------------------------------------

    [Fact]
    public void TheBackgroundIsAGradientNotAFlatColour()
    {
        using Fixture fixture = Fixture.Create(Size);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        // Grid off, so only the gradient is under test.
        fixture.Render(showGrid: false);

        Pixel top = fixture.Surface.At(Size / 2, 2);
        Pixel bottom = fixture.Surface.At(Size / 2, Size - 3);

        top.IsCloseTo(bottom).Should().BeFalse(
            $"the background should shade from top to bottom, but both were {top}");

        // Darker at the top, which is what gives a part something to contrast against wherever it
        // sits rather than only at one end of the viewport.
        (top.R + top.G + top.B).Should().BeLessThan(bottom.R + bottom.G + bottom.B);
    }

    [Fact]
    public void TheGridDrawsLinesOverTheGradient()
    {
        using Fixture fixture = Fixture.Create(Size);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        fixture.Render(showGrid: false);
        int plainColours = fixture.Surface.DistinctColours(default, bucket: 4);

        fixture.Render(showGrid: true);
        int griddedColours = fixture.Surface.DistinctColours(default, bucket: 4);

        griddedColours.Should().BeGreaterThan(
            plainColours, "grid lines and axes add colours a bare gradient does not have");
    }

    [Fact]
    public void TheGridIsAbsentWhenSwitchedOff()
    {
        using Fixture fixture = Fixture.Create(Size);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        // A vertical gradient alone gives one colour per row, so any variation *along* a row is
        // something else having been drawn.
        //
        // Every pixel of every row, not a sample and not one row. The first version stepped in
        // sevens and the second checked a single row near the bottom; both passed with the grid
        // forced on. Near the camera a tenth of a metre can span more than the whole viewport, so
        // an individual row may legitimately fall between two lines and prove nothing.
        fixture.Render(showGrid: false);

        int worstRow = -1;
        int worstRange = 0;

        for (int y = 0; y < Size; ++y)
        {
            int lightest = 0;
            int darkest = int.MaxValue;

            for (int x = 0; x < Size; ++x)
            {
                Pixel pixel = fixture.Surface.At(x, y);
                int luminance = pixel.R + pixel.G + pixel.B;

                lightest = System.Math.Max(lightest, luminance);
                darkest = System.Math.Min(darkest, luminance);
            }

            if (lightest - darkest > worstRange)
            {
                worstRange = lightest - darkest;
                worstRow = y;
            }
        }

        worstRange.Should().BeLessThan(
            6, $"every row should be flat with the grid off, but row {worstRow} ranged {worstRange}");
    }

    [Fact]
    public void TheAxesAreDrawnInTheConventionalColours()
    {
        using Fixture fixture = Fixture.Create(Size);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        // X red and Y green is near-universal, and departing from it costs a user their
        // orientation in a way no legend recovers.
        fixture.Render(showGrid: true);

        bool reddish = false;
        bool greenish = false;

        for (int y = 0; y < Size; ++y)
        {
            for (int x = 0; x < Size; ++x)
            {
                Pixel pixel = fixture.Surface.At(x, y);

                reddish |= pixel.R > pixel.G + 25 && pixel.R > pixel.B + 25;
                greenish |= pixel.G > pixel.R + 20 && pixel.G > pixel.B + 20;
            }
        }

        reddish.Should().BeTrue("the X axis should be visibly red");
        greenish.Should().BeTrue("the Y axis should be visibly green");
    }

    [Fact]
    public void TheEnvironmentWritesNoDepthSoGeometryDrawsOverIt()
    {
        using Fixture fixture = Fixture.Create(Size);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        // The gradient sits at the far plane. If it wrote depth, everything drawn afterwards would
        // fail the test and the viewport would show nothing but background -- which looks exactly
        // like a renderer that has stopped working.
        fixture.RenderWithCube();

        Pixel centre = fixture.Surface.Centre();
        Pixel corner = fixture.Surface.At(2, 2);

        centre.IsCloseTo(corner).Should().BeFalse(
            "the cube should be visible against the background, not hidden behind it");
    }

    // --- Fixture ------------------------------------------------------------------------------

    private sealed class Fixture : IDisposable
    {
        private Fixture(string skipped) => Skipped = skipped;

        private Fixture(
            D3D12RenderDevice device,
            OffscreenSurface surface,
            EnvironmentPass pass,
            FacePass faces,
            SceneGeometry scene)
        {
            Device = device;
            Surface = surface;
            Pass = pass;
            Faces = faces;
            Scene = scene;

            _constants = device.Device.CreateCommittedResource(
                HeapType.Upload,
                HeapFlags.None,
                ResourceDescription.Buffer(256),
                ResourceStates.GenericRead);
        }

        private readonly ID3D12Resource _constants = null!;

        public string? Skipped { get; }

        public D3D12RenderDevice Device { get; } = null!;

        public OffscreenSurface Surface { get; } = null!;

        public EnvironmentPass Pass { get; } = null!;

        public FacePass Faces { get; } = null!;

        public SceneGeometry Scene { get; } = null!;

        public Camera Camera { get; } = new();

        public static Fixture Create(int size)
        {
            D3D12RenderDevice? device = null;
            OffscreenSurface? surface = null;
            EnvironmentPass? pass = null;
            FacePass? faces = null;
            SceneGeometry? scene = null;

            try
            {
                device = new D3D12RenderDevice(Software);
                surface = new OffscreenSurface(device, size, size);
                pass = new EnvironmentPass(device.Device, OffscreenSurface.ColourFormat, optimiseShaders: false);
                faces = new FacePass(device.Device, OffscreenSurface.ColourFormat, optimiseShaders: false);

                SnapshotBuilder builder = new();
                builder.Add(EdgePassTestsGeometry.SolidBox(0.4));
                scene = SceneGeometry.Upload(device, builder.Build(1));

                Fixture fixture = new(device, surface, pass, faces, scene);
                fixture.Camera.AspectRatio = 1.0;
                fixture.Camera.LookFrom(StandardView.Isometric);
                fixture.Camera.ZoomToFit(Metre);

                return fixture;
            }
            catch (Exception exception)
                when (exception is RenderDeviceUnavailableException or SharpGenException)
            {
                scene?.Dispose();
                faces?.Dispose();
                pass?.Dispose();
                surface?.Dispose();
                device?.Dispose();

                return new Fixture($"No usable D3D12 device: {exception.Message}");
            }
        }

        public void Render(bool showGrid)
        {
            WriteConstants(showGrid);

            // Cleared to something unmistakable, so a gradient that failed to draw shows up as
            // magenta rather than as a plausible dark colour.
            Surface.Render(
                new Color4(1, 0, 1, 1),
                commands => Pass.Draw(commands, _constants.GPUVirtualAddress));
        }

        public void RenderWithCube()
        {
            WriteConstants(showGrid: true);

            Mat4d projection = Camera.ProjectionMatrix(Scene.Bounds);
            Vec3d origin = Scene.Origin;

            Surface.SetConstants(new FrameConstants
            {
                ViewProjection = ToShaderMatrix(
                    projection * Mat4d.LookAt(
                        Camera.Position - origin, Camera.Target - origin, Camera.Up)),
                CameraPosition = ToVector3(Camera.Position - origin),
                LightDirection = ToVector3(FacePass.KeyLightDirection(Camera)),
                ViewportSize = new System.Numerics.Vector2(Surface.Width, Surface.Height),
            });

            Surface.Render(new Color4(1, 0, 1, 1), commands =>
            {
                Pass.Draw(commands, _constants.GPUVirtualAddress);
                Faces.Draw(commands, Scene, Surface.ConstantBufferAddress);
            });
        }

        public void Dispose()
        {
            _constants?.Dispose();
            Scene?.Dispose();
            Faces?.Dispose();
            Pass?.Dispose();
            Surface?.Dispose();
            Device?.Dispose();
        }

        private void WriteConstants(bool showGrid)
        {
            EnvironmentConstants constants = EnvironmentPass.ConstantsFor(
                Camera,
                Metre,
                Vec3d.Zero,
                EnvironmentStyle.Default.ForScene(Metre),
                showGrid);

            ReadOnlySpan<EnvironmentConstants> one = new(in constants);
            _constants.SetData(MemoryMarshal.AsBytes(one));
        }

        private static System.Numerics.Matrix4x4 ToShaderMatrix(Mat4d m) => new(
            (float)m.M11, (float)m.M12, (float)m.M13, (float)m.M14,
            (float)m.M21, (float)m.M22, (float)m.M23, (float)m.M24,
            (float)m.M31, (float)m.M32, (float)m.M33, (float)m.M34,
            (float)m.M41, (float)m.M42, (float)m.M43, (float)m.M44);

        private static System.Numerics.Vector3 ToVector3(Vec3d v)
            => new((float)v.X, (float)v.Y, (float)v.Z);
    }
}
