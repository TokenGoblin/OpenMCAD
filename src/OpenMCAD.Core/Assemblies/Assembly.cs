using System.Collections.Immutable;

using OpenMCAD.Math;

namespace OpenMCAD.Core.Assemblies;

/// <summary>
/// The structure of one assembly: which components it uses, and where each is placed (P5-T04, §5.9).
/// </summary>
/// <remarks>
/// <para>
/// Two collections, and keeping them apart is the whole design. <see cref="Definitions"/> holds one
/// entry per distinct document, however many times it appears; <see cref="Occurrences"/> holds one
/// entry per placement. §5.9: "never duplicate the definition. This distinction is structural — get
/// it wrong and large assemblies become unusable."
/// </para>
/// <para>
/// <b>This is one assembly, not a whole product.</b> An occurrence of a sub-assembly does not carry
/// that sub-assembly's own occurrences — those live in its document, reached through
/// <see cref="ComponentDefinition.Source"/>, and are read by whoever can load it. So the tree a user
/// sees spans documents, and the thing that names one node of it is an
/// <see cref="OccurrencePath"/> rather than anything held here. <see cref="WorldTransformOf"/> takes
/// a resolver for the same reason, and that reason is the same one <c>DatumResolver</c> takes its
/// geometry queries as delegates: the layer that can load another document is above this one.
/// </para>
/// <para>
/// <b>Immutable, like <see cref="Documents.Document"/>.</b> The <c>With…</c> methods return a new
/// assembly, so undo is holding an earlier reference and a rebuild reads a structure that cannot
/// change underneath it.
/// </para>
/// </remarks>
public sealed class Assembly
{
    private readonly ImmutableDictionary<ComponentDefinitionId, ComponentDefinition> _definitionsById;
    private readonly ImmutableDictionary<OccurrenceId, ComponentOccurrence> _occurrencesById;

    private Assembly(
        ImmutableArray<ComponentDefinition> definitions,
        ImmutableDictionary<ComponentDefinitionId, ComponentDefinition> definitionsById,
        ImmutableArray<ComponentOccurrence> occurrences,
        ImmutableDictionary<OccurrenceId, ComponentOccurrence> occurrencesById)
    {
        Definitions = definitions;
        _definitionsById = definitionsById;
        Occurrences = occurrences;
        _occurrencesById = occurrencesById;
    }

    /// <summary>Gets an assembly with nothing in it.</summary>
    public static Assembly Empty { get; } = new(
        [],
        ImmutableDictionary<ComponentDefinitionId, ComponentDefinition>.Empty,
        [],
        ImmutableDictionary<OccurrenceId, ComponentOccurrence>.Empty);

    /// <summary>Gets the components used, one entry each however often they are placed.</summary>
    public ImmutableArray<ComponentDefinition> Definitions { get; }

    /// <summary>Gets the placements, in the order the user arranged them.</summary>
    public ImmutableArray<ComponentOccurrence> Occurrences { get; }

    /// <summary>Gets whether this assembly holds no placements.</summary>
    public bool IsEmpty => Occurrences.IsEmpty;

    /// <summary>Finds a definition by its id.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The definition, or <see langword="null"/> if there is none.</returns>
    public ComponentDefinition? FindDefinition(ComponentDefinitionId id)
        => _definitionsById.TryGetValue(id, out ComponentDefinition? found) ? found : null;

    /// <summary>Finds a placement by its id.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The occurrence, or <see langword="null"/> if there is none.</returns>
    public ComponentOccurrence? FindOccurrence(OccurrenceId id)
        => _occurrencesById.TryGetValue(id, out ComponentOccurrence? found) ? found : null;

