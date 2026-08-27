namespace OpenMCAD.Core.Rebuild;

/// <summary>
/// Remembers what a feature produced, so that an identical situation can skip the kernel.
/// </summary>
/// <remarks>
/// This is what makes undo cheap and dragging the rollback bar feel instant (§5.4): both of those
/// return the document to a state it has been in before, so every key is one that has already been
/// computed and every feature hits.
/// </remarks>
public interface IGeometryCache
{
    /// <summary>Gets how many results are held.</summary>
    int Count { get; }

    /// <summary>Raised when an entry is dropped, so its geometry can be released.</summary>
    /// <remarks>
    /// A cached <see cref="FeatureOutput"/> holds <see cref="Kernel.KernelShape"/> handles, which
    /// name shapes living inside the kernel. Dropping the entry without telling anyone leaks those
    /// shapes for the life of the process. The cache does not release them itself because it does
    /// not own them — shape lifetime belongs to the kernel's handle table — so it says what it has
    /// dropped and leaves the releasing to whoever does own it.
    /// </remarks>
    event Action<FeatureOutput>? Evicted;

    /// <summary>Looks for a result.</summary>
    /// <param name="key">What the result depends on.</param>
    /// <param name="output">The result, if it is held.</param>
    /// <returns>Whether it was found.</returns>
    bool TryGet(RebuildKey key, out FeatureOutput output);

    /// <summary>Records a result.</summary>
    /// <param name="key">What the result depends on.</param>
    /// <param name="output">The result.</param>
    void Store(RebuildKey key, FeatureOutput output);

    /// <summary>Drops everything.</summary>
    void Clear();
}

/// <summary>
/// A geometry cache that remembers nothing.
/// </summary>
/// <remarks>
/// <para>
/// What <c>--no-cache</c> is (P3-T05). Phase 3's fifth exit criterion is that opening a fixture
/// with caches disabled produces identical results to opening it with them, which is the check that
/// the cache is genuinely transparent rather than merely usually right. A mode that is a different
/// implementation of the cache would not be evidence about anything; a mode that is the same engine
/// with a cache that never hits is.
/// </para>
/// <para>
/// Also the honest default for a batch tool. A converter that opens ten thousand files gains
/// nothing from remembering the first one and would rather have the memory.
/// </para>
/// </remarks>
public sealed class NullGeometryCache : IGeometryCache
{
    /// <summary>Gets the shared instance. It has no state to keep apart.</summary>
    public static NullGeometryCache Instance { get; } = new();

    /// <inheritdoc />
    public int Count => 0;

    /// <inheritdoc />
    /// <remarks>Never raised: nothing is ever held, so nothing is ever dropped.</remarks>
    public event Action<FeatureOutput>? Evicted
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public bool TryGet(RebuildKey key, out FeatureOutput output)
    {
        output = FeatureOutput.None;
        return false;
    }

    /// <inheritdoc />
    public void Store(RebuildKey key, FeatureOutput output)
    {
    }

    /// <inheritdoc />
    public void Clear()
    {
    }
}

/// <summary>
/// A geometry cache holding a bounded number of results, dropping the least recently used.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bounded by count rather than by memory.</b> The obvious policy is a memory budget, and it is
/// not available: what an entry costs is dominated by the shapes it names, which live inside the
/// kernel and whose size this side cannot see. A count is a blunt proxy, but it is a proxy over a
/// real number rather than over an invented one, and it can be tuned by whoever can measure the
/// result.
/// </para>
/// <para>
/// <b>Least recently used, not least recently written.</b> The access pattern this exists for is
/// undo and rollback-bar scrubbing, which return repeatedly to the same handful of states. Ordering
/// by when an entry was written would evict exactly the entries being returned to.
/// </para>
/// </remarks>
public sealed class GeometryCache : IGeometryCache
{
    /// <summary>The number of results held when no other capacity is given.</summary>
    public const int DefaultCapacity = 512;

    private readonly Dictionary<RebuildKey, LinkedListNode<Entry>> _entries;
    private readonly LinkedList<Entry> _order = new();
    private readonly Lock _gate = new();
    private readonly int _capacity;

    /// <summary>Creates the cache.</summary>
    /// <param name="capacity">How many results to hold.</param>
    public GeometryCache(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _capacity = capacity;
        _entries = new Dictionary<RebuildKey, LinkedListNode<Entry>>(capacity);
    }

    /// <inheritdoc />
    public event Action<FeatureOutput>? Evicted;

    /// <inheritdoc />
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Gets how many lookups have found what they wanted.</summary>
    /// <remarks>
    /// Phase 3's second exit criterion is that a parameter change rebuilds only the dirty subgraph,
    /// "verified by instrumentation". This and <see cref="Misses"/> are that instrumentation.
    /// </remarks>
    public long Hits { get; private set; }

    /// <summary>Gets how many lookups have found nothing.</summary>
    public long Misses { get; private set; }

    /// <inheritdoc />
    public bool TryGet(RebuildKey key, out FeatureOutput output)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                // Moved to the front on every read, which is what makes this least *recently used*
                // rather than least recently written.
                _order.Remove(node);
                _order.AddFirst(node);

                Hits++;
                output = node.Value.Output;

                return true;
            }
        }

        Misses++;
        output = FeatureOutput.None;

        return false;
    }

    /// <inheritdoc />
    public void Store(RebuildKey key, FeatureOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        List<FeatureOutput> dropped = [];

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out LinkedListNode<Entry>? existing))
            {
                // Replacing an entry drops what was there, and that still has to be announced --
                // the shapes it named are no longer reachable from here either.
                dropped.Add(existing.Value.Output);

                _order.Remove(existing);
                _entries.Remove(key);
            }

            LinkedListNode<Entry> node = _order.AddFirst(new Entry(key, output));
            _entries[key] = node;

            while (_entries.Count > _capacity)
            {
                LinkedListNode<Entry> last = _order.Last!;

                _order.RemoveLast();
                _entries.Remove(last.Value.Key);

                dropped.Add(last.Value.Output);
            }
        }

        // Outside the lock. A handler releases kernel shapes, which is a call onto the kernel
        // thread, and holding a cache lock across that would let a slow release block every lookup.
        Announce(dropped);
    }

    /// <inheritdoc />
    public void Clear()
    {
        List<FeatureOutput> dropped = [];

        lock (_gate)
        {
            foreach (Entry entry in _order)
            {
                dropped.Add(entry.Output);
            }

            _order.Clear();
            _entries.Clear();
        }

        Announce(dropped);
    }

    private void Announce(List<FeatureOutput> dropped)
    {
        foreach (FeatureOutput output in dropped)
        {
            Evicted?.Invoke(output);
        }
    }

    private readonly record struct Entry(RebuildKey Key, FeatureOutput Output);
}
