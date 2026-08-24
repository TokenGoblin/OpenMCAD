using System.Collections.Immutable;
using System.Numerics;

using FluentAssertions;

using OpenMCAD.Kernel;
using OpenMCAD.Math;
using OpenMCAD.Render.Direct3D12;

using SharpGen.Runtime;

using Vortice.Mathematics;

using Xunit;

namespace OpenMCAD.Render.Tests;

/// <summary>
/// The edge pass (P2-T06), rendered on WARP and read back.
/// </summary>
/// <remarks>
/// Edges are the part of a CAD viewport a user actually reads, and almost every way of getting
/// them wrong still produces a picture: too thin to see, z-fighting with the surface they lie on,
/// scaling with distance, or whipping across the screen when a vertex passes behind the eye. None
/// of those is visible from a return value.
/// </remarks>
public sealed class EdgePassTests
{
    private const int Size = 200;

    private static RenderDeviceOptions Software => new(EnableDebugLayer: true, ForceSoftware: true);

    [Fact]
    public void TheEdgeShaderIsEmbeddedAndCompiles()
    {
        ShaderLibrary.Compile(EdgePass.ShaderFile, "VSMain", ShaderLibrary.VertexProfile, false)
            .Length.Should().BeGreaterThan(0);

        ShaderLibrary.Compile(EdgePass.ShaderFile, "PSMain", ShaderLibrary.PixelProfile, false)
            .Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void TheEdgeConstantsFitTheRootSignature()
    {
        // Eight 32-bit values. If the struct grows past what the root signature declares, the
        // driver reads past the end of what was pushed and the line colour becomes noise.
        EdgeConstants.RootConstantCount.Should().Be(8);
    }

    [Fact]
    public void PolylinesBecomeOneSegmentPerSpan()
    {
        // Two polylines: a four-point open line and a two-point one. Five points, four segments.
        DisplayEdges edges = new(
            [0, 0, 0, 1, 0, 0, 2, 0, 0, 3, 0, 0, 10, 0, 0, 11, 0, 0],
            [0, 4],
            [4, 2],
            [new DisplayId(1), new DisplayId(2)]);

        EdgeSegments segments = SceneGeometry.SegmentsOf(edges);

        segments.Points.Length.Should().Be(
            4 * 6, "three spans in the first polyline and one in the second");

        // The first segment runs from the first point to the second.
        segments.Points[0].Should().Be(0);
        segments.Points[3].Should().Be(1);

        // The last comes from the second polyline, which starts at point index four.
        segments.Points[18].Should().Be(10);
        segments.Points[21].Should().Be(11);
    }

    [Fact]
    public void ADegeneratePolylineOfOnePointProducesNoSegment()
    {
        // The kernel should not emit one, but a sick model must not take the viewport down.
        DisplayEdges edges = new([0, 0, 0], [0], [1], [new DisplayId(1)]);

        SceneGeometry.SegmentsOf(edges).Count.Should().Be(0);
    }

    // --- Rendering ----------------------------------------------------------------------------

    [Fact]
    public void EdgesAreDrawnAndAreDarkerThanTheFacesTheyBound()
    {
        using Fixture fixture = Fixture.Create(Size);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        fixture.Draw(WireBox(1.0), withFaces: true, withEdges: false);
        int withoutEdges = fixture.Surface.CountDifferingFrom(fixture.Background);

        fixture.Draw(WireBox(1.0), withFaces: true, withEdges: true);
        int withEdges = fixture.Surface.CountDifferingFrom(fixture.Background);

        // Edges extend a shaded body's footprint slightly, and more importantly they must exist.
        fixture.Pass.SegmentsDrawn.Should().BeGreaterThan(0);
        withEdges.Should().BeGreaterThan(
            withoutEdges - 1, "drawing edges must not remove coverage");

        // Somewhere along the silhouette there must be a pixel darker than any shaded face.
        fixture.DarkestPixel().Should().BeLessThan(
            110, "an edge should read as a dark line against a light face");
    }

    [Fact]
    public void EdgesSurviveTheDepthBufferTheyLieOn()
    {
        using Fixture fixture = Fixture.Create(Size);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        // The whole difficulty of this pass. Tessellated edges lie exactly on the surface they
        // bound, so with no depth bias they z-fight and come out stippled -- present in the count,
        // and useless on screen. Rendering with the bias removed must measurably lose edge pixels.
        fixture.Draw(WireBox(1.0), withFaces: true, withEdges: true);
        int biased = fixture.CountDarkerThan(110);

        fixture.Draw(WireBox(1.0), withFaces: true, withEdges: true, depthBias: 0f);
        int unbiased = fixture.CountDarkerThan(110);

        biased.Should().BeGreaterThan(0, "biased edges must be visible over their own faces");

        biased.Should().BeGreaterThan(
            unbiased,
            $"the depth bias is what keeps edges out of a z-fight, but biased={biased} "
            + $"and unbiased={unbiased}");
    }

    [Fact]
    public void EdgeWidthIsMeasuredInPixelsNotMetres()
    {
        using Fixture fixture = Fixture.Create(Size);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        // A line primitive would be one pixel whatever was asked for; a line built from world-space
        // geometry would thin out with distance. Asking for four times the width must produce
        // substantially more edge pixels at the same camera.
        fixture.Draw(WireBox(1.0), withFaces: false, withEdges: true, width: 1.0f);
        int thin = fixture.CountDifferingFrom(fixture.Background);

        fixture.Draw(WireBox(1.0), withFaces: false, withEdges: true, width: 4.0f);
        int thick = fixture.CountDifferingFrom(fixture.Background);

        thin.Should().BeGreaterThan(0);
        thick.Should().BeGreaterThan(
            thin * 2, $"a four-times-wider line should cover far more, but thin={thin} thick={thick}");
    }

    [Fact]
    public void AWireframeBodyWithNoTrianglesStillDraws()
    {
        using Fixture fixture = Fixture.Create(Size);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        // A sketch is edges with no faces. It must upload and draw rather than being skipped as an
        // empty body, which is what the face-only path used to do.
        fixture.Draw(WireBox(1.0, facesToo: false), withFaces: true, withEdges: true);

        fixture.Pass.SegmentsDrawn.Should().BeGreaterThan(0);
        fixture.Surface.CountDifferingFrom(fixture.Background).Should().BeGreaterThan(0);
    }

    [Fact]
    public void AnEdgeCrossingBehindTheCameraDoesNotSmearAcrossTheViewport()
    {
        using Fixture fixture = Fixture.Create(Size);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        // A vertex with w <= 0 mirrors to the far side of the screen when divided through, and an
        // edge with one endpoint behind the eye becomes a line right across the viewport. The
        // shader clips the segment against a small positive w first.
        using SceneGeometry scene = fixture.Upload(WireBox(2.0));

        Camera camera = fixture.Camera;
        camera.LookFrom(StandardView.Front);
        camera.ZoomToFit(scene.Bounds);

        // Inside the box, so its near edges are behind the eye.
        camera.Distance = 0.2;

        fixture.Render(scene, camera, withFaces: false, withEdges: true, EdgeStyle.Default);

        // A smear would paint a line clean across the image. Corners far from any real edge must
        // stay background.
        foreach ((int x, int y) in new[] { (2, 2), (Size - 3, 2), (2, Size - 3), (Size - 3, Size - 3) })
        {
            fixture.Surface.At(x, y).IsCloseTo(fixture.Background).Should().BeTrue(
                $"pixel ({x},{y}) should not have been painted by a segment behind the camera");
        }
    }

    [Fact]
    public void TheAntiAliasingRampDoesNotHaloTheLine()
    {
        using Fixture fixture = Fixture.Create(Size);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        // The coverage ramp exists to soften an edge, and it is blended with SourceBlend = One --
        // a premultiplied state, despite BlendDescription.AlphaBlend's name. A shader returning
        // straight alpha against it computes edge + dst*(1-a) rather than a*edge + (1-a)*dst, so
        // wherever coverage is low the whole edge colour is *added* to the background and the
        // softening band comes out brighter than either: a halo down both sides of every line.
        //
        // Correctly premultiplied, every result is a blend between the background and the edge
        // colour, so nothing can exceed the edge colour on any channel. That ceiling is the test.
        // Counting coverage or measuring the darkest pixel both miss this entirely, which is how
        // it survived the first round.
        EdgeStyle style = EdgeStyle.Default with { WidthPixels = 3.0f };

        fixture.Draw(WireBox(1.0, facesToo: false), withFaces: false, withEdges: true, width: 3.0f);

        Pixel ceiling = new(
            (byte)System.Math.Round(style.Colour.R * 255),
            (byte)System.Math.Round(style.Colour.G * 255),
            (byte)System.Math.Round(style.Colour.B * 255),
            255);

        Pixel brightest = new(0, 0, 0, 255);

        for (int y = 0; y < Size; ++y)
        {
            for (int x = 0; x < Size; ++x)
            {
                Pixel pixel = fixture.Surface.At(x, y);

                brightest = new Pixel(
                    System.Math.Max(brightest.R, pixel.R),
                    System.Math.Max(brightest.G, pixel.G),
                    System.Math.Max(brightest.B, pixel.B),
                    255);
            }
        }

        // Two counts of slack for rounding through an 8-bit target.
        brightest.R.Should().BeLessThanOrEqualTo(
            (byte)(ceiling.R + 2), $"brightest was {brightest}, edge colour is {ceiling}");

        brightest.G.Should().BeLessThanOrEqualTo((byte)(ceiling.G + 2));
        brightest.B.Should().BeLessThanOrEqualTo((byte)(ceiling.B + 2));
    }

    [Fact]
    public void AMalformedEdgeSetIsSkippedRatherThanThrowing()
    {
        // DisplayEdges is a public record with no enforced invariants and can arrive from a plugin.
        // This runs inside the frame loop, where an IndexOutOfRangeException does not report a bad
        // snapshot -- it takes the window down.
        DisplayEdges pastTheEnd = new(
            [0, 0, 0, 1, 0, 0],
            [0],
            [9],
            [new DisplayId(1)]);

        SceneGeometry.SegmentsOf(pastTheEnd).Count.Should().Be(0, "the span runs past the positions");

        DisplayEdges negativeStart = new([0, 0, 0, 1, 0, 0], [-1], [2], [new DisplayId(1)]);
        SceneGeometry.SegmentsOf(negativeStart).Count.Should().Be(0);

        // Fewer lengths than starts: only the polylines that have both are considered.
        DisplayEdges ragged = new([0, 0, 0, 1, 0, 0], [0, 1], [2], [new DisplayId(1)]);
        SceneGeometry.SegmentsOf(ragged).Count.Should().Be(1, "one well-formed segment survives");
    }

    [Fact]
    public void TheStyleScalesWithTheDisplay()
    {
        EdgeStyle.Default.AtScale(1.5).WidthPixels.Should()
            .BeApproximately(EdgeStyle.Default.WidthPixels * 1.5f, 1e-4f);

        // A nonsensical scale must not produce a zero-width or negative line.
        EdgeStyle.Default.AtScale(0).WidthPixels.Should().BeGreaterThan(0);
    }

    // --- Fixtures -----------------------------------------------------------------------------

    /// <summary>A cube with its twelve edges as polylines, optionally with faces too.</summary>
    private static MeshBuffer WireBox(double size, bool facesToo = true)
    {
        double h = size / 2;

        Vec3d[] corners =
        [
            new(-h, -h, -h), new(h, -h, -h), new(h, h, -h), new(-h, h, -h),
            new(-h, -h, h), new(h, -h, h), new(h, h, h), new(-h, h, h),
        ];

        (int A, int B)[] wires =
        [
            (0, 1), (1, 2), (2, 3), (3, 0),
            (4, 5), (5, 6), (6, 7), (7, 4),
            (0, 4), (1, 5), (2, 6), (3, 7),
        ];

        ImmutableArray<Vec3d>.Builder points = ImmutableArray.CreateBuilder<Vec3d>();
        ImmutableArray<int>.Builder offsets = ImmutableArray.CreateBuilder<int>();
        ImmutableArray<SubEntity>.Builder edgeEntities = ImmutableArray.CreateBuilder<SubEntity>();

        foreach ((int a, int b) in wires)
        {
            offsets.Add(points.Count);
            points.Add(corners[a]);
            points.Add(corners[b]);
            edgeEntities.Add(new SubEntity(
                new KernelShape(1), (ulong)(100 + edgeEntities.Count), SubEntityKind.Edge));
        }

        // MeshEdges.Offsets has one entry past the end, so a length is a difference of two.
        offsets.Add(points.Count);

        MeshEdges edges = new(points.ToImmutable(), offsets.ToImmutable(), edgeEntities.ToImmutable());

        if (!facesToo)
        {
            return new MeshBuffer([], [], [], [], [], edges);
        }

        MeshBuffer solid = SolidBox(size);
        return solid with { Edges = edges };
    }

    /// <summary>A cube's triangles, four vertices per face so the normals stay flat.</summary>
    private static MeshBuffer SolidBox(double size)
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
            Vec3d origin = normal * h;
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

    private sealed class Fixture : IDisposable
    {
        private Fixture(string skipped) => Skipped = skipped;

        private Fixture(D3D12RenderDevice device, OffscreenSurface surface, FacePass faces, EdgePass edges)
        {
            Device = device;
            Surface = surface;
            Faces = faces;
            Pass = edges;
        }

        public string? Skipped { get; }

        public D3D12RenderDevice Device { get; } = null!;

        public OffscreenSurface Surface { get; } = null!;

        public FacePass Faces { get; } = null!;

        public EdgePass Pass { get; } = null!;

        public Camera Camera { get; } = new();

        public Color4 Clear { get; } = new(0.05f, 0.05f, 0.08f, 1.0f);

        public Pixel Background => new(
            (byte)System.Math.Round(Clear.R * 255),
            (byte)System.Math.Round(Clear.G * 255),
            (byte)System.Math.Round(Clear.B * 255),
            (byte)System.Math.Round(Clear.A * 255));

        public static Fixture Create(int size)
        {
            D3D12RenderDevice? device = null;
            OffscreenSurface? surface = null;
            FacePass? faces = null;

            try
            {
                device = new D3D12RenderDevice(Software);
                surface = new OffscreenSurface(device, size, size);
                faces = new FacePass(device.Device, OffscreenSurface.ColourFormat, optimiseShaders: false);
                EdgePass edges = new(device.Device, OffscreenSurface.ColourFormat, optimiseShaders: false);

                return new Fixture(device, surface, faces, edges);
            }
            catch (Exception exception)
                when (exception is RenderDeviceUnavailableException or SharpGenException)
            {
                faces?.Dispose();
                surface?.Dispose();
                device?.Dispose();

                return new Fixture($"No usable D3D12 device: {exception.Message}");
            }
        }

        public SceneGeometry Upload(MeshBuffer mesh)
        {
            SnapshotBuilder builder = new();
            builder.Add(mesh);

            return SceneGeometry.Upload(Device, builder.Build(1));
        }

        public void Draw(
            MeshBuffer mesh,
            bool withFaces,
            bool withEdges,
            float width = 1.4f,
            float depthBias = 2e-4f)
        {
            using SceneGeometry scene = Upload(mesh);

            Camera.LookFrom(StandardView.Isometric);
            Camera.ZoomToFit(scene.Bounds);

            Render(
                scene,
                Camera,
                withFaces,
                withEdges,
                EdgeStyle.Default with { WidthPixels = width, DepthBias = depthBias });
        }

        public void Render(
            SceneGeometry scene, Camera camera, bool withFaces, bool withEdges, EdgeStyle style)
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
                ViewportSize = new Vector2(Surface.Width, Surface.Height),
            });

