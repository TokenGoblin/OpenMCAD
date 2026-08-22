using System.IO;
using System.Reflection;
using System.Xml.Linq;

namespace OpenMCAD.ArchitectureTests;

/// <summary>
/// Resolves the repository layout and loads the built assemblies for inspection.
/// </summary>
/// <remarks>
/// Assemblies are located by file path rather than by <see cref="Assembly.Load(AssemblyName)"/>,
/// because a project name is not an assembly name: <c>OpenMCAD.Shell</c> produces
/// <c>OpenMCAD.dll</c> and <c>OpenMCAD.Cli</c> produces <c>omcad.dll</c>. The mapping is read from
/// the project files themselves so it cannot drift out of sync with a rename.
/// </remarks>
internal static class ProjectCatalog
{
    private static readonly Lazy<DirectoryInfo> LazyRepoRoot = new(ResolveRepoRoot);

    private static readonly Lazy<IReadOnlyDictionary<string, string>> LazyAssemblyNames =
        new(ResolveAssemblyNames);

    private static readonly Dictionary<string, Assembly> LoadedAssemblies =
        new(StringComparer.Ordinal);

    /// <summary>Gets the repository root, stamped into this assembly at build time.</summary>
    public static DirectoryInfo RepoRoot => LazyRepoRoot.Value;

    /// <summary>Gets the assembly simple name produced by each src project.</summary>
    public static IReadOnlyDictionary<string, string> AssemblyNamesByProject
        => LazyAssemblyNames.Value;

    /// <summary>Gets every project file under <c>src</c>, ordered stably.</summary>
    public static IReadOnlyList<FileInfo> SourceProjects()
        => [.. new DirectoryInfo(Path.Combine(RepoRoot.FullName, "src"))
            .GetFiles("*.csproj", SearchOption.AllDirectories)
            .OrderBy(file => file.Name, StringComparer.Ordinal)];

    /// <summary>Gets every project file under <c>src</c> and <c>tests</c>, ordered stably.</summary>
    public static IReadOnlyList<FileInfo> AllProjects()
        => [.. RepoRoot
            .GetDirectories()
            .Where(directory => directory.Name is "src" or "tests")
            .SelectMany(directory => directory.GetFiles("*.csproj", SearchOption.AllDirectories))
            .OrderBy(file => file.FullName, StringComparer.Ordinal)];

    /// <summary>Loads the assembly produced by the named src project.</summary>
    /// <param name="projectName">The project name, for example <c>OpenMCAD.Core</c>.</param>
    /// <exception cref="FileNotFoundException">The built assembly is not beside the test host.</exception>
    public static Assembly Load(string projectName)
    {
        lock (LoadedAssemblies)
        {
            if (LoadedAssemblies.TryGetValue(projectName, out Assembly? cached))
            {
                return cached;
            }

            string assemblyName = AssemblyNamesByProject.TryGetValue(projectName, out string? mapped)
                ? mapped
                : projectName;

            // Everything referenced by this test project is copied next to the test host, so the
            // host directory is the one place all of them are guaranteed to exist together.
            string hostDirectory = Path.GetDirectoryName(typeof(ProjectCatalog).Assembly.Location)!;
            string path = Path.Combine(hostDirectory, assemblyName + ".dll");

            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"Project '{projectName}' should produce '{assemblyName}.dll' beside the test "
                    + "host, but it is missing. Is it referenced by OpenMCAD.Architecture.Tests?",
                    path);
            }

            Assembly assembly = Assembly.LoadFrom(path);
            LoadedAssemblies[projectName] = assembly;
            return assembly;
        }
    }

    private static DirectoryInfo ResolveRepoRoot()
    {
        string? root = typeof(ProjectCatalog).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "RepoRoot")
            ?.Value;

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            throw new InvalidOperationException(
                "The RepoRoot assembly metadata is missing or does not point at an existing "
                + "directory. OpenMCAD.Architecture.Tests.csproj is misconfigured.");
        }

        return new DirectoryInfo(root);
    }

    private static Dictionary<string, string> ResolveAssemblyNames()
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);

        foreach (FileInfo project in SourceProjects())
        {
            string projectName = Path.GetFileNameWithoutExtension(project.Name);

            string? assemblyName = XDocument.Load(project.FullName)
                .Descendants("AssemblyName")
                .Select(element => element.Value.Trim())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

            map[projectName] = assemblyName ?? projectName;
        }

        return map;
    }
}
