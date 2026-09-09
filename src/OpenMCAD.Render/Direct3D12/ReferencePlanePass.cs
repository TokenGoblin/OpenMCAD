using System.Numerics;
using System.Runtime.InteropServices;

using OpenMCAD.Math;

using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace OpenMCAD.Render.Direct3D12;

/// <summary>How reference planes are drawn.</summary>
/// <param name="Colour">The tint, before the edge fade.</param>
/// <param name="Opacity">How solid the middle of a plane is, from nought to one.</param>
/// <param name="SizeFraction">
/// How big the square standing for a plane is, as a fraction of the scene's diagonal.
/// </param>
/// <param name="FadeFraction">
/// How much of the way in from the rim the plane fades out, as a fraction of its half-size.
/// </param>
public readonly record struct PlaneStyle(
    Color4 Colour, float Opacity, float SizeFraction, float FadeFraction)
{
    /// <summary>Gets the settings used when a caller names none.</summary>
    /// <remarks>
    /// Faint on purpose. A datum plane is scenery: it has to be findable when looked for and
    /// invisible when not, and the commonest way to get this wrong is to make it solid enough to
    /// compete with the model it exists to locate.
    /// </remarks>
    public static PlaneStyle Default { get; } =
        new(new Color4(0.42f, 0.55f, 0.78f, 1.0f), 0.13f, 0.75f, 0.35f);
}

/// <summary>Constants for one reference plane draw.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct PlaneConstants
{
    /// <summary>World to clip, in the snapshot's shifted frame.</summary>
    public Matrix4x4 ViewProjection;

    /// <summary>The tint.</summary>
    public Vector3 Colour;

    /// <summary>How solid the middle is.</summary>
    public float Alpha;

    /// <summary>Half the side of the square, in metres.</summary>
    public float HalfSize;

    /// <summary>How far in from the rim the fade starts, as a fraction.</summary>
    public float FadeFraction;

    private readonly float _pad0;
    private readonly float _pad1;
}

/// <summary>
/// Draws the datum planes a part is built on (P2-T11).
/// </summary>
/// <remarks>
/// <para>
/// <b>The square is sized from the scene, not from the camera.</b> A reference plane is
/// conceptually infinite and what gets drawn stands for it, so some size has to be invented. Taking
/// it from the camera would make the planes hold still on screen while the model grew and shrank
/// against them, which is the same failure the grid avoids by taking its spacing from the scene —
/// a reference that changes size as you zoom is worse than one at the wrong scale. Taking it from
/// the scene means the planes grow with the model and stay the same thing relative to it.
/// </para>
/// <para>
/// <b>Depth-tested, and writing none.</b> Tested because a datum plane belongs in the scene and
/// should be hidden by the solid standing on it — the same reasoning that makes the origin triad
/// depth-tested and the corner gizmo not. Not written because three datum planes meeting at the
/// origin overlap across most of the region they share, and a plane that wrote depth would hide the
/// two behind it outright rather than blending with them.
/// </para>
/// <para>
/// <b>This is the one translucent thing here that does not go through weighted-blended OIT</b>
/// (P2-T10), and the reason is worth stating rather than assuming. What that machinery buys is
/// freedom from ordering when fragments genuinely occlude one another — a housing containing its own
/// contents, faces of one body overlapping from the current angle. Three flat quads through the
/// origin are not that: nothing is in front of anything in a way that changes what the user
/// concludes, and the visible consequence of drawing them in an arbitrary order is a slightly
/// different tint where two cross. Routing them through the accumulation buffers would cost two
/// full-size targets and a composite for a difference nobody can point at.
/// </para>
/// <para>
/// <b>Two-sided.</b> A datum plane is a reference rather than a surface, and a user who orbits
/// beneath one and finds it gone would reasonably conclude it had been switched off.
/// </para>
/// </remarks>
public sealed class ReferencePlanePass : IDisposable
{
    /// <summary>The shader file this pass is built from.</summary>
    public const string ShaderFile = "Planes.hlsl";

    /// <summary>Bytes per plane: an origin and two in-plane directions.</summary>
    public const int PlaneStride = 36;

