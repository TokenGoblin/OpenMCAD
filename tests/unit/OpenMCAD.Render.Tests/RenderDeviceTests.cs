using FluentAssertions;

using OpenMCAD.Render;
using OpenMCAD.Render.Direct3D12;

using Vortice.Direct3D12;

using Xunit;

namespace OpenMCAD.Render.Tests;

/// <summary>
/// The D3D12 plumbing (P2-T01), exercised against a real device.
/// </summary>
/// <remarks>
/// <para>
/// Every test here forces the WARP software adapter. That is not a compromise, it is the point:
/// WARP is present on every Windows machine including a build agent with no display and no GPU,
/// so descriptor allocation, fences and the upload ring get covered on every run rather than only
/// on a developer's desk. WARP is far too slow to render with, and none of these tests render.
/// </para>
/// <para>
/// The debug layer is on. It turns misuse that would otherwise appear as a corrupt frame or a
/// removed device into an immediate failure, which is exactly the trade a test wants.
/// </para>
/// </remarks>
public sealed class RenderDeviceTests
{
    private static RenderDeviceOptions Software
        => new(EnableDebugLayer: true, ForceSoftware: true);

    [Fact]
    public void ADeviceCanBeCreatedWithoutAWindowOrAGpu()
    {
        using D3D12RenderDevice device = new(Software);

        device.Info.IsSoftware.Should().BeTrue("the test asked for the software adapter");
        device.Info.AdapterName.Should().NotBeNullOrWhiteSpace();
        device.Info.FeatureLevel.Should().Contain("11");
    }

    [Fact]
    public void AStaticBufferHoldsWhatItWasGiven()
    {
        using D3D12RenderDevice device = new(Software);

        byte[] data = [.. Enumerable.Range(0, 1024).Select(i => (byte)i)];
        using IGpuBuffer buffer = device.CreateStaticBuffer(data, GpuBufferKind.Vertex, "test-vertices");

        buffer.ByteLength.Should().Be(1024);
        buffer.Kind.Should().Be(GpuBufferKind.Vertex);
        buffer.Name.Should().Be("test-vertices");
    }

