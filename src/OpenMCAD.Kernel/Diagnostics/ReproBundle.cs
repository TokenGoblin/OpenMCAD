using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenMCAD.Kernel.Operations;

namespace OpenMCAD.Kernel.Diagnostics;

/// <summary>How repro-bundle capture behaves.</summary>
/// <param name="Enabled">
/// Whether to capture at all. Off by default: writing B-rep for every failed operation costs real
/// time and disk, and a rebuild with a broken feature can fail hundreds of times.
/// </param>
/// <param name="Directory">Where bundles are written.</param>
/// <param name="MaxBundles">
/// How many bundles to keep before refusing to write more, or zero for
/// <see cref="DefaultMaxBundles"/>. A runaway rebuild must not fill the disk; refusing is better
/// than evicting, because the first failure is usually the interesting one.
/// </param>
/// <param name="OmitInputGeometry">
/// Whether to leave the input shapes out. Writing them is what makes a bundle reproducible rather
/// than merely descriptive, and it is also nearly all of the cost.
/// </param>
/// <remarks>
/// <para>
/// <b>Every field means the safe thing when zero or false.</b> That is not a style choice. A
/// <c>record struct</c> reaches its primary-constructor defaults only through the constructor, so
/// <c>default</c> and <c>new ReproBundleOptions { Enabled = true }</c> both bypass them. An earlier
/// version declared <c>MaxBundles = 50</c> and <c>IncludeInputGeometry = true</c> positionally,
/// which meant the second of those -- code that reads exactly like the documented configuration --
/// silently produced a writer that refused to capture anything, and would have dropped the input
/// geometry if it had.
/// </para>
/// <para>
/// Hence the negative form of <see cref="OmitInputGeometry"/>. A negative flag is mildly ugly and
/// is the price of the field behaving correctly however it was constructed.
/// </para>
/// </remarks>
public readonly record struct ReproBundleOptions(
    bool Enabled = false,
    string? Directory = null,
    int MaxBundles = 0,
    bool OmitInputGeometry = false)
{
    /// <summary>The bundle limit used when <see cref="MaxBundles"/> is not set.</summary>
    public const int DefaultMaxBundles = 50;

    /// <summary>Gets the bundle limit actually in force.</summary>
    public int EffectiveMaxBundles => MaxBundles > 0 ? MaxBundles : DefaultMaxBundles;

    /// <summary>Gets a value indicating whether input geometry is written.</summary>
    public bool IncludeInputGeometry => !OmitInputGeometry;

    /// <summary>Gets options with capture switched on, writing to the default directory.</summary>
    /// <param name="directory">Where to write, or null for the default.</param>
    public static ReproBundleOptions On(string? directory = null)
        => new(Enabled: true, Directory: directory);

    /// <summary>Gets the directory bundles are written to.</summary>
    public string ResolvedDirectory => Directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenMCAD",
        "repro-bundles");
}

