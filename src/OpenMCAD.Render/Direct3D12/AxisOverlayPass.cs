using System.Numerics;
using System.Runtime.InteropServices;

using OpenMCAD.Math;

using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace OpenMCAD.Render.Direct3D12;

/// <summary>Where the corner gizmo sits.</summary>
public enum GizmoCorner
{
    /// <summary>Bottom left.</summary>
    BottomLeft,

    /// <summary>Bottom right.</summary>
    BottomRight,

    /// <summary>Top left.</summary>
    TopLeft,

    /// <summary>Top right.</summary>
    TopRight,
}

/// <summary>How the axis overlay looks.</summary>
/// <param name="AxisX">The X axis colour.</param>
/// <param name="AxisY">The Y axis colour.</param>
/// <param name="AxisZ">The Z axis colour.</param>
/// <param name="WidthPixels">Line width in physical pixels, before display scaling.</param>
/// <param name="GizmoPixels">How long each gizmo axis is, in physical pixels.</param>
/// <param name="GizmoMarginPixels">How far the gizmo sits from the viewport edge.</param>
/// <param name="Corner">Which corner the gizmo occupies.</param>
public readonly record struct AxisStyle(
    Color4 AxisX,
    Color4 AxisY,
    Color4 AxisZ,
    float WidthPixels,
    float GizmoPixels,
    float GizmoMarginPixels,
    GizmoCorner Corner)
{
    /// <summary>Gets the default appearance.</summary>
    /// <remarks>
    /// X red, Y green, Z blue. This convention is old and universal enough that a user reads it
    /// without thinking; any other assignment costs them their orientation permanently, because
    /// they will not believe a legend over thirty years of habit.
    /// </remarks>
    public static AxisStyle Default => new(
        new Color4(0.86f, 0.30f, 0.32f, 1.0f),
        new Color4(0.38f, 0.76f, 0.40f, 1.0f),
        new Color4(0.34f, 0.55f, 0.92f, 1.0f),
        2.0f,
        34.0f,
        22.0f,
        GizmoCorner.BottomLeft);

    /// <summary>Gets this style scaled for a display.</summary>
    /// <param name="dpiScale">The display scale, where 1.0 is 96 DPI.</param>
    /// <returns>The scaled style.</returns>
    public AxisStyle AtScale(double dpiScale)
    {
        float factor = (float)System.Math.Max(dpiScale, 0.1);

        return this with
        {
            WidthPixels = WidthPixels * factor,
            GizmoPixels = GizmoPixels * factor,
            GizmoMarginPixels = GizmoMarginPixels * factor,
        };
    }
}

/// <summary>The constants the axis shader reads.</summary>
/// <remarks>Matches <c>AxisConstants</c> in <c>Axes.hlsl</c>.</remarks>
[StructLayout(LayoutKind.Explicit, Size = 80)]
public struct AxisConstants
{
    /// <summary>Takes an axis endpoint to clip space.</summary>
    [FieldOffset(0)]
    public Matrix4x4 Transform;

    /// <summary>The render target size in physical pixels.</summary>
    [FieldOffset(64)]
    public Vector2 ViewportSize;

    /// <summary>Half the line width, in physical pixels.</summary>
    [FieldOffset(72)]
    public float HalfWidthPixels;

    /// <summary>Gets how many bytes to upload.</summary>
    public static int SizeInBytes => Marshal.SizeOf<AxisConstants>();
}

/// <summary>
/// Draws the origin triad and the corner orientation gizmo (P2-T11, and the gizmo P2-T08 owes).
/// </summary>
/// <remarks>
/// <para>
/// <b>One shader and one vertex buffer serve both.</b> They differ only in the matrix that takes
/// an axis vector to clip space — the triad uses the ordinary view-projection, the gizmo one built
/// from the camera's rotation alone, scaled to a fixed pixel size and shifted into a corner. Two
/// shaders would mean two copies of quad-expansion arithmetic that are supposed to agree, and the
/// gizmo would eventually stop matching the axes it exists to report on.
/// </para>
/// <para>
/// <b>The gizmo has no perspective and no translation.</b> Only the rotation matters: it answers
/// "which way is the model turned", and a gizmo that grew as you zoomed or slid as you panned
/// would be answering a question nobody asked. Building it as a matrix rather than special-casing
/// the shader is what keeps the two paths honest.
/// </para>
/// <para>
/// <b>The triad is depth-tested and the gizmo is not</b>, which is the difference between a
/// landmark and an overlay. An un-occluded triad reads as three lines floating in front of the
/// model rather than as an axis marker standing in it — the eye takes "drawn over a solid" to mean
/// "nearer than the solid", and no amount of colour argues it out of that. The gizmo is genuinely
/// not in the scene, so it is drawn on top and looks right there.
/// </para>
/// <para>
/// Neither writes depth. They are drawn last and nothing needs to sort against them, and a triad
/// that wrote depth would punch three lines through anything drawn afterwards.
/// </para>
/// </remarks>
public sealed class AxisOverlayPass : IDisposable
{
    /// <summary>The shader file this pass is built from.</summary>
    public const string ShaderFile = "Axes.hlsl";

