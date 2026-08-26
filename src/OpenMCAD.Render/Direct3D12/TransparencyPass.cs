using System.Runtime.InteropServices;

using OpenMCAD.Math;

using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace OpenMCAD.Render.Direct3D12;

/// <summary>The constants the composite shader reads.</summary>
/// <remarks>Matches <c>CompositeConstants</c> in <c>Composite.hlsl</c>.</remarks>
[StructLayout(LayoutKind.Explicit, Size = 16)]
public struct CompositeConstants
{
    /// <summary>Non-zero when the accumulation buffers carry more than one sample per pixel.</summary>
    [FieldOffset(0)]
    public uint Multisampled;

    /// <summary>How many samples to average across.</summary>
    [FieldOffset(4)]
    public uint SampleCount;

    /// <summary>Gets how many bytes to upload.</summary>
    public static int SizeInBytes => Marshal.SizeOf<CompositeConstants>();
}

/// <summary>
/// Accumulates transparent geometry, and resolves it over the opaque image (P2-T10).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two passes, and both are needed.</b> The first draws every transparent face into a weighted
/// accumulation buffer and a revealage buffer, in whatever order they happen to arrive. The second
/// turns those into a colour and lays it over what was already drawn. Splitting them is what makes
/// the result independent of order: neither the sum nor the product cares which fragment came
/// first.
/// </para>
/// <para>
/// <b>Depth is tested and not written.</b> Transparent geometry must be hidden by the opaque solid
/// in front of it, and must not hide other transparent geometry behind it — a transparent face
/// that wrote depth would occlude the very fragments the accumulation exists to combine, and the
/// result would depend on draw order again by a different route.
/// </para>
/// <para>
/// <b>Back faces are drawn.</b> A transparent box shows its inside, and culling them would make a
/// hollow object look like a sheet. This is the same reasoning as the opaque pass, and it matters
/// more here because the far side is actually visible.
/// </para>
/// </remarks>
public sealed class TransparencyPass : IDisposable
{
    /// <summary>The shader the accumulation entry point lives in.</summary>
    public const string AccumulateShaderFile = "Surface.hlsl";

    /// <summary>The shader the composite lives in.</summary>
    public const string CompositeShaderFile = "Composite.hlsl";

    private readonly ID3D12RootSignature _accumulateSignature;
    private readonly ID3D12RootSignature _compositeSignature;
    private readonly ID3D12PipelineState _accumulate;
    private readonly ID3D12PipelineState _composite;
    private readonly ID3D12Resource _noHighlights;

    private bool _disposed;

    /// <summary>Builds both pipeline states.</summary>
    /// <param name="device">The device to create on.</param>
    /// <param name="renderTargetFormat">The format the composite writes into.</param>
    /// <param name="depthFormat">The depth format the accumulation tests against.</param>
    /// <param name="optimiseShaders">Whether to compile optimised. Tests turn this off.</param>
    /// <param name="sampleCount">How many samples per pixel both stages work at.</param>
    /// <exception cref="ShaderCompilationException">A shader will not compile.</exception>
    public TransparencyPass(
        ID3D12Device device,
        Format renderTargetFormat = SwapChainTarget.BackBufferFormat,
        Format depthFormat = DepthBuffer.DepthFormat,
        bool optimiseShaders = true,
        int sampleCount = 1)
    {
        ArgumentNullException.ThrowIfNull(device);

        _accumulateSignature = CreateAccumulateSignature(device);
        _compositeSignature = CreateCompositeSignature(device);

        _noHighlights = device.CreateCommittedResource(
            HeapType.Upload,
            HeapFlags.None,
            ResourceDescription.Buffer(SceneGeometry.IdStride),
            ResourceStates.GenericRead);

        _noHighlights.Name = "no highlight states (transparency)";

        _accumulate = device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = _accumulateSignature,

            // The same vertex shader the opaque and ID passes use. A transparent face has to
            // occupy exactly the pixels its opaque version would, or a body fading in and out
            // would appear to change shape while it did so.
            VertexShader = ShaderLibrary.Compile(
                AccumulateShaderFile, "VSMain", ShaderLibrary.VertexProfile, optimiseShaders),

            PixelShader = ShaderLibrary.Compile(
                AccumulateShaderFile, "PSMainTransparent", ShaderLibrary.PixelProfile, optimiseShaders),

            InputLayout = new InputLayoutDescription(
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 0, 1)),

            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = AccumulationBlend,

