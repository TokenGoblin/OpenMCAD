namespace OpenMCAD.Api;

/// <summary>
/// The version of the plugin API this build presents.
/// </summary>
/// <remarks>
/// <para>
/// PLAN.md 5.12: <c>OpenMCAD.Api</c> carries a semver contract independent of the application's own
/// version. A plugin is written against an API version, not against a release, and the two move at
/// different speeds — a year of application releases may not change this surface at all.
/// </para>
/// <para>
/// The rules, which the checked-in baseline in <c>PublicAPI.Unshipped.txt</c> exists to enforce:
/// </para>
/// <list type="bullet">
///   <item><description>Adding a member raises the minor version.</description></item>
///   <item><description>
///     Removing or changing the meaning of a member raises the major version, and there is no
///     shortcut. Every plugin compiled against the old surface breaks.
///   </description></item>
///   <item><description>
///     A fix that does not touch the surface raises the patch version, or nothing at all.
///   </description></item>
/// </list>
/// <para>
/// <b>Zero-major, deliberately.</b> Semver treats 0.x as unstable, and this surface is: almost
/// nothing it is eventually meant to expose exists yet. Claiming 1.0 before the document model
/// exists would be promising compatibility that cannot be kept, and the promise is the whole point
/// of the number.
/// </para>
/// </remarks>
public static class ApiVersion
{
    /// <summary>Gets the major version. A change here breaks existing plugins.</summary>
    public static int Major => 0;

    /// <summary>Gets the minor version. Raised when the surface gains something.</summary>
    public static int Minor => 1;

    /// <summary>Gets the patch version.</summary>
    public static int Patch => 0;

    /// <summary>Gets the version as <c>major.minor.patch</c>.</summary>
    public static string Value => $"{Major}.{Minor}.{Patch}";

    /// <summary>
    /// Determines whether a plugin built against <paramref name="required"/> can run here.
    /// </summary>
    /// <param name="required">The API version the plugin was built against.</param>
    /// <returns><see langword="true"/> if this build satisfies it.</returns>
    /// <remarks>
    /// <para>
    /// The usual semver rule: the major version must match exactly, and the minor version must be
    /// at least what the plugin asked for. A plugin built against 1.2 runs on 1.5 because
    /// everything it uses is still there; it does not run on 2.0, because something it uses may
    /// not be; and it does not run on 1.1, because it may use something 1.1 lacks.
    /// </para>
    /// <para>
    /// While the major version is zero, the minor version carries the breaking changes — that is
    /// what 0.x means — so each one is treated as its own major.
    /// </para>
    /// </remarks>
    public static bool Supports(Version required)
    {
        ArgumentNullException.ThrowIfNull(required);

        if (required.Major != Major)
        {
            return false;
        }

        return Major == 0
            ? required.Minor == Minor
            : required.Minor <= Minor;
    }
}
