using System.Collections.Immutable;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;
using OpenMCAD.Kernel;
using OpenMCAD.Math;

namespace OpenMCAD.Core.Serialization;

/// <summary>
/// Writes a document graph as MessagePack and reads it back.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is not written, and why.</b> A <see cref="Body"/> keeps its identity, its owner and its
/// kind, but not its <see cref="KernelShape"/> — that is a handle into a running kernel's table and
/// means nothing in the next process. The rebuild report is not written either: it describes what
/// the last rebuild found, and §5.8 is explicit that anything regenerable is never the source of
/// truth. Both come back when the document is rebuilt, and a file that claimed otherwise would be
/// asserting something it cannot know.
/// </para>
/// <para>
/// <b>Everything is written in a fixed order.</b> Parameters and bodies live in dictionaries, which
/// have none to inherit, so they are sorted — by name and by id respectively. Without that, saving
/// the same document twice could produce different bytes, and Phase 3's first exit criterion is
/// that it does not.
/// </para>
/// <para>
/// <b>Unknown fields are skipped rather than kept, for now.</b> Preserving them is P3-T20, and the
/// reader is already built for it: <see cref="MessagePackReader.ReadRaw"/> takes a value's bytes
/// without interpreting them. Until that task, a document written by a newer build and saved by
/// this one loses whatever this one did not recognise.
/// </para>
/// </remarks>
public static class DocumentCodec
{
    /// <summary>The schema version this build writes.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Writes a document.</summary>
    /// <param name="document">The document.</param>
    /// <returns>Its bytes.</returns>
    public static byte[] Write(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);

        MessagePackWriter writer = new(4096);

        ILookup<string, UnknownField> unknown =
            document.UnknownFields.ToLookup(f => f.Owner, StringComparer.Ordinal);

        writer.WriteMapHeader(7 + Extra(unknown, UnknownField.Root));

        writer.Write("schema");
        writer.Write(SchemaVersion);

        writer.Write("features");
        writer.WriteArrayHeader(document.Features.Length);

        foreach (Feature feature in document.Features)
        {
            WriteFeature(writer, feature, unknown);
        }

        writer.Write("parameters");
        WriteParameters(
            writer,
            [.. document.Parameters.OrderBy(p => p.Name, StringComparer.Ordinal)],
            unknown,
            UnknownField.Root);

        writer.Write("bodies");

        ImmutableArray<Body> bodies =
            [.. document.Bodies.OrderBy(b => b.Id.ToStorageString(), StringComparer.Ordinal)];

        writer.WriteArrayHeader(bodies.Length);

        foreach (Body body in bodies)
        {
            WriteBody(writer, body, unknown);
        }

        writer.Write("references");
        writer.WriteArrayHeader(document.References.Length);

        foreach (ReferenceGeometry reference in document.References)
        {
            WriteReferenceGeometry(writer, reference, unknown);
        }

        writer.Write("metadata");
        WriteMetadata(writer, document.Metadata, unknown);

        writer.Write("rollback");

        if (document.RollbackPosition is { } rollback)
        {
            writer.Write(rollback);
        }
        else
        {
            writer.WriteNil();
        }

        Preserved(writer, unknown, UnknownField.Root);

