using System.Collections.Immutable;

using OpenMCAD.Math;

namespace OpenMCAD.Solver.Sketching;

/// <summary>
/// What each constraint asks of the geometry, written as equations that must come to zero.
/// </summary>
/// <remarks>
/// <para>
/// P4-T02. Every solver reduces to this: a vector of numbers that is zero when the sketch is right
/// and non-zero by how wrong it is. Written here rather than inside a solver because the equations
/// are the meaning of the constraints, not an implementation detail of solving them — and because
/// a second solver deriving its own would be a second opinion about what "tangent" means.
/// </para>
/// <para>
/// Every residual is scaled to a length, not to whatever the algebra naturally produced. The
/// obvious form of "parallel" is the cross product of the two directions, which grows with the
/// lengths of both lines: two parallel metre-long lines then have a residual a million times larger
/// than two parallel millimetre-long ones. Dividing through by the lengths is what makes one
/// tolerance mean the same thing everywhere in a sketch, which is what "solved" is decided by.
/// </para>
/// <para>
/// The conditioning argument usually made for this — that a least-squares step would otherwise
/// spend its effort on the long lines — is weaker than it sounds here, because Levenberg–Marquardt
/// damps each column in proportion to its own magnitude and largely compensates. Removing the
/// scaling breaks the verdict rather than the answer, and the test says so.
/// </para>
/// </remarks>
public static class ConstraintResiduals
{
    /// <summary>Evaluates every constraint of a sketch.</summary>
    /// <param name="sketch">The sketch.</param>
    /// <returns>The residuals, in constraint order, and which constraint produced each.</returns>
    /// <remarks>
    /// Reference dimensions are left out. They measure rather than constrain, so including them
    /// would let a solver move geometry to satisfy a number the user explicitly said was only being
    /// displayed.
    /// </remarks>
    public static (ImmutableArray<double> Values, ImmutableArray<SketchConstraintId> From) Of(
        Sketch sketch)
    {
        ArgumentNullException.ThrowIfNull(sketch);

        ImmutableArray<double>.Builder values = ImmutableArray.CreateBuilder<double>();
        ImmutableArray<SketchConstraintId>.Builder from =
            ImmutableArray.CreateBuilder<SketchConstraintId>();

        foreach (SketchConstraint constraint in sketch.Constraints.Ordered)
        {
            if (!constraint.IsDriving)
            {
                continue;
            }

            foreach (double residual in Of(constraint, sketch.Entities))
            {
                values.Add(residual);
                from.Add(constraint.Id);
            }
        }

        return (values.ToImmutable(), from.ToImmutable());
    }

    /// <summary>Evaluates one constraint.</summary>
    /// <param name="constraint">The constraint.</param>
    /// <param name="entities">The geometry it names.</param>
    /// <returns>Its residuals, zero when it is satisfied.</returns>
    /// <remarks>
    /// A constraint naming geometry that is not there produces no equations rather than throwing.
    /// The set is validated before a solve (P4-T04) and the message a user gets comes from there;
    /// throwing here would turn a diagnosis into a crash halfway through an iteration.
    /// </remarks>
    public static ImmutableArray<double> Of(SketchConstraint constraint, SketchEntitySet entities)
    {
        ArgumentNullException.ThrowIfNull(constraint);
        ArgumentNullException.ThrowIfNull(entities);

        return constraint.Kind switch
        {
            ConstraintKind.Coincident => Coincident(constraint, entities),
            ConstraintKind.Fix => Fix(constraint, entities),
            ConstraintKind.Concentric => Concentric(constraint, entities),
            ConstraintKind.Midpoint => Midpoint(constraint, entities),
            ConstraintKind.Symmetric => Symmetric(constraint, entities),
            ConstraintKind.Distance => Distance(constraint, entities),
            ConstraintKind.HorizontalDistance => AxisDistance(constraint, entities, vertical: false),
            ConstraintKind.VerticalDistance => AxisDistance(constraint, entities, vertical: true),
            ConstraintKind.Horizontal => Aligned(constraint, entities, vertical: false),
            ConstraintKind.Vertical => Aligned(constraint, entities, vertical: true),
            ConstraintKind.Parallel => Parallel(constraint, entities),
            ConstraintKind.Perpendicular => Perpendicular(constraint, entities),
            ConstraintKind.Angle => Angle(constraint, entities),
            ConstraintKind.Radius => Radius(constraint, entities, half: false),
            ConstraintKind.Diameter => Radius(constraint, entities, half: true),
            ConstraintKind.Equal => Equal(constraint, entities),
            ConstraintKind.PointOnObject => PointOnObject(constraint, entities),
            ConstraintKind.Tangent => Tangent(constraint, entities),
            _ => [],
        };
    }

    private static ImmutableArray<double> Coincident(
        SketchConstraint constraint, SketchEntitySet entities)
        => Pair(constraint, entities) is not ({ } a, { } b) ? [] : [a.X - b.X, a.Y - b.Y];

