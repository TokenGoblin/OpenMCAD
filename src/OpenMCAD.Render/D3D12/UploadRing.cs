using Vortice.Direct3D12;

namespace OpenMCAD.Render.Direct3D12;

/// <summary>
/// A ring of CPU-writable GPU memory for staging uploads, reclaimed by fence (P2-T01).
/// </summary>
/// <remarks>
/// <para>
/// Getting data to the GPU means writing it somewhere the CPU can write and the GPU can read, then
/// copying. Allocating a staging buffer per upload would mean an allocation and a release every
/// frame; a ring allocates once and hands out slices of it.
/// </para>
/// <para>
/// <b>The hard part is knowing when a slice is free again.</b> The CPU runs ahead of the GPU, so
/// memory written for frame N is still being read while frame N+1 is being recorded. Overwriting
/// it produces geometry that flickers between two states — a bug that reproduces only under load
/// and looks like a driver fault. So each frame's allocations are tagged with that frame's number,
/// and a region is reusable only once the GPU has signalled past it.
/// </para>
/// <para>
/// Allocation is a bump pointer that wraps. Reclamation is one comparison against the completed
/// frame. Neither does any bookkeeping per allocation, which matters because a large assembly
/// makes thousands of them per frame.
/// </para>
/// <para>Not thread-safe: uploads are staged on the render thread (PLAN.md 4.2).</para>
/// </remarks>
public sealed class UploadRing : IDisposable
{
    /// <summary>
    /// D3D12 requires a constant-buffer view to start on a 256-byte boundary, and every
    /// allocation is aligned to it rather than tracking which are constants. The waste is at most
    /// 255 bytes per allocation against a ring measured in megabytes.
    /// </summary>
    public const int Alignment = 256;

    private readonly ID3D12Resource _buffer;
    private readonly nint _mapped;
    private readonly List<Reservation> _reservations = [];

    private int _head;
    private int _tail;
    private long _currentFrame;
    private bool _disposed;

