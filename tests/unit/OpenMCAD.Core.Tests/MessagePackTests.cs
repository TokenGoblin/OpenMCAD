using System.Globalization;

using FluentAssertions;

using OpenMCAD.Core.Serialization;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// The MessagePack primitives (P3-T18).
/// </summary>
/// <remarks>
/// <para>
/// The expected bytes below come from the MessagePack specification rather than from running this
/// code and writing down what it produced. Pinning an implementation to its own output proves only
/// that it is consistent, which a wrong implementation also is; these are the byte sequences the
/// format defines, so a file written here is one anything else can read.
/// </para>
/// <para>
/// The encoding is canonical — always the shortest form that holds the value. That is a
/// correctness requirement rather than a preference: Phase 3's first exit criterion is that a
/// document is bit-identical on re-save, so the bytes have to be a function of the value alone.
/// </para>
/// </remarks>
public sealed class MessagePackTests
{
    [Theory]
    [InlineData(0, "00")]
    [InlineData(1, "01")]
    [InlineData(127, "7F")]
    [InlineData(128, "CC80")]
    [InlineData(255, "CCFF")]
    [InlineData(256, "CD0100")]
    [InlineData(65535, "CDFFFF")]
    [InlineData(65536, "CE00010000")]
    [InlineData(4294967295L, "CEFFFFFFFF")]
    [InlineData(4294967296L, "CF0000000100000000")]
    [InlineData(-1, "FF")]
    [InlineData(-32, "E0")]
    [InlineData(-33, "D0DF")]
    [InlineData(-128, "D080")]
    [InlineData(-129, "D1FF7F")]
    [InlineData(-32768, "D18000")]
    [InlineData(-32769, "D2FFFF7FFF")]
    [InlineData(long.MinValue, "D38000000000000000")]
    public void IntegersUseTheShortestFormThatHoldsThem(long value, string expected)
    {
        // Every boundary in both directions. An off-by-one here writes a file that is still valid
        // MessagePack and still round-trips through this reader -- so nothing would notice until
        // another implementation read it, or until a re-save produced different bytes.
        Write(w => w.Write(value)).Should().Be(expected);
        ReadInteger(expected).Should().Be(value);
    }

    [Theory]
    [InlineData("", "A0")]
    [InlineData("a", "A161")]
    [InlineData("hello", "A568656C6C6F")]
    public void ShortTextIsWrittenInline(string value, string expected)
    {
        Write(w => w.Write(value)).Should().Be(expected);
        ReadText(expected).Should().Be(value);
    }

    [Fact]
    public void TextIsMeasuredInBytesAndNotCharacters()
    {
        // The classic mistake. "é" is one character and two bytes, and a length written in
        // characters produces a file that truncates on the way back in.
        string text = new('é', 20);

        string encoded = Write(w => w.Write(text));

        encoded.Should().StartWith("D9", "forty bytes does not fit in a fixstr");
        encoded[2..4].Should().Be("28", "forty, in hexadecimal");

        ReadText(encoded).Should().Be(text);
    }

    [Theory]
    [InlineData(31, "BF")]
    [InlineData(32, "D920")]
    [InlineData(255, "D9FF")]
    [InlineData(256, "DA0100")]
    public void TextLengthCrossesItsBoundariesCorrectly(int length, string prefix)
    {
        string text = new('x', length);

        Write(w => w.Write(text)).Should().StartWith(prefix);
        ReadText(Write(w => w.Write(text))).Should().Be(text);
    }

    [Fact]
    public void SimpleValuesAreOneByteEach()
    {
        Write(w => w.WriteNil()).Should().Be("C0");
        Write(w => w.Write(false)).Should().Be("C2");
        Write(w => w.Write(true)).Should().Be("C3");
    }

