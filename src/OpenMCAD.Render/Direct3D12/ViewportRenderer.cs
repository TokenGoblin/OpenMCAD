using System.Numerics;
using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using OpenMCAD.Math;

using Vortice.Direct3D12;
using Vortice.Mathematics;

namespace OpenMCAD.Render.Direct3D12;

/// <summary>
/// Draws frames into a <see cref="SwapChainTarget"/> (P2-T02 and P2-T05).
/// </summary>
/// <remarks>
/// <para>
/// <b>One command allocator per frame in flight.</b> An allocator cannot be reset while the GPU is
/// still executing commands recorded from it, so a single shared one would force a full stall
/// every frame. The fence value each allocator was last used with is remembered, and the loop
/// waits on that one value rather than for the device to go idle.
/// </para>
/// <para>
/// <b>Fence values and frame numbers are the same sequence.</b> One signal per presented frame
/// means the fence's completed value is the number of the newest frame the GPU has finished, which
/// is exactly what the upload ring needs to know before it reclaims. Keeping them as one counter
/// rather than two removes the possibility of the two drifting apart, which is a class of bug that
/// only ever shows up as intermittent corruption under load.
/// </para>
/// </remarks>
public sealed class ViewportRenderer : IDisposable
{
    /// <summary>
    /// How much upload memory to keep for per-frame constants.
    /// </summary>
    /// <remarks>
    /// A frame currently writes one 96-byte block. A quarter of a megabyte is far more than that
    /// needs and still small enough not to matter; it is sized for the per-object constants and
    /// dynamic geometry that later passes will want rather than for what is written today.
    /// </remarks>
    public const int UploadRingBytes = 256 * 1024;

    private readonly ILogger _logger;
    private readonly D3D12RenderDevice _device;
    private readonly SwapChainTarget _target;
    private readonly ID3D12CommandAllocator[] _allocators;
    private readonly ulong[] _frameFenceValues;
    private readonly ID3D12GraphicsCommandList _commands;
    private readonly ID3D12Fence _fence;
    private readonly AutoResetEvent _fenceReached = new(false);
    private readonly DepthBuffer _depth;
    private readonly FacePass _faces;
    private readonly EdgePass _edges;
    private readonly UploadRing _uploads;

    private DisplaySnapshot _snapshot = DisplaySnapshot.Empty;
    private SceneGeometry? _scene;
    private ulong _lastSignalled;
    private bool _disposed;

    /// <summary>Creates the renderer.</summary>
    /// <param name="device">The device to record on.</param>
    /// <param name="target">Where frames go.</param>
    /// <param name="logger">Where to report device loss.</param>
    public ViewportRenderer(D3D12RenderDevice device, SwapChainTarget target, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(target);

        _logger = logger ?? NullLogger.Instance;
        _device = device;
        _target = target;

        _allocators = new ID3D12CommandAllocator[SwapChainTarget.BufferCount];
        _frameFenceValues = new ulong[SwapChainTarget.BufferCount];

        for (int i = 0; i < _allocators.Length; ++i)
        {
            _allocators[i] = device.Device.CreateCommandAllocator(CommandListType.Direct);
            _allocators[i].Name = $"frame allocator {i}";
        }

        _commands = device.Device.CreateCommandList<ID3D12GraphicsCommandList>(
            CommandListType.Direct, _allocators[0]);

        // Created in the recording state and closed immediately, so the loop below can treat every
        // frame identically: reset, record, close.
        _commands.Close();
        _commands.Name = "viewport commands";

        _fence = device.Device.CreateFence(0);
        _fence.Name = "viewport frame fence";

        _depth = new DepthBuffer(device.Device);
        _faces = new FacePass(device.Device, SwapChainTarget.BackBufferFormat, DepthBuffer.DepthFormat);
        _edges = new EdgePass(device.Device, SwapChainTarget.BackBufferFormat, DepthBuffer.DepthFormat);
        _uploads = new UploadRing(device.Device, UploadRingBytes, "viewport constants");
    }

    /// <summary>Gets or sets the colour the viewport is cleared to.</summary>
    /// <remarks>
    /// A mid grey-blue rather than black or white. Shaded parts read badly against both: black
    /// swallows dark faces and hides silhouettes, and white makes every edge look like glare. A
    /// neutral mid-tone is what CAD packages settle on for the same reason.
    /// </remarks>
    public Color4 Background { get; set; } = new(0.22f, 0.24f, 0.27f, 1.0f);

    /// <summary>Gets the camera the scene is drawn through.</summary>
    /// <remarks>
    /// Starts isometric. A camera left at the identity orientation looks straight down an axis,
    /// which renders a box as a plain rectangle and a cylinder as a circle -- correct, and useless
    /// as a first impression of a solid. Three-quarter view is what every CAD package opens on,
    /// for the same reason.
    /// </remarks>
    public Camera Camera { get; } = CreateCamera();

