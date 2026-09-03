using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;
using OpenMCAD.Kernel;
using OpenMCAD.Math;
using OpenMCAD.Modeling;
using OpenMCAD.Solver.Sketching;

using Xunit;

namespace OpenMCAD.Modeling.Tests;

/// <summary>
/// Turning a <see cref="SketchExternalReference"/> into the sketch geometry it names, against a
/// real <see cref="NameResolver"/> and a resolved <see cref="SketchPlane"/> (P4-T11).
/// </summary>
/// <remarks>
/// The sketch plane throughout is <see cref="SketchPlane.WorldXY"/>, whose axes are <em>not</em>
/// world X and Y (see <c>SketchPlaneTests.FromNormal_OnTheWorldZAxisMatchesPlaneCreateFrame</c>) --
/// deliberately, so a test that quietly assumed they were would be caught by its own numbers not
/// matching, rather than by luck.
/// </remarks>
public sealed class SketchExternalReferenceResolverTests
{
    private static readonly FeatureId Sketch = FeatureId.New();
    private static readonly SketchPlane Plane = SketchPlane.WorldXY;

    [Fact]
    public void Project_ALineNotPerpendicularToThePlane_Succeeds()
    {
        Scenario scenario = new();
        WorldCurve.Line line = new(new Vec3d(0, 0, 1), new Vec3d(3, 4, 1));
        SketchExternalReference reference = scenario.ReferenceTo(line, SketchExternalReferenceOperation.Project);

        SketchExternalReferenceResolution result = scenario.Resolve(reference, Plane);

        result.IsResolved.Should().BeTrue();
        SketchLine produced = (SketchLine)result.Entity!;
        produced.Start.Should().Be(Plane.ToLocal(line.Start));
        produced.End.Should().Be(Plane.ToLocal(line.End));
    }

    [Fact]
    public void Project_ALinePerpendicularToThePlane_IsDegenerate()
    {
        // Both ends share the same (X, Y): the projection collapses the line to a point.
        Scenario scenario = new();
        WorldCurve.Line line = new(new Vec3d(1, 2, -5), new Vec3d(1, 2, 5));
        SketchExternalReference reference = scenario.ReferenceTo(line, SketchExternalReferenceOperation.Project);

        SketchExternalReferenceResolution result = scenario.Resolve(reference, Plane);

        result.Outcome.Should().Be(SketchExternalReferenceResolutionOutcome.Degenerate);
    }

    [Fact]
    public void Convert_ALineAlreadyOnThePlane_MatchesWhatProjectWouldGive()
    {
        Scenario scenario = new();
        WorldCurve.Line onPlane = new(new Vec3d(0, 0, 0), new Vec3d(3, 4, 0));
        WorldCurve.Line offPlane = new(new Vec3d(0, 0, 1), new Vec3d(3, 4, 1));

        SketchExternalReferenceResolution converted = scenario.Resolve(
            scenario.ReferenceTo(onPlane, SketchExternalReferenceOperation.Convert), Plane);
        SketchExternalReferenceResolution projected = scenario.Resolve(
            scenario.ReferenceTo(offPlane, SketchExternalReferenceOperation.Project), Plane);

        converted.IsResolved.Should().BeTrue();
        ((SketchLine)converted.Entity!).Start.Should().Be(((SketchLine)projected.Entity!).Start);
        ((SketchLine)converted.Entity!).End.Should().Be(((SketchLine)projected.Entity!).End);
    }

    [Fact]
    public void Convert_ALineOffThePlane_Refuses()
    {
        Scenario scenario = new();
        WorldCurve.Line line = new(new Vec3d(0, 0, 1), new Vec3d(3, 4, 1));
        SketchExternalReference reference = scenario.ReferenceTo(line, SketchExternalReferenceOperation.Convert);

        SketchExternalReferenceResolution result = scenario.Resolve(reference, Plane);

        result.Outcome.Should().Be(SketchExternalReferenceResolutionOutcome.NotInPlane);
    }

    [Fact]
    public void Project_ACircleParallelToThePlane_TracesTheSameSenseAsTheEdge()
    {
        Scenario scenario = new();
        WorldCurve.Circle circle = new(
            Vec3d.Zero, Vec3d.UnitZ, Vec3d.UnitX, 5, 0, System.Math.PI / 2);
        SketchExternalReference reference =
            scenario.ReferenceTo(circle, SketchExternalReferenceOperation.Project);

        SketchExternalReferenceResolution result = scenario.Resolve(reference, Plane);

        result.IsResolved.Should().BeTrue();
        SketchArc arc = (SketchArc)result.Entity!;

        arc.Sweep.Should().BeApproximately(System.Math.PI / 2, 1e-9);
        NearPoint(arc.PointAt(0), new Vec2d(0, -5));
        NearPoint(arc.PointAt(1), new Vec2d(5, 0));
    }

