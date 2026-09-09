using System.Collections.Immutable;

namespace OpenMCAD.Core.Documents;

/// <summary>What a document is doing about one of the documents it depends on.</summary>
/// <remarks>
/// §5.9 asks for lock, break and unlock by name, and these are the three states those verbs move
/// between. The distinction that matters is not how each behaves today but what each promises about
/// tomorrow: a linked reference will change when its target does, a locked one will not, and a
/// broken one has stopped being a reference at all.
/// </remarks>
public enum ExternalReferenceState
{
    /// <summary>Follows the target. It updates when the target changes.</summary>
    Linked,

    /// <summary>
    /// Frozen where it was. The target may move on; this document keeps what it had.
    /// </summary>
    /// <remarks>
    /// Deliberate, and reversible — unlocking makes it <see cref="Linked"/> again and the next
    /// rebuild picks up whatever the target has become. What a user reaches for when they need a
    /// drawing to stop moving under them before a release.
    /// </remarks>
    Locked,

    /// <summary>
    /// Severed. Whatever came from the target is kept as it was and nothing tracks it any more.
    /// </summary>
    /// <remarks>
    /// The end of the relationship rather than a pause in it. Kept in the list rather than deleted
    /// because a user needs to see that geometry they are looking at came from somewhere once —
    /// a document that silently forgot would leave them with a shape and no account of it.
    /// </remarks>
    Broken,
}

/// <summary>How an external reference stands right now.</summary>
/// <remarks>
/// Six, because each is a different thing to show a user and a different thing for them to do about
/// it. Collapsing them is tempting and wrong: "out of date" and "you locked it and it has moved on"
/// look alike and are opposites — one is a thing to fix, the other a thing the user chose.
/// </remarks>
public enum ExternalReferenceHealth
{
    /// <summary>Linked, and the target is what it was when this document last read it.</summary>
    UpToDate,

    /// <summary>Linked, and the target has changed. This document needs rebuilding.</summary>
    /// <remarks>The out-of-date indicator §5.9 asks for, and the reason the stamp is stored.</remarks>
    OutOfDate,

    /// <summary>Locked, and the target has not moved. Nothing to say.</summary>
    Held,

    /// <summary>Locked, and the target has moved on. The user chose this, and should know.</summary>
    HeldBehind,

    /// <summary>Severed. Nothing is tracked and nothing will change.</summary>
    Broken,

    /// <summary>
    /// The target cannot be found at all — moved, renamed, or on a drive nobody has mounted.
    /// </summary>
    /// <remarks>
    /// Told apart from <see cref="OutOfDate"/> because the repairs are unrelated: one is a rebuild,
    /// the other is finding a file. Reported for a locked or broken reference too, since "the thing
    /// this came from is gone" is worth knowing even when nothing is following it.
    /// </remarks>
    Missing,
}

/// <summary>
/// One document this one depends on, and what is being done about it (P5-T11, §5.9).
/// </summary>
/// <param name="Target">
/// Which document, in whatever form the caller resolves — the same slot
/// <see cref="Assemblies.ComponentDefinition.Source"/> uses, and deliberately not interpreted here.
/// </param>
/// <param name="Stamp">
/// What the target was when this document last read it. Compared against the target's stamp now to
/// decide whether anything has changed; its form is the caller's business, so a content hash, a
/// version number and a modification time all fit.
/// </param>
/// <param name="State">Whether this document is following the target, holding, or has let go.</param>
/// <param name="Note">
/// Why, in the user's own words or the application's, for a reference that was locked or broken.
/// Kept because §5.9 calls in-context references a hazardous feature and asks for clear UI: "broken"
/// with no account of when or why is exactly the dead end it warns about.
/// </param>
/// <remarks>
/// <para>
/// <b>Stored, not derived.</b> Which documents an assembly uses could be read off its components,
/// and that is the part that <em>is</em> derivable — but what the target was when it was last read
/// is not recoverable from anything, and neither is whether the user locked it. Those are the two
/// facts an out-of-date indicator needs, so this list is a record rather than a view.
/// </para>
/// <para>
/// <b>It lives in its own part of the container, not in the document graph.</b> §5.8 reserves
/// <c>/refs/external.json</c>, and the reason is that a file browser, a PDM system or an open dialog
/// wants to know what a document depends on <em>without</em> parsing the graph — which for a large
/// assembly is most of the cost of opening it.
/// </para>
/// </remarks>
public sealed record ExternalReference(
    string Target,
    string Stamp,
    ExternalReferenceState State = ExternalReferenceState.Linked,
    string? Note = null)
{
    /// <summary>Gets whether this reference still follows its target.</summary>
    public bool IsFollowing => State == ExternalReferenceState.Linked;

    /// <summary>Says how this reference stands, given what the target is now.</summary>
    /// <param name="current">
    /// The target's stamp now, or <see langword="null"/> if the target cannot be found.
    /// </param>
    /// <returns>How it stands.</returns>
    /// <remarks>
    /// A missing target outranks every other answer, because there is nothing to compare against
    /// and reporting a reference as up to date when the file it names has gone would be the worst
    /// available lie.
    /// </remarks>
    public ExternalReferenceHealth Health(string? current)
    {
        if (current is null)
        {
            return ExternalReferenceHealth.Missing;
        }

        bool moved = !string.Equals(current, Stamp, StringComparison.Ordinal);

        return State switch
        {
            ExternalReferenceState.Linked => moved
                ? ExternalReferenceHealth.OutOfDate
                : ExternalReferenceHealth.UpToDate,

            ExternalReferenceState.Locked => moved
                ? ExternalReferenceHealth.HeldBehind
                : ExternalReferenceHealth.Held,

            _ => ExternalReferenceHealth.Broken,
        };
    }

    /// <summary>Returns this reference frozen where it is.</summary>
    /// <param name="why">Why it was locked.</param>
    /// <returns>The locked reference.</returns>
    public ExternalReference Lock(string? why = null)
        => this with { State = ExternalReferenceState.Locked, Note = why };

    /// <summary>Returns this reference following its target again.</summary>
    /// <returns>The linked reference.</returns>
    /// <remarks>
    /// The stamp is left alone. Unlocking says "follow this again", not "you are already current" —
    /// so a reference unlocked after its target moved reports <see cref="ExternalReferenceHealth.OutOfDate"/>
    /// and gets rebuilt, which is the whole point of unlocking it.
    /// </remarks>
    public ExternalReference Unlock() => this with
    {
        State = ExternalReferenceState.Linked,
        Note = null,
    };

    /// <summary>Returns this reference severed.</summary>
    /// <param name="why">Why it was broken.</param>
    /// <returns>The broken reference.</returns>
    public ExternalReference Break(string? why = null)
        => this with { State = ExternalReferenceState.Broken, Note = why };

    /// <summary>Returns this reference brought up to date with its target.</summary>
    /// <param name="stamp">What the target is now.</param>
    /// <returns>The refreshed reference.</returns>
    /// <exception cref="InvalidOperationException">This reference is not following its target.</exception>
    /// <remarks>
    /// Refused for a locked or broken reference rather than quietly doing it. A rebuild that
    /// refreshed a locked reference would defeat the lock, and doing it silently is how a user
    /// discovers their frozen drawing was not frozen after all.
    /// </remarks>
    public ExternalReference RefreshedTo(string stamp)
    {
        ArgumentNullException.ThrowIfNull(stamp);

        return IsFollowing
            ? this with { Stamp = stamp }
            : throw new InvalidOperationException(
                $"'{Target}' is {State} and does not follow its target, so it cannot be refreshed.");
    }

    /// <inheritdoc />
    public override string ToString() => Note is null
        ? $"{State} to {Target}"
        : $"{State} to {Target} ({Note})";
}

