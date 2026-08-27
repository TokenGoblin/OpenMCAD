using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using OpenMCAD.Core.Documents;

namespace OpenMCAD.Core.Serialization;

/// <summary>What kind of document a package holds.</summary>
public enum DocumentKind
{
    /// <summary>A single part.</summary>
    Part,

    /// <summary>An assembly of components.</summary>
    Assembly,

    /// <summary>A drawing.</summary>
    Drawing,
}

/// <summary>
/// What a package says about itself, before anything reads the graph.
/// </summary>
/// <param name="FormatVersion">The container layout's version.</param>
/// <param name="SchemaVersion">The document graph's version, inside the container.</param>
/// <param name="Application">Which build wrote it, for a bug report.</param>
/// <param name="Kind">Part, assembly or drawing.</param>
/// <param name="DocumentId">This document's identity, stable across saves and renames.</param>
/// <param name="Created">When it was first made.</param>
/// <param name="Modified">When it was last written.</param>
/// <remarks>
/// <para>
/// Plain JSON, and deliberately so: it is the part a person reads when a file will not open, and
/// the part a tool that is not this program reads to decide whether it wants to. Making the
/// outermost layer of a binary format legible costs a few hundred bytes and buys every diagnosis
/// after the first.
/// </para>
/// <para>
/// <b>The timestamps are given rather than taken.</b> A manifest that stamped
/// <see cref="Modified"/> from the clock on every save would make it impossible for saving the
/// same document twice to produce the same bytes, and Phase 3's first exit criterion asks for
/// exactly that. So the caller decides, and can decide to leave it alone when nothing changed.
/// </para>
/// </remarks>
public sealed record DocumentManifest(
    int FormatVersion,
    int SchemaVersion,
    string Application,
    DocumentKind Kind,
    Guid DocumentId,
    DateTimeOffset Created,
    DateTimeOffset Modified)
{
    /// <summary>The container layout this build writes.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>A manifest for a document being written for the first time.</summary>
    /// <param name="application">Which build is writing it.</param>
    /// <param name="kind">What sort of document it is.</param>
    /// <param name="at">The time to record.</param>
    /// <returns>The manifest.</returns>
    public static DocumentManifest ForNewDocument(
        string application, DocumentKind kind, DateTimeOffset at) => new(
            CurrentFormatVersion,
            DocumentCodec.SchemaVersion,
            application,
            kind,
            Guid.NewGuid(),
            at,
            at);
}

/// <summary>
/// The parts of a package that are not the document graph.
/// </summary>
/// <param name="Geometry">Cached kernel shapes, by feature id.</param>
/// <param name="Tessellation">Cached display meshes, by body id.</param>
/// <param name="Thumbnail">A picture for a file browser, or null.</param>
/// <param name="Previews">Per-configuration pictures, by configuration name.</param>
/// <param name="ExternalReferences">The contents of <c>/refs/external.json</c>, or null.</param>
/// <param name="Custom">Anything a plugin or a user put in <c>/custom/</c>.</param>
/// <remarks>
/// Carried as opaque bytes. §5.8 makes the geometry and tessellation caches regenerable and never
/// the source of truth, and this layer is not the one that knows how to regenerate them — its job
/// is to keep what it was given and hand back what it found, including anything written by a build
/// or a plugin this one has never heard of.
/// </remarks>
public sealed record PackageContents(
    ImmutableDictionary<string, byte[]> Geometry,
    ImmutableDictionary<string, byte[]> Tessellation,
    byte[]? Thumbnail = null,
    ImmutableDictionary<string, byte[]>? Previews = null,
    byte[]? ExternalReferences = null,
    ImmutableDictionary<string, byte[]>? Custom = null)
{
    /// <summary>Gets contents with nothing in them.</summary>
    public static PackageContents Empty { get; } = new(
        ImmutableDictionary<string, byte[]>.Empty,
        ImmutableDictionary<string, byte[]>.Empty);
}

