using Microsoft.Extensions.Logging;

using OpenMCAD.Api;

namespace OpenMCAD.SamplePlugin;

/// <summary>A minimal plugin, used by the loader tests and as the worked example.</summary>
public sealed class SamplePlugin : IPlugin
{
    /// <summary>Gets whether Initialize has run, so a test can prove the host reached it.</summary>
    public static bool Initialized { get; private set; }

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
    }
}
