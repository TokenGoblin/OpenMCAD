using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

using OpenMCAD.Math;
using OpenMCAD.Render.Direct3D12;

using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace OpenMCAD.Render.Perf;

/// <summary>What one scene measured.</summary>
/// <param name="Label">What was measured.</param>
/// <param name="Bodies">How many bodies the scene had.</param>
/// <param name="Triangles">How many triangles it actually contained.</param>
/// <param name="Frames">How many frames were timed.</param>
/// <param name="MedianMs">The median frame, in milliseconds.</param>
/// <param name="P95Ms">The 95th percentile frame.</param>
/// <param name="P99Ms">The 99th percentile frame.</param>
/// <param name="GpuMedianMs">The median GPU time, from timestamps on the queue.</param>
/// <param name="DrawnBodies">How many bodies survived culling on the last frame.</param>
public readonly record struct FrameMeasurement(
    string Label,
    int Bodies,
    int Triangles,
    int Frames,
    double MedianMs,
    double P95Ms,
    double P99Ms,
    double GpuMedianMs,
    int DrawnBodies)
{
    /// <summary>Gets whether the median frame fits the plan's sixteen-millisecond budget.</summary>
    public bool WithinBudget => MedianMs <= 16.0;

    /// <inheritdoc />
    public override string ToString()
        => $"{Label,-22} {Bodies,7} bodies {Triangles,9} tris  "
            + $"median {MedianMs,7:0.00} ms  p95 {P95Ms,7:0.00}  p99 {P99Ms,7:0.00}  "
            + $"gpu {GpuMedianMs,7:0.00}  drawn {DrawnBodies,6}  {(WithinBudget ? "ok" : "OVER")}";
}

/// <summary>
/// Renders a scene many times and reports how long the frames took (P2-T13).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every frame is fenced.</b> Without that the CPU runs ahead and the measurement is of how
/// fast commands can be recorded, which on a scene the GPU is struggling with is a number that
/// looks wonderful and means nothing. Fencing costs the pipelining a real application enjoys, so
/// the figures here are slightly pessimistic — and pessimistic in a known direction is the right
/// way for a budget to be wrong.
/// </para>
/// <para>
/// <b>The camera orbits.</b> The plan's budget is for a rotating view, and a still one lets the
/// driver and the caches do work once that a moving one has to repeat — quite apart from the
/// frustum culler seeing the same answer every frame.
/// </para>
/// <para>
/// GPU time is taken from timestamp queries on the queue, so it can be compared against the wall
/// clock: when the two diverge, the difference is the CPU, and knowing which of the two is the
/// ceiling decides whether the next optimisation is batching or geometry.
/// </para>
/// </remarks>
public sealed class FrameTimer : IDisposable
{
    private readonly D3D12RenderDevice _device;
    private readonly int _width;
    private readonly int _height;

    private readonly MsaaTarget _msaa;
    private readonly EnvironmentPass _environment;
    private readonly FacePass _faces;
    private readonly EdgePass _edges;
    private readonly ID3D12CommandAllocator _allocator;
    private readonly ID3D12GraphicsCommandList _commands;
    private readonly ID3D12Fence _fence;
    private readonly AutoResetEvent _fenceReached = new(false);
    private readonly ID3D12Resource _constants;
    private readonly ID3D12QueryHeap _timestamps;
    private readonly ID3D12Resource _timestampReadback;

    private ulong _lastSignalled;
    private bool _disposed;

