using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using OpenMCAD.Core.Documents;

namespace OpenMCAD.Core.Serialization;

/// <summary>
/// Reads and writes <c>/refs/external.json</c>, the container part that says what a document
/// depends on (P5-T11, §5.8).
/// </summary>
/// <remarks>
/// <para>
/// <b>JSON, and in its own part, on purpose.</b> Everything else about a document is MessagePack
/// inside <c>document.msgpack</c>, and this deliberately is not. The audience is a file browser, a
/// PDM system or an open dialog asking "what does this need?" — and for a large assembly, parsing
/// the graph to answer that is most of the cost of opening the file. A small JSON part is readable
/// by anything, including tools nobody here will write.
/// </para>
/// <para>
/// <b>Written deterministically</b>, like the rest of the container: references in the order the
/// document holds them, no indentation, and a fixed property order from the record's own shape. Two
/// saves of an unchanged document have to produce the same bytes (§3 of <c>persistence.md</c>), and
/// a serialiser left to its own devices about ordering would break that.
/// </para>
/// <para>
/// <b>Enums by name, not by number.</b> A number means whatever the declaration order happened to
/// be when the file was written, so inserting a state would silently change what every existing
/// file says — the same trap <c>ChoiceValue</c> avoids by storing an option's name. This part is
/// also the one most likely to be read by something that is not this program, and a name is the
/// only form such a reader can interpret.
/// </para>
/// </remarks>
public static class ExternalReferenceFormat
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Writes a document's external references.</summary>
    /// <param name="references">What the document depends on.</param>
    /// <returns>
    /// The bytes of <c>/refs/external.json</c>, or <see langword="null"/> when there are none.
    /// </returns>
    /// <remarks>
    /// Null rather than an empty array, so a document that depends on nothing carries no such part
    /// at all — the same call a part makes about the assembly section, and for the same reason: the
    /// bytes should be a function of the document, and an empty part in every file written so far
    /// would change all of them to say nothing.
    /// </remarks>
    public static byte[]? Write(ImmutableArray<ExternalReference> references)
    {
        if (references.IsDefaultOrEmpty)
        {
            return null;
        }

        Stored[] stored =
        [
            .. references.Select(r => new Stored(r.Target, r.Stamp, r.State, r.Note)),
        ];

        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(stored, Options));
    }

    /// <summary>Reads a document's external references.</summary>
    /// <param name="data">The bytes of <c>/refs/external.json</c>, or null.</param>
    /// <returns>What the document depends on, empty when the part is absent.</returns>
    /// <exception cref="DocumentFormatException">The part is not readable.</exception>
    /// <remarks>
    /// An absent part means a document that depends on nothing, which is what every file written
    /// before this existed says. A <em>malformed</em> part is a different matter and is refused:
    /// unlike a cache, this cannot be regenerated — the stamps in it are the only record of what
    /// the targets were when they were last read, and inventing them would report a stale document
    /// as current.
    /// </remarks>
    public static ImmutableArray<ExternalReference> Read(byte[]? data)
    {
        if (data is null || data.Length == 0)
        {
            return [];
        }

        try
        {
            Stored[]? stored = JsonSerializer.Deserialize<Stored[]>(data, Options);

            if (stored is null)
            {
                return [];
            }

            ImmutableArray<ExternalReference>.Builder found =
                ImmutableArray.CreateBuilder<ExternalReference>(stored.Length);

            foreach (Stored one in stored)
            {
                if (string.IsNullOrEmpty(one.Target))
                {
                    throw new DocumentFormatException(
                        "This document records a dependency without saying what it depends on.");
                }

                found.Add(new ExternalReference(
                    one.Target, one.Stamp ?? string.Empty, one.State, one.Note));
            }

            return found.ToImmutable();
        }
        catch (JsonException exception)
        {
            throw new DocumentFormatException(
                $"This document's list of what it depends on is damaged: {exception.Message}",
                exception);
        }
    }

    /// <summary>The shape on disk.</summary>
    /// <remarks>
    /// Separate from <see cref="ExternalReference"/> rather than serialising that directly. The
    /// record is free to grow computed members, change its constructor or take on behaviour; this
    /// is a file format and may not. Keeping them apart means a refactor cannot silently rewrite
    /// what a file means.
    /// </remarks>
    private sealed record Stored(
        string Target,
        string? Stamp,
        ExternalReferenceState State,
        string? Note);
}
