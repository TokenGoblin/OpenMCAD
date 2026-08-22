using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using OpenMCAD.Kernel.Fake.Geometry;
using OpenMCAD.Kernel.Operations;
using OpenMCAD.Kernel.Threading;
using OpenMCAD.Math;

namespace OpenMCAD.Kernel.Fake;

/// <summary>
/// A deterministic in-memory geometry kernel with analytic geometry.
/// </summary>
/// <remarks>
/// <para>
/// P1-T09, and the plan is emphatic that this is "a first-class deliverable, not a stub". ADR-0002
/// justifies the whole kernel abstraction largely on this: with a fast, deterministic mock, the
/// parametric engine, naming layer, undo, and document model become testable in seconds rather
/// than minutes, and testable at all without a native toolchain.
/// </para>
/// <para>
/// <b>What is exact.</b> Topology, entity identity, provenance, and operation roles — everything
/// naming is built on. Mass properties for primitives, profiles, and right prisms, computed in
/// closed form.
/// </para>
/// <para>
/// <b>What is not.</b> Booleans and blends do no real geometry. They synthesise a topologically
/// plausible result and adjust volume analytically, and they report
/// <see cref="ResultAccuracy.Approximate"/> so nothing downstream mistakes the figure for a
/// measurement. <see cref="KernelCapabilities.ProducesExactMassProperties"/> is false for the same
/// reason, which is how the contract tests know to demand exactness only where it is promised.
/// </para>
/// <para>
/// <b>What it deliberately reproduces.</b> The handle table with generation counters, so a stale
/// tag is detected rather than aliasing a recycled slot, exactly as the native shim will behave
/// (ADR-0003). Lifetime bugs then surface against the fast mock instead of against OCCT.
/// </para>
/// </remarks>
public sealed class FakeKernel : GeometryKernelBase
{
    private const int IndexBits = 40;
    private const ulong IndexMask = (1UL << IndexBits) - 1;

    private readonly Dictionary<ulong, FakeShape> _shapes = [];
    private readonly Dictionary<ulong, ulong> _entityOwners = [];
    private readonly Dictionary<ulong, uint> _generations = [];

    private ulong _nextIndex = 1;

    /// <summary>Creates a fake kernel with its own dispatcher.</summary>
    /// <param name="logger">Where to log.</param>
    public FakeKernel(ILogger<FakeKernel>? logger = null)
        : base(new KernelDispatcher("OpenMCAD Kernel (fake)"), logger)
    {
    }

    /// <summary>Creates a fake kernel on a supplied dispatcher.</summary>
    /// <param name="dispatcher">The dispatcher to use.</param>
    /// <param name="logger">Where to log.</param>
    public FakeKernel(KernelDispatcher dispatcher, ILogger<FakeKernel>? logger = null)
        : base(dispatcher, logger)
    {
    }

    /// <inheritdoc />
    public override KernelCapabilities Capabilities => new(
        Name: "fake",
        Version: "1.0",
        ProducesExactMassProperties: false,
        SupportsRetryLadder: false);

    /// <summary>Gets the number of shapes currently alive, for leak tests.</summary>
    public int LiveShapeCount
    {
        get
        {
            lock (_shapes)
            {
                return _shapes.Count;
            }
        }
    }

    // --- Primitives ---------------------------------------------------------------------------------

    /// <inheritdoc />
    protected override OperationResult CreateBox(BoxDefinition definition, KernelRequest request)
    {
        TopologyBuilder topology = new(NextTag);
        Vec3d size = new(definition.SizeX, definition.SizeY, definition.SizeZ);
        topology.BuildBox(size, definition.Placement);

        return CompletePrimitive(
            new BoxGeometry(size, definition.Placement), topology, isSolid: true);
    }

    /// <inheritdoc />
    protected override OperationResult CreateCylinder(CylinderDefinition definition, KernelRequest request)
    {
        TopologyBuilder topology = new(NextTag);
        topology.BuildCylinder(definition.Radius, definition.Height, definition.Placement);

        return CompletePrimitive(
            new CylinderGeometry(definition.Radius, definition.Height, definition.Placement),
            topology,
            isSolid: true);
    }

    /// <inheritdoc />
    protected override OperationResult CreateSphere(SphereDefinition definition, KernelRequest request)
    {
        TopologyBuilder topology = new(NextTag);
        topology.BuildSphere(definition.Radius, definition.Placement);

        return CompletePrimitive(
            new SphereGeometry(definition.Radius, definition.Placement), topology, isSolid: true);
    }

