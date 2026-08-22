using System.Collections.Immutable;
using OpenMCAD.Kernel.Operations;
using OpenMCAD.Kernel.Threading;
using OpenMCAD.Math;

namespace OpenMCAD.Kernel;

/// <summary>
/// What a kernel implementation can do.
/// </summary>
/// <param name="Name">A short identifier, such as <c>occt</c> or <c>fake</c>.</param>
/// <param name="Version">The underlying kernel version, for logs and repro bundles.</param>
/// <param name="ProducesExactMassProperties">
/// Whether mass properties come from exact integration. <c>FakeKernel</c> says false for anything
/// beyond primitives and prisms, which lets the contract tests demand exactness only where it is
/// actually promised.
/// </param>
/// <param name="SupportsRetryLadder">Whether fragile operations escalate through PLAN.md 5.2.4.</param>
public readonly record struct KernelCapabilities(
    string Name,
    string Version,
    bool ProducesExactMassProperties,
    bool SupportsRetryLadder);

/// <summary>
/// The geometry kernel, as the rest of OpenMCAD sees it.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0002. The abstraction is at the level of <b>modelling operations</b>, not geometric
/// entities. Faces, edges, surfaces, curves, and tolerances are never exposed; they cross as opaque
/// handles (<see cref="SubEntity"/>) and nothing above this assembly may interpret them. Full
/// entity-level abstractions have a poor track record because kernels differ in topology model and
/// tolerance semantics, so such an abstraction degrades to a lowest common denominator and becomes
/// a permanent tax.
/// </para>
/// <para>
/// Two things justify the cost of the abstraction, and both are worth more than kernel portability:
/// the parametric engine, naming layer, undo, and document model become testable against
/// <c>FakeKernel</c> in seconds rather than minutes, and persistent naming lives in our code, where
/// it belongs.
/// </para>
/// <para>
/// <b>Every shape-producing operation returns a <see cref="HistoryMap"/>.</b> Not optionally, and
/// not on request. It is the raw material for topological naming (ADR-0005), it exists only while
/// the operation runs, and an implementation that omits it has destroyed information nothing can
/// recover.
/// </para>
/// <para>
/// <b>Everything is asynchronous</b> because everything is marshalled onto the single kernel thread
/// (ADR-0004). Awaiting one of these methods from the kernel thread deadlocks; the debug guard in
/// <see cref="KernelThreadGuard"/> catches it.
/// </para>
/// <para>
/// <b>Returned <see cref="KernelShapeHandle"/> values are owned by the caller.</b> Dispose them.
/// Input <see cref="KernelShape"/> values are borrowed and remain owned by whoever holds the handle.
/// </para>
/// <para>
/// This is the Phase 1 subset: primitives, extrude, revolve, boolean, blends, queries,
/// tessellation, and serialisation. ADR-0002 estimates 200 to 300 operations for a complete MCAD
/// application; the shape of the surface is settled here so the rest is addition rather than
/// redesign.
/// </para>
/// </remarks>
public interface IGeometryKernel : IAsyncDisposable
{
    /// <summary>Gets what this implementation can do.</summary>
    KernelCapabilities Capabilities { get; }

    // --- Primitives -------------------------------------------------------------------------------

