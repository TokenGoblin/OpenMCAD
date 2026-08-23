using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Kernel;
using OpenMCAD.Math;
using OpenMCAD.Render;

using Xunit;

namespace OpenMCAD.Render.Tests;

/// <summary>
/// The snapshot contract that PLAN.md 4.2 rests on (P2-T03, P2-T04).
/// </summary>
public sealed class SnapshotTests
{
    /// <summary>A single triangle, one face, at the given corner.</summary>
    private static MeshBuffer Triangle(Vec3d at, SubEntity face)
        => new(
            [at, at + Vec3d.UnitX, at + Vec3d.UnitY],
            [Vec3d.UnitZ, Vec3d.UnitZ, Vec3d.UnitZ],
            [0, 1, 2],
            [0],
            [face]);

    private static SubEntity Face(ulong tag)
        => new(new KernelShape(1), tag, SubEntityKind.Face);

    // --- Identity and resolution --------------------------------------------------------------

    [Fact]
    public void EveryFaceGetsAnIdAndEveryTriangleCarriesItsFaces()
    {
        SnapshotBuilder builder = new();
        builder.Add(Triangle(Vec3d.Zero, Face(10)));

        DisplaySnapshot snapshot = builder.Build(1);

        DisplayId id = snapshot.Bodies[0].Mesh.TriangleIds[0];
        id.IsSomething.Should().BeTrue("a triangle with no id is a triangle that cannot be picked");
        snapshot.Resolve(id).Should().Be(Face(10));
    }

    [Fact]
    public void IdsAreUniqueAcrossBodies()
    {
        // Ids are snapshot-scoped, not body-scoped: the ID buffer holds one number per pixel with
        // no room for a body index, so two bodies reusing an id would make a pick ambiguous.
        SnapshotBuilder builder = new();
        builder.Add(Triangle(Vec3d.Zero, Face(10)));
        builder.Add(Triangle(new Vec3d(5, 0, 0), Face(10)));

        DisplaySnapshot snapshot = builder.Build(1);

        DisplayId first = snapshot.Bodies[0].Mesh.TriangleIds[0];
        DisplayId second = snapshot.Bodies[1].Mesh.TriangleIds[0];

        first.Should().NotBe(second);
        snapshot.Entities.Should().HaveCount(2);
    }

    [Fact]
    public void ZeroResolvesToNothing()
    {
        // The ID buffer is cleared to zero, so every pixel of background reads back as zero. That
        // has to mean "nothing", not "entity zero".
        DisplayId.None.IsSomething.Should().BeFalse();
        DisplaySnapshot.Empty.Resolve(DisplayId.None).Should().Be(SubEntity.None);
    }

    [Fact]
    public void AnIdFromAnotherSnapshotResolvesToNothingRatherThanThrowing()
    {
        // The ID readback is asynchronous (P2-T07), so a pick can arrive after the snapshot it was
        // rendered against has been replaced. A stale pick must select nothing, not crash.
        SnapshotBuilder builder = new();
        builder.Add(Triangle(Vec3d.Zero, Face(10)));

        DisplaySnapshot snapshot = builder.Build(1);

        snapshot.Resolve(new DisplayId(9999)).Should().Be(SubEntity.None);
    }

    [Fact]
    public void AMeshWhoseTrianglesNameAMissingFaceIsRejected()
    {
        MeshBuffer broken = new(
            [Vec3d.Zero, Vec3d.UnitX, Vec3d.UnitY],
            [],
            [0, 1, 2],
            [7],
            [Face(10)]);

        SnapshotBuilder builder = new();

        builder.Invoking(b => b.Add(broken))
            .Should().Throw<ArgumentException>()
            .WithMessage("*names face 7*");
    }