    /// <inheritdoc />
    protected override OperationResult CreateCone(ConeDefinition definition, KernelRequest request)
    {
        TopologyBuilder topology = new(NextTag);
        topology.BuildCone(
            definition.BottomRadius, definition.TopRadius, definition.Height, definition.Placement);

        return CompletePrimitive(
            new ConeGeometry(
                definition.BottomRadius, definition.TopRadius, definition.Height, definition.Placement),
            topology,
            isSolid: true);
    }

    /// <inheritdoc />
    protected override OperationResult CreateTorus(TorusDefinition definition, KernelRequest request)
    {
        TopologyBuilder topology = new(NextTag);
        topology.BuildTorus(definition.MajorRadius, definition.MinorRadius, definition.Placement);

        return CompletePrimitive(
            new TorusGeometry(definition.MajorRadius, definition.MinorRadius, definition.Placement),
            topology,
            isSolid: true);
    }

    /// <inheritdoc />
    protected override OperationResult CreatePolygonProfile(
        PolygonProfileDefinition definition,
        KernelRequest request)
    {
        TopologyBuilder topology = new(NextTag);
        topology.BuildProfile(definition.Points, definition.Frame);

        return CompletePrimitive(
            new ProfileGeometry(definition.Points, definition.Frame), topology, isSolid: false);
    }

    // --- Construction -----------------------------------------------------------------------------------

    /// <inheritdoc />
    protected override OperationResult Extrude(ExtrudeDefinition definition, KernelRequest request)
    {
        if (!TryResolve(definition.Profile, out FakeShape? profile, out OperationResult? failure))
        {
            return failure;
        }

        if (profile.Geometry is not ProfileGeometry source)
        {
            return OperationResult.Failed.From(
                KernelDiagnosticCodes.InvalidProfile,
                "Extrude needs a planar profile. The shape supplied is a solid, which cannot be swept.");
        }

        Vec3d direction = definition.Direction.Normalized();

        TopologyBuilder topology = new(NextTag);
        topology.BuildPrism(
            source.Points,
            source.Frame,
            direction,
            definition.Distance,
            out ImmutableArray<FakeEntity> sideWalls,
            out FakeEntity startCap,
            out FakeEntity endCap,
            out ImmutableArray<FakeEntity> sideEdges);

        FakeShape result = Register(
            new PrismGeometry(source.Points, source.Frame, direction, definition.Distance),
            topology,
            isSolid: definition.Capped);

        // Provenance. This is the part that matters: each side wall is named as the wall swept
        // from a specific profile edge, so a fillet on it survives an edit to that edge.
        HistoryMapBuilder history = new();
        KernelShape from = profile.Reference;
        KernelShape to = result.Reference;

        for (int i = 0; i < profile.Edges.Length && i < sideWalls.Length; i++)
        {
            history.AddGenerated(
                new SubEntity(from, profile.Edges[i].Tag, SubEntityKind.Edge),
                new SubEntity(to, sideWalls[i].Tag, SubEntityKind.Face),
                OperationRole.SideWall);
        }

        for (int i = 0; i < profile.Vertices.Length && i < sideEdges.Length; i++)
        {
            history.AddGenerated(
                new SubEntity(from, profile.Vertices[i].Tag, SubEntityKind.Vertex),
                new SubEntity(to, sideEdges[i].Tag, SubEntityKind.Edge),
                OperationRole.SideEdge);
        }

        SubEntity profileFace = new(from, profile.Faces[0].Tag, SubEntityKind.Face);
        history.AddModified(profileFace, new SubEntity(to, startCap.Tag, SubEntityKind.Face), OperationRole.StartCap);
        history.AddGenerated(profileFace, new SubEntity(to, endCap.Tag, SubEntityKind.Face), OperationRole.EndCap);

        RoleRemainingOutputs(history, result, sideWalls, startCap, endCap, sideEdges);

        return new OperationResult.Success(Track(result.Reference), history.Build());
    }

