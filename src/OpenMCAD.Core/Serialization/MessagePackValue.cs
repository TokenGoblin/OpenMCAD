using System.Collections.Immutable;

namespace OpenMCAD.Core.Serialization;

/// <summary>
/// A document as a tree of MessagePack values, with nothing interpreted.
/// </summary>
/// <remarks>
/// <para>
/// What a schema migration works on. A migration by definition reads a document this build's codec
/// cannot read — that is the only reason it exists — so it cannot be handed a
/// <see cref="Documents.Document"/>, and asking it to patch raw bytes would make every migration a
/// hand-written parser. A tree is the middle ground: the shape is visible, nothing is validated,
/// and re-encoding it produces bytes the current reader can take.
/// </para>
/// <para>
/// Order is preserved. A map here keeps its pairs in the order they were read and writes them back
/// the same way, because P3-T18's first exit criterion is that a document is bit-identical on
/// re-save and a tree that reordered fields would break it the moment a migration touched a file.
/// </para>
/// </remarks>
internal abstract record MessagePackValue
{
    /// <summary>Reads a whole document into a tree.</summary>
    /// <param name="data">The encoded document.</param>
    /// <returns>The tree.</returns>
    /// <exception cref="DocumentFormatException">The bytes are not MessagePack.</exception>
    public static MessagePackValue Read(ReadOnlySpan<byte> data)
    {
        MessagePackReader reader = new(data);

        return Read(ref reader);
    }

    /// <summary>Reads one value.</summary>
    /// <param name="reader">Where to read from.</param>
    /// <returns>The value.</returns>
    /// <exception cref="DocumentFormatException">The bytes are not MessagePack.</exception>
    public static MessagePackValue Read(ref MessagePackReader reader)
    {
        switch (reader.Peek())
        {
            case MessagePackType.Nil:
                reader.ReadNil();
                return MessagePackNil.Instance;

            case MessagePackType.Boolean:
                return new MessagePackBoolean(reader.ReadBoolean());

            case MessagePackType.Integer:
                return new MessagePackInteger(reader.ReadInt64());

            case MessagePackType.Float:
                return new MessagePackFloat(reader.ReadDouble());

            case MessagePackType.String:
                return new MessagePackString(reader.ReadString());

            case MessagePackType.Binary:
                return new MessagePackBinary([.. reader.ReadBinary()]);

            case MessagePackType.Array:
                int elements = reader.ReadArrayHeader();
                ImmutableArray<MessagePackValue>.Builder items =
                    ImmutableArray.CreateBuilder<MessagePackValue>(elements);

                for (int i = 0; i < elements; ++i)
                {
                    items.Add(Read(ref reader));
                }

                return new MessagePackArray(items.ToImmutable());

            case MessagePackType.Map:
                int count = reader.ReadMapHeader();
                ImmutableArray<MessagePackPair>.Builder pairs =
                    ImmutableArray.CreateBuilder<MessagePackPair>(count);

                for (int i = 0; i < count; ++i)
                {
                    MessagePackValue key = Read(ref reader);
                    pairs.Add(new MessagePackPair(key, Read(ref reader)));
                }

                return new MessagePackMap(pairs.ToImmutable());

            // Kept exactly as it arrived, because nothing here knows what it means. Re-encoding a
            // value this build cannot interpret is how a migration would silently corrupt one.
            case MessagePackType.Extension:
                return new MessagePackOpaque([.. reader.ReadRaw()]);

            default:
                throw new DocumentFormatException(
                    $"Byte {reader.Position} of this document is not the start of any MessagePack "
                    + "value.");
        }
    }

    /// <summary>Writes this value.</summary>
    /// <param name="writer">Where to write it.</param>
    public abstract void Write(MessagePackWriter writer);

    /// <summary>Encodes this value on its own.</summary>
    /// <returns>The bytes.</returns>
    public byte[] ToBytes()
    {
        MessagePackWriter writer = new();
        Write(writer);

        return writer.ToArray();
    }
}

/// <summary>Nothing.</summary>
internal sealed record MessagePackNil : MessagePackValue
{
    private MessagePackNil()
    {
    }

    /// <summary>Gets the one nil there is.</summary>
    public static MessagePackNil Instance { get; } = new();

    /// <inheritdoc/>
    public override void Write(MessagePackWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteNil();
    }
}

/// <summary>True or false.</summary>
/// <param name="Value">Which.</param>
internal sealed record MessagePackBoolean(bool Value) : MessagePackValue
{
    /// <inheritdoc/>
    public override void Write(MessagePackWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Write(Value);
    }
}

/// <summary>A whole number.</summary>
/// <param name="Value">The number.</param>
internal sealed record MessagePackInteger(long Value) : MessagePackValue
{
    /// <inheritdoc/>
    public override void Write(MessagePackWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Write(Value);
    }
}

/// <summary>A number with a fractional part.</summary>
/// <param name="Value">The number.</param>
internal sealed record MessagePackFloat(double Value) : MessagePackValue
{
    /// <inheritdoc/>
    public override void Write(MessagePackWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Write(Value);
    }
}

/// <summary>Text.</summary>
/// <param name="Value">The text.</param>
internal sealed record MessagePackString(string Value) : MessagePackValue
{
    /// <inheritdoc/>
    public override void Write(MessagePackWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Write(Value);
    }
}

