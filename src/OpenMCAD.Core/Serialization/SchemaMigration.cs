using System.Collections.Immutable;

namespace OpenMCAD.Core.Serialization;

/// <summary>
/// One step of the document schema's history: everything needed to turn a document written at one
/// version into the same document at the next.
/// </summary>
/// <remarks>
/// <para>
/// One step, never several. A migration that jumped two versions could not be composed with the
/// steps around it, and the first time a version was inserted between them every such shortcut
/// would have to be rewritten. Chains of single steps cost a little more to run and can be
/// reasoned about one at a time.
/// </para>
/// <para>
/// A migration must never be edited once it has shipped. It is not a description of how the format
/// changed; it is the only remaining record of a format nobody can write any more, and changing it
/// changes the meaning of files already on disk.
/// </para>
/// </remarks>
internal interface ISchemaMigration
{
    /// <summary>Gets the schema version this reads.</summary>
    int From { get; }

    /// <summary>Gets what changed, for the message a failed open shows.</summary>
    string Summary { get; }

    /// <summary>Rewrites a document as the next version.</summary>
    /// <param name="document">The document at version <see cref="From"/>.</param>
    /// <returns>The same document at version <see cref="From"/> + 1.</returns>
    MessagePackValue Apply(MessagePackValue document);
}

/// <summary>
/// Runs the chain of migrations between a document's schema version and this build's.
/// </summary>
/// <remarks>
/// <para>
/// P3-T19. §5.8 requires that a file this project has ever written stays openable, and the way to
/// keep that promise without the reader growing a branch per historical version is to move the file
/// forward before the reader sees it. <see cref="DocumentCodec"/> therefore only ever parses the
/// current schema, and everything about older ones lives here.
/// </para>
/// <para>
/// The registry is empty while <see cref="DocumentCodec.SchemaVersion"/> is 1, because there is no
/// older version to come from. That is not an untested framework: the chain logic is exercised by
/// migrations declared in the tests, and the fixture corpus proves that what this build writes today
/// is still openable by every build after it.
/// </para>
/// </remarks>
internal static class SchemaMigrator
{
    /// <summary>The migrations this build knows, in the order they must run.</summary>
    /// <remarks>
    /// Add one here the same commit that raises <see cref="DocumentCodec.SchemaVersion"/>, and add
    /// a fixture written at the version being left behind. Both are enforced by tests rather than
    /// by memory.
    /// </remarks>
    private static readonly ImmutableArray<ISchemaMigration> Known = [];

    /// <summary>Gets the migrations this build knows.</summary>
    public static ImmutableArray<ISchemaMigration> Migrations => Known;

    /// <summary>Brings a document up to the current schema.</summary>
    /// <param name="document">The document as it was read.</param>
    /// <param name="from">The schema version it was written at.</param>
    /// <returns>The document at <see cref="DocumentCodec.SchemaVersion"/>.</returns>
    /// <exception cref="DocumentFormatException">
    /// The version is newer than this build, or older than anything it can still reach.
    /// </exception>
    public static MessagePackValue Migrate(MessagePackValue document, int from)
        => Migrate(document, from, DocumentCodec.SchemaVersion, Known);

    /// <summary>Brings a document up to a given schema, using a given set of migrations.</summary>
    /// <param name="document">The document as it was read.</param>
    /// <param name="from">The schema version it was written at.</param>
    /// <param name="to">The schema version wanted.</param>
    /// <param name="migrations">The steps available.</param>
    /// <returns>The document at <paramref name="to"/>.</returns>
    /// <exception cref="DocumentFormatException">
    /// The version is newer than <paramref name="to"/>, or a step in between is missing.
    /// </exception>
    public static MessagePackValue Migrate(
        MessagePackValue document,
        int from,
        int to,
        ImmutableArray<ISchemaMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (from > to)
        {
            throw new DocumentFormatException(
                $"This document was written by a newer version of OpenMCAD (schema {from}; this "
                + $"build reads {to}). Opening it would lose whatever the newer version added.");
        }

        MessagePackValue current = document;

        for (int version = from; version < to; ++version)
        {
            ISchemaMigration? step = Step(migrations, version);

            if (step is null)
            {
                throw new DocumentFormatException(
                    $"This document was written at schema {from} and this build reads {to}, but "
                    + $"nothing here knows how to get from {version} to {version + 1}. The file is "
                    + "not damaged; this build is missing a migration.");
            }

            current = step.Apply(current)
                ?? throw new DocumentFormatException(
                    $"The migration from schema {version} to {version + 1} ({step.Summary}) "
                    + "produced nothing.");

            current = Stamped(current, version + 1);
        }

        return current;
    }

    /// <summary>Records what version a document has reached.</summary>
    /// <param name="document">The document.</param>
    /// <param name="version">The version it is now at.</param>
    /// <returns>The document, saying so.</returns>
    /// <remarks>
    /// Done here rather than left to each migration. A migration that forgot would hand the reader
    /// a document still claiming the version it came from, and the failure would surface as a
    /// confusing complaint about the file rather than about the step that wrote it.
    /// </remarks>
    private static MessagePackValue Stamped(MessagePackValue document, int version)
        => document is MessagePackMap map
            ? map.With("schema", new MessagePackInteger(version))
            : document;

    /// <summary>Finds the step out of a version, and refuses to guess if there are two.</summary>
    /// <param name="migrations">The steps available.</param>
    /// <param name="from">The version to leave.</param>
    /// <returns>The step, or null if there is none.</returns>
    /// <exception cref="DocumentFormatException">Two steps claim the same version.</exception>
    private static ISchemaMigration? Step(ImmutableArray<ISchemaMigration> migrations, int from)
    {
        ISchemaMigration? found = null;

        foreach (ISchemaMigration migration in migrations)
        {
            if (migration.From != from)
            {
                continue;
            }

            if (found is not null)
            {
                // Refused rather than resolved by order, because the two would produce different
                // documents from the same file and whichever ran would be an accident of
                // declaration order.
                throw new DocumentFormatException(
                    $"Two migrations claim to read schema {from} ({found.Summary}, and "
                    + $"{migration.Summary}). Only one can be right.");
            }

            found = migration;
        }

        return found;
    }
}
