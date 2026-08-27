using System.Collections.Immutable;

using OpenMCAD.Core.Documents;
using OpenMCAD.Kernel;

namespace OpenMCAD.Core.Naming;

/// <summary>
/// What every feature in a rebuild did to its inputs, in the order they were evaluated.
/// </summary>
/// <remarks>
/// <para>
/// The raw material tier one resolves against. A <see cref="HistoryMap"/> answers "what did this
/// one operation do", and a reference usually has to cross several operations to get from where it
/// was recorded to where it is being used — so what resolution needs is the maps together, in
/// order, which is what this holds.
/// </para>
/// <para>
/// Order is not a convenience here. Carrying an entity forward means applying each operation's map
/// in the sequence they actually ran; applying them in any other order would follow a chain of
/// correspondences that never happened.
/// </para>
/// </remarks>
public sealed class RebuildHistory
{
    private readonly ImmutableDictionary<FeatureId, HistoryMap> _maps;
    private readonly ImmutableDictionary<FeatureId, int> _positions;

    private RebuildHistory(
        ImmutableArray<FeatureId> order,
        ImmutableDictionary<FeatureId, HistoryMap> maps,
        ImmutableDictionary<FeatureId, int> positions)
    {
        Order = order;
        _maps = maps;
        _positions = positions;
    }

    /// <summary>Gets a history of nothing.</summary>
    public static RebuildHistory Empty { get; } = new(
        [],
        ImmutableDictionary<FeatureId, HistoryMap>.Empty,
        ImmutableDictionary<FeatureId, int>.Empty);

    /// <summary>Gets the features, in the order they were evaluated.</summary>
    public ImmutableArray<FeatureId> Order { get; }

    /// <summary>Gets what one feature did.</summary>
    /// <param name="feature">Which feature.</param>
    /// <returns>Its map, or null if it was not evaluated in this rebuild.</returns>
    /// <remarks>
    /// Null and <see cref="HistoryMap.Empty"/> mean different things and are worth keeping apart.
    /// An empty map is a feature that ran and touched nothing; null is a feature that did not run,
    /// which means a reference through it cannot be resolved rather than resolving to nothing.
    /// </remarks>
    public HistoryMap? For(FeatureId feature)
        => _maps.TryGetValue(feature, out HistoryMap? map) ? map : null;

    /// <summary>Gets where a feature came in the evaluation order.</summary>
    /// <param name="feature">Which feature.</param>
    /// <returns>Its position, or -1 if it was not evaluated.</returns>
    public int PositionOf(FeatureId feature)
        => _positions.TryGetValue(feature, out int position) ? position : -1;

    /// <summary>Collects the maps of a rebuild.</summary>
    public sealed class Builder
    {
        private readonly ImmutableArray<FeatureId>.Builder _order =
            ImmutableArray.CreateBuilder<FeatureId>();

        private readonly ImmutableDictionary<FeatureId, HistoryMap>.Builder _maps =
            ImmutableDictionary.CreateBuilder<FeatureId, HistoryMap>();

        /// <summary>Records what a feature did.</summary>
        /// <param name="feature">Which feature.</param>
        /// <param name="map">What it did.</param>
        /// <remarks>
        /// A feature evaluated twice in one rebuild keeps its later position, because that is the
        /// state the rest of the rebuild saw.
        /// </remarks>
        public void Add(FeatureId feature, HistoryMap map)
        {
            ArgumentNullException.ThrowIfNull(map);

            if (_maps.ContainsKey(feature))
            {
                _order.Remove(feature);
            }

            _order.Add(feature);
            _maps[feature] = map;
        }

        /// <summary>Produces the history.</summary>
        /// <returns>The history.</returns>
        public RebuildHistory Build()
        {
            ImmutableArray<FeatureId> order = _order.ToImmutable();

            ImmutableDictionary<FeatureId, int>.Builder positions =
                ImmutableDictionary.CreateBuilder<FeatureId, int>();

            for (int i = 0; i < order.Length; ++i)
            {
                positions[order[i]] = i;
            }

            return new RebuildHistory(order, _maps.ToImmutable(), positions.ToImmutable());
        }
    }
}
