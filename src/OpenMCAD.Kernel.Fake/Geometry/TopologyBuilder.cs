using System.Collections.Immutable;
using OpenMCAD.Math;

namespace OpenMCAD.Kernel.Fake.Geometry;

/// <summary>
/// Builds the explicit topology of a fake shape.
/// </summary>
/// <remarks>
/// <para>
/// Every entity gets a role and an ordinal at the moment it is created, in a canonical order that
/// is part of the kernel's contract rather than an accident of how the loop happened to run. That
/// is the whole reason this class exists as a distinct step: topology is the part of the mock that
/// has to be <i>exact</i>, because names are built on it, whereas the volume behind it only has to
/// be plausible.
/// </para>
/// <para>
/// Tag allocation is sequential from a counter the caller supplies, so building the same shape
/// twice with a fresh kernel produces identical tags — which is what makes the determinism gate in
/// P1-T12 meaningful when run against <c>FakeKernel</c>.
/// </para>
/// </remarks>
internal sealed class TopologyBuilder(Func<ulong> nextTag)
{
    private readonly List<FakeEntity> _faces = [];
    private readonly List<FakeEntity> _edges = [];
    private readonly List<FakeEntity> _vertices = [];

    internal ImmutableArray<FakeEntity> Faces => [.. _faces];

    internal ImmutableArray<FakeEntity> Edges => [.. _edges];

    internal ImmutableArray<FakeEntity> Vertices => [.. _vertices];

    internal FakeEntity AddFace(OperationRole role, int ordinal, Vec3d centroid, Vec3d normal, double area)
    {
        FakeEntity face = new(nextTag(), SubEntityKind.Face, role, ordinal, centroid, normal, area);
        _faces.Add(face);
        return face;
    }

    internal FakeEntity AddEdge(
        OperationRole role,
        int ordinal,
        Vec3d midpoint,
        Vec3d direction,
        double length,
        params ReadOnlySpan<ulong> adjacentFaces)
    {
        FakeEntity edge = new(nextTag(), SubEntityKind.Edge, role, ordinal, midpoint, direction, length)
        {
            AdjacentFaces = [.. adjacentFaces],
        };

        _edges.Add(edge);
        return edge;
    }

    internal FakeEntity AddVertex(OperationRole role, int ordinal, Vec3d position)
    {
        FakeEntity vertex = new(nextTag(), SubEntityKind.Vertex, role, ordinal, position, Vec3d.Zero, 0.0);
        _vertices.Add(vertex);
        return vertex;
    }

    /// <summary>
    /// Builds box topology in the canonical face order documented on <c>BoxDefinition</c>.
    /// </summary>
    /// <param name="size">The box extents.</param>
    /// <param name="placement">Where the box sits.</param>
    internal void BuildBox(Vec3d size, Transform placement)
    {
        // Canonical face order: -X, +X, -Y, +Y, -Z, +Z. Part of the contract; names refer to it.
        (Vec3d Normal, Vec3d Centre, double Area)[] faces =
        [
            (-Vec3d.UnitX, new Vec3d(0, size.Y / 2, size.Z / 2), size.Y * size.Z),
            (Vec3d.UnitX, new Vec3d(size.X, size.Y / 2, size.Z / 2), size.Y * size.Z),
            (-Vec3d.UnitY, new Vec3d(size.X / 2, 0, size.Z / 2), size.X * size.Z),
            (Vec3d.UnitY, new Vec3d(size.X / 2, size.Y, size.Z / 2), size.X * size.Z),
            (-Vec3d.UnitZ, new Vec3d(size.X / 2, size.Y / 2, 0), size.X * size.Y),
            (Vec3d.UnitZ, new Vec3d(size.X / 2, size.Y / 2, size.Z), size.X * size.Y),
        ];

        ulong[] faceTags = new ulong[6];
        for (int i = 0; i < faces.Length; i++)
        {
            faceTags[i] = AddFace(
                OperationRole.PrimitiveFace,
                i,
                placement.TransformPoint(faces[i].Centre),
                placement.TransformNormal(faces[i].Normal),
                faces[i].Area).Tag;
        }

        // Eight corners, indexed by the same bit convention Bounds3d.Corners uses.
        Vec3d[] corners = new Vec3d[8];
        for (int i = 0; i < 8; i++)
        {
            corners[i] = placement.TransformPoint(new Vec3d(
                (i & 1) == 0 ? 0 : size.X,
                (i & 2) == 0 ? 0 : size.Y,
                (i & 4) == 0 ? 0 : size.Z));
        }

        for (int i = 0; i < 8; i++)
        {
            AddVertex(OperationRole.PrimitiveVertex, i, corners[i]);
        }

        // Twelve edges: four along each axis, ordered X then Y then Z, each in ascending
        // corner-index order so the sequence is reproducible.
        (int A, int B, int F1, int F2)[] edges =
        [
            (0, 1, 2, 4), (2, 3, 3, 4), (4, 5, 2, 5), (6, 7, 3, 5),
            (0, 2, 0, 4), (1, 3, 1, 4), (4, 6, 0, 5), (5, 7, 1, 5),
            (0, 4, 0, 2), (1, 5, 1, 2), (2, 6, 0, 3), (3, 7, 1, 3),
        ];

        for (int i = 0; i < edges.Length; i++)
        {
            Vec3d a = corners[edges[i].A];
            Vec3d b = corners[edges[i].B];
            AddEdge(
                OperationRole.PrimitiveEdge,
                i,
                (a + b) * 0.5,
                (b - a).TryNormalize(out Vec3d direction) ? direction : Vec3d.UnitX,
                Vec3d.Distance(a, b),
                faceTags[edges[i].F1],
                faceTags[edges[i].F2]);
        }
    }

