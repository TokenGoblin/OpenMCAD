using System.Collections.Immutable;
using System.Numerics;

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
/// The ID pass (P2-T07), rendered on WARP and read back through the real pick path.
/// </summary>
/// <remarks>
/// Picking fails by returning a plausible wrong answer: a neighbouring face, the entity behind the
/// one you clicked, or an id one out because the geometry and its ids were flattened separately.
/// None of that is visible from a return value, and all of it feels to a user like the application
/// simply mis-selecting.
/// </remarks>
public sealed class IdPassTests
{
    private const int Size = 128;


    /// <summary>Both segments of the three-point polyline, then the two-point one.</summary>
    private static readonly uint[] ExpectedSharedIds = [41u, 41u, 42u];

    /// <summary>Only the well-formed polyline survives.</summary>
    private static readonly uint[] ExpectedSurvivingId = [42u];

    [Fact]
    public void TheIdShadersCompile()
    {
        ShaderLibrary.Compile(FacePass.ShaderFile, "PSMainId", ShaderLibrary.PixelProfile, false)
            .Length.Should().BeGreaterThan(0);

        ShaderLibrary.Compile(EdgePass.ShaderFile, "PSMainId", ShaderLibrary.PixelProfile, false)
            .Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ADisplayIdIsOneUnsignedIntegerWide()
    {
        // The snapshot's id arrays are uploaded without repacking and read as a StructuredBuffer of
        // uint. If DisplayId ever grew a field, every id in the buffer would be misaligned and
        // picks would resolve to unrelated entities.
        System.Runtime.InteropServices.Marshal.SizeOf<DisplayId>()
            .Should().Be(SceneGeometry.IdStride);
    }

    [Fact]
    public void EverySegmentOfAPolylineCarriesTheEdgesId()
    {
        // Geometry and ids are flattened in one pass so the two cannot disagree about which
        // polylines were skipped. An off-by-one here selects the wrong edge, and only on models
        // that contain a degenerate polyline.
        DisplayEdges edges = new(
            [0, 0, 0, 1, 0, 0, 2, 0, 0, 10, 0, 0, 11, 0, 0],
            [0, 3],
            [3, 2],
            [new DisplayId(41), new DisplayId(42)]);

        EdgeSegments segments = SceneGeometry.SegmentsOf(edges);

        segments.Count.Should().Be(3, "two spans in the first polyline and one in the second");
        segments.Ids.Should().Equal(ExpectedSharedIds);
    }

    [Fact]
    public void ADegeneratePolylineDropsItsIdToo()
    {
        DisplayEdges edges = new(
            [0, 0, 0, 5, 0, 0, 6, 0, 0],
            [0, 1],
            [1, 2],
            [new DisplayId(41), new DisplayId(42)]);

        EdgeSegments segments = SceneGeometry.SegmentsOf(edges);

        segments.Count.Should().Be(1);
        segments.Ids.Should().Equal(
            ExpectedSurvivingId, "the one-point polyline contributes neither geometry nor id");
    }

    // --- Rendering ----------------------------------------------------------------------------

    [Fact]
    public void TheFaceUnderTheCentreIsWrittenToTheIdBuffer()
    {
        using Fixture fixture = Fixture.Create(Size);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        fixture.Render();

        fixture.IdAt(Size / 2, Size / 2).IsSomething.Should().BeTrue(
            "the middle of the viewport is covered by the cube");

        // Deliberately not the exact centre. An isometric cube puts its near vertical corner
        // there, so the middle pixel legitimately belongs to an edge -- asserting a face at the
        // centre would be asserting that the edge pass had failed.
        DisplayId id = fixture.IdAt(Size / 2, (Size / 2) - (Size / 8));

        id.IsSomething.Should().BeTrue();

        SubEntity entity = fixture.Snapshot.Resolve(id);
        entity.Kind.Should().Be(SubEntityKind.Face, "well inside the top face of the cube");
    }

    [Fact]
    public void EmptySpaceReadsBackAsNothing()
    {
        using Fixture fixture = Fixture.Create(Size);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        fixture.Render();

        // The clear value is zero, which is DisplayId.None.
        fixture.IdAt(1, 1).Should().Be(DisplayId.None);
    }

    [Fact]
    public void TheIdBufferAgreesWithWhatIsVisible()
    {
        using Fixture fixture = Fixture.Create(Size);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        // The ID pass shares its vertex shaders with the visible passes precisely so the two
        // rasterise the same silhouette. If they diverged, picks near the boundary would land on
        // nothing while the user could plainly see geometry there.
        fixture.Render();

        int covered = 0;
        int idsPresent = 0;

        for (int y = 0; y < Size; ++y)
        {
            for (int x = 0; x < Size; ++x)
            {
                if (!fixture.Colour.At(x, y).IsCloseTo(fixture.Background))
                {
                    covered++;
                }

                if (fixture.IdAt(x, y).IsSomething)
                {
                    idsPresent++;
                }
            }
        }

        covered.Should().BeGreaterThan(0);

        // Not exact: the visible edge pass anti-aliases, so its outermost ramp pixels are faintly
        // shaded while the ID pass writes a hard id there. Within a few percent is agreement.
        idsPresent.Should().BeCloseTo(covered, (uint)(covered / 10));
    }

    [Fact]
    public void AnOccludedFaceIsNotPicked()
    {
        using Fixture fixture = Fixture.Create(Size);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        // Looking straight at one face of a cube. The id under the cursor must be the near face,
        // never the one behind it -- which is what the ID pass's own depth buffer is for.
        fixture.Frame(StandardView.Front);
        fixture.Render();

        DisplayId id = fixture.IdAt(Size / 2, Size / 2);
        SubEntity entity = fixture.Snapshot.Resolve(id);

        entity.Kind.Should().Be(SubEntityKind.Face);

        // Face 3 is -Y, the one facing the camera in a front view; face 2 is +Y, behind it.
        entity.Tag.Should().Be(4, "the near face, not the far one");
    }

    [Fact]
    public void EdgesAreWrittenOverTheFacesTheyBound()
    {
        using Fixture fixture = Fixture.Create(Size);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        fixture.Render();

        int edgePixels = 0;

        for (int y = 0; y < Size; ++y)
        {
            for (int x = 0; x < Size; ++x)
            {
                DisplayId id = fixture.IdAt(x, y);

                if (id.IsSomething && fixture.Snapshot.Resolve(id).Kind == SubEntityKind.Edge)
                {
                    edgePixels++;
                }
            }
        }

        edgePixels.Should().BeGreaterThan(
            0, "edges carry the same depth bias in the ID pass, so they must survive it");
    }

    [Fact]
    public void AHighlightedFaceIsTintedButKeepsItsShading()
    {
        using Fixture fixture = Fixture.Create(Size);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        fixture.Render();

        int x = Size / 2;
        int y = (Size / 2) - (Size / 8);

        DisplayId id = fixture.IdAt(x, y);
        id.IsSomething.Should().BeTrue();

        Pixel plain = fixture.Colour.At(x, y);

        // Highlight exactly the face under that pixel and draw again.
        fixture.Highlights = HighlightTable.Build(
            [new(id, HighlightState.Selected)], 1);

        fixture.Render();
        Pixel highlighted = fixture.Colour.At(x, y);

        highlighted.IsCloseTo(plain).Should().BeFalse(
            $"the selected face should change colour, but stayed {plain}");

        highlighted.B.Should().BeGreaterThan(
            plain.B, "the selection colour is blue, so blue should rise");

        // A tint, not a replacement. Painting a selected face flat destroys the shading that
        // tells the user what shape they have selected -- a curved surface stops reading as
        // curved, and two faces at different angles become one silhouette.
        //
        // A flat cube face is uniformly lit, so there is no gradient across one to preserve. What
        // does prove it is two faces that differ in shading staying different once both are
        // selected: under a replacement they would come out identical.
        DisplayId second = DisplayId.None;

        for (int probe = 0; probe < Size && !second.IsSomething; ++probe)
        {
            DisplayId here = fixture.IdAt(probe, Size / 2);

            if (here.IsSomething
                && here != id
                && fixture.Snapshot.Resolve(here).Kind == SubEntityKind.Face)
            {
                second = here;
            }
        }

        second.IsSomething.Should().BeTrue("an isometric cube shows more than one face");

        fixture.Highlights = HighlightTable.Build(
            [
                new(id, HighlightState.Selected),
                new(second, HighlightState.Selected),
            ],
            2);

        fixture.Render();

        Pixel first = fixture.Colour.At(x, y);
        Pixel other = PixelOfFirst(fixture, second, Size / 2);

        first.IsCloseTo(other).Should().BeFalse(
            $"two differently lit faces must stay distinguishable when selected, "
            + $"but both came out {first}");
    }

    /// <summary>The colour of the first pixel on a row belonging to a given entity.</summary>
    private static Pixel PixelOfFirst(Fixture fixture, DisplayId id, int row)
    {
        for (int x = 0; x < Size; ++x)
        {
            if (fixture.IdAt(x, row) == id)
            {
                return fixture.Colour.At(x, row);
            }
        }

        return default;
    }

    [Fact]
    public void AnUnhighlightedSceneRendersIdenticallyToOneWithAnEmptyTable()
    {
        using Fixture fixture = Fixture.Create(Size);

        if (fixture.Skipped is { } reason)
        {
            Assert.Skip(reason);
            return;
        }

        // The shader takes a cheap path when no state buffer is bound. It must produce exactly the
        // same image as one bound to a table in which nothing is highlighted -- otherwise merely
        // selecting and deselecting would leave the viewport subtly different.
        fixture.Render();
        Pixel unbound = fixture.Colour.At(Size / 2, (Size / 2) - (Size / 8));

        fixture.Highlights = HighlightTable.Build(
            [new(new DisplayId(1), HighlightState.None)], 7);

        fixture.Render();
        Pixel bound = fixture.Colour.At(Size / 2, (Size / 2) - (Size / 8));

        bound.IsCloseTo(unbound, 1).Should().BeTrue($"got {bound} against {unbound}");
    }

    // --- Fixture ------------------------------------------------------------------------------

    private sealed class Fixture : IDisposable
    {
        private uint[] _ids = [];

        private Fixture(string skipped) => Skipped = skipped;

        private Fixture(
            D3D12RenderDevice device,
            OffscreenSurface colour,
            IdOffscreen ids,
            FacePass faces,
            EdgePass edges,
            IdPass idPass,
            SceneGeometry scene,
            DisplaySnapshot snapshot)
        {
            _highlightStates = new HighlightBuffer(device.Device);
            Device = device;
            Colour = colour;
            Ids = ids;
            Faces = faces;
            Edges = edges;
            IdPass = idPass;
            Scene = scene;
            Snapshot = snapshot;
        }

        public string? Skipped { get; }

        public D3D12RenderDevice Device { get; } = null!;

        public OffscreenSurface Colour { get; } = null!;

        public IdOffscreen Ids { get; } = null!;

        public FacePass Faces { get; } = null!;

        public EdgePass Edges { get; } = null!;

        public IdPass IdPass { get; } = null!;

        public SceneGeometry Scene { get; } = null!;

        public DisplaySnapshot Snapshot { get; } = DisplaySnapshot.Empty;

        public Camera Camera { get; } = new();

        /// <summary>Which entities are highlighted on the next render.</summary>
        public HighlightTable Highlights { get; set; } = HighlightTable.Empty;

        private readonly HighlightBuffer? _highlightStates;

        public Color4 Clear { get; } = new(0.05f, 0.05f, 0.08f, 1.0f);

        /// <summary>The clear colour as it comes back out of the framebuffer.</summary>
        public Pixel Background => new(
            (byte)System.Math.Round(Clear.R * 255),
            (byte)System.Math.Round(Clear.G * 255),
            (byte)System.Math.Round(Clear.B * 255),
            (byte)System.Math.Round(Clear.A * 255));

        public static Fixture Create(int size)
        {
            D3D12RenderDevice? device = null;
            OffscreenSurface? colour = null;
            IdOffscreen? ids = null;
            FacePass? faces = null;
            EdgePass? edges = null;
            IdPass? idPass = null;
            SceneGeometry? scene = null;

            try
            {
                device = new D3D12RenderDevice(TestDevices.Software);
                colour = new OffscreenSurface(device, size, size);
                ids = new IdOffscreen(device, size, size);
                faces = new FacePass(device.Device, OffscreenSurface.ColourFormat, optimiseShaders: false);
                edges = new EdgePass(device.Device, OffscreenSurface.ColourFormat, optimiseShaders: false);
                idPass = new IdPass(device.Device, optimiseShaders: false);

                SnapshotBuilder builder = new();
                builder.Add(EdgePassTestsGeometry.WireBox(1.0));
                DisplaySnapshot snapshot = builder.Build(1);

                scene = SceneGeometry.Upload(device, snapshot);

                Fixture fixture = new(device, colour, ids, faces, edges, idPass, scene, snapshot);
                fixture.Frame(StandardView.Isometric);

                return fixture;
            }
            catch (Exception exception)
                when (exception is RenderDeviceUnavailableException or SharpGenException)
            {
                scene?.Dispose();
                idPass?.Dispose();
                edges?.Dispose();
                faces?.Dispose();
                ids?.Dispose();
                colour?.Dispose();
                device?.Dispose();

                return new Fixture($"No usable D3D12 device: {exception.Message}");
            }
        }

        /// <summary>Points the camera at the scene from a standard view.</summary>
        public void Frame(StandardView view)
        {
            Camera.AspectRatio = 1.0;
            Camera.LookFrom(view);
            Camera.ZoomToFit(Snapshot.Bounds);
        }

        /// <summary>Renders the shaded frame and the ID buffer through the same camera.</summary>
        public void Render()
        {
            // Before the constants are built, because the count of live states goes into them.
            // The renderer does this in the same order for the same reason.
            _highlightStates!.Update(Highlights);
            ulong states = _highlightStates.Address;

            Mat4d projection = Camera.ProjectionMatrix(Scene.Bounds);
            Vec3d origin = Scene.Origin;

            FrameConstants constants = new()
            {
                ViewProjection = ToShaderMatrix(
                    projection * Mat4d.LookAt(
                        Camera.Position - origin, Camera.Target - origin, Camera.Up)),
                CameraPosition = ToVector3(Camera.Position - origin),
                LightDirection = ToVector3(FacePass.KeyLightDirection(Camera)),
                ViewportSize = new Vector2(Colour.Width, Colour.Height),
                HighlightCount = (uint)_highlightStates.Length,
                PreSelectedColour = HighlightStyle.Default.PreSelected,
                SelectedColour = HighlightStyle.Default.Selected,
                ErrorColour = HighlightStyle.Default.Error,
            };

            Colour.SetConstants(constants);
            Ids.SetConstants(constants);

            Frustum frustum = Frustum.FromViewProjection(projection * Camera.ViewMatrix());

            Colour.Render(Clear, commands =>
            {
                Faces.Draw(
                    commands, Scene, Colour.ConstantBufferAddress, frustum, colour: null, states);

                Edges.Draw(
                    commands, Scene, Colour.ConstantBufferAddress, EdgeStyle.Default, frustum, states);
            });

            _ids = Ids.Render(commands =>
                IdPass.Draw(commands, Scene, Ids.ConstantBufferAddress, EdgeStyle.Default, frustum));
        }

        public DisplayId IdAt(int x, int y) => new(_ids[(y * Ids.Width) + x]);

        public void Dispose()
        {
            _highlightStates?.Dispose();
            Scene?.Dispose();
            IdPass?.Dispose();
            Edges?.Dispose();
            Faces?.Dispose();
            Ids?.Dispose();
            Colour?.Dispose();
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

/// <summary>Shared geometry so the edge and ID tests describe the same cube.</summary>
internal static class EdgePassTestsGeometry
{
    /// <summary>A cube with faces and its twelve edges as polylines.</summary>
    public static MeshBuffer WireBox(double size)
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

        offsets.Add(points.Count);

        MeshEdges edges = new(points.ToImmutable(), offsets.ToImmutable(), edgeEntities.ToImmutable());

        return SolidBox(size) with { Edges = edges };
    }

    /// <summary>A cube's triangles, four vertices per face so the normals stay flat.</summary>
    public static MeshBuffer SolidBox(double size)
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
}
