using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;

using FluentAssertions;

using OpenMCAD.Kernel;
using OpenMCAD.Kernel.Threading;
using OpenMCAD.Math;
using OpenMCAD.Render.Direct3D12;

using SharpGen.Runtime;

using Vortice.Mathematics;

using Xunit;

namespace OpenMCAD.Render.Tests;

/// <summary>
/// Phase 2's fourth exit criterion: the viewport keeps rendering at full rate while a long kernel
/// operation runs.
/// </summary>
/// <remarks>
/// <para>
/// This is the criterion that everything else in the viewport design is arranged around — the
/// immutable snapshot, the atomic reference swap, the single-threaded dispatcher — and it was the
/// one with nothing checking it. That combination is worth being suspicious of: a claim this
/// central is exactly the sort that gets designed for, believed, and never actually tried.
/// </para>
/// <para>
/// <b>Why the synthetic operation blocks rather than burning processor time.</b> The failure this
/// is guarding against is architectural: a lock shared with the render path, a synchronous marshal
/// onto the kernel thread, a snapshot read that waits for a rebuild to finish. Every one of those
/// stops the render loop dead, and an operation that simply occupies the kernel thread exposes all
/// of them without depending on how the scheduler feels. An operation that burned processor time
/// instead would add contention for cores, which would slow the render measurably — but that is a
/// property of the machine having finite processors, not of this design, and a test that failed
/// when the build agent was busy would be turned off rather than fixed.
/// </para>
/// </remarks>
public sealed class ViewportResponsivenessTests
{

    /// <summary>How many frames each measurement averages over.</summary>
    private const int Frames = 30;

    [Fact]
    public async Task TheViewportKeepsRenderingWhileTheKernelThreadIsOccupied()
    {
        using Fixture fixture = Fixture.Create();

        if (fixture.Skipped is not null)
        {
            Assert.Skip(fixture.Skipped);
        }

        // Measured first, and deliberately not from a cold device: the first frames of any D3D12
        // process include pipeline warm-up that has nothing to do with what is being tested here,
        // and comparing a warm run against a cold one would show a speed-up from doing more work.
        fixture.RenderFrames(Frames);

        double idle = fixture.MedianFrameMs(Frames);

        using KernelDispatcher dispatcher = new("responsiveness test kernel");
        using ManualResetEventSlim started = new(false);
        using ManualResetEventSlim release = new(false);

        // Stands in for the ten-second operation of the exit criterion. Held open for exactly as
        // long as the measurement takes, rather than for a fixed duration, so the test is neither
        // slow nor racing a wall clock.
        ValueTask operation = dispatcher.RunAsync(
            "synthetic long operation",
            () =>
            {
                started.Set();
                release.Wait();
            },
            KernelPriority.Rebuild);

        started.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue(
            "the dispatcher should have started the operation on its own thread");

        double busy = fixture.MedianFrameMs(Frames);

        // Checked before the operation is released, because the whole test turns on it. Without
        // this the frames above could have been rendered after the kernel finished, and the
        // assertion below would then be comparing an idle run against another idle run and
        // passing however the viewport behaved.
        operation.IsCompleted.Should().BeFalse(
            "the kernel operation must still have been in flight while those frames were drawn, "
            + "or this measured nothing");

        release.Set();
        await operation;

        // A generous multiple. Anything that serialises rendering behind kernel work does not slow
        // it down — it stops it entirely, and the frames would not have been produced at all.
        // The bound is here to catch a partial stall; it is not a pacing measurement, and tying it
        // tighter would only buy failures on a loaded machine.
        busy.Should().BeLessThan(
            (idle * 3.0) + 2.0,
            $"rendering should be unaffected by kernel work, but went from {idle:0.00} ms a frame "
            + $"to {busy:0.00} ms while the kernel thread was occupied");
    }