    /// <summary>Bytes per segment: two endpoints and a colour.</summary>
    public const int SegmentStride = 40;

    private const int SegmentCount = 3;

    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _occluded;
    private readonly ID3D12PipelineState _onTop;
    private readonly ID3D12Resource _segments;
    private readonly VertexBufferView _segmentView;

    private bool _disposed;

    /// <summary>Builds the pipeline state and the three axis segments.</summary>
    /// <param name="device">The device to create on.</param>
    /// <param name="style">The colours to bake into the segments.</param>
    /// <param name="renderTargetFormat">The format of the target being drawn into.</param>
    /// <param name="depthFormat">The depth format, matching the depth buffer.</param>
    /// <param name="optimiseShaders">Whether to compile optimised. Tests turn this off.</param>
    /// <param name="sampleCount">
    /// How many samples per pixel the target has. Must match the target this pass draws into: a
    /// pipeline state carries its sample count and the device refuses a mismatch outright.
    /// </param>
    /// <exception cref="ShaderCompilationException">The shader will not compile.</exception>
    public AxisOverlayPass(
        ID3D12Device device,
        AxisStyle style = default,
        Format renderTargetFormat = SwapChainTarget.BackBufferFormat,
        Format depthFormat = DepthBuffer.DepthFormat,
        bool optimiseShaders = true,
        int sampleCount = 1)
    {
        ArgumentNullException.ThrowIfNull(device);

        AxisStyle colours = style.WidthPixels <= 0 ? AxisStyle.Default : style;

        _rootSignature = CreateRootSignature(device);

        GraphicsPipelineStateDescription description = new()
        {
            RootSignature = _rootSignature,

            VertexShader = ShaderLibrary.Compile(
                ShaderFile, "VSMain", ShaderLibrary.VertexProfile, optimiseShaders),

            PixelShader = ShaderLibrary.Compile(
                ShaderFile, "PSMain", ShaderLibrary.PixelProfile, optimiseShaders),

            InputLayout = new InputLayoutDescription(
                new InputElementDescription(
                    "AXISSTART", 0, Format.R32G32B32_Float, 0, 0, InputClassification.PerInstanceData, 1),
                new InputElementDescription(
                    "AXISEND", 0, Format.R32G32B32_Float, 12, 0, InputClassification.PerInstanceData, 1),
                new InputElementDescription(
                    "AXISCOLOUR", 0, Format.R32G32B32A32_Float, 24, 0, InputClassification.PerInstanceData, 1)),

            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = BlendDescription.AlphaBlend,

            // Tested but not written: the triad sits in the scene and is occluded by it, while
            // writing depth would punch three lines through anything drawn afterwards.
            DepthStencilState = DepthStencilDescription.Read,
            DepthStencilFormat = depthFormat,
            RenderTargetFormats = [renderTargetFormat],
            SampleDescription = new SampleDescription((uint)sampleCount, 0),
            SampleMask = uint.MaxValue,
        };

        _occluded = device.CreateGraphicsPipelineState(description);
        _occluded.Name = "axis overlay (occluded)";

        description.DepthStencilState = DepthStencilDescription.None;
        _onTop = device.CreateGraphicsPipelineState(description);
        _onTop.Name = "axis overlay (on top)";

        // Unit vectors. Length is decided by the transform, so the same three segments serve a
        // triad scaled to a model a kilometre across and a gizmo thirty pixels tall.
        float[] segments =
        [
            0, 0, 0, 1, 0, 0, colours.AxisX.R, colours.AxisX.G, colours.AxisX.B, colours.AxisX.A,
            0, 0, 0, 0, 1, 0, colours.AxisY.R, colours.AxisY.G, colours.AxisY.B, colours.AxisY.A,
            0, 0, 0, 0, 0, 1, colours.AxisZ.R, colours.AxisZ.G, colours.AxisZ.B, colours.AxisZ.A,
        ];

        _segments = device.CreateCommittedResource(
            HeapType.Upload,
            HeapFlags.None,
            ResourceDescription.Buffer((ulong)(SegmentCount * SegmentStride)),
            ResourceStates.GenericRead);

        _segments.Name = "axis segments";
        _segments.SetData(MemoryMarshal.AsBytes(segments.AsSpan()));

        _segmentView = new VertexBufferView(
            _segments.GPUVirtualAddress, SegmentCount * SegmentStride, SegmentStride);
    }

    /// <summary>Records one set of axes.</summary>
    /// <param name="commands">An open command list with a render target bound.</param>
    /// <param name="constantBufferAddress">Where the caller wrote an <see cref="AxisConstants"/>.</param>
    /// <param name="onTop">
    /// Whether to ignore the depth buffer. <see langword="false"/> for the triad, which belongs in
    /// the scene and should be hidden by it; <see langword="true"/> for the gizmo, which does not.
    /// </param>
    public void Draw(
        ID3D12GraphicsCommandList commands, ulong constantBufferAddress, bool onTop = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(commands);

        commands.SetGraphicsRootSignature(_rootSignature);
        commands.SetPipelineState(onTop ? _onTop : _occluded);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        commands.SetGraphicsRootConstantBufferView(0, constantBufferAddress);
        commands.IASetVertexBuffers(0, _segmentView);
        commands.DrawInstanced(4, SegmentCount, 0, 0);
    }