    /// <inheritdoc />
    protected override OperationResult Revolve(RevolveDefinition definition, KernelRequest request)
    {
        if (!TryResolve(definition.Profile, out FakeShape? profile, out OperationResult? failure))
        {
            return failure;
        }

        if (profile.Geometry is not ProfileGeometry source)
        {
            return OperationResult.Failed.From(
                KernelDiagnosticCodes.InvalidProfile,
                "Revolve needs a planar profile. The shape supplied is a solid, which cannot be swept.");
        }

        // Pappus's theorem: the volume of a solid of revolution is the profile area times the
        // distance its centroid travels. Exact for a full revolution of a profile that does not
        // cross the axis, which is the only case Phase 1 needs.
        Vec2d centroid2d = Polygon2d.Centroid(source.Points);
        Vec3d centroid = source.Frame.TransformPoint(new Vec3d(centroid2d.X, centroid2d.Y, 0));
        Vec3d axis = definition.AxisDirection.Normalized();
        double radius = (centroid - definition.AxisPoint).PerpendicularTo(axis).Length;

        if (radius <= Tolerance.Linear)
        {
            return OperationResult.Failed.From(
                KernelDiagnosticCodes.SelfIntersecting,
                "The profile centroid lies on the axis of revolution, so the result would pass "
                + "through itself. Move the profile away from the axis.");
        }

        TopologyBuilder topology = new(NextTag);
        double area = Polygon2d.Area(source.Points);
        double sweptDistance = System.Math.Abs(definition.Angle) * radius;

        // Modelled as a torus of equivalent volume: the topology is synthesised below, and the
        // volumetric stand-in only has to be deterministic and close.
        double equivalentMinor = System.Math.Sqrt(area / System.Math.PI);
        topology.BuildTorus(radius, equivalentMinor, Transform.FromTranslation(definition.AxisPoint));

        FakeShape result = Register(
            new TorusGeometry(radius, equivalentMinor, Transform.FromTranslation(definition.AxisPoint)),
            topology,
            isSolid: true);

        HistoryMapBuilder history = new();
        KernelShape from = profile.Reference;
        KernelShape to = result.Reference;

        SubEntity profileFace = new(from, profile.Faces[0].Tag, SubEntityKind.Face);
        history.AddGenerated(
            profileFace,
            new SubEntity(to, result.Faces[0].Tag, SubEntityKind.Face),
            OperationRole.SideWall);

        foreach (FakeEntity edge in result.Edges)
        {
            history.AddNew(new SubEntity(to, edge.Tag, SubEntityKind.Edge), OperationRole.Seam);
        }

        foreach (FakeEntity vertex in result.Vertices)
        {
            history.AddNew(new SubEntity(to, vertex.Tag, SubEntityKind.Vertex), OperationRole.PrimitiveVertex);
        }

        ImmutableArray<KernelDiagnostic> warnings =
        [
            KernelDiagnostic.Warning(
                KernelDiagnosticCodes.ApproximateMassProperties,
                $"FakeKernel models a revolution as an equivalent torus, giving a volume from "
                + $"Pappus's theorem ({DescribeSweep(area, sweptDistance)}) rather than exact "
                + "integration. Use OcctKernel where the figure matters."),
        ];

        return new OperationResult.Degraded(Track(result.Reference), history.Build(), warnings);
    }

    private static string DescribeSweep(double area, double distance)
        => System.Globalization.CultureInfo.InvariantCulture is var culture
            ? $"area {area.ToString("G4", culture)} m² swept {distance.ToString("G4", culture)} m"
            : string.Empty;

    // --- Modification -----------------------------------------------------------------------------------------

