using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Solver.Sketching;

using Xunit;

namespace OpenMCAD.Solver.Fake.Tests;

/// <summary>A reference to one named point, as a fixture's <c>expected.json</c> writes one.</summary>
/// <param name="Entity">The entity's id, in <see cref="SketchEntityId.ToStorageString"/> form.</param>
/// <param name="Point">Which of its points, or null for <see cref="EntityPoint.Self"/>.</param>
public sealed record FixturePointRef(string Entity, string? Point)
{
    /// <summary>Resolves this reference against a solved sketch.</summary>
    /// <param name="sketch">The sketch.</param>
    /// <returns>Where the point is.</returns>
    public Vec2d Locate(Sketch sketch)
    {
        EntityPoint point = Point is null ? EntityPoint.Self : Enum.Parse<EntityPoint>(Point);
        SketchPointRef reference = new(SketchEntityId.Parse(Entity), point);

        return sketch.Entities.Locate(reference)
            ?? throw new InvalidOperationException(
                $"Fixture expectation names {reference}, and the solved sketch has no such point.");
    }
}

/// <summary>Where a fixture's expected position sits.</summary>
/// <param name="Entity">The entity's id.</param>
/// <param name="Point">Which of its points, or null for <see cref="EntityPoint.Self"/>.</param>
/// <param name="Expected">The expected coordinates, as <c>[x, y]</c>.</param>
/// <param name="Tolerance">How close counts as a match.</param>
public sealed record FixturePosition(string Entity, string? Point, double[] Expected, double Tolerance = 1e-6);

/// <summary>An expected distance between two named points, after solving.</summary>
/// <param name="From">One point.</param>
/// <param name="To">The other.</param>
/// <param name="Expected">The expected distance.</param>
/// <param name="Tolerance">How close counts as a match.</param>
public sealed record FixtureDistance(
    FixturePointRef From, FixturePointRef To, double Expected, double Tolerance = 1e-6);

/// <summary>The drag a fixture asks the solver to apply, if any.</summary>
/// <param name="Entity">Which entity is being dragged.</param>
/// <param name="Point">Which of its points, or null for <see cref="EntityPoint.Self"/>.</param>
/// <param name="To">Where the pointer is.</param>
public sealed record FixtureDrag(string Entity, string? Point, double[] To);

/// <summary>Overrides to <see cref="SolverOptions"/> a fixture asks for.</summary>
/// <param name="MaxIterations">Overrides <see cref="SolverOptions.MaximumIterations"/>.</param>
/// <param name="Tolerance">Overrides <see cref="SolverOptions.Tolerance"/>.</param>
public sealed record FixtureOptions(int? MaxIterations, double? Tolerance);

/// <summary>
/// What a sketch corpus fixture must show (P4-T16). See <c>tests/regression/corpus/sketch/README.md</c>.
/// </summary>
/// <param name="Description">What the fixture is for, and what breaking it would mean. Required.</param>
/// <param name="Drag">A drag to apply before checking anything, if this is a drag-stability fixture.</param>
/// <param name="Options">Overrides to the solver's settings.</param>
/// <param name="Outcome">
/// The expected <see cref="SolveOutcome"/>, by name. Null skips solving altogether -- for a
/// degenerate-input fixture that is caught by <see cref="Sketch.Problems"/> before a solve would
/// even be attempted.
/// </param>
/// <param name="RemainingFreedom">The expected <see cref="SolveDiagnosis.RemainingFreedom"/>, if checked.</param>
/// <param name="FreeEntities">The expected free entities, by id, compared as a set.</param>
/// <param name="Conflicting">The expected conflicting constraints, by id, compared as a set.</param>
/// <param name="Redundant">The expected redundant constraints, by id, compared as a set.</param>
/// <param name="Positions">Expected positions of named points after solving.</param>
/// <param name="Distances">Expected distances between named points after solving.</param>
/// <param name="ProblemContains">
/// A substring every fixture with this set must find among <see cref="Sketch.Problems"/>, checked
/// before any solve is attempted.
/// </param>
public sealed record SketchFixtureExpectation(
    string Description,
    FixtureDrag? Drag = null,
    FixtureOptions? Options = null,
    string? Outcome = null,
    int? RemainingFreedom = null,
    string[]? FreeEntities = null,
    string[]? Conflicting = null,
    string[]? Redundant = null,
    FixturePosition[]? Positions = null,
    FixtureDistance[]? Distances = null,
    string? ProblemContains = null);

/// <summary>
/// The sketch regression corpus: solver convergence, diagnosis, drag stability and degenerate
/// inputs, against real fixtures under <c>tests/regression/corpus/sketch</c> (P4-T16).
/// </summary>
/// <remarks>
/// <para>
/// PLAN.md 8.2 mandates a <c>sketch/</c> category and this is it, but it does not run through
/// <c>OpenMCAD.Regression</c> — that runner's fixture schema (<c>ScenarioStep</c>, kernel operation
/// names, mass properties) describes building a body with a kernel, and a sketch has neither. Each
/// fixture is instead the sketch's own JSON interchange form (<see cref="SketchFormat"/>, P4-T04 —
/// whose own remarks name this corpus as the reason that form exists) plus a small expectation file
/// this class reads directly, run against <see cref="FakeSolver"/> because that is what this
/// project already has a reference to.
/// </para>
/// <para>
/// Entity and constraint ids in a fixture's <c>sketch.json</c> are small sequential GUIDs rather
/// than random ones, precisely so <c>expected.json</c> can name them by hand and stay legible to
/// whoever reads the fixture next.
/// </para>
/// </remarks>
public sealed class SketchCorpusTests
{
    private static readonly FakeSolver Solver = new();

