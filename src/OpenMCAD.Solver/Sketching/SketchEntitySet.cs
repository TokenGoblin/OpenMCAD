using System.Collections;
using System.Collections.Immutable;

using OpenMCAD.Math;

namespace OpenMCAD.Solver.Sketching;

/// <summary>
/// The geometry of one sketch.
/// </summary>
/// <remarks>
/// <para>
/// P4-T03. Immutable and ordered. The order is the order the user drew things in, and it is kept
/// for the same reason the feature tree's is (P3-T03): a solver reads the entities in some order,
/// and if that order came from a dictionary the same sketch would converge differently on two
/// machines. ADR-0011 makes that unacceptable.
/// </para>
/// <para>
/// Lookup by id is a dictionary alongside the list rather than a search, because constraint
/// resolution asks the question once per constraint per solve and a 200-entity sketch is expected
/// to solve in under 16 ms.
/// </para>
/// </remarks>
public sealed class SketchEntitySet : IReadOnlyCollection<SketchEntity>
{
    private readonly ImmutableArray<SketchEntity> _ordered;
    private readonly ImmutableDictionary<SketchEntityId, SketchEntity> _byId;

    private SketchEntitySet(
        ImmutableArray<SketchEntity> ordered,
        ImmutableDictionary<SketchEntityId, SketchEntity> byId)
    {
        _ordered = ordered;
        _byId = byId;
    }

    /// <summary>Gets a sketch with no geometry in it.</summary>
    public static SketchEntitySet Empty { get; } =
        new([], ImmutableDictionary<SketchEntityId, SketchEntity>.Empty);

    /// <summary>Gets how many entities there are.</summary>
    public int Count => _ordered.Length;

    /// <summary>Gets the entities, in the order they were drawn.</summary>
    public ImmutableArray<SketchEntity> Ordered => _ordered;

    /// <summary>Gets the entities that will become profile geometry.</summary>
    public IEnumerable<SketchEntity> Profile => _ordered.Where(e => !e.IsConstruction);

    /// <summary>Gets the entities that are scaffolding.</summary>
    public IEnumerable<SketchEntity> Construction => _ordered.Where(e => e.IsConstruction);

    /// <summary>Builds a set from some entities.</summary>
    /// <param name="entities">The entities, in the order they were drawn.</param>
    /// <returns>The set.</returns>
    /// <exception cref="ArgumentException">Two entities share an id, or one has none.</exception>
    public static SketchEntitySet Of(IEnumerable<SketchEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        SketchEntitySet set = Empty;

        foreach (SketchEntity entity in entities)
        {
            set = set.With(entity);
        }

        return set;
    }

    /// <summary>Adds an entity, or replaces the one with the same id.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>The new set.</returns>
    /// <exception cref="ArgumentException">The entity has no id.</exception>
    /// <remarks>
    /// Replacing keeps the entity's position in the order. A solve moves every point in the sketch
    /// and writes them all back; if that reordered the set, one drag would change the order the next
    /// solve read them in, and the sketch would drift from its own history for no reason a user
    /// could see.
    /// </remarks>
    public SketchEntitySet With(SketchEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (!entity.Id.IsValid)
        {
            throw new ArgumentException(
                $"This {entity.Kind} has no id, so nothing could constrain it.", nameof(entity));
        }

        int at = IndexOf(entity.Id);

        return new SketchEntitySet(
            at < 0 ? _ordered.Add(entity) : _ordered.SetItem(at, entity),
            _byId.SetItem(entity.Id, entity));
    }

    /// <summary>Removes an entity.</summary>
    /// <param name="id">Which one.</param>
    /// <returns>The new set, or this one if it was not there.</returns>
    public SketchEntitySet Without(SketchEntityId id)
    {
        int at = IndexOf(id);

        return at < 0
            ? this
            : new SketchEntitySet(_ordered.RemoveAt(at), _byId.Remove(id));
    }

    /// <summary>Finds an entity.</summary>
    /// <param name="id">Which one.</param>
    /// <returns>The entity, or <see langword="null"/> if this sketch has no such entity.</returns>
    public SketchEntity? Find(SketchEntityId id)
        => _byId.TryGetValue(id, out SketchEntity? entity) ? entity : null;

    /// <summary>Finds where a point reference points.</summary>
    /// <param name="reference">The reference.</param>
    /// <returns>
    /// The position, or <see langword="null"/> if the entity is not here or has no such point.
    /// </returns>
    /// <remarks>
    /// The single place a reference becomes a coordinate. Constraint evaluation, inference,
    /// snapping and the drag objective all ask this question, and four answers to it would be four
    /// chances to disagree about where the middle of an arc is.
    /// </remarks>
    public Vec2d? Locate(SketchPointRef reference)
        => Find(reference.Entity)?.PointOf(reference.Point);

    /// <summary>Gets what is wrong with this sketch's geometry, empty if nothing is.</summary>
    /// <remarks>
    /// Checked before a solve rather than after. A degenerate entity does not make the solver fail
    /// where the problem is; it makes it fail to converge somewhere else, and the message is then
    /// about an iteration count rather than about the circle with no radius.
    /// </remarks>
    public ImmutableArray<string> Degeneracies =>
    [
        .. _ordered
            .Where(e => e.Degeneracy is not null)
            .Select(e => e.Degeneracy!),
    ];

    private int IndexOf(SketchEntityId id)
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

    /// <inheritdoc/>
    public IEnumerator<SketchEntity> GetEnumerator()
        => ((IEnumerable<SketchEntity>)_ordered).GetEnumerator();

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc/>
    public override string ToString()
    {
        int construction = _ordered.Count(e => e.IsConstruction);

        return construction == 0
            ? $"{Count} entities"
            : $"{Count} entities, {construction} of them construction";
    }
}
