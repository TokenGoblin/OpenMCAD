using System.Collections.Immutable;

using OpenMCAD.Math;
using OpenMCAD.Solver;
using OpenMCAD.Solver.Sketching;

namespace OpenMCAD.ViewModels;

/// <summary>
/// The view model behind a sketch's entity toolbar, constraint palette and DOF readout (P4-T15).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is, and what it deliberately is not.</b> §5.6 asks for placement, display and
/// editing above the solver, and everything here is that: adding geometry, applying a constraint
/// to the current selection, reading whether the sketch is fully defined, and knowing which
/// constraints to highlight as conflicting or redundant. It is not the sketch canvas — there is no
/// mouse handling, no rendering, and no drag session here. <c>MainWindowViewModel</c>'s own
/// remarks record exactly this pattern already: the property manager is a placeholder until
/// P6-T04, the viewport is a placeholder until P2-T01/T02, both because the underlying mechanism
/// (a schema-driven property manager, an MVVM framework worth adopting) is a decision PLAN.md 4.1
/// defers until there is a real requirement to inform it, not a decision this task is positioned
/// to make. A sketch canvas has the same shape of dependency — it needs the shell chrome
/// (docking, the ribbon) that Phase 6 builds — so this is the same placeholder move one layer
/// closer to the geometry: everything a toolbar button or a palette click actually <em>does</em>,
/// built and tested now, wired to pixels later.
/// </para>
/// <para>
/// <b>No <c>System.Windows</c> type appears here</b> (ADR-0007, enforced by <c>tests/arch</c>), so
/// commands are plain methods rather than <c>ICommand</c> — the same choice
/// <c>PluginCommandItem.Invoke</c> already made as a bare <see cref="Action"/>. A future XAML view
/// wraps each in a relay command; this assembly stays framework-agnostic underneath it.
/// </para>
/// <para>
/// <b>Every mutation re-solves.</b> A toolbar or a palette exists so the user can see the effect of
/// what they just did, and a stale <see cref="StatusText"/> after an edit is a worse bug than a
/// slow one — §5.6's whole argument for reporting a diagnosis at all is that the user needs to
/// know now, not at the next unrelated redraw.
/// </para>
/// </remarks>
public sealed class SketchEditorViewModel : ObservableObject
{
    private readonly ISketchSolver _solver;

    private Sketch _sketch = Sketch.Empty;
    private SolveResult? _lastSolve;
    private string _statusText = "Empty sketch";
    private bool _isFullyDefined;
    private ImmutableArray<SketchPointRef> _selection = [];
    private ImmutableHashSet<SketchConstraintId> _conflictingConstraints = [];
    private ImmutableHashSet<SketchConstraintId> _redundantConstraints = [];
    private ImmutableHashSet<SketchEntityId> _freeEntities = [];

    /// <summary>Creates the view model.</summary>
    /// <param name="solver">
    /// What to solve with. Taken rather than defaulted to a concrete solver, so this assembly
    /// depends only on <see cref="ISketchSolver"/> and the caller decides which implementation —
    /// <c>FakeSolver</c> until P4-T01 lands the real one, and then either without this type
    /// changing at all, which is the entire point of ADR-0006.
    /// </param>
    public SketchEditorViewModel(ISketchSolver solver)
    {
        ArgumentNullException.ThrowIfNull(solver);

        _solver = solver;
    }

    /// <summary>Gets the sketch as it currently stands.</summary>
    public Sketch Sketch
    {
        get => _sketch;
        private set => SetProperty(ref _sketch, value);
    }

    /// <summary>Gets what the last solve found, or null before anything has been solved.</summary>
    public SolveResult? LastSolve
    {
        get => _lastSolve;
        private set => SetProperty(ref _lastSolve, value);
    }

    /// <summary>Gets a one-line description of the sketch's state, for the status readout.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>Gets whether the sketch has no freedom left to give and nothing wrong with it.</summary>
    /// <remarks>
    /// True for <see cref="SolveOutcome.WellConstrained"/> and for
    /// <see cref="SolveOutcome.Redundant"/> alike — a redundant sketch is, geometrically, exactly
    /// as defined as a well-constrained one; it merely has a constraint doing nothing, which
    /// <see cref="RedundantConstraints"/> is what names.
    /// </remarks>
    public bool IsFullyDefined
    {
        get => _isFullyDefined;
        private set => SetProperty(ref _isFullyDefined, value);
    }

    /// <summary>
    /// Gets or sets what the constraint palette will act on — the operands the next
    /// <see cref="ApplyConstraint"/> call uses, in the order they were selected.
    /// </summary>
    public ImmutableArray<SketchPointRef> Selection
    {
        get => _selection;
        set => SetProperty(ref _selection, value.IsDefault ? [] : value);
    }

    /// <summary>Gets the constraints to highlight as contradicting one another.</summary>
    public ImmutableHashSet<SketchConstraintId> ConflictingConstraints
    {
        get => _conflictingConstraints;
        private set => SetProperty(ref _conflictingConstraints, value);
    }

    /// <summary>Gets the constraints to highlight as saying nothing new.</summary>
    public ImmutableHashSet<SketchConstraintId> RedundantConstraints
    {
        get => _redundantConstraints;
        private set => SetProperty(ref _redundantConstraints, value);
    }

    /// <summary>Gets the entities the last solve reported as still having freedom in their group.</summary>
    public ImmutableHashSet<SketchEntityId> FreeEntities
    {
        get => _freeEntities;
        private set => SetProperty(ref _freeEntities, value);
    }

