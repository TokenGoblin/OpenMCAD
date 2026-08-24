using FluentAssertions;

using OpenMCAD.Kernel;
using OpenMCAD.Math;
using OpenMCAD.Render.Direct3D12;

using Xunit;

namespace OpenMCAD.Render.Tests;

/// <summary>
/// Resolving a square of the ID buffer into the entity the user meant (P2-T07).
/// </summary>
/// <remarks>
/// These exercise the resolution rules against hand-built samples rather than rendered ones. The
/// GPU half is covered by <see cref="IdPassTests"/>; the rules are where the judgement lives, and
/// every one of them produces a plausible answer when wrong — the wrong entity selected is
/// indistinguishable from a misclick unless the behaviour is pinned down.
/// </remarks>
public sealed class PickTests
{
    private static DisplayId FaceId => new(10);

    private static DisplayId EdgeId => new(20);

    private static SubEntity Face => new(new KernelShape(1), 1, SubEntityKind.Face);

    private static SubEntity Edge => new(new KernelShape(1), 2, SubEntityKind.Edge);

    /// <summary>A snapshot that knows about one face and one edge.</summary>
    private static DisplaySnapshot Snapshot(long version = 1)
    {
        System.Collections.Immutable.ImmutableDictionary<DisplayId, SubEntity> entities =
            System.Collections.Immutable.ImmutableDictionary<DisplayId, SubEntity>.Empty
                .Add(FaceId, Face)
                .Add(EdgeId, Edge);

        return new DisplaySnapshot(version, Vec3d.Zero, [], entities, default);
    }

    /// <summary>A square sample, filled with one id, optionally with another painted at a point.</summary>
    private static PickSample Sample(
        int size,
        DisplayId fill,
        long version = 1,
        DisplayId? spot = null,
        int spotX = 0,
        int spotY = 0)
    {
        uint[] ids = new uint[size * size];
        Array.Fill(ids, fill.Value);

        int centre = size / 2;

        if (spot is { } painted)
        {
            ids[((centre + spotY) * size) + centre + spotX] = painted.Value;
        }

        return new PickSample(new PickRequest(100, 100, version), ids, size, size, centre, centre);
    }

    [Fact]
    public void TheFaceUnderTheCursorIsPicked()
    {
        PickHit hit = PickResolver.Resolve(Sample(9, FaceId), Snapshot());

        hit.IsSomething.Should().BeTrue();
        hit.Id.Should().Be(FaceId);
        hit.Entity.Should().Be(Face);
        hit.DistancePixels.Should().Be(0);
    }

    [Fact]
    public void EmptySpacePicksNothing()
    {
        PickHit hit = PickResolver.Resolve(Sample(9, DisplayId.None), Snapshot());

        hit.IsSomething.Should().BeFalse();
        hit.Entity.Should().Be(SubEntity.None);
    }

    [Fact]
    public void AnEdgeNearTheCursorBeatsTheFaceUnderIt()
    {
        // The point of the whole exercise. An edge is a pixel and a half wide and nobody can put a
        // mouse on it; the user who wanted the face has the rest of the face to click on.
        PickSample sample = Sample(9, FaceId, spot: EdgeId, spotX: 2, spotY: 0);

        PickHit hit = PickResolver.Resolve(sample, Snapshot());

        hit.Id.Should().Be(EdgeId);
        hit.Entity.Kind.Should().Be(SubEntityKind.Edge);
        hit.DistancePixels.Should().Be(2);
    }

    [Fact]
    public void AnEdgeBeyondTheBiasDoesNotStealTheClick()
    {
        // Otherwise the middle of a face in a dense wireframe would be unselectable.
        PickSample sample = Sample(21, FaceId, spot: EdgeId, spotX: 9, spotY: 0);

        PickHit hit = PickResolver.Resolve(sample, Snapshot(), edgeBiasPixels: 4);

        hit.Id.Should().Be(FaceId, "the edge is nine pixels away and the bias is four");
    }

