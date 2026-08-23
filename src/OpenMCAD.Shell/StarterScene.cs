using Microsoft.Extensions.Logging;

using OpenMCAD.Kernel;
using OpenMCAD.Kernel.Operations;
using OpenMCAD.Math;
using OpenMCAD.Render;

namespace OpenMCAD.Shell;

/// <summary>
/// Builds the scene the viewport shows when the application opens.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scaffolding.</b> A new document should open empty, and will once the document model exists
/// (P3-T04) — at which point this goes away and the viewport draws whatever the rebuild produced.
/// Until then the alternative is an empty grey rectangle, which is indistinguishable from a broken
/// renderer and tells nobody whether the kernel, the tessellation and the pass actually meet.
/// </para>
/// <para>
/// It goes through the real kernel rather than a hand-built mesh, deliberately. A hard-coded cube
/// would prove the pass draws triangles, which the tests already establish on WARP; what is not
/// otherwise covered anywhere is that OCCT tessellation, the snapshot conversion and the GPU
/// buffers agree with one another on real hardware.
/// </para>
/// </remarks>
public static class StarterScene
{
    /// <summary>
    /// Creates a few solids, tessellates them and converts them for display.
    /// </summary>
    /// <param name="kernel">The kernel to build with.</param>
    /// <param name="logger">Where to report what happened.</param>
    /// <param name="cancellationToken">Abandons the work if the application is closing.</param>
    /// <returns>
    /// The scene, or <see cref="DisplaySnapshot.Empty"/> if the kernel could not produce one.
    /// </returns>
    /// <remarks>
    /// Never throws for a geometry failure. Start-up must not depend on this succeeding: a kernel
    /// that cannot make a box is a serious problem, but showing an empty viewport and a log line
    /// beats refusing to open the application.
    /// </remarks>
    public static async Task<DisplaySnapshot> BuildAsync(
        IGeometryKernel kernel,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            SnapshotBuilder builder = new();
            int added = 0;

            // Two solids side by side, one faceted and one curved. A single box would leave the
            // curved-surface normals untested, and a single body would leave the per-body draw
            // loop and the depth test between bodies untested.
            IOperationDefinition[] definitions =
            [
                new BoxDefinition(0.12, 0.08, 0.04, Transform.FromTranslation(new Vec3d(-0.08, 0, 0))),
                new CylinderDefinition(0.035, 0.09, Transform.FromTranslation(new Vec3d(0.07, 0, -0.02))),
            ];

            foreach (IOperationDefinition definition in definitions)
            {
                OperationResult result = await Create(kernel, definition, cancellationToken)
                    .ConfigureAwait(false);

                if (!result.TryGetShape(out KernelShapeHandle? shape, out _))
                {
                    logger.LogWarning(
                        "The starter {Solid} could not be created: {Why}",
                        definition.OperationName,
                        result.Describe());

                    continue;
                }

                using (shape)
                {
                    KernelResult<MeshBuffer> mesh = await kernel
                        .TriangulateAsync(shape.Shape, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);

                    if (mesh is not KernelResult<MeshBuffer>.Success success)
                    {
                        logger.LogWarning(
                            "The starter {Solid} could not be tessellated", definition.OperationName);

                        continue;
                    }

                    builder.Add(success.Result);
                    added++;
                }
            }

            if (added == 0)
            {
                return DisplaySnapshot.Empty;
            }

            DisplaySnapshot snapshot = builder.Build(1);

            logger.LogInformation(
                "Starter scene: {Bodies} bodies, {Triangles} triangles, extent {Extent:0.###} m",
                snapshot.Bodies.Length,
                snapshot.TriangleCount,
                snapshot.Bounds.DiagonalLength);

            return snapshot;
        }
        catch (OperationCanceledException)
        {
            return DisplaySnapshot.Empty;
        }
        catch (Exception exception)
        {
            // Deliberately broad. This runs during start-up, and nothing it can fail at is worth
            // preventing the application from opening.
            logger.LogError(exception, "The starter scene could not be built");
            return DisplaySnapshot.Empty;
        }
    }

    /// <summary>Calls whichever primitive a definition names.</summary>
    /// <remarks>
    /// A switch rather than a table of pre-made tasks: a <see cref="ValueTask"/> may only be
    /// consumed once, so building an array of them and awaiting later is a bug even when it
    /// happens to work.
    /// </remarks>
    private static ValueTask<OperationResult> Create(
        IGeometryKernel kernel, IOperationDefinition definition, CancellationToken cancellationToken)
        => definition switch
        {
            BoxDefinition box => kernel.CreateBoxAsync(box, cancellationToken: cancellationToken),

            CylinderDefinition cylinder
                => kernel.CreateCylinderAsync(cylinder, cancellationToken: cancellationToken),

            _ => throw new NotSupportedException(
                $"The starter scene does not know how to create a {definition.OperationName}."),
        };
}
