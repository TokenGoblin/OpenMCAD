using System.Collections.Immutable;
using System.Globalization;

using OpenMCAD.Kernel;
using OpenMCAD.Math;

namespace OpenMCAD.Core.Naming;

/// <summary>
/// Finds the entity a name refers to by comparing what was recorded about its geometry against
/// what is there now.
/// </summary>
/// <remarks>
/// <para>
/// Tier two of §5.3, and the fallback rather than the answer. It runs when history could not
/// settle the question — a face split into two, or a feature was reordered or deleted and the
/// chain is broken — and it is deliberately worse than tier one at the job. History is a record of
/// what actually happened; this is a resemblance argument.
/// </para>
/// <para>
/// <b>Kind is a gate, not a score.</b> A plane is never the face a cylinder became. Letting surface
/// type be one term among several would allow a strong centroid match to outvote it, and produce
/// exactly the confident wrong answer this tier is most at risk of.
/// </para>
/// <para>
/// <b>Distance is measured against the entity's own size.</b> A face that has moved by a tenth of
/// its width has barely moved; one that has moved by ten times its width is a different face. An
/// absolute tolerance in millimetres would have to be wrong at one of the two ends of the range of
/// models this has to work on, and CAD spans from watch parts to aircraft.
/// </para>
/// <para>
/// <b>Nothing here is allowed to guess.</b> A match is accepted only if it is good in absolute
/// terms and clearly better than the runner-up; anything else is reported as unresolved with the
/// scores attached, and P3-T11 turns that into a question for the user.
/// </para>
/// </remarks>
public sealed class GeometricNameResolver
{
    private readonly Func<SubEntity, GeoHint?> _hintOf;
    private readonly GeometricMatchSettings _settings;

    /// <summary>Creates a resolver.</summary>
    /// <param name="hintOf">
    /// How to measure a candidate as things now stand. It must return the hint in the same
    /// feature-local frame the recorded hint was taken in, or every distance below is being
    /// computed between points in two different coordinate systems — which produces numbers, and
    /// no meaning.
    /// </param>
    /// <param name="settings">How much each kind of evidence counts, and how sure to be.</param>
    public GeometricNameResolver(
        Func<SubEntity, GeoHint?> hintOf, GeometricMatchSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(hintOf);

        _hintOf = hintOf;
        _settings = settings ?? GeometricMatchSettings.Default;
    }

    /// <summary>Picks the candidate that best answers to a name, if one clearly does.</summary>
    /// <param name="name">The reference.</param>
    /// <param name="candidates">
    /// What to choose between — tier one's shortlist when history was ambiguous, or the entities of
    /// the rebuilt model when the chain is broken altogether.
    /// </param>
    /// <returns>What was found, with every candidate's score attached.</returns>
    public NameResolution Resolve(PersistentName name, IEnumerable<SubEntity> candidates)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(candidates);

        if (name.Head.Hint is not { } recorded)
        {
            return NameResolution.NotFound(
                "This reference has no record of what its geometry looked like, so there is "
                + "nothing to compare the current model against.");
        }

        ImmutableArray<ScoredEntity> ranking = Rank(recorded, candidates);

        if (ranking.IsEmpty)
        {
            return new NameResolution(
                NameResolutionOutcome.NotFound,
                SubEntity.None,
                [],
                $"Nothing in the rebuilt model is a {recorded.Kind} where this reference expects "
                + "one.",
                ranking);
        }

        ScoredEntity best = ranking[0];

        if (best.Score < _settings.Confidence)
        {
            return new NameResolution(
                NameResolutionOutcome.NotFound,
                SubEntity.None,
                [.. ranking.Select(r => r.Entity)],
                $"The closest thing to what this reference described scores {Format(best.Score)} "
                + $"out of 1, below the {Format(_settings.Confidence)} needed to be sure. What it "
                + "referred to has most likely gone.",
                ranking);
        }

        if (ranking.Length > 1 && best.Score - ranking[1].Score < _settings.Margin)
        {
            return new NameResolution(
                NameResolutionOutcome.Ambiguous,
                SubEntity.None,
                [.. ranking.Select(r => r.Entity)],
                $"Two candidates fit this reference almost equally well, at "
                + $"{Format(best.Score)} and {Format(ranking[1].Score)}. The geometry does not say "
                + "which was meant.",
                ranking);
        }