/// <summary>Bytes.</summary>
/// <param name="Value">The bytes.</param>
internal sealed record MessagePackBinary(ImmutableArray<byte> Value) : MessagePackValue
{
    /// <inheritdoc/>
    public override void Write(MessagePackWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteBinary(Value.AsSpan());
    }

    /// <inheritdoc/>
    public bool Equals(MessagePackBinary? other) => other is not null && Value.SequenceEqual(other.Value);

    /// <inheritdoc/>
    public override int GetHashCode() => Value.Length;
}

/// <summary>A value this build keeps but does not interpret.</summary>
/// <param name="Encoded">Its bytes, marker and all.</param>
/// <remarks>
/// The extension family. Nothing here writes one and a peer may, so the only safe thing to do with
/// one is hand back exactly what arrived.
/// </remarks>
internal sealed record MessagePackOpaque(ImmutableArray<byte> Encoded) : MessagePackValue
{
    /// <inheritdoc/>
    public override void Write(MessagePackWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteRaw(Encoded.AsSpan());
    }

    /// <inheritdoc/>
    public bool Equals(MessagePackOpaque? other)
        => other is not null && Encoded.SequenceEqual(other.Encoded);

    /// <inheritdoc/>
    public override int GetHashCode() => Encoded.Length;
}

/// <summary>A sequence.</summary>
/// <param name="Items">What is in it, in order.</param>
internal sealed record MessagePackArray(ImmutableArray<MessagePackValue> Items) : MessagePackValue
{
    /// <inheritdoc/>
    public override void Write(MessagePackWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteArrayHeader(Items.Length);

        foreach (MessagePackValue item in Items)
        {
            item.Write(writer);
        }
    }

    /// <summary>Applies a change to every element.</summary>
    /// <param name="change">What to do to each.</param>
    /// <returns>The new array.</returns>
    public MessagePackArray Select(Func<MessagePackValue, MessagePackValue> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        return new MessagePackArray([.. Items.Select(change)]);
    }

    /// <inheritdoc/>
    public bool Equals(MessagePackArray? other) => other is not null && Items.SequenceEqual(other.Items);

    /// <inheritdoc/>
    public override int GetHashCode() => Items.Length;
}

/// <summary>One entry of a map.</summary>
/// <param name="Key">Its key.</param>
/// <param name="Value">Its value.</param>
internal sealed record MessagePackPair(MessagePackValue Key, MessagePackValue Value);

/// <summary>Pairs, in the order they were written.</summary>
/// <param name="Pairs">The entries.</param>
/// <remarks>
/// A list rather than a dictionary. Two things need the order: re-encoding a document that was not
/// migrated has to reproduce its bytes exactly, and a migration that adds a field should be able to
/// say where it goes rather than having it land wherever a hash put it.
/// </remarks>
internal sealed record MessagePackMap(ImmutableArray<MessagePackPair> Pairs) : MessagePackValue
{
    /// <summary>Gets a map with nothing in it.</summary>
    public static MessagePackMap Empty { get; } = new([]);

    /// <inheritdoc/>
    public override void Write(MessagePackWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteMapHeader(Pairs.Length);

        foreach (MessagePackPair pair in Pairs)
        {
            pair.Key.Write(writer);
            pair.Value.Write(writer);
        }
    }

    /// <summary>Finds the value under a text key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The value, or null if the map has no such key.</returns>
    public MessagePackValue? Find(string key)
        => Pairs.FirstOrDefault(p => p.Key is MessagePackString name && name.Value == key)?.Value;

    /// <summary>Sets a text key, in place if it is already there and at the end if it is not.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <returns>The new map.</returns>
    public MessagePackMap With(string key, MessagePackValue value)
    {
        ArgumentNullException.ThrowIfNull(value);

        int at = IndexOf(key);
        MessagePackPair pair = new(new MessagePackString(key), value);

        return at < 0
            ? new MessagePackMap(Pairs.Add(pair))
            : new MessagePackMap(Pairs.SetItem(at, pair));
    }

    /// <summary>Removes a text key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The new map, or this one if the key was not there.</returns>
    public MessagePackMap Without(string key)
    {
        int at = IndexOf(key);

        return at < 0 ? this : new MessagePackMap(Pairs.RemoveAt(at));
    }

    /// <summary>Changes a key's name, keeping its value and its place.</summary>
    /// <param name="from">The old name.</param>
    /// <param name="to">The new name.</param>
    /// <returns>The new map, or this one if the old name was not there.</returns>
    /// <remarks>
    /// The commonest migration there is, and the one most easily got wrong by removing and adding:
    /// that moves the field to the end, which changes the bytes of every document the migration
    /// touches for no reason anyone reading the diff could explain.
    /// </remarks>
    public MessagePackMap Renamed(string from, string to)
    {
        int at = IndexOf(from);

        return at < 0
            ? this
            : new MessagePackMap(
                Pairs.SetItem(at, new MessagePackPair(new MessagePackString(to), Pairs[at].Value)));
    }

    private int IndexOf(string key)
    {
        for (int i = 0; i < Pairs.Length; ++i)
        {
            if (Pairs[i].Key is MessagePackString name && name.Value == key)
            {
                return i;
            }
        }

        return -1;
    }

    /// <inheritdoc/>
    public bool Equals(MessagePackMap? other) => other is not null && Pairs.SequenceEqual(other.Pairs);

    /// <inheritdoc/>
    public override int GetHashCode() => Pairs.Length;
}
