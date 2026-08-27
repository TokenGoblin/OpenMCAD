using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;

namespace OpenMCAD.Core.Rebuild;

/// <summary>
/// Identifies what a feature's result depends on, so that an identical situation can be recognised.
/// </summary>
/// <param name="High">The first half of the digest.</param>
/// <param name="Low">The second half.</param>
/// <remarks>
/// <para>
/// <b>What it identifies.</b> The feature itself, its type, its resolved parameters, and what each
/// of its inputs produced. Everything that can change the answer, and nothing that cannot — the
/// display name is absent, so renaming a feature in the tree does not throw its geometry away.
/// </para>
/// <para>
/// <b>Chained, not local.</b> A feature's result depends on its own type and parameters and on
/// <em>what its inputs produced</em> — so the key folds in the keys of those inputs rather than
/// their identities. That makes it a Merkle chain: two documents that describe the same chain of
/// operations produce the same key at every step, whoever built them and whenever. Keying on the
/// input <see cref="FeatureId"/>s instead would say two chains are the same when their parameters
/// differ, which serves wrong geometry; keying on the input <see cref="Kernel.KernelShape"/>s would
/// be correct but useless, because a shape tag is a handle into the running kernel and a fresh one
/// is issued every rebuild, so nothing would ever hit.
/// </para>
/// <para>
/// <b>Stable across processes, which rules out the obvious implementation.</b> .NET randomises
/// string hashing per process by default, so a key built from <c>string.GetHashCode</c> would
/// differ between two runs of the same program — and a cache that never hits after a restart is
/// not a cache. This uses SHA-256, truncated to 128 bits, over a canonical encoding.
/// </para>
/// <para>
/// <b>Every variable-length part is length-prefixed.</b> Without that, a feature named <c>ab</c>
/// with a parameter <c>c</c> and one named <c>a</c> with a parameter <c>bc</c> encode to the same
/// bytes and collide — and a cache collision is not a slow lookup, it is the wrong solid.
/// </para>
/// </remarks>
public readonly record struct RebuildKey(ulong High, ulong Low)
{
    /// <summary>Gets the key that means "no key", used for an input that produced nothing.</summary>
    public static RebuildKey None => default;

    /// <summary>Gets whether this is a real key.</summary>
    public bool IsValid => High != 0 || Low != 0;

    /// <summary>Computes the key for one feature.</summary>
    /// <param name="feature">The feature being evaluated.</param>
    /// <param name="inputKeys">
    /// The keys of the features it consumes, in the order they are declared. Order matters: a
    /// boolean subtract of A from B is not a subtract of B from A.
    /// </param>
    /// <returns>The key.</returns>
    public static RebuildKey For(Feature feature, ImmutableArray<RebuildKey> inputKeys)
    {
        ArgumentNullException.ThrowIfNull(feature);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // A version tag. When the encoding below changes, every key changes with it, so entries
        // written by an older build are missed rather than misread. Without this, adding a field
        // here would silently make old cache entries answer new questions -- which is exactly what
        // adding the entity references at version 2 would otherwise have done.
        Write(hash, 2);

        // The feature's own identity, and not only its definition. Two features of the same type
        // with the same parameters do produce the same geometry, so a purely content-addressed key
        // would be sound in principle -- but what is cached is a FeatureOutput, whose bodies each
        // name the feature that owns them. Handing one feature's output to another gives it bodies
        // belonging to its neighbour. Sharing across features would mean canonicalising the output
        // first, which is a different and speculative optimisation; identity in the key costs one
        // field and makes the question not arise.
        Write(hash, feature.Id.Value.ToByteArray());

        WriteText(hash, feature.FeatureType);

        // The resolved values, not the expressions. Two documents whose parameters were typed
        // differently but evaluate the same produce the same geometry, and should hit.
        Write(hash, feature.Parameters.Length);

        foreach (Parameter parameter in feature.Parameters)
        {
            WriteText(hash, parameter.Name);
            Write(hash, (int)parameter.Value.Dimension);
            Write(hash, BitConverter.DoubleToInt64Bits(parameter.Value.Value));
        }

        // Re-pointing a reference changes what the feature is built on, and therefore what it
        // produces. Leaving these out of the key would have a repaired reference (P3-T11) hit the
        // cache and hand back the geometry from before the repair -- the one case where a stale
        // answer is guaranteed to be wrong, because the user has just said so.
        Write(hash, feature.EntityReferences.Length);

        foreach (EntityReference reference in feature.EntityReferences)
        {
            WriteText(hash, PersistentNameFormat.Write(reference.Name));
            Write(hash, (int)reference.Multiplicity);
        }

        Write(hash, inputKeys.Length);

        foreach (RebuildKey input in inputKeys)
        {
            Write(hash, (long)input.High);
            Write(hash, (long)input.Low);
        }

        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);

        return new RebuildKey(
            BinaryPrimitives.ReadUInt64BigEndian(digest),
            BinaryPrimitives.ReadUInt64BigEndian(digest[8..]));
    }

    /// <inheritdoc />
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"key({High:X16}{Low:X16})");

    private static void Write(IncrementalHash hash, byte[] value)
    {
        Write(hash, value.Length);
        hash.AppendData(value);
    }

    private static void Write(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void Write(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    /// <summary>Appends text, length first.</summary>
    /// <remarks>
    /// UTF-8 and big-endian throughout, so a key computed on one machine means the same on
    /// another. A document opened on a different architecture must find the cache entries its own
    /// geometry cache wrote, and more importantly must not find different ones.
    /// </remarks>
    private static void WriteText(IncrementalHash hash, string? text)
    {
        if (text is null)
        {
            Write(hash, -1);
            return;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(text);

        Write(hash, bytes.Length);
        hash.AppendData(bytes);
    }
}
