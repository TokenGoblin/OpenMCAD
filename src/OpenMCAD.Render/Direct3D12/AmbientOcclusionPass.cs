using System.Numerics;
using System.Runtime.InteropServices;

using OpenMCAD.Math;

using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace OpenMCAD.Render.Direct3D12;

/// <summary>How ambient occlusion looks.</summary>
/// <param name="Radius">
/// How far, in metres, a surface can be occluded from. The most important setting by some margin:
/// too small and only the sharpest creases darken, too large and the model turns muddy and the
/// effect reads as dirt rather than as contact.
/// </param>
/// <param name="Intensity">How strongly to darken. One is subtle; three is obvious.</param>
/// <param name="RangeCutoff">
/// Depth difference, in metres, beyond which a sample is a different surface rather than an
/// occluder. Stops a near object darkening the distant background behind it.
/// </param>
/// <param name="Samples">How many directions to test per pixel, up to sixteen.</param>
public readonly record struct OcclusionStyle(
    float Radius, float Intensity, float RangeCutoff, int Samples)
{
    /// <summary>Gets the default settings, relative to a scene of unit size.</summary>
    /// <remarks>
    /// Deliberately restrained. Occlusion is a depth cue, and a CAD viewport where it is the first
    /// thing a user notices is one where it is too strong — the moment it competes with shading for
    /// attention, it stops telling them about the shape and starts telling them about the renderer.
    /// </remarks>
    public static OcclusionStyle Default => new(0.08f, 1.1f, 0.05f, 16);

    /// <summary>Returns this style with its distances scaled to a scene.</summary>
    /// <param name="bounds">The scene to suit. An empty one leaves the style unchanged.</param>
    /// <returns>The adjusted style.</returns>
    /// <remarks>
    /// The radius is in metres, so a fixed one is wrong at every scale but the one it was tuned
    /// at: eighty millimetres darkens the corners of a bracket and does nothing whatever to a
    /// building. It is taken as a fraction of the scene, like the grid spacing and for the same
    /// reason — except that here it is taken from the scene rather than the camera because
    /// occlusion is a property of the geometry, not of where somebody is standing.
    /// </remarks>
    public OcclusionStyle ForScene(Bounds3d bounds)
    {
        if (bounds.IsEmpty)
        {
            return this;
        }

        double extent = bounds.DiagonalLength;

        if (extent <= 0 || !double.IsFinite(extent))
        {
            return this;
        }

        return this with
        {
            Radius = (float)System.Math.Clamp(extent * 0.05, 1e-6, 1e6),
            RangeCutoff = (float)System.Math.Clamp(extent * 0.03, 1e-6, 1e6),
        };
    }
}

/// <summary>The constants the occlusion shader reads.</summary>
/// <remarks>Matches <c>OcclusionConstants</c> in <c>AmbientOcclusion.hlsl</c>.</remarks>
[StructLayout(LayoutKind.Explicit, Size = 160)]
public struct OcclusionConstants
{
    /// <summary>Clip space back to view space, for turning depth into a position.</summary>
    [FieldOffset(0)]
    public Matrix4x4 InverseProjection;

    /// <summary>View space to clip space, for finding where a sampled point landed.</summary>
    [FieldOffset(64)]
    public Matrix4x4 Projection;

    /// <summary>The render target size in physical pixels.</summary>
    [FieldOffset(128)]
    public Vector2 ViewportSize;

    /// <summary>How far a surface can be occluded from, in metres.</summary>
    [FieldOffset(136)]
    public float Radius;

    /// <summary>How strongly to darken.</summary>
    [FieldOffset(140)]
    public float Intensity;

    /// <summary>Depth difference beyond which a sample is a different surface.</summary>
    [FieldOffset(144)]
    public float RangeCutoff;

    /// <summary>How many directions to test per pixel.</summary>
    [FieldOffset(148)]
    public uint SampleCount;

    /// <summary>Gets how many bytes to upload.</summary>
    public static int SizeInBytes => Marshal.SizeOf<OcclusionConstants>();
}

