using System.Numerics;
using System.Runtime.InteropServices;

using OpenMCAD.Math;

using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace OpenMCAD.Render.Direct3D12;

/// <summary>The per-frame constants the surface shader reads.</summary>
/// <remarks>
/// Laid out to match the <c>FrameConstants</c> cbuffer in <c>Surface.hlsl</c> exactly. HLSL packs
/// a cbuffer into 16-byte registers and will not split a float3 across a boundary, so each float3
/// here is followed by four bytes of nothing. The offsets are stated explicitly rather than left
/// to sequential layout with pad fields, because the numbers are the contract with the shader and
/// this way they are readable next to it: get one wrong and the geometry appears somewhere
/// unexpected rather than failing.
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 160)]
public struct FrameConstants
{
    /// <summary>Sixteen floats, row-major, matching the <c>row_major</c> declaration in HLSL.</summary>
    [FieldOffset(0)]
    public Matrix4x4 ViewProjection;

    /// <summary>Where the camera is, relative to the snapshot origin.</summary>
    [FieldOffset(64)]
    public Vector3 CameraPosition;

    /// <summary>Unit vector from the surface towards the light.</summary>
    [FieldOffset(80)]
    public Vector3 LightDirection;

    /// <summary>The render target size in physical pixels.</summary>
    /// <remarks>
    /// Only the edge pass reads this, to give a line a width measured in pixels rather than in
    /// metres. It lives in the shared block anyway: one constant buffer written once a frame and
    /// bound by both passes is simpler than two that can disagree about the camera.
    /// </remarks>
    [FieldOffset(96)]
    public Vector2 ViewportSize;

    /// <summary>How many entries the highlight state buffer holds.</summary>
    /// <remarks>
    /// The shader cannot work this out for itself. The states are bound as a root descriptor,
    /// which carries a GPU address and nothing else — no length, no stride — so an out-of-range
    /// index reads unmapped memory rather than returning zero. This is the only bound there is.
    /// </remarks>
    [FieldOffset(104)]
    public uint HighlightCount;

    /// <summary>The colour an entity under the cursor is tinted towards. Alpha is tint strength.</summary>
    [FieldOffset(112)]
    public Color4 PreSelectedColour;

    /// <summary>The colour a selected entity is tinted towards. Alpha is tint strength.</summary>
    [FieldOffset(128)]
    public Color4 SelectedColour;

    /// <summary>The colour an entity in error is tinted towards. Alpha is tint strength.</summary>
    [FieldOffset(144)]
    public Color4 ErrorColour;

    /// <summary>Gets how many bytes to upload.</summary>
    public static int SizeInBytes => Marshal.SizeOf<FrameConstants>();
}

/// <summary>
/// Draws shaded triangles (P2-T05).
/// </summary>
/// <remarks>
/// <para>
/// One pipeline state, one root signature, one draw call per body. Instancing and GPU-side culling
/// are what this grows into when assemblies get large; what is here is the correct single-body
/// path, with CPU frustum culling per body already in place because that is where most of the
/// saving is in a mechanical assembly and it costs six dot products.
/// </para>
/// <para>
/// <b>Back faces are not culled.</b> That is not an oversight and not a default left unset. A CAD
/// model is routinely looked at from inside — a section view, an open shell, a surface body with no
/// inside at all — and a pass that culled back faces would render those as holes. The pixel shader
/// flips the normal towards the viewer to match, so a face lit from the front stays lit when seen
/// from behind. The cost is shading fragments that a closed solid will overdraw, which the depth
/// test mostly absorbs.
/// </para>
/// </remarks>
public sealed class FacePass : IDisposable
{
    /// <summary>The shader file this pass is built from.</summary>
    public const string ShaderFile = "Surface.hlsl";

    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _pipelineState;

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
    public FacePass(
        ID3D12Device device,
        Format renderTargetFormat = SwapChainTarget.BackBufferFormat,
        Format depthFormat = DepthBuffer.DepthFormat,
        bool optimiseShaders = true,
        int sampleCount = 1)
    {
        ArgumentNullException.ThrowIfNull(device);

        _rootSignature = CreateRootSignature(device);

        ReadOnlyMemory<byte> vertexShader = ShaderLibrary.Compile(
            ShaderFile, "VSMain", ShaderLibrary.VertexProfile, optimiseShaders);

        ReadOnlyMemory<byte> pixelShader = ShaderLibrary.Compile(
            ShaderFile, "PSMain", ShaderLibrary.PixelProfile, optimiseShaders);

        GraphicsPipelineStateDescription description = new()
        {
            RootSignature = _rootSignature,
            VertexShader = vertexShader,
            PixelShader = pixelShader,
            InputLayout = new InputLayoutDescription(
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 0, 1)),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.Default,
            DepthStencilFormat = depthFormat,
            RenderTargetFormats = [renderTargetFormat],
            SampleDescription = new SampleDescription((uint)sampleCount, 0),
            SampleMask = uint.MaxValue,
        };

