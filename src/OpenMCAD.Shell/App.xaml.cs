using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenMCAD.App;
using OpenMCAD.App.Diagnostics;
using OpenMCAD.App.Hosting;
using OpenMCAD.ViewModels;

namespace OpenMCAD.Shell;

/// <summary>
/// The WPF application and composition root.
/// </summary>
/// <remarks>
/// P0-T09 and P0-T10. Everything the shell needs is resolved here and injected downward; no layer
/// below reaches back into the container. The last-chance exception handler is wired before the
/// window exists, so a failure during start-up is logged rather than silently closing the process.
/// </remarks>
public partial class App : Application
{
    private ILoggerFactory? _loggerFactory;
    private ServiceProvider? _services;
    private ILogger? _logger;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        // Windowed application: no console to write to, so file logging only.
        _loggerFactory = LogSetup.Create(
            AppInfo.IsDebuggerAttached ? LogVerbosity.Diagnostic : LogVerbosity.Normal,
            logToConsole: false);

        ServiceCollection services = [];
        services.AddOpenMcadCore(_loggerFactory);
        services.AddSingleton<MainWindowViewModel>();
        _services = services.BuildServiceProvider();

        _logger = _loggerFactory.CreateLogger("shell");
        _logger.LogInformation("Starting {Banner}", AppInfo.Banner);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        base.OnStartup(e);

        MainWindow = new MainWindow
        {
            DataContext = _services.GetRequiredService<MainWindowViewModel>(),
        };

        MainWindow.Show();
        _logger.LogInformation("Main window shown");
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("Shutting down with exit code {ExitCode}", e.ApplicationExitCode);

        base.OnExit(e);

        _services?.Dispose();

        // Disposing the factory flushes buffered file writes. Do it last, and do not skip it:
        // the log of a crash is written during the crash.
        _loggerFactory?.Dispose();
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogCritical(e.Exception, "Unhandled exception on the UI thread");

        // TODO(P6-T09): write a minidump and offer session recovery instead of terminating.
        // Until then, fail loudly rather than continuing in an unknown state.
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _logger?.LogCritical(
            e.ExceptionObject as Exception,
            "Unhandled exception, terminating: {IsTerminating}",
            e.IsTerminating);

        _loggerFactory?.Dispose();
    }
}
