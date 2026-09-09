using OpenMCAD.Core.Documents;
using OpenMCAD.Solver.Assemblies;

namespace OpenMCAD.Core.Assemblies;

/// <summary>
/// Turns the name a mate records into the geometry a solve needs, by opening the component
/// (P5-T11, P5-T12).
/// </summary>
/// <remarks>
/// <para>
/// The delegate <see cref="MateSystem.For"/> has taken since it was written, now with something
/// real behind it. A mate end names an occurrence and an element inside <em>that component's own
/// document</em>, so answering it means opening that document — which is why this could not be
/// written until there was a store to open it with.
/// </para>
/// <para>
/// <b>Reference geometry only, and that is a deliberate first cut.</b> A datum plane, axis or point
/// in the component has a name of its own that survives the component being edited, which is
/// exactly what a durable mate needs. A <em>face</em> does not: naming one means a
/// <see cref="Naming.PersistentName"/>, which is a trail through that component's rebuild history
/// and can only be resolved by replaying it — work that belongs with the component's own rebuild
/// rather than here. Mating to faces is therefore not yet possible and says so, rather than
/// half-working.
/// </para>
/// <para>
/// <b>A coordinate system is refused rather than guessed at.</b> It could reasonably mean its XY
/// plane or its origin, the two readings <c>DatumResolver</c> distinguishes by asking what
/// <em>role</em> an input plays — and a mate element has no role, it simply is. So a user who wants
/// one or the other names the plane or the point, and the ambiguity never arises.
/// </para>
/// </remarks>
public static class ComponentElements
{
    /// <summary>Builds the element source a mate solve needs.</summary>
    /// <param name="assembly">The assembly whose mates are being resolved.</param>
    /// <param name="store">Where the component documents are found.</param>
    /// <returns>
    /// What a mate's end attaches to, in the component's own frame, or <see langword="null"/> when
    /// it cannot be found — which <see cref="MateSystem.For"/> reports as an unresolved mate rather
    /// than dropping.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// A closure over the assembly and the store rather than a method taking all three, because
    /// what <see cref="MateSystem.For"/> wants is a function of one argument — and because the
    /// store caches, so resolving forty mates against one component opens it once.
    /// </remarks>
    public static Func<MateEnd, MateElement?> For(Assembly assembly, IDocumentStore store)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(store);

        return end =>
        {
            if (end.Occurrence.IsRoot
                || assembly.FindOccurrence(end.Occurrence.Steps[0]) is not { } occurrence
                || assembly.FindDefinition(occurrence.Definition) is not { } definition)
            {
                return null;
            }

            StoredDocument? component = store.Open(definition.Source);

            return component is null
                ? null
                : Of(component.Document, end.Element);
        };
    }

    /// <summary>What a named piece of reference geometry is, as a mate element.</summary>
    /// <param name="component">The component's document.</param>
    /// <param name="element">What the mate names.</param>
    /// <returns>The element, or <see langword="null"/> if the component has no such thing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="component"/> is null.</exception>
    /// <remarks>
    /// Searched by name across every owner, unlike <see cref="Document.FindReference"/>, which takes
    /// the owning feature as well. A mate is written by someone who picked "Front" in another
    /// document's tree and has no idea which feature made it — and requiring them to know would make
    /// the mate break when the component was rebuilt by a different feature, which is precisely what
    /// naming the datum was meant to survive.
    /// </remarks>
    public static MateElement? Of(Document component, string element)
    {
        ArgumentNullException.ThrowIfNull(component);

        if (string.IsNullOrEmpty(element))
        {
            return null;
        }

        foreach (ReferenceGeometry geometry in component.References)
        {
            if (!string.Equals(geometry.Name, element, StringComparison.Ordinal))
            {
                continue;
            }

            return geometry switch
            {
                ReferenceGeometry.Plane plane => new MateElement.Plane(plane.Origin, plane.Normal),
                ReferenceGeometry.Axis axis => new MateElement.Axis(axis.Origin, axis.Direction),
                ReferenceGeometry.Point point => new MateElement.Point(point.Position),

                // A coordinate system could mean its plane or its origin, and nothing here says
                // which. Refused rather than guessed.
                _ => null,
            };
        }

        return null;
    }
}
