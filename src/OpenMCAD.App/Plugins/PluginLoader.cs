using System.Reflection;
using System.Runtime.Loader;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using OpenMCAD.Api;

namespace OpenMCAD.App.Plugins;

/// <summary>Why a plugin was not loaded.</summary>
public enum PluginRejection
{
    /// <summary>It loaded.</summary>
    None,

    /// <summary>The file could not be read as a .NET assembly.</summary>
    NotAnAssembly,

    /// <summary>It contains no <see cref="IPlugin"/> implementation.</summary>
    NoEntryPoint,

    /// <summary>It contains more than one, so which to run is ambiguous.</summary>
    AmbiguousEntryPoint,

    /// <summary>It was built against an API version this host does not support.</summary>
    IncompatibleApiVersion,

    /// <summary>Constructing it or initialising it threw.</summary>
    Faulted,
}

/// <summary>What happened to one candidate plugin.</summary>
/// <param name="Path">Where it came from.</param>
/// <param name="Name">Its name, if it got far enough to have one.</param>
/// <param name="Rejection">Why it was not loaded, or <see cref="PluginRejection.None"/>.</param>
/// <param name="Detail">A message for the user, empty when it loaded.</param>
/// <param name="Plugin">The instance, when it loaded.</param>
public sealed record PluginLoadResult(
    string Path,
    string Name,
    PluginRejection Rejection,
    string Detail,
    IPlugin? Plugin)
{
    /// <summary>Gets whether the plugin loaded.</summary>
    public bool Loaded => Rejection == PluginRejection.None && Plugin is not null;
}

/// <summary>
/// Loads plugins into isolated contexts (P2-T15, ADR-0012).
/// </summary>
/// <remarks>
/// <para>
/// Each plugin gets its own <see cref="AssemblyLoadContext"/> so that its dependencies cannot
/// collide with another plugin's or with the host's. Without isolation, the first plugin to load a
/// given library decides the version everyone gets, and the second plugin fails with a
/// <see cref="MissingMethodException"/> that names neither plugin.
/// </para>
/// <para>
/// <b>The shared-type set is the crux.</b> A plugin implements <see cref="IPlugin"/>, and for the
/// host to see that it does, both must mean the <i>same</i> <see cref="IPlugin"/> — the same
/// <see cref="Type"/> from the same loaded assembly. If a plugin's own copy of
/// <c>OpenMCAD.Api.dll</c> were loaded into its context, its interface would be a different type
/// with the same name, the cast would fail, and the message would be the famously unhelpful
/// "unable to cast object of type IPlugin to type IPlugin". So the API assembly, and only it, is
/// deliberately resolved from the host.
/// </para>
/// <para>
/// Nothing here trusts a plugin. A faulted one is reported and skipped rather than allowed to stop
/// the application from starting: a broken third-party assembly must not make the product
/// unlaunchable, because the user's only remedy would be to find and delete a file they may not
/// know about.
/// </para>
/// <para>
/// <b>A loaded plugin's file stays locked until the process exits</b>, because the assembly is
/// loaded from its path. Loading from a byte stream instead would leave the file free and allow a
/// plugin to be replaced or uninstalled while the application runs, at the cost of
/// <see cref="Assembly.Location"/> being empty — and a plugin that locates its own icons or
/// configuration beside itself, which a CAD plugin very plausibly does, would then break in a way
/// its author could not diagnose. Requiring a restart to update a plugin is the smaller cost, and
/// it is the behaviour users already expect.
/// </para>
/// </remarks>
public sealed class PluginLoader
{
    private readonly ILogger _logger;
    private readonly List<PluginContext> _contexts = [];

