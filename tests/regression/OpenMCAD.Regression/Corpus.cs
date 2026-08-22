using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenMCAD.Regression;

/// <summary>
/// One step of a corpus scenario: an operation to perform.
/// </summary>
/// <param name="Op">
/// Which operation. One of <c>box</c>, <c>cylinder</c>, <c>sphere</c>, <c>cone</c>, <c>torus</c>,
/// <c>profile</c>, <c>extrude</c>, <c>fillet</c>, <c>chamfer</c>, <c>boolean</c>.
/// </param>
/// <param name="Name">A name later steps refer to this result by.</param>
/// <param name="Values">Numeric arguments, positional per operation.</param>
/// <param name="Input">The name of the shape this step consumes, if any.</param>
/// <param name="Tool">The name of the second shape, for booleans.</param>
/// <param name="Points">A profile outline, as interleaved XY pairs.</param>
/// <param name="EdgeIndices">Which edges of the input to act on, by canonical index.</param>
public sealed record ScenarioStep(
    string Op,
    string Name,
    ImmutableArray<double> Values,
    string? Input = null,
    string? Tool = null,
    ImmutableArray<double> Points = default,
    ImmutableArray<int> EdgeIndices = default);

/// <summary>
/// The golden values a fixture asserts.
/// </summary>
/// <param name="Volume">Expected volume in cubic metres.</param>
/// <param name="SurfaceArea">Expected surface area in square metres.</param>
/// <param name="Centroid">Expected centroid, three doubles.</param>
/// <param name="Faces">Expected face count.</param>
/// <param name="Edges">Expected edge count.</param>
/// <param name="Vertices">Expected vertex count.</param>
/// <param name="RelativeTolerance">
/// How far the measured values may differ, relative to their magnitude.
/// </param>
/// <param name="Roles">
/// Expected count of each operation role in the final history map, keyed by role name.
/// </param>
/// <remarks>
/// PLAN.md 8.2 lists what <c>expected.json</c> must capture: mass properties to a stated tolerance,
/// topology counts, the resolved persistent-name map, per-feature rebuild status, and a
/// tessellation hash. Names arrive at P3; roles stand in for them until then, and they are the part
/// that actually catches a provenance regression.
/// </remarks>
public sealed record ExpectedValues(
    double? Volume = null,
    double? SurfaceArea = null,
    ImmutableArray<double> Centroid = default,
    int? Faces = null,
    int? Edges = null,
    int? Vertices = null,
    double RelativeTolerance = 1e-9,
    ImmutableDictionary<string, int>? Roles = null);

/// <summary>A corpus fixture: what to build, and what the result must be.</summary>
/// <param name="Name">The fixture name, matching its directory.</param>
/// <param name="Category">The corpus category, matching its parent directory.</param>
/// <param name="Description">What this fixture is for, and what breaking it would mean.</param>
/// <param name="Steps">The operations to perform, in order.</param>
/// <param name="Expected">The golden values, asserted against the last step.</param>
/// <param name="RequiresExactMassProperties">
/// Whether the mass-property assertions apply. Skipped on a kernel that does not claim exactness,
/// so one corpus can run against <c>FakeKernel</c> and <c>OcctKernel</c> without either lying.
/// </param>
public sealed record Fixture(
    string Name,
    string Category,
    string Description,
    ImmutableArray<ScenarioStep> Steps,
    ExpectedValues Expected,
    bool RequiresExactMassProperties = true);

/// <summary>Loads fixtures from <c>tests/regression/corpus</c>.</summary>
/// <remarks>
/// <para>
/// The corpus is the single most important thing in the repository, and PLAN.md 8 is blunt about
/// why: "Skipping this section is how projects like this die. The modeling code is not what kills
/// them — the absence of a regression corpus is."
/// </para>
/// <para>
/// Fixtures are JSON rather than compiled code so that a repro bundle can become one by hand, and
/// so a fixture captured from a user's failing model needs no build step to run.
/// </para>
/// </remarks>
public static class CorpusLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Finds the corpus directory, walking up from a starting point.</summary>
    /// <param name="start">Where to start looking. Defaults to the running assembly's directory.</param>
    /// <exception cref="DirectoryNotFoundException">The corpus could not be found.</exception>
    public static DirectoryInfo FindCorpus(string? start = null)
    {
        DirectoryInfo? candidate = new(start ?? AppContext.BaseDirectory);

        while (candidate is not null)
        {
            string corpus = Path.Combine(candidate.FullName, "tests", "regression", "corpus");
            if (Directory.Exists(corpus))
            {
                return new DirectoryInfo(corpus);
            }

            candidate = candidate.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find tests/regression/corpus. Pass the repository root explicitly.");
    }

    /// <summary>Loads every fixture in the corpus, in a stable order.</summary>
    /// <param name="corpus">The corpus directory.</param>
    public static ImmutableArray<Fixture> LoadAll(DirectoryInfo corpus)
    {
        ArgumentNullException.ThrowIfNull(corpus);

        ImmutableArray<Fixture>.Builder fixtures = ImmutableArray.CreateBuilder<Fixture>();

        // Ordered so a corpus run is reproducible and a failure list is diffable.
        foreach (FileInfo file in corpus
            .GetFiles("fixture.json", SearchOption.AllDirectories)
            .OrderBy(f => f.FullName, StringComparer.Ordinal))
        {
            fixtures.Add(Load(file));
        }

        return fixtures.ToImmutable();
    }

    /// <summary>Loads one fixture.</summary>
    /// <param name="file">The <c>fixture.json</c> to load.</param>
    /// <exception cref="InvalidDataException">The fixture is malformed.</exception>
    public static Fixture Load(FileInfo file)
    {
        ArgumentNullException.ThrowIfNull(file);

        Fixture? fixture;
        try
        {
            using FileStream stream = file.OpenRead();
            fixture = JsonSerializer.Deserialize<Fixture>(stream, Options);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{file.FullName} is not valid JSON.", exception);
        }

        if (fixture is null)
        {
            throw new InvalidDataException($"{file.FullName} deserialised to nothing.");
        }

        string directoryName = file.Directory!.Name;
        string categoryName = file.Directory!.Parent!.Name;

        // The directory layout is the index. A fixture whose name disagrees with its directory
        // would be reported under a name nobody can find on disk.
        if (!string.Equals(fixture.Name, directoryName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{file.FullName}: fixture name '{fixture.Name}' does not match its directory "
                + $"'{directoryName}'.");
        }

        if (!string.Equals(fixture.Category, categoryName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{file.FullName}: category '{fixture.Category}' does not match its directory "
                + $"'{categoryName}'.");
        }

        if (fixture.Steps.IsDefaultOrEmpty)
        {
            throw new InvalidDataException($"{file.FullName}: a fixture needs at least one step.");
        }

        if (string.IsNullOrWhiteSpace(fixture.Description))
        {
            throw new InvalidDataException(
                $"{file.FullName}: a fixture needs a description saying what it is for. A golden "
                + "value nobody can explain is a golden value nobody dares change.");
        }

        return fixture;
    }
}
