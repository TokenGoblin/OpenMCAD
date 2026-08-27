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
    /// A drag solves only the group holding what is dragged, because nothing outside it can have
    /// moved. It is still diagnosed as a whole sketch: the saving is in the iteration, which is the
    /// expensive part, and a drag that reported only its own group would flip the status to "fully
    /// defined" while the user held the mouse down over a sketch that was nothing of the kind.
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

        // One clock for the whole solve, not one per group. A budget spent afresh on each of forty
        // subsystems is forty times the budget, which is the opposite of what §5.6 asks for.
        //
        // No test distinguishes the two, and honestly: making one would need forty groups that
        // each exhaust their own budget, and the groups a sketch is actually made of converge in a
        // handful of iterations. The shared clock is right on the reasoning, not on a measurement.
        Stopwatch clock = Stopwatch.StartNew();

        SketchAnalysis analysis = SketchAnalysis.Of(drag is null ? sketch : Dragged(sketch, drag));
        Sketch working = analysis.Sketch;

        ImmutableArray<Subsystem> wanted = Wanted(analysis, drag);

        int iterations = 0;

        foreach (Subsystem subsystem in wanted)
        {
            // Both are also checked inside the sub-solve, so this saves the call rather than the
            // work, and no test can tell it from its absence. It stays because "stop when you have
            // been told to stop" belongs at the loop that would otherwise keep going.
            if (cancellationToken.IsCancellationRequested || OutOfTime(settings, clock))
            {
                break;
            }

            (Sketch moved, int steps) =
                SolveOne(analysis.Restrict(subsystem), settings, clock, cancellationToken);

            // Only what this group owns is written back. The ground it borrowed belongs to the
            // whole sketch and was never the sub-solve's to move -- though today that is a
            // statement of intent rather than a load-bearing check, because ground is frozen in
            // the sub-solve too and writing it back would write the same numbers.
            foreach (SketchEntityId id in subsystem.Entities)
            {
                if (moved.Entities.Find(id) is { } entity)
                {
                    working = working.With(entity);
                }
            }

            iterations += steps;
        }

        // Diagnosed after everything has moved, and over every group rather than only the ones
        // solved: a verdict about a sketch has to be about the sketch. A drag that reported only
        // its own group would flip the status to "fully defined" while the user held the mouse
        // down over a sketch that was nothing of the kind.
        SketchAnalysis after = SketchAnalysis.Of(working);

        HashSet<SketchEntityId> solvedHere = [.. wanted.SelectMany(w => w.Entities)];

        List<SolveDiagnosis> verdicts = [];
        double residual = 0;

        foreach (Subsystem subsystem in after.Subsystems)
        {
            Sketch part = after.Restrict(subsystem);

            bool touched = subsystem.Entities.IsEmpty
                || subsystem.Entities.Any(solvedHere.Contains);

            (SolveDiagnosis verdict, double left) = touched
                ? Diagnose(part, settings)
                : Unchanged(part, subsystem, settings);

            verdicts.Add(verdict);
            residual = System.Math.Max(residual, left);
        }

        return new SolveResult(working, Combined(verdicts), iterations, residual);
    }

    /// <summary>Which groups this solve is going to touch.</summary>
    /// <remarks>
    /// A drag narrows it to the group holding the dragged point — unless there is no such group,
    /// which happens for two quite different reasons. The point may be ground, in which case
    /// nothing about it can move; or the id may name geometry that is not in the sketch at all,
    /// which is what a drag begun before a delete looks like by the time it arrives. Neither is a
    /// reason to skip the solve, and treating them as one silently turned a broken sketch into a
    /// healthy-looking one.
    /// </remarks>
    private static ImmutableArray<Subsystem> Wanted(SketchAnalysis analysis, DragTarget? drag)
        => drag is not null && analysis.Containing(drag.Point.Entity) is { } touched
            ? [touched]
            : analysis.Subsystems;

    private static bool OutOfTime(SolverOptions settings, Stopwatch clock)
        => settings.TimeBudget is { } budget && clock.Elapsed > budget;

    /// <summary>Rolls several groups' verdicts into one for the sketch.</summary>
    /// <remarks>
    /// The worst outcome wins. A sketch with one contradicting group is a contradicting sketch
    /// however well the others solved, because the user cannot proceed — and reporting the best of
    /// them would say "fully defined" about a sketch that is not.
    /// </remarks>
    private static SolveDiagnosis Combined(List<SolveDiagnosis> verdicts)
    {
        if (verdicts.Count == 0)
        {
            return new SolveDiagnosis(SolveOutcome.WellConstrained, Message: "Nothing can move.");
        }

        if (verdicts.Count == 1)
        {
            return verdicts[0];
        }

        SolveOutcome worst = verdicts.Select(v => v.Outcome).OrderByDescending(Severity).First();

        // Freedom and the free-entity list belong to the under-constrained case and are reported
        // only there. "Conflicting, four degrees of freedom left" is two answers to two different
        // questions presented as one, and the field's own contract says which one it answers.
        bool loose = worst == SolveOutcome.UnderConstrained;

        return new SolveDiagnosis(
            worst,
            loose ? verdicts.Sum(v => v.RemainingFreedom) : 0,
            loose ? [.. verdicts.SelectMany(v => v.Free).Distinct()] : [],
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
    private static (Sketch Sketch, int Iterations) SolveOne(
        Sketch working,
        SolverOptions settings,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        SketchParameters layout = SketchParameters.Of(working);
        ImmutableArray<int> frozen = SketchAnalysis.FrozenBy(working, layout);

        double[] values = [.. layout.Values];

        double damping = 1e-3;
        int iteration = 0;

        double residual = Norm(Residuals(working, layout, values).Values);

        while (iteration < settings.MaximumIterations
            && residual > settings.Tolerance
            && !cancellationToken.IsCancellationRequested
            && !OutOfTime(settings, clock))
        {
            ++iteration;

            if (!Step(working, layout, values, frozen, ref damping, ref residual))
            {
                break;
            }
        }

        return (layout.Scatter(working, [.. values]), iteration);
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
    /// A point that is fixed is not moved at all. Seeding it and letting the solve pull it back
    /// would work only where something else constrained it; a lone fixed point has no equation to
    /// restore it, so the drag would quietly relocate the one piece of geometry the user had said
    /// must not move.
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

        SketchParameters layout = SketchParameters.Of(sketch);
        ImmutableArray<int> frozen = SketchAnalysis.FrozenBy(sketch, layout);

        if (layout.IndexOf(drag.Point) is { } at
            && frozen.Contains(at.X)
            && frozen.Contains(at.Y))
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

    /// <summary>Judges a group this solve did not touch, without paying for a Jacobian.</summary>
    /// <param name="part">The group, as a sketch that can stand on its own.</param>
    /// <param name="subsystem">What the analysis already worked out about it.</param>
    /// <param name="settings">How hard the solve was told to try.</param>
    /// <returns>The verdict, and how far from satisfied the group is.</returns>
    /// <remarks>
    /// <para>
    /// A drag has to report on the whole sketch and cannot afford to re-analyse it: the rank of a
    /// Jacobian costs one residual evaluation per free parameter, and paying that for every group
    /// on every frame is exactly the cost the decomposition exists to avoid. Nothing about an
    /// untouched group has moved, so its residual answers the question that matters — a group that
    /// cannot be satisfied has one, and gets the full analysis; a group that is satisfied does not,
    /// and its freedom is already known.
    /// </para>
    /// <para>
    /// What this gives up: a redundancy in an untouched group is not re-detected mid-drag, because
    /// a redundant group is satisfied and looks like any other satisfied group from the residual
    /// alone. It was reported when that group was last solved and nothing has happened to it since.
    /// </para>
    /// </remarks>
    private static (SolveDiagnosis Diagnosis, double Residual) Unchanged(
        Sketch part, Subsystem subsystem, SolverOptions settings)
    {
        double residual = Norm(ConstraintResiduals.Of(part).Values);

        if (residual > System.Math.Max(settings.Tolerance, 1e-7))
        {
            return Diagnose(part, settings);
        }

        return subsystem.RemainingFreedom > 0
            ? (Under(part, subsystem.RemainingFreedom), residual)
            : (new SolveDiagnosis(SolveOutcome.WellConstrained, Message: "Fully defined."),
                residual);
    }

    /// <summary>Works out which of the four situations one group is in.</summary>
    /// <param name="part">The group, as a sketch that can stand on its own.</param>
    /// <param name="settings">How hard the solve was told to try.</param>
    /// <returns>The verdict, and how far from satisfied the group ended up.</returns>
    private static (SolveDiagnosis Diagnosis, double Residual) Diagnose(
        Sketch part, SolverOptions settings)
    {
        SketchParameters layout = SketchParameters.Of(part);
        ImmutableArray<int> frozen = SketchAnalysis.FrozenBy(part, layout);

        double residual = Norm(ConstraintResiduals.Of(part).Values);

        return (Diagnose(part, layout, frozen, residual, settings), residual);
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
