using System.Runtime.InteropServices;

using Vortice.Direct3D12;

namespace OpenMCAD.Render.Direct3D12;

/// <summary>How highlighted entities are coloured.</summary>
/// <param name="PreSelected">The colour an entity under the cursor is tinted towards.</param>
/// <param name="Selected">The colour a selected entity is tinted towards.</param>
/// <param name="Error">The colour an entity implicated in a failure is tinted towards.</param>
/// <remarks>
/// The alpha of each is <b>tint strength</b>, not opacity — how far a lit surface is pushed
/// towards the colour. Faces keep their shading and take the hue; edges take the colour outright,
/// having no shading to protect.
/// </remarks>
public readonly record struct HighlightStyle(
    Vortice.Mathematics.Color4 PreSelected,
    Vortice.Mathematics.Color4 Selected,
    Vortice.Mathematics.Color4 Error)
{
    /// <summary>Gets the default palette.</summary>
    /// <remarks>
    /// <para>
    /// Pre-selection is a pale, weak cyan; selection is a stronger, deeper blue; error is red.
    /// Blue is the CAD convention for selection and, more usefully, it is the hue furthest from
    /// the warm grey of unselected metal, so the two never look like the same surface under
    /// different lighting.
    /// </para>
    /// <para>
    /// The tints are deliberately partial. At full strength a selected face becomes a flat
    /// silhouette and stops reading as a shape at all, which matters most on exactly the curved
    /// surfaces a user is most likely to be inspecting.
    /// </para>
    /// </remarks>
    public static HighlightStyle Default => new(
        new Vortice.Mathematics.Color4(0.45f, 0.75f, 0.95f, 0.35f),
        new Vortice.Mathematics.Color4(0.18f, 0.50f, 0.90f, 0.62f),
        new Vortice.Mathematics.Color4(0.90f, 0.25f, 0.20f, 0.70f));
}

/// <summary>
/// The GPU-side copy of a <see cref="HighlightTable"/> (P2-T09).
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="uint"/> per display id, read in the pixel shaders by the same id the ID pass
/// writes. Re-uploaded only when the table's version changes, which for hover means once per
/// entity the cursor crosses rather than once per frame.
/// </para>
/// <para>
/// <b>The buffer is never freed and re-created for a smaller table.</b> It grows and is then
/// reused, because hover changes the contents constantly and allocating a committed resource per
/// mouse move would spend more time in the driver than the highlight is worth. A table shorter
/// than the buffer simply leaves the tail stale, and the shader never reads past the length it is
/// told about.
/// </para>
/// </remarks>
public sealed class HighlightBuffer : IDisposable
{
    private readonly ID3D12Device _device;

    private ID3D12Resource? _buffer;
    private int _capacity;
    private long _uploaded = -1;
    private bool _disposed;

    /// <summary>Creates the buffer.</summary>
    /// <param name="device">The device to allocate on.</param>
    public HighlightBuffer(ID3D12Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
    }

    /// <summary>Gets how many entries the GPU buffer currently holds.</summary>
    public int Capacity => _capacity;

    /// <summary>Gets how many entries the shader should consider live.</summary>
    public int Length { get; private set; }

    /// <summary>Gets where the states live.</summary>
    /// <remarks>
    /// A buffer is allocated on first use and kept, so this is never a null address even when
    /// nothing is highlighted. A root descriptor bound to zero is a loaded gun: the shader is
    /// supposed not to dereference it, and the first mistake that does is an access violation
    /// inside the driver rather than a readable error. One kilobyte avoids the whole category.
    /// </remarks>
    public ulong Address => _buffer?.GPUVirtualAddress ?? 0;

    /// <summary>
    /// Uploads a table if it has changed since the last call.
    /// </summary>
    /// <param name="table">What to upload.</param>
    /// <returns>Whether anything was written.</returns>
    public bool Update(HighlightTable table)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(table);

        if (table.Version == _uploaded)
        {
            return false;
        }

        _uploaded = table.Version;
        Length = table.Length;

        EnsureCapacity(System.Math.Max(table.Length, 1));

        if (table.Length == 0)
        {
            return true;
        }

        // An upload heap, written directly. The states change as the cursor moves, so this is
        // dynamic data by nature and a staged copy through a default heap would cost a barrier and
        // a copy per mouse move to save a read that happens once per pixel.
        _buffer!.SetData(MemoryMarshal.AsBytes(table.Raw));

        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _buffer?.Dispose();
        _buffer = null;
    }

    private void EnsureCapacity(int entries)
    {
        if (_buffer is not null && entries <= _capacity)
        {
            return;
        }

        // Grown in steps rather than to the exact size, so a scene whose entity count creeps
        // upwards does not reallocate on every increment.
        int wanted = System.Math.Max(entries, System.Math.Max(_capacity * 2, 1024));

        _buffer?.Dispose();

        _buffer = _device.CreateCommittedResource(
            HeapType.Upload,
            HeapFlags.None,
            ResourceDescription.Buffer((ulong)(wanted * SceneGeometry.IdStride)),
            ResourceStates.GenericRead);

        _buffer.Name = $"highlight states ({wanted})";
        _capacity = wanted;
    }
}
