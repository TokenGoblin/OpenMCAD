namespace OpenMCAD.Render;

/// <summary>
/// The single point of contact between whatever produces snapshots and whatever draws them.
/// </summary>
/// <remarks>
/// <para>
/// PLAN.md 4.2: "There is no lock between rebuild and render — only a reference swap." This is
/// that swap, in one place, so the claim is something the codebase enforces rather than something
/// it hopes for. A reviewer who wants to check that the render path never blocks on the rebuild
/// path has one type to read.
/// </para>
/// <para>
/// Correctness rests on two things. A reference assignment is atomic on every runtime .NET
/// supports, so a reader can never observe a torn reference. And a
/// <see cref="DisplaySnapshot"/> is deeply immutable, so the object a reader gets cannot change
/// underneath it while the producer builds the next one. Take either away and this becomes a data
/// race rather than a design.
/// </para>
/// <para>
/// Nothing here waits. <see cref="Publish"/> never blocks the producer and
/// <see cref="Current"/> never blocks the renderer, which is the property that keeps the viewport
/// at frame rate through a ten-second rebuild.
/// </para>
/// </remarks>
public sealed class SnapshotHolder
{
    private DisplaySnapshot _current = DisplaySnapshot.Empty;

    /// <summary>Raised after a newer snapshot is published, on the publishing thread.</summary>
    /// <remarks>
    /// For waking an idle renderer, not for doing work. A handler runs on the rebuild thread and
    /// delays the next rebuild for as long as it takes, so it should do no more than signal.
    /// </remarks>
    public event Action<DisplaySnapshot>? Published;

    /// <summary>Gets the newest published snapshot.</summary>
    /// <remarks>
    /// <para>
    /// A volatile read, which on its own is nearly free. The renderer reads this once at frame
    /// start and uses the result for the whole frame: reading it again mid-frame could pick up a
    /// newer snapshot and draw half of one scene and half of another.
    /// </para>
    /// <para>
    /// Never null. The holder starts at <see cref="DisplaySnapshot.Empty"/> so the first frame
    /// needs no special case.
    /// </para>
    /// </remarks>
    public DisplaySnapshot Current => Volatile.Read(ref _current);

    /// <summary>
    /// Publishes a snapshot, if it is newer than the one already held.
    /// </summary>
    /// <param name="snapshot">The snapshot to publish.</param>
    /// <returns>
    /// <see langword="true"/> if it was published; <see langword="false"/> if a newer or equal
    /// version was already held and this one was discarded.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// The version check is the point of the compare-and-swap loop, and it is not defensive
    /// programming. Rebuilds are allowed to run concurrently where the feature graph permits
    /// (PLAN.md 4.2), so two of them can finish out of order — and a plain assignment would let
    /// the slower, older one win and leave the viewport showing a scene that has been superseded,
    /// with nothing to correct it until the next edit.
    /// </para>
    /// <para>
    /// Equal versions are rejected as well as older ones. A version is meant to identify a
    /// snapshot, so two snapshots sharing one is a bug in the producer; taking the first and
    /// discarding the second at least makes the behaviour deterministic rather than a race.
    /// </para>
    /// </remarks>
    public bool Publish(DisplaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        while (true)
        {
            DisplaySnapshot current = Volatile.Read(ref _current);
            if (snapshot.Version <= current.Version)
            {
                return false;
            }

            if (ReferenceEquals(Interlocked.CompareExchange(ref _current, snapshot, current), current))
            {
                Published?.Invoke(snapshot);
                return true;
            }

            // Another producer got in first. Loop rather than give up: the snapshot may still be
            // newer than whatever landed, and if it is not, the version check above will say so.
        }
    }

    /// <summary>Resets to the empty scene, for closing a document.</summary>
    /// <remarks>
    /// Deliberately not a <see cref="Publish"/>: it moves the version backwards to zero, which
    /// <see cref="Publish"/> exists to prevent. Closing a document is the one time that is the
    /// intent rather than a mistake, and the next document starts numbering again.
    /// </remarks>
    public void Clear() => Volatile.Write(ref _current, DisplaySnapshot.Empty);
}
