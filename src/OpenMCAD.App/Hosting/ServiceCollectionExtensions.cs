using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenMCAD.App.Diagnostics;

namespace OpenMCAD.App.Hosting;

/// <summary>
/// Composition root helpers shared by every OpenMCAD entry point.
/// </summary>
/// <remarks>
/// <para>
/// P0-T09. The shell and the CLI compose the same services from here, so a subsystem cannot come
/// to depend on being hosted by a window. That is what keeps PLAN.md 4.1's "everything is
/// scriptable and CI-testable without a window" true as the application grows, rather than true
/// only on the day it was written.
/// </para>
/// <para>
/// Later phases register their services by adding a method here, not by reaching into the
/// container from inside a layer.
/// </para>
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the services every entry point needs: logging and application identity.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="loggerFactory">
    /// The logger factory built by <see cref="LogSetup.Create"/>. The caller owns it and must
    /// dispose it at shutdown to flush buffered writes.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IServiceCollection AddOpenMcadCore(
        this IServiceCollection services,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        services.AddSingleton(loggerFactory);
        services.AddLogging();

        // TODO(P1-T08): register KernelDispatcher and IGeometryKernel here.
        // TODO(P3-T04): register the document session and RebuildEngine here.
        // TODO(P2-T15): register the AssemblyLoadContext plugin loader here.
        return services;
    }
}
