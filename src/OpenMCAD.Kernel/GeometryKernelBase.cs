using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenMCAD.Kernel.Operations;
using OpenMCAD.Kernel.Threading;
using OpenMCAD.Math;

namespace OpenMCAD.Kernel;

/// <summary>
/// The base every kernel implementation derives from.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class exists to make ADR-0004 unbypassable rather than merely stated.</b>
/// <see cref="IGeometryKernel"/>'s asynchronous methods are sealed here and forward to protected
/// synchronous methods that implementations override. The only path to an implementation runs
/// through <see cref="KernelDispatcher"/>, so kernel work cannot happen on the wrong thread even by
/// mistake — there is no public entry point that would allow it.
/// </para>
/// <para>
/// The alternative — an interface each implementation dispatches for itself — was rejected because
/// it makes the rule a convention that every future implementation must remember, and PLAN.md 12
/// lists calling the kernel off the dispatcher thread first among the things that are always wrong.
/// A rule worth stating that plainly is worth enforcing structurally.
/// </para>
/// <para>
/// Three other things are handled once here instead of in every operation of every implementation:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Pre-flight validation.</b> A definition that fails <see cref="IOperationDefinition.Validate"/>
/// is rejected without dispatching, so both kernels refuse the same input with the same message and
/// the kernel thread is never occupied by work that cannot succeed.
/// </description></item>
/// <item><description>
/// <b>The exception firewall.</b> An implementation that throws produces a
/// <see cref="OperationResult.Failed"/>, not a propagating exception. A rebuild must survive one
/// feature failing (PLAN.md 5.4 error containment), and that is much easier to guarantee if the
/// kernel layer simply never throws for geometric reasons.
/// </description></item>
/// <item><description>
/// <b>Shape ownership.</b> <see cref="Track"/> wraps a raw tag in an owning handle bound to this
/// kernel, so implementations cannot forget to make results disposable.
/// </description></item>
/// </list>
/// </remarks>
public abstract class GeometryKernelBase : IGeometryKernel, IKernelShapeReleaser
{
    private bool _disposed;

