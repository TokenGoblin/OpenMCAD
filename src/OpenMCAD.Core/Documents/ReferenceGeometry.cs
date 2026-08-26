using OpenMCAD.Math;

namespace OpenMCAD.Core.Documents;

/// <summary>
/// Geometry a document carries to be referred to, rather than to be seen as part of the part:
/// datum planes, axes, points and coordinate systems.
/// </summary>
/// <param name="Owner">
/// The feature that created it, or <see cref="FeatureId.None"/> for the origin geometry every
/// document starts with.
/// </param>
/// <param name="Name">What it is called.</param>
/// <remarks>
/// <para>
/// <b>Why this is a closed hierarchy rather than one record with optional fields.</b> A plane needs
/// an origin and a normal, an axis an origin and a direction, a point only an origin, and a
/// coordinate system an origin and two independent axes. Folding those into a single shape means
/// nullable vectors that are meaningful for some kinds and meaningless for others, and every reader
/// then has to know which combination goes with which kind — a rule the compiler cannot enforce and
/// which is therefore broken eventually. Separate cases put that knowledge in the type.
/// </para>
/// <para>
/// <b>Reference geometry is not a body.</b> It has no volume, no mass and no faces to select, and
/// it does not belong in a mass property calculation or an exported solid. Keeping it out of
/// <see cref="Body"/> is what stops a datum plane from being exported as a zero-thickness sheet.
/// </para>
/// </remarks>
public abstract record ReferenceGeometry(FeatureId Owner, string Name)
{
    /// <summary>The three planes and the origin every document begins with.</summary>
    /// <returns>The origin geometry, owned by no feature.</returns>
    /// <remarks>
    /// Present from the moment a document exists, because a first sketch needs something to be
    /// drawn on and a document whose first action must be "create somewhere to work" is a document
    /// that starts by asking the user to do the modeller's job.
    /// </remarks>
    public static ReferenceGeometry[] StandardDatums() =>
    [
        new Point(FeatureId.None, "Origin", Vec3d.Zero),
        new Plane(FeatureId.None, "Front", Vec3d.Zero, Vec3d.UnitY),
        new Plane(FeatureId.None, "Top", Vec3d.Zero, Vec3d.UnitZ),
        new Plane(FeatureId.None, "Right", Vec3d.Zero, Vec3d.UnitX),
    ];

    /// <summary>An infinite plane, for sketching on and measuring from.</summary>
    /// <param name="Owner">The feature that created it.</param>
    /// <param name="Name">What it is called.</param>
    /// <param name="Origin">A point on the plane.</param>
    /// <param name="Normal">The plane's normal. Need not be unit length.</param>
    public sealed record Plane(FeatureId Owner, string Name, Vec3d Origin, Vec3d Normal)
        : ReferenceGeometry(Owner, Name);

    /// <summary>An infinite line, for revolving about and patterning along.</summary>
    /// <param name="Owner">The feature that created it.</param>
    /// <param name="Name">What it is called.</param>
    /// <param name="Origin">A point on the axis.</param>
    /// <param name="Direction">Which way it runs. Need not be unit length.</param>
    public sealed record Axis(FeatureId Owner, string Name, Vec3d Origin, Vec3d Direction)
        : ReferenceGeometry(Owner, Name);

    /// <summary>A single located point.</summary>
    /// <param name="Owner">The feature that created it.</param>
    /// <param name="Name">What it is called.</param>
    /// <param name="Position">Where it is.</param>
    public sealed record Point(FeatureId Owner, string Name, Vec3d Position)
        : ReferenceGeometry(Owner, Name);

    /// <summary>A located, oriented frame.</summary>
    /// <param name="Owner">The feature that created it.</param>
    /// <param name="Name">What it is called.</param>
    /// <param name="Origin">Where the frame sits.</param>
    /// <param name="XAxis">Its first axis.</param>
    /// <param name="ZAxis">
    /// Its third axis. The second is derived from these two rather than stored, so the frame cannot
    /// be recorded in a state where its three axes disagree about handedness.
    /// </param>
    public sealed record CoordinateSystem(
        FeatureId Owner, string Name, Vec3d Origin, Vec3d XAxis, Vec3d ZAxis)
        : ReferenceGeometry(Owner, Name)
    {
        /// <summary>Gets the second axis, derived so it is always perpendicular to the other two.</summary>
        public Vec3d YAxis => Vec3d.Cross(ZAxis, XAxis);
    }
}