    /// <inheritdoc />
    protected override OperationResult CombineBodies(BooleanDefinition definition, KernelRequest request)
    {
        if (!TryResolve(definition.Target, out FakeShape? target, out OperationResult? failure))
        {
            return failure;
        }

        List<FakeShape> tools = [];
        foreach (KernelShape toolReference in definition.Tools)
        {
            if (!TryResolve(toolReference, out FakeShape? tool, out OperationResult? toolFailure))
            {
                return toolFailure;
            }

            tools.Add(tool);
        }

        MassProperties targetProperties = target.Geometry.Compute(1000.0);
        double volume = targetProperties.Volume;
        Bounds3d bounds = target.Geometry.Bounds;

        foreach (FakeShape tool in tools)
        {
            MassProperties toolProperties = tool.Geometry.Compute(1000.0);
            Bounds3d overlap = Bounds3d.Intersection(bounds, tool.Geometry.Bounds);
            double overlapFraction = toolProperties.Volume <= Tolerance.LinearResolution
                ? 0.0
                : System.Math.Min(1.0, overlap.Volume / toolProperties.Volume);

            volume = definition.Operation switch
            {
                BooleanOperation.Union => volume + (toolProperties.Volume * (1.0 - overlapFraction)),
                BooleanOperation.Subtract => System.Math.Max(0.0, volume - (toolProperties.Volume * overlapFraction)),
                _ => System.Math.Min(volume, toolProperties.Volume) * overlapFraction,
            };

            bounds = definition.Operation switch
            {
                BooleanOperation.Union => Bounds3d.Union(bounds, tool.Geometry.Bounds),
                BooleanOperation.Subtract => bounds,
                _ => overlap,
            };
        }

        if (volume <= Tolerance.LinearResolution)
        {
            return OperationResult.Failed.From(
                KernelDiagnosticCodes.EmptyResult,
                definition.Operation == BooleanOperation.Subtract
                    ? "The subtraction removed the whole body, leaving nothing."
                    : "The bodies do not overlap, so the intersection is empty.");
        }

        // Topology: retain the target's entities, add the tool's, and add an intersection edge for
        // each pair of faces that could plausibly meet. Not the real answer -- see the class
        // remarks -- but stable, deterministic, and shaped like one.
        TopologyBuilder topology = new(NextTag);
        HistoryMapBuilder history = new();

        FakeShape placeholder = Register(
            new CompositeGeometry(volume, targetProperties.SurfaceArea, bounds),
            topology,
            isSolid: true);

        Dictionary<ulong, FakeEntity> targetMap = CarryOver(topology, target, OperationRole.Retained);
        List<Dictionary<ulong, FakeEntity>> toolMaps = [];
        foreach (FakeShape tool in tools)
        {
            toolMaps.Add(CarryOver(
                topology,
                tool,
                definition.Operation == BooleanOperation.Subtract
                    ? OperationRole.Trimmed
                    : OperationRole.Retained));
        }

        FakeShape result = Rebuild(placeholder, topology, isSolid: true);
        KernelShape to = result.Reference;

        RecordCarryOver(history, target, targetMap, to, OperationRole.Retained);
        for (int i = 0; i < tools.Count; i++)
        {
            RecordCarryOver(
                history,
                tools[i],
                toolMaps[i],
                to,
                definition.Operation == BooleanOperation.Subtract
                    ? OperationRole.Trimmed
                    : OperationRole.Retained);
        }

        // One intersection edge per tool, created between the two bodies that produced it.
        for (int i = 0; i < tools.Count; i++)
        {
            FakeEntity intersection = topology.AddEdge(
                OperationRole.IntersectionEdge,
                i,
                bounds.IsEmpty ? Vec3d.Zero : bounds.Center,
                Vec3d.UnitX,
                bounds.IsEmpty ? 0.0 : bounds.DiagonalLength);

            result = Rebuild(result, topology, isSolid: true);
            to = result.Reference;

            history.AddNewBetween(
                new SubEntity(to, intersection.Tag, SubEntityKind.Edge),
                OperationRole.IntersectionEdge,
                new SubEntity(target.Reference, target.Faces[0].Tag, SubEntityKind.Face),
                new SubEntity(tools[i].Reference, tools[i].Faces[0].Tag, SubEntityKind.Face));
        }

        ImmutableArray<KernelDiagnostic> warnings =
        [
            KernelDiagnostic.Warning(
                KernelDiagnosticCodes.ApproximateMassProperties,
                "FakeKernel does not evaluate booleans geometrically. The topology is synthetic and "
                + "the volume is estimated from bounding-box overlap. Use OcctKernel for a real result."),
        ];

        return new OperationResult.Degraded(Track(result.Reference), history.Build(), warnings);
    }

    /// <inheritdoc />
    protected override OperationResult Fillet(FilletDefinition definition, KernelRequest request)
        => ApplyBlend(
            definition.Body,
            [.. definition.Edges.Select(e => (e.Edge, e.Radius))],
            isFillet: true);

    /// <inheritdoc />
    protected override OperationResult Chamfer(ChamferDefinition definition, KernelRequest request)
        => ApplyBlend(
            definition.Body,
            [.. definition.Edges.Select(e => (e.Edge, e.Distance))],
            isFillet: false);

