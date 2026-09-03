namespace OpenMCAD.Math;

/// <summary>
/// A 3-D curve, described the way a kernel curve query reports one: an analytic shape plus a
/// parameter range, rather than a sampled approximation.
/// </summary>
/// <remarks>
/// <para>
/// This is the same kind of type as <see cref="Plane"/> — a bare geometric fact, produced by
/// whatever eventually answers a kernel curve query and consumed by the modelling layer above it.
/// Nothing in <c>OpenMCAD.Kernel</c> exposes a curve query yet, so for now this is what a caller
/// hands the sketcher's external-reference machinery directly (<c>SketchExternalReferenceResolver</c>
/// lives in <c>OpenMCAD.Modeling</c>) rather than something either of them derives from a kernel
/// handle themselves.
/// </para>
/// <para>
/// Deliberately not every curve a kernel can produce. §5.6's external references are edges brought
/// into a sketch, and a first cut has to draw the line somewhere; straight and circular are the two
/// analytic kinds common enough to be worth it, and anything else is reported as unsupported by
/// whoever consumes this rather than silently approximated.
/// </para>
/// </remarks>
public abstract record WorldCurve
{
    private WorldCurve()
    {
    }

    /// <summary>A straight segment between two points.</summary>
    /// <param name="Start">Where it begins.</param>
    /// <param name="End">Where it ends.</param>
    public sealed record Line(Vec3d Start, Vec3d End) : WorldCurve;

    /// <summary>A circle, or an arc of one.</summary>
    /// <param name="Centre">Where its centre is.</param>
    /// <param name="Normal">
    /// The circle's plane normal. Need not be unit length, but must be non-degenerate.
    /// </param>
    /// <param name="XDirection">
    /// Where angle zero points — the same role <see cref="Plane.CreateFrame"/>'s <c>xAxis</c> plays
    /// for a bare plane, except this one is not invented: a circle's own parameterisation has to be
    /// carried through, not picked freely, because two curve queries against the same edge must
    /// agree on where its start and end angles sit. Need not be unit length or already perpendicular
    /// to <paramref name="Normal"/>; the same latitude <see cref="Math.Plane.CreateFrame"/> gives an
    /// invented basis is given here to a supplied one.
    /// </param>
    /// <param name="Radius">How big it is.</param>
    /// <param name="StartAngle">
    /// Where the arc begins, in radians from <paramref name="XDirection"/>, anticlockwise about
    /// <paramref name="Normal"/> (right-hand rule).
    /// </param>
    /// <param name="EndAngle">Where it ends, measured the same way.</param>
    public sealed record Circle(
        Vec3d Centre,
        Vec3d Normal,
        Vec3d XDirection,
        double Radius,
        double StartAngle,
        double EndAngle)
        : WorldCurve
    {
        /// <summary>Gets a full circle, with no particular start.</summary>
        /// <param name="centre">Where its centre is.</param>
        /// <param name="normal">The plane normal.</param>
        /// <param name="xDirection">An arbitrary in-plane reference direction.</param>
        /// <param name="radius">How big it is.</param>
        /// <returns>The circle.</returns>
        public static Circle Full(Vec3d centre, Vec3d normal, Vec3d xDirection, double radius)
            => new(centre, normal, xDirection, radius, 0, 2 * System.Math.PI);

        /// <summary>Gets how far the arc sweeps, always positive.</summary>
        /// <remarks>The same modulo convention <c>SketchArc.Sweep</c> uses, for the same reason.</remarks>
        public double Sweep
        {
            get
            {
                double sweep = (EndAngle - StartAngle) % (2 * System.Math.PI);

                return sweep < 0 ? sweep + (2 * System.Math.PI) : sweep;
            }
        }

        /// <summary>Gets whether this covers the whole circle rather than an arc of it.</summary>
        /// <remarks>
        /// Measured against the raw angular span, not <see cref="Sweep"/> — a full turn wraps to
        /// exactly zero under <see cref="Sweep"/>'s modulo, which is indistinguishable from a
        /// zero-length arc reported the same way, and <see cref="Full"/> passes <c>(0, 2*pi)</c>
        /// specifically to be recognised here.
        /// </remarks>
        public bool IsFull
            => System.Math.Abs(EndAngle - StartAngle) >= (2 * System.Math.PI) - Tolerance.AngularResolution;
    }
}