    /// <summary>Creates the kernel and its dispatcher.</summary>
    /// <param name="dispatcher">
    /// The dispatcher to marshal work onto. The kernel takes ownership and disposes it.
    /// </param>
    /// <param name="logger">Where to log.</param>
    protected GeometryKernelBase(KernelDispatcher dispatcher, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        Dispatcher = dispatcher;
        Logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public abstract KernelCapabilities Capabilities { get; }

    /// <summary>Gets the dispatcher all work is marshalled onto.</summary>
    protected KernelDispatcher Dispatcher { get; }

    /// <summary>Gets the logger.</summary>
    protected ILogger Logger { get; }

    // --- Primitives ---------------------------------------------------------------------------------

    /// <inheritdoc />
    public ValueTask<OperationResult> CreateBoxAsync(
        BoxDefinition definition,
        KernelRequest request = default,
        CancellationToken cancellationToken = default)
        => RunOperationAsync(definition, () => CreateBox(definition, request), request, cancellationToken);

    /// <inheritdoc />
    public ValueTask<OperationResult> CreateCylinderAsync(
        CylinderDefinition definition,
        KernelRequest request = default,
        CancellationToken cancellationToken = default)
        => RunOperationAsync(definition, () => CreateCylinder(definition, request), request, cancellationToken);

    /// <inheritdoc />
    public ValueTask<OperationResult> CreateSphereAsync(
        SphereDefinition definition,
        KernelRequest request = default,
        CancellationToken cancellationToken = default)
        => RunOperationAsync(definition, () => CreateSphere(definition, request), request, cancellationToken);

    /// <inheritdoc />
    public ValueTask<OperationResult> CreateConeAsync(
        ConeDefinition definition,
        KernelRequest request = default,
        CancellationToken cancellationToken = default)
        => RunOperationAsync(definition, () => CreateCone(definition, request), request, cancellationToken);

    /// <inheritdoc />
    public ValueTask<OperationResult> CreateTorusAsync(
        TorusDefinition definition,
        KernelRequest request = default,
        CancellationToken cancellationToken = default)
        => RunOperationAsync(definition, () => CreateTorus(definition, request), request, cancellationToken);

    /// <inheritdoc />
    public ValueTask<OperationResult> CreatePolygonProfileAsync(
        PolygonProfileDefinition definition,
        KernelRequest request = default,
        CancellationToken cancellationToken = default)
        => RunOperationAsync(definition, () => CreatePolygonProfile(definition, request), request, cancellationToken);

    // --- Construction and modification -------------------------------------------------------------------

    /// <inheritdoc />
    public ValueTask<OperationResult> ExtrudeAsync(
        ExtrudeDefinition definition,
        KernelRequest request = default,
        CancellationToken cancellationToken = default)
        => RunOperationAsync(definition, () => Extrude(definition, request), request, cancellationToken);

    /// <inheritdoc />
    public ValueTask<OperationResult> RevolveAsync(
        RevolveDefinition definition,
        KernelRequest request = default,
        CancellationToken cancellationToken = default)
        => RunOperationAsync(definition, () => Revolve(definition, request), request, cancellationToken);

    /// <inheritdoc />
    public ValueTask<OperationResult> BooleanAsync(
        BooleanDefinition definition,
        KernelRequest request = default,
        CancellationToken cancellationToken = default)
        => RunOperationAsync(definition, () => CombineBodies(definition, request), request, cancellationToken);

    /// <inheritdoc />
    public ValueTask<OperationResult> FilletAsync(
        FilletDefinition definition,
        KernelRequest request = default,
        CancellationToken cancellationToken = default)
        => RunOperationAsync(definition, () => Fillet(definition, request), request, cancellationToken);

    /// <inheritdoc />
    public ValueTask<OperationResult> ChamferAsync(
        ChamferDefinition definition,
        KernelRequest request = default,
        CancellationToken cancellationToken = default)
        => RunOperationAsync(definition, () => Chamfer(definition, request), request, cancellationToken);

    // --- Queries -------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public ValueTask<KernelResult<MassProperties>> ComputeMassPropertiesAsync(
        KernelShape shape,
        double density = 1000.0,
        KernelRequest request = default,
        CancellationToken cancellationToken = default)
        => RunQueryAsync(
            "ComputeMassProperties",
            shape,
            () => ComputeMassProperties(shape, density, request),
            request,
            cancellationToken);

    /// <inheritdoc />
    public ValueTask<KernelResult<Bounds3d>> ComputeBoundsAsync(
        KernelShape shape,
        KernelRequest request = default,
        CancellationToken cancellationToken = default)
        => RunQueryAsync(
            "ComputeBounds", shape, () => ComputeBounds(shape, request), request, cancellationToken);

    /// <inheritdoc />
    public ValueTask<KernelResult<TopologyCounts>> CountTopologyAsync(
        KernelShape shape,
        KernelRequest request = default,
        CancellationToken cancellationToken = default)
        => RunQueryAsync(
            "CountTopology", shape, () => CountTopology(shape, request), request, cancellationToken);

    /// <inheritdoc />
    public ValueTask<KernelResult<ImmutableArray<SubEntity>>> EnumerateAsync(
        KernelShape shape,
        SubEntityKind kind,
        KernelRequest request = default,
        CancellationToken cancellationToken = default)
        => RunQueryAsync(
            "Enumerate", shape, () => Enumerate(shape, kind, request), request, cancellationToken);

    /// <inheritdoc />
    public ValueTask<KernelResult<ShapeValidity>> CheckValidityAsync(
        KernelShape shape,
        KernelRequest request = default,
        CancellationToken cancellationToken = default)
        => RunQueryAsync(
            "CheckValidity", shape, () => CheckValidity(shape, request), request, cancellationToken);

