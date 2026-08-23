using System.Collections.Immutable;
using OpenMCAD.Kernel;
using OpenMCAD.Kernel.Fake;
using OpenMCAD.Kernel.Occt;
using OpenMCAD.Kernel.Operations;
using OpenMCAD.Math;

namespace OpenMCAD.KernelContractTests;

/// <summary>
/// Supplies each kernel implementation to the contract battery.
/// </summary>
/// <remarks>
/// <para>
/// P1-T10. The exit criterion is that "the same test battery passes against FakeKernel and
/// OcctKernel", which is what turns ADR-0002's abstraction from a nominal interface into a real
/// contract. When <c>OcctKernel</c> lands at P1-T06, adding it here is the whole integration.
/// </para>
/// <para>
/// The battery asserts what both implementations must agree on: the shape of results, provenance
/// completeness, role assignment, entity ordering, error handling, and determinism. It does
/// <b>not</b> assert exact geometry across kernels, because <c>FakeKernel</c> does not claim it —
/// <see cref="KernelCapabilities.ProducesExactMassProperties"/> is how each implementation says
/// what it promises, and the tests demand exactness only where it is promised.
/// </para>
/// </remarks>
public static class KernelImplementations
{
    /// <summary>Gets a factory per implementation under test.</summary>
    public static TheoryData<KernelFactory> All =>
    [
        new KernelFactory("fake", () => new FakeKernel()),
        new KernelFactory("occt", () => new OcctKernel()),
    ];
}

/// <summary>Creates a kernel under test.</summary>
/// <param name="Name">The implementation name, shown in test output.</param>
/// <param name="Create">Creates a fresh instance.</param>
public sealed record KernelFactory(string Name, Func<IGeometryKernel> Create)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
/// The behaviour every geometry kernel must exhibit.
/// </summary>
public sealed class KernelContractTests
{
    private static readonly ImmutableArray<Vec2d> UnitSquare =
    [
        new(0, 0), new(1, 0), new(1, 1), new(0, 1),
    ];

    // --- Primitives ---------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(KernelImplementations.All), MemberType = typeof(KernelImplementations))]
    public async Task Box_HasTheTopologyOfABox(KernelFactory factory)
    {
        await using IGeometryKernel kernel = factory.Create();

        OperationResult result = await kernel.CreateBoxAsync(new BoxDefinition(2, 3, 4));

        result.Should().BeOfType<OperationResult.Success>();
        result.TryGetShape(out KernelShapeHandle shape, out _).Should().BeTrue();
        using (shape)
        {
            TopologyCounts counts = (await kernel.CountTopologyAsync(shape.Shape)).Value;
            counts.Faces.Should().Be(6);
            counts.Edges.Should().Be(12);
            counts.Vertices.Should().Be(8);
            counts.Solids.Should().Be(1);
        }
    }

    [Theory]
    [MemberData(nameof(KernelImplementations.All), MemberType = typeof(KernelImplementations))]
    public async Task Box_HasExactMassProperties(KernelFactory factory)
    {
        await using IGeometryKernel kernel = factory.Create();

        using KernelShapeHandle box = await CreateAsync(
            kernel.CreateBoxAsync(new BoxDefinition(2, 3, 4)));

        MassProperties properties = (await kernel.ComputeMassPropertiesAsync(box.Shape, 7850.0)).Value;

        // A box is exact in every kernel worth using. If this is approximate, something is wrong
        // with the implementation rather than with the test.
        properties.Accuracy.Should().Be(ResultAccuracy.Exact);
        properties.Volume.Should().BeApproximately(24.0, 1e-9);
        properties.SurfaceArea.Should().BeApproximately(52.0, 1e-9);
        properties.Centroid.IsNear(new Vec3d(1, 1.5, 2), 1e-9).Should().BeTrue();
        properties.Mass.Should().BeApproximately(24.0 * 7850.0, 1e-6);
    }

