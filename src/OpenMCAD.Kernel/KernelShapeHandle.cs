using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace OpenMCAD.Kernel;

/// <summary>
/// Releases shapes on behalf of a <see cref="KernelShapeHandle"/>.
/// </summary>
/// <remarks>
/// Implemented by the kernel. Exists so a handle can be released without holding a reference to the
/// whole kernel, and so tests can observe releases.
/// </remarks>
public interface IKernelShapeReleaser
{
    /// <summary>
    /// Queues <paramref name="shape"/> for release on the kernel thread.
    /// </summary>
    /// <param name="shape">The shape to release.</param>
    /// <remarks>
    /// <b>Must not block and must not throw.</b> It is called from
    /// <see cref="System.Runtime.InteropServices.SafeHandle.ReleaseHandle"/>, which runs on the
    /// finalizer thread during a garbage collection. Blocking there stalls finalization
    /// process-wide; throwing there terminates the process. Enqueue and return.
    /// </remarks>
    void EnqueueRelease(KernelShape shape);
}

/// <summary>
/// Owns a kernel shape and releases it deterministically.
/// </summary>
/// <remarks>
/// <para>
/// P1-T07. The kernel allocates shapes in native memory that the garbage collector knows nothing
/// about; a 200-feature part can hold hundreds of megabytes the CLR believes to be a few hundred
/// bytes of managed object. Reference counting across the boundary, with an owning handle on the
/// managed side, is what keeps that from accumulating.
/// </para>
/// <para>
/// <b>Dispose these.</b> The finalizer is a backstop for leaks, not a strategy: it releases on some
/// later garbage collection, which for a rebuild that churns through intermediate bodies means peak
/// memory several times what it needs to be.
/// </para>
/// <para>
/// Release is queued onto the kernel thread rather than performed inline, because
/// <see cref="ReleaseHandle"/> can run on the finalizer thread and OCCT may only be touched from
/// one thread (ADR-0004). This is the one place where the actor boundary is crossed by the runtime
/// rather than by our own code, so it is the one place the rule has to be enforced by design rather
/// than by an assertion.
/// </para>
/// <para>
/// <b>64-bit only.</b> The tag carries a generation counter in its high bits and is stored in the
/// <see cref="SafeHandle"/> pointer field, so a 32-bit process would truncate it. OpenMCAD does not
/// target 32-bit — a CAD application cannot live in 2 GB of address space — and
/// <see cref="KernelShapeHandle"/> checks this at first use rather than corrupting handles quietly.
/// </para>
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1419:Provide a parameterless constructor that is as visible as the containing type",
    Justification =
        "CA1419 exists so the P/Invoke marshaller can construct the handle. This type is never "
        + "marshalled: tags cross the boundary as plain uint64 (ADR-0003 forbids pointers), and "
        + "the handle is constructed only by the kernel that issued the tag. A public "
        + "parameterless constructor would let callers fabricate an unowned handle, which is "
        + "strictly worse than the rule it satisfies.")]
public sealed class KernelShapeHandle : SafeHandle
{
    private readonly IKernelShapeReleaser? _releaser;

    static KernelShapeHandle()
    {
        if (IntPtr.Size != 8)
        {
            throw new PlatformNotSupportedException(
                "OpenMCAD requires a 64-bit process: kernel shape tags carry a generation counter "
                + "in their high bits and would be truncated in a 32-bit pointer.");
        }
    }

    /// <summary>Creates an owning handle for <paramref name="shape"/>.</summary>
    /// <param name="shape">The shape to take ownership of.</param>
    /// <param name="releaser">The kernel that issued it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="releaser"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="shape"/> is not valid.</exception>
    public KernelShapeHandle(KernelShape shape, IKernelShapeReleaser releaser)
        : base(IntPtr.Zero, ownsHandle: true)
    {
        ArgumentNullException.ThrowIfNull(releaser);
        if (!shape.IsValid)
        {
            throw new ArgumentException("Cannot take ownership of an invalid shape.", nameof(shape));
        }

        _releaser = releaser;
        SetHandle(ToHandle(shape.Tag));
    }

    private KernelShapeHandle()
        : base(IntPtr.Zero, ownsHandle: false)
    {
        _releaser = null;
    }

    /// <summary>
    /// Gets a handle that owns nothing, for representing "no shape" without a null reference.
    /// </summary>
    public static KernelShapeHandle Invalid { get; } = new();

    /// <inheritdoc />
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>Gets the non-owning reference to the shape this handle owns.</summary>
    /// <exception cref="ObjectDisposedException">The handle has been released.</exception>
    public KernelShape Shape
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsClosed || IsInvalid, this);
            return new KernelShape(FromHandle(handle));
        }
    }

    /// <summary>
    /// Gets the shape without throwing, for diagnostics and logging on a possibly-released handle.
    /// </summary>
    public KernelShape ShapeOrNone => IsClosed || IsInvalid
        ? KernelShape.None
        : new KernelShape(FromHandle(handle));

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        // Runs on the finalizer thread when Dispose was missed. Nothing here may block or throw.
        try
        {
            _releaser?.EnqueueRelease(new KernelShape(FromHandle(handle)));
        }
        catch
        {
            // A failed release leaks one shape. An exception escaping here terminates the process.
            // Leaking is the better outcome, and the kernel logs the failure on its own side.
            return false;
        }

        return true;
    }

    /// <inheritdoc />
    public override string ToString() => ShapeOrNone.ToString();

    // Reinterpretation, not conversion. The tag uses the whole 64-bit range because the
    // generation counter sits in the high bits, so the sign bit can legitimately be set and a
    // checked conversion would throw on a perfectly valid handle.
    private static IntPtr ToHandle(ulong tag) => new(unchecked((long)tag));

    private static ulong FromHandle(IntPtr value) => unchecked((ulong)value.ToInt64());
}
