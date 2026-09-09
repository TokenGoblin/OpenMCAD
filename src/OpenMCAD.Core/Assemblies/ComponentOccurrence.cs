using OpenMCAD.Math;

namespace OpenMCAD.Core.Assemblies;

/// <summary>
/// <em>Where</em> an instance of a component is: one placement of a
/// <see cref="ComponentDefinition"/> within the assembly holding it (P5-T04, §5.9).
/// </summary>
/// <param name="Id">Identifies this placement within the assembly holding it.</param>
/// <param name="Definition">Which component is placed.</param>
/// <param name="Placement">
/// Where it sits, relative to the assembly that holds it — not to the world. A world position is
/// the product of the placements along an <see cref="OccurrencePath"/>, and storing one here
/// instead would have to be rewritten for every instance whenever a sub-assembly moved.
/// </param>
/// <param name="IsGrounded">
/// Whether this placement is fixed rather than free to be moved by a solver. §5.9's minimal mate
/// set (P5-T05) needs somewhere for "grounded components" to live, and it is a fact about a
/// placement rather than about the component: the same bracket can be the fixed one in this
/// assembly and free in another.
/// </param>
/// <param name="IsSuppressed">
/// Whether the user has switched this placement off. Per occurrence, so that one of ten thousand
/// bolts can be left out without affecting the other nine thousand nine hundred and ninety-nine.
/// </param>
/// <param name="IsHidden">Whether this placement is hidden from view without being suppressed.</param>
/// <param name="Appearance">
/// An appearance overriding the component's own, by name, or <see langword="null"/> to use whatever
/// the definition says. A library of appearances is a later phase; the name is the durable half and
/// is worth carrying from the start so documents authored now do not lose it.
/// </param>
/// <param name="Label">
/// What this placement is called in the tree, or <see langword="null"/> to use the definition's
/// name. Separate from <see cref="ComponentDefinition.Name"/> because renaming a part should rename
/// it everywhere, while "left bracket" and "right bracket" are two placements of one part.
/// </param>
/// <remarks>
/// <para>
/// <b>Hidden and suppressed are two different things, on purpose.</b> A suppressed occurrence is
/// not in the product: it contributes no geometry, no mass and no line on the bill of materials, and
/// a mate to it cannot be solved. A hidden one is all of those things and merely not drawn. Folding
/// them into one flag would make "let me see past this cover" silently change the mass properties.
/// </para>
/// <para>
/// <b>A per-occurrence configuration selection is not here yet.</b> §5.9 lists one, and it is real —
/// the same definition placed as its long variant and its short variant. Configurations are Phase
/// 14, and a slot with nothing to put in it and no rules about what happens when the named
/// configuration is missing would be a promise this build cannot keep.
/// </para>
/// </remarks>
public sealed record ComponentOccurrence(
    OccurrenceId Id,
    ComponentDefinitionId Definition,
    Transform Placement,
    bool IsGrounded = false,
    bool IsSuppressed = false,
    bool IsHidden = false,
    string? Appearance = null,
    string? Label = null)
{
    /// <summary>Gets whether this placement contributes to the product at all.</summary>
    /// <remarks>
    /// Suppression only. A hidden occurrence still counts — it is in the assembly, has mass, and
    /// appears on the bill of materials.
    /// </remarks>
    public bool IsActive => !IsSuppressed;

    /// <summary>Gets what to call this placement in the tree.</summary>
    /// <param name="definition">The definition it places.</param>
    /// <returns>The label, falling back to the definition's name.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="definition"/> is not the one this occurrence places.
    /// </exception>
    public string NameIn(ComponentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.Id != Definition)
        {
            // Cheap, and it catches the mistake that would otherwise show up as a tree labelling
            // every instance after the first with its neighbour's name.
            throw new ArgumentException(
                "That is not the definition this occurrence places.", nameof(definition));
        }

        return Label ?? definition.Name;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        string state = (IsSuppressed, IsHidden) switch
        {
            (true, _) => " (suppressed)",
            (false, true) => " (hidden)",
            _ => "",
        };

        return $"{Label ?? Definition.ToString()}{state}";
    }
}