    /// <summary>Creates the ring.</summary>
    /// <param name="device">The device to allocate on.</param>
    /// <param name="byteLength">How large the ring is.</param>
    /// <param name="name">A debug name.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="byteLength"/> is not positive.</exception>
    public unsafe UploadRing(ID3D12Device device, int byteLength, string name)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteLength);

        Capacity = byteLength;

        _buffer = device.CreateCommittedResource(
            HeapType.Upload,
            HeapFlags.None,
            ResourceDescription.Buffer((ulong)byteLength),

            // Upload heaps begin in GenericRead and may never leave it.
            ResourceStates.GenericRead);

        _buffer.Name = name;

        // Mapped once and left mapped for its lifetime. Mapping is not free, and an upload heap
        // is designed to be persistently mapped -- unmapping between writes would cost more than
        // the writes. Nothing here reads it back, so the read range stays the default.
        _mapped = (nint)_buffer.Map<byte>(0);
    }

    /// <summary>Gets the ring's size in bytes.</summary>
    public int Capacity { get; }

    /// <summary>Gets how many bytes are currently reserved and not yet reclaimed.</summary>
    /// <remarks>
    /// The reservation count is what disambiguates a full ring from an empty one. In a ring,
    /// head == tail means both, and reading it as empty is the classic version of this bug: a
    /// ring filled exactly to its capacity reported nothing in use and then handed the same bytes
    /// out again while the GPU was still reading them.
    /// </remarks>
    public int InUse => _reservations.Count == 0
        ? 0
        : _head > _tail ? _head - _tail : Capacity - _tail + _head;

    /// <summary>Gets the underlying resource, to copy from.</summary>
    public ID3D12Resource Resource => _buffer;

    /// <summary>Begins a frame's allocations.</summary>
    /// <param name="frame">A monotonically increasing frame number.</param>
    /// <remarks>
    /// Allocations made after this are tagged with <paramref name="frame"/> and become reclaimable
    /// only once <see cref="Reclaim"/> is told that frame has completed on the GPU.
    /// </remarks>
    public void BeginFrame(long frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (frame < _currentFrame)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frame),
                frame,
                $"Frame numbers must not go backwards; the ring is on frame {_currentFrame}. "
                + "Reclamation compares against them, so a repeated or earlier number would free "
                + "memory the GPU is still reading.");
        }

        _currentFrame = frame;
    }

    /// <summary>Reserves a slice and returns where to write it.</summary>
    /// <param name="byteLength">How many bytes are needed.</param>
    /// <param name="offset">The offset into <see cref="Resource"/> to copy from.</param>
    /// <returns>A span to write the data into.</returns>
    /// <exception cref="InvalidOperationException">
    /// The ring has no contiguous room. The caller should submit and wait, or the ring is too
    /// small for a frame's traffic.
    /// </exception>
    public unsafe Span<byte> Allocate(int byteLength, out int offset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteLength);

        if (byteLength > Capacity)
        {
            throw new InvalidOperationException(
                $"An upload of {byteLength} bytes cannot fit in a ring of {Capacity}. The ring "
                + "must be larger than the largest single upload, because a slice cannot be split.");
        }

        int aligned = (byteLength + Alignment - 1) & ~(Alignment - 1);
        int start = _head;

        // A slice has to be contiguous -- it is copied as one region -- so a request that would
        // run off the end restarts at the beginning rather than wrapping around the seam.
        if (start + aligned > Capacity)
        {
            start = 0;
        }

        if (Overlaps(start, aligned))
        {
            throw new InvalidOperationException(
                $"The upload ring '{_buffer.Name}' has no room for {byteLength} bytes: "
                + $"{InUse} of {Capacity} are still in use by frames the GPU has not finished. "
                + "Submit the pending work and reclaim, or size the ring for a whole frame.");
        }

        _head = start + aligned;
        if (_head == Capacity)
        {
            _head = 0;
        }

        _reservations.Add(new Reservation(_currentFrame, _head));

        offset = start;
        return new Span<byte>((void*)(_mapped + start), byteLength);
    }

    /// <summary>Releases everything used by frames the GPU has finished.</summary>
    /// <param name="completedFrame">The newest frame number the GPU has signalled.</param>
    /// <remarks>
    /// Called once per frame, after reading the fence. Passing a frame the GPU has not actually
    /// reached is the one way to corrupt this ring, and it cannot be detected here.
    /// </remarks>
    public void Reclaim(long completedFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int released = 0;
        while (released < _reservations.Count && _reservations[released].Frame <= completedFrame)
        {
            _tail = _reservations[released].End;
            released++;
        }

        if (released > 0)
        {
            _reservations.RemoveRange(0, released);
        }

        // Nothing outstanding means the ring is entirely free. Saying so explicitly avoids the
        // state where head and tail have drifted apart across a wrap and InUse reports the whole
        // ring as busy when none of it is.
        if (_reservations.Count == 0)
        {
            _tail = _head;
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
        _buffer.Unmap(0);
        _buffer.Dispose();
    }

    /// <summary>
    /// Whether a slice would land on bytes a frame still owns.
    /// </summary>
    /// <param name="start">Where the slice would start. Never wraps: the caller resets to zero.</param>
    /// <param name="length">How long it is.</param>
    /// <returns><see langword="true"/> if it would overwrite live data.</returns>
    /// <remarks>
    /// Stated as an interval intersection rather than as a comparison of head against tail. The
    /// head-and-tail form has to enumerate which of them is ahead and whether the ring has
    /// wrapped, and the version here got the full-ring case wrong in a way no reading caught --
    /// two tests did. The live region is simply <c>InUse</c> bytes beginning at the tail, possibly
    /// crossing the end, and the question is whether the candidate slice touches it.
    /// </remarks>
    private bool Overlaps(int start, int length)
    {
        int used = InUse;
        if (used == 0)
        {
            return false;
        }

        int end = start + length;

        if (_tail + used <= Capacity)
        {
            // The live region is one interval, [_tail, _tail + used).
            return start < _tail + used && _tail < end;
        }

        // It crosses the end, so it is [_tail, Capacity) and [0, _tail + used - Capacity).
        return end > _tail || start < _tail + used - Capacity;
    }

    /// <summary>One frame's claim on the ring, up to <paramref name="End"/>.</summary>
    /// <param name="Frame">The frame that made it.</param>
    /// <param name="End">Where the ring head stood afterwards.</param>
    private readonly record struct Reservation(long Frame, int End);
}