    private OperationResult ApplyBlend(
        KernelShape body,
        ImmutableArray<(SubEntity Edge, double Size)> requested,
        bool isFillet)
    {
        if (!TryResolve(body, out FakeShape? shape, out OperationResult? failure))
        {
            return failure;
        }

        string what = isFillet ? "fillet" : "chamfer";
        List<KernelDiagnostic> warnings = [];
        List<(FakeEntity Edge, double Size)> applicable = [];

        foreach ((SubEntity edge, double size) in requested)
        {
            FakeEntity? found = shape.Find(edge.Tag);
            if (found is null || found.Kind != SubEntityKind.Edge)
            {
                warnings.Add(KernelDiagnostic.Warning(
                    KernelDiagnosticCodes.BlendFailed,
                    $"The selected edge is no longer part of this body, so it cannot be {what}ed.",
                    [edge]));
                continue;
            }

            // The characteristic check a real kernel makes: a blend cannot consume more material
            // than the adjacent faces provide. Approximated here by comparing against the
            // shortest adjacent face extent, which is enough to exercise the Degraded path.
            double available = SmallestAdjacentExtent(shape, found);
            if (size >= available)
            {
                warnings.Add(KernelDiagnostic.Warning(
                    KernelDiagnosticCodes.BlendRadiusTooLarge,
                    $"The {what} of {Format(size)} m exceeds the material available at this edge "
                    + $"(about {Format(available)} m). Try a smaller value, or apply this feature "
                    + "earlier in the tree.",
                    [edge]));
                continue;
            }

            applicable.Add((found, size));
        }

        if (applicable.Count == 0)
        {
            return new OperationResult.Failed(
                [.. warnings.Select(w => w with { Severity = DiagnosticSeverity.Error })],
                RetryRung.PerEntityIsolation);
        }

        TopologyBuilder topology = new(NextTag);
        HistoryMapBuilder history = new();

        Dictionary<ulong, FakeEntity> carried = CarryOver(
            topology, shape, OperationRole.Retained, skipEdges: [.. applicable.Select(a => a.Edge.Tag)]);

        // One blend face per edge, created between the two faces that edge separated.
        List<(FakeEntity Blend, FakeEntity Source)> blends = [];
        for (int i = 0; i < applicable.Count; i++)
        {
            (FakeEntity edge, double size) = applicable[i];
            double blendArea = isFillet
                ? edge.Measure * size * System.Math.PI / 2.0
                : edge.Measure * size * System.Math.Sqrt(2.0);

            blends.Add((
                topology.AddFace(
                    isFillet ? OperationRole.BlendFace : OperationRole.BlendFace,
                    i,
                    edge.Point,
                    edge.Direction.IsZeroLength ? Vec3d.UnitZ : edge.Direction.AnyPerpendicular(),
                    blendArea),
                edge));
        }

        double removed = 0.0;
        foreach ((FakeEntity edge, double size) in applicable)
        {
            // Volume of the material a constant blend removes along an edge, per unit length.
            double crossSection = isFillet
                ? (size * size) - (System.Math.PI * size * size / 4.0)
                : size * size / 2.0;

            removed += crossSection * edge.Measure;
        }

        MassProperties before = shape.Geometry.Compute(1000.0);
        FakeShape result = Register(
            new CompositeGeometry(
                System.Math.Max(before.Volume - removed, before.Volume * 0.5),
                before.SurfaceArea,
                shape.Geometry.Bounds),
            topology,
            isSolid: shape.IsSolid);

        KernelShape from = shape.Reference;
        KernelShape to = result.Reference;

        RecordCarryOver(history, shape, carried, to, OperationRole.Retained);

        foreach ((FakeEntity blend, FakeEntity source) in blends)
        {
            SubEntity blendFace = new(to, blend.Tag, SubEntityKind.Face);

            // The blend face is named as the blend between the two faces the edge separated, not
            // as an anonymous new face. That relationship is what survives a rebuild.
            if (source.AdjacentFaces.Length >= 2)
            {
                history.AddNewBetween(
                    blendFace,
                    OperationRole.BlendFace,
                    new SubEntity(from, source.AdjacentFaces[0], SubEntityKind.Face),
                    new SubEntity(from, source.AdjacentFaces[1], SubEntityKind.Face));
            }
            else
            {
                history.AddGenerated(
                    new SubEntity(from, source.Tag, SubEntityKind.Edge),
                    blendFace,
                    OperationRole.BlendFace);
            }

            history.AddDeleted(new SubEntity(from, source.Tag, SubEntityKind.Edge));
        }

        HistoryMap map = history.Build();

        return warnings.Count == 0
            ? new OperationResult.Success(Track(result.Reference), map, RetryRung.ModelTolerance)
            : new OperationResult.Degraded(
                Track(result.Reference), map, [.. warnings], RetryRung.PerEntityIsolation);
    }

    private static double SmallestAdjacentExtent(FakeShape shape, FakeEntity edge)
    {
        double smallest = double.MaxValue;
        foreach (ulong faceTag in edge.AdjacentFaces)
        {
            FakeEntity? face = shape.Faces.FirstOrDefault(f => f.Tag == faceTag);
            if (face is not null && face.Measure > 0)
            {
                smallest = System.Math.Min(smallest, System.Math.Sqrt(face.Measure));
            }
        }

        return smallest == double.MaxValue ? edge.Measure : smallest;
    }

    private static string Format(double value)
        => value.ToString("G4", System.Globalization.CultureInfo.InvariantCulture);

    // --- Queries -----------------------------------------------------------------------------------------------

    /// <inheritdoc />
    protected override KernelResult<MassProperties> ComputeMassProperties(
        KernelShape shape, double density, KernelRequest request)
    {
        if (!TryResolve(shape, out FakeShape? found, out KernelResult<MassProperties>? failure))
        {
            return failure;
        }

        MassProperties properties = found.Geometry.Compute(density);

        return properties.Accuracy == ResultAccuracy.Exact
            ? KernelResult.Ok(properties)
            : KernelResult.Degraded(properties,
            [
                KernelDiagnostic.Warning(
                    KernelDiagnosticCodes.ApproximateMassProperties,
                    "These mass properties are approximate. FakeKernel does not evaluate booleans "
                    + "or blends geometrically."),
            ]);
    }

