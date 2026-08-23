using System.Collections.Immutable;
using System.Text.Json;
using OpenMCAD.Kernel;
using OpenMCAD.Kernel.Diagnostics;
using OpenMCAD.Kernel.Operations;
using OpenMCAD.Math;

namespace OpenMCAD.KernelTests;

/// <summary>
/// Regression tests for the repro-bundle defects found in code review.
/// </summary>
/// <remarks>
/// Every test here corresponds to a specific way the bundle machinery was wrong. The originals
/// passed because they exercised only <c>BoxDefinition</c>, which has no shape inputs and no
/// collection members — the two things that broke.
/// </remarks>
public sealed class ReproBundleRegressionTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "openmcad-repro-regression", Guid.NewGuid().ToString("N"));

    private static readonly KernelCapabilities Capabilities = new("test", "1.0", true, false);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private ReproBundleWriter Writer(ReproBundleOptions? options = null)
        => new(options ?? ReproBundleOptions.On(_directory), Capabilities);

    private static ImmutableArray<KernelDiagnostic> Failure()
        => [KernelDiagnostic.Error(KernelDiagnosticCodes.BlendFailed, "could not be applied")];

    private static FilletDefinition Fillet(ulong bodyTag, double radius, params ulong[] edgeTags)
    {
        KernelShape body = new(bodyTag);
        ImmutableArray<FilletEdge> edges =
        [
            .. edgeTags.Select(t => new FilletEdge(new SubEntity(body, t, SubEntityKind.Edge), radius)),
        ];

        return new FilletDefinition(body, edges);
    }

    [Fact]
    public void TheSameFailureAfterARebuild_StillProducesOneBundle()
    {
        // Finding 1. Shape tags are slot indices with a generation counter, so a rebuild produces
        // the same body under a different tag. Fingerprinting the tag meant the identical failure
        // hashed differently every rebuild, and the deduplication this class exists for did not
        // work for any operation that consumes a shape -- fillet, chamfer, boolean, extrude,
        // revolve, which is to say all the ones that actually fail.
        ReproBundleWriter writer = Writer();

        string? before = writer.Capture(
            "Fillet", Fillet(0x100, 0.01, 0x101, 0x102), Failure(), default, _ => []);

        string? afterRebuild = writer.Capture(
            "Fillet", Fillet(0x200, 0.01, 0x201, 0x202), Failure(), default, _ => []);

        afterRebuild.Should().Be(before);
        Directory.GetDirectories(_directory).Should().ContainSingle();
    }

    [Fact]
    public void DifferentRadii_ProduceDifferentBundles()
    {
        // Finding 2. ImmutableArray renders as its type name under the compiler-generated
        // ToString, so a fillet at 10 mm and one at 50 mm fingerprinted identically and the second
        // failure was silently discarded as a duplicate of the first.
        ReproBundleWriter writer = Writer();

        writer.Capture("Fillet", Fillet(0x100, 0.01, 0x101), Failure(), default, _ => []);
        writer.Capture("Fillet", Fillet(0x100, 0.05, 0x101), Failure(), default, _ => []);

        Directory.GetDirectories(_directory).Should().HaveCount(2);
    }

    [Fact]
    public void DifferentEdgeSelections_ProduceDifferentBundles()
    {
        ReproBundleWriter writer = Writer();

        writer.Capture("Fillet", Fillet(0x100, 0.01, 0x101), Failure(), default, _ => []);
        writer.Capture("Fillet", Fillet(0x100, 0.01, 0x101, 0x102), Failure(), default, _ => []);

        Directory.GetDirectories(_directory).Should().HaveCount(2);
    }

    [Fact]
    public void TheManifestRecordsTheParameters()
    {
        // PLAN.md 6.1 requires "the operation and parameters". A manifest naming neither the radius
        // nor the edges describes a failure nobody can reproduce.
        string? path = Writer().Capture(
            "Fillet", Fillet(0x100, 0.0125, 0x101, 0x102), Failure(), default, _ => []);

        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(path!, "manifest.json")));

        string description = manifest.RootElement.GetProperty("Definition").GetString()!;

        description.Should().Contain("0.0125", "the radius is the parameter that matters");
        description.Should().Contain("Edge", "the edge selection must be recorded");
        description.Should().NotContain(
            "ImmutableArray", "a collection must be expanded, not named by its type");
    }

    [Fact]
    public void WhenWritingGeometryFails_TheBundleIsStillTracked()
    {
        // Finding 3. The bundle used to be recorded only after the geometry loop, so a throwing
        // callback left a directory on disk that the writer did not know about: the limit never
        // engaged and every recurrence rewrote the manifest with a count of one.
        ReproBundleWriter writer = Writer();

        for (int i = 0; i < 5; i++)
        {
            writer.Capture(
                "Fillet",
                Fillet(0x100, 0.01, 0x101),
                Failure(),
                default,
                _ => throw new InvalidOperationException("the shape is gone"));
        }

        writer.Written.Should().ContainSingle();
        Directory.GetDirectories(_directory).Should().ContainSingle();

        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(writer.Written.First(), "manifest.json")));

        manifest.RootElement.GetProperty("Recurrences").GetInt32().Should().Be(5);
    }

    [Fact]
    public void GeometryFailureCostsTheGeometryAndNotTheBundle()
    {
        string? path = Writer().Capture(
            "Fillet",
            Fillet(0x100, 0.01, 0x101),
            Failure(),
            default,
            _ => throw new InvalidOperationException("the shape is gone"));

        path.Should().NotBeNull();
        File.Exists(Path.Combine(path!, "manifest.json")).Should().BeTrue();
        File.Exists(Path.Combine(path!, "README.md")).Should().BeTrue();
    }

    [Fact]
    public void ObjectInitialiserConstruction_BehavesLikeTheFactory()
    {
        // Finding 4. A record struct reaches its primary-constructor defaults only through the
        // constructor, so this -- which reads exactly like the documented configuration -- used to
        // produce MaxBundles = 0 and no geometry, and refused to capture anything at all.
        ReproBundleOptions options = new() { Enabled = true, Directory = _directory };

        options.EffectiveMaxBundles.Should().Be(ReproBundleOptions.DefaultMaxBundles);
        options.IncludeInputGeometry.Should().BeTrue();

        string? path = new ReproBundleWriter(options, Capabilities).Capture(
            "Fillet", Fillet(0x100, 0.01, 0x101), Failure(), default, _ => [1, 2, 3]);

        path.Should().NotBeNull();
        Directory.GetFiles(Path.Combine(path!, "inputs")).Should().ContainSingle();
    }

    [Fact]
    public void DefaultOptions_AreDisabledRatherThanBroken()
    {
        ReproBundleOptions options = default;

        options.Enabled.Should().BeFalse();
        options.EffectiveMaxBundles.Should().Be(ReproBundleOptions.DefaultMaxBundles);
        options.IncludeInputGeometry.Should().BeTrue();
    }
}

