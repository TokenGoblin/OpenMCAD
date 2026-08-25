using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;

using FluentAssertions;

using OpenMCAD.Kernel;
using OpenMCAD.Math;
using OpenMCAD.Render.Direct3D12;

using SharpGen.Runtime;

using Vortice.Mathematics;

using Xunit;

namespace OpenMCAD.Render.Tests;

/// <summary>
/// The shaded face pass (P2-T05), rendered for real and read back pixel by pixel.
/// </summary>
/// <remarks>
/// <para>
/// Every test here renders on the WARP software adapter and inspects the framebuffer. That is the
/// only kind of assertion worth making about a render pass: the D3D12 calls all report success
/// whether or not the matrix was transposed, the depth test was lost or the geometry ended up
/// behind the camera, and the pixels are the only place the difference shows.
/// </para>
/// <para>
/// The colours asserted on are ranges rather than values. WARP is not bit-identical to hardware
/// and hardware is not bit-identical between vendors, so an exact-byte assertion would be a test
/// that passes on the machine it was written on.
/// </para>
/// </remarks>
public sealed class FacePassTests
{
    private const int Size = 160;

    private static RenderDeviceOptions Software => new(EnableDebugLayer: true, ForceSoftware: true);

    // --- The shader itself --------------------------------------------------------------------

