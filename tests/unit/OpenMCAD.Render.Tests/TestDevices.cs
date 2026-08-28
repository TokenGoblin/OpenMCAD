using OpenMCAD.Render.Direct3D12;

using Xunit;

[assembly: AssemblyFixture(typeof(OpenMCAD.Render.Tests.DrainFinalizers))]

namespace OpenMCAD.Render.Tests;

/// <summary>
/// How the tests in this assembly ask for a device.
/// </summary>
/// <remarks>
/// One place, because every class here wants the same thing and eleven copies of it drifted
/// apart the moment one of them needed to differ.
/// </remarks>
internal static class TestDevices
{
    /// <summary>The software rasteriser, which is all a build machine has.</summary>
    public static RenderDeviceOptions Software
        => new(EnableDebugLayer: WantsDebugLayer, ForceSoftware: true);

    /// <summary>Whether to attach the debug layer to the devices these tests create.</summary>
    /// <remarks>
    /// <para>
    /// On a developer's machine, yes: it catches genuine misuse of the API, which is most of what
    /// can go wrong in a renderer and none of which shows up as a wrong picture until much later.
    /// </para>
    /// <para>
    /// On a build machine, no. Fourteen classes here each create and destroy a WARP device, and
    /// this assembly already carries a note about four of them concurrently crashing the test host
    /// one run in three — fixed then by running collections one at a time. On GitHub's runners the
    /// host was dying every run instead: every test passing, then the process failing fast on the
    /// way out, which is what an exception on the finalizer thread looks like from outside. The
    /// debug layer holds its own references to every object a device made and reports on them at
    /// teardown, so it is the part of that arrangement most likely to still be holding something
    /// when the D3D12 runtime starts unloading.
    /// </para>
    /// <para>
    /// Behaviour that differs between a laptop and a build machine is a wart, and this is a real
    /// one: the layer's validation is exactly what a pipeline ought to be running. It is a
    /// deliberate trade against a suite that fails every time and therefore tells nobody anything.
    /// If the shared-device refactor lands — one device for the assembly rather than fourteen —
    /// this should go with it.
    /// </para>
    /// </remarks>
    private static bool WantsDebugLayer
        => string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"));
}

/// <summary>
/// Runs every finalizer while the runtime is still alive.
/// </summary>
/// <remarks>
/// <para>
/// Not a fix on its own: a diagnostic. A COM object left to its finalizer is released whenever the
/// garbage collector gets to it, which at process exit can be after the D3D12 runtime has begun
/// unloading — and an exception on the finalizer thread does not fail a test, it fails the process,
/// with an exit code and nothing else. That is precisely what the build machine was showing.
/// </para>
/// <para>
/// Draining here means anything of that kind throws while xunit is still running and can say which
/// object and where, rather than after everything that could report it has gone. Twice, because
/// finalizing one object can make another unreachable.
/// </para>
/// </remarks>
public sealed class DrainFinalizers : IDisposable
{
    /// <inheritdoc/>
    public void Dispose()
    {
        for (int pass = 0; pass < 2; ++pass)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        GC.Collect();
    }
}