    /// <inheritdoc />
    protected override KernelResult<Bounds3d> ComputeBounds(KernelShape shape, KernelRequest request)
        => TryResolve(shape, out FakeShape? found, out KernelResult<Bounds3d>? failure)
            ? KernelResult.Ok(found.Geometry.Bounds)
            : failure;

    /// <inheritdoc />
    protected override KernelResult<TopologyCounts> CountTopology(KernelShape shape, KernelRequest request)
        => TryResolve(shape, out FakeShape? found, out KernelResult<TopologyCounts>? failure)
            ? KernelResult.Ok(found.Counts)
            : failure;

    /// <inheritdoc />
    protected override KernelResult<ImmutableArray<SubEntity>> Enumerate(
        KernelShape shape, SubEntityKind kind, KernelRequest request)
    {
        if (!TryResolve(shape, out FakeShape? found, out KernelResult<ImmutableArray<SubEntity>>? failure))
        {
            return failure;
        }

        // Already in canonical order: the topology builder assigns tags in that order.
        ImmutableArray<SubEntity> entities =
            [.. found.EntitiesOf(kind).Select(e => new SubEntity(found.Reference, e.Tag, e.Kind))];

        return KernelResult.Ok(entities);
    }

    /// <inheritdoc />
    protected override KernelResult<ShapeValidity> CheckValidity(KernelShape shape, KernelRequest request)
    {
        if (!TryResolve(shape, out FakeShape? found, out KernelResult<ShapeValidity>? failure))
        {
            return failure;
        }

        List<KernelDiagnostic> problems = [];

        if (found.Faces.IsEmpty)
        {
            problems.Add(KernelDiagnostic.Error(
                KernelDiagnosticCodes.InvalidResult, "The shape has no faces."));
        }

        foreach (FakeEntity face in found.Faces)
        {
            if (face.Role == OperationRole.Unknown)
            {
                problems.Add(KernelDiagnostic.Error(
                    KernelDiagnosticCodes.InvalidResult,
                    "A face carries no operation role, which means the operation that created it "
                    + "is incomplete.",
                    [new SubEntity(found.Reference, face.Tag, SubEntityKind.Face)]));
            }
        }

        return KernelResult.Ok(new ShapeValidity(
            problems.Count == 0, found.IsSolid, [.. problems]));
    }

    /// <inheritdoc />
    protected override KernelResult<MeshBuffer> Triangulate(
        KernelShape shape, TessellationOptions options, KernelRequest request)
    {
        if (!TryResolve(shape, out FakeShape? found, out KernelResult<MeshBuffer>? failure))
        {
            return failure;
        }

        MeshAccumulator mesh = new();
        found.Geometry.Tessellate(mesh, options, 0);

        ImmutableArray<SubEntity> faces =
            [.. found.Faces.Select(f => new SubEntity(found.Reference, f.Tag, SubEntityKind.Face))];

        return KernelResult.Ok(mesh.Build(faces));
    }

    // --- Serialisation -------------------------------------------------------------------------------------------

    /// <inheritdoc />
    protected override KernelResult<ImmutableArray<byte>> WriteBRep(KernelShape shape, KernelRequest request)
    {
        if (!TryResolve(shape, out FakeShape? found, out KernelResult<ImmutableArray<byte>>? failure))
        {
            return failure;
        }

        return KernelResult.Ok(FakeBRep.Write(found));
    }

