using FluentAssertions;

using OpenMCAD.Render.Direct3D12;

using SharpGen.Runtime;

using Vortice.Direct3D12;

using Xunit;

namespace OpenMCAD.Render.Tests;

/// <summary>
/// That the device this assembly shares is a live one every time it is asked for (P2).
/// </summary>
/// <remarks>
/// <para>
/// The property CI #39 and #40 found missing. <see cref="DeviceLossTests"/> removes a device
/// deliberately, and on a build agent's WARP that takes every device in the process with it. When
/// each class made its own that was invisible — the next test built a fresh one and never learned
/// the last had died — but a shared device outlives the removal and would then be handed to every
/// test that followed. Six <see cref="SwapChainTests"/> failures on the runner, and nothing at all
/// locally, which is why this test exists rather than a second CI round trip.
/// </para>
/// <para>
/// <b>What this can and cannot show.</b> It asserts the replacement, which is a property of
/// <c>TestDevices</c> and is the same on any machine. It cannot reproduce the runner's behaviour,
/// where removing one WARP device appears to remove them all — here the removal is done to the
/// shared device directly, so the test states the rule rather than the symptom that revealed it.
/// </para>
/// <para>
/// This class removes a device, so it is as disruptive as <see cref="DeviceLossTests"/> and is
/// exactly what the fix under test is for. Collections in this assembly run one at a time
/// (see the csproj), so it cannot run while another class holds the device it kills.
/// </para>
/// </remarks>
public sealed class SharedDeviceTests
{
    [Fact]
    public void AskingTwiceGivesTheSameDevice()
    {
        // The sharing itself, which the rest of this class must not be read as undoing.
        if (TestDevices.Shared is null)
        {
            Assert.Skip(TestDevices.Unavailable!);
        }

        TestDevices.Shared.Should().BeSameAs(TestDevices.Shared);
    }

    [Fact]
    public void ADeviceThatHasBeenRemovedIsReplacedRatherThanHandedOutAgain()
    {
        if (TestDevices.Shared is not { } original)
        {
            Assert.Skip(TestDevices.Unavailable!);
            return;
        }

        if (!ForceRemoval(original))
        {
            Assert.Skip("This machine has no ID3D12Device5, so a device cannot be removed on demand.");
            return;
        }

        original.IsRemoved.Should().BeTrue("the removal is the premise of this test");

        D3D12RenderDevice? replacement = TestDevices.Shared;

        // The whole point. A dead device handed back is the failure; a live one, even a different
        // one, is the behaviour every test in this assembly was written against.
        replacement.Should().NotBeNull("a removal must not leave the assembly with no device");
        replacement.Should().NotBeSameAs(original);
        replacement!.IsRemoved.Should().BeFalse();
    }

    /// <summary>Kills a device the way a driver update would.</summary>
    /// <param name="device">The device.</param>
    /// <returns>Whether it could be removed on this machine.</returns>
    private static bool ForceRemoval(D3D12RenderDevice device)
    {
        using ID3D12Device5? removable = device.Device.QueryInterfaceOrNull<ID3D12Device5>();

        if (removable is null)
        {
            return false;
        }

        removable.RemoveDevice();
        return true;
    }
}
