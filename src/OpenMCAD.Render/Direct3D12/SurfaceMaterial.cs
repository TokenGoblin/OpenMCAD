using System.Numerics;
using System.Runtime.InteropServices;

using Vortice.Mathematics;

namespace OpenMCAD.Render.Direct3D12;

/// <summary>How a shaded surface responds to the light (P2-T12).</summary>
/// <param name="Ambient">
/// How much of the surrounding fill a surface picks up, at its brightest.
/// </param>
/// <param name="Diffuse">How much of the key light a surface facing it returns.</param>
/// <param name="Specular">How bright the highlight is.</param>
/// <param name="Gloss">
/// How tight the highlight is. Larger is tighter: an exponent in the Blinn-Phong sense, so
/// doubling it roughly halves the highlight's width rather than its brightness.
/// </param>
/// <remarks>
/// <para>
/// These were literals in the shader until this became a type. That is the difference between a
/// default and an accident: a number written once inside an expression cannot be justified, tested
/// or overridden, and nobody can tell whether it was chosen or simply typed.
/// </para>
/// <para>
/// <b>The energy rule.</b> The base colour multiplies the ambient and diffuse terms, and the
/// highlight is added afterwards. So as long as <see cref="Ambient"/> plus <see cref="Diffuse"/>
/// comes to no more than one, no surface is ever drawn brighter than its own colour, and a white
/// part renders as white rather than as something whiter. Break that rule and light faces stop
/// being distinguishable from one another: they all clamp to the same saturated value and the
/// shading that was carrying the shape disappears, which is worst on exactly the pale greys that
/// mechanical parts are usually shown in.
/// </para>
/// </remarks>
public readonly record struct SurfaceMaterial(
    float Ambient, float Diffuse, float Specular, float Gloss)
{
    /// <summary>Gets the default: a matte, slightly glossy surface for engineering geometry.</summary>
    /// <remarks>
    /// Ambient and diffuse total exactly one, so the shading uses the whole range available to it
    /// and none of it beyond. The highlight is deliberately weak and fairly tight — enough to say
    /// which way a face is curving, not enough to look like polished plastic, which would misread
    /// a machined finish and would compete with the selection colours laid over it.
    /// </remarks>
    public static SurfaceMaterial Default => new(0.30f, 0.70f, 0.16f, 48.0f);

    /// <summary>Gets whether the shading can exceed the surface's own colour.</summary>
    /// <remarks>
    /// The highlight is excluded deliberately. A specular highlight brighter than the surface is
    /// what a highlight is; a diffuse term brighter than the surface is a mistake.
    /// </remarks>
    public bool IsWithinEnergyBudget => Ambient + Diffuse <= 1.0f + 1e-6f;
}

/// <summary>What is pushed inline for each body: its colour and the material.</summary>
/// <remarks>
/// <para>Matches <c>BodyConstants</c> in <c>Surface.hlsl</c>.</para>
/// <para>
/// Shared by the shaded pass and the transparent one because they read the same block from the
/// same register in the same shader file. Declaring it twice would work right up until one of them
/// gained a field, at which point the other would push too few root constants and read whatever
/// was left in the slots — silently, since a shader that reads past what was pushed is not an
/// error.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct BodyConstants
{
    /// <summary>The body's own colour. Its alpha is the transparency.</summary>
    public Color4 Colour;

    /// <summary>Ambient, diffuse, specular and gloss, in that order.</summary>
    public Vector4 Material;

    /// <summary>Gets how many root constants this occupies.</summary>
    /// <remarks>
    /// Derived rather than written as eight. The root signature declares a count, the shader
    /// declares a layout and this declares a struct; of the three, only the root signature can be
    /// told what to expect, so it is told from here rather than from a literal.
    /// </remarks>
    public static uint DwordCount => (uint)(Marshal.SizeOf<BodyConstants>() / sizeof(float));

    /// <summary>Builds the block for one body.</summary>
    /// <param name="colour">The body's colour.</param>
    /// <param name="material">How it responds to the light.</param>
    /// <returns>The constants to push.</returns>
    public static BodyConstants For(Color4 colour, SurfaceMaterial material) => new()
    {
        Colour = colour,
        Material = new Vector4(
            material.Ambient, material.Diffuse, material.Specular, material.Gloss),
    };
}
