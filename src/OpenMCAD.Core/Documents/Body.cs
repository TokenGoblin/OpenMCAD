using OpenMCAD.Kernel;

namespace OpenMCAD.Core.Documents;

/// <summary>What kind of thing a body is.</summary>
public enum BodyKind
{
    /// <summary>A closed volume.</summary>
    Solid,

    /// <summary>A surface or shell, with no enclosed volume.</summary>
    Sheet,

    /// <summary>Edges and vertices only.</summary>
    Wire,
}

/// <summary>
/// A geometric result held by a document, produced by one of its features.
/// </summary>
/// <param name="Id">Identifies this body for as long as it keeps its identity.</param>
/// <param name="Owner">The feature that produced it.</param>
/// <param name="Kind">Whether it is solid, sheet or wire.</param>
/// <param name="Shape">Where the geometry lives inside the kernel.</param>
/// <param name="Name">What it is called, if the user has named it.</param>
/// <remarks>
/// <para>
/// <b>The shape is a handle, and it does not survive a save.</b> <see cref="KernelShape"/> is a tag
/// into the running kernel's table, so a body read back from a file has geometry only once the
/// feature that owns it has been rebuilt or its cached geometry loaded. That is why <see cref="Id"/>
/// and <see cref="Shape"/> are separate fields rather than one: identity has to outlive the process
/// and the handle cannot.
/// </para>
/// <para>
/// <b>Owned by exactly one feature.</b> A body is a result, and every result has one producer. When
/// a later feature modifies a body it produces its own, whose identity is decided by the multiplicity
/// policy for that reference (P3-T12) — which may reuse this id or mint a new one, but never leaves
/// two features claiming to own the same one.
/// </para>
/// </remarks>
public sealed record Body(
    BodyId Id,
    FeatureId Owner,
    BodyKind Kind,
    KernelShape Shape,
    string? Name = null)
{
    /// <summary>Gets whether this body currently has geometry behind it.</summary>
    /// <remarks>
    /// False for a body that has been read from a file but not yet rebuilt, and for one whose
    /// producing feature is in error. Callers that are about to hand the shape to the kernel should
    /// ask; the alternative is a stale-tag rejection reported against the operation rather than
    /// against the body that had no geometry.
    /// </remarks>
    public bool HasGeometry => Shape.IsValid;

    /// <summary>The same body with different geometry, as produced by a rebuild.</summary>
    /// <param name="shape">The new shape.</param>
    /// <returns>The body.</returns>
    public Body WithShape(KernelShape shape) => this with { Shape = shape };

    /// <inheritdoc />
    public override string ToString()
        => $"{Kind} body {(Name is null ? Id.ToString() : $"'{Name}'")}";
}
