using System.Collections.Immutable;

using OpenMCAD.Math;

namespace OpenMCAD.Solver.Sketching;

/// <summary>
/// One piece of sketch geometry.
/// </summary>
/// <param name="Id">Which entity this is.</param>
/// <param name="IsConstruction">
/// Whether it is scaffolding rather than profile geometry. Construction geometry constrains and is
/// never swept, extruded or exported.
/// </param>
/// <remarks>
/// <para>
/// P4-T03. Entities hold their geometry as values — a line has a start and an end, not two indices
/// into a parameter vector. planegcs wants a flat vector of doubles and gets one, but that
/// flattening belongs at the solver boundary (P4-T02): everything above it, which is the whole
/// sketcher, reads and writes points. A model built the solver's way would make every piece of UI,
/// inference and serialization code do arithmetic on offsets.
/// </para>
/// <para>
/// Construction is a flag on the entity rather than a separate collection. It changes only what the
/// geometry is <em>for</em>, never how it is solved: a construction line participates in
/// constraints exactly like any other, which is the point of it.
/// </para>
/// <para>
/// Text is deliberately absent. §5.7 puts it in Phase 7 because it needs font outlines converted to
/// curves, which is a different problem from anything here.
/// </para>
/// </remarks>
public abstract record SketchEntity(SketchEntityId Id, bool IsConstruction = false)
{
    /// <summary>Gets what kind of geometry this is, for messages and for a file.</summary>
    public abstract string Kind { get; }

    /// <summary>Gets whether the curve joins its own end to its own start.</summary>
    public virtual bool IsClosed => false;

    /// <summary>Gets which of its named points this entity actually has.</summary>
    /// <remarks>
    /// Asked rather than assumed, because a constraint pointed at a point an entity does not have
    /// is a mistake worth catching when the constraint is made rather than when the solver reads a
    /// coordinate that was never written.
    /// </remarks>
    public abstract ImmutableArray<EntityPoint> Points { get; }

    /// <summary>Finds one of this entity's named points.</summary>
    /// <param name="point">Which point.</param>
    /// <returns>Where it is, or <see langword="null"/> if this entity has no such point.</returns>
    public Vec2d? PointOf(EntityPoint point)
        => Points.Contains(point) ? Locate(point) : null;

    /// <summary>Evaluates the curve.</summary>
    /// <param name="t">
    /// How far along, from 0 at the start to 1 at the end. Values outside that range extrapolate
    /// where the geometry allows it.
    /// </param>
    /// <returns>The point.</returns>
    public abstract Vec2d PointAt(double t);

    /// <summary>Gets why this entity cannot be solved, or null if it can.</summary>
    /// <remarks>
    /// A degenerate entity is not a solver failure waiting to happen; it is one that has already
    /// happened, and the solver will report it as a non-convergence somewhere unrelated. Catching
    /// it here means the message names the circle with no radius.
    /// </remarks>
    public abstract string? Degeneracy { get; }

    /// <summary>Gets whether this entity can be solved.</summary>
    public bool IsValid => Degeneracy is null;

    /// <summary>Finds a named point this entity is known to have.</summary>
    protected abstract Vec2d Locate(EntityPoint point);

    /// <inheritdoc/>
    /// <remarks>
    /// Sealed. A record generates its own <see cref="object.ToString"/> in every derived type,
    /// which would shadow this one and print the whole member list -- including the computed
    /// properties, so a degenerate spline's description would run to a paragraph and a diagnostic
    /// naming the entity would be unreadable.
    /// </remarks>
    public sealed override string ToString()
        => IsConstruction ? $"{Kind} (construction)" : Kind;
}

/// <summary>A point.</summary>
/// <param name="Id">Which entity this is.</param>
/// <param name="Position">Where it is.</param>
/// <param name="IsConstruction">Whether it is scaffolding.</param>
public sealed record SketchPoint(SketchEntityId Id, Vec2d Position, bool IsConstruction = false)
    : SketchEntity(Id, IsConstruction)
{
    /// <inheritdoc/>
    public override string Kind => "point";

    /// <inheritdoc/>
    public override ImmutableArray<EntityPoint> Points => [EntityPoint.Self];

    /// <inheritdoc/>
    public override Vec2d PointAt(double t)
    {
        _ = t;
        return Position;
    }

    /// <inheritdoc/>
    public override string? Degeneracy
        => Position.IsFinite ? null : "This point is not at a finite position.";

    /// <inheritdoc/>
    protected override Vec2d Locate(EntityPoint point)
    {
        _ = point;
        return Position;
    }
}