/// <summary>
/// Finds the cycles cross-document references can make (P5-T11, §5.9).
/// </summary>
/// <remarks>
/// §5.9 asks for "cycle detection at commit", and puts in-context references on a short list of
/// features it calls hazardous. A cycle is the hazard: part A shaped against part B while B is
/// shaped against A has no rebuild order, and neither document is wrong on its own — which is why
/// it has to be caught where the edge is added rather than where the rebuild fails.
/// </remarks>
public static class ExternalReferenceGraph
{
    /// <summary>Finds a cycle reachable from a document, if there is one.</summary>
    /// <param name="from">The document to start at.</param>
    /// <param name="targetsOf">
    /// What a document depends on. Returning nothing for a document that cannot be opened is the
    /// right answer: an unreadable document cannot be shown to complete a cycle, and reporting one
    /// on that basis would refuse an edit for a file that might be innocent.
    /// </param>
    /// <returns>
    /// The cycle, beginning and ending at the same document, or empty when there is none.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// <para>
    /// The path rather than a yes or no, for the reason §5.6 gives about over-constrained sketches:
    /// a refusal the user cannot act on is barely better than no refusal. "A depends on B depends on
    /// C depends on A" tells them which link to cut.
    /// </para>
    /// <para>
    /// Depth-first with an explicit stack, so that a deep product does not overflow one. Documents
    /// are visited once even when reached by several routes — the second route cannot make a cycle
    /// the first did not, and a product where every part references a common library would
    /// otherwise be exponential.
    /// </para>
    /// </remarks>
    public static ImmutableArray<string> FindCycle(
        string from, Func<string, IEnumerable<string>> targetsOf)
    {
        ArgumentException.ThrowIfNullOrEmpty(from);
        ArgumentNullException.ThrowIfNull(targetsOf);

        HashSet<string> settled = new(StringComparer.Ordinal);
        HashSet<string> onPath = new(StringComparer.Ordinal);
        List<string> path = [];

        return Walk(from, targetsOf, settled, onPath, path) ? [.. path] : [];
    }

    private static bool Walk(
        string at,
        Func<string, IEnumerable<string>> targetsOf,
        HashSet<string> settled,
        HashSet<string> onPath,
        List<string> path)
    {
        if (!onPath.Add(at))
        {
            // Round to where we already are. Trim the run-up so the answer is the loop itself and
            // not the road that reached it -- a user asked to cut a link needs the links that form
            // the ring, not the ones leading to it.
            int start = path.IndexOf(at);
            path.RemoveRange(0, start);
            path.Add(at);

            return true;
        }

        path.Add(at);

        foreach (string next in targetsOf(at) ?? [])
        {
            if (settled.Contains(next))
            {
                continue;
            }

            if (Walk(next, targetsOf, settled, onPath, path))
            {
                return true;
            }
        }

        onPath.Remove(at);
        settled.Add(at);
        path.RemoveAt(path.Count - 1);

        return false;
    }
}
