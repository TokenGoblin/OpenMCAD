using System.Collections.Immutable;
using OpenMCAD.Math;

namespace OpenMCAD.Kernel.Fake.Geometry;

/// <summary>A box with its minimum corner at the placement origin.</summary>
/// <param name="Size">The extents along the local axes.</param>
/// <param name="Placement">Where the local frame sits.</param>
internal sealed record BoxGeometry(Vec3d Size, Transform Placement) : FakeGeometry
{
    internal override ResultAccuracy Accuracy => ResultAccuracy.Exact;

    internal override Bounds3d Bounds
        => new Bounds3d(Vec3d.Zero, Size).Transformed(Placement);

    internal override MassProperties Compute(double density)
    {
        double volume = Size.X * Size.Y * Size.Z;
        double area = 2.0 * ((Size.X * Size.Y) + (Size.Y * Size.Z) + (Size.Z * Size.X));
        double mass = volume * density;

        Vec3d localCentroid = Size * 0.5;

        InertiaTensor local = new(
            mass * ((Size.Y * Size.Y) + (Size.Z * Size.Z)) / 12.0,
            mass * ((Size.X * Size.X) + (Size.Z * Size.Z)) / 12.0,
            mass * ((Size.X * Size.X) + (Size.Y * Size.Y)) / 12.0);

        return new MassProperties(
            volume,
            area,
            Placement.TransformPoint(localCentroid),
            density,
            local.RotatedBy(Placement.Rotation),
            Accuracy);
    }

    internal override void Tessellate(MeshAccumulator mesh, TessellationOptions options, int faceIndex)
    {
        // Faces in the canonical order documented on BoxDefinition: -X, +X, -Y, +Y, -Z, +Z.
        (Vec3d Normal, Vec3d[] Corners)[] faces =
        [
            (-Vec3d.UnitX, [new(0, 0, 0), new(0, 0, Size.Z), new(0, Size.Y, Size.Z), new(0, Size.Y, 0)]),
            (Vec3d.UnitX, [new(Size.X, 0, 0), new(Size.X, Size.Y, 0), new(Size.X, Size.Y, Size.Z), new(Size.X, 0, Size.Z)]),
            (-Vec3d.UnitY, [new(0, 0, 0), new(Size.X, 0, 0), new(Size.X, 0, Size.Z), new(0, 0, Size.Z)]),
            (Vec3d.UnitY, [new(0, Size.Y, 0), new(0, Size.Y, Size.Z), new(Size.X, Size.Y, Size.Z), new(Size.X, Size.Y, 0)]),
            (-Vec3d.UnitZ, [new(0, 0, 0), new(0, Size.Y, 0), new(Size.X, Size.Y, 0), new(Size.X, 0, 0)]),
            (Vec3d.UnitZ, [new(0, 0, Size.Z), new(Size.X, 0, Size.Z), new(Size.X, Size.Y, Size.Z), new(0, Size.Y, Size.Z)]),
        ];

        for (int f = 0; f < faces.Length; f++)
        {
            Vec3d normal = Placement.TransformNormal(faces[f].Normal);
            int[] indices = new int[4];
            for (int i = 0; i < 4; i++)
            {
                indices[i] = mesh.AddVertex(Placement.TransformPoint(faces[f].Corners[i]), normal);
            }

            mesh.AddTriangle(indices[0], indices[1], indices[2], faceIndex + f);
            mesh.AddTriangle(indices[0], indices[2], indices[3], faceIndex + f);
        }
    }
}

