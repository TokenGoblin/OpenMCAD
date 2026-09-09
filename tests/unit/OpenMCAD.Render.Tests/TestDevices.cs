using OpenMCAD.Render.Direct3D12;

using SharpGen.Runtime;

using Xunit;

[assembly: AssemblyFixture(typeof(OpenMCAD.Render.Tests.RenderTestHost))]

namespace OpenMCAD.Render.Tests;

/// <summary>
/// How the tests in this assembly ask for a device.
/// </summary>
/// <remarks>
/// <para>
/// One place, because every class here wants the same thing and eleven copies of it drifted
/// apart the moment one of them needed to differ.
/// </para>
/// <para>
/// <b>One device for the assembly, not one per class.</b> Fourteen classes each creating and
/// destroying a WARP device was the arrangement that made this suite fragile: four of them running
/// concurrently crashed the test host one run in three (fixed then by serialising the collections),
/// and on a build agent the host was dying on the way out of every run — every test passing, then
/// the process failing fast, which is what an exception on the finalizer thread looks like from
/// outside. Creating a device is legal and destroying one is legal; doing it fourteen times in a
/// process is where the trouble was, and none of these tests ever wanted a *fresh* device. They
/// wanted *a* device.
/// </para>
/// <para>
/// The exceptions are the classes that are about the lifecycle itself —
/// <see cref="RenderDeviceTests"/> and <see cref="DeviceLossTests"/> — which create and destroy
/// their own on purpose. Sharing one with them would be testing nothing.
/// </para>
/// <para>
/// <b>This paragraph used to name <see cref="SwapChainTests"/> as a third, and it was wrong.</b>
/// The commit that introduced the sharing converted that class to <see cref="Required"/> while
/// saying it had not, and the mismatch matters: a swap chain built on the shared device is the
/// thing that breaks when <see cref="DeviceLossTests"/> removes a device, which is the CI failure
/// recorded in <c>docs/notes/open-decisions.md</c>. Whether it should go back to making its own is
/// the open question there; what is fixed here is only that the comment now describes the code.
/// </para>
/// </remarks>
internal static class TestDevices
{
    private static readonly Lock Gate = new();
    private static Attempt? _attempt;

    /// <summary>The software rasteriser, which is all a build machine has.</summary>
    /// <remarks>
    /// For the classes that must create their own. Everything else takes <see cref="Shared"/>.
    /// </remarks>
    public static RenderDeviceOptions Software
        => new(EnableDebugLayer: WantsDebugLayer, ForceSoftware: true);

    /// <summary>Whether to ask for the D3D12 debug layer.</summary>
    /// <remarks>
    /// <para>
    /// Not on a build agent, and this gate has now been removed once and put back. Sharing one
    /// device for the assembly was expected to make it unnecessary — the previous note said "if the
    /// shared-device refactor lands, this should go with it" — so it went, and the refactor was
    /// then merged without ever reaching CI, because sixteen commits sat unpushed.
    /// </para>
    /// <para>
    /// <b>Putting it back did not fix the failure, and that is why it is worth writing down.</b>
    /// CI #39 failed eight render tests with <c>DXGI_ERROR_DEVICE_REMOVED</c>; CI #40, with this
    /// gate restored, failed the same eight identically. So the debug layer is not the cause and
    /// was a red herring. The gate stays because this is the configuration CI was last green with
    /// and re-enabling the layer there is an untested change that deserves its own commit — not
    /// because it fixed anything. The cause is the shared device, and
    /// <c>docs/notes/open-decisions.md</c> holds the diagnosis and the two candidate remedies.
    /// </para>
    /// <para>
    /// Behaviour that differs between a laptop and a build machine is a wart, and this is a real
    /// one: the layer's validation is exactly what a pipeline ought to be running. It is a
    /// deliberate trade against a suite that fails every time and therefore tells nobody anything.
    /// Nothing here can be checked locally — this machine has no <c>D3D12SDKLayers.dll</c>, so the
    /// layer never attaches whichever way the gate is set, and the only instrument is CI itself.
    /// </para>
    /// </remarks>
    private static bool WantsDebugLayer
        => string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"));

    /// <summary>The one device this assembly shares, or null if there is none to be had.</summary>
    public static D3D12RenderDevice? Shared => Attempted().Device;

    /// <summary>Why there is no shared device, when there is not.</summary>
    public static string? Unavailable => Attempted().Reason;

    /// <summary>The shared device, for tests that have nothing to say without one.</summary>
    /// <remarks>
    /// The pass tests skip when there is no device, because "there is no GPU here" is not a defect
    /// in a pass. These assert on the device and its own allocators, so there is nothing left of
    /// them to run and failing is the honest outcome.
    /// </remarks>
    public static D3D12RenderDevice Required
        => Shared ?? throw new InvalidOperationException(Unavailable);

    /// <summary>Releases the shared device, if one was ever made.</summary>
    /// <remarks>
    /// Called by <see cref="RenderTestHost"/> rather than by a fixture, so that it happens before
    /// finalizers are drained rather than in whatever order two assembly fixtures are disposed in.
    /// </remarks>
    internal static void ReleaseShared()
    {
        lock (Gate)
        {
            _attempt?.Device?.Dispose();
            _attempt = null;
        }
    }

    private static Attempt Attempted()
    {
        lock (Gate)
        {
            return _attempt ??= Create();
        }
    }

    private static Attempt Create()
    {
        try
        {
            return new Attempt(new D3D12RenderDevice(Software), null);
        }
        catch (Exception exception)
            when (exception is RenderDeviceUnavailableException or SharpGenException)
        {
            // A build agent with no D3D12 at all. Recorded once and reported by every test that
            // asks, rather than each of them discovering it again.
            return new Attempt(null, $"No usable D3D12 device: {exception.Message}");
        }
    }

    private sealed record Attempt(D3D12RenderDevice? Device, string? Reason);
}

/// <summary>
/// Owns what outlives a test class, and takes it down in an order that is actually defined.
/// </summary>
/// <remarks>
/// <para>
/// The shared device is released here, then finalizers are drained, and that sequence is the whole
/// reason this is one fixture rather than two. Two assembly fixtures are disposed in an order xunit
/// does not promise, and draining before the device is released would drain the wrong side of the
/// problem.
/// </para>
/// <para>
/// Draining is a diagnostic, not a fix. A COM object left to its finalizer is released whenever the
/// garbage collector gets to it, which at process exit can be after the D3D12 runtime has begun
/// unloading — and an exception on the finalizer thread does not fail a test, it fails the process,
/// with an exit code and nothing else. Draining here means anything of that kind throws while xunit
/// is still running and can say which object and where. Twice, because finalizing one object can
/// make another unreachable.
/// </para>
/// </remarks>
public sealed class RenderTestHost : IDisposable
{
    /// <inheritdoc/>
    public void Dispose()
    {
        TestDevices.ReleaseShared();

        for (int pass = 0; pass < 2; ++pass)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        GC.Collect();
    }
}
