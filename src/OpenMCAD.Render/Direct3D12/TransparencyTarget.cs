using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace OpenMCAD.Render.Direct3D12;

/// <summary>
/// The two buffers weighted-blended transparency accumulates into (P2-T10).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why order-independent transparency at all.</b> The usual answer to transparency is to sort
/// the geometry back to front, which fails on exactly the things CAD produces: a housing that
/// contains its own contents, two parts that interpenetrate, a single body whose faces overlap
/// from the current angle. Sorting is per object, the failure is per pixel, and no ordering of
/// objects fixes an object that overlaps itself. The visible symptom is faces popping in front of
/// one another as the camera turns, which reads as the model changing rather than as the renderer
/// approximating.
/// </para>
/// <para>
/// <b>How weighted blending avoids it.</b> Every transparent fragment is accumulated with a weight
/// that falls off with depth, and separately the product of what each one lets through is kept.
/// Both operations are commutative, so the result does not depend on the order fragments arrive
/// in. It is an approximation — a near fragment and a far one at the same weight blend more evenly
/// than they should — and the approximation is uniform and stable, which is worth far more than
/// occasional exactness that flips as the camera moves.
/// </para>
/// <para>
/// <b>Accumulation is 16-bit float, and that is not negotiable.</b> The weights span several
/// orders of magnitude by design, so an 8-bit target saturates on the near fragments and rounds
/// the far ones to nothing.
/// </para>
/// </remarks>
public sealed class TransparencyTarget : IDisposable
{
    /// <summary>Where weighted colour accumulates. Four half floats.</summary>
    public const Format AccumulationFormat = Format.R16G16B16A16_Float;

    /// <summary>How much light gets through. One half float.</summary>
    /// <remarks>
    /// A single channel, holding the running product of one minus each fragment's alpha. It starts
    /// at one — everything visible — which is why it is cleared to white rather than to black.
    /// </remarks>
    public const Format RevealageFormat = Format.R16_Float;

    private readonly ID3D12Device _device;
    private readonly ID3D12DescriptorHeap _rtvHeap;
    private readonly ID3D12DescriptorHeap _srvHeap;
    private readonly uint _rtvStride;
    private readonly uint _srvStride;
    private readonly int _sampleCount;

    private ID3D12Resource? _accumulation;
    private ID3D12Resource? _revealage;
    private bool _disposed;