    [Fact]
    public void Project_ACircleAntiparallelToThePlane_StillTracesA90DegreeArc()
    {
        // Same edge as the parallel case, seen from the other side: the sketch plane's normal is
        // opposite the circle's, so its own anticlockwise sense reads as clockwise once projected.
        // SketchArc can only ever be anticlockwise (P4-T03), so representing the same 90-degree
        // physical arc means the entity's Start and End swap relative to the edge's own -- verified
        // here by the sweep staying 90 degrees (not the 270-degree "long way") and by tracing the
        // exact same two physical points as the parallel case, in the opposite order.
        Scenario scenario = new();
        WorldCurve.Circle circle = new(
            Vec3d.Zero, -Vec3d.UnitZ, Vec3d.UnitX, 5, 0, System.Math.PI / 2);
        SketchExternalReference reference =
            scenario.ReferenceTo(circle, SketchExternalReferenceOperation.Project);

        SketchExternalReferenceResolution result = scenario.Resolve(reference, Plane);

        result.IsResolved.Should().BeTrue();
        SketchArc arc = (SketchArc)result.Entity!;

        arc.Sweep.Should().BeApproximately(System.Math.PI / 2, 1e-9);
        NearPoint(arc.PointAt(0), new Vec2d(-5, 0));
        NearPoint(arc.PointAt(1), new Vec2d(0, -5));
    }

    [Fact]
    public void Project_AFullCircle_DoesNotDependOnWhichSideItIsViewedFrom()
    {
        Scenario scenario = new();
        WorldCurve.Circle parallel = WorldCurve.Circle.Full(new Vec3d(1, 2, 0), Vec3d.UnitZ, Vec3d.UnitX, 5);
        WorldCurve.Circle antiparallel = WorldCurve.Circle.Full(new Vec3d(1, 2, 0), -Vec3d.UnitZ, Vec3d.UnitX, 5);

        SketchExternalReferenceResolution first = scenario.Resolve(
            scenario.ReferenceTo(parallel, SketchExternalReferenceOperation.Project), Plane);
        SketchExternalReferenceResolution second = scenario.Resolve(
            scenario.ReferenceTo(antiparallel, SketchExternalReferenceOperation.Project), Plane);

        SketchCircle a = (SketchCircle)first.Entity!;
        SketchCircle b = (SketchCircle)second.Entity!;

        a.Centre.Should().Be(b.Centre);
        a.Radius.Should().Be(b.Radius);
    }

    [Fact]
    public void Project_ACircleNotParallelToThePlane_IsUnsupported()
    {
        Scenario scenario = new();
        WorldCurve.Circle circle = WorldCurve.Circle.Full(Vec3d.Zero, Vec3d.UnitX, Vec3d.UnitY, 5);
        SketchExternalReference reference =
            scenario.ReferenceTo(circle, SketchExternalReferenceOperation.Project);

        SketchExternalReferenceResolution result = scenario.Resolve(reference, Plane);

        result.Outcome.Should().Be(SketchExternalReferenceResolutionOutcome.Unsupported);
    }

    [Fact]
    public void Convert_ACircleParallelButOffThePlane_Refuses()
    {
        Scenario scenario = new();
        WorldCurve.Circle circle = WorldCurve.Circle.Full(new Vec3d(0, 0, 3), Vec3d.UnitZ, Vec3d.UnitX, 5);
        SketchExternalReference reference =
            scenario.ReferenceTo(circle, SketchExternalReferenceOperation.Convert);

        SketchExternalReferenceResolution result = scenario.Resolve(reference, Plane);

        result.Outcome.Should().Be(SketchExternalReferenceResolutionOutcome.NotInPlane);
    }

    [Fact]
    public void Intersect_ALineCrossingWithinItsExtent_ProducesThePoint()
    {
        Scenario scenario = new();
        WorldCurve.Line line = new(new Vec3d(0, 0, -2), new Vec3d(4, 4, 2));
        SketchExternalReference reference =
            scenario.ReferenceTo(line, SketchExternalReferenceOperation.Intersect);

        SketchExternalReferenceResolution result = scenario.Resolve(reference, Plane);

        result.IsResolved.Should().BeTrue();
        ((SketchPoint)result.Entity!).Position.Should().Be(Plane.ToLocal(new Vec3d(2, 2, 0)));
    }

    [Fact]
    public void Intersect_ALineThatCrossesOutsideItsOwnExtent_Refuses()
    {
        // The infinite line through these two points does cross the plane -- just not between them.
        Scenario scenario = new();
        WorldCurve.Line line = new(new Vec3d(0, 0, 1), new Vec3d(4, 4, 2));
        SketchExternalReference reference =
            scenario.ReferenceTo(line, SketchExternalReferenceOperation.Intersect);

        SketchExternalReferenceResolution result = scenario.Resolve(reference, Plane);

        result.Outcome.Should().Be(SketchExternalReferenceResolutionOutcome.NoIntersection);
    }

    [Fact]
    public void Intersect_ALineParallelToThePlane_Refuses()
    {
        Scenario scenario = new();
        WorldCurve.Line line = new(new Vec3d(0, 0, 5), new Vec3d(4, 4, 5));
        SketchExternalReference reference =
            scenario.ReferenceTo(line, SketchExternalReferenceOperation.Intersect);

        SketchExternalReferenceResolution result = scenario.Resolve(reference, Plane);

        result.Outcome.Should().Be(SketchExternalReferenceResolutionOutcome.NoIntersection);
    }