            Frustum frustum = Frustum.FromViewProjection(projection * camera.ViewMatrix());

            Surface.Render(Clear, commands =>
            {
                if (withFaces)
                {
                    Faces.Draw(commands, scene, Surface.ConstantBufferAddress, frustum);
                }

                if (withEdges)
                {
                    Pass.Draw(commands, scene, Surface.ConstantBufferAddress, style, frustum);
                }
            });
        }

        /// <summary>The luminance of the darkest non-background pixel.</summary>
        public int DarkestPixel()
        {
            int darkest = 255;

            for (int y = 0; y < Surface.Height; ++y)
            {
                for (int x = 0; x < Surface.Width; ++x)
                {
                    Pixel pixel = Surface.At(x, y);

                    if (pixel.IsCloseTo(Background))
                    {
                        continue;
                    }

                    darkest = System.Math.Min(darkest, Luminance(pixel));
                }
            }

            return darkest;
        }

        /// <summary>How many non-background pixels are darker than a threshold.</summary>
        public int CountDarkerThan(int luminance)
        {
            int count = 0;

            for (int y = 0; y < Surface.Height; ++y)
            {
                for (int x = 0; x < Surface.Width; ++x)
                {
                    Pixel pixel = Surface.At(x, y);

                    if (!pixel.IsCloseTo(Background) && Luminance(pixel) < luminance)
                    {
                        count++;
                    }
                }
            }

            return count;
        }

        public int CountDifferingFrom(Pixel background) => Surface.CountDifferingFrom(background);

        public void Dispose()
        {
            Pass?.Dispose();
            Faces?.Dispose();
            Surface?.Dispose();
            Device?.Dispose();
        }

        private static int Luminance(Pixel pixel) => ((pixel.R * 30) + (pixel.G * 59) + (pixel.B * 11)) / 100;

        private static Matrix4x4 ToShaderMatrix(Mat4d m) => new(
            (float)m.M11, (float)m.M12, (float)m.M13, (float)m.M14,
            (float)m.M21, (float)m.M22, (float)m.M23, (float)m.M24,
            (float)m.M31, (float)m.M32, (float)m.M33, (float)m.M34,
            (float)m.M41, (float)m.M42, (float)m.M43, (float)m.M44);

        private static Vector3 ToVector3(Vec3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
    }
}
