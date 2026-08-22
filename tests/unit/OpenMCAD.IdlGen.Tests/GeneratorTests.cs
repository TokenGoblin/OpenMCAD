using System.Reflection;
using System.Text.Json;
using OpenMCAD.IdlGen;

namespace OpenMCAD.IdlGenTests;

/// <summary>
/// Tests the generator against the real IDL.
/// </summary>
/// <remarks>
/// <para>
/// These assert <i>structural</i> properties of the output rather than comparing against golden
/// text. A golden file would fail on every whitespace change and teach people to regenerate it
/// without reading the diff, which is the opposite of what a test on a compatibility surface should
/// do. What matters is that no operation goes missing from an artefact, that every output pointer
/// is checked, and that output does not depend on the machine.
/// </para>
/// <para>
/// They run against <c>native/kernel.api.json</c> itself, not a fixture, so adding an operation
/// exercises them for free.
/// </para>
/// </remarks>
public sealed class GeneratorTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static string RepoRoot { get; } =
        typeof(GeneratorTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "RepoRoot").Value!;

    private static ApiDocument Document { get; } = Load();

    private static ApiDocument Load()
    {
        string path = Path.Combine(RepoRoot, "native", "kernel.api.json");
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<ApiDocument>(stream, Options)!;
    }

    [Fact]
    public void TheIdlParsesAndHasOperations()
    {
        Document.Version.Should().Be(1);
        Document.Operations.Should().NotBeEmpty();
        Document.Prefix.Should().Be("openmcad");
    }

    [Fact]
    public void EveryOperationUsesAKnownType()
    {
        List<string> unknown =
        [
            .. Document.Operations
                .SelectMany(o => o.Parameters)
                .Select(p => p.Type)
                .Distinct(StringComparer.Ordinal)
                .Where(t => !TypeTable.KnownTypes.Contains(t, StringComparer.Ordinal)),
        ];

        unknown.Should().BeEmpty();
    }

    [Fact]
    public void EveryOperationAppearsInEveryArtefactThatShouldMentionIt()
    {
        string header = CHeaderEmitter.Emit(Document);
        string opsHeader = OpsHeaderEmitter.Emit(Document);
        string dispatch = DispatchEmitter.Emit(Document);
        string stubs = StubEmitter.Emit(Document);
        string bindings = CSharpEmitter.Emit(Document);

        foreach (Operation operation in Document.Operations)
        {
            string symbol = operation.CSymbol(Document.Prefix);

            header.Should().Contain(symbol, $"{symbol} must be declared");
            dispatch.Should().Contain(symbol, $"{symbol} must have an exported entry point");
            opsHeader.Should().Contain($"void {operation.Name}(", $"{operation.Name} needs a body signature");
            bindings.Should().Contain($"EntryPoint = \"{symbol}\"", $"{symbol} must be bound from C#");
            bindings.Should().Contain($"partial int {operation.CSharp}(", $"{operation.CSharp} must exist");

            // A stub, or an explicit note saying why there is none. Silence would mean a link error.
            stubs.Should().Contain(operation.Name, $"{operation.Name} must be stubbed or excused");
        }
    }

    [Fact]
    public void HandwrittenOperationsAreExcludedFromTheStubs()
    {
        string stubs = StubEmitter.Emit(Document);

        foreach (Operation operation in Document.Operations.Where(o => o.Handwritten))
        {
            // A generated stub here would collide at link time with the real body.
            stubs.Should().NotContain(
                $"void {operation.Name}(",
                $"{operation.Name} is hand-written in every build");

            stubs.Should().Contain($"/* {operation.Name}: hand-written");
        }

        Document.Operations.Count(o => o.Handwritten).Should().BeGreaterThan(0);
    }

    [Fact]
    public void EveryOutputPointerIsNullCheckedBeforeUse()
    {
        // Writing through a null output pointer corrupts the caller's memory instead of reporting
        // their mistake. The generator emits the check; this is what proves it did.
        string dispatch = DispatchEmitter.Emit(Document);

        foreach (Operation operation in Document.Operations)
        {
            string symbol = operation.CSymbol(Document.Prefix);

            foreach (Parameter parameter in operation.Parameters)
            {
                if (!TypeTable.For(parameter.Type).NeedsNullCheck)
                {
                    continue;
                }

                dispatch.Should().Contain(
                    $"openmcad::fail_null(\"{symbol}\", \"{parameter.Name}\")",
                    $"{symbol} must reject a null {parameter.Name}");
            }
        }
    }

    [Fact]
    public void EveryEntryPointIsInsideTheExceptionFirewall()
    {
        // A C++ exception crossing a C ABI is undefined behaviour, so this is not optional
        // anywhere. Counting is the cheap way to be sure none was missed.
        string dispatch = DispatchEmitter.Emit(Document);

        int guards = CountOccurrences(dispatch, "OPENMCAD_GUARD(");
        guards.Should().Be(Document.Operations.Count);
    }

    [Fact]
    public void FragileOperationsReportWhichRungSucceeded()
    {
        List<Operation> fragile = [.. Document.Operations.Where(o => o.Fragile)];

        fragile.Should().NotBeEmpty("booleans and blends are the known weak point (ADR-0001)");

        foreach (Operation operation in fragile)
        {
            operation.Parameters.Should().Contain(
                p => p.Name == "rung",
                $"{operation.Name} must report its retry rung for the PLAN.md 5.2.4 health metric");
        }
    }

    [Fact]
    public void GenerationIsDeterministic()
    {
        // Generated files are checked in, so output that varied between runs would produce a diff
        // on every build and make the CI freshness check meaningless.
        for (int i = 0; i < 3; i++)
        {
            CHeaderEmitter.Emit(Document).Should().Be(CHeaderEmitter.Emit(Document));
            DispatchEmitter.Emit(Document).Should().Be(DispatchEmitter.Emit(Document));
            CSharpEmitter.Emit(Document).Should().Be(CSharpEmitter.Emit(Document));
        }
    }

    [Fact]
    public void GeneratedFilesOnDiskMatchTheIdl()
    {
        // The same check CI runs. Having it here too means a stale binding fails in the local test
        // run rather than in CI ten minutes later.
        (string Relative, string Content)[] expected =
        [
            ("native/openmcad_occt/include/openmcad_occt.g.h", CHeaderEmitter.Emit(Document)),
            ("native/openmcad_occt/include/openmcad_ops.g.h", OpsHeaderEmitter.Emit(Document)),
            ("native/openmcad_occt/src/openmcad_dispatch.g.cpp", DispatchEmitter.Emit(Document)),
            ("native/openmcad_occt/src/openmcad_stubs.g.cpp", StubEmitter.Emit(Document)),
            ("src/OpenMCAD.Kernel.Occt/Interop/OcctBindings.g.cs", CSharpEmitter.Emit(Document)),
        ];

        foreach ((string relative, string content) in expected)
        {
            string path = Path.Combine(RepoRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(path).Should().BeTrue($"{relative} should have been generated");

            Normalise(File.ReadAllText(path)).Should().Be(
                Normalise(content),
                $"{relative} is out of step with the IDL. Run ./build.ps1 -Generate.");
        }
    }

    [Fact]
    public void EveryOperationAndParameterIsDocumented()
    {
        // The summary becomes the public documentation on both sides of the boundary. An
        // undocumented ABI entry point is one nobody can call correctly.
        foreach (Operation operation in Document.Operations)
        {
            operation.Summary.Should().NotBeNullOrWhiteSpace($"{operation.Name} needs a summary");

            foreach (Parameter parameter in operation.Parameters)
            {
                parameter.Summary.Should().NotBeNullOrWhiteSpace(
                    $"{operation.Name}.{parameter.Name} needs a summary");
            }
        }
    }

    [Fact]
    public void CSharpNamesAreValidIdentifiers()
    {
        string bindings = CSharpEmitter.Emit(Document);

        // "params" and friends would not compile; the emitter escapes them.
        bindings.Should().NotContain(" params,");
        bindings.Should().NotContain(" out out ");

        foreach (Operation operation in Document.Operations)
        {
            operation.CSharp.Should().MatchRegex("^[A-Z][A-Za-z0-9]*$");
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string Normalise(string content)
        => content.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";
}

/// <summary>Tests that the generator refuses an IDL that would produce broken output.</summary>
public sealed class ValidationTests
{
    [Fact]
    public void UnknownTypesAreRejectedWithTheListOfKnownOnes()
    {
        Action act = () => TypeTable.For("quaternion_array");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Unknown IDL type*")
            .WithMessage("*Known types*");
    }

    [Fact]
    public void TheTypeTableCoversBothDirections()
    {
        foreach (string type in TypeTable.KnownTypes)
        {
            Marshalling rule = TypeTable.For(type);

            rule.CParameters("x").Should().NotBeNullOrWhiteSpace();
            rule.CSharpParameters("x").Should().NotBeNullOrWhiteSpace();
            rule.OpsParameter("x").Should().NotBeNullOrWhiteSpace();
            rule.OpsArgument("x").Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void OutputTypesAreMarkedAsSuch()
    {
        foreach (string type in TypeTable.KnownTypes.Where(t => t.EndsWith("_out", StringComparison.Ordinal)))
        {
            TypeTable.For(type).IsOutput.Should().BeTrue($"{type} carries a result outwards");
        }
    }

    [Fact]
    public void NoInputTypeIsMarkedAsOutput()
    {
        foreach (string type in TypeTable.KnownTypes.Where(t => !t.EndsWith("_out", StringComparison.Ordinal)))
        {
            TypeTable.For(type).IsOutput.Should().BeFalse($"{type} is an input");
        }
    }
}