/// <summary>A straight segment.</summary>
/// <param name="Id">Which entity this is.</param>
/// <param name="Start">Where it begins.</param>
/// <param name="End">Where it ends.</param>
/// <param name="IsConstruction">Whether it is scaffolding.</param>
public sealed record SketchLine(
    SketchEntityId Id, Vec2d Start, Vec2d End, bool IsConstruction = false)
    : SketchEntity(Id, IsConstruction)
{
    /// <inheritdoc/>
    public override string Kind => "line";

    /// <inheritdoc/>
    public override ImmutableArray<EntityPoint> Points =>
        [EntityPoint.Start, EntityPoint.End, EntityPoint.Middle];

    /// <summary>Gets which way the line runs, of unit length, or zero if it is degenerate.</summary>
    public Vec2d Direction => (End - Start).IsZeroLength ? Vec2d.Zero : (End - Start) / Length;

    /// <summary>Gets how long the line is.</summary>
    public double Length => (End - Start).Length;

    /// <inheritdoc/>
    public override Vec2d PointAt(double t) => Start + ((End - Start) * t);

    /// <inheritdoc/>
    public override string? Degeneracy => !Start.IsFinite || !End.IsFinite
        ? "This line does not have finite ends."
        : (End - Start).IsZeroLength
            ? "This line begins and ends in the same place, so it has no direction."
            : null;

    /// <inheritdoc/>
    protected override Vec2d Locate(EntityPoint point) => point switch
    {
        EntityPoint.Start => Start,
        EntityPoint.End => End,
        _ => PointAt(0.5),
    };
}

/// <summary>A full circle.</summary>
/// <param name="Id">Which entity this is.</param>
/// <param name="Centre">Where its centre is.</param>
/// <param name="Radius">How big it is.</param>
/// <param name="IsConstruction">Whether it is scaffolding.</param>
public sealed record SketchCircle(
    SketchEntityId Id, Vec2d Centre, double Radius, bool IsConstruction = false)
    : SketchEntity(Id, IsConstruction)
{
    /// <inheritdoc/>
    public override string Kind => "circle";

    /// <inheritdoc/>
    public override bool IsClosed => true;

    /// <inheritdoc/>
    public override ImmutableArray<EntityPoint> Points => [EntityPoint.Centre];

    /// <inheritdoc/>
    public override Vec2d PointAt(double t)
    {
        double angle = t * 2 * System.Math.PI;

        return Centre + new Vec2d(Radius * System.Math.Cos(angle), Radius * System.Math.Sin(angle));
    }

    /// <inheritdoc/>
    public override string? Degeneracy => !Centre.IsFinite || !double.IsFinite(Radius)
        ? "This circle is not at a finite position or has no finite radius."
        : Radius <= Tolerance.LinearResolution
            ? "This circle has no radius, so it is a point."
            : null;

    /// <inheritdoc/>
    protected override Vec2d Locate(EntityPoint point)
    {
        _ = point;
        return Centre;
    }
}

