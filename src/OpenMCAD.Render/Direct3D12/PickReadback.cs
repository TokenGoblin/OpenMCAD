using Vortice.Direct3D12;
using Vortice.Mathematics;

namespace OpenMCAD.Render.Direct3D12;

/// <summary>Somewhere the user asked what is under the cursor.</summary>
/// <param name="X">Column in physical pixels, from the left of the viewport.</param>
/// <param name="Y">Row in physical pixels, from the top of the viewport.</param>
/// <param name="SnapshotVersion">Which snapshot was on screen when they asked.</param>
public readonly record struct PickRequest(int X, int Y, long SnapshotVersion);

/// <summary>
/// A small square of the ID buffer, read back from the GPU.
/// </summary>
/// <param name="Request">What was asked.</param>
/// <param name="Ids">Row-major ids, <paramref name="Width"/> per row.</param>
/// <param name="Width">How many columns were captured.</param>
/// <param name="Height">How many rows were captured.</param>
/// <param name="CentreX">Which column holds the requested pixel.</param>
/// <param name="CentreY">Which row holds the requested pixel.</param>
/// <remarks>
/// A window rather than a single pixel, because an edge a pixel and a half wide is almost
/// impossible to hit dead-on with a mouse. Reading a neighbourhood costs the same round trip and
/// lets <see cref="PickResolver"/> prefer a nearby edge over the face behind it — which is what
/// makes thin things feel selectable rather than fiddly.
/// </remarks>
public readonly record struct PickSample(
    PickRequest Request,
    uint[] Ids,
    int Width,
    int Height,
    int CentreX,
    int CentreY)
{
    /// <summary>Gets the id at an offset from the requested pixel.</summary>
    /// <param name="dx">Columns right of the centre.</param>
    /// <param name="dy">Rows below the centre.</param>
    /// <returns>The id, or <see cref="DisplayId.None"/> outside the window.</returns>
    public DisplayId At(int dx, int dy)
    {
        int x = CentreX + dx;
        int y = CentreY + dy;

        return x < 0 || y < 0 || x >= Width || y >= Height
            ? DisplayId.None
            : new DisplayId(Ids[(y * Width) + x]);
    }

    /// <summary>Gets the id exactly under the cursor.</summary>
    public DisplayId Centre => At(0, 0);
}

/// <summary>
/// Copies squares of the ID buffer back to the CPU without ever blocking on the GPU (P2-T07).
/// </summary>
/// <remarks>
/// <para>
/// <b>Reading a GPU resource is the classic place to lose a frame.</b> Map it in the same frame it
/// was written and the CPU waits for the whole pipeline to drain — at sixty frames a second that
/// is a stall long enough to feel, on every mouse move. So a pick is submitted, tagged with the
/// fence value that will mark it done, and collected some frames later when that value has
/// passed. Nothing waits.
/// </para>
/// <para>
/// The cost is latency: an answer describes where the cursor was two or three frames ago. For
/// hover highlighting that is invisible. For a click it matters not at all, because the request
/// carries the coordinates and the snapshot version it was made against, so the answer is
/// interpreted against the world as it was when the user clicked rather than as it is when the
/// reply arrives.
/// </para>
/// <para>
/// When every slot is busy the request is <b>dropped rather than queued</b>. A queue would build a
/// backlog of stale hover positions during a drag and answer each of them in turn, arriving at the
/// current one last; dropping means the next request is always the newest.
/// </para>
/// </remarks>
public sealed class PickReadback : IDisposable
{
    /// <summary>How wide a square is read back, in pixels.</summary>
    public const int DefaultWindow = 15;

    /// <summary>How many picks may be in flight.</summary>
    /// <remarks>
    /// Three, matching the frames in flight. Fewer would drop requests while the pipeline is full;
    /// more would only add latency to answers nobody is waiting for.
    /// </remarks>
    public const int DefaultSlots = 3;

    private readonly Slot[] _slots;
    private readonly int _window;
    private readonly int _rowPitch;

    private bool _disposed;

