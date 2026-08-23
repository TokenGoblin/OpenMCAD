using OpenMCAD.Api;

namespace OpenMCAD.SamplePlugin.Ambiguous;

/// <summary>One of two entry points in the same assembly, which the host must refuse.</summary>
public sealed class FirstPlugin : IPlugin
{
    /// <inheritdoc />
    public string Name => "First";

    /// <inheritdoc />
    public Version Version => new(1, 0, 0);

    /// <inheritdoc />
    public Version ApiVersion => new(OpenMCAD.Api.ApiVersion.Major, OpenMCAD.Api.ApiVersion.Minor);

    /// <inheritdoc />
    public void Initialize(IPluginHost host)
    {
    }
}

/// <summary>The second entry point. Which one the host should run is not determined.</summary>
public sealed class SecondPlugin : IPlugin
{
    /// <inheritdoc />
    public string Name => "Second";

    /// <inheritdoc />
    public Version Version => new(1, 0, 0);

    /// <inheritdoc />
    public Version ApiVersion => new(OpenMCAD.Api.ApiVersion.Major, OpenMCAD.Api.ApiVersion.Minor);

    /// <inheritdoc />
    public void Initialize(IPluginHost host)
    {
    }
}