    private static readonly JsonSerializerOptions ExpectationFormat = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Every fixture directory in the sketch corpus, as xUnit theory data.</summary>
    public static TheoryData<string> Fixtures()
    {
        TheoryData<string> data = [];

        foreach (DirectoryInfo fixture in FixtureDirectories())
        {
            data.Add(fixture.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EveryFixtureMatchesItsExpectation(string name)
    {
        DirectoryInfo directory = FixtureDirectories().Single(d => d.Name == name);

        Sketch sketch = SketchFormat.Read(File.ReadAllText(Path.Combine(directory.FullName, "sketch.json")));

        SketchFixtureExpectation expected = JsonSerializer.Deserialize<SketchFixtureExpectation>(
            File.ReadAllText(Path.Combine(directory.FullName, "expected.json")), ExpectationFormat)!;

        expected.Description.Should().NotBeNullOrWhiteSpace(
            "a golden value nobody can explain is one nobody dares change");

        if (expected.ProblemContains is { } problem)
        {
            sketch.Problems.Should().Contain(p => p.Contains(problem, StringComparison.Ordinal));
        }

        if (expected.Outcome is null)
        {
            return;
        }

        DragTarget? drag = expected.Drag is { } d
            ? new DragTarget(
                new SketchPointRef(
                    SketchEntityId.Parse(d.Entity),
                    d.Point is null ? EntityPoint.Self : Enum.Parse<EntityPoint>(d.Point)),
                new Vec2d(d.To[0], d.To[1]))
            : null;

        SolverOptions options = expected.Options is { } o
            ? new SolverOptions(
                o.MaxIterations ?? SolverOptions.Default.MaximumIterations,
                o.Tolerance ?? SolverOptions.Default.Tolerance)
            : SolverOptions.Default;

        SolveResult result = Solver.Solve(sketch, drag, options);

        result.Diagnosis.Outcome.Should().Be(Enum.Parse<SolveOutcome>(expected.Outcome));

        if (expected.RemainingFreedom is { } freedom)
        {
            result.Diagnosis.RemainingFreedom.Should().Be(freedom);
        }

        if (expected.FreeEntities is { } free)
        {
            result.Diagnosis.Free.Select(e => e.ToStorageString())
                .Should().BeEquivalentTo(free);
        }

        if (expected.Conflicting is { } conflicting)
        {
            result.Diagnosis.Conflicts.Select(c => c.ToStorageString())
                .Should().BeEquivalentTo(conflicting);
        }

        if (expected.Redundant is { } redundant)
        {
            result.Diagnosis.Surplus.Select(c => c.ToStorageString())
                .Should().BeEquivalentTo(redundant);
        }

        foreach (FixturePosition position in expected.Positions ?? [])
        {
            EntityPoint point = position.Point is null ? EntityPoint.Self : Enum.Parse<EntityPoint>(position.Point);
            Vec2d actual = result.Sketch.Entities.Locate(new SketchPointRef(SketchEntityId.Parse(position.Entity), point))
                ?? throw new InvalidOperationException(
                    $"Fixture '{name}' expects a position for {position.Entity}.{position.Point}, and "
                    + "the solved sketch has no such point.");

            actual.X.Should().BeApproximately(position.Expected[0], position.Tolerance);
            actual.Y.Should().BeApproximately(position.Expected[1], position.Tolerance);
        }

        foreach (FixtureDistance distance in expected.Distances ?? [])
        {
            double actual = Vec2d.Distance(distance.From.Locate(result.Sketch), distance.To.Locate(result.Sketch));

            actual.Should().BeApproximately(distance.Expected, distance.Tolerance);
        }
    }

    [Fact]
    public void TheCorpusIsNotEmpty()
    {
        // PLAN.md 8.2's rule for every corpus category: it exists to grow, and a category with
        // nothing in it is one nobody would notice had stopped growing.
        FixtureDirectories().Should().NotBeEmpty("the sketch corpus is what P4-T16 is for");
    }

    [Fact]
    public void EveryMandatoryCategoryHasAtLeastOneFixture()
    {
        // PLAN.md 8.2 names four things this category has to cover. Not a proof that each is
        // covered *well* -- only that nobody quietly stopped short of one of them.
        ImmutableArray<string> names = [.. FixtureDirectories().Select(d => d.Name)];

        names.Should().Contain(n => n.StartsWith("convergence", StringComparison.Ordinal));
        names.Should().Contain(n => n.StartsWith("diagnosis", StringComparison.Ordinal));
        names.Should().Contain(n => n.StartsWith("drag-stability", StringComparison.Ordinal));
        names.Should().Contain(n => n.StartsWith("degenerate", StringComparison.Ordinal));
    }

    private static ImmutableArray<DirectoryInfo> FixtureDirectories()
        => [.. FindCorpus().GetDirectories().OrderBy(d => d.Name, StringComparer.Ordinal)];

    /// <summary>Finds <c>tests/regression/corpus/sketch</c>, walking up from the running assembly.</summary>
    private static DirectoryInfo FindCorpus()
    {
        DirectoryInfo? candidate = new(AppContext.BaseDirectory);

        while (candidate is not null)
        {
            string corpus = Path.Combine(candidate.FullName, "tests", "regression", "corpus", "sketch");

            if (Directory.Exists(corpus))
            {
                return new DirectoryInfo(corpus);
            }

            candidate = candidate.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find tests/regression/corpus/sketch. It is the sketch corpus and it is not "
            + "optional (P4-T16).");
    }
}
