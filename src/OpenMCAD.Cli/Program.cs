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
/// The document commands (P3-T22) do their work in <see cref="DocumentCommands"/> rather than in
/// the actions here. Every later phase tests through this tool, and a test that had to start a
/// process to check an exit code would be slow, awkward to debug, and unable to see anything the
/// command did not print.
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

        Option<bool> jsonOption = new("--json")
        {
            Description = "Answer in JSON, for a script rather than a person.",
        };

        Option<bool> noCacheOption = new("--no-cache")
        {
            Description = "Ignore the regenerable caches in the file (\u00a75.8).",
        };

        Argument<FileInfo> specArgument = new("spec")
        {
            Description = "A document spec, as JSON.",
        };

        Option<FileInfo> outputOption = new("--output", "-o")
        {
            Description = "Where to write the result.",
            Required = true,
        };

        Command buildCommand = new("build", "Build a document from a spec and write it out.");
        buildCommand.Arguments.Add(specArgument);
        buildCommand.Options.Add(outputOption);
        buildCommand.Options.Add(jsonOption);
        buildCommand.SetAction(parse => DocumentCommands.Build(
            parse.GetValue(specArgument)!,
            parse.GetValue(outputOption)!,
            parse.GetValue(jsonOption),
            Console.Out));

        Argument<FileInfo> packageArgument = new("document")
        {
            Description = "A document package.",
        };

        Command inspectCommand = new("inspect", "Describe a document.");
        inspectCommand.Arguments.Add(packageArgument);
        inspectCommand.Options.Add(jsonOption);
        inspectCommand.Options.Add(noCacheOption);
        inspectCommand.SetAction(parse => DocumentCommands.Inspect(
            parse.GetValue(packageArgument)!,
            parse.GetValue(jsonOption),
            Console.Out,
            !parse.GetValue(noCacheOption)));

        Command saveCommand = new("save", "Open a document and write it out again.");
        saveCommand.Arguments.Add(packageArgument);
        saveCommand.Options.Add(outputOption);
        saveCommand.Options.Add(jsonOption);
        saveCommand.Options.Add(noCacheOption);
        saveCommand.SetAction(parse => DocumentCommands.Save(
            parse.GetValue(packageArgument)!,
            parse.GetValue(outputOption)!,
            parse.GetValue(jsonOption),
            Console.Out,
            !parse.GetValue(noCacheOption)));

        Command rebuildCommand = new(
            "rebuild", "Check a document against what this build knows how to make.");

        rebuildCommand.Arguments.Add(packageArgument);
        rebuildCommand.Options.Add(jsonOption);
        rebuildCommand.SetAction(parse => DocumentCommands.Rebuild(
            parse.GetValue(packageArgument)!, parse.GetValue(jsonOption), Console.Out));

        Argument<FileInfo> firstArgument = new("first") { Description = "A document package." };
        Argument<FileInfo> secondArgument = new("second") { Description = "Another one." };

        Command diffCommand = new("diff", "Compare two documents. Exits 1 if they differ.");
        diffCommand.Arguments.Add(firstArgument);
        diffCommand.Arguments.Add(secondArgument);
        diffCommand.Options.Add(jsonOption);
        diffCommand.SetAction(parse => DocumentCommands.Diff(
            parse.GetValue(firstArgument)!,
            parse.GetValue(secondArgument)!,
            parse.GetValue(jsonOption),
            Console.Out));

        root.Subcommands.Add(buildCommand);
        root.Subcommands.Add(inspectCommand);
        root.Subcommands.Add(rebuildCommand);
        root.Subcommands.Add(saveCommand);
        root.Subcommands.Add(diffCommand);

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
            lines.Add($"  {command.Name,-9} {command.Description}");
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
