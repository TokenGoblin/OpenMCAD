using System.Collections.Immutable;

using OpenMCAD.Solver.Assemblies;

namespace OpenMCAD.Core.Assemblies;

/// <summary>
/// One end of a mate as a document holds it: which instance, and what on it, by name (P5-T06).
/// </summary>
/// <param name="Occurrence">Which instance in this assembly.</param>
/// <param name="Element">
/// What the mate attaches to, named in the component's <em>own</em> document — a datum plane it
/// carries, or eventually a persistent name for one of its faces.
/// </param>
/// <remarks>
/// <para>
/// A name and not geometry, for the reason <see cref="ComponentDefinition.Source"/> is a reference
/// and not a document: the point of a mate is that it keeps meaning something when the component it
/// names is edited. Storing the plane a face lay on when the mate was made would give an assembly
/// that quietly stopped following its own parts.
/// </para>
/// <para>
/// <b>The name belongs to the other document, not this one.</b> That is what makes it a string here
/// rather than a <see cref="Naming.PersistentName"/>: a persistent name is a trail through one
/// model's rebuild history, and a mate names something inside a component whose history this
/// assembly has never seen. Turning the name into geometry therefore needs that document open,
/// which is P5-T11's cross-document work; until then it arrives through a delegate, the same way
/// <c>DatumResolver</c> takes the queries no layer here can answer yet.
/// </para>
/// </remarks>
public sealed record MateEnd(OccurrencePath Occurrence, string Element);

/// <summary>
/// A mate as a document holds it: durable, named, and independent of where anything currently sits
/// (P5-T06, §5.9).
/// </summary>
/// <param name="Id">Identifies this mate, so a diagnosis can name it and a user can delete it.</param>
/// <param name="Kind">What it says.</param>
/// <param name="First">One end.</param>
/// <param name="Second">The other.</param>
/// <param name="Value">How far apart, for <see cref="MateKind.Distance"/>.</param>
/// <param name="IsFlipped">Whether it is satisfied the other way round.</param>
/// <param name="IsSuppressed">
/// Whether the user has switched it off. A suppressed mate constrains nothing and is not handed to
/// the solver, which is different from deleting it: the mate is still there to switch back on, and
/// the freedom it was removing comes back in the meantime.
/// </param>
/// <remarks>
/// <para>
/// The same split as everything else durable here: this is what a file holds, and
/// <see cref="Solver.Assemblies.AssemblyMate"/> is what a solve is handed. The solver's version
/// carries geometry in each body's frame and knows nothing of documents; this one carries names and
/// knows nothing of arithmetic. <see cref="MateSystem"/> is the one place that turns one into the
/// other.
/// </para>
/// <para>
/// <b><see cref="MateId"/> and <see cref="MateKind"/> are the solver's own types, deliberately
/// reused.</b> A second identity would mean every diagnosis had to be translated back before it
/// could name anything a user recognises, and a second enumeration of the mate kinds would be two
/// lists that nothing forces to agree — the duplicate-vocabulary mistake `DocumentKind` had already
/// been made once here (P5-T04). <c>OpenMCAD.Solver</c> is below this layer, so depending on it is
/// the right direction.
/// </para>
/// </remarks>
public sealed record MateDefinition(
    MateId Id,
    MateKind Kind,
    MateEnd First,
    MateEnd Second,
    double Value = 0,
    bool IsFlipped = false,
    bool IsSuppressed = false)
{
    /// <summary>Gets the instances this mate joins, without duplicates.</summary>
    public ImmutableArray<OccurrencePath> Instances => First.Occurrence.Equals(Second.Occurrence)
        ? [First.Occurrence]
        : [First.Occurrence, Second.Occurrence];

    /// <summary>Gets whether this mate takes part in a solve.</summary>
    public bool IsActive => !IsSuppressed;

    /// <inheritdoc />
    public override string ToString()
    {
        string state = IsSuppressed ? " (suppressed)" : "";

        return Kind == MateKind.Distance
            ? $"{Kind} {Value} of {First.Element} to {Second.Element}{state}"
            : $"{Kind} of {First.Element} to {Second.Element}{state}";
    }
}
