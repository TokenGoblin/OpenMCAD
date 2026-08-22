using System.Globalization;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace OpenMCAD.App.Diagnostics;

/// <summary>
/// How verbose logging should be.
/// </summary>
public enum LogVerbosity
{
    /// <summary>Warnings and errors only.</summary>
    Quiet,

    /// <summary>The default: informational messages and above.</summary>
    Normal,

    /// <summary>Everything, including per-operation rebuild traces.</summary>
    Diagnostic,
}

/// <summary>
/// Configures structured logging for every OpenMCAD entry point.
/// </summary>
/// <remarks>
/// <para>
/// P0-T09. PLAN.md 6.1 requires structured logging throughout, because the things worth knowing
/// about a rebuild — per-feature duration, which rung of the retry ladder a kernel operation
/// reached, which tier resolved a persistent name, cache hit or miss — are structured facts, not
/// prose. Serilog is the sink implementation; everything above logs through
/// <c>Microsoft.Extensions.Logging</c> so the sink stays replaceable.
/// </para>
/// <para>
/// Both the shell and the CLI call this. Neither configures logging itself, so a diagnostic
/// captured from a headless run is directly comparable with one captured from the application.
/// </para>
/// </remarks>
public static class LogSetup
{
    /// <summary>
    /// Builds the Serilog logger and returns a factory for the rest of the application.
    /// </summary>
    /// <param name="verbosity">How much to log.</param>
    /// <param name="logToConsole">
    /// Whether to write to the console. True for the CLI; false for the windowed shell, which has
    /// no console attached.
    /// </param>
    /// <param name="logDirectory">
    /// Where to write rolling log files, or <see langword="null"/> for
    /// <see cref="AppInfo.LogDirectory"/>. Pass a value only in tests.
    /// </param>
    /// <returns>
    /// A logger factory owning the underlying Serilog logger. Dispose it on shutdown so buffered
    /// file writes are flushed.
    /// </returns>
    public static ILoggerFactory Create(
        LogVerbosity verbosity = LogVerbosity.Normal,
        bool logToConsole = true,
        string? logDirectory = null)
    {
        LogEventLevel level = verbosity switch
        {
            LogVerbosity.Quiet => LogEventLevel.Warning,
            LogVerbosity.Diagnostic => LogEventLevel.Verbose,
            _ => LogEventLevel.Information,
        };

        string directory = logDirectory ?? AppInfo.LogDirectory;
        Directory.CreateDirectory(directory);

        LoggerConfiguration configuration = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.WithProperty("Product", AppInfo.ProductName)
            .Enrich.WithProperty("Version", AppInfo.Version)
            .WriteTo.File(
                Path.Combine(directory, "openmcad-.log"),
                // Invariant culture, always. A log is machine-parsed before it is human-read,
                // and a decimal comma in a duration field breaks every downstream analysis.
                formatProvider: CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate:
                    "{Timestamp:yyyy-MM-ddTHH:mm:ss.fffZ} [{Level:u3}] ({SourceContext}) {Message:lj}{NewLine}{Exception}");

        if (logToConsole)
        {
            configuration = configuration.WriteTo.Console(
                outputTemplate: "[{Level:u3}] {Message:lj}{NewLine}{Exception}",
                formatProvider: CultureInfo.InvariantCulture);
        }

        Serilog.Core.Logger logger = configuration.CreateLogger();
        Log.Logger = logger;

        return LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(logger, dispose: true);
        });
    }
}
