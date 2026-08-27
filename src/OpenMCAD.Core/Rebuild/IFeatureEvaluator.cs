using System.Collections.Immutable;

using OpenMCAD.Core.Documents;

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
public sealed record FeatureEvaluation(
    Document Document,
    Feature Feature,
    ImmutableArray<Body> Inputs);

/// <summary>
/// What a feature produced.
/// </summary>
/// <param name="Bodies">
/// The bodies this feature now owns. Whatever it owned before is replaced by these, so a feature
/// that used to produce two bodies and now produces one leaves nothing behind.
/// </param>
/// <param name="References">Any reference geometry it created, such as a datum plane.</param>
public sealed record FeatureOutput(
    ImmutableArray<Body> Bodies,
    ImmutableArray<ReferenceGeometry> References)
{
    /// <summary>Gets an output with nothing in it, for a feature that produces no geometry.</summary>
    public static FeatureOutput None { get; } = new([], []);

    /// <summary>An output of just bodies.</summary>
    /// <param name="bodies">The bodies.</param>
    /// <returns>The output.</returns>
    public static FeatureOutput Of(params Body[] bodies) => new([.. bodies], []);
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
    /// <returns>What the feature produced.</returns>
    FeatureOutput Evaluate(FeatureEvaluation evaluation, CancellationToken cancellationToken);
}
