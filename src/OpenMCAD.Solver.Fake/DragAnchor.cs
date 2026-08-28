using System.Collections.Immutable;

using OpenMCAD.Solver.Sketching;

namespace OpenMCAD.Solver.Fake;

/// <summary>
/// Where a drag would rather everything stayed.
/// </summary>
/// <param name="Towards">
/// One value per parameter: where the pointer is for the point being dragged, and where it already
/// was for everything else.
/// </param>
/// <param name="Before">Where everything was when the drag began, the dragged point included.</param>
/// <param name="Held">The parameters of the dragged point.</param>
/// <remarks>
/// <para>
/// P4-T07. The minimal-motion objective, as data. A drag on an under-constrained sketch has
/// infinitely many correct answers and the constraints do not care which is chosen, so something
/// else has to — otherwise a user moving one corner watches the opposite side of the drawing swing
/// about, which is correct and unusable.
/// </para>
/// <para>
/// Measured from where the geometry was when the drag <em>began</em>, not from the previous frame.
/// Anchoring each frame to the last would let a slow drag creep: every frame's small compromise
/// becomes the next frame's baseline, and geometry the user never touched drifts across the plane
/// over a few hundred milliseconds.
/// </para>
/// </remarks>
internal sealed record DragAnchor(
    ImmutableArray<double> Towards,
    ImmutableArray<double> Before,
    ImmutableArray<int> Held)
{
    /// <summary>Builds the anchor for a drag.</summary>
    /// <param name="before">The sketch as it was when the drag began.</param>
    /// <param name="layout">How that sketch lays out as a vector.</param>
    /// <param name="drag">What is being dragged, and where to.</param>
    /// <returns>The anchor, or null if the dragged point is not a parameter of its entity.</returns>
    /// <remarks>
    /// Null rather than an anchor with nothing held, when the drag names a point that is computed
    /// rather than stored — an arc's midpoint, say. There is nothing to pull towards a pointer, so
    /// the two settle passes would ask every parameter to stay exactly where it already is and
    /// arrive at the same answer after doing the work. Saying so up front is cheaper and clearer;
    /// no test can tell the two apart, because they agree.
    /// </remarks>
    public static DragAnchor? For(Sketch before, SketchParameters layout, DragTarget drag)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(drag);

        if (layout.IndexOf(drag.Point) is not { } at)
        {
            return null;
        }

        double[] towards = [.. layout.Values];

        towards[at.X] = drag.To.X;
        towards[at.Y] = drag.To.Y;

        return new DragAnchor([.. towards], layout.Values, [at.X, at.Y]);
    }

    /// <inheritdoc/>
    public bool Equals(DragAnchor? other)
        => other is not null
            && Towards.SequenceEqual(other.Towards)
            && Before.SequenceEqual(other.Before)
            && Held.SequenceEqual(other.Held);

    /// <inheritdoc/>
    public override int GetHashCode()
        => HashCode.Combine(Towards.Length, Before.Length, Held.Length);
}