            // Tested, not written. Transparent geometry must be hidden by the solid in front of it
            // and must not hide other transparent geometry behind it.
            DepthStencilState = DepthStencilDescription.Read,
            DepthStencilFormat = depthFormat,
            RenderTargetFormats =
            [
                TransparencyTarget.AccumulationFormat,
                TransparencyTarget.RevealageFormat,
            ],
            SampleDescription = new SampleDescription((uint)sampleCount, 0),
            SampleMask = uint.MaxValue,
        });

        _accumulate.Name = "transparency accumulation";

        _composite = device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = _compositeSignature,

            VertexShader = ShaderLibrary.Compile(
                CompositeShaderFile, "VSMain", ShaderLibrary.VertexProfile, optimiseShaders),

            PixelShader = ShaderLibrary.Compile(
                CompositeShaderFile, "PSMain", ShaderLibrary.PixelProfile, optimiseShaders),

            InputLayout = new InputLayoutDescription(),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RasterizerState = RasterizerDescription.CullNone,

            // Premultiplied: the shader multiplies colour by coverage, so the source is added
            // whole and the destination is scaled by what got through.
            BlendState = BlendDescription.AlphaBlend,
            DepthStencilState = DepthStencilDescription.None,
            DepthStencilFormat = depthFormat,
            RenderTargetFormats = [renderTargetFormat],
            SampleDescription = new SampleDescription((uint)sampleCount, 0),
            SampleMask = uint.MaxValue,
        });

        _composite.Name = "transparency composite";
    }

    /// <summary>Gets how many bodies the last accumulation drew.</summary>
    public int BodiesDrawn { get; private set; }

    /// <summary>Gets or sets how the transparent surfaces respond to the light.</summary>
    /// <remarks>Kept in step with the shaded pass, so a body does not change material when it
    /// becomes transparent.</remarks>
    public SurfaceMaterial Material { get; set; } = SurfaceMaterial.Default;

    /// <summary>Accumulates a scene's transparent faces.</summary>
    /// <param name="commands">An open command list with the transparency targets bound.</param>
    /// <param name="scene">The geometry to draw.</param>
    /// <param name="constantBufferAddress">Where the caller wrote a <see cref="FrameConstants"/>.</param>
    /// <param name="colour">The body colour. Its alpha is the transparency.</param>
    /// <param name="frustum">The frustum to cull against, in world space.</param>
    /// <param name="highlightStates">Where the per-entity highlight states live.</param>
    public void Accumulate(
        ID3D12GraphicsCommandList commands,
        SceneGeometry scene,
        ulong constantBufferAddress,
        Color4 colour,
        Frustum? frustum = null,
        ulong highlightStates = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(scene);

        BodiesDrawn = 0;

        if (scene.Bodies.Count == 0)
        {
            return;
        }

        commands.SetGraphicsRootSignature(_accumulateSignature);
        commands.SetPipelineState(_accumulate);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.SetGraphicsRootConstantBufferView(0, constantBufferAddress);
        commands.SetGraphicsRoot32BitConstants(1, BodyConstants.For(colour, Material), 0);

        commands.SetGraphicsRootShaderResourceView(
            3, highlightStates == 0 ? _noHighlights.GPUVirtualAddress : highlightStates);

        foreach (BodyGeometry body in scene.Bodies)
        {
            if (!body.HasFaces || body.TriangleIdAddress == 0)
            {
                continue;
            }

            if (frustum is { } bounds && !bounds.Intersects(body.Bounds))
            {
                continue;
            }

            commands.SetGraphicsRootShaderResourceView(2, body.TriangleIdAddress);
            commands.IASetVertexBuffers(0, body.PositionView);
            commands.IASetVertexBuffers(1, body.NormalView);
            commands.IASetIndexBuffer(body.IndexView);
            commands.DrawIndexedInstanced((uint)body.IndexCount, 1, 0, 0, 0);

            BodiesDrawn++;
        }
    }

    /// <summary>
    /// Resolves the accumulated transparency over whatever is already drawn.
    /// </summary>
    /// <param name="commands">An open command list with the colour target bound.</param>
    /// <param name="target">The buffers the accumulation wrote, already readable.</param>
    /// <param name="constantBufferAddress">Where the caller wrote a <see cref="CompositeConstants"/>.</param>
    public void Composite(
        ID3D12GraphicsCommandList commands, TransparencyTarget target, ulong constantBufferAddress)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(target);

        commands.SetGraphicsRootSignature(_compositeSignature);
        commands.SetPipelineState(_composite);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.SetDescriptorHeaps(target.ShaderHeap);
        commands.SetGraphicsRootConstantBufferView(0, constantBufferAddress);
        commands.SetGraphicsRootDescriptorTable(1, target.ShaderTable);
        commands.DrawInstanced(3, 1, 0, 0);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _noHighlights.Dispose();
        _composite.Dispose();
        _accumulate.Dispose();
        _compositeSignature.Dispose();
        _accumulateSignature.Dispose();
    }

    /// <summary>
    /// Additive on the accumulation, multiplicative on the revealage.
    /// </summary>
    /// <remarks>
    /// The two targets blend differently, which is why independent blending has to be switched on.
    /// Accumulation sums weighted colour; revealage multiplies by one minus each alpha, which is
    /// expressed as a destination blend of <c>InverseSourceColor</c> with the source contributing
    /// nothing. Both operations commute, and that is the entire mechanism by which the result stops
    /// depending on order.
    /// </remarks>
    private static BlendDescription AccumulationBlend
    {
        get
        {
            BlendDescription description = BlendDescription.Opaque;
            description.IndependentBlendEnable = true;

            description.RenderTarget[0] = new RenderTargetBlendDescription(
                blendEnable: true,
                logicOpEnable: false,
                srcBlend: Blend.One,
                destBlend: Blend.One,
                blendOp: BlendOperation.Add,
                srcBlendAlpha: Blend.One,
                destBlendAlpha: Blend.One,
                blendOpAlpha: BlendOperation.Add,
                logicOp: LogicOp.Noop,
                renderTargetWriteMask: ColorWriteEnable.All);

            description.RenderTarget[1] = new RenderTargetBlendDescription(
                blendEnable: true,
                logicOpEnable: false,
                srcBlend: Blend.Zero,
                destBlend: Blend.InverseSourceColor,
                blendOp: BlendOperation.Add,
                srcBlendAlpha: Blend.Zero,
                destBlendAlpha: Blend.InverseSourceAlpha,
                blendOpAlpha: BlendOperation.Add,
                logicOp: LogicOp.Noop,
                renderTargetWriteMask: ColorWriteEnable.Red);

            return description;
        }
    }

    /// <summary>Identical to the opaque pass, because it shares that pass's vertex shader.</summary>
    private static ID3D12RootSignature CreateAccumulateSignature(ID3D12Device device)
    {
        RootSignatureDescription1 description = new(
            RootSignatureFlags.AllowInputAssemblerInputLayout,
            [
                new RootParameter1(
                    RootParameterType.ConstantBufferView,
                    new RootDescriptor1(0, 0, RootDescriptorFlags.DataStatic),
                    ShaderVisibility.All),
                new RootParameter1(
                    new RootConstants(1, 0, BodyConstants.DwordCount), ShaderVisibility.Pixel),
                new RootParameter1(
                    RootParameterType.ShaderResourceView,
                    new RootDescriptor1(0, 0, RootDescriptorFlags.DataStatic),
                    ShaderVisibility.Pixel),
                new RootParameter1(
                    RootParameterType.ShaderResourceView,
                    new RootDescriptor1(1, 0, RootDescriptorFlags.DataVolatile),
                    ShaderVisibility.Pixel),
            ],
            []);

        ID3D12RootSignature signature = device.CreateRootSignature(in description);
        signature.Name = "transparency accumulation root signature";

        return signature;
    }

    /// <summary>
    /// A constant buffer and a table of four textures.
    /// </summary>
    /// <remarks>
    /// The one descriptor table in the renderer. Everything else is addressed by root descriptor,
    /// which can reach a buffer directly but not a texture — and the composite reads textures. All
    /// four slots are declared although only two are ever bound, because HLSL cannot choose between
    /// a <c>Texture2D</c> and a <c>Texture2DMS</c> at run time and the shader branches instead.
    /// </remarks>
    private static ID3D12RootSignature CreateCompositeSignature(ID3D12Device device)
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
                        new DescriptorRange1(DescriptorRangeType.ShaderResourceView, 4, 0)),
                    ShaderVisibility.Pixel),
            ],
            []);

        ID3D12RootSignature signature = device.CreateRootSignature(in description);
        signature.Name = "transparency composite root signature";

        return signature;
    }
}
