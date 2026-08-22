namespace OpenMCAD.Kernel;

/// <summary>
/// What an operation's output entity <i>is</i>, in the operation's own terms.
/// </summary>
/// <remarks>
/// <para>
/// This enum is OpenMCAD's invention. No kernel provides it, and it is the single thing that makes
/// topological naming stable and human-legible (ADR-0005). Kernel history maps say only "input
/// entity <i>a</i> produced output entities <i>x</i> and <i>y</i>"; the role says <i>which</i> of
/// them is the side wall and which is the cap. Without that, a name degrades to an ordinal, and an
/// ordinal is exactly the thing that changes when a sketch gains a line.
/// </para>
/// <para>
/// <b>Every operation must assign roles deliberately.</b> PLAN.md 5.1 is explicit: an operation
/// returning <see cref="Unknown"/> is an incomplete implementation and fails review. The value
/// exists so that omission is detectable — not so that it can be used.
/// </para>
/// <para>
/// Values are part of the native ABI and are persisted inside names. <b>Append only; never
/// renumber and never reuse.</b> Changing a value silently repoints every stored name that
/// mentions it.
/// </para>
/// <para>
/// A role is not a provenance. Whether an entity was generated, modified, or created from nothing
/// is recorded separately by <see cref="HistoryMap"/>; the role describes the entity's purpose
/// regardless of how it arose.
/// </para>
/// </remarks>
public enum OperationRole
{
    /// <summary>
    /// Unassigned. Indicates an incomplete operation implementation, never a legitimate result.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// An input entity that came through the operation geometrically unchanged.
    /// </summary>
    /// <remarks>
    /// The overwhelmingly common case in a boolean: most faces of the target are untouched. Naming
    /// them <see cref="Retained"/> rather than leaving them out is what lets a name resolve
    /// straight through an operation that did not affect it.
    /// </remarks>
    Retained = 1,

    /// <summary>An input entity that survived but had its boundary cut.</summary>
    Trimmed = 2,

    // --- Sweeps: extrude, revolve, sweep, loft -------------------------------------------------

    /// <summary>A lateral face swept from a profile edge.</summary>
    SideWall = 10,

    /// <summary>The face capping the start of a sweep, at the profile's original position.</summary>
    StartCap = 11,

    /// <summary>The face capping the end of a sweep.</summary>
    EndCap = 12,

    /// <summary>A lateral edge swept from a profile vertex.</summary>
    SideEdge = 13,

    /// <summary>An edge of the start cap, corresponding to a profile edge.</summary>
    StartProfileEdge = 14,

    /// <summary>An edge of the end cap, corresponding to a profile edge.</summary>
    EndProfileEdge = 15,

    /// <summary>
    /// The seam edge where a full-revolution surface closes on itself.
    /// </summary>
    /// <remarks>
    /// Seams are an artefact of parameterisation rather than of design intent, and their position
    /// is arbitrary. They are named so they can be recognised and deliberately excluded from
    /// selection, not so that users can pick them.
    /// </remarks>
    Seam = 16,

    /// <summary>The degenerate point where a revolved or conical surface converges.</summary>
    Apex = 17,

    // --- Blends: fillet, chamfer ---------------------------------------------------------------

    /// <summary>A face created to blend between the faces adjacent to a filleted edge.</summary>
    BlendFace = 30,

    /// <summary>An edge where a blend face meets a face it was blended onto.</summary>
    BlendEdge = 31,

    /// <summary>A face created where three or more blends meet at a corner.</summary>
    BlendCornerFace = 32,

    /// <summary>A face created by the setback region at the end of a variable blend.</summary>
    SetbackFace = 33,

    // --- Booleans ------------------------------------------------------------------------------

    /// <summary>An edge created where two input bodies intersect.</summary>
    IntersectionEdge = 50,

    /// <summary>A vertex created where intersection curves meet.</summary>
    IntersectionVertex = 51,

    /// <summary>
    /// A face lying on the boundary shared by both operands, where they touched rather than
    /// crossed.
    /// </summary>
    /// <remarks>
    /// Coincident-face booleans are the classic robustness cliff (ADR-0001 names them
    /// specifically). Giving the result its own role means the naming layer can tell a genuine
    /// tangential contact from an ordinary trimmed face.
    /// </remarks>
    CoincidentFace = 52,

    // --- Splits and partitions -----------------------------------------------------------------

    /// <summary>The piece of a split entity on the positive side of the splitting geometry.</summary>
    SplitPositive = 70,

    /// <summary>The piece of a split entity on the negative side of the splitting geometry.</summary>
    SplitNegative = 71,

    // --- Offsets, shells, drafts ---------------------------------------------------------------

    /// <summary>A face produced by offsetting an input face.</summary>
    OffsetFace = 90,

    /// <summary>An inner wall face created by shelling.</summary>
    ShellInnerFace = 91,

    /// <summary>A face left open by shelling, at a removed face.</summary>
    ShellOpeningFace = 92,

    /// <summary>A face tapered by a draft operation.</summary>
    DraftFace = 93,

    // --- Primitives ------------------------------------------------------------------------------

    /// <summary>
    /// A face of a primitive solid, distinguished from its siblings by ordinal.
    /// </summary>
    /// <remarks>
    /// The ordinal must follow a documented canonical order for each primitive — for a box,
    /// minus-X, plus-X, minus-Y, plus-Y, minus-Z, plus-Z — so that the same primitive built twice
    /// yields the same names.
    /// </remarks>
    PrimitiveFace = 110,

    /// <summary>An edge of a primitive solid, distinguished by ordinal in canonical order.</summary>
    PrimitiveEdge = 111,

    /// <summary>A vertex of a primitive solid, distinguished by ordinal in canonical order.</summary>
    PrimitiveVertex = 112,

    // --- Patterns, mirrors, transforms -----------------------------------------------------------

    /// <summary>A copy of a seed entity produced by a pattern.</summary>
    PatternInstance = 130,

    /// <summary>A reflected copy of a seed entity.</summary>
    MirrorImage = 131,

    /// <summary>An entity carried through a rigid transform, unchanged in shape.</summary>
    Transformed = 132,

    // --- Foreign geometry --------------------------------------------------------------------------

    /// <summary>
    /// An entity that arrived from outside the feature tree, via import or a cached B-rep read.
    /// </summary>
    /// <remarks>
    /// Imported entities have no generative provenance to name them by, so the naming layer must
    /// fall back to geometric matching for them (PLAN.md 5.3 tier 2). Marking them explicitly is
    /// what lets it know to do so.
    /// </remarks>
    Imported = 150,
}
