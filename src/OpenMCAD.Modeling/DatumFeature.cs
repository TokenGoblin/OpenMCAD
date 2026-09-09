using System.Collections.Immutable;

using OpenMCAD.Core.Documents;

namespace OpenMCAD.Modeling;

/// <summary>
/// The feature types that build reference geometry, and how one of them is read back into the
/// <see cref="DatumDefinition"/> it describes (P5-T03).
/// </summary>
/// <remarks>
/// <para>
/// <b>One feature type per construction, not one per kind of datum.</b> Thirteen types rather than
/// three with a "method" to choose between. The reason is in the schema machinery:
/// <see cref="PropertyCondition"/> decides visibility on one property having one value, so a single
/// <c>DatumPlane</c> type could not say "this input applies when the method is offset <em>or</em>
/// angle" — and every construction shares some inputs with some others. Generalising the condition
/// to a set of values would be extending the schema for one feature's convenience, and the thing it
/// bought would be worse anyway: a file would record <c>DatumPlane</c> plus a setting, where it now
/// records exactly which construction was used in the one field that is never dropped or defaulted.
/// Grouping the thirteen back into three menus is <see cref="FeatureSchema.Category"/>'s job and the
/// ribbon's (P6-T01), which is presentation and belongs there.
/// </para>
/// <para>
/// <b>The datum's name is the feature's name.</b> <see cref="DatumDefinition.Name"/> is what
/// <see cref="Document.FindReference"/> looks up by, and a feature type that makes exactly one datum
/// has exactly one thing to call it. Storing a second name in a setting would let the two disagree,
/// and then the tree would say "Plane1" while every sketch built on it named something else.
/// </para>
/// </remarks>
public static class DatumFeature
{
    /// <summary>The category every datum feature belongs to.</summary>
    public const string Category = "Reference geometry";

    /// <summary>The feature type of a plane offset from another.</summary>
    public const string PlaneOffset = "DatumPlaneOffset";

    /// <summary>The feature type of a plane through a point, parallel to another.</summary>
    public const string PlaneThroughPoint = "DatumPlaneThroughPoint";

    /// <summary>The feature type of a plane at an angle to another.</summary>
    public const string PlaneAtAngle = "DatumPlaneAtAngle";

    /// <summary>The feature type of a plane through three points.</summary>
    public const string PlaneThreePoints = "DatumPlaneThreePoints";

    /// <summary>The feature type of a plane halfway between two.</summary>
    public const string PlaneMidway = "DatumPlaneMidway";

    /// <summary>The feature type of an axis through two points.</summary>
    public const string AxisTwoPoints = "DatumAxisTwoPoints";

    /// <summary>The feature type of an axis along a straight edge.</summary>
    public const string AxisAlongEdge = "DatumAxisAlongEdge";

    /// <summary>The feature type of an axis where two planes meet.</summary>
    public const string AxisPlanesMeet = "DatumAxisPlanesMeet";

    /// <summary>The feature type of an axis normal to a plane.</summary>
    public const string AxisNormalToPlane = "DatumAxisNormalToPlane";

    /// <summary>The feature type of a point where something already is.</summary>
    public const string Point = "DatumPoint";

    /// <summary>The feature type of a point at the centre of a circular edge.</summary>
    public const string PointCentre = "DatumPointCentre";

    /// <summary>The feature type of a point where an axis crosses a plane.</summary>
    public const string PointAxisMeetsPlane = "DatumPointAxisMeetsPlane";

    /// <summary>The feature type of a point a fraction of the way along an edge.</summary>
    public const string PointAlongEdge = "DatumPointAlongEdge";

