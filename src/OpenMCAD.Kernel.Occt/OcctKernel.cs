using System.Collections.Immutable;
using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;

using OpenMCAD.Kernel.Diagnostics;
using OpenMCAD.Kernel.Occt.Interop;
using OpenMCAD.Kernel.Operations;
using OpenMCAD.Kernel.Threading;
using OpenMCAD.Math;

namespace OpenMCAD.Kernel.Occt;

/// <summary>
/// The real geometry kernel: <see cref="IGeometryKernel"/> implemented against Open CASCADE
/// through the C shim.
/// </summary>
/// <remarks>
/// <para>
/// Every method here runs on the kernel thread, because <see cref="GeometryKernelBase"/> puts it
/// there (ADR-0004). That is not a convenience -- the shim's handle table, its error slot and its
/// diagnostic queue are all unsynchronised process-wide state, and OCCT itself is not thread-safe
/// for concurrent modelling on shared shapes.
/// </para>
/// <para>
/// More than one of these may exist -- a determinism check builds the same model on two kernels and
/// compares -- but each brings its own dispatcher thread, and the shim's handle table is shared
/// between them. Every call into the shim therefore takes <see cref="NativeGate"/> first, so at
/// most one thread is inside native code at any moment. The lock is uncontended in the normal
/// single-kernel case and costs nothing measurable against the work it guards.
/// </para>
/// <para>
/// The shim's error slot and diagnostic queue are <c>thread_local</c> on its side, so each kernel
/// reads its own. Only the handle table and the initialise/shutdown pair are genuinely shared, and
/// the latter is reference counted below.
/// </para>
/// </remarks>
public sealed class OcctKernel : GeometryKernelBase
{
    /// <summary>
    /// Serialises entry into the shim. See the remarks on the class.
    /// </summary>
    private static readonly Lock NativeGate = new();

    /// <summary>
    /// How many kernels have initialised the shim. The last one out shuts it down, which is what
    /// releases the handle table and lets the leak assertion mean something.
    /// </summary>
    private static int _initialisedCount;

    private bool _initialised;

    /// <summary>Creates the kernel with its own dispatcher.</summary>
    /// <param name="logger">Where to log.</param>
    /// <param name="repro">Repro-bundle capture settings.</param>
    public OcctKernel(ILogger<OcctKernel>? logger = null, ReproBundleOptions repro = default)
        : this(new KernelDispatcher("OpenMCAD Kernel (OCCT)"), logger, repro)
    {
    }

    /// <summary>Creates the kernel on a supplied dispatcher.</summary>
    /// <param name="dispatcher">The dispatcher to use. The kernel takes ownership.</param>
    /// <param name="logger">Where to log.</param>
    /// <param name="repro">Repro-bundle capture settings.</param>
    public OcctKernel(
        KernelDispatcher dispatcher,
        ILogger<OcctKernel>? logger = null,
        ReproBundleOptions repro = default)
        : base(dispatcher, logger, repro)
    {
    }

    /// <inheritdoc />
    public override KernelCapabilities Capabilities => new(
        Name: "occt",

        // Read from the shim rather than hard-coded, so a repro bundle records the OCCT build that
        // actually produced the geometry rather than the one this file was written against.
        Version: NativeVersion.Value,

        // OCCT integrates over the analytic surfaces, not over a tessellation. The FakeKernel
        // cannot, which is exactly the difference this flag exists to express.
        ProducesExactMassProperties: true,

        // P1-T11. Booleans climb model tolerance, conditioned inputs, then relaxed tolerance;
        // blends climb model tolerance, conditioned inputs, then edge by edge.
        SupportsRetryLadder: true);

    // --- Primitives ---------------------------------------------------------------------------------

