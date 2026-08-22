using System.Globalization;

namespace OpenMCAD.Kernel;

/// <summary>
/// An opaque, non-owning reference to a shape living inside the geometry kernel.
/// </summary>
/// <param name="Tag">
/// The kernel-side handle. Zero is never a valid shape. The low bits index a slot in the kernel's
/// handle table and the high bits hold a generation counter, so a tag left over from a released
/// shape is detected rather than silently aliasing whatever now occupies that slot.
/// </param>
/// <remarks>
/// <para>
/// <b>This type conveys no ownership.</b> A <see cref="KernelShape"/> is valid only while some
/// <see cref="KernelShapeHandle"/> holding the same tag is alive, and only within the kernel
/// instance that issued it. Passing a shape from one kernel instance to another is a bug that the
/// kernel will reject rather than misinterpret.
/// </para>
/// <para>
/// Nothing above <c>OpenMCAD.Kernel</c> may interpret the tag. It is not an index, not a pointer,
/// and not stable across a save and reload. Anything that must survive a rebuild is referred to by
/// a <c>PersistentName</c> instead (ADR-0005), which is a different mechanism entirely and lives in
/// <c>OpenMCAD.Core</c>.
/// </para>
/// <para>
/// The representation is deliberately identical to how Parasolid identifies entities — an integer
/// tag in a side table rather than a pointer. That keeps the swap path in ADR-0002 a matter of
/// reimplementing operations rather than redesigning the boundary.
/// </para>
/// </remarks>
public readonly record struct KernelShape(ulong Tag)
{
    /// <summary>Gets the reference that denotes no shape.</summary>
    public static KernelShape None => default;

    /// <summary>Gets a value indicating whether this reference denotes a shape.</summary>
    public bool IsValid => Tag != 0;

    /// <inheritdoc />
    public override string ToString() => Tag == 0
        ? "shape(none)"
        : string.Create(CultureInfo.InvariantCulture, $"shape(0x{Tag:X})");
}