    /// <summary>Adds a point and re-solves.</summary>
    /// <param name="at">Where it goes.</param>
    /// <returns>Its id.</returns>
    public SketchEntityId AddPoint(Vec2d at) => Add(id => new SketchPoint(id, at));

    /// <summary>Adds a line and re-solves.</summary>
    /// <param name="from">Where it starts.</param>
    /// <param name="to">Where it ends.</param>
    /// <returns>Its id.</returns>
    public SketchEntityId AddLine(Vec2d from, Vec2d to) => Add(id => new SketchLine(id, from, to));

    /// <summary>Adds a circle and re-solves.</summary>
    /// <param name="centre">Where its centre is.</param>
    /// <param name="radius">How big it is.</param>
    /// <returns>Its id.</returns>
    public SketchEntityId AddCircle(Vec2d centre, double radius)
        => Add(id => new SketchCircle(id, centre, radius));

    /// <summary>Adds an arc and re-solves.</summary>
    /// <param name="centre">Where its centre is.</param>
    /// <param name="radius">How big it is.</param>
    /// <param name="startAngle">Where it begins, in radians.</param>
    /// <param name="endAngle">Where it ends, measured anticlockwise from the start.</param>
    /// <returns>Its id.</returns>
    public SketchEntityId AddArc(Vec2d centre, double radius, double startAngle, double endAngle)
        => Add(id => new SketchArc(id, centre, radius, startAngle, endAngle));

    /// <summary>Removes an entity, and every constraint that named it, and re-solves.</summary>
    /// <param name="id">Which entity.</param>
    public void RemoveEntity(SketchEntityId id)
    {
        Sketch = Sketch.Without(id);
        Selection = [.. Selection.Where(o => o.Entity != id)];
        Resolve();
    }

    /// <summary>Removes a constraint and re-solves.</summary>
    /// <param name="id">Which constraint.</param>
    public void RemoveConstraint(SketchConstraintId id)
    {
        Sketch = Sketch.Without(id);
        Resolve();
    }

    /// <summary>Marks the selected entities as construction geometry, or as profile geometry again.</summary>
    /// <param name="isConstruction">Whether they become construction geometry.</param>
    /// <returns>Why it could not be done, or null on success.</returns>
    public string? SetSelectionConstruction(bool isConstruction)
    {
        SketchEditResult result = SketchEdit.SetConstruction(
            Sketch, Selection.Select(o => o.Entity).Distinct(), isConstruction);

        if (!result.IsResolved)
        {
            return result.Reason;
        }

        Sketch = result.Sketch!;
        Resolve();
        return null;
    }

    /// <summary>
    /// Applies a constraint of the given kind to the current <see cref="Selection"/>, and re-solves.
    /// </summary>
    /// <param name="kind">Which constraint.</param>
    /// <param name="value">The number it carries, where its kind has one.</param>
    /// <returns>
    /// Why it could not be applied — the selection is the wrong shape, names geometry that is not
    /// there, or names something twice — or null on success. §5.6 is blunt that a diagnosis naming
    /// no specific constraint is useless, and this is worded by the same validation a saved sketch
    /// is checked against (<see cref="Sketch.Problems"/>), not a second opinion invented here.
    /// </returns>
    public string? ApplyConstraint(ConstraintKind kind, double? value = null)
    {
        SketchConstraint proposed = SketchConstraint.Of(kind, Selection, value);
        Sketch attempt = Sketch.With(proposed);

        ImmutableArray<ConstraintViolation> complaints = attempt.Constraints.Validate(attempt.Entities);
        ConstraintViolation? problem = complaints.FirstOrDefault(v => v.Constraint == proposed.Id);

        if (problem is not null)
        {
            return problem.Message;
        }

        Sketch = attempt;
        Resolve();
        return null;
    }

    private SketchEntityId Add(Func<SketchEntityId, SketchEntity> make)
    {
        SketchEntityId id = SketchEntityId.New();

        Sketch = Sketch.With(make(id));
        Resolve();

        return id;
    }

    /// <summary>Re-solves the sketch and refreshes everything the readout and the palette show.</summary>
    private void Resolve()
    {
        SolveResult result = _solver.Solve(Sketch);

        Sketch = result.Sketch;
        LastSolve = result;
        ConflictingConstraints = [.. result.Diagnosis.Conflicts];
        RedundantConstraints = [.. result.Diagnosis.Surplus];
        FreeEntities = [.. result.Diagnosis.Free];
        IsFullyDefined = result.Diagnosis.Outcome
            is SolveOutcome.WellConstrained or SolveOutcome.Redundant;
        StatusText = Describe(result.Diagnosis);
    }

    private static string Describe(SolveDiagnosis diagnosis) => diagnosis.Outcome switch
    {
        SolveOutcome.WellConstrained => "Fully defined",

        SolveOutcome.UnderConstrained => diagnosis.RemainingFreedom == 1
            ? "Under-defined — 1 degree of freedom remaining"
            : $"Under-defined — {diagnosis.RemainingFreedom} degrees of freedom remaining",

        SolveOutcome.OverConstrained => diagnosis.Conflicts.Length == 1
            ? "Over-defined — 1 conflicting constraint"
            : $"Over-defined — {diagnosis.Conflicts.Length} conflicting constraints",

        SolveOutcome.Redundant => diagnosis.Surplus.Length == 1
            ? "Fully defined — 1 redundant constraint"
            : $"Fully defined — {diagnosis.Surplus.Length} redundant constraints",

        SolveOutcome.Failed => "Could not solve — the numbers did not converge",

        _ => diagnosis.Outcome.ToString(),
    };
}
