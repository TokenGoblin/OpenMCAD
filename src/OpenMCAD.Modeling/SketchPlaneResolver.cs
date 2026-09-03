using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;
using OpenMCAD.Kernel;
using OpenMCAD.Math;

namespace OpenMCAD.Modeling;

/// <summary>How resolving a <see cref="SketchPlaneReference"/> came to.</summary>
public enum SketchPlaneResolutionOutcome
{
    /// <summary>The reference names a plane, and it was found.</summary>
    Resolved,

    /// <summary>
    /// Nothing answers to the reference — the name does not exist, names something of the wrong
    /// kind, or (for a face) could not be traced through the model's history at all.
    /// </summary>
    NotFound,

    /// <summary>
    /// A face reference traced to more than one candidate and nothing said which was meant.
    /// </summary>
    /// <remarks>
    /// Bubbled up from <see cref="NameResolutionOutcome.Ambiguous"/>. A sketch plane always wants
    /// exactly one face (see <see cref="SketchPlaneReference"/>), so unlike
    /// <see cref="EntityReference"/>'s <see cref="MultiplicityPolicy"/> there is no policy here
    /// that could turn a split into an answer — it is always a refusal.
    /// </remarks>
    Ambiguous,

    /// <summary>The reference resolved to something that is not usable as a sketch plane.</summary>
    /// <remarks>
    /// A face that is not flat, or reference geometry with a degenerate normal or axis set. Told
    /// apart from <see cref="NotFound"/> because the two are different repairs: a missing reference
    /// is fixed by pointing the sketch somewhere else, but a curved face found exactly as named is
    /// not a naming failure at all.
    /// </remarks>
    NotPlanar,
}

/// <summary>What resolving a <see cref="SketchPlaneReference"/> came to.</summary>
/// <param name="Outcome">How it turned out.</param>
/// <param name="Plane">The resolved plane, when <paramref name="Outcome"/> is <see cref="SketchPlaneResolutionOutcome.Resolved"/>.</param>
/// <param name="Reason">Why, in words, when it could not be resolved.</param>
public sealed record SketchPlaneResolution(
    SketchPlaneResolutionOutcome Outcome,
    SketchPlane? Plane = null,
    string? Reason = null)
{
    /// <summary>Gets whether the reference resolved to a usable plane.</summary>
    public bool IsResolved => Outcome == SketchPlaneResolutionOutcome.Resolved;

    /// <summary>Creates a resolution that found a plane.</summary>
    /// <param name="plane">The plane.</param>
    /// <returns>The resolution.</returns>
    public static SketchPlaneResolution Found(SketchPlane plane) => new(
        SketchPlaneResolutionOutcome.Resolved, plane);

    /// <summary>Creates a resolution that failed.</summary>
    /// <param name="outcome">How it failed. Must not be <see cref="SketchPlaneResolutionOutcome.Resolved"/>.</param>
    /// <param name="reason">Why, in words.</param>
    /// <returns>The resolution.</returns>
    public static SketchPlaneResolution Failed(SketchPlaneResolutionOutcome outcome, string reason)
        => new(outcome, null, reason);
}

/// <summary>
/// Resolves a <see cref="SketchPlaneReference"/> against a document, into the plane a sketch
/// should actually be drawn on.
/// </summary>
/// <remarks>
/// <para>
/// A static class rather than an instance, unlike <c>NameResolver</c>: there is no multi-call state
/// worth bundling here (no history to hold, no cached tiers), so each call simply takes what it
/// needs. The two optional parameters are exactly the two things a caller wiring a real document up
/// might not always have — a plane reference on a document with no sketches on faces yet needs
/// neither, and the batch tools P3-T10's <c>NameResolver</c> already tolerates a null geometric tier
/// for are exactly the callers that will not have them either.
/// </para>
/// <para>
/// <b>Why a face's plane comes from a separate delegate rather than from <c>GeoHint</c>.</b> The
/// naming layer already carries evidence about a face — <c>GeoHint.Centroid</c> and
/// <c>GeoHint.Direction</c> — but deliberately in coordinates local to the feature that produced
/// it, so that moving the whole part does not stop its faces being recognised. A sketch plane needs
/// the opposite: genuine world-space coordinates to place geometry at. Reusing the hint would place
/// a sketch at the wrong point the first time the feature that made its face moved. <see cref="Plane"/>
/// is the right shape for the answer instead — a world-space plane is exactly what a planar face
/// query has to report — and nothing in <see cref="OpenMCAD.Kernel"/> exposes one yet, so this takes
/// it as a delegate the same way a solver's diagnosis takes evidence rather than reaching into a
/// kernel it does not have a reference to.
/// </para>
/// </remarks>
public static class SketchPlaneResolver
{
    /// <summary>Resolves a sketch plane reference.</summary>
    /// <param name="reference">The reference.</param>
    /// <param name="document">The document to resolve reference geometry against.</param>
    /// <param name="consumer">The feature holding the reference, for face resolution's history search.</param>
    /// <param name="faceResolver">
    /// How to resolve a face reference through the naming tiers (§5.3), or <see langword="null"/> if
    /// this configuration cannot — any <see cref="SketchPlaneReference.OnFace"/> then fails with
    /// <see cref="SketchPlaneResolutionOutcome.NotFound"/> rather than throwing.
    /// </param>
    /// <param name="planeOf">
    /// How to get the world-space plane a resolved face lies on, or <see langword="null"/> to the
    /// same effect. Returning <see langword="null"/> for one face is a legitimate answer — the face
    /// is not flat — and resolves to <see cref="SketchPlaneResolutionOutcome.NotPlanar"/>.
    /// </param>
    /// <returns>The resolution.</returns>
    public static SketchPlaneResolution Resolve(
        SketchPlaneReference reference,
        Document document,
        FeatureId consumer,
        NameResolver? faceResolver = null,
        Func<SubEntity, Plane?>? planeOf = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(document);

        return reference switch
        {
            SketchPlaneReference.OnDatumPlane onPlane => ResolveDatum(document, onPlane),
            SketchPlaneReference.OnCoordinateSystem onCoordinateSystem
                => ResolveCoordinateSystem(document, onCoordinateSystem),
            SketchPlaneReference.OnFace onFace
                => ResolveFace(onFace, consumer, faceResolver, planeOf),
            _ => throw new ArgumentOutOfRangeException(
                nameof(reference), reference, "Unknown sketch plane reference kind."),
        };
    }