/// <summary>A cylinder about the local Z axis, base centred at the placement origin.</summary>
/// <param name="Radius">The radius.</param>
/// <param name="Height">The height.</param>
/// <param name="Placement">Where the local frame sits.</param>
internal sealed record CylinderGeometry(double Radius, double Height, Transform Placement)
    : FakeGeometry
{
    internal override ResultAccuracy Accuracy => ResultAccuracy.Exact;

    internal override Bounds3d Bounds => new Bounds3d(
        new Vec3d(-Radius, -Radius, 0),
        new Vec3d(Radius, Radius, Height)).Transformed(Placement);

    internal override MassProperties Compute(double density)
    {
        double volume = System.Math.PI * Radius * Radius * Height;
        double area = (2.0 * System.Math.PI * Radius * Height) + (2.0 * System.Math.PI * Radius * Radius);
        double mass = volume * density;

        InertiaTensor local = new(
            mass * ((3.0 * Radius * Radius) + (Height * Height)) / 12.0,
            mass * ((3.0 * Radius * Radius) + (Height * Height)) / 12.0,
            mass * Radius * Radius / 2.0);

        return new MassProperties(
            volume,
            area,
            Placement.TransformPoint(new Vec3d(0, 0, Height / 2.0)),
            density,
            local.RotatedBy(Placement.Rotation),
            Accuracy);
    }

    internal override void Tessellate(MeshAccumulator mesh, TessellationOptions options, int faceIndex)
    {
        int segments = SegmentsForCircle(Radius, options);

        // Lateral surface: face 0.
        for (int i = 0; i < segments; i++)
        {
            double a0 = 2.0 * System.Math.PI * i / segments;
            double a1 = 2.0 * System.Math.PI * (i + 1) / segments;

            Vec3d n0 = Placement.TransformNormal(new Vec3d(System.Math.Cos(a0), System.Math.Sin(a0), 0));
            Vec3d n1 = Placement.TransformNormal(new Vec3d(System.Math.Cos(a1), System.Math.Sin(a1), 0));

            int b0 = mesh.AddVertex(LocalToWorld(a0, 0), n0);
            int b1 = mesh.AddVertex(LocalToWorld(a1, 0), n1);
            int t0 = mesh.AddVertex(LocalToWorld(a0, Height), n0);
            int t1 = mesh.AddVertex(LocalToWorld(a1, Height), n1);

            mesh.AddTriangle(b0, b1, t1, faceIndex);
            mesh.AddTriangle(b0, t1, t0, faceIndex);
        }

        TessellateCap(mesh, segments, 0.0, -Vec3d.UnitZ, faceIndex + 1);
        TessellateCap(mesh, segments, Height, Vec3d.UnitZ, faceIndex + 2);
    }

    private Vec3d LocalToWorld(double angle, double z)
        => Placement.TransformPoint(
            new Vec3d(Radius * System.Math.Cos(angle), Radius * System.Math.Sin(angle), z));

    private void TessellateCap(
        MeshAccumulator mesh,
        int segments,
        double z,
        Vec3d localNormal,
        int faceIndex)
    {
        Vec3d normal = Placement.TransformNormal(localNormal);
        int centre = mesh.AddVertex(Placement.TransformPoint(new Vec3d(0, 0, z)), normal);

        int[] ring = new int[segments];
        for (int i = 0; i < segments; i++)
        {
            double angle = 2.0 * System.Math.PI * i / segments;
            ring[i] = mesh.AddVertex(LocalToWorld(angle, z), normal);
        }

        for (int i = 0; i < segments; i++)
        {
            int next = (i + 1) % segments;
            if (localNormal.Z > 0)
            {
                mesh.AddTriangle(centre, ring[i], ring[next], faceIndex);
            }
            else
            {
                mesh.AddTriangle(centre, ring[next], ring[i], faceIndex);
            }
        }
    }
}

/// <summary>A sphere centred at the placement origin.</summary>
/// <param name="Radius">The radius.</param>
/// <param name="Placement">Where the local frame sits.</param>
internal sealed record SphereGeometry(double Radius, Transform Placement) : FakeGeometry
{
    internal override ResultAccuracy Accuracy => ResultAccuracy.Exact;

    internal override Bounds3d Bounds => new Bounds3d(
        new Vec3d(-Radius, -Radius, -Radius),
        new Vec3d(Radius, Radius, Radius)).Transformed(Placement);

    internal override MassProperties Compute(double density)
    {
        double volume = 4.0 / 3.0 * System.Math.PI * Radius * Radius * Radius;
        double area = 4.0 * System.Math.PI * Radius * Radius;
        double mass = volume * density;
        double moment = 2.0 * mass * Radius * Radius / 5.0;

        return new MassProperties(
            volume,
            area,
            Placement.TransformPoint(Vec3d.Zero),
            density,
            new InertiaTensor(moment, moment, moment),
            Accuracy);
    }

