using System.Collections.Immutable;
using System.Globalization;
using OpenMCAD.Kernel;
using OpenMCAD.Kernel.Operations;
using OpenMCAD.Math;

namespace OpenMCAD.Regression;

/// <summary>What happened when a fixture ran.</summary>
/// <param name="Fixture">The fixture.</param>
/// <param name="Passed">Whether every assertion held.</param>
/// <param name="Failures">The assertions that did not hold.</param>
/// <param name="Skipped">Assertions not applicable to this kernel.</param>
/// <param name="Signature">
/// A compact fingerprint of the result: topology counts and the role histogram. Two runs that
/// produce different signatures have diverged, which is what the determinism gate compares.
/// </param>
/// <param name="Duration">How long the fixture took.</param>
/// <param name="Rungs">
/// Which rung of the retry ladder each step that reported one succeeded on (PLAN.md 5.2.4).
/// Steps that do not run a ladder are absent rather than recorded as
/// <see cref="RetryRung.NotApplicable"/>, so the rate below is over operations that could have
/// escalated rather than over every step in the corpus.
/// </param>
public sealed record FixtureResult(
    Fixture Fixture,
    bool Passed,
    ImmutableArray<string> Failures,
    ImmutableArray<string> Skipped,
    string Signature,
    TimeSpan Duration,
    ImmutableArray<RetryRung> Rungs);

/// <summary>
/// Replays a fixture against a kernel and checks it against its golden values.
/// </summary>
/// <remarks>
/// <para>
/// P1-T14. Deliberately kernel-agnostic: the same fixture runs against <c>FakeKernel</c> in
/// seconds during development and against <c>OcctKernel</c> nightly, and PLAN.md 8.1 wants exactly
/// that — "the same test battery run against both to prove the abstraction holds".
/// </para>
/// <para>
/// Assertions that a kernel cannot honestly satisfy are <i>skipped and reported</i>, never quietly
/// relaxed. A corpus that loosens its tolerances to keep passing has stopped being a corpus.
/// </para>
/// </remarks>
public sealed class ScenarioRunner(IGeometryKernel kernel)
{
    /// <summary>Runs one fixture.</summary>
    /// <param name="fixture">The fixture to run.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public async Task<FixtureResult> RunAsync(
        Fixture fixture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        List<string> failures = [];
        List<string> skipped = [];
        Dictionary<string, KernelShapeHandle> shapes = new(StringComparer.Ordinal);
        HistoryMap lastHistory = HistoryMap.Empty;
        string signature = "(none)";
        List<RetryRung> rungs = [];

