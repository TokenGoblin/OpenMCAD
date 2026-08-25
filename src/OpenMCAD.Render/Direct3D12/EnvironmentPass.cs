using System.Numerics;
using System.Runtime.InteropServices;

using OpenMCAD.Math;

using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace OpenMCAD.Render.Direct3D12;

/// <summary>How the background and ground grid look.</summary>
/// <param name="Top">The colour at the top of the viewport.</param>
/// <param name="Bottom">The colour at the bottom.</param>
/// <param name="Grid">The grid line colour. Alpha is how strongly the lines show.</param>
/// <param name="AxisX">The colour of the X axis line on the ground plane.</param>
/// <param name="AxisY">The colour of the Y axis line on the ground plane.</param>
/// <param name="Spacing">Fine line spacing in metres. Coarse lines are ten times this.</param>
/// <param name="FadeDistance">How far from the camera the grid has faded out, in metres.</param>
public readonly record struct EnvironmentStyle(
    Color4 Top,
    Color4 Bottom,
    Color4 Grid,
    Color4 AxisX,
    Color4 AxisY,
    float Spacing,
    float FadeDistance)
{
    /// <summary>Gets the default appearance.</summary>
    /// <remarks>
    /// <para>
    /// A gradient rather than a flat colour, darker at the top. It reads as a room rather than a
    /// void, and — more usefully — it gives the silhouette of a part something to contrast against
    /// wherever it happens to sit, which a single flat tone cannot do at both extremes.
    /// </para>
    /// <para>
    /// The axis colours are the near-universal convention: X red, Y green. Departing from it
    /// costs a user their orientation in a way no legend recovers.
    /// </para>
    /// </remarks>
    public static EnvironmentStyle Default => new(
        new Color4(0.16f, 0.17f, 0.20f, 1.0f),
        new Color4(0.28f, 0.30f, 0.34f, 1.0f),
        new Color4(0.55f, 0.58f, 0.63f, 0.55f),
        new Color4(0.80f, 0.30f, 0.32f, 0.85f),
        new Color4(0.36f, 0.70f, 0.38f, 0.85f),
        0.01f,
        1.0f);

    /// <summary>
    /// Returns this style with its spacing and fade chosen for a scene of a given size.
    /// </summary>
    /// <param name="bounds">The scene to suit. An empty one leaves the style unchanged.</param>
    /// <returns>The adjusted style.</returns>
    /// <remarks>
    /// <para>
    /// A grid at a fixed spacing is wrong at almost every scale a CAD user works at. Ten
    /// millimetres is right for a bracket and invisible for a building; a metre is right for the
    /// building and swallows the bracket whole. The spacing is snapped to the nearest power of ten
    /// below a tenth of the scene, so it lands on a round number a user can count in rather than
    /// on an arbitrary fraction of the model.
    /// </para>
    /// <para>
    /// This is chosen from the scene rather than from the camera deliberately. Deriving it from
    /// zoom means the grid changes density as the user moves, and a reference that keeps changing
    /// underneath is worse than one at the wrong scale.
    /// </para>
    /// </remarks>
    public EnvironmentStyle ForScene(Bounds3d bounds)
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

        double decade = System.Math.Pow(10.0, System.Math.Floor(System.Math.Log10(extent / 10.0)));

        return this with
        {
            Spacing = (float)System.Math.Clamp(decade, 1e-6, 1e6),
            FadeDistance = (float)System.Math.Clamp(extent * 8.0, 1e-4, 1e9),
        };
    }
}

/// <summary>The constants the environment shader reads.</summary>
/// <remarks>Matches <c>EnvironmentConstants</c> in <c>Environment.hlsl</c>.</remarks>
[StructLayout(LayoutKind.Explicit, Size = 176)]
public struct EnvironmentConstants
{
    /// <summary>Clip space back to world space.</summary>
    [FieldOffset(0)]
    public Matrix4x4 InverseViewProjection;

    /// <summary>Where the camera is, relative to the snapshot origin.</summary>
    [FieldOffset(64)]
    public Vector3 CameraPosition;

