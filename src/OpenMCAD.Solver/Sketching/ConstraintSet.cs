using System.Collections;
using System.Collections.Immutable;

namespace OpenMCAD.Solver.Sketching;

/// <summary>Something wrong with a constraint, found before the solver saw it.</summary>
/// <param name="Constraint">Which constraint.</param>
/// <param name="Message">What is wrong with it.</param>
public sealed record ConstraintViolation(SketchConstraintId Constraint, string Message)
{
    /// <inheritdoc/>
    public override string ToString() => Message;
}

/// <summary>
/// The constraints of one sketch.
/// </summary>
/// <remarks>
/// <para>
/// P4-T04. Ordered like the entity set and for the same reason: a solver reads constraints in some
/// order, and an order that came from a dictionary would make the same sketch converge differently
/// on two machines.
/// </para>
/// <para>
/// Validation happens against a sketch rather than in isolation, because most of what can be wrong
/// with a constraint is a fact about the geometry it names — a tangency between two points, a
/// radius on a line, a reference to something that has been deleted. §5.6 is explicit that a
/// diagnosis naming no specific constraint is useless to a user, and these are the ones that can be
/// named without solving anything.
/// </para>
/// </remarks>
public sealed class ConstraintSet : IReadOnlyCollection<SketchConstraint>
{
    private readonly ImmutableArray<SketchConstraint> _ordered;
    private readonly ImmutableDictionary<SketchConstraintId, SketchConstraint> _byId;

    private ConstraintSet(
        ImmutableArray<SketchConstraint> ordered,
        ImmutableDictionary<SketchConstraintId, SketchConstraint> byId)
    {
        _ordered = ordered;
        _byId = byId;
    }

    /// <summary>Gets a sketch with no constraints on it.</summary>
    public static ConstraintSet Empty { get; } =
        new([], ImmutableDictionary<SketchConstraintId, SketchConstraint>.Empty);

    /// <summary>Gets how many constraints there are.</summary>
    public int Count => _ordered.Length;

    /// <summary>Gets the constraints, in the order they were made.</summary>
    public ImmutableArray<SketchConstraint> Ordered => _ordered;

    /// <summary>Gets how many degrees of freedom these constraints remove between them.</summary>
    /// <remarks>
    /// A sum, not an analysis. Two constraints saying the same thing both count here and only one
    /// of them removes anything, which is exactly what makes a sketch redundant rather than
    /// over-constrained — telling the two apart needs the rank of the Jacobian and is the solver's
    /// job (P4-T06). This number is the upper bound the diagnosis starts from.
    /// </remarks>
    public int Removes => _ordered.Sum(c => c.Removes);

    /// <summary>Builds a set from some constraints.</summary>
    /// <param name="constraints">The constraints, in the order they were made.</param>
    /// <returns>The set.</returns>
    /// <exception cref="ArgumentException">A constraint has no id.</exception>
    public static ConstraintSet Of(IEnumerable<SketchConstraint> constraints)
    {
        ArgumentNullException.ThrowIfNull(constraints);

        ConstraintSet set = Empty;

        foreach (SketchConstraint constraint in constraints)
        {
            set = set.With(constraint);
        }

        return set;
    }

    /// <summary>Adds a constraint, or replaces the one with the same id.</summary>
    /// <param name="constraint">The constraint.</param>
    /// <returns>The new set.</returns>
    /// <exception cref="ArgumentException">The constraint has no id.</exception>
    public ConstraintSet With(SketchConstraint constraint)
    {
        ArgumentNullException.ThrowIfNull(constraint);

        if (!constraint.Id.IsValid)
        {
            throw new ArgumentException(
                $"This {constraint.Kind} constraint has no id, so nothing could remove it again.",
                nameof(constraint));
        }

        int at = IndexOf(constraint.Id);

        return new ConstraintSet(
            at < 0 ? _ordered.Add(constraint) : _ordered.SetItem(at, constraint),
            _byId.SetItem(constraint.Id, constraint));
    }

    /// <summary>Removes a constraint.</summary>
    /// <param name="id">Which one.</param>
    /// <returns>The new set, or this one if it was not there.</returns>
    public ConstraintSet Without(SketchConstraintId id)
    {
        int at = IndexOf(id);

        return at < 0 ? this : new ConstraintSet(_ordered.RemoveAt(at), _byId.Remove(id));
    }

    /// <summary>Removes every constraint that names an entity.</summary>
    /// <param name="entity">Which entity.</param>
    /// <returns>The new set.</returns>
    /// <remarks>
    /// What deleting a piece of geometry has to do. A constraint left pointing at something that no
    /// longer exists is not a constraint the solver can decline to apply — it is one that names a
    /// coordinate nobody will write, and the failure surfaces as a solve that does not converge.
    /// </remarks>
    public ConstraintSet WithoutThoseNaming(SketchEntityId entity)
    {
        ImmutableArray<SketchConstraint> kept =
            [.. _ordered.Where(c => !c.On.Any(o => o.Entity == entity))];

        return kept.Length == _ordered.Length
            ? this
            : new ConstraintSet(kept, kept.ToImmutableDictionary(c => c.Id));
    }

