using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Render.Direct3D12;

using SharpGen.Runtime;

using Vortice.Direct3D12;
using Vortice.Mathematics;

using Xunit;

namespace OpenMCAD.Render.Tests;

/// <summary>
/// Losing the graphics device, and coming back from it (P2-T02).
/// </summary>
/// <remarks>
/// <para>
/// Device loss is the failure most applications handle by assertion rather than by test: it
/// happens on somebody else's machine, during a driver update, months after the code shipped.
/// <c>ID3D12Device5.RemoveDevice</c> makes it reproducible, so the recovery path can be exercised
/// on demand rather than reasoned about.
/// </para>
/// <para>
/// What is verified here is the renderer's half: that a lost device is detectable, that submitting
/// to one is not, and that everything can be released and rebuilt on a fresh device.
/// </para>
/// <para>
/// <b>Two things are deliberately not tested.</b> That the camera survives recovery needs a
/// <see cref="Direct3D12.ViewportRenderer"/>, which needs a swapchain, which needs a window; and
/// the shell's attempt counting lives in a WPF message loop. Neither is reachable from a test
/// host. A first attempt at the camera test asserted that a local variable equalled the object it
/// had just been assigned from, which is worse than no test, so it was removed rather than left to
/// look like coverage.
/// </para>
/// </remarks>
public sealed class DeviceLossTests
{
    private const int Size = 64;


    /// <summary>Kills a device the way a driver update would.</summary>
    /// <returns>Whether the device could be removed on this machine.</returns>
    private static bool ForceRemoval(D3D12RenderDevice device)
    {
        // ID3D12Device5 is a Windows 10 1809 interface. Everything this project targets has it,
        // but querying rather than casting means a machine without it skips rather than fails.
        using ID3D12Device5? removable = device.Device.QueryInterfaceOrNull<ID3D12Device5>();

        if (removable is null)
        {
            return false;
        }

        removable.RemoveDevice();
        return true;
    }

    [Fact]
    public void ARemovedDeviceReportsAReason()
    {
        using D3D12RenderDevice device = new(TestDevices.Software);

        device.Device.DeviceRemovedReason.Success.Should().BeTrue("the device starts healthy");

        if (!ForceRemoval(device))
        {
            Assert.Skip("this device does not expose ID3D12Device5");
            return;
        }

        // The reason is what goes in the log, and it is the only thing distinguishing a driver
        // update from a hang the renderer caused itself.
        device.Device.DeviceRemovedReason.Failure.Should().BeTrue();
    }

    [Fact]
    public void WorkSubmittedToARemovedDeviceSucceedsWithoutDoingAnything()
    {
        using D3D12RenderDevice device = new(TestDevices.Software);
        using OffscreenSurface surface = new(device, Size, Size);

        // Healthy first, so anything observed below is attributable to the removal rather than to
        // the surface never having worked.
        surface.Render(new Color4(0.2f, 0.3f, 0.4f, 1.0f), _ => { });
        surface.At(1, 1).R.Should().BeGreaterThan(0);

        if (!ForceRemoval(device))
        {
            Assert.Skip("this device does not expose ID3D12Device5");
            return;
        }

        // This was written the other way round, expecting a throw, and it failed -- which is the
        // more useful fact. Recording, executing and waiting on a fence all report success on a
        // removed device: the work never happens, and the fence claims completion because a
        // removed device signals every fence to its maximum.
        //
        // A viewport notices anyway, because presenting does report it. Anything rendering
        // off-screen has no present to catch it and would write an empty image and call it a
        // result, so those paths have to ask explicitly.
        Action act = () => surface.Render(new Color4(0.2f, 0.3f, 0.4f, 1.0f), _ => { });

        act.Should().NotThrow("submission is silent on a removed device, which is the hazard");
        device.IsRemoved.Should().BeTrue("asking is the only way to find out");
    }

    [Fact]
    public void EverythingCanBeRebuiltOnAFreshDeviceAfterALoss()
    {
        // The recovery itself: release everything that belonged to the dead device, build it again
        // on a new one, and draw. If any resource outlived its device this would fail here rather
        // than months later on a machine nobody can reach.
        D3D12RenderDevice? first = null;
        OffscreenSurface? firstSurface = null;

        try
        {
            first = new D3D12RenderDevice(TestDevices.Software);
            firstSurface = new OffscreenSurface(first, Size, Size);
            firstSurface.Render(new Color4(0.2f, 0.3f, 0.4f, 1.0f), _ => { });

            if (!ForceRemoval(first))
            {
                Assert.Skip("this device does not expose ID3D12Device5");
                return;
            }
        }
        finally
        {
            // Disposing resources belonging to a removed device is itself part of the path: the
            // renderer waits for the GPU before releasing, and that wait cannot succeed.
            try
            {
                firstSurface?.Dispose();
            }
            catch (SharpGenException)
            {
                // Expected. The device is gone; there is nothing to wait for.
            }

            try
            {
                first?.Dispose();
            }
            catch (SharpGenException)
            {
            }
        }

        using D3D12RenderDevice second = new(TestDevices.Software);
        using OffscreenSurface secondSurface = new(second, Size, Size);
        using FacePass faces = new(
            second.Device, OffscreenSurface.ColourFormat, optimiseShaders: false);

        SnapshotBuilder builder = new();
        builder.Add(EdgePassTestsGeometry.SolidBox(1.0));
        DisplaySnapshot snapshot = builder.Build(1);

        using SceneGeometry scene = SceneGeometry.Upload(second, snapshot);

        Camera camera = new() { AspectRatio = 1.0 };
        camera.LookFrom(StandardView.Isometric);
        camera.ZoomToFit(snapshot.Bounds);

        secondSurface.SetConstants(FrameConstantsFor(camera, snapshot, secondSurface));

        secondSurface.Render(
            new Color4(0.05f, 0.05f, 0.08f, 1.0f),
            commands => faces.Draw(commands, scene, secondSurface.ConstantBufferAddress));

        secondSurface.Centre().R.Should().BeGreaterThan(
            20, "the cube should be drawn on the rebuilt device");
    }

    private static FrameConstants FrameConstantsFor(
        Camera camera, DisplaySnapshot snapshot, OffscreenSurface surface)
    {
        Mat4d projection = camera.ProjectionMatrix(snapshot.Bounds);
        Vec3d origin = snapshot.Origin;

        Mat4d view = Mat4d.LookAt(
            camera.Position - origin, camera.Target - origin, camera.Up);

        Mat4d combined = projection * view;

        return new FrameConstants
        {
            ViewProjection = new System.Numerics.Matrix4x4(
                (float)combined.M11, (float)combined.M12, (float)combined.M13, (float)combined.M14,
                (float)combined.M21, (float)combined.M22, (float)combined.M23, (float)combined.M24,
                (float)combined.M31, (float)combined.M32, (float)combined.M33, (float)combined.M34,
                (float)combined.M41, (float)combined.M42, (float)combined.M43, (float)combined.M44),

            CameraPosition = new System.Numerics.Vector3(
                (float)(camera.Position - origin).X,
                (float)(camera.Position - origin).Y,
                (float)(camera.Position - origin).Z),

            LightDirection = new System.Numerics.Vector3(
                (float)FacePass.KeyLightDirection(camera).X,
                (float)FacePass.KeyLightDirection(camera).Y,
                (float)FacePass.KeyLightDirection(camera).Z),

            ViewportSize = new System.Numerics.Vector2(surface.Width, surface.Height),
        };
    }
}