    [Fact]
    public void AMeshWithAFaceAttributionPerTriangleMissingIsRejected()
    {
        MeshBuffer broken = new(
            [Vec3d.Zero, Vec3d.UnitX, Vec3d.UnitY],
            [],
            [0, 1, 2],
            [],
            [Face(10)]);

        SnapshotBuilder builder = new();

        builder.Invoking(b => b.Add(broken))
            .Should().Throw<ArgumentException>()
            .WithMessage("*face attributions*");
    }

    // --- Precision ----------------------------------------------------------------------------

    [Fact]
    public void PositionsFarFromTheWorldOriginKeepMicronAccuracy()
    {
        // The reason DisplaySnapshot.Origin exists. A float carries about seven significant
        // decimal digits, so a point a kilometre out has roughly 0.1 mm of resolution if stored
        // absolutely — coarser than the features being modelled. Measured from a nearby origin it
        // is nanometres.
        Vec3d far = new(1000.0, 2000.0, 3000.0);
        Vec3d feature = far + new Vec3d(0.000_01, 0, 0);

        SnapshotBuilder builder = new();
        builder.Add(new MeshBuffer([far, feature, far + Vec3d.UnitY], [], [0, 1, 2], [0], [Face(10)]));

        DisplaySnapshot snapshot = builder.Build(1);
        ImmutableArray<float> positions = snapshot.Bodies[0].Mesh.Positions;

        double reconstructedX = snapshot.Origin.X + positions[0];
        double reconstructedFeatureX = snapshot.Origin.X + positions[3];

        (reconstructedFeatureX - reconstructedX).Should().BeApproximately(0.000_01, 1e-9);

        // And the naive alternative genuinely fails, so the test above is measuring something.
        float naive = (float)far.X;
        float naiveFeature = (float)feature.X;
        (naiveFeature - naive).Should().NotBeApproximately(0.000_01f, 1e-9f,
            "if absolute floats were good enough here, the relative origin would be pointless");
    }

    [Fact]
    public void ASmallEditDoesNotMoveTheOrigin()
    {
        // Moving the origin invalidates every vertex buffer. Nudging a body by a millimetre must
        // not cost a full re-upload.
        //
        // This body centres on 10.5, exactly on a grid line, which is the case that rounding
        // alone gets wrong: 10.5 rounds to 10 and 10.501 rounds to 11. Carrying the previous
        // origin forward is what actually holds it still.
        SnapshotBuilder before = new();
        before.Add(Triangle(new Vec3d(10.0, 10.0, 10.0), Face(10)));
        DisplaySnapshot first = before.Build(1);

        SnapshotBuilder after = new();
        after.Add(Triangle(new Vec3d(10.001, 10.0, 10.0), Face(10)));

        after.Build(2, first.Origin).Origin.Should().Be(first.Origin);
    }

    [Fact]
    public void TheOriginFollowsTheSceneWhenItGenuinelyMovesAway()
    {
        // Sticky must not mean stuck. A body dragged far from the origin has to get a new one, or
        // the precision the origin exists to protect is lost.
        SnapshotBuilder before = new();
        before.Add(Triangle(Vec3d.Zero, Face(10)));
        DisplaySnapshot first = before.Build(1);

        SnapshotBuilder after = new();
        after.Add(Triangle(new Vec3d(500.0, 0, 0), Face(10)));

        Vec3d moved = after.Build(2, first.Origin).Origin;

        moved.Should().NotBe(first.Origin);
        (moved.X - 500.0).Should().BeInRange(-SnapshotBuilder.OriginGrid, SnapshotBuilder.OriginGrid);
    }

    [Fact]
    public void JitterAroundAGridBoundaryDoesNotOscillateTheOrigin()
    {
        // The failure mode hysteresis exists for: a scene centred on a grid line, edited back and
        // forth, must not re-upload every buffer on every edit.
        Vec3d origin = new SnapshotBuilder().Build(0).Origin;

        for (int i = 0; i < 10; ++i)
        {
            SnapshotBuilder builder = new();
            builder.Add(Triangle(new Vec3d(10.0 + (i % 2 == 0 ? 0.0 : 0.002), 10.0, 10.0), Face(10)));

            Vec3d next = builder.Build(i + 1, origin).Origin;

            if (i > 0)
            {
                next.Should().Be(origin, "edit {0} moved the origin", i);
            }

            origin = next;
        }
    }

