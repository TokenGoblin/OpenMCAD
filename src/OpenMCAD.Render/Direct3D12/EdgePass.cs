using System.Runtime.InteropServices;

using OpenMCAD.Math;

using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace OpenMCAD.Render.Direct3D12;

/// <summary>How edges are drawn.</summary>
/// <param name="Colour">The line colour.</param>
/// <param name="WidthPixels">Line width in physical pixels, before display scaling.</param>
/// <param name="DepthBias">
/// How far towards the viewer to push an edge, in normalised depth. Tessellated edges lie exactly
/// on the surface they bound, so without this they z-fight with it.
/// </param>
public readonly record struct EdgeStyle(Color4 Colour, float WidthPixels, float DepthBias)
{
    /// <summary>Gets the default appearance: a near-black hairline.</summary>
    /// <remarks>
    /// <para>
    /// Not pure black. Against the dark viewport background a black edge disappears wherever it
    /// crosses a shadowed face, and against a light face it reads as harsher than the shading
    /// around it.
    /// </para>
    /// <para>
    /// 1.4 pixels rather than 1: a one-pixel line is what D3D12's own line primitives give, and it
    /// is too thin to read once the display is scaled past 100%. The anti-aliasing in the shader
    /// means the width need not be an integer.
    /// </para>
    /// </remarks>
    public static EdgeStyle Default => new(new Color4(0.10f, 0.11f, 0.13f, 1.0f), 1.4f, 2e-4f);

    /// <summary>Gets this style scaled for a display, so a line looks the same size everywhere.</summary>
    /// <param name="dpiScale">The display scale, where 1.0 is 96 DPI.</param>
    /// <returns>The scaled style.</returns>
    public EdgeStyle AtScale(double dpiScale)
        => this with { WidthPixels = (float)(WidthPixels * System.Math.Max(dpiScale, 0.1)) };
}

/// <summary>The per-draw constants the edge shader reads.</summary>
/// <remarks>Laid out to match the <c>EdgeConstants</c> cbuffer in <c>Edges.hlsl</c>.</remarks>
[StructLayout(LayoutKind.Explicit, Size = 32)]
public struct EdgeConstants
{
    /// <summary>The line colour.</summary>
    [FieldOffset(0)]
    public Color4 Colour;

    /// <summary>Half the line width, in physical pixels.</summary>
    [FieldOffset(16)]
    public float HalfWidthPixels;

    /// <summary>Depth offset towards the viewer, in normalised depth.</summary>
    [FieldOffset(20)]
    public float DepthBias;

    /// <summary>Gets how many 32-bit root constants this occupies.</summary>
    public static int RootConstantCount => Marshal.SizeOf<EdgeConstants>() / 4;
}

/// <summary>
/// Draws edge polylines as anti-aliased, depth-biased, constant-width lines (P2-T06).
/// </summary>
/// <remarks>
/// <para>
/// <b>Quads, not line primitives.</b> A D3D12 line is exactly one pixel wide and cannot be made
/// wider or anti-aliased. On a 150% display that is a line the user can barely see, and a CAD
/// drawing is mostly edges. Each segment is expanded in the vertex shader into a quad of constant
/// pixel width instead, which also gives somewhere to put a coverage ramp — so edges are smooth
/// without multisampling, and stay smooth when multisampling arrives.
/// </para>
/// <para>
/// <b>Nothing is drawn from a vertex buffer.</b> Four corners per segment come from
/// <c>SV_VertexID</c> and the two endpoints arrive as per-instance data, so a body's edges are one
/// 24-byte-stride stream with no index buffer and one draw call.
/// </para>
/// <para>
/// Silhouette edges on curved surfaces are not here yet. A cylinder shows its two end circles and
/// its seam, because those are real edges the kernel knows about, but the two lines where the wall
/// turns away from the viewer are a property of the view rather than of the model and have to be
/// found per frame. That is its own piece of work.
/// </para>
/// </remarks>
public sealed class EdgePass : IDisposable
{
    /// <summary>The shader file this pass is built from.</summary>
    public const string ShaderFile = "Edges.hlsl";

