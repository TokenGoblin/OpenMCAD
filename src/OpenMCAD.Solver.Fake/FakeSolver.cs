using System.Collections.Immutable;
using System.Diagnostics;

using OpenMCAD.Math;
using OpenMCAD.Solver.Sketching;

namespace OpenMCAD.Solver.Fake;

/// <summary>
/// A working sketch solver, small enough to read.
/// </summary>
/// <remarks>
/// <para>
/// P4-T02. A real implementation, not a stub, for the same reason <c>FakeKernel</c> is: a fake that
/// returned its input unchanged would let every test above it pass without anything being solved,
/// and the first real solver would then be the first thing ever to run the code under test.
/// Levenberg–Marquardt over numerically differentiated residuals, with a dense factorisation — a
/// few hundred lines that converge on the sketches a unit test writes.
/// </para>
/// <para>
/// What it is not: fast, or decomposed into subsystems (P4-T05), or careful about the minimal-motion
/// objective a drag really needs (P4-T07). §5.6's 16 ms budget for a 200-entity sketch is
/// planegcs's to meet. This one exists so that everything above the solver — inference, snapping,
/// dimensions, profile detection, the corpus — can be built and tested before the native shim lands,
/// and so that the interface is proved by something that actually has to work through it.
/// </para>
/// <para>
/// The Jacobian is numerical. An analytic one would be faster and is what planegcs does; here it
/// would be several hundred lines of derivatives to get subtly wrong, and being wrong would show up
/// as slow convergence rather than as a failure — the worst way for an error to present.
/// </para>
/// </remarks>
public sealed class FakeSolver : ISketchSolver
{
    /// <summary>How far a parameter is nudged to measure a derivative.</summary>
    /// <remarks>
    /// The square root of machine epsilon, scaled by the parameter. Smaller loses the difference to
    /// rounding; larger measures the wrong slope on a curved residual.
    /// </remarks>
    private const double Nudge = 1.49e-8;

    /// <summary>How small a pivot counts as zero when finding the rank.</summary>
    /// <remarks>
    /// The line between "these constraints say something new" and "this one is implied by the
    /// others", and therefore between an over-constrained sketch and a redundant one. Too small and
    /// every nearly-dependent constraint is called independent, which turns redundancy into an
    /// unexplained failure to converge.
    /// </remarks>
    private const double RankTolerance = 1e-9;

    /// <inheritdoc/>
    public string Name => "fake";

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Decomposed first (P4-T05). A sketch is almost never one problem — it is several features
    /// that happen to share a plane — and solving all of it to satisfy a constraint in one corner
    /// is the difference between a drag that keeps up and one that does not.
    /// </para>
    /// <para>
    /// A drag goes further and solves only the group holding what is being dragged. Nothing outside
    /// it can have moved, by the definition of a subsystem, so there is nothing for the rest of the
    /// sketch to be re-solved to.
    /// </para>
    /// </remarks>
    public SolveResult Solve(
        Sketch sketch,
        DragTarget? drag = null,
        SolverOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sketch);

        SolverOptions settings = options ?? SolverOptions.Default;

        Sketch working = drag is null ? sketch : Dragged(sketch, drag);
        SketchAnalysis analysis = SketchAnalysis.Of(working);

        ImmutableArray<Subsystem> wanted = drag is null
            ? analysis.Subsystems
            : analysis.Containing(drag.Point.Entity) is { } touched ? [touched] : [];

        if (wanted.IsEmpty)
        {
            // Every entity is ground, or the drag is on geometry that is. The sketch still comes
            // back, because the caller has to draw something either way.
            return new SolveResult(
                working,
                new SolveDiagnosis(SolveOutcome.WellConstrained, Message: "Nothing can move."));
        }

        Sketch solved = working;
        List<SolveDiagnosis> verdicts = [];

        int iterations = 0;
        double residual = 0;