    /// <summary>Counts how many times a component is placed in this assembly.</summary>
    /// <param name="definition">The component.</param>
    /// <returns>The number of placements, suppressed ones included.</returns>
    /// <remarks>
    /// Suppressed placements count here, because this answers "how is this definition used" — the
    /// question a caller asks before removing one. What goes on a bill of materials is a different
    /// count and a later phase, and folding them together would make one of the two silently wrong.
    /// </remarks>
    public int PlacementsOf(ComponentDefinitionId definition)
        => Occurrences.Count(o => o.Definition == definition);

    /// <summary>Adds a component this assembly can place.</summary>
    /// <param name="definition">The component.</param>
    /// <returns>The new assembly.</returns>
    /// <exception cref="ArgumentException">A definition with that id is already here.</exception>
    public Assembly WithDefinition(ComponentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (!definition.Id.IsValid)
        {
            throw new ArgumentException(
                "A component definition needs an id.", nameof(definition));
        }

        if (_definitionsById.ContainsKey(definition.Id))
        {
            throw new ArgumentException(
                $"{definition.Id} is already a component of this assembly.", nameof(definition));
        }

        return new Assembly(
            Definitions.Add(definition),
            _definitionsById.Add(definition.Id, definition),
            Occurrences,
            _occurrencesById);
    }

    /// <summary>Places a component.</summary>
    /// <param name="occurrence">The placement.</param>
    /// <returns>The new assembly.</returns>
    /// <exception cref="ArgumentException">
    /// The placement has no id, one with that id is already here, or it names a component this
    /// assembly does not have.
    /// </exception>
    /// <remarks>
    /// Refusing a placement of an unknown definition is the invariant that makes everything else
    /// safe to write: <see cref="WorldTransformOf"/>, a tree walk and a count can all assume a
    /// placement's definition is present, so none of them has to invent an answer for a dangling
    /// one. It is enforced here rather than checked later because a structure that can hold the
    /// broken state will eventually be found holding it.
    /// </remarks>
    public Assembly WithOccurrence(ComponentOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);

        if (!occurrence.Id.IsValid)
        {
            throw new ArgumentException("A placement needs an id.", nameof(occurrence));
        }

        if (_occurrencesById.ContainsKey(occurrence.Id))
        {
            throw new ArgumentException(
                $"{occurrence.Id} is already a placement in this assembly.", nameof(occurrence));
        }

        if (!_definitionsById.ContainsKey(occurrence.Definition))
        {
            throw new ArgumentException(
                $"{occurrence.Definition} is not a component of this assembly, so it cannot be "
                + "placed in it.",
                nameof(occurrence));
        }

