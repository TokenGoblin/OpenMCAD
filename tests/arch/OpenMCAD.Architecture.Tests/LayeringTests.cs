using System.Reflection;
using NetArchTest.Rules;

// Xunit also defines TestResult; alias to keep the architecture-rule type unambiguous.
using ArchTestResult = NetArchTest.Rules.TestResult;

namespace OpenMCAD.ArchitectureTests;

/// <summary>
/// P0-T05 — the layering rules from PLAN.md 4.1, enforced mechanically rather than by discipline.
/// </summary>
/// <remarks>
/// PLAN.md 12 lists things that are "always wrong, no matter how convenient". Four of them are
/// checkable here, and so they are checked here. A rule that lives only in a document is a rule
/// that erodes.
/// </remarks>
public sealed class LayeringTests
{
    /// <summary>
    /// The declared layer order, lowest first. A project may reference anything strictly below
    /// it and nothing at or above it.
    /// </summary>
    private static readonly string[][] Layers =
    [
        ["OpenMCAD.Math"],
        ["OpenMCAD.Kernel", "OpenMCAD.Solver"],
        ["OpenMCAD.Kernel.Occt", "OpenMCAD.Kernel.Fake", "OpenMCAD.Solver.Planegcs"],
        ["OpenMCAD.Core"],
        ["OpenMCAD.Modeling"],
        ["OpenMCAD.Exchange", "OpenMCAD.Render", "OpenMCAD.Api"],
        ["OpenMCAD.Interaction"],
        ["OpenMCAD.App"],
        ["OpenMCAD.ViewModels"],
        ["OpenMCAD.Shell", "OpenMCAD.Cli"],
    ];