    /// <inheritdoc />
    public ValueTask<KernelResult<MeshBuffer>> TriangulateAsync(
        KernelShape shape,
        TessellationOptions options = default,
        KernelRequest request = default,
        CancellationToken cancellationToken = default)
        => RunQueryAsync(
            "Triangulate",
            shape,
            () => Triangulate(shape, options.ChordalDeviation <= 0 ? TessellationOptions.Display : options, request),
            request,
            cancellationToken);

    /// <inheritdoc />
    public ValueTask<KernelResult<ImmutableArray<byte>>> WriteBRepAsync(
        KernelShape shape,
        KernelRequest request = default,
        CancellationToken cancellationToken = default)
        => RunQueryAsync(
            "WriteBRep", shape, () => WriteBRep(shape, request), request, cancellationToken);

    /// <inheritdoc />
    public ValueTask<OperationResult> ReadBRepAsync(
        ReadOnlyMemory<byte> data,
        KernelRequest request = default,
        CancellationToken cancellationToken = default)
        => RunOperationAsync(
            definition: null,
            () => ReadBRep(data, request),
            request,
            cancellationToken,
            operationName: "ReadBRep");

    /// <inheritdoc />
    public ValueTask<KernelResult<int>> WriteStepAsync(
        ImmutableArray<KernelShape> shapes,
        Stream destination,
        KernelRequest request = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (shapes.IsDefaultOrEmpty)
        {
            return ValueTask.FromResult(KernelResult.Fail<int>(
                KernelDiagnosticCodes.EmptySelection,
                "STEP export needs at least one body."));
        }

        return RunQueryAsync(
            "WriteStep",
            KernelShape.None,
            () => WriteStep(shapes, destination, request),
            request,
            cancellationToken);
    }

    // --- What implementations provide -------------------------------------------------------------------------

    /// <summary>Creates a box. Runs on the kernel thread.</summary>
    /// <param name="definition">The validated definition.</param>
    /// <param name="request">Per-call options.</param>
    protected abstract OperationResult CreateBox(BoxDefinition definition, KernelRequest request);

    /// <summary>Creates a cylinder. Runs on the kernel thread.</summary>
    /// <param name="definition">The validated definition.</param>
    /// <param name="request">Per-call options.</param>
    protected abstract OperationResult CreateCylinder(CylinderDefinition definition, KernelRequest request);

    /// <summary>Creates a sphere. Runs on the kernel thread.</summary>
    /// <param name="definition">The validated definition.</param>
    /// <param name="request">Per-call options.</param>
    protected abstract OperationResult CreateSphere(SphereDefinition definition, KernelRequest request);

    /// <summary>Creates a cone. Runs on the kernel thread.</summary>
    /// <param name="definition">The validated definition.</param>
    /// <param name="request">Per-call options.</param>
    protected abstract OperationResult CreateCone(ConeDefinition definition, KernelRequest request);

    /// <summary>Creates a torus. Runs on the kernel thread.</summary>
    /// <param name="definition">The validated definition.</param>
    /// <param name="request">Per-call options.</param>
    protected abstract OperationResult CreateTorus(TorusDefinition definition, KernelRequest request);

    /// <summary>Creates a planar profile. Runs on the kernel thread.</summary>
    /// <param name="definition">The validated definition.</param>
    /// <param name="request">Per-call options.</param>
    protected abstract OperationResult CreatePolygonProfile(
        PolygonProfileDefinition definition, KernelRequest request);

    /// <summary>Extrudes a profile. Runs on the kernel thread.</summary>
    /// <param name="definition">The validated definition.</param>
    /// <param name="request">Per-call options.</param>
    protected abstract OperationResult Extrude(ExtrudeDefinition definition, KernelRequest request);

    /// <summary>Revolves a profile. Runs on the kernel thread.</summary>
    /// <param name="definition">The validated definition.</param>
    /// <param name="request">Per-call options.</param>
    protected abstract OperationResult Revolve(RevolveDefinition definition, KernelRequest request);

