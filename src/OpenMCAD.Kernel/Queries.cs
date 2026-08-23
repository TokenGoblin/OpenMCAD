using System.Collections.Immutable;
using OpenMCAD.Math;

namespace OpenMCAD.Kernel;

/// <summary>How trustworthy a computed quantity is.</summary>
public enum ResultAccuracy
{
    /// <summary>Computed by exact integration over the analytic geometry.</summary>
    Exact = 0,

    /// <summary>
    /// Computed by approximation — typically integration over a tessellation — and subject to a
    /// stated relative tolerance.
    /// </summary>
    Approximate = 1,
}

/// <summary>
/// The mass properties of a shape, in SI units.
/// </summary>
/// <param name="Volume">Volume in cubic metres.</param>
/// <param name="SurfaceArea">Total surface area in square metres.</param>
/// <param name="Centroid">Centre of volume, in world coordinates and metres.</param>
/// <param name="Density">The density used, in kilograms per cubic metre.</param>
/// <param name="Inertia">The inertia tensor about the centroid.</param>
/// <param name="Accuracy">Whether these figures are exact or approximate.</param>
/// <param name="RelativeTolerance">
/// For approximate results, the relative error bound. Zero when <paramref name="Accuracy"/> is
/// <see cref="ResultAccuracy.Exact"/>.
/// </param>
/// <remarks>
/// SI throughout, per ADR-0013. A mass property that arrives in millimetres because that is what
/// the user is looking at is the origin of an entire genus of CAD bugs; conversion happens at the
/// display boundary and nowhere else.
/// </remarks>
public readonly record struct MassProperties(
    double Volume,
    double SurfaceArea,
    Vec3d Centroid,
    double Density,
    InertiaTensor Inertia,
    ResultAccuracy Accuracy = ResultAccuracy.Exact,
    double RelativeTolerance = 0.0)
{
    /// <summary>Gets the mass in kilograms.</summary>
    public double Mass => Volume * Density;
}

/// <summary>
/// A symmetric inertia tensor about the centroid, in kilogram square metres.
/// </summary>
/// <param name="Ixx">Moment about the X axis.</param>
/// <param name="Iyy">Moment about the Y axis.</param>
/// <param name="Izz">Moment about the Z axis.</param>
/// <param name="Ixy">Product of inertia in the XY plane.</param>
/// <param name="Ixz">Product of inertia in the XZ plane.</param>
/// <param name="Iyz">Product of inertia in the YZ plane.</param>
/// <remarks>
/// Stored as six components rather than a full matrix because the tensor is symmetric and storing
/// nine invites the two halves drifting apart.
/// </remarks>
/// <seealso href="https://en.wikipedia.org/wiki/Moment_of_inertia">Moment of inertia</seealso>
public readonly record struct InertiaTensor(
    double Ixx,
    double Iyy,
    double Izz,
    double Ixy = 0.0,
    double Ixz = 0.0,
    double Iyz = 0.0)
{
    /// <summary>Gets the zero tensor.</summary>
    public static InertiaTensor Zero => default;

    /// <summary>Gets the tensor as a matrix, with the products of inertia negated as convention requires.</summary>
    /// <remarks>
    /// TODO(P6-T07): principal axes and principal moments need an eigen decomposition, which the
    /// mass-properties dialog will want. Not needed by anything in Phase 1.
    /// </remarks>
    public Mat4d ToMatrix() => new(
        Ixx, -Ixy, -Ixz, 0,
        -Ixy, Iyy, -Iyz, 0,
        -Ixz, -Iyz, Izz, 0,
        0, 0, 0, 1);
}

