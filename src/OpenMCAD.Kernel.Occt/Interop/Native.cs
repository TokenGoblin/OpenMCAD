using System.Buffers;
using System.Collections.Immutable;
using System.Text;

namespace OpenMCAD.Kernel.Occt.Interop;

/// <summary>
/// The status codes the shim returns, mirroring <c>OpenMcadStatus</c> in <c>openmcad_occt.h</c>.
/// </summary>
/// <remarks>
/// Appended to, never renumbered: the values are part of the ABI, and a shim built from an older
/// header must keep meaning what it meant.
/// </remarks>
internal enum NativeStatus
{
    /// <summary>The call succeeded.</summary>
    Ok = 0,

    /// <summary>OCCT reported a failure.</summary>
    KernelFailure = 1,

    /// <summary>Arguments failed validation before the kernel was reached.</summary>
    InvalidInput = 2,

    /// <summary>An unknown or stale handle: the slot was recycled and the generation no longer matches.</summary>
    InvalidHandle = 3,

    /// <summary>The buffer was too small. Call again with the reported size.</summary>
    BufferTooSmall = 4,

    /// <summary>Allocation failed.</summary>
    OutOfMemory = 5,

    /// <summary>The operation is declared in the IDL but has no implementation in this build.</summary>
    NotImplemented = 6,

    /// <summary>A superseded rebuild cancelled at an operation boundary.</summary>
    Cancelled = 7,

    /// <summary>An exception escaped the operation body and was caught by the firewall.</summary>
    Internal = 100,
}

/// <summary>
/// A failed native call, carrying the shim's own account of what went wrong.
/// </summary>
/// <remarks>
/// Thrown across the interop boundary and caught by <see cref="OcctKernel"/>, which turns it into a
/// <see cref="OperationResult.Failed"/>. It is not allowed to escape the kernel: the rest of the
/// system deals in results, not exceptions, so that a failed feature is a rebuildable state rather
/// than an unwound stack.
/// </remarks>
internal sealed class NativeCallException(NativeStatus status, string operation, string detail)
    : Exception($"{operation} failed ({status}): {detail}")
{
    /// <summary>Gets the status the shim returned.</summary>
    public NativeStatus Status { get; } = status;

    /// <summary>Gets the entry point that failed.</summary>
    public string Operation { get; } = operation;

    /// <summary>Gets the shim's message, without the framing this exception adds.</summary>
    public string Detail { get; } = detail;
}

/// <summary>
/// The three things every call across the boundary needs: status checking, the two-call buffer
/// protocol, and draining the diagnostic queue.
/// </summary>
/// <remarks>
/// <para>
/// Written once here rather than at each of the forty-nine call sites. The two-call protocol in
/// particular is easy to get subtly wrong -- ignoring the size on the second call, or treating a
/// legitimately empty collection as a failure -- and a mistake in it reads uninitialised memory
/// rather than throwing.
/// </para>
/// <para>
/// Everything here runs on the kernel thread (ADR-0004). The shim's error and diagnostic state is
/// thread-local on its side, so reading it from anywhere else would return another thread's story.
/// </para>
/// </remarks>
internal static class Native
{
    /// <summary>
    /// The largest buffer this will allocate on the stack rather than renting. Sized so a typical
    /// query -- six topology counts, eleven mass properties, a few dozen face tags -- never touches
    /// the pool, while a mesh with a million vertices always does.
    /// </summary>
    private const int StackLimit = 256;

    /// <summary>A native call that fills a caller-supplied buffer, or reports the size it needs.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="buffer">Where to write, or empty to ask for the size.</param>
    /// <param name="capacity">How many elements <paramref name="buffer"/> holds.</param>
    /// <param name="required">How many elements the result actually needs.</param>
    /// <returns>A <see cref="NativeStatus"/>.</returns>
    internal delegate int BufferCall<T>(Span<T> buffer, int capacity, out int required);

    /// <summary>Throws if <paramref name="status"/> is not <see cref="NativeStatus.Ok"/>.</summary>
    /// <param name="status">The status the shim returned.</param>
    /// <param name="operation">The entry point, for the message.</param>
    /// <exception cref="NativeCallException">The call failed.</exception>
    internal static void Check(int status, string operation)
    {
        if (status == (int)NativeStatus.Ok)
        {
            return;
        }

        throw new NativeCallException((NativeStatus)status, operation, LastError());
    }

    /// <summary>
    /// Reads the shim's description of the most recent failure on this thread.
    /// </summary>
    /// <returns>The message, or a stand-in if the shim recorded none.</returns>
    internal static string LastError()
    {
        try
        {
            byte[] text = ReadBytes(
                (Span<byte> buffer, int capacity, out int required)
                    => OcctBindings.LastError(buffer, capacity, out required),
                nameof(OcctBindings.LastError));

            // The shim writes a NUL-terminated string and counts the terminator in the size.
            return text.Length <= 1 ? "The shim recorded no detail." : DecodeUtf8(text);
        }
        catch (NativeCallException)
        {
            // Reading the error must never replace the error being reported. This is reachable
            // when the shim is in a state where even its diagnostics are unavailable.
            return "The shim recorded no detail, and reading it failed as well.";
        }
    }

