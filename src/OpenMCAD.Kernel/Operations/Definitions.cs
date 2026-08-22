using System.Collections.Immutable;
using OpenMCAD.Math;

namespace OpenMCAD.Kernel.Operations;

/// <summary>
/// The input to a kernel operation, in a form that can be validated before the kernel is touched.
/// </summary>
/// <remarks>
/// <para>
/// Definitions are plain data with a <see cref="Validate"/> method, deliberately separate from the
/// kernel that executes them. Three things fall out of that:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Both kernels reject the same bad input identically.</b> Validation lives here, not in each
/// implementation, so the contract tests can assert that <c>FakeKernel</c> and <c>OcctKernel</c>
/// agree on what is invalid — which is a large part of what makes the abstraction real rather than
/// nominal (ADR-0002).
/// </description></item>
/// <item><description>
/// <b>Obviously invalid input never reaches the kernel.</b> P7-T17 requires pre-flight validation
/// for every feature, because a good message costs nothing here and is nearly impossible to
/// reconstruct from a kernel exception afterwards.
/// </description></item>
/// <item><description>
/// A definition is hashable, which is what the geometry cache in P3-T05 is keyed on.
/// </description></item>
/// </list>
/// </remarks>
public interface IOperationDefinition
{
    /// <summary>Gets the stable operation name, used for logging, metrics, and cache keys.</summary>
    string OperationName { get; }

    /// <summary>
    /// Checks the definition without touching the kernel.
    /// </summary>
    /// <returns>
    /// The problems found, empty if the definition is well formed. Anything of severity
    /// <see cref="DiagnosticSeverity.Error"/> means the operation must not be attempted.
    /// </returns>
    ImmutableArray<KernelDiagnostic> Validate();
}

/// <summary>Shared validation helpers for operation definitions.</summary>
internal static class DefinitionValidation
{
    internal static void RequirePositive(
        List<KernelDiagnostic> into,
        double value,
        string what)
    {
        if (!double.IsFinite(value) || value <= Tolerance.LinearResolution)
        {
            into.Add(KernelDiagnostic.Error(
                KernelDiagnosticCodes.InvalidDimension,
                $"{what} must be greater than zero, but is {Format(value)}."));
        }
    }

    internal static void RequireNonNegative(
        List<KernelDiagnostic> into,
        double value,
        string what)
    {
        if (!double.IsFinite(value) || value < 0.0)
        {
            into.Add(KernelDiagnostic.Error(
                KernelDiagnosticCodes.InvalidDimension,
                $"{what} cannot be negative, but is {Format(value)}."));
        }
    }

    internal static void RequireDirection(
        List<KernelDiagnostic> into,
        Vec3d direction,
        string what)
    {
        if (!direction.IsFinite || direction.IsZeroLength)
        {
            into.Add(KernelDiagnostic.Error(
                KernelDiagnosticCodes.DegenerateDirection,
                $"{what} must be a non-zero direction, but is {direction}."));
        }
    }

    internal static void RequireShape(List<KernelDiagnostic> into, KernelShape shape, string what)
    {
        if (!shape.IsValid)
        {
            into.Add(KernelDiagnostic.Error(
                KernelDiagnosticCodes.EmptySelection,
                $"{what} was not supplied."));
        }
    }

    internal static void RequireEntityKind(
        List<KernelDiagnostic> into,
        SubEntity entity,
        SubEntityKind expected,
        KernelShape owner,
        string what)
    {
        if (!entity.IsValid)
        {
            into.Add(KernelDiagnostic.Error(
                KernelDiagnosticCodes.EmptySelection,
                $"{what} was not supplied."));
            return;
        }

        if (entity.Kind != expected)
        {
            into.Add(KernelDiagnostic.Error(
                KernelDiagnosticCodes.WrongEntityKind,
                $"{what} must be {expected.ToString().ToLowerInvariant()}, but a "
                + $"{entity.Kind.ToString().ToLowerInvariant()} was given.",
                [entity]));
        }

        if (owner.IsValid && entity.Owner != owner)
        {
            into.Add(KernelDiagnostic.Error(
                KernelDiagnosticCodes.EntityNotInShape,
                $"{what} belongs to a different body than the one being modified.",
                [entity]));
        }
    }

