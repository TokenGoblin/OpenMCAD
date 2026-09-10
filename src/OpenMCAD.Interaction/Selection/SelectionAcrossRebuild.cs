using System.Collections.Immutable;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;
using OpenMCAD.Kernel;

namespace OpenMCAD.Interaction.Selection;

/// <summary>What became of a selection carried across a rebuild.</summary>
/// <param name="Restored">
/// How many of the remembered names were found again. This counts names, not entities, so it can
/// exceed the size of the resulting selection: a rebuild that merged two selected faces into one
/// found both of them, and the selection is simply smaller than it was.
/// </param>
/// <param name="Lost">
/// How many were not. A count rather than a silence, because a selection quietly shrinking is
/// indistinguishable to a user from one they mis-clicked, and the two want different reactions.
/// </param>
public readonly record struct SelectionCarry(int Restored, int Lost)
{
    /// <summary>Gets whether everything survived.</summary>
    public bool IsComplete => Lost == 0;
}

/// <summary>
/// Carries a selection through a rebuild that renumbers the model (P2-T09, §5.3).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SelectionSet"/> holds <see cref="SubEntity"/>, and that is deliberate rather than a
/// gap to close. A selection is read every frame to decide what is highlighted, and it is compared
/// against what the display snapshot holds — kernel entities. Storing persistent names instead
/// would put name resolution on the highlight path, which is the wrong place for it twice over: it
/// is per-frame work to answer a question that only changes when the model does, and a name
/// resolves to zero or one entity where highlighting needs the entity itself.
/// </para>
/// <para>
/// So the durability belongs at the boundary where it is actually needed. A rebuild is the only
/// thing that renumbers entities, so a selection is written down as names before one and read back
/// after: <see cref="Remember"/> then <see cref="Restore"/>. Between those two calls the selection
/// is ordinary and cheap, which is what every other frame wants.
/// </para>
/// <para>
/// <b>What cannot be found is dropped, and counted.</b> A name that no longer resolves means the
/// face is gone or has become several, and §5.3's third tier is explicit that the answer to an
/// ambiguous reference is not a guess. Selecting both halves of a split face would be a guess, and
/// one made silently: the user would go on to act on something they did not choose. Dropping and
/// reporting lets the caller say what happened, which is the only honest option — though selecting
/// every candidate is a defensible alternative for a *selection* specifically, where the cost of
/// being wrong is a wasted click rather than a broken feature, and it is worth revisiting with the
/// UI that will surface this.
/// </para>
/// </remarks>
public static class SelectionAcrossRebuild
{
    /// <summary>Writes down what is selected, in a form a later rebuild can find.</summary>
    /// <param name="selection">What the user has selected.</param>
    /// <param name="history">The rebuild that produced the entities now selected.</param>
    /// <param name="consumer">
    /// The feature the names are written on behalf of, or <see cref="FeatureId.None"/> for a
    /// selection that belongs to no feature — which is what a user's own selection is.
    /// </param>
    /// <returns>
    /// A name for every entity that could be named. Entities the history cannot account for are
    /// left out here rather than failing the whole call: one unnameable face in a selection of
    /// twenty should cost that face, not the other nineteen.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ImmutableArray<PersistentName> Remember(
        SelectionSet selection, RebuildHistory history, FeatureId consumer)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(history);

        NameMinter minter = new(history);
        ImmutableArray<PersistentName>.Builder names =
            ImmutableArray.CreateBuilder<PersistentName>(selection.Count);

        foreach (SubEntity entity in selection.Selected)
        {
            if (minter.Mint(entity, consumer) is { } name)
            {
                names.Add(name);
            }
        }

        return names.ToImmutable();
    }

    /// <summary>Puts back what can still be found, and says what could not.</summary>
    /// <param name="selection">The selection to rewrite.</param>
    /// <param name="names">What <see cref="Remember"/> wrote down.</param>
    /// <param name="history">The rebuild that has just finished.</param>
    /// <param name="consumer">As passed to <see cref="Remember"/>.</param>
    /// <returns>How many were found again, and how many were not.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// The selection is cleared first, so what comes back is what the names found and nothing
    /// else. Restoring on top of the existing selection would leave entities from the previous
    /// rebuild in place — which are exactly the stale handles this whole exercise exists to
    /// replace, and they would be invisible among the ones that resolved.
    /// </remarks>
    public static SelectionCarry Restore(
        SelectionSet selection,
        ImmutableArray<PersistentName> names,
        RebuildHistory history,
        FeatureId consumer)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(history);

        HistoryNameResolver resolver = new(history);

        selection.Clear();

        int restored = 0;

        foreach (PersistentName name in names)
        {
            NameResolution resolution = resolver.Resolve(name, consumer);

            if (!resolution.IsResolved)
            {
                continue;
            }

            // Counted on the resolution rather than on whether the set grew. A rebuild that merged
            // two selected faces into one leaves both names resolving to the same entity, and the
            // second Apply reports no change -- which is true of the set and false about the name,
            // because that face was found. Counting the set would report it lost and tell the user
            // something did not survive when it did.
            selection.Apply(resolution.Entity, SelectionAction.Add);
            ++restored;
        }

        return new SelectionCarry(restored, names.Length - restored);
    }
}
