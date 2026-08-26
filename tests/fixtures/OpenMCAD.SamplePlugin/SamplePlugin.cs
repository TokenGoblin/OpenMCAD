using Microsoft.Extensions.Logging;

using OpenMCAD.Api;

namespace OpenMCAD.SamplePlugin;

/// <summary>
/// A minimal plugin, used by the loader tests and as the worked example (P2-T15).
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of what a plugin has to do to put a button on the ribbon: implement
/// <see cref="IPlugin"/>, and register a <see cref="PluginCommand"/> during
/// <see cref="Initialize"/>. There is no XAML, no reference to a UI framework, and nothing about
/// how the button is drawn — the plugin describes a command and the host decides how to present
/// it.
/// </para>
/// <para>
/// It is deliberately a *fixture* rather than a separate sample project. A plugin declared
/// alongside the tests would share the host's types by construction and prove nothing about
/// isolation, so this is separately compiled — which makes it both the example and the thing the
/// loader tests load.
/// </para>
/// </remarks>
public sealed class SamplePlugin : IPlugin
{
    /// <summary>Gets whether Initialize has run, so a test can prove the host reached it.</summary>
    public static bool Initialized { get; private set; }

    /// <summary>Gets how many times the contributed command has been invoked.</summary>
    public static int Invocations { get; private set; }

    /// <inheritdoc />
    public string Name => "Sample";

    /// <inheritdoc />
    public Version Version => new(1, 0, 0);

    /// <inheritdoc />
    public Version ApiVersion => new(OpenMCAD.Api.ApiVersion.Major, OpenMCAD.Api.ApiVersion.Minor);

    /// <inheritdoc />
    public void Initialize(IPluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        Initialized = true;
        host.Logger.LogInformation("Sample plugin initialised against API {Version}", host.ApiVersion);

        // The id is namespaced because two plugins both offering "Say hello" must not collide, and
        // because shortcuts and recorded macros need something stabler than a label to refer to.
        host.Commands.Add(new PluginCommand(
            "openmcad.sample.hello",
            "Say Hello",
            "Writes a greeting to the log, to show that a plugin can reach the ribbon.",
            "Sample",
            () =>
            {
                Invocations++;
                host.Logger.LogInformation("Hello from the sample plugin");
            }));
    }
}
