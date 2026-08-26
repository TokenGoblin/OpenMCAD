using System.Collections.Immutable;

namespace OpenMCAD.Core.Documents;

/// <summary>
/// Thrown when a document's features depend on one another in a loop.
/// </summary>
/// <remarks>
/// <para>
/// A cycle has no evaluation order, so there is nothing sensible for a rebuild to do with one. It
/// is also always the result of an edit somebody just made, which is why this carries the loop
/// itself rather than only saying that one exists: "this document contains a circular dependency"
/// tells the user that they are stuck, and nothing about where.
/// </para>
/// <para>
/// The message renders the loop by feature name, in order, returning to where it started. Names
/// rather than ids because the user chose the names and the ids mean nothing to them.
/// </para>
/// </remarks>
public sealed class FeatureCycleException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="cycle">
    /// The features in the loop, in dependency order. The first is not repeated at the end; the
    /// loop closing back to it is what makes it a cycle and is rendered rather than stored.
    /// </param>
    /// <param name="names">What each of those features is called.</param>
    public FeatureCycleException(ImmutableArray<FeatureId> cycle, ImmutableArray<string> names)
        : base(Describe(names))
    {
        Cycle = cycle;
        Names = names;
    }

    /// <summary>Creates the exception with a plain message.</summary>
    /// <param name="message">The message.</param>
    public FeatureCycleException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a plain message and an inner cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public FeatureCycleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with nothing to say.</summary>
    public FeatureCycleException()
        : base("The features in this document depend on one another in a loop.")
    {
    }

    /// <summary>Gets the features in the loop, in dependency order.</summary>
    public ImmutableArray<FeatureId> Cycle { get; }

    /// <summary>Gets what each feature in the loop is called.</summary>
    public ImmutableArray<string> Names { get; }

    private static string Describe(ImmutableArray<string> names)
    {
        if (names.IsDefaultOrEmpty)
        {
            return "The features in this document depend on one another in a loop.";
        }

        // Closed back to the start, so the loop reads as a loop rather than as a list that happens
        // to be circular.
        string path = string.Join(" -> ", names.Append(names[0]));

        return $"These features depend on one another in a loop: {path}. A loop has no order to "
            + "evaluate in, so one of these references has to be removed.";
    }
}