    /// <summary>Builds cylinder topology: lateral face, two caps, two circles, and a seam.</summary>
    /// <param name="radius">The radius.</param>
    /// <param name="height">The height.</param>
    /// <param name="placement">Where the cylinder sits.</param>
    internal void BuildCylinder(double radius, double height, Transform placement)
    {
        double circumference = 2.0 * System.Math.PI * radius;

        ulong lateral = AddFace(
            OperationRole.SideWall,
            0,
            placement.TransformPoint(new Vec3d(0, 0, height / 2)),
            placement.TransformNormal(Vec3d.UnitX),
            circumference * height).Tag;

        ulong bottom = AddFace(
            OperationRole.StartCap,
            0,
            placement.TransformPoint(Vec3d.Zero),
            placement.TransformNormal(-Vec3d.UnitZ),
            System.Math.PI * radius * radius).Tag;

        ulong top = AddFace(
            OperationRole.EndCap,
            0,
            placement.TransformPoint(new Vec3d(0, 0, height)),
            placement.TransformNormal(Vec3d.UnitZ),
            System.Math.PI * radius * radius).Tag;

        AddEdge(
            OperationRole.StartProfileEdge, 0,
            placement.TransformPoint(Vec3d.Zero),
            placement.TransformNormal(Vec3d.UnitY),
            circumference, lateral, bottom);

        AddEdge(
            OperationRole.EndProfileEdge, 0,
            placement.TransformPoint(new Vec3d(0, 0, height)),
            placement.TransformNormal(Vec3d.UnitY),
            circumference, lateral, top);

        AddEdge(
            OperationRole.Seam, 0,
            placement.TransformPoint(new Vec3d(radius, 0, height / 2)),
            placement.TransformNormal(Vec3d.UnitZ),
            height, lateral);

        AddVertex(OperationRole.PrimitiveVertex, 0, placement.TransformPoint(new Vec3d(radius, 0, 0)));
        AddVertex(OperationRole.PrimitiveVertex, 1, placement.TransformPoint(new Vec3d(radius, 0, height)));
    }

    /// <summary>Builds sphere topology: one face, a seam, and two poles.</summary>
    /// <param name="radius">The radius.</param>
    /// <param name="placement">Where the sphere sits.</param>
    internal void BuildSphere(double radius, Transform placement)
    {
        ulong surface = AddFace(
            OperationRole.PrimitiveFace,
            0,
            placement.TransformPoint(Vec3d.Zero),
            placement.TransformNormal(Vec3d.UnitZ),
            4.0 * System.Math.PI * radius * radius).Tag;

        AddEdge(
            OperationRole.Seam, 0,
            placement.TransformPoint(new Vec3d(radius, 0, 0)),
            placement.TransformNormal(Vec3d.UnitZ),
            System.Math.PI * radius, surface);

        AddVertex(OperationRole.Apex, 0, placement.TransformPoint(new Vec3d(0, 0, -radius)));
        AddVertex(OperationRole.Apex, 1, placement.TransformPoint(new Vec3d(0, 0, radius)));
    }