/// <summary>Tests the rendering the fingerprint and the manifest are built from.</summary>
public sealed class DefinitionDescriptorTests
{
    [Fact]
    public void HandlesAreAnonymisedForTheFingerprintAndKeptForTheManifest()
    {
        FilletDefinition definition = new(
            new KernelShape(0xABC),
            0.01,
            new SubEntity(new KernelShape(0xABC), 0xDEF, SubEntityKind.Edge));

        string manifest = Describe(definition, forManifest: true);
        string fingerprint = Describe(definition, forManifest: false);

        manifest.Should().Contain("ABC");
        fingerprint.Should().NotContain("ABC");
        fingerprint.Should().Contain("#0", "handles become positional slots");
    }

    [Fact]
    public void RepeatedHandlesKeepTheirIdentityInTheFingerprint()
    {
        // "Fillet this edge twice" and "fillet two different edges" must not collapse together.
        KernelShape body = new(1);
        SubEntity edge = new(body, 2, SubEntityKind.Edge);
        SubEntity other = new(body, 3, SubEntityKind.Edge);

        string sameTwice = Describe(
            new FilletDefinition(body, [new FilletEdge(edge, 0.01), new FilletEdge(edge, 0.01)]),
            forManifest: false);

        string twoDistinct = Describe(
            new FilletDefinition(body, [new FilletEdge(edge, 0.01), new FilletEdge(other, 0.01)]),
            forManifest: false);

        sameTwice.Should().NotBe(twoDistinct);
    }

