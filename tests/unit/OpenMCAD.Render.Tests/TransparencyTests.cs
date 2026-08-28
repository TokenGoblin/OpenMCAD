using System.Numerics;
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
/// Weighted-blended transparency (P2-T10).
/// </summary>
/// <remarks>
/// The claim being tested is in the name of the technique: the result must not depend on the order
/// fragments arrive in. Every other property here — that it blends at all, that opaque geometry
/// still occludes it — is shared with sorted transparency, which is the thing this exists to
/// avoid. Only the order-independence assertion distinguishes the two.
/// </remarks>
public sealed class TransparencyTests
{
    private const int Size = 128;


    private static Color4 Clear => new(0.05f, 0.05f, 0.08f, 1.0f);

    /// <summary>The clear colour as it comes back out of the framebuffer.</summary>
    private static Pixel Background => new(
        (byte)System.Math.Round(Clear.R * 255),
        (byte)System.Math.Round(Clear.G * 255),
        (byte)System.Math.Round(Clear.B * 255),
        255);

    [Fact]
    public void TheTransparencyShadersCompile()
    {
        ShaderLibrary.Compile(
            TransparencyPass.AccumulateShaderFile, "PSMainTransparent",
            ShaderLibrary.PixelProfile, false).Length.Should().BeGreaterThan(0);

        ShaderLibrary.Compile(
            TransparencyPass.CompositeShaderFile, "PSMain",
            ShaderLibrary.PixelProfile, false).Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void TheCompositeConstantsMatchTheShaderPacking()
    {
        CompositeConstants.SizeInBytes.Should().Be(16);

        Marshal.OffsetOf<CompositeConstants>(nameof(CompositeConstants.SampleCount))
            .ToInt32().Should().Be(4);
    }

    [Fact]
    public void ATransparentBodyLetsTheBackgroundThrough()
    {
        using Fixture fixture = Fixture.Create();

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        fixture.RenderTransparent(alpha: 1.0f);
        Pixel opaque = fixture.Surface.Centre();

        fixture.RenderTransparent(alpha: 0.35f);
        Pixel translucent = fixture.Surface.Centre();

        translucent.IsCloseTo(opaque).Should().BeFalse(
            $"a translucent body should differ from a solid one, but both were {opaque}");

        translucent.IsCloseTo(Background).Should().BeFalse(
            "and it should still be visible rather than invisible");
    }

    [Fact]
    public void TheResultDoesNotDependOnTheOrderBodiesAreDrawn()
    {
        using Fixture fixture = Fixture.Create();

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        // The whole reason this technique exists. Sorted transparency gives a different image for
        // each order, and the ordering that is correct changes as the camera moves -- which is why
        // sorted transparency makes faces appear to pop in front of one another during an orbit.
        //
        // Two overlapping bodies at different depths, drawn both ways round.
        Pixel nearFirst = fixture.RenderTwoOverlapping(nearFirst: true);
        Pixel farFirst = fixture.RenderTwoOverlapping(nearFirst: false);

        // Checked first: two bodies must actually both reach this pixel, or the assertion below
        // would hold for the uninteresting reason that only one of them was ever visible.
        fixture.RenderTransparent(alpha: 0.5f);
        Pixel single = fixture.Surface.Centre();

        nearFirst.IsCloseTo(single).Should().BeFalse(
            $"both bodies must contribute, but two looked the same as one at {single}");

        nearFirst.IsCloseTo(farFirst, tolerance: 2).Should().BeTrue(
            $"order must not change the result, but got {nearFirst} against {farFirst}");
    }

    [Fact]
    public void TheMultisampledPathProducesTheSameResultAsTheSingleSampledOne()
    {
        using Fixture plain = Fixture.Create(1);
        using Fixture multisampled = Fixture.Create(4);

        string? skipped = plain.Skipped ?? multisampled.Skipped;

        if (skipped is not null)
        {
            Assert.Skip(skipped);
            return;
        }

        if (multisampled.Msaa.SampleCount == 1)
        {
            Assert.Skip("this device offers no multisampling, so there are not two paths");
            return;
        }

        // The composite declares two pairs of textures -- Texture2D and Texture2DMS -- because
        // HLSL cannot choose a resource type at run time, and reads whichever matches the sample
        // count. The descriptors therefore have to land at the slots the *taken* branch reads.
        //
        // Writing them to t0 and t1 regardless is the obvious mistake, and it was mine: the
        // multisampled branch then read unpopulated slots, got zeros, and interpreted zero
        // revealage as "nothing got through" -- laying black over the entire viewport. Every test
        // passed, because all of them ran at one sample.
        plain.RenderTransparent(alpha: 0.5f);
        Pixel single = plain.Surface.Centre();

        multisampled.RenderTransparent(alpha: 0.5f);
        Pixel many = multisampled.Surface.Centre();

        many.IsCloseTo(Background).Should().BeFalse(
            "the multisampled path must produce an image, not a black film");

        many.IsCloseTo(single, tolerance: 12).Should().BeTrue(
            $"both paths should agree, but got {many} against {single}");
    }

    [Fact]
    public void OpaqueGeometryStillHidesWhatIsBehindIt()
    {
        using Fixture fixture = Fixture.Create();

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        // Transparency tests depth without writing it. Losing the test would let a transparent
        // face show through the solid in front of it, which reads as the solid having a hole.
        Pixel behind = fixture.RenderTransparentBehindOpaque();
        Pixel opaqueOnly = fixture.RenderOpaqueOnly();

        behind.IsCloseTo(opaqueOnly, tolerance: 3).Should().BeTrue(
            $"the opaque body should hide it entirely, but got {behind} against {opaqueOnly}");
    }

    [Fact]
    public void NothingTransparentLeavesTheImageExactlyAsItWas()
    {
        using Fixture fixture = Fixture.Create();

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        // The composite runs whether or not anything was accumulated. A pixel no transparent
        // fragment touched must come out untouched rather than with a film of black over it,
        // which is what an unguarded divide by an empty accumulation produces.
        Pixel plain = fixture.RenderOpaqueOnly();
        Pixel composited = fixture.RenderOpaqueThenEmptyComposite();

        composited.IsCloseTo(plain, tolerance: 1).Should().BeTrue(
            $"an empty composite should change nothing, but got {composited} against {plain}");
    }

    // --- Fixture ------------------------------------------------------------------------------

    private sealed class Fixture : IDisposable
    {
        private readonly ID3D12Resource _composite = null!;

        private Fixture(string skipped) => Skipped = skipped;

        private Fixture(
            D3D12RenderDevice device,
            OffscreenSurface surface,
            MsaaTarget msaa,
            TransparencyTarget transparency,
            TransparencyPass pass,
            FacePass faces)
        {
            Device = device;
            Surface = surface;
            Msaa = msaa;
            Transparency = transparency;
            Pass = pass;
            Faces = faces;

            _composite = device.Device.CreateCommittedResource(
                HeapType.Upload, HeapFlags.None, ResourceDescription.Buffer(256),
                ResourceStates.GenericRead);
        }

        public string? Skipped { get; }

        public D3D12RenderDevice Device { get; } = null!;

        public OffscreenSurface Surface { get; } = null!;

        public MsaaTarget Msaa { get; } = null!;

        public TransparencyTarget Transparency { get; } = null!;

        public TransparencyPass Pass { get; } = null!;

        public FacePass Faces { get; } = null!;

        public Camera Camera { get; } = new();

        public static Fixture Create(int samples = 1)
        {
            D3D12RenderDevice? device = null;
            OffscreenSurface? surface = null;
            MsaaTarget? msaa = null;
            TransparencyTarget? transparency = null;
            TransparencyPass? pass = null;
            FacePass? faces = null;

            try
            {
                device = new D3D12RenderDevice(TestDevices.Software);
                surface = new OffscreenSurface(device, Size, Size);

                msaa = new MsaaTarget(device.Device, Clear, samples);
                msaa.Resize(Size, Size);

                transparency = new TransparencyTarget(device.Device, msaa.SampleCount);
                transparency.Resize(Size, Size);

                pass = new TransparencyPass(
                    device.Device, OffscreenSurface.ColourFormat, DepthBuffer.DepthFormat,
                    optimiseShaders: false, msaa.SampleCount);

                faces = new FacePass(
                    device.Device, OffscreenSurface.ColourFormat, DepthBuffer.DepthFormat,
                    optimiseShaders: false, msaa.SampleCount);

                Fixture fixture = new(device, surface, msaa, transparency, pass, faces);
                fixture.Camera.AspectRatio = 1.0;
                fixture.Camera.LookFrom(StandardView.Front);
                fixture.Camera.ZoomToFit(new Bounds3d(new Vec3d(-1, -2, -1), new Vec3d(1, 2, 1)));

                return fixture;
            }
            catch (Exception exception)
                when (exception is RenderDeviceUnavailableException or SharpGenException)
            {
                faces?.Dispose();
                pass?.Dispose();
                transparency?.Dispose();
                msaa?.Dispose();
                surface?.Dispose();
                device?.Dispose();

                return new Fixture($"No usable D3D12 device: {exception.Message}");
            }
        }

        public void RenderTransparent(float alpha)
        {
            using SceneGeometry scene = Upload(Vec3d.Zero);
            Draw(transparent: [(scene, alpha)], opaque: null);
        }

        public Pixel RenderTwoOverlapping(bool nearFirst)
        {
            using SceneGeometry near = Upload(new Vec3d(0, -1.0, 0));
            using SceneGeometry far = Upload(new Vec3d(0, 1.0, 0));

            (SceneGeometry Scene, float Alpha)[] order = nearFirst
                ? [(near, 0.5f), (far, 0.5f)]
                : [(far, 0.5f), (near, 0.5f)];

            Draw(order, opaque: null);
            return Surface.Centre();
        }

        public Pixel RenderTransparentBehindOpaque()
        {
            using SceneGeometry front = Upload(new Vec3d(0, -1.0, 0));
            using SceneGeometry behind = Upload(new Vec3d(0, 1.0, 0));

            Draw([(behind, 0.6f)], front);
            return Surface.Centre();
        }

        public Pixel RenderOpaqueOnly()
        {
            using SceneGeometry front = Upload(new Vec3d(0, -1.0, 0));

            Draw([], front);
            return Surface.Centre();
        }

        public Pixel RenderOpaqueThenEmptyComposite()
        {
            using SceneGeometry front = Upload(new Vec3d(0, -1.0, 0));
            using SceneGeometry nothing = Upload(new Vec3d(0, -1.0, 0));

            // Accumulate nothing at all, then composite anyway.
            Draw([(nothing, 0.0f)], front);
            return Surface.Centre();
        }

        public void Dispose()
        {
            _composite?.Dispose();
            Faces?.Dispose();
            Pass?.Dispose();
            Transparency?.Dispose();
            Msaa?.Dispose();
            Surface?.Dispose();
            Device?.Dispose();
        }

        private SceneGeometry Upload(Vec3d at)
        {
            SnapshotBuilder builder = new();
            builder.Add(Shift(EdgePassTestsGeometry.SolidBox(1.0), at));

            return SceneGeometry.Upload(Device, builder.Build(1, Vec3d.Zero));
        }

        private static Kernel.MeshBuffer Shift(Kernel.MeshBuffer mesh, Vec3d by)
            => mesh with { Positions = [.. mesh.Positions.Select(p => p + by)] };

        /// <summary>Draws an optional opaque body, then accumulates and composites transparency.</summary>
        private void Draw(
            IReadOnlyList<(SceneGeometry Scene, float Alpha)> transparent, SceneGeometry? opaque)
        {
            Bounds3d bounds = new(new Vec3d(-1, -2, -1), new Vec3d(1, 2, 1));
            Mat4d projection = Camera.ProjectionMatrix(bounds);

            Surface.SetConstants(new FrameConstants
            {
                ViewProjection = ToShaderMatrix(
                    projection * Mat4d.LookAt(Camera.Position, Camera.Target, Camera.Up)),
                CameraPosition = ToVector3(Camera.Position),
                LightDirection = ToVector3(FacePass.KeyLightDirection(Camera)),
                ViewportSize = new Vector2(Size, Size),
            });

            CompositeConstants composite = new()
            {
                Multisampled = Msaa.IsMultisampled ? 1u : 0u,
                SampleCount = (uint)Msaa.SampleCount,
            };

            ReadOnlySpan<CompositeConstants> one = new(in composite);
            _composite.SetData(MemoryMarshal.AsBytes(one));

            Surface.RenderTransparency(
                Msaa,
                Transparency,
                Clear,
                commands =>
                {
                    if (opaque is not null)
                    {
                        Faces.Draw(commands, opaque, Surface.ConstantBufferAddress);
                    }
                },
                commands =>
                {
                    foreach ((SceneGeometry scene, float alpha) in transparent)
                    {
                        Pass.Accumulate(
                            commands,
                            scene,
                            Surface.ConstantBufferAddress,
                            new Color4(0.75f, 0.72f, 0.68f, alpha));
                    }
                },
                commands => Pass.Composite(commands, Transparency, _composite.GPUVirtualAddress));
        }

        private static Matrix4x4 ToShaderMatrix(Mat4d m) => new(
            (float)m.M11, (float)m.M12, (float)m.M13, (float)m.M14,
            (float)m.M21, (float)m.M22, (float)m.M23, (float)m.M24,
            (float)m.M31, (float)m.M32, (float)m.M33, (float)m.M34,
            (float)m.M41, (float)m.M42, (float)m.M43, (float)m.M44);

        private static Vector3 ToVector3(Vec3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
    }
}