/// <summary>
/// Screen-space ambient occlusion (P2-T12).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is for in a CAD viewport.</b> A pocket, a counterbore, the inside corner where a
/// rib meets a wall — a directional light treats all of them almost exactly as it treats the flat
/// surface beside them, because their normals barely differ. Darkening what is enclosed is the cue
/// the eye actually uses to read depth in a machined part, and no arrangement of lights supplies
/// it.
/// </para>
/// <para>
/// <b>Three passes, at full resolution.</b> Occlusion, then a blur to remove the sampling noise,
/// then a multiply over the image. Half-resolution is the usual economy and it is the wrong one
/// here: the features being darkened are millimetre fillets and chamfer edges, which is exactly
/// the detail a half-resolution buffer loses.
/// </para>
/// <para>
/// <b>It reads the depth buffer and no other input.</b> Normals are reconstructed from depth
/// rather than carried in a G-buffer, because the renderer is forward-shaded and a normal target
/// would cost bandwidth on every frame to save work in a pass that runs once.
/// </para>
/// </remarks>
public sealed class AmbientOcclusionPass : IDisposable
{
    /// <summary>The shader file this pass is built from.</summary>
    public const string ShaderFile = "AmbientOcclusion.hlsl";

    /// <summary>The format occlusion is stored in. One channel, eight bits.</summary>
    /// <remarks>
    /// Occlusion is a single number between nought and one and is about to be blurred, so eight
    /// bits of it is more than the eye can distinguish once it has been multiplied into a shaded
    /// surface.
    /// </remarks>
    public const Format OcclusionFormat = Format.R8_UNorm;

    /// <summary>Where the depth and raw-occlusion pair starts in the shader heap.</summary>
    private const int BlurTable = 0;

    /// <summary>Where the depth and blurred-occlusion pair starts in the shader heap.</summary>
    private const int ApplyTable = 2;

    private readonly ID3D12Device _device;
    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _occlusion;
    private readonly ID3D12PipelineState _blur;
    private readonly ID3D12PipelineState _apply;
    private readonly ID3D12DescriptorHeap _rtvHeap;
    private readonly ID3D12DescriptorHeap _srvHeap;
    private readonly uint _rtvStride;
    private readonly uint _srvStride;

    private ID3D12Resource? _raw;
    private ID3D12Resource? _blurred;
    private bool _disposed;