    internal override void Tessellate(MeshAccumulator mesh, TessellationOptions options, int faceIndex)
    {
        int segments = SegmentsForCircle(Radius, options);
        int rings = System.Math.Max(segments / 2, 4);

        int[,] grid = new int[rings + 1, segments + 1];

        for (int r = 0; r <= rings; r++)
        {
            double phi = System.Math.PI * r / rings;
            for (int s = 0; s <= segments; s++)
            {
                double theta = 2.0 * System.Math.PI * s / segments;
                Vec3d local = new(
                    System.Math.Sin(phi) * System.Math.Cos(theta),
                    System.Math.Sin(phi) * System.Math.Sin(theta),
                    System.Math.Cos(phi));

                grid[r, s] = mesh.AddVertex(
                    Placement.TransformPoint(local * Radius),
                    Placement.TransformNormal(local));
            }
        }

        for (int r = 0; r < rings; r++)
        {
            for (int s = 0; s < segments; s++)
            {
                mesh.AddTriangle(grid[r, s], grid[r + 1, s], grid[r + 1, s + 1], faceIndex);
                mesh.AddTriangle(grid[r, s], grid[r + 1, s + 1], grid[r, s + 1], faceIndex);
            }
        }
    }
}

/// <summary>A cone or truncated cone about the local Z axis.</summary>
/// <param name="BottomRadius">Radius at the base.</param>
/// <param name="TopRadius">Radius at the top.</param>
/// <param name="Height">The height.</param>
/// <param name="Placement">Where the local frame sits.</param>
internal sealed record ConeGeometry(
    double BottomRadius,
    double TopRadius,
    double Height,
    Transform Placement) : FakeGeometry
{
    internal override ResultAccuracy Accuracy => ResultAccuracy.Exact;

    internal override Bounds3d Bounds
    {
        get
        {
            double r = System.Math.Max(BottomRadius, TopRadius);
            return new Bounds3d(new Vec3d(-r, -r, 0), new Vec3d(r, r, Height)).Transformed(Placement);
        }
    }

    internal override MassProperties Compute(double density)
    {
        double r1 = BottomRadius;
        double r2 = TopRadius;

        // A frustum is a solid of revolution whose radius is linear in z, so every integral needed
        // here is a polynomial in z of degree at most four. Three-point Gauss-Legendre is exact for
        // degree five, so these are closed-form results computed by quadrature rather than
        // approximations -- which is why this geometry can honestly claim exactness.
        double volumeIntegral = Integrate(z => Radius(z) * Radius(z));
        double firstMoment = Integrate(z => z * Radius(z) * Radius(z));
        double quartic = Integrate(z => System.Math.Pow(Radius(z), 4));

        double volume = System.Math.PI * volumeIntegral;
        double slant = System.Math.Sqrt(((r1 - r2) * (r1 - r2)) + (Height * Height));
        double area = (System.Math.PI * (r1 + r2) * slant)
            + (System.Math.PI * r1 * r1)
            + (System.Math.PI * r2 * r2);

        double mass = volume * density;
        double centroidZ = volumeIntegral <= Tolerance.LinearResolution
            ? Height / 2.0
            : firstMoment / volumeIntegral;

        // Each disc contributes a quarter of its own diametral moment plus a parallel-axis term.
        double transverseIntegral = Integrate(
            z => Radius(z) * Radius(z) * (z - centroidZ) * (z - centroidZ));

        double izz = density * System.Math.PI * quartic / 2.0;
        double transverse = density * System.Math.PI * ((quartic / 4.0) + transverseIntegral);

        InertiaTensor local = new(transverse, transverse, izz);

        return new MassProperties(
            volume,
            area,
            Placement.TransformPoint(new Vec3d(0, 0, centroidZ)),
            density,
            local.RotatedBy(Placement.Rotation),
            Accuracy);
    }

    private double Radius(double z) => BottomRadius + ((TopRadius - BottomRadius) * z / Height);

    /// <summary>
    /// Integrates over the height with a three-point Gauss-Legendre rule, exact to degree five.
    /// </summary>
    private double Integrate(Func<double, double> integrand)
    {
        // Nodes and weights on [-1, 1] for the three-point rule.
        double node = System.Math.Sqrt(3.0 / 5.0);
        double half = Height / 2.0;

        double Map(double x) => half * (x + 1.0);

        return half * (
            (5.0 / 9.0 * integrand(Map(-node)))
            + (8.0 / 9.0 * integrand(Map(0.0)))
            + (5.0 / 9.0 * integrand(Map(node))));
    }

    internal override void Tessellate(MeshAccumulator mesh, TessellationOptions options, int faceIndex)
    {
        int segments = SegmentsForCircle(System.Math.Max(BottomRadius, TopRadius), options);

        for (int i = 0; i < segments; i++)
        {
            double a0 = 2.0 * System.Math.PI * i / segments;
            double a1 = 2.0 * System.Math.PI * (i + 1) / segments;

            Vec3d n0 = SlantNormal(a0);
            Vec3d n1 = SlantNormal(a1);

            int b0 = mesh.AddVertex(Point(a0, BottomRadius, 0), n0);
            int b1 = mesh.AddVertex(Point(a1, BottomRadius, 0), n1);
            int t0 = mesh.AddVertex(Point(a0, TopRadius, Height), n0);
            int t1 = mesh.AddVertex(Point(a1, TopRadius, Height), n1);

            mesh.AddTriangle(b0, b1, t1, faceIndex);
            mesh.AddTriangle(b0, t1, t0, faceIndex);
        }

        int next = faceIndex + 1;
        if (BottomRadius > Tolerance.LinearResolution)
        {
            TessellateCap(mesh, segments, BottomRadius, 0.0, -Vec3d.UnitZ, next++);
        }

        if (TopRadius > Tolerance.LinearResolution)
        {
            TessellateCap(mesh, segments, TopRadius, Height, Vec3d.UnitZ, next);
        }
    }

    private Vec3d Point(double angle, double radius, double z)
        => Placement.TransformPoint(
            new Vec3d(radius * System.Math.Cos(angle), radius * System.Math.Sin(angle), z));

    private Vec3d SlantNormal(double angle)
    {
        // Perpendicular to the slant line, in the plane containing the axis and this angle.
        double dr = TopRadius - BottomRadius;
        Vec3d local = new(
            Height * System.Math.Cos(angle),
            Height * System.Math.Sin(angle),
            -dr);

        return local.TryNormalize(out Vec3d unit)
            ? Placement.TransformNormal(unit)
            : Placement.TransformNormal(Vec3d.UnitZ);
    }

    private void TessellateCap(
        MeshAccumulator mesh,
        int segments,
        double radius,
        double z,
        Vec3d localNormal,
        int faceIndex)
    {
        Vec3d normal = Placement.TransformNormal(localNormal);
        int centre = mesh.AddVertex(Placement.TransformPoint(new Vec3d(0, 0, z)), normal);

        int[] ring = new int[segments];
        for (int i = 0; i < segments; i++)
        {
            ring[i] = mesh.AddVertex(Point(2.0 * System.Math.PI * i / segments, radius, z), normal);
        }

        for (int i = 0; i < segments; i++)
        {
            int nextIndex = (i + 1) % segments;
            if (localNormal.Z > 0)
            {
                mesh.AddTriangle(centre, ring[i], ring[nextIndex], faceIndex);
            }
            else
            {
                mesh.AddTriangle(centre, ring[nextIndex], ring[i], faceIndex);
            }
        }
    }
}