    /// <inheritdoc />
    protected override OperationResult CreateBox(BoxDefinition definition, KernelRequest request)
        => Build(
            (out ulong shape, out ulong history) => OcctBindings.CreateBox(
                definition.SizeX,
                definition.SizeY,
                definition.SizeZ,
                Placement(definition.Placement),
                out shape,
                out history),
            nameof(OcctBindings.CreateBox),
            []);

    /// <inheritdoc />
    protected override OperationResult CreateCylinder(
        CylinderDefinition definition, KernelRequest request)
        => Build(
            (out ulong shape, out ulong history) => OcctBindings.CreateCylinder(
                definition.Radius,
                definition.Height,
                Placement(definition.Placement),
                out shape,
                out history),
            nameof(OcctBindings.CreateCylinder),
            []);

    /// <inheritdoc />
    protected override OperationResult CreateSphere(SphereDefinition definition, KernelRequest request)
        => Build(
            (out ulong shape, out ulong history) => OcctBindings.CreateSphere(
                definition.Radius,
                Placement(definition.Placement),
                out shape,
                out history),
            nameof(OcctBindings.CreateSphere),
            []);

    /// <inheritdoc />
    protected override OperationResult CreateCone(ConeDefinition definition, KernelRequest request)
        => Build(
            (out ulong shape, out ulong history) => OcctBindings.CreateCone(
                definition.BottomRadius,
                definition.TopRadius,
                definition.Height,
                Placement(definition.Placement),
                out shape,
                out history),
            nameof(OcctBindings.CreateCone),
            []);

    /// <inheritdoc />
    protected override OperationResult CreateTorus(TorusDefinition definition, KernelRequest request)
        => Build(
            (out ulong shape, out ulong history) => OcctBindings.CreateTorus(
                definition.MajorRadius,
                definition.MinorRadius,
                Placement(definition.Placement),
                out shape,
                out history),
            nameof(OcctBindings.CreateTorus),
            []);

    /// <inheritdoc />
    protected override OperationResult CreatePolygonProfile(
        PolygonProfileDefinition definition, KernelRequest request)
    {
        double[] points = new double[definition.Points.Length * 2];
        for (int i = 0; i < definition.Points.Length; ++i)
        {
            points[(i * 2) + 0] = definition.Points[i].X;
            points[(i * 2) + 1] = definition.Points[i].Y;
        }

        return Build(
            (out ulong shape, out ulong history) => OcctBindings.CreatePolygonProfile(
                points,
                definition.Points.Length,
                Placement(definition.Frame),
                out shape,
                out history),
            nameof(OcctBindings.CreatePolygonProfile),
            []);
    }

    // --- Features -----------------------------------------------------------------------------------

    /// <inheritdoc />
    protected override OperationResult Extrude(ExtrudeDefinition definition, KernelRequest request)
        => Build(
            (out ulong shape, out ulong history) => OcctBindings.Extrude(
                definition.Profile.Tag,
                Vector(definition.Direction),
                definition.Distance,
                definition.Capped ? 1 : 0,
                out shape,
                out history),
            nameof(OcctBindings.Extrude),
            [definition.Profile]);

    /// <inheritdoc />
    protected override OperationResult Revolve(RevolveDefinition definition, KernelRequest request)
        => Build(
            (out ulong shape, out ulong history) => OcctBindings.Revolve(
                definition.Profile.Tag,
                Vector(definition.AxisPoint),
                Vector(definition.AxisDirection),
                definition.Angle,
                definition.Capped ? 1 : 0,
                out shape,
                out history),
            nameof(OcctBindings.Revolve),
            [definition.Profile]);

