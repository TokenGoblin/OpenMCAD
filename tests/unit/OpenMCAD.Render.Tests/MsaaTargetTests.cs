using System.Numerics;

using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Render.Direct3D12;

using SharpGen.Runtime;

using Vortice.Direct3D12;
using Vortice.Mathematics;

using Xunit;

namespace OpenMCAD.Render.Tests;

/// <summary>
/// Multisampling and the resolve into the swapchain (P2-T12).
/// </summary>
public sealed class MsaaTargetTests
{
    private const int Size = 160;

    private static RenderDeviceOptions Software => new(EnableDebugLayer: true, ForceSoftware: true);

    private static Color4 Clear => new(0.05f, 0.05f, 0.08f, 1.0f);

    /// <summary>The clear colour as it comes back out of the framebuffer.</summary>
    private static Pixel Background => new(
        (byte)System.Math.Round(Clear.R * 255),
        (byte)System.Math.Round(Clear.G * 255),
        (byte)System.Math.Round(Clear.B * 255),
        (byte)System.Math.Round(Clear.A * 255));

    [Fact]
    public void TheSampleCountIsNegotiatedNotAssumed()
    {
        using Fixture fixture = Fixture.Create(MsaaTarget.DefaultSampleCount);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        int samples = fixture.Target.SampleCount;

        samples.Should().BeGreaterThanOrEqualTo(1);
        (samples & (samples - 1)).Should().Be(0, "sample counts are powers of two");
        samples.Should().BeLessThanOrEqualTo(MsaaTarget.DefaultSampleCount);
    }

    [Fact]
    public void AskingForOneSampleGivesOne()
    {
        using Fixture fixture = Fixture.Create(1);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        fixture.Target.SampleCount.Should().Be(1);
        fixture.Target.IsMultisampled.Should().BeFalse();
    }

    [Fact]
    public void ASingleSampledTargetStillReachesTheDestination()
    {
        using Fixture fixture = Fixture.Create(1);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        // The regression that matters. ResolveSubresource requires a multisampled source and is
        // rejected outright otherwise -- the command list refuses to close, with no mention of the
        // resolve that caused it. That is precisely the path a device offering no multisampling
        // takes, so the fallback the negotiation exists to provide was broken until this ran.
        Pixel centre = fixture.RenderTriangleAndRead();

        centre.IsCloseTo(Background).Should().BeFalse(
            "the triangle should have reached the destination through the copy path");
    }