    /// <summary>The colour at the top of the viewport.</summary>
    [FieldOffset(80)]
    public Color4 TopColour;

    /// <summary>The colour at the bottom.</summary>
    [FieldOffset(96)]
    public Color4 BottomColour;

    /// <summary>The grid line colour.</summary>
    [FieldOffset(112)]
    public Color4 GridColour;

    /// <summary>The X axis colour.</summary>
    [FieldOffset(128)]
    public Color4 AxisXColour;

    /// <summary>The Y axis colour.</summary>
    [FieldOffset(144)]
    public Color4 AxisYColour;

    /// <summary>Fine line spacing in metres.</summary>
    [FieldOffset(160)]
    public float GridSpacing;

    /// <summary>How far from the camera the grid has faded out.</summary>
    [FieldOffset(164)]
    public float GridFade;

    /// <summary>Non-zero to draw the grid as well as the gradient.</summary>
    [FieldOffset(168)]
    public float ShowGrid;

    /// <summary>Gets how many bytes to upload.</summary>
    public static int SizeInBytes => Marshal.SizeOf<EnvironmentConstants>();
}

/// <summary>
/// Draws the background gradient and the ground grid (P2-T11).
/// </summary>
/// <remarks>
/// <para>
/// <b>One full-screen triangle, no vertex buffer, no geometry.</b> The three corners come from
/// <c>SV_VertexID</c>. A triangle rather than a quad, because a quad's diagonal makes the
/// rasteriser shade the pixels along it twice.
/// </para>
/// <para>
/// <b>The grid is computed per pixel rather than drawn as lines.</b> Line geometry needs an extent
/// and a spacing decided in advance, and a CAD user zooms across six orders of magnitude in a
/// session: the lines either run out at the edge of the world or collapse into a solid mass.
/// Intersecting a ray with the ground plane and asking whether the pixel lands on a line gives a
/// grid with no extent at all, and screen-space derivatives keep every line the same width however
/// far away it is.
/// </para>
/// <para>
/// It runs first and writes no depth, at the far plane, so everything else draws over it and the
/// depth buffer is untouched — the grid never occludes geometry and geometry never has to be
/// sorted against it.
/// </para>
/// </remarks>
public sealed class EnvironmentPass : IDisposable
{
    /// <summary>The shader file this pass is built from.</summary>
    public const string ShaderFile = "Environment.hlsl";

    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _pipelineState;

    private bool _disposed;

