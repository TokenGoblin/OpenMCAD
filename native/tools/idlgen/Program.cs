using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OpenMCAD.IdlGen;

/// <summary>
/// Turns <c>native/kernel.api.json</c> into the C header, the dispatch layer, the implementation
/// signatures, the not-implemented fallbacks, and the C# bindings.
/// </summary>
/// <remarks>
/// <para>
/// P1-T03. ADR-0003 puts it plainly: generating this surface rather than hand-writing it "is the
/// difference between a maintainable 300-operation surface and a clerical nightmare". Adding an
/// operation should be one IDL entry and one C++ body, not five edits kept in sync by hand.
/// </para>
/// <para>
/// Outputs are checked in rather than generated during the build, for two reasons. CMake would
/// otherwise need the .NET SDK to build the native shim, which is a dependency nobody wants in a
/// C++ toolchain. And a generated diff is reviewable: when the IDL changes, the pull request shows
/// exactly what happened to the ABI, which for a compatibility surface is worth a great deal.
/// </para>
/// <para>
/// <c>--check</c> regenerates in memory and compares. CI runs it, so checked-in output cannot drift
/// from the IDL that produced it.
/// </para>
/// </remarks>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static int Main(string[] args)
    {
        // Generated code must not depend on the machine that generated it. Number formatting is
        // the usual way that leaks in, so the whole process runs invariant.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

        bool check = args.Contains("--check", StringComparer.Ordinal);
        string? rootArgument = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));

        try
        {
            DirectoryInfo root = ResolveRoot(rootArgument);
            string idlPath = Path.Combine(root.FullName, "native", "kernel.api.json");

            if (!File.Exists(idlPath))
            {
                Console.Error.WriteLine($"idlgen: cannot find the IDL at {idlPath}");
                return 2;
            }

            ApiDocument document = Load(idlPath);
            Validate(document);

            Dictionary<string, string> outputs = new(StringComparer.Ordinal)
            {
                [Path.Combine("native", "openmcad_occt", "include", "openmcad_occt.g.h")] =
                    CHeaderEmitter.Emit(document),
                [Path.Combine("native", "openmcad_occt", "include", "openmcad_ops.g.h")] =
                    OpsHeaderEmitter.Emit(document),
                [Path.Combine("native", "openmcad_occt", "src", "openmcad_dispatch.g.cpp")] =
                    DispatchEmitter.Emit(document),
                [Path.Combine("native", "openmcad_occt", "src", "openmcad_stubs.g.cpp")] =
                    StubEmitter.Emit(document),
                [Path.Combine("src", "OpenMCAD.Kernel.Occt", "Interop", "OcctBindings.g.cs")] =
                    CSharpEmitter.Emit(document),
            };

            int stale = 0;
            foreach ((string relative, string content) in outputs.OrderBy(o => o.Key, StringComparer.Ordinal))
            {
                string path = Path.Combine(root.FullName, relative);
                string normalised = Normalise(content);

                if (check)
                {
                    string existing = File.Exists(path) ? Normalise(File.ReadAllText(path)) : string.Empty;
                    if (existing != normalised)
                    {
                        Console.Error.WriteLine($"  STALE  {relative.Replace('\\', '/')}");
                        stale++;
                    }
                    else
                    {
                        Console.WriteLine($"  ok     {relative.Replace('\\', '/')}");
                    }

                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                // Only touch a file whose content actually changed, so an unnecessary rebuild is
                // not triggered by running the generator.
                if (!File.Exists(path) || Normalise(File.ReadAllText(path)) != normalised)
                {
                    File.WriteAllText(path, normalised, new UTF8Encoding(false));
                    Console.WriteLine($"  wrote  {relative.Replace('\\', '/')}");
                }
                else
                {
                    Console.WriteLine($"  same   {relative.Replace('\\', '/')}");
                }
            }

            if (check && stale > 0)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine(
                    $"idlgen: {stale} generated file(s) do not match native/kernel.api.json.");
                Console.Error.WriteLine("Run ./build.ps1 -Generate and commit the result.");
                return 1;
            }

            Console.WriteLine(
                $"idlgen: {document.Operations.Count} operations, {outputs.Count} artefacts"
                + (check ? " verified." : " generated."));

            return 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException or IOException)
        {
            Console.Error.WriteLine($"idlgen: {exception.Message}");
            return 2;
        }
    }

    private static ApiDocument Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<ApiDocument>(stream, JsonOptions)
            ?? throw new InvalidOperationException("The IDL deserialised to nothing.");
    }

    /// <summary>
    /// Rejects an IDL that would generate code that does not compile, or an ABI that is unsafe.
    /// </summary>
    /// <param name="document">The document to check.</param>
    /// <remarks>
    /// Cheap here, expensive later: a duplicate export name is a link error with no line number,
    /// and an operation with no way to report a result is a silent hole in the surface.
    /// </remarks>
    private static void Validate(ApiDocument document)
    {
        if (document.Version != 1)
        {
            throw new InvalidOperationException(
                $"IDL version {document.Version} is not supported by this generator.");
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        HashSet<string> csharpNames = new(StringComparer.Ordinal);

        foreach (Operation operation in document.Operations)
        {
            if (string.IsNullOrWhiteSpace(operation.Name) || string.IsNullOrWhiteSpace(operation.CSharp))
            {
                throw new InvalidOperationException("Every operation needs a name and a csharp name.");
            }

            if (!names.Add(operation.Name))
            {
                throw new InvalidOperationException(
                    $"Duplicate operation name '{operation.Name}'. Export names must be unique.");
            }

            if (!csharpNames.Add(operation.CSharp))
            {
                throw new InvalidOperationException(
                    $"Duplicate C# name '{operation.CSharp}'.");
            }

            if (string.IsNullOrWhiteSpace(operation.Summary))
            {
                throw new InvalidOperationException(
                    $"Operation '{operation.Name}' has no summary. The summary becomes the public "
                    + "documentation on both sides of the boundary; an undocumented ABI entry point "
                    + "is one nobody can use correctly.");
            }

            HashSet<string> parameterNames = new(StringComparer.Ordinal);
            foreach (Parameter parameter in operation.Parameters)
            {
                // Throws with the list of known types if this is not one.
                Marshalling rule = TypeTable.For(parameter.Type);

                if (!parameterNames.Add(parameter.Name))
                {
                    throw new InvalidOperationException(
                        $"Operation '{operation.Name}' has two parameters named '{parameter.Name}'.");
                }

                if (string.IsNullOrWhiteSpace(parameter.Summary))
                {
                    throw new InvalidOperationException(
                        $"Parameter '{parameter.Name}' of '{operation.Name}' has no summary.");
                }

                if (parameter.Fixed is int size && (size <= 0 || !rule.IsOutput))
                {
                    throw new InvalidOperationException(
                        $"Parameter '{parameter.Name}' of '{operation.Name}' declares a fixed size "
                        + "but is not a positively-sized output buffer.");
                }
            }

            if (operation.Fragile && !operation.Parameters.Any(p => p.Name == "rung"))
            {
                throw new InvalidOperationException(
                    $"Operation '{operation.Name}' is marked fragile but has no 'rung' output. A "
                    + "fragile operation must report which rung of the retry ladder produced its "
                    + "result, or the health metric in PLAN.md 5.2.4 has nothing to aggregate.");
            }
        }
    }

    private static DirectoryInfo ResolveRoot(string? argument)
    {
        if (!string.IsNullOrWhiteSpace(argument))
        {
            return new DirectoryInfo(argument);
        }

        // Walk up looking for the marker file, so the tool works from anywhere.
        DirectoryInfo? candidate = new(AppContext.BaseDirectory);
        while (candidate is not null)
        {
            if (File.Exists(Path.Combine(candidate.FullName, "OpenMCAD.slnx")))
            {
                return candidate;
            }

            candidate = candidate.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root. Pass it as the first argument.");
    }

    /// <summary>Normalises line endings so generated output does not depend on the platform.</summary>
    /// <param name="content">The content to normalise.</param>
    private static string Normalise(string content)
        => content.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";
}