    /// <summary>Creates the target.</summary>
    /// <param name="device">The device to allocate on.</param>
    /// <param name="sampleCount">
    /// How many samples per pixel, matching the target the opaque pass drew into. The transparent
    /// pass depth-tests against that pass's depth buffer, and a depth buffer cannot be shared
    /// between resources of different sample counts.
    /// </param>
    public TransparencyTarget(ID3D12Device device, int sampleCount)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);

        _device = device;
        _sampleCount = sampleCount;

        _rtvHeap = device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.RenderTargetView, 2, DescriptorHeapFlags.None));

        // Shader-visible, because the composite reads both as textures rather than resolving them
        // first. This is the one place in the renderer that needs a descriptor heap: a root
        // descriptor can address a buffer directly but not a texture.
        // Four slots, of which two are ever populated. The composite shader declares both a
        // Texture2D and a Texture2DMS pair, because HLSL cannot choose between resource types at
        // run time, and reads whichever matches the sample count. A multisampled resource cannot
        // be given a non-multisampled view, so the pair that is not read is simply left empty --
        // which is safe only because the branch guarantees it is never sampled.
        _srvHeap = device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            4,
            DescriptorHeapFlags.ShaderVisible));

        _rtvHeap.Name = "transparency render target view heap";
        _srvHeap.Name = "transparency shader resource view heap";

        _rtvStride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

        _srvStride = device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
    }

    /// <summary>Gets the current width in pixels, or zero before the first resize.</summary>
    public int Width { get; private set; }

    /// <summary>Gets the current height in pixels, or zero before the first resize.</summary>
    public int Height { get; private set; }

    /// <summary>Gets whether there is anything to render into.</summary>
    public bool IsAllocated => _accumulation is not null;

    /// <summary>Gets how many samples per pixel these carry.</summary>
    public int SampleCount => _sampleCount;

    /// <summary>Gets the shader-visible heap the composite binds.</summary>
    public ID3D12DescriptorHeap ShaderHeap => _srvHeap;

    /// <summary>Gets where the composite reads both textures from.</summary>
    /// <exception cref="InvalidOperationException">Nothing has been allocated yet.</exception>
    public GpuDescriptorHandle ShaderTable
        => _accumulation is null
            ? throw new InvalidOperationException(
                "The transparency target has no size yet. Resize it before binding it.")
            : _srvHeap.GetGPUDescriptorHandleForHeapStart();

    /// <summary>Gets the accumulation texture.</summary>
    /// <exception cref="InvalidOperationException">Nothing has been allocated yet.</exception>
    public ID3D12Resource Accumulation
        => _accumulation ?? throw new InvalidOperationException(
            "The transparency target has no size yet. Resize it before using it.");

    /// <summary>Gets the revealage texture.</summary>
    /// <exception cref="InvalidOperationException">Nothing has been allocated yet.</exception>
    public ID3D12Resource Revealage
        => _revealage ?? throw new InvalidOperationException(
            "The transparency target has no size yet. Resize it before using it.");

    /// <summary>Gets the state both textures rest in between frames.</summary>
    public static ResourceStates RestingState => ResourceStates.PixelShaderResource;

    /// <summary>Reallocates for a new size, doing nothing if the size has not changed.</summary>
    /// <param name="width">The new width in pixels.</param>
    /// <param name="height">The new height in pixels.</param>
    public void Resize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (width == Width && height == Height && _accumulation is not null)
        {
            return;
        }

        Invalidate();

        _accumulation = Create(AccumulationFormat, width, height, new Color4(0, 0, 0, 0));
        _accumulation.Name = $"transparency accumulation {width}x{height}";

        // Cleared to one: nothing has been drawn, so everything behind is fully visible.
        _revealage = Create(RevealageFormat, width, height, new Color4(1, 1, 1, 1));
        _revealage.Name = $"transparency revealage {width}x{height}";

        CpuDescriptorHandle rtv = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        _device.CreateRenderTargetView(_accumulation, null, rtv);
        _device.CreateRenderTargetView(_revealage, null, Offset(rtv, _rtvStride));

        // Placed at the slots the shader will actually read: t0 and t1 when there is one sample
        // per pixel, t2 and t3 when there are more. Writing them to t0 and t1 regardless is the
        // obvious mistake, and it produces a composite that reads zeros -- which is not a blank
        // result but a black one, laid over the entire viewport, because zero revealage means
        // "nothing got through".
        int slot = _sampleCount > 1 ? 2 : 0;

        CpuDescriptorHandle srv = At(_srvHeap.GetCPUDescriptorHandleForHeapStart(), slot);
        _device.CreateShaderResourceView(_accumulation, null, srv);
        _device.CreateShaderResourceView(_revealage, null, Offset(srv, _srvStride));

        Width = width;
        Height = height;
    }

    /// <summary>Gets the two render target views, in the order the shader writes them.</summary>
    /// <returns>Accumulation first, revealage second.</returns>
    /// <exception cref="InvalidOperationException">Nothing has been allocated yet.</exception>
    public CpuDescriptorHandle[] RenderTargetViews()
    {
        if (_accumulation is null)
        {
            throw new InvalidOperationException(
                "The transparency target has no size yet. Resize it before binding it.");
        }

        CpuDescriptorHandle first = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        return [first, Offset(first, _rtvStride)];
    }

    /// <summary>Moves both textures between rendering and being read.</summary>
    /// <param name="commands">An open command list.</param>
    /// <param name="from">The state they are in.</param>
    /// <param name="to">The state they should be in.</param>
    public void Transition(
        ID3D12GraphicsCommandList commands, ResourceStates from, ResourceStates to)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(commands);

        if (from == to)
        {
            return;
        }

        commands.ResourceBarrierTransition(Accumulation, from, to);
        commands.ResourceBarrierTransition(Revealage, from, to);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Invalidate();
        _srvHeap.Dispose();
        _rtvHeap.Dispose();
    }

    /// <summary>The descriptor one place along from a heap start.</summary>
    /// <remarks>
    /// The increment is asked of the device rather than assumed: descriptor size is a property of
    /// the hardware and the heap type, not a constant, and hard-coding one produces a heap that
    /// works on the machine it was written on.
    /// </remarks>
    private static CpuDescriptorHandle Offset(CpuDescriptorHandle handle, uint stride)
        => handle.Offset(1, stride);

    /// <summary>The descriptor a given number of places along from a heap start.</summary>
    private CpuDescriptorHandle At(CpuDescriptorHandle start, int index)
        => index == 0 ? start : start.Offset(index, _srvStride);

    private ID3D12Resource Create(Format format, int width, int height, Color4 clear)
        => _device.CreateCommittedResource(
            HeapType.Default,
            HeapFlags.None,
            ResourceDescription.Texture2D(
                format,
                (uint)width,
                (uint)height,
                arraySize: 1,
                mipLevels: 1,
                sampleCount: (uint)_sampleCount,
                sampleQuality: 0,
                flags: ResourceFlags.AllowRenderTarget),
            RestingState,
            new ClearValue(format, clear));

    private void Invalidate()
    {
        _accumulation?.Dispose();
        _revealage?.Dispose();
        _accumulation = null;
        _revealage = null;

        Width = 0;
        Height = 0;
    }
}
