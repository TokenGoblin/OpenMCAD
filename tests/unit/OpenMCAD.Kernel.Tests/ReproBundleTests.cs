using System.Collections.Immutable;
using System.Text.Json;
using OpenMCAD.Kernel;
using OpenMCAD.Kernel.Diagnostics;
using OpenMCAD.Kernel.Operations;

namespace OpenMCAD.KernelTests;

public sealed class ReproBundleTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "openmcad-repro-tests", Guid.NewGuid().ToString("N"));

    private static readonly KernelCapabilities Capabilities = new("test", "1.0", true, false);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private ReproBundleWriter Writer(int maxBundles = 50)
        => new(ReproBundleOptions.On(_directory) with { MaxBundles = maxBundles }, Capabilities);

    private static ImmutableArray<KernelDiagnostic> Failure(string code = KernelDiagnosticCodes.BlendFailed)
        => [KernelDiagnostic.Error(code, "the blend could not be applied", kernelDetail: "raw kernel text")];

    [Fact]
    public void DisabledCapture_WritesNothing()
    {
        ReproBundleWriter writer = new(default, Capabilities);

        string? path = writer.Capture(
            "Fillet", new BoxDefinition(1, 1, 1), Failure(), default, _ => []);

        path.Should().BeNull();
        writer.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Capture_WritesAManifestDescribingTheFailure()
    {
        string? path = Writer().Capture(
            "Fillet",
            new BoxDefinition(0.1, 0.2, 0.3),
            Failure(),
            new KernelRequest(LinearTolerance: 1e-6, CorrelationId: "Feature-7"),
            _ => []);

        path.Should().NotBeNull();

        string manifestPath = Path.Combine(path!, "manifest.json");
        File.Exists(manifestPath).Should().BeTrue();

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement root = manifest.RootElement;

        root.GetProperty("Operation").GetString().Should().Be("Fillet");
        root.GetProperty("Kernel").GetString().Should().Be("test");
        root.GetProperty("Tolerance").GetDouble().Should().Be(1e-6);
        root.GetProperty("CorrelationId").GetString().Should().Be("Feature-7");

        // The parameters must be there, or the bundle describes a failure nobody can reproduce.
        root.GetProperty("Definition").GetString().Should().Contain("0.1");

        JsonElement diagnostics = root.GetProperty("Diagnostics");
        diagnostics.GetArrayLength().Should().Be(1);
        diagnostics[0].GetProperty("Code").GetString().Should().Be(KernelDiagnosticCodes.BlendFailed);

        // The raw kernel text belongs in the bundle even though it never reaches a user.
        diagnostics[0].GetProperty("KernelDetail").GetString().Should().Be("raw kernel text");
    }

    [Fact]
    public void Capture_WritesTheInputGeometry()
    {
        byte[] geometry = [1, 2, 3, 4, 5];

        string? path = Writer().Capture(
            "Fillet",
            new FilletDefinition(new KernelShape(42), 0.01),
            Failure(),
            default,
            _ => [.. geometry]);

        path.Should().NotBeNull();

        string[] inputs = Directory.GetFiles(Path.Combine(path!, "inputs"));
        inputs.Should().ContainSingle();

        // Sequence-numbered, not tag-numbered: tags differ between runs, so a tag-named file would
        // make two captures of the same failure look like different failures.
        Path.GetFileName(inputs[0]).Should().Be("00.brep");
        File.ReadAllBytes(inputs[0]).Should().Equal(geometry);
    }

    [Fact]
    public void TheSameFailureTwice_ProducesOneBundleAndCountsTheRecurrence()
    {
        // A rebuild loop can fail identically hundreds of times. Two hundred near-identical
        // bundles would bury the one that matters and fill the disk doing it.
        ReproBundleWriter writer = Writer();
        BoxDefinition definition = new(1, 1, 1);

        string? first = writer.Capture("Fillet", definition, Failure(), default, _ => []);
        string? second = writer.Capture("Fillet", definition, Failure(), default, _ => []);
        string? third = writer.Capture("Fillet", definition, Failure(), default, _ => []);

        second.Should().Be(first);
        third.Should().Be(first);
        Directory.GetDirectories(_directory).Should().ContainSingle();

        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(first!, "manifest.json")));

        manifest.RootElement.GetProperty("Recurrences").GetInt32().Should().Be(3);
    }

    [Fact]
    public void DifferentFailures_ProduceDifferentBundles()
    {
        ReproBundleWriter writer = Writer();

        writer.Capture("Fillet", new BoxDefinition(1, 1, 1), Failure(), default, _ => []);
        writer.Capture("Fillet", new BoxDefinition(2, 2, 2), Failure(), default, _ => []);
        writer.Capture("Chamfer", new BoxDefinition(1, 1, 1), Failure(), default, _ => []);

        Directory.GetDirectories(_directory).Should().HaveCount(3);
    }

    [Fact]
    public void TheBundleLimitIsRespected()
    {
        ReproBundleWriter writer = Writer(maxBundles: 2);

        for (int i = 0; i < 10; i++)
        {
            writer.Capture("Fillet", new BoxDefinition(i + 1, 1, 1), Failure(), default, _ => []);
        }

        // Refusing rather than evicting: the first failure is usually the interesting one.
        Directory.GetDirectories(_directory).Should().HaveCount(2);
    }

    [Fact]
    public void CaptureNeverThrows_EvenWhenTheDirectoryIsUnusable()
    {
        // A failure to record a failure must not become a second, more confusing failure.
        ReproBundleWriter writer = new(
            ReproBundleOptions.On("\0invalid\0path"), Capabilities);

        Func<string?> act = () => writer.Capture(
            "Fillet", new BoxDefinition(1, 1, 1), Failure(), default, _ => []);

        act.Should().NotThrow();
        act().Should().BeNull();
    }

    [Fact]
    public void CaptureNeverThrows_WhenWritingGeometryFails()
    {
        Func<string?> act = () => Writer().Capture(
            "Fillet",
            new FilletDefinition(new KernelShape(1), 0.01),
            Failure(),
            default,
            _ => throw new InvalidOperationException("the shape is gone"));

        act.Should().NotThrow();
    }

    [Fact]
    public void TheBundleExplainsHowToTurnItIntoAFixture()
    {
        // The whole value of a bundle is that path being short. If the README ever stops saying
        // how, the bundles become archaeology.
        string? path = Writer().Capture(
            "Fillet", new BoxDefinition(1, 1, 1), Failure(), default, _ => []);

        string readme = File.ReadAllText(Path.Combine(path!, "README.md"));

        readme.Should().Contain("tests/regression/corpus/pathological/");
        readme.Should().Contain("every bug fix ships with a corpus fixture");
    }
}

public sealed class DefinitionInputShapeTests
{
    [Fact]
    public void PrimitivesReadNoShapes()
    {
        // InputShapes has a default implementation, so it is reached through the interface.
        ((IOperationDefinition)new BoxDefinition(1, 1, 1)).InputShapes().Should().BeEmpty();
        ((IOperationDefinition)new SphereDefinition(1)).InputShapes().Should().BeEmpty();
    }

    [Fact]
    public void OperationsReportEveryShapeTheyRead()
    {
        KernelShape profile = new(10);
        KernelShape target = new(20);
        KernelShape tool = new(30);

        new ExtrudeDefinition(profile, OpenMCAD.Math.Vec3d.UnitZ, 1).InputShapes()
            .Should().Equal(profile);

        new FilletDefinition(target, 0.1).InputShapes().Should().Equal(target);

        new BooleanDefinition(BooleanOperation.Subtract, target, tool).InputShapes()
            .Should().Equal(target, tool);
    }
}