    /// <summary>Combines bodies. Runs on the kernel thread.</summary>
    /// <param name="definition">The validated definition.</param>
    /// <param name="request">Per-call options.</param>
    /// <remarks>
    /// Named <c>CombineBodies</c> rather than <c>Boolean</c> because <c>Boolean</c> is a type name
    /// in several CLR languages and an overridable member cannot safely use it.
    /// </remarks>
    protected abstract OperationResult CombineBodies(BooleanDefinition definition, KernelRequest request);

    /// <summary>Rounds edges. Runs on the kernel thread.</summary>
    /// <param name="definition">The validated definition.</param>
    /// <param name="request">Per-call options.</param>
    protected abstract OperationResult Fillet(FilletDefinition definition, KernelRequest request);

    /// <summary>Bevels edges. Runs on the kernel thread.</summary>
    /// <param name="definition">The validated definition.</param>
    /// <param name="request">Per-call options.</param>
    protected abstract OperationResult Chamfer(ChamferDefinition definition, KernelRequest request);

    /// <summary>Computes mass properties. Runs on the kernel thread.</summary>
    /// <param name="shape">The shape to measure.</param>
    /// <param name="density">Density in kilograms per cubic metre.</param>
    /// <param name="request">Per-call options.</param>
    protected abstract KernelResult<MassProperties> ComputeMassProperties(
        KernelShape shape, double density, KernelRequest request);

    /// <summary>Computes the axis-aligned bound. Runs on the kernel thread.</summary>
    /// <param name="shape">The shape to bound.</param>
    /// <param name="request">Per-call options.</param>
    protected abstract KernelResult<Bounds3d> ComputeBounds(KernelShape shape, KernelRequest request);

    /// <summary>Counts topology. Runs on the kernel thread.</summary>
    /// <param name="shape">The shape to count.</param>
    /// <param name="request">Per-call options.</param>
    protected abstract KernelResult<TopologyCounts> CountTopology(KernelShape shape, KernelRequest request);

    /// <summary>Lists entities in canonical order. Runs on the kernel thread.</summary>
    /// <param name="shape">The shape to enumerate.</param>
    /// <param name="kind">Which kind to list.</param>
    /// <param name="request">Per-call options.</param>
    protected abstract KernelResult<ImmutableArray<SubEntity>> Enumerate(
        KernelShape shape, SubEntityKind kind, KernelRequest request);

    /// <summary>Checks validity. Runs on the kernel thread.</summary>
    /// <param name="shape">The shape to check.</param>
    /// <param name="request">Per-call options.</param>
    protected abstract KernelResult<ShapeValidity> CheckValidity(KernelShape shape, KernelRequest request);

    /// <summary>Triangulates. Runs on the kernel thread.</summary>
    /// <param name="shape">The shape to tessellate.</param>
    /// <param name="options">How finely.</param>
    /// <param name="request">Per-call options.</param>
    protected abstract KernelResult<MeshBuffer> Triangulate(
        KernelShape shape, TessellationOptions options, KernelRequest request);

    /// <summary>Writes native B-rep bytes. Runs on the kernel thread.</summary>
    /// <param name="shape">The shape to write.</param>
    /// <param name="request">Per-call options.</param>
    protected abstract KernelResult<ImmutableArray<byte>> WriteBRep(KernelShape shape, KernelRequest request);

    /// <summary>Reads native B-rep bytes. Runs on the kernel thread.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="request">Per-call options.</param>
    protected abstract OperationResult ReadBRep(ReadOnlyMemory<byte> data, KernelRequest request);

    /// <summary>Writes STEP. Runs on the kernel thread.</summary>
    /// <param name="shapes">The shapes to write.</param>
    /// <param name="destination">Where to write.</param>
    /// <param name="request">Per-call options.</param>
    /// <returns>The number of bytes written.</returns>
    protected abstract KernelResult<int> WriteStep(
        ImmutableArray<KernelShape> shapes, Stream destination, KernelRequest request);

    /// <summary>Releases a shape. Runs on the kernel thread.</summary>
    /// <param name="shape">The shape to release.</param>
    protected abstract void ReleaseShape(KernelShape shape);

