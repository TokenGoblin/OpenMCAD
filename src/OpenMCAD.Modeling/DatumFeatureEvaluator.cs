using System.Collections.Immutable;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;
using OpenMCAD.Core.Rebuild;
using OpenMCAD.Kernel;
using OpenMCAD.Math;

namespace OpenMCAD.Modeling;

/// <summary>
/// Evaluates the datum feature types: the producer <see cref="FeatureOutput.References"/> has been
/// waiting for since P3-T04 (P5-T03).
/// </summary>
/// <remarks>
/// <para>
/// The first real <see cref="IFeatureEvaluator"/>. It touches no kernel and produces no bodies,
/// which is exactly why it is the first: a datum is naming and arithmetic, so the whole path from a
/// feature in the tree to reference geometry in the document can be built and tested before
/// <c>OPENMCAD_WITH_OCCT</c> is ever switched on.
/// </para>
/// <para>
/// <b>Failure is an exception here, and data everywhere else.</b> <see cref="DatumResolver"/>
/// reports a refusal as a <see cref="DatumResolution"/> because its other caller is an interactive
/// tool that has to show the user what went wrong while they are still choosing. A rebuild has a
/// different containment boundary: <c>RebuildEngine</c> catches what an evaluator throws and records
/// it as <see cref="FeatureState.Failed"/> against that one feature, which is how §5.4's rule that
/// one bad feature must not take the document with it is actually implemented. Returning an empty
/// output instead would report success and quietly delete the datum.
/// </para>
/// <para>
/// <b>Topology comes from what the engine already resolved.</b> <see cref="FeatureEvaluation.Resolved"/>
/// holds the entities this feature's declared references came to, in declaration order, and
/// <see cref="EntityReference.Property"/> is what says which input each one was for. Re-resolving
/// the names here would be the same work done twice and could come out differently — the geometric
/// tier scores candidates against a model, and the model is not in the same state at both moments.
/// </para>
/// </remarks>
/// <param name="planeOf">
/// How to get the world-space plane a resolved face lies on, or <see langword="null"/> if this
/// configuration cannot — a datum built on a face then fails rather than throwing something
/// unhelpful. Nothing in <see cref="OpenMCAD.Kernel"/> exposes the query yet, which is why it
/// arrives this way (see <see cref="DatumResolver"/>).
/// </param>
/// <param name="pointOf">How to get the world-space position of a resolved vertex.</param>
/// <param name="curveOf">How to get the world-space curve a resolved edge is.</param>
public sealed class DatumFeatureEvaluator(
    Func<SubEntity, Plane?>? planeOf = null,
    Func<SubEntity, Vec3d?>? pointOf = null,
    Func<SubEntity, WorldCurve?>? curveOf = null)
    : IFeatureEvaluator
{
    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// The feature does not describe a datum, or the datum it describes cannot be built. Thrown
    /// rather than returned so that <c>RebuildEngine</c> records it against this feature alone.
    /// </exception>
    public FeatureOutput Evaluate(FeatureEvaluation evaluation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        cancellationToken.ThrowIfCancellationRequested();

        Feature feature = evaluation.Feature;
        DatumTranslation translation = DatumFeature.Translate(feature);

        if (translation.Definition is not { } definition)
        {
            throw new InvalidOperationException(
                $"'{feature.Name}' does not describe reference geometry: {translation.Reason}");
        }

        DatumResolution resolution = DatumResolver.Resolve(
            definition,
            feature.Id,
            evaluation.Document,
            EntitySource(evaluation),
            planeOf,
            pointOf,
            curveOf);

        if (resolution.Geometry is not { } geometry)
        {
            throw new InvalidOperationException(
                $"'{feature.Name}' could not be built: {resolution.Reason}");
        }

        // No bodies and an empty history, and both are correct rather than unfinished. A datum has
        // no faces, edges or vertices, so there is nothing for a HistoryMap to relate to an input;
        // what points at a datum later points at it by name (Owner, Name), which is the document's
        // reference collection and not the naming layer at all.
        return new FeatureOutput([], [geometry], HistoryMap.Empty);
    }

    /// <summary>What a topology reference of this feature already came to.</summary>
    /// <param name="evaluation">What is being evaluated.</param>
    /// <returns>The source.</returns>
    /// <remarks>
    /// Matched by <see cref="PersistentName"/> rather than by position, even though the two arrays
    /// are in the same order. The definition holds names, not indices — it was built from the
    /// feature's references but does not remember which slot each came from — and looking the name
    /// up again is what keeps that true. Two inputs naming the same entity land on the same answer,
    /// which is right.
    /// </remarks>
    private static Func<PersistentName, ResolvedReference> EntitySource(FeatureEvaluation evaluation)
    {
        ImmutableArray<EntityReference> declared = evaluation.Feature.EntityReferences;
        ImmutableArray<ResolvedReference> resolved = evaluation.Resolved;

        return name =>
        {
            for (int i = 0; i < declared.Length && i < resolved.Length; ++i)
            {
                if (declared[i].Name == name)
                {
                    return resolved[i];
                }
            }

            // The name came from this feature's own references, so not finding it means the engine
            // handed over fewer answers than the feature declared -- which happens when a reference
            // failed to resolve and the feature was evaluated anyway.
            return new ResolvedReference(
                NameResolutionOutcome.NotFound,
                [],
                "This feature's reference to a face, edge or vertex was not resolved.");
        };
    }
}