/// <summary>What was found in a package.</summary>
/// <param name="Manifest">What it said about itself.</param>
/// <param name="Document">The graph.</param>
/// <param name="Contents">Everything else, including the regenerable caches.</param>
public sealed record OpenedPackage(
    DocumentManifest Manifest, Document Document, PackageContents Contents);

/// <summary>
/// Reads and writes the <c>.ompart</c> container.
/// </summary>
/// <remarks>
/// <para>
/// A Zip holding the layout §5.8 defines: a JSON manifest, the document graph as MessagePack, and
/// optional caches, pictures and custom parts beside them.
/// </para>
/// <para>
/// <b>Written deterministically.</b> Entries go in a fixed order, every timestamp is the Zip
/// epoch, and the compression level is fixed — so saving the same document with the same manifest
/// twice produces the same bytes. Left to itself a Zip records the wall clock on every entry,
/// which would make two saves of an unchanged document differ and Phase 3's first exit criterion
/// unmeetable.
/// </para>
/// <para>
/// <b>A missing or unreadable cache is not an error.</b> §5.8: caches are always regenerable, so a
/// corrupt one means rebuild rather than data loss. <see cref="Open"/> takes a mode that ignores
/// them entirely, which is the <c>--no-cache</c> the plan asks be testable.
/// </para>
/// </remarks>
public static class DocumentPackage
{
    /// <summary>The manifest's name inside the container.</summary>
    public const string ManifestEntry = "manifest.json";

    /// <summary>The document graph's name inside the container.</summary>
    public const string DocumentEntry = "document.msgpack";

    /// <summary>
    /// The timestamp every entry carries.
    /// </summary>
    /// <remarks>
    /// The Zip format's own epoch. A real time here would be recorded in each entry's header and
    /// would differ between two saves of an unchanged document; when a file was written is the
    /// manifest's business, where it can be seen and controlled.
    /// </remarks>
    private static readonly DateTimeOffset FixedTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions ManifestFormat = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Writes a package.</summary>
    /// <param name="stream">Where to write it.</param>
    /// <param name="document">The graph.</param>
    /// <param name="manifest">What the package should say about itself.</param>
    /// <param name="contents">The caches and extra parts, or null for none.</param>
    public static void Save(
        Stream stream,
        Document document,
        DocumentManifest manifest,
        PackageContents? contents = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(manifest);

        contents ??= PackageContents.Empty;

        using ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true);