    /// <summary>Gets the schema of every datum feature type.</summary>
    public static ImmutableArray<FeatureSchema> Schemas { get; } =
    [
        Schema(PlaneOffset, "Offset plane", "A plane parallel to another, a distance away.",
            Input("from", "From"),
            Length("distance", "Distance")),

        Schema(PlaneThroughPoint, "Plane through a point", "A plane through a point, parallel to another.",
            Input("through", "Through"),
            Input("parallelTo", "Parallel to")),

        Schema(PlaneAtAngle, "Plane at an angle", "A plane rotated from another about an axis lying in it.",
            Input("from", "From"),
            Input("about", "About"),
            Angle("angle", "Angle")),

        Schema(PlaneThreePoints, "Plane through three points", "A plane through three points.",
            Input("first", "First point"),
            Input("second", "Second point"),
            Input("third", "Third point")),

        Schema(PlaneMidway, "Midplane", "The plane halfway between two parallel planes.",
            Input("first", "First plane"),
            Input("second", "Second plane")),

        Schema(AxisTwoPoints, "Axis through two points", "An axis through two points.",
            Input("from", "From"),
            Input("to", "To")),

        Schema(AxisAlongEdge, "Axis along an edge", "An axis along a straight edge.",
            Input("edge", "Edge")),

        Schema(AxisPlanesMeet, "Axis where two planes meet", "The axis two planes share.",
            Input("first", "First plane"),
            Input("second", "Second plane")),

        Schema(AxisNormalToPlane, "Axis normal to a plane", "An axis normal to a plane, through a point.",
            Input("plane", "Plane"),
            Input("through", "Through")),

        Schema(Point, "Point", "A point where something already is.",
            Input("at", "At")),

        Schema(PointCentre, "Point at a centre", "The centre of a circular edge.",
            Input("edge", "Edge")),

        Schema(PointAxisMeetsPlane, "Point where an axis meets a plane", "Where an axis crosses a plane.",
            Input("axis", "Axis"),
            Input("plane", "Plane")),

        Schema(PointAlongEdge, "Point along an edge", "A point a fraction of the way along an edge.",
            Input("edge", "Edge"),
            Fraction("fraction", "Fraction")),
    ];

    /// <summary>Gets a catalogue holding every datum feature type.</summary>
    public static FeatureCatalogue Catalogue { get; } = FeatureCatalogue.Of(Schemas);

    /// <summary>Gets whether a feature type is one of these.</summary>
    /// <param name="featureType">The type.</param>
    /// <returns>Whether this class knows it.</returns>
    public static bool Handles(string featureType) => Catalogue.Find(featureType) is not null;

    /// <summary>Reads a feature back into the datum it describes.</summary>
    /// <param name="feature">The feature.</param>
    /// <returns>The definition, or why the feature does not describe one.</returns>
    /// <remarks>
    /// Returns a complaint rather than throwing on a malformed feature, and the complaint is the
    /// same shape <see cref="FeatureSchema.Validate"/> produces, because they are asking the same
    /// question at different times: validation asks before a rebuild whether a feature is worth
    /// running, and this asks during one whether it can be. A feature that passes validation always
    /// translates, so a failure here is either a feature that was never validated or a schema and a
    /// translation that have drifted apart.
    /// </remarks>
    public static DatumTranslation Translate(Feature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);

