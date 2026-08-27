using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Serialization;
using OpenMCAD.Modeling;

namespace OpenMCAD.Cli;

/// <summary>
/// The headless document API: build, rebuild, inspect, save and diff, with no window anywhere.
/// </summary>
/// <remarks>
/// <para>
/// P3-T22, and the plan is explicit that every later phase tests through it. That makes two things
/// requirements rather than niceties. Everything is a method returning an exit code and writing to
/// a <see cref="TextWriter"/>, so a test can call it without starting a process; and everything can
/// answer in JSON, so a test can assert on a field rather than on the wording of a sentence that
/// will be rephrased.
/// </para>
/// <para>
/// Exit codes follow the shell's conventions rather than inventing any: 0 for success, 1 for "the
/// question has a negative answer" (documents differ, a rebuild has failures), 2 for "the command
/// could not be carried out at all". A script that treats a missing file the same as a genuine
/// difference is a script that reports success when the file was never there.
/// </para>
/// </remarks>
public static class DocumentCommands
{
    /// <summary>The exit code when a command did what it was asked.</summary>
    public const int Ok = 0;

    /// <summary>The exit code when the answer is no: documents differ, or a rebuild failed.</summary>
    public const int Negative = 1;

    /// <summary>The exit code when the command could not be carried out.</summary>
    public const int Failed = 2;

    /// <summary>What a built document records as its creation time.</summary>
    /// <remarks>
    /// Fixed, not the clock. Building the same spec twice has to produce the same file, or a
    /// document built by a test could never be compared with a stored one -- which is the whole
    /// reason later phases build documents through this tool. When a file was actually written is
    /// the filesystem's business and it already records it.
    /// </remarks>
    public static readonly DateTimeOffset BuildTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Builds a document from a spec and writes it out.</summary>
    /// <param name="spec">The spec file.</param>
    /// <param name="output">Where to write the package.</param>
    /// <param name="json">Whether to answer in JSON.</param>
    /// <param name="writer">Where to write the answer.</param>
    /// <returns>An exit code.</returns>
    public static int Build(FileInfo spec, FileInfo output, bool json, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(writer);

        return Guarded(writer, json, () =>
        {
            Document document = DocumentSpec.Parse(File.ReadAllText(spec.FullName)).Build();

            Write(document, output, spec.Name);

            Say(
                writer,
                json,
                $"Built {document.Features.Length} features into {output.Name}.",
                new
                {
                    built = output.FullName,
                    features = document.Features.Length,
                    parameters = document.Parameters.Count,
                });

            return Ok;
        });
    }

    /// <summary>Describes a document.</summary>
    /// <param name="package">The package to open.</param>
    /// <param name="json">Whether to answer in JSON.</param>
    /// <param name="writer">Where to write the answer.</param>
    /// <param name="useCaches">Whether to load the regenerable caches.</param>
    /// <returns>An exit code.</returns>
    public static int Inspect(
        FileInfo package, bool json, TextWriter writer, bool useCaches = true)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(writer);