    /// <summary>Vertices per segment: a four-corner triangle strip.</summary>
    public const int VerticesPerSegment = 4;

    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _pipelineState;

    private readonly ID3D12Resource _noHighlights;

    private bool _disposed;

    /// <summary>
    /// Builds the pipeline state.
    /// </summary>
    /// <param name="device">The device to create on.</param>
    /// <param name="renderTargetFormat">The format of the target being drawn into.</param>
    /// <param name="depthFormat">The depth format, matching the depth buffer.</param>
    /// <param name="optimiseShaders">Whether to compile optimised. Tests turn this off.</param>
    /// <param name="sampleCount">
    /// How many samples per pixel the target has. Must match the target this pass draws into: a
    /// pipeline state carries its sample count and the device refuses a mismatch outright.
    /// </param>
    /// <exception cref="ShaderCompilationException">The shader will not compile.</exception>
    public EdgePass(
        ID3D12Device device,
        Format renderTargetFormat = SwapChainTarget.BackBufferFormat,
        Format depthFormat = DepthBuffer.DepthFormat,
        bool optimiseShaders = true,
        int sampleCount = 1)
    {
        ArgumentNullException.ThrowIfNull(device);

        _rootSignature = CreateRootSignature(device);

        // A root descriptor bound to address zero removes the device on some drivers -- observed
        // on AMD, and it is the whole device rather than a draw that fails. It happens even though
        // the shader never dereferences it, because the count that guards the read is zero: the
        // binding alone is enough. WARP tolerates it, so every test passed while real hardware
        // did not.
        //
        // Rather than requiring every caller to remember, a one-entry buffer stands in whenever
        // there are no highlight states to bind. Sixteen bytes to make the API safe by
        // construction.
        _noHighlights = device.CreateCommittedResource(
            HeapType.Upload,
            HeapFlags.None,
            ResourceDescription.Buffer(SceneGeometry.IdStride),
            ResourceStates.GenericRead);

        _noHighlights.Name = "no highlight states";

        ReadOnlyMemory<byte> vertexShader = ShaderLibrary.Compile(
            ShaderFile, "VSMain", ShaderLibrary.VertexProfile, optimiseShaders);

        ReadOnlyMemory<byte> pixelShader = ShaderLibrary.Compile(
            ShaderFile, "PSMain", ShaderLibrary.PixelProfile, optimiseShaders);

        GraphicsPipelineStateDescription description = new()
        {
            RootSignature = _rootSignature,
            VertexShader = vertexShader,
            PixelShader = pixelShader,

            // Per-instance only. There is no per-vertex stream at all: the corner index comes from
            // SV_VertexID, which costs nothing to supply and saves a whole buffer.
            InputLayout = new InputLayoutDescription(
                new InputElementDescription(
                    "EDGESTART", 0, Format.R32G32B32_Float, 0, 0, InputClassification.PerInstanceData, 1),
                new InputElementDescription(
                    "EDGEEND", 0, Format.R32G32B32_Float, 12, 0, InputClassification.PerInstanceData, 1)),

            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RasterizerState = RasterizerDescription.CullNone,

            // Alpha blending, for the coverage ramp that anti-aliases the line.
            //
            // This one is PREMULTIPLIED -- SourceBlend is One, not SourceAlpha; the straight-alpha
            // state is BlendDescription.NonPremultiplied, despite the names. The pixel shader
            // multiplies through to match. Pairing this state with a straight-alpha shader adds
            // the whole edge colour to the destination wherever coverage is low, which turns the
            // softening ramp into a bright halo around every line.
            //
            // Depth is still written: a partially covered edge pixel counts as solid for
            // occlusion, which stops the far side of a body showing through its own silhouette.
            BlendState = BlendDescription.AlphaBlend,
            DepthStencilState = DepthStencilDescription.Default,
            DepthStencilFormat = depthFormat,
            RenderTargetFormats = [renderTargetFormat],
            SampleDescription = new SampleDescription((uint)sampleCount, 0),
            SampleMask = uint.MaxValue,
        };

        _pipelineState = device.CreateGraphicsPipelineState(description);
        _pipelineState.Name = "edge pass";
    }

