using System.Diagnostics;
using System.Reflection;

namespace OpenMCAD.App;

/// <summary>
/// Product and version identity, read from assembly attributes stamped at build time.
/// </summary>
/// <remarks>
/// P0-T14. The version is never hard-coded in source: <c>Directory.Version.props</c> is the single
/// place it is declared, and CI appends build metadata there. Anything that displays or reports a
/// version reads it from here, so there is exactly one answer to "which build is this?".
/// </remarks>
public static class AppInfo
{
    /// <summary>Gets the product name.</summary>
    public static string ProductName => "OpenMCAD";

    /// <summary>
    /// Gets the full semantic version, including any prerelease tag and build metadata.
    /// </summary>
    /// <remarks>
    /// This is the string to quote in a bug report. It is the informational version, so for a CI
    /// build it looks like <c>0.1.0-alpha+20260822.417.a1b2c3d</c> and identifies the exact commit.
    /// </remarks>
    public static string Version { get; } =
        typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString()
        ?? "0.0.0-unknown";

    /// <summary>Gets the numeric assembly version, without prerelease or build metadata.</summary>
    public static string AssemblyVersion { get; } =
        typeof(AppInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

    /// <summary>Gets the runtime framework description, for diagnostics.</summary>
    public static string RuntimeDescription => RuntimeInformationDescription();

    /// <summary>
    /// Gets a one-line banner identifying the product, version, and runtime.
    /// </summary>
    public static string Banner
        => $"{ProductName} {Version} ({RuntimeDescription})";

    private static string RuntimeInformationDescription()
        => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

    /// <summary>
    /// Gets the per-user directory for logs, crash dumps, and recovery data.
    /// </summary>
    /// <remarks>
    /// Deliberately under <c>LocalApplicationData</c> rather than <c>ApplicationData</c>: this
    /// content is machine-local, can be large, and must never follow a roaming profile.
    /// </remarks>
    public static string LocalDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductName);

    /// <summary>Gets the directory log files are written to.</summary>
    public static string LogDirectory { get; } = Path.Combine(LocalDataDirectory, "logs");

    /// <summary>
    /// Gets a value indicating whether a debugger is attached, used to widen diagnostics.
    /// </summary>
    public static bool IsDebuggerAttached => Debugger.IsAttached;
}
