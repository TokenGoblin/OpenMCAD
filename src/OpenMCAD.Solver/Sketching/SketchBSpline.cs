using System.Collections.Immutable;

using OpenMCAD.Math;

namespace OpenMCAD.Solver.Sketching;

/// <summary>
/// A NURBS curve.
/// </summary>
/// <param name="Id">Which entity this is.</param>
/// <param name="Degree">The polynomial degree. Three is the usual choice.</param>
/// <param name="Poles">The control points, which the curve follows but need not pass through.</param>
/// <param name="Weights">
/// One weight per pole. All ones make a plain B-spline; unequal weights make it rational, which is
/// what lets a spline represent a circle exactly.
/// </param>
/// <param name="Knots">The distinct knot values, ascending.</param>
/// <param name="Multiplicities">How many times each knot repeats.</param>
/// <param name="IsPeriodic">Whether the curve closes on itself smoothly.</param>
/// <param name="IsConstruction">Whether it is scaffolding.</param>
/// <remarks>
/// <para>
/// Knots are stored as distinct values with multiplicities rather than as a flat repeated vector.
/// Both forms hold the same information and the compact one cannot express the mistake that matters
/// — a repeated value with a multiplicity that disagrees with how many times it appears — because
/// there is only one place the count is written.
/// </para>
/// <para>
/// Rational from the start, weights and all. A non-rational spline is the special case where every
/// weight is one, and retrofitting weights later would mean rewriting every file and every
/// evaluation written against the simpler form.
/// </para>
/// </remarks>
public sealed record SketchBSpline(
    SketchEntityId Id,
    int Degree,
    ImmutableArray<Vec2d> Poles,
    ImmutableArray<double> Weights,
    ImmutableArray<double> Knots,
    ImmutableArray<int> Multiplicities,
    bool IsPeriodic = false,
    bool IsConstruction = false)
    : SketchEntity(Id, IsConstruction)
{
    /// <inheritdoc/>
    public override string Kind => "spline";

    /// <inheritdoc/>
    public override bool IsClosed => IsPeriodic;

    /// <inheritdoc/>
    public override ImmutableArray<EntityPoint> Points => IsPeriodic
        ? [EntityPoint.Middle]
        : [EntityPoint.Start, EntityPoint.End, EntityPoint.Middle];

    /// <summary>Gets the control points, never a default array.</summary>
    public ImmutableArray<Vec2d> ControlPoints => Poles.IsDefault ? [] : Poles;

    /// <summary>Gets the weights, never a default array.</summary>
    public ImmutableArray<double> PoleWeights => Weights.IsDefault ? [] : Weights;

    /// <summary>Gets the knot values, never a default array.</summary>
    public ImmutableArray<double> KnotValues => Knots.IsDefault ? [] : Knots;

    /// <summary>Gets the knot multiplicities, never a default array.</summary>
    public ImmutableArray<int> KnotMultiplicities => Multiplicities.IsDefault ? [] : Multiplicities;

    /// <summary>Gets whether any weight differs from the others.</summary>
    public bool IsRational => PoleWeights.Any(
        w => !Tolerance.AreRelativelyEqual(w, PoleWeights.FirstOrDefault(1)));

    /// <summary>Builds a spline through nothing but poles, clamped and uniform.</summary>
    /// <param name="id">Which entity this is.</param>
    /// <param name="degree">The polynomial degree.</param>
    /// <param name="poles">The control points.</param>
    /// <param name="isConstruction">Whether it is scaffolding.</param>
    /// <returns>The spline.</returns>
    /// <remarks>
    /// What a drawing tool produces: the user places points and expects a curve that starts at the
    /// first and ends at the last. That is a clamped knot vector, which is the degree repeated at
    /// each end, and getting it wrong is the commonest way a spline ends up not touching its own
    /// endpoints.
    /// </remarks>
    public static SketchBSpline Through(
        SketchEntityId id, int degree, IEnumerable<Vec2d> poles, bool isConstruction = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(degree, 1);
        ArgumentNullException.ThrowIfNull(poles);

        ImmutableArray<Vec2d> points = [.. poles];

        ArgumentOutOfRangeException.ThrowIfLessThan(points.Length, degree + 1, nameof(poles));

        int interior = points.Length - degree - 1;

        ImmutableArray<double>.Builder knots = ImmutableArray.CreateBuilder<double>(interior + 2);
        ImmutableArray<int>.Builder multiplicities = ImmutableArray.CreateBuilder<int>(interior + 2);

        knots.Add(0);
        multiplicities.Add(degree + 1);

        for (int i = 1; i <= interior; ++i)
        {
            knots.Add((double)i / (interior + 1));
            multiplicities.Add(1);
        }

        knots.Add(1);
        multiplicities.Add(degree + 1);

        return new SketchBSpline(
            id,
            degree,
            points,
            [.. Enumerable.Repeat(1.0, points.Length)],
            knots.ToImmutable(),
            multiplicities.ToImmutable(),
            IsPeriodic: false,
            isConstruction);
    }

    /// <inheritdoc/>
    public override Vec2d PointAt(double t)
    {
        if (Degeneracy is not null)
        {
            return ControlPoints.Length > 0 ? ControlPoints[0] : Vec2d.Zero;
        }

        ImmutableArray<double> flat = FlatKnots();

        double first = flat[Degree];
        double last = flat[flat.Length - Degree - 1];
        double u = Tolerance.Clamp(first + ((last - first) * t), first, last);

        return DeBoor(flat, u);
    }

    /// <inheritdoc/>
    public override string? Degeneracy
    {
        get
        {
            if (Degree < 1)
            {
                return "This spline has a degree below one, so it is not a curve.";
            }

            if (ControlPoints.Length < Degree + 1)
            {
                return $"This spline is degree {Degree} and has {ControlPoints.Length} control "
                    + $"points, and needs at least {Degree + 1}.";
            }

            if (PoleWeights.Length != ControlPoints.Length)
            {
                return $"This spline has {ControlPoints.Length} control points and "
                    + $"{PoleWeights.Length} weights, and needs one weight for each.";
            }

            if (PoleWeights.Any(w => w <= 0 || !double.IsFinite(w)))
            {
                return "This spline has a weight that is zero, negative or infinite, which makes "
                    + "the curve undefined where it applies.";
            }

            if (KnotValues.Length != KnotMultiplicities.Length)
            {
                return $"This spline has {KnotValues.Length} knots and "
                    + $"{KnotMultiplicities.Length} multiplicities, and needs one of each.";
            }

            if (KnotValues.Length < 2)
            {
                return "This spline has fewer than two knots, so it spans no parameter range.";
            }

            for (int i = 1; i < KnotValues.Length; ++i)
            {
                if (KnotValues[i] <= KnotValues[i - 1])
                {
                    return "This spline's knots are not strictly ascending. Repeats are recorded "
                        + "as multiplicity, not by writing a value twice.";
                }
            }

            if (KnotMultiplicities.Any(m => m < 1))
            {
                return "This spline has a knot with a multiplicity below one, which is a knot that "
                    + "is not there.";
            }

            int expected = ControlPoints.Length + Degree + 1;
            int total = KnotMultiplicities.Sum();

            return IsPeriodic
                ? null
                : total != expected
                    ? $"This spline's knot vector has {total} entries and needs {expected}: one "
                        + $"more than the control points plus the degree."
                    : ControlPoints.Any(p => !p.IsFinite)
                        ? "This spline has a control point that is not at a finite position."
                        : null;
        }
    }

    /// <inheritdoc/>
    public bool Equals(SketchBSpline? other)
        => other is not null
            && Id == other.Id
            && Degree == other.Degree
            && IsPeriodic == other.IsPeriodic
            && IsConstruction == other.IsConstruction
            && ControlPoints.SequenceEqual(other.ControlPoints)
            && PoleWeights.SequenceEqual(other.PoleWeights)
            && KnotValues.SequenceEqual(other.KnotValues)
            && KnotMultiplicities.SequenceEqual(other.KnotMultiplicities);

    /// <inheritdoc/>
    public override int GetHashCode()
        => HashCode.Combine(Id, Degree, IsPeriodic, ControlPoints.Length, KnotValues.Length);

    /// <inheritdoc/>
    protected override Vec2d Locate(EntityPoint point) => point switch
    {
        EntityPoint.Start => PointAt(0),
        EntityPoint.End => PointAt(1),
        _ => PointAt(0.5),
    };

    /// <summary>Expands the compact knots into the repeated vector evaluation needs.</summary>
    private ImmutableArray<double> FlatKnots()
    {
        ImmutableArray<double>.Builder flat =
            ImmutableArray.CreateBuilder<double>(KnotMultiplicities.Sum());

        for (int i = 0; i < KnotValues.Length; ++i)
        {
            for (int repeat = 0; repeat < KnotMultiplicities[i]; ++repeat)
            {
                flat.Add(KnotValues[i]);
            }
        }

        return flat.ToImmutable();
    }

    /// <summary>Evaluates the curve at a knot value.</summary>
    /// <remarks>
    /// De Boor's algorithm, in homogeneous coordinates so that the weights are handled by the same
    /// recursion rather than by a separate correction afterwards. That is what makes a rational
    /// spline exact rather than approximately right.
    /// </remarks>
    private Vec2d DeBoor(ImmutableArray<double> flat, double u)
    {
        int span = Degree;

        while (span < flat.Length - Degree - 2 && u >= flat[span + 1])
        {
            ++span;
        }

        Vec2d[] points = new Vec2d[Degree + 1];
        double[] weights = new double[Degree + 1];

        for (int i = 0; i <= Degree; ++i)
        {
            int pole = System.Math.Clamp(span - Degree + i, 0, ControlPoints.Length - 1);

            weights[i] = PoleWeights[pole];
            points[i] = ControlPoints[pole] * weights[i];
        }

        for (int level = 1; level <= Degree; ++level)
        {
            for (int i = Degree; i >= level; --i)
            {
                int knot = span - Degree + i;

                double lower = flat[knot];
                double upper = flat[knot + Degree - level + 1];
                double width = upper - lower;

                double alpha = width <= Tolerance.Parametric ? 0 : (u - lower) / width;

                points[i] = (points[i - 1] * (1 - alpha)) + (points[i] * alpha);
                weights[i] = (weights[i - 1] * (1 - alpha)) + (weights[i] * alpha);
            }
        }

        return weights[Degree] <= Tolerance.LinearResolution
            ? points[Degree]
            : points[Degree] / weights[Degree];
    }
}
