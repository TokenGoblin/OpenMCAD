using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenMCAD.App;
using OpenMCAD.App.Diagnostics;
using OpenMCAD.App.Hosting;
using OpenMCAD.Kernel.Occt;

namespace OpenMCAD.Cli;

/// <summary>
/// The headless entry point.
/// </summary>
/// <remarks>
/// <para>
/// P0-T11. This is deliberately built in Phase 0 rather than when it is first needed, because
/// every later test harness runs through it: the kernel smoke suite (P1), the headless document
/// API (P3-T22), the regression corpus runner, and the nightly determinism gate all invoke
/// <c>openmcad</c> rather than driving the application. Building it last would mean retrofitting
/// headless operation onto subsystems that had quietly assumed a window.
/// </para>
/// <para>
/// Commands are added by later phases. Phase 0 ships the shell of the tool and nothing else.
/// </para>
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        RootCommand root = new("OpenMCAD headless runner — build, rebuild, convert, and regress models without a UI.");

        Option<bool> verboseOption = new("--verbose", "-v")
        {
            Description = "Emit diagnostic-level logging.",
        };

        Option<bool> quietOption = new("--quiet", "-q")
        {
            Description = "Emit warnings and errors only.",
        };

        root.Options.Add(verboseOption);
        root.Options.Add(quietOption);

        Command versionCommand = new("version", "Print version and environment information.");
        versionCommand.SetAction(parseResult =>
        {
            LogVerbosity verbosity = VerbosityFrom(parseResult, verboseOption, quietOption);
            using ILoggerFactory loggerFactory = LogSetup.Create(verbosity);

            ServiceCollection services = [];
            services.AddOpenMcadCore(loggerFactory);
            using ServiceProvider provider = services.BuildServiceProvider();

            ILogger logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("cli");
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Resolved {Banner}", AppInfo.Banner);
            }

            Console.WriteLine(AppInfo.Banner);
            Console.WriteLine($"  assembly version : {AppInfo.AssemblyVersion}");
            Console.WriteLine($"  log directory    : {AppInfo.LogDirectory}");

            // Read from the shim rather than stated here. This line said "not yet linked" for
            // three phases after it stopped being true, because nothing made it wrong -- a
            // hard-coded status is only ever accurate on the day it is written.
            Console.WriteLine($"  geometry kernel  : {OcctKernel.ShimVersion}");
            return 0;
        });

        root.Subcommands.Add(versionCommand);

        // Bare invocation prints help rather than doing nothing, which is what a person running
        // an unfamiliar tool actually wants.
        root.SetAction(parseResult =>
        {
            _ = parseResult;
            Console.WriteLine(AppInfo.Banner);
            Console.WriteLine();
            Console.Write(HelpTextFor(root));
            return 0;
        });

        return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
    }

    private static LogVerbosity VerbosityFrom(
        ParseResult parseResult,
        Option<bool> verboseOption,
        Option<bool> quietOption)
    {
        if (parseResult.GetValue(quietOption))
        {
            return LogVerbosity.Quiet;
        }

        return parseResult.GetValue(verboseOption) ? LogVerbosity.Diagnostic : LogVerbosity.Normal;
    }

    private static string HelpTextFor(RootCommand root)
    {
        List<string> lines =
        [
            "Usage: openmcad [command] [options]",
            string.Empty,
            "Commands:",
        ];

        foreach (Command command in root.Subcommands)
        {
            lines.Add($"  {command.Name,-12} {command.Description}");
        }

        lines.Add(string.Empty);
        lines.Add("Options:");
        lines.Add("  -v, --verbose  Emit diagnostic-level logging.");
        lines.Add("  -q, --quiet    Emit warnings and errors only.");
        lines.Add("      --version  Print the version.");
        lines.Add(string.Empty);

        return string.Join(Environment.NewLine, lines);
    }
}