    /// <summary>Finds a constraint.</summary>
    /// <param name="id">Which one.</param>
    /// <returns>The constraint, or <see langword="null"/> if there is none.</returns>
    public SketchConstraint? Find(SketchConstraintId id)
        => _byId.TryGetValue(id, out SketchConstraint? constraint) ? constraint : null;

    /// <summary>Finds every constraint that names an entity.</summary>
    /// <param name="entity">Which entity.</param>
    /// <returns>The constraints, in order.</returns>
    public IEnumerable<SketchConstraint> Naming(SketchEntityId entity)
        => _ordered.Where(c => c.On.Any(o => o.Entity == entity));

    /// <summary>Checks every constraint against the geometry it names.</summary>
    /// <param name="entities">The sketch.</param>
    /// <returns>What is wrong, empty if nothing is.</returns>
    public ImmutableArray<ConstraintViolation> Validate(SketchEntitySet entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        ImmutableArray<ConstraintViolation>.Builder found =
            ImmutableArray.CreateBuilder<ConstraintViolation>();

        foreach (SketchConstraint constraint in _ordered)
        {
            string? complaint = Check(constraint, entities);

            if (complaint is not null)
            {
                found.Add(new ConstraintViolation(constraint.Id, complaint));
            }
        }

        return found.ToImmutable();
    }

    /// <inheritdoc/>
    public IEnumerator<SketchConstraint> GetEnumerator()
        => ((IEnumerable<SketchConstraint>)_ordered).GetEnumerator();

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc/>
    public override string ToString() => $"{Count} constraints, removing {Removes}";

    /// <summary>What is wrong with one constraint, or null if nothing is.</summary>
    private static string? Check(SketchConstraint constraint, SketchEntitySet entities)
    {
        ConstraintSchema schema = constraint.Schema;

        if (schema.HasValue && constraint.Value is null)
        {
            return $"A {schema.Label.ToLowerInvariant()} constraint needs a value and has none.";
        }

        if (!schema.HasValue && constraint.Value is not null)
        {
            return $"A {schema.Label.ToLowerInvariant()} constraint carries a value and has no use "
                + "for one.";
        }

        if (constraint.Value is { } value && !double.IsFinite(value))
        {
            return $"This {schema.Label.ToLowerInvariant()} constraint's value is not a number.";
        }

        // Every shape the kind accepts is tried, and the complaint reported is the one from the
        // shape with the right number of operands -- otherwise "horizontal" given one bad line
        // complains that it is not two points, which is true and unhelpful.
        List<string> complaints = [];

        foreach (ImmutableArray<OperandKind> shape in schema.Shapes)
        {
            if (shape.Length != constraint.On.Length)
            {
                continue;
            }

            string? complaint = Against(constraint, shape, entities, schema);

            if (complaint is null)
            {
                return null;
            }

            complaints.Add(complaint);
        }

        if (complaints.Count > 0)
        {
            return complaints[0];
        }

        string counts = string.Join(
            " or ", schema.Shapes.Select(s => s.Length).Distinct().Order());

        return $"A {schema.Label.ToLowerInvariant()} constraint takes {counts} operands and was "
            + $"given {constraint.On.Length}.";
    }

    private static string? Against(
        SketchConstraint constraint,
        ImmutableArray<OperandKind> shape,
        SketchEntitySet entities,
        ConstraintSchema schema)
    {
        for (int i = 0; i < shape.Length; ++i)
        {
            string? why = ConstraintSchema.Rejects(shape[i], constraint.On[i], entities);

            if (why is not null)
            {
                return $"A {schema.Label.ToLowerInvariant()} constraint cannot use "
                    + $"{constraint.On[i]}: {why}.";
            }
        }

        // Two operands that resolve to the same thing is the commonest way to write a constraint
        // that is trivially satisfied and removes nothing -- a line parallel to itself, a point
        // coincident with itself -- and the solver reports it as redundancy far from the cause.
        for (int i = 0; i < shape.Length; ++i)
        {
            for (int j = i + 1; j < shape.Length; ++j)
            {
                if (shape[i] == shape[j] && constraint.On[i] == constraint.On[j])
                {
                    return $"A {schema.Label.ToLowerInvariant()} constraint names "
                        + $"{constraint.On[i]} twice, which is always true and constrains nothing.";
                }
            }
        }

        return null;
    }

    private int IndexOf(SketchConstraintId id)
    {
        for (int i = 0; i < _ordered.Length; ++i)
        {
            if (_ordered[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }
}