    [Fact]
    public void AnEmptyBufferIsRejectedRatherThanCreated()
    {
        using D3D12RenderDevice device = new(Software);

        // D3D12 accepts a zero-length buffer and produces a resource nothing can be done with.
        // An empty mesh reaching the renderer is a bug upstream, and it should say so here rather
        // than draw nothing.
        FluentActions.Invoking(() => device.CreateStaticBuffer([], GpuBufferKind.Vertex, "empty"))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WaitingForIdleReturnsRatherThanHanging()
    {
        using D3D12RenderDevice device = new(Software);

        // Nothing has been submitted, so this must complete immediately. A fence wait that hangs
        // on an idle queue is the classic off-by-one in fence values, and it deadlocks shutdown.
        device.WaitForIdle();
        device.WaitForIdle();
    }

    [Fact]
    public void UsingADisposedDeviceFailsCleanly()
    {
        D3D12RenderDevice device = new(Software);
        device.Dispose();

        FluentActions.Invoking(device.WaitForIdle).Should().Throw<ObjectDisposedException>();
        FluentActions.Invoking(() => device.Dispose()).Should().NotThrow("disposal must be idempotent");
    }

    // --- Descriptor heap ------------------------------------------------------------------------

    [Fact]
    public void DescriptorsAreHandedOutAndRecycled()
    {
        using D3D12RenderDevice device = new(Software);
        using DescriptorHeapAllocator heap = new(
            device.Device, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            capacity: 4, shaderVisible: false, "test-heap");

        int a = heap.Allocate();
        int b = heap.Allocate();
        heap.Allocated.Should().Be(2);

        heap.Free(a);
        heap.Allocated.Should().Be(1);

        // Recycling is the whole point: a viewport that replaces its bodies each rebuild would
        // otherwise exhaust any heap, and a D3D12 heap cannot grow.
        heap.Allocate().Should().Be(a);
        heap.Allocated.Should().Be(2);

        b.Should().NotBe(a);
    }

    [Fact]
    public void AFullHeapSaysSoRatherThanCorrupting()
    {
        using D3D12RenderDevice device = new(Software);
        using DescriptorHeapAllocator heap = new(
            device.Device, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            capacity: 2, shaderVisible: false, "tiny-heap");

        heap.Allocate();
        heap.Allocate();

        FluentActions.Invoking(() => heap.Allocate())
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*full at 2 descriptors*");
    }

    [Fact]
    public void HandlesAreSpacedByTheDeviceIncrement()
    {
        using D3D12RenderDevice device = new(Software);
        using DescriptorHeapAllocator heap = new(
            device.Device, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            capacity: 8, shaderVisible: false, "spacing-heap");

        // The increment is a device property, not a constant, and hard-coding it is a classic way
        // to write descriptors on top of each other on a different GPU.
        ulong first = heap.CpuHandle(0).Ptr;
        ulong second = heap.CpuHandle(1).Ptr;
        ulong fourth = heap.CpuHandle(3).Ptr;

        (second - first).Should().BePositive();
        (fourth - first).Should().Be((second - first) * 3);
    }

    [Fact]
    public void ANonShaderVisibleHeapRefusesGpuHandles()
    {
        using D3D12RenderDevice device = new(Software);
        using DescriptorHeapAllocator heap = new(
            device.Device, DescriptorHeapType.RenderTargetView,
            capacity: 2, shaderVisible: false, "rtv-heap");

        // Asking D3D12 directly is a debug-layer error and returns a handle that faults when
        // bound. Refusing here names the actual mistake.
        FluentActions.Invoking(() => heap.GpuHandle(0))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*not shader-visible*");
    }

    // --- Upload ring ----------------------------------------------------------------------------

    [Fact]
    public void WhatIsWrittenToTheRingIsWhatTheGpuWouldRead()
    {
        using D3D12RenderDevice device = new(Software);
        using UploadRing ring = new(device.Device, 64 * 1024, "test-ring");

        ring.BeginFrame(1);
        Span<byte> slice = ring.Allocate(16, out int offset);

        for (int i = 0; i < slice.Length; ++i)
        {
            slice[i] = (byte)(i + 1);
        }

        // Read back through the resource rather than through the span that was just written, so
        // this proves the span really pointed into mapped GPU memory and not at a stray copy.
        Span<byte> mapped = ring.Resource.Map<byte>(0, ring.Capacity);
        try
        {
            mapped.Slice(offset, 16).ToArray().Should().Equal([.. Enumerable.Range(1, 16).Select(i => (byte)i)]);
        }
        finally
        {
            ring.Resource.Unmap(0);
        }
    }

    [Fact]
    public void AllocationsAreAlignedForConstantBuffers()
    {
        using D3D12RenderDevice device = new(Software);
        using UploadRing ring = new(device.Device, 64 * 1024, "aligned-ring");

        ring.BeginFrame(1);
        ring.Allocate(1, out int first);
        ring.Allocate(1, out int second);

        // D3D12 requires a constant-buffer view to start on a 256-byte boundary. A one-byte
        // allocation followed by another must therefore not be adjacent.
        (first % UploadRing.Alignment).Should().Be(0);
        (second % UploadRing.Alignment).Should().Be(0);
        second.Should().Be(first + UploadRing.Alignment);
    }

    [Fact]
    public void TheRingRefusesToOverwriteMemoryAFrameStillOwns()
    {
        // The failure this exists to prevent: the CPU runs ahead, so memory written for frame N is
        // still being read while N+1 is recorded. Silently reusing it makes geometry flicker under
        // load and looks like a driver fault.
        using D3D12RenderDevice device = new(Software);
        using UploadRing ring = new(device.Device, 1024, "small-ring");

        ring.BeginFrame(1);
        ring.Allocate(512, out _);
        ring.Allocate(512, out _);

        ring.BeginFrame(2);

        FluentActions.Invoking(() => ring.Allocate(256, out _))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*still in use by frames the GPU has not finished*");
    }

    [Fact]
    public void ReclaimingACompletedFrameFreesItsMemory()
    {
        using D3D12RenderDevice device = new(Software);
        using UploadRing ring = new(device.Device, 1024, "recycling-ring");

        ring.BeginFrame(1);
        ring.Allocate(512, out _);
        ring.Allocate(512, out _);
        ring.InUse.Should().Be(1024);

        // The GPU has finished frame 1, so all of it is reusable.
        ring.Reclaim(1);
        ring.InUse.Should().Be(0);

        ring.BeginFrame(2);
        ring.Allocate(512, out _);
        ring.InUse.Should().Be(512);
    }

    [Fact]
    public void ReclaimingOnlyFreesFramesTheGpuHasActuallyFinished()
    {
        using D3D12RenderDevice device = new(Software);
        using UploadRing ring = new(device.Device, 2048, "partial-ring");

        ring.BeginFrame(1);
        ring.Allocate(512, out _);

        ring.BeginFrame(2);
        ring.Allocate(512, out _);

        // Frame 1 is done; frame 2 is not. Freeing both would be the bug.
        ring.Reclaim(1);
        ring.InUse.Should().Be(512);
    }

    [Fact]
    public void AnUploadLargerThanTheRingSaysSoPlainly()
    {
        using D3D12RenderDevice device = new(Software);
        using UploadRing ring = new(device.Device, 1024, "tiny-ring");

        ring.BeginFrame(1);

        FluentActions.Invoking(() => ring.Allocate(2048, out _))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*cannot fit in a ring of 1024*");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NoAllocationEverLandsOnMemoryAFrameStillOwns(int framesInFlight)
    {
        // Written because the hand-picked cases above missed two real bugs in this allocator: a
        // ring filled exactly to capacity reported itself empty, and the head-versus-tail
        // comparison got the wrapped case wrong. Both are states that only arise from a
        // particular sequence of sizes, which is what a randomised walk finds and a worked
        // example does not.
        //
        // Fixed seed: a failure has to be reproducible to be worth anything.
        Random random = new(20260823);

        using D3D12RenderDevice device = new(Software);
        using UploadRing ring = new(device.Device, 8 * 1024, "fuzz-ring");

        // What the GPU is still reading, as this test understands it, independent of the ring.
        List<(long Frame, int Start, int Length)> live = [];
        int refused = 0;

        for (long frame = 1; frame <= 400; ++frame)
        {
            ring.BeginFrame(frame);

            for (int i = 0; i < random.Next(1, 6); ++i)
            {
                int length = random.Next(1, 900);

                Span<byte> slice;
                int offset;
                try
                {
                    slice = ring.Allocate(length, out offset);
                }
                catch (InvalidOperationException)
                {
                    // Refusing is always a correct answer -- it costs an upload, not correctness.
                    // Counted so the test can assert the ring is not simply refusing everything,
                    // which would make the overlap check vacuous.
                    refused++;
                    continue;
                }

                foreach ((long owner, int start, int owned) in live)
                {
                    bool overlaps = offset < start + owned && start < offset + length;
                    overlaps.Should().BeFalse(
                        "frame {0} was handed [{1}, {2}) which overlaps [{3}, {4}) still owned by frame {5}",
                        frame, offset, offset + length, start, start + owned, owner);
                }

                slice.Length.Should().Be(length);
                slice.Fill((byte)frame);

                live.Add((frame, offset, length));
            }

            // The GPU trails the CPU by framesInFlight, which is the whole reason this is hard.
            long completed = frame - framesInFlight;
            if (completed > 0)
            {
                ring.Reclaim(completed);
                live.RemoveAll(entry => entry.Frame <= completed);
            }
        }

        refused.Should().BeLessThan(
            400, "a ring that refuses every request would satisfy the overlap check vacuously");

        // Everything drains once the GPU catches up.
        ring.Reclaim(long.MaxValue);
        ring.InUse.Should().Be(0);
    }

    [Fact]
    public void FrameNumbersMayNotGoBackwards()
    {
        using D3D12RenderDevice device = new(Software);
        using UploadRing ring = new(device.Device, 1024, "ordered-ring");

        ring.BeginFrame(5);

        // Reclamation compares against these, so an earlier number would free memory the GPU is
        // still reading -- silently.
        FluentActions.Invoking(() => ring.BeginFrame(4))
            .Should().Throw<ArgumentOutOfRangeException>();
    }
}