    [Theory]
    [MemberData(nameof(KernelImplementations.All), MemberType = typeof(KernelImplementations))]
    public async Task Sphere_MatchesTheClosedFormVolume(KernelFactory factory)
    {
        await using IGeometryKernel kernel = factory.Create();

        using KernelShapeHandle sphere = await CreateAsync(
            kernel.CreateSphereAsync(new SphereDefinition(0.5)));

        MassProperties properties = (await kernel.ComputeMassPropertiesAsync(sphere.Shape)).Value;

        double expected = 4.0 / 3.0 * System.Math.PI * 0.125;
        properties.Volume.Should().BeApproximately(expected, expected * 1e-6);
    }

    [Theory]
    [MemberData(nameof(KernelImplementations.All), MemberType = typeof(KernelImplementations))]
    public async Task Cone_ReducesToTheCylinderAndConeLimits(KernelFactory factory)
    {
        await using IGeometryKernel kernel = factory.Create();

        // Equal radii: a cylinder. Axial moment must be one half m r squared.
        using KernelShapeHandle cylinderLike = await CreateAsync(
            kernel.CreateConeAsync(new ConeDefinition(0.5, 0.5, 2.0)));

        MassProperties asCylinder =
            (await kernel.ComputeMassPropertiesAsync(cylinderLike.Shape, 1000.0)).Value;

        asCylinder.Volume.Should().BeApproximately(System.Math.PI * 0.25 * 2.0, 1e-9);
        asCylinder.Inertia.Izz.Should().BeApproximately(
            0.5 * asCylinder.Mass * 0.25, asCylinder.Mass * 1e-6);

        // Zero top radius: a full cone. Axial moment must be three tenths m r squared.
        using KernelShapeHandle fullCone = await CreateAsync(
            kernel.CreateConeAsync(new ConeDefinition(0.5, 0.0, 2.0)));

        MassProperties asCone = (await kernel.ComputeMassPropertiesAsync(fullCone.Shape, 1000.0)).Value;

        asCone.Volume.Should().BeApproximately(System.Math.PI * 0.25 * 2.0 / 3.0, 1e-9);
        asCone.Inertia.Izz.Should().BeApproximately(0.3 * asCone.Mass * 0.25, asCone.Mass * 1e-6);

        // Centroid of a cone sits a quarter of the way up from the base.
        asCone.Centroid.Z.Should().BeApproximately(0.5, 1e-9);
    }

    // --- Provenance ---------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(KernelImplementations.All), MemberType = typeof(KernelImplementations))]
    public async Task EveryOperation_AssignsARoleToEveryOutput(KernelFactory factory)
    {
        await using IGeometryKernel kernel = factory.Create();

        OperationResult result = await kernel.CreateBoxAsync(new BoxDefinition(1, 1, 1));
        result.TryGetShape(out KernelShapeHandle shape, out HistoryMap history).Should().BeTrue();

        using (shape)
        {
            // PLAN.md 5.1: an operation returning unrolled outputs is an incomplete implementation.
            history.UnrolledOutputs.Should().BeEmpty();
            history.Outputs.Should().NotBeEmpty();

            foreach (SubEntity output in history.Outputs)
            {
                history.RoleOf(output).Should().NotBe(OperationRole.Unknown);
            }
        }
    }