    [Fact]
    public async Task TheViewportKeepsUpWithSnapshotsPublishedByTheKernel()
    {
        using Fixture fixture = Fixture.Create();

        if (fixture.Skipped is not null)
        {
            Assert.Skip(fixture.Skipped);
        }

        // The other half of the criterion, and the half a stalled-thread test cannot reach: not
        // just that frames keep coming while the kernel works, but that they show what the kernel
        // has produced. A viewport that carried on drawing a snapshot from ten seconds ago would
        // satisfy "renders at full rate" perfectly and be useless.
        SnapshotHolder holder = new();
        holder.Publish(Snapshot(1));

        using KernelDispatcher dispatcher = new("publishing test kernel");
        using ManualResetEventSlim started = new(false);
        using CancellationTokenSource stop = new();

        ValueTask operation = dispatcher.RunAsync(
            "synthetic rebuild storm",
            () =>
            {
                long version = 1;
                started.Set();

                while (!stop.IsCancellationRequested)
                {
                    holder.Publish(Snapshot(++version));

                    // Yielded rather than slept. Thread.Sleep(1) asks for a millisecond and gets
                    // whatever the system timer's granularity allows, which on Windows is about
                    // fifteen by default -- so this published four snapshots across thirty frames
                    // and the test could not tell a viewport that was keeping up from one that was
                    // not. Yielding gives up the core without going near the timer.
                    Thread.Yield();
                }
            },
            KernelPriority.Rebuild);

        started.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        List<long> observed = [];

        for (int i = 0; i < Frames; ++i)
        {
            DisplaySnapshot snapshot = holder.Current;

            observed.Add(snapshot.Version);
            fixture.RenderSnapshot(snapshot);
        }

        operation.IsCompleted.Should().BeFalse(
            "the kernel must still have been publishing while those frames were drawn");

        await stop.CancelAsync();
        await operation;

        observed.Distinct().Should().HaveCountGreaterThan(
            Frames / 3,
            "the viewport should have picked up successive snapshots as fast as the kernel "
            + $"produced them, but saw only {observed.Distinct().Count()} across {Frames} frames");

        observed.Should().BeInAscendingOrder(
            "a frame must never show an older scene than the frame before it, which is what the "
            + "holder's compare-and-swap is for: rebuilds may finish out of order");
    }

    [Fact]
    public void RenderingReadsWhicheverSnapshotIsCurrentWithoutWaiting()
    {
        // The swap is the mechanism the criterion above depends on, so it is worth an assertion
        // that does not involve timing at all: a reader takes whatever is published at the moment
        // it looks, and publishing never waits for a reader to finish with the previous one.
        SnapshotHolder holder = new();

        DisplaySnapshot first = Snapshot(1);
        DisplaySnapshot second = Snapshot(2);

        holder.Publish(first);

        DisplaySnapshot? held = holder.Current;

        holder.Publish(second);

        held.Should().BeSameAs(
            first, "a reader that already took a snapshot keeps the one it took");

        holder.Current.Should().BeSameAs(
            second, "and the next reader gets the new one, with nothing in between");
    }

    // --- Fixtures -----------------------------------------------------------------------------

    private static DisplaySnapshot Snapshot(long version)
    {
        SnapshotBuilder builder = new();
        builder.Add(Cube());

        return builder.Build(version);
    }

    /// <summary>A cube, so a frame has some real work in it.</summary>
    private static MeshBuffer Cube()
    {
        const double H = 0.5;

        (Vec3d Normal, Vec3d U, Vec3d V)[] faces =
        [
            (Vec3d.UnitX, Vec3d.UnitY, Vec3d.UnitZ),
            (-Vec3d.UnitX, Vec3d.UnitZ, Vec3d.UnitY),
            (Vec3d.UnitY, Vec3d.UnitZ, Vec3d.UnitX),
            (-Vec3d.UnitY, Vec3d.UnitX, Vec3d.UnitZ),
            (Vec3d.UnitZ, Vec3d.UnitX, Vec3d.UnitY),
            (-Vec3d.UnitZ, Vec3d.UnitY, Vec3d.UnitX),
        ];

        ImmutableArray<Vec3d>.Builder positions = ImmutableArray.CreateBuilder<Vec3d>();
        ImmutableArray<Vec3d>.Builder normals = ImmutableArray.CreateBuilder<Vec3d>();
        ImmutableArray<int>.Builder indices = ImmutableArray.CreateBuilder<int>();
        ImmutableArray<int>.Builder triangleFaces = ImmutableArray.CreateBuilder<int>();
        ImmutableArray<SubEntity>.Builder subEntities = ImmutableArray.CreateBuilder<SubEntity>();

        for (int face = 0; face < faces.Length; ++face)
        {
            (Vec3d normal, Vec3d u, Vec3d v) = faces[face];
            Vec3d origin = normal * H;
            int baseIndex = positions.Count;

            positions.Add(origin - (u * H) - (v * H));
            positions.Add(origin + (u * H) - (v * H));
            positions.Add(origin + (u * H) + (v * H));
            positions.Add(origin - (u * H) + (v * H));

            for (int i = 0; i < 4; ++i)
            {
                normals.Add(normal);
            }

            indices.AddRange(baseIndex, baseIndex + 1, baseIndex + 2);
            indices.AddRange(baseIndex, baseIndex + 2, baseIndex + 3);
            triangleFaces.AddRange(face, face);
            subEntities.Add(new SubEntity(new KernelShape(1), (ulong)(face + 1), SubEntityKind.Face));
        }

        return new MeshBuffer(
            positions.ToImmutable(),
            normals.ToImmutable(),
            indices.ToImmutable(),
            triangleFaces.ToImmutable(),
            subEntities.ToImmutable());
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string skipped) => Skipped = skipped;