        foreach (Subsystem subsystem in wanted)
        {
            (Sketch moved, SolveDiagnosis verdict, int steps, double left) =
                SolveOne(analysis.Restrict(subsystem), settings, cancellationToken);

            // Only what this group owns is written back. The ground it borrowed belongs to the
            // whole sketch and was never the sub-solve's to move -- though today that is a
            // statement of intent rather than a load-bearing check, because ground is frozen in
            // the sub-solve too and writing it back would write the same numbers. Sabotaging this
            // to copy everything fails no test, and the loop stays because "a sub-solve owns its
            // own geometry" is the property, not "it happens to come out the same".
            foreach (SketchEntityId id in subsystem.Entities)
            {
                if (moved.Entities.Find(id) is { } entity)
                {
                    solved = solved.With(entity);
                }
            }

            verdicts.Add(verdict);
            iterations += steps;
            residual = System.Math.Max(residual, left);
        }

        return new SolveResult(solved, Combined(verdicts), iterations, residual);
    }

    /// <summary>Rolls several groups' verdicts into one for the sketch.</summary>
    /// <remarks>
    /// The worst outcome wins and the freedom adds up. A sketch with one contradicting group is a
    /// contradicting sketch however well the others solved, because the user cannot proceed — and
    /// reporting the best of them would say "fully defined" about a sketch that is not.
    /// </remarks>
    private static SolveDiagnosis Combined(List<SolveDiagnosis> verdicts)
    {
        if (verdicts.Count == 1)
        {
            return verdicts[0];
        }

        SolveOutcome worst = verdicts.Select(v => v.Outcome).OrderByDescending(Severity).First();

        return new SolveDiagnosis(
            worst,
            verdicts.Sum(v => v.RemainingFreedom),
            [.. verdicts.SelectMany(v => v.Free).Distinct()],
            [.. verdicts.SelectMany(v => v.Conflicts).Distinct()],
            [.. verdicts.SelectMany(v => v.Surplus).Distinct()],
            verdicts.First(v => v.Outcome == worst).Message);
    }

    private static int Severity(SolveOutcome outcome) => outcome switch
    {
        SolveOutcome.OverConstrained => 4,
        SolveOutcome.Failed => 3,
        SolveOutcome.Redundant => 2,
        SolveOutcome.UnderConstrained => 1,
        _ => 0,
    };

    /// <summary>Solves one group, which is one subsystem plus whatever ground it refers to.</summary>
    private static (Sketch Sketch, SolveDiagnosis Diagnosis, int Iterations, double Residual) SolveOne(
        Sketch working, SolverOptions settings, CancellationToken cancellationToken)
    {
        SketchAnalysis analysis = SketchAnalysis.Of(working);

        SketchParameters layout = analysis.Parameters;
        ImmutableArray<int> frozen = analysis.FrozenParameters;

        double[] values = [.. layout.Values];

        Stopwatch clock = Stopwatch.StartNew();
        double damping = 1e-3;
        int iteration = 0;

        double residual = Norm(Residuals(working, layout, values).Values);

        while (iteration < settings.MaximumIterations
            && residual > settings.Tolerance
            && !cancellationToken.IsCancellationRequested)
        {
            if (settings.TimeBudget is { } budget && clock.Elapsed > budget)
            {
                break;
            }

            ++iteration;

            if (!Step(working, layout, values, frozen, ref damping, ref residual))
            {
                break;
            }
        }

        Sketch moved = layout.Scatter(working, [.. values]);

        return (moved, Diagnose(moved, layout, frozen, residual, settings), iteration, residual);
    }

    /// <summary>Moves the dragged point to where the pointer is, as the starting guess.</summary>
    /// <remarks>
    /// <para>
    /// A seed, not a constraint. The dragged point is left free, so the constraints still act on it
    /// and pull it to the nearest configuration that satisfies them: drag the end of a line whose
    /// length is fixed and it slides round the circle of that radius rather than stretching. An
    /// earlier version froze the point instead, which let a drag violate a dimension outright, and
    /// the test noticed.
    /// </para>
    /// <para>
    /// Because a least-squares step starts from where the pointer is, the answer it walks to is
    /// usually the nearest one, which is roughly the minimal-motion objective §5.6 asks for. Only
    /// roughly: nothing here weights the movement of everything else, so a badly under-constrained
    /// sketch can still swing about in a way a user would not expect. P4-T07 is where that is put
    /// right.
    /// </para>
    /// </remarks>
    private static Sketch Dragged(Sketch sketch, DragTarget drag)
    {
        if (sketch.Entities.Find(drag.Point.Entity) is not { } entity)
        {
            return sketch;
        }

        SketchEntity moved = (entity, drag.Point.Point) switch
        {
            (SketchPoint point, _) => point with { Position = drag.To },
            (SketchLine line, EntityPoint.Start) => line with { Start = drag.To },
            (SketchLine line, EntityPoint.End) => line with { End = drag.To },
            (SketchCircle circle, EntityPoint.Centre) => circle with { Centre = drag.To },
            (SketchArc arc, EntityPoint.Centre) => arc with { Centre = drag.To },
            _ => entity,
        };

        return sketch.With(moved);
    }

    private static (ImmutableArray<double> Values, ImmutableArray<SketchConstraintId> From) Residuals(
        Sketch sketch, SketchParameters layout, double[] values)
        => ConstraintResiduals.Of(layout.Scatter(sketch, [.. values]));

    /// <summary>One Levenberg–Marquardt step, accepted only if it improves matters.</summary>
    /// <remarks>
    /// The improvement check is what makes this Levenberg–Marquardt rather than Gauss–Newton: a
    /// step that made things worse is rejected and retried with more damping, which is a shorter
    /// step in a safer direction. Removing it fails no test here, because Gauss–Newton converges on
    /// everything a unit test writes; it earns its place on the badly conditioned sketches a user
    /// draws, and it is standard for that reason rather than a demonstrated one.
    /// </remarks>
    private static bool Step(
        Sketch sketch,
        SketchParameters layout,
        double[] values,
        ImmutableArray<int> frozen,
        ref double damping,
        ref double residual)
    {
        ImmutableArray<double> at = Residuals(sketch, layout, values).Values;

        if (at.IsEmpty)
        {
            return false;
        }

        int[] free = [.. Enumerable.Range(0, values.Length).Where(i => !frozen.Contains(i))];

        if (free.Length == 0)
        {
            return false;
        }

        double[,] jacobian = Jacobian(sketch, layout, values, free, at);

        // The normal equations, damped. Solving J'J + lambda I rather than J directly is what lets
        // this cope with a singular Jacobian -- which is the ordinary case, since a sketch being
        // drawn is under-constrained almost all the time.
        double[,] normal = new double[free.Length, free.Length];
        double[] gradient = new double[free.Length];

        for (int i = 0; i < free.Length; ++i)
        {
            for (int j = 0; j < free.Length; ++j)
            {
                double sum = 0;

                for (int k = 0; k < at.Length; ++k)
                {
                    sum += jacobian[k, i] * jacobian[k, j];
                }

                normal[i, j] = sum;
            }

            double projected = 0;

            for (int k = 0; k < at.Length; ++k)
            {
                projected += jacobian[k, i] * at[k];
            }

            gradient[i] = -projected;
        }

        for (int attempt = 0; attempt < 12; ++attempt)
        {
            double[,] damped = (double[,])normal.Clone();

            for (int i = 0; i < free.Length; ++i)
            {
                damped[i, i] += damping * (1 + normal[i, i]);
            }

            if (Dense.Solve(damped, gradient) is not { } delta)
            {
                damping *= 10;
                continue;
            }

            double[] candidate = [.. values];

            for (int i = 0; i < free.Length; ++i)
            {
                candidate[free[i]] += delta[i];
            }

            double after = Norm(Residuals(sketch, layout, candidate).Values);

            if (after < residual)
            {
                Array.Copy(candidate, values, values.Length);
                residual = after;
                damping = System.Math.Max(damping / 10, 1e-12);

                return true;
            }

            damping *= 10;
        }

        return false;
    }

    private static double[,] Jacobian(
        Sketch sketch,
        SketchParameters layout,
        double[] values,
        int[] free,
        ImmutableArray<double> at)
    {
        double[,] jacobian = new double[at.Length, free.Length];

        for (int column = 0; column < free.Length; ++column)
        {
            int parameter = free[column];

            double step = Nudge * System.Math.Max(1, System.Math.Abs(values[parameter]));
            double original = values[parameter];

            values[parameter] = original + step;
            ImmutableArray<double> moved = Residuals(sketch, layout, values).Values;
            values[parameter] = original;

            // A nudge that changes how many residuals there are means the geometry crossed a case
            // boundary -- a line collapsing to a point, say -- and the column is meaningless.
            if (moved.Length != at.Length)
            {
                continue;
            }

            for (int row = 0; row < at.Length; ++row)
            {
                jacobian[row, column] = (moved[row] - at[row]) / step;
            }
        }

        return jacobian;
    }

    /// <summary>Works out which of the four situations the sketch is in.</summary>
    /// <remarks>
    /// <para>
    /// From the rank of the Jacobian, which is the only thing that can tell the cases apart.
    /// Counting equations against unknowns says a sketch with two identical constraints is fully
    /// determined when it is not, and says nothing at all about which constraints are at fault.
    /// </para>
    /// <para>
    /// A rank below the number of equations means some constraint is implied by the others. Whether
    /// that is harmless duplication or a contradiction depends on whether the residual came down:
    /// two constraints saying the same true thing solve, and two saying different things about the
    /// same freedom do not.
    /// </para>
    /// </remarks>
    private static SolveDiagnosis Diagnose(
        Sketch sketch,
        SketchParameters layout,
        ImmutableArray<int> frozen,
        double residual,
        SolverOptions options)
    {
        (ImmutableArray<double> values, ImmutableArray<SketchConstraintId> from) =
            ConstraintResiduals.Of(sketch);

        int[] free = [.. Enumerable.Range(0, layout.Count).Where(i => !frozen.Contains(i))];

        bool solved = residual <= System.Math.Max(options.Tolerance, 1e-7);

        if (values.IsEmpty)
        {
            return free.Length == 0
                ? new SolveDiagnosis(SolveOutcome.WellConstrained, Message: "Nothing can move.")
                : Under(sketch, free.Length);
        }

        double[] parameters = [.. layout.Values];
        double[,] jacobian = Jacobian(sketch, layout, parameters, free, values);

        (int rank, ImmutableArray<int> dependent) = Dense.Rank(jacobian, RankTolerance);

        ImmutableArray<SketchConstraintId> implied =
            [.. dependent.Select(row => from[row]).Distinct()];

        if (rank < values.Length)
        {
            // Some equation adds nothing. Whether that is a duplicate or a contradiction is
            // decided by whether the sketch actually came out solved.
            return solved
                ? new SolveDiagnosis(
                    SolveOutcome.Redundant,
                    System.Math.Max(0, free.Length - rank),
                    Redundant: implied,
                    Message: implied.Length == 1
                        ? "One constraint says what the others already said."
                        : $"{implied.Length} constraints say what the others already said.")
                : new SolveDiagnosis(
                    SolveOutcome.OverConstrained,
                    Conflicting: implied,
                    Message: "These constraints contradict each other, so no arrangement of the "
                        + "geometry satisfies them all.");
        }

        if (!solved)
        {
            return new SolveDiagnosis(
                SolveOutcome.Failed,
                Message: "The constraints do not contradict each other and the solve did not "
                    + "converge. Moving the geometry nearer to where it should be usually helps.");
        }

        return free.Length > rank ? Under(sketch, free.Length - rank) : new SolveDiagnosis(
            SolveOutcome.WellConstrained, Message: "Fully defined.");
    }

    private static SolveDiagnosis Under(Sketch sketch, int remaining) => new(
        SolveOutcome.UnderConstrained,
        remaining,
        [.. sketch.Entities.Ordered.Select(e => e.Id)],
        Message: remaining == 1
            ? "One degree of freedom is left."
            : $"{remaining} degrees of freedom are left.");

    private static double Norm(ImmutableArray<double> values)
    {
        double sum = 0;

        foreach (double value in values)
        {
            sum += value * value;
        }

        return System.Math.Sqrt(sum);
    }
}