    [Theory]
    [MemberData(nameof(KernelImplementations.All), MemberType = typeof(KernelImplementations))]
    public async Task Extrude_NamesEachSideWallAfterTheProfileEdgeThatSweptIt(KernelFactory factory)
    {
        await using IGeometryKernel kernel = factory.Create();

        using KernelShapeHandle profile = await CreateAsync(
            kernel.CreatePolygonProfileAsync(new PolygonProfileDefinition(UnitSquare, Transform.Identity)));

        ImmutableArray<SubEntity> profileEdges =
            (await kernel.EnumerateAsync(profile.Shape, SubEntityKind.Edge)).Value;

        profileEdges.Should().HaveCount(4);

        OperationResult result = await kernel.ExtrudeAsync(
            new ExtrudeDefinition(profile.Shape, Vec3d.UnitZ, 5.0));

        result.TryGetShape(out KernelShapeHandle solid, out HistoryMap history).Should().BeTrue();

        using (solid)
        {
            // This is the property the whole naming layer rests on: every profile edge must be
            // traceable forward to the wall it produced, and that wall must be identified as a
            // side wall rather than as "some face".
            foreach (SubEntity edge in profileEdges)
            {
                ImmutableArray<SubEntity> generated = history.Generated(edge);

                generated.Should().ContainSingle(
                    $"profile edge {edge} should have swept exactly one side wall");

                history.RoleOf(generated[0]).Should().Be(OperationRole.SideWall);
                history.SourceOf(generated[0]).Should().Be(edge);
            }

            history.WithRole(OperationRole.StartCap).Should().ContainSingle();
            history.WithRole(OperationRole.EndCap).Should().ContainSingle();
        }
    }

