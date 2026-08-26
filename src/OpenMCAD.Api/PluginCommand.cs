namespace OpenMCAD.Api;

/// <summary>
/// Something a plugin contributes to the ribbon (P2-T15, PLAN.md 5.12).
/// </summary>
/// <remarks>
/// <para>
/// <b>A description of a command, not a control.</b> Nothing here mentions WPF, a button, an icon
/// format or a ribbon library. A plugin says what it wants to offer and the host decides how to
/// present it — which is what lets the shell change UI framework, or grow a command palette and a
/// context menu showing the same commands, without every plugin in existence needing a rebuild.
/// ADR-0007 draws that line for the application's own code; there is no reason plugins should sit
/// on the wrong side of it.
/// </para>
/// <para>
/// <b>The identifier is the plugin's promise, not the label.</b> Keyboard shortcuts, toolbar
/// customisation and recorded macros all refer to a command by something stable, and a display
/// label is exactly what gets reworded and translated. It is namespaced by convention —
/// <c>vendor.plugin.command</c> — because two plugins that both offer "Export" must not collide.
/// </para>
/// <para>
/// Validation lives in the property accessors rather than in a constructor, so that a
/// <c>with</c> expression cannot produce a command a constructor would have refused. A command
/// with no action looks perfectly correct in a ribbon and does nothing when pressed, which a user
/// reports as the application being broken rather than as the plugin being wrong.
/// </para>
/// </remarks>
public sealed record PluginCommand
{
    private readonly string _id = string.Empty;
    private readonly string _label = string.Empty;
    private readonly string _group = string.Empty;
    private readonly Action _execute = static () => { };

    /// <summary>Creates a command.</summary>
    /// <param name="id">A stable identifier, conventionally <c>vendor.plugin.command</c>.</param>
    /// <param name="label">The text on the button.</param>
    /// <param name="description">A sentence for the tooltip.</param>
    /// <param name="group">Which ribbon group to place it in.</param>
    /// <param name="execute">What to run when the user invokes it.</param>
    /// <exception cref="ArgumentException">An identifier, label or group is missing.</exception>
    /// <exception cref="ArgumentNullException">There is nothing to execute.</exception>
    public PluginCommand(string id, string label, string description, string group, Action execute)
    {
        Id = id;
        Label = label;
        Description = description ?? string.Empty;
        Group = group;
        Execute = execute;
    }

    /// <summary>Gets the stable identifier. Never shown to the user.</summary>
    public string Id
    {
        get => _id;

        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            _id = value;
        }
    }

    /// <summary>Gets the text on the button.</summary>
    public string Label
    {
        get => _label;

        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            _label = value;
        }
    }

    /// <summary>Gets a sentence for the tooltip.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Gets which ribbon group to place the command in.
    /// </summary>
    /// <remarks>
    /// Commands sharing a group name are gathered together, so a plugin offering several related
    /// commands stays together rather than being scattered across the tab.
    /// </remarks>
    public string Group
    {
        get => _group;

        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            _group = value;
        }
    }

    /// <summary>Gets what to run when the user invokes the command.</summary>
    public Action Execute
    {
        get => _execute;

        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _execute = value;
        }
    }
}

/// <summary>
/// Where a plugin registers what it wants to offer the user.
/// </summary>
/// <remarks>
/// Contributions are made during <see cref="IPlugin.Initialize"/> and not afterwards. A ribbon
/// that grew buttons at arbitrary later moments would be one the user cannot learn, and a host that
/// had to support insertion at any time could never lay one out properly.
/// </remarks>
public interface ICommandRegistry
{
    /// <summary>Offers a command to the host.</summary>
    /// <param name="command">What to add.</param>
    /// <exception cref="ArgumentNullException">There is no command.</exception>
    /// <exception cref="InvalidOperationException">
    /// Registration has closed, or another plugin already claimed that identifier.
    /// </exception>
    void Add(PluginCommand command);
}
