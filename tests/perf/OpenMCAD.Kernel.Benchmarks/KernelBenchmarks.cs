using System.Collections.Immutable;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

using OpenMCAD.Kernel;
using OpenMCAD.Kernel.Fake;
using OpenMCAD.Kernel.Occt;
using OpenMCAD.Kernel.Operations;
using OpenMCAD.Math;

namespace OpenMCAD.Kernel.Benchmarks;

/// <summary>
/// Which kernel a benchmark runs against.
/// </summary>
/// <remarks>
/// Both, always. <c>FakeKernel</c> is not a control group for OCCT — it computes different things
/// — but it is the floor: it measures what the dispatcher, the handle plumbing and the history
/// machinery cost with the geometry taken out. A gap that grows between the two is a regression in
/// the shim; a gap that shrinks usually means the fake stopped being cheap.
/// </remarks>
public enum KernelUnderTest
{
    /// <summary>The analytic mock.</summary>
    Fake,

    /// <summary>Open CASCADE through the shim.</summary>
    Occt,
}

/// <summary>
/// Per-operation timings for the kernel (P1-T15).
/// </summary>
/// <remarks>
/// <para>
/// These feed the §7 budget for a cold full rebuild of a hundred-feature part in under eight
/// seconds, which is an average of eighty milliseconds a feature. An operation that costs more
/// than that on its own is not automatically wrong, but it does have to be accounted for.
/// </para>
/// <para>
/// Every benchmark measures one operation including its history map, because that is what a
/// rebuild actually pays. Timing the geometry alone would flatter the shim and hide the cost of
/// the thing ADR-0005 depends on.
/// </para>
/// <para>
/// The kernel is created once per parameter set and reused. Constructing one starts a thread and
/// initialises OCCT, which is a real cost but not a per-operation one, and folding it into every
/// sample would drown the measurement.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput)]
public class KernelBenchmarks
{
    private IGeometryKernel _kernel = null!;
    private KernelShapeHandle _block = null!;
    private KernelShapeHandle _tool = null!;
    private KernelShapeHandle _profile = null!;
    private ImmutableArray<SubEntity> _blockEdges;

    /// <summary>Gets or sets the kernel under test.</summary>
    [Params(KernelUnderTest.Fake, KernelUnderTest.Occt)]
    public KernelUnderTest Kernel { get; set; }

    /// <summary>Creates the kernel and the shapes the benchmarks operate on.</summary>
    [GlobalSetup]
    public async Task SetUpAsync()
    {
        _kernel = Kernel switch
        {
            KernelUnderTest.Occt => new OcctKernel(),
            _ => new FakeKernel(),
        };

        _block = await CreateAsync(_kernel.CreateBoxAsync(new BoxDefinition(0.100, 0.060, 0.040)));

        // Offset so the cut removes material rather than touching exactly along a face, which is
        // the ambiguous case and would measure the retry ladder instead of the boolean.
        _tool = await CreateAsync(_kernel.CreateCylinderAsync(
            new CylinderDefinition(
                0.010, 0.080, new Transform(Quatd.Identity, new Vec3d(0.05, 0.03, -0.02), 1.0))));

        _profile = await CreateAsync(_kernel.CreatePolygonProfileAsync(
            new PolygonProfileDefinition(
                [new Vec2d(0, 0), new Vec2d(0.05, 0), new Vec2d(0.05, 0.03), new Vec2d(0, 0.03)],
                Transform.Identity)));

        _blockEdges = (await _kernel.EnumerateAsync(_block.Shape, SubEntityKind.Edge)).Value;
    }

    /// <summary>Releases the shapes and the kernel.</summary>
    [GlobalCleanup]
    public async Task TearDownAsync()
    {
        _profile.Dispose();
        _tool.Dispose();
        _block.Dispose();
        await _kernel.DisposeAsync();
    }

    /// <summary>The cheapest thing the kernel can be asked to do, and so the floor for everything.</summary>
    [Benchmark(Baseline = true, Description = "create box")]
    public async Task<int> CreateBoxAsync()
    {
        using KernelShapeHandle shape = await CreateAsync(
            _kernel.CreateBoxAsync(new BoxDefinition(0.100, 0.060, 0.040)));
        return shape.Shape.Tag.GetHashCode();
    }

    /// <summary>A surface of revolution: curved geometry and a seam to name.</summary>
    [Benchmark(Description = "create cylinder")]
    public async Task<int> CreateCylinderAsync()
    {
        using KernelShapeHandle shape = await CreateAsync(
            _kernel.CreateCylinderAsync(new CylinderDefinition(0.020, 0.075, Transform.Identity)));
        return shape.Shape.Tag.GetHashCode();
    }

    /// <summary>The commonest modelling operation there is.</summary>
    [Benchmark(Description = "extrude profile")]
    public async Task<int> ExtrudeAsync()
    {
        using KernelShapeHandle shape = await CreateAsync(
            _kernel.ExtrudeAsync(new ExtrudeDefinition(_profile.Shape, Vec3d.UnitZ, 0.025)));
        return shape.Shape.Tag.GetHashCode();
    }

    /// <summary>The operation ADR-0001 calls the weak point, so the one worth watching.</summary>
    [Benchmark(Description = "boolean subtract")]
    public async Task<int> BooleanAsync()
    {
        using KernelShapeHandle shape = await CreateAsync(
            _kernel.BooleanAsync(
                new BooleanDefinition(BooleanOperation.Subtract, _block.Shape, [_tool.Shape])));
        return shape.Shape.Tag.GetHashCode();
    }

    /// <summary>Four edges rather than one: blends get superlinear quickly.</summary>
    [Benchmark(Description = "fillet 4 edges")]
    public async Task<int> FilletAsync()
    {
        using KernelShapeHandle shape = await CreateAsync(
            _kernel.FilletAsync(
                new FilletDefinition(_block.Shape, 0.004, [.. _blockEdges.Take(4)])));
        return shape.Shape.Tag.GetHashCode();
    }

    /// <summary>What the viewport pays on every geometry change.</summary>
    [Benchmark(Description = "triangulate")]
    public async Task<int> TriangulateAsync()
        => (await _kernel.TriangulateAsync(_block.Shape, TessellationOptions.Display))
            .Value.TriangleCount;

    /// <summary>The measurement a properties panel asks for interactively.</summary>
    [Benchmark(Description = "mass properties")]
    public async Task<double> MassPropertiesAsync()
        => (await _kernel.ComputeMassPropertiesAsync(_block.Shape, 7850.0)).Value.Volume;

    /// <summary>What the geometry cache pays per body (ADR-0010).</summary>
    [Benchmark(Description = "write brep")]
    public async Task<int> WriteBRepAsync()
        => (await _kernel.WriteBRepAsync(_block.Shape)).Value.Length;

    private static async Task<KernelShapeHandle> CreateAsync(ValueTask<OperationResult> operation)
    {
        OperationResult result = await operation;

        if (!result.TryGetShape(out KernelShapeHandle shape, out _))
        {
            // A benchmark measuring a failure path is measuring nothing. Fail loudly rather than
            // recording the time it took to not do the work.
            throw new InvalidOperationException(
                $"Benchmark setup or body failed: {result.Describe()}");
        }

        return shape;
    }
}