    /// <summary>Builds the three pipeline states.</summary>
    /// <param name="device">The device to create on.</param>
    /// <param name="renderTargetFormat">The format the apply pass multiplies into.</param>
    /// <param name="optimiseShaders">Whether to compile optimised. Tests turn this off.</param>
    /// <param name="applySampleCount">How many samples the target being darkened has.</param>
    /// <exception cref="ShaderCompilationException">A shader will not compile.</exception>
    public AmbientOcclusionPass(
        ID3D12Device device,
        Format renderTargetFormat = SwapChainTarget.BackBufferFormat,
        bool optimiseShaders = true,
        int applySampleCount = 1)
    {
        ArgumentNullException.ThrowIfNull(device);

        _device = device;
        _rootSignature = CreateRootSignature(device);

        ReadOnlyMemory<byte> vertexShader = ShaderLibrary.Compile(
            ShaderFile, "VSMain", ShaderLibrary.VertexProfile, optimiseShaders);

        // Occlusion and blur both write a single-sampled, single-channel target.
        _occlusion = CreateState(
            device, _rootSignature, vertexShader, "PSOcclusion", OcclusionFormat, 1,
            BlendDescription.Opaque, optimiseShaders);

        _blur = CreateState(
            device, _rootSignature, vertexShader, "PSBlur", OcclusionFormat, 1,
            BlendDescription.Opaque, optimiseShaders);

        // The apply multiplies: destination times source, with the source contributing nothing of
        // its own. That is what darkens the image in place rather than drawing grey over it.
        BlendDescription multiply = BlendDescription.Opaque;

        multiply.RenderTarget[0] = new RenderTargetBlendDescription(
            blendEnable: true,
            logicOpEnable: false,
            srcBlend: Blend.Zero,
            destBlend: Blend.SourceColor,
            blendOp: BlendOperation.Add,
            srcBlendAlpha: Blend.Zero,
            destBlendAlpha: Blend.One,
            blendOpAlpha: BlendOperation.Add,
            logicOp: LogicOp.Noop,
            renderTargetWriteMask: ColorWriteEnable.All);

        _apply = CreateState(
            device, _rootSignature, vertexShader, "PSApply", renderTargetFormat, applySampleCount,
            multiply, optimiseShaders);

        _rtvHeap = device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.RenderTargetView, 2, DescriptorHeapFlags.None));

        // Two tables of two: depth plus raw occlusion for the blur, and depth plus blurred
        // occlusion for the apply. The depth slot is unread by the latter two but has to be
        // populated, because a bound table's descriptors must all be valid.
        _srvHeap = device.CreateDescriptorHeap(new DescriptorHeapDescription(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            4,
            DescriptorHeapFlags.ShaderVisible));

        _rtvHeap.Name = "occlusion render target view heap";
        _srvHeap.Name = "occlusion shader resource view heap";

        _rtvStride = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

        _srvStride = device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
    }

    /// <summary>Gets the current width in pixels, or zero before the first resize.</summary>
    public int Width { get; private set; }

    /// <summary>Gets the current height in pixels, or zero before the first resize.</summary>
    public int Height { get; private set; }

    /// <summary>Gets whether there is anything to render into.</summary>
    public bool IsAllocated => _raw is not null;

    /// <summary>
    /// Reallocates for a new size, and points the descriptors at a depth buffer.
    /// </summary>
    /// <param name="width">The new width in pixels.</param>
    /// <param name="height">The new height in pixels.</param>
    /// <param name="depth">The multisampled depth buffer the scene was drawn against.</param>
    /// <param name="depthSamples">How many samples that buffer has.</param>
    public void Resize(int width, int height, ID3D12Resource depth, int depthSamples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(depth);

        if (width != Width || height != Height || _raw is null)
        {
            Invalidate();

            _raw = CreateOcclusionTexture(width, height, "occlusion (raw)");
            _blurred = CreateOcclusionTexture(width, height, "occlusion (blurred)");

            _device.CreateRenderTargetView(_raw, null, RenderTargetSlot(0));
            _device.CreateRenderTargetView(_blurred, null, RenderTargetSlot(1));

            Width = width;
            Height = height;
        }

        // Rebuilt every time, because the depth buffer is reallocated on resize and on device
        // loss, and a descriptor pointing at a released texture is a use-after-free the GPU
        // discovers rather than the CPU.
        // A depth format cannot be read directly; the typeless view of it can. D32_Float reads as
        // R32_Float, and a multisampled resource needs a multisampled view.
        ShaderResourceViewDescription depthView = new()
        {
            Format = Format.R32_Float,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            ViewDimension = depthSamples > 1
                ? Vortice.Direct3D12.ShaderResourceViewDimension.Texture2DMultisampled
                : Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
        };

        if (depthSamples <= 1)
        {
            depthView.Texture2D = new Texture2DShaderResourceView { MipLevels = 1 };
        }

        // Two pairs, because a descriptor table is bound as a contiguous run and the two draws
        // that use one read a different second texture: the blur reads the raw occlusion, the apply
        // reads the blurred. The depth is duplicated rather than shared so each pair is contiguous.
        _device.CreateShaderResourceView(depth, depthView, ShaderSlot(BlurTable));
        _device.CreateShaderResourceView(_raw, null, ShaderSlot(BlurTable + 1));

        _device.CreateShaderResourceView(depth, depthView, ShaderSlot(ApplyTable));
        _device.CreateShaderResourceView(_blurred, null, ShaderSlot(ApplyTable + 1));
    }

    /// <summary>
    /// Computes occlusion and blurs it, leaving the result ready to apply.
    /// </summary>
    /// <param name="commands">An open command list.</param>
    /// <param name="constantBufferAddress">Where the caller wrote an <see cref="OcclusionConstants"/>.</param>
    /// <remarks>
    /// The depth buffer must already be readable by a shader, and is left that way. Moving it back
    /// to being written is the caller's business, because only the caller knows what still has to
    /// depth-test afterwards.
    /// </remarks>
    public void Compute(ID3D12GraphicsCommandList commands, ulong constantBufferAddress)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(commands);

        if (_raw is null || _blurred is null)
        {
            throw new InvalidOperationException(
                "The occlusion pass has no size yet. Resize it before using it.");
        }

        commands.SetDescriptorHeaps(_srvHeap);
        commands.SetGraphicsRootSignature(_rootSignature);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.RSSetViewport(0, 0, Width, Height);
        commands.RSSetScissorRect(Width, Height);

        // Occlusion, from depth.
        commands.ResourceBarrierTransition(
            _raw, ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);

        commands.OMSetRenderTargets(RenderTargetSlot(0), null);
        commands.SetPipelineState(_occlusion);
        commands.SetGraphicsRootConstantBufferView(0, constantBufferAddress);
        commands.SetGraphicsRootDescriptorTable(1, ShaderTable(BlurTable));
        commands.DrawInstanced(3, 1, 0, 0);

        commands.ResourceBarrierTransition(
            _raw, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);

        // Blur, from that.
        commands.ResourceBarrierTransition(
            _blurred, ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);

        commands.OMSetRenderTargets(RenderTargetSlot(1), null);
        commands.SetPipelineState(_blur);
        commands.SetGraphicsRootDescriptorTable(1, ShaderTable(BlurTable));
        commands.DrawInstanced(3, 1, 0, 0);

        commands.ResourceBarrierTransition(
            _blurred, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
    }

    /// <summary>
    /// Multiplies the blurred occlusion over whatever is currently bound.
    /// </summary>
    /// <param name="commands">An open command list with the colour target bound.</param>
    /// <param name="constantBufferAddress">The same constants the computation used.</param>
    public void Apply(ID3D12GraphicsCommandList commands, ulong constantBufferAddress)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(commands);

        commands.SetDescriptorHeaps(_srvHeap);
        commands.SetGraphicsRootSignature(_rootSignature);
        commands.SetPipelineState(_apply);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.SetGraphicsRootConstantBufferView(0, constantBufferAddress);
        commands.SetGraphicsRootDescriptorTable(1, ShaderTable(ApplyTable));
        commands.DrawInstanced(3, 1, 0, 0);
    }

    /// <summary>Builds the constants for a camera.</summary>
    /// <param name="camera">The camera being rendered through.</param>
    /// <param name="sceneBounds">The scene, for the projection's depth range.</param>
    /// <param name="width">Viewport width in physical pixels.</param>
    /// <param name="height">Viewport height in physical pixels.</param>
    /// <param name="style">How strong the effect should be.</param>
    /// <returns>The constants to upload.</returns>
    /// <remarks>
    /// The projection alone, without the view. Occlusion is computed entirely in view space, where
    /// the camera is at the origin looking down its own axis — which means none of this depends on
    /// where the camera is in the world, and the large numbers that the snapshot origin exists to
    /// keep out of the arithmetic never enter it.
    /// </remarks>
    public static OcclusionConstants ConstantsFor(
        Camera camera, Bounds3d sceneBounds, int width, int height, OcclusionStyle style)
    {
        ArgumentNullException.ThrowIfNull(camera);

        Mat4d projection = camera.ProjectionMatrix(sceneBounds);
        Mat4d inverse = projection.Inverted();

        return new OcclusionConstants
        {
            InverseProjection = ToMatrix(inverse),
            Projection = ToMatrix(projection),
            ViewportSize = new Vector2(width, height),
            Radius = style.Radius,
            Intensity = style.Intensity,
            RangeCutoff = style.RangeCutoff,
            SampleCount = (uint)System.Math.Clamp(style.Samples, 1, 16),
        };
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
        _apply.Dispose();
        _blur.Dispose();
        _occlusion.Dispose();
        _rootSignature.Dispose();
    }

    private static Matrix4x4 ToMatrix(Mat4d m) => new(
        (float)m.M11, (float)m.M12, (float)m.M13, (float)m.M14,
        (float)m.M21, (float)m.M22, (float)m.M23, (float)m.M24,
        (float)m.M31, (float)m.M32, (float)m.M33, (float)m.M34,
        (float)m.M41, (float)m.M42, (float)m.M43, (float)m.M44);

    /// <summary>Builds one of the three pipeline states.</summary>
    /// <remarks>
    /// The root signature is passed in rather than created here. Creating one per state is the
    /// easy mistake -- they are identical, so nothing misbehaves -- and it silently leaks two COM
    /// objects for the life of the process, because only the one the field holds is ever disposed.
    /// </remarks>
    private static ID3D12PipelineState CreateState(
        ID3D12Device device,
        ID3D12RootSignature rootSignature,
        ReadOnlyMemory<byte> vertexShader,
        string pixelEntry,
        Format format,
        int samples,
        BlendDescription blend,
        bool optimise)
    {
        ID3D12PipelineState state = device.CreateGraphicsPipelineState(
            new GraphicsPipelineStateDescription
            {
                RootSignature = rootSignature,
                VertexShader = vertexShader,

                PixelShader = ShaderLibrary.Compile(
                    ShaderFile, pixelEntry, ShaderLibrary.PixelProfile, optimise),

                InputLayout = new InputLayoutDescription(),
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RasterizerState = RasterizerDescription.CullNone,
                BlendState = blend,
                DepthStencilState = DepthStencilDescription.None,
                RenderTargetFormats = [format],
                SampleDescription = new SampleDescription((uint)samples, 0),
                SampleMask = uint.MaxValue,
            });

        state.Name = $"ambient occlusion ({pixelEntry})";
        return state;
    }

    private static ID3D12RootSignature CreateRootSignature(ID3D12Device device)
    {
        RootSignatureDescription1 description = new(
            RootSignatureFlags.None,
            [
                new RootParameter1(
                    RootParameterType.ConstantBufferView,
                    new RootDescriptor1(0, 0, RootDescriptorFlags.DataStatic),
                    ShaderVisibility.Pixel),
                new RootParameter1(
                    new RootDescriptorTable1(
                        new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 2, 0)),
                    ShaderVisibility.Pixel),
            ],
            []);

        ID3D12RootSignature signature = device.CreateRootSignature(in description);
        signature.Name = "ambient occlusion root signature";

        return signature;
    }

    /// <summary>One render target view, by slot.</summary>
    /// <remarks>
    /// Every handle is measured from the heap start rather than walked on from the last one.
    /// Vortice follows the CD3DX12 convention in which <c>Offset</c> mutates the handle it is
    /// called on and returns it, so a chain of them accumulates: writing slots 0, 1, 2 and 3 as
    /// four successive <c>Offset(n)</c> calls on one handle actually writes 0, 1, 3 and 6. Nothing
    /// reports that — the descriptors are created somewhere else, possibly past the end of the
    /// heap, and each draw reads whatever happens to be at the slot it binds.
    /// </remarks>
    private CpuDescriptorHandle RenderTargetSlot(int index)
        => _rtvHeap.GetCPUDescriptorHandleForHeapStart() + (index * (int)_rtvStride);

    /// <summary>One shader resource view, by slot.</summary>
    private CpuDescriptorHandle ShaderSlot(int index)
        => _srvHeap.GetCPUDescriptorHandleForHeapStart() + (index * (int)_srvStride);

    /// <summary>Where a descriptor table starts on the GPU, by slot.</summary>
    private GpuDescriptorHandle ShaderTable(int index)
        => _srvHeap.GetGPUDescriptorHandleForHeapStart() + (index * (int)_srvStride);

    private ID3D12Resource CreateOcclusionTexture(int width, int height, string name)
    {
        ID3D12Resource texture = _device.CreateCommittedResource(
            HeapType.Default,
            HeapFlags.None,
            ResourceDescription.Texture2D(
                OcclusionFormat,
                (uint)width,
                (uint)height,
                arraySize: 1,
                mipLevels: 1,
                sampleCount: 1,
                sampleQuality: 0,
                flags: ResourceFlags.AllowRenderTarget),
            ResourceStates.PixelShaderResource,
            new ClearValue(OcclusionFormat, new Color4(1, 1, 1, 1)));

        texture.Name = name;
        return texture;
    }

    private void Invalidate()
    {
        _raw?.Dispose();
        _blurred?.Dispose();
        _raw = null;
        _blurred = null;

        Width = 0;
        Height = 0;
    }
}
