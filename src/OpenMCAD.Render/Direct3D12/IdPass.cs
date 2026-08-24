using OpenMCAD.Math;

using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace OpenMCAD.Render.Direct3D12;

/// <summary>
/// Renders display ids into an <see cref="IdTarget"/> for picking (P2-T07).
/// </summary>
/// <remarks>
/// <para>
/// <b>The vertex shaders are shared with the visible passes, byte for byte.</b> Only the pixel
/// shaders differ — one writes a shaded colour, the other writes the id of whatever it is shading.
/// That is the whole design: a pick is only correct if the ID buffer was rasterised from geometry
/// identical to what the user is looking at, and two vertex shaders maintained separately agree
/// right up until the day one of them is edited.
/// </para>
/// <para>
/// Ids reach the pixel shaders without a descriptor heap. A structured buffer is one of the few
/// things a D3D12 root descriptor can address directly, so each body's id array is bound as a root
/// shader resource view — faces indexed by <c>SV_PrimitiveID</c>, edges by the instance carried
/// through from the vertex shader.
/// </para>
/// <para>
/// Nothing here runs per frame. The ID buffer is rendered only when something asks to pick, which
/// is why it carries its own depth buffer: rendering it every frame would double the cost of the
/// viewport to answer a question nobody had asked.
/// </para>
/// </remarks>
public sealed class IdPass : IDisposable
{
    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _facePipeline;
    private readonly ID3D12PipelineState _edgePipeline;

    private bool _disposed;

