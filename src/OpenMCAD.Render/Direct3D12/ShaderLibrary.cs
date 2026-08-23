using System.Reflection;

using Vortice.D3DCompiler;
using Vortice.Direct3D;

namespace OpenMCAD.Render.Direct3D12;

/// <summary>Thrown when a shader will not compile.</summary>
public sealed class ShaderCompilationException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What the compiler said.</param>
    public ShaderCompilationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public ShaderCompilationException()
        : base("A shader failed to compile.")
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What the compiler said.</param>
    /// <param name="innerException">The underlying failure.</param>
    public ShaderCompilationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Compiles the shaders that ship inside this assembly (P2-T05).
/// </summary>
/// <remarks>
/// <para>
/// Shaders are embedded as HLSL source and compiled at run time rather than built to bytecode
/// offline. The cost is a few milliseconds once per process; what it buys is that the shader a
/// user is running is the shader in the repository, with no build step that can silently go stale
/// and no compiled artefact to keep in sync with the source beside it.
/// </para>
/// <para>
/// <b>Shader model 5.1 through D3DCompiler, not 6.x through DXC.</b> Every D3D12 device supports
/// 5.1 by definition, including the WARP adapter the tests run on and the oldest hardware this
/// will ever meet, and <c>D3DCompiler_47.dll</c> is part of Windows. DXC would mean carrying a
/// 20 MB redistributable and querying <c>D3D12_FEATURE_SHADER_MODEL</c> before trusting it. None
/// of what these shaders do needs anything 5.1 lacks; when a compute pass eventually does, that
/// pass can bring DXC with it.
/// </para>
/// </remarks>
public static class ShaderLibrary
{
    /// <summary>Gets the vertex shader profile everything here is compiled against.</summary>
    public const string VertexProfile = "vs_5_1";

    /// <summary>Gets the pixel shader profile everything here is compiled against.</summary>
    public const string PixelProfile = "ps_5_1";

    /// <summary>Reads an embedded HLSL file.</summary>
    /// <param name="name">The file name, such as <c>Surface.hlsl</c>.</param>
    /// <returns>Its source.</returns>
    /// <exception cref="InvalidOperationException">There is no such resource.</exception>
    public static string Source(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Assembly assembly = typeof(ShaderLibrary).Assembly;
        string resource = $"OpenMCAD.Render.Direct3D12.Shaders.{name}";

        using Stream? stream = assembly.GetManifestResourceStream(resource);

        if (stream is null)
        {
            // Naming what is actually embedded turns "resource not found" from a guessing game
            // into a one-line diagnosis; the usual cause is a csproj that did not pick the file up.
            throw new InvalidOperationException(
                $"No embedded shader '{resource}'. This assembly carries: "
                + string.Join(", ", assembly.GetManifestResourceNames()));
        }

        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Compiles one entry point out of an embedded shader file.
    /// </summary>
    /// <param name="fileName">The file, such as <c>Surface.hlsl</c>.</param>
    /// <param name="entryPoint">The function to compile, such as <c>VSMain</c>.</param>
    /// <param name="profile">A shader profile, such as <see cref="VertexProfile"/>.</param>
    /// <param name="optimise">
    /// Whether to optimise. Tests compile unoptimised so a failure points at the line it came
    /// from; the application optimises.
    /// </param>
    /// <returns>The bytecode, ready for a pipeline state description.</returns>
    /// <exception cref="ShaderCompilationException">The shader has an error in it.</exception>
    public static ReadOnlyMemory<byte> Compile(
        string fileName, string entryPoint, string profile, bool optimise = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);

        ShaderFlags flags = optimise
            ? ShaderFlags.OptimizationLevel3
            : ShaderFlags.SkipOptimization | ShaderFlags.Debug;

        // Row-major packing is the default for anything not declared otherwise, but saying so
        // explicitly means the constant upload cannot be silently transposed by a compiler
        // default changing underneath it.
        flags |= ShaderFlags.PackMatrixRowMajor;

        SharpGen.Runtime.Result result = Compiler.Compile(
            Source(fileName),

            // Vortice types these as non-nullable, but the D3DCompile beneath takes null for "no
            // preprocessor defines" and "no #include handler", which is what these shaders want.
            defines: null!,
            include: null!,
            entryPoint,
            fileName,
            profile,
            flags,
            out Blob? bytecode,
            out Blob? errors);

        using (bytecode)
        using (errors)
        {
            if (result.Failure || bytecode is null)
            {
                string detail = errors is null ? "no detail" : errors.AsString().Trim();

                throw new ShaderCompilationException(
                    $"{fileName}:{entryPoint} ({profile}) failed to compile: {detail}");
            }

            // Copied out before the blob is released. Returning a view over native memory that is
            // about to be freed is a use-after-free that would surface much later, inside the
            // driver, as a corrupt pipeline state.
            return bytecode.AsBytes().ToArray();
        }
    }
}
