using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using OpenMCAD.Api;

namespace OpenMCAD.App.Plugins;

/// <summary>
/// What the application hands a plugin during initialisation (P2-T15).
/// </summary>
/// <remarks>
/// <para>
/// One host object serves every plugin, but each gets its own view of the parts that need to know
/// who is asking: the logger is scoped to the plugin's name so a support bundle attributes its
/// output, and the command registry is scoped so a contribution carries its contributor without
/// the plugin being asked to declare it — and so one plugin cannot register commands on another's
/// behalf.
/// </para>
/// <para>
/// <b>Nothing here exposes a kernel handle.</b> A plugin never sees a <c>KernelShape</c> or a
/// <c>SubEntity</c>; that is ADR-0002's rule and the one thing on this surface that cannot be
/// relaxed later, because relaxing it stays invisible until the kernel is swapped.
/// </para>
/// </remarks>
public sealed class PluginHost : IPluginHost
{
    private readonly CommandRegistry _registry;
    private readonly ILoggerFactory? _loggerFactory;

    private string _current = "plugin";

    /// <summary>Creates the host.</summary>
    /// <param name="registry">Where contributed commands are collected.</param>
    /// <param name="loggerFactory">Used to make a logger per plugin.</param>
    public PluginHost(CommandRegistry registry, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        _registry = registry;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public Version ApiVersion => PluginLoader.HostApiVersion;

    /// <inheritdoc />
    public ICommandRegistry Commands => _registry.For(_current);

    /// <inheritdoc />
    public ILogger Logger => _loggerFactory?.CreateLogger($"plugin:{_current}") ?? NullLogger.Instance;

    /// <summary>
    /// Names the plugin about to be initialised, so its contributions are attributed to it.
    /// </summary>
    /// <param name="pluginName">Which plugin is being initialised.</param>
    /// <remarks>
    /// Set by the loader immediately before <see cref="IPlugin.Initialize"/>. Plugins are
    /// initialised one at a time on one thread, so a single current name is sufficient — and it is
    /// what lets a plugin be handed a host it can hold without also being handed the ability to
    /// impersonate another.
    /// </remarks>
    public void BeginPlugin(string pluginName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        _current = pluginName;
    }
}
