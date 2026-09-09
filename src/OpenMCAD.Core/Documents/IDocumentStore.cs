using OpenMCAD.Core.Serialization;

namespace OpenMCAD.Core.Documents;

/// <summary>A document a store found, and what it was when it was found.</summary>
/// <param name="Document">The document.</param>
/// <param name="Stamp">
/// What it was at that moment, in whatever form the store issues — to be handed to
/// <see cref="ExternalReference"/> and compared for equality later, never interpreted.
/// </param>
/// <param name="Manifest">What the container said about itself.</param>
public sealed record StoredDocument(Document Document, string Stamp, DocumentManifest Manifest);

/// <summary>
/// Where the documents a reference names are found (P5-T12).
/// </summary>
/// <remarks>
/// <para>
/// The thing every cross-document delegate written so far has been standing in for.
/// <c>ComponentDefinition.Source</c>, <c>MateEnd.Element</c> and the <c>elementOf</c> parameter of
/// <c>MateSystem.For</c> all needed something that could turn a name into an open document, and
/// this is it.
/// </para>
/// <para>
/// <b>An interface because the answer is not always a file.</b> A component can come from a folder,
/// a PDM vault, a library shipped with the application, or a test fixture that never touches a
/// disk. Everything above this layer works in terms of a target string it does not interpret, which
/// is exactly what lets those be different implementations rather than special cases.
/// </para>
/// <para>
/// <b><see cref="StampOf"/> is separate from <see cref="Open"/> on purpose.</b> An out-of-date
/// indicator asks about every dependency of an assembly, and answering by opening each one would
/// make showing the indicator as expensive as loading the whole product — which is the cost §5.9's
/// lightweight display modes exist to avoid. A store is expected to answer this cheaply, and a
/// stamp that required reading the document would be the wrong stamp.
/// </para>
/// </remarks>
public interface IDocumentStore
{
    /// <summary>Opens the document a reference names.</summary>
    /// <param name="target">Which document, as a reference records it.</param>
    /// <returns>The document, or <see langword="null"/> if there is nothing there.</returns>
    /// <exception cref="DocumentFormatException">The document is there and cannot be read.</exception>
    /// <remarks>
    /// Null for absent and an exception for damaged, because they are different things to tell a
    /// user: one is a file to find and the other is a file to repair. Reporting a corrupt document
    /// as missing would send them looking for something that is right where they left it.
    /// </remarks>
    StoredDocument? Open(string target);

    /// <summary>Says what a document is now, without opening it.</summary>
    /// <param name="target">Which document.</param>
    /// <returns>
    /// Its stamp, or <see langword="null"/> if there is nothing there — which is what
    /// <see cref="ExternalReference.Health"/> reads as <see cref="ExternalReferenceHealth.Missing"/>.
    /// </returns>
    string? StampOf(string target);
}