    /// <inheritdoc />
    protected override OperationResult CombineBodies(
        BooleanDefinition definition, KernelRequest request)
    {
        ulong[] tools = new ulong[definition.Tools.Length];
        for (int i = 0; i < definition.Tools.Length; ++i)
        {
            tools[i] = definition.Tools[i].Tag;
        }

        KernelShape[] inputs = new KernelShape[definition.Tools.Length + 1];
        inputs[0] = definition.Target;
        definition.Tools.CopyTo(inputs, 1);

        int rung = 0;
        OperationResult result = Build(
            (out ulong shape, out ulong history) => OcctBindings.Boolean(
                (int)definition.Operation,
                definition.Target.Tag,
                tools,
                tools.Length,
                request.EffectiveTolerance,

                // No fuzzy value on the first attempt. Relaxing tolerance to make a boolean
                // succeed also lets it quietly merge features that should have stayed distinct,
                // so it is something the ladder escalates to (P1-T11), never a default.
                0.0,
                out shape,
                out history,
                out rung),
            nameof(OcctBindings.Boolean),
            inputs);

        return WithRung(result, rung);
    }

    /// <inheritdoc />
    protected override OperationResult Fillet(FilletDefinition definition, KernelRequest request)
    {
        (ulong[] edges, double[] values) = BlendArguments(
            definition.Edges, static edge => edge.Edge, static edge => edge.Radius);

        int rung = 0;
        OperationResult result = Build(
            (out ulong shape, out ulong history) => OcctBindings.Fillet(
                definition.Body.Tag,
                edges,
                edges.Length,
                values,
                values.Length,
                request.EffectiveTolerance,
                out shape,
                out history,
                out rung),
            nameof(OcctBindings.Fillet),
            [definition.Body],
            [.. definition.Edges.Select(static edge => edge.Edge)]);

        return WithRung(result, rung);
    }

    /// <inheritdoc />
    protected override OperationResult Chamfer(ChamferDefinition definition, KernelRequest request)
    {
        (ulong[] edges, double[] values) = BlendArguments(
            definition.Edges, static edge => edge.Edge, static edge => edge.Distance);

        int rung = 0;
        OperationResult result = Build(
            (out ulong shape, out ulong history) => OcctBindings.Chamfer(
                definition.Body.Tag,
                edges,
                edges.Length,
                values,
                values.Length,
                request.EffectiveTolerance,
                out shape,
                out history,
                out rung),
            nameof(OcctBindings.Chamfer),
            [definition.Body],
            [.. definition.Edges.Select(static edge => edge.Edge)]);

        return WithRung(result, rung);
    }

    // --- Queries ------------------------------------------------------------------------------------

    /// <inheritdoc />
    protected override KernelResult<MassProperties> ComputeMassProperties(
        KernelShape shape, double density, KernelRequest request)
        => Query<MassProperties>(nameof(OcctBindings.MassProperties), () =>
        {
            double[] values = Native.Read<double>(
                (Span<double> buffer, int capacity, out int required)
                    => OcctBindings.MassProperties(
                        shape.Tag, density, buffer, capacity, out required, out _),
                nameof(OcctBindings.MassProperties));

            Native.Check(
                OcctBindings.MassProperties(shape.Tag, density, [], 0, out _, out int accuracy),
                nameof(OcctBindings.MassProperties));

            if (values.Length != 11)
            {
                throw new NativeCallException(
                    NativeStatus.Internal,
                    nameof(OcctBindings.MassProperties),
                    $"Expected eleven figures and got {values.Length}.");
            }

            return new MassProperties(
                Volume: values[0],
                SurfaceArea: values[1],
                Centroid: new Vec3d(values[2], values[3], values[4]),
                Density: density,
                Inertia: new InertiaTensor(
                    Ixx: values[5],
                    Iyy: values[6],
                    Izz: values[7],
                    Ixy: values[8],
                    Ixz: values[9],
                    Iyz: values[10]),
                Accuracy: accuracy == 0 ? ResultAccuracy.Exact : ResultAccuracy.Approximate);
        });