    /// <summary>
    /// Builds the transform for the triad at the world origin.
    /// </summary>
    /// <param name="camera">The camera being rendered through.</param>
    /// <param name="sceneBounds">The scene, for the projection's depth range.</param>
    /// <param name="origin">The snapshot origin the geometry is relative to.</param>
    /// <param name="length">How long each arm should be, in metres.</param>
    /// <returns>The transform.</returns>
    public static Matrix4x4 TriadTransform(
        Camera camera, Bounds3d sceneBounds, Vec3d origin, double length)
    {
        ArgumentNullException.ThrowIfNull(camera);

        Mat4d viewProjection = camera.ProjectionMatrix(sceneBounds)
            * Mat4d.LookAt(camera.Position - origin, camera.Target - origin, camera.Up);

        // The axis vectors are unit length, so scaling them here is what sets the arm length --
        // and the triad is drawn in the snapshot's shifted frame, so the world origin sits at
        // -origin rather than at zero.
        Mat4d placement = Mat4d.FromTranslation(-origin) * Mat4d.FromScale(length);

        return ToMatrix(viewProjection * placement);
    }

    /// <summary>
    /// Builds the transform for the corner gizmo.
    /// </summary>
    /// <param name="camera">The camera whose rotation the gizmo reports.</param>
    /// <param name="style">Size, margin and which corner.</param>
    /// <param name="viewportWidth">Viewport width in physical pixels.</param>
    /// <param name="viewportHeight">Viewport height in physical pixels.</param>
    /// <returns>The transform.</returns>
    /// <remarks>
    /// <para>
    /// Rotation only, with no projection and no translation. The gizmo answers "which way is the
    /// model turned", so a version that grew as the user zoomed or slid as they panned would be
    /// answering a question nobody asked.
    /// </para>
    /// <para>
    /// A world direction is taken to view space by the camera's basis, and its x and y are then
    /// the screen direction directly — which is an orthographic projection, arrived at by leaving
    /// the perspective out rather than by adding anything.
    /// </para>
    /// </remarks>
    public static Matrix4x4 GizmoTransform(
        Camera camera, AxisStyle style, int viewportWidth, int viewportHeight)
    {
        ArgumentNullException.ThrowIfNull(camera);

        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return Matrix4x4.Identity;
        }

        Vec3d right = camera.Right;
        Vec3d up = camera.Up;

        // Normalised device coordinates span -1 to 1 across the viewport, so one unit is half the
        // width in pixels. Scaling each axis separately is what keeps the gizmo square rather than
        // stretched with the window.
        double scaleX = style.GizmoPixels / (viewportWidth * 0.5);
        double scaleY = style.GizmoPixels / (viewportHeight * 0.5);

        double insetX = (style.GizmoPixels + style.GizmoMarginPixels) / (viewportWidth * 0.5);
        double insetY = (style.GizmoPixels + style.GizmoMarginPixels) / (viewportHeight * 0.5);

        (double cx, double cy) = style.Corner switch
        {
            GizmoCorner.BottomLeft => (-1 + insetX, -1 + insetY),
            GizmoCorner.BottomRight => (1 - insetX, -1 + insetY),
            GizmoCorner.TopLeft => (-1 + insetX, 1 - insetY),
            _ => (1 - insetX, 1 - insetY),
        };

        // Rows one and two project a world direction onto the screen axes; row three pins depth
        // at the near plane, and row four leaves w at one so nothing divides.
        return new Matrix4x4(
            (float)(right.X * scaleX), (float)(right.Y * scaleX), (float)(right.Z * scaleX), (float)cx,
            (float)(up.X * scaleY), (float)(up.Y * scaleY), (float)(up.Z * scaleY), (float)cy,
            0, 0, 0, 0.5f,
            0, 0, 0, 1);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _segments.Dispose();
        _onTop.Dispose();
        _occluded.Dispose();
        _rootSignature.Dispose();
    }

    private static Matrix4x4 ToMatrix(Mat4d m) => new(
        (float)m.M11, (float)m.M12, (float)m.M13, (float)m.M14,
        (float)m.M21, (float)m.M22, (float)m.M23, (float)m.M24,
        (float)m.M31, (float)m.M32, (float)m.M33, (float)m.M34,
        (float)m.M41, (float)m.M42, (float)m.M43, (float)m.M44);

    private static ID3D12RootSignature CreateRootSignature(ID3D12Device device)
    {
        RootSignatureDescription1 description = new(
            RootSignatureFlags.AllowInputAssemblerInputLayout,
            [
                new RootParameter1(
                    RootParameterType.ConstantBufferView,
                    new RootDescriptor1(0, 0, RootDescriptorFlags.DataStatic),
                    ShaderVisibility.All),
            ],
            []);

        ID3D12RootSignature rootSignature = device.CreateRootSignature(in description);
        rootSignature.Name = "axis overlay root signature";

        return rootSignature;
    }
}