    /// <summary>Creates the queue.</summary>
    /// <param name="device">The device to allocate readback buffers on.</param>
    /// <param name="window">How wide a square to read. Forced odd so there is a centre pixel.</param>
    /// <param name="slots">How many picks may be in flight.</param>
    public PickReadback(ID3D12Device device, int window = DefaultWindow, int slots = DefaultSlots)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(window);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slots);

        // An even window has no middle pixel, and the requested one would sit half a pixel off
        // centre -- which biases every proximity search in one direction.
        _window = window % 2 == 0 ? window + 1 : window;

        // A texture copied into a buffer has its rows padded to 256 bytes.
        int unaligned = _window * SceneGeometry.IdStride;
        _rowPitch = (unaligned + 255) & ~255;

        _slots = new Slot[slots];

        for (int i = 0; i < slots; ++i)
        {
            ID3D12Resource buffer = device.CreateCommittedResource(
                HeapType.Readback,
                HeapFlags.None,
                ResourceDescription.Buffer((ulong)(_rowPitch * _window)),
                ResourceStates.CopyDest);

            buffer.Name = $"pick readback {i}";
            _slots[i] = new Slot(buffer);
        }
    }

    /// <summary>Gets the width of the square read back.</summary>
    public int Window => _window;

    /// <summary>Gets how many picks are waiting on the GPU.</summary>
    public int InFlight
    {
        get
        {
            int count = 0;

            foreach (Slot slot in _slots)
            {
                if (slot.Busy)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Gets how many requests have been dropped because every slot was busy.</summary>
    public int Dropped { get; private set; }

    /// <summary>
    /// Records a copy of the ID buffer around a point.
    /// </summary>
    /// <param name="commands">An open command list.</param>
    /// <param name="target">The ID buffer to read, in its resting state.</param>
    /// <param name="request">Where to look.</param>
    /// <param name="fenceValueWhenDone">The fence value that will signal this copy has completed.</param>
    /// <returns>
    /// <see langword="false"/> if the point is off the target or every slot is busy, in which case
    /// nothing was recorded.
    /// </returns>
    public bool TrySubmit(
        ID3D12GraphicsCommandList commands,
        IdTarget target,
        PickRequest request,
        ulong fenceValueWhenDone)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(target);

        if (!target.IsAllocated
            || request.X < 0 || request.Y < 0
            || request.X >= target.Width || request.Y >= target.Height)
        {
            return false;
        }

        int index = FreeSlot();

        if (index < 0)
        {
            Dropped++;
            return false;
        }

        int half = _window / 2;

        // Clamped to the target, so a pick near an edge of the viewport reads a smaller window
        // rather than reading out of bounds -- and the centre moves within it accordingly.
        int left = System.Math.Max(request.X - half, 0);
        int top = System.Math.Max(request.Y - half, 0);
        int right = System.Math.Min(request.X + half + 1, target.Width);
        int bottom = System.Math.Min(request.Y + half + 1, target.Height);

        Slot slot = _slots[index];
        slot.Busy = true;
        slot.Fence = fenceValueWhenDone;
        slot.Request = request;
        slot.Width = right - left;
        slot.Height = bottom - top;
        slot.CentreX = request.X - left;
        slot.CentreY = request.Y - top;

        commands.ResourceBarrierTransition(
            target.Resource, IdTarget.RestingState, ResourceStates.CopySource);

        PlacedSubresourceFootPrint footprint = new()
        {
            Offset = 0,
            Footprint = new SubresourceFootPrint(
                IdTarget.IdFormat, (uint)slot.Width, (uint)slot.Height, 1, (uint)_rowPitch),
        };

        commands.CopyTextureRegion(
            new TextureCopyLocation(slot.Buffer, footprint),
            0,
            0,
            0,
            new TextureCopyLocation(target.Resource, 0),
            new Box(left, top, 0, right, bottom, 1));

        commands.ResourceBarrierTransition(
            target.Resource, ResourceStates.CopySource, IdTarget.RestingState);

        return true;
    }

    /// <summary>
    /// Takes the oldest pick the GPU has finished with.
    /// </summary>
    /// <param name="completedFenceValue">The newest fence value the GPU has signalled.</param>
    /// <param name="sample">The ids read back.</param>
    /// <returns>Whether there was one ready.</returns>
    public bool TryCollect(ulong completedFenceValue, out PickSample sample)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int oldest = -1;

        foreach (Slot slot in _slots)
        {
            if (slot.Busy && slot.Fence <= completedFenceValue
                && (oldest < 0 || slot.Fence < _slots[oldest].Fence))
            {
                oldest = Array.IndexOf(_slots, slot);
            }
        }

        if (oldest < 0)
        {
            sample = default;
            return false;
        }

        Slot ready = _slots[oldest];
        uint[] ids = new uint[ready.Width * ready.Height];

        Span<byte> mapped = ready.Buffer.Map<byte>(0, _rowPitch * ready.Height);

        try
        {
            for (int y = 0; y < ready.Height; ++y)
            {
                Span<byte> row = mapped.Slice(y * _rowPitch, ready.Width * SceneGeometry.IdStride);

                System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(row)
                    .CopyTo(ids.AsSpan(y * ready.Width, ready.Width));
            }
        }
        finally
        {
            ready.Buffer.Unmap(0);
        }

        sample = new PickSample(
            ready.Request, ids, ready.Width, ready.Height, ready.CentreX, ready.CentreY);

        ready.Busy = false;
        return true;
    }

    /// <summary>Abandons every pick in flight.</summary>
    /// <remarks>
    /// For a resize or a device reset, after which the fence values these are waiting on may never
    /// arrive and the ID buffer they refer to no longer exists.
    /// </remarks>
    public void Abandon()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (Slot slot in _slots)
        {
            slot.Busy = false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (Slot slot in _slots)
        {
            slot.Buffer.Dispose();
        }
    }

    private int FreeSlot()
    {
        for (int i = 0; i < _slots.Length; ++i)
        {
            if (!_slots[i].Busy)
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class Slot(ID3D12Resource buffer)
    {
        public ID3D12Resource Buffer { get; } = buffer;

        public bool Busy { get; set; }

        public ulong Fence { get; set; }

        public PickRequest Request { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public int CentreX { get; set; }

        public int CentreY { get; set; }
    }
}