    [Fact]
    public void TheNearestOfTwoEdgesWins()
    {
        uint[] ids = new uint[9 * 9];
        DisplayId farEdge = new(21);

        // Two edges, at three pixels and one pixel.
        ids[(4 * 9) + 7] = EdgeId.Value;
        ids[(4 * 9) + 5] = farEdge.Value;

        PickSample sample = new(new PickRequest(0, 0, 1), ids, 9, 9, 4, 4);

        System.Collections.Immutable.ImmutableDictionary<DisplayId, SubEntity> entities =
            System.Collections.Immutable.ImmutableDictionary<DisplayId, SubEntity>.Empty
                .Add(EdgeId, Edge)
                .Add(farEdge, new SubEntity(new KernelShape(1), 3, SubEntityKind.Edge));

        DisplaySnapshot snapshot = new(1, Vec3d.Zero, [], entities, default);

        PickResolver.Resolve(sample, snapshot).Id.Should().Be(
            farEdge, "it is one pixel away and the other is three");
    }

    [Fact]
    public void AFaceJustOffTheCursorIsStillPicked()
    {
        // Clicking a pixel outside a thin part should select it rather than nothing; a user cannot
        // see which side of the boundary their cursor landed on.
        PickSample sample = Sample(9, DisplayId.None, spot: FaceId, spotX: 2, spotY: 1);

        PickHit hit = PickResolver.Resolve(sample, Snapshot());

        hit.Id.Should().Be(FaceId);
    }

    [Fact]
    public void AFaceWellAwayFromTheCursorIsNotPicked()
    {
        // Clicking clear space must deselect, not grab whatever happens to be in the window.
        PickSample sample = Sample(21, DisplayId.None, spot: FaceId, spotX: 9, spotY: 9);

        PickResolver.Resolve(sample, Snapshot()).IsSomething.Should().BeFalse();
    }

    [Fact]
    public void APickAgainstAnOlderSnapshotResolvesToNothing()
    {
        // Readback is deliberately several frames behind, so a pick landing after a rebuild is
        // routine. Ids are snapshot-scoped: the same number names a different entity in the next
        // snapshot, so answering from a stale one selects something nobody pointed at.
        PickSample sample = Sample(9, FaceId, version: 1);

        PickHit hit = PickResolver.Resolve(sample, Snapshot(version: 2));

        hit.IsSomething.Should().BeFalse();
    }

    [Fact]
    public void AnIdTheSnapshotDoesNotKnowResolvesToNothingRatherThanThrowing()
    {
        PickSample sample = Sample(9, new DisplayId(999));

        PickHit hit = PickResolver.Resolve(sample, Snapshot());

        // The id was found, but it names nothing -- so there is nothing to select.
        hit.Entity.Should().Be(SubEntity.None);
    }

    [Fact]
    public void AnEmptySampleIsHandled()
    {
        PickSample empty = new(new PickRequest(0, 0, 1), [], 0, 0, 0, 0);

        PickResolver.Resolve(empty, Snapshot()).IsSomething.Should().BeFalse();
    }

    [Fact]
    public void ASampleClippedByTheViewportEdgeKeepsItsCentre()
    {
        // A pick near the corner reads a smaller window, and the requested pixel is no longer in
        // the middle of it. If the centre were assumed rather than carried, every distance in the
        // proximity search would be measured from the wrong place.
        uint[] ids = new uint[5 * 5];
        ids[(1 * 5) + 1] = EdgeId.Value;

        PickSample sample = new(new PickRequest(1, 1, 1), ids, 5, 5, 1, 1);

        PickHit hit = PickResolver.Resolve(sample, Snapshot());

        hit.Id.Should().Be(EdgeId);
        hit.DistancePixels.Should().Be(0);
    }

    [Fact]
    public void SampleIndexingIsRowMajorAboutTheCentre()
    {
        uint[] ids = new uint[3 * 3];
        ids[(0 * 3) + 2] = 7;

        PickSample sample = new(new PickRequest(0, 0, 1), ids, 3, 3, 1, 1);

        sample.At(1, -1).Value.Should().Be(7);
        sample.At(0, 0).Should().Be(DisplayId.None);
        sample.At(99, 99).Should().Be(DisplayId.None, "outside the window is nothing, not a throw");
    }
}
