using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Solver.Sketching;

using Xunit;

namespace OpenMCAD.Solver.Tests;

/// <summary>
/// Turning what a solver found into what a user is told (P4-T06).
/// </summary>
/// <remarks>
/// <para>
/// Tested without solving anything. The classification rules are where the subtlety is, and
/// reaching a particular rank and residual through an actual solve is a slow and indirect way to
/// check one — it also cannot reach the combinations a real solver never happens to produce but a
/// different solver will.
/// </para>
/// <para>
/// §5.6 is blunt that "over-constrained" without a list is useless to a user, so these check which
/// constraints come back named, not merely that the outcome is right.
/// </para>
/// </remarks>
public sealed class SolveDiagnosticsTests
{
    [Fact]
    public void EverythingIndependentAndSatisfiedIsFullyDefined()
    {
        SolveDiagnosis verdict = SolveDiagnostics.From(Evidence(
            residual: 0, rank: 4, equations: 4, free: 4));

        verdict.Outcome.Should().Be(SolveOutcome.WellConstrained);
        verdict.RemainingFreedom.Should().Be(0);
    }

    [Fact]
    public void FreedomBeyondTheRankIsWhatIsLeftLoose()
    {
        SolveDiagnosis verdict = SolveDiagnostics.From(Evidence(
            residual: 0, rank: 2, equations: 2, free: 5));

        verdict.Outcome.Should().Be(SolveOutcome.UnderConstrained);
        verdict.RemainingFreedom.Should().Be(3);
        verdict.Free.Should().NotBeEmpty("a user needs to know what can still move");
        verdict.IsUsable.Should().BeTrue("a sketch being drawn is loose almost all the time");
    }

    [Fact]
    public void ARankShortOfTheEquationsAndSatisfiedIsRedundant()
    {
        // Two constraints saying the same true thing. The sketch solves and one of them is doing
        // nothing, which needs a different fix from a contradiction.
        SolveDiagnosis verdict = SolveDiagnostics.From(Evidence(
            residual: 0, rank: 1, equations: 2, free: 4, dependent: [Constraint(2)]));

        verdict.Outcome.Should().Be(SolveOutcome.Redundant);
        verdict.Surplus.Should().Equal(Constraint(2));
        verdict.IsUsable.Should().BeTrue("a redundant sketch still solves");
    }

    [Fact]
    public void ARankShortOfTheEquationsAndUnsatisfiedIsAContradiction()
    {
        // The same rank, the opposite residual, and the opposite advice: one of these has to go.
        SolveDiagnosis verdict = SolveDiagnostics.From(Evidence(
            residual: 5, rank: 1, equations: 2, free: 4, dependent: [Constraint(2)]));

        verdict.Outcome.Should().Be(SolveOutcome.OverConstrained);
        verdict.Conflicts.Should().Equal(Constraint(2));
        verdict.Message.Should().Contain("contradict");
        verdict.IsUsable.Should().BeFalse();
    }

    [Fact]
    public void TheResidualAloneDecidesBetweenRedundantAndConflicting()
    {
        // The pair above, stated as the property: identical evidence but for the residual, and
        // opposite verdicts. Getting this backwards would tell a user to delete a constraint that
        // was fine and keep one that was not.
        SolveEvidence satisfied = Evidence(
            residual: 0, rank: 1, equations: 2, free: 4, dependent: [Constraint(2)]);

        SolveDiagnostics.From(satisfied).Outcome.Should().Be(SolveOutcome.Redundant);

        SolveDiagnostics.From(satisfied with { Residual = 1 }).Outcome
            .Should().Be(SolveOutcome.OverConstrained);
    }

    [Fact]
    public void IndependentEquationsThatAreNotSatisfiedAreAFailureToConverge()
    {
        // Nothing contradictory: the equations are independent, so an answer exists and was not
        // found. That is a different message from "these constraints cannot all be true".
        SolveDiagnosis verdict = SolveDiagnostics.From(Evidence(
            residual: 3, rank: 2, equations: 2, free: 4));

        verdict.Outcome.Should().Be(SolveOutcome.Failed);
        verdict.Message.Should().Contain("did not converge");
    }

    [Fact]
    public void ASolveThatWasStoppedSaysSoRatherThanBlamingTheSketch()
    {
        // Advice to move the geometry is only good advice when the solver actually tried. A drag
        // that ran out of its 16 ms is not evidence about the sketch at all.
        SolveDiagnosis verdict = SolveDiagnostics.From(Evidence(
            residual: 3, rank: 2, equations: 2, free: 4) with
        { Converged = false });

        verdict.Outcome.Should().Be(SolveOutcome.Failed);
        verdict.Message.Should().Contain("stopped before it finished");
        verdict.Message.Should().NotContain("Moving the geometry");
    }

    [Fact]
    public void NothingConstrainedAndNothingFreeIsFullyDefined()
    {
        SolveDiagnostics.From(Evidence(residual: 0, rank: 0, equations: 0, free: 0))
            .Outcome.Should().Be(SolveOutcome.WellConstrained);
    }

    [Fact]
    public void NothingConstrainedButSomethingFreeIsLoose()
    {
        SolveDiagnosis verdict = SolveDiagnostics.From(Evidence(
            residual: 0, rank: 0, equations: 0, free: 2));

        verdict.Outcome.Should().Be(SolveOutcome.UnderConstrained);
        verdict.RemainingFreedom.Should().Be(2);
    }