    [Fact]
    public void MultisamplingProducesIntermediateColoursAlongASlopedEdge()
    {
        using Fixture multisampled = Fixture.Create(4);
        using Fixture plain = Fixture.Create(1);

        if (multisampled.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        if (multisampled.Target.SampleCount == 1)
        {
            Assert.Skip("this device offers no multisampling, so there is nothing to compare");
            return;
        }

        // A hard edge has two colours and nothing between; a smoothed one has a run of blends.
        // Counting distinct colours is what separates the two, and it is the whole visible point
        // of the feature -- a sample count that was configured but never reached the rasteriser
        // would still report 4 from the device.
        multisampled.RenderTriangleAndRead();
        int blended = multisampled.DistinctColours();

        plain.RenderTriangleAndRead();
        int hard = plain.DistinctColours();

        blended.Should().BeGreaterThan(
            hard, $"multisampling should add intermediate colours, but got {blended} against {hard}");
    }

    [Fact]
    public void ResizingToTheSameSizeDoesNotReallocate()
    {
        using Fixture fixture = Fixture.Create(4);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        fixture.Target.Resize(Size, Size);
        nint first = fixture.Target.Colour.NativePointer;

        fixture.Target.Resize(Size, Size);

        fixture.Target.Colour.NativePointer.Should().Be(
            first, "an unchanged size should not throw away a full-screen target");
    }

    [Fact]
    public void UsingATargetWithNoSizeSaysSoRatherThanCrashing()
    {
        using Fixture fixture = Fixture.Create(4, size: 0);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        fixture.Target.IsAllocated.Should().BeFalse();

        Action act = () => _ = fixture.Target.Colour;
        act.Should().Throw<InvalidOperationException>().WithMessage("*Resize*");
    }

    // --- Fixture ------------------------------------------------------------------------------

    private sealed class Fixture : IDisposable
    {
        private Fixture(string skipped) => Skipped = skipped;

        private Fixture(
            D3D12RenderDevice device,
            MsaaTarget target,
            OffscreenSurface surface,
            FacePass faces,
            SceneGeometry scene)
        {
            Device = device;
            Target = target;
            Surface = surface;
            Faces = faces;
            Scene = scene;
        }

        public string? Skipped { get; }

        public D3D12RenderDevice Device { get; } = null!;

        public MsaaTarget Target { get; } = null!;

        public OffscreenSurface Surface { get; } = null!;

        public FacePass Faces { get; } = null!;

        public SceneGeometry Scene { get; } = null!;

        public Camera Camera { get; } = new();

        public static Fixture Create(int requestedSamples, int size = Size)
        {
            D3D12RenderDevice? device = null;
            MsaaTarget? target = null;
            OffscreenSurface? surface = null;
            FacePass? faces = null;
            SceneGeometry? scene = null;

            try
            {
                device = new D3D12RenderDevice(Software);
                target = new MsaaTarget(device.Device, Clear, requestedSamples);

                if (size > 0)
                {
                    target.Resize(size, size);
                }

                surface = new OffscreenSurface(device, System.Math.Max(size, 1), System.Math.Max(size, 1));

                faces = new FacePass(
                    device.Device,
                    OffscreenSurface.ColourFormat,
                    DepthBuffer.DepthFormat,
                    optimiseShaders: false,
                    target.SampleCount);

                SnapshotBuilder builder = new();
                builder.Add(EdgePassTestsGeometry.SolidBox(1.0));
                scene = SceneGeometry.Upload(device, builder.Build(1));

                Fixture fixture = new(device, target, surface, faces, scene);
                fixture.Camera.AspectRatio = 1.0;

                // A three-quarter view, so the silhouette runs diagonally: a box seen square-on
                // has only axis-aligned edges, which alias barely at all and would make the
                // comparison below look like multisampling does nothing.
                fixture.Camera.LookFrom(StandardView.Isometric);
                fixture.Camera.ZoomToFit(scene.Bounds);

                return fixture;
            }
            catch (Exception exception)
                when (exception is RenderDeviceUnavailableException or SharpGenException)
            {
                scene?.Dispose();
                faces?.Dispose();
                surface?.Dispose();
                target?.Dispose();
                device?.Dispose();

                return new Fixture($"No usable D3D12 device: {exception.Message}");
            }
        }

        /// <summary>Renders the box into the multisampled target, resolves, and reads it back.</summary>
        public Pixel RenderTriangleAndRead()
        {
            Mat4d projection = Camera.ProjectionMatrix(Scene.Bounds);
            Vec3d origin = Scene.Origin;

            Surface.SetConstants(new FrameConstants
            {
                ViewProjection = ToShaderMatrix(
                    projection * Mat4d.LookAt(
                        Camera.Position - origin, Camera.Target - origin, Camera.Up)),
                CameraPosition = ToVector3(Camera.Position - origin),
                LightDirection = ToVector3(FacePass.KeyLightDirection(Camera)),
                ViewportSize = new Vector2(Surface.Width, Surface.Height),
            });

            Surface.RenderInto(
                Target,
                Clear,
                commands => Faces.Draw(commands, Scene, Surface.ConstantBufferAddress));

            return Surface.Centre();
        }

        /// <summary>How many distinct colours the last frame contains.</summary>
        public int DistinctColours() => Surface.DistinctColours(default, bucket: 1);

        public void Dispose()
        {
            Scene?.Dispose();
            Faces?.Dispose();
            Surface?.Dispose();
            Target?.Dispose();
            Device?.Dispose();
        }

        private static Matrix4x4 ToShaderMatrix(Mat4d m) => new(
            (float)m.M11, (float)m.M12, (float)m.M13, (float)m.M14,
            (float)m.M21, (float)m.M22, (float)m.M23, (float)m.M24,
            (float)m.M31, (float)m.M32, (float)m.M33, (float)m.M34,
            (float)m.M41, (float)m.M42, (float)m.M43, (float)m.M44);

        private static Vector3 ToVector3(Vec3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
    }
}
