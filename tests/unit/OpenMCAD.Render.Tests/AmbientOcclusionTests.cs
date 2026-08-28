using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;

using FluentAssertions;

using OpenMCAD.Kernel;
using OpenMCAD.Math;
using OpenMCAD.Render.Direct3D12;

using SharpGen.Runtime;

using Vortice.Direct3D12;
using Vortice.Mathematics;

using Xunit;

namespace OpenMCAD.Render.Tests;

/// <summary>
/// Screen-space ambient occlusion (P2-T12), rendered for real and read back.
/// </summary>
/// <remarks>
/// <para>
/// Occlusion is the render feature most able to look like it is working while doing nothing. It
/// darkens by a few percent, over a soft area, in exactly the places the shading is already dark —
/// so a screenshot is close to worthless as evidence, and so is any assertion that merely finds
/// some pixel somewhere that changed. Every test here therefore renders the same scene through the
/// same path twice, once with the darkening applied and once without, and asserts on the difference.
/// </para>
/// <para>
/// The interesting failures are not "nothing happened". They are "everything happened": a wrong
/// sign in the depth comparison darkens flat faces uniformly, a missing range cutoff traces a halo
/// around every silhouette, and a depth view built for the wrong sample count reads zeros and
/// blackens the frame. There is a test below for each.
/// </para>
/// </remarks>
public sealed class AmbientOcclusionTests
{

    [Fact]
    public void AConcaveCornerIsDarkened()
    {
        using Fixture fixture = Fixture.Create(160);

        if (fixture.Skipped is not null)
        {
            Assert.Skip(fixture.Skipped);
        }

        // A block sitting on a plate. The crease where they meet is the shape occlusion exists to
        // reveal: both surfaces face nearly the same way as the flat plate around them, so no light
        // direction distinguishes them and only enclosure does.
        using SceneGeometry scene = fixture.Upload(Plate(), Block());

        fixture.Frame(scene);

        Pixel[,] without = fixture.Capture(scene, apply: false);
        Pixel[,] with = fixture.Capture(scene, apply: true);

        // Sampled over the whole frame rather than at one hand-picked pixel: where the crease lands
        // on screen depends on the camera framing, and a test that hard-codes it breaks the first
        // time the default view changes for an unrelated reason.
        int darkened = 0;
        int lightened = 0;

        for (int y = 0; y < fixture.Size; ++y)
        {
            for (int x = 0; x < fixture.Size; ++x)
            {
                int difference = Luminance(without[x, y]) - Luminance(with[x, y]);

                if (difference > 4)
                {
                    darkened++;
                }
                else if (difference < -1)
                {
                    lightened++;
                }
            }
        }

        darkened.Should().BeGreaterThan(
            40, "the crease between the block and the plate should be visibly enclosed");

        lightened.Should().Be(
            0, "multiplying by occlusion can only ever darken, so anything brighter is a defect");

        // And not everywhere. Sampling a full sphere rather than the hemisphere the surface faces
        // counts a flat face as occluding itself, which dims the whole model like a badly exposed
        // photograph instead of marking what is enclosed.
        //
        // The bound is placed between the two measured outcomes rather than at a round number:
        // this scene darkens about 2,600 pixels when the hemisphere is right and about 6,700 when
        // it is not, so a sixth of the frame separates them with roughly equal margin either side.
        // A looser bound — half the frame, say — passes just as happily on both and tests nothing.
        darkened.Should().BeLessThan(
            fixture.Size * fixture.Size / 6,
            "occlusion should mark the enclosed parts, not dim the whole image");
    }

    [Fact]
    public void TheBackgroundIsUntouched()
    {
        using Fixture fixture = Fixture.Create(128);

        if (fixture.Skipped is not null)
        {
            Assert.Skip(fixture.Skipped);
        }

        using SceneGeometry scene = fixture.Upload(Plate(), Block());

        fixture.Frame(scene);
        fixture.Capture(scene, apply: true);

        // The corners cannot be covered by an isometric view of a square plate framed to fit.
        (int X, int Y)[] corners =
        [
            (0, 0),
            (fixture.Size - 1, 0),
            (0, fixture.Size - 1),
            (fixture.Size - 1, fixture.Size - 1),
        ];

        foreach ((int x, int y) in corners)
        {
            Pixel corner = fixture.Surface.At(x, y);

            corner.IsCloseTo(fixture.Background).Should().BeTrue(
                $"nothing was drawn at ({x}, {y}), so the occlusion pass must leave it alone, "
                + $"but it is {corner} rather than {fixture.Background}");
        }
    }

