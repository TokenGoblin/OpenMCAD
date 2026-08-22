using System.Globalization;

namespace OpenMCAD.Kernel;

/// <summary>
/// The kind of topological entity a <see cref="SubEntity"/> refers to.
/// </summary>
/// <remarks>
/// Values are part of the native ABI. Append only; never renumber.
/// </remarks>
public enum SubEntityKind
{
    /// <summary>Not a valid kind. Present so an uninitialised value is detectable.</summary>
    None = 0,

    /// <summary>A point in space bounding an edge.</summary>
    Vertex = 1,

    /// <summary>A bounded curve between vertices.</summary>
    Edge = 2,

    /// <summary>An ordered, connected set of edges.</summary>
    Wire = 3,

    /// <summary>A bounded region of a surface.</summary>
    Face = 4,

    /// <summary>A connected set of faces.</summary>
    Shell = 5,

    /// <summary>A region of space bounded by shells.</summary>
    Solid = 6,

    /// <summary>A heterogeneous collection of shapes.</summary>
    Compound = 7,
}

/// <summary>
/// An opaque, non-owning reference to one face, edge, or vertex within a shape.
/// </summary>
/// <param name="Owner">The shape this entity belongs to.</param>
/// <param name="Tag">
/// The kernel-side handle for the entity, from the same table and with the same generation-counter
/// scheme as <see cref="KernelShape.Tag"/>.
/// </param>
/// <param name="Kind">What sort of entity this is.</param>
/// <remarks>
/// <para>
/// Sub-entities are owned by their shape and are released with it. There is no separate lifetime to
/// manage, and no <c>SafeHandle</c> per face — a shape with ten thousand faces would otherwise
/// require ten thousand finalizable objects. <see cref="Owner"/> is carried explicitly so that
/// lifetime is legible at every call site and so that using an entity after its shape has been
/// released is a detectable error rather than an undefined one.
/// </para>
/// <para>
/// <b>These references do not survive a rebuild.</b> A face's tag after an edit bears no relation
/// to its tag before, which is the entire reason ADR-0005 exists. Within a single operation they
/// are exact, and that is what <see cref="HistoryMap"/> uses them for: correlating the inputs of an
/// operation with its outputs, once, while both are in hand. Storing one in a document is a bug.
/// </para>
/// <para>
/// Ordering: kernels must issue tags for an operation's outputs in a canonical, geometry-derived
/// order, so that sorting by tag is both stable and reproducible across runs. Determinism is a hard
/// requirement (ADR-0011) and it starts here, because a set of faces returned in memory-allocation
/// order would silently make every downstream name unstable.
/// </para>
/// </remarks>
public readonly record struct SubEntity(KernelShape Owner, ulong Tag, SubEntityKind Kind)
    : IComparable<SubEntity>
{
    /// <summary>Gets the reference that denotes no entity.</summary>
    public static SubEntity None => default;

    /// <summary>Gets a value indicating whether this reference denotes an entity.</summary>
    public bool IsValid => Tag != 0 && Kind != SubEntityKind.None;

    /// <summary>
    /// Compares by kind and then by tag, giving the stable ordering that determinism depends on.
    /// </summary>
    /// <param name="other">The entity to compare against.</param>
    public int CompareTo(SubEntity other)
    {
        int byKind = ((int)Kind).CompareTo((int)other.Kind);
        if (byKind != 0)
        {
            return byKind;
        }

        int byOwner = Owner.Tag.CompareTo(other.Owner.Tag);
        return byOwner != 0 ? byOwner : Tag.CompareTo(other.Tag);
    }

    /// <summary>Orders by <see cref="CompareTo"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    public static bool operator <(SubEntity left, SubEntity right) => left.CompareTo(right) < 0;

    /// <summary>Orders by <see cref="CompareTo"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    public static bool operator <=(SubEntity left, SubEntity right) => left.CompareTo(right) <= 0;

    /// <summary>Orders by <see cref="CompareTo"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    public static bool operator >(SubEntity left, SubEntity right) => left.CompareTo(right) > 0;

    /// <summary>Orders by <see cref="CompareTo"/>.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    public static bool operator >=(SubEntity left, SubEntity right) => left.CompareTo(right) >= 0;

    /// <inheritdoc />
    public override string ToString() => IsValid
        ? string.Create(CultureInfo.InvariantCulture, $"{Kind}(0x{Tag:X} of 0x{Owner.Tag:X})")
        : "entity(none)";
}
