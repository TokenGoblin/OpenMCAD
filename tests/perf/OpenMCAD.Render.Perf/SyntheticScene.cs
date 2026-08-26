using System.Collections.Immutable;

using OpenMCAD.Kernel;
using OpenMCAD.Math;
using OpenMCAD.Render;

namespace OpenMCAD.Render.Perf;

/// <summary>
/// Builds scenes of a requested size, for measuring against (P2-T13).
/// </summary>
/// <remarks>
/// <para>
/// <b>Many bodies rather than one enormous mesh.</b> A single five-million-triangle body measures
/// how fast the GPU consumes vertices, which is not what a CAD viewport is usually short of. What
/// it is short of is draw calls, state changes and per-body work across an assembly of thousands
/// of small parts — so the scene is built as a field of separate bodies, and the body count is a
/// parameter in its own right.
/// </para>
/// <para>
/// <b>Deterministic.</b> Given the same request it produces the same scene, so two measurements
/// taken a week apart are comparable. Randomised geometry would make every regression argue about
/// whether the scene had changed.
/// </para>
/// </remarks>
public static class SyntheticScene
{
    /// <summary>Triangles in one generated body.</summary>
    /// <remarks>
    /// A tessellated sphere rather than a box. A box is twelve triangles and has no curvature, so
    /// a scene made of boxes exercises neither the vertex load nor the normal interpolation that a
    /// real part does — and its silhouette is axis-aligned, which flatters the rasteriser.
    /// </remarks>
    public static int TrianglesPerBody(int segments) => segments * segments * 2;

    /// <summary>
    /// Builds a scene of approximately the requested size.
    /// </summary>
    /// <param name="triangleTarget">How many triangles in total, approximately.</param>
    /// <param name="bodyCount">How many separate bodies to spread them across.</param>
    /// <returns>The snapshot, and what it actually contains.</returns>
    /// <remarks>
    /// The triangle count is approximate because the tessellation is a whole number of segments
    /// per body. Reporting what was actually built rather than what was asked for is the point:
    /// a measurement labelled "1M triangles" that was really 700k is a measurement that cannot be
    /// compared with anything.
    /// </remarks>
    public static DisplaySnapshot Build(int triangleTarget, int bodyCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(triangleTarget);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bodyCount);

        int perBody = System.Math.Max(triangleTarget / bodyCount, 2);
        int segments = System.Math.Max((int)System.Math.Sqrt(perBody / 2.0), 2);

        SnapshotBuilder builder = new();

        // Laid out on a square grid with a gap, so bodies do not intersect and the frustum culler
        // has a realistic spread to work against rather than one dense clump.
        int side = (int)System.Math.Ceiling(System.Math.Sqrt(bodyCount));
        double spacing = 2.5;

        for (int i = 0; i < bodyCount; ++i)
        {
            double x = (i % side) * spacing;
            double y = (i / side) * spacing;

            builder.Add(Sphere(new Vec3d(x, y, 0), 1.0, segments, (ulong)(i * 1000)));
        }

        return builder.Build(1);
    }

    /// <summary>A UV-tessellated sphere, as one face.</summary>
    /// <remarks>
    /// One face rather than one per quad: a real tessellated face is thousands of triangles all
    /// naming the same entity, and splitting them would make the id buffer and the highlight table
    /// far larger than a real model of the same size.
    /// </remarks>
    private static MeshBuffer Sphere(Vec3d centre, double radius, int segments, ulong tag)
    {
        int rings = segments;
        int sectors = segments;

        ImmutableArray<Vec3d>.Builder positions = ImmutableArray.CreateBuilder<Vec3d>();
        ImmutableArray<Vec3d>.Builder normals = ImmutableArray.CreateBuilder<Vec3d>();
        ImmutableArray<int>.Builder indices = ImmutableArray.CreateBuilder<int>();
        ImmutableArray<int>.Builder triangleFaces = ImmutableArray.CreateBuilder<int>();

        for (int ring = 0; ring <= rings; ++ring)
        {
            double phi = System.Math.PI * ring / rings;
            double sinPhi = System.Math.Sin(phi);
            double cosPhi = System.Math.Cos(phi);

            for (int sector = 0; sector <= sectors; ++sector)
            {
                double theta = 2.0 * System.Math.PI * sector / sectors;

                Vec3d normal = new(
                    sinPhi * System.Math.Cos(theta),
                    sinPhi * System.Math.Sin(theta),
                    cosPhi);

                normals.Add(normal);
                positions.Add(centre + (normal * radius));
            }
        }

        int stride = sectors + 1;

        for (int ring = 0; ring < rings; ++ring)
        {
            for (int sector = 0; sector < sectors; ++sector)
            {
                int a = (ring * stride) + sector;
                int b = a + stride;

                indices.AddRange(a, b, a + 1);
                indices.AddRange(a + 1, b, b + 1);
                triangleFaces.AddRange(0, 0);
            }
        }

        return new MeshBuffer(
            positions.ToImmutable(),
            normals.ToImmutable(),
            indices.ToImmutable(),
            triangleFaces.ToImmutable(),
            [new SubEntity(new KernelShape(1), tag + 1, SubEntityKind.Face)]);
    }
}