    /// <summary>Creates a rectangular box.</summary>
    /// <param name="definition">The box to create.</param>
    /// <param name="request">Priority and tolerance for this call.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    ValueTask<OperationResult> CreateBoxAsync(
        BoxDefinition definition,
        KernelRequest request = default,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a cylinder.</summary>
    /// <param name="definition">The cylinder to create.</param>
    /// <param name="request">Priority and tolerance for this call.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    ValueTask<OperationResult> CreateCylinderAsync(
        CylinderDefinition definition,
        KernelRequest request = default,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a sphere.</summary>
    /// <param name="definition">The sphere to create.</param>
    /// <param name="request">Priority and tolerance for this call.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    ValueTask<OperationResult> CreateSphereAsync(
        SphereDefinition definition,
        KernelRequest request = default,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a cone or truncated cone.</summary>
    /// <param name="definition">The cone to create.</param>
    /// <param name="request">Priority and tolerance for this call.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    ValueTask<OperationResult> CreateConeAsync(
        ConeDefinition definition,
        KernelRequest request = default,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a torus.</summary>
    /// <param name="definition">The torus to create.</param>
    /// <param name="request">Priority and tolerance for this call.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    ValueTask<OperationResult> CreateTorusAsync(
        TorusDefinition definition,
        KernelRequest request = default,
        CancellationToken cancellationToken = default);

    // --- Profiles (Phase 1 scaffolding; real sketches arrive in Phase 4) ---------------------------

    /// <summary>Creates a closed planar profile that can be swept.</summary>
    /// <param name="definition">The profile to create.</param>
    /// <param name="request">Priority and tolerance for this call.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    ValueTask<OperationResult> CreatePolygonProfileAsync(
        PolygonProfileDefinition definition,
        KernelRequest request = default,
        CancellationToken cancellationToken = default);

    // --- Construction ------------------------------------------------------------------------------

    /// <summary>Sweeps a profile along a straight path.</summary>
    /// <param name="definition">The extrusion to perform.</param>
    /// <param name="request">Priority and tolerance for this call.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    ValueTask<OperationResult> ExtrudeAsync(
        ExtrudeDefinition definition,
        KernelRequest request = default,
        CancellationToken cancellationToken = default);

    /// <summary>Sweeps a profile about an axis.</summary>
    /// <param name="definition">The revolution to perform.</param>
    /// <param name="request">Priority and tolerance for this call.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    ValueTask<OperationResult> RevolveAsync(
        RevolveDefinition definition,
        KernelRequest request = default,
        CancellationToken cancellationToken = default);

    // --- Modification --------------------------------------------------------------------------------

    /// <summary>Combines bodies.</summary>
    /// <param name="definition">The boolean to perform.</param>
    /// <param name="request">Priority and tolerance for this call.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    ValueTask<OperationResult> BooleanAsync(
        BooleanDefinition definition,
        KernelRequest request = default,
        CancellationToken cancellationToken = default);

    /// <summary>Rounds edges.</summary>
    /// <param name="definition">The fillet to apply.</param>
    /// <param name="request">Priority and tolerance for this call.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    /// <remarks>
    /// May return <see cref="OperationResult.Degraded"/> when some edges succeed and others do not.
    /// That is a useful result, not a failure — see the remarks on <see cref="OperationResult"/>.
    /// </remarks>
    ValueTask<OperationResult> FilletAsync(
        FilletDefinition definition,
        KernelRequest request = default,
        CancellationToken cancellationToken = default);

    /// <summary>Bevels edges.</summary>
    /// <param name="definition">The chamfer to apply.</param>
    /// <param name="request">Priority and tolerance for this call.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    ValueTask<OperationResult> ChamferAsync(
        ChamferDefinition definition,
        KernelRequest request = default,
        CancellationToken cancellationToken = default);

    // --- Queries ---------------------------------------------------------------------------------------

    /// <summary>Computes volume, area, centroid, and inertia.</summary>
    /// <param name="shape">The shape to measure.</param>
    /// <param name="density">Density in kilograms per cubic metre.</param>
    /// <param name="request">Priority and tolerance for this call.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    ValueTask<KernelResult<MassProperties>> ComputeMassPropertiesAsync(
        KernelShape shape,
        double density = 1000.0,
        KernelRequest request = default,
        CancellationToken cancellationToken = default);

    /// <summary>Computes the axis-aligned bound.</summary>
    /// <param name="shape">The shape to bound.</param>
    /// <param name="request">Priority and tolerance for this call.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    ValueTask<KernelResult<Bounds3d>> ComputeBoundsAsync(
        KernelShape shape,
        KernelRequest request = default,
        CancellationToken cancellationToken = default);

    /// <summary>Counts the shape's topological entities.</summary>
    /// <param name="shape">The shape to count.</param>
    /// <param name="request">Priority and tolerance for this call.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    ValueTask<KernelResult<TopologyCounts>> CountTopologyAsync(
        KernelShape shape,
        KernelRequest request = default,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the shape's entities of one kind, in canonical order.</summary>
    /// <param name="shape">The shape to enumerate.</param>
    /// <param name="kind">Which kind of entity to list.</param>
    /// <param name="request">Priority and tolerance for this call.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    /// <remarks>
    /// The order is canonical and reproducible, not an artefact of iteration. Determinism depends
    /// on it (ADR-0011), and so does every test that selects "the third edge".
    /// </remarks>
    ValueTask<KernelResult<ImmutableArray<SubEntity>>> EnumerateAsync(
        KernelShape shape,
        SubEntityKind kind,
        KernelRequest request = default,
        CancellationToken cancellationToken = default);

    /// <summary>Checks the shape for structural validity.</summary>
    /// <param name="shape">The shape to check.</param>
    /// <param name="request">Priority and tolerance for this call.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    ValueTask<KernelResult<ShapeValidity>> CheckValidityAsync(
        KernelShape shape,
        KernelRequest request = default,
        CancellationToken cancellationToken = default);

    // --- Tessellation --------------------------------------------------------------------------------------

    /// <summary>Triangulates the shape for display or export.</summary>
    /// <param name="shape">The shape to tessellate.</param>
    /// <param name="options">How finely to tessellate.</param>
    /// <param name="request">Priority and tolerance for this call.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    ValueTask<KernelResult<MeshBuffer>> TriangulateAsync(
        KernelShape shape,
        TessellationOptions options = default,
        KernelRequest request = default,
        CancellationToken cancellationToken = default);

    // --- Serialisation ---------------------------------------------------------------------------------------

    /// <summary>
    /// Writes the shape to the kernel's native boundary-representation format.
    /// </summary>
    /// <param name="shape">The shape to write.</param>
    /// <param name="request">Priority and tolerance for this call.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    /// <remarks>
    /// The bytes are opaque and belong to the kernel, not to OpenMCAD. They are a
    /// <i>cache</i> (ADR-0010) — always regenerable by rebuilding, never the source of truth. A
    /// document whose geometry cache cannot be read must rebuild, not fail.
    /// </remarks>
    ValueTask<KernelResult<ImmutableArray<byte>>> WriteBRepAsync(
        KernelShape shape,
        KernelRequest request = default,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a shape previously written by <see cref="WriteBRepAsync"/>.</summary>
    /// <param name="data">The bytes to read.</param>
    /// <param name="request">Priority and tolerance for this call.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    /// <remarks>
    /// The resulting entities carry <see cref="OperationRole.Imported"/>: they arrived without
    /// generative provenance, so the naming layer must fall back to geometric matching for them.
    /// </remarks>
    ValueTask<OperationResult> ReadBRepAsync(
        ReadOnlyMemory<byte> data,
        KernelRequest request = default,
        CancellationToken cancellationToken = default);

    /// <summary>Writes shapes as a STEP AP242 file.</summary>
    /// <param name="shapes">The shapes to write.</param>
    /// <param name="destination">Where to write. Left open.</param>
    /// <param name="request">Priority and tolerance for this call.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    ValueTask<KernelResult<int>> WriteStepAsync(
        ImmutableArray<KernelShape> shapes,
        Stream destination,
        KernelRequest request = default,
        CancellationToken cancellationToken = default);
}