    [Theory]
    [MemberData(nameof(KernelImplementations.All), MemberType = typeof(KernelImplementations))]
    public async Task Extrude_ProducesAPrismWithTheExpectedVolume(KernelFactory factory)
    {
        await using IGeometryKernel kernel = factory.Create();

        using KernelShapeHandle profile = await CreateAsync(
            kernel.CreatePolygonProfileAsync(new PolygonProfileDefinition(UnitSquare, Transform.Identity)));

        using KernelShapeHandle solid = await CreateAsync(
            kernel.ExtrudeAsync(new ExtrudeDefinition(profile.Shape, Vec3d.UnitZ, 5.0)));

        MassProperties properties = (await kernel.ComputeMassPropertiesAsync(solid.Shape)).Value;

        properties.Volume.Should().BeApproximately(5.0, 1e-9);
        properties.Centroid.IsNear(new Vec3d(0.5, 0.5, 2.5), 1e-9).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(KernelImplementations.All), MemberType = typeof(KernelImplementations))]
    public async Task Fillet_RecordsTheBlendFaceAsLyingBetweenTheFacesItJoins(KernelFactory factory)
    {
        await using IGeometryKernel kernel = factory.Create();

        using KernelShapeHandle box = await CreateAsync(
            kernel.CreateBoxAsync(new BoxDefinition(1, 1, 1)));

        ImmutableArray<SubEntity> edges = (await kernel.EnumerateAsync(box.Shape, SubEntityKind.Edge)).Value;

        OperationResult result = await kernel.FilletAsync(
            new FilletDefinition(box.Shape, 0.1, edges[0]));

        result.HasShape.Should().BeTrue(result.Describe());
        result.TryGetShape(out KernelShapeHandle rounded, out HistoryMap history).Should().BeTrue();

        using (rounded)
        {
            List<SubEntity> blends = [.. history.WithRole(OperationRole.BlendFace)];
            blends.Should().ContainSingle();

            // The filleted edge is consumed, and that must be reported rather than left implicit:
            // a downstream feature referring to it has to be told, not left to guess.
            history.IsDeleted(edges[0]).Should().BeTrue();

            // The blend must be reachable from the faces it was created between, which is what
            // makes it nameable at all.
            ImmutableArray<SubEntity> faces = (await kernel.EnumerateAsync(box.Shape, SubEntityKind.Face)).Value;
            bool reachable = faces.Any(face => history.Generated(face).Contains(blends[0]));
            reachable.Should().BeTrue(
                "a blend face must be recorded as generated from the faces it blends between");
        }
    }

    [Theory]
    [MemberData(nameof(KernelImplementations.All), MemberType = typeof(KernelImplementations))]
    public async Task Fillet_WithAnImpossibleRadius_DegradesOrFailsButNeverThrows(KernelFactory factory)
    {
        await using IGeometryKernel kernel = factory.Create();

        using KernelShapeHandle box = await CreateAsync(
            kernel.CreateBoxAsync(new BoxDefinition(1, 1, 1)));

        ImmutableArray<SubEntity> edges = (await kernel.EnumerateAsync(box.Shape, SubEntityKind.Edge)).Value;

        // A radius larger than the box cannot be applied by any kernel.
        OperationResult result = await kernel.FilletAsync(
            new FilletDefinition(box.Shape, 10.0, edges[0]));

        result.Outcome.Should().NotBe(OperationOutcome.Success);
        result.Diagnostics.Should().NotBeEmpty();

        // The standard PLAN.md 6.1 sets: the message must be actionable, not a kernel stack trace.
        KernelDiagnostic diagnostic = result.Diagnostics[0];
        diagnostic.Message.Should().NotBeNullOrWhiteSpace();
        diagnostic.Message.Should().NotContain("Exception");
        diagnostic.Entities.Should().NotBeEmpty("the failure should say which edge it was");

        if (result is OperationResult.Degraded or OperationResult.Success)
        {
            result.TryGetShape(out KernelShapeHandle shape, out _);
            shape.Dispose();
        }
    }

    // --- Ordering and determinism -----------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(KernelImplementations.All), MemberType = typeof(KernelImplementations))]
    public async Task Enumerate_ReturnsEntitiesInAStableOrder(KernelFactory factory)
    {
        await using IGeometryKernel kernel = factory.Create();

        using KernelShapeHandle box = await CreateAsync(
            kernel.CreateBoxAsync(new BoxDefinition(2, 3, 4)));

        ImmutableArray<SubEntity> first = (await kernel.EnumerateAsync(box.Shape, SubEntityKind.Face)).Value;

        for (int i = 0; i < 10; i++)
        {
            ImmutableArray<SubEntity> again =
                (await kernel.EnumerateAsync(box.Shape, SubEntityKind.Face)).Value;

            again.Should().Equal(first, "entity order is part of the contract, not an accident");
        }
    }

    [Theory]
    [MemberData(nameof(KernelImplementations.All), MemberType = typeof(KernelImplementations))]
    public async Task IdenticalInputs_ProduceIdenticalTopologyAndNames(KernelFactory factory)
    {
        // The determinism gate (ADR-0011, P1-T12) in miniature. Two fresh kernels given the same
        // instructions must agree on everything a name is built from.
        static async Task<(TopologyCounts Counts, List<string> Roles)> BuildAsync(IGeometryKernel kernel)
        {
            using KernelShapeHandle profile = await CreateAsync(
                kernel.CreatePolygonProfileAsync(
                    new PolygonProfileDefinition(UnitSquare, Transform.Identity)));

            OperationResult result = await kernel.ExtrudeAsync(
                new ExtrudeDefinition(profile.Shape, Vec3d.UnitZ, 3.0));

            result.TryGetShape(out KernelShapeHandle solid, out HistoryMap history);

            using (solid)
            {
                TopologyCounts counts = (await kernel.CountTopologyAsync(solid.Shape)).Value;
                List<string> roles = [.. history.Outputs.Select(o => $"{o.Kind}:{history.RoleOf(o)}")];
                return (counts, roles);
            }
        }

        await using IGeometryKernel first = factory.Create();
        await using IGeometryKernel second = factory.Create();

        (TopologyCounts countsA, List<string> rolesA) = await BuildAsync(first);
        (TopologyCounts countsB, List<string> rolesB) = await BuildAsync(second);

        countsA.Should().Be(countsB);
        rolesA.Should().Equal(rolesB);
    }

    // --- Validation ---------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(KernelImplementations.All), MemberType = typeof(KernelImplementations))]
    public async Task InvalidDimensions_AreRejectedWithoutTouchingTheKernel(KernelFactory factory)
    {
        await using IGeometryKernel kernel = factory.Create();

        OperationResult result = await kernel.CreateBoxAsync(new BoxDefinition(0, 1, 1));

        result.Should().BeOfType<OperationResult.Failed>();
        result.Diagnostics.Should().ContainSingle();
        result.Diagnostics[0].Code.Should().Be(KernelDiagnosticCodes.InvalidDimension);
    }

    [Theory]
    [MemberData(nameof(KernelImplementations.All), MemberType = typeof(KernelImplementations))]
    public async Task DegenerateProfile_IsRejected(KernelFactory factory)
    {
        await using IGeometryKernel kernel = factory.Create();

        ImmutableArray<Vec2d> collinear = [new(0, 0), new(1, 0), new(2, 0)];

        OperationResult result = await kernel.CreatePolygonProfileAsync(
            new PolygonProfileDefinition(collinear, Transform.Identity));

        result.Should().BeOfType<OperationResult.Failed>();
        result.Diagnostics.Select(d => d.Code).Should().Contain(KernelDiagnosticCodes.InvalidProfile);
    }

    [Theory]
    [MemberData(nameof(KernelImplementations.All), MemberType = typeof(KernelImplementations))]
    public async Task StaleShape_IsDetectedRatherThanAliased(KernelFactory factory)
    {
        await using IGeometryKernel kernel = factory.Create();

        KernelShape stale;
        using (KernelShapeHandle box = await CreateAsync(kernel.CreateBoxAsync(new BoxDefinition(1, 1, 1))))
        {
            stale = box.Shape;
        }

        // The handle is disposed. Give the release a moment to run on the kernel thread, then
        // confirm the tag is rejected rather than resolving to whatever now occupies that slot.
        await kernel.ComputeBoundsAsync(KernelShape.None with { Tag = 0 });

        using KernelShapeHandle replacement = await CreateAsync(
            kernel.CreateBoxAsync(new BoxDefinition(9, 9, 9)));

        KernelResult<TopologyCounts> result = await kernel.CountTopologyAsync(stale);

        if (result.HasValue)
        {
            // If it resolved, it must at least not have silently become the replacement body.
            result.Value.Should().NotBe(new TopologyCounts(1, 1, 6, 6, 12, 8),
                "a released tag must never resolve to a different body");
        }
        else
        {
            result.Diagnostics[0].Code.Should().Be(KernelDiagnosticCodes.StaleHandle);
        }
    }

    // --- Tessellation and serialisation -------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(KernelImplementations.All), MemberType = typeof(KernelImplementations))]
    public async Task Triangulate_ProducesAClosedMeshAttributedToFaces(KernelFactory factory)
    {
        await using IGeometryKernel kernel = factory.Create();

        using KernelShapeHandle box = await CreateAsync(
            kernel.CreateBoxAsync(new BoxDefinition(2, 3, 4)));

        MeshBuffer mesh = (await kernel.TriangulateAsync(box.Shape)).Value;

        mesh.TriangleCount.Should().BeGreaterThan(0);
        mesh.Indices.Length.Should().Be(mesh.TriangleCount * 3);
        mesh.TriangleFaces.Length.Should().Be(mesh.TriangleCount);
        mesh.Normals.Length.Should().Be(mesh.VertexCount);

        // Every triangle must name a face that exists, or picking cannot work.
        foreach (int faceIndex in mesh.TriangleFaces)
        {
            faceIndex.Should().BeInRange(0, mesh.Faces.Length - 1);
        }

        // PLAN.md 8.3: cross-check the volume a second way. A mesh whose divergence volume
        // disagrees with the analytic one has inverted normals or an unclosed shell, which no
        // amount of mass-property checking would reveal.
        mesh.ComputeVolumeByDivergence().Should().BeApproximately(24.0, 0.01);
    }

    [Theory]
    [MemberData(nameof(KernelImplementations.All), MemberType = typeof(KernelImplementations))]
    public async Task BRep_RoundTripsWithoutLosingTheShape(KernelFactory factory)
    {
        await using IGeometryKernel kernel = factory.Create();

        using KernelShapeHandle original = await CreateAsync(
            kernel.CreateCylinderAsync(new CylinderDefinition(0.25, 1.5)));

        MassProperties before = (await kernel.ComputeMassPropertiesAsync(original.Shape)).Value;
        ImmutableArray<byte> bytes = (await kernel.WriteBRepAsync(original.Shape)).Value;

        bytes.Should().NotBeEmpty();

        OperationResult read = await kernel.ReadBRepAsync(bytes.AsMemory());
        read.TryGetShape(out KernelShapeHandle restored, out HistoryMap history).Should().BeTrue();

        using (restored)
        {
            MassProperties after = (await kernel.ComputeMassPropertiesAsync(restored.Shape)).Value;
            after.Volume.Should().BeApproximately(before.Volume, before.Volume * 1e-9);

            // Read geometry has no generative provenance, so it must be marked imported and let
            // tier-2 geometric matching take over (PLAN.md 5.3).
            history.Outputs.Should().NotBeEmpty();
            history.Outputs.Should().OnlyContain(o => history.RoleOf(o) == OperationRole.Imported);
        }
    }

    [Theory]
    [MemberData(nameof(KernelImplementations.All), MemberType = typeof(KernelImplementations))]
    public async Task BRep_RoundTripIsByteIdentical(KernelFactory factory)
    {
        await using IGeometryKernel kernel = factory.Create();

        using KernelShapeHandle original = await CreateAsync(
            kernel.CreateBoxAsync(new BoxDefinition(1, 2, 3)));

        ImmutableArray<byte> first = (await kernel.WriteBRepAsync(original.Shape)).Value;
        ImmutableArray<byte> second = (await kernel.WriteBRepAsync(original.Shape)).Value;

        // These blobs become the geometry cache inside a saved document (P3-T18). If they varied
        // between writes, every saved file would differ from itself.
        second.Should().Equal(first);
    }

    [Theory]
    [MemberData(nameof(KernelImplementations.All), MemberType = typeof(KernelImplementations))]
    public async Task ReadBRep_OnGarbage_FailsCleanly(KernelFactory factory)
    {
        await using IGeometryKernel kernel = factory.Create();

        byte[] garbage = [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01, 0x02, 0x03];

        OperationResult result = await kernel.ReadBRepAsync(garbage);

        // A corrupt cache means rebuild, not data loss (ADR-0010).
        result.Should().BeOfType<OperationResult.Failed>();
        result.Diagnostics[0].Code.Should().Be(KernelDiagnosticCodes.SerializationFailed);
    }

    [Theory]
    [MemberData(nameof(KernelImplementations.All), MemberType = typeof(KernelImplementations))]
    public async Task WriteStep_ProducesAWellFormedFile(KernelFactory factory)
    {
        await using IGeometryKernel kernel = factory.Create();

        using KernelShapeHandle box = await CreateAsync(
            kernel.CreateBoxAsync(new BoxDefinition(1, 1, 1)));

        using MemoryStream stream = new();
        KernelResult<int> result = await kernel.WriteStepAsync([box.Shape], stream);

        result.HasValue.Should().BeTrue();
        result.Value.Should().BeGreaterThan(0);

        string text = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        text.Should().StartWith("ISO-10303-21;");
        text.Should().EndWith("END-ISO-10303-21;\n");
        text.Should().Contain("FILE_SCHEMA");
    }

    // --- Validity ---------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(KernelImplementations.All), MemberType = typeof(KernelImplementations))]
    public async Task EveryProducedShape_IsValid(KernelFactory factory)
    {
        await using IGeometryKernel kernel = factory.Create();

        using KernelShapeHandle box = await CreateAsync(kernel.CreateBoxAsync(new BoxDefinition(1, 2, 3)));
        using KernelShapeHandle cylinder = await CreateAsync(
            kernel.CreateCylinderAsync(new CylinderDefinition(0.5, 2)));

        foreach (KernelShapeHandle shape in new[] { box, cylinder })
        {
            ShapeValidity validity = (await kernel.CheckValidityAsync(shape.Shape)).Value;

            // PLAN.md 8.3: an invalid shape is a failure even if it looks right.
            validity.IsValid.Should().BeTrue(string.Join("; ", validity.Problems.Select(p => p.Message)));
            validity.IsClosed.Should().BeTrue();
        }
    }

    // --- The retry ladder (P1-T11, PLAN.md 5.2.4) ----------------------------------------------

    [Theory]
    [MemberData(nameof(KernelImplementations.All), MemberType = typeof(KernelImplementations))]
    public async Task HealthyOperation_ReportsThatItSucceededOnTheFirstRung(KernelFactory factory)
    {
        await using IGeometryKernel kernel = factory.Create();

        if (!kernel.Capabilities.SupportsRetryLadder)
        {
            return;
        }

        using KernelShapeHandle box = await CreateAsync(
            kernel.CreateBoxAsync(new BoxDefinition(1, 1, 1)));

        ImmutableArray<SubEntity> edges =
            (await kernel.EnumerateAsync(box.Shape, SubEntityKind.Edge)).Value;

        OperationResult result = await kernel.FilletAsync(
            new FilletDefinition(box.Shape, 0.1, edges[0]));

        result.TryGetShape(out KernelShapeHandle rounded, out _).Should().BeTrue(result.Describe());
        using (rounded)
        {
            // The health metric in PLAN.md 5.2.4 is the distribution of this value across the
            // corpus. A blend this easy reporting anything but the first rung means either the
            // ladder is firing when it should not, or something regressed in the kernel.
            result.Rung.Should().Be(
                RetryRung.ModelTolerance,
                "a 0.1 fillet on a unit cube needs no help at all");
        }
    }

    [Theory]
    [MemberData(nameof(KernelImplementations.All), MemberType = typeof(KernelImplementations))]
    public async Task Blend_WhenOnlySomeEdgesFit_DegradesAndNamesTheOnesItSkipped(
        KernelFactory factory)
    {
        await using IGeometryKernel kernel = factory.Create();

        if (!kernel.Capabilities.SupportsRetryLadder)
        {
            return;
        }

        // A thin plate. The four short edges through the thickness have the whole plate to blend
        // into; the eight long ones have only the thickness, so a radius between the two is
        // possible for some of the selection and impossible for the rest.
        using KernelShapeHandle plate = await CreateAsync(
            kernel.CreateBoxAsync(new BoxDefinition(1.0, 1.0, 0.05)));

        ImmutableArray<SubEntity> edges =
            (await kernel.EnumerateAsync(plate.Shape, SubEntityKind.Edge)).Value;

        edges.Should().HaveCount(12);

        OperationResult result = await kernel.FilletAsync(
            new FilletDefinition(plate.Shape, 0.2, [.. edges]));

        // Rung 4: some of what was asked for, and an explicit account of the rest. Failing the
        // whole feature because one edge of twelve was impossible is the behaviour the ladder
        // exists to prevent.
        result.Outcome.Should().Be(OperationOutcome.Degraded, result.Describe());

        result.TryGetShape(out KernelShapeHandle blended, out _).Should().BeTrue();
        using (blended)
        {
            (await kernel.CheckValidityAsync(blended.Shape)).Value.IsValid.Should().BeTrue(
                "a partially applied blend must still leave a sound body");

            KernelDiagnostic reported = result.Diagnostics.Should()
                .ContainSingle(d => d.Code == KernelDiagnosticCodes.BlendPartiallyApplied).Subject;

            reported.Entities.Should().NotBeEmpty(
                "the user has to be told which edges to change, not merely that some failed");

            reported.Entities.Should().BeSubsetOf(
                edges, "every skipped edge must be one the caller actually selected");
        }
    }

    private static async Task<KernelShapeHandle> CreateAsync(ValueTask<OperationResult> operation)
    {
        OperationResult result = await operation;
        result.TryGetShape(out KernelShapeHandle shape, out _).Should().BeTrue(result.Describe());
        return shape;
    }
}
