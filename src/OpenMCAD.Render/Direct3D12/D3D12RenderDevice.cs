using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using SharpGen.Runtime;

using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;

namespace OpenMCAD.Render.Direct3D12;

/// <summary>
/// <see cref="IRenderDevice"/> on Direct3D 12, through Vortice (ADR-0008, P2-T01).
/// </summary>
/// <remarks>
/// <para>
/// Owns the device, the direct queue, and the fence that everything else paces against. Descriptor
/// heaps and upload rings are created by callers that need them, because their sizes depend on the
/// scene and one global choice would be wrong for both a single part and a five-thousand-component
/// assembly.
/// </para>
/// <para>
/// Creating this does not require a window. That is deliberate: the whole of P2-T01 can then be
/// tested on a build agent with no display, against the WARP software adapter, which is the only
/// way any of this plumbing gets covered before there is something to look at.
/// </para>
/// </remarks>
public sealed class D3D12RenderDevice : IRenderDevice
{
    private readonly ILogger _logger;
    private readonly IDXGIFactory6 _factory;
    private readonly ID3D12Device _device;
    private readonly ID3D12CommandQueue _queue;
    private readonly ID3D12Fence _fence;
    private readonly AutoResetEvent _fenceReached = new(false);

    private ulong _lastSignalled;
    private bool _disposed;

    /// <summary>Creates the device.</summary>
    /// <param name="options">How to choose an adapter and whether to enable validation.</param>
    /// <param name="logger">Where to record which adapter was chosen and why.</param>
    /// <exception cref="RenderDeviceUnavailableException">No usable adapter was found.</exception>
    public D3D12RenderDevice(RenderDeviceOptions options = default, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;

        // Before the factory, or it is too late: the debug layer has to be enabled before the
        // device exists. It costs real performance, so it is opt-in rather than
        // debug-build-by-default -- a developer profiling a scene should not be measuring it
        // without having asked.
        if (options.EnableDebugLayer
            && D3D12.D3D12GetDebugInterface(out ID3D12Debug? debug).Success
            && debug is not null)
        {
            using (debug)
            {
                debug.EnableDebugLayer();
                _logger.LogInformation("D3D12 debug layer enabled");
            }
        }

        // Factory6 rather than 4: EnumAdapterByGpuPreference lives there, and asking for the
        // high-performance adapter is the difference between the discrete GPU and the integrated
        // one on any laptop.
        _factory = DXGI.CreateDXGIFactory1<IDXGIFactory6>();

        (_device, IDXGIAdapter1? adapter, FeatureLevel level) = CreateDevice(options);

        using (adapter)
        {
            AdapterDescription1 description = adapter!.Description1;

            Info = new RenderDeviceInfo(
                description.Description,
                (description.Flags & AdapterFlags.Software) != 0,
                (long)description.DedicatedVideoMemory,
                level.ToString());
        }

        _device.Name = "OpenMCAD device";

        _queue = _device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
        _queue.Name = "OpenMCAD direct queue";

        _fence = _device.CreateFence(0);
        _fence.Name = "OpenMCAD frame fence";

        _logger.LogInformation("Render device: {Device}", Info);

        if (Info.IsSoftware)
        {
            // Worth saying plainly. WARP is correct and roughly two orders of magnitude too slow
            // for PLAN.md 7, and a user who does not know they are on it will report the
            // application as broken rather than the driver as missing.
            _logger.LogWarning(
                "No hardware adapter was available, so rendering falls back to the WARP software "
                + "rasteriser. This is correct but far too slow for interactive use.");
        }
    }

    /// <inheritdoc />
    public RenderDeviceInfo Info { get; }

    /// <summary>Gets the underlying device, for the parts of the renderer that are D3D12-specific.</summary>
    /// <remarks>
    /// Exposed within the render layer rather than through <see cref="IRenderDevice"/>. Descriptor
    /// heaps and upload rings need it and are themselves D3D12 concepts; putting it on the
    /// interface would make the abstraction pointless.
    /// </remarks>
    public ID3D12Device Device => _device;

    /// <summary>Gets the direct command queue.</summary>
    public ID3D12CommandQueue Queue => _queue;

    /// <inheritdoc />
    public IGpuBuffer CreateStaticBuffer(ReadOnlySpan<byte> data, GpuBufferKind kind, string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (data.IsEmpty)
        {
            throw new ArgumentException(
                "A zero-length buffer cannot be created. An empty mesh should not reach the "
                + "renderer at all.",
                nameof(data));
        }

        // An upload-heap buffer rather than a default-heap one with a copy. It is the slower place
        // for the GPU to read from, and it is the right choice until there is a copy queue and a
        // frame loop to schedule the transition on (P2-T02). Recorded here so it is a decision
        // rather than an oversight.
        ID3D12Resource resource = _device.CreateCommittedResource(
            HeapType.Upload,
            HeapFlags.None,
            ResourceDescription.Buffer((ulong)data.Length),
            ResourceStates.GenericRead);

        resource.Name = name;
        resource.SetData(data);

        return new D3D12Buffer(resource, kind, data.Length, name);
    }