        return new Assembly(
            Definitions,
            _definitionsById,
            Occurrences.Add(occurrence),
            _occurrencesById.Add(occurrence.Id, occurrence));
    }

    /// <summary>Replaces a placement, keeping its position in the tree.</summary>
    /// <param name="occurrence">The placement, with the id of the one it replaces.</param>
    /// <returns>The new assembly.</returns>
    /// <exception cref="ArgumentException">
    /// There is no placement with that id, or it names a component this assembly does not have.
    /// </exception>
    public Assembly ReplaceOccurrence(ComponentOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);

        int index = IndexOf(occurrence.Id);

        if (index < 0)
        {
            throw new ArgumentException(
                $"{occurrence.Id} is not a placement in this assembly.", nameof(occurrence));
        }

        if (!_definitionsById.ContainsKey(occurrence.Definition))
        {
            throw new ArgumentException(
                $"{occurrence.Definition} is not a component of this assembly.", nameof(occurrence));
        }

        return new Assembly(
            Definitions,
            _definitionsById,
            Occurrences.SetItem(index, occurrence),
            _occurrencesById.SetItem(occurrence.Id, occurrence));
    }

    /// <summary>Removes a placement.</summary>
    /// <param name="id">Which placement.</param>
    /// <returns>The new assembly, or this one if there was no such placement.</returns>
    /// <remarks>
    /// The definition stays, even when this was its last placement. Removing it too would throw
    /// away the user's choice of what this assembly is built from on the strength of one deletion,
    /// and re-inserting the component would then have to find the document again.
    /// <see cref="WithoutUnusedDefinitions"/> is the deliberate version of that.
    /// </remarks>
    public Assembly WithoutOccurrence(OccurrenceId id)
    {
        int index = IndexOf(id);

        return index < 0
            ? this
            : new Assembly(
                Definitions,
                _definitionsById,
                Occurrences.RemoveAt(index),
                _occurrencesById.Remove(id));
    }

    /// <summary>Removes every component that is not placed anywhere.</summary>
    /// <returns>The new assembly.</returns>
    public Assembly WithoutUnusedDefinitions()
    {
        HashSet<ComponentDefinitionId> used = [.. Occurrences.Select(o => o.Definition)];

        if (used.Count == Definitions.Length)
        {
            return this;
        }

        ImmutableArray<ComponentDefinition> kept = [.. Definitions.Where(d => used.Contains(d.Id))];

        return new Assembly(
            kept,
            kept.ToImmutableDictionary(d => d.Id),
            Occurrences,
            _occurrencesById);
    }

    /// <summary>Works out where an instance sits in the world.</summary>
    /// <param name="path">Which instance.</param>
    /// <param name="assemblyOf">
    /// How to get the structure of a sub-assembly, given the definition an occurrence places, or
    /// <see langword="null"/> if this configuration cannot load one — a path that descends into a
    /// sub-assembly then reports <see cref="OccurrenceOutcome.Unloaded"/> rather than throwing.
    /// </param>
    /// <returns>Where it sits, or why that could not be worked out.</returns>
    /// <remarks>
    /// <para>
    /// The composition is outermost-first: a bolt in a bracket in a chassis sits at
    /// <c>chassis × bracket × bolt</c>. Written in that order because <see cref="Transform"/>
    /// composes so that <c>(a * b).TransformPoint(p)</c> is <c>a.TransformPoint(b.TransformPoint(p))</c>,
    /// and the bolt's own placement is relative to the bracket.
    /// </para>
    /// <para>
    /// Deriving it every time rather than caching a world transform per instance: there is no
    /// instance to cache it on (see <see cref="OccurrencePath"/>), and a cache would have to be
    /// invalidated for every instance under a sub-assembly whenever that sub-assembly moved, which
    /// is the update this design exists to avoid doing ten thousand times.
    /// </para>
    /// </remarks>
    public OccurrenceResolution WorldTransformOf(
        OccurrencePath path, Func<ComponentDefinition, Assembly?>? assemblyOf = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        Assembly level = this;
        Transform world = Transform.Identity;

        for (int i = 0; i < path.Depth; ++i)
        {
            OccurrenceId step = path.Steps[i];
            ComponentOccurrence? occurrence = level.FindOccurrence(step);

            if (occurrence is null)
            {
                return OccurrenceResolution.Failed(
                    OccurrenceOutcome.NotFound,
                    $"There is no placement {step} at depth {i} of this path.");
            }

            world *= occurrence.Placement;

            if (i == path.Depth - 1)
            {
                return OccurrenceResolution.Found(world, occurrence);
            }

            ComponentDefinition definition = level.FindDefinition(occurrence.Definition)!;

            if (!definition.IsAssembly)
            {
                return OccurrenceResolution.Failed(
                    OccurrenceOutcome.NotAnAssembly,
                    $"'{definition.Name}' is a part, so the path cannot continue inside it.");
            }

            Assembly? inner = assemblyOf?.Invoke(definition);

            if (inner is null)
            {
                return OccurrenceResolution.Failed(
                    OccurrenceOutcome.Unloaded,
                    $"The structure of '{definition.Name}' is not available here.");
            }

            level = inner;
        }

        // The root path names this assembly, which sits where it sits: at the identity.
        return OccurrenceResolution.Found(Transform.Identity, null);
    }

    /// <summary>Says what is wrong with this assembly, if anything.</summary>
    /// <returns>The complaints, empty when there are none.</returns>
    /// <remarks>
    /// Only what this assembly can answer alone. Whether a source document exists, whether it is
    /// really the kind its definition claims, and whether the product contains a cycle are all
    /// cross-document questions and belong to P5-T11 — asserting them here would mean either
    /// loading every document, which is what §5.9's lightweight modes exist to avoid, or guessing.
    /// </remarks>
    public ImmutableArray<string> Problems()
    {
        ImmutableArray<string>.Builder found = ImmutableArray.CreateBuilder<string>();

        foreach (ComponentDefinition definition in Definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.Source))
            {
                found.Add($"'{definition.Name}' does not say which document defines it.");
            }
        }

        foreach (ComponentOccurrence occurrence in Occurrences)
        {
            if (!occurrence.Placement.IsValid)
            {
                ComponentDefinition? definition = FindDefinition(occurrence.Definition);

                found.Add(
                    $"'{occurrence.Label ?? definition?.Name ?? "A placement"}' has a placement "
                    + "that is not a usable transform.");
            }
        }

        return found.ToImmutable();
    }

    /// <inheritdoc />
    public override string ToString()
        => $"{Occurrences.Length} placements of {Definitions.Length} components";

    private int IndexOf(OccurrenceId id)
    {
        for (int i = 0; i < Occurrences.Length; ++i)
        {
            if (Occurrences[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>How working out where an instance sits came to.</summary>
public enum OccurrenceOutcome
{
    /// <summary>The path named an instance and it was placed.</summary>
    Resolved,

    /// <summary>A step of the path names no placement in the assembly it points into.</summary>
    NotFound,

    /// <summary>The path descends into a component that is a part and has nothing inside it.</summary>
    NotAnAssembly,

    /// <summary>
    /// The path descends into a sub-assembly whose structure this configuration cannot load.
    /// </summary>
    /// <remarks>
    /// Not a failure of the model. §5.9's Lightweight and Graphics-only modes exist so that a large
    /// assembly need not load every document it names, so "not loaded" is an ordinary answer and is
    /// told apart from <see cref="NotFound"/>, which means the path is wrong.
    /// </remarks>
    Unloaded,
}

/// <summary>Where an instance sits, or why that could not be worked out.</summary>
/// <param name="Outcome">How it turned out.</param>
/// <param name="World">
/// Where it sits, when <paramref name="Outcome"/> is <see cref="OccurrenceOutcome.Resolved"/>.
/// </param>
/// <param name="Occurrence">
/// The placement the path named, or <see langword="null"/> for the root path, which names the
/// assembly itself.
/// </param>
/// <param name="Reason">Why, in words, when it could not be worked out.</param>
public sealed record OccurrenceResolution(
    OccurrenceOutcome Outcome,
    Transform World,
    ComponentOccurrence? Occurrence = null,
    string? Reason = null)
{
    /// <summary>Gets whether the instance was placed.</summary>
    public bool IsResolved => Outcome == OccurrenceOutcome.Resolved;

    /// <summary>Creates a resolution that placed an instance.</summary>
    /// <param name="world">Where it sits.</param>
    /// <param name="occurrence">The placement, or null for the root.</param>
    /// <returns>The resolution.</returns>
    public static OccurrenceResolution Found(Transform world, ComponentOccurrence? occurrence)
        => new(OccurrenceOutcome.Resolved, world, occurrence);

    /// <summary>Creates a resolution that failed.</summary>
    /// <param name="outcome">How it failed. Must not be <see cref="OccurrenceOutcome.Resolved"/>.</param>
    /// <param name="reason">Why, in words.</param>
    /// <returns>The resolution.</returns>
    public static OccurrenceResolution Failed(OccurrenceOutcome outcome, string reason)
        => new(outcome, Transform.Identity, null, reason);
}