    /// <summary>
    /// Builds both pipeline states.
    /// </summary>
    /// <param name="device">The device to create on.</param>
    /// <param name="depthFormat">The depth format, matching the target's depth buffer.</param>
    /// <param name="optimiseShaders">Whether to compile optimised. Tests turn this off.</param>
    /// <exception cref="ShaderCompilationException">A shader will not compile.</exception>
    public IdPass(
        ID3D12Device device,
        Format depthFormat = DepthBuffer.DepthFormat,
        bool optimiseShaders = true)
    {
        ArgumentNullException.ThrowIfNull(device);

        _rootSignature = CreateRootSignature(device);

        _facePipeline = device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = _rootSignature,

            // The very same entry point the shaded pass compiles.
            VertexShader = ShaderLibrary.Compile(
                FacePass.ShaderFile, "VSMain", ShaderLibrary.VertexProfile, optimiseShaders),

            PixelShader = ShaderLibrary.Compile(
                FacePass.ShaderFile, "PSMainId", ShaderLibrary.PixelProfile, optimiseShaders),

            InputLayout = new InputLayoutDescription(
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 0, 1)),

            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = NoBlending,
            DepthStencilState = DepthStencilDescription.Default,
            DepthStencilFormat = depthFormat,
            RenderTargetFormats = [IdTarget.IdFormat],
            SampleDescription = SampleDescription.Default,
            SampleMask = uint.MaxValue,
        });

        _facePipeline.Name = "id pass (faces)";

        _edgePipeline = device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = _rootSignature,

            VertexShader = ShaderLibrary.Compile(
                EdgePass.ShaderFile, "VSMain", ShaderLibrary.VertexProfile, optimiseShaders),

            PixelShader = ShaderLibrary.Compile(
                EdgePass.ShaderFile, "PSMainId", ShaderLibrary.PixelProfile, optimiseShaders),

            InputLayout = new InputLayoutDescription(
                new InputElementDescription(
                    "EDGESTART", 0, Format.R32G32B32_Float, 0, 0, InputClassification.PerInstanceData, 1),
                new InputElementDescription(
                    "EDGEEND", 0, Format.R32G32B32_Float, 12, 0, InputClassification.PerInstanceData, 1)),

            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = NoBlending,
            DepthStencilState = DepthStencilDescription.Default,
            DepthStencilFormat = depthFormat,
            RenderTargetFormats = [IdTarget.IdFormat],
            SampleDescription = SampleDescription.Default,
            SampleMask = uint.MaxValue,
        });

        _edgePipeline.Name = "id pass (edges)";
    }

    /// <summary>Gets how many bodies the last <see cref="Draw"/> submitted.</summary>
    public int BodiesDrawn { get; private set; }

    /// <summary>
    /// Records the ID pass for a scene.
    /// </summary>
    /// <param name="commands">An open command list with the ID target and its depth bound.</param>
    /// <param name="scene">The buffers to draw.</param>
    /// <param name="constantBufferAddress">Where the caller wrote a <see cref="FrameConstants"/>.</param>
    /// <param name="style">
    /// The same style the visible edges are drawn with. It must match, or an edge will occupy a
    /// different set of pixels in the ID buffer than it does on screen and picks near it will miss.
    /// </param>
    /// <param name="frustum">The frustum to cull against, in world space.</param>
    public void Draw(
        ID3D12GraphicsCommandList commands,
        SceneGeometry scene,
        ulong constantBufferAddress,
        EdgeStyle style,
        Frustum? frustum = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(scene);

        BodiesDrawn = 0;

        if (scene.Bodies.Count == 0)
        {
            return;
        }

        EdgeConstants constants = new()
        {
            Colour = default,
            HalfWidthPixels = System.Math.Max(style.WidthPixels, 0.1f) * 0.5f,
            DepthBias = style.DepthBias,
        };

        commands.SetGraphicsRootSignature(_rootSignature);
        commands.SetGraphicsRootConstantBufferView(0, constantBufferAddress);
        commands.SetGraphicsRoot32BitConstants(1, constants, 0);

        // Faces first, then edges over them, matching the visible order so the depth bias resolves
        // the same way it does on screen.
        commands.SetPipelineState(_facePipeline);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

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

        commands.SetPipelineState(_edgePipeline);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);

        foreach (BodyGeometry body in scene.Bodies)
        {
            if (body.SegmentCount == 0 || body.SegmentIdAddress == 0)
            {
                continue;
            }

            if (frustum is { } bounds && !bounds.Intersects(body.Bounds))
            {
                continue;
            }

            commands.SetGraphicsRootShaderResourceView(2, body.SegmentIdAddress);
            commands.IASetVertexBuffers(0, body.EdgeSegmentView);
            commands.DrawInstanced(EdgePass.VerticesPerSegment, (uint)body.SegmentCount, 0, 0);
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

        _edgePipeline.Dispose();
        _facePipeline.Dispose();
        _rootSignature.Dispose();
    }

    /// <summary>Blending disabled outright, which an integer render target requires.</summary>
    /// <remarks>
    /// <see cref="BlendDescription.Opaque"/> is One/Zero but still has blending <i>enabled</i>, and
    /// D3D12 rejects an enabled blend on a UINT target. More to the point, blending two ids would
    /// produce a third that names a different entity.
    /// </remarks>
    private static BlendDescription NoBlending
    {
        get
        {
            BlendDescription description = BlendDescription.Opaque;
            description.RenderTarget[0].BlendEnable = false;

            return description;
        }
    }

    /// <summary>Frame constants, the edge style, and the body's id array.</summary>
    private static ID3D12RootSignature CreateRootSignature(ID3D12Device device)
    {
        RootSignatureDescription1 description = new(
            RootSignatureFlags.AllowInputAssemblerInputLayout,
            [
                new RootParameter1(
                    RootParameterType.ConstantBufferView,
                    new RootDescriptor1(0, 0, RootDescriptorFlags.DataStatic),
                    ShaderVisibility.All),
                new RootParameter1(
                    new RootConstants(1, 0, (uint)EdgeConstants.RootConstantCount),
                    ShaderVisibility.All),
                new RootParameter1(
                    RootParameterType.ShaderResourceView,
                    new RootDescriptor1(0, 0, RootDescriptorFlags.DataStatic),
                    ShaderVisibility.Pixel),
            ],
            []);

        ID3D12RootSignature rootSignature = device.CreateRootSignature(in description);
        rootSignature.Name = "id pass root signature";

        return rootSignature;
    }
}