        return feature.FeatureType switch
        {
            PlaneOffset => Build(feature, ["from"], ["distance"], (name, r, q)
                => new DatumDefinition.PlaneOffsetFrom(name, r[0], q[0])),

            PlaneThroughPoint => Build(feature, ["through", "parallelTo"], [], (name, r, _)
                => new DatumDefinition.PlaneThroughPoint(name, r[0], r[1])),

            PlaneAtAngle => Build(feature, ["from", "about"], ["angle"], (name, r, q)
                => new DatumDefinition.PlaneAtAngle(name, r[0], r[1], q[0])),

            PlaneThreePoints => Build(feature, ["first", "second", "third"], [], (name, r, _)
                => new DatumDefinition.PlaneThroughThreePoints(name, r[0], r[1], r[2])),

            PlaneMidway => Build(feature, ["first", "second"], [], (name, r, _)
                => new DatumDefinition.PlaneMidwayBetween(name, r[0], r[1])),

            AxisTwoPoints => Build(feature, ["from", "to"], [], (name, r, _)
                => new DatumDefinition.AxisThroughTwoPoints(name, r[0], r[1])),

            AxisAlongEdge => Build(feature, ["edge"], [], (name, r, _)
                => new DatumDefinition.AxisAlongEdge(name, r[0])),

            AxisPlanesMeet => Build(feature, ["first", "second"], [], (name, r, _)
                => new DatumDefinition.AxisWherePlanesMeet(name, r[0], r[1])),

            AxisNormalToPlane => Build(feature, ["plane", "through"], [], (name, r, _)
                => new DatumDefinition.AxisNormalToPlane(name, r[0], r[1])),

            Point => Build(feature, ["at"], [], (name, r, _)
                => new DatumDefinition.PointAt(name, r[0])),

            PointCentre => Build(feature, ["edge"], [], (name, r, _)
                => new DatumDefinition.PointAtCentreOf(name, r[0])),

            PointAxisMeetsPlane => Build(feature, ["axis", "plane"], [], (name, r, _)
                => new DatumDefinition.PointWhereAxisMeetsPlane(name, r[0], r[1])),

            PointAlongEdge => Build(feature, ["edge"], ["fraction"], (name, r, q)
                => new DatumDefinition.PointAlongEdge(name, r[0], q[0])),

            _ => DatumTranslation.Failed(
                $"'{feature.FeatureType}' is not a kind of reference geometry this build makes."),
        };
    }

    /// <summary>Reads one of a feature's declared inputs.</summary>
    /// <param name="feature">The feature.</param>
    /// <param name="property">The input's stable name.</param>
    /// <returns>What it points at, or <see langword="null"/> if nothing does.</returns>
    /// <remarks>
    /// The two ways a <see cref="PropertyKind.Reference"/> input can be answered, read in the order
    /// that makes a file naming both an error rather than a silent preference: this reports the
    /// setting, and <see cref="FeatureSchema.SatisfiedBy"/> is what refuses the pair outright.
    /// </remarks>
    public static DatumReference? InputOf(Feature feature, string property)
    {
        ArgumentNullException.ThrowIfNull(feature);
        ArgumentException.ThrowIfNullOrEmpty(property);

        if (feature.FindSetting(property) is ReferenceValue geometry)
        {
            return new DatumReference.OnGeometry(geometry.Owner, geometry.Name);
        }

        int index = feature.FindSelection(property);

        return index >= 0
            ? new DatumReference.OnTopology(feature.EntityReferences[index].Name)
            : null;
    }

    private static DatumTranslation Build(
        Feature feature,
        string[] inputs,
        string[] quantities,
        Func<string, DatumReference[], double[], DatumDefinition> make)
    {
        DatumReference[] references = new DatumReference[inputs.Length];

        for (int i = 0; i < inputs.Length; ++i)
        {
            if (InputOf(feature, inputs[i]) is not { } reference)
            {
                return DatumTranslation.Failed(
                    $"'{inputs[i]}' has not been given anything to point at.");
            }

            references[i] = reference;
        }

        double[] values = new double[quantities.Length];

        for (int i = 0; i < quantities.Length; ++i)
        {
            if (feature.FindParameter(quantities[i]) is not { } parameter)
            {
                return DatumTranslation.Failed($"'{quantities[i]}' has not been given a value.");
            }

            values[i] = parameter.Value.Value;
        }

        return DatumTranslation.Of(make(feature.Name, references, values));
    }

    private static FeatureSchema Schema(
        string featureType, string label, string description, params FeatureProperty[] properties)
        => FeatureSchema.Create(featureType, label, Category, properties, description);

    private static FeatureProperty Input(string name, string label) => new(
        name,
        label,
        PropertyKind.Reference,
        Description: "Reference geometry, or a face, edge or vertex of a body.");

    private static FeatureProperty Length(string name, string label) => new(
        name, label, PropertyKind.Quantity, Dimension: Dimension.Length);

    private static FeatureProperty Angle(string name, string label) => new(
        name, label, PropertyKind.Quantity, Dimension: Dimension.Angle);

    private static FeatureProperty Fraction(string name, string label) => new(
        name,
        label,
        PropertyKind.Quantity,
        Description: "How far along, from 0 at the edge's start to 1 at its end.",
        Minimum: 0,
        Maximum: 1);
}

/// <summary>What reading a feature back into a datum came to.</summary>
/// <param name="Definition">The datum, when the feature described one.</param>
/// <param name="Reason">Why it did not, in words.</param>
public sealed record DatumTranslation(DatumDefinition? Definition, string? Reason = null)
{
    /// <summary>Gets whether the feature described a datum.</summary>
    public bool IsTranslated => Definition is not null;

    /// <summary>Creates a translation that succeeded.</summary>
    /// <param name="definition">The datum.</param>
    /// <returns>The translation.</returns>
    public static DatumTranslation Of(DatumDefinition definition) => new(definition);

    /// <summary>Creates a translation that failed.</summary>
    /// <param name="reason">Why, in words.</param>
    /// <returns>The translation.</returns>
    public static DatumTranslation Failed(string reason) => new(null, reason);
}