    /// <summary>
    /// Gets the resource behind a buffer this device created.
    /// </summary>
    /// <param name="buffer">A buffer from <see cref="CreateStaticBuffer"/>.</param>
    /// <returns>The underlying resource.</returns>
    /// <exception cref="ArgumentException">The buffer came from another implementation.</exception>
    /// <remarks>
    /// <see cref="IGpuBuffer"/> is deliberately opaque, because ADR-0008 keeps D3D12 types out of
    /// the render abstraction. The passes in this namespace are the D3D12 implementation, so they
    /// are entitled to look inside — but only through here, so that the cast exists once and is
    /// checked, rather than being repeated at every call site.
    /// </remarks>
    internal static ID3D12Resource ResourceOf(IGpuBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        return buffer is D3D12Buffer d3d12
            ? d3d12.Resource
            : throw new ArgumentException(
                $"'{buffer.Name}' is a {buffer.GetType().Name}, which this device did not create.",
                nameof(buffer));
    }

    /// <inheritdoc />
    public void WaitForIdle()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ulong target = ++_lastSignalled;
        _queue.Signal(_fence, target).CheckError();

        if (_fence.CompletedValue >= target)
        {
            return;
        }

        _fence.SetEventOnCompletion(target, _fenceReached).CheckError();
        _fenceReached.WaitOne();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Before releasing anything: destroying a resource the GPU is still reading is a
        // use-after-free that surfaces as a device-removed error somewhere else entirely.
        try
        {
            WaitForIdle();
        }
        catch (SharpGenException exception)
        {
            _logger.LogWarning(exception, "The GPU did not go idle cleanly during shutdown");
        }

        _disposed = true;

        _fence.Dispose();
        _fenceReached.Dispose();
        _queue.Dispose();
        _device.Dispose();
        _factory.Dispose();
    }

    /// <summary>Finds an adapter and creates a device on it.</summary>
    private (ID3D12Device Device, IDXGIAdapter1? Adapter, FeatureLevel Level) CreateDevice(
        RenderDeviceOptions options)
    {
        FeatureLevel minimum = FeatureLevel.Level_11_0;

        if (!options.ForceSoftware)
        {
            // Highest-performance order rather than enumeration order, which on a laptop is the
            // difference between the discrete GPU and the integrated one.
            for (uint index = 0;
                _factory.EnumAdapterByGpuPreference(
                    index, GpuPreference.HighPerformance, out IDXGIAdapter1? adapter).Success;
                index++)
            {
                if ((adapter!.Description1.Flags & AdapterFlags.Software) != 0)
                {
                    // Enumerated as hardware would be, and rejected here so that the software path
                    // is only ever taken deliberately.
                    adapter.Dispose();
                    continue;
                }

                if (D3D12.D3D12CreateDevice(adapter, minimum, out ID3D12Device? device).Success)
                {
                    return (device!, adapter, minimum);
                }

                _logger.LogDebug(
                    "Adapter {Adapter} does not support feature level {Level}",
                    adapter.Description1.Description,
                    minimum);

                adapter.Dispose();
            }
        }

        // WARP. Always present on Windows, so this is a fallback rather than a failure -- but the
        // constructor logs a warning, because it is not a fallback anyone should be running on.
        IDXGIAdapter1 warp = _factory.EnumWarpAdapter<IDXGIAdapter1>();

        if (D3D12.D3D12CreateDevice(warp, minimum, out ID3D12Device? software).Success)
        {
            return (software!, warp, minimum);
        }

        warp.Dispose();

        throw new RenderDeviceUnavailableException(
            "No Direct3D 12 adapter could be created, including the WARP software rasteriser. "
            + "This usually means the graphics driver is too old or the D3D12 runtime is missing.");
    }

    /// <summary>A buffer in GPU memory.</summary>
    private sealed class D3D12Buffer(
        ID3D12Resource resource, GpuBufferKind kind, int byteLength, string name) : IGpuBuffer
    {
        private bool _disposed;

        /// <inheritdoc />
        public GpuBufferKind Kind => kind;

        /// <inheritdoc />
        public int ByteLength => byteLength;

        /// <inheritdoc />
        public string Name => name;

        /// <summary>Gets the underlying resource.</summary>
        public ID3D12Resource Resource => resource;

        /// <summary>Gets the address a view binds to.</summary>
        public ulong GpuAddress => resource.GPUVirtualAddress;

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            resource.Dispose();
        }
    }
}

/// <summary>How to create the render device.</summary>
/// <param name="EnableDebugLayer">
/// Whether to turn on the D3D12 validation layer. It catches misuse that otherwise appears as a
/// corrupt frame or a removed device, and it costs enough performance that it must be asked for.
/// </param>
/// <param name="ForceSoftware">
/// Whether to skip hardware adapters entirely. For tests and for reproducing a report from a
/// machine without a usable GPU.
/// </param>
public readonly record struct RenderDeviceOptions(
    bool EnableDebugLayer = false,
    bool ForceSoftware = false);

/// <summary>Thrown when no Direct3D 12 device can be created at all.</summary>
public sealed class RenderDeviceUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    public RenderDeviceUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public RenderDeviceUnavailableException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The underlying failure.</param>
    public RenderDeviceUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
