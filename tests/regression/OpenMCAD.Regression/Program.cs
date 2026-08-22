using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using OpenMCAD.Kernel;
using OpenMCAD.Kernel.Fake;

namespace OpenMCAD.Regression;

/// <summary>
/// The regression corpus runner.
/// </summary>
/// <remarks>
/// <para>
/// P1-T14. PLAN.md 14 names this the single most important discipline in the project: "build the
/// regression corpus from Phase 1, and never let a fix ship without a fixture. Everything else is
/// recoverable. That is the discipline that separates a CAD system that gets better every year from
/// one that oscillates forever."
/// </para>
/// <para>
/// A separate executable rather than a test project, because it has two jobs a test runner does not
/// do well: run the whole corpus twice and diff the results (the determinism gate, ADR-0011), and
/// run against a chosen kernel so the same fixtures can be replayed nightly against OCCT.
/// </para>
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

        bool determinism = args.Contains("--determinism", StringComparer.Ordinal);
        bool verbose = args.Contains("--verbose", StringComparer.Ordinal);
        string? filter = ArgumentValue(args, "--filter");
        string kernelName = ArgumentValue(args, "--kernel") ?? "fake";

        try
        {
            DirectoryInfo corpus = CorpusLoader.FindCorpus();
            ImmutableArray<Fixture> fixtures = CorpusLoader.LoadAll(corpus);

            if (filter is not null)
            {
                fixtures = [.. fixtures.Where(f =>
                    f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || f.Category.Contains(filter, StringComparison.OrdinalIgnoreCase))];
            }

            if (fixtures.IsEmpty)
            {
                Console.Error.WriteLine("No fixtures matched. The corpus must never be empty.");
                return 2;
            }

            Console.WriteLine($"OpenMCAD regression corpus: {fixtures.Length} fixtures, kernel '{kernelName}'");
            Console.WriteLine($"  {corpus.FullName}");
            Console.WriteLine();

            ImmutableArray<FixtureResult> first = await RunAllAsync(kernelName, fixtures, verbose)
                .ConfigureAwait(false);

            int failed = Report(first, verbose);

            if (determinism)
            {
                Console.WriteLine();
                Console.WriteLine("Determinism gate: replaying the corpus on a fresh kernel.");

                ImmutableArray<FixtureResult> second = await RunAllAsync(kernelName, fixtures, verbose: false)
                    .ConfigureAwait(false);

                failed += CompareRuns(first, second);
            }

            Console.WriteLine();
            Console.WriteLine(failed == 0
                ? $"PASS  {fixtures.Length} fixtures"
                : $"FAIL  {failed} of {fixtures.Length} fixtures");

            return failed == 0 ? 0 : 1;
        }
        catch (Exception exception) when (
            exception is DirectoryNotFoundException or InvalidDataException or IOException)
        {
            Console.Error.WriteLine($"regression: {exception.Message}");
            return 2;
        }
    }

    private static async Task<ImmutableArray<FixtureResult>> RunAllAsync(
        string kernelName,
        ImmutableArray<Fixture> fixtures,
        bool verbose)
    {
        await using IGeometryKernel kernel = CreateKernel(kernelName);
        ScenarioRunner runner = new(kernel);

        ImmutableArray<FixtureResult>.Builder results =
            ImmutableArray.CreateBuilder<FixtureResult>(fixtures.Length);

        foreach (Fixture fixture in fixtures)
        {
            FixtureResult result = await runner.RunAsync(fixture).ConfigureAwait(false);
            results.Add(result);

            if (verbose)
            {
                Console.WriteLine(
                    $"  {(result.Passed ? "ok  " : "FAIL")}  {fixture.Category}/{fixture.Name} "
                    + $"({result.Duration.TotalMilliseconds:F0} ms)");
            }
        }

        return results.MoveToImmutable();
    }

    [SuppressMessage(
        "Performance",
        "CA1859:Use concrete types when possible for improved performance",
        Justification =
            "The analyser infers FakeKernel because it is the only case today. Selecting a kernel "
            + "at run time is the entire purpose of this method -- OcctKernel joins it at P1-T06 "
            + "-- so narrowing the return type would have to be undone immediately.")]
    private static IGeometryKernel CreateKernel(string name) => name.ToLowerInvariant() switch
    {
        "fake" => new FakeKernel(),

        // TODO(P1-T06): "occt" => new OcctKernel(). The corpus is deliberately kernel-agnostic so
        // that adding it here is the whole integration.
        _ => throw new InvalidDataException(
            $"Unknown kernel '{name}'. Known kernels: fake."),
    };

    private static int Report(ImmutableArray<FixtureResult> results, bool verbose)
    {
        int failed = 0;

        foreach (FixtureResult result in results)
        {
            foreach (string skip in result.Skipped)
            {
                Console.WriteLine($"  skip  {result.Fixture.Category}/{result.Fixture.Name}: {skip}");
            }

            if (result.Passed)
            {
                continue;
            }

            failed++;
            Console.Error.WriteLine($"  FAIL  {result.Fixture.Category}/{result.Fixture.Name}");
            Console.Error.WriteLine($"        {result.Fixture.Description}");

            foreach (string failure in result.Failures)
            {
                Console.Error.WriteLine($"        - {failure}");
            }
        }

        if (verbose)
        {
            Console.WriteLine();
            foreach (FixtureResult result in results)
            {
                Console.WriteLine($"  {result.Fixture.Name,-28} {result.Signature}");
            }
        }

        return failed;
    }

    /// <summary>
    /// Compares two runs of the corpus and reports any divergence.
    /// </summary>
    /// <remarks>
    /// ADR-0011 and PLAN.md 5.2.3. Non-determinism silently corrupts undo, caching, and naming, and
    /// it does so in ways that look like unrelated intermittent bugs months later. A difference
    /// here is a P0 even when every fixture still passes.
    /// </remarks>
    private static int CompareRuns(
        ImmutableArray<FixtureResult> first,
        ImmutableArray<FixtureResult> second)
    {
        int diverged = 0;

        for (int i = 0; i < first.Length && i < second.Length; i++)
        {
            if (string.Equals(first[i].Signature, second[i].Signature, StringComparison.Ordinal))
            {
                continue;
            }

            diverged++;
            Console.Error.WriteLine($"  NONDETERMINISTIC  {first[i].Fixture.Name}");
            Console.Error.WriteLine($"        run 1: {first[i].Signature}");
            Console.Error.WriteLine($"        run 2: {second[i].Signature}");
        }

        Console.WriteLine(diverged == 0
            ? "  determinism: identical across both runs"
            : $"  determinism: {diverged} fixtures diverged - this is a P0");

        return diverged;
    }

    private static string? ArgumentValue(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
