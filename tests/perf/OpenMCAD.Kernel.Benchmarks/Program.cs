using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

namespace OpenMCAD.Kernel.Benchmarks;

/// <summary>
/// Entry point for the kernel benchmark harness (P1-T15).
/// </summary>
/// <remarks>
/// <para>
/// Run it with <c>dotnet run -c Release --project tests/perf/OpenMCAD.Kernel.Benchmarks</c>, after
/// a <c>./build.ps1 -WithOcct</c> so the native shim and its dependency closure are present.
/// Deliberately not wired into <c>build.ps1</c>: BenchmarkDotNet takes minutes, wants a quiet
/// machine, and a number measured on a busy laptop is worse than no number because it will be
/// compared against later.
/// </para>
/// <para>
/// The recorded baseline lives in <c>docs/notes/kernel-baseline.md</c>, together with the machine
/// it was taken on — a timing without its hardware is not a baseline, it is a rumour.
/// </para>
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        BenchmarkSwitcher
            .FromAssembly(typeof(Program).Assembly)
            .Run(args, DefaultConfig.Instance.WithOptions(ConfigOptions.DisableOptimizationsValidator));

        return 0;
    }
}