    [Fact]
    public void NestedValuesAreExpanded()
    {
        BoxDefinition definition = new(
            0.1, 0.2, 0.3, Transform.FromTranslation(new Vec3d(1, 2, 3)));

        string text = Describe(definition, forManifest: true);

        text.Should().Contain("0.1");
        text.Should().Contain("Translation");
        text.Should().NotContain("OpenMCAD.Math.Transform");
    }

    [Fact]
    public void ANullDefinitionIsHandled()
    {
        Describe(null, forManifest: true).Should().Be("(none)");
        Describe(null, forManifest: false).Should().Be("(none)");
    }

    private static string Describe(IOperationDefinition? definition, bool forManifest)
        => forManifest
            ? DefinitionDescriptor.ForManifest(definition)
            : DefinitionDescriptor.ForFingerprint(definition);
}

/// <summary>
/// The deletion invariant, which code review found was too strict.
/// </summary>
public sealed class HistoryMapDeletionTests
{
    private static readonly KernelShape Body = new(1);

    private static SubEntity Entity(ulong tag, SubEntityKind kind)
        => new(Body, tag, kind);

    [Fact]
    public void AnEntityMayBeBothDeletedAndGenerating()
    {
        // The ordinary fillet, and what the OCCT spike measured: Generated(edge) yields the blend
        // face and IsDeleted(edge) is true. The builder used to reject this, which made the single
        // most common blend impossible to record faithfully.
        SubEntity edge = Entity(10, SubEntityKind.Edge);
        SubEntity blend = Entity(20, SubEntityKind.Face);

        HistoryMapBuilder builder = new();
        builder.AddGenerated(edge, blend, OperationRole.BlendFace);
        builder.AddDeleted(edge);

        HistoryMap map = builder.Build();

        map.IsDeleted(edge).Should().BeTrue();
        map.Generated(edge).Should().ContainSingle().Which.Should().Be(blend);
        map.RoleOf(blend).Should().Be(OperationRole.BlendFace);
        map.SourceOf(blend).Should().Be(edge);
    }

    [Fact]
    public void AnEntityMayNotBeBothDeletedAndModified()
    {
        // Modified asserts the entity survived in altered form. It cannot also be gone.
        SubEntity face = Entity(10, SubEntityKind.Face);
        SubEntity successor = Entity(20, SubEntityKind.Face);

        HistoryMapBuilder builder = new();
        builder.AddModified(face, successor, OperationRole.Trimmed);
        builder.AddDeleted(face);

        Action act = () => builder.Build();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*deleted but also has a modified successor*");
    }

    [Fact]
    public void RetainedCountsAsModifiedForTheInvariant()
    {
        SubEntity face = Entity(10, SubEntityKind.Face);

        HistoryMapBuilder builder = new();
        builder.AddRetained(face, Entity(20, SubEntityKind.Face));
        builder.AddDeleted(face);

        Action act = () => builder.Build();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void BothBlendRelationshipsCanBeRecordedTogether()
    {
        // What P1-T06 will actually write: the blend lies between two faces (which is what
        // survives a rebuild) and descends from the consumed edge (which is what SourceOf answers).
        SubEntity edge = Entity(10, SubEntityKind.Edge);
        SubEntity faceA = Entity(11, SubEntityKind.Face);
        SubEntity faceB = Entity(12, SubEntityKind.Face);
        SubEntity blend = Entity(20, SubEntityKind.Face);

        HistoryMapBuilder builder = new();
        builder.AddNewBetween(blend, OperationRole.BlendFace, faceA, faceB);
        builder.AddGenerated(edge, blend, OperationRole.BlendFace);
        builder.AddDeleted(edge);

        HistoryMap map = builder.Build();

        map.Generated(faceA).Should().Contain(blend);
        map.Generated(faceB).Should().Contain(blend);
        map.Generated(edge).Should().Contain(blend);
        map.IsDeleted(edge).Should().BeTrue();
        map.NewEntities.Should().Contain(blend);
    }
}
