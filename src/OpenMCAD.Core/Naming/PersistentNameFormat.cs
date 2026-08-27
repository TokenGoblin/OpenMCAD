using System.Collections.Immutable;
using System.Globalization;
using System.Text;

using OpenMCAD.Core.Documents;
using OpenMCAD.Math;

namespace OpenMCAD.Core.Naming;

/// <summary>
/// Writes a <see cref="PersistentName"/> to text and reads it back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Length-prefixed rather than delimited.</b> The structure is recursive and its strings are
/// arbitrary — a role can be anything a plugin invents, and a sketch entity id is whatever the
/// sketch layer chose. A delimited format needs escaping, escaping needs unescaping, and the two
/// disagree eventually. Writing the length of every string in front of it means no character in it
/// is special, so nothing needs escaping and the reader cannot lose its place.
/// </para>
/// <para>
/// <b>What this is for.</b> P3-T18 will write documents with MessagePack over the structure, and
/// that is the wire format. This is the form a name takes when it has to be a string: a diagnostic
/// that must round-trip, a regression fixture in the naming corpus (P3-T13) with an expected value
/// written down in it, a key in a side table. It is versioned so that changing it later is a
/// decision rather than a silent incompatibility.
/// </para>
/// </remarks>
public static class PersistentNameFormat
{
    /// <summary>The version this writer produces.</summary>
    private const int Version = 1;

    /// <summary>Writes a name.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The text.</returns>
    public static string Write(PersistentName name)
    {
        ArgumentNullException.ThrowIfNull(name);

        StringBuilder text = new();

        text.Append('v').Append(Version).Append(';');
        WriteName(name, text);

        return text.ToString();
    }

    /// <summary>Reads a name back.</summary>
    /// <param name="text">The text, as produced by <see cref="Write"/>.</param>
    /// <returns>The name.</returns>
    /// <exception cref="FormatException">The text is not a name this build can read.</exception>
    public static PersistentName Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        Reader reader = new(text);

        reader.Expect('v');

        int version = reader.ReadInt();

        if (version != Version)
        {
            throw new FormatException(
                $"This is a version {version} entity reference and this build writes version "
                + $"{Version}. Reading it as though it were the current form would produce a name "
                + "that resolves to the wrong entity rather than failing.");
        }

        reader.Expect(';');

        PersistentName name = reader.ReadName();

        reader.ExpectEnd();