        return Guarded(writer, json, () =>
        {
            OpenedPackage opened = Open(package, useCaches);
            Document document = opened.Document;

            if (json)
            {
                writer.WriteLine(JsonSerializer.Serialize(Describe(opened), DocumentSpec.Format));
                return Ok;
            }

            writer.WriteLine($"{package.Name}");
            writer.WriteLine(
                $"  schema {opened.Manifest.SchemaVersion}, format "
                + $"{opened.Manifest.FormatVersion}, written by {opened.Manifest.Application}");
            writer.WriteLine($"  title      : {document.Metadata.Title ?? "(none)"}");
            writer.WriteLine($"  part       : {document.Metadata.PartNumber ?? "(none)"}");
            writer.WriteLine($"  revision   : {document.Metadata.Revision ?? "(none)"}");
            writer.WriteLine(
                $"  rollback   : {(document.RollbackPosition is { } bar ? bar.ToString(CultureInfo.InvariantCulture) : "(none)")}");

            writer.WriteLine($"  parameters : {document.Parameters.Count}");

            foreach (Parameter parameter in document.Parameters.OrderBy(
                p => p.Name, StringComparer.Ordinal))
            {
                writer.WriteLine(
                    $"    {parameter.Name} = {parameter.Value}"
                    + (parameter.Expression is { } expression ? $"  [{expression}]" : string.Empty));
            }

            writer.WriteLine($"  features   : {document.Features.Length}");

            for (int i = 0; i < document.Features.Length; ++i)
            {
                Feature feature = document.Features[i];

                writer.WriteLine(
                    $"    {i,3}  {feature.Name} ({feature.FeatureType})"
                    + (feature.IsSuppressed ? "  suppressed" : string.Empty)
                    + (i >= document.ActiveFeatureCount ? "  rolled back" : string.Empty));
            }

            writer.WriteLine($"  bodies     : {document.Bodies.Count}");

            if (document.UnreadFieldCount > 0)
            {
                // Worth saying out loud. It means the file came from a build that knows things this
                // one does not, and anyone editing it should know that before they save over it.
                writer.WriteLine(
                    $"  carried    : {document.UnreadFieldCount} fields from a newer version, "
                    + "kept and not understood");
            }

            return Ok;
        });
    }

    /// <summary>Opens a document and writes it out again.</summary>
    /// <param name="package">The package to open.</param>
    /// <param name="output">Where to write it. The same path re-saves in place.</param>
    /// <param name="json">Whether to answer in JSON.</param>
    /// <param name="writer">Where to write the answer.</param>
    /// <param name="useCaches">Whether to load the regenerable caches.</param>
    /// <returns>An exit code.</returns>
    /// <remarks>
    /// What the determinism gate runs. §5.8's first exit criterion is that a document is
    /// bit-identical on re-save, and this is the command that lets that be checked from a shell
    /// rather than only from inside a test.
    /// </remarks>
    public static int Save(
        FileInfo package, FileInfo output, bool json, TextWriter writer, bool useCaches = true)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(writer);

        return Guarded(writer, json, () =>
        {
            OpenedPackage opened = Open(package, useCaches);

            byte[] before = File.ReadAllBytes(package.FullName);

            using (FileStream stream = output.Create())
            {
                DocumentPackage.Save(
                    stream, opened.Document, opened.Manifest, opened.Contents);
            }

            bool identical = File.ReadAllBytes(output.FullName).AsSpan().SequenceEqual(before);

            Say(
                writer,
                json,
                identical
                    ? $"Re-saved {output.Name}, byte for byte the same."
                    : $"Re-saved {output.Name}, and the bytes changed.",
                new { saved = output.FullName, identical });

            return Ok;
        });
    }

    /// <summary>Compares two documents.</summary>
    /// <param name="left">The first package.</param>
    /// <param name="right">The second package.</param>
    /// <param name="json">Whether to answer in JSON.</param>
    /// <param name="writer">Where to write the answer.</param>
    /// <returns><see cref="Ok"/> if they describe the same model, <see cref="Negative"/> if not.</returns>
    /// <remarks>
    /// Compares the documents rather than the bytes. Two files can differ byte for byte and describe
    /// the same model — a different manifest timestamp is enough — and a diff that reported those as
    /// changes would be useless for the thing a diff is for.
    /// </remarks>
    public static int Diff(FileInfo left, FileInfo right, bool json, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(writer);

        return Guarded(writer, json, () =>
        {
            Document a = Open(left, useCaches: false).Document;
            Document b = Open(right, useCaches: false).Document;

            ImmutableArray<string> differences = Differences(a, b);

            if (json)
            {
                writer.WriteLine(JsonSerializer.Serialize(
                    new { same = differences.IsEmpty, differences },
                    DocumentSpec.Format));
            }
            else if (differences.IsEmpty)
            {
                writer.WriteLine($"{left.Name} and {right.Name} describe the same model.");
            }
            else
            {
                writer.WriteLine($"{left.Name} and {right.Name} differ:");

                foreach (string difference in differences)
                {
                    writer.WriteLine($"  {difference}");
                }
            }

            return differences.IsEmpty ? Ok : Negative;
        });
    }

    /// <summary>Checks a document against what this build knows how to make.</summary>
    /// <param name="package">The package to open.</param>
    /// <param name="json">Whether to answer in JSON.</param>
    /// <param name="writer">Where to write the answer.</param>
    /// <param name="catalogue">The features this build knows. Empty until Phase 5 declares any.</param>
    /// <returns><see cref="Ok"/> if nothing is wrong, <see cref="Negative"/> if something is.</returns>
    /// <remarks>
    /// The pre-flight half of a rebuild, which is all of it that exists before Phase 5 gives
    /// features something to do. Wired now rather than later so that the command's shape, its
    /// output and its exit codes are settled before anything depends on them — and so that the
    /// catalogue growing turns this from a shell into a real check without a caller changing.
    /// </remarks>
    public static int Rebuild(
        FileInfo package, bool json, TextWriter writer, FeatureCatalogue? catalogue = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(writer);

        return Guarded(writer, json, () =>
        {
            Document document = Open(package, useCaches: false).Document;

            ImmutableArray<SchemaViolation> violations =
                (catalogue ?? FeatureCatalogue.Empty).Validate(document);

            bool failed = violations.Any(v => v.IsError);

            if (json)
            {
                writer.WriteLine(JsonSerializer.Serialize(
                    new
                    {
                        features = document.Features.Length,
                        errors = violations.Count(v => v.IsError),
                        warnings = violations.Count(v => !v.IsError),
                        violations = violations.Select(v => new
                        {
                            feature = v.Feature.ToStorageString(),
                            property = v.Property,
                            severity = v.Severity.ToString(),
                            message = v.Message,
                        }),
                    },
                    DocumentSpec.Format));

                return failed ? Negative : Ok;
            }

            writer.WriteLine(
                $"{package.Name}: {document.Features.Length} features, {violations.Length} things "
                + "to say.");

            foreach (SchemaViolation violation in violations)
            {
                writer.WriteLine(
                    $"  {(violation.IsError ? "error  " : "warning")} {violation.Property}: "
                    + violation.Message);
            }

            return failed ? Negative : Ok;
        });
    }

    /// <summary>What is different between two documents, in words.</summary>
    /// <param name="left">The first document.</param>
    /// <param name="right">The second document.</param>
    /// <returns>One line per difference, empty if there are none.</returns>
    /// <remarks>
    /// Reported as a list rather than as a yes or no, because <see cref="Document.Matches"/> already
    /// answers that and a person looking at two files that should be the same needs to know which
    /// part is not.
    /// </remarks>
    public static ImmutableArray<string> Differences(Document left, Document right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        ImmutableArray<string>.Builder found = ImmutableArray.CreateBuilder<string>();

        if (left.Metadata != right.Metadata)
        {
            found.Add("the document properties differ");
        }

        if (left.RollbackPosition != right.RollbackPosition)
        {
            found.Add(
                $"the rollback bar is at {Describe(left.RollbackPosition)} and "
                + $"{Describe(right.RollbackPosition)}");
        }

        Compare(
            found,
            "feature",
            [.. left.Features.Select(f => f.Name)],
            [.. right.Features.Select(f => f.Name)]);

        foreach (Feature feature in left.Features)
        {
            Feature? other = right.Features.FirstOrDefault(
                f => string.Equals(f.Name, feature.Name, StringComparison.Ordinal));

            if (other is not null && other != feature)
            {
                found.Add($"feature '{feature.Name}' is not the same in both");
            }
        }

        Compare(
            found,
            "parameter",
            [.. left.Parameters.Select(p => p.Name)],
            [.. right.Parameters.Select(p => p.Name)]);

        foreach (Parameter parameter in left.Parameters)
        {
            Parameter? other = right.FindParameter(parameter.Name);

            if (other is not null && other != parameter)
            {
                found.Add(
                    $"parameter '{parameter.Name}' is {parameter.Value} and {other.Value}");
            }
        }

        return found.ToImmutable();
    }

    private static void Compare(
        ImmutableArray<string>.Builder found,
        string what,
        ImmutableArray<string> left,
        ImmutableArray<string> right)
    {
        foreach (string name in left.Except(right, StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal))
        {
            found.Add($"{what} '{name}' is only in the first");
        }

        foreach (string name in right.Except(left, StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal))
        {
            found.Add($"{what} '{name}' is only in the second");
        }

        if (left.Length == right.Length
            && !left.Except(right, StringComparer.Ordinal).Any()
            && !left.SequenceEqual(right, StringComparer.Ordinal))
        {
            // The same set in a different order. For features that is a real difference -- the tree
            // order is what the user arranged and what a rebuild follows -- and reporting it as
            // "no differences" would hide a reorder entirely.
            found.Add($"the {what}s are in a different order");
        }
    }

    private static string Describe(int? rollback)
        => rollback is { } bar ? bar.ToString(CultureInfo.InvariantCulture) : "the end";

    private static object Describe(OpenedPackage opened) => new
    {
        schema = opened.Manifest.SchemaVersion,
        format = opened.Manifest.FormatVersion,
        application = opened.Manifest.Application,
        kind = opened.Manifest.Kind.ToString(),
        title = opened.Document.Metadata.Title,
        part = opened.Document.Metadata.PartNumber,
        revision = opened.Document.Metadata.Revision,
        rollback = opened.Document.RollbackPosition,
        carried = opened.Document.UnreadFieldCount,
        parameters = opened.Document.Parameters
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new
            {
                name = p.Name,
                value = p.Value.Value,
                dimension = p.Value.Dimension.ToString(),
                expression = p.Expression,
            }),
        features = opened.Document.Features.Select((f, i) => new
        {
            index = i,
            id = f.Id.ToStorageString(),
            name = f.Name,
            type = f.FeatureType,
            suppressed = f.IsSuppressed,
            rolledBack = i >= opened.Document.ActiveFeatureCount,
            inputs = f.Inputs.Select(input => input.ToStorageString()),
        }),
        bodies = opened.Document.Bodies
            .OrderBy(b => b.Id.ToStorageString(), StringComparer.Ordinal)
            .Select(b => new { id = b.Id.ToStorageString(), name = b.Name, kind = b.Kind.ToString() }),
    };

    private static OpenedPackage Open(FileInfo package, bool useCaches)
    {
        if (!package.Exists)
        {
            throw new FileNotFoundException($"There is no file at {package.FullName}.", package.FullName);
        }

        using FileStream stream = package.OpenRead();

        return DocumentPackage.Open(stream, useCaches);
    }

    private static void Write(Document document, FileInfo output, string application)
    {
        // A fixed identity and fixed timestamps, so that building the same spec twice produces the
        // same file. A build stamped with the wall clock could never be compared with a stored one,
        // which is exactly what the later phases that test through this need to do.
        DocumentManifest manifest = new(
            DocumentManifest.CurrentFormatVersion,
            DocumentCodec.SchemaVersion,
            $"omcad build ({application})",
            DocumentKind.Part,
            Identity(application),
            BuildTimestamp,
            BuildTimestamp);

        output.Directory?.Create();

        using FileStream stream = output.Create();

        DocumentPackage.Save(stream, document, manifest);
    }

    private static Guid Identity(string application)
        => new(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"omcad:{application}")).AsSpan(0, 16));

    private static void Say(TextWriter writer, bool json, string sentence, object structured)
        => writer.WriteLine(
            json ? JsonSerializer.Serialize(structured, DocumentSpec.Format) : sentence);

    /// <summary>Runs a command, turning anything it throws into a message and an exit code.</summary>
    /// <remarks>
    /// A stack trace is what a developer wants and never what someone running a command-line tool
    /// wants. Every exception this can produce has a message written to be read by a person, and
    /// the trace is available by asking for it (<c>--verbose</c>) rather than by default.
    /// </remarks>
    private static int Guarded(TextWriter writer, bool json, Func<int> command)
    {
        try
        {
            return command();
        }
        catch (Exception failure) when (failure
            is SpecException
            or DocumentFormatException
            or FileNotFoundException
            or DirectoryNotFoundException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            writer.WriteLine(
                json
                    ? JsonSerializer.Serialize(
                        new { error = failure.Message }, DocumentSpec.Format)
                    : failure.Message);

            return Failed;
        }
    }
}
