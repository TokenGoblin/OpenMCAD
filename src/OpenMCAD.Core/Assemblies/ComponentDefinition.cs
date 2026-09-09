namespace OpenMCAD.Core.Assemblies;

/// <summary>What kind of document a component definition points at.</summary>
public enum ComponentKind
{
    /// <summary>A part: it has features and bodies of its own and holds no occurrences.</summary>
    Part,

    /// <summary>
    /// A sub-assembly: it holds occurrences of its own, which are reached through this one rather
    /// than copied into it.
    /// </summary>
    SubAssembly,
}

/// <summary>
/// <em>What</em> a component is: one entry per distinct document an assembly uses, however many
/// times it is placed (P5-T04, §5.9).
/// </summary>
/// <param name="Id">Identifies this definition within the assembly holding it.</param>
/// <param name="Source">
/// Where the document defining the component lives, as the reference a later phase resolves. Held
/// as text rather than as a loaded document on purpose — see the remarks.
/// </param>
/// <param name="Name">
/// What the component is called in the tree. A property of the definition rather than of a
/// placement, because renaming the part should rename it everywhere it appears; an occurrence that
/// wants to read differently gets there through <see cref="ComponentOccurrence.Label"/>.
/// </param>
/// <param name="Kind">Whether the document is a part or a sub-assembly.</param>
/// <remarks>
/// <para>
/// <b>The definition is referenced once, and this is the once.</b> §5.9 is unusually emphatic:
/// "never duplicate the definition. This distinction is structural — get it wrong and large
/// assemblies become unusable." Ten thousand bolts are one of these and ten thousand
/// <see cref="ComponentOccurrence"/>s. Storing the definition per placement would multiply memory,
/// load time and every edit by the placement count, and would make "change the bolt" a ten-thousand
/// point edit that can partially fail.
/// </para>
/// <para>
/// <b>Why <paramref name="Source"/> is a reference and not a document.</b> Loading is what
/// <see cref="ComponentKind"/> lets a caller defer: §5.9's Lightweight and Graphics-only display
/// modes exist precisely so that a large assembly need not load every document it names, and a
/// definition that held a loaded document would make them impossible to express. Resolving one is
/// P5-T11's cross-document reference tracking, and this deliberately says nothing about how — a
/// path, a URI and a library key all fit through the same slot.
/// </para>
/// <para>
/// <b>Why <see cref="ComponentKind"/> is recorded rather than discovered.</b> An assembly has to
/// know whether an occurrence can have children before it has loaded the document that would say
/// so, or it cannot draw a tree, count instances or detect a cycle without opening everything —
/// which is the cost the whole lightweight arrangement exists to avoid. Recording it means the file
/// can be wrong about it; that is a cross-document consistency check for P5-T11, and a cheaper
/// mistake than the alternative.
/// </para>
/// </remarks>
public sealed record ComponentDefinition(
    ComponentDefinitionId Id,
    string Source,
    string Name,
    ComponentKind Kind = ComponentKind.Part)
{
    /// <summary>Gets whether occurrences of this definition can have children.</summary>
    public bool IsAssembly => Kind == ComponentKind.SubAssembly;

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({Kind}, {Source})";
}