    [Theory]
    [InlineData(0.0, "CB0000000000000000")]
    [InlineData(1.0, "CB3FF0000000000000")]
    [InlineData(-2.5, "CBC004000000000000")]
    public void DoublesAreAlwaysSixtyFourBits(double value, string expected)
    {
        // Narrowing a value that happens to fit in thirty-two bits would make the encoding depend
        // on the number's history rather than on the number.
        Write(w => w.Write(value)).Should().Be(expected);
        ReadNumber(expected).Should().Be(value);
    }

    [Fact]
    public void ADoubleSurvivesToItsLastBit()
    {
        // A dimension that came back a bit different would be a model that changed by being saved.
        foreach (double value in new[]
        {
            1.0 / 3.0, double.Epsilon, double.MaxValue, double.MinValue,
            -0.0, 1e-300, 25.4, System.Math.PI,
        })
        {
            string encoded = Write(w => w.Write(value));

            ReadNumber(encoded).Should().Be(value);
        }
    }

    [Fact]
    public void AWholeNumberIsAcceptedWhereAFractionWasExpected()
    {
        // Something that wrote 0 rather than 0.0 -- another implementation, or a hand-edited
        // fixture -- meant the number. Refusing it would lose a file over a formality.
        ReadNumber("00").Should().Be(0);
        ReadNumber("D0FF").Should().Be(-1);
    }

    [Theory]
    [InlineData(0, "90")]
    [InlineData(15, "9F")]
    [InlineData(16, "DC0010")]
    [InlineData(65536, "DD00010000")]
    public void ArrayHeadersCrossTheirBoundariesCorrectly(int count, string expected)
    {
        Write(w => w.WriteArrayHeader(count)).Should().Be(expected);
        ReadArrayCount(expected).Should().Be(count);
    }

    [Theory]
    [InlineData(0, "80")]
    [InlineData(15, "8F")]
    [InlineData(16, "DE0010")]
    [InlineData(65536, "DF00010000")]
    public void MapHeadersCrossTheirBoundariesCorrectly(int count, string expected)
    {
        Write(w => w.WriteMapHeader(count)).Should().Be(expected);
        ReadMapCount(expected).Should().Be(count);
    }

    [Fact]
    public void BinaryRoundTrips()
    {
        byte[] bytes = [0, 1, 2, 254, 255];

        string encoded = Write(w => w.WriteBinary(bytes));

        encoded.Should().Be("C4050001 02FEFF".Replace(" ", string.Empty, StringComparison.Ordinal));

        MessagePackReader reader = new(Bytes(encoded));
        reader.ReadBinary().ToArray().Should().Equal(bytes);
    }

    [Fact]
    public void AMapOfKnownShapeReadsBackAsItWasWritten()
    {
        string encoded = Write(w =>
        {
            w.WriteMapHeader(2);
            w.Write("name");
            w.Write("Extrude1");
            w.Write("depth");
            w.Write(25.4);
        });

        MessagePackReader reader = new(Bytes(encoded));

        reader.ReadMapHeader().Should().Be(2);
        reader.ReadString().Should().Be("name");
        reader.ReadString().Should().Be("Extrude1");
        reader.ReadString().Should().Be("depth");
        reader.ReadDouble().Should().Be(25.4);
        reader.AtEnd.Should().BeTrue();
    }

    [Fact]
    public void SkippingAValueStepsOverAllOfIt()
    {
        // Recursive, because a field this build does not recognise may be a map of arrays of maps.
        // Skipping only the header would leave the reader inside a structure it cannot interpret,
        // and everything after it would be read as rubbish.
        string encoded = Write(w =>
        {
            w.WriteMapHeader(2);
            w.Write("nested");

            w.WriteArrayHeader(2);
            w.WriteMapHeader(1);
            w.Write("deep");
            w.WriteArrayHeader(1);
            w.Write(7);
            w.Write("second");

            w.Write("after");
            w.Write(42);
        });

        MessagePackReader reader = new(Bytes(encoded));

        reader.ReadMapHeader().Should().Be(2);
        reader.ReadString().Should().Be("nested");
        reader.Skip();

        reader.ReadString().Should().Be("after", "the reader must land exactly after the value");
        reader.ReadInt64().Should().Be(42);
        reader.AtEnd.Should().BeTrue();
    }

