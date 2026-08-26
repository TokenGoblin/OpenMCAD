using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenMCAD.App;
using OpenMCAD.App.Diagnostics;
using OpenMCAD.App.Hosting;
using OpenMCAD.Api;
using OpenMCAD.App.Plugins;
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

        LoadPlugins(window);

        MainWindow = window;
        window.Show();
        _logger.LogInformation("Main window shown");

        // After Show, so the window is up before the kernel starts. Creating the kernel spins up
        // its dedicated thread and loads the native shim, which is not work to do between the
        // splash and the first paint.
        _ = ShowStarterSceneAsync(window);
    }

    /// <summary>
    /// Loads plugins and puts whatever they contribute on the ribbon.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before the window is shown, so the ribbon is laid out once rather than growing buttons
    /// while the user watches. Registration closes immediately afterwards: a plugin that held the
    /// registry and added a command later would produce a ribbon nobody can learn.
    /// </para>
    /// <para>
    /// Plugins are loaded from a <c>plugins</c> directory beside the executable. A missing
    /// directory is the ordinary case and yields nothing.
    /// </para>
    /// </remarks>
    private void LoadPlugins(MainWindow window)
    {
        ILogger logger = _logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        try
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "plugins");

            PluginLoader loader = new(_loggerFactory?.CreateLogger<PluginLoader>());
            CommandRegistry registry = new(logger);
            PluginHost host = new(registry, _loggerFactory);

            IReadOnlyList<PluginLoadResult> results = loader.LoadFrom(directory, host);
            registry.Close();

            int loaded = results.Count(r => r.Loaded);

            if (results.Count > 0)
            {
                logger.LogInformation(
                    "Loaded {Loaded} of {Total} plugins from {Directory}",
                    loaded,
                    results.Count,
                    directory);
            }

            PublishCommands(window, registry, logger);
        }
        catch (Exception exception)
        {
            // Deliberately broad, and for the same reason the loader itself is: a third-party
            // assembly must not be able to stop the application from starting, because the user's
            // only remedy would be to find and delete a file they may not know exists.
            logger.LogError(exception, "Plugins could not be loaded");
        }
    }

    /// <summary>Groups contributed commands and hands them to the view model.</summary>
    private static void PublishCommands(
        MainWindow window, CommandRegistry registry, ILogger logger)
    {
        if (window.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        foreach (ContributedCommand contributed in registry.Commands)
        {
            PluginCommand command = contributed.Command;
            string plugin = contributed.PluginName;

            viewModel.PluginCommands.Add(
                new PluginCommandItem(command.Label, command.Description, plugin, command.Group)
                {
                    Invoke = () =>
                    {
                        // The command is somebody else's code, and an exception escaping a click
                        // handler takes the application down. A plugin that throws is reported and
                        // survived, exactly as one that throws during loading is.
                        try
                        {
                            command.Execute();
                        }
                        catch (Exception exception)
                        {
                            logger.LogError(
                                exception,
                                "The command '{Command}' from plugin '{Plugin}' threw",
                                command.Id,
                                plugin);
                        }
                    },
                });
        }
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
