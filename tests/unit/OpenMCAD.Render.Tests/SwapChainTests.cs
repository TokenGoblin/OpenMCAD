using System.Runtime.InteropServices;

using FluentAssertions;

using OpenMCAD.Render.Direct3D12;

using Xunit;

namespace OpenMCAD.Render.Tests;

/// <summary>
/// The swapchain and its resize path (P2-T02), against a real window.
/// </summary>
/// <remarks>
/// <para>
/// A swapchain needs an <c>HWND</c>, so these create a genuine off-screen Win32 window rather than
/// mocking one — there is nothing to mock, since DXGI validates the handle. The window is never
/// shown and never pumps messages, which is enough: creation, resize and present do not depend on
/// the message loop.
/// </para>
/// <para>
/// This is the part of P2-T02 that can be verified without a person looking at a screen. Whether
/// the result is the right pixels is not established here and cannot be; what is established is
/// that the chain is created flip-model, that resizing releases and rebuilds the buffers rather
/// than leaking or faulting, and that a minimised window does not destroy anything.
/// </para>
/// </remarks>
public sealed partial class SwapChainTests
{
    private static RenderDeviceOptions Software
        => new(EnableDebugLayer: true, ForceSoftware: true);

    [Fact]
    public void ASwapChainCanBeCreatedForAWindow()
    {
        using OffscreenWindow window = new(800, 600);
        using D3D12RenderDevice device = new(Software);
        using SwapChainTarget target = new(device, window.Handle, 800, 600);

        target.Width.Should().Be(800);
        target.Height.Should().Be(600);
        target.CurrentBackBufferIndex.Should().BeInRange(0, SwapChainTarget.BufferCount - 1);
    }

    [Fact]
    public void EveryBackBufferHasItsOwnRenderTargetView()
    {
        using OffscreenWindow window = new(320, 240);
        using D3D12RenderDevice device = new(Software);
        using SwapChainTarget target = new(device, window.Handle, 320, 240);

        HashSet<nuint> handles = [];
        for (int i = 0; i < SwapChainTarget.BufferCount; ++i)
        {
            target.BackBuffer(i).Should().NotBeNull();
            handles.Add(target.RenderTargetView(i).Ptr).Should().BeTrue(
                "back buffer {0} must not share a descriptor with another", i);
        }
    }

    [Fact]
    public void ResizingRebuildsTheBuffersAtTheNewSize()
    {
        using OffscreenWindow window = new(400, 300);
        using D3D12RenderDevice device = new(Software);
        using SwapChainTarget target = new(device, window.Handle, 400, 300);

        target.Resize(640, 480);

        target.Width.Should().Be(640);
        target.Height.Should().Be(480);

        // The resources are new ones; the old were released before ResizeBuffers, which DXGI
        // requires and which is the usual cause of a resize failing much later.
        for (int i = 0; i < SwapChainTarget.BufferCount; ++i)
        {
            target.BackBuffer(i).Description.Width.Should().Be(640);
            target.BackBuffer(i).Description.Height.Should().Be(480);
        }
    }

    [Fact]
    public void ResizingToTheSameSizeDoesNothing()
    {
        using OffscreenWindow window = new(400, 300);
        using D3D12RenderDevice device = new(Software);
        using SwapChainTarget target = new(device, window.Handle, 400, 300);

        // WM_SIZE arrives for moves between monitors and for restores, not only for real resizes.
        // Rebuilding the chain each time would drop frames during a window drag for no reason.
        nuint before = target.RenderTargetView(0).Ptr;
        target.Resize(400, 300);

        target.RenderTargetView(0).Ptr.Should().Be(before);
    }

    [Fact]
    public void MinimisingIsIgnoredRatherThanFailing()
    {
        using OffscreenWindow window = new(400, 300);
        using D3D12RenderDevice device = new(Software);
        using SwapChainTarget target = new(device, window.Handle, 400, 300);

        // A minimised window reports a client area of zero. DXGI refuses to resize to it, and it
        // is not an error -- the old buffers are kept until the window comes back.
        target.Resize(0, 0);

        target.Width.Should().Be(400);
        target.Height.Should().Be(300);
    }

    [Fact]
    public void PresentingRotatesTheBackBuffer()
    {
        using OffscreenWindow window = new(256, 256);
        using D3D12RenderDevice device = new(Software);
        using SwapChainTarget target = new(device, window.Handle, 256, 256);

        // Exactly BufferCount - 1 presents, which is the most a flip chain will accept before it
        // blocks waiting for the display to release a buffer. Presenting more than that hangs on
        // any machine where nothing is compositing the window -- a build agent, or a window that
        // was never shown -- and a hung test is far worse than a missing one. Two presents still
        // observe three distinct indices, which is the whole claim.
        List<int> seen = [target.CurrentBackBufferIndex];

        for (int i = 0; i < SwapChainTarget.BufferCount - 1; ++i)
        {
            target.Present(verticalSync: false).Should().BeTrue();
            seen.Add(target.CurrentBackBufferIndex);
        }

        seen.Distinct().Should().HaveCount(
            SwapChainTarget.BufferCount,
            "a flip-model chain cycles through every buffer, and drawing into the same one twice "
            + "in a row would overwrite a frame the display still owns");
    }

    [Fact]
    public void ASwapChainNeedsAWindow()
    {
        using D3D12RenderDevice device = new(Software);

        FluentActions.Invoking(() => new SwapChainTarget(device, 0, 100, 100))
            .Should().Throw<ArgumentException>();
    }

    /// <summary>A real but never-shown Win32 window, so DXGI has a handle it accepts.</summary>
    private sealed partial class OffscreenWindow : IDisposable
    {
        private const int WsOverlappedWindow = 0x00CF0000;

        private readonly nint _handle;
        private bool _disposed;

        public OffscreenWindow(int width, int height)
        {
            // The static "STATIC" window class always exists, so no class has to be registered
            // and no window procedure has to be written. Nothing here processes messages.
            _handle = CreateWindowExW(
                0, "STATIC", "OpenMCAD test", WsOverlappedWindow,
                0, 0, width, height, 0, 0, 0, 0);

            if (_handle == 0)
            {
                throw new InvalidOperationException(
                    $"Could not create a test window (Win32 error {Marshal.GetLastWin32Error()}). "
                    + "The swapchain tests need one; they cannot be meaningfully mocked.");
            }
        }

        public nint Handle => _handle;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_handle != 0)
            {
                DestroyWindow(_handle);
            }
        }

        [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial nint CreateWindowExW(
            int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height,
            nint parent, nint menu, nint instance, nint param);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DestroyWindow(nint window);
    }
}