    private static ImmutableArray<double> Fix(
        SketchConstraint constraint, SketchEntitySet entities)
    {
        // Zero equations, and that is correct rather than a gap. A fixed point is one the solver is
        // not allowed to move, which is a statement about the unknowns and not about the residuals:
        // the solver holds its columns out of the Jacobian. Writing "this point equals where it
        // already is" would instead let a least-squares step trade a little of it away against
        // some other constraint, which is exactly what fixing something is meant to prevent.
        _ = constraint;
        _ = entities;

        return [];
    }

    private static ImmutableArray<double> Concentric(
        SketchConstraint constraint, SketchEntitySet entities)
    {
        if (Two(constraint, entities) is not ({ } first, { } second))
        {
            return [];
        }

        Vec2d? a = first.PointOf(EntityPoint.Centre);
        Vec2d? b = second.PointOf(EntityPoint.Centre);

        return a is null || b is null ? [] : [a.Value.X - b.Value.X, a.Value.Y - b.Value.Y];
    }

    private static ImmutableArray<double> Midpoint(
        SketchConstraint constraint, SketchEntitySet entities)
    {
        if (entities.Locate(constraint.On[0]) is not { } point
            || entities.Find(constraint.On[1].Entity) is not SketchLine line)
        {
            return [];
        }

        Vec2d middle = (line.Start + line.End) * 0.5;

        return [point.X - middle.X, point.Y - middle.Y];
    }

    private static ImmutableArray<double> Symmetric(
        SketchConstraint constraint, SketchEntitySet entities)
    {
        if (entities.Locate(constraint.On[0]) is not { } a
            || entities.Locate(constraint.On[1]) is not { } b
            || entities.Find(constraint.On[2].Entity) is not SketchLine mirror)
        {
            return [];
        }

        Vec2d along = mirror.End - mirror.Start;
        double length = along.Length;

        if (length <= Tolerance.LinearResolution)
        {
            return [];
        }

        along /= length;

        Vec2d across = new(-along.Y, along.X);
        Vec2d middle = (a + b) * 0.5;

        // Two things at once: the pair's midpoint lies on the line, and the segment joining them
        // crosses it squarely. Either alone leaves a reflection that is not one.
        return
        [
            Vec2d.Dot(middle - mirror.Start, across),
            Vec2d.Dot(b - a, along),
        ];
    }

    private static ImmutableArray<double> Distance(
        SketchConstraint constraint, SketchEntitySet entities)
    {
        double wanted = constraint.Value ?? 0;

        if (constraint.On.Length == 2
            && entities.Find(constraint.On[1].Entity) is SketchLine line
            && constraint.On[1].Point == EntityPoint.Self)
        {
            return entities.Locate(constraint.On[0]) is not { } point
                ? []
                : [DistanceToLine(point, line) - wanted];
        }

        return Pair(constraint, entities) is not ({ } a, { } b)
            ? []
            : [(a - b).Length - wanted];
    }

    private static ImmutableArray<double> AxisDistance(
        SketchConstraint constraint, SketchEntitySet entities, bool vertical)
    {
        double wanted = constraint.Value ?? 0;

        // Unsigned, the same convention Distance uses and for the same reason: which point is
        // first is an accident of drawing order, not a claim about which side of the other it sits
        // on, and a signed residual would make the constraint satisfiable by only one of the two
        // arrangements that measure the same linear dimension.
        return Pair(constraint, entities) is not ({ } a, { } b)
            ? []
            : [System.Math.Abs(vertical ? b.Y - a.Y : b.X - a.X) - wanted];
    }

    private static ImmutableArray<double> Aligned(
        SketchConstraint constraint, SketchEntitySet entities, bool vertical)
    {
        (Vec2d a, Vec2d b)? ends = Ends(constraint, entities);

        if (ends is not ({ } start, { } end))
        {
            return [];
        }

        Vec2d delta = end - start;
        double length = delta.Length;

        // Scaled by length, so "horizontal" means the same tilt for a short line and a long one.
        return length <= Tolerance.LinearResolution
            ? []
            : [(vertical ? delta.X : delta.Y) / length];
    }

    private static ImmutableArray<double> Parallel(
        SketchConstraint constraint, SketchEntitySet entities)
        => Directions(constraint, entities) is not ({ } first, { } second)
            ? []
            : [Vec2d.Cross(first, second)];

    private static ImmutableArray<double> Perpendicular(
        SketchConstraint constraint, SketchEntitySet entities)
        => Directions(constraint, entities) is not ({ } first, { } second)
            ? []
            : [Vec2d.Dot(first, second)];

    private static ImmutableArray<double> Angle(
        SketchConstraint constraint, SketchEntitySet entities)
    {
        if (Directions(constraint, entities) is not ({ } first, { } second))
        {
            return [];
        }

        double between = System.Math.Atan2(
            Vec2d.Cross(first, second), Vec2d.Dot(first, second));

        double difference = between - (constraint.Value ?? 0);

        // Wrapped into (-pi, pi], because an angle and that angle plus a full turn are the same
        // angle, and a residual that did not know it would drive the solve a whole revolution.
        return [System.Math.Atan2(System.Math.Sin(difference), System.Math.Cos(difference))];
    }

