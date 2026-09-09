using System.Collections.Immutable;

using OpenMCAD.Math;
using OpenMCAD.Solver.Assemblies;

namespace OpenMCAD.Core.Assemblies;

/// <summary>
/// One drag of a component, from mouse-down to mouse-up (P5-T06, §5.9).
/// </summary>
/// <remarks>
/// <para>
/// The same three policies <c>DragSession</c> settled for sketches (P4-T07), because they are about
/// the gap between how fast a pointer moves and how fast a solve finishes, and that gap does not
/// care what is being solved.
/// </para>
/// <para>
/// <b>Coalescing.</b> Pointer positions arrive faster than solves finish, and every one but the
/// newest is stale before it could be worked on. Keeping a queue would make the assembly lag
/// further behind the cursor the longer the drag went on, and the lag would never be recovered.
/// <see cref="Skipped"/> counts what was dropped, because a drag losing most of its frames is worth
/// being able to measure rather than discover from a video.
/// </para>
/// <para>
/// <b>A fixed baseline.</b> Every solve starts from the assembly as it was at mouse-down, never
/// from the previous frame's answer. Chaining frames lets components creep: each frame's small
/// compromise becomes the next frame's starting point, and parts the user never touched wander
/// across the product over a few hundred milliseconds. It also makes the drag reversible — moving
/// back to where it started gives back the assembly that was there.
/// </para>
/// <para>
/// <b>Only what the drag can disturb.</b> The sketcher solves the whole sketch; an assembly must
/// not solve the whole product, because the product is where the ten thousand components are.
/// <see cref="MateAnalysis.Reaching"/> says which bodies a movement can carry to, stopping at
/// grounded ones, and the solver is handed that sub-problem alone. Everything else is not merely
/// left unchanged — it is never passed to the solver, so no amount of numerical wandering can move
/// it.
/// </para>
/// <para>
/// <b>There is no separate minimal-motion pass</b>, and that is a claim worth being careful about.
/// The sketch solver needed one because it could jump to a distant solution; here every frame's
/// solve starts from the assembly at mouse-down and Levenberg–Marquardt converges to a nearby
/// configuration, so "the answer closest to where things were" falls out of where the search
/// begins. If a drag is ever seen to rearrange more than it must, that is the thing to build and
/// this note is the record that it was reasoned about rather than forgotten.
/// </para>
/// <para>
/// Synchronous, and deliberately not a thread — the same call the sketch session makes. Where a
/// solve runs is the shell's decision, and a session that started its own threads would take that
/// decision away while making itself untestable without one. What lives here is the policy: which
/// position gets solved, what it is solved from, and how much of the assembly it is solved over.
/// </para>
/// </remarks>
public sealed class AssemblyDragSession
{
    private readonly IAssemblySolver _solver;
    private readonly Func<MateEnd, MateElement?> _elementOf;
    private readonly MateSolverOptions _options;

    private Transform? _pending;

    /// <summary>Begins a drag.</summary>
    /// <param name="solver">What places the bodies.</param>
    /// <param name="assembly">The assembly as it is at mouse-down.</param>
    /// <param name="held">Which instance the user has hold of.</param>
    /// <param name="elementOf">What a mate's end attaches to, in the component's own frame.</param>
    /// <param name="options">How hard to try.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="held"/> does not name a placement of <paramref name="assembly"/>.
    /// </exception>
    public AssemblyDragSession(
        IAssemblySolver solver,
        Assembly assembly,
        OccurrencePath held,
        Func<MateEnd, MateElement?> elementOf,
        MateSolverOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(solver);
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(elementOf);

        if (held.IsRoot || assembly.FindOccurrence(held.Leaf) is null)
        {
            // Refused at mouse-down rather than reported at the first frame: a drag of nothing has
            // no sensible answer, and finding out sixteen milliseconds later is worse than finding
            // out now.
            throw new ArgumentException(
                $"'{held}' is not a placement in this assembly, so it cannot be dragged.",
                nameof(held));
        }

        _solver = solver;
        _elementOf = elementOf;
        _options = options ?? MateSolverOptions.Default;

        Start = assembly;
        Current = assembly;
        Held = held;
    }

