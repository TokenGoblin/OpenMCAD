using System.Collections.Immutable;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;
using OpenMCAD.Kernel;

namespace OpenMCAD.Core.Rebuild;

/// <summary>
/// What one feature is being asked to produce, and from what.
/// </summary>
/// <param name="Document">
/// The document as the rebuild has it so far: earlier features in this rebuild have already written
/// their results into it. Not the session's document, which may have moved on.
/// </param>
/// <param name="Feature">The feature to evaluate.</param>
/// <param name="Inputs">
/// The bodies produced by the features this one consumes, in the order those inputs were declared.
/// </param>
/// <param name="References">
/// The entities this feature's declared references resolved to, in declaration order.
/// </param>
public sealed record FeatureEvaluation(
    Document Document,
    Feature Feature,
    ImmutableArray<Body> Inputs,
    ImmutableArray<ResolvedReference> References = default)
{
    /// <summary>Gets the resolved references, never a default array.</summary>
    /// <remarks>
    /// Already resolved when the evaluator is called, and resolved by the core rather than by each
    /// operation. Every feature type would otherwise reimplement the three tiers of §5.3, and they
    /// would disagree -- which is the same as having no policy at all. A reference that could not
    /// be resolved never reaches here: the feature is marked and skipped instead.
    /// </remarks>
    public ImmutableArray<ResolvedReference> Resolved
        => References.IsDefault ? [] : References;
}

/// <summary>
/// What a feature produced.
/// </summary>
/// <param name="Bodies">
/// The bodies this feature now owns. Whatever it owned before is replaced by these, so a feature
/// that used to produce two bodies and now produces one leaves nothing behind.
/// </param>
/// <param name="References">Any reference geometry it created, such as a datum plane.</param>
/// <param name="History">
/// Which of the operation's outputs came from which of its inputs.
/// </param>
public sealed record FeatureOutput(
    ImmutableArray<Body> Bodies,
    ImmutableArray<ReferenceGeometry> References,
    HistoryMap History)
{
    /// <summary>Gets an output with nothing in it, for a feature that produces no geometry.</summary>
    public static FeatureOutput None { get; } = new([], [], HistoryMap.Empty);

    /// <summary>An output of just bodies, with no history recorded.</summary>
    /// <param name="bodies">The bodies.</param>
    /// <returns>The output.</returns>
    /// <remarks>
    /// For features that produce geometry nothing can point into, and for tests. A real modelling
    /// operation that used this would compile and would quietly make every reference into its
    /// result unresolvable, so it is worth being deliberate about.
    /// </remarks>
    public static FeatureOutput Of(params Body[] bodies) => new([.. bodies], [], HistoryMap.Empty);
}

/// <summary>
/// Knows how to turn a feature into geometry.
/// </summary>
/// <remarks>
/// <para>
/// The seam between the document graph and the operations it describes. <c>OpenMCAD.Core</c> knows
/// that features have inputs, an order and results; it does not and must not know what an extrude
/// is. Those definitions live in <c>OpenMCAD.Modeling</c>, which is a layer above this one, so the
/// dependency has to point this way round — and it also means a plugin can supply feature types
/// the core has never heard of (§5.12).
/// </para>
/// <para>
/// <b>Synchronous on purpose.</b> Kernel operations are blocking native calls, and the engine
/// already runs this on the kernel thread through the dispatcher (ADR-0004). Making it
/// <c>async</c> would mean either wrapping a synchronous call in a task, which buys nothing and
/// costs a thread hop, or letting an implementation await something and release the kernel thread
/// mid-operation, which is exactly what a single-threaded kernel actor exists to prevent.
/// </para>
/// </remarks>
public interface IFeatureEvaluator
{
    /// <summary>Evaluates one feature.</summary>
    /// <param name="evaluation">What to produce, and from what.</param>
    /// <param name="cancellationToken">
    /// Cancels a long operation. Honouring it is optional but strongly wanted: a rebuild superseded
    /// by a newer edit can only stop at a boundary the evaluator gives it.
    /// </param>
    /// <returns>
    /// What the feature produced, including the <see cref="HistoryMap"/> relating its outputs to
    /// its inputs. That map is the only record of which output came from which input, it exists
    /// only while the operation runs, and it cannot be asked for afterwards (ADR-0002, §5.1) —
    /// an implementation that returns an empty one has destroyed the information every reference
    /// into its result depends on.
    /// </returns>
    FeatureOutput Evaluate(FeatureEvaluation evaluation, CancellationToken cancellationToken);
}
