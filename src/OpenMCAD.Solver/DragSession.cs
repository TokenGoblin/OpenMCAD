using OpenMCAD.Math;
using OpenMCAD.Solver.Sketching;

namespace OpenMCAD.Solver;

/// <summary>
/// One drag, from mouse-down to mouse-up.
/// </summary>
/// <remarks>
/// <para>
/// P4-T07. Two jobs, and both are about the gap between how fast a pointer moves and how fast a
/// sketch solves.
/// </para>
/// <para>
/// <b>Coalescing.</b> Pointer events arrive faster than solves finish, and every position but the
/// newest is already stale by the time it could be worked on. A queue would make the geometry lag
/// further behind the cursor the longer the drag went on, and the lag would never be recovered.
/// This keeps only the latest position and reports how many were skipped, because a drag that is
/// dropping most of its frames is a fact worth being able to measure rather than one to discover
/// from a video.
/// </para>
/// <para>
/// <b>A fixed baseline.</b> Every solve starts from the sketch as it was at mouse-down, not from
/// the result of the previous frame. Chaining frames lets a slow drag creep: each frame's small
/// compromise becomes the next frame's starting point, and geometry the user never touched drifts
/// across the plane over a few hundred milliseconds. It also makes the drag reversible — moving the
/// pointer back where it started gives back the sketch that was there.
/// </para>
/// <para>
/// Deliberately synchronous, and deliberately not a thread. Where the solve runs is the UI's
/// decision (P4-T15), and a session that started its own threads would take that decision away
/// while making itself untestable without one. What lives here is the policy: which position gets
/// solved, and what it is solved from.
/// </para>
/// </remarks>
public sealed class DragSession
{
    private readonly ISketchSolver _solver;
    private readonly SolverOptions _options;

    private Vec2d? _pending;

    /// <summary>Begins a drag.</summary>
    /// <param name="solver">What solves the sketch.</param>
    /// <param name="sketch">The sketch as it is at mouse-down.</param>
    /// <param name="held">Which point of which entity the user has hold of.</param>
    /// <param name="options">How hard to try. Defaults to the drag settings.</param>
    public DragSession(
        ISketchSolver solver,
        Sketch sketch,
        SketchPointRef held,
        SolverOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(solver);
        ArgumentNullException.ThrowIfNull(sketch);

        _solver = solver;
        _options = options ?? SolverOptions.ForDrag;

        Start = sketch;
        Current = sketch;
        Held = held;
    }

    /// <summary>Gets the sketch as it was at mouse-down.</summary>
    public Sketch Start { get; }

    /// <summary>Gets the sketch as it stands after the last solve.</summary>
    public Sketch Current { get; private set; }

    /// <summary>Gets which point is being dragged.</summary>
    public SketchPointRef Held { get; }

    /// <summary>Gets the result of the last solve, or null if none has run.</summary>
    public SolveResult? Last { get; private set; }

    /// <summary>Gets how many pointer positions were replaced before they could be solved.</summary>
    public int Skipped { get; private set; }

    /// <summary>Gets whether there is a position waiting to be solved.</summary>
    public bool HasWork => _pending is not null;

    /// <summary>Records where the pointer is now.</summary>
    /// <param name="to">Where it is.</param>
    /// <remarks>
    /// Replaces whatever was waiting. The position it replaces was going to be solved into geometry
    /// the user has already moved away from.
    /// </remarks>
    public void MoveTo(Vec2d to)
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
    public SolveResult? Solve(CancellationToken cancellationToken = default)
    {
        if (_pending is not { } to)
        {
            return null;
        }

        _pending = null;

        // From Start, never from Current. See the note about creep above.
        SolveResult result = _solver.Solve(
            Start, new DragTarget(Held, to), _options, cancellationToken);

        Current = result.Sketch;
        Last = result;

        return result;
    }

    /// <summary>Ends the drag, keeping where things ended up.</summary>
    /// <returns>The sketch as it now stands.</returns>
    /// <remarks>
    /// Any position still waiting is solved first, so that letting go where the pointer actually is
    /// leaves the sketch where the user last saw it heading rather than one frame behind.
    /// </remarks>
    public Sketch Commit()
    {
        Solve();

        return Current;
    }

    /// <summary>Abandons the drag, putting everything back.</summary>
    /// <returns>The sketch as it was at mouse-down.</returns>
    public Sketch Cancel()
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