/// <summary>Part of a circle.</summary>
/// <param name="Id">Which entity this is.</param>
/// <param name="Centre">Where its centre is.</param>
/// <param name="Radius">How big it is.</param>
/// <param name="StartAngle">Where the arc begins, in radians from the positive X axis.</param>
/// <param name="EndAngle">Where the arc ends, measured anticlockwise from the start.</param>
/// <param name="IsConstruction">Whether it is scaffolding.</param>
/// <remarks>
/// Always anticlockwise from start to end. A signed sweep would make "the same arc" two different
/// entities, and every constraint on its endpoints would have to know which convention it was
/// written under. Drawing an arc the other way is a matter of which angle is called the start.
/// </remarks>
public sealed record SketchArc(
    SketchEntityId Id,
    Vec2d Centre,
    double Radius,
    double StartAngle,
    double EndAngle,
    bool IsConstruction = false)
    : SketchEntity(Id, IsConstruction)
{
    /// <inheritdoc/>
    public override string Kind => "arc";

    /// <inheritdoc/>
    public override ImmutableArray<EntityPoint> Points =>
        [EntityPoint.Start, EntityPoint.End, EntityPoint.Centre, EntityPoint.Middle];

    /// <summary>Gets how far the arc sweeps, always positive.</summary>
    public double Sweep
    {
        get
        {
            double sweep = (EndAngle - StartAngle) % (2 * System.Math.PI);

            return sweep < 0 ? sweep + (2 * System.Math.PI) : sweep;
        }
    }

    /// <inheritdoc/>
    public override Vec2d PointAt(double t)
    {
        double angle = StartAngle + (Sweep * t);

        return Centre + new Vec2d(Radius * System.Math.Cos(angle), Radius * System.Math.Sin(angle));
    }

    /// <inheritdoc/>
    public override string? Degeneracy => !Centre.IsFinite
        || !double.IsFinite(Radius)
        || !double.IsFinite(StartAngle)
        || !double.IsFinite(EndAngle)
            ? "This arc is not at a finite position or has no finite radius."
            : Radius <= Tolerance.LinearResolution
                ? "This arc has no radius, so it is a point."
                : Sweep <= Tolerance.AngularResolution
                    ? "This arc begins and ends at the same angle, so it has no length."
                    : null;

    /// <inheritdoc/>
    protected override Vec2d Locate(EntityPoint point) => point switch
    {
        EntityPoint.Centre => Centre,
        EntityPoint.Start => PointAt(0),
        EntityPoint.End => PointAt(1),
        _ => PointAt(0.5),
    };
}

/// <summary>A full ellipse.</summary>
/// <param name="Id">Which entity this is.</param>
/// <param name="Centre">Where its centre is.</param>
/// <param name="MajorRadius">The longer semi-axis.</param>
/// <param name="MinorRadius">The shorter semi-axis.</param>
/// <param name="Rotation">Where the major axis points, in radians from the positive X axis.</param>
/// <param name="IsConstruction">Whether it is scaffolding.</param>
public sealed record SketchEllipse(
    SketchEntityId Id,
    Vec2d Centre,
    double MajorRadius,
    double MinorRadius,
    double Rotation = 0,
    bool IsConstruction = false)
    : SketchEntity(Id, IsConstruction)
{
    /// <inheritdoc/>
    public override string Kind => "ellipse";

    /// <inheritdoc/>
    public override bool IsClosed => true;

    /// <inheritdoc/>
    public override ImmutableArray<EntityPoint> Points =>
        [EntityPoint.Centre, EntityPoint.Focus, EntityPoint.SecondFocus];

    /// <summary>Gets how far each focus is from the centre.</summary>
    public double FocalDistance => System.Math.Sqrt(
        System.Math.Max(0, (MajorRadius * MajorRadius) - (MinorRadius * MinorRadius)));

    /// <inheritdoc/>
    public override Vec2d PointAt(double t) => Conic.OnEllipse(
        Centre, MajorRadius, MinorRadius, Rotation, t * 2 * System.Math.PI);

    /// <inheritdoc/>
    public override string? Degeneracy => !Centre.IsFinite
        || !double.IsFinite(MajorRadius)
        || !double.IsFinite(MinorRadius)
        || !double.IsFinite(Rotation)
            ? "This ellipse is not at a finite position or has no finite radii."
            : MinorRadius <= Tolerance.LinearResolution
                ? "This ellipse has no minor radius, so it is a segment."
                : MinorRadius > MajorRadius + Tolerance.Linear
                    ? "This ellipse's minor radius is larger than its major one, which means the "
                        + "axes have been swapped rather than that it is a different shape."
                    : null;

    /// <inheritdoc/>
    protected override Vec2d Locate(EntityPoint point)
    {
        Vec2d along = new(System.Math.Cos(Rotation), System.Math.Sin(Rotation));

        return point switch
        {
            EntityPoint.Focus => Centre + (along * FocalDistance),
            EntityPoint.SecondFocus => Centre - (along * FocalDistance),
            _ => Centre,
        };
    }
}