    public static TheoryData<string> AllAssemblyNames()
    {
        TheoryData<string> data = [];
        foreach (string[] layer in Layers)
        {
            foreach (string name in layer)
            {
                data.Add(name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllAssemblyNames))]
    public void EveryProjectAssemblyLoads(string assemblyName)
    {
        // If this fails, every other rule in this file is vacuously passing. Guard it explicitly.
        AssemblyFor(assemblyName).Should().NotBeNull();
    }

    [Fact]
    public void DependenciesPointDownwardOnly()
    {
        List<string> violations = [];

        for (int i = 0; i < Layers.Length; i++)
        {
            // Everything at this layer or above is forbidden, minus the assembly itself.
            HashSet<string> forbidden = AssemblyNamesOf(Layers.Skip(i).SelectMany(layer => layer));

            foreach (string name in Layers[i])
            {
                string self = ProjectCatalog.AssemblyNamesByProject.TryGetValue(name, out string? a)
                    ? a
                    : name;

                forbidden.Remove(self);
                Assembly assembly = AssemblyFor(name);

                foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
                {
                    if (reference.Name is not null && forbidden.Contains(reference.Name))
                    {
                        violations.Add($"{name} -> {reference.Name}");
                    }
                }

                forbidden.Add(self);
            }
        }

        violations.Should().BeEmpty(
            "PLAN.md 4.1 requires dependencies to point downward only");
    }

    [Fact]
    public void CoreDoesNotDependOnAnyKernelImplementation()
    {
        // ADR-0002: Core is testable against FakeKernel because it knows only the interfaces.
        // Depending on a concrete kernel would silently destroy that property.
        HashSet<string> implementations = AssemblyNamesOf(
        [
            "OpenMCAD.Kernel.Occt",
            "OpenMCAD.Kernel.Fake",
            "OpenMCAD.Solver.Planegcs",
        ]);

        foreach (string layer in new[] { "OpenMCAD.Core", "OpenMCAD.Modeling" })
        {
            IEnumerable<string?> references = AssemblyFor(layer)
                .GetReferencedAssemblies()
                .Select(reference => reference.Name);

            references.Should().NotIntersectWith(
                implementations,
                $"{layer} must depend on kernel and solver abstractions only (ADR-0002)");
        }
    }

    [Fact]
    public void ModelingDoesNotDependOnAnyUserInterfaceLayer()
    {
        // PLAN.md 4.1: a feature must be creatable, rebuildable, serialisable and validatable
        // headlessly, or none of it is CI-testable.
        HashSet<string> userInterface = AssemblyNamesOf(
        [
            "OpenMCAD.Render",
            "OpenMCAD.Interaction",
            "OpenMCAD.ViewModels",
            "OpenMCAD.Shell",
        ]);

        AssemblyFor("OpenMCAD.Modeling")
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Should()
            .NotIntersectWith(userInterface);
    }

    [Fact]
    public void OnlyTheShellTargetsWindows()
    {
        // ADR-0014: keeping the Windows TFM confined to the shell is what makes ADR-0007's
        // portability insurance real rather than notional.
        string[] windowsAssemblies = ["WindowsBase", "PresentationCore", "PresentationFramework"];

        foreach (string[] layer in Layers)
        {
            foreach (string name in layer)
            {
                if (name == "OpenMCAD.Shell")
                {
                    continue;
                }

                AssemblyFor(name)
                    .GetReferencedAssemblies()
                    .Select(reference => reference.Name)
                    .Should()
                    .NotIntersectWith(
                        windowsAssemblies,
                        $"{name} must not reference WPF; only OpenMCAD.Shell may (ADR-0014)");
            }
        }
    }

    internal static Assembly AssemblyFor(string projectName) => ProjectCatalog.Load(projectName);

    /// <summary>
    /// Translates project names to the assembly names they actually produce, so reference checks
    /// compare like with like.
    /// </summary>
    internal static HashSet<string> AssemblyNamesOf(IEnumerable<string> projectNames)
        => projectNames
            .Select(name => ProjectCatalog.AssemblyNamesByProject.TryGetValue(name, out string? a)
                ? a
                : name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Type-level rules, which need IL inspection rather than assembly references.
/// </summary>
public sealed class TypeDependencyTests
{
    [Fact]
    public void NoWpfTypeAppearsInViewModels()
    {
        // ADR-0007's hard rule. Enforced by a test, not by discipline, exactly as the ADR says.
        ArchTestResult result = Types.InAssembly(LayeringTests.AssemblyFor("OpenMCAD.ViewModels"))
            .ShouldNot()
            .HaveDependencyOnAny("System.Windows", "System.Windows.Forms", "PresentationFramework")
            .GetResult();

        FailingTypeNames(result).Should().BeEmpty(
            "no System.Windows.* type may appear in OpenMCAD.ViewModels (ADR-0007)");
    }

    [Fact]
    public void NoOcctTypeAppearsOutsideTheOcctBindingAssembly()
    {
        // ADR-0002: callers above the kernel layer never touch a TopoDS_Face. The native binding
        // namespace is the fence; this test is the fence post.
        const string OcctNamespace = "OpenMCAD.Kernel.Occt";

        string[] mustNotSee =
        [
            "OpenMCAD.Math", "OpenMCAD.Kernel", "OpenMCAD.Solver", "OpenMCAD.Solver.Planegcs",
            "OpenMCAD.Kernel.Fake", "OpenMCAD.Core", "OpenMCAD.Modeling", "OpenMCAD.Exchange",
            "OpenMCAD.Render", "OpenMCAD.Interaction", "OpenMCAD.Api", "OpenMCAD.App",
            "OpenMCAD.ViewModels", "OpenMCAD.Shell",
        ];

        foreach (string assemblyName in mustNotSee)
        {
            ArchTestResult result = Types.InAssembly(LayeringTests.AssemblyFor(assemblyName))
                .ShouldNot()
                .HaveDependencyOn(OcctNamespace)
                .GetResult();

            FailingTypeNames(result).Should().BeEmpty(
                $"{assemblyName} must not reference {OcctNamespace} (ADR-0002)");
        }
    }

    private static IEnumerable<string> FailingTypeNames(ArchTestResult result)
        => result.FailingTypeNames ?? [];
}