    [Fact]
    public void Intersect_ACircle_IsUnsupported()
    {
        Scenario scenario = new();
        WorldCurve.Circle circle = WorldCurve.Circle.Full(Vec3d.Zero, Vec3d.UnitX, Vec3d.UnitY, 5);
        SketchExternalReference reference =
            scenario.ReferenceTo(circle, SketchExternalReferenceOperation.Intersect);

        SketchExternalReferenceResolution result = scenario.Resolve(reference, Plane);

        result.Outcome.Should().Be(SketchExternalReferenceResolutionOutcome.Unsupported);
    }

    [Fact]
    public void Resolve_FailsWithoutThrowingWhenNoEdgeResolverIsConfigured()
    {
        SketchExternalReference reference = new(
            SketchEntityId.New(),
            PersistentName.Of(NameSegment.Of(FeatureId.New(), ProvenanceKind.New, EntityRole.Unknown)),
            SketchExternalReferenceOperation.Project);

        SketchExternalReferenceResolution result = SketchExternalReferenceResolver.Resolve(
            reference, Plane, Sketch);

        result.Outcome.Should().Be(SketchExternalReferenceResolutionOutcome.NotFound);
    }

    [Fact]
    public void Resolve_FailsWhenHistoryHasNoRecordOfTheEdge()
    {
        SketchExternalReference reference = new(
            SketchEntityId.New(),
            PersistentName.Of(NameSegment.Of(FeatureId.New(), ProvenanceKind.New, EntityRole.Unknown)),
            SketchExternalReferenceOperation.Project);

        SketchExternalReferenceResolution result = SketchExternalReferenceResolver.Resolve(
            reference, Plane, Sketch, new NameResolver(RebuildHistory.Empty));

        result.Outcome.Should().Be(SketchExternalReferenceResolutionOutcome.NotFound);
    }

    [Fact]
    public void Resolve_FailsWhenTheReferenceNamesAVertexNotAnEdge()
    {
        Scenario scenario = new();
        SubEntity vertex = new(scenario.Shape, 1, SubEntityKind.Vertex);
        PersistentName name = scenario.NameOf(vertex);
        SketchExternalReference reference =
            new(SketchEntityId.New(), name, SketchExternalReferenceOperation.Project);

        SketchExternalReferenceResolution result = SketchExternalReferenceResolver.Resolve(
            reference, Plane, Sketch, scenario.Resolver(), _ => new WorldCurve.Line(Vec3d.Zero, Vec3d.UnitX));

        result.Outcome.Should().Be(SketchExternalReferenceResolutionOutcome.NotFound);
    }

    [Fact]
    public void Resolve_ReportsNoGeometryRatherThanThrowingWhenTheCurveDelegateHasNothing()
    {
        Scenario scenario = new();
        WorldCurve.Line line = new(Vec3d.Zero, Vec3d.UnitX);
        SketchExternalReference reference = scenario.ReferenceTo(line, SketchExternalReferenceOperation.Project);

        SketchExternalReferenceResolution result = SketchExternalReferenceResolver.Resolve(
            reference, Plane, Sketch, scenario.Resolver(), curveOf: null);

        result.Outcome.Should().Be(SketchExternalReferenceResolutionOutcome.Unsupported);
    }

    private static void NearPoint(Vec2d actual, Vec2d expected)
    {
        actual.X.Should().BeApproximately(expected.X, 1e-9);
        actual.Y.Should().BeApproximately(expected.Y, 1e-9);
    }

    /// <summary>
    /// Builds just enough rebuild history for an edge reference to resolve through the real naming
    /// tiers, the same shape <c>SketchPlaneResolverTests.Scenario</c> uses.
    /// </summary>
    private sealed class Scenario
    {
        private readonly RebuildHistory.Builder _history = new();
        private readonly Dictionary<SubEntity, WorldCurve> _curves = [];
        private ulong _nextTag = 1;

        public KernelShape Shape { get; } = new(1);

        public SketchExternalReference ReferenceTo(WorldCurve curve, SketchExternalReferenceOperation operation)
        {
            SubEntity edge = new(Shape, _nextTag++, SubEntityKind.Edge);
            _curves[edge] = curve;

            return new SketchExternalReference(SketchEntityId.New(), NameOf(edge), operation);
        }

        public SketchExternalReferenceResolution Resolve(SketchExternalReference reference, SketchPlane plane)
            => SketchExternalReferenceResolver.Resolve(
                reference, plane, Sketch, Resolver(), entity => _curves.GetValueOrDefault(entity));

        /// <summary>A persistent name for an entity created out of nothing by its own feature.</summary>
        public PersistentName NameOf(SubEntity entity)
        {
            FeatureId feature = FeatureId.New();
            EntityRole role = EntityRole.From(OperationRole.Retained);

            _history.Add(feature, new HistoryMapBuilder().AddNew(entity, OperationRole.Retained).Build());

            return PersistentName.Of(NameSegment.Of(feature, ProvenanceKind.New, role));
        }

        public NameResolver Resolver() => new(_history.Build());
    }
}