    /// <summary>How many planes one pass will draw before it stops.</summary>
    /// <remarks>
    /// A fixed buffer rather than one that grows, because the number of datum planes in a document
    /// is three plus whatever a user has added by hand, and a viewport showing sixty-four of them
    /// has a problem this pass cannot fix.
    /// </remarks>
    public const int MaximumPlanes = 64;

    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _state;
    private readonly ID3D12Resource _planes;
    private readonly VertexBufferView _planeView;
    private readonly float[] _staging = new float[MaximumPlanes * 9];

    private int _uploaded;
    private bool _disposed;

    /// <summary>Builds the pipeline state and the buffer the planes are uploaded into.</summary>
    /// <param name="device">The device to create on.</param>
    /// <param name="renderTargetFormat">The format of the target being drawn into.</param>
    /// <param name="depthFormat">The depth format, matching the depth buffer.</param>
    /// <param name="optimiseShaders">Whether to compile optimised. Tests turn this off.</param>
    /// <param name="sampleCount">
    /// How many samples per pixel the target has. Must match the target this pass draws into.
    /// </param>
    /// <exception cref="ShaderCompilationException">The shader will not compile.</exception>
    public ReferencePlanePass(
        ID3D12Device device,
        Format renderTargetFormat = SwapChainTarget.BackBufferFormat,
        Format depthFormat = DepthBuffer.DepthFormat,
        bool optimiseShaders = true,
        int sampleCount = 1)
    {
        ArgumentNullException.ThrowIfNull(device);

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
                    "PLANEORIGIN", 0, Format.R32G32B32_Float, 0, 0, InputClassification.PerInstanceData, 1),
                new InputElementDescription(
                    "PLANERIGHT", 0, Format.R32G32B32_Float, 12, 0, InputClassification.PerInstanceData, 1),
                new InputElementDescription(
                    "PLANEUP", 0, Format.R32G32B32_Float, 24, 0, InputClassification.PerInstanceData, 1)),

            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RasterizerState = RasterizerDescription.CullNone,

            // Premultiplied: SourceBlend is One, not SourceAlpha. Planes.hlsl multiplies through
            // to match, and the pixel test that reads the middle of a plane back is what catches
            // the pairing being wrong -- straight-alpha against this state draws a datum solid.
            BlendState = BlendDescription.AlphaBlend,

            // Tested, not written. A datum plane belongs in the scene and should be hidden by the
            // solid standing on it; writing depth would let the first plane drawn hide the two
            // behind it outright rather than blending with them.
            DepthStencilState = DepthStencilDescription.Read,
            DepthStencilFormat = depthFormat,
            RenderTargetFormats = [renderTargetFormat],
            SampleDescription = new SampleDescription((uint)sampleCount, 0),
            SampleMask = uint.MaxValue,
        };

        _state = device.CreateGraphicsPipelineState(description);
        _state.Name = "reference planes";

        _planes = device.CreateCommittedResource(
            HeapType.Upload,
            HeapFlags.None,
            ResourceDescription.Buffer((ulong)(MaximumPlanes * PlaneStride)),
            ResourceStates.GenericRead);

        _planes.Name = "reference planes";

        _planeView = new VertexBufferView(
            _planes.GPUVirtualAddress, MaximumPlanes * PlaneStride, PlaneStride);
    }

    /// <summary>Gets how many planes the last <see cref="Upload"/> accepted.</summary>
    public int PlanesUploaded => _uploaded;

    /// <summary>
    /// Half the side of the square that stands for a plane, for a scene of this size.
    /// </summary>
    /// <param name="bounds">The scene's extent.</param>
    /// <param name="style">The style, whose <see cref="PlaneStyle.SizeFraction"/> decides it.</param>
    /// <returns>The half-size in metres, never zero.</returns>
    /// <remarks>
    /// An empty or degenerate scene still needs a size, or the planes collapse to nothing and a
    /// brand-new document shows no datums at all — which is exactly when a user most needs to see
    /// them, having nothing else to sketch on.
    /// </remarks>
    public static float HalfSizeFor(Bounds3d bounds, PlaneStyle style)
    {
        double diagonal = bounds.DiagonalLength;
        double half = diagonal * style.SizeFraction / 2;

        return half <= Tolerance.Linear ? 1.0f : (float)half;
    }

    /// <summary>Uploads the planes to draw.</summary>
    /// <param name="planes">The planes, in world coordinates.</param>
    /// <param name="origin">The snapshot origin the draw is relative to.</param>
    /// <remarks>
    /// The origin shift happens here rather than in the shader, so the float positions the GPU sees
    /// are small however far the model sits from the world origin — the same reason a mesh's
    /// positions are stored relative (P2-T04). Anything past <see cref="MaximumPlanes"/> is dropped
    /// rather than growing the buffer mid-frame.
    /// </remarks>
    public void Upload(IReadOnlyList<DisplayPlane> planes, Vec3d origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(planes);

        _uploaded = System.Math.Min(planes.Count, MaximumPlanes);

        for (int i = 0; i < _uploaded; ++i)
        {
            DisplayPlane plane = planes[i];
            Vec3d relative = plane.Origin - origin;
            int at = i * 9;

            _staging[at + 0] = (float)relative.X;
            _staging[at + 1] = (float)relative.Y;
            _staging[at + 2] = (float)relative.Z;
            _staging[at + 3] = (float)plane.Right.X;
            _staging[at + 4] = (float)plane.Right.Y;
            _staging[at + 5] = (float)plane.Right.Z;
            _staging[at + 6] = (float)plane.Up.X;
            _staging[at + 7] = (float)plane.Up.Y;
            _staging[at + 8] = (float)plane.Up.Z;
        }

        if (_uploaded > 0)
        {
            _planes.SetData(
                MemoryMarshal.AsBytes(_staging.AsSpan(0, _uploaded * 9)));
        }
    }

    /// <summary>Records the planes uploaded by the last <see cref="Upload"/>.</summary>
    /// <param name="commands">An open command list with a render target bound.</param>
    /// <param name="constantBufferAddress">Where the caller wrote a <see cref="PlaneConstants"/>.</param>
    public void Draw(ID3D12GraphicsCommandList commands, ulong constantBufferAddress)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(commands);

        if (_uploaded == 0)
        {
            return;
        }

        commands.SetGraphicsRootSignature(_rootSignature);
        commands.SetPipelineState(_state);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.SetGraphicsRootConstantBufferView(0, constantBufferAddress);
        commands.IASetVertexBuffers(0, _planeView);
        commands.DrawInstanced(6, (uint)_uploaded, 0, 0);
    }

    /// <summary>Builds the constants for one frame.</summary>
    /// <param name="camera">The camera being rendered through.</param>
    /// <param name="sceneBounds">The scene, for the projection and for the size.</param>
    /// <param name="origin">The snapshot origin the geometry is relative to.</param>
    /// <param name="style">Colour, opacity, size and fade.</param>
    /// <returns>The constants.</returns>
    public static PlaneConstants ConstantsFor(
        Camera camera, Bounds3d sceneBounds, Vec3d origin, PlaneStyle style)
    {
        ArgumentNullException.ThrowIfNull(camera);

        Mat4d viewProjection = camera.ProjectionMatrix(sceneBounds)
            * Mat4d.LookAt(camera.Position - origin, camera.Target - origin, camera.Up);

        return new PlaneConstants
        {
            ViewProjection = ToMatrix(viewProjection),
            Colour = new Vector3(style.Colour.R, style.Colour.G, style.Colour.B),
            Alpha = style.Opacity,
            HalfSize = HalfSizeFor(sceneBounds, style),
            FadeFraction = style.FadeFraction,
        };
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _planes.Dispose();
        _state.Dispose();
        _rootSignature.Dispose();
    }

    private static ID3D12RootSignature CreateRootSignature(ID3D12Device device)
    {
        RootSignatureDescription1 description = new(
            RootSignatureFlags.AllowInputAssemblerInputLayout,
            [new RootParameter1(RootParameterType.ConstantBufferView, new RootDescriptor1(0, 0), ShaderVisibility.All)]);

        return device.CreateRootSignature(description);
    }

    private static Matrix4x4 ToMatrix(Mat4d m) => new(
        (float)m.M11, (float)m.M12, (float)m.M13, (float)m.M14,
        (float)m.M21, (float)m.M22, (float)m.M23, (float)m.M24,
        (float)m.M31, (float)m.M32, (float)m.M33, (float)m.M34,
        (float)m.M41, (float)m.M42, (float)m.M43, (float)m.M44);
}
