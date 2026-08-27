using System.Collections.Immutable;

namespace OpenMCAD.Solver.Sketching;

/// <summary>What sort of thing an operand slot takes.</summary>
public enum OperandKind
{
    /// <summary>A point: either a point entity, or a named point of a curve.</summary>
    Point,

    /// <summary>A straight line, as a whole.</summary>
    Line,

    /// <summary>A circle or an arc, as a whole.</summary>
    Circular,

    /// <summary>Any curve, as a whole.</summary>
    Curve,

    /// <summary>Any entity at all, as a whole.</summary>
    Any,
}

/// <summary>
/// What one kind of constraint requires, declared once.
/// </summary>
/// <param name="Kind">Which constraint kind this describes.</param>
/// <param name="Label">What a person is shown.</param>
/// <param name="Operands">What each operand slot takes, in order.</param>
/// <param name="ValueKind">What sort of number it carries, if any.</param>
/// <param name="Equations">How many degrees of freedom it removes when driving.</param>
/// <param name="Alternatives">
/// Other operand shapes the same kind accepts. Horizontal takes one line or two points, and both
/// mean the same thing to a user.
/// </param>
/// <remarks>
/// <para>
/// P4-T04, and the same idea as <c>FeatureSchema</c> (P3-T21): the description is data in one
/// place, so validation, the eventual constraint palette, serialization and the degree-of-freedom
/// count cannot drift apart. A constraint kind added without a row here has no schema and is
/// refused, which is the failure worth having.
/// </para>
/// <para>
/// <see cref="Equations"/> is what makes diagnosis possible at all. "Under-constrained by three"
/// is the sum over the constraints of what each removes, subtracted from the sketch's freedom, and
/// a table that guessed those numbers would produce a degree-of-freedom readout that was merely
/// plausible.
/// </para>
/// </remarks>
public sealed record ConstraintSchema(
    ConstraintKind Kind,
    string Label,
    ImmutableArray<OperandKind> Operands,
    ConstraintValueKind ValueKind,
    int Equations,
    ImmutableArray<ImmutableArray<OperandKind>> Alternatives = default)
{
    private static readonly ImmutableDictionary<ConstraintKind, ConstraintSchema> Table = Build();

    /// <summary>Gets the accepted operand shapes, the declared one first.</summary>
    public ImmutableArray<ImmutableArray<OperandKind>> Shapes =>
        [Operands, .. Alternatives.IsDefault ? [] : Alternatives];

    /// <summary>Gets whether this kind carries a number.</summary>
    public bool HasValue => ValueKind != ConstraintValueKind.None;

    /// <summary>Gets every kind there is, in declaration order.</summary>
    public static ImmutableArray<ConstraintSchema> All =>
        [.. Enum.GetValues<ConstraintKind>().Select(For)];

    /// <summary>Describes a kind of constraint.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>Its schema.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Nothing describes that kind.</exception>
    public static ConstraintSchema For(ConstraintKind kind)
        => Table.TryGetValue(kind, out ConstraintSchema? schema)
            ? schema
            : throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "There is no schema for this constraint kind, so nothing could validate it, count "
                + "its degrees of freedom, or write it to a file.");

    /// <summary>Whether an entity can stand in an operand slot of a given sort.</summary>
    /// <param name="wanted">What the slot takes.</param>
    /// <param name="reference">What was given.</param>
    /// <param name="entities">The sketch.</param>
    /// <returns>Why not, or null if it can.</returns>
    public static string? Rejects(
        OperandKind wanted, SketchPointRef reference, SketchEntitySet entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        SketchEntity? entity = entities.Find(reference.Entity);

        if (entity is null)
        {
            return "there is no such entity in this sketch";
        }

        if (wanted == OperandKind.Point)
        {
            return entity.PointOf(reference.Point) is null
                ? $"a {entity.Kind} has no {Describe(reference.Point)}"
                : null;
        }

        // Every other slot wants the entity itself, so naming one of its points is a mistake
        // rather than a shorthand: "this line's end is parallel to that line" is not a sentence.
        if (reference.Point != EntityPoint.Self)
        {
            return $"it names a point of the {entity.Kind} where the whole {entity.Kind} is wanted";
        }

        return wanted switch
        {
            OperandKind.Line => entity is SketchLine ? null : $"a {entity.Kind} is not a line",
            OperandKind.Circular => entity is SketchCircle or SketchArc
                ? null
                : $"a {entity.Kind} has no radius",
            OperandKind.Curve => entity is SketchPoint ? "a point is not a curve" : null,
            _ => null,
        };
    }

    /// <inheritdoc/>
    public bool Equals(ConstraintSchema? other)
        => other is not null
            && Kind == other.Kind
            && Label == other.Label
            && ValueKind == other.ValueKind
            && Equations == other.Equations
            && Shapes.Length == other.Shapes.Length
            && Shapes.Zip(other.Shapes).All(pair => pair.First.SequenceEqual(pair.Second));

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Kind, Label, ValueKind, Equations);

    /// <inheritdoc/>
    public override string ToString() => $"{Label} ({Equations} equations)";

    private static string Describe(EntityPoint point) => point switch
    {
        EntityPoint.Self => "position",
        EntityPoint.Start => "start point",
        EntityPoint.End => "end point",
        EntityPoint.Centre => "centre",
        EntityPoint.Focus => "focus",
        EntityPoint.SecondFocus => "second focus",
        _ => "midpoint",
    };

    /// <summary>The table itself.</summary>
    /// <remarks>
    /// The equation counts are what a solver actually gets, not what feels right. A coincidence is
    /// two equations because it fixes both coordinates; a distance is one because it fixes only how
    /// far apart the two points are and leaves the direction free. Getting these wrong does not
    /// break the solve — it breaks the degree-of-freedom readout, which is worse, because the
    /// number looks authoritative and is quietly false.
    /// </remarks>
    private static ImmutableDictionary<ConstraintKind, ConstraintSchema> Build()
    {
        ConstraintSchema[] schemas =
        [
            new(ConstraintKind.Coincident, "Coincident",
                [OperandKind.Point, OperandKind.Point], ConstraintValueKind.None, 2),

            new(ConstraintKind.PointOnObject, "Point on object",
                [OperandKind.Point, OperandKind.Curve], ConstraintValueKind.None, 1),

            new(ConstraintKind.Distance, "Distance",
                [OperandKind.Point, OperandKind.Point], ConstraintValueKind.Length, 1,
                [[OperandKind.Point, OperandKind.Line]]),

            // One line or two points. Both say the same thing to a user, and both remove one
            // freedom: the difference in Y is zero.
            new(ConstraintKind.Horizontal, "Horizontal",
                [OperandKind.Line], ConstraintValueKind.None, 1,
                [[OperandKind.Point, OperandKind.Point]]),

            new(ConstraintKind.Vertical, "Vertical",
                [OperandKind.Line], ConstraintValueKind.None, 1,
                [[OperandKind.Point, OperandKind.Point]]),

            new(ConstraintKind.Parallel, "Parallel",
                [OperandKind.Line, OperandKind.Line], ConstraintValueKind.None, 1),

            new(ConstraintKind.Perpendicular, "Perpendicular",
                [OperandKind.Line, OperandKind.Line], ConstraintValueKind.None, 1),

            new(ConstraintKind.Tangent, "Tangent",
                [OperandKind.Curve, OperandKind.Curve], ConstraintValueKind.None, 1),

            new(ConstraintKind.Equal, "Equal",
                [OperandKind.Any, OperandKind.Any], ConstraintValueKind.None, 1),

            // Two points mirrored about a line: two equations, because the pair is pinned both
            // across the line and along it.
            new(ConstraintKind.Symmetric, "Symmetric",
                [OperandKind.Point, OperandKind.Point, OperandKind.Line],
                ConstraintValueKind.None, 2),

            new(ConstraintKind.Concentric, "Concentric",
                [OperandKind.Circular, OperandKind.Circular], ConstraintValueKind.None, 2),

            new(ConstraintKind.Midpoint, "Midpoint",
                [OperandKind.Point, OperandKind.Line], ConstraintValueKind.None, 2),

            new(ConstraintKind.Angle, "Angle",
                [OperandKind.Line, OperandKind.Line], ConstraintValueKind.Angle, 1),

            new(ConstraintKind.Radius, "Radius",
                [OperandKind.Circular], ConstraintValueKind.Length, 1),

            new(ConstraintKind.Diameter, "Diameter",
                [OperandKind.Circular], ConstraintValueKind.Length, 1),

            // A point, not an entity. Fixing a circle means fixing its centre and giving it a
            // radius, which are two constraints that say exactly what they do; a Fix whose meaning
            // changed with what it was pointed at would remove a different number of freedoms each
            // time and make the readout unexplainable.
            new(ConstraintKind.Fix, "Fix",
                [OperandKind.Point], ConstraintValueKind.None, 2),
        ];

        return schemas.ToImmutableDictionary(s => s.Kind);
    }
}