/// <summary>A torus about the local Z axis, centred at the placement origin.</summary>
/// <param name="MajorRadius">Distance from the axis to the tube centre.</param>
/// <param name="MinorRadius">The tube radius.</param>
/// <param name="Placement">Where the local frame sits.</param>
internal sealed record TorusGeometry(
    double MajorRadius,
    double MinorRadius,
    Transform Placement) : FakeGeometry
{
    internal override ResultAccuracy Accuracy => ResultAccuracy.Exact;

    internal override Bounds3d Bounds
    {
        get
        {
            double outer = MajorRadius + MinorRadius;
            return new Bounds3d(
                new Vec3d(-outer, -outer, -MinorRadius),
                new Vec3d(outer, outer, MinorRadius)).Transformed(Placement);
        }
    }

    internal override MassProperties Compute(double density)
    {
        double volume = 2.0 * System.Math.PI * System.Math.PI * MajorRadius * MinorRadius * MinorRadius;
        double area = 4.0 * System.Math.PI * System.Math.PI * MajorRadius * MinorRadius;
        double mass = volume * density;

        double izz = mass * ((MajorRadius * MajorRadius) + (0.75 * MinorRadius * MinorRadius));
        double transverse = mass
            * ((0.5 * MajorRadius * MajorRadius) + (0.625 * MinorRadius * MinorRadius));

        InertiaTensor local = new(transverse, transverse, izz);

        return new MassProperties(
            volume,
            area,
            Placement.TransformPoint(Vec3d.Zero),
            density,
            local.RotatedBy(Placement.Rotation),
            Accuracy);
    }

    internal override void Tessellate(MeshAccumulator mesh, TessellationOptions options, int faceIndex)
    {
        int major = SegmentsForCircle(MajorRadius + MinorRadius, options);
        int minor = SegmentsForCircle(MinorRadius, options);

        int[,] grid = new int[major + 1, minor + 1];

        for (int i = 0; i <= major; i++)
        {
            double u = 2.0 * System.Math.PI * i / major;
            for (int j = 0; j <= minor; j++)
            {
                double v = 2.0 * System.Math.PI * j / minor;

                Vec3d tubeCentre = new(
                    MajorRadius * System.Math.Cos(u),
                    MajorRadius * System.Math.Sin(u),
                    0);

                Vec3d normal = new(
                    System.Math.Cos(u) * System.Math.Cos(v),
                    System.Math.Sin(u) * System.Math.Cos(v),
                    System.Math.Sin(v));

                grid[i, j] = mesh.AddVertex(
                    Placement.TransformPoint(tubeCentre + (normal * MinorRadius)),
                    Placement.TransformNormal(normal));
            }
        }

        for (int i = 0; i < major; i++)
        {
            for (int j = 0; j < minor; j++)
            {
                mesh.AddTriangle(grid[i, j], grid[i + 1, j], grid[i + 1, j + 1], faceIndex);
                mesh.AddTriangle(grid[i, j], grid[i + 1, j + 1], grid[i, j + 1], faceIndex);
            }
        }
    }
}

