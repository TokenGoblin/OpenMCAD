using System.Collections.Immutable;
using OpenMCAD.Math;

namespace OpenMCAD.Kernel.Fake.Geometry;

/// <summary>
/// Exact area and second-moment properties of a simple polygon, plus triangulation.
/// </summary>
/// <remarks>
/// <para>
/// These are the closed-form Green's-theorem formulae, so a prism's mass properties come out exact
/// rather than approximate. That is what lets <c>FakeKernel</c> claim exactness for extrusions and
/// lets the contract tests demand agreement with OCCT to full double precision on those cases.
/// </para>
/// <para>
/// "Simple" means non-self-intersecting. Self-intersecting input produces nonsense here rather than
/// an error; <c>PolygonProfileDefinition.Validate</c> is where that is caught.
/// </para>
/// </remarks>
internal static class Polygon2d
{
    /// <summary>Returns twice the signed area, positive when wound counter-clockwise.</summary>
    /// <param name="points">The polygon vertices, without a repeated closing point.</param>
    internal static double SignedDoubleArea(ImmutableArray<Vec2d> points)
    {
        double total = 0.0;
        for (int i = 0; i < points.Length; i++)
        {
            Vec2d a = points[i];
            Vec2d b = points[(i + 1) % points.Length];
            total += Vec2d.Cross(a, b);
        }

        return total;
    }

    /// <summary>Returns the unsigned area.</summary>
    /// <param name="points">The polygon vertices.</param>
    internal static double Area(ImmutableArray<Vec2d> points)
        => System.Math.Abs(SignedDoubleArea(points)) * 0.5;

    /// <summary>Returns the perimeter length.</summary>
    /// <param name="points">The polygon vertices.</param>
    internal static double Perimeter(ImmutableArray<Vec2d> points)
    {
        double total = 0.0;
        for (int i = 0; i < points.Length; i++)
        {
            total += Vec2d.Distance(points[i], points[(i + 1) % points.Length]);
        }

        return total;
    }

    /// <summary>Returns the area centroid.</summary>
    /// <param name="points">The polygon vertices.</param>
    internal static Vec2d Centroid(ImmutableArray<Vec2d> points)
    {
        double doubleArea = SignedDoubleArea(points);
        if (System.Math.Abs(doubleArea) <= Tolerance.LinearResolution)
        {
            // Degenerate: fall back to the vertex average so the result is finite and stable
            // rather than an infinity that propagates.
            Vec2d sum = Vec2d.Zero;
            foreach (Vec2d point in points)
            {
                sum += point;
            }

            return sum / points.Length;
        }

        double cx = 0.0;
        double cy = 0.0;
        for (int i = 0; i < points.Length; i++)
        {
            Vec2d a = points[i];
            Vec2d b = points[(i + 1) % points.Length];
            double cross = Vec2d.Cross(a, b);
            cx += (a.X + b.X) * cross;
            cy += (a.Y + b.Y) * cross;
        }

        return new Vec2d(cx / (3.0 * doubleArea), cy / (3.0 * doubleArea));
    }

    /// <summary>
    /// Returns the second moments of area about the centroid.
    /// </summary>
    /// <param name="points">The polygon vertices.</param>
    /// <param name="ixx">The integral of y squared over the area.</param>
    /// <param name="iyy">The integral of x squared over the area.</param>
    /// <param name="ixy">The integral of x times y over the area.</param>
    internal static void SecondMomentsAboutCentroid(
        ImmutableArray<Vec2d> points,
        out double ixx,
        out double iyy,
        out double ixy)
    {
        double doubleArea = SignedDoubleArea(points);
        double sign = doubleArea < 0 ? -1.0 : 1.0;

        double sxx = 0.0;
        double syy = 0.0;
        double sxy = 0.0;

        for (int i = 0; i < points.Length; i++)
        {
            Vec2d a = points[i];
            Vec2d b = points[(i + 1) % points.Length];
            double cross = Vec2d.Cross(a, b);

            sxx += ((a.Y * a.Y) + (a.Y * b.Y) + (b.Y * b.Y)) * cross;
            syy += ((a.X * a.X) + (a.X * b.X) + (b.X * b.X)) * cross;
            sxy += ((a.X * b.Y) + (2.0 * a.X * a.Y) + (2.0 * b.X * b.Y) + (b.X * a.Y)) * cross;
        }

        // About the origin, then shifted to the centroid by the parallel axis theorem.
        double area = System.Math.Abs(doubleArea) * 0.5;
        Vec2d centroid = Centroid(points);

        ixx = (sign * sxx / 12.0) - (area * centroid.Y * centroid.Y);
        iyy = (sign * syy / 12.0) - (area * centroid.X * centroid.X);
        ixy = (sign * sxy / 24.0) - (area * centroid.X * centroid.Y);
    }

