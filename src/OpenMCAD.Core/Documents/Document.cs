using System.Collections.Immutable;

using OpenMCAD.Core.Assemblies;
using OpenMCAD.Core.Serialization;

namespace OpenMCAD.Core.Documents;

/// <summary>
/// A part document: its parameters, its features, what they produced, and the geometry they refer
/// to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Immutable, with every change producing a new document.</b> §5.4 requires that nothing mutates
/// outside a transaction, and names internal setters as the mechanism. This goes one further and
/// makes the state unchangeable altogether: the <c>With…</c> methods below are <see langword="internal"/>,
/// so only a transaction in this assembly can produce a new version, and no amount of holding a
/// reference lets a caller alter the one they have.
/// </para>
/// <para>
/// That is not gold-plating; it is what makes the rest of the phase cheap. Undo becomes holding an
/// earlier reference rather than replaying inverse operations (P3-T17). A rebuild running on the
/// kernel thread reads a document that cannot change underneath it, which is the same reason the
/// viewport renders from an immutable snapshot. And "identical after undo, asserted by full graph
/// comparison" — Phase 3's fourth exit criterion — is a comparison the type can answer itself.
/// The cost is allocation per edit, and the collections here share structure, so an edit to one
/// feature copies a spine of pointers rather than the document.
/// </para>
/// <para>
/// <b>The feature list is order and the inputs are truth.</b> <see cref="Features"/> is the
/// sequence the user arranged and sees in the tree. What must be evaluated before what comes from
/// each feature's declared inputs, and building that graph is P3-T03. Nothing here should be read
/// as implying that a feature depends on the one before it.
/// </para>
/// </remarks>
public sealed class Document
{
    private readonly ImmutableDictionary<FeatureId, Feature> _featuresById;
    private readonly ImmutableDictionary<BodyId, Body> _bodiesById;
    private readonly ImmutableDictionary<string, Parameter> _parametersByName;

    private Document(
        ImmutableArray<Feature> features,
        ImmutableDictionary<FeatureId, Feature> featuresById,
        ImmutableDictionary<BodyId, Body> bodiesById,
        ImmutableDictionary<string, Parameter> parametersByName,
        ImmutableArray<ReferenceGeometry> references,
        DocumentMetadata metadata,
        int? rollbackPosition,
        RebuildReport report,
        long version,
        DocumentKind kind,
        Assembly assembly,
        ImmutableArray<ExternalReference> externalReferences,
        ImmutableArray<UnknownField> unknownFields = default)
    {
        Kind = kind;
        Assembly = assembly;
        ExternalReferences = externalReferences;
        UnknownFields = unknownFields.IsDefault ? [] : unknownFields;
        RollbackPosition = rollbackPosition;
        Report = report;
        Features = features;
        _featuresById = featuresById;
        _bodiesById = bodiesById;
        _parametersByName = parametersByName;
        References = references;
        Metadata = metadata;
        Version = version;
    }

    /// <summary>Gets what this document is for, and so which of its parts carry meaning.</summary>
    public DocumentKind Kind { get; }

    /// <summary>Gets the components this document places, and where.</summary>
    /// <remarks>
    /// Empty for anything that is not a <see cref="DocumentKind.Assembly"/>, and a transaction
    /// refuses to put a component in one — a part that could quietly acquire occurrences would be
    /// an assembly that nothing had declared, and every reader deciding for itself which it was
    /// looking at is the disagreement <see cref="Kind"/> exists to prevent.
    /// </remarks>
    public Assembly Assembly { get; }

    /// <summary>Gets the documents this one depends on, and what is being done about each.</summary>
    /// <remarks>
    /// Carried on the document so that locking or breaking one is an edit like any other — a
    /// transaction, and therefore an undo. It is written to its own part of the container rather
    /// than into the graph (§5.8's <c>/refs/external.json</c>), so that something which only wants
    /// to know what a file depends on need not parse the graph to find out.
    /// </remarks>
    public ImmutableArray<ExternalReference> ExternalReferences { get; }

