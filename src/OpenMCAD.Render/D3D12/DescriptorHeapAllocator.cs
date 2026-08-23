using Vortice.Direct3D12;

namespace OpenMCAD.Render.Direct3D12;

/// <summary>
/// Hands out descriptors from a fixed D3D12 heap, and takes them back (P2-T01).
/// </summary>
/// <remarks>
/// <para>
/// A descriptor heap is a fixed-size array of slots allocated once; D3D12 offers no growth and no
/// allocator. Every renderer therefore writes this, and the interesting part is not the bump
/// pointer but the recycling: a viewport that creates and destroys views as bodies come and go
/// will exhaust any heap that only ever moves forward.
/// </para>
/// <para>
/// Freed slots are reused before new ones are taken, so a scene that repeatedly replaces its
/// bodies settles at the high-water mark rather than climbing. The free list is LIFO because a
/// just-freed slot is the one most likely to still be in cache, and because the order is otherwise
/// arbitrary and LIFO is the cheapest.
/// </para>
/// <para>
/// <b>Not thread-safe, and not a candidate for it.</b> Descriptors are allocated while building a
/// frame, which happens on the render thread (PLAN.md 4.2). A lock here would suggest otherwise.
/// </para>
/// </remarks>
public sealed class DescriptorHeapAllocator : IDisposable
{
    private readonly ID3D12DescriptorHeap _heap;
    private readonly int _descriptorSize;
    private readonly Stack<int> _free = new();
    private readonly CpuDescriptorHandle _cpuStart;
    private readonly GpuDescriptorHandle _gpuStart;
    private readonly bool _shaderVisible;

    private int _highWater;
    private bool _disposed;

    /// <summary>Creates an allocator over a newly created heap.</summary>
    /// <param name="device">The device to create the heap on.</param>
    /// <param name="kind">What sort of descriptors the heap holds.</param>
    /// <param name="capacity">How many descriptors it can hold.</param>
    /// <param name="shaderVisible">
    /// Whether shaders can read from it. Only CBV/SRV/UAV and sampler heaps may be, and a
    /// shader-visible heap is a scarcer resource, so it is asked for rather than assumed.
    /// </param>
    /// <param name="name">A debug name.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not positive.</exception>
    public DescriptorHeapAllocator(
        ID3D12Device device,
        DescriptorHeapType kind,
        int capacity,
        bool shaderVisible,
        string name)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        Capacity = capacity;
        _shaderVisible = shaderVisible;

        _heap = device.CreateDescriptorHeap(new DescriptorHeapDescription(
            kind,
            (uint)capacity,
            shaderVisible ? DescriptorHeapFlags.ShaderVisible : DescriptorHeapFlags.None));

        _heap.Name = name;

        _descriptorSize = (int)device.GetDescriptorHandleIncrementSize(kind);
        _cpuStart = _heap.GetCPUDescriptorHandleForHeapStart();

        // Only a shader-visible heap has GPU handles. Asking for one otherwise is a debug-layer
        // error, so the default is left alone rather than queried.
        _gpuStart = shaderVisible ? _heap.GetGPUDescriptorHandleForHeapStart() : default;
    }

    /// <summary>Gets how many descriptors the heap holds.</summary>
    public int Capacity { get; }

    /// <summary>Gets how many are currently allocated.</summary>
    public int Allocated => _highWater - _free.Count;

    /// <summary>Gets the underlying heap, for binding.</summary>
    public ID3D12DescriptorHeap Heap => _heap;

    /// <summary>Takes a descriptor slot.</summary>
    /// <returns>Its index.</returns>
    /// <exception cref="InvalidOperationException">The heap is full.</exception>
    public int Allocate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_free.Count > 0)
        {
            return _free.Pop();
        }

        if (_highWater >= Capacity)
        {
            // Naming the capacity matters: the caller cannot grow a D3D12 heap, so the only
            // remedies are a bigger heap at creation or releasing descriptors that are no longer
            // needed, and the number is what tells them which.
            throw new InvalidOperationException(
                $"The descriptor heap '{_heap.Name}' is full at {Capacity} descriptors. A D3D12 "
                + "heap cannot grow, so either create it larger or release descriptors that are "
                + "no longer in use.");
        }

        return _highWater++;
    }

    /// <summary>Returns a descriptor slot for reuse.</summary>
    /// <param name="index">The index returned by <see cref="Allocate"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The index was never allocated.</exception>
    /// <remarks>
    /// The caller must be certain no frame still in flight refers to it. Freeing a descriptor the
    /// GPU is about to read is not diagnosable after the fact: the slot is simply overwritten and
    /// something is drawn with the wrong resource.
    /// </remarks>
    public void Free(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _highWater);

        _free.Push(index);
    }

    /// <summary>Gets the CPU handle of a slot.</summary>
    /// <param name="index">The slot.</param>
    /// <returns>The handle.</returns>
    public CpuDescriptorHandle CpuHandle(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Capacity);

        return _cpuStart + (index * _descriptorSize);
    }

    /// <summary>Gets the GPU handle of a slot.</summary>
    /// <param name="index">The slot.</param>
    /// <returns>The handle.</returns>
    /// <exception cref="InvalidOperationException">The heap is not shader-visible.</exception>
    public GpuDescriptorHandle GpuHandle(int index)
    {
        if (!_shaderVisible)
        {
            throw new InvalidOperationException(
                $"The descriptor heap '{_heap.Name}' is not shader-visible, so it has no GPU "
                + "handles. Create it with shaderVisible: true if shaders must read from it.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Capacity);

        return _gpuStart + (index * _descriptorSize);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _heap.Dispose();
    }
}
