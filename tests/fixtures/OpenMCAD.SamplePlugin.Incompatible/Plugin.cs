using OpenMCAD.Api;

namespace OpenMCAD.SamplePlugin.Incompatible;

/// <summary>A plugin built against an API version this host cannot present.</summary>
public sealed class FuturePlugin : IPlugin
{
    /// <summary>Set if the host ever calls Initialize, which it must not.</summary>
    public static bool Initialized { get; private set; }

    /// <inheritdoc />
    public string Name => "From the future";

    /// <inheritdoc />
    public Version Version => new(1, 0, 0);

    /// <inheritdoc />
    public Version ApiVersion => new(OpenMCAD.Api.ApiVersion.Major + 7, 0);

    /// <inheritdoc />
    public void Initialize(IPluginHost host) => Initialized = true;
}