    /// <summary>Builds the pipeline state.</summary>
    /// <param name="device">The device to create on.</param>
    /// <param name="renderTargetFormat">The format of the target being drawn into.</param>
    /// <param name="depthFormat">The depth format, matching the depth buffer.</param>
    /// <param name="optimiseShaders">Whether to compile optimised. Tests turn this off.</param>
    /// <param name="sampleCount">
    /// How many samples per pixel the target has. Must match the target this pass draws into: a
    /// pipeline state carries its sample count and the device refuses a mismatch outright.
    /// </param>
    /// <exception cref="ShaderCompilationException">The shader will not compile.</exception>
    public EnvironmentPass(
        ID3D12Device device,
        Format renderTargetFormat = SwapChainTarget.BackBufferFormat,
        Format depthFormat = DepthBuffer.DepthFormat,
        bool optimiseShaders = true,
        int sampleCount = 1)
    {
        ArgumentNullException.ThrowIfNull(device);

        _rootSignature = CreateRootSignature(device);

        // Depth reading and writing both off. The gradient sits at the far plane and must not
        // occlude anything; leaving depth writes on would put the entire scene behind it.
        DepthStencilDescription noDepth = DepthStencilDescription.None;

        _pipelineState = device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = _rootSignature,

            VertexShader = ShaderLibrary.Compile(
                ShaderFile, "VSMain", ShaderLibrary.VertexProfile, optimiseShaders),

            PixelShader = ShaderLibrary.Compile(
                ShaderFile, "PSMain", ShaderLibrary.PixelProfile, optimiseShaders),

            // No input layout at all: the corners are generated from the vertex id.
            InputLayout = new InputLayoutDescription(),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = BlendDescription.Opaque,
            DepthStencilState = noDepth,
            DepthStencilFormat = depthFormat,
            RenderTargetFormats = [renderTargetFormat],
            SampleDescription = new SampleDescription((uint)sampleCount, 0),
            SampleMask = uint.MaxValue,
        });

        _pipelineState.Name = "environment pass";
    }

    /// <summary>Records the pass.</summary>
    /// <param name="commands">An open command list with a render target bound.</param>
    /// <param name="constantBufferAddress">Where the caller wrote an <see cref="EnvironmentConstants"/>.</param>
    public void Draw(ID3D12GraphicsCommandList commands, ulong constantBufferAddress)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(commands);

        commands.SetGraphicsRootSignature(_rootSignature);
        commands.SetPipelineState(_pipelineState);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.SetGraphicsRootConstantBufferView(0, constantBufferAddress);
        commands.DrawInstanced(3, 1, 0, 0);
    }

    /// <summary>
    /// Builds the constants for a camera.
    /// </summary>
    /// <param name="camera">The camera being rendered through.</param>
    /// <param name="sceneBounds">The scene, for the projection's depth range.</param>
    /// <param name="origin">The snapshot origin the geometry is relative to.</param>
    /// <param name="style">How it should look.</param>
    /// <param name="showGrid">Whether to draw the grid as well as the gradient.</param>
    /// <returns>The constants to upload.</returns>
    /// <remarks>
    /// Built in the snapshot's shifted frame, exactly as the shaded passes are, so that the grid
    /// lands on the same ground plane the geometry sits on rather than on one displaced by however
    /// far the model is from the world origin.
    /// </remarks>
    public static EnvironmentConstants ConstantsFor(
        Camera camera,
        Bounds3d sceneBounds,
        Vec3d origin,
        EnvironmentStyle style,
        bool showGrid = true)
    {
        ArgumentNullException.ThrowIfNull(camera);

        Mat4d viewProjection = camera.ProjectionMatrix(sceneBounds)
            * Mat4d.LookAt(camera.Position - origin, camera.Target - origin, camera.Up);

        Mat4d inverse = viewProjection.Inverted();

        return new EnvironmentConstants
        {
            InverseViewProjection = new Matrix4x4(
                (float)inverse.M11, (float)inverse.M12, (float)inverse.M13, (float)inverse.M14,
                (float)inverse.M21, (float)inverse.M22, (float)inverse.M23, (float)inverse.M24,
                (float)inverse.M31, (float)inverse.M32, (float)inverse.M33, (float)inverse.M34,
                (float)inverse.M41, (float)inverse.M42, (float)inverse.M43, (float)inverse.M44),

            CameraPosition = ToVector3(camera.Position - origin),
            TopColour = style.Top,
            BottomColour = style.Bottom,
            GridColour = style.Grid,
            AxisXColour = style.AxisX,
            AxisYColour = style.AxisY,
            GridSpacing = style.Spacing,
            GridFade = style.FadeDistance,
            ShowGrid = showGrid ? 1.0f : 0.0f,
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

        _pipelineState.Dispose();
        _rootSignature.Dispose();
    }

    private static Vector3 ToVector3(Vec3d v) => new((float)v.X, (float)v.Y, (float)v.Z);

    private static ID3D12RootSignature CreateRootSignature(ID3D12Device device)
    {
        RootSignatureDescription1 description = new(
            RootSignatureFlags.None,
            [
                new RootParameter1(
                    RootParameterType.ConstantBufferView,
                    new RootDescriptor1(0, 0, RootDescriptorFlags.DataStatic),
                    ShaderVisibility.All),
            ],
            []);

        ID3D12RootSignature rootSignature = device.CreateRootSignature(in description);
        rootSignature.Name = "environment pass root signature";

        return rootSignature;
    }
}