    /// <summary>Gets how many segments the last <see cref="Draw"/> submitted.</summary>
    public int SegmentsDrawn { get; private set; }

    /// <summary>Gets how many bodies the last <see cref="Draw"/> culled.</summary>
    public int BodiesCulled { get; private set; }

    /// <summary>
    /// Records the draw calls for a scene's edges.
    /// </summary>
    /// <param name="commands">An open command list with a render target already bound.</param>
    /// <param name="scene">The buffers to draw.</param>
    /// <param name="constantBufferAddress">Where the caller wrote a <see cref="FrameConstants"/>.</param>
    /// <param name="style">How the lines should look.</param>
    /// <param name="frustum">
    /// The frustum to cull against, in world space. Pass <see langword="null"/> to draw everything.
    /// </param>
    /// <param name="highlightStates">
    /// Where the per-entity highlight states live, or zero when nothing is highlighted.
    /// </param>
    public void Draw(
        ID3D12GraphicsCommandList commands,
        SceneGeometry scene,
        ulong constantBufferAddress,
        EdgeStyle style,
        Frustum? frustum = null,
        ulong highlightStates = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(scene);

        Reset();

        if (scene.Bodies.Count == 0)
        {
            return;
        }

        EdgeConstants constants = new()
        {
            Colour = style.Colour,
            HalfWidthPixels = System.Math.Max(style.WidthPixels, 0.1f) * 0.5f,
            DepthBias = style.DepthBias,
        };

        commands.SetGraphicsRootSignature(_rootSignature);
        commands.SetPipelineState(_pipelineState);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        commands.SetGraphicsRootConstantBufferView(0, constantBufferAddress);
        commands.SetGraphicsRoot32BitConstants(1, constants, 0);
        commands.SetGraphicsRootShaderResourceView(
            3, highlightStates == 0 ? _noHighlights.GPUVirtualAddress : highlightStates);

        foreach (BodyGeometry body in scene.Bodies)
        {
            if (body.SegmentCount == 0 || body.SegmentIdAddress == 0)
            {
                continue;
            }

            if (frustum is { } bounds && !bounds.Intersects(body.Bounds))
            {
                BodiesCulled++;
                continue;
            }

            commands.SetGraphicsRootShaderResourceView(2, body.SegmentIdAddress);
            commands.IASetVertexBuffers(0, body.EdgeSegmentView);
            commands.DrawInstanced(VerticesPerSegment, (uint)body.SegmentCount, 0, 0);

            SegmentsDrawn += body.SegmentCount;
        }
    }

    /// <summary>Clears the counters, for a frame that draws no edges at all.</summary>
    /// <remarks>
    /// Without this, switching edges off leaves the last frame's totals in place and every
    /// diagnostic that reads them reports edges still being drawn.
    /// </remarks>
    public void Reset()
    {
        SegmentsDrawn = 0;
        BodiesCulled = 0;
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
        _pipelineState.Dispose();
        _rootSignature.Dispose();
    }

    /// <summary>
    /// The frame constants, plus the style as root constants.
    /// </summary>
    /// <remarks>
    /// The style is the same for every body in a frame, so it could equally live in the frame
    /// buffer. It is kept separate because selection highlighting will want to draw one body's
    /// edges in another colour, and changing eight root constants between draws costs nothing.
    /// </remarks>
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

                // t0: this body's per-segment display ids. t1: the highlight state of every
                // entity in the scene.
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

        ID3D12RootSignature rootSignature = device.CreateRootSignature(in description);
        rootSignature.Name = "edge pass root signature";

        return rootSignature;
    }
}