        return new NameResolution(
            NameResolutionOutcome.Resolved, best.Entity, [best.Entity], null, ranking);
    }

    /// <summary>Scores every candidate of the right kind, best first.</summary>
    private ImmutableArray<ScoredEntity> Rank(GeoHint recorded, IEnumerable<SubEntity> candidates)
    {
        List<ScoredEntity> scored = [];

        foreach (SubEntity candidate in candidates)
        {
            if (_hintOf(candidate) is not { } now || now.Kind != recorded.Kind)
            {
                continue;
            }

            scored.Add(new ScoredEntity(candidate, Score(recorded, now)));
        }

        // Ties broken by the entity's own ordering rather than left to the sort's whim, so that two
        // runs over the same model rank them the same way. It changes no outcome -- a tie is inside
        // any sensible margin and will be reported as ambiguous either way -- but an unstable order
        // would make the reported runner-up differ between runs, and a diagnostic that changes when
        // nothing changed is one nobody trusts.
        scored.Sort((a, b) => b.Score != a.Score
            ? b.Score.CompareTo(a.Score)
            : a.Entity.CompareTo(b.Entity));

        return [.. scored];
    }

    /// <summary>How well one candidate fits, from nought to one.</summary>
    /// <remarks>
    /// Terms with nothing to say are left out of both the total and the divisor, rather than
    /// scoring zero. A face with no recorded normal is not a face that faces the wrong way, and
    /// counting missing evidence as evidence against would push every older reference below the
    /// confidence threshold.
    /// </remarks>
    private double Score(GeoHint recorded, GeoHint now)
    {
        double total = 0;
        double weight = 0;

        double scale = CharacteristicLength(recorded);

        if (scale > 0)
        {
            double distance = (recorded.Centroid - now.Centroid).Length;

            Accumulate(_settings.Centroid, 1.0 / (1.0 + (distance / scale)), ref total, ref weight);
        }

        double recordedLength = recorded.Direction.Length;
        double nowLength = now.Direction.Length;

        if (recordedLength > Tolerance.Linear && nowLength > Tolerance.Linear)
        {
            double alignment = Vec3d.Dot(recorded.Direction, now.Direction) / (recordedLength * nowLength);

            // Mapped from [-1, 1] onto [0, 1] rather than clamped at zero. A face pointing the
            // opposite way is strong evidence against, and clamping would make it merely neutral.
            Accumulate(_settings.Direction, (alignment + 1.0) / 2.0, ref total, ref weight);
        }

        if (recorded.Measure > 0 && now.Measure > 0)
        {
            double ratio = System.Math.Min(recorded.Measure, now.Measure)
                / System.Math.Max(recorded.Measure, now.Measure);

            Accumulate(_settings.Measure, ratio, ref total, ref weight);
        }

        if (recorded.AdjacencyDegree > 0 && now.AdjacencyDegree > 0)
        {
            int difference = System.Math.Abs(recorded.AdjacencyDegree - now.AdjacencyDegree);

            Accumulate(_settings.Adjacency, 1.0 / (1.0 + difference), ref total, ref weight);
        }

        // Kind matched and nothing else was recorded. Half is the honest answer: the gate has been
        // passed and no further evidence exists either way, which under the default confidence is
        // not enough to accept — as it should not be.
        return weight <= 0 ? 0.5 : total / weight;
    }

    private static void Accumulate(double weight, double similarity, ref double total, ref double sum)
    {
        total += weight * System.Math.Clamp(similarity, 0.0, 1.0);
        sum += weight;
    }

    /// <summary>Roughly how big the entity is, for judging how far is far.</summary>
    /// <remarks>
    /// The square root of an area, or the length of an edge. Crude, and the right kind of crude:
    /// the question it answers is "has this moved by a lot compared with itself", and the answer
    /// only has to be right to within a factor of two or so for the score to behave sensibly.
    /// </remarks>
    private static double CharacteristicLength(GeoHint hint)
    {
        if (hint.Measure <= 0)
        {
            return 0;
        }

        return hint.Kind switch
        {
            GeometryKind.Line or GeometryKind.Circle or GeometryKind.Ellipse
                or GeometryKind.FreeformCurve => hint.Measure,
            GeometryKind.Point => 0,
            _ => System.Math.Sqrt(hint.Measure),
        };
    }

    private static string Format(double value)
        => value.ToString("0.00", CultureInfo.InvariantCulture);
}