    /// <summary>Gets the features, in the order the user arranged them.</summary>
    public ImmutableArray<Feature> Features { get; }

    /// <summary>Gets the fields of the file this document came from that this build cannot read.</summary>
    /// <remarks>
    /// P3-T20. Carried on the document rather than handed back beside it, because it has to survive
    /// editing: someone who opens a colleague's file from a newer build, changes one parameter and
    /// saves must not thereby delete everything that build had added. Anything held at the file
    /// boundary would be dropped by the first edit, which is the case that matters.
    /// </remarks>
    internal ImmutableArray<UnknownField> UnknownFields { get; }

    /// <summary>Gets how many fields of the file this document came from it could not read.</summary>
    /// <remarks>
    /// Public where the fields themselves are not, because the count is the part anyone outside
    /// this assembly has a use for: it means the file came from a build that knows things this one
    /// does not, and someone about to edit and save it is entitled to be told. The fields are kept
    /// whole and handed back untouched (P3-T20); what is in them is nobody here's business.
    /// </remarks>
    public int UnreadFieldCount => UnknownFields.Length;

    /// <summary>
    /// Gets how many features from the top of the tree are active, or null when all of them are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rollback bar (P3-T06). Everything at or after this position is rolled back: not
    /// evaluated, and holding no geometry. It is a position rather than a feature id because that
    /// is what the user is manipulating -- they drag a line to a place in the list, and a line that
    /// stuck to a particular feature would move when the tree was reordered around it.
    /// </para>
    /// <para>
    /// Null rather than <c>Features.Length</c> for "no rollback", so that adding a feature to a
    /// document that was never rolled back does not leave the bar sitting one short of the end and
    /// silently hide the new feature.
    /// </para>
    /// </remarks>
    public int? RollbackPosition { get; }

    /// <summary>Gets how many features are active.</summary>
    public int ActiveFeatureCount
        => System.Math.Clamp(RollbackPosition ?? Features.Length, 0, Features.Length);

    /// <summary>Gets the features that are not rolled back, in tree order.</summary>
    public ImmutableArray<Feature> ActiveFeatures => Features[..ActiveFeatureCount];

    /// <summary>Gets whether the document is rolled back at all.</summary>
    public bool IsRolledBack => ActiveFeatureCount < Features.Length;

    /// <summary>Gets the reference geometry: datum planes, axes, points and frames.</summary>
    public ImmutableArray<ReferenceGeometry> References { get; }

    /// <summary>Gets the document's properties.</summary>
    public DocumentMetadata Metadata { get; }

    /// <summary>Gets how every feature stood after the last rebuild.</summary>
    /// <remarks>
    /// Derived state, held here rather than returned from the rebuild that produced it, because the
    /// tree has to keep showing which features are in error long after that rebuild's caller has
    /// finished with its result. Keeping it on the document also means undo restores the report
    /// belonging to the state it restored, instead of leaving error marks from a version of the
    /// model that no longer exists.
    /// </remarks>
    public RebuildReport Report { get; }

    /// <summary>
    /// Gets which version of this document's history this is. Increases with every change.
    /// </summary>
    /// <remarks>
    /// Not a revision in the engineering sense — that lives in <see cref="DocumentMetadata"/> and
    /// means something to a person. This one exists so that two documents can be told apart, so a
    /// cache can know whether what it holds is current, and so a rebuild that finishes late can be
    /// recognised as superseded rather than applied over a newer state.
    /// </remarks>
    public long Version { get; }

    /// <summary>Gets the bodies the features have produced.</summary>
    public IReadOnlyCollection<Body> Bodies => _bodiesById.Values.ToImmutableArray();

    /// <summary>Gets the document's named values.</summary>
    public IReadOnlyCollection<Parameter> Parameters => _parametersByName.Values.ToImmutableArray();

    /// <summary>Gets an empty document, with origin geometry and nothing else.</summary>
    /// <returns>The document.</returns>
    public static Document Empty() => Empty(DocumentKind.Part);