    /// <summary>Gets the assembly as it was at mouse-down.</summary>
    public Assembly Start { get; }

    /// <summary>Gets the assembly as it stands after the last solve.</summary>
    public Assembly Current { get; private set; }

    /// <summary>Gets which instance is being dragged.</summary>
    public OccurrencePath Held { get; }

    /// <summary>Gets what the last solve found, or null if none has run.</summary>
    public MateSolveResult? Last { get; private set; }

    /// <summary>Gets how many pointer positions were replaced before they could be solved.</summary>
    public int Skipped { get; private set; }

    /// <summary>Gets how many bodies the last solve was over.</summary>
    /// <remarks>
    /// The measurement §5.9's ten-thousand-component case turns on. A drag that solved the whole
    /// product would report every body here, and the number is what makes that visible in a test
    /// rather than only in a frame time.
    /// </remarks>
    public int SolvedBodies { get; private set; }

    /// <summary>Gets whether there is a position waiting to be solved.</summary>
    public bool HasWork => _pending is not null;

    /// <summary>Records where the user has moved the component to.</summary>
    /// <param name="to">Where it is now.</param>
    /// <remarks>
    /// Replaces whatever was waiting, which was going to be solved into a position the user has
    /// already moved away from.
    /// </remarks>
    public void MoveTo(Transform to)
    {
        if (_pending is not null)
        {
            ++Skipped;
        }

        _pending = to;
    }

    /// <summary>Solves the latest position, if there is one waiting.</summary>
    /// <param name="cancellationToken">Abandons this solve.</param>
    /// <returns>What the solve found, or null if there was nothing to do.</returns>
    public MateSolveResult? Solve(CancellationToken cancellationToken = default)
    {
        if (_pending is not { } to)
        {
            return null;
        }

        _pending = null;

        // From Start, never from Current. See the note about creep above.
        ComponentOccurrence held = Start.FindOccurrence(Held.Leaf)!;
        Assembly moved = Start.ReplaceOccurrence(held with { Placement = to });

        MateSystem system = MateSystem.For(moved, _elementOf, Held);
        MateBodyId anchor = system.Instances.FirstOrDefault(pair => pair.Value.Equals(Held)).Key;

        ImmutableArray<MateBodyId> reaching =
            MateAnalysis.Reaching(system.Bodies, system.Mates, anchor);

        HashSet<MateBodyId> inPlay = [.. reaching];

        ImmutableArray<MateBody> bodies = [.. system.Bodies.Where(b => inPlay.Contains(b.Id))];

        // A mate with one end outside the sub-problem would be asking the solver about a body it
        // has not been given. It cannot arise -- Reaching follows exactly these mates -- except
        // through a grounded body, which is in the set but not expanded from, so both its ends are
        // present anyway. Filtered rather than assumed, because the assumption is one edit away
        // from being wrong and the failure would be an exception in a drag.
        ImmutableArray<AssemblyMate> mates =
        [
            .. system.Mates.Where(m =>
                m.Bodies.Length == 2 && inPlay.Contains(m.Bodies[0]) && inPlay.Contains(m.Bodies[1])),
        ];

        SolvedBodies = bodies.Length;

        MateSolveResult result = _solver.Solve(bodies, mates, _options, cancellationToken);

        Current = system.ApplyTo(moved, result);
        Last = result;

        return result;
    }

    /// <summary>Ends the drag, keeping where things ended up.</summary>
    /// <returns>The assembly as it now stands.</returns>
    /// <remarks>
    /// Anything still waiting is solved first, so that letting go leaves the assembly where the
    /// user last saw it heading rather than one frame behind.
    /// </remarks>
    public Assembly Commit()
    {
        Solve();

        return Current;
    }

    /// <summary>Abandons the drag, putting everything back.</summary>
    /// <returns>The assembly as it was at mouse-down.</returns>
    public Assembly Cancel()
    {
        _pending = null;
        Current = Start;
        Last = null;

        return Start;
    }

    /// <inheritdoc/>
    public override string ToString()
        => Skipped == 0
            ? $"dragging {Held}"
            : $"dragging {Held}, {Skipped} positions skipped";
}
