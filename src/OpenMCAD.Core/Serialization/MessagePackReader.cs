using System.Buffers.Binary;
using System.Text;

namespace OpenMCAD.Core.Serialization;

/// <summary>What sort of value comes next.</summary>
internal enum MessagePackType
{
    /// <summary>Nothing.</summary>
    Nil,

    /// <summary>True or false.</summary>
    Boolean,

    /// <summary>A whole number.</summary>
    Integer,

    /// <summary>A number with a fractional part.</summary>
    Float,

    /// <summary>Text.</summary>
    String,

    /// <summary>Bytes.</summary>
    Binary,

    /// <summary>A sequence.</summary>
    Array,

    /// <summary>Pairs.</summary>
    Map,

    /// <summary>Something this build has no name for.</summary>
    Unknown,
}

/// <summary>
/// Thrown when a document's bytes are not what they claim to be.
/// </summary>
public sealed class DocumentFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong.</param>
    public DocumentFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    /// <param name="message">What is wrong.</param>
    /// <param name="innerException">The cause.</param>
    public DocumentFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with nothing to say.</summary>
    public DocumentFormatException()
        : base("This file is not a document this build can read.")
    {
    }
}

/// <summary>
/// Reads MessagePack, and can hand back a value it was not asked to understand.
/// </summary>
/// <remarks>
/// Reads from a buffer rather than a stream, and that is what makes P3-T20 possible: preserving an
/// unknown field means keeping its bytes exactly, which needs the ability to note where a value
/// started, skip over it, and take the slice between. A forward-only stream can skip a value but
/// cannot go back and copy it.
/// </remarks>
internal ref struct MessagePackReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _at;

    /// <summary>Gets whether every byte has been read.</summary>
    public readonly bool AtEnd => _at >= _data.Length;

    /// <summary>Gets where the reader is, for slicing out a value that was skipped.</summary>
    public readonly int Position => _at;

    /// <summary>Looks at what comes next without consuming it.</summary>
    /// <returns>Its type.</returns>
    public readonly MessagePackType Peek()
    {
        byte marker = PeekMarker();

        return marker switch
        {
            <= 0x7F => MessagePackType.Integer,
            >= 0xE0 => MessagePackType.Integer,
            >= 0x80 and <= 0x8F => MessagePackType.Map,
            >= 0x90 and <= 0x9F => MessagePackType.Array,
            >= 0xA0 and <= 0xBF => MessagePackType.String,
            0xC0 => MessagePackType.Nil,
            0xC2 or 0xC3 => MessagePackType.Boolean,
            0xC4 or 0xC5 or 0xC6 => MessagePackType.Binary,
            0xCA or 0xCB => MessagePackType.Float,
            0xCC or 0xCD or 0xCE or 0xCF => MessagePackType.Integer,
            0xD0 or 0xD1 or 0xD2 or 0xD3 => MessagePackType.Integer,
            0xD9 or 0xDA or 0xDB => MessagePackType.String,
            0xDC or 0xDD => MessagePackType.Array,
            0xDE or 0xDF => MessagePackType.Map,
            _ => MessagePackType.Unknown,
        };
    }

    /// <summary>Reads a nil.</summary>
    public void ReadNil()
    {
        if (Take() != 0xC0)
        {
            throw Fail("nothing");
        }
    }

    /// <summary>Reads whether the next value is nil, consuming it only if it is.</summary>
    /// <returns>Whether it was nil.</returns>
    public bool TryReadNil()
    {
        if (Peek() != MessagePackType.Nil)
        {
            return false;
        }

        _at++;
        return true;
    }

    /// <summary>Reads a boolean.</summary>
    /// <returns>The value.</returns>
    public bool ReadBoolean() => Take() switch
    {
        0xC2 => false,
        0xC3 => true,
        _ => throw Fail("true or false"),
    };

    /// <summary>Reads an integer.</summary>
    /// <returns>The value.</returns>
    public long ReadInt64()
    {
        byte marker = Take();

        return marker switch
        {
            <= 0x7F => marker,
            >= 0xE0 => (sbyte)marker,
            0xCC => Take(),
            0xCD => TakeUInt16(),
            0xCE => TakeUInt32(),
            0xCF => checked((long)TakeUInt64()),
            0xD0 => (sbyte)Take(),
            0xD1 => (short)TakeUInt16(),
            0xD2 => (int)TakeUInt32(),
            0xD3 => (long)TakeUInt64(),
            _ => throw Fail("a whole number"),
        };
    }

    /// <summary>Reads an integer that has to fit in an <see cref="int"/>.</summary>
    /// <returns>The value.</returns>
    public int ReadInt32() => checked((int)ReadInt64());

    /// <summary>Reads a double.</summary>
    /// <returns>The value.</returns>
    public double ReadDouble()
    {
        byte marker = Take();

        return marker switch
        {
            0xCA => BitConverter.UInt32BitsToSingle(TakeUInt32()),
            0xCB => BitConverter.UInt64BitsToDouble(TakeUInt64()),

            // A whole number where a fraction was expected is not an error. Something that wrote 0
            // rather than 0.0 -- another implementation, or a hand-edited fixture -- meant the
            // number, and refusing it would be pedantry that loses a file.
            _ => Rewind(marker) ? ReadInt64() : 0,
        };
    }

    /// <summary>Reads text.</summary>
    /// <returns>The text.</returns>
    public string ReadString()
    {
        byte marker = Take();

        int length = marker switch
        {
            >= 0xA0 and <= 0xBF => marker & 0x1F,
            0xD9 => Take(),
            0xDA => TakeUInt16(),
            0xDB => checked((int)TakeUInt32()),
            _ => throw Fail("text"),
        };

        return Encoding.UTF8.GetString(TakeBytes(length));
    }

    /// <summary>Reads bytes.</summary>
    /// <returns>The bytes.</returns>
    public ReadOnlySpan<byte> ReadBinary()
    {
        byte marker = Take();

        int length = marker switch
        {
            0xC4 => Take(),
            0xC5 => TakeUInt16(),
            0xC6 => checked((int)TakeUInt32()),
            _ => throw Fail("bytes"),
        };

        return TakeBytes(length);
    }

    /// <summary>Reads the header of an array.</summary>
    /// <returns>How many elements follow.</returns>
    public int ReadArrayHeader()
    {
        byte marker = Take();

        return marker switch
        {
            >= 0x90 and <= 0x9F => marker & 0x0F,
            0xDC => TakeUInt16(),
            0xDD => checked((int)TakeUInt32()),
            _ => throw Fail("a sequence"),
        };
    }

    /// <summary>Reads the header of a map.</summary>
    /// <returns>How many pairs follow.</returns>
    public int ReadMapHeader()
    {
        byte marker = Take();

        return marker switch
        {
            >= 0x80 and <= 0x8F => marker & 0x0F,
            0xDE => TakeUInt16(),
            0xDF => checked((int)TakeUInt32()),
            _ => throw Fail("a map"),
        };
    }

    /// <summary>Steps over the next value, whatever it is.</summary>
    /// <remarks>
    /// Recursive, because a value this build does not recognise may be a map of arrays of maps.
    /// Skipping only the header would leave the reader inside a structure it does not understand,
    /// and every field after it would be read as rubbish.
    /// </remarks>
    public void Skip()
    {
        switch (Peek())
        {
            case MessagePackType.Nil:
                ReadNil();
                break;

            case MessagePackType.Boolean:
                ReadBoolean();
                break;

            case MessagePackType.Integer:
                ReadInt64();
                break;

            case MessagePackType.Float:
                ReadDouble();
                break;

            case MessagePackType.String:
                ReadString();
                break;

            case MessagePackType.Binary:
                ReadBinary();
                break;

            case MessagePackType.Array:
                int elements = ReadArrayHeader();

                for (int i = 0; i < elements; ++i)
                {
                    Skip();
                }

                break;

            case MessagePackType.Map:
                int pairs = ReadMapHeader();

                for (int i = 0; i < pairs; ++i)
                {
                    Skip();
                    Skip();
                }

                break;

            default:
                throw Fail("a value");
        }
    }

    /// <summary>Reads the next value without interpreting it, and hands back its bytes.</summary>
    /// <returns>Exactly the bytes of one value.</returns>
    /// <remarks>
    /// What P3-T20 needs. A field written by a newer build, or by a plugin this one does not have,
    /// is kept exactly as it arrived so that saving the file again does not quietly delete it.
    /// </remarks>
    public ReadOnlySpan<byte> ReadRaw()
    {
        int start = _at;
        Skip();

        return _data[start.._at];
    }

    private readonly byte PeekMarker()
        => _at < _data.Length ? _data[_at] : throw new DocumentFormatException(
            "This document ends in the middle of a value.");

    private byte Take() => _at < _data.Length
        ? _data[_at++]
        : throw new DocumentFormatException("This document ends in the middle of a value.");

    private bool Rewind(byte marker)
    {
        _ = marker;
        _at--;

        return true;
    }

    private ushort TakeUInt16()
    {
        ushort value = BinaryPrimitives.ReadUInt16BigEndian(TakeBytes(2));
        return value;
    }

    private uint TakeUInt32() => BinaryPrimitives.ReadUInt32BigEndian(TakeBytes(4));

    private ulong TakeUInt64() => BinaryPrimitives.ReadUInt64BigEndian(TakeBytes(8));

    private ReadOnlySpan<byte> TakeBytes(int count)
    {
        if (count < 0 || _at + count > _data.Length)
        {
            throw new DocumentFormatException(
                "This document claims a value longer than what is left of the file.");
        }

        ReadOnlySpan<byte> slice = _data.Slice(_at, count);
        _at += count;

        return slice;
    }

    private readonly DocumentFormatException Fail(string expected)
        => new($"Expected {expected} at byte {_at - 1} of this document, and found something else.");
}