        try
        {
            foreach (ScenarioStep step in fixture.Steps)
            {
                OperationResult result = await ExecuteAsync(step, shapes, cancellationToken)
                    .ConfigureAwait(false);

                if (result.Rung != RetryRung.NotApplicable)
                {
                    rungs.Add(result.Rung);
                }

                if (!result.TryGetShape(out KernelShapeHandle shape, out HistoryMap history))
                {
                    failures.Add($"step '{step.Name}' ({step.Op}) failed: {result.Describe()}");
                    break;
                }

                shapes[step.Name] = shape;
                lastHistory = history;
            }

            if (failures.Count == 0)
            {
                KernelShapeHandle final = shapes[fixture.Steps[^1].Name];
                signature = await CheckAsync(fixture, final, lastHistory, failures, skipped, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failures.Add($"threw: {exception.Message}");
        }
        finally
        {
            foreach (KernelShapeHandle shape in shapes.Values)
            {
                shape.Dispose();
            }
        }

        return new FixtureResult(
            fixture,
            failures.Count == 0,
            [.. failures],
            [.. skipped],
            signature,
            System.Diagnostics.Stopwatch.GetElapsedTime(started),
            [.. rungs]);
    }

    private async Task<string> CheckAsync(
        Fixture fixture,
        KernelShapeHandle shape,
        HistoryMap history,
        List<string> failures,
        List<string> skipped,
        CancellationToken cancellationToken)
    {
        ExpectedValues expected = fixture.Expected;

        TopologyCounts counts =
            (await kernel.CountTopologyAsync(shape.Shape, cancellationToken: cancellationToken)
                .ConfigureAwait(false)).Value;

        Check(expected.Faces, counts.Faces, "face count", failures);
        Check(expected.Edges, counts.Edges, "edge count", failures);
        Check(expected.Vertices, counts.Vertices, "vertex count", failures);

        // PLAN.md 8.3: an invalid shape is a failure even if it looks right.
        ShapeValidity validity =
            (await kernel.CheckValidityAsync(shape.Shape, cancellationToken: cancellationToken)
                .ConfigureAwait(false)).Value;

        if (!validity.IsValid)
        {
            failures.Add(
                "the result failed validity checking: "
                + string.Join("; ", validity.Problems.Select(p => p.Message)));
        }

        bool massPropertiesApply =
            !fixture.RequiresExactMassProperties || kernel.Capabilities.ProducesExactMassProperties;

        KernelResult<MassProperties> massResult = await kernel
            .ComputeMassPropertiesAsync(shape.Shape, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (massResult.TryGetValue(out MassProperties properties))
        {
            bool exact = properties.Accuracy == ResultAccuracy.Exact;

            if (massPropertiesApply || exact)
            {
                CheckDouble(expected.Volume, properties.Volume, "volume", expected.RelativeTolerance, failures);
                CheckDouble(expected.SurfaceArea, properties.SurfaceArea, "surface area", expected.RelativeTolerance, failures);

                if (!expected.Centroid.IsDefaultOrEmpty && expected.Centroid.Length == 3)
                {
                    Vec3d want = new(expected.Centroid[0], expected.Centroid[1], expected.Centroid[2]);
                    if (!properties.Centroid.IsNear(want, System.Math.Max(expected.RelativeTolerance, 1e-9)))
                    {
                        failures.Add($"centroid: expected {want}, measured {properties.Centroid}");
                    }
                }
            }
            else
            {
                skipped.Add(
                    $"mass properties: {kernel.Capabilities.Name} reports them as approximate for "
                    + "this shape, and the fixture asks for exact ones");
            }
        }
        else
        {
            failures.Add("mass properties could not be computed");
        }

        // Roles are the provenance assertion, and the one that catches a naming regression before
        // there is a naming layer to regress.
        if (expected.Roles is { Count: > 0 })
        {
            Dictionary<string, int> actual = new(StringComparer.Ordinal);
            foreach (SubEntity output in history.Outputs)
            {
                string role = history.RoleOf(output).ToString();
                actual[role] = actual.GetValueOrDefault(role) + 1;
            }

            foreach ((string role, int want) in expected.Roles.OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                int got = actual.GetValueOrDefault(role);
                if (got != want)
                {
                    failures.Add($"role {role}: expected {want}, found {got}");
                }
            }
        }

        if (history.UnrolledOutputs.Any())
        {
            failures.Add(
                $"{history.UnrolledOutputs.Count()} output entities carry no OperationRole, which "
                + "means the operation that produced them is incomplete (PLAN.md 5.1)");
        }

        return BuildSignature(counts, history);
    }

    /// <summary>
    /// Builds the fingerprint the determinism gate compares between runs.
    /// </summary>
    /// <remarks>
    /// Topology and roles, not geometry. Those are what a name is built from, so a difference here
    /// means names would differ — which ADR-0011 makes a P0 regardless of whether any measurement
    /// changed.
    /// </remarks>
    private static string BuildSignature(TopologyCounts counts, HistoryMap history)
    {
        Dictionary<string, int> roles = new(StringComparer.Ordinal);
        foreach (SubEntity output in history.Outputs)
        {
            string role = history.RoleOf(output).ToString();
            roles[role] = roles.GetValueOrDefault(role) + 1;
        }

        string roleText = string.Join(
            ",",
            roles.OrderBy(r => r.Key, StringComparer.Ordinal)
                 .Select(r => $"{r.Key}={r.Value.ToString(CultureInfo.InvariantCulture)}"));

        return $"{counts}|{roleText}";
    }

    private static void Check(int? expected, int actual, string what, List<string> failures)
    {
        if (expected is int want && want != actual)
        {
            failures.Add($"{what}: expected {want}, measured {actual}");
        }
    }

    private static void CheckDouble(
        double? expected,
        double actual,
        string what,
        double tolerance,
        List<string> failures)
    {
        if (expected is not double want)
        {
            return;
        }

        if (!Tolerance.AreRelativelyEqual(want, actual, System.Math.Max(tolerance, 1e-12)))
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{what}: expected {want:G12}, measured {actual:G12} (relative tolerance {tolerance:G3})"));
        }
    }

    private ValueTask<OperationResult> ExecuteAsync(
        ScenarioStep step,
        Dictionary<string, KernelShapeHandle> shapes,
        CancellationToken cancellationToken)
    {
        double V(int index, double fallback = 0.0)
            => step.Values.IsDefaultOrEmpty || index >= step.Values.Length
                ? fallback
                : step.Values[index];

        KernelShape Input()
            => step.Input is not null && shapes.TryGetValue(step.Input, out KernelShapeHandle? handle)
                ? handle.Shape
                : throw new InvalidDataException(
                    $"step '{step.Name}' refers to input '{step.Input}', which no earlier step produced");

        return step.Op.ToLowerInvariant() switch
        {
            "box" => kernel.CreateBoxAsync(
                new BoxDefinition(V(0), V(1), V(2)), cancellationToken: cancellationToken),

            "cylinder" => kernel.CreateCylinderAsync(
                new CylinderDefinition(V(0), V(1)), cancellationToken: cancellationToken),

            "sphere" => kernel.CreateSphereAsync(
                new SphereDefinition(V(0)), cancellationToken: cancellationToken),

            "cone" => kernel.CreateConeAsync(
                new ConeDefinition(V(0), V(1), V(2)), cancellationToken: cancellationToken),

            "torus" => kernel.CreateTorusAsync(
                new TorusDefinition(V(0), V(1)), cancellationToken: cancellationToken),

            "profile" => kernel.CreatePolygonProfileAsync(
                new PolygonProfileDefinition(ReadPoints(step), Transform.Identity),
                cancellationToken: cancellationToken),

            "extrude" => kernel.ExtrudeAsync(
                new ExtrudeDefinition(Input(), new Vec3d(V(0), V(1), V(2, 1.0)), V(3, 1.0)),
                cancellationToken: cancellationToken),

            "fillet" => FilletAsync(step, Input(), V(0), cancellationToken),
            "chamfer" => ChamferAsync(step, Input(), V(0), cancellationToken),

            "boolean" => kernel.BooleanAsync(
                new BooleanDefinition(
                    (BooleanOperation)(int)V(0),
                    Input(),
                    shapes[step.Tool ?? throw new InvalidDataException(
                        $"step '{step.Name}' is a boolean but names no tool")].Shape),
                cancellationToken: cancellationToken),

            _ => throw new InvalidDataException($"step '{step.Name}' uses unknown operation '{step.Op}'"),
        };
    }

    private async ValueTask<OperationResult> FilletAsync(
        ScenarioStep step, KernelShape body, double radius, CancellationToken cancellationToken)
    {
        ImmutableArray<SubEntity> edges = await SelectEdgesAsync(step, body, cancellationToken)
            .ConfigureAwait(false);

        return await kernel.FilletAsync(
            new FilletDefinition(body, radius, [.. edges]), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<OperationResult> ChamferAsync(
        ScenarioStep step, KernelShape body, double distance, CancellationToken cancellationToken)
    {
        ImmutableArray<SubEntity> edges = await SelectEdgesAsync(step, body, cancellationToken)
            .ConfigureAwait(false);

        return await kernel.ChamferAsync(
            new ChamferDefinition(body, distance, [.. edges]), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ImmutableArray<SubEntity>> SelectEdgesAsync(
        ScenarioStep step, KernelShape body, CancellationToken cancellationToken)
    {
        ImmutableArray<SubEntity> all =
            (await kernel.EnumerateAsync(body, SubEntityKind.Edge, cancellationToken: cancellationToken)
                .ConfigureAwait(false)).Value;

        if (step.EdgeIndices.IsDefaultOrEmpty)
        {
            return all;
        }

        ImmutableArray<SubEntity>.Builder selected =
            ImmutableArray.CreateBuilder<SubEntity>(step.EdgeIndices.Length);

        foreach (int index in step.EdgeIndices)
        {
            if (index < 0 || index >= all.Length)
            {
                throw new InvalidDataException(
                    $"step '{step.Name}' selects edge {index}, but the body has {all.Length} edges. "
                    + "Edge indices are canonical order, so this fixture is out of step with the "
                    + "kernel that produced it.");
            }

            selected.Add(all[index]);
        }

        return selected.MoveToImmutable();
    }

    private static ImmutableArray<Vec2d> ReadPoints(ScenarioStep step)
    {
        if (step.Points.IsDefaultOrEmpty || step.Points.Length % 2 != 0)
        {
            throw new InvalidDataException(
                $"step '{step.Name}' needs an even number of point coordinates");
        }

        ImmutableArray<Vec2d>.Builder points =
            ImmutableArray.CreateBuilder<Vec2d>(step.Points.Length / 2);

        for (int i = 0; i + 1 < step.Points.Length; i += 2)
        {
            points.Add(new Vec2d(step.Points[i], step.Points[i + 1]));
        }

        return points.MoveToImmutable();
    }
}
