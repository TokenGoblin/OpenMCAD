using System.Collections.Immutable;

namespace OpenMCAD.Core.Assemblies;

/// <summary>
/// Names one instance in a product: the chain of placements from the top assembly down to it
/// (P5-T04, §5.9).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an instance has no id.</b> A sub-assembly placed twice is one document with one set of
/// occurrences, reached through two parents — the whole point of not duplicating the definition. So
/// its children have the same <see cref="OccurrenceId"/> under both parents, and the id alone
/// cannot say which of the two bolts anyone means. The path can, and it is the shortest thing that
/// can: the ids are unique within each assembly, so the chain is unique within the product.
/// </para>
/// <para>
/// The alternative — minting an instance id per placement when a sub-assembly is inserted — is
/// exactly the duplication §5.9 forbids, in a different disguise. It also breaks the moment someone
/// adds a bolt to the sub-assembly: every product using it would need new ids issued for the
/// instances that appeared, and nothing would relate them to what was there before.
/// </para>
/// <para>
/// <b>Ordering is by content, not by identity.</b> Two paths built separately from the same ids are
/// the same path, so this compares and hashes structurally rather than by array reference — the
/// trap <see cref="Documents.Feature"/> already documents, met here again.
/// </para>
/// </remarks>
public sealed class OccurrencePath : IEquatable<OccurrencePath>
{
    private readonly int _hash;

    private OccurrencePath(ImmutableArray<OccurrenceId> steps)
    {
        Steps = steps;

        HashCode hash = default;

        foreach (OccurrenceId step in steps)
        {
            hash.Add(step);
        }

        _hash = hash.ToHashCode();
    }

    /// <summary>Gets the path that names the top assembly itself.</summary>
    public static OccurrencePath Root { get; } = new([]);

    /// <summary>Gets the placements from the top assembly down, outermost first.</summary>
    public ImmutableArray<OccurrenceId> Steps { get; }

    /// <summary>Gets how deep this instance sits. Zero for the top assembly.</summary>
    public int Depth => Steps.Length;

    /// <summary>Gets whether this names the top assembly rather than something in it.</summary>
    public bool IsRoot => Steps.IsEmpty;

    /// <summary>Gets the last step, which is the placement this path actually names.</summary>
    /// <exception cref="InvalidOperationException">This is the root, which names no placement.</exception>
    public OccurrenceId Leaf => IsRoot
        ? throw new InvalidOperationException(
            "The root path names the assembly itself rather than a placement in it.")
        : Steps[^1];

    /// <summary>Builds a path from its steps, outermost first.</summary>
    /// <param name="steps">The placements.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentException">A step does not name a placement.</exception>
    public static OccurrencePath Of(params OccurrenceId[] steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        return Of((IEnumerable<OccurrenceId>)steps);
    }

    /// <summary>Builds a path from its steps, outermost first.</summary>
    /// <param name="steps">The placements.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentException">A step does not name a placement.</exception>
    public static OccurrencePath Of(IEnumerable<OccurrenceId> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        ImmutableArray<OccurrenceId> taken = [.. steps];

        foreach (OccurrenceId step in taken)
        {
            if (!step.IsValid)
            {
                // A default id in the middle of a path would name nothing and compare equal to
                // every other default, so two unrelated instances would look like one.
                throw new ArgumentException(
                    "A path step must name a placement.", nameof(steps));
            }
        }

        return taken.IsEmpty ? Root : new OccurrencePath(taken);
    }

    /// <summary>Returns this path extended by one placement inside what it names.</summary>
    /// <param name="step">The placement.</param>
    /// <returns>The longer path.</returns>
    /// <exception cref="ArgumentException"><paramref name="step"/> does not name a placement.</exception>
    public OccurrencePath Then(OccurrenceId step)
    {
        if (!step.IsValid)
        {
            throw new ArgumentException("A path step must name a placement.", nameof(step));
        }

        return new OccurrencePath(Steps.Add(step));
    }

    /// <summary>Returns the path of the assembly holding what this one names.</summary>
    /// <returns>The shorter path.</returns>
    /// <exception cref="InvalidOperationException">This is the root, which has no parent.</exception>
    public OccurrencePath Parent() => IsRoot
        ? throw new InvalidOperationException("The root path has no parent.")
        : Of(Steps[..^1]);

    /// <summary>Gets whether this path names <paramref name="other"/> or something inside it.</summary>
    /// <param name="other">The possible ancestor.</param>
    /// <returns>Whether it is one.</returns>
    /// <remarks>
    /// True for a path and itself. What "everything under this sub-assembly" means when a user
    /// hides one, and the reason it is a prefix test rather than an id test: the same sub-assembly
    /// placed elsewhere shares its children's ids and must not be hidden too.
    /// </remarks>
    public bool IsUnder(OccurrencePath other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (other.Depth > Depth)
        {
            return false;
        }

        for (int i = 0; i < other.Depth; ++i)
        {
            if (Steps[i] != other.Steps[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public bool Equals(OccurrencePath? other)
        => other is not null && (ReferenceEquals(this, other) || Steps.SequenceEqual(other.Steps));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as OccurrencePath);

    /// <inheritdoc />
    public override int GetHashCode() => _hash;

    /// <inheritdoc />
    public override string ToString() => IsRoot
        ? "/"
        : "/" + string.Join("/", Steps.Select(s => s.Value.ToString("N")[..8]));
}
