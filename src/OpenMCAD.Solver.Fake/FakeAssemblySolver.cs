using System.Collections.Immutable;

using OpenMCAD.Math;
using OpenMCAD.Solver.Assemblies;

namespace OpenMCAD.Solver.Fake;

/// <summary>
/// Places an assembly's bodies so its mates hold, by Levenberg–Marquardt over numerically
/// differentiated residuals (P5-T05).
/// </summary>
/// <remarks>
/// <para>
/// The same shape and the same bargain as <see cref="FakeSolver"/>: correct, dependency-free, and
/// not fast. It exists so that everything above it — mate editing, drag, the feature that places a
/// component — can be built and tested against a real answer rather than waiting for a solver
/// worth shipping. Unlike the sketch side there is no planegcs waiting to replace it, so if it
/// turns out to be quick enough it may simply stay.
/// </para>
/// <para>
/// <b>Seven numbers per body, not six.</b> A placement is a quaternion and a translation, and the
/// obvious alternative — three rotation angles — has gimbal lock, where two of the three stop being
/// independent and the Jacobian silently loses a column. A quaternion has no such orientation, at
/// the price of one redundant number, and that price is paid by one extra residual per body saying
/// its length is one. Scale is not solved at all: mates constrain where a component sits, not how
/// big it is, so each body keeps the scale it came in with.
/// </para>
/// <para>
/// <b>The Jacobian is numerical</b>, as <see cref="FakeSolver"/>'s is and for the same reason: an
/// analytic one is faster and is another chance to be wrong in a way that shows up as slow
/// convergence rather than as an error. Central differences, so the step is second-order accurate
/// and a residual that is flat to first order — which is most of them at the solution — still gives
/// a usable derivative.
/// </para>
/// </remarks>
public sealed class FakeAssemblySolver : IAssemblySolver
{
    /// <summary>How far to nudge a parameter when differentiating.</summary>
    /// <remarks>
    /// One step for both halves of the parameter vector, which is only defensible because the two
    /// halves are comparable: a quaternion component is of order one, and a translation is in
    /// metres, so the model is metre-scale assemblies. A product measured in kilometres would want
    /// this scaled per column, which is a real limitation and is written down rather than hidden.
    /// </remarks>
    private const double Step = 1e-7;

    /// <summary>How many times to push off a stalled position before believing the stall.</summary>
    /// <remarks>
    /// Three, because each nudge turns every free body about a different axis and three mutually
    /// perpendicular axes cannot all leave a body on the same saddle. A fourth would be a fourth
    /// chance for a genuinely contradictory assembly to spend iterations proving it again.
    /// </remarks>
    private const int MaximumNudges = 3;

    /// <inheritdoc />
    public string Name => "fake-assembly";

    /// <inheritdoc />
    public MateSolveResult Solve(
        IReadOnlyCollection<MateBody> bodies,
        IReadOnlyCollection<AssemblyMate> mates,
        MateSolverOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bodies);
        ArgumentNullException.ThrowIfNull(mates);

        MateSolverOptions settings = options ?? MateSolverOptions.Default;

        ImmutableArray<UnsupportedMate> unsupported =
        [
            .. mates
                .Select(m => (Mate: m, Pairing: MatePairing.For(m)))
                .Where(pair => !pair.Pairing.IsSupported)
                .Select(pair => new UnsupportedMate(pair.Mate.Id, pair.Pairing.Reason!)),
        ];

        ImmutableArray<AssemblyMate> solvable =
            [.. mates.Where(m => MatePairing.For(m).IsSupported && m.Bodies.Length == 2)];

        Layout layout = new(bodies);

        if (layout.Free.Length == 0 || solvable.Length == 0)
        {
            // Nothing to move, or nothing asking it to move. Reported rather than run, because a
            // least-squares step over no unknowns is not a solve that trivially succeeds -- it is a
            // question nobody asked.
            return Settled(layout, solvable, unsupported, 0, settings);
        }

        double[] state = layout.Current();
        double best = SumOfSquares(Evaluate(layout, solvable, state));
        double damping = 1e-3;
        int taken = 0;
        int nudges = 0;

        for (int step = 0; step < settings.MaximumIterations && best > Squared(settings.Tolerance); ++step)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ++taken;