/// <summary>Part of an ellipse.</summary>
/// <param name="Id">Which entity this is.</param>
/// <param name="Centre">Where its centre is.</param>
/// <param name="MajorRadius">The longer semi-axis.</param>
/// <param name="MinorRadius">The shorter semi-axis.</param>
/// <param name="Rotation">Where the major axis points, in radians from the positive X axis.</param>
/// <param name="StartAngle">Where the arc begins, as an eccentric angle.</param>
/// <param name="EndAngle">Where it ends, measured anticlockwise from the start.</param>
/// <param name="IsConstruction">Whether it is scaffolding.</param>
/// <remarks>
/// The angles are eccentric, not polar: the parameter of the standard parameterisation rather than
/// the angle a ray from the centre makes. The two agree only on a circle, and a model that stored
/// the polar angle would need a transcendental solve to evaluate a point.
/// </remarks>
public sealed record SketchEllipticalArc(
    SketchEntityId Id,
    Vec2d Centre,
    double MajorRadius,
    double MinorRadius,
    double Rotation,
    double StartAngle,
    double EndAngle,
    bool IsConstruction = false)
    : SketchEntity(Id, IsConstruction)
{
    /// <inheritdoc/>
    public override string Kind => "elliptical arc";

    /// <inheritdoc/>
    public override ImmutableArray<EntityPoint> Points =>
        [EntityPoint.Start, EntityPoint.End, EntityPoint.Centre, EntityPoint.Middle];

    /// <summary>Gets how far the arc sweeps in eccentric angle, always positive.</summary>
    public double Sweep
    {
        get
        {
            double sweep = (EndAngle - StartAngle) % (2 * System.Math.PI);

            return sweep < 0 ? sweep + (2 * System.Math.PI) : sweep;
        }
    }

    /// <inheritdoc/>
    public override Vec2d PointAt(double t) => Conic.OnEllipse(
        Centre, MajorRadius, MinorRadius, Rotation, StartAngle + (Sweep * t));

    /// <inheritdoc/>
    public override string? Degeneracy
        => new SketchEllipse(Id, Centre, MajorRadius, MinorRadius, Rotation).Degeneracy
            ?? (!double.IsFinite(StartAngle) || !double.IsFinite(EndAngle)
                ? "This elliptical arc does not begin and end at finite angles."
                : Sweep <= Tolerance.AngularResolution
                    ? "This elliptical arc begins and ends at the same angle, so it has no length."
                    : null);

    /// <inheritdoc/>
    protected override Vec2d Locate(EntityPoint point) => point switch
    {
        EntityPoint.Centre => Centre,
        EntityPoint.Start => PointAt(0),
        EntityPoint.End => PointAt(1),
        _ => PointAt(0.5),
    };
}

/// <summary>Part of a parabola.</summary>
/// <param name="Id">Which entity this is.</param>
/// <param name="Vertex">The turning point.</param>
/// <param name="Focus">The focus, whose distance from the vertex sets how open the curve is.</param>
/// <param name="StartParameter">Where the segment begins, in the standard parameterisation.</param>
/// <param name="EndParameter">Where it ends.</param>
/// <param name="IsConstruction">Whether it is scaffolding.</param>
/// <remarks>
/// Stored by vertex and focus rather than by coefficients. Coefficients are relative to whichever
/// axes they were written in, so any change to the sketch's placement rewrites them; a vertex and a
/// focus are two points, and points are what constraints attach to.
/// </remarks>
public sealed record SketchParabola(
    SketchEntityId Id,
    Vec2d Vertex,
    Vec2d Focus,
    double StartParameter,
    double EndParameter,
    bool IsConstruction = false)
    : SketchEntity(Id, IsConstruction)
{
    /// <inheritdoc/>
    public override string Kind => "parabola";

    /// <inheritdoc/>
    public override ImmutableArray<EntityPoint> Points =>
        [EntityPoint.Start, EntityPoint.End, EntityPoint.Focus, EntityPoint.Middle];

    /// <summary>Gets the distance from the vertex to the focus.</summary>
    public double FocalLength => (Focus - Vertex).Length;

    /// <inheritdoc/>
    public override Vec2d PointAt(double t)
    {
        double parameter = StartParameter + ((EndParameter - StartParameter) * t);

        Vec2d axis = FocalLength <= Tolerance.LinearResolution
            ? Vec2d.UnitX
            : (Focus - Vertex) / FocalLength;

        Vec2d across = new(-axis.Y, axis.X);

        // The standard parameterisation about the vertex: 2ft along the axis by ft^2 across it,
        // written the other way round so the parameter runs along the opening rather than across.
        return Vertex
            + (axis * (FocalLength * parameter * parameter))
            + (across * (2 * FocalLength * parameter));
    }

    /// <inheritdoc/>
    public override string? Degeneracy => !Vertex.IsFinite
        || !Focus.IsFinite
        || !double.IsFinite(StartParameter)
        || !double.IsFinite(EndParameter)
            ? "This parabola is not at a finite position."
            : FocalLength <= Tolerance.LinearResolution
                ? "This parabola's focus is at its vertex, so it is a ray."
                : System.Math.Abs(EndParameter - StartParameter) <= Tolerance.Parametric
                    ? "This parabola begins and ends at the same place, so it has no length."
                    : null;

    /// <inheritdoc/>
    protected override Vec2d Locate(EntityPoint point) => point switch
    {
        EntityPoint.Focus => Focus,
        EntityPoint.Start => PointAt(0),
        EntityPoint.End => PointAt(1),
        _ => PointAt(0.5),
    };
}