    [Fact]
    public void AnUnknownValueCanBeKeptExactlyAndWrittenBack()
    {
        // How P3-T20 will preserve a field written by a newer build, or by a plugin this one does
        // not have. The bytes are captured without being interpreted, because interpreting them is
        // exactly what cannot be done.
        string encoded = Write(w =>
        {
            w.WriteMapHeader(1);
            w.Write("fromTheFuture");
            w.WriteArrayHeader(2);
            w.Write("something");
            w.Write(-9.5);
        });

        byte[] captured;

        {
            MessagePackReader reader = new(Bytes(encoded));

            reader.ReadMapHeader();
            reader.ReadString();

            captured = reader.ReadRaw().ToArray();
        }

        string rewritten = Write(w =>
        {
            w.WriteMapHeader(1);
            w.Write("fromTheFuture");
            w.WriteRaw(captured);
        });

        rewritten.Should().Be(encoded, "the bytes must go back out exactly as they came in");
    }

    [Fact]
    public void PeekingSaysWhatComesNextWithoutConsumingIt()
    {
        string encoded = Write(w =>
        {
            w.Write("text");
            w.Write(1);
            w.Write(true);
            w.WriteNil();
        });

        MessagePackReader reader = new(Bytes(encoded));

        reader.Peek().Should().Be(MessagePackType.String);
        reader.ReadString().Should().Be("text");

        reader.Peek().Should().Be(MessagePackType.Integer);
        reader.ReadInt64().Should().Be(1);

        reader.Peek().Should().Be(MessagePackType.Boolean);
        reader.ReadBoolean().Should().BeTrue();

        reader.TryReadNil().Should().BeTrue();
        reader.AtEnd.Should().BeTrue();
    }

    [Fact]
    public void ATruncatedDocumentIsRefusedRatherThanRead()
    {
        // A file cut short by a failed write must not read back as a smaller document. That would
        // be silent data loss dressed as a successful open.
        Action truncated = () =>
        {
            MessagePackReader reader = new(Bytes("A5686574"));
            reader.ReadString();
        };

        truncated.Should().Throw<DocumentFormatException>().WithMessage("*longer than what is left*");
    }

    [Fact]
    public void AValueOfTheWrongKindIsRefused()
    {
        Action wrong = () =>
        {
            MessagePackReader reader = new(Bytes("C3"));
            reader.ReadString();
        };

        wrong.Should().Throw<DocumentFormatException>().WithMessage("*Expected text*");
    }

    private static string Write(Action<MessagePackWriter> write)
    {
        MessagePackWriter writer = new();
        write(writer);

        return Convert.ToHexString(writer.ToArray());
    }

    // A ref struct cannot be captured by a lambda, so each read gets its own small helper rather
    // than one generic one taking a delegate.
    private static long ReadInteger(string hex)
    {
        MessagePackReader reader = new(Bytes(hex));
        return reader.ReadInt64();
    }

    private static string ReadText(string hex)
    {
        MessagePackReader reader = new(Bytes(hex));
        return reader.ReadString();
    }

    private static double ReadNumber(string hex)
    {
        MessagePackReader reader = new(Bytes(hex));
        return reader.ReadDouble();
    }

    private static int ReadArrayCount(string hex)
    {
        MessagePackReader reader = new(Bytes(hex));
        return reader.ReadArrayHeader();
    }

    private static int ReadMapCount(string hex)
    {
        MessagePackReader reader = new(Bytes(hex));
        return reader.ReadMapHeader();
    }

    private static byte[] Bytes(string hex) => Convert.FromHexString(hex);
}