    /// <summary>
    /// Triangulates a simple polygon by ear clipping.
    /// </summary>
    /// <param name="points">The polygon vertices.</param>
    /// <returns>Vertex index triples, wound counter-clockwise.</returns>
    /// <remarks>
    /// <para>
    /// Ear clipping rather than a centroid fan, because a fan is only correct for convex or
    /// star-shaped polygons and the naming corpus in Phase 3 needs concave profiles — an L-shaped
    /// section is the standard way to produce a face that a later feature splits.
    /// </para>
    /// <para>
    /// O(n squared), which is irrelevant: this runs on test profiles with a handful of vertices, and
    /// real tessellation comes from OCCT.
    /// </para>
    /// </remarks>
    internal static ImmutableArray<int> Triangulate(ImmutableArray<Vec2d> points)
    {
        if (points.Length < 3)
        {
            return [];
        }

        // Work counter-clockwise so the ear test has a consistent sense.
        List<int> remaining = [.. Enumerable.Range(0, points.Length)];
        if (SignedDoubleArea(points) < 0)
        {
            remaining.Reverse();
        }

        ImmutableArray<int>.Builder triangles = ImmutableArray.CreateBuilder<int>();
        int guard = points.Length * points.Length;

        while (remaining.Count > 3 && guard-- > 0)
        {
            bool clipped = false;

            for (int i = 0; i < remaining.Count; i++)
            {
                int previous = remaining[(i - 1 + remaining.Count) % remaining.Count];
                int current = remaining[i];
                int next = remaining[(i + 1) % remaining.Count];

                if (!IsEar(points, remaining, previous, current, next))
                {
                    continue;
                }

                triangles.Add(previous);
                triangles.Add(current);
                triangles.Add(next);
                remaining.RemoveAt(i);
                clipped = true;
                break;
            }

            if (!clipped)
            {
                // No ear found. The polygon is self-intersecting or degenerate, which validation
                // should have rejected. Emit what is left as a fan rather than looping forever;
                // FakeKernel tessellation is explicitly approximate and a wrong mesh here is
                // better than a hang.
                break;
            }
        }

        for (int i = 1; i + 1 < remaining.Count; i++)
        {
            triangles.Add(remaining[0]);
            triangles.Add(remaining[i]);
            triangles.Add(remaining[i + 1]);
        }

        return triangles.ToImmutable();
    }

    private static bool IsEar(
        ImmutableArray<Vec2d> points,
        List<int> remaining,
        int previous,
        int current,
        int next)
    {
        Vec2d a = points[previous];
        Vec2d b = points[current];
        Vec2d c = points[next];

        // Reflex vertices cannot be ears.
        if (Vec2d.Cross(b - a, c - b) <= 0)
        {
            return false;
        }

        foreach (int index in remaining)
        {
            if (index == previous || index == current || index == next)
            {
                continue;
            }

            if (IsInsideTriangle(points[index], a, b, c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsInsideTriangle(Vec2d point, Vec2d a, Vec2d b, Vec2d c)
    {
        double d1 = Vec2d.Cross(b - a, point - a);
        double d2 = Vec2d.Cross(c - b, point - b);
        double d3 = Vec2d.Cross(a - c, point - c);

        bool anyNegative = d1 < 0 || d2 < 0 || d3 < 0;
        bool anyPositive = d1 > 0 || d2 > 0 || d3 > 0;
        return !(anyNegative && anyPositive);
    }
}
