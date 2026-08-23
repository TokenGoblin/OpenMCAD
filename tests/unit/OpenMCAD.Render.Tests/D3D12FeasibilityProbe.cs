using FluentAssertions;

using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

using Xunit;

namespace OpenMCAD.Render.Tests;

/// <summary>Establishes that a D3D12 device can be created with no display attached.</summary>
public sealed class D3D12FeasibilityProbe
{
    [Fact]
    public void AWarpDeviceCanBeCreatedHeadlessly()
    {
        using IDXGIFactory4 factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
        using IDXGIAdapter warp = factory.EnumWarpAdapter<IDXGIAdapter>();

        warp.Should().NotBeNull();

        D3D12.D3D12CreateDevice(warp, FeatureLevel.Level_11_0, out ID3D12Device? device)
            .Success.Should().BeTrue();

        using (device)
        {
            device.Should().NotBeNull();
        }
    }
}