    [Fact]
    public void NormalsAreNotTranslated()
    {
        // A normal is a direction. Subtracting the origin from it would leave lighting subtly
        // wrong everywhere rather than obviously wrong somewhere.
        SnapshotBuilder builder = new();
        builder.Add(Triangle(new Vec3d(100, 100, 100), Face(10)));

        DisplaySnapshot snapshot = builder.Build(1);
        ImmutableArray<float> normals = snapshot.Bodies[0].Mesh.Normals;

        normals[0].Should().Be(0f);
        normals[1].Should().Be(0f);
        normals[2].Should().Be(1f);
    }

    [Fact]
    public void AnEmptySceneHasAnOriginAtZero()
    {
        new SnapshotBuilder().Build(1).Origin.Should().Be(Vec3d.Zero);
    }

    // --- Publication --------------------------------------------------------------------------

    [Fact]
    public void TheHolderStartsEmptyRatherThanNull()
    {
        // So the first frame needs no special case.
        new SnapshotHolder().Current.Should().BeSameAs(DisplaySnapshot.Empty);
    }

    [Fact]
    public void PublishingANewerSnapshotReplacesTheCurrentOne()
    {
        SnapshotHolder holder = new();
        DisplaySnapshot newer = new SnapshotBuilder().Build(1);

        holder.Publish(newer).Should().BeTrue();
        holder.Current.Should().BeSameAs(newer);
    }

    [Fact]
    public void AnOlderSnapshotIsDiscarded()
    {
        // Rebuilds run concurrently where the feature graph allows, so two can finish out of
        // order. A plain assignment would let the slower, older one win and leave the viewport
        // showing a superseded scene with nothing to correct it.
        SnapshotHolder holder = new();
        DisplaySnapshot newer = new SnapshotBuilder().Build(5);
        DisplaySnapshot older = new SnapshotBuilder().Build(4);

        holder.Publish(newer).Should().BeTrue();
        holder.Publish(older).Should().BeFalse();
        holder.Current.Should().BeSameAs(newer);
    }

    [Fact]
    public void RepublishingTheSameVersionIsRejected()
    {
        SnapshotHolder holder = new();

        holder.Publish(new SnapshotBuilder().Build(3)).Should().BeTrue();
        holder.Publish(new SnapshotBuilder().Build(3)).Should().BeFalse();
    }

    [Fact]
    public void ClearingReturnsToTheEmptyScene()
    {
        SnapshotHolder holder = new();
        holder.Publish(new SnapshotBuilder().Build(9));

        holder.Clear();

        holder.Current.Should().BeSameAs(DisplaySnapshot.Empty);
    }

    [Fact]
    public async Task ConcurrentPublishersLeaveTheNewestSnapshotWinning()
    {
        // The property the compare-and-swap exists for. Many producers, arbitrary interleaving,
        // and the holder must still end up on the highest version -- and must never be observed
        // holding anything but a fully-formed snapshot.
        SnapshotHolder holder = new();
        const int versions = 200;

        DisplaySnapshot[] snapshots = [.. Enumerable.Range(1, versions)
            .Select(v => new SnapshotBuilder().Build(v))];

        List<long> observed = [];
        using CancellationTokenSource reading = new();

        Task reader = Task.Run(() =>
        {
            while (!reading.IsCancellationRequested)
            {
                observed.Add(holder.Current.Version);
            }
        });

        await Task.WhenAll(snapshots.Select(s => Task.Run(() => holder.Publish(s))));
        await reading.CancelAsync();
        await reader;

        holder.Current.Version.Should().Be(versions);

        // Versions must never go backwards from a reader's point of view.
        observed.Should().BeInAscendingOrder();
    }
}
