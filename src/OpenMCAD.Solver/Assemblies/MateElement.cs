using OpenMCAD.Math;

namespace OpenMCAD.Solver.Assemblies;

/// <summary>
/// What a mate attaches to on one body, in that body's own frame (P5-T05).
/// </summary>
/// <remarks>
/// <para>
/// <b>In the body's frame, not the world's.</b> A mate says something about two components that
/// stays true wherever the assembly is dragged, so what it names has to be a fact about each
/// component rather than about where the component happens to be. The solver moves bodies by
/// changing their placements; if an element carried world coordinates it would have to be rewritten
/// on every iteration, and a mate would stop meaning anything the moment the thing it was attached
/// to moved.
/// </para>
/// <para>
/// <b>Three shapes, not the faces and edges a user picks.</b> A user mates a face to a face; what
/// constrains the arithmetic is the plane that face lies on. Likewise a cylindrical face and a
/// circular edge both reduce to an axis, and a vertex to a point. Turning topology into one of these
/// is the modelling layer's job — this layer sits below <c>OpenMCAD.Core</c> (PLAN.md 4.1) and has
/// no way to ask what a face is, which is the same separation that keeps <c>ISketchSolver</c>
/// working on <c>Sketch</c> rather than on a document.
/// </para>
/// <para>
/// A closed hierarchy: these three are what the minimal mate set of §5.9 needs, and a fourth added
/// later should be a deliberate act rather than something a caller can slip in.
/// </para>
/// </remarks>
public abstract record MateElement
{
    private MateElement()
    {
    }

    /// <summary>A plane, as a planar face reduces to.</summary>
    /// <param name="Origin">A point on it, in the body's frame.</param>
    /// <param name="Normal">
    /// Which way it faces. Need not be unit length, and its direction is meaningful: two faces
    /// brought together are flush when their normals oppose, which is what a coincident mate
    /// between planes means and what distinguishes it from the same two planes back to back.
    /// </param>
    public sealed record Plane(Vec3d Origin, Vec3d Normal) : MateElement;

    /// <summary>An infinite line, as a cylindrical face or a circular edge reduces to.</summary>
    /// <param name="Origin">A point on it, in the body's frame.</param>
    /// <param name="Direction">Which way it runs. Need not be unit length.</param>
    /// <remarks>
    /// The direction is deliberately <em>not</em> meaningful for a concentric mate: a shaft in a
    /// hole is concentric whichever way round it is inserted, so the residual is written on the
    /// line and not on the ray. A mate that did care is a different mate — §5.9's parallel and
    /// anti-parallel pair — and is not in the minimal set.
    /// </remarks>
    public sealed record Axis(Vec3d Origin, Vec3d Direction) : MateElement;

    /// <summary>A single point, as a vertex or a hole's centre reduces to.</summary>
    /// <param name="Position">Where it is, in the body's frame.</param>
    public sealed record Point(Vec3d Position) : MateElement;

    /// <summary>Gets a short description of the shape, for a diagnostic.</summary>
    public string Shape => this switch
    {
        Plane => "a plane",
        Axis => "an axis",
        Point => "a point",
        _ => "something this build does not recognise",
    };

    /// <summary>Returns this element placed in the world by a body's transform.</summary>
    /// <param name="placement">Where the body sits.</param>
    /// <returns>The element, in world coordinates.</returns>
    /// <remarks>
    /// A direction goes through <see cref="Transform.TransformNormal"/> rather than
    /// <see cref="Transform.TransformDirection"/>: a scaled component's plane normal is still a
    /// direction and must not come back scaled, and the residuals below compare directions by angle.
    /// </remarks>
    public MateElement PlacedBy(Transform placement) => this switch
    {
        Plane(var origin, var normal) => new Plane(
            placement.TransformPoint(origin), placement.TransformNormal(normal)),

        Axis(var origin, var direction) => new Axis(
            placement.TransformPoint(origin), placement.TransformNormal(direction)),

        Point(var position) => new Point(placement.TransformPoint(position)),

        _ => throw new ArgumentOutOfRangeException(nameof(placement), this, "Unknown element kind."),
    };
}