    /// <summary>Gets an empty document of a given kind, with origin geometry and nothing else.</summary>
    /// <param name="kind">What it is for.</param>
    /// <returns>The document.</returns>
    /// <remarks>
    /// An assembly gets the standard datums too. They are what a first component is placed against
    /// and what a mate to "the front plane" resolves to, so a document that had to grow them later
    /// would be one whose first action was to create somewhere to work — the same argument
    /// <see cref="ReferenceGeometry.StandardDatums"/> already makes for parts.
    /// </remarks>
    public static Document Empty(DocumentKind kind) => new(
        [],
        ImmutableDictionary<FeatureId, Feature>.Empty,
        ImmutableDictionary<BodyId, Body>.Empty,
        ImmutableDictionary.Create<string, Parameter>(Parameter.NameComparer),
        [.. ReferenceGeometry.StandardDatums()],
        DocumentMetadata.Empty,
        rollbackPosition: null,
        report: RebuildReport.Empty,
        version: 0,
        kind: kind,
        assembly: Assembly.Empty,
        externalReferences: []);

    /// <summary>Whether this document describes the same model as another.</summary>
    /// <param name="other">The document to compare with.</param>
    /// <returns>Whether the two are the same in every respect that matters.</returns>
    /// <remarks>
    /// <para>
    /// Deep, and deliberately not <see cref="object.Equals(object)"/>. Two documents that are the
    /// same model are still two different points in one editing history, and a session that
    /// treated them as interchangeable would lose the distinction undo depends on.
    /// </para>
    /// <para>
    /// <see cref="Version"/> is excluded, because it counts edits rather than describing the
    /// model: undoing three changes and redoing them returns the same model at a higher version,
    /// and that is not a difference anybody means. Everything else is compared, including the
    /// order of the features, the rollback bar, and the report — Phase 3's fourth exit criterion
    /// asks for an identical state after a hundred operations, and a comparison that skipped a
    /// field would be unable to see the one thing that had gone wrong.
    /// </para>
    /// </remarks>
    public bool Matches(Document? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null
            || !UnknownFields.SequenceEqual(other.UnknownFields)
            || !Features.SequenceEqual(other.Features)
            || !References.SequenceEqual(other.References)
            || !Metadata.Equals(other.Metadata)
            || Kind != other.Kind
            || !Assembly.Definitions.SequenceEqual(other.Assembly.Definitions)
            || !Assembly.Occurrences.SequenceEqual(other.Assembly.Occurrences)
            || !Assembly.Mates.SequenceEqual(other.Assembly.Mates)
            || !ExternalReferences.SequenceEqual(other.ExternalReferences)
            || ActiveFeatureCount != other.ActiveFeatureCount
            || _parametersByName.Count != other._parametersByName.Count
            || _bodiesById.Count != other._bodiesById.Count)
        {
            return false;
        }

        foreach (Parameter parameter in _parametersByName.Values)
        {
            if (other.FindParameter(parameter.Name) != parameter)
            {
                return false;
            }
        }

        foreach (Body body in _bodiesById.Values)
        {
            if (other.FindBody(body.Id) != body)
            {
                return false;
            }
        }