        Put(archive, ManifestEntry, JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestFormat));
        Put(archive, DocumentEntry, DocumentCodec.Write(document));

        PutAll(archive, "geometry/", contents.Geometry, ".brep");
        PutAll(archive, "tessellation/", contents.Tessellation, ".mesh");

        if (contents.Thumbnail is { } thumbnail)
        {
            Put(archive, "thumbnail.png", thumbnail);
        }

        PutAll(archive, "preview/", contents.Previews, ".png");

        if (contents.ExternalReferences is { } refs)
        {
            Put(archive, "refs/external.json", refs);
        }

        PutAll(archive, "custom/", contents.Custom, string.Empty);
    }

    /// <summary>Reads a package.</summary>
    /// <param name="stream">Where to read it from.</param>
    /// <param name="useCaches">
    /// Whether to load the regenerable caches. False is the <c>--no-cache</c> mode §5.8 asks be
    /// testable: the same reader, with the caches ignored, so that the two can be compared.
    /// </param>
    /// <returns>What was found.</returns>
    /// <exception cref="DocumentFormatException">It is not a package this build can read.</exception>
    public static OpenedPackage Open(Stream stream, bool useCaches = true)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: true);

        DocumentManifest manifest = ReadManifest(archive);

        if (manifest.FormatVersion > DocumentManifest.CurrentFormatVersion)
        {
            throw new DocumentFormatException(
                $"This file uses container format {manifest.FormatVersion}, and this build reads "
                + $"{DocumentManifest.CurrentFormatVersion}. It was written by a newer version of "
                + "OpenMCAD.");
        }

        ZipArchiveEntry graph = archive.GetEntry(DocumentEntry)
            ?? throw new DocumentFormatException(
                $"This file has no {DocumentEntry}, so it holds no document.");

        Document document = DocumentCodec.Read(Read(graph));

        ImmutableDictionary<string, byte[]>.Builder geometry = Builder();
        ImmutableDictionary<string, byte[]>.Builder tessellation = Builder();
        ImmutableDictionary<string, byte[]>.Builder previews = Builder();
        ImmutableDictionary<string, byte[]>.Builder custom = Builder();

        byte[]? thumbnail = null;
        byte[]? externals = null;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName;

            if (name is ManifestEntry or DocumentEntry)
            {
                continue;
            }

            if (name.StartsWith("geometry/", StringComparison.Ordinal))
            {
                if (useCaches)
                {
                    geometry[Stem(name, "geometry/")] = Read(entry);
                }
            }
            else if (name.StartsWith("tessellation/", StringComparison.Ordinal))
            {
                if (useCaches)
                {
                    tessellation[Stem(name, "tessellation/")] = Read(entry);
                }
            }
            else if (name == "thumbnail.png")
            {
                thumbnail = Read(entry);
            }
            else if (name.StartsWith("preview/", StringComparison.Ordinal))
            {
                previews[Stem(name, "preview/")] = Read(entry);
            }
            else if (name == "refs/external.json")
            {
                externals = Read(entry);
            }
            else if (name.StartsWith("custom/", StringComparison.Ordinal))
            {
                // Kept whole, including the extension: this build has no idea what any of it is,
                // and a plugin that wrote settings.json expects settings.json back.
                custom[name["custom/".Length..]] = Read(entry);
            }
        }

        return new OpenedPackage(
            manifest,
            document,
            new PackageContents(
                geometry.ToImmutable(),
                tessellation.ToImmutable(),
                thumbnail,
                previews.ToImmutable(),
                externals,
                custom.ToImmutable()));
    }

    private static DocumentManifest ReadManifest(ZipArchive archive)
    {
        ZipArchiveEntry entry = archive.GetEntry(ManifestEntry)
            ?? throw new DocumentFormatException(
                $"This file has no {ManifestEntry}, so it is not an OpenMCAD document.");

        try
        {
            return JsonSerializer.Deserialize<DocumentManifest>(Read(entry), ManifestFormat)
                ?? throw new DocumentFormatException($"The {ManifestEntry} in this file is empty.");
        }
        catch (JsonException exception)
        {
            throw new DocumentFormatException(
                $"The {ManifestEntry} in this file is not readable: {exception.Message}",
                exception);
        }
    }

    private static ImmutableDictionary<string, byte[]>.Builder Builder()
        => ImmutableDictionary.CreateBuilder<string, byte[]>(StringComparer.Ordinal);

    private static string Stem(string name, string folder)
    {
        string rest = name[folder.Length..];
        int dot = rest.LastIndexOf('.');

        return dot < 0 ? rest : rest[..dot];
    }

    private static void PutAll(
        ZipArchive archive,
        string folder,
        ImmutableDictionary<string, byte[]>? parts,
        string extension)
    {
        if (parts is null)
        {
            return;
        }

        // Sorted, because a dictionary has no order and the entry order is part of the bytes.
        foreach (string key in parts.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            Put(archive, $"{folder}{key}{extension}", parts[key]);
        }
    }

    private static void Put(ZipArchive archive, string name, byte[] data)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = FixedTimestamp;

        using Stream stream = entry.Open();
        stream.Write(data);
    }

    private static byte[] Read(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using MemoryStream buffer = new();

        stream.CopyTo(buffer);

        return buffer.ToArray();
    }
}
