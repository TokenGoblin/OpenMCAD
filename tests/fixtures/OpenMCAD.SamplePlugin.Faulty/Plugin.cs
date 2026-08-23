using OpenMCAD.Api;

namespace OpenMCAD.SamplePlugin.Faulty;

/// <summary>A plugin that throws while initialising, as a badly written one will.</summary>
public sealed class ThrowingPlugin : IPlugin
{
    /// <inheritdoc />
    public string Name => "Throws on startup";

    /// <inheritdoc />
    public Version Version => new(1, 0, 0);

    /// <inheritdoc />
    public Version ApiVersion => new(OpenMCAD.Api.ApiVersion.Major, OpenMCAD.Api.ApiVersion.Minor);

    /// <inheritdoc />
    public void Initialize(IPluginHost host)
        => throw new InvalidOperationException("this plugin is broken on purpose");
}
