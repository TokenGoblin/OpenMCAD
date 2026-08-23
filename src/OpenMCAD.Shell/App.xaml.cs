using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenMCAD.App;
using OpenMCAD.App.Diagnostics;
using OpenMCAD.App.Hosting;
using OpenMCAD.Kernel;
using OpenMCAD.Kernel.Occt;
using OpenMCAD.Render;
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
public sealed partial class App : Application, IDisposable
{
    private readonly CancellationTokenSource _closing = new();

    private ILoggerFactory? _loggerFactory;
    private ServiceProvider? _services;
    private ILogger? _logger;
    private OcctKernel? _kernel;

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

        // Before the window is built: XAML constructs the viewport during MainWindow's
        // initialisation, and it reads this then.
        ViewportHost.LoggerFactory = _loggerFactory;

        base.OnStartup(e);

        MainWindow window = new()
        {
            DataContext = _services.GetRequiredService<MainWindowViewModel>(),
        };

        MainWindow = window;
        window.Show();
        _logger.LogInformation("Main window shown");

        // After Show, so the window is up before the kernel starts. Creating the kernel spins up
        // its dedicated thread and loads the native shim, which is not work to do between the
        // splash and the first paint.
        _ = ShowStarterSceneAsync(window);
    }

    /// <summary>
    /// Builds the opening scene and hands it to the viewport.
    /// </summary>
    /// <remarks>
    /// Scaffolding until there is a document model; see <see cref="StarterScene"/>. Awaited on the
    /// UI thread's synchronisation context, so assigning to the viewport afterwards is safe -- and
    /// nothing here is allowed to throw, because a failure to make a box must not close the window
    /// that would have reported it.
    /// </remarks>
    private async Task ShowStarterSceneAsync(MainWindow window)
    {
        ILogger logger = _logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        try
        {
            _kernel = new OcctKernel(_loggerFactory?.CreateLogger<OcctKernel>());

            // ConfigureAwait(true) is the point rather than an oversight: the continuation
            // assigns to a WPF element and must come back to the UI thread.
            DisplaySnapshot snapshot = await StarterScene
                .BuildAsync(_kernel, logger, _closing.Token)
                .ConfigureAwait(true);

            window.Viewport.Snapshot = snapshot;
            window.Viewport.ZoomToFit();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The kernel could not be started; the viewport stays empty");
        }
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("Shutting down with exit code {ExitCode}", e.ApplicationExitCode);

        _closing.Cancel();

        // The kernel owns a thread and a native handle table, and disposing it waits for work in
        // flight. Done before the container, which owns the logger it writes its shutdown to.
        if (_kernel is not null)
        {
            _kernel.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _kernel = null;
        }

        base.OnExit(e);

        _closing.Dispose();
        _services?.Dispose();

        // Disposing the factory flushes buffered file writes. Do it last, and do not skip it:
        // the log of a crash is written during the crash.
        _loggerFactory?.Dispose();
    }

    /// <summary>Releases the kernel and the shutdown signal.</summary>
    /// <remarks>
    /// <see cref="Application"/> is not disposable, so this exists for the analyser and for tests
    /// rather than for WPF, which calls <see cref="OnExit"/> instead. Both paths are idempotent.
    /// </remarks>
    public void Dispose()
    {
        _closing.Dispose();
        _kernel?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _kernel = null;
        GC.SuppressFinalize(this);
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