        _pipelineState = device.CreateGraphicsPipelineState(description);
        _pipelineState.Name = "face pass";
    }

    /// <summary>Gets how many bodies the last <see cref="Draw"/> submitted.</summary>
    public int BodiesDrawn { get; private set; }

    /// <summary>Gets how many bodies the last <see cref="Draw"/> culled.</summary>
    public int BodiesCulled { get; private set; }

    /// <summary>Gets how many triangles the last <see cref="Draw"/> submitted.</summary>
    public int TrianglesDrawn { get; private set; }

    /// <summary>
    /// Records the draw calls for a scene.
    /// </summary>
    /// <param name="commands">An open command list with a render target already bound.</param>
    /// <param name="scene">The buffers to draw.</param>
    /// <param name="constantBufferAddress">
    /// Where the caller wrote a <see cref="FrameConstants"/>. Must be 256-byte aligned, which is
    /// what <see cref="UploadRing"/> guarantees.
    /// </param>
    /// <param name="frustum">
    /// The frustum to cull against, in world space to match <see cref="BodyGeometry.Bounds"/>.
    /// Pass <see langword="null"/> to draw everything.
    /// </param>
    /// <param name="colour">The body colour.</param>
    /// <param name="highlightStates">
    /// Where the per-entity highlight states live, or zero when nothing is highlighted.
    /// </param>
    public void Draw(
        ID3D12GraphicsCommandList commands,
        SceneGeometry scene,
        ulong constantBufferAddress,
        Frustum? frustum = null,
        Color4? colour = null,
        ulong highlightStates = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(scene);

        BodiesDrawn = 0;
        BodiesCulled = 0;
        TrianglesDrawn = 0;

        if (scene.Bodies.Count == 0)
        {
            return;
        }

        Color4 bodyColour = colour ?? DefaultColour;

        commands.SetGraphicsRootSignature(_rootSignature);
        commands.SetPipelineState(_pipelineState);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.SetGraphicsRootConstantBufferView(0, constantBufferAddress);
        commands.SetGraphicsRoot32BitConstants(1, bodyColour, 0);

        // Bound once for the whole scene. A root descriptor of zero is legal and the shader
        // handles it: GetDimensions reports nothing, so every entity reads as unhighlighted.
        commands.SetGraphicsRootShaderResourceView(3, highlightStates);

        foreach (BodyGeometry body in scene.Bodies)
        {
            if (!body.HasFaces)
            {
                // Edges only -- a wireframe body. The edge pass will draw it.
                continue;
            }

            // Both the bounds and the frustum are in world space, so the origin shift the vertex
            // buffers are expressed in does not enter into it. Culling in the shifted frame would
            // work equally well but would mean this pass knowing about the shift, which belongs to
            // the snapshot rather than to the pass.
            if (frustum is { } bounds && !bounds.Intersects(body.Bounds))
            {
                BodiesCulled++;
                continue;
            }

            commands.SetGraphicsRootShaderResourceView(2, body.TriangleIdAddress);
            commands.IASetVertexBuffers(0, body.PositionView);
            commands.IASetVertexBuffers(1, body.NormalView);
            commands.IASetIndexBuffer(body.IndexView);
            commands.DrawIndexedInstanced((uint)body.IndexCount, 1, 0, 0, 0);

            BodiesDrawn++;
            TrianglesDrawn += body.IndexCount / 3;
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

        _pipelineState.Dispose();
        _rootSignature.Dispose();
    }

    /// <summary>
    /// Where the key light comes from, in world space, for a given camera.
    /// </summary>
    /// <param name="camera">The camera being rendered through.</param>
    /// <returns>A unit vector pointing from the surface towards the light.</returns>
    /// <remarks>
    /// <para>
    /// A headlight, offset up and to the left. A light placed exactly at the camera is the obvious
    /// choice and the wrong one: in an isometric view the three visible faces of a cube make the
    /// same angle with the view axis, so a pure headlight shades all three identically and the
    /// shape reads as a flat hexagon. Offsetting the light breaks that symmetry.
    /// </para>
    /// <para>
    /// A fixed world-space light would break it too, but leaves faces unlit and unreadable as the
    /// model is orbited. A camera-relative light with an offset is the compromise every CAD
    /// package converges on.
    /// </para>
    /// </remarks>
    public static Vec3d KeyLightDirection(Camera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);

        Vec3d direction = camera.Backward + (camera.Up * 0.45) - (camera.Right * 0.35);
        double length = direction.Length;

        return length < Tolerance.Linear ? camera.Backward : direction / length;
    }

    /// <summary>Gets the colour bodies are drawn in when the caller names none.</summary>
    /// <remarks>
    /// A warm mid grey. Steel and aluminium read as neutral, and a saturated default would fight
    /// every selection and analysis colour laid over it later.
    /// </remarks>
    private static Color4 DefaultColour => new(0.72f, 0.71f, 0.68f, 1.0f);

    /// <summary>
    /// Two root parameters: the frame constants and the body colour.
    /// </summary>
    /// <remarks>
    /// The frame constants are a root descriptor rather than a descriptor table, so there is no
    /// descriptor heap to bind and no per-frame heap management for a single buffer. The body
    /// colour is four root constants, which is cheaper still: pushing sixteen bytes inline beats
    /// allocating, writing and addressing a constant buffer per body.
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
                    new RootConstants(1, 0, 4),
                    ShaderVisibility.Pixel),

                // t0: this body's per-triangle display ids. t1: the highlight state of every
                // entity in the scene. Both are structured buffers, which a root descriptor can
                // address directly -- so the shaded pass still needs no descriptor heap.
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
        rootSignature.Name = "face pass root signature";

        return rootSignature;
    }
}