    [Fact]
    public void TheSurfaceShaderIsEmbeddedAndCompiles()
    {
        // Compiled with no device at all: a shader error should be reported as a shader error,
        // not as a failure to create a pipeline state.
        ReadOnlyMemory<byte> vertex = ShaderLibrary.Compile(
            FacePass.ShaderFile, "VSMain", ShaderLibrary.VertexProfile, optimise: false);

        ReadOnlyMemory<byte> pixel = ShaderLibrary.Compile(
            FacePass.ShaderFile, "PSMain", ShaderLibrary.PixelProfile, optimise: false);

        vertex.Length.Should().BeGreaterThan(0);
        pixel.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void AMissingShaderNamesWhatIsActuallyEmbedded()
    {
        // The usual cause is a csproj that stopped picking the file up, and a bare "not found"
        // sends you looking in the wrong place entirely.
        Action act = () => ShaderLibrary.Source("NoSuchShader.hlsl");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Surface.hlsl*");
    }

    [Fact]
    public void AShaderErrorReportsTheCompilerMessage()
    {
        Action act = () => ShaderLibrary.Compile(
            FacePass.ShaderFile, "ThereIsNoSuchEntryPoint", ShaderLibrary.PixelProfile);

        act.Should().Throw<ShaderCompilationException>()
            .WithMessage("*ThereIsNoSuchEntryPoint*");
    }

    [Fact]
    public void TheFrameConstantsMatchTheShaderPacking()
    {
        // HLSL lets a shader declare a *prefix* of a constant buffer, so a mismatch does not fail
        // to compile: it silently reads the wrong offsets, and the symptom is colour or geometry
        // appearing somewhere unexpected. Surface.hlsl and Edges.hlsl had already drifted apart
        // once before they were made to share one declaration.
        //
        // The offsets are pinned rather than just the total, because two fields swapping places
        // leaves the size unchanged.
        FrameConstants.SizeInBytes.Should().Be(160);

        Marshal.OffsetOf<FrameConstants>(nameof(FrameConstants.ViewProjection)).ToInt32()
            .Should().Be(0);

        Marshal.OffsetOf<FrameConstants>(nameof(FrameConstants.CameraPosition)).ToInt32()
            .Should().Be(64);

        Marshal.OffsetOf<FrameConstants>(nameof(FrameConstants.LightDirection)).ToInt32()
            .Should().Be(80);

        Marshal.OffsetOf<FrameConstants>(nameof(FrameConstants.ViewportSize)).ToInt32()
            .Should().Be(96);

        Marshal.OffsetOf<FrameConstants>(nameof(FrameConstants.HighlightCount)).ToInt32()
            .Should().Be(104);

        Marshal.OffsetOf<FrameConstants>(nameof(FrameConstants.PreSelectedColour)).ToInt32()
            .Should().Be(112);

        Marshal.OffsetOf<FrameConstants>(nameof(FrameConstants.SelectedColour)).ToInt32()
            .Should().Be(128);

        Marshal.OffsetOf<FrameConstants>(nameof(FrameConstants.ErrorColour)).ToInt32()
            .Should().Be(144);
    }

    // --- Rendering ----------------------------------------------------------------------------

    [Fact]
    public void ACubeIsDrawnShadedAgainstTheBackground()
    {
        using Fixture fixture = Fixture.Create(Size);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        fixture.Draw(Cube(Vec3d.Zero, 1.0));

        Pixel background = fixture.Background;

        fixture.Surface.Centre().IsCloseTo(background).Should().BeFalse(
            "the middle of a framed cube must not still be the background colour");

        // A corner. Zoom-to-fit frames the bounding sphere with a margin, so the cube cannot reach
        // the corners of the image -- if it has, the projection is wrong rather than merely tight.
        fixture.Surface.At(2, 2).IsCloseTo(background).Should().BeTrue(
            "a framed cube should not fill the viewport to its corners");

        int covered = fixture.Surface.CountDifferingFrom(background);
        int total = Size * Size;

        covered.Should().BeInRange(
            total / 20,
            total * 3 / 4,
            "the cube should cover a sensible share of the image, neither a speck nor everything");
    }

    [Fact]
    public void TheThreeVisibleFacesOfACubeAreShadedDifferently()
    {
        using Fixture fixture = Fixture.Create(Size);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        fixture.Draw(Cube(Vec3d.Zero, 1.0));

        // An isometric view shows exactly three faces. If lighting were flat, or the normals were
        // dropped somewhere between the mesh and the input assembler, they would all come back the
        // same colour and this is the only thing that would notice.
        fixture.Surface.DistinctColours(fixture.Background).Should().BeGreaterThanOrEqualTo(
            3, "three faces at three orientations must not shade identically");
    }

    [Fact]
    public void AMeshWithoutNormalsIsStillShaded()
    {
        using Fixture fixture = Fixture.Create(Size);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        // Normals are optional on a MeshBuffer, so the shader reconstructs the facet normal from
        // screen-space derivatives when it finds a zero one. Without that fallback this renders
        // as a black silhouette, which reads as a modelling error rather than a missing attribute.
        MeshBuffer cube = Cube(Vec3d.Zero, 1.0);
        fixture.Draw(cube with { Normals = [] });

        fixture.Surface.Centre().IsCloseTo(fixture.Background).Should().BeFalse();

        fixture.Surface.DistinctColours(fixture.Background).Should().BeGreaterThanOrEqualTo(
            3, "flat shading from derivatives should still distinguish the three visible faces");
    }

    [Fact]
    public void TheNearerBodyHidesTheFurtherWhicheverOrderTheyAreDrawn()
    {
        using Fixture fixture = Fixture.Create(Size);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        // Two cubes on the view axis. Drawing the near one last is the easy case; drawing it first
        // is the one that fails when the depth buffer is not bound, not cleared, or not written --
        // and that failure looks like nothing at all until a model has two overlapping parts.
        //
        // Both are pinned to the same snapshot origin. Two snapshots each pick the origin nearest
        // their own contents, and a single set of frame constants can only carry one of them --
        // the second body would then be drawn shifted by the difference, which lands it on top of
        // the first and makes the later draw appear to win no matter what the depth buffer does.
        using SceneGeometry near = fixture.Upload(Cube(new Vec3d(0, -1, 0), 1.0), Vec3d.Zero);
        using SceneGeometry far = fixture.Upload(Cube(new Vec3d(0, 1, 0), 1.0), Vec3d.Zero);

        near.Origin.Should().Be(far.Origin, "the two scenes must share a frame for this to mean anything");

        Camera camera = fixture.Camera;
        camera.LookFrom(StandardView.Front);
        camera.ZoomToFit(Bounds3d.Union(near.Bounds, far.Bounds));

        Color4 nearColour = new(0.9f, 0.1f, 0.1f, 1.0f);
        Color4 farColour = new(0.1f, 0.1f, 0.9f, 1.0f);

        Pixel nearLast = fixture.RenderTwo(far, farColour, near, nearColour);
        Pixel nearFirst = fixture.RenderTwo(near, nearColour, far, farColour);

        nearFirst.IsCloseTo(nearLast).Should().BeTrue(
            $"draw order must not decide what is visible, but got {nearFirst} then {nearLast}");

        nearLast.R.Should().BeGreaterThan(
            nearLast.B, "the red cube is nearer, so red should survive the depth test");
    }

    [Fact]
    public void ABodyOutsideTheFrustumIsCulledRatherThanDrawn()
    {
        using Fixture fixture = Fixture.Create(Size);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        using SceneGeometry scene = fixture.Upload(Cube(Vec3d.Zero, 1.0));

        Camera camera = fixture.Camera;
        camera.LookFrom(StandardView.Front);
        camera.ZoomToFit(scene.Bounds);

        // Look somewhere else entirely. Orbiting would not do it: an orbit turns the camera about
        // its target, so the cube stays centred in view however far it goes round.
        camera.Target = new Vec3d(500, 0, 0);

        fixture.Render(scene, camera);

        fixture.Pass.BodiesCulled.Should().Be(1);
        fixture.Pass.BodiesDrawn.Should().Be(0);
        fixture.Surface.Centre().IsCloseTo(fixture.Background).Should().BeTrue(
            "nothing was drawn, so the image should be exactly the clear colour");
    }

    [Fact]
    public void AnEmptySceneDrawsNothingAndDoesNotThrow()
    {
        using Fixture fixture = Fixture.Create(Size);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        SnapshotBuilder builder = new();
        using SceneGeometry scene = SceneGeometry.Upload(fixture.Device, builder.Build(1));

        fixture.Render(scene, fixture.Camera);

        fixture.Pass.BodiesDrawn.Should().Be(0);
        fixture.Surface.Centre().IsCloseTo(fixture.Background).Should().BeTrue();
    }

    [Fact]
    public void ACubeFarFromTheOriginRendersIdenticallyToOneAtIt()
    {
        using Fixture fixture = Fixture.Create(Size);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        // The whole point of the snapshot origin: a part modelled a kilometre out must look the
        // same as one at the origin. If the origin shift were dropped, or applied to the geometry
        // instead of the camera, float precision would visibly break this up.
        fixture.Draw(Cube(Vec3d.Zero, 1.0));
        Pixel atOrigin = fixture.Surface.Centre();
        int coveredAtOrigin = fixture.Surface.CountDifferingFrom(fixture.Background);

        fixture.Draw(Cube(new Vec3d(1000, -2000, 500), 1.0));
        Pixel farAway = fixture.Surface.Centre();
        int coveredFarAway = fixture.Surface.CountDifferingFrom(fixture.Background);

        farAway.IsCloseTo(atOrigin).Should().BeTrue(
            $"distance from the origin must not change shading, but got {farAway} vs {atOrigin}");

        coveredFarAway.Should().BeCloseTo(coveredAtOrigin, 32);
    }

    // --- Fixtures -----------------------------------------------------------------------------

    /// <summary>An axis-aligned cube, four vertices per face so the normals stay flat.</summary>
    private static MeshBuffer Cube(Vec3d centre, double size)
    {
        double h = size / 2;

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
            Vec3d origin = centre + (normal * h);
            int baseIndex = positions.Count;

            positions.Add(origin - (u * h) - (v * h));
            positions.Add(origin + (u * h) - (v * h));
            positions.Add(origin + (u * h) + (v * h));
            positions.Add(origin - (u * h) + (v * h));

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

    /// <summary>A device, a surface and a pass, or a reason there is none.</summary>
    private sealed class Fixture : IDisposable
    {
        private Fixture(string skipped) => Skipped = skipped;

        private Fixture(D3D12RenderDevice device, OffscreenSurface surface, FacePass pass)
        {
            Device = device;
            Surface = surface;
            Pass = pass;
        }

        public string? Skipped { get; }

        public D3D12RenderDevice Device { get; } = null!;

        public OffscreenSurface Surface { get; } = null!;

        public FacePass Pass { get; } = null!;

        public Camera Camera { get; } = new();

        public Color4 Clear { get; } = new(0.05f, 0.05f, 0.08f, 1.0f);

        /// <summary>The clear colour as it comes back out of the framebuffer.</summary>
        /// <remarks>
        /// Derived from <see cref="Clear"/> rather than written out, so the two cannot drift. The
        /// conversion is a plain scale because the target format is UNorm rather than sRGB; were
        /// it sRGB, every one of these comparisons would be off by the gamma curve.
        /// </remarks>
        public Pixel Background => new(
            (byte)System.Math.Round(Clear.R * 255),
            (byte)System.Math.Round(Clear.G * 255),
            (byte)System.Math.Round(Clear.B * 255),
            (byte)System.Math.Round(Clear.A * 255));

        public static Fixture Create(int size)
        {
            D3D12RenderDevice? device = null;
            OffscreenSurface? surface = null;

            try
            {
                device = new D3D12RenderDevice(Software);
                surface = new OffscreenSurface(device, size, size);
                FacePass pass = new(device.Device, OffscreenSurface.ColourFormat, optimiseShaders: false);

                return new Fixture(device, surface, pass);
            }
            catch (Exception exception)
                when (exception is RenderDeviceUnavailableException or SharpGenException)
            {
                // A build agent with no D3D12 at all, or a WARP that will not create a pipeline
                // state. Skipped rather than failed: this asserts on the pass, and "there is no
                // GPU here" is not a defect in the pass.
                surface?.Dispose();
                device?.Dispose();

                return new Fixture($"No usable D3D12 device: {exception.Message}");
            }
        }

        /// <summary>Uploads a mesh as a one-body scene.</summary>
        /// <param name="mesh">The geometry.</param>
        /// <param name="pinnedOrigin">
        /// An origin to hold on to, for tests that draw two scenes through one set of frame
        /// constants and so need both expressed in the same frame.
        /// </param>
        public SceneGeometry Upload(MeshBuffer mesh, Vec3d? pinnedOrigin = null)
        {
            SnapshotBuilder builder = new();
            builder.Add(mesh);

            return SceneGeometry.Upload(Device, builder.Build(1, pinnedOrigin));
        }

        /// <summary>Uploads a mesh, frames it isometrically and renders it.</summary>
        public void Draw(MeshBuffer mesh)
        {
            using SceneGeometry scene = Upload(mesh);

            Camera.LookFrom(StandardView.Isometric);
            Camera.ZoomToFit(scene.Bounds);

            Render(scene, Camera);
        }

        /// <summary>Renders one scene through a camera.</summary>
        public void Render(SceneGeometry scene, Camera camera)
        {
            camera.AspectRatio = (double)Surface.Width / Surface.Height;

            Mat4d projection = camera.ProjectionMatrix(scene.Bounds);
            Vec3d origin = scene.Origin;

            Surface.SetConstants(new FrameConstants
            {
                ViewProjection = ToShaderMatrix(
                    projection * Mat4d.LookAt(
                        camera.Position - origin, camera.Target - origin, camera.Up)),
                CameraPosition = ToVector3(camera.Position - origin),
                LightDirection = ToVector3(FacePass.KeyLightDirection(camera)),
            });

            Frustum frustum = Frustum.FromViewProjection(projection * camera.ViewMatrix());

            Surface.Render(
                Clear,
                commands => Pass.Draw(commands, scene, Surface.ConstantBufferAddress, frustum));
        }

        /// <summary>Renders two scenes in the order given and returns the centre pixel.</summary>
        public Pixel RenderTwo(
            SceneGeometry first, Color4 firstColour, SceneGeometry second, Color4 secondColour)
        {
            Camera.AspectRatio = (double)Surface.Width / Surface.Height;

            Bounds3d bounds = Bounds3d.Union(first.Bounds, second.Bounds);
            Mat4d projection = Camera.ProjectionMatrix(bounds);
            Vec3d origin = first.Origin;

            Surface.SetConstants(new FrameConstants
            {
                ViewProjection = ToShaderMatrix(
                    projection * Mat4d.LookAt(
                        Camera.Position - origin, Camera.Target - origin, Camera.Up)),
                CameraPosition = ToVector3(Camera.Position - origin),
                LightDirection = ToVector3(FacePass.KeyLightDirection(Camera)),
            });

            Surface.Render(Clear, commands =>
            {
                Pass.Draw(commands, first, Surface.ConstantBufferAddress, frustum: null, firstColour);
                Pass.Draw(commands, second, Surface.ConstantBufferAddress, frustum: null, secondColour);
            });

            return Surface.Centre();
        }

        public void Dispose()
        {
            Pass?.Dispose();
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