    // --- Helpers for implementations -----------------------------------------------------------------------------

    /// <summary>Wraps a freshly created shape in a handle owned by the caller.</summary>
    /// <param name="shape">The shape the implementation just created.</param>
    protected KernelShapeHandle Track(KernelShape shape) => new(shape, this);

    // --- Plumbing ------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    void IKernelShapeReleaser.EnqueueRelease(KernelShape shape)
    {
        if (_disposed || !shape.IsValid)
        {
            return;
        }

        // Never inline: this can run on the finalizer thread, and the kernel may only be touched
        // from the kernel thread (ADR-0004). Post never blocks and never throws.
        Dispatcher.Post("ReleaseShape", () => ReleaseShape(shape));
    }

    private ValueTask<OperationResult> RunOperationAsync(
        IOperationDefinition? definition,
        Func<OperationResult> work,
        KernelRequest request,
        CancellationToken cancellationToken,
        string? operationName = null)
    {
        string name = operationName ?? definition?.OperationName ?? "Operation";

        if (_disposed)
        {
            return ValueTask.FromResult<OperationResult>(OperationResult.Failed.From(
                KernelDiagnosticCodes.KernelDisposed,
                "The geometry kernel has been shut down."));
        }

        // Pre-flight. Rejecting here rather than on the kernel thread means both kernels refuse
        // the same input identically, and the serial kernel thread is never spent on work that
        // cannot succeed.
        if (definition is not null)
        {
            ImmutableArray<KernelDiagnostic> problems = definition.Validate();
            if (problems.Any(p => p.Severity == DiagnosticSeverity.Error))
            {
                return ValueTask.FromResult<OperationResult>(new OperationResult.Failed(problems));
            }
        }

        return Dispatcher.RunAsync(
            name,
            () =>
            {
                KernelThreadGuard.AssertOnKernelThread(name);

                try
                {
                    return work();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Logger.LogError(exception, "Kernel operation {Operation} threw", name);

                    return OperationResult.Failed.From(
                        KernelDiagnosticCodes.InternalError,
                        $"The {name} operation failed unexpectedly. This is a defect in OpenMCAD; "
                        + "the details have been logged.",
                        kernelDetail: exception.ToString());
                }
            },
            request.Priority,
            cancellationToken);
    }

    private ValueTask<KernelResult<T>> RunQueryAsync<T>(
        string name,
        KernelShape shape,
        Func<KernelResult<T>> work,
        KernelRequest request,
        CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return ValueTask.FromResult(KernelResult.Fail<T>(
                KernelDiagnosticCodes.KernelDisposed,
                "The geometry kernel has been shut down."));
        }

        // KernelShape.None is passed by queries that operate on a collection rather than one shape.
        if (shape != KernelShape.None && !shape.IsValid)
        {
            return ValueTask.FromResult(KernelResult.Fail<T>(
                KernelDiagnosticCodes.EmptySelection,
                $"{name} was given no shape."));
        }

        return Dispatcher.RunAsync(
            name,
            () =>
            {
                KernelThreadGuard.AssertOnKernelThread(name);

                try
                {
                    return work();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Logger.LogError(exception, "Kernel query {Operation} threw", name);

                    return KernelResult.Fail<T>(
                        KernelDiagnosticCodes.InternalError,
                        $"The {name} query failed unexpectedly. This is a defect in OpenMCAD; the "
                        + "details have been logged.",
                        kernelDetail: exception.ToString());
                }
            },
            request.Priority,
            cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await OnDisposingAsync().ConfigureAwait(false);
        await Dispatcher.DisposeAsync().ConfigureAwait(false);

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases implementation resources before the dispatcher stops.
    /// </summary>
    /// <remarks>
    /// Still able to queue kernel work when called: the dispatcher shuts down afterwards. This is
    /// where an implementation frees its shape universe.
    /// </remarks>
    protected virtual ValueTask OnDisposingAsync() => ValueTask.CompletedTask;
}