        return name;
    }

    /// <summary>Reads a name back, or reports that it could not be read.</summary>
    /// <param name="text">The text.</param>
    /// <param name="name">The name, if it was read.</param>
    /// <returns>Whether it was read.</returns>
    public static bool TryRead(string? text, out PersistentName? name)
    {
        name = null;

        if (text is null)
        {
            return false;
        }

        try
        {
            name = Read(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void WriteName(PersistentName name, StringBuilder text)
    {
        text.Append(name.Path.Length).Append(':');

        foreach (NameSegment segment in name.Path)
        {
            WriteSegment(segment, text);
        }
    }

    private static void WriteSegment(NameSegment segment, StringBuilder text)
    {
        text.Append(segment.Feature.ToStorageString()).Append(';');
        text.Append((int)segment.Provenance).Append(';');
        WriteText(segment.Role.Value, text);
        text.Append(segment.Ordinal).Append(';');

        text.Append(segment.Sources.Length).Append(':');

        foreach (NameSource source in segment.Sources)
        {
            switch (source)
            {
                case NameSource.Entity entity:
                    text.Append("e;");
                    WriteName(entity.Name, text);
                    break;

                case NameSource.Sketch sketch:
                    text.Append("s;").Append(sketch.Owner.ToStorageString()).Append(';');
                    WriteText(sketch.EntityId, text);
                    break;

                default:
                    throw new NotSupportedException(
                        $"There is no way to write a {source.GetType().Name} source. A source kind "
                        + "added without a case here would be dropped on save, which loses the "
                        + "reference rather than reporting that it cannot be stored.");
            }
        }

        if (segment.Hint is { } hint)
        {
            text.Append("h;");
            text.Append((int)hint.Kind).Append(';');
            WriteNumber(hint.Measure, text);
            WriteVector(hint.Centroid, text);
            WriteVector(hint.Direction, text);
            text.Append(hint.AdjacencyDegree).Append(';');
        }
        else
        {
            text.Append("n;");
        }
    }

    private static void WriteText(string value, StringBuilder text)
        => text.Append(value.Length).Append(':').Append(value);

    private static void WriteNumber(double value, StringBuilder text)
        => text.Append(value.ToString("R", CultureInfo.InvariantCulture)).Append(';');

    private static void WriteVector(Vec3d value, StringBuilder text)
    {
        WriteNumber(value.X, text);
        WriteNumber(value.Y, text);
        WriteNumber(value.Z, text);
    }

    /// <summary>Walks the text, never guessing where anything ends.</summary>
    private ref struct Reader(string text)
    {
        private readonly string _text = text;
        private int _at;

        public void Expect(char expected)
        {
            if (_at >= _text.Length || _text[_at] != expected)
            {
                throw Fail($"expected '{expected}'");
            }

            _at++;
        }

        public void ExpectEnd()
        {
            if (_at != _text.Length)
            {
                throw Fail("expected the end of the reference");
            }
        }

        public int ReadInt()
        {
            int start = _at;

            if (_at < _text.Length && _text[_at] == '-')
            {
                _at++;
            }

            while (_at < _text.Length && char.IsAsciiDigit(_text[_at]))
            {
                _at++;
            }

            if (_at == start)
            {
                throw Fail("expected a number");
            }

            return int.Parse(_text.AsSpan(start, _at - start), CultureInfo.InvariantCulture);
        }

        public PersistentName ReadName()
        {
            int count = ReadInt();
            Expect(':');

            if (count <= 0)
            {
                throw Fail("a name needs at least one segment");
            }

            ImmutableArray<NameSegment>.Builder path =
                ImmutableArray.CreateBuilder<NameSegment>(count);

            for (int i = 0; i < count; ++i)
            {
                path.Add(ReadSegment());
            }

            return new PersistentName(path.ToImmutable());
        }

        private NameSegment ReadSegment()
        {
            FeatureId feature = ReadFeature();
            ProvenanceKind provenance = (ProvenanceKind)ReadInt();
            Expect(';');

            EntityRole role = new(ReadText());
            int ordinal = ReadInt();
            Expect(';');

            int sourceCount = ReadInt();
            Expect(':');

            ImmutableArray<NameSource>.Builder sources =
                ImmutableArray.CreateBuilder<NameSource>(sourceCount);

            for (int i = 0; i < sourceCount; ++i)
            {
                sources.Add(ReadSource());
            }

            return new NameSegment(feature, provenance, sources.ToImmutable(), role, ordinal, ReadHint());
        }

        private NameSource ReadSource()
        {
            char kind = _text[_at];
            _at++;
            Expect(';');

            return kind switch
            {
                'e' => new NameSource.Entity(ReadName()),
                's' => new NameSource.Sketch(ReadFeature(), ReadText()),
                _ => throw Fail($"unknown source kind '{kind}'"),
            };
        }

        private GeoHint? ReadHint()
        {
            char kind = _text[_at];
            _at++;
            Expect(';');

            if (kind == 'n')
            {
                return null;
            }

            if (kind != 'h')
            {
                throw Fail($"unknown hint marker '{kind}'");
            }

            GeometryKind geometry = (GeometryKind)ReadInt();
            Expect(';');

            double measure = ReadNumber();
            Vec3d centroid = ReadVector();
            Vec3d direction = ReadVector();

            int degree = ReadInt();
            Expect(';');

            return new GeoHint(geometry, measure, centroid, direction, degree);
        }

        private FeatureId ReadFeature()
        {
            const int Length = 36;

            if (_at + Length > _text.Length)
            {
                throw Fail("expected a feature id");
            }

            string value = _text.Substring(_at, Length);
            _at += Length;

            Expect(';');

            return FeatureId.TryParse(value, out FeatureId id)
                ? id
                : throw Fail($"'{value}' is not a feature id");
        }

        private string ReadText()
        {
            int length = ReadInt();
            Expect(':');

            if (length < 0 || _at + length > _text.Length)
            {
                throw Fail("a length runs past the end of the reference");
            }

            string value = _text.Substring(_at, length);
            _at += length;

            return value;
        }

        private double ReadNumber()
        {
            int start = _at;

            while (_at < _text.Length && _text[_at] != ';')
            {
                _at++;
            }

            double value = double.Parse(
                _text.AsSpan(start, _at - start), CultureInfo.InvariantCulture);

            Expect(';');

            return value;
        }

        private Vec3d ReadVector() => new(ReadNumber(), ReadNumber(), ReadNumber());

        private readonly FormatException Fail(string what)
            => new($"This is not a readable entity reference: {what}, at character {_at}.");
    }
}