        private Fixture(
            D3D12RenderDevice device, OffscreenSurface surface, FacePass pass, SceneGeometry scene)
        {
            Device = device;
            Surface = surface;
            Pass = pass;
            Scene = scene;

            Camera.AspectRatio = 1.0;
            Camera.LookFrom(StandardView.Isometric);
            Camera.ZoomToFit(scene.Bounds);
        }

        public string? Skipped { get; }

        public D3D12RenderDevice Device { get; } = null!;

        public OffscreenSurface Surface { get; } = null!;

        public FacePass Pass { get; } = null!;

        public SceneGeometry Scene { get; } = null!;

        public Camera Camera { get; } = new();

        public static Fixture Create(int size = 128)
        {
            D3D12RenderDevice? device = null;
            OffscreenSurface? surface = null;
            FacePass? pass = null;

            try
            {
                device = new D3D12RenderDevice(TestDevices.Software);
                surface = new OffscreenSurface(device, size, size);

                pass = new FacePass(
                    device.Device, OffscreenSurface.ColourFormat, DepthBuffer.DepthFormat,
                    optimiseShaders: false);

                SnapshotBuilder builder = new();
                builder.Add(Cube());

                SceneGeometry scene = SceneGeometry.Upload(device, builder.Build(1));

                return new Fixture(device, surface, pass, scene);
            }
            catch (Exception exception)
                when (exception is RenderDeviceUnavailableException or SharpGenException)
            {
                pass?.Dispose();
                surface?.Dispose();
                device?.Dispose();

                return new Fixture($"No usable D3D12 device: {exception.Message}");
            }
        }

        /// <summary>Uploads a snapshot and draws it, as the viewport does on a rebuild.</summary>
        /// <param name="snapshot">The scene to show.</param>
        /// <remarks>
        /// Uploaded and released per frame, which no real viewport would do — it would keep the
        /// buffers and replace them only when the scene changed. Here the scene changes every
        /// frame by construction, so the wasteful version and the careful one do the same work,
        /// and the wasteful one does not need a cache to be trusted.
        /// </remarks>
        public void RenderSnapshot(DisplaySnapshot snapshot)
        {
            using SceneGeometry geometry = SceneGeometry.Upload(Device, snapshot);

            RenderOneFrame(geometry);
        }

        /// <summary>Draws a number of frames, each fenced, and discards the timings.</summary>
        /// <param name="count">How many.</param>
        public void RenderFrames(int count)
        {
            for (int i = 0; i < count; ++i)
            {
                RenderOneFrame();
            }
        }

        /// <summary>Draws a number of frames and returns the median time one took.</summary>
        /// <param name="count">How many.</param>
        /// <returns>The median, in milliseconds.</returns>
        /// <remarks>
        /// The median rather than the mean. A single frame that lands next to a garbage collection
        /// or a scheduler quantum drags a mean of thirty a long way, and the question here is what
        /// a typical frame cost, not what the worst one did.
        /// </remarks>
        public double MedianFrameMs(int count)
        {
            double[] samples = new double[count];
            Stopwatch clock = new();

            for (int i = 0; i < count; ++i)
            {
                clock.Restart();
                RenderOneFrame();
                samples[i] = clock.Elapsed.TotalMilliseconds;
            }

            Array.Sort(samples);
            return samples[count / 2];
        }

        public void Dispose()
        {
            Scene?.Dispose();
            Pass?.Dispose();
            Surface?.Dispose();
            Device?.Dispose();
        }

        /// <summary>One frame, submitted and waited on, as the viewport paces itself.</summary>
        private void RenderOneFrame() => RenderOneFrame(Scene);

        /// <summary>One frame of a given scene, submitted and waited on.</summary>
        private void RenderOneFrame(SceneGeometry scene)
        {
            Mat4d projection = Camera.ProjectionMatrix(scene.Bounds);
            Vec3d origin = scene.Origin;

            Surface.SetConstants(new FrameConstants
            {
                ViewProjection = ToShaderMatrix(
                    projection * Mat4d.LookAt(
                        Camera.Position - origin, Camera.Target - origin, Camera.Up)),
                CameraPosition = ToVector3(Camera.Position - origin),
                LightDirection = ToVector3(FacePass.KeyLightDirection(Camera)),
            });

            Surface.Render(
                new Color4(0.05f, 0.05f, 0.08f, 1.0f),
                commands => Pass.Draw(
                    commands, scene, Surface.ConstantBufferAddress, frustum: null));
        }

        private static Matrix4x4 ToShaderMatrix(Mat4d m) => new(
            (float)m.M11, (float)m.M12, (float)m.M13, (float)m.M14,
            (float)m.M21, (float)m.M22, (float)m.M23, (float)m.M24,
            (float)m.M31, (float)m.M32, (float)m.M33, (float)m.M34,
            (float)m.M41, (float)m.M42, (float)m.M43, (float)m.M44);

        private static Vector3 ToVector3(Vec3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
    }
}