        return writer.ToArray();
    }

    /// <summary>How many fields an owner carries that this build cannot read.</summary>
    /// <param name="unknown">Everything being preserved, by owner.</param>
    /// <param name="owner">Whose fields to count.</param>
    /// <returns>The count, to add to a map header.</returns>
    private static int Extra(ILookup<string, UnknownField> unknown, string owner)
        => unknown[owner].Count();

    /// <summary>Writes back the fields of an owner that this build cannot read.</summary>
    /// <param name="writer">Where to write.</param>
    /// <param name="unknown">Everything being preserved, by owner.</param>
    /// <param name="owner">Whose fields to write.</param>
    /// <remarks>
    /// <para>
    /// After the known fields, in the order they were read. Their original position among the known
    /// ones is not kept: doing so would mean recording an index that stops meaning anything the
    /// moment the schema gains or loses a field of its own, and a map has no defined order anyway.
    /// What is kept is the content, exactly, and the order the unknown fields had relative to each
    /// other -- which is enough for the file to survive a round trip through a build that cannot
    /// read it, and enough to be deterministic.
    /// </para>
    /// <para>
    /// The bytes go out verbatim. Re-encoding a value whose meaning is by definition unavailable is
    /// how a preserving reader corrupts what it set out to preserve.
    /// </para>
    /// </remarks>
    private static void Preserved(
        MessagePackWriter writer, ILookup<string, UnknownField> unknown, string owner)
    {
        foreach (UnknownField field in unknown[owner])
        {
            writer.Write(field.Field);
            writer.WriteRaw(field.Value.AsSpan());
        }
    }

    /// <summary>Reads a document back.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The document.</returns>
    /// <exception cref="DocumentFormatException">The bytes are not a document this build can read.</exception>
    /// <remarks>
    /// <para>
    /// Every field is collected before anything is assembled, and that is not merely tidy: a
    /// MessagePack map has no defined order, so a legal file may put its bodies before its
    /// features or its rollback bar before either. Applying each field as it arrived would make a
    /// body whose owner has not been read yet, or a rollback bar past the end of a tree that is
    /// still empty, into a crash on a file that is perfectly valid.
    /// </para>
    /// <para>
    /// Everything that can go wrong with a damaged file comes out as
    /// <see cref="DocumentFormatException"/>. A malformed identifier throws
    /// <see cref="FormatException"/> and a duplicate one throws
    /// <see cref="ArgumentException"/>, and a caller told to expect one exception type should not
    /// have to catch three because of where in this method the bytes went wrong.
    /// </para>
    /// </remarks>
    public static Document Read(ReadOnlySpan<byte> data)
    {
        try
        {
            int version = VersionOf(data);

            if (version == SchemaVersion)
            {
                return ReadDocument(data);
            }

            // Only an old file pays for this. Bringing a document forward means parsing it into a
            // tree, rewriting it and encoding it again, which is three passes where opening a
            // current file takes one -- worth it once for a file written years ago, and not worth
            // paying on every open of a file that needs nothing done to it.
            return ReadDocument(
                SchemaMigrator.Migrate(MessagePackValue.Read(data), version).ToBytes());
        }
        catch (Exception exception) when (exception is FormatException
            or ArgumentException
            or OverflowException
            or IndexOutOfRangeException)
        {
            throw new DocumentFormatException(
                $"This document is damaged or was not written by OpenMCAD: {exception.Message}",
                exception);
        }
    }

    /// <summary>Reads only the schema version, without parsing the rest.</summary>
    /// <param name="data">The encoded document.</param>
    /// <returns>What version the document says it is.</returns>
    /// <remarks>
    /// A file with no schema field at all is taken to be current. That is a guess either way, and
    /// it is the guess that changes nothing: every file this project writes states its version, so
    /// one that does not was made by hand or by something else, and running the migration chain
    /// over it would rewrite a document that never asked to be rewritten.
    /// </remarks>
    private static int VersionOf(ReadOnlySpan<byte> data)
    {
        MessagePackReader reader = new(data);

        int fields = reader.ReadMapHeader();

        for (int i = 0; i < fields; ++i)
        {
            if (reader.ReadString() == "schema")
            {
                return reader.ReadInt32();
            }

            reader.Skip();
        }

        return SchemaVersion;
    }

    private static Document ReadDocument(ReadOnlySpan<byte> data)
    {
        MessagePackReader reader = new(data);

        int fields = reader.ReadMapHeader();

        ImmutableArray<Feature> features = [];
        ImmutableArray<Parameter> parameters = [];
        ImmutableArray<Body> bodies = [];
        ImmutableArray<ReferenceGeometry>? references = null;
        DocumentMetadata metadata = DocumentMetadata.Empty;
        int? rollback = null;
        List<UnknownField> unknown = [];

        for (int i = 0; i < fields; ++i)
        {
            string key = reader.ReadString();

            switch (key)
            {
                case "schema":
                    int version = reader.ReadInt32();

                    if (version > SchemaVersion)
                    {
                        throw new DocumentFormatException(
                            $"This document was written by a newer version of OpenMCAD (schema "
                            + $"{version}; this build reads {SchemaVersion}). Opening it would "
                            + "lose whatever the newer version added.");
                    }

                    break;

                case "features":
                    int count = reader.ReadArrayHeader();
                    ImmutableArray<Feature>.Builder read =
                        ImmutableArray.CreateBuilder<Feature>(count);

                    for (int f = 0; f < count; ++f)
                    {
                        read.Add(ReadFeature(ref reader, unknown));
                    }

                    features = read.ToImmutable();
                    break;

                case "parameters":
                    parameters = ReadParameters(ref reader, unknown, UnknownField.Root);
                    break;

                case "bodies":
                    int bodyCount = reader.ReadArrayHeader();
                    ImmutableArray<Body>.Builder found =
                        ImmutableArray.CreateBuilder<Body>(bodyCount);

                    for (int b = 0; b < bodyCount; ++b)
                    {
                        found.Add(ReadBody(ref reader, unknown));
                    }

                    bodies = found.ToImmutable();
                    break;

                case "references":
                    int referenceCount = reader.ReadArrayHeader();
                    ImmutableArray<ReferenceGeometry>.Builder geometry =
                        ImmutableArray.CreateBuilder<ReferenceGeometry>(referenceCount);

                    for (int r = 0; r < referenceCount; ++r)
                    {
                        geometry.Add(ReadReferenceGeometry(ref reader, unknown));
                    }

                    references = geometry.ToImmutable();
                    break;

                case "metadata":
                    metadata = ReadMetadata(ref reader, unknown);
                    break;

                case "rollback":
                    rollback = reader.TryReadNil() ? null : reader.ReadInt32();
                    break;

                default:
                    // Kept, not skipped (P3-T20). A field this build has no name for was put there
                    // by something that did, and a reader that dropped it would make opening a
                    // colleague's file and saving out of habit into silent data loss.
                    unknown.Add(Keep(ref reader, UnknownField.Root, key));
                    break;
            }
        }

        // A file with no references field at all keeps the standard datums, rather than opening
        // with no origin and no planes. Only a file that says what its reference geometry is --
        // including saying that it has none -- replaces them.
        return Document.FromParts(
            features,
            parameters,
            bodies,
            references ?? [.. ReferenceGeometry.StandardDatums()],
            metadata,
            rollback,
            [.. unknown]);
    }

    /// <summary>Takes the value of a field this build has no name for, exactly as it is.</summary>
    /// <param name="reader">Where to read from, positioned at the value.</param>
    /// <param name="owner">What the field belongs to.</param>
    /// <param name="field">Its key.</param>
    /// <returns>The field, kept.</returns>
    private static UnknownField Keep(ref MessagePackReader reader, string owner, string field)
        => new(owner, field, [.. reader.ReadRaw()]);

    /// <summary>Files away fields collected before their owner had a name.</summary>
    /// <param name="into">Where the document's kept fields are being gathered.</param>
    /// <param name="pending">What was collected while reading, with owners relative to this one.</param>
    /// <param name="owner">What they all belong to, now that it is known.</param>
    /// <remarks>
    /// A map has no defined order, so a parameter's unknown field can be read before the parameter's
    /// name and a feature's before its id. Collecting relative and prefixing afterwards is what
    /// makes preservation independent of the order the writer happened to choose.
    /// </remarks>
    private static void Attribute(
        List<UnknownField> into, List<UnknownField> pending, string owner)
    {
        foreach (UnknownField field in pending)
        {
            into.Add(field with { Owner = UnknownField.Nest(owner, field.Owner) });
        }
    }

    private static void WriteFeature(
        MessagePackWriter writer, Feature feature, ILookup<string, UnknownField> unknown)
    {
        string owner = UnknownField.Feature(feature.Id.ToStorageString());

        writer.WriteMapHeader(7 + Extra(unknown, owner));

        writer.Write("id");
        writer.Write(feature.Id.ToStorageString());

        writer.Write("name");
        writer.Write(feature.Name);

        writer.Write("type");
        writer.Write(feature.FeatureType);

        writer.Write("inputs");
        writer.WriteArrayHeader(feature.Inputs.Length);

        foreach (FeatureId input in feature.Inputs)
        {
            writer.Write(input.ToStorageString());
        }

        writer.Write("parameters");
        WriteParameters(writer, feature.Parameters, unknown, owner);

        writer.Write("refs");
        writer.WriteArrayHeader(feature.EntityReferences.Length);

        foreach (EntityReference reference in feature.EntityReferences)
        {
            writer.WriteMapHeader(2);

            writer.Write("name");
            writer.Write(PersistentNameFormat.Write(reference.Name));

            writer.Write("mult");
            writer.Write((int)reference.Multiplicity);
        }

        writer.Write("suppressed");
        writer.Write(feature.IsSuppressed);

        Preserved(writer, unknown, owner);
    }

    private static Feature ReadFeature(ref MessagePackReader reader, List<UnknownField> unknown)
    {
        int fields = reader.ReadMapHeader();

        List<UnknownField> pending = [];
        FeatureId id = default;
        string name = string.Empty;
        string type = string.Empty;
        ImmutableArray<FeatureId> inputs = [];
        ImmutableArray<Parameter> parameters = [];
        ImmutableArray<EntityReference> references = [];
        bool suppressed = false;

        for (int i = 0; i < fields; ++i)
        {
            string key = reader.ReadString();

            switch (key)
            {
                case "id":
                    id = FeatureId.Parse(reader.ReadString());
                    break;

                case "name":
                    name = reader.ReadString();
                    break;

                case "type":
                    type = reader.ReadString();
                    break;

                case "inputs":
                    int count = reader.ReadArrayHeader();
                    ImmutableArray<FeatureId>.Builder found =
                        ImmutableArray.CreateBuilder<FeatureId>(count);

                    for (int j = 0; j < count; ++j)
                    {
                        found.Add(FeatureId.Parse(reader.ReadString()));
                    }

                    inputs = found.ToImmutable();
                    break;

                case "parameters":
                    parameters = ReadParameters(ref reader, pending, UnknownField.Root);
                    break;

                case "refs":
                    references = ReadEntityReferences(ref reader);
                    break;

                case "suppressed":
                    suppressed = reader.ReadBoolean();
                    break;

                default:
                    pending.Add(Keep(ref reader, UnknownField.Root, key));
                    break;
            }
        }

        Attribute(unknown, pending, UnknownField.Feature(id.ToStorageString()));

        return new Feature(id, name, type, inputs, parameters, references, suppressed);
    }

    private static ImmutableArray<EntityReference> ReadEntityReferences(ref MessagePackReader reader)
    {
        int count = reader.ReadArrayHeader();

        ImmutableArray<EntityReference>.Builder found =
            ImmutableArray.CreateBuilder<EntityReference>(count);

        for (int i = 0; i < count; ++i)
        {
            int fields = reader.ReadMapHeader();

            PersistentName? name = null;
            MultiplicityPolicy multiplicity = MultiplicityPolicy.ExactlyOne;

            for (int f = 0; f < fields; ++f)
            {
                switch (reader.ReadString())
                {
                    case "name":
                        name = PersistentNameFormat.Read(reader.ReadString());
                        break;

                    case "mult":
                        multiplicity = (MultiplicityPolicy)reader.ReadInt32();
                        break;

                    default:
                        reader.Skip();
                        break;
                }
            }

            if (name is null)
            {
                throw new DocumentFormatException(
                    "A feature refers to an entity without saying which one.");
            }

            found.Add(new EntityReference(name, multiplicity));
        }

        return found.ToImmutable();
    }

    private static void WriteParameters(
        MessagePackWriter writer,
        ImmutableArray<Parameter> parameters,
        ILookup<string, UnknownField> unknown,
        string within)
    {
        writer.WriteArrayHeader(parameters.Length);

        foreach (Parameter parameter in parameters)
        {
            string owner = UnknownField.Nest(within, UnknownField.Parameter(parameter.Name));

            writer.WriteMapHeader(4 + Extra(unknown, owner));

            writer.Write("name");
            writer.Write(parameter.Name);

            writer.Write("value");
            writer.WriteArrayHeader(2);
            writer.Write(parameter.Value.Value);
            writer.Write((int)parameter.Value.Dimension);

            writer.Write("expr");
            WriteOptional(writer, parameter.Expression);

            writer.Write("desc");
            WriteOptional(writer, parameter.Description);

            Preserved(writer, unknown, owner);
        }
    }

    private static ImmutableArray<Parameter> ReadParameters(
        ref MessagePackReader reader, List<UnknownField> unknown, string within)
    {
        int count = reader.ReadArrayHeader();

        ImmutableArray<Parameter>.Builder found = ImmutableArray.CreateBuilder<Parameter>(count);

        for (int i = 0; i < count; ++i)
        {
            int fields = reader.ReadMapHeader();

            List<UnknownField> pending = [];
            string name = string.Empty;
            Quantity value = Quantity.Zero;
            string? expression = null;
            string? description = null;

            for (int f = 0; f < fields; ++f)
            {
                string key = reader.ReadString();

                switch (key)
                {
                    case "name":
                        name = reader.ReadString();
                        break;

                    case "value":
                        reader.ReadArrayHeader();
                        double magnitude = reader.ReadDouble();
                        value = new Quantity(magnitude, (Dimension)reader.ReadInt32());
                        break;

                    case "expr":
                        expression = ReadOptional(ref reader);
                        break;

                    case "desc":
                        description = ReadOptional(ref reader);
                        break;

                    default:
                        pending.Add(Keep(ref reader, UnknownField.Root, key));
                        break;
                }
            }

            Attribute(
                unknown, pending, UnknownField.Nest(within, UnknownField.Parameter(name)));

            found.Add(new Parameter(name, value, expression, description));
        }

        return found.ToImmutable();
    }

    private static void WriteBody(
        MessagePackWriter writer, Body body, ILookup<string, UnknownField> unknown)
    {
        string owner = UnknownField.Body(body.Id.ToStorageString());

        writer.WriteMapHeader(4 + Extra(unknown, owner));

        writer.Write("id");
        writer.Write(body.Id.ToStorageString());

        writer.Write("owner");
        writer.Write(body.Owner.ToStorageString());

        writer.Write("kind");
        writer.Write((int)body.Kind);

        writer.Write("name");
        WriteOptional(writer, body.Name);

        Preserved(writer, unknown, owner);
    }

    private static Body ReadBody(ref MessagePackReader reader, List<UnknownField> unknown)
    {
        int fields = reader.ReadMapHeader();

        List<UnknownField> pending = [];
        BodyId id = default;
        FeatureId owner = default;
        BodyKind kind = BodyKind.Solid;
        string? name = null;

        for (int i = 0; i < fields; ++i)
        {
            string key = reader.ReadString();

            switch (key)
            {
                case "id":
                    id = BodyId.Parse(reader.ReadString());
                    break;

                case "owner":
                    owner = FeatureId.Parse(reader.ReadString());
                    break;

                case "kind":
                    kind = (BodyKind)reader.ReadInt32();
                    break;

                case "name":
                    name = ReadOptional(ref reader);
                    break;

                default:
                    pending.Add(Keep(ref reader, UnknownField.Root, key));
                    break;
            }
        }

        Attribute(unknown, pending, UnknownField.Body(id.ToStorageString()));

        // No shape. It is a handle into a kernel that is not running any more, and the body gets
        // its geometry back when the document is rebuilt.
        return new Body(id, owner, kind, KernelShape.None, name);
    }

    private static void WriteReferenceGeometry(
        MessagePackWriter writer, ReferenceGeometry geometry, ILookup<string, UnknownField> unknown)
    {
        string owner = UnknownField.Reference(geometry.Name);

        writer.WriteMapHeader(4 + Extra(unknown, owner));

        writer.Write("kind");
        writer.Write(KindOf(geometry));

        writer.Write("owner");
        writer.Write(geometry.Owner.ToStorageString());

        writer.Write("name");
        writer.Write(geometry.Name);

        writer.Write("at");

        switch (geometry)
        {
            case ReferenceGeometry.Plane plane:
                writer.WriteArrayHeader(6);
                WriteVector(writer, plane.Origin);
                WriteVector(writer, plane.Normal);
                break;

            case ReferenceGeometry.Axis axis:
                writer.WriteArrayHeader(6);
                WriteVector(writer, axis.Origin);
                WriteVector(writer, axis.Direction);
                break;

            case ReferenceGeometry.Point point:
                writer.WriteArrayHeader(3);
                WriteVector(writer, point.Position);
                break;

            case ReferenceGeometry.CoordinateSystem frame:
                writer.WriteArrayHeader(9);
                WriteVector(writer, frame.Origin);
                WriteVector(writer, frame.XAxis);
                WriteVector(writer, frame.ZAxis);
                break;

            default:
                throw new DocumentFormatException(
                    $"There is no way to write a {geometry.GetType().Name}. A kind added without a "
                    + "case here would be dropped on save.");
        }

        Preserved(writer, unknown, owner);
    }

    private static ReferenceGeometry ReadReferenceGeometry(
        ref MessagePackReader reader, List<UnknownField> unknown)
    {
        int fields = reader.ReadMapHeader();

        List<UnknownField> pending = [];
        int kind = 0;
        FeatureId owner = default;
        string name = string.Empty;
        double[] numbers = [];

        for (int i = 0; i < fields; ++i)
        {
            string key = reader.ReadString();

            switch (key)
            {
                case "kind":
                    kind = reader.ReadInt32();
                    break;

                case "owner":
                    owner = FeatureId.Parse(reader.ReadString());
                    break;

                case "name":
                    name = reader.ReadString();
                    break;

                case "at":
                    int count = reader.ReadArrayHeader();
                    numbers = new double[count];

                    for (int n = 0; n < count; ++n)
                    {
                        numbers[n] = reader.ReadDouble();
                    }

                    break;

                default:
                    pending.Add(Keep(ref reader, UnknownField.Root, key));
                    break;
            }
        }

        Attribute(unknown, pending, UnknownField.Reference(name));

        return kind switch
        {
            0 when numbers.Length >= 6 => new ReferenceGeometry.Plane(
                owner, name, Vector(numbers, 0), Vector(numbers, 3)),

            1 when numbers.Length >= 6 => new ReferenceGeometry.Axis(
                owner, name, Vector(numbers, 0), Vector(numbers, 3)),

            2 when numbers.Length >= 3 => new ReferenceGeometry.Point(
                owner, name, Vector(numbers, 0)),

            3 when numbers.Length >= 9 => new ReferenceGeometry.CoordinateSystem(
                owner, name, Vector(numbers, 0), Vector(numbers, 3), Vector(numbers, 6)),

            _ => throw new DocumentFormatException(
                $"'{name}' is reference geometry of a kind this build does not know, or is missing "
                + "the numbers that place it."),
        };
    }

    private static int KindOf(ReferenceGeometry geometry) => geometry switch
    {
        ReferenceGeometry.Plane => 0,
        ReferenceGeometry.Axis => 1,
        ReferenceGeometry.Point => 2,
        ReferenceGeometry.CoordinateSystem => 3,
        _ => -1,
    };

    private static void WriteMetadata(
        MessagePackWriter writer, DocumentMetadata metadata, ILookup<string, UnknownField> unknown)
    {
        writer.WriteMapHeader(6 + Extra(unknown, UnknownField.Metadata));

        writer.Write("title");
        WriteOptional(writer, metadata.Title);

        writer.Write("part");
        WriteOptional(writer, metadata.PartNumber);

        writer.Write("rev");
        WriteOptional(writer, metadata.Revision);

        writer.Write("material");
        WriteOptional(writer, metadata.Material);

        writer.Write("desc");
        WriteOptional(writer, metadata.Description);

        writer.Write("props");

        ImmutableArray<KeyValuePair<string, string>> properties =
            [.. metadata.Properties.OrderBy(p => p.Key, StringComparer.Ordinal)];

        writer.WriteMapHeader(properties.Length);

        foreach ((string key, string value) in properties)
        {
            writer.Write(key);
            writer.Write(value);
        }

        Preserved(writer, unknown, UnknownField.Metadata);
    }

    private static DocumentMetadata ReadMetadata(
        ref MessagePackReader reader, List<UnknownField> unknown)
    {
        int fields = reader.ReadMapHeader();

        DocumentMetadata metadata = DocumentMetadata.Empty;

        for (int i = 0; i < fields; ++i)
        {
            string key = reader.ReadString();

            switch (key)
            {
                case "title":
                    metadata = metadata with { Title = ReadOptional(ref reader) };
                    break;

                case "part":
                    metadata = metadata with { PartNumber = ReadOptional(ref reader) };
                    break;

                case "rev":
                    metadata = metadata with { Revision = ReadOptional(ref reader) };
                    break;

                case "material":
                    metadata = metadata with { Material = ReadOptional(ref reader) };
                    break;

                case "desc":
                    metadata = metadata with { Description = ReadOptional(ref reader) };
                    break;

                case "props":
                    int count = reader.ReadMapHeader();

                    for (int p = 0; p < count; ++p)
                    {
                        metadata = metadata.WithProperty(reader.ReadString(), reader.ReadString());
                    }

                    break;

                default:
                    unknown.Add(Keep(ref reader, UnknownField.Metadata, key));
                    break;
            }
        }

        return metadata;
    }

    private static void WriteVector(MessagePackWriter writer, Vec3d value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private static Vec3d Vector(double[] numbers, int at)
        => new(numbers[at], numbers[at + 1], numbers[at + 2]);

    private static void WriteOptional(MessagePackWriter writer, string? value)
    {
        if (value is null)
        {
            writer.WriteNil();
        }
        else
        {
            writer.Write(value);
        }
    }

    private static string? ReadOptional(ref MessagePackReader reader)
        => reader.TryReadNil() ? null : reader.ReadString();
}