            if (TryStep(layout, solvable, state, ref damping, ref best, out double[] moved))
            {
                state = moved;
                continue;
            }

            // A step that cannot improve a residual that is not yet zero means one of two things,
            // and they need opposite responses. Either the mates genuinely contradict each other,
            // or the assembly is sitting exactly on a saddle of the least-squares objective, where
            // the gradient vanishes without the residual doing so.
            //
            // The saddle is not a curiosity: two faces both pointing up, asked to be flush, is the
            // commonest mate anybody makes. Turning a unit vector at the pole changes its component
            // along the pole only to second order, so the whole residual lies in a direction the
            // Jacobian cannot see, and every gradient method stops dead.
            //
            // Nudging off it and trying again tells the two apart -- a contradiction stalls again
            // from the new position, a saddle does not. The turn is a fixed axis and a fixed angle
            // rather than a random one, because ADR-0011 asks a rebuild to be reproducible and a
            // random restart would make the answer depend on the seed.
            if (nudges >= MaximumNudges)
            {
                break;
            }

            ++nudges;
            layout.Nudge(state, nudges);
            best = SumOfSquares(Evaluate(layout, solvable, state));
            damping = 1e-3;
        }

        layout.Apply(state);

        return Settled(layout, solvable, unsupported, taken, settings);
    }

    private static double Squared(double value) => value * value;

    private static double SumOfSquares(double[] values)
    {
        double total = 0;

        foreach (double value in values)
        {
            total += value * value;
        }

        return total;
    }

    /// <summary>One Levenberg–Marquardt step, accepted only if it improves matters.</summary>
    /// <remarks>
    /// The improvement test is what makes this Levenberg–Marquardt rather than Gauss–Newton. An
    /// assembly is where that earns its keep: a component starting far from where its mates want it
    /// gives a Jacobian that points confidently in a direction that overshoots, and the damping is
    /// what stops the first step throwing it further away than it began.
    /// </remarks>
    private static bool TryStep(
        Layout layout,
        ImmutableArray<AssemblyMate> mates,
        double[] state,
        ref double damping,
        ref double best,
        out double[] moved)
    {
        double[] residuals = Evaluate(layout, mates, state);
        double[,] jacobian = Jacobian(layout, mates, state, residuals.Length);

        int unknowns = state.Length;

        double[,] normal = new double[unknowns, unknowns];
        double[] gradient = new double[unknowns];

        for (int i = 0; i < unknowns; ++i)
        {
            for (int j = 0; j < unknowns; ++j)
            {
                double sum = 0;

                for (int r = 0; r < residuals.Length; ++r)
                {
                    sum += jacobian[r, i] * jacobian[r, j];
                }

                normal[i, j] = sum;
            }

            double slope = 0;

            for (int r = 0; r < residuals.Length; ++r)
            {
                slope += jacobian[r, i] * residuals[r];
            }

            gradient[i] = -slope;
        }

        for (int attempt = 0; attempt < 8; ++attempt)
        {
            double[,] damped = (double[,])normal.Clone();

            for (int i = 0; i < unknowns; ++i)
            {
                // Damping on the diagonal is also what keeps this solvable at all: an
                // under-constrained assembly -- the ordinary case while a user is still mating --
                // has a singular normal matrix, and lambda is what makes it invertible.
                damped[i, i] += damping;
            }

            double[]? delta = Dense.Solve(damped, gradient);

            if (delta is null)
            {
                damping *= 10;
                continue;
            }

            double[] candidate = new double[unknowns];

            for (int i = 0; i < unknowns; ++i)
            {
                candidate[i] = state[i] + delta[i];
            }

            layout.Renormalise(candidate);

            double score = SumOfSquares(Evaluate(layout, mates, candidate));

            if (score < best)
            {
                best = score;
                damping = System.Math.Max(damping / 10, 1e-12);
                moved = candidate;

                return true;
            }

            damping *= 10;
        }

        moved = state;

        return false;
    }

    private static double[] Evaluate(
        Layout layout, ImmutableArray<AssemblyMate> mates, double[] state)
    {
        List<double> values = [];

        foreach (AssemblyMate mate in mates)
        {
            values.AddRange(MateResiduals.Of(
                mate, layout.PlacementOf(mate.First.Body, state), layout.PlacementOf(mate.Second.Body, state)));
        }

        // One per free body, holding its quaternion on the unit sphere. Without it the seventh
        // number per body is unconstrained and the solver wanders along it, which shows up as a
        // rank that never reaches what the mates should have removed.
        foreach (MateBodyId id in layout.Free)
        {
            values.Add(layout.QuaternionOf(id, state).LengthSquared - 1);
        }

        return [.. values];
    }

    private static double[,] Jacobian(
        Layout layout, ImmutableArray<AssemblyMate> mates, double[] state, int rows)
    {
        double[,] jacobian = new double[rows, state.Length];
        double[] nudged = (double[])state.Clone();

        for (int column = 0; column < state.Length; ++column)
        {
            double original = nudged[column];

            nudged[column] = original + Step;
            double[] forward = Evaluate(layout, mates, nudged);

            nudged[column] = original - Step;
            double[] backward = Evaluate(layout, mates, nudged);

            nudged[column] = original;

            for (int row = 0; row < rows; ++row)
            {
                jacobian[row, column] = (forward[row] - backward[row]) / (2 * Step);
            }
        }

        return jacobian;
    }

    /// <summary>Reads the outcome off the residuals and the rank of the Jacobian.</summary>
    /// <remarks>
    /// <para>
    /// Four questions in order, and the order is what makes the answers mean anything. Does
    /// anything still fail to hold? Then either the mates contradict each other or the numbers did
    /// not converge, and the rank is what tells those apart: a system with independent rows that
    /// cannot be satisfied is over-constrained, and one that ran out of iterations is a failure.
    /// Does everything hold? Then the freedom left is the unknowns less the rank, and a mate that
    /// contributed no independent row is saying what another already said.
    /// </para>
    /// <para>
    /// This is the rank-based figure <c>MateAnalysis.Freedom</c> deliberately does not claim. That
    /// one counts mates one at a time and reports a lower bound because it has no Jacobian; this one
    /// has, so it can say what the freedom actually is.
    /// </para>
    /// </remarks>
    private static MateSolveResult Settled(
        Layout layout,
        ImmutableArray<AssemblyMate> mates,
        ImmutableArray<UnsupportedMate> unsupported,
        int iterations,
        MateSolverOptions options)
    {
        double[] state = layout.Current();
        double[] residuals = Evaluate(layout, mates, state);
        bool holds = residuals.All(r => System.Math.Abs(r) <= options.Tolerance);

        int unknowns = state.Length;
        double[,] jacobian = unknowns > 0 && residuals.Length > 0
            ? Jacobian(layout, mates, state, residuals.Length)
            : new double[0, 0];

        int rank = residuals.Length > 0 && unknowns > 0
            ? Dense.Rank(jacobian, options.Tolerance).Rank
            : 0;

        // The normalisation rows always contribute one apiece and constrain nothing the user cares
        // about, so they come off both sides: six real degrees of freedom per free body, less what
        // the mates independently remove.
        int freedom = System.Math.Max(0, unknowns - rank);

        ImmutableArray<MateBodyId> free = layout.Free;

        if (!holds)
        {
            bool ranOut = iterations >= options.MaximumIterations;

            return new MateSolveResult(
                layout.Placements(),
                new MateDiagnosis(
                    ranOut ? MateSolveOutcome.Failed : MateSolveOutcome.OverConstrained,
                    freedom,
                    free,
                    Conflicting: ranOut ? [] : Unsatisfied(layout, mates, state, options.Tolerance),
                    Unsupported: unsupported,
                    Message: ranOut
                        ? "The mates did not converge in the iterations allowed."
                        : "No placement satisfies all of these mates."),
                iterations);
        }

        ImmutableArray<MateId> redundant = Redundant(mates, jacobian, rank, options.Tolerance);

        MateSolveOutcome outcome = (freedom, redundant.Length) switch
        {
            (0, 0) => MateSolveOutcome.WellConstrained,
            (0, _) => MateSolveOutcome.Redundant,
            _ => MateSolveOutcome.UnderConstrained,
        };

        return new MateSolveResult(
            layout.Placements(),
            new MateDiagnosis(
                outcome,
                freedom,
                free,
                Redundant: redundant,
                Unsupported: unsupported,
                Message: outcome == MateSolveOutcome.UnderConstrained
                    ? $"{freedom} degrees of freedom remain."
                    : null),
            iterations);
    }

    /// <summary>Which mates say nothing the others had not already said.</summary>
    /// <remarks>
    /// <para>
    /// A mate is redundant when taking it away leaves the rank unchanged. That is the definition,
    /// and it is worth spelling out because the obvious cheaper test is wrong: asking which
    /// <em>rows</em> a rank found to be linearly dependent flags nearly every mate ever written,
    /// since almost all of them contribute more rows than degrees of freedom on purpose. Two planes
    /// flush are four rows describing three; three point pins are nine rows describing six. Those
    /// rows are dependent by construction and the mates are not redundant at all.
    /// </para>
    /// <para>
    /// Two mates that repeat each other are both named, which is right rather than a limitation:
    /// neither is more spare than the other, and a diagnosis that picked one would be telling the
    /// user to delete a mate it had no reason to prefer.
    /// </para>
    /// <para>
    /// One rank per mate, over a matrix already computed. That is quadratic in the mates for a
    /// dense factorisation and is affordable at the scale this solver is honest about; a product
    /// with thousands of mates wants the incremental factorisation that a solver worth shipping
    /// would have anyway.
    /// </para>
    /// </remarks>
    private static ImmutableArray<MateId> Redundant(
        ImmutableArray<AssemblyMate> mates, double[,] jacobian, int rank, double tolerance)
    {
        if (mates.Length <= 1 || jacobian.Length == 0)
        {
            return [];
        }

        ImmutableArray<MateId>.Builder found = ImmutableArray.CreateBuilder<MateId>();
        int at = 0;

        foreach (AssemblyMate mate in mates)
        {
            int count = MateResiduals.Count(mate);

            if (count > 0 && Dense.Rank(Without(jacobian, at, count), tolerance).Rank == rank)
            {
                found.Add(mate.Id);
            }

            at += count;
        }

        return found.ToImmutable();
    }

    /// <summary>The Jacobian without one mate's rows.</summary>
    private static double[,] Without(double[,] jacobian, int from, int count)
    {
        int rows = jacobian.GetLength(0);
        int columns = jacobian.GetLength(1);
        double[,] kept = new double[rows - count, columns];

        int at = 0;

        for (int row = 0; row < rows; ++row)
        {
            if (row >= from && row < from + count)
            {
                continue;
            }

            for (int column = 0; column < columns; ++column)
            {
                kept[at, column] = jacobian[row, column];
            }

            ++at;
        }

        return kept;
    }

    /// <summary>Which mates are not satisfied where the solve ended up.</summary>
    /// <remarks>
    /// The honest answer to "which mates conflict", and better than anything the rank can say. A
    /// least-squares solve settles where the error is spread across whichever mates disagree, so
    /// the ones still measurably wrong are exactly the ones the user has to look at. §5.6 asks for
    /// the set rather than the verdict, and this is a set the user can act on without knowing what
    /// a Jacobian is.
    /// </remarks>
    private static ImmutableArray<MateId> Unsatisfied(
        Layout layout, ImmutableArray<AssemblyMate> mates, double[] state, double tolerance)
    {
        ImmutableArray<MateId>.Builder found = ImmutableArray.CreateBuilder<MateId>();

        foreach (AssemblyMate mate in mates)
        {
            double[] residuals = MateResiduals.Of(
                mate,
                layout.PlacementOf(mate.First.Body, state),
                layout.PlacementOf(mate.Second.Body, state));

            if (residuals.Any(r => System.Math.Abs(r) > tolerance))
            {
                found.Add(mate.Id);
            }
        }

        return found.ToImmutable();
    }

    /// <summary>Which body owns which numbers, and how to read a placement back out.</summary>
    private sealed class Layout
    {
        private readonly ImmutableArray<MateBody> _bodies;
        private readonly Dictionary<MateBodyId, int> _columnOf = [];
        private readonly Dictionary<MateBodyId, MateBody> _byId = [];
        private readonly Dictionary<MateBodyId, Transform> _solved = [];

        public Layout(IReadOnlyCollection<MateBody> bodies)
        {
            _bodies = [.. bodies];

            foreach (MateBody body in _bodies)
            {
                _byId[body.Id] = body;
                _solved[body.Id] = body.Placement;
            }

            // Ordered by id so that two runs assemble the same system. A column order taken from a
            // dictionary would converge differently on two machines, which ADR-0011 forbids.
            ImmutableArray<MateBody> free =
                [.. _bodies.Where(b => !b.IsGrounded).OrderBy(b => b.Id.Value)];

            Free = [.. free.Select(b => b.Id)];

            for (int i = 0; i < free.Length; ++i)
            {
                _columnOf[free[i].Id] = i * 7;
            }
        }

        public ImmutableArray<MateBodyId> Free { get; }

        public double[] Current()
        {
            double[] state = new double[Free.Length * 7];

            foreach (MateBodyId id in Free)
            {
                Transform placement = _solved[id];
                int at = _columnOf[id];

                state[at] = placement.Rotation.X;
                state[at + 1] = placement.Rotation.Y;
                state[at + 2] = placement.Rotation.Z;
                state[at + 3] = placement.Rotation.W;
                state[at + 4] = placement.Translation.X;
                state[at + 5] = placement.Translation.Y;
                state[at + 6] = placement.Translation.Z;
            }

            return state;
        }

        public Quatd QuaternionOf(MateBodyId id, double[] state)
        {
            int at = _columnOf[id];

            return new Quatd(state[at], state[at + 1], state[at + 2], state[at + 3]);
        }

        public Transform PlacementOf(MateBodyId id, double[] state)
        {
            if (!_columnOf.TryGetValue(id, out int at))
            {
                // Grounded, or a body nobody listed. Either way its placement is an input.
                return _byId.TryGetValue(id, out MateBody? body) ? body.Placement : Transform.Identity;
            }

            Quatd rotation = new(state[at], state[at + 1], state[at + 2], state[at + 3]);
            Vec3d translation = new(state[at + 4], state[at + 5], state[at + 6]);

            // Normalised on the way out rather than trusted. The normalisation residual pulls the
            // quaternion towards the unit sphere but never pins it exactly, and a placement built
            // from a slightly long quaternion scales the geometry it moves.
            return new Transform(
                rotation.LengthSquared > 0 ? rotation.Normalized() : Quatd.Identity,
                translation,
                _byId[id].Placement.Scale);
        }

        /// <summary>Turns every free body a little, to leave a saddle the gradient cannot.</summary>
        /// <param name="state">The parameters, changed in place.</param>
        /// <param name="attempt">Which nudge this is, choosing the axis.</param>
        /// <remarks>
        /// A fixed axis per attempt rather than a random direction, so that the same assembly
        /// solves the same way twice (ADR-0011). The three axes are the world's, which cannot all
        /// be parallel to whatever direction the residual is stuck in.
        /// </remarks>
        public void Nudge(double[] state, int attempt)
        {
            Vec3d axis = attempt switch
            {
                1 => Vec3d.UnitX,
                2 => Vec3d.UnitY,
                _ => Vec3d.UnitZ,
            };

            Quatd turn = Quatd.FromAxisAngle(axis, 0.1);

            foreach (MateBodyId id in Free)
            {
                int at = _columnOf[id];
                Quatd rotation = new(state[at], state[at + 1], state[at + 2], state[at + 3]);
                Quatd turned = turn * (rotation.LengthSquared > 0 ? rotation.Normalized() : Quatd.Identity);

                state[at] = turned.X;
                state[at + 1] = turned.Y;
                state[at + 2] = turned.Z;
                state[at + 3] = turned.W;
            }
        }

        public void Renormalise(double[] state)
        {
            foreach (MateBodyId id in Free)
            {
                int at = _columnOf[id];
                Quatd rotation = new(state[at], state[at + 1], state[at + 2], state[at + 3]);

                if (rotation.LengthSquared <= 0)
                {
                    continue;
                }

                Quatd unit = rotation.Normalized();

                state[at] = unit.X;
                state[at + 1] = unit.Y;
                state[at + 2] = unit.Z;
                state[at + 3] = unit.W;
            }
        }

        public void Apply(double[] state)
        {
            foreach (MateBodyId id in Free)
            {
                _solved[id] = PlacementOf(id, state);
            }
        }

        public ImmutableDictionary<MateBodyId, Transform> Placements()
            => _solved.ToImmutableDictionary();
    }
}