    private static ImmutableArray<double> Radius(
        SketchConstraint constraint, SketchEntitySet entities, bool half)
    {
        double? radius = RadiusOf(entities.Find(constraint.On[0].Entity));

        return radius is null ? [] : [(radius.Value * (half ? 2 : 1)) - (constraint.Value ?? 0)];
    }

    private static ImmutableArray<double> Equal(
        SketchConstraint constraint, SketchEntitySet entities)
    {
        if (Two(constraint, entities) is not ({ } first, { } second))
        {
            return [];
        }

        if (first is SketchLine a && second is SketchLine b)
        {
            return [a.Length - b.Length];
        }

        double? one = RadiusOf(first);
        double? other = RadiusOf(second);

        // Two things of different sorts have no "same size" to be equal to, and a residual invented
        // for it would move geometry to satisfy a sentence with no meaning.
        return one is null || other is null ? [] : [one.Value - other.Value];
    }

    private static ImmutableArray<double> PointOnObject(
        SketchConstraint constraint, SketchEntitySet entities)
    {
        if (entities.Locate(constraint.On[0]) is not { } point
            || entities.Find(constraint.On[1].Entity) is not { } curve)
        {
            return [];
        }

        return curve switch
        {
            SketchLine line => [DistanceToLine(point, line)],

            SketchCircle or SketchArc when curve.PointOf(EntityPoint.Centre) is { } centre
                && RadiusOf(curve) is { } radius => [(point - centre).Length - radius],

            _ => [],
        };
    }

    private static ImmutableArray<double> Tangent(
        SketchConstraint constraint, SketchEntitySet entities)
    {
        if (Two(constraint, entities) is not ({ } first, { } second))
        {
            return [];
        }

        (SketchEntity one, SketchEntity other) = first is SketchLine
            ? (first, second)
            : (second, first);

        if (one is SketchLine line
            && other.PointOf(EntityPoint.Centre) is { } centre
            && RadiusOf(other) is { } radius)
        {
            return [DistanceToLine(centre, line) - radius];
        }

        if (first.PointOf(EntityPoint.Centre) is { } a
            && second.PointOf(EntityPoint.Centre) is { } b
            && RadiusOf(first) is { } one2
            && RadiusOf(second) is { } other2)
        {
            double apart = (a - b).Length;

            // Externally or internally tangent, whichever the geometry is already nearer. A
            // residual that assumed one would drag a circle through another to reach it.
            double outside = apart - (one2 + other2);
            double inside = apart - System.Math.Abs(one2 - other2);

            return [System.Math.Abs(outside) <= System.Math.Abs(inside) ? outside : inside];
        }

        return [];
    }

    private static double DistanceToLine(Vec2d point, SketchLine line)
    {
        Vec2d along = line.End - line.Start;
        double length = along.Length;

        // Signed, not absolute. An absolute distance is not differentiable at zero, which is
        // exactly where the solver is trying to get to, so a Newton method loses its quadratic
        // convergence there and a numerically differentiated Jacobian measures a slope that depends
        // on which side the nudge landed. Least-squares over |d| still converges -- sabotaging this
        // to the absolute form fails no test here -- so this is a property planegcs will want
        // rather than one the fake can demonstrate.
        return length <= Tolerance.LinearResolution
            ? (point - line.Start).Length
            : Vec2d.Cross(along, point - line.Start) / length;
    }

    private static double? RadiusOf(SketchEntity? entity) => entity switch
    {
        SketchCircle circle => circle.Radius,
        SketchArc arc => arc.Radius,
        _ => null,
    };

    private static (Vec2d, Vec2d)? Pair(SketchConstraint constraint, SketchEntitySet entities)
        => constraint.On.Length < 2
            || entities.Locate(constraint.On[0]) is not { } a
            || entities.Locate(constraint.On[1]) is not { } b
                ? null
                : (a, b);

    private static (SketchEntity, SketchEntity)? Two(
        SketchConstraint constraint, SketchEntitySet entities)
        => constraint.On.Length < 2
            || entities.Find(constraint.On[0].Entity) is not { } first
            || entities.Find(constraint.On[1].Entity) is not { } second
                ? null
                : (first, second);

    /// <summary>The two ends a horizontal or vertical constraint is about.</summary>
    private static (Vec2d, Vec2d)? Ends(SketchConstraint constraint, SketchEntitySet entities)
    {
        if (constraint.On.Length == 1)
        {
            return entities.Find(constraint.On[0].Entity) is SketchLine line
                ? (line.Start, line.End)
                : null;
        }

        return Pair(constraint, entities);
    }

    /// <summary>The unit directions of two lines.</summary>
    private static (Vec2d, Vec2d)? Directions(
        SketchConstraint constraint, SketchEntitySet entities)
    {
        if (Two(constraint, entities) is not (SketchLine first, SketchLine second))
        {
            return null;
        }

        // Normalised, so the residual is a sine or a cosine rather than a number that grows with
        // the lengths of both lines. One tolerance then means the same thing across a sketch.
        return first.Length <= Tolerance.LinearResolution
            || second.Length <= Tolerance.LinearResolution
                ? null
                : (first.Direction, second.Direction);
    }
}