/// <summary>
/// How many topological entities of each kind a shape has.
/// </summary>
/// <param name="Solids">Number of solids.</param>
/// <param name="Shells">Number of shells.</param>
/// <param name="Faces">Number of faces.</param>
/// <param name="Wires">Number of wires.</param>
/// <param name="Edges">Number of edges.</param>
/// <param name="Vertices">Number of vertices.</param>
/// <remarks>
/// A cheap, exact fingerprint of a shape's topology. The regression corpus asserts on these
/// alongside mass properties (PLAN.md 8.2), because they catch a different class of error: a
/// boolean that produced the right volume with the wrong face count has gone wrong in a way no
/// amount of mass-property checking will reveal.
/// </remarks>
public readonly record struct TopologyCounts(
    int Solids,
    int Shells,
    int Faces,
    int Wires,
    int Edges,
    int Vertices)
{
    /// <summary>Gets the count for one kind of entity.</summary>
    /// <param name="kind">The kind to count.</param>
    /// <exception cref="ArgumentOutOfRangeException">The kind is not counted.</exception>
    public int this[SubEntityKind kind] => kind switch
    {
        SubEntityKind.Solid => Solids,
        SubEntityKind.Shell => Shells,
        SubEntityKind.Face => Faces,
        SubEntityKind.Wire => Wires,
        SubEntityKind.Edge => Edges,
        SubEntityKind.Vertex => Vertices,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <inheritdoc />
    public override string ToString()
        => $"{Solids}so {Shells}sh {Faces}f {Wires}w {Edges}e {Vertices}v";
}

/// <summary>
/// The outcome of checking a shape for structural validity.
/// </summary>
/// <param name="IsValid">Whether the shape is structurally sound.</param>
/// <param name="IsClosed">Whether the shape's shells are closed, as a solid's must be.</param>
/// <param name="Problems">What is wrong, empty when valid.</param>
/// <remarks>
/// PLAN.md 8.3 requires this on every result in tests: "an invalid shape is a failure even if it
/// looks right". Invalid shapes propagate — a boolean against one produces garbage, often much
/// later and far from the cause — so catching them at the point of creation is the difference
/// between a one-line fix and an afternoon of bisection.
/// </remarks>
public readonly record struct ShapeValidity(
    bool IsValid,
    bool IsClosed,
    ImmutableArray<KernelDiagnostic> Problems)
{
    /// <summary>Gets a validity result for a sound, closed shape.</summary>
    public static ShapeValidity Valid => new(true, true, []);
}

/// <summary>
/// How finely to tessellate.
/// </summary>
/// <param name="ChordalDeviation">
/// Maximum distance between the tessellation and the true surface, in metres.
/// </param>
/// <param name="AngularDeviation">
/// Maximum angle between adjacent facet normals, in radians. Controls smoothness on curved
/// surfaces independently of size.
/// </param>
/// <param name="Relative">
/// Whether <paramref name="ChordalDeviation"/> is relative to the shape's size rather than
/// absolute. Relative is usually what is wanted: a fixed absolute deviation produces either a
/// coarse blob for a small part or a hundred million triangles for a large one.
/// </param>
/// <param name="ComputeNormals">Whether to compute per-vertex normals.</param>
public readonly record struct TessellationOptions(
    double ChordalDeviation = Tolerance.DisplayChordal,
    double AngularDeviation = 0.5,
    bool Relative = true,
    bool ComputeNormals = true)
{
    /// <summary>Gets settings suitable for interactive display.</summary>
    /// <remarks>
    /// Spelled out rather than written <c>new()</c>. On a record struct, <c>new()</c> binds to the
    /// implicit parameterless constructor that zeroes every field -- it does <b>not</b> run the
    /// primary constructor, so the default arguments above are skipped entirely. That produced a
    /// zero chordal deviation here, which the FakeKernel silently ignored and OCCT rejected.
    /// </remarks>
    public static TessellationOptions Display
        => new(Tolerance.DisplayChordal, AngularDeviation: 0.5, Relative: true, ComputeNormals: true);

    /// <summary>Gets settings suitable for export and analysis, an order of magnitude finer.</summary>
    public static TessellationOptions Fine => new(Tolerance.DisplayChordal / 10.0, 0.1);

    /// <summary>Gets coarse settings for thumbnails and lightweight previews.</summary>
    public static TessellationOptions Coarse => new(Tolerance.DisplayChordal * 20.0, 1.0);
}

/// <summary>
/// The tessellated edges of a body, as polylines.
/// </summary>
/// <remarks>
/// <para>
/// Concatenated rather than a jagged array: this is upload-shaped, and one buffer with offsets is
/// what a renderer wants. Polyline <c>i</c> spans points <c>[Offsets[i], Offsets[i + 1])</c>, so
/// <see cref="Offsets"/> carries one more entry than there are polylines and the last polyline
/// needs no special case.
/// </para>
/// <para>
/// Edges are a separate result rather than something derived from the triangles. A CAD edge is an
/// entity the user selects and dimensions to; the boundary between two coplanar triangles is not
/// one. Deriving them from the mesh would invent edges that do not exist and lose the ones that do.
/// </para>
/// </remarks>
/// <param name="Positions">Every polyline point, concatenated, in metres.</param>
/// <param name="Offsets">
/// Where each polyline starts, plus a closing total. Empty when there are no edges.
/// </param>
/// <param name="Edges">Which edge each polyline represents.</param>
public sealed record MeshEdges(
    ImmutableArray<Vec3d> Positions,
    ImmutableArray<int> Offsets,
    ImmutableArray<SubEntity> Edges)
{
    /// <summary>Gets the empty edge set.</summary>
    public static MeshEdges Empty { get; } = new([], [], []);

    /// <summary>Gets the number of polylines.</summary>
    public int PolylineCount => Edges.Length;

    /// <summary>Returns the points of one polyline.</summary>
    /// <param name="index">Which polyline.</param>
    /// <returns>Its points.</returns>
    /// <exception cref="ArgumentOutOfRangeException">There is no such polyline.</exception>
    public ReadOnlySpan<Vec3d> PointsOf(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, PolylineCount);

        int start = Offsets[index];
        return Positions.AsSpan()[start..Offsets[index + 1]];
    }
}

