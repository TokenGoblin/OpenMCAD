using System.Buffers.Binary;
using System.Text;

namespace OpenMCAD.Core.Serialization;

/// <summary>
/// Writes MessagePack, always choosing the shortest encoding for a value.
/// </summary>
/// <remarks>
/// <para>
/// §5.8 names MessagePack for the document graph, and only the primitives are implemented here —
/// the integer, floating-point, string, binary, array and map families the schema uses. A library
/// would supply exactly this and nothing more that is usable: its attribute-driven serialisation
/// cannot be used, because P3-T20 requires unknown fields to survive a round trip, which means
/// explicit read and write code either way.
/// </para>
/// <para>
/// <b>Canonical, and that is a correctness requirement rather than a tidiness one.</b> The number
/// 1 can be written as a fixint in one byte or as a uint64 in nine, and both are valid
/// MessagePack. Phase 3's first exit criterion is that a document is bit-identical on re-save, so
/// the encoding has to be a function of the value alone: always the shortest form, always
/// big-endian, no exceptions. A writer that sometimes chose differently would produce files that
/// differ without their contents differing, and every fixture comparison downstream would be
/// meaningless.
/// </para>
/// </remarks>
internal sealed class MessagePackWriter
{
    private byte[] _buffer;
    private int _length;

    /// <summary>Creates a writer.</summary>
    /// <param name="capacity">How much room to start with.</param>
    public MessagePackWriter(int capacity = 1024) => _buffer = new byte[System.Math.Max(16, capacity)];

    /// <summary>Gets how many bytes have been written.</summary>
    public int Length => _length;

    /// <summary>Gets what has been written.</summary>
    /// <returns>The bytes.</returns>
    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

    /// <summary>Gets what has been written, without copying.</summary>
    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _length);

    /// <summary>Writes nothing at all, which is a value.</summary>
    public void WriteNil() => Put(0xC0);

    /// <summary>Writes a boolean.</summary>
    /// <param name="value">The value.</param>
    public void Write(bool value) => Put(value ? (byte)0xC3 : (byte)0xC2);

    /// <summary>Writes an integer in the shortest form that holds it.</summary>
    /// <param name="value">The value.</param>
    public void Write(long value)
    {
        switch (value)
        {
            case >= 0 and <= 127:
                Put((byte)value);
                return;

            case >= -32 and < 0:
                Put((byte)(0xE0 | (value + 32)));
                return;

            case >= 0 and <= byte.MaxValue:
                Put(0xCC);
                Put((byte)value);
                return;

            case >= 0 and <= ushort.MaxValue:
                Put(0xCD);
                PutBigEndian((ushort)value);
                return;

            case >= 0 and <= uint.MaxValue:
                Put(0xCE);
                PutBigEndian((uint)value);
                return;

            case >= 0:
                Put(0xCF);
                PutBigEndian((ulong)value);
                return;

            case >= sbyte.MinValue:
                Put(0xD0);
                Put((byte)(sbyte)value);
                return;

            case >= short.MinValue:
                Put(0xD1);
                PutBigEndian((ushort)(short)value);
                return;

            case >= int.MinValue:
                Put(0xD2);
                PutBigEndian((uint)(int)value);
                return;

            default:
                Put(0xD3);
                PutBigEndian((ulong)value);
                return;
        }
    }

    /// <summary>Writes a double.</summary>
    /// <param name="value">The value.</param>
    /// <remarks>
    /// Always sixty-four bits, even for a value that would fit in thirty-two. Narrowing would be
    /// shorter and would lose the distinction between a number that was measured to that precision
    /// and one that happens to be round — and, worse for this purpose, would make the encoding
    /// depend on the value's history rather than on the value.
    /// </remarks>
    public void Write(double value)
    {
        Put(0xCB);
        PutBigEndian(BitConverter.DoubleToUInt64Bits(value));
    }

    /// <summary>Writes text.</summary>
    /// <param name="value">The text.</param>
    public void Write(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        int bytes = Encoding.UTF8.GetByteCount(value);

        switch (bytes)
        {
            case < 32:
                Put((byte)(0xA0 | bytes));
                break;

            case <= byte.MaxValue:
                Put(0xD9);
                Put((byte)bytes);
                break;

            case <= ushort.MaxValue:
                Put(0xDA);
                PutBigEndian((ushort)bytes);
                break;

            default:
                Put(0xDB);
                PutBigEndian((uint)bytes);
                break;
        }

        Reserve(bytes);
        Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_length));
        _length += bytes;
    }

    /// <summary>Writes bytes.</summary>
    /// <param name="value">The bytes.</param>
    public void WriteBinary(ReadOnlySpan<byte> value)
    {
        switch (value.Length)
        {
            case <= byte.MaxValue:
                Put(0xC4);
                Put((byte)value.Length);
                break;

            case <= ushort.MaxValue:
                Put(0xC5);
                PutBigEndian((ushort)value.Length);
                break;

            default:
                Put(0xC6);
                PutBigEndian((uint)value.Length);
                break;
        }

        Reserve(value.Length);
        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
    }

    /// <summary>Writes the header of an array, whose elements follow.</summary>
    /// <param name="count">How many elements.</param>
    public void WriteArrayHeader(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        switch (count)
        {
            case < 16:
                Put((byte)(0x90 | count));
                break;

            case <= ushort.MaxValue:
                Put(0xDC);
                PutBigEndian((ushort)count);
                break;

            default:
                Put(0xDD);
                PutBigEndian((uint)count);
                break;
        }
    }

    /// <summary>Writes the header of a map, whose keys and values follow in pairs.</summary>
    /// <param name="count">How many pairs.</param>
    public void WriteMapHeader(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        switch (count)
        {
            case < 16:
                Put((byte)(0x80 | count));
                break;

            case <= ushort.MaxValue:
                Put(0xDE);
                PutBigEndian((ushort)count);
                break;

            default:
                Put(0xDF);
                PutBigEndian((uint)count);
                break;
        }
    }

    /// <summary>Writes an already-encoded value straight through.</summary>
    /// <param name="encoded">The bytes, which must be exactly one MessagePack value.</param>
    /// <remarks>
    /// How a field this build does not understand survives a round trip (P3-T20). It was captured
    /// verbatim on the way in and goes back out unexamined, which is the only way to preserve
    /// something whose meaning is unknown.
    /// </remarks>
    public void WriteRaw(ReadOnlySpan<byte> encoded)
    {
        Reserve(encoded.Length);
        encoded.CopyTo(_buffer.AsSpan(_length));
        _length += encoded.Length;
    }

    private void Put(byte value)
    {
        Reserve(1);
        _buffer[_length++] = value;
    }

    private void PutBigEndian(ushort value)
    {
        Reserve(2);
        BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(_length), value);
        _length += 2;
    }

    private void PutBigEndian(uint value)
    {
        Reserve(4);
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(_length), value);
        _length += 4;
    }

    private void PutBigEndian(ulong value)
    {
        Reserve(8);
        BinaryPrimitives.WriteUInt64BigEndian(_buffer.AsSpan(_length), value);
        _length += 8;
    }

    private void Reserve(int extra)
    {
        if (_length + extra <= _buffer.Length)
        {
            return;
        }

        int size = _buffer.Length;

        while (size < _length + extra)
        {
            size *= 2;
        }

        Array.Resize(ref _buffer, size);
    }
}
