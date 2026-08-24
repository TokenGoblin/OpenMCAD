using OpenMCAD.Kernel;
using OpenMCAD.Render.Direct3D12;

namespace OpenMCAD.Render;

/// <summary>What a pick landed on.</summary>
/// <param name="Id">The display id, or <see cref="DisplayId.None"/> for empty space.</param>
/// <param name="Entity">What that id names in the snapshot.</param>
/// <param name="DistancePixels">How far from the cursor it was found.</param>
/// <param name="Request">The request this answers.</param>
public readonly record struct PickHit(
    DisplayId Id,
    SubEntity Entity,
    double DistancePixels,
    PickRequest Request)
{
    /// <summary>Gets a miss.</summary>
    /// <param name="request">The request that found nothing.</param>
    /// <returns>A hit on nothing.</returns>
    public static PickHit Nothing(PickRequest request)
        => new(DisplayId.None, SubEntity.None, double.PositiveInfinity, request);

    /// <summary>Gets whether anything was found.</summary>
    public bool IsSomething => Id.IsSomething;
}

/// <summary>
/// Turns a square of the ID buffer into the one entity the user meant (P2-T07).
/// </summary>
/// <remarks>
/// <para>
/// <b>The pixel under the cursor is the wrong answer.</b> An edge is a pixel and a half wide, and
/// nobody can put a mouse on it reliably; a picker that reads one pixel makes edge selection feel
/// broken while being, strictly, correct. So the resolver searches outward and applies a
/// preference: a nearby edge beats the face behind it, because the user who wanted the face has
/// the whole rest of it to click on, while the user who wanted the edge has a pixel and a half.
/// </para>
/// <para>
/// The bias is a radius, not a rule. Beyond <see cref="DefaultEdgeBiasPixels"/> an edge no longer
/// steals the click, so clicking the middle of a face selects the face even in a dense wireframe.
/// </para>
/// <para>
/// <b>Vertices are not resolved yet.</b> They rank above edges when they arrive, on the same
/// reasoning. What is missing is upstream: the kernel's mesh reports faces and edges as entities
/// but not vertices, so there is nothing to give an id to and nothing to draw into the ID buffer.
/// </para>
/// </remarks>
public static class PickResolver
{
    /// <summary>How near an edge must be, in pixels, to win against a face.</summary>
    /// <remarks>
    /// Four pixels at 100% scale. Wide enough that an edge is comfortable to hit, narrow enough
    /// that a face two edges wide is still selectable in the middle.
    /// </remarks>
    public const int DefaultEdgeBiasPixels = 4;

    /// <summary>
    /// Resolves a sample against the snapshot it was rendered from.
    /// </summary>
    /// <param name="sample">The ids read back.</param>
    /// <param name="snapshot">The snapshot to resolve against.</param>
    /// <param name="edgeBiasPixels">How near an edge must be to beat a face.</param>
    /// <returns>What was picked.</returns>
    /// <remarks>
    /// A sample whose <see cref="PickRequest.SnapshotVersion"/> does not match resolves to nothing
    /// rather than being interpreted against the wrong scene. Readback is deliberately several
    /// frames behind, so a pick landing after a rebuild is normal rather than exceptional — and an
    /// id from the previous snapshot names a different entity in this one, so answering from it
    /// would select something the user never pointed at.
    /// </remarks>
    public static PickHit Resolve(
        PickSample sample,
        DisplaySnapshot snapshot,
        int edgeBiasPixels = DefaultEdgeBiasPixels)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (sample.Ids is null || sample.Width <= 0 || sample.Height <= 0)
        {
            return PickHit.Nothing(sample.Request);
        }

        if (sample.Request.SnapshotVersion != snapshot.Version)
        {
            return PickHit.Nothing(sample.Request);
        }

        DisplayId nearestEdge = DisplayId.None;
        double nearestEdgeDistance = double.PositiveInfinity;

        DisplayId nearestFace = DisplayId.None;
        double nearestFaceDistance = double.PositiveInfinity;

        double biasSquared = (double)edgeBiasPixels * edgeBiasPixels;

        for (int y = 0; y < sample.Height; ++y)
        {
            for (int x = 0; x < sample.Width; ++x)
            {
                DisplayId id = new(sample.Ids[(y * sample.Width) + x]);

                if (!id.IsSomething)
                {
                    continue;
                }

                int dx = x - sample.CentreX;
                int dy = y - sample.CentreY;
                double distanceSquared = (double)(dx * dx) + (dy * dy);

                SubEntity entity = snapshot.Resolve(id);

                if (entity.Kind == SubEntityKind.Edge)
                {
                    if (distanceSquared <= biasSquared && distanceSquared < nearestEdgeDistance)
                    {
                        nearestEdge = id;
                        nearestEdgeDistance = distanceSquared;
                    }
                }
                else if (distanceSquared < nearestFaceDistance)
                {
                    nearestFace = id;
                    nearestFaceDistance = distanceSquared;
                }
            }
        }

        if (nearestEdge.IsSomething)
        {
            return new PickHit(
                nearestEdge,
                snapshot.Resolve(nearestEdge),
                System.Math.Sqrt(nearestEdgeDistance),
                sample.Request);
        }

        // A face found off-centre still counts. The cursor may be a pixel outside a thin body, and
        // refusing the click there would make small parts unselectable for no reason the user can
        // see -- but only within the same radius, so clicking clear space selects nothing.
        if (nearestFace.IsSomething && nearestFaceDistance <= biasSquared)
        {
            return new PickHit(
                nearestFace,
                snapshot.Resolve(nearestFace),
                System.Math.Sqrt(nearestFaceDistance),
                sample.Request);
        }

        return PickHit.Nothing(sample.Request);
    }
}
