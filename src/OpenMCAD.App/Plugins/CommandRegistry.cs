using System.Collections.ObjectModel;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using OpenMCAD.Api;

namespace OpenMCAD.App.Plugins;

/// <summary>A command a plugin contributed, and which plugin contributed it.</summary>
/// <param name="Command">What the plugin offered.</param>
/// <param name="PluginName">Who offered it.</param>
/// <remarks>
/// The contributor is kept because everything about supporting a plugin ecosystem needs it: a
/// command that throws has to be attributable, a user disabling a plugin has to have its commands
/// disappear with it, and a support bundle listing "Export" tells nobody anything.
/// </remarks>
public sealed record ContributedCommand(PluginCommand Command, string PluginName);

/// <summary>
/// Collects the commands plugins contribute during loading (P2-T15).
/// </summary>
/// <remarks>
/// <para>
/// <b>Registration closes once loading finishes.</b> A ribbon that grew buttons at arbitrary later
/// moments would be one the user cannot learn, and a host that had to support insertion at any
/// time could never lay one out properly. Closing it also means a plugin cannot hold the registry
/// and add a command months later, from a thread nobody expected.
/// </para>
/// <para>
/// <b>Identifiers are claimed first-come.</b> Two plugins offering <c>export</c> is not a
/// hypothetical, and silently letting the second overwrite the first would make one plugin's
/// buttons vanish depending on directory enumeration order. The second is refused and named in the
/// log, which is a complaint someone can act on.
/// </para>
/// </remarks>
public sealed class CommandRegistry
{
    private readonly List<ContributedCommand> _commands = [];
    private readonly HashSet<string> _claimed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger;

    private bool _closed;

    /// <summary>Creates the registry.</summary>
    /// <param name="logger">Where to report refused contributions.</param>
    public CommandRegistry(ILogger? logger = null) => _logger = logger ?? NullLogger.Instance;

    /// <summary>Gets the commands contributed so far, in the order they were offered.</summary>
    public ReadOnlyCollection<ContributedCommand> Commands => _commands.AsReadOnly();

    /// <summary>Gets whether registration has closed.</summary>
    public bool IsClosed => _closed;

    /// <summary>
    /// Gets a view of this registry scoped to one plugin.
    /// </summary>
    /// <param name="pluginName">Which plugin is contributing.</param>
    /// <returns>What to hand that plugin.</returns>
    /// <remarks>
    /// A per-plugin view rather than the registry itself, so a contribution carries its
    /// contributor without the plugin being asked to declare it — and so a plugin cannot register
    /// commands on another plugin's behalf.
    /// </remarks>
    public ICommandRegistry For(string pluginName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        return new Scoped(this, pluginName);
    }

    /// <summary>Closes registration, after every plugin has been initialised.</summary>
    public void Close() => _closed = true;

    private void Add(PluginCommand command, string pluginName)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (_closed)
        {
            throw new InvalidOperationException(
                $"'{pluginName}' tried to register the command '{command.Id}' after loading "
                + "finished. Commands must be contributed during Initialize.");
        }

        if (!_claimed.Add(command.Id))
        {
            // Refused rather than allowed to overwrite. Which of two plugins wins would otherwise
            // depend on the order a directory happened to enumerate in.
            throw new InvalidOperationException(
                $"The command id '{command.Id}' is already registered by another plugin. Ids are "
                + "namespaced by convention as vendor.plugin.command for this reason.");
        }

        _commands.Add(new ContributedCommand(command, pluginName));

        _logger.LogInformation(
            "Plugin '{Plugin}' contributed the command '{Label}' ({Id})",
            pluginName,
            command.Label,
            command.Id);
    }

    /// <summary>One plugin's view of the registry.</summary>
    private sealed class Scoped(CommandRegistry owner, string pluginName) : ICommandRegistry
    {
        /// <inheritdoc />
        public void Add(PluginCommand command) => owner.Add(command, pluginName);
    }
}