/// <summary>A planar profile face, the thing sweeps consume.</summary>
/// <param name="Points">The profile outline in the frame's XY plane.</param>
/// <param name="Frame">Maps the profile plane into the world.</param>
internal sealed record ProfileGeometry(ImmutableArray<Vec2d> Points, Transform Frame) : FakeGeometry
{
    internal override ResultAccuracy Accuracy => ResultAccuracy.Exact;

    internal override Bounds3d Bounds
        => Bounds3d.FromPoints(Points.Select(p => Frame.TransformPoint(new Vec3d(p.X, p.Y, 0))));

    internal override MassProperties Compute(double density)
    {
        // A profile has no volume; its "area" is the enclosed region, which is what a caller
        // asking about a profile actually wants.
        double area = Polygon2d.Area(Points);
        Vec2d centroid = Polygon2d.Centroid(Points);

        return new MassProperties(
            0.0,
            area,
            Frame.TransformPoint(new Vec3d(centroid.X, centroid.Y, 0)),
            density,
            InertiaTensor.Zero,
            Accuracy);
    }

    internal override void Tessellate(MeshAccumulator mesh, TessellationOptions options, int faceIndex)
    {
        Vec3d normal = Frame.TransformNormal(Vec3d.UnitZ);

        int[] indices = new int[Points.Length];
        for (int i = 0; i < Points.Length; i++)
        {
            indices[i] = mesh.AddVertex(
                Frame.TransformPoint(new Vec3d(Points[i].X, Points[i].Y, 0)),
                normal);
        }

        ImmutableArray<int> triangles = Polygon2d.Triangulate(Points);
        for (int i = 0; i + 2 < triangles.Length; i += 3)
        {
            mesh.AddTriangle(
                indices[triangles[i]], indices[triangles[i + 1]], indices[triangles[i + 2]], faceIndex);
        }
    }
}