    [Theory]
    [InlineData(1e-9, 1e-10, SolveOutcome.WellConstrained)]
    [InlineData(1e-6, 1e-10, SolveOutcome.Failed)]
    [InlineData(1e-9, 1e-3, SolveOutcome.WellConstrained)]
    public void TheToleranceHasAFloorUnderIt(
        double residual, double tolerance, SolveOutcome expected)
    {
        // A drag asks for 1e-8 and a careful solve for 1e-10, and a sketch that reached 1e-9 is
        // solved either way. Without the floor, a perfectly good sketch is reported as failed for
        // having been solved slightly less hard than it might have been.
        SolveDiagnostics.From(
            Evidence(residual, rank: 2, equations: 2, free: 2) with { Tolerance = tolerance })
            .Outcome.Should().Be(expected);
    }

    [Fact]
    public void TheWorstOfSeveralVerdictsWins()
    {
        SolveDiagnosis combined = SolveDiagnostics.Combine(
        [
            Verdict(SolveOutcome.WellConstrained),
            Verdict(SolveOutcome.OverConstrained),
            Verdict(SolveOutcome.UnderConstrained),
        ]);

        combined.Outcome.Should().Be(SolveOutcome.OverConstrained);
    }

    [Theory]
    [InlineData(SolveOutcome.OverConstrained, SolveOutcome.Failed)]
    [InlineData(SolveOutcome.Failed, SolveOutcome.Redundant)]
    [InlineData(SolveOutcome.Redundant, SolveOutcome.UnderConstrained)]
    [InlineData(SolveOutcome.UnderConstrained, SolveOutcome.WellConstrained)]
    public void OutcomesRankByHowMuchTheyStopTheUser(SolveOutcome worse, SolveOutcome better)
    {
        // A contradiction outranks a failure to converge because it is a statement about the
        // sketch rather than about the attempt; both outrank redundancy, which can be ignored.
        SolveDiagnostics.Severity(worse).Should().BeGreaterThan(SolveDiagnostics.Severity(better));
    }

    [Fact]
    public void FreedomAddsUpAcrossGroupsWhenTheSketchIsLoose()
    {
        SolveDiagnosis combined = SolveDiagnostics.Combine(
        [
            Verdict(SolveOutcome.UnderConstrained, freedom: 3),
            Verdict(SolveOutcome.UnderConstrained, freedom: 2),
        ]);

        combined.RemainingFreedom.Should().Be(5);
    }

    [Fact]
    public void FreedomIsNotReportedWhenTheAnswerIsNotAboutFreedom()
    {
        // "Conflicting, three degrees of freedom left" is two answers to two different questions
        // presented as one, and the fields' own contract says which they answer.
        SolveDiagnosis combined = SolveDiagnostics.Combine(
        [
            Verdict(SolveOutcome.OverConstrained),
            Verdict(SolveOutcome.UnderConstrained, freedom: 3),
        ]);

        combined.Outcome.Should().Be(SolveOutcome.OverConstrained);
        combined.RemainingFreedom.Should().Be(0);
        combined.Free.Should().BeEmpty();
    }

    [Fact]
    public void EveryGroupsNamedConstraintsSurviveTheCombination()
    {
        // Two groups can each be broken, and a user shown only one list would fix half a sketch
        // and find the message still there.
        SolveDiagnosis combined = SolveDiagnostics.Combine(
        [
            Verdict(SolveOutcome.OverConstrained, conflicting: [Constraint(1)]),
            Verdict(SolveOutcome.OverConstrained, conflicting: [Constraint(2)]),
        ]);

        combined.Conflicts.Should().BeEquivalentTo([Constraint(1), Constraint(2)]);
    }

    [Fact]
    public void OneVerdictComesBackUntouched()
    {
        SolveDiagnosis only = Verdict(SolveOutcome.Redundant, freedom: 4);

        SolveDiagnostics.Combine([only]).Should().Be(only);
    }

    [Fact]
    public void NoVerdictsAtAllMeansNothingCouldMove()
    {
        SolveDiagnostics.Combine([]).Outcome.Should().Be(SolveOutcome.WellConstrained);
    }

    private static SolveEvidence Evidence(
        double residual,
        int rank,
        int equations,
        int free,
        ImmutableArray<SketchConstraintId> dependent = default)
        => new(
            residual,
            rank,
            equations,
            free,
            dependent.IsDefault ? [] : dependent,
            [Entity(1)],
            Tolerance: 1e-10);

    private static SolveDiagnosis Verdict(
        SolveOutcome outcome,
        int freedom = 0,
        ImmutableArray<SketchConstraintId> conflicting = default)
        => new(
            outcome,
            freedom,
            freedom > 0 ? [Entity(1)] : [],
            conflicting.IsDefault ? [] : conflicting,
            [],
            outcome.ToString());

    private static SketchEntityId Entity(int n)
        => new(new Guid($"00000000-0000-0000-0000-{n:D12}"));

    private static SketchConstraintId Constraint(int n)
        => new(new Guid($"00000000-0000-0000-0001-{n:D12}"));
}
