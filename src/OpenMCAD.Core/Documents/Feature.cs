using System.Collections.Immutable;

namespace OpenMCAD.Core.Documents;

/// <summary>
/// One node in a document's feature graph.
/// </summary>
/// <param name="Id">Identifies this feature for its whole life.</param>
/// <param name="Name">What it is called in the tree. Shown to the user, and editable by them.</param>
/// <param name="FeatureType">
/// Which kind of operation this is — <c>Extrude</c>, <c>Fillet</c>, <c>Revolve</c>. A name rather
/// than a type, because what the operation *does* is declared in <c>OpenMCAD.Modeling</c> and this
/// layer must not depend on it: the document graph has to be readable, diffable and migratable
/// without loading the code that executes it, which is also what makes a plugin's feature type
/// survive being opened on a machine where that plugin is missing.
/// </param>
/// <param name="Inputs">
/// The features this one consumes, in declaration order. This is the edge set of the dependency
/// graph and the reason tree order is not it.
/// </param>
/// <param name="Parameters">This feature's own values, such as a depth or a radius.</param>
/// <param name="References">
/// The particular faces, edges and vertices this feature was built on, each with the multiplicity
/// policy its declaration chose (P3-T12).
/// </param>
/// <param name="IsSuppressed">Whether the user has switched this feature off.</param>
/// <remarks>
/// <para>
/// <b>Inputs are declared, never inferred from tree order.</b> The tree is a sequence a person
/// arranged and can rearrange; the graph is what actually constrains evaluation. Treating the
/// sequence as the graph makes every feature depend on the one above it, which turns a change to
/// the first feature into a rebuild of all of them and makes reordering unsafe for no reason
/// (§5.4). Deriving the graph from what each feature says it consumes costs nothing and is right.
/// </para>
/// <para>
/// <b>Inputs are coarse and references are fine, and both are kept.</b> A fillet does not really
/// consume an extrude — it consumes four particular edges of what the extrude produced.
/// <see cref="Inputs"/> records the dependency at feature grain, which is all the graph needs in
/// order to sequence a rebuild; <see cref="References"/> records which entities, which is what the
/// operation needs in order to run. The graph reads both, so a feature that declares only
/// references still gets its edges (P3-T03).
/// </para>
/// <para>
/// <b>Suppression is not error state.</b> This flag is the user's decision to skip a feature.
/// A feature that failed, or that cannot run because something it depends on failed, is in a
/// different condition that the user did not ask for and needs to be told about — which is
/// P3-T07's rebuild report, kept separate from this on purpose.
/// </para>
/// </remarks>
public sealed record Feature(
    FeatureId Id,
    string Name,
    string FeatureType,
    ImmutableArray<FeatureId> Inputs,
    ImmutableArray<Parameter> Parameters,
    ImmutableArray<Naming.EntityReference> References = default,
    bool IsSuppressed = false)
{
    /// <summary>Gets the entity references, never a default array.</summary>
    public ImmutableArray<Naming.EntityReference> EntityReferences
        => References.IsDefault ? [] : References;
    /// <summary>Creates a feature with no inputs and no parameters.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="name">Its display name.</param>
    /// <param name="featureType">Which kind of operation it is.</param>
    /// <returns>The feature.</returns>
    public static Feature Create(FeatureId id, string name, string featureType)
        => new(id, name, featureType, [], []);

    /// <summary>Gets whether this feature consumes nothing and so can always evaluate first.</summary>
    public bool IsRoot => Inputs.IsEmpty;

    /// <summary>Finds one of this feature's own parameters.</summary>
    /// <param name="name">What it is called, compared without regard to case.</param>
    /// <returns>The parameter, or <see langword="null"/> if this feature has none by that name.</returns>
    public Parameter? FindParameter(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        foreach (Parameter parameter in Parameters)
        {
            if (Parameter.NameComparer.Equals(parameter.Name, name))
            {
                return parameter;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public override string ToString()
        => IsSuppressed ? $"{FeatureType} '{Name}' (suppressed)" : $"{FeatureType} '{Name}'";
}