/// <summary>A prism: a polygon profile swept along a straight path.</summary>
/// <param name="Points">The profile outline in the frame's XY plane.</param>
/// <param name="Frame">Maps the profile plane into the world.</param>
/// <param name="Direction">The unit sweep direction in world space.</param>
/// <param name="Distance">How far the profile was swept.</param>
internal sealed record PrismGeometry(
    ImmutableArray<Vec2d> Points,
    Transform Frame,
    Vec3d Direction,
    double Distance) : FakeGeometry
{
    /// <summary>
    /// Gets a value indicating whether the sweep runs along the profile normal.
    /// </summary>
    /// <remarks>
    /// A right prism is modelled exactly. An oblique one -- swept at an angle to its profile --
    /// has the same volume but a different inertia tensor, which the right-prism formulae below do
    /// not capture, so those results are reported as approximate rather than quietly wrong.
    /// </remarks>
    private bool IsRightPrism
        => Direction.IsParallelTo(Frame.TransformNormal(Vec3d.UnitZ), 1e-6);

    internal override ResultAccuracy Accuracy
        => IsRightPrism ? ResultAccuracy.Exact : ResultAccuracy.Approximate;

    internal override Bounds3d Bounds
    {
        get
        {
            Bounds3d bottom = Bounds3d.FromPoints(
                Points.Select(p => Frame.TransformPoint(new Vec3d(p.X, p.Y, 0))));

            Bounds3d top = Bounds3d.FromPoints(
                Points.Select(p =>
                    Frame.TransformPoint(new Vec3d(p.X, p.Y, 0)) + (Direction * Distance)));

            return Bounds3d.Union(bottom, top);
        }
    }

    internal override MassProperties Compute(double density)
    {
        double area = Polygon2d.Area(Points);
        double perimeter = Polygon2d.Perimeter(Points);
        Vec2d centroid2d = Polygon2d.Centroid(Points);

        // Only the component of the sweep along the profile normal adds volume. A sweep at an
        // angle produces an oblique prism, whose volume is base area times perpendicular height.
        Vec3d normal = Frame.TransformNormal(Vec3d.UnitZ);
        double perpendicularHeight = System.Math.Abs(Vec3d.Dot(Direction, normal)) * Distance;

        double volume = area * perpendicularHeight;
        double surfaceArea = (2.0 * area) + (perimeter * Distance);
        double mass = volume * density;

        Polygon2d.SecondMomentsAboutCentroid(Points, out double ixx, out double iyy, out double ixy);

        // Prism about its own centroid, in the profile frame with the sweep along local Z.
        double heightSquared = perpendicularHeight * perpendicularHeight;
        InertiaTensor local = new(
            (density * perpendicularHeight * ixx) + (mass * heightSquared / 12.0),
            (density * perpendicularHeight * iyy) + (mass * heightSquared / 12.0),
            density * perpendicularHeight * (ixx + iyy),
            density * perpendicularHeight * ixy);

        Vec3d baseCentroid = Frame.TransformPoint(new Vec3d(centroid2d.X, centroid2d.Y, 0));
        Vec3d centroid = baseCentroid + (Direction * (Distance / 2.0));

        return new MassProperties(
            volume,
            surfaceArea,
            centroid,
            density,
            local.RotatedBy(Frame.Rotation),
            Accuracy,
            IsRightPrism ? 0.0 : 0.05);
    }

    internal override void Tessellate(MeshAccumulator mesh, TessellationOptions options, int faceIndex)
    {
        Vec3d offset = Direction * Distance;
        Vec3d normal = Frame.TransformNormal(Vec3d.UnitZ);

        Vec3d[] bottom = new Vec3d[Points.Length];
        Vec3d[] top = new Vec3d[Points.Length];
        for (int i = 0; i < Points.Length; i++)
        {
            bottom[i] = Frame.TransformPoint(new Vec3d(Points[i].X, Points[i].Y, 0));
            top[i] = bottom[i] + offset;
        }

        // Side walls: one face per profile edge, matching the topology built for a prism.
        for (int i = 0; i < Points.Length; i++)
        {
            int next = (i + 1) % Points.Length;
            Vec3d along = bottom[next] - bottom[i];
            Vec3d wallNormal = Vec3d.Cross(along, offset);
            if (!wallNormal.TryNormalize(out wallNormal))
            {
                wallNormal = normal;
            }

            int b0 = mesh.AddVertex(bottom[i], wallNormal);
            int b1 = mesh.AddVertex(bottom[next], wallNormal);
            int t1 = mesh.AddVertex(top[next], wallNormal);
            int t0 = mesh.AddVertex(top[i], wallNormal);

            mesh.AddTriangle(b0, b1, t1, faceIndex + i);
            mesh.AddTriangle(b0, t1, t0, faceIndex + i);
        }

        ImmutableArray<int> triangles = Polygon2d.Triangulate(Points);

        int startCapFace = faceIndex + Points.Length;
        int endCapFace = startCapFace + 1;

        int[] bottomIndices = new int[Points.Length];
        int[] topIndices = new int[Points.Length];
        for (int i = 0; i < Points.Length; i++)
        {
            bottomIndices[i] = mesh.AddVertex(bottom[i], -normal);
            topIndices[i] = mesh.AddVertex(top[i], normal);
        }

        for (int i = 0; i + 2 < triangles.Length; i += 3)
        {
            // Start cap wound the other way so its outward normal points away from the sweep.
            mesh.AddTriangle(
                bottomIndices[triangles[i]],
                bottomIndices[triangles[i + 2]],
                bottomIndices[triangles[i + 1]],
                startCapFace);

            mesh.AddTriangle(
                topIndices[triangles[i]],
                topIndices[triangles[i + 1]],
                topIndices[triangles[i + 2]],
                endCapFace);
        }
    }
}
