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

    /// <summary>How much a step of the minimal-motion pass must respect the constraints.</summary>
    /// <remarks>
    /// <para>
    /// The minimal-motion objective §5.6 asks for is a second pass, run once the constraints are
    /// already satisfied. It looks for the movement that most nearly takes the geometry to where it
    /// would rather be, out of the movements that leave the constraints alone — solving
    /// <c>(w·J'J + I)δ = d</c>, which for a large <c>w</c> is the projection of the wish <c>d</c>
    /// onto the directions the constraints do not pin.
    /// </para>
    /// <para>
    /// A second pass rather than extra rows in the first one. Weighting an objective against the
    /// constraints inside the same least-squares problem makes it bend them: the term's pull grows
    /// with how far the pointer is from where the geometry can reach, so a dimension of four came
    /// out as 4.0036 when the cursor was thirty units away. There is no weight that both breaks
    /// ties and never bends anything — a constraint is not a preference, and the two do not belong
    /// in one sum.
    /// </para>
    /// </remarks>
    private const double RespectConstraints = 1e6;

    /// <summary>How much the rest of the sketch matters while the pointer is being followed.</summary>
    /// <remarks>
    /// <para>
    /// Almost nothing, and not zero. The two wishes of a drag are ordered rather than weighed: get
    /// the point under the pointer, and <em>then</em> move as little else as possible. A single
    /// weighted sum cannot express that — a point tied to another by a coincidence came to rest at
    /// <c>(8·target + 1·origin)/9</c>, the exact compromise the weights asked for, which is a
    /// sketch that lags behind the cursor by an amount nobody chose.
    /// </para>
    /// <para>
    /// So the pointer is followed first with the rest of the sketch carrying this, which is enough
    /// to make the system solvable and to pick the smallest of the movements that follow the
    /// pointer equally well, and no more than that. What the rest of the sketch actually wants is
    /// asked separately, afterwards, once the pointer has what it came for.
    /// </para>
    /// <para>
    /// Small enough that what it costs is invisible. It holds the pointer back by roughly its own
    /// size times how far the pointer moved — at a millionth that was three microns on a three-unit
    /// drag, which a test at a part in a million could see.
    /// </para>
    /// </remarks>
    private const double BarelyMatters = 1e-9;

    /// <summary>How small a movement counts as having settled.</summary>
    private const double Settled = 1e-10;

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

            (Sketch moved, int steps) = SolveOne(
                analysis.Restrict(subsystem), sketch, drag, settings, clock, cancellationToken);

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

        return new SolveResult(
            working, SolveDiagnostics.Combine(verdicts), iterations, residual);
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

    /// <summary>Solves one group, which is one subsystem plus whatever ground it refers to.</summary>
    /// <param name="working">The group, with the drag's seed already applied.</param>
    /// <param name="before">
    /// The sketch as it was when the drag began. The minimal-motion objective measures from here
    /// rather than from the seeded state, or the drag would be asked to stay where it had just
    /// been put and would never move at all.
    /// </param>
    /// <param name="drag">What is being dragged, or null when this is not a drag.</param>
    /// <param name="settings">How hard to try.</param>
    /// <param name="clock">The whole solve's clock.</param>
    /// <param name="cancellationToken">Abandons the solve.</param>
    private static (Sketch Sketch, int Iterations) SolveOne(
        Sketch working,
        Sketch before,
        DragTarget? drag,
        SolverOptions settings,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        SketchParameters layout = SketchParameters.Of(working);
        ImmutableArray<int> frozen = SketchAnalysis.FrozenBy(working, layout);

        // Built from the seeded layout, which is the same numbers as before the drag but for the
        // held point -- and the held point's anchor is the pointer, not where it was. Restoring
        // the sketch first to measure from it was code that could not change an answer.
        _ = before;

        DragAnchor? anchor = drag is null ? null : DragAnchor.For(working, layout, drag);

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

        // Only once the constraints hold. An objective applied on top of a state the solve could
        // not fix would move geometry to suit a preference against a sketch that is already wrong.
        // No test tells this from its absence: the pass that follows leaves the constraints as it
        // found them, so on a sketch that never solved it moves things a preference asked for and
        // nothing contradicts it. The guard is about what the solver is entitled to assume.
        if (anchor is { } pull && residual <= System.Math.Max(settings.Tolerance, 1e-7))
        {
            // First the pointer, then everything else. Two passes because the two wishes are
            // ordered and not weighed: see BarelyMatters.
            iteration += Settle(
                working, layout, values, frozen,
                pull.Towards,
                p => pull.Held.Contains(p) ? 1 : BarelyMatters,
                settings, clock, cancellationToken, ref damping, ref residual);

            iteration += Settle(
                working, layout, values, [.. frozen, .. pull.Held],
                pull.Before,
                _ => 1,
                settings, clock, cancellationToken, ref damping, ref residual);
        }

        return (layout.Scatter(working, [.. values]), iteration);
    }

    /// <summary>Moves the geometry as near to where it would rather be as the constraints allow.</summary>
    /// <param name="sketch">The group being solved.</param>
    /// <param name="layout">How it lays out as a vector.</param>
    /// <param name="values">The numbers, moved in place.</param>
    /// <param name="frozen">What may not move.</param>
    /// <param name="towards">Where each parameter would rather be.</param>
    /// <param name="weightOf">How much each parameter's wish counts.</param>
    /// <param name="settings">How hard to try.</param>
    /// <param name="clock">The whole solve's clock.</param>
    /// <param name="cancellationToken">Abandons the pass.</param>
    /// <param name="damping">The Levenberg-Marquardt damping, carried between passes.</param>
    /// <param name="residual">How wrong the constraints are, updated as it goes.</param>
    /// <returns>How many rounds it took.</returns>
    /// <remarks>
    /// <para>
    /// The minimal-motion objective (P4-T07, §5.6), run only once the constraints are satisfied so
    /// that it can never be traded against them. Each round asks for the movement nearest the wish
    /// that the constraints barely notice — <c>(w·J'J + W)δ = W·d</c> with a large <c>w</c>, which
    /// is the projection of <c>d</c> onto the null space of the Jacobian — and then lets the
    /// ordinary solve clean up whatever second-order drift the step introduced.
    /// </para>
    /// <para>
    /// Where the constraints pin everything, the projection is nothing and this does nothing, which
    /// is right: a fully defined sketch has one answer and a drag cannot ask for another.
    /// </para>
    /// </remarks>
    private static int Settle(
        Sketch sketch,
        SketchParameters layout,
        double[] values,
        ImmutableArray<int> frozen,
        ImmutableArray<double> towards,
        Func<int, double> weightOf,
        SolverOptions settings,
        Stopwatch clock,
        CancellationToken cancellationToken,
        ref double damping,
        ref double residual)
    {
        int[] free = [.. Enumerable.Range(0, values.Length).Where(i => !frozen.Contains(i))];

        if (free.Length == 0)
        {
            return 0;
        }

        int rounds = 0;

        for (; rounds < 12; ++rounds)
        {
            if (cancellationToken.IsCancellationRequested || OutOfTime(settings, clock))
            {
                break;
            }

            ImmutableArray<double> at = Residuals(sketch, layout, values).Values;
            double[,] jacobian = Jacobian(sketch, layout, values, free, at);

            double[,] normal = new double[free.Length, free.Length];
            double[] wish = new double[free.Length];

            for (int i = 0; i < free.Length; ++i)
            {
                for (int j = 0; j < free.Length; ++j)
                {
                    double sum = 0;

                    for (int k = 0; k < at.Length; ++k)
                    {
                        sum += jacobian[k, i] * jacobian[k, j];
                    }

                    normal[i, j] = sum * RespectConstraints;
                }

                // The weight goes on the diagonal and on the right-hand side alike, because it
                // is the weight of a squared term: putting it on one and not the other scales the
                // answer by it, so a point pulled eight times harder moved eight times too far and
                // the next round did it again.
                double weight = weightOf(free[i]);

                normal[i, i] += weight;
                wish[i] = weight * (towards[free[i]] - values[free[i]]);
            }

            if (Dense.Solve(normal, wish) is not { } delta)
            {
                break;
            }

            double moved = 0;

            for (int i = 0; i < free.Length; ++i)
            {
                values[free[i]] += delta[i];
                moved += delta[i] * delta[i];
            }

            residual = Norm(Residuals(sketch, layout, values).Values);

            // Second-order drift: the projection is exact only for an infinitesimal step, so the
            // ordinary solve is let back in to put the constraints right before the next round
            // measures from a state that is slightly wrong.
            while (residual > System.Math.Max(settings.Tolerance, 1e-9)
                && Step(sketch, layout, values, frozen, ref damping, ref residual))
            {
            }

            if (System.Math.Sqrt(moved) <= Settled)
            {
                break;
            }
        }

        return rounds;
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

        if (residual > System.Math.Max(settings.Tolerance, SolveDiagnostics.SolvedWithin))
        {
            return Diagnose(part, settings);
        }

        // Satisfied, so the only question left is how much of it can still move -- which the
        // analysis already counted. Expressed as evidence rather than as a diagnosis built by
        // hand, so this path and the expensive one cannot word the same situation differently.
        return (
            SolveDiagnostics.From(new SolveEvidence(
                residual,
                Rank: 0,
                Equations: 0,
                subsystem.RemainingFreedom,
                [],
                [.. part.Entities.Ordered.Select(e => e.Id)],
                settings.Tolerance)),
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

    /// <summary>Measures what a diagnosis is made of, and leaves the judging to P4-T06.</summary>
    /// <remarks>
    /// <para>
    /// This solver's job here is the rank of its own Jacobian and which rows turned out to be
    /// implied by earlier ones. What those numbers <em>mean</em> — which of the five situations the
    /// sketch is in — is <see cref="SolveDiagnostics"/>'s, because it is a statement about sketches
    /// rather than about numerical methods, and two solvers deciding it separately would give a
    /// user two different diagnoses of one drawing.
    /// </para>
    /// <para>
    /// The rank is what makes any of it possible. Counting equations against unknowns says a sketch
    /// with two identical constraints is fully determined when it is not, and says nothing at all
    /// about which constraints are at fault.
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

        ImmutableArray<SketchEntityId> movable =
            [.. sketch.Entities.Ordered.Select(e => e.Id)];

        if (values.IsEmpty)
        {
            return SolveDiagnostics.From(new SolveEvidence(
                residual, 0, 0, free.Length, [], movable, options.Tolerance));
        }

        double[] parameters = [.. layout.Values];
        double[,] jacobian = Jacobian(sketch, layout, parameters, free, values);

        (int rank, ImmutableArray<int> dependent) = Dense.Rank(jacobian, RankTolerance);

        return SolveDiagnostics.From(new SolveEvidence(
            residual,
            rank,
            values.Length,
            free.Length,
            [.. dependent.Select(row => from[row]).Distinct()],
            movable,
            options.Tolerance));
    }

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
