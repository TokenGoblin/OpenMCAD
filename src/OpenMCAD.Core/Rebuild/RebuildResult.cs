using System.Collections.Immutable;

using OpenMCAD.Core.Documents;

namespace OpenMCAD.Core.Rebuild;

/// <summary>How a rebuild ended.</summary>
public enum RebuildOutcome
{
    /// <summary>It ran to the end. Some features may still have failed.</summary>
    Completed,

    /// <summary>There was nothing to do.</summary>
    NothingToDo,

    /// <summary>A newer edit arrived, so this rebuild's results were discarded.</summary>
    /// <remarks>
    /// Not a failure. The user edited again before the previous rebuild finished, which is the
    /// normal case while dragging a dimension, and the right response is to throw away work that
    /// describes a document nobody is looking at any more.
    /// </remarks>
    Superseded,

    /// <summary>The caller cancelled it.</summary>
    Cancelled,
}

/// <summary>
/// What a rebuild did.
/// </summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Rebuilt">The features that evaluated, in the order they were evaluated.</param>
/// <param name="Failed">
/// The features whose evaluation threw. P3-T07 turns these into per-feature error state that the
/// user is shown; for now they are recorded so that a caller can tell a clean rebuild from a
/// partial one.
/// </param>
/// <param name="Skipped">
/// The features that could not be attempted because something they consume failed, or because they
/// were suppressed. Reported separately from <paramref name="Failed"/> because the distinction
/// matters to the user: one of these is a problem to fix, the rest are consequences of it.
/// </param>
/// <param name="FromCache">
/// The features whose results were remembered rather than recomputed. A subset of
/// <paramref name="Rebuilt"/>: from the document point of view they were rebuilt, and the
/// distinction is about what it cost -- which is what makes this the instrumentation Phase 3 second
/// exit criterion asks for.
/// </param>
/// <param name="Document">The document as it stood when the rebuild finished.</param>
public sealed record RebuildResult(
    RebuildOutcome Outcome,
    ImmutableArray<FeatureId> Rebuilt,
    ImmutableArray<FeatureId> Failed,
    ImmutableArray<FeatureId> Skipped,
    ImmutableArray<FeatureId> FromCache,
    Document Document)
{
    /// <summary>Gets how many features actually reached the kernel.</summary>
    public int Evaluated => Rebuilt.Length - FromCache.Length;

    /// <summary>Gets whether every feature that was attempted succeeded.</summary>
    public bool IsClean => Outcome is RebuildOutcome.Completed or RebuildOutcome.NothingToDo
        && Failed.IsEmpty
        && Skipped.IsEmpty;

    /// <inheritdoc />
    public override string ToString()
        => $"{Outcome}: {Rebuilt.Length} rebuilt ({FromCache.Length} cached), "
            + $"{Failed.Length} failed, {Skipped.Length} skipped";
}