    internal static string Format(double value)
        => value.ToString("G6", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// A rectangular box, with its minimum corner at the local origin and edges along the local axes.
/// </summary>
/// <param name="SizeX">Extent along local X, in metres.</param>
/// <param name="SizeY">Extent along local Y, in metres.</param>
/// <param name="SizeZ">Extent along local Z, in metres.</param>
/// <param name="Placement">Where the local frame sits in the world.</param>
/// <remarks>
/// Faces are produced in the canonical order minus-X, plus-X, minus-Y, plus-Y, minus-Z, plus-Z, and
/// carry <see cref="OperationRole.PrimitiveFace"/> with that ordinal. The order is part of the
/// contract, not an implementation detail: names refer to it, so changing it would repoint every
/// stored reference to a box face.
/// </remarks>
public sealed record BoxDefinition(
    double SizeX,
    double SizeY,
    double SizeZ,
    Transform Placement) : IOperationDefinition
{
    /// <summary>Creates a box at the world origin.</summary>
    /// <param name="sizeX">Extent along X.</param>
    /// <param name="sizeY">Extent along Y.</param>
    /// <param name="sizeZ">Extent along Z.</param>
    public BoxDefinition(double sizeX, double sizeY, double sizeZ)
        : this(sizeX, sizeY, sizeZ, Transform.Identity)
    {
    }

    /// <inheritdoc />
    public string OperationName => "CreateBox";

    /// <inheritdoc />
    public ImmutableArray<KernelDiagnostic> Validate()
    {
        List<KernelDiagnostic> problems = [];
        DefinitionValidation.RequirePositive(problems, SizeX, "Box width (X)");
        DefinitionValidation.RequirePositive(problems, SizeY, "Box depth (Y)");
        DefinitionValidation.RequirePositive(problems, SizeZ, "Box height (Z)");
        return [.. problems];
    }
}

/// <summary>
/// A cylinder about the local Z axis, with its base circle centred on the local origin.
/// </summary>
/// <param name="Radius">Radius in metres.</param>
/// <param name="Height">Height along local Z, in metres.</param>
/// <param name="Placement">Where the local frame sits in the world.</param>
public sealed record CylinderDefinition(
    double Radius,
    double Height,
    Transform Placement) : IOperationDefinition
{
    /// <summary>Creates a cylinder at the world origin.</summary>
    /// <param name="radius">Radius in metres.</param>
    /// <param name="height">Height in metres.</param>
    public CylinderDefinition(double radius, double height)
        : this(radius, height, Transform.Identity)
    {
    }

    /// <inheritdoc />
    public string OperationName => "CreateCylinder";

    /// <inheritdoc />
    public ImmutableArray<KernelDiagnostic> Validate()
    {
        List<KernelDiagnostic> problems = [];
        DefinitionValidation.RequirePositive(problems, Radius, "Cylinder radius");
        DefinitionValidation.RequirePositive(problems, Height, "Cylinder height");
        return [.. problems];
    }
}

/// <summary>A sphere centred on the local origin.</summary>
/// <param name="Radius">Radius in metres.</param>
/// <param name="Placement">Where the local frame sits in the world.</param>
public sealed record SphereDefinition(double Radius, Transform Placement) : IOperationDefinition
{
    /// <summary>Creates a sphere at the world origin.</summary>
    /// <param name="radius">Radius in metres.</param>
    public SphereDefinition(double radius)
        : this(radius, Transform.Identity)
    {
    }

    /// <inheritdoc />
    public string OperationName => "CreateSphere";

    /// <inheritdoc />
    public ImmutableArray<KernelDiagnostic> Validate()
    {
        List<KernelDiagnostic> problems = [];
        DefinitionValidation.RequirePositive(problems, Radius, "Sphere radius");
        return [.. problems];
    }
}

/// <summary>
/// A cone or truncated cone about the local Z axis, with its base circle centred on the local
/// origin.
/// </summary>
/// <param name="BottomRadius">Radius at the base, in metres. May be zero for a point.</param>
/// <param name="TopRadius">Radius at the top, in metres. May be zero for a point.</param>
/// <param name="Height">Height along local Z, in metres.</param>
/// <param name="Placement">Where the local frame sits in the world.</param>
public sealed record ConeDefinition(
    double BottomRadius,
    double TopRadius,
    double Height,
    Transform Placement) : IOperationDefinition
{
    /// <summary>Creates a cone at the world origin.</summary>
    /// <param name="bottomRadius">Radius at the base.</param>
    /// <param name="topRadius">Radius at the top.</param>
    /// <param name="height">Height in metres.</param>
    public ConeDefinition(double bottomRadius, double topRadius, double height)
        : this(bottomRadius, topRadius, height, Transform.Identity)
    {
    }

    /// <inheritdoc />
    public string OperationName => "CreateCone";

    /// <inheritdoc />
    public ImmutableArray<KernelDiagnostic> Validate()
    {
        List<KernelDiagnostic> problems = [];
        DefinitionValidation.RequireNonNegative(problems, BottomRadius, "Cone bottom radius");
        DefinitionValidation.RequireNonNegative(problems, TopRadius, "Cone top radius");
        DefinitionValidation.RequirePositive(problems, Height, "Cone height");

        if (BottomRadius <= Tolerance.LinearResolution && TopRadius <= Tolerance.LinearResolution)
        {
            problems.Add(KernelDiagnostic.Error(
                KernelDiagnosticCodes.InvalidDimension,
                "A cone needs at least one non-zero radius; both are zero, which describes a line."));
        }

        return [.. problems];
    }
}

/// <summary>A torus about the local Z axis, centred on the local origin.</summary>
/// <param name="MajorRadius">Distance from the axis to the tube centre, in metres.</param>
/// <param name="MinorRadius">Tube radius, in metres.</param>
/// <param name="Placement">Where the local frame sits in the world.</param>
public sealed record TorusDefinition(
    double MajorRadius,
    double MinorRadius,
    Transform Placement) : IOperationDefinition
{
    /// <summary>Creates a torus at the world origin.</summary>
    /// <param name="majorRadius">Distance from the axis to the tube centre.</param>
    /// <param name="minorRadius">Tube radius.</param>
    public TorusDefinition(double majorRadius, double minorRadius)
        : this(majorRadius, minorRadius, Transform.Identity)
    {
    }

    /// <inheritdoc />
    public string OperationName => "CreateTorus";

    /// <inheritdoc />
    public ImmutableArray<KernelDiagnostic> Validate()
    {
        List<KernelDiagnostic> problems = [];
        DefinitionValidation.RequirePositive(problems, MajorRadius, "Torus major radius");
        DefinitionValidation.RequirePositive(problems, MinorRadius, "Torus minor radius");

        if (MinorRadius >= MajorRadius)
        {
            problems.Add(KernelDiagnostic.Error(
                KernelDiagnosticCodes.InvalidDimension,
                $"The torus tube radius ({DefinitionValidation.Format(MinorRadius)} m) must be "
                + $"smaller than the major radius ({DefinitionValidation.Format(MajorRadius)} m), "
                + "or the tube passes through the axis and self-intersects."));
        }

        return [.. problems];
    }
}

/// <summary>
/// A closed planar profile, as scaffolding until real sketches arrive.
/// </summary>
/// <param name="Points">
/// The profile vertices in the frame's XY plane, in order. The closing segment from the last point
/// back to the first is implied and must not be repeated.
/// </param>
/// <param name="Frame">Maps the profile's local XY plane into the world.</param>
/// <remarks>
/// <b>Phase 1 scaffolding.</b> Extrude and revolve need something to sweep, and the sketcher does
/// not exist until Phase 4. This is deliberately the crudest thing that suffices — a closed
/// polygon — so that no design effort is spent on a profile representation that P4 will replace
/// with constrained sketch geometry.
/// </remarks>
public sealed record PolygonProfileDefinition(
    ImmutableArray<Vec2d> Points,
    Transform Frame) : IOperationDefinition
{
    /// <inheritdoc />
    public string OperationName => "CreatePolygonProfile";

    /// <summary>Gets the signed area of the polygon, positive when wound counter-clockwise.</summary>
    public double SignedArea()
    {
        if (Points.IsDefaultOrEmpty)
        {
            return 0.0;
        }

        double total = 0.0;
        for (int i = 0; i < Points.Length; i++)
        {
            Vec2d a = Points[i];
            Vec2d b = Points[(i + 1) % Points.Length];
            total += Vec2d.Cross(a, b);
        }

        return total / 2.0;
    }

    /// <inheritdoc />
    public ImmutableArray<KernelDiagnostic> Validate()
    {
        List<KernelDiagnostic> problems = [];

        if (Points.IsDefaultOrEmpty || Points.Length < 3)
        {
            problems.Add(KernelDiagnostic.Error(
                KernelDiagnosticCodes.InvalidProfile,
                $"A closed profile needs at least three points, but {(Points.IsDefault ? 0 : Points.Length)} were given."));
            return [.. problems];
        }

        for (int i = 0; i < Points.Length; i++)
        {
            if (!Points[i].IsFinite)
            {
                problems.Add(KernelDiagnostic.Error(
                    KernelDiagnosticCodes.InvalidProfile,
                    $"Profile point {i} is not a finite coordinate."));
            }

            Vec2d next = Points[(i + 1) % Points.Length];
            if (Points[i].IsNear(next, Tolerance.LinearResolution))
            {
                problems.Add(KernelDiagnostic.Error(
                    KernelDiagnosticCodes.InvalidProfile,
                    $"Profile points {i} and {(i + 1) % Points.Length} are coincident, which makes "
                    + "a zero-length segment."));
            }
        }

        if (System.Math.Abs(SignedArea()) <= Tolerance.LinearResolution)
        {
            problems.Add(KernelDiagnostic.Error(
                KernelDiagnosticCodes.InvalidProfile,
                "The profile encloses no area; its points are collinear."));
        }

        return [.. problems];
    }
}

/// <summary>Extrudes a profile along a direction.</summary>
/// <param name="Profile">The profile shape to sweep.</param>
/// <param name="Direction">The sweep direction. Need not be unit length.</param>
/// <param name="Distance">How far to sweep, in metres.</param>
/// <param name="Capped">Whether to cap the ends, producing a solid rather than a shell.</param>
/// <remarks>
/// Phase 1 supports a blind extrude only. The full set of end conditions — through-all,
/// up-to-face, up-to-body, midplane, two-direction, draft, thin — lands at P7-T01.
/// </remarks>
public sealed record ExtrudeDefinition(
    KernelShape Profile,
    Vec3d Direction,
    double Distance,
    bool Capped = true) : IOperationDefinition
{
    /// <inheritdoc />
    public string OperationName => "Extrude";

    /// <inheritdoc />
    public ImmutableArray<KernelDiagnostic> Validate()
    {
        List<KernelDiagnostic> problems = [];
        DefinitionValidation.RequireShape(problems, Profile, "The profile to extrude");
        DefinitionValidation.RequireDirection(problems, Direction, "The extrude direction");
        DefinitionValidation.RequirePositive(problems, Distance, "Extrude distance");
        return [.. problems];
    }
}

/// <summary>Revolves a profile about an axis.</summary>
/// <param name="Profile">The profile shape to sweep.</param>
/// <param name="AxisPoint">A point on the axis of revolution.</param>
/// <param name="AxisDirection">The axis direction. Need not be unit length.</param>
/// <param name="Angle">How far to revolve, in radians.</param>
/// <param name="Capped">Whether to cap the ends of a partial revolution.</param>
public sealed record RevolveDefinition(
    KernelShape Profile,
    Vec3d AxisPoint,
    Vec3d AxisDirection,
    double Angle,
    bool Capped = true) : IOperationDefinition
{
    /// <summary>Gets a value indicating whether this is a full revolution.</summary>
    public bool IsFullRevolution
        => System.Math.Abs(System.Math.Abs(Angle) - (2.0 * System.Math.PI)) <= Tolerance.Angular;

    /// <inheritdoc />
    public string OperationName => "Revolve";

    /// <inheritdoc />
    public ImmutableArray<KernelDiagnostic> Validate()
    {
        List<KernelDiagnostic> problems = [];
        DefinitionValidation.RequireShape(problems, Profile, "The profile to revolve");
        DefinitionValidation.RequireDirection(problems, AxisDirection, "The axis of revolution");

        if (!double.IsFinite(Angle) || System.Math.Abs(Angle) <= Tolerance.Angular)
        {
            problems.Add(KernelDiagnostic.Error(
                KernelDiagnosticCodes.InvalidAngle,
                $"The revolve angle must be non-zero, but is {DefinitionValidation.Format(Angle)} rad."));
        }
        else if (System.Math.Abs(Angle) > (2.0 * System.Math.PI) + Tolerance.Angular)
        {
            problems.Add(KernelDiagnostic.Error(
                KernelDiagnosticCodes.InvalidAngle,
                $"The revolve angle {DefinitionValidation.Format(Angle)} rad exceeds a full turn, "
                + "which would make the result overlap itself."));
        }

        return [.. problems];
    }
}

/// <summary>Which boolean operation to perform.</summary>
public enum BooleanOperation
{
    /// <summary>Add the tools to the target.</summary>
    Union = 0,

    /// <summary>Remove the tools from the target.</summary>
    Subtract = 1,

    /// <summary>Keep only what the target and the tools have in common.</summary>
    Intersect = 2,
}

/// <summary>Combines bodies.</summary>
/// <param name="Operation">Which combination to perform.</param>
/// <param name="Target">The body being modified.</param>
/// <param name="Tools">The bodies being combined with it.</param>
/// <remarks>
/// The fragile operation, per ADR-0001, and the one the retry ladder exists for. Tangencies,
/// coincident faces, and near-degenerate overlaps are where OCCT differs most from Parasolid.
/// </remarks>
public sealed record BooleanDefinition(
    BooleanOperation Operation,
    KernelShape Target,
    ImmutableArray<KernelShape> Tools) : IOperationDefinition
{
    /// <summary>Combines a target with a single tool.</summary>
    /// <param name="operation">Which combination to perform.</param>
    /// <param name="target">The body being modified.</param>
    /// <param name="tool">The body being combined with it.</param>
    public BooleanDefinition(BooleanOperation operation, KernelShape target, KernelShape tool)
        : this(operation, target, [tool])
    {
    }

    /// <inheritdoc />
    public string OperationName => $"Boolean.{Operation}";

    /// <inheritdoc />
    public ImmutableArray<KernelDiagnostic> Validate()
    {
        List<KernelDiagnostic> problems = [];
        DefinitionValidation.RequireShape(problems, Target, "The target body");

        if (Tools.IsDefaultOrEmpty)
        {
            problems.Add(KernelDiagnostic.Error(
                KernelDiagnosticCodes.EmptySelection,
                "A boolean needs at least one tool body."));
        }
        else
        {
            for (int i = 0; i < Tools.Length; i++)
            {
                DefinitionValidation.RequireShape(problems, Tools[i], $"Tool body {i + 1}");

                if (Tools[i] == Target)
                {
                    problems.Add(KernelDiagnostic.Error(
                        KernelDiagnosticCodes.InvalidProfile,
                        "A body cannot be combined with itself."));
                }
            }
        }

        return [.. problems];
    }
}

/// <summary>One edge to blend, and by how much.</summary>
/// <param name="Edge">The edge.</param>
/// <param name="Radius">The blend radius in metres.</param>
public readonly record struct FilletEdge(SubEntity Edge, double Radius);

/// <summary>Rounds edges of a body.</summary>
/// <param name="Body">The body to modify.</param>
/// <param name="Edges">The edges to round, each with its radius.</param>
/// <remarks>
/// Phase 1 supports constant-radius fillets only. Variable radius, face fillets, full-round, and
/// setback corners land at P7-T07, which the plan flags as the place to exercise the retry ladder
/// hardest.
/// </remarks>
public sealed record FilletDefinition(
    KernelShape Body,
    ImmutableArray<FilletEdge> Edges) : IOperationDefinition
{
    /// <summary>Rounds several edges by the same radius.</summary>
    /// <param name="body">The body to modify.</param>
    /// <param name="radius">The radius to apply to every edge.</param>
    /// <param name="edges">The edges to round.</param>
    public FilletDefinition(KernelShape body, double radius, params ReadOnlySpan<SubEntity> edges)
        : this(body, BuildEdges(radius, edges))
    {
    }

    /// <inheritdoc />
    public string OperationName => "Fillet";

    /// <inheritdoc />
    public ImmutableArray<KernelDiagnostic> Validate()
    {
        List<KernelDiagnostic> problems = [];
        DefinitionValidation.RequireShape(problems, Body, "The body to fillet");

        if (Edges.IsDefaultOrEmpty)
        {
            problems.Add(KernelDiagnostic.Error(
                KernelDiagnosticCodes.EmptySelection,
                "A fillet needs at least one edge."));
            return [.. problems];
        }

        HashSet<SubEntity> seen = [];
        foreach (FilletEdge edge in Edges)
        {
            DefinitionValidation.RequireEntityKind(
                problems, edge.Edge, SubEntityKind.Edge, Body, "A fillet selection");
            DefinitionValidation.RequirePositive(problems, edge.Radius, "Fillet radius");

            if (edge.Edge.IsValid && !seen.Add(edge.Edge))
            {
                problems.Add(KernelDiagnostic.Error(
                    KernelDiagnosticCodes.InvalidProfile,
                    "The same edge is listed twice with different radii.",
                    [edge.Edge]));
            }
        }

        return [.. problems];
    }

    private static ImmutableArray<FilletEdge> BuildEdges(double radius, ReadOnlySpan<SubEntity> edges)
    {
        ImmutableArray<FilletEdge>.Builder builder = ImmutableArray.CreateBuilder<FilletEdge>(edges.Length);
        foreach (SubEntity edge in edges)
        {
            builder.Add(new FilletEdge(edge, radius));
        }

        return builder.MoveToImmutable();
    }
}

/// <summary>One edge to chamfer, and by how much.</summary>
/// <param name="Edge">The edge.</param>
/// <param name="Distance">The setback distance in metres, equal on both adjacent faces.</param>
public readonly record struct ChamferEdge(SubEntity Edge, double Distance);

/// <summary>Bevels edges of a body.</summary>
/// <param name="Body">The body to modify.</param>
/// <param name="Edges">The edges to bevel, each with its distance.</param>
/// <remarks>
/// Phase 1 supports equal-distance chamfers only. Distance-distance, distance-angle, and vertex
/// chamfers land at P7-T08.
/// </remarks>
public sealed record ChamferDefinition(
    KernelShape Body,
    ImmutableArray<ChamferEdge> Edges) : IOperationDefinition
{
    /// <summary>Bevels several edges by the same distance.</summary>
    /// <param name="body">The body to modify.</param>
    /// <param name="distance">The distance to apply to every edge.</param>
    /// <param name="edges">The edges to bevel.</param>
    public ChamferDefinition(KernelShape body, double distance, params ReadOnlySpan<SubEntity> edges)
        : this(body, BuildEdges(distance, edges))
    {
    }

    /// <inheritdoc />
    public string OperationName => "Chamfer";

    /// <inheritdoc />
    public ImmutableArray<KernelDiagnostic> Validate()
    {
        List<KernelDiagnostic> problems = [];
        DefinitionValidation.RequireShape(problems, Body, "The body to chamfer");

        if (Edges.IsDefaultOrEmpty)
        {
            problems.Add(KernelDiagnostic.Error(
                KernelDiagnosticCodes.EmptySelection,
                "A chamfer needs at least one edge."));
            return [.. problems];
        }

        foreach (ChamferEdge edge in Edges)
        {
            DefinitionValidation.RequireEntityKind(
                problems, edge.Edge, SubEntityKind.Edge, Body, "A chamfer selection");
            DefinitionValidation.RequirePositive(problems, edge.Distance, "Chamfer distance");
        }

        return [.. problems];
    }

    private static ImmutableArray<ChamferEdge> BuildEdges(
        double distance,
        ReadOnlySpan<SubEntity> edges)
    {
        ImmutableArray<ChamferEdge>.Builder builder =
            ImmutableArray.CreateBuilder<ChamferEdge>(edges.Length);

        foreach (SubEntity edge in edges)
        {
            builder.Add(new ChamferEdge(edge, distance));
        }

        return builder.MoveToImmutable();
    }
}