    /// <summary>
    /// Takes everything the shim queued during the last operation, clearing it as it goes.
    /// </summary>
    /// <returns>The diagnostics, in the order the shim recorded them.</returns>
    /// <remarks>
    /// Always called after an operation, whether it succeeded or not: a succeeded-with-warnings
    /// result is the whole reason <see cref="OperationOutcome.Degraded"/> exists, and dropping the
    /// warnings would silently turn one into a plain success.
    /// </remarks>
    internal static ImmutableArray<KernelDiagnostic> DrainDiagnostics()
    {
        Check(OcctBindings.DiagnosticCount(out int count), nameof(OcctBindings.DiagnosticCount));

        if (count == 0)
        {
            return [];
        }

        ImmutableArray<KernelDiagnostic>.Builder drained =
            ImmutableArray.CreateBuilder<KernelDiagnostic>(count);

        for (int i = 0; i < count; ++i)
        {
            int index = i;

            Check(
                OcctBindings.DiagnosticSeverity(index, out int severity),
                nameof(OcctBindings.DiagnosticSeverity));

            string code = DecodeUtf8(ReadBytes(
                (Span<byte> buffer, int capacity, out int required)
                    => OcctBindings.DiagnosticCode(index, buffer, capacity, out required),
                nameof(OcctBindings.DiagnosticCode)));

            string message = DecodeUtf8(ReadBytes(
                (Span<byte> buffer, int capacity, out int required)
                    => OcctBindings.DiagnosticMessage(index, buffer, capacity, out required),
                nameof(OcctBindings.DiagnosticMessage)));

            // Entities are read but not attributed to an owning shape here: the shim reports raw
            // tags, and only the caller knows which shape they belong to. OcctKernel re-owns them.
            _ = Read<ulong>(
                (Span<ulong> buffer, int capacity, out int required)
                    => OcctBindings.DiagnosticEntities(index, buffer, capacity, out required),
                nameof(OcctBindings.DiagnosticEntities));

            drained.Add(severity switch
            {
                0 => KernelDiagnostic.Information(code, message),
                1 => KernelDiagnostic.Warning(code, message),
                _ => KernelDiagnostic.Error(code, message),
            });
        }

        Check(OcctBindings.DiagnosticsClear(), nameof(OcctBindings.DiagnosticsClear));
        return drained.ToImmutable();
    }

    /// <summary>
    /// Runs the two-call buffer protocol: ask for the size, allocate, then fetch.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="call">The native entry point.</param>
    /// <param name="operation">Its name, for error messages.</param>
    /// <returns>The elements, or empty when the result genuinely has none.</returns>
    /// <remarks>
    /// An empty result is a success, not a failure. Several queries are legitimately empty -- a
    /// mesh has no normals unless they were asked for, a body with no blends has no blend faces --
    /// and treating zero as an error would make those callers handle a failure that did not happen.
    /// </remarks>
    internal static T[] Read<T>(BufferCall<T> call, string operation)
        where T : unmanaged
    {
        Check(call([], 0, out int required), operation);

        if (required <= 0)
        {
            return [];
        }

        T[] values = new T[required];
        Check(call(values, required, out int written), operation);

        // The size can move between the two calls only if something mutated the shape in between,
        // which ADR-0004's single kernel thread rules out. Checking anyway costs one comparison
        // and turns a would-be silent truncation into a diagnosable failure.
        if (written != required)
        {
            throw new NativeCallException(
                NativeStatus.Internal,
                operation,
                $"The shim asked for {required} elements and then wrote {written}. The shape "
                + "changed between the two calls, which should not be possible on a single "
                + "kernel thread.");
        }

        return values;
    }

    /// <summary>
    /// The byte-buffer form of <see cref="Read{T}"/>, using the stack or the array pool for the
    /// short strings that make up most of its traffic.
    /// </summary>
    /// <param name="call">The native entry point.</param>
    /// <param name="operation">Its name, for error messages.</param>
    /// <returns>The bytes, including any trailing NUL the shim wrote.</returns>
    internal static byte[] ReadBytes(BufferCall<byte> call, string operation)
    {
        Check(call([], 0, out int required), operation);

        if (required <= 0)
        {
            return [];
        }

        if (required <= StackLimit)
        {
            Span<byte> scratch = stackalloc byte[StackLimit];
            Check(call(scratch, required, out _), operation);
            return scratch[..required].ToArray();
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(required);
        try
        {
            Check(call(rented, required, out _), operation);
            return rented[..required];
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Decodes a NUL-terminated UTF-8 buffer from the shim.</summary>
    /// <param name="text">The bytes, as the shim wrote them.</param>
    /// <returns>The string, without the terminator.</returns>
    internal static string DecodeUtf8(ReadOnlySpan<byte> text)
    {
        int end = text.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? text : text[..end]);
    }
}