    /// <summary>Builds cone topology, omitting a cap where the radius is zero.</summary>
    /// <param name="bottomRadius">Radius at the base.</param>
    /// <param name="topRadius">Radius at the top.</param>
    /// <param name="height">The height.</param>
    /// <param name="placement">Where the cone sits.</param>
    internal void BuildCone(double bottomRadius, double topRadius, double height, Transform placement)
    {
        double slant = System.Math.Sqrt(
            ((bottomRadius - topRadius) * (bottomRadius - topRadius)) + (height * height));

        ulong lateral = AddFace(
            OperationRole.SideWall,
            0,
            placement.TransformPoint(new Vec3d(0, 0, height / 2)),
            placement.TransformNormal(Vec3d.UnitX),
            System.Math.PI * (bottomRadius + topRadius) * slant).Tag;

        bool hasBottom = bottomRadius > Tolerance.LinearResolution;
        bool hasTop = topRadius > Tolerance.LinearResolution;

        if (hasBottom)
        {
            ulong bottom = AddFace(
                OperationRole.StartCap, 0,
                placement.TransformPoint(Vec3d.Zero),
                placement.TransformNormal(-Vec3d.UnitZ),
                System.Math.PI * bottomRadius * bottomRadius).Tag;

            AddEdge(
                OperationRole.StartProfileEdge, 0,
                placement.TransformPoint(Vec3d.Zero),
                placement.TransformNormal(Vec3d.UnitY),
                2.0 * System.Math.PI * bottomRadius, lateral, bottom);
        }

        if (hasTop)
        {
            ulong top = AddFace(
                OperationRole.EndCap, 0,
                placement.TransformPoint(new Vec3d(0, 0, height)),
                placement.TransformNormal(Vec3d.UnitZ),
                System.Math.PI * topRadius * topRadius).Tag;

            AddEdge(
                OperationRole.EndProfileEdge, 0,
                placement.TransformPoint(new Vec3d(0, 0, height)),
                placement.TransformNormal(Vec3d.UnitY),
                2.0 * System.Math.PI * topRadius, lateral, top);
        }

        AddEdge(
            OperationRole.Seam, 0,
            placement.TransformPoint(new Vec3d((bottomRadius + topRadius) / 2, 0, height / 2)),
            placement.TransformNormal(Vec3d.UnitZ),
            slant, lateral);

        if (!hasBottom)
        {
            AddVertex(OperationRole.Apex, 0, placement.TransformPoint(Vec3d.Zero));
        }

        if (!hasTop)
        {
            AddVertex(OperationRole.Apex, 0, placement.TransformPoint(new Vec3d(0, 0, height)));
        }
    }

    /// <summary>Builds torus topology: one face and two seams.</summary>
    /// <param name="majorRadius">Distance from the axis to the tube centre.</param>
    /// <param name="minorRadius">The tube radius.</param>
    /// <param name="placement">Where the torus sits.</param>
    internal void BuildTorus(double majorRadius, double minorRadius, Transform placement)
    {
        ulong surface = AddFace(
            OperationRole.PrimitiveFace, 0,
            placement.TransformPoint(Vec3d.Zero),
            placement.TransformNormal(Vec3d.UnitX),
            4.0 * System.Math.PI * System.Math.PI * majorRadius * minorRadius).Tag;

        AddEdge(
            OperationRole.Seam, 0,
            placement.TransformPoint(new Vec3d(majorRadius + minorRadius, 0, 0)),
            placement.TransformNormal(Vec3d.UnitZ),
            2.0 * System.Math.PI * minorRadius, surface);

        AddEdge(
            OperationRole.Seam, 1,
            placement.TransformPoint(new Vec3d(majorRadius, 0, minorRadius)),
            placement.TransformNormal(Vec3d.UnitY),
            2.0 * System.Math.PI * majorRadius, surface);

        AddVertex(
            OperationRole.PrimitiveVertex, 0,
            placement.TransformPoint(new Vec3d(majorRadius + minorRadius, 0, 0)));
    }

    /// <summary>Builds profile topology: one planar face bounded by the outline.</summary>
    /// <param name="points">The profile outline.</param>
    /// <param name="frame">Maps the profile plane into the world.</param>
    internal void BuildProfile(ImmutableArray<Vec2d> points, Transform frame)
    {
        Vec2d centroid = Polygon2d.Centroid(points);

        ulong face = AddFace(
            OperationRole.PrimitiveFace, 0,
            frame.TransformPoint(new Vec3d(centroid.X, centroid.Y, 0)),
            frame.TransformNormal(Vec3d.UnitZ),
            Polygon2d.Area(points)).Tag;

        for (int i = 0; i < points.Length; i++)
        {
            Vec3d a = frame.TransformPoint(new Vec3d(points[i].X, points[i].Y, 0));
            Vec2d nextPoint = points[(i + 1) % points.Length];
            Vec3d b = frame.TransformPoint(new Vec3d(nextPoint.X, nextPoint.Y, 0));

            AddEdge(
                OperationRole.PrimitiveEdge, i,
                (a + b) * 0.5,
                (b - a).TryNormalize(out Vec3d direction) ? direction : Vec3d.UnitX,
                Vec3d.Distance(a, b),
                face);
        }

        for (int i = 0; i < points.Length; i++)
        {
            AddVertex(
                OperationRole.PrimitiveVertex, i,
                frame.TransformPoint(new Vec3d(points[i].X, points[i].Y, 0)));
        }
    }