/// <summary>
/// A triangulated approximation of a shape.
/// </summary>
/// <param name="Positions">Vertex positions in world coordinates and metres.</param>
/// <param name="Normals">
/// Per-vertex normals, empty when normals were not requested. Same length as
/// <paramref name="Positions"/> when present.
/// </param>
/// <param name="Indices">Triangle vertex indices, three per triangle.</param>
/// <param name="TriangleFaces">
/// For each triangle, the index into <paramref name="Faces"/> of the face it came from.
/// </param>
/// <param name="Faces">The faces that contributed, in order.</param>
/// <param name="Edges">
/// The tessellated edges, or <see langword="null"/> when the kernel supplied none. Read it
/// through <see cref="MeshBuffer.EdgeSet"/> rather than directly.
/// </param>
/// <remarks>
/// <para>
/// Positions are <b>double</b>. Converting to float happens at buffer-fill time relative to a
/// per-view origin (PLAN.md 5.10); doing it here would throw away precision that a part positioned
/// far from the world origin needs, and the resulting shimmer is unfixable downstream.
/// </para>
/// <para>
/// <paramref name="TriangleFaces"/> is what makes pixel-exact picking possible: the renderer writes
/// a face identifier per triangle into the integer ID buffer, so a hover test is a texture read
/// rather than a ray cast against the B-rep.
/// </para>
/// </remarks>
public sealed record MeshBuffer(
    ImmutableArray<Vec3d> Positions,
    ImmutableArray<Vec3d> Normals,
    ImmutableArray<int> Indices,
    ImmutableArray<int> TriangleFaces,
    ImmutableArray<SubEntity> Faces,
    MeshEdges? Edges = null)
{
    /// <summary>Gets an empty mesh.</summary>
    public static MeshBuffer Empty { get; } = new([], [], [], [], []);

    /// <summary>Gets the tessellated edges, or an empty set when the kernel supplied none.</summary>
    /// <remarks>
    /// Optional on the record so that existing callers and fixtures that only care about triangles
    /// need not construct an empty one, but never null here — a caller iterating edges should not
    /// have to distinguish "no edges" from "edges not asked for".
    /// </remarks>
    public MeshEdges EdgeSet => Edges ?? MeshEdges.Empty;

    /// <summary>Gets the number of triangles.</summary>
    public int TriangleCount => Indices.Length / 3;

    /// <summary>Gets the number of vertices.</summary>
    public int VertexCount => Positions.Length;

    /// <summary>Gets the axis-aligned bound of the tessellation.</summary>
    public Bounds3d Bounds => Bounds3d.FromPoints(Positions);

    /// <summary>
    /// Computes the enclosed volume by the divergence theorem over the triangles.
    /// </summary>
    /// <remarks>
    /// PLAN.md 8.3 asks for volume to be cross-checked two ways where feasible: the kernel's own
    /// exact integration against this. They should agree to within tessellation tolerance, and when
    /// they do not, the topology is wrong in a way that neither figure alone would reveal —
    /// typically an inverted face normal or an unclosed shell.
    /// </remarks>
    public double ComputeVolumeByDivergence()
    {
        double total = 0.0;

        for (int i = 0; i + 2 < Indices.Length; i += 3)
        {
            Vec3d a = Positions[Indices[i]];
            Vec3d b = Positions[Indices[i + 1]];
            Vec3d c = Positions[Indices[i + 2]];
            total += Vec3d.Dot(a, Vec3d.Cross(b, c));
        }

        return total / 6.0;
    }
}