    /// <inheritdoc />
    protected override KernelResult<Bounds3d> ComputeBounds(KernelShape shape, KernelRequest request)
        => Query<Bounds3d>(nameof(OcctBindings.BoundingBox), () =>
        {
            double[] values = Native.Read<double>(
                (Span<double> buffer, int capacity, out int required)
                    => OcctBindings.BoundingBox(shape.Tag, buffer, capacity, out required),
                nameof(OcctBindings.BoundingBox));

            return new Bounds3d(
                new Vec3d(values[0], values[1], values[2]),
                new Vec3d(values[3], values[4], values[5]));
        });

    /// <inheritdoc />
    protected override KernelResult<TopologyCounts> CountTopology(
        KernelShape shape, KernelRequest request)
        => Query<TopologyCounts>(nameof(OcctBindings.TopologyCounts), () =>
        {
            int[] counts = Native.Read<int>(
                (Span<int> buffer, int capacity, out int required)
                    => OcctBindings.TopologyCounts(shape.Tag, buffer, capacity, out required),
                nameof(OcctBindings.TopologyCounts));

            return new TopologyCounts(
                Solids: counts[0],
                Shells: counts[1],
                Faces: counts[2],
                Wires: counts[3],
                Edges: counts[4],
                Vertices: counts[5]);
        });

    /// <inheritdoc />
    protected override KernelResult<ImmutableArray<SubEntity>> Enumerate(
        KernelShape shape, SubEntityKind kind, KernelRequest request)
        => Query(nameof(OcctBindings.Enumerate), () => NativeHistory.Enumerate(shape, kind));

    /// <inheritdoc />
    protected override KernelResult<ShapeValidity> CheckValidity(
        KernelShape shape, KernelRequest request)
        => Query<ShapeValidity>(nameof(OcctBindings.CheckValidity), () =>
        {
            Native.Check(
                OcctBindings.CheckValidity(shape.Tag, out int valid, out int closed),
                nameof(OcctBindings.CheckValidity));

            ImmutableArray<KernelDiagnostic> problems = valid != 0
                ? []
                : [KernelDiagnostic.Error(
                    KernelDiagnosticCodes.InvalidResult,
                    "The kernel's own checker rejects this body. It may have self-intersecting "
                    + "faces, inconsistent orientations, or edges without curves.")];

            return new ShapeValidity(valid != 0, closed != 0, problems);
        });

    /// <inheritdoc />
    protected override KernelResult<MeshBuffer> Triangulate(
        KernelShape shape, TessellationOptions options, KernelRequest request)
        => Query<MeshBuffer>(nameof(OcctBindings.Triangulate), () =>
        {
            Native.Check(
                OcctBindings.Triangulate(
                    shape.Tag,
                    options.ChordalDeviation,
                    options.AngularDeviation,
                    options.Relative ? 1 : 0,
                    options.ComputeNormals ? 1 : 0,
                    out ulong mesh),
                nameof(OcctBindings.Triangulate));

            try
            {
                return ReadMesh(mesh, shape);
            }
            finally
            {
                // The mesh handle is the shim's, and the managed MeshBuffer is a full copy, so it
                // is released here rather than surfaced. A mesh that outlived this call would be a
                // second lifetime for callers to manage for no benefit.
                _ = OcctBindings.MeshRelease(mesh);
            }
        });

    // --- Serialisation ------------------------------------------------------------------------------

    /// <inheritdoc />
    protected override KernelResult<ImmutableArray<byte>> WriteBRep(
        KernelShape shape, KernelRequest request)
        => Query(nameof(OcctBindings.WriteBRep), () => ImmutableArray.Create(
            Native.Read<byte>(
                (Span<byte> buffer, int capacity, out int required)
                    => OcctBindings.WriteBRep(shape.Tag, buffer, capacity, out required),
                nameof(OcctBindings.WriteBRep))));

