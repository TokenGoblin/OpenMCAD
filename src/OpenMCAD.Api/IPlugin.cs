using Microsoft.Extensions.Logging;

namespace OpenMCAD.Api;

/// <summary>
/// What the host gives a plugin during initialisation.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately almost empty. PLAN.md 5.12 lists what this surface will eventually carry —
/// documents, features, custom properties, commands and ribbon contributions, selection, event
/// hooks, geometry queries — and none of it exists yet. Declaring those members now would mean
/// designing them against an imagined implementation and then either breaking plugins when the
/// real one disagrees, or freezing the design to avoid it. §5.12 opens by naming that trade
/// explicitly; the way to take the good half is to establish the *contract* early and let the
/// surface grow as each capability becomes real.
/// </para>
/// <para>
/// <b>No kernel handles, ever.</b> A plugin never sees a <c>KernelShape</c>, a <c>SubEntity</c>, or
/// anything else the geometry kernel owns. Plugins get the abstraction, so that ADR-0002's kernel
/// swap does not break every plugin in existence. This is the one rule here that cannot be relaxed
/// later, because relaxing it is invisible until the swap.
/// </para>
/// </remarks>
public interface IPluginHost
{
    /// <summary>Gets the API version this host presents.</summary>
    /// <remarks>
    /// The plugin has already been version-checked before it is loaded, so this is for logging and
    /// for a plugin that wants to light up a feature only on newer hosts.
    /// </remarks>
    Version ApiVersion { get; }

    /// <summary>Gets a logger scoped to the plugin.</summary>
    /// <remarks>
    /// Scoped rather than shared, so a misbehaving plugin is identifiable in a support bundle
    /// without the user having to work out which one it was.
    /// </remarks>
    ILogger Logger { get; }
}

/// <summary>
/// The entry point a plugin assembly implements.
/// </summary>
/// <remarks>
/// <para>
/// Exactly one implementation per plugin assembly, found by reflection when the assembly is loaded
/// into its own <see cref="System.Runtime.Loader.AssemblyLoadContext"/> (P2-T15). Isolation is what
/// keeps one plugin's dependency versions from colliding with another's, or with the host's.
/// </para>
/// <para>
/// A plugin declares which API version it was built against and the host refuses to load it if
/// that version is not supported. Refusing is friendlier than it sounds: the alternative is a
/// <see cref="MissingMethodException"/> at some later moment, blamed on whatever the user happened
/// to be doing.
/// </para>
/// </remarks>
public interface IPlugin
{
    /// <summary>Gets a short name, shown in the plugin list and used in log messages.</summary>
    string Name { get; }

    /// <summary>Gets the plugin's own version, which the host does not interpret.</summary>
    Version Version { get; }

    /// <summary>Gets the API version this plugin was built against.</summary>
    /// <remarks>
    /// Checked against <see cref="OpenMCAD.Api.ApiVersion.Supports"/> before
    /// <see cref="Initialize"/> is called. Report the version actually built against rather than
    /// the newest known: claiming a newer one to get past the check produces exactly the late,
    /// misattributed failure the check exists to prevent.
    /// </remarks>
    Version ApiVersion { get; }

    /// <summary>Called once, after the plugin is loaded and version-checked.</summary>
    /// <param name="host">What the host offers.</param>
    /// <remarks>
    /// Runs on the UI thread during startup, so it must be quick: work done here delays the window
    /// appearing, and PLAN.md 7 budgets 2.5 seconds for cold start including every plugin. Anything
    /// slow belongs on a background thread that this method starts and does not wait for.
    /// </remarks>
    void Initialize(IPluginHost host);
}
