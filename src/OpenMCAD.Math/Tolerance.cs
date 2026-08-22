namespace OpenMCAD.Math;

/// <summary>
/// Tolerance constants and comparison helpers for OpenMCAD geometry.
/// </summary>
/// <remarks>
/// <para>
/// All linear values in OpenMCAD are stored in <b>metres</b> and all angular values in
/// <b>radians</b> (ADR-0013). These constants are expressed in those units and must not be
/// reinterpreted for display units.
/// </para>
/// <para>
/// Never compare geometry with <c>==</c>. Floating-point geometry comparison always needs an
/// explicit tolerance, and the tolerance that is correct depends on whether the quantity is a
/// coordinate, a direction, a parameter, or an angle. Pick the right constant deliberately.
/// </para>
/// </remarks>
public static class Tolerance
{
    /// <summary>
    /// Smallest linear distance the system will ever treat as non-zero, in metres (1 nm).
    /// </summary>
    /// <remarks>
    /// This is a <i>resolution</i> floor, not a modelling tolerance. Two points closer than this
    /// are the same point for every purpose. Use <see cref="Linear"/> for modelling comparisons.
    /// </remarks>
    public const double LinearResolution = 1e-9;

    /// <summary>
    /// Default linear modelling tolerance, in metres (0.1 micrometre).
    /// </summary>
    /// <remarks>
    /// This is the default confusion distance for coincidence tests in modelling code. Kernel
    /// shapes carry their own per-entity tolerances which may be looser; where a shape tolerance
    /// is available, prefer it over this constant.
    /// </remarks>
    public const double Linear = 1e-7;

    /// <summary>
    /// Smallest angle the system will ever treat as non-zero, in radians.
    /// </summary>
    public const double AngularResolution = 1e-12;

    /// <summary>
    /// Default angular tolerance, in radians (roughly 5.7e-8 degrees).
    /// </summary>
    /// <remarks>
    /// Used for parallelism, perpendicularity, and tangency tests between unit directions.
    /// </remarks>
    public const double Angular = 1e-9;

    /// <summary>
    /// Tolerance for curve and surface parameter-space comparisons (dimensionless).
    /// </summary>
    public const double Parametric = 1e-9;

    /// <summary>
    /// Default chordal deviation used when tessellating for display, in metres.
    /// </summary>
    /// <remarks>
    /// Display tessellation is adaptive and scales with body size; this is only the floor.
    /// </remarks>
    public const double DisplayChordal = 1e-5;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="value"/> is within
    /// <paramref name="tolerance"/> of zero.
    /// </summary>
    /// <param name="value">The value to test.</param>
    /// <param name="tolerance">The non-negative tolerance to test against.</param>
    public static bool IsZero(double value, double tolerance = Linear)
        => System.Math.Abs(value) <= tolerance;

    /// <summary>
    /// Returns <see langword="true"/> when two values differ by no more than
    /// <paramref name="tolerance"/>.
    /// </summary>
    /// <param name="a">The first value.</param>
    /// <param name="b">The second value.</param>
    /// <param name="tolerance">The non-negative tolerance to test against.</param>
    public static bool AreEqual(double a, double b, double tolerance = Linear)
        => System.Math.Abs(a - b) <= tolerance;

    /// <summary>
    /// Returns <see langword="true"/> when two values differ by no more than
    /// <paramref name="relativeTolerance"/> scaled by their magnitude.
    /// </summary>
    /// <param name="a">The first value.</param>
    /// <param name="b">The second value.</param>
    /// <param name="relativeTolerance">The non-negative relative tolerance.</param>
    /// <remarks>
    /// Use this for quantities whose magnitude varies widely — volumes, moments of inertia — where
    /// an absolute tolerance is meaningless. Falls back to an absolute comparison near zero.
    /// </remarks>
    public static bool AreRelativelyEqual(double a, double b, double relativeTolerance = 1e-9)
    {
        double diff = System.Math.Abs(a - b);
        double scale = System.Math.Max(System.Math.Abs(a), System.Math.Abs(b));
        return scale < 1.0 ? diff <= relativeTolerance : diff <= relativeTolerance * scale;
    }

    /// <summary>
    /// Clamps <paramref name="value"/> into the inclusive range
    /// [<paramref name="min"/>, <paramref name="max"/>].
    /// </summary>
    /// <param name="value">The value to clamp.</param>
    /// <param name="min">The lower bound.</param>
    /// <param name="max">The upper bound.</param>
    public static double Clamp(double value, double min, double max)
        => value < min ? min : value > max ? max : value;
}
