using System.Collections.Immutable;

namespace OpenMCAD.Solver.Sketching;

/// <summary>What a constraint asks of the geometry.</summary>
/// <remarks>
/// The set §5.6 requires. Named rather than numbered in files, so inserting a kind here cannot
/// change what an existing sketch means.
/// </remarks>
public enum ConstraintKind
{
    /// <summary>Two points are in the same place.</summary>
    Coincident,

    /// <summary>A point lies somewhere on a curve.</summary>
    PointOnObject,

    /// <summary>Two points are a given distance apart.</summary>
    Distance,

    /// <summary>A line, or the line between two points, is horizontal.</summary>
    Horizontal,

    /// <summary>A line, or the line between two points, is vertical.</summary>
    Vertical,

    /// <summary>Two lines run in the same direction.</summary>
    Parallel,

    /// <summary>Two lines meet at a right angle.</summary>
    Perpendicular,

    /// <summary>Two curves touch without crossing.</summary>
    Tangent,

    /// <summary>Two entities of the same sort have the same size.</summary>
    Equal,

    /// <summary>Two points are mirror images about a line.</summary>
    Symmetric,

    /// <summary>Two curves share a centre.</summary>
    Concentric,

    /// <summary>A point is at the middle of a line.</summary>
    Midpoint,

    /// <summary>Two lines meet at a given angle.</summary>
    Angle,

    /// <summary>A circle or arc has a given radius.</summary>
    Radius,

    /// <summary>A circle or arc has a given diameter.</summary>
    Diameter,

    /// <summary>A point does not move.</summary>
    Fix,
}

/// <summary>What sort of number a constraint carries, if any.</summary>
/// <remarks>
/// Not <c>OpenMCAD.Core.Documents.Dimension</c>, which would put the whole document layer beneath
/// the solver. A sketch constraint needs to know a length from an angle and nothing more; the
/// conversion to a real dimensioned <c>Quantity</c> happens where a sketch becomes a feature.
/// </remarks>
public enum ConstraintValueKind
{
    /// <summary>The constraint carries no number.</summary>
    None,

    /// <summary>A distance, in the sketch's units.</summary>
    Length,

    /// <summary>An angle, in radians.</summary>
    Angle,
}

/// <summary>
/// Identifies one constraint.
/// </summary>
/// <param name="Value">The underlying value.</param>
public readonly record struct SketchConstraintId(Guid Value)
{
    /// <summary>Gets the id that denotes no constraint.</summary>
    public static SketchConstraintId None => default;

    /// <summary>Gets a value indicating whether this denotes a constraint.</summary>
    public bool IsValid => Value != Guid.Empty;

    /// <summary>Creates a new, unique id.</summary>
    /// <returns>The id.</returns>
    public static SketchConstraintId New() => new(Guid.NewGuid());

    /// <summary>Reads an id back from its round-trip form.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The id.</returns>
    /// <exception cref="FormatException">The text is not a recognised identifier.</exception>
    public static SketchConstraintId Parse(string text) => new(Guid.ParseExact(text, "D"));

    /// <summary>Writes the id in the form a file holds.</summary>
    /// <returns>The text.</returns>
    public string ToStorageString()
        => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);

    /// <inheritdoc/>
    public override string ToString() => IsValid ? ToStorageString() : "(none)";
}

/// <summary>
/// One thing the user has said must be true of the sketch.
/// </summary>
/// <param name="Id">Which constraint this is.</param>
/// <param name="Kind">What it asks.</param>
/// <param name="Operands">What it asks it of, in the order the kind expects.</param>
/// <param name="Value">The number it carries, where its kind has one.</param>
/// <param name="IsDriving">
/// Whether it constrains the geometry or merely measures it. A reference dimension is displayed and
/// updated but removes no freedom.
/// </param>
/// <remarks>
/// <para>
/// P4-T04. One record with a kind rather than sixteen records. The solver boundary needs a uniform
/// representation anyway — planegcs takes tagged constraints over a parameter vector — and what
/// each kind requires is then a table (<see cref="ConstraintSchema"/>) rather than a type per kind
/// plus a case in every switch that ever walks them. Adding a constraint kind becomes one row.
/// </para>
/// <para>
/// Operands are point references throughout. Where a kind wants a whole entity — two lines being
/// parallel — the operand names the entity with <see cref="EntityPoint.Self"/>, which for a point
/// entity is also its position, and that is not a coincidence: a point entity <em>is</em> its
/// position. One operand type means one resolution path, which is what stops the sketcher and the
/// solver disagreeing about what a constraint was attached to.
/// </para>
/// <para>
/// A reference dimension is a flag rather than a kind. It measures the same thing, is displayed the
/// same way, and differs only in whether the solver is told about it — making it a separate kind
/// would double the table and make "convert to reference" a change of type.
/// </para>
/// </remarks>
public sealed record SketchConstraint(
    SketchConstraintId Id,
    ConstraintKind Kind,
    ImmutableArray<SketchPointRef> Operands,
    double? Value = null,
    bool IsDriving = true)
{
    /// <summary>Gets the operands, never a default array.</summary>
    public ImmutableArray<SketchPointRef> On => Operands.IsDefault ? [] : Operands;

    /// <summary>Gets how the schema describes this kind.</summary>
    public ConstraintSchema Schema => ConstraintSchema.For(Kind);

    /// <summary>Gets how many degrees of freedom this removes.</summary>
    /// <remarks>
    /// Zero for a reference dimension. That is the whole difference between a driving and a driven
    /// dimension, and expressing it here rather than at each use means the degree-of-freedom count
    /// cannot disagree with what the solver was actually given.
    /// </remarks>
    public int Removes => IsDriving ? Schema.Equations : 0;

    /// <summary>Builds a constraint with a fresh id.</summary>
    /// <param name="kind">What it asks.</param>
    /// <param name="operands">What it asks it of.</param>
    /// <param name="value">The number it carries, where its kind has one.</param>
    /// <returns>The constraint.</returns>
    public static SketchConstraint Of(
        ConstraintKind kind, IEnumerable<SketchPointRef> operands, double? value = null)
    {
        ArgumentNullException.ThrowIfNull(operands);

        return new SketchConstraint(SketchConstraintId.New(), kind, [.. operands], value);
    }

    /// <inheritdoc/>
    public bool Equals(SketchConstraint? other)
        => other is not null
            && Id == other.Id
            && Kind == other.Kind
            && Nullable.Equals(Value, other.Value)
            && IsDriving == other.IsDriving
            && On.SequenceEqual(other.On);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Id, Kind, Value, IsDriving, On.Length);

    /// <inheritdoc/>
    public override string ToString()
    {
        string driving = IsDriving ? string.Empty : " (reference)";

        return Value is { } value
            ? $"{Kind} {value}{driving}"
            : $"{Kind}{driving}";
    }
}