/// <summary>
/// Captures everything needed to reproduce a kernel failure, as a directory on disk.
/// </summary>
/// <remarks>
/// <para>
/// P1-T13, implementing PLAN.md 6.1: "Every kernel failure captures a repro bundle: the input
/// shapes as BREP, the operation and parameters, the tolerance, and the OCCT exception. This turns
/// a bug report into a regression fixture in one step, and is how you build a robustness corpus
/// faster than your users find bugs."
/// </para>
/// <para>
/// That last clause is the whole justification. OCCT's failures are concentrated in inputs nobody
/// would think to write a test for — tangencies, near-coincident faces, self-intersecting blend
/// chains (ADR-0001). Those cases arrive from users, and a bundle is the difference between "a
/// fillet failed on some model" and a fixture that goes straight into <c>tests/regression</c>.
/// </para>
/// <para>
/// <b>Bundles are named by content hash, not by timestamp.</b> A rebuild loop that fails the same
/// way two hundred times therefore produces one bundle rather than two hundred, and re-running a
/// failing case overwrites its bundle instead of accumulating near-duplicates. The count in the
/// manifest records how often it recurred.
/// </para>
/// </remarks>
public sealed class ReproBundleWriter(
    ReproBundleOptions options,
    KernelCapabilities capabilities,
    ILogger? logger = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly HashSet<string> _written = new(StringComparer.Ordinal);

    /// <summary>Gets a value indicating whether capture is switched on.</summary>
    public bool Enabled => options.Enabled;

    /// <summary>Gets the bundles written by this instance, most recent last.</summary>
    public IReadOnlyCollection<string> Written => _written;

    /// <summary>
    /// Captures a failure. Runs on the kernel thread, so <paramref name="writeBRep"/> may call the
    /// kernel directly.
    /// </summary>
    /// <param name="operation">The operation name.</param>
    /// <param name="definition">The definition that failed, if there was one.</param>
    /// <param name="diagnostics">Why it failed.</param>
    /// <param name="request">The per-call options, which carry the tolerance.</param>
    /// <param name="writeBRep">
    /// Writes one input shape to bytes, or returns an empty array if it cannot. Called only when
    /// <see cref="ReproBundleOptions.IncludeInputGeometry"/> is set.
    /// </param>
    /// <returns>The bundle directory, or <see langword="null"/> if nothing was written.</returns>
    /// <remarks>
    /// Never throws. A failure to record a failure must not become a second, more confusing
    /// failure — the operation has already gone wrong and the caller is about to be told so.
    /// </remarks>
    public string? Capture(
        string operation,
        IOperationDefinition? definition,
        ImmutableArray<KernelDiagnostic> diagnostics,
        KernelRequest request,
        Func<KernelShape, ImmutableArray<byte>> writeBRep)
    {
        if (!options.Enabled)
        {
            return null;
        }

        try
        {
            ImmutableArray<KernelShape> inputs = definition?.InputShapes() ?? [];
            string fingerprint = Fingerprint(operation, definition, diagnostics);
            string directory = Path.Combine(options.ResolvedDirectory, $"{operation}-{fingerprint}");

            if (_written.Contains(directory))
            {
                RecordRecurrence(directory);
                return directory;
            }

            if (_written.Count >= options.EffectiveMaxBundles)
            {
                _logger.LogDebug(
                    "Repro-bundle limit of {Max} reached; not capturing {Operation}",
                    options.EffectiveMaxBundles,
                    operation);

                return null;
            }

            System.IO.Directory.CreateDirectory(directory);

            Manifest manifest = new(
                Operation: operation,
                Kernel: capabilities.Name,
                KernelVersion: capabilities.Version,
                Tolerance: request.EffectiveTolerance,
                CorrelationId: request.CorrelationId,
                Definition: DefinitionDescriptor.ForManifest(definition),
                Diagnostics: [.. diagnostics.Select(d => new ManifestDiagnostic(
                    d.Severity.ToString(), d.Code, d.Message, d.KernelDetail))],
                InputShapes: inputs.Length,
                Recurrences: 1);

            File.WriteAllText(
                Path.Combine(directory, "manifest.json"),
                JsonSerializer.Serialize(manifest, JsonOptions),
                new UTF8Encoding(false));

            File.WriteAllText(
                Path.Combine(directory, "README.md"),
                BuildReadme(operation, fingerprint),
                new UTF8Encoding(false));

            // Recorded before the geometry is written, not after. The geometry callback comes from
            // a caller and may throw -- the catch-all below says as much -- and an exception there
            // used to unwind past this line, leaving a bundle on disk the writer did not know
            // about. The limit then never engaged, and every later recurrence rewrote the manifest
            // with a count of one.
            _written.Add(directory);

            if (options.IncludeInputGeometry && inputs.Length > 0)
            {
                WriteInputGeometry(directory, inputs, writeBRep, operation);
            }

            _logger.LogWarning(
                "Captured a repro bundle for {Operation} at {Directory}", operation, directory);

            return directory;
        }
        catch (Exception exception)
        {
            // Deliberately catching everything. This method runs on the failure path of an
            // operation that has already gone wrong, and the geometry callback is supplied by a
            // caller that can throw anything. Turning a diagnostic aid into a second, more
            // confusing failure would be strictly worse than losing the bundle.
            _logger.LogError(exception, "Could not capture a repro bundle for {Operation}", operation);
            return null;
        }
    }

    /// <summary>
    /// Writes the input shapes. Isolated so a failure here costs the geometry, not the bundle.
    /// </summary>
    private void WriteInputGeometry(
        string directory,
        ImmutableArray<KernelShape> inputs,
        Func<KernelShape, ImmutableArray<byte>> writeBRep,
        string operation)
    {
        string inputDirectory = Path.Combine(directory, "inputs");
        System.IO.Directory.CreateDirectory(inputDirectory);

        for (int i = 0; i < inputs.Length; i++)
        {
            try
            {
                ImmutableArray<byte> bytes = writeBRep(inputs[i]);
                if (bytes.IsDefaultOrEmpty)
                {
                    continue;
                }

                // Sequence-numbered, not tag-numbered: tags are not stable across runs, so a
                // tag-named file would make two captures of the same failure look different.
                string name = i.ToString("D2", CultureInfo.InvariantCulture) + ".brep";
                File.WriteAllBytes(Path.Combine(inputDirectory, name), [.. bytes]);
            }
            catch (Exception exception)
            {
                // A bundle missing one input is far better than no bundle. The shape may already
                // have been released, which is exactly the situation worth recording.
                _logger.LogWarning(
                    exception,
                    "Could not write input {Index} of the repro bundle for {Operation}",
                    i,
                    operation);
            }
        }
    }

    private static void RecordRecurrence(string directory)
    {
        string path = Path.Combine(directory, "manifest.json");
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            Manifest? manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), JsonOptions);
            if (manifest is null)
            {
                return;
            }

            File.WriteAllText(
                path,
                JsonSerializer.Serialize(manifest with { Recurrences = manifest.Recurrences + 1 }, JsonOptions),
                new UTF8Encoding(false));
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            // A recurrence count is a nicety; losing it is not worth reporting.
        }
    }

    /// <summary>
    /// Produces a stable identifier for a failure, so the same failure captures once.
    /// </summary>
    private static string Fingerprint(
        string operation,
        IOperationDefinition? definition,
        ImmutableArray<KernelDiagnostic> diagnostics)
    {
        StringBuilder material = new();
        material.Append(operation);
        material.Append('\n');

        // Handle tags are deliberately excluded. They are slot indices carrying a generation
        // counter, so they change on every rebuild; including them gave the same failure a
        // different fingerprint each time and defeated the deduplication entirely.
        material.Append(DefinitionDescriptor.ForFingerprint(definition));
        material.Append('\n');

        // Codes and entity counts, not messages: a message may carry a formatted measurement that
        // differs in its last digit between runs, which would defeat the deduplication.
        foreach (KernelDiagnostic diagnostic in diagnostics)
        {
            material.Append(diagnostic.Code);
            material.Append(':');
            material.Append(diagnostic.Entities.Length.ToString(CultureInfo.InvariantCulture));
            material.Append(';');
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()));
        return Convert.ToHexStringLower(hash.AsSpan(0, 6));
    }

    private static string BuildReadme(string operation, string fingerprint) =>
        $"""
        # Repro bundle: {operation}

        Captured automatically when this operation failed (P1-T13, PLAN.md 6.1).

        - `manifest.json` — the operation, its parameters, the tolerance, and the diagnostics.
        - `inputs/NN.brep` — the input shapes, in the order the operation received them.

        ## Turning this into a regression fixture

        PLAN.md 8.2: **every bug fix ships with a corpus fixture that reproduces it.** This bundle
        exists so that is a five-minute job rather than an afternoon of reconstruction:

        1. Copy this directory into `tests/regression/corpus/pathological/{operation}-{fingerprint}/`.
        2. Add a `scenario.json` describing the operation to replay and what should happen.
        3. Run the regression suite, confirm it fails, fix the defect, confirm it passes.

        The `pathological/` category exists for exactly this: real inputs that once broke us. It is
        the mechanism by which the product gets more robust over years instead of oscillating.
        """;

    private sealed record Manifest(
        string Operation,
        string Kernel,
        string KernelVersion,
        double Tolerance,
        string? CorrelationId,
        string Definition,
        ImmutableArray<ManifestDiagnostic> Diagnostics,
        int InputShapes,
        int Recurrences);

    private sealed record ManifestDiagnostic(
        string Severity,
        string Code,
        string Message,
        string? KernelDetail);
}