    /// <summary>Creates the harness.</summary>
    /// <param name="device">The device to render on.</param>
    /// <param name="width">Render width in pixels.</param>
    /// <param name="height">Render height in pixels.</param>
    /// <param name="sampleCount">How many samples to request.</param>
    public FrameTimer(D3D12RenderDevice device, int width, int height, int sampleCount)
    {
        ArgumentNullException.ThrowIfNull(device);

        _device = device;
        _width = width;
        _height = height;

        _msaa = new MsaaTarget(device.Device, new Color4(0.22f, 0.24f, 0.27f, 1.0f), sampleCount);
        _msaa.Resize(width, height);

        _environment = new EnvironmentPass(
            device.Device, SwapChainTarget.BackBufferFormat, DepthBuffer.DepthFormat,
            optimiseShaders: true, _msaa.SampleCount);

        _faces = new FacePass(
            device.Device, SwapChainTarget.BackBufferFormat, DepthBuffer.DepthFormat,
            optimiseShaders: true, _msaa.SampleCount);

        _edges = new EdgePass(
            device.Device, SwapChainTarget.BackBufferFormat, DepthBuffer.DepthFormat,
            optimiseShaders: true, _msaa.SampleCount);

        _allocator = device.Device.CreateCommandAllocator(CommandListType.Direct);

        _commands = device.Device.CreateCommandList<ID3D12GraphicsCommandList>(
            CommandListType.Direct, _allocator);

        _commands.Close();
        _fence = device.Device.CreateFence(0);

        _constants = device.Device.CreateCommittedResource(
            HeapType.Upload, HeapFlags.None, ResourceDescription.Buffer(512),
            ResourceStates.GenericRead);

        _timestamps = device.Device.CreateQueryHeap<ID3D12QueryHeap>(
            new QueryHeapDescription(QueryHeapType.Timestamp, 2));

        _timestampReadback = device.Device.CreateCommittedResource(
            HeapType.Readback, HeapFlags.None, ResourceDescription.Buffer(16),
            ResourceStates.CopyDest);
    }

    /// <summary>Gets how many samples per pixel the scene is drawn with.</summary>
    public int SampleCount => _msaa.SampleCount;

    /// <summary>
    /// Times a scene.
    /// </summary>
    /// <param name="label">What to call it in the report.</param>
    /// <param name="snapshot">The scene to draw.</param>
    /// <param name="frames">How many frames to time.</param>
    /// <param name="warmup">How many frames to run first and discard.</param>
    /// <returns>What it measured.</returns>
    /// <remarks>
    /// The warm-up matters more than it looks. The first frame after an upload pays for pipeline
    /// state creation, driver-side shader compilation and the upload itself; including it in a
    /// median of thirty would move the median, and in a p99 it would *be* the p99.
    /// </remarks>
    public FrameMeasurement Measure(
        string label, DisplaySnapshot snapshot, int frames = 60, int warmup = 10)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(snapshot);

        using SceneGeometry scene = SceneGeometry.Upload(_device, snapshot);

        Camera camera = new() { AspectRatio = (double)_width / _height };
        camera.LookFrom(StandardView.Isometric);
        camera.ZoomToFit(snapshot.Bounds);

        double[] wall = new double[frames];
        double[] gpu = new double[frames];

        for (int i = 0; i < warmup + frames; ++i)
        {
            // A degree per frame. The budget is for a rotating view, and a still one lets caches
            // and the culler answer the same question repeatedly.
            camera.Orbit(0.0175, 0.0);

            long start = Stopwatch.GetTimestamp();
            RenderOneFrame(scene, camera, snapshot);
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            if (i >= warmup)
            {
                wall[i - warmup] = elapsed;
                gpu[i - warmup] = ReadGpuMilliseconds();
            }
        }

        Array.Sort(wall);
        Array.Sort(gpu);

        return new FrameMeasurement(
            label,
            snapshot.Bodies.Length,
            snapshot.TriangleCount,
            frames,
            Percentile(wall, 0.50),
            Percentile(wall, 0.95),
            Percentile(wall, 0.99),
            Percentile(gpu, 0.50),
            _faces.BodiesDrawn);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        WaitForGpu();
        _disposed = true;

