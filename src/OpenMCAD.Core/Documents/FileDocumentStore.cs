using System.Collections.Concurrent;
using System.Globalization;

using OpenMCAD.Core.Serialization;

namespace OpenMCAD.Core.Documents;

/// <summary>
/// Finds documents on disk, relative to a folder (P5-T12).
/// </summary>
/// <param name="root">
/// The folder a relative target is resolved against — for an assembly, normally the folder the
/// assembly itself is in, which is what makes a product movable as a whole.
/// </param>
/// <remarks>
/// <para>
/// <b>Opened documents are cached, and the stamp is the key.</b> A product where forty components
/// come from one library would otherwise read that library forty times in one load. The cached copy
/// is used only while the stamp still matches, so a document edited by another application between
/// two reads is picked up rather than served stale — which matters because a user with two
/// applications open is the ordinary case and not an exotic one.
/// </para>
/// <para>
/// <b>The stamp is the modification time and the length.</b> Cheap, which is the requirement:
/// <see cref="StampOf"/> is asked about every dependency whenever an out-of-date indicator is
/// drawn, and a content hash would read every file to answer. What that costs is real and worth
/// writing down — two edits inside the filesystem's timestamp granularity that leave the length
/// unchanged are indistinguishable, so a document can be reported current when it is not. A store
/// backed by a vault should use the revision the vault already knows, which is both cheaper and
/// exact; this is the honest best a bare filesystem offers.
/// </para>
/// <para>
/// A target that escapes <paramref name="root"/> is refused. A document is data, and a document
/// that could name <c>../../../etc/passwd</c> as a component would make opening an untrusted file
/// a way to read the machine. The comparison is against the root <em>plus a separator</em>, because
/// a bare prefix test lets a sibling folder whose name starts with the root's out through the
/// fence.
/// </para>
/// </remarks>
public sealed class FileDocumentStore(string root) : IDocumentStore
{
    private readonly string _root = Path.GetFullPath(root);
    private readonly string _fence = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
        + Path.DirectorySeparatorChar;

    private readonly ConcurrentDictionary<string, StoredDocument> _opened = new(StringComparer.Ordinal);

    /// <summary>Gets the folder relative targets are resolved against.</summary>
    public string Root => _root;

    /// <inheritdoc />
    public StoredDocument? Open(string target)
    {
        if (Locate(target) is not { } path)
        {
            return null;
        }

        string? stamp = StampOfFile(path);

        if (stamp is null)
        {
            return null;
        }

        if (_opened.TryGetValue(path, out StoredDocument? cached) && cached.Stamp == stamp)
        {
            return cached;
        }

        using FileStream stream = File.OpenRead(path);

        OpenedPackage opened = DocumentPackage.Open(stream);
        StoredDocument found = new(opened.Document, stamp, opened.Manifest);

        _opened[path] = found;

        return found;
    }

    /// <inheritdoc />
    public string? StampOf(string target)
        => Locate(target) is { } path ? StampOfFile(path) : null;

    /// <summary>Forgets everything opened, so the next read goes to disk.</summary>
    /// <remarks>
    /// For a caller that knows the world has changed underneath it in a way the stamps cannot see —
    /// a vault sync, a branch switch. Not needed in the ordinary case, where a changed stamp already
    /// bypasses the cache.
    /// </remarks>
    public void Forget() => _opened.Clear();

    /// <summary>
    /// Where a target would be, or null if it is not somewhere this store may look.
    /// </summary>
    /// <remarks>
    /// Says nothing about whether anything is there — that is <see cref="StampOfFile"/>'s single
    /// answer, and asking it here as well only meant two places could disagree.
    /// </remarks>
    private string? Locate(string target)
    {
        string full;

        try
        {
            full = Path.GetFullPath(Path.Combine(_root, target));
        }
        catch (ArgumentException)
        {
            // A target with characters no path can hold -- an embedded NUL, most likely, because
            // the target came out of a file. Not found rather than thrown: a damaged document must
            // not be able to crash the reader. This also answers a blank or null target, which is
            // why there is no separate check for one.
            return null;
        }

        // Compared after both have been made absolute, so that "..", a symlink-free relative path
        // and a differently-cased drive letter all reduce to the same question. The separator on
        // the end of the fence is load-bearing: without it a sibling folder whose name merely
        // starts with the root's -- "parts" and "parts-archive" -- would pass a prefix test.
        return full.StartsWith(_fence, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static string? StampOfFile(string path)
    {
        try
        {
            FileInfo file = new(path);

            return file.Exists
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"{file.LastWriteTimeUtc.Ticks:D}-{file.Length:D}")
                : null;
        }
        catch (IOException)
        {
            // Locked by something else, or on a drive that has just gone away. Reported as missing,
            // which is what it is from here: the answer cannot be had right now. This also catches
            // the file being deleted between the existence test above and reading its length, so
            // the two overlap on purpose -- the test is the ordinary path and this is the race.
            return null;
        }
    }
}