    [Fact]
    public void NoIntensityChangesNothing()
    {
        using Fixture fixture = Fixture.Create(96);

        if (fixture.Skipped is not null)
        {
            Assert.Skip(fixture.Skipped);
        }

        using SceneGeometry scene = fixture.Upload(Plate(), Block());

        fixture.Frame(scene, OcclusionStyle.Default with { Intensity = 0 });

        Pixel[,] applied = fixture.Capture(scene, apply: true);
        Pixel[,] skipped = fixture.Capture(scene, apply: false);

        for (int y = 0; y < fixture.Size; ++y)
        {
            for (int x = 0; x < fixture.Size; ++x)
            {
                applied[x, y].IsCloseTo(skipped[x, y], tolerance: 1).Should().BeTrue(
                    $"at zero intensity every pixel must survive the multiply, but ({x}, {y}) is "
                    + $"{applied[x, y]} with the pass and {skipped[x, y]} without it");
            }
        }
    }

    [Fact]
    public void ADistantBackdropIsNotHaloed()
    {
        using Fixture fixture = Fixture.Create(160);

        if (fixture.Skipped is not null)
        {
            Assert.Skip(fixture.Skipped);
        }

        // A small block a long way in front of a large wall. Nothing here is enclosed: the two are
        // separated by far more than any occlusion radius. Without the range cutoff the block still
        // occludes the wall, because the wall's pixels find the block in front of them and have no
        // way to tell "in front of me" from "close to me" — and the result is a dark outline
        // tracing the block onto the wall behind it.
        using SceneGeometry scene = fixture.Upload(Wall(), Cube(new Vec3d(0, 0, 4.0), 1.0));

        fixture.Frame(scene);

        Pixel[,] without = fixture.Capture(scene, apply: false);
        Pixel[,] with = fixture.Capture(scene, apply: true);

        int worst = 0;

        for (int y = 0; y < fixture.Size; ++y)
        {
            for (int x = 0; x < fixture.Size; ++x)
            {
                worst = System.Math.Max(worst, Luminance(without[x, y]) - Luminance(with[x, y]));
            }
        }

        // Some darkening is expected — the block's own edges are legitimately occluded, and the
        // reconstructed normals are unreliable right across a silhouette. What must not happen is
        // the wall going appreciably dark.
        worst.Should().BeLessThan(
            24,
            "geometry separated by far more than the occlusion radius must not shadow the "
            + "background behind it, and a halo around the silhouette is how that failure looks");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void OcclusionSurvivesEitherSampleCount(int samples)
    {
        using Fixture fixture = Fixture.Create(128, samples);

        if (fixture.Skipped is not null)
        {
            Assert.Skip(fixture.Skipped);
        }

        if (fixture.SampleCount != samples)
        {
            Assert.Skip($"The device offers no {samples}x mode; it negotiated {fixture.SampleCount}.");
        }

        using SceneGeometry scene = fixture.Upload(Plate(), Block());

        fixture.Frame(scene);

        Pixel[,] without = fixture.Capture(scene, apply: false);
        Pixel[,] with = fixture.Capture(scene, apply: true);

        int darkened = 0;
        int black = 0;

        for (int y = 0; y < fixture.Size; ++y)
        {
            for (int x = 0; x < fixture.Size; ++x)
            {
                if (Luminance(without[x, y]) - Luminance(with[x, y]) > 4)
                {
                    darkened++;
                }

                if (Luminance(with[x, y]) < 4)
                {
                    black++;
                }
            }
        }

        // The depth buffer needs a multisampled view when it is multisampled and a plain one when
        // it is not, and getting that wrong does not fail: it reads zeros. Zero depth is the near
        // plane, so every pixel believes it is buried and the image goes black. That is what the
        // second count is for — the first would pass happily on a completely black frame.
        darkened.Should().BeGreaterThan(
            20, $"occlusion should still find the crease at {samples}x");

        black.Should().BeLessThan(
            fixture.Size * fixture.Size / 20,
            $"at {samples}x the depth buffer is being read as zeros, which buries the whole scene");
    }

    [Fact]
    public void TheRadiusFollowsTheScene()
    {
        // Not a rendering test: purely that the same model, measured in millimetres and in metres,
        // gets an occlusion radius that is the same fraction of it. A fixed radius in metres is the
        // usual mistake, and it makes the effect vanish on a small part and swamp a large one.
        Bounds3d small = new(new Vec3d(0, 0, 0), new Vec3d(0.01, 0.01, 0.01));
        Bounds3d large = new(new Vec3d(0, 0, 0), new Vec3d(10, 10, 10));

        OcclusionStyle forSmall = OcclusionStyle.Default.ForScene(small);
        OcclusionStyle forLarge = OcclusionStyle.Default.ForScene(large);

        forLarge.Radius.Should().BeGreaterThan(
            forSmall.Radius, "a larger part should be sampled over a larger distance");

        double ratio = large.DiagonalLength / small.DiagonalLength;

        (forLarge.Radius / forSmall.Radius).Should().BeApproximately(
            (float)ratio,
            (float)(0.05 * ratio),
            "the radius should scale with the model rather than being anchored to a unit");
    }

    [Fact]
    public void ADegenerateSceneDoesNotProduceADegenerateRadius()
    {
        // A single point, or a sheet seen exactly edge-on, has a zero diagonal. Scaling by it gives
        // a zero radius, and every sample then lands back on the pixel it started from — which is
        // not a crash but a uniform grey wash over the model.
        Bounds3d point = new(new Vec3d(1, 2, 3), new Vec3d(1, 2, 3));

        OcclusionStyle style = OcclusionStyle.Default.ForScene(point);

        style.Radius.Should().BeGreaterThan(0, "a zero radius samples nothing but its own pixel");
    }

    // --- Fixtures -----------------------------------------------------------------------------

    /// <summary>Perceived brightness, for comparing two renders of the same scene.</summary>
    /// <remarks>
    /// Rec. 601 weights. Occlusion multiplies all three channels equally, so any weighting would
    /// do; this one keeps the numbers close to what the difference looks like.
    /// </remarks>
    private static int Luminance(Pixel pixel)
        => (int)System.Math.Round((0.299 * pixel.R) + (0.587 * pixel.G) + (0.114 * pixel.B));

    /// <summary>A wide flat slab for things to sit on.</summary>
    private static MeshBuffer Plate() => Box(new Vec3d(0, 0, -0.5), new Vec3d(6, 6, 1));

    /// <summary>A block resting on the plate, making a crease all the way round its base.</summary>
    private static MeshBuffer Block() => Box(new Vec3d(0, 0, 1), new Vec3d(2, 2, 2));

    /// <summary>A large backdrop, for the silhouette test.</summary>
    private static MeshBuffer Wall() => Box(new Vec3d(0, 0, -0.5), new Vec3d(12, 12, 1));

    private static MeshBuffer Cube(Vec3d centre, double size)
        => Box(centre, new Vec3d(size, size, size));

    /// <summary>An axis-aligned box, four vertices per face so the normals stay flat.</summary>
    private static MeshBuffer Box(Vec3d centre, Vec3d size)
    {
        Vec3d h = new(size.X / 2, size.Y / 2, size.Z / 2);

        (Vec3d Normal, Vec3d U, Vec3d V)[] faces =
        [
            (Vec3d.UnitX, Vec3d.UnitY, Vec3d.UnitZ),
            (-Vec3d.UnitX, Vec3d.UnitZ, Vec3d.UnitY),
            (Vec3d.UnitY, Vec3d.UnitZ, Vec3d.UnitX),
            (-Vec3d.UnitY, Vec3d.UnitX, Vec3d.UnitZ),
            (Vec3d.UnitZ, Vec3d.UnitX, Vec3d.UnitY),
            (-Vec3d.UnitZ, Vec3d.UnitY, Vec3d.UnitX),
        ];

        ImmutableArray<Vec3d>.Builder positions = ImmutableArray.CreateBuilder<Vec3d>();
        ImmutableArray<Vec3d>.Builder normals = ImmutableArray.CreateBuilder<Vec3d>();
        ImmutableArray<int>.Builder indices = ImmutableArray.CreateBuilder<int>();
        ImmutableArray<int>.Builder triangleFaces = ImmutableArray.CreateBuilder<int>();
        ImmutableArray<SubEntity>.Builder subEntities = ImmutableArray.CreateBuilder<SubEntity>();

        for (int face = 0; face < faces.Length; ++face)
        {
            (Vec3d normal, Vec3d u, Vec3d v) = faces[face];

            // Scaled per axis, so the half extent along each edge of a face is the box's own along
            // that axis. Using one scalar half-extent, as a cube builder can, silently produces a
            // cube whatever size was asked for.
            Vec3d offset = new(normal.X * h.X, normal.Y * h.Y, normal.Z * h.Z);
            Vec3d origin = centre + offset;

            Vec3d du = new(u.X * h.X, u.Y * h.Y, u.Z * h.Z);
            Vec3d dv = new(v.X * h.X, v.Y * h.Y, v.Z * h.Z);

            int baseIndex = positions.Count;

            positions.Add(origin - du - dv);
            positions.Add(origin + du - dv);
            positions.Add(origin + du + dv);
            positions.Add(origin - du + dv);

            for (int i = 0; i < 4; ++i)
            {
                normals.Add(normal);
            }

            indices.AddRange(baseIndex, baseIndex + 1, baseIndex + 2);
            indices.AddRange(baseIndex, baseIndex + 2, baseIndex + 3);
            triangleFaces.AddRange(face, face);
            subEntities.Add(new SubEntity(new KernelShape(1), (ulong)(face + 1), SubEntityKind.Face));
        }

        return new MeshBuffer(
            positions.ToImmutable(),
            normals.ToImmutable(),
            indices.ToImmutable(),
            triangleFaces.ToImmutable(),
            subEntities.ToImmutable());
    }

    /// <summary>A device, a surface, a multisampled target and the two passes.</summary>
    private sealed class Fixture : IDisposable
    {
        private ID3D12Resource? _constants;

        private Fixture(string skipped) => Skipped = skipped;

        private Fixture(
            D3D12RenderDevice device,
            OffscreenSurface surface,
            MsaaTarget msaa,
            FacePass faces,
            AmbientOcclusionPass occlusion,
            int size)
        {
            Device = device;
            Surface = surface;
            Msaa = msaa;
            Faces = faces;
            Occlusion = occlusion;
            Size = size;
        }

        public string? Skipped { get; }

        public D3D12RenderDevice Device { get; } = null!;

        public OffscreenSurface Surface { get; } = null!;

        public MsaaTarget Msaa { get; } = null!;

        public FacePass Faces { get; } = null!;

        public AmbientOcclusionPass Occlusion { get; } = null!;

        public Camera Camera { get; } = new();

        public int Size { get; }

        public int SampleCount => Msaa.SampleCount;

        public Color4 Clear { get; } = new(0.05f, 0.05f, 0.08f, 1.0f);

        public Pixel Background => new(
            (byte)System.Math.Round(Clear.R * 255),
            (byte)System.Math.Round(Clear.G * 255),
            (byte)System.Math.Round(Clear.B * 255),
            (byte)System.Math.Round(Clear.A * 255));

        public static Fixture Create(int size, int samples = 1)
        {
            D3D12RenderDevice? device = null;
            OffscreenSurface? surface = null;
            MsaaTarget? msaa = null;
            FacePass? faces = null;

            try
            {
                device = new D3D12RenderDevice(TestDevices.Software);
                surface = new OffscreenSurface(device, size, size);

                msaa = new MsaaTarget(
                    device.Device, new Color4(0.05f, 0.05f, 0.08f, 1.0f), samples);

                msaa.Resize(size, size);

                faces = new FacePass(
                    device.Device,
                    OffscreenSurface.ColourFormat,
                    DepthBuffer.DepthFormat,
                    optimiseShaders: false,
                    sampleCount: msaa.SampleCount);

                AmbientOcclusionPass occlusion = new(
                    device.Device,
                    OffscreenSurface.ColourFormat,
                    optimiseShaders: false,
                    applySampleCount: msaa.SampleCount);

                occlusion.Resize(size, size, msaa.Depth, msaa.SampleCount);

                return new Fixture(device, surface, msaa, faces, occlusion, size);
            }
            catch (Exception exception)
                when (exception is RenderDeviceUnavailableException or SharpGenException)
            {
                // A build agent with no D3D12 at all, or a WARP that will not create a pipeline
                // state. Skipped rather than failed: this asserts on the pass, and "there is no GPU
                // here" is not a defect in the pass.
                faces?.Dispose();
                msaa?.Dispose();
                surface?.Dispose();
                device?.Dispose();

                return new Fixture($"No usable D3D12 device: {exception.Message}");
            }
        }

        /// <summary>Uploads meshes as a single scene.</summary>
        public SceneGeometry Upload(params MeshBuffer[] meshes)
        {
            SnapshotBuilder builder = new();

            foreach (MeshBuffer mesh in meshes)
            {
                builder.Add(mesh);
            }

            return SceneGeometry.Upload(Device, builder.Build(1));
        }

        /// <summary>Frames the scene isometrically and writes both sets of constants.</summary>
        /// <param name="scene">What is being framed.</param>
        /// <param name="style">How strong the occlusion should be.</param>
        /// <remarks>
        /// The frame constants and the occlusion constants have to describe the same camera, and
        /// they are written together here so a test cannot move one without the other. They live in
        /// separate buffers because the occlusion pass runs under its own root signature.
        /// </remarks>
        public void Frame(SceneGeometry scene, OcclusionStyle? style = null)
        {
            Camera.AspectRatio = 1.0;
            Camera.LookFrom(StandardView.Isometric);
            Camera.ZoomToFit(scene.Bounds);

            Mat4d projection = Camera.ProjectionMatrix(scene.Bounds);
            Vec3d origin = scene.Origin;

            Surface.SetConstants(new FrameConstants
            {
                ViewProjection = ToShaderMatrix(
                    projection * Mat4d.LookAt(
                        Camera.Position - origin, Camera.Target - origin, Camera.Up)),
                CameraPosition = ToVector3(Camera.Position - origin),
                LightDirection = ToVector3(FacePass.KeyLightDirection(Camera)),
            });

            OcclusionConstants constants = AmbientOcclusionPass.ConstantsFor(
                Camera,
                scene.Bounds,
                Size,
                Size,
                (style ?? OcclusionStyle.Default).ForScene(scene.Bounds));

            _constants ??= Device.Device.CreateCommittedResource(
                HeapType.Upload,
                HeapFlags.None,
                ResourceDescription.Buffer(256),
                ResourceStates.GenericRead);

            ReadOnlySpan<OcclusionConstants> one = new(in constants);
            _constants.SetData(MemoryMarshal.AsBytes(one));
        }

        /// <summary>Renders the scene and returns every pixel.</summary>
        /// <param name="scene">What to draw.</param>
        /// <param name="apply">Whether to darken with the occlusion.</param>
        /// <returns>The frame, indexed by column then row.</returns>
        public Pixel[,] Capture(SceneGeometry scene, bool apply)
        {
            Surface.RenderOcclusion(
                Msaa,
                Occlusion,
                Clear,
                _constants!.GPUVirtualAddress,
                commands => Faces.Draw(commands, scene, Surface.ConstantBufferAddress, frustum: null),
                apply);

            Pixel[,] pixels = new Pixel[Size, Size];

            for (int y = 0; y < Size; ++y)
            {
                for (int x = 0; x < Size; ++x)
                {
                    pixels[x, y] = Surface.At(x, y);
                }
            }

            return pixels;
        }

        public void Dispose()
        {
            _constants?.Dispose();
            Occlusion?.Dispose();
            Faces?.Dispose();
            Msaa?.Dispose();
            Surface?.Dispose();
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