    /// <inheritdoc />
    protected override OperationResult ReadBRep(ReadOnlyMemory<byte> data, KernelRequest request)
    {
        byte[] bytes = data.ToArray();

        return Build(
            (out ulong shape, out ulong history)
                => OcctBindings.ReadBRep(bytes, bytes.Length, out shape, out history),
            nameof(OcctBindings.ReadBRep),
            []);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The contract is a stream; OCCT's STEP writer only accepts a path. So the file is written to
    /// a temporary one and copied across. Buffering the whole model in memory instead would be the
    /// alternative, and a large assembly is exactly the case where that is worst.
    /// </remarks>
    protected override KernelResult<int> WriteStep(
        ImmutableArray<KernelShape> shapes, Stream destination, KernelRequest request)
        => Query(nameof(OcctBindings.WriteStep), () =>
        {
            ulong[] tags = new ulong[shapes.Length];
            for (int i = 0; i < shapes.Length; ++i)
            {
                tags[i] = shapes[i].Tag;
            }

            string path = Path.Combine(
                Path.GetTempPath(), $"openmcad-step-{Guid.NewGuid():N}.stp");

            try
            {
                Native.Check(
                    OcctBindings.WriteStep(tags, tags.Length, path, out int written),
                    nameof(OcctBindings.WriteStep));

                using FileStream file = File.OpenRead(path);
                file.CopyTo(destination);

                return written;
            }
            finally
            {
                // Best effort. A leftover temp file is untidy; throwing here would turn a
                // successful export into a failure over one.
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        });

    // --- Lifetime -----------------------------------------------------------------------------------

    /// <inheritdoc />
    protected override void ReleaseShape(KernelShape shape)
    {
        if (!shape.IsValid)
        {
            return;
        }

        // Deliberately not checked. This runs while a handle is being reclaimed, and there is
        // nothing useful to do with a failure: the shape is going away either way, and throwing
        // here would propagate out of a release path. A stale tag is already reported at the point
        // something tries to use it, which is where the caller can act on it.
        int status;
        using (NativeGate.EnterScope())
        {
            status = OcctBindings.ShapeRelease(shape.Tag);
        }

        if (status != (int)NativeStatus.Ok)
        {
            Logger.LogWarning(
                "Releasing shape {Tag} returned status {Status}. The handle was already gone or "
                + "never valid.",
                shape.Tag,
                (NativeStatus)status);
        }
    }

    /// <inheritdoc />
    protected override async ValueTask OnDisposingAsync()
    {
        if (_initialised && Interlocked.Decrement(ref _initialisedCount) == 0)
        {
            // On the kernel thread, because everything else was. Shutting the shim down from the
            // disposing thread would touch the handle table from a thread that never owned it.
            await Dispatcher.RunAsync(
                "openmcad_shutdown",
                () =>
                {
                    using Lock.Scope gate = NativeGate.EnterScope();

                    int status = OcctBindings.Shutdown();
                    if (status != (int)NativeStatus.Ok)
                    {
                        Logger.LogWarning(
                            "The native shim reported status {Status} on shutdown.",
                            (NativeStatus)status);
                    }
                },
                KernelPriority.Background).ConfigureAwait(false);
        }
    }

    // --- Plumbing -----------------------------------------------------------------------------------

    /// <summary>A native call that produces a shape and its history.</summary>
    /// <param name="shape">The resulting shape tag.</param>
    /// <param name="history">The resulting history tag.</param>
    /// <returns>A <see cref="NativeStatus"/>.</returns>
    private delegate int ShapeCall(out ulong shape, out ulong history);

    /// <summary>
    /// Runs a shape-producing call and turns the result into an <see cref="OperationResult"/>.
    /// </summary>
    /// <param name="call">The native entry point.</param>
    /// <param name="operation">Its name, for messages.</param>
    /// <param name="inputs">The shapes the operation consumed, for resolving history entities.</param>
    /// <param name="subjects">
    /// The entities the operation was about, attached to a failure so it can say which edge or
    /// face it was. Empty for operations that select nothing.
    /// </param>
    /// <returns>The result.</returns>
    private OperationResult Build(
        ShapeCall call,
        string operation,
        KernelShape[] inputs,
        ImmutableArray<SubEntity> subjects = default)
    {
        using Lock.Scope gate = NativeGate.EnterScope();

        EnsureInitialised();

        ulong history = 0;
        try
        {
            Native.Check(call(out ulong shape, out history), operation);

            KernelShape produced = new(shape);
            HistoryMap map = NativeHistory.Read(history, inputs, produced);

            // The subjects are what a diagnostic can name: the edges a blend was asked to apply
            // to. The shim reports refused ones by tag, and this is what turns those back into
            // entities the caller recognises.
            Dictionary<ulong, SubEntity> named = [];
            if (!subjects.IsDefaultOrEmpty)
            {
                foreach (SubEntity subject in subjects)
                {
                    named[subject.Tag] = subject;
                }
            }

            ImmutableArray<KernelDiagnostic> diagnostics = Native.DrainDiagnostics(named);

            KernelShapeHandle handle = new(produced, this);

            // Warnings with a shape are the Degraded case: the operation produced geometry but not
            // the geometry that was asked for, and silently returning Success would hide that.
            return diagnostics.Any(static d => d.Severity >= DiagnosticSeverity.Warning)
                ? new OperationResult.Degraded(handle, map, diagnostics, RetryRung.ModelTolerance)
                : new OperationResult.Success(handle, map, RetryRung.ModelTolerance, diagnostics);
        }
        catch (NativeCallException failure)
        {
            return Failed(failure, subjects);
        }
        finally
        {
            // The history is fully copied into the managed map, so its native lifetime ends here
            // whether the call succeeded or not.
            if (history != 0)
            {
                _ = OcctBindings.HistoryRelease(history);
            }
        }
    }

    /// <summary>Runs a query and turns a native failure into a failed result rather than a throw.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="operation">The entry point, for messages.</param>
    /// <param name="read">The work.</param>
    /// <returns>The result.</returns>
    private KernelResult<T> Query<T>(string operation, Func<T> read)
    {
        using Lock.Scope gate = NativeGate.EnterScope();

        EnsureInitialised();

        try
        {
            T value = read();
            ImmutableArray<KernelDiagnostic> diagnostics = Native.DrainDiagnostics();

            return diagnostics.IsEmpty
                ? KernelResult.Ok(value)
                : KernelResult.Degraded(value, diagnostics);
        }
        catch (NativeCallException failure)
        {
            _ = Native.DrainDiagnostics();
            return KernelResult.Fail<T>(
                MapCode(failure.Status, operation), failure.Detail, kernelDetail: failure.Message);
        }
    }

    /// <summary>Reads a native mesh into the managed buffer, copying every array.</summary>
    /// <param name="mesh">The native mesh handle.</param>
    /// <param name="shape">The shape it came from, to own the face entities.</param>
    /// <returns>The mesh.</returns>
    private static MeshBuffer ReadMesh(ulong mesh, KernelShape shape)
    {
        double[] positions = Native.Read<double>(
            (Span<double> buffer, int capacity, out int required)
                => OcctBindings.MeshPositions(mesh, buffer, capacity, out required),
            nameof(OcctBindings.MeshPositions));

        double[] normals = Native.Read<double>(
            (Span<double> buffer, int capacity, out int required)
                => OcctBindings.MeshNormals(mesh, buffer, capacity, out required),
            nameof(OcctBindings.MeshNormals));

        int[] indices = Native.Read<int>(
            (Span<int> buffer, int capacity, out int required)
                => OcctBindings.MeshIndices(mesh, buffer, capacity, out required),
            nameof(OcctBindings.MeshIndices));

        int[] triangleFaces = Native.Read<int>(
            (Span<int> buffer, int capacity, out int required)
                => OcctBindings.MeshTriangleFaces(mesh, buffer, capacity, out required),
            nameof(OcctBindings.MeshTriangleFaces));

        ulong[] faceTags = Native.Read<ulong>(
            (Span<ulong> buffer, int capacity, out int required)
                => OcctBindings.MeshFaces(mesh, buffer, capacity, out required),
            nameof(OcctBindings.MeshFaces));

        return new MeshBuffer(
            Triples(positions),
            Triples(normals),
            [.. indices],
            [.. triangleFaces],
            [.. faceTags.Select(tag => new SubEntity(shape, tag, SubEntityKind.Face))]);
    }

    /// <summary>Repacks a flat XYZ array into vectors.</summary>
    /// <param name="values">Three doubles per point.</param>
    /// <returns>The points.</returns>
    private static ImmutableArray<Vec3d> Triples(double[] values)
    {
        ImmutableArray<Vec3d>.Builder points = ImmutableArray.CreateBuilder<Vec3d>(values.Length / 3);
        for (int i = 0; i + 2 < values.Length; i += 3)
        {
            points.Add(new Vec3d(values[i], values[i + 1], values[i + 2]));
        }

        return points.MoveToImmutable();
    }

    /// <summary>Splits blend edges into the two parallel arrays the shim expects.</summary>
    /// <typeparam name="T">The edge record type.</typeparam>
    /// <param name="edges">The selection.</param>
    /// <param name="entity">How to read the entity from one.</param>
    /// <param name="value">How to read the radius or setback from one.</param>
    /// <returns>The tags and the values, in the same order.</returns>
    private static (ulong[] Edges, double[] Values) BlendArguments<T>(
        ImmutableArray<T> edges, Func<T, SubEntity> entity, Func<T, double> value)
    {
        ulong[] tags = new ulong[edges.Length];
        double[] values = new double[edges.Length];

        for (int i = 0; i < edges.Length; ++i)
        {
            tags[i] = entity(edges[i]).Tag;
            values[i] = value(edges[i]);
        }

        return (tags, values);
    }

    /// <summary>Restates a result with the rung the shim actually reported.</summary>
    /// <param name="result">The result as built.</param>
    /// <param name="rung">The shim's rung, which uses <see cref="RetryRung"/>'s own values.</param>
    /// <returns>The result.</returns>
    private static OperationResult WithRung(OperationResult result, int rung) => result switch
    {
        // Reconstructed rather than copied with `with`: Rung is an override with only a getter, so
        // a with-expression silently keeps the old value instead of failing to compile.
        OperationResult.Success success => new OperationResult.Success(
            success.Shape, success.History, (RetryRung)rung, success.Diagnostics),
        OperationResult.Degraded degraded => new OperationResult.Degraded(
            degraded.Shape, degraded.History, degraded.Diagnostics, (RetryRung)rung),
        _ => result,
    };

    /// <summary>Turns a native failure into a failed result with a diagnosable code.</summary>
    /// <param name="failure">The failure.</param>
    /// <returns>The result.</returns>
    /// <param name="subjects">
    /// The entities the operation was about, attached so the failure can say which edge or face it
    /// was. PLAN.md 6.1: a message the user cannot act on is not a diagnostic, and "the fillet
    /// failed" without naming the edge cannot be acted on when eleven edges were selected.
    /// </param>
    private static OperationResult.Failed Failed(
        NativeCallException failure, ImmutableArray<SubEntity> subjects)
        => OperationResult.Failed.From(
            MapCode(failure.Status, failure.Operation),
            failure.Detail,
            entities: subjects.IsDefault ? [] : subjects,
            kernelDetail: failure.Message);

    /// <summary>
    /// Maps a shim status onto the diagnostic code the rest of the system reasons about.
    /// </summary>
    /// <param name="status">The shim's status.</param>
    /// <param name="operation">The entry point, used to tell a blend failure from a boolean one.</param>
    /// <returns>A code from <see cref="KernelDiagnosticCodes"/>.</returns>
    private static string MapCode(NativeStatus status, string operation) => status switch
    {
        NativeStatus.InvalidHandle => KernelDiagnosticCodes.StaleHandle,

        // Serialisation first: a corrupt cache is a rebuild, not a modelling error (ADR-0010), and
        // the code is what tells the caller which of the two it is looking at.
        _ when IsSerialisation(operation) => KernelDiagnosticCodes.SerializationFailed,

        NativeStatus.InvalidInput => KernelDiagnosticCodes.InvalidDimension,
        NativeStatus.KernelFailure when operation.Contains("Fillet", StringComparison.Ordinal)
            || operation.Contains("Chamfer", StringComparison.Ordinal)
            => KernelDiagnosticCodes.BlendFailed,
        NativeStatus.KernelFailure when operation.Contains("Boolean", StringComparison.Ordinal)
            => KernelDiagnosticCodes.BooleanFailed,
        NativeStatus.KernelFailure => KernelDiagnosticCodes.InvalidResult,
        _ => KernelDiagnosticCodes.InvalidResult,
    };

    /// <summary>Whether an entry point reads or writes a file format.</summary>
    /// <param name="operation">The entry point name.</param>
    /// <returns><see langword="true"/> for the BREP and STEP entry points.</returns>
    private static bool IsSerialisation(string operation)
        => operation.Contains("BRep", StringComparison.Ordinal)
        || operation.Contains("Step", StringComparison.Ordinal);

    /// <summary>Encodes a transform as the eight doubles the shim's <c>Transform</c> expects.</summary>
    /// <param name="transform">The placement.</param>
    /// <returns>Quaternion xyzw, translation xyz, then uniform scale.</returns>
    /// <remarks>
    /// The order matches <c>openmcad_types.h</c> exactly, because the shim memcpys the array over
    /// the struct rather than reading it field by field. A reordering here is silent corruption
    /// there, which is why both sides are generated from the same IDL type entry.
    /// </remarks>
    private static double[] Placement(Transform transform) =>
    [
        transform.Rotation.X,
        transform.Rotation.Y,
        transform.Rotation.Z,
        transform.Rotation.W,
        transform.Translation.X,
        transform.Translation.Y,
        transform.Translation.Z,
        transform.Scale,
    ];

    /// <summary>Encodes a vector as the three doubles the shim expects.</summary>
    /// <param name="vector">The vector.</param>
    /// <returns>Its components.</returns>
    private static double[] Vector(Vec3d vector) => [vector.X, vector.Y, vector.Z];

    /// <summary>Initialises the shim on first use, from the kernel thread.</summary>
    /// <remarks>
    /// Not in the constructor, because the constructor runs on the caller's thread and the shim
    /// must be initialised from the thread that will use it -- <c>OSD::SetSignal</c> installs
    /// per-thread handlers that turn access violations into catchable exceptions, and installing
    /// them on the wrong thread leaves the kernel thread unprotected.
    /// </remarks>
    private void EnsureInitialised()
    {
        if (_initialised)
        {
            return;
        }

        Native.Check(OcctBindings.Initialize(), nameof(OcctBindings.Initialize));
        _initialised = true;
        Interlocked.Increment(ref _initialisedCount);
    }

    /// <summary>The shim's version string, read once.</summary>
    private static class NativeVersion
    {
        /// <summary>Gets the version, including the OCCT build behind it.</summary>
        internal static string Value { get; } = Read();

        private static string Read()
        {
            try
            {
                return Native.DecodeUtf8(Native.ReadBytes(
                    (Span<byte> buffer, int capacity, out int required)
                        => OcctBindings.Version(buffer, capacity, out required),
                    nameof(OcctBindings.Version)));
            }
            catch (Exception error) when (error is NativeCallException or DllNotFoundException
                or EntryPointNotFoundException)
            {
                // Capabilities is read for logging and for repro-bundle manifests, sometimes
                // before anything has forced the library to load. Failing to name the version is
                // not a reason to fail those.
                return "unavailable";
            }
        }
    }
}