    /// <summary>Creates the loader.</summary>
    /// <param name="logger">Where to record what loaded and what did not.</param>
    public PluginLoader(ILogger<PluginLoader>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Gets the API version presented to plugins.</summary>
    public static Version HostApiVersion
        => new(Api.ApiVersion.Major, Api.ApiVersion.Minor, Api.ApiVersion.Patch);

    /// <summary>
    /// Loads every plugin assembly in a directory.
    /// </summary>
    /// <param name="directory">Where to look. A missing directory yields nothing.</param>
    /// <param name="host">What to hand each plugin.</param>
    /// <returns>One result per candidate, whether it loaded or not.</returns>
    /// <remarks>
    /// Ordered by file name so that a set of plugins initialises in the same sequence on every
    /// run. Directory enumeration order is not specified, and a load order that varies between
    /// machines turns an interaction between two plugins into a bug that reproduces for one user
    /// and not another.
    /// </remarks>
    public IReadOnlyList<PluginLoadResult> LoadFrom(string directory, IPluginHost host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(host);

        if (!Directory.Exists(directory))
        {
            _logger.LogDebug("No plugin directory at {Directory}", directory);
            return [];
        }

        List<PluginLoadResult> results = [];

        foreach (string path in Directory.EnumerateFiles(directory, "*.dll")
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            results.Add(Load(path, host));
        }

        return results;
    }

    /// <summary>
    /// Loads one plugin assembly.
    /// </summary>
    /// <param name="path">The assembly to load.</param>
    /// <param name="host">What to hand it.</param>
    /// <returns>What happened.</returns>
    public PluginLoadResult Load(string path, IPluginHost host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(host);

        PluginContext context = new(path);
        Assembly assembly;

        try
        {
            assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is BadImageFormatException or FileLoadException
            or IOException)
        {
            // A directory of plugins will contain support libraries too, and they are not
            // failures. Reported at debug rather than as a warning the user cannot act on.
            _logger.LogDebug("{Path} is not a loadable assembly: {Reason}", path, exception.Message);
            context.Unload();

            return Rejected(path, PluginRejection.NotAnAssembly, exception.Message);
        }

        List<Type> candidates;
        try
        {
            candidates = [.. assembly.GetTypes().Where(IsPluginType)];
        }
        catch (ReflectionTypeLoadException exception)
        {
            // A plugin whose dependencies are missing cannot be inspected. Naming the assembly is
            // the only useful thing to say, since the loader exception itself names types nobody
            // outside the plugin recognises.
            context.Unload();
            return Rejected(path, PluginRejection.NotAnAssembly, exception.Message);
        }

        if (candidates.Count == 0)
        {
            context.Unload();
            return Rejected(path, PluginRejection.NoEntryPoint, "It implements no IPlugin.");
        }

        if (candidates.Count > 1)
        {
            context.Unload();

            return Rejected(
                path,
                PluginRejection.AmbiguousEntryPoint,
                $"It implements IPlugin {candidates.Count} times ("
                + string.Join(", ", candidates.Select(t => t.FullName))
                + "). Exactly one entry point per assembly.");
        }

        IPlugin plugin;
        try
        {
            plugin = (IPlugin)Activator.CreateInstance(candidates[0])!;
        }
        catch (Exception exception) when (exception is MissingMethodException
            or TargetInvocationException or MemberAccessException or InvalidCastException)
        {
            context.Unload();
            return Rejected(path, PluginRejection.Faulted, $"Constructing it threw: {exception.Message}");
        }

        if (!Api.ApiVersion.Supports(plugin.ApiVersion))
        {
            context.Unload();

            // Refused before Initialize, so the plugin never runs against a surface it was not
            // built for. The alternative is a MissingMethodException at some later moment, blamed
            // on whatever the user happened to be doing at the time.
            return Rejected(
                path,
                PluginRejection.IncompatibleApiVersion,
                $"'{plugin.Name}' was built against API {plugin.ApiVersion} and this build "
                + $"presents {Api.ApiVersion.Value}.",
                plugin.Name);
        }

        try
        {
            // Named first, so anything it contributes is attributed to it rather than to whichever
            // plugin was initialised before.
            if (host is PluginHost named)
            {
                named.BeginPlugin(plugin.Name);
            }

            plugin.Initialize(host);
        }
        catch (Exception exception)
        {
            // Deliberately catching everything. A third-party assembly must not be able to stop
            // the application from starting -- the user's only remedy would be to find and delete
            // a file they may not know exists.
            _logger.LogError(exception, "Plugin '{Plugin}' threw while initialising", plugin.Name);
            context.Unload();

            return Rejected(
                path, PluginRejection.Faulted, $"Initialising it threw: {exception.Message}", plugin.Name);
        }

        _contexts.Add(context);
        _logger.LogInformation("Loaded plugin '{Plugin}' {Version}", plugin.Name, plugin.Version);

        return new PluginLoadResult(path, plugin.Name, PluginRejection.None, string.Empty, plugin);
    }

    /// <summary>Whether a type is a concrete, constructible plugin entry point.</summary>
    private static bool IsPluginType(Type type)
        => typeof(IPlugin).IsAssignableFrom(type)
            && type is { IsAbstract: false, IsInterface: false }
            && type.GetConstructor(Type.EmptyTypes) is not null;

    private PluginLoadResult Rejected(
        string path, PluginRejection rejection, string detail, string name = "")
    {
        _logger.LogWarning("Not loading {Path}: {Reason} — {Detail}", path, rejection, detail);
        return new PluginLoadResult(path, name, rejection, detail, null);
    }

    /// <summary>
    /// One plugin's isolation boundary.
    /// </summary>
    /// <remarks>
    /// Collectible, so a plugin that fails to load does not leave its assemblies resident for the
    /// life of the process. That matters more than it sounds: a user iterating on a plugin they are
    /// writing would otherwise accumulate a copy per attempt.
    /// </remarks>
    private sealed class PluginContext(string path)
        : AssemblyLoadContext($"plugin:{Path.GetFileNameWithoutExtension(path)}", isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);

        /// <inheritdoc />
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // The contract assemblies come from the host, always. A plugin's own copy would define
            // a different IPlugin with the same name, the cast would fail, and the error would be
            // "unable to cast object of type IPlugin to type IPlugin" -- which has cost more
            // developer hours across the .NET ecosystem than almost any other message.
            if (IsSharedContract(assemblyName))
            {
                return null;
            }

            string? resolved = _resolver.ResolveAssemblyToPath(assemblyName);
            return resolved is null ? null : LoadFromAssemblyPath(resolved);
        }

        /// <inheritdoc />
        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            string? resolved = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return resolved is null ? 0 : LoadUnmanagedDllFromPath(resolved);
        }

        /// <summary>
        /// Whether an assembly must be the host's copy rather than the plugin's.
        /// </summary>
        /// <remarks>
        /// Deliberately narrow. Sharing the whole of OpenMCAD would defeat the isolation and let
        /// an internal refactor break plugins; sharing nothing would break the cast. Only the
        /// public contract and the logging abstractions it exposes are shared, which is exactly
        /// what appears in the API surface baseline.
        /// </remarks>
        private static bool IsSharedContract(AssemblyName assemblyName)
            => assemblyName.Name is "OpenMCAD.Api"
                or "Microsoft.Extensions.Logging.Abstractions";
    }
}
