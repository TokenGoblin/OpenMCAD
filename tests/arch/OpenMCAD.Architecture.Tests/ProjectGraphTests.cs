using System.IO;
using System.Reflection;
using System.Xml.Linq;

namespace OpenMCAD.ArchitectureTests;

/// <summary>
/// Layering rules checked against the <i>project graph</i> rather than compiled metadata.
/// </summary>
/// <remarks>
/// This is not redundant with <see cref="LayeringTests"/>. The C# compiler omits unreferenced
/// assemblies from the emitted metadata, so a project reference that violates the layering but is
/// not yet used by any code is invisible to a metadata-based check. It becomes visible the moment
/// someone writes the first line that uses it — which is exactly too late. Checking the csproj
/// graph catches the wrong reference when it is added, which is when it is cheap to remove.
/// </remarks>
public sealed class ProjectGraphTests
{
    /// <summary>Layer index by project name, lowest first. Must match PLAN.md 4.1.</summary>
    private static readonly Dictionary<string, int> LayerIndex = new(StringComparer.Ordinal)
    {
        ["OpenMCAD.Math"] = 0,
        ["OpenMCAD.Kernel"] = 1,
        ["OpenMCAD.Solver"] = 1,
        ["OpenMCAD.Kernel.Occt"] = 2,
        ["OpenMCAD.Kernel.Fake"] = 2,
        ["OpenMCAD.Solver.Planegcs"] = 2,
        ["OpenMCAD.Core"] = 3,
        ["OpenMCAD.Modeling"] = 4,
        ["OpenMCAD.Exchange"] = 5,
        ["OpenMCAD.Render"] = 5,
        ["OpenMCAD.Api"] = 5,
        ["OpenMCAD.Interaction"] = 6,
        ["OpenMCAD.App"] = 7,
        ["OpenMCAD.ViewModels"] = 8,
        ["OpenMCAD.Shell"] = 9,
        ["OpenMCAD.Cli"] = 9,
    };

    [Fact]
    public void EverySourceProjectIsAssignedALayer()
    {
        IEnumerable<string> onDisk = ProjectCatalog.SourceProjects().Select(p => Path.GetFileNameWithoutExtension(p.Name));

        onDisk.Should().BeSubsetOf(
            LayerIndex.Keys,
            "a new src project must be placed in the layer table in PLAN.md 4.1 and here");
    }

    [Fact]
    public void NoProjectReferencePointsUpwardOrSideways()
    {
        List<string> violations = [];

        foreach (FileInfo project in ProjectCatalog.SourceProjects())
        {
            string name = Path.GetFileNameWithoutExtension(project.Name);
            if (!LayerIndex.TryGetValue(name, out int layer))
            {
                continue;
            }

            foreach (string referenced in ProjectReferencesOf(project))
            {
                if (!LayerIndex.TryGetValue(referenced, out int referencedLayer))
                {
                    violations.Add($"{name} -> {referenced} (unknown layer)");
                    continue;
                }

                if (referencedLayer >= layer)
                {
                    violations.Add(
                        $"{name} (layer {layer}) -> {referenced} (layer {referencedLayer})");
                }
            }
        }

        violations.Should().BeEmpty(
            "PLAN.md 4.1: dependencies point downward only, never sideways within a layer");
    }

    [Fact]
    public void OnlyTheShellDeclaresAWindowsTargetFramework()
    {
        List<string> violations = [];

        foreach (FileInfo project in ProjectCatalog.SourceProjects())
        {
            string name = Path.GetFileNameWithoutExtension(project.Name);
            string text = File.ReadAllText(project.FullName);

            bool declaresWindowsTfm = text.Contains("net10.0-windows", StringComparison.Ordinal);
            if (declaresWindowsTfm && name != "OpenMCAD.Shell")
            {
                violations.Add(name);
            }
        }

        violations.Should().BeEmpty(
            "ADR-0014: only OpenMCAD.Shell may target net10.0-windows");
    }

    [Fact]
    public void EveryTestProjectFollowsTheNamingConvention()
    {
        // Directory.Build.props keys IsTestProject off a ".Tests" suffix, and build.ps1 discovers
        // hosts by the same glob. A project named "FooTests" therefore compiles without xunit and
        // is skipped by the runner -- it does not fail, it silently tests nothing, which is the
        // worst failure mode a test suite can have. This caught exactly that during P1-T10.
        List<string> violations =
        [
            .. ProjectCatalog.AllProjects()
                .Select(p => Path.GetFileNameWithoutExtension(p.Name))
                .Where(name => name.EndsWith("Tests", StringComparison.Ordinal)
                    && !name.EndsWith(".Tests", StringComparison.Ordinal)),
        ];

        violations.Should().BeEmpty(
            "a test project must be named <Something>.Tests or it is silently excluded from the run");
    }