        _timestampReadback.Dispose();
        _timestamps.Dispose();
        _constants.Dispose();
        _fence.Dispose();
        _fenceReached.Dispose();
        _commands.Dispose();
        _allocator.Dispose();
        _edges.Dispose();
        _faces.Dispose();
        _environment.Dispose();
        _msaa.Dispose();
    }

    private static double Percentile(double[] sorted, double fraction)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        int index = System.Math.Clamp((int)(fraction * (sorted.Length - 1)), 0, sorted.Length - 1);
        return sorted[index];
    }

    private void RenderOneFrame(SceneGeometry scene, Camera camera, DisplaySnapshot snapshot)
    {
        _allocator.Reset();
        _commands.Reset(_allocator);

        _commands.EndQuery(_timestamps, QueryType.Timestamp, 0);

        _commands.OMSetRenderTargets(_msaa.RenderTargetView, _msaa.DepthStencilView);
        _commands.ClearRenderTargetView(_msaa.RenderTargetView, _msaa.ClearColour);

        _commands.ClearDepthStencilView(
            _msaa.DepthStencilView, ClearFlags.Depth, DepthBuffer.ClearDepth, 0);

        _commands.RSSetViewport(0, 0, _width, _height);
        _commands.RSSetScissorRect(_width, _height);

        Mat4d projection = camera.ProjectionMatrix(snapshot.Bounds);
        Vec3d origin = snapshot.Origin;

        Mat4d view = Mat4d.LookAt(
            camera.Position - origin, camera.Target - origin, camera.Up);

        FrameConstants frame = new()
        {
            ViewProjection = ToMatrix(projection * view),
            CameraPosition = ToVector3(camera.Position - origin),
            LightDirection = ToVector3(FacePass.KeyLightDirection(camera)),
            ViewportSize = new Vector2(_width, _height),
        };

        EnvironmentConstants environment = EnvironmentPass.ConstantsFor(
            camera, snapshot.Bounds, origin, EnvironmentStyle.Default.ForScene(snapshot.Bounds));

        WriteConstants(frame, environment);

        ulong frameAddress = _constants.GPUVirtualAddress;
        ulong environmentAddress = frameAddress + 256;

        Frustum frustum = Frustum.FromViewProjection(projection * camera.ViewMatrix());

        _environment.Draw(_commands, environmentAddress);
        // Deliberately without highlight states, so the harness exercises the passes' fallback
        // binding -- which is the path that removed the device before they had one.
        _faces.Draw(_commands, scene, frameAddress, frustum);
        _edges.Draw(_commands, scene, frameAddress, EdgeStyle.Default, frustum);

        _commands.EndQuery(_timestamps, QueryType.Timestamp, 1);

        _commands.ResolveQueryData(
            _timestamps, QueryType.Timestamp, 0, 2, _timestampReadback, 0);

        _commands.Close();
        _device.Queue.ExecuteCommandList(_commands);

        WaitForGpu();
    }

    private void WriteConstants(FrameConstants frame, EnvironmentConstants environment)
    {
        // Both blocks in one buffer, each on a 256-byte boundary because that is the alignment a
        // constant buffer view requires.
        Span<byte> bytes = stackalloc byte[512];

        MemoryMarshal.Write(bytes, in frame);
        MemoryMarshal.Write(bytes[256..], in environment);

        _constants.SetData<byte>(bytes);
    }

    private double ReadGpuMilliseconds()
    {
        // Ticks per second on the queue's own clock, which is not the CPU's and is not constant
        // between devices -- so the conversion has to be asked for rather than assumed.
        _device.Queue.GetTimestampFrequency(out ulong frequency);

        if (frequency == 0)
        {
            return 0;
        }

        Span<ulong> stamps = _timestampReadback.Map<ulong>(0, 2);

        try
        {
            return stamps[1] <= stamps[0]
                ? 0
                : (stamps[1] - stamps[0]) * 1000.0 / frequency;
        }
        finally
        {
            _timestampReadback.Unmap(0);
        }
    }

    private void WaitForGpu()
    {
        ulong target = ++_lastSignalled;
        _device.Queue.Signal(_fence, target);

        if (_fence.CompletedValue >= target)
        {
            return;
        }

        _fence.SetEventOnCompletion(target, _fenceReached);
        _fenceReached.WaitOne();
    }

    private static Matrix4x4 ToMatrix(Mat4d m) => new(
        (float)m.M11, (float)m.M12, (float)m.M13, (float)m.M14,
        (float)m.M21, (float)m.M22, (float)m.M23, (float)m.M24,
        (float)m.M31, (float)m.M32, (float)m.M33, (float)m.M34,
        (float)m.M41, (float)m.M42, (float)m.M43, (float)m.M44);

    private static Vector3 ToVector3(Vec3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
}