/// <summary>Part of one branch of a hyperbola.</summary>
/// <param name="Id">Which entity this is.</param>
/// <param name="Centre">Where the two branches are centred.</param>
/// <param name="MajorRadius">The semi-transverse axis, from centre to vertex.</param>
/// <param name="MinorRadius">The semi-conjugate axis, which sets the asymptotes.</param>
/// <param name="Rotation">Where the transverse axis points, in radians.</param>
/// <param name="StartParameter">Where the segment begins, in the standard parameterisation.</param>
/// <param name="EndParameter">Where it ends.</param>
/// <param name="IsConstruction">Whether it is scaffolding.</param>
/// <remarks>
/// One branch only, the one on the positive side of the transverse axis. Both branches in one
/// entity would be a curve that is not connected, and every operation that walks a profile would
/// have to special-case it.
/// </remarks>
public sealed record SketchHyperbola(
    SketchEntityId Id,
    Vec2d Centre,
    double MajorRadius,
    double MinorRadius,
    double Rotation,
    double StartParameter,
    double EndParameter,
    bool IsConstruction = false)
    : SketchEntity(Id, IsConstruction)
{
    /// <inheritdoc/>
    public override string Kind => "hyperbola";

    /// <inheritdoc/>
    public override ImmutableArray<EntityPoint> Points =>
        [EntityPoint.Start, EntityPoint.End, EntityPoint.Centre, EntityPoint.Middle];

    /// <inheritdoc/>
    public override Vec2d PointAt(double t)
    {
        double parameter = StartParameter + ((EndParameter - StartParameter) * t);

        Vec2d along = new(System.Math.Cos(Rotation), System.Math.Sin(Rotation));
        Vec2d across = new(-along.Y, along.X);

        return Centre
            + (along * (MajorRadius * System.Math.Cosh(parameter)))
            + (across * (MinorRadius * System.Math.Sinh(parameter)));
    }

    /// <inheritdoc/>
    public override string? Degeneracy => !Centre.IsFinite
        || !double.IsFinite(MajorRadius)
        || !double.IsFinite(MinorRadius)
        || !double.IsFinite(Rotation)
        || !double.IsFinite(StartParameter)
        || !double.IsFinite(EndParameter)
            ? "This hyperbola is not at a finite position or has no finite radii."
            : MajorRadius <= Tolerance.LinearResolution || MinorRadius <= Tolerance.LinearResolution
                ? "This hyperbola has no radius, so it collapses to its asymptotes."
                : System.Math.Abs(EndParameter - StartParameter) <= Tolerance.Parametric
                    ? "This hyperbola begins and ends at the same place, so it has no length."
                    : null;

    /// <inheritdoc/>
    protected override Vec2d Locate(EntityPoint point) => point switch
    {
        EntityPoint.Centre => Centre,
        EntityPoint.Start => PointAt(0),
        EntityPoint.End => PointAt(1),
        _ => PointAt(0.5),
    };
}

/// <summary>Where the maths that more than one conic needs lives.</summary>
internal static class Conic
{
    /// <summary>A point on an ellipse at a given eccentric angle.</summary>
    public static Vec2d OnEllipse(
        Vec2d centre, double majorRadius, double minorRadius, double rotation, double angle)
    {
        double cos = System.Math.Cos(rotation);
        double sin = System.Math.Sin(rotation);

        double x = majorRadius * System.Math.Cos(angle);
        double y = minorRadius * System.Math.Sin(angle);

        return centre + new Vec2d((x * cos) - (y * sin), (x * sin) + (y * cos));
    }
}