    /// <summary>Gets how many frames have been presented.</summary>
    public long FrameCount { get; private set; }

    /// <summary>Gets how many bodies the last frame drew.</summary>
    public int BodiesDrawn => _faces.BodiesDrawn;

    /// <summary>Gets how many bodies the last frame culled as off-screen.</summary>
    public int BodiesCulled => _faces.BodiesCulled;

    /// <summary>Gets how many triangles the last frame drew.</summary>
    public int TrianglesDrawn => _faces.TrianglesDrawn;

    /// <summary>Gets how many edge segments the last frame drew.</summary>
    public int SegmentsDrawn => _edges.SegmentsDrawn;

    /// <summary>Gets or sets how edges are drawn.</summary>
    public EdgeStyle EdgeStyle { get; set; } = EdgeStyle.Default;

    /// <summary>Gets or sets whether to draw edges at all.</summary>
    public bool ShowEdges { get; set; } = true;

    /// <summary>Gets or sets what to draw.</summary>
    /// <remarks>
    /// Setting this does not touch the GPU. The buffers are rebuilt at the start of the next frame,
    /// on the thread that renders, because uploading from whichever thread finished the rebuild
    /// would race the frame currently being recorded.
    /// </remarks>
    public DisplaySnapshot Snapshot
    {
        get => _snapshot;
        set => _snapshot = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Gets whether a scene has been uploaded and has something in it.</summary>
    public bool HasGeometry => _scene is { Bodies.Count: > 0 };

    /// <summary>
    /// Draws and presents one frame.
    /// </summary>
    /// <param name="verticalSync">Whether to wait for the vertical blank.</param>
    /// <returns>
    /// <see langword="false"/> if the device was lost and everything must be rebuilt.
    /// </returns>
    public bool RenderFrame(bool verticalSync = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int index = _target.CurrentBackBufferIndex;

        // The allocator for this slot may still be in use by the frame that last had it, which is
        // BufferCount frames ago. Waiting on that one fence value is what lets the CPU stay ahead;
        // waiting for idle instead would serialise the two and halve the frame rate.
        WaitForFence(_frameFenceValues[index]);

        // Safe only because the fence value is the frame number. See the remarks on this type.
        _uploads.Reclaim((long)_fence.CompletedValue);
        _uploads.BeginFrame((long)_lastSignalled + 1);

        SyncScene();
        _depth.Resize(_target.Width, _target.Height);

        _allocators[index].Reset();
        _commands.Reset(_allocators[index]);

        ID3D12Resource backBuffer = _target.BackBuffer(index);

        // Present -> RenderTarget, and back again before presenting. Omitting either barrier is
        // undefined behaviour that happens to work on some drivers, which is the worst kind.
        _commands.ResourceBarrierTransition(
            backBuffer, ResourceStates.Present, ResourceStates.RenderTarget);

        CpuDescriptorHandle view = _target.RenderTargetView(index);
        CpuDescriptorHandle depthView = _depth.View;

        _commands.OMSetRenderTargets(view, depthView);
        _commands.ClearRenderTargetView(view, Background);
        _commands.ClearDepthStencilView(depthView, ClearFlags.Depth, DepthBuffer.ClearDepth, 0);

        _commands.RSSetViewport(0, 0, _target.Width, _target.Height);
        _commands.RSSetScissorRect(_target.Width, _target.Height);

        DrawScene();

        _commands.ResourceBarrierTransition(
            backBuffer, ResourceStates.RenderTarget, ResourceStates.Present);

        _commands.Close();
        _device.Queue.ExecuteCommandList(_commands);

        if (!_target.Present(verticalSync))
        {
            return false;
        }

        _frameFenceValues[index] = ++_lastSignalled;
        _device.Queue.Signal(_fence, _lastSignalled);

        FrameCount++;
        return true;
    }

    /// <summary>Frames the whole scene, if there is one.</summary>
    /// <returns>Whether there was anything to frame.</returns>
    public bool ZoomToFit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_snapshot.Bounds.IsEmpty)
        {
            return false;
        }

        // The aspect ratio first. Fitting decides a distance from the narrower of the two field
        // of view axes, so doing this against a stale ratio frames the scene for a viewport shape
        // that is not the one it is about to be drawn in.
        if (_target.Width > 0 && _target.Height > 0)
        {
            Camera.AspectRatio = (double)_target.Width / _target.Height;
        }