        return MatchesReport(other.Report);
    }

    /// <summary>Whether the two reports say the same about the same features.</summary>
    private bool MatchesReport(RebuildReport other)
    {
        if (Report.Count != other.Count)
        {
            return false;
        }

        foreach (FeatureDiagnostic diagnostic in Report.Diagnostics)
        {
            if (other.For(diagnostic.Feature) is not { } theirs
                || theirs.State != diagnostic.State
                || theirs.Cause != diagnostic.Cause)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Gets whether a feature is evaluated, or is behind the rollback bar.</summary>
    /// <param name="id">Which feature.</param>
    /// <returns>Whether it is active. A feature that is not in this document is not.</returns>
    public bool IsActive(FeatureId id)
    {
        int index = IndexOf(id);

        return index >= 0 && index < ActiveFeatureCount;
    }

    /// <summary>Finds a feature by its id.</summary>
    /// <param name="id">Which feature.</param>
    /// <returns>The feature, or <see langword="null"/> if this document has no such feature.</returns>
    public Feature? FindFeature(FeatureId id)
        => _featuresById.TryGetValue(id, out Feature? feature) ? feature : null;

    /// <summary>Finds a body by its id.</summary>
    /// <param name="id">Which body.</param>
    /// <returns>The body, or <see langword="null"/> if this document has no such body.</returns>
    public Body? FindBody(BodyId id) => _bodiesById.TryGetValue(id, out Body? body) ? body : null;

    /// <summary>Finds a parameter by name.</summary>
    /// <param name="name">What it is called, compared without regard to case.</param>
    /// <returns>The parameter, or <see langword="null"/> if there is none by that name.</returns>
    public Parameter? FindParameter(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _parametersByName.TryGetValue(name, out Parameter? parameter) ? parameter : null;
    }

    /// <summary>Finds reference geometry by its owner and name.</summary>
    /// <param name="owner">
    /// Which feature created it, or <see cref="FeatureId.None"/> for the standard datums.
    /// </param>
    /// <param name="name">What it is called.</param>
    /// <returns>The geometry, or <see langword="null"/> if this document has no such reference.</returns>
    /// <remarks>
    /// A linear scan rather than a dictionary: a document's reference geometry numbers in the tens,
    /// not the thousands, and paying for an index rebuilt on every edit would cost more than it saves.
    /// </remarks>
    public ReferenceGeometry? FindReference(FeatureId owner, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        foreach (ReferenceGeometry reference in References)
        {
            if (reference.Owner == owner
                && string.Equals(reference.Name, name, StringComparison.Ordinal))
            {
                return reference;
            }
        }

        return null;
    }

    /// <summary>Gets the bodies a given feature produced.</summary>
    /// <param name="owner">Which feature.</param>
    /// <returns>Its bodies, in no particular order.</returns>
    public ImmutableArray<Body> BodiesOf(FeatureId owner)
    {
        ImmutableArray<Body>.Builder found = ImmutableArray.CreateBuilder<Body>();

        foreach (Body body in _bodiesById.Values)
        {
            if (body.Owner == owner)
            {
                found.Add(body);
            }
        }

        return found.ToImmutable();
    }

    /// <summary>Adds a feature to the end of the tree.</summary>
    /// <param name="feature">The feature.</param>
    /// <returns>The new document.</returns>
    /// <exception cref="ArgumentException">A feature with that id is already present.</exception>
    internal Document WithFeatureAdded(Feature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);

        if (_featuresById.ContainsKey(feature.Id))
        {
            throw new ArgumentException(
                $"This document already contains {feature.Id}. Adding a second feature under one "
                + "id would make every lookup ambiguous and every reference to it undecidable.",
                nameof(feature));
        }

        return With(
            features: Features.Add(feature),
            featuresById: _featuresById.Add(feature.Id, feature));
    }

    /// <summary>Replaces a feature, keeping its position in the tree.</summary>
    /// <param name="feature">The replacement, carrying the same id.</param>
    /// <returns>The new document.</returns>
    /// <exception cref="ArgumentException">No feature with that id is present.</exception>
    internal Document WithFeatureReplaced(Feature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);

        int index = IndexOf(feature.Id);

        if (index < 0)
        {
            throw new ArgumentException(
                $"This document contains no {feature.Id}, so there is nothing to replace. Adding a "
                + "feature and replacing one are different intentions and are kept separate so a "
                + "typo in an id cannot silently become an insertion.",
                nameof(feature));
        }

        return With(
            features: Features.SetItem(index, feature),
            featuresById: _featuresById.SetItem(feature.Id, feature));
    }

    /// <summary>Removes a feature, and every body it produced.</summary>
    /// <param name="id">Which feature.</param>
    /// <returns>The new document.</returns>
    /// <exception cref="ArgumentException">No feature with that id is present.</exception>
    /// <remarks>
    /// Removing the bodies with it is not tidying up. A body names its producer, so a body left
    /// behind would point at a feature that is gone — and it could never be rebuilt, because the
    /// thing that knew how to build it no longer exists.
    /// </remarks>
    internal Document WithFeatureRemoved(FeatureId id)
    {
        int index = IndexOf(id);

        if (index < 0)
        {
            throw new ArgumentException(
                $"This document contains no {id}, so there is nothing to remove.", nameof(id));
        }

        ImmutableDictionary<BodyId, Body> bodies = _bodiesById;

        foreach (Body body in BodiesOf(id))
        {
            bodies = bodies.Remove(body.Id);
        }

        // The bar is a position, so removing a feature above it shifts every feature below up by
        // one -- and a bar left where it was would quietly roll back one feature that was active a
        // moment ago. Moving the bar with the removal keeps the same features active, which is what
        // the user means by deleting something that is already visible.
        int? rollback = RollbackPosition is { } bar && index < bar ? bar - 1 : RollbackPosition;

        return With(
            features: Features.RemoveAt(index),
            featuresById: _featuresById.Remove(id),
            bodiesById: bodies,
            rollbackPosition: rollback);
    }

    /// <summary>Moves a feature to a different position in the tree.</summary>
    /// <param name="id">Which feature.</param>
    /// <param name="index">Where it should end up.</param>
    /// <returns>The new document.</returns>
    /// <remarks>
    /// Only the order changes. Whether the move is legal — whether it puts a feature before
    /// something it consumes — is a question about the dependency graph, which P3-T03 builds and
    /// P3-T02's commit is where it gets asked. This method does not know the answer and does not
    /// pretend to.
    /// </remarks>
    internal Document WithFeatureMoved(FeatureId id, int index)
    {
        int from = IndexOf(id);

        if (from < 0)
        {
            throw new ArgumentException(
                $"This document contains no {id}, so there is nothing to move.", nameof(id));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Features.Length);

        Feature feature = Features[from];

        return With(features: Features.RemoveAt(from).Insert(index, feature));
    }

    /// <summary>Adds or replaces a body.</summary>
    /// <param name="body">The body.</param>
    /// <returns>The new document.</returns>
    /// <exception cref="ArgumentException">No feature owns it.</exception>
    internal Document WithBody(Body body)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (!_featuresById.ContainsKey(body.Owner))
        {
            throw new ArgumentException(
                $"No feature in this document has id {body.Owner}, so nothing could have produced "
                + $"{body.Id}. Every body is the result of a feature, and one whose producer is "
                + "absent can never be rebuilt.",
                nameof(body));
        }

        return With(bodiesById: _bodiesById.SetItem(body.Id, body));
    }

    /// <summary>Removes a body.</summary>
    /// <param name="id">Which body.</param>
    /// <returns>The new document.</returns>
    internal Document WithBodyRemoved(BodyId id) => With(bodiesById: _bodiesById.Remove(id));

    /// <summary>Adds or replaces a parameter.</summary>
    /// <param name="parameter">The parameter.</param>
    /// <returns>The new document.</returns>
    internal Document WithParameter(Parameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameter.Name);

        return With(parametersByName: _parametersByName.SetItem(parameter.Name, parameter));
    }

    /// <summary>Removes a parameter.</summary>
    /// <param name="name">What it is called.</param>
    /// <returns>The new document.</returns>
    internal Document WithParameterRemoved(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return With(parametersByName: _parametersByName.Remove(name));
    }

    /// <summary>Adds a piece of reference geometry.</summary>
    /// <param name="reference">The geometry.</param>
    /// <returns>The new document.</returns>
    internal Document WithReference(ReferenceGeometry reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        // Add or replace, keyed by owner and name -- the pair that identifies a datum, and
        // what a sketch plane reference resolves against (P4-T10). A plain append would give a
        // feature two planes called the same thing the second time it was rebuilt, and the
        // resolution that has to choose between them has no way to prefer either.
        int existing = -1;

        for (int i = 0; i < References.Length; ++i)
        {
            if (References[i].Owner == reference.Owner && References[i].Name == reference.Name)
            {
                existing = i;
                break;
            }
        }

        return With(references: existing < 0
            ? References.Add(reference)
            : References.SetItem(existing, reference));
    }

    /// <summary>Returns this document depending on one more thing, or on it differently.</summary>
    /// <param name="reference">What it depends on.</param>
    /// <returns>The new document.</returns>
    /// <remarks>
    /// Add-or-replace on the target, the same shape <see cref="WithReference"/> settled on for
    /// reference geometry (P3-T04) and for the same reason: a document depends on another document
    /// once, however many things inside it reach across, so a second entry for one target would be
    /// two answers to "is this up to date".
    /// </remarks>
    internal Document WithExternalReference(ExternalReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        int existing = IndexOfExternal(reference.Target);

        return With(externalReferences: existing < 0
            ? ExternalReferences.Add(reference)
            : ExternalReferences.SetItem(existing, reference));
    }

    /// <summary>Returns this document depending on exactly these things.</summary>
    /// <param name="references">What it depends on.</param>
    /// <returns>The new document.</returns>
    /// <remarks>
    /// For a reader, which knows the whole list before it builds anything — the same reason
    /// <see cref="FromParts"/> exists beside the one-at-a-time editing API. Replaces rather than
    /// merges: what a file says it depends on <em>is</em> what it depends on, and folding a file's
    /// list into an existing one would let a stale entry survive a reload.
    /// </remarks>
    internal Document WithExternalReferences(ImmutableArray<ExternalReference> references)
        => references.IsDefaultOrEmpty && ExternalReferences.IsEmpty
            ? this
            : With(externalReferences: references.IsDefault ? [] : references);

    /// <summary>Returns this document no longer recording a dependency.</summary>
    /// <param name="target">Which document.</param>
    /// <returns>The new document, or this one if it did not depend on that.</returns>
    /// <remarks>
    /// Forgetting the dependency entirely, which is not the same as
    /// <see cref="ExternalReferenceState.Broken"/> — a broken reference is a severed link the user
    /// can still see, and this is for a dependency that genuinely no longer exists because whatever
    /// reached across has been deleted.
    /// </remarks>
    internal Document WithoutExternalReference(string target)
    {
        int existing = IndexOfExternal(target);

        return existing < 0 ? this : With(externalReferences: ExternalReferences.RemoveAt(existing));
    }

    /// <summary>Finds what this document records about a dependency.</summary>
    /// <param name="target">Which document.</param>
    /// <returns>The reference, or <see langword="null"/> if it does not depend on that.</returns>
    public ExternalReference? FindExternalReference(string target)
    {
        int existing = IndexOfExternal(target);

        return existing < 0 ? null : ExternalReferences[existing];
    }

    private int IndexOfExternal(string target)
    {
        ArgumentNullException.ThrowIfNull(target);

        for (int i = 0; i < ExternalReferences.Length; ++i)
        {
            if (string.Equals(ExternalReferences[i].Target, target, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Returns this document with a different assembly structure.</summary>
    /// <param name="assembly">The structure.</param>
    /// <returns>The new document.</returns>
    /// <exception cref="InvalidOperationException">This document is not an assembly.</exception>
    /// <remarks>
    /// The whole structure rather than an operation per edit. <see cref="Assembly"/> is already
    /// immutable and already enforces its own invariants — a placement of a component it does not
    /// have is refused there — so a set of <c>WithOccurrence…</c> methods here would be a second
    /// copy of rules that already have one place to live, and the second copy is the one that
    /// drifts.
    /// </remarks>
    internal Document WithAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        if (Kind != DocumentKind.Assembly)
        {
            throw new InvalidOperationException(
                $"A {Kind} document holds no components. Create the document as an assembly if it "
                + "is meant to place them.");
        }

        return With(assembly: assembly);
    }

    /// <summary>Replaces the document's properties.</summary>
    /// <param name="metadata">The new properties.</param>
    /// <returns>The new document.</returns>
    internal Document WithMetadata(DocumentMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        return With(metadata: metadata);
    }

    /// <summary>Assembles a document from parts that have already been collected.</summary>
    /// <param name="features">The features, in tree order.</param>
    /// <param name="parameters">The parameters.</param>
    /// <param name="bodies">The bodies.</param>
    /// <param name="references">The reference geometry.</param>
    /// <param name="metadata">The properties.</param>
    /// <param name="rollbackPosition">Where the rollback bar sits.</param>
    /// <param name="unknownFields">Fields of the file this build could not read.</param>
    /// <param name="kind">What the document is for.</param>
    /// <param name="assembly">The components it places, for an assembly.</param>
    /// <param name="externalReferences">The documents it depends on.</param>
    /// <returns>The document.</returns>
    /// <remarks>
    /// For a reader, which knows everything before it builds anything. Adding features one at a
    /// time through <see cref="WithFeatureAdded"/> copies the whole array and allocates a document
    /// per feature, so opening a part with ten thousand of them does fifty million element copies
    /// on the file-open path -- quadratic in the size of the model, for no reason except that the
    /// editing API is the wrong shape for bulk loading.
    /// </remarks>
    internal static Document FromParts(
        ImmutableArray<Feature> features,
        IEnumerable<Parameter> parameters,
        IEnumerable<Body> bodies,
        ImmutableArray<ReferenceGeometry> references,
        DocumentMetadata metadata,
        int? rollbackPosition,
        ImmutableArray<UnknownField> unknownFields = default,
        DocumentKind kind = DocumentKind.Part,
        Assembly? assembly = null,
        ImmutableArray<ExternalReference> externalReferences = default)
    {
        ImmutableDictionary<FeatureId, Feature>.Builder featuresById =
            ImmutableDictionary.CreateBuilder<FeatureId, Feature>();

        foreach (Feature feature in features)
        {
            if (featuresById.ContainsKey(feature.Id))
            {
                throw new ArgumentException(
                    $"Two features share the id {feature.Id}, so every reference to it would be "
                    + "ambiguous.",
                    nameof(features));
            }

            featuresById.Add(feature.Id, feature);
        }

        ImmutableDictionary<BodyId, Body>.Builder bodiesById =
            ImmutableDictionary.CreateBuilder<BodyId, Body>();

        foreach (Body body in bodies)
        {
            if (!featuresById.ContainsKey(body.Owner))
            {
                throw new ArgumentException(
                    $"No feature has id {body.Owner}, so nothing could have produced {body.Id}.",
                    nameof(bodies));
            }

            bodiesById[body.Id] = body;
        }

        ImmutableDictionary<string, Parameter>.Builder byName =
            ImmutableDictionary.CreateBuilder<string, Parameter>(Parameter.NameComparer);

        foreach (Parameter parameter in parameters)
        {
            byName[parameter.Name] = parameter;
        }

        if (rollbackPosition is { } bar && (bar < 0 || bar > features.Length))
        {
            throw new ArgumentOutOfRangeException(
                nameof(rollbackPosition),
                bar,
                $"The rollback bar is at {bar} and there are {features.Length} features.");
        }

        return new Document(
            features,
            featuresById.ToImmutable(),
            bodiesById.ToImmutable(),
            byName.ToImmutable(),
            references,
            metadata,
            rollbackPosition,
            RebuildReport.Empty,
            version: 0,
            kind,
            assembly ?? Assembly.Empty,
            externalReferences.IsDefault ? [] : externalReferences,
            unknownFields);
    }

    /// <summary>Drops the reference geometry, so a reader can supply the file's own.</summary>
    /// <returns>The new document.</returns>
    /// <remarks>
    /// An empty document starts with the standard datums, which is right for a new part and wrong
    /// for one being read back: the file carries its own, and adding them to the ones already
    /// there would give the document two of each after every open.
    /// </remarks>
    internal Document WithoutReferences() => With(references: []);

    /// <summary>Takes away one piece of reference geometry.</summary>
    /// <param name="owner">Who produced it.</param>
    /// <param name="name">What it is called.</param>
    /// <returns>The new document.</returns>
    /// <remarks>
    /// The pair is the identity, so this is the exact counterpart of
    /// <see cref="WithBodyRemoved"/>: a rebuild publishes what a feature produced and takes away
    /// what it no longer does, and a feature that made three datums and now makes one has to lose
    /// the other two by name rather than by owner.
    /// </remarks>
    internal Document WithoutReference(FeatureId owner, string name)
        => With(references: [.. References.Where(r => r.Owner != owner || r.Name != name)]);

    /// <summary>Takes away the reference geometry one feature produced.</summary>
    /// <param name="owner">Whose geometry to remove.</param>
    /// <returns>The new document.</returns>
    /// <remarks>
    /// <para>
    /// The counterpart of <see cref="WithBodyRemoved"/>, and needed for the same reason: a rebuild
    /// replaces what a feature produced rather than adding to it. Bodies are held by id and so
    /// replace themselves, but reference geometry is a plain collection that only ever grew — a
    /// datum plane rebuilt twice would exist twice, and a suppressed one would stay in the document
    /// and remain sketchable.
    /// </para>
    /// <para>
    /// <see cref="FeatureId.None"/> owns the standard datums, so it is ignored rather than obeyed.
    /// Today's only caller cannot reach it — the rebuild removes an owner only when its geometry
    /// has gone, and the standard datums never go — so no test can distinguish the branch being
    /// there. It stays for the next caller, because the cost of being wrong is a document whose
    /// origin planes silently vanish and every sketch loses its plane.
    /// </para>
    /// </remarks>
    internal Document WithoutReferencesOf(FeatureId owner)
        => owner == FeatureId.None
            ? this
            : With(references: [.. References.Where(r => r.Owner != owner)]);

    /// <summary>Records how every feature stood after a rebuild.</summary>
    /// <param name="report">The report.</param>
    /// <returns>The new document.</returns>
    internal Document WithReport(RebuildReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return With(report: report);
    }

    /// <summary>Moves the rollback bar.</summary>
    /// <param name="position">
    /// How many features from the top stay active, or null to roll forward to the end.
    /// </param>
    /// <returns>The new document.</returns>
    internal Document WithRollbackPosition(int? position)
    {
        if (position is { } value)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(position));

            ArgumentOutOfRangeException.ThrowIfGreaterThan(
                value, Features.Length, nameof(position));
        }

        return With(rollbackPosition: position, clearRollback: position is null);
    }

    private int IndexOf(FeatureId id)
    {
        for (int i = 0; i < Features.Length; ++i)
        {
            if (Features[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The same document with some parts replaced and the version advanced.</summary>
    /// <remarks>
    /// Every mutator goes through here, so the version cannot be advanced by one of them and
    /// forgotten by another — which would leave two different documents claiming to be the same
    /// one, and a geometry cache confidently serving the wrong result for it.
    /// </remarks>
    private Document With(
        ImmutableArray<Feature>? features = null,
        ImmutableDictionary<FeatureId, Feature>? featuresById = null,
        ImmutableDictionary<BodyId, Body>? bodiesById = null,
        ImmutableDictionary<string, Parameter>? parametersByName = null,
        ImmutableArray<ReferenceGeometry>? references = null,
        DocumentMetadata? metadata = null,
        int? rollbackPosition = null,
        bool clearRollback = false,
        RebuildReport? report = null,
        Assembly? assembly = null,
        ImmutableArray<ExternalReference>? externalReferences = null)
        => new(
            features ?? Features,
            featuresById ?? _featuresById,
            bodiesById ?? _bodiesById,
            parametersByName ?? _parametersByName,
            references ?? References,
            metadata ?? Metadata,
            clearRollback ? null : rollbackPosition ?? RollbackPosition,
            report ?? Report,
            Version + 1,
            Kind,
            assembly ?? Assembly,
            externalReferences ?? ExternalReferences,

            // Never dropped by an edit. A field nothing here understands is not made irrelevant by
            // the user moving a rollback bar, and the one thing that must not happen is for it to
            // survive being opened and then quietly vanish on the first change.
            UnknownFields);

    /// <inheritdoc />
    public override string ToString()
        => $"document v{Version}: {Features.Length} features, {_bodiesById.Count} bodies, "
            + $"{_parametersByName.Count} parameters"
            + (IsRolledBack ? $", rolled back to {ActiveFeatureCount}" : string.Empty);
}
