namespace OpenMCAD.Render;

/// <summary>
/// Turns a laid-out viewport size into a back-buffer size (P2-T02).
/// </summary>
/// <remarks>
/// <para>
/// Lives here rather than in the WPF host because none of it is about WPF: it is arithmetic on a
/// logical size and a scale factor. Keeping it in a plain <c>net10.0</c> assembly is what makes it
/// testable at all — ADR-0014 confines the Windows-specific target framework to the shell, and a
/// test project cannot follow it there.
/// </para>
/// </remarks>
public static class ViewportScaling
{
    /// <summary>
    /// Converts a device-independent size to physical pixels.
    /// </summary>
    /// <param name="width">Logical width, at 96 units to the inch.</param>
    /// <param name="height">Logical height.</param>
    /// <param name="scaleX">Horizontal scale, 1.0 at 96 DPI and 1.5 at 150%.</param>
    /// <param name="scaleY">Vertical scale.</param>
    /// <returns>The size in physical pixels, never smaller than one in either direction.</returns>
    /// <remarks>
    /// <para>
    /// The whole of per-monitor DPI, arithmetically. WPF lays out in ninety-sixths of an inch and a
    /// swapchain is sized in pixels, so on a 150% display an 800-unit viewport needs a 1200-pixel
    /// buffer. Sizing it at 800 and letting the compositor stretch is the usual mistake, and it
    /// gives a viewport visibly softer than the rest of the window — worse on a CAD drawing than on
    /// a photograph, because it is thin lines that suffer most.
    /// </para>
    /// <para>
    /// Rounded up. A fractional scale otherwise leaves a sub-pixel remainder and an unpainted seam
    /// at the right or bottom edge.
    /// </para>
    /// <para>
    /// Clamped to at least one pixel, because a minimised window lays out at zero and DXGI refuses
    /// a zero-sized buffer. A degenerate scale is treated as 1.0 for the same reason: it can only
    /// arrive from a monitor query that failed, and guessing 100% is better than failing to create
    /// a viewport.
    /// </para>
    /// </remarks>
    public static (int Width, int Height) ToPhysicalPixels(
        double width, double height, double scaleX, double scaleY)
    {
        double x = scaleX > 0 && double.IsFinite(scaleX) ? scaleX : 1.0;
        double y = scaleY > 0 && double.IsFinite(scaleY) ? scaleY : 1.0;

        double pixelWidth = double.IsFinite(width) ? System.Math.Ceiling(width * x) : 1.0;
        double pixelHeight = double.IsFinite(height) ? System.Math.Ceiling(height * y) : 1.0;

        return (
            (int)System.Math.Clamp(pixelWidth, 1.0, int.MaxValue),
            (int)System.Math.Clamp(pixelHeight, 1.0, int.MaxValue));
    }
}