    private static SketchPlaneResolution ResolveDatum(
        Document document, SketchPlaneReference.OnDatumPlane reference)
    {
        ReferenceGeometry? found = document.FindReference(reference.Owner, reference.Name);

        if (found is null)
        {
            return NotFound(reference.Name);
        }

        if (found is not ReferenceGeometry.Plane plane)
        {
            return WrongKind(reference.Name, "a plane", found);
        }

        try
        {
            return SketchPlaneResolution.Found(SketchPlane.FromNormal(plane.Origin, plane.Normal));
        }
        catch (InvalidOperationException)
        {
            return SketchPlaneResolution.Failed(
                SketchPlaneResolutionOutcome.NotPlanar,
                $"'{reference.Name}' has a degenerate normal and cannot be sketched on.");
        }
    }

    private static SketchPlaneResolution ResolveCoordinateSystem(
        Document document, SketchPlaneReference.OnCoordinateSystem reference)
    {
        ReferenceGeometry? found = document.FindReference(reference.Owner, reference.Name);

        if (found is null)
        {
            return NotFound(reference.Name);
        }

        if (found is not ReferenceGeometry.CoordinateSystem coordinateSystem)
        {
            return WrongKind(reference.Name, "a coordinate system", found);
        }

        try
        {
            return SketchPlaneResolution.Found(SketchPlane.FromFrame(
                coordinateSystem.Origin, coordinateSystem.XAxis, coordinateSystem.ZAxis));
        }
        catch (InvalidOperationException)
        {
            return SketchPlaneResolution.Failed(
                SketchPlaneResolutionOutcome.NotPlanar,
                $"'{reference.Name}' has degenerate axes and cannot be sketched on.");
        }
    }

    private static SketchPlaneResolution ResolveFace(
        SketchPlaneReference.OnFace reference,
        FeatureId consumer,
        NameResolver? faceResolver,
        Func<SubEntity, Plane?>? planeOf)
    {
        if (faceResolver is null)
        {
            return SketchPlaneResolution.Failed(
                SketchPlaneResolutionOutcome.NotFound,
                "No way to resolve a face reference is available in this configuration.");
        }

        NameResolution resolution = faceResolver.Resolve(reference.Face, consumer);

        if (resolution.Outcome == NameResolutionOutcome.Ambiguous)
        {
            return SketchPlaneResolution.Failed(
                SketchPlaneResolutionOutcome.Ambiguous,
                resolution.Reason ?? "More than one face answers to this reference.");
        }

        if (!resolution.IsResolved)
        {
            return SketchPlaneResolution.Failed(
                SketchPlaneResolutionOutcome.NotFound,
                resolution.Reason ?? "The referenced face could not be resolved.");
        }

        if (resolution.Entity.Kind != SubEntityKind.Face)
        {
            // Naming a persistent name that turns out to resolve to an edge or a vertex is a
            // programming mistake by whoever built the reference, not a fact about the model, but
            // it is still reported as data rather than thrown -- the caller is a rebuild, and a
            // rebuild that throws on one bad reference takes the whole document with it (§5.4).
            return SketchPlaneResolution.Failed(
                SketchPlaneResolutionOutcome.NotPlanar,
                "The reference does not name a face.");
        }

        Plane? plane = planeOf?.Invoke(resolution.Entity);

        return plane is { } found
            ? SketchPlaneResolution.Found(SketchPlane.FromPlane(found))
            : SketchPlaneResolution.Failed(
                SketchPlaneResolutionOutcome.NotPlanar, "The referenced face is not flat.");
    }

    private static SketchPlaneResolution NotFound(string name) => SketchPlaneResolution.Failed(
        SketchPlaneResolutionOutcome.NotFound, $"There is no reference geometry named '{name}'.");

    private static SketchPlaneResolution WrongKind(string name, string wanted, ReferenceGeometry found)
        => SketchPlaneResolution.Failed(
            SketchPlaneResolutionOutcome.NotFound,
            $"'{name}' is {DescribeKind(found)}, not {wanted}.");

    private static string DescribeKind(ReferenceGeometry geometry) => geometry switch
    {
        ReferenceGeometry.Plane => "a plane",
        ReferenceGeometry.Axis => "an axis",
        ReferenceGeometry.Point => "a point",
        ReferenceGeometry.CoordinateSystem => "a coordinate system",
        _ => "something this build does not recognise",
    };
}