    /// <summary>
    /// Builds prism topology and reports which profile entity produced each output.
    /// </summary>
    /// <param name="points">The profile outline.</param>
    /// <param name="frame">Maps the profile plane into the world.</param>
    /// <param name="direction">The unit sweep direction.</param>
    /// <param name="distance">How far to sweep.</param>
    /// <param name="sideWalls">Receives the side wall generated by each profile edge, in order.</param>
    /// <param name="startCap">Receives the start cap.</param>
    /// <param name="endCap">Receives the end cap.</param>
    /// <param name="sideEdges">Receives the lateral edge generated by each profile vertex, in order.</param>
    internal void BuildPrism(
        ImmutableArray<Vec2d> points,
        Transform frame,
        Vec3d direction,
        double distance,
        out ImmutableArray<FakeEntity> sideWalls,
        out FakeEntity startCap,
        out FakeEntity endCap,
        out ImmutableArray<FakeEntity> sideEdges)
    {
        Vec3d offset = direction * distance;
        Vec3d normal = frame.TransformNormal(Vec3d.UnitZ);
        Vec2d centroid2d = Polygon2d.Centroid(points);
        double area = Polygon2d.Area(points);

        Vec3d[] bottom = new Vec3d[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            bottom[i] = frame.TransformPoint(new Vec3d(points[i].X, points[i].Y, 0));
        }

        // Side walls, one per profile edge, in profile-edge order.
        ImmutableArray<FakeEntity>.Builder walls =
            ImmutableArray.CreateBuilder<FakeEntity>(points.Length);

        for (int i = 0; i < points.Length; i++)
        {
            int next = (i + 1) % points.Length;
            Vec3d along = bottom[next] - bottom[i];
            double length = along.Length;

            Vec3d wallNormal = Vec3d.Cross(along, offset);
            if (!wallNormal.TryNormalize(out wallNormal))
            {
                wallNormal = normal;
            }

            walls.Add(AddFace(
                OperationRole.SideWall, i,
                ((bottom[i] + bottom[next]) * 0.5) + (offset * 0.5),
                wallNormal,
                length * distance));
        }

        sideWalls = walls.MoveToImmutable();

        Vec3d baseCentroid = frame.TransformPoint(new Vec3d(centroid2d.X, centroid2d.Y, 0));
        startCap = AddFace(OperationRole.StartCap, 0, baseCentroid, -normal, area);
        endCap = AddFace(OperationRole.EndCap, 0, baseCentroid + offset, normal, area);

        // Profile edges swept to the far end, then the lateral edges.
        for (int i = 0; i < points.Length; i++)
        {
            int next = (i + 1) % points.Length;
            AddEdge(
                OperationRole.StartProfileEdge, i,
                (bottom[i] + bottom[next]) * 0.5,
                (bottom[next] - bottom[i]).TryNormalize(out Vec3d d0) ? d0 : Vec3d.UnitX,
                Vec3d.Distance(bottom[i], bottom[next]),
                sideWalls[i].Tag, startCap.Tag);
        }

        for (int i = 0; i < points.Length; i++)
        {
            int next = (i + 1) % points.Length;
            Vec3d a = bottom[i] + offset;
            Vec3d b = bottom[next] + offset;
            AddEdge(
                OperationRole.EndProfileEdge, i,
                (a + b) * 0.5,
                (b - a).TryNormalize(out Vec3d d1) ? d1 : Vec3d.UnitX,
                Vec3d.Distance(a, b),
                sideWalls[i].Tag, endCap.Tag);
        }

        ImmutableArray<FakeEntity>.Builder laterals =
            ImmutableArray.CreateBuilder<FakeEntity>(points.Length);

        for (int i = 0; i < points.Length; i++)
        {
            int previous = (i - 1 + points.Length) % points.Length;
            laterals.Add(AddEdge(
                OperationRole.SideEdge, i,
                bottom[i] + (offset * 0.5),
                direction,
                distance,
                sideWalls[previous].Tag, sideWalls[i].Tag));
        }

        sideEdges = laterals.MoveToImmutable();

        for (int i = 0; i < points.Length; i++)
        {
            AddVertex(OperationRole.PrimitiveVertex, i, bottom[i]);
        }

        for (int i = 0; i < points.Length; i++)
        {
            AddVertex(OperationRole.PrimitiveVertex, points.Length + i, bottom[i] + offset);
        }
    }
}
