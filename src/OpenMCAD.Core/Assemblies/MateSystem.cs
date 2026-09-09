using System.Collections.Immutable;

using OpenMCAD.Math;
using OpenMCAD.Solver.Assemblies;

namespace OpenMCAD.Core.Assemblies;

/// <summary>A mate that could not be handed to the solver, and why.</summary>
/// <param name="Mate">Which mate.</param>
/// <param name="Reason">Why, in words the user can act on.</param>
public sealed record UnresolvedMate(MateId Mate, string Reason);

/// <summary>
/// One assembly's mates, as a system a solver can be handed, and the way back (P5-T06).
/// </summary>
/// <param name="Bodies">The instances the solve may move, and which are fixed.</param>
/// <param name="Mates">The mates, with geometry in each body's own frame.</param>
/// <param name="Instances">Which instance each body stands for.</param>
/// <param name="Unresolved">The mates that could not be translated, and why.</param>
/// <remarks>
/// <para>
/// The one place the document's vocabulary and the solver's meet. Above it an assembly is
/// components, occurrences and named mates; below it there are rigid bodies, placements and
/// residuals, and <c>OpenMCAD.Solver</c> is a layer that has never heard of a document. Everything
/// that crosses does so here, which is what keeps the solver reusable and the document ignorant of
/// least squares.
/// </para>
/// <para>
/// <b>A body per instance, keyed by <see cref="OccurrencePath"/>.</b> That is the whole reason a
/// path exists (P5-T04): the same component placed twice is two bodies that move independently, and
/// an id would name both. The <see cref="MateBodyId"/>s are minted per system and are not durable —
/// they mean nothing outside the solve they were made for, which is why <see cref="Instances"/>
/// is carried alongside rather than the ids being stored anywhere.
/// </para>
/// </remarks>
public sealed record MateSystem(
    ImmutableArray<MateBody> Bodies,
    ImmutableArray<AssemblyMate> Mates,
    ImmutableDictionary<MateBodyId, OccurrencePath> Instances,
    ImmutableArray<UnresolvedMate> Unresolved)
{
    /// <summary>Builds the system an assembly's mates describe.</summary>
    /// <param name="assembly">The assembly.</param>
    /// <param name="elementOf">
    /// What a mate's end attaches to, in the component's own frame, or <see langword="null"/> if it
    /// cannot be found. Supplied by the caller because the element is named inside another
    /// document, which this layer cannot open (P5-T11).
    /// </param>
    /// <param name="moving">
    /// The instance being dragged, whose placement the solve must not change, or
    /// <see langword="null"/> when this is not a drag. Grounded for the duration, so the rest of
    /// the assembly comes to it rather than it springing back.
    /// </param>
    /// <returns>The system.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// <para>
    /// <b>Only this assembly's own placements become bodies.</b> A sub-assembly's contents are in
    /// another document, so a product-wide solve needs those documents open and belongs with the
    /// rest of P5-T11's cross-document work. Every path here is therefore one step long, and a mate
    /// naming something deeper is reported rather than guessed at.
    /// </para>
    /// <para>
    /// <b>A suppressed placement takes its mates with it.</b> It is not in the product, so a mate to
    /// it has nothing to hold, and handing the solver a body that is not there would let a
    /// suppressed component go on constraining the ones that are.
    /// </para>
    /// </remarks>
    public static MateSystem For(
        Assembly assembly,
        Func<MateEnd, MateElement?> elementOf,
        OccurrencePath? moving = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(elementOf);

        Dictionary<OccurrencePath, MateBodyId> ids = [];
        ImmutableArray<MateBody>.Builder bodies = ImmutableArray.CreateBuilder<MateBody>();

        foreach (ComponentOccurrence occurrence in assembly.Occurrences.Where(o => o.IsActive))
        {
            OccurrencePath path = OccurrencePath.Of(occurrence.Id);
            MateBodyId id = MateBodyId.New();

            ids[path] = id;

            // Grounded because the user said so, or because they are holding it. A dragged
            // component is the fixed point of its own drag: §5.9's live mate solving means the rest
            // of the assembly follows the hand, not that the hand is corrected by the assembly.
            bool fixedNow = occurrence.IsGrounded || path.Equals(moving);

            bodies.Add(new MateBody(id, occurrence.Placement, fixedNow));
        }

        ImmutableArray<AssemblyMate>.Builder mates = ImmutableArray.CreateBuilder<AssemblyMate>();
        ImmutableArray<UnresolvedMate>.Builder unresolved =
            ImmutableArray.CreateBuilder<UnresolvedMate>();

        foreach (MateDefinition mate in assembly.Mates.Where(m => m.IsActive))
        {
            if (Translate(mate, ids, elementOf, out AssemblyMate? translated, out string? why))
            {
                mates.Add(translated!);
            }
            else
            {
                unresolved.Add(new UnresolvedMate(mate.Id, why!));
            }
        }

        return new MateSystem(
            bodies.ToImmutable(),
            mates.ToImmutable(),
            ids.ToImmutableDictionary(pair => pair.Value, pair => pair.Key),
            unresolved.ToImmutable());
    }

    /// <summary>Puts a solve's answer back into the assembly.</summary>
    /// <param name="assembly">The assembly the system was built from.</param>
    /// <param name="result">What the solve came to.</param>
    /// <returns>The assembly, with its placements moved to where the mates put them.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// <para>
    /// A placement is only replaced when it actually moved. An assembly whose mates already held
    /// would otherwise come back as a different object with the same numbers, and every rebuild
    /// would look like an edit — which is the difference between a document that reports itself
    /// dirty after opening and one that does not.
    /// </para>
    /// <para>
    /// Grounded instances are skipped rather than written back unchanged, for the same reason and
    /// one more: a solver is not the thing that decides where a fixed component sits, so reading
    /// its answer for one would be asking a question whose answer must not matter.
    /// </para>
    /// </remarks>
    public Assembly ApplyTo(Assembly assembly, MateSolveResult result)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(result);

        Assembly moved = assembly;

        foreach (MateBody body in Bodies.Where(b => !b.IsGrounded))
        {
            if (!result.Placements.TryGetValue(body.Id, out Transform placed)
                || !Instances.TryGetValue(body.Id, out OccurrencePath? path)
                || path.IsRoot)
            {
                continue;
            }

            ComponentOccurrence? occurrence = moved.FindOccurrence(path.Leaf);

            if (occurrence is null || occurrence.Placement.IsNear(placed))
            {
                continue;
            }

            moved = moved.ReplaceOccurrence(occurrence with { Placement = placed });
        }

        return moved;
    }

    private static bool Translate(
        MateDefinition mate,
        Dictionary<OccurrencePath, MateBodyId> ids,
        Func<MateEnd, MateElement?> elementOf,
        out AssemblyMate? translated,
        out string? why)
    {
        translated = null;
        why = null;

        if (!ids.TryGetValue(mate.First.Occurrence, out MateBodyId first)
            || !ids.TryGetValue(mate.Second.Occurrence, out MateBodyId second))
        {
            why = "One of the components this mate joins is not in the assembly, or is suppressed.";
            return false;
        }

        MateElement? a = elementOf(mate.First);
        MateElement? b = elementOf(mate.Second);

        if (a is null || b is null)
        {
            // Told apart from a missing component because the repair differs: a mate whose
            // component is gone has to be deleted, and one whose face has been renamed away can be
            // re-pointed at another.
            why = "What this mate attaches to could not be found in the component.";
            return false;
        }

        translated = new AssemblyMate(
            mate.Id,
            mate.Kind,
            new MateAttachment(first, a),
            new MateAttachment(second, b),
            mate.Value,
            mate.IsFlipped);

        return true;
    }
}