    /// <summary>
    /// Projects under <c>tests/</c> that are deliberately standalone executables rather than xunit
    /// projects, with the reason each one is.
    /// </summary>
    /// <remarks>
    /// An allow-list rather than a loosened rule. Anything not named here and not ending in
    /// <c>.Tests</c> is a project the runner silently skips, which is how a suite comes to test
    /// nothing without anyone noticing.
    /// </remarks>
    private static readonly Dictionary<string, string> StandaloneRunners = new(StringComparer.Ordinal)
    {
        ["OpenMCAD.Regression"] =
            "The corpus runner needs to replay every fixture twice on fresh kernels for the "
            + "determinism gate, and to select a kernel at run time. Neither fits a test host, so "
            + "it is an executable that build.ps1 and the nightly workflow invoke directly.",

        ["OpenMCAD.Kernel.Benchmarks"] =
            "BenchmarkDotNet is its own host: it spawns a process per benchmark, needs Release "
            + "and a quiet machine, and takes minutes. Running it inside the test suite would "
            + "make every build slow and every measurement worthless.",
    };

    /// <summary>
    /// Where fixture projects live: assemblies that exist to be consumed by a test rather than to
    /// contain one.
    /// </summary>
    /// <remarks>
    /// A plugin loader cannot be tested against a plugin declared in the test assembly — it would
    /// share the host's types by construction and prove nothing about isolation — so the fixture
    /// has to be a separately compiled assembly. That is a third category, neither a test project
    /// nor a standalone runner, and it gets a directory rather than an allow-list entry because
    /// more of them are expected.
    /// </remarks>
    private const string FixtureDirectory = "fixtures";

    [Fact]
    public void EveryProjectUnderTestsIsATestProjectAFixtureOrAKnownRunner()
    {
        List<string> violations =
        [
            .. new DirectoryInfo(Path.Combine(ProjectCatalog.RepoRoot.FullName, "tests"))
                .GetFiles("*.csproj", SearchOption.AllDirectories)
                .Where(p => !IsFixture(p))
                .Select(p => Path.GetFileNameWithoutExtension(p.Name))
                .Where(name => !name.EndsWith(".Tests", StringComparison.Ordinal))
                .Where(name => !StandaloneRunners.ContainsKey(name)),
        ];

        violations.Should().BeEmpty(
            "a project under tests/ that is not named *.Tests, not under tests/fixtures, and not a "
            + "documented standalone runner is silently skipped by the test runner");
    }

    [Fact]
    public void EveryFixtureIsUsedByATestProject()
    {
        // Otherwise tests/fixtures becomes a place unused projects can sit unnoticed, which is the
        // hole the rule above exists to close rather than to move.
        DirectoryInfo tests = new(Path.Combine(ProjectCatalog.RepoRoot.FullName, "tests"));

        string[] references =
        [
            .. tests.GetFiles("*.csproj", SearchOption.AllDirectories)
                .Where(p => !IsFixture(p))
                .SelectMany(p => File.ReadAllLines(p.FullName)),
        ];

        List<string> unused =
        [
            .. tests.GetFiles("*.csproj", SearchOption.AllDirectories)
                .Where(IsFixture)
                .Select(p => Path.GetFileName(p.Name))
                .Where(fixture => !references.Any(
                    line => line.Contains(fixture, StringComparison.OrdinalIgnoreCase))),
        ];

        unused.Should().BeEmpty("a fixture nothing references is dead weight");
    }

    private static bool IsFixture(FileInfo project)
        => project.Directory?.Parent?.Name.Equals(FixtureDirectory, StringComparison.Ordinal) == true;

    [Fact]
    public void EveryDocumentedStandaloneRunnerStillExists()
    {
        // The allow-list must not outlive its entries, or it quietly becomes a hole in the rule.
        List<string> present =
        [
            .. new DirectoryInfo(Path.Combine(ProjectCatalog.RepoRoot.FullName, "tests"))
                .GetFiles("*.csproj", SearchOption.AllDirectories)
                .Select(p => Path.GetFileNameWithoutExtension(p.Name)),
        ];

        StandaloneRunners.Keys.Should().BeSubsetOf(present);
    }

    [Fact]
    public void NoProjectPinsItsOwnPackageVersions()
    {
        // P0-T03: central package management is the single source of truth. A Version attribute
        // on a PackageReference silently escapes it.
        List<string> violations = [];

        foreach (FileInfo project in ProjectCatalog.AllProjects())
        {
            XDocument document = XDocument.Load(project.FullName);

            IEnumerable<XElement> pinned = document
                .Descendants("PackageReference")
                .Where(element => element.Attribute("Version") is not null);

            foreach (XElement element in pinned)
            {
                violations.Add(
                    $"{project.Name}: {element.Attribute("Include")?.Value ?? "<unnamed>"}");
            }
        }

        violations.Should().BeEmpty(
            "package versions belong in Directory.Packages.props (P0-T03)");
    }

    private static IEnumerable<string> ProjectReferencesOf(FileInfo project)
        => XDocument.Load(project.FullName)
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', '/')));
}
