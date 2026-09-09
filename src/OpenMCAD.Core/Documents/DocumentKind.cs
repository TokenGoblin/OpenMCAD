namespace OpenMCAD.Core.Documents;

/// <summary>
/// What a document is for, and therefore which of its parts carry meaning (P5-T04).
/// </summary>
/// <remarks>
/// <para>
/// One document type with a kind, rather than three types. Everything the container does —
/// transactions, undo, parameters, metadata, the unknown-field preservation that lets a newer
/// file open here — is the same for all three, and the alternative is that machinery written once
/// and then inherited or copied twice. What actually differs is which payload is populated: a part
/// has features and the bodies they make, an assembly has component definitions and occurrences,
/// and a drawing will have sheets and views.
/// </para>
/// <para>
/// <b>Recorded rather than inferred.</b> A document with no occurrences could be a part or an
/// empty assembly, and those are different things to open, different things to offer commands for,
/// and different things to refuse an edit against. Deriving the kind from what happens to be
/// present would make an assembly become a part the moment its last component was deleted.
/// </para>
/// <para>
/// <b>There used to be two of these.</b> <c>Serialization.DocumentPackage</c> declared its own
/// with the same three values, because a package manifest has to say what it holds — and a manifest
/// describing a document should read the document's own answer rather than keep a parallel
/// vocabulary that nothing forces to agree. The enum moved here, where the concept belongs, and the
/// manifest now uses it. The values keep their original order, so no file already written changes
/// meaning.
/// </para>
/// <para>
/// <see cref="Drawing"/> is declared here before anything produces one. That is deliberate and not
/// speculative generality: the plan commits to drawings at P5-T07, and the cost of the value
/// existing early is nothing, while the cost of adding it later is a schema migration for every
/// file already written — which is exactly what <c>persistence.md</c> says stops being free at the
/// first release.
/// </para>
/// </remarks>
public enum DocumentKind
{
    /// <summary>A part: features, and the bodies they produce.</summary>
    Part = 0,

    /// <summary>An assembly: components, and where each is placed.</summary>
    Assembly = 1,

    /// <summary>A drawing: sheets and views of a model. Nothing produces one yet (P5-T07).</summary>
    Drawing = 2,
}