    /// <inheritdoc />
    protected override OperationResult ReadBRep(ReadOnlyMemory<byte> data, KernelRequest request)
    {
        try
        {
            FakeBRep.Read(data.Span, NextTag, out FakeGeometry geometry, out TopologyBuilder topology, out bool isSolid);
            FakeShape result = Register(geometry, topology, isSolid);

            // Imported entities have no generative provenance, so they are named as imported and
            // tier-2 geometric matching takes over for them in P3-T10.
            HistoryMapBuilder history = new();
            KernelShape to = result.Reference;

            foreach (FakeEntity face in result.Faces)
            {
                history.AddNew(new SubEntity(to, face.Tag, SubEntityKind.Face), OperationRole.Imported);
            }

            foreach (FakeEntity edge in result.Edges)
            {
                history.AddNew(new SubEntity(to, edge.Tag, SubEntityKind.Edge), OperationRole.Imported);
            }

            foreach (FakeEntity vertex in result.Vertices)
            {
                history.AddNew(new SubEntity(to, vertex.Tag, SubEntityKind.Vertex), OperationRole.Imported);
            }

            return new OperationResult.Success(Track(to), history.Build());
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
        {
            return OperationResult.Failed.From(
                KernelDiagnosticCodes.SerializationFailed,
                "The cached geometry could not be read. It will be rebuilt from the feature tree.",
                kernelDetail: exception.Message);
        }
    }

    /// <inheritdoc />
    protected override KernelResult<int> WriteStep(
        ImmutableArray<KernelShape> shapes, Stream destination, KernelRequest request)
    {
        List<FakeShape> resolved = [];
        foreach (KernelShape shape in shapes)
        {
            if (!TryResolve(shape, out FakeShape? found, out KernelResult<int>? failure))
            {
                return failure;
            }

            resolved.Add(found);
        }

        int written = FakeStepWriter.Write(destination, resolved);

        // Honest about what this is. The file is well formed ISO-10303-21 and carries the product
        // structure, but FakeKernel has no B-rep to export, so there is no shape representation in
        // it. Silently writing a geometry-free file that claims to be a STEP export of a model is
        // exactly the sort of thing that wastes an afternoon downstream.
        return KernelResult.Degraded(written,
        [
            KernelDiagnostic.Warning(
                KernelDiagnosticCodes.SerializationFailed,
                "FakeKernel wrote STEP product structure only; the file contains no geometry. "
                + "Use OcctKernel for a real STEP export."),
        ]);
    }

    /// <inheritdoc />
    protected override void ReleaseShape(KernelShape shape)
    {
        lock (_shapes)
        {
            if (!_shapes.Remove(shape.Tag, out FakeShape? removed))
            {
                return;
            }

            // Bump the generation so any tag still referring to this slot is detected as stale
            // rather than aliasing whatever is allocated next. The native shim does the same.
            ulong index = shape.Tag & IndexMask;
            _generations[index] = _generations.GetValueOrDefault(index) + 1;

            foreach (FakeEntity entity in removed.Faces.Concat(removed.Edges).Concat(removed.Vertices))
            {
                _entityOwners.Remove(entity.Tag);
            }
        }
    }

    // --- Internals ------------------------------------------------------------------------------------------------

    private ulong NextTag()
    {
        ulong index = _nextIndex++;
        uint generation = _generations.GetValueOrDefault(index);
        return index | ((ulong)generation << IndexBits);
    }

    private OperationResult.Success CompletePrimitive(
        FakeGeometry geometry, TopologyBuilder topology, bool isSolid)
    {
        FakeShape shape = Register(geometry, topology, isSolid);

        HistoryMapBuilder history = new();
        KernelShape reference = shape.Reference;

        foreach (FakeEntity entity in shape.Faces.Concat(shape.Edges).Concat(shape.Vertices))
        {
            history.AddNew(new SubEntity(reference, entity.Tag, entity.Kind), entity.Role);
        }

        return new OperationResult.Success(Track(reference), history.Build());
    }

    private FakeShape Register(FakeGeometry geometry, TopologyBuilder topology, bool isSolid)
    {
        ulong tag = NextTag();
        FakeShape shape = new(
            tag, isSolid, geometry, topology.Faces, topology.Edges, topology.Vertices);

        lock (_shapes)
        {
            _shapes[tag] = shape;
            foreach (FakeEntity entity in shape.Faces.Concat(shape.Edges).Concat(shape.Vertices))
            {
                _entityOwners[entity.Tag] = tag;
            }
        }

        return shape;
    }

    private FakeShape Rebuild(FakeShape existing, TopologyBuilder topology, bool isSolid)
    {
        FakeShape shape = new(
            existing.Tag, isSolid, existing.Geometry, topology.Faces, topology.Edges, topology.Vertices);

        lock (_shapes)
        {
            _shapes[existing.Tag] = shape;
            foreach (FakeEntity entity in shape.Faces.Concat(shape.Edges).Concat(shape.Vertices))
            {
                _entityOwners[entity.Tag] = existing.Tag;
            }
        }

        return shape;
    }

    private static Dictionary<ulong, FakeEntity> CarryOver(
        TopologyBuilder topology,
        FakeShape source,
        OperationRole role,
        HashSet<ulong>? skipEdges = null)
    {
        Dictionary<ulong, FakeEntity> map = [];

        foreach (FakeEntity face in source.Faces)
        {
            map[face.Tag] = topology.AddFace(role, face.Ordinal, face.Point, face.Direction, face.Measure);
        }

        foreach (FakeEntity edge in source.Edges)
        {
            if (skipEdges?.Contains(edge.Tag) == true)
            {
                continue;
            }

            map[edge.Tag] = topology.AddEdge(
                role,
                edge.Ordinal,
                edge.Point,
                edge.Direction,
                edge.Measure,
                [.. edge.AdjacentFaces.Select(t => map.TryGetValue(t, out FakeEntity? f) ? f.Tag : t)]);
        }

        foreach (FakeEntity vertex in source.Vertices)
        {
            map[vertex.Tag] = topology.AddVertex(role, vertex.Ordinal, vertex.Point);
        }

        return map;
    }

    private static void RecordCarryOver(
        HistoryMapBuilder history,
        FakeShape source,
        Dictionary<ulong, FakeEntity> map,
        KernelShape target,
        OperationRole role)
    {
        foreach (FakeEntity entity in source.Faces.Concat(source.Edges).Concat(source.Vertices))
        {
            if (!map.TryGetValue(entity.Tag, out FakeEntity? carried))
            {
                history.AddDeleted(new SubEntity(source.Reference, entity.Tag, entity.Kind));
                continue;
            }

            history.AddModified(
                new SubEntity(source.Reference, entity.Tag, entity.Kind),
                new SubEntity(target, carried.Tag, carried.Kind),
                role);
        }
    }

    private static void RoleRemainingOutputs(
        HistoryMapBuilder history,
        FakeShape result,
        ImmutableArray<FakeEntity> sideWalls,
        FakeEntity startCap,
        FakeEntity endCap,
        ImmutableArray<FakeEntity> sideEdges)
    {
        HashSet<ulong> already = [.. sideWalls.Select(w => w.Tag), startCap.Tag, endCap.Tag];
        foreach (FakeEntity edge in sideEdges)
        {
            already.Add(edge.Tag);
        }

        KernelShape reference = result.Reference;

        foreach (FakeEntity entity in result.Faces.Concat(result.Edges).Concat(result.Vertices))
        {
            if (already.Contains(entity.Tag))
            {
                continue;
            }

            history.AddNew(new SubEntity(reference, entity.Tag, entity.Kind), entity.Role);
        }
    }

    private bool TryResolve(
        KernelShape shape,
        out FakeShape resolved,
        out OperationResult failure)
    {
        if (TryLookup(shape, out FakeShape? found))
        {
            resolved = found;
            failure = null!;
            return true;
        }

        resolved = null!;
        failure = OperationResult.Failed.From(
            KernelDiagnosticCodes.StaleHandle,
            "The body this operation refers to no longer exists. It was released, or belongs to a "
            + "different kernel session.");

        return false;
    }

    private bool TryResolve<T>(
        KernelShape shape,
        out FakeShape resolved,
        out KernelResult<T> failure)
    {
        if (TryLookup(shape, out FakeShape? found))
        {
            resolved = found;
            failure = null!;
            return true;
        }

        resolved = null!;
        failure = KernelResult.Fail<T>(
            KernelDiagnosticCodes.StaleHandle,
            "The body this query refers to no longer exists. It was released, or belongs to a "
            + "different kernel session.");

        return false;
    }

    private bool TryLookup(KernelShape shape, out FakeShape found)
    {
        lock (_shapes)
        {
            return _shapes.TryGetValue(shape.Tag, out found!);
        }
    }

    /// <inheritdoc />
    protected override ValueTask OnDisposingAsync()
    {
        lock (_shapes)
        {
            _shapes.Clear();
            _entityOwners.Clear();
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A volumetric stand-in for a shape whose real geometry FakeKernel does not evaluate.
/// </summary>
/// <param name="VolumeValue">The estimated volume.</param>
/// <param name="AreaValue">The estimated surface area.</param>
/// <param name="BoundsValue">The bound.</param>
internal sealed record CompositeGeometry(double VolumeValue, double AreaValue, Bounds3d BoundsValue)
    : FakeGeometry
{
    internal override Bounds3d Bounds => BoundsValue;

    internal override ResultAccuracy Accuracy => ResultAccuracy.Approximate;

    internal override MassProperties Compute(double density) => new(
        VolumeValue,
        AreaValue,
        BoundsValue.IsEmpty ? Vec3d.Zero : BoundsValue.Center,
        density,
        InertiaTensor.Zero,
        ResultAccuracy.Approximate,
        0.25);

    internal override void Tessellate(MeshAccumulator mesh, TessellationOptions options, int faceIndex)
    {
        if (BoundsValue.IsEmpty)
        {
            return;
        }

        // Tessellate the bound. Wrong in detail and right in extent, which is what a caller can
        // reasonably expect from a kernel that has not evaluated the geometry.
        new BoxGeometry(BoundsValue.Size, Transform.FromTranslation(BoundsValue.Min))
            .Tessellate(mesh, options, faceIndex);
    }
}