        Camera.ZoomToFit(_snapshot.Bounds);
        return true;
    }

    /// <summary>Blocks until the GPU has finished every frame recorded so far.</summary>
    /// <remarks>Needed before a resize, which must release the back buffers.</remarks>
    public void WaitForGpu()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        WaitForFence(_lastSignalled);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Nothing may be released while the GPU is still reading it.
        try
        {
            WaitForFence(_lastSignalled);
        }
        catch (SharpGen.Runtime.SharpGenException exception)
        {
            _logger.LogWarning(exception, "The GPU did not go idle cleanly while closing the viewport");
        }

        _disposed = true;

        _scene?.Dispose();
        _uploads.Dispose();
        _edges.Dispose();
        _faces.Dispose();
        _depth.Dispose();
        _fence.Dispose();
        _fenceReached.Dispose();
        _commands.Dispose();

        foreach (ID3D12CommandAllocator allocator in _allocators)
        {
            allocator.Dispose();
        }
    }

    /// <summary>Creates the camera a new viewport starts with.</summary>
    private static Camera CreateCamera()
    {
        Camera camera = new();
        camera.LookFrom(StandardView.Isometric);

        return camera;
    }

    /// <summary>Converts a matrix for upload, preserving element order.</summary>
    /// <remarks>
    /// A straight element-for-element copy, not a change of convention. <see cref="Mat4d"/> is
    /// row-major with translation in the fourth column, <see cref="Matrix4x4"/> stores its fields
    /// in the same row-major order, and the shader declares the constant <c>row_major</c>. All
    /// three agree, so nothing is transposed anywhere; treating this as a conversion between
    /// row-vector and column-vector conventions is what would break it.
    /// </remarks>
    private static Matrix4x4 ToShaderMatrix(Mat4d m) => new(
        (float)m.M11, (float)m.M12, (float)m.M13, (float)m.M14,
        (float)m.M21, (float)m.M22, (float)m.M23, (float)m.M24,
        (float)m.M31, (float)m.M32, (float)m.M33, (float)m.M34,
        (float)m.M41, (float)m.M42, (float)m.M43, (float)m.M44);

    private static Vector3 ToVector3(Vec3d v) => new((float)v.X, (float)v.Y, (float)v.Z);

    /// <summary>Rebuilds the GPU buffers when the snapshot has moved on.</summary>
    private void SyncScene()
    {
        DisplaySnapshot snapshot = _snapshot;

        if (_scene is not null && _scene.Version == snapshot.Version)
        {
            return;
        }

        // Disposed only once the GPU has finished with it. The buffers are still referenced by
        // command lists that may not have retired, and releasing them here would be a
        // use-after-free that surfaces as a device-removed error during the next present.
        WaitForFence(_lastSignalled);

        _scene?.Dispose();
        _scene = SceneGeometry.Upload(_device, snapshot);

        _logger.LogDebug(
            "Uploaded snapshot {Version}: {Bodies} bodies, {Triangles} triangles",
            snapshot.Version,
            _scene.Bodies.Count,
            _scene.TriangleCount);
    }

    /// <summary>Records the face pass for the current scene.</summary>
    private void DrawScene()
    {
        if (_scene is null || _scene.Bodies.Count == 0 || _target.Width <= 0 || _target.Height <= 0)
        {
            return;
        }

        Camera.AspectRatio = (double)_target.Width / _target.Height;

        Bounds3d bounds = _scene.Bounds;
        Vec3d origin = _scene.Origin;

        Mat4d projection = Camera.ProjectionMatrix(bounds);

        // The camera works in world coordinates; the vertex buffers are relative to the snapshot
        // origin. Shifting the camera by the origin rather than the geometry is what keeps the
        // float precision the origin exists to protect -- translating a matrix built around a
        // point a kilometre away would put the large number straight back into the transform.
        Mat4d shiftedView = Mat4d.LookAt(
            Camera.Position - origin, Camera.Target - origin, Camera.Up);

        FrameConstants constants = new()
        {
            ViewProjection = ToShaderMatrix(projection * shiftedView),
            CameraPosition = ToVector3(Camera.Position - origin),
            LightDirection = ToVector3(FacePass.KeyLightDirection(Camera)),
            ViewportSize = new Vector2(_target.Width, _target.Height),
        };

        Span<byte> destination = _uploads.Allocate(FrameConstants.SizeInBytes, out int offset);
        MemoryMarshal.Write(destination, in constants);

        ulong address = _uploads.Resource.GPUVirtualAddress + (ulong)offset;

        // Culled in world space, against a frustum built from the unshifted matrices, because a
        // body's bounds are in world space too.
        Frustum frustum = Frustum.FromViewProjection(projection * Camera.ViewMatrix());

        _faces.Draw(_commands, _scene, address, frustum);

        // Edges last, over the faces they bound. They carry their own depth bias rather than
        // relying on draw order: order alone would put an edge in front of the face behind it as
        // well, so the back of a solid would show its own far edges through the front.
        if (ShowEdges)
        {
            _edges.Draw(_commands, _scene, address, EdgeStyle, frustum);
        }
    }

    /// <summary>Waits for a fence value, returning at once if it has already passed.</summary>
    private void WaitForFence(ulong value)
    {
        if (value == 0 || _fence.CompletedValue >= value)
        {
            return;
        }

        _fence.SetEventOnCompletion(value, _fenceReached);
        _fenceReached.WaitOne();
    }
}
