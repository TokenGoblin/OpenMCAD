using System.Collections.Immutable;

namespace OpenMCAD.Solver.Sketching;

/// <summary>
/// A sketch: geometry, and what has been said about it.
/// </summary>
/// <remarks>
/// <para>
/// P4-T04. The two halves are held together because almost nothing can be said about either alone.
/// Whether a constraint is valid is a fact about the geometry it names; whether a piece of geometry
/// is free is a fact about the constraints on it; and deleting an entity has to take its
/// constraints with it, which is the operation most easily got wrong when the two are separate
/// collections a caller is trusted to keep in step.
/// </para>
/// <para>
/// Immutable, like the document. A solve produces a new sketch rather than moving points in this
/// one, so a drag that is superseded can be discarded by dropping a reference, and the sketch the
/// UI is drawing cannot change underneath it.
/// </para>
/// </remarks>
public sealed record Sketch(SketchEntitySet Entities, ConstraintSet Constraints)
{
    /// <summary>Gets a sketch with nothing in it.</summary>
    public static Sketch Empty { get; } = new(SketchEntitySet.Empty, ConstraintSet.Empty);

    /// <summary>Gets how many degrees of freedom the geometry has before constraints.</summary>
    /// <remarks>
    /// The sum over the entities of how many numbers it takes to place one: two for a point, four
    /// for a line, three for a circle. Construction geometry counts, because it is solved like
    /// everything else — that is what it is for.
    /// </remarks>
    public int Freedom => Entities.Sum(FreedomOf);

    /// <summary>Gets how many degrees of freedom are left once the constraints are counted.</summary>
    /// <remarks>
    /// An upper bound on what is actually free, not a diagnosis. Two constraints saying the same
    /// thing both subtract here and only one of them removes anything, so a redundant sketch
    /// reports fewer remaining degrees of freedom than it has. Telling redundancy from
    /// over-constraint needs the rank of the Jacobian, which is P4-T06's job; this is the number
    /// that analysis starts from.
    /// </remarks>
    public int RemainingFreedom => Freedom - Constraints.Removes;

    /// <summary>Adds or replaces an entity.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>The new sketch.</returns>
    public Sketch With(SketchEntity entity) => this with { Entities = Entities.With(entity) };

    /// <summary>Adds or replaces a constraint.</summary>
    /// <param name="constraint">The constraint.</param>
    /// <returns>The new sketch.</returns>
    public Sketch With(SketchConstraint constraint)
        => this with { Constraints = Constraints.With(constraint) };

    /// <summary>Removes an entity, and every constraint that named it.</summary>
    /// <param name="id">Which entity.</param>
    /// <returns>The new sketch.</returns>
    /// <remarks>
    /// The constraints go with it. A constraint left pointing at deleted geometry is not one the
    /// solver can decline to apply; it names a coordinate nobody will write, and the failure
    /// surfaces as a solve that does not converge for no reason the user can see.
    /// </remarks>
    public Sketch Without(SketchEntityId id) => new(
        Entities.Without(id), Constraints.WithoutThoseNaming(id));

    /// <summary>Removes a constraint.</summary>
    /// <param name="id">Which constraint.</param>
    /// <returns>The new sketch.</returns>
    public Sketch Without(SketchConstraintId id)
        => this with { Constraints = Constraints.Without(id) };

    /// <summary>Gets everything wrong with this sketch that can be found without solving it.</summary>
    /// <returns>The complaints, empty if there are none.</returns>
    public ImmutableArray<string> Problems =>
    [
        .. Entities.Degeneracies,
        .. Constraints.Validate(Entities).Select(v => v.Message),
    ];

    /// <inheritdoc/>
    public bool Equals(Sketch? other)
        => other is not null
            && Entities.Ordered.SequenceEqual(other.Entities.Ordered)
            && Constraints.Ordered.SequenceEqual(other.Constraints.Ordered);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Entities.Count, Constraints.Count);

    /// <inheritdoc/>
    public override string ToString()
        => $"{Entities.Count} entities, {Constraints.Count} constraints, "
            + $"{RemainingFreedom} degrees of freedom remaining";

    /// <summary>How many numbers it takes to place one entity.</summary>
    /// <remarks>
    /// Asked of <see cref="SketchParameters"/> rather than written out here. The two are the same
    /// table, and a copy would agree on the day it was written and drift the first time an entity
    /// kind gained a parameter — leaving the degree-of-freedom count a user is shown disagreeing
    /// with the vector a solver actually works on.
    /// </remarks>
    private static int FreedomOf(SketchEntity entity) => SketchParameters.WidthOf(entity);
}
