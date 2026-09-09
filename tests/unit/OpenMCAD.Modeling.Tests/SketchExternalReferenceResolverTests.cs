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
    public void Project_ACircleSeenEdgeOn_IsDegenerate()
    {
        // Its own plane is perpendicular to the sketch's, so it projects to a straight line: an
        // ellipse with no minor axis, which is not something SketchEllipse can hold. Degenerate
        // rather than unsupported -- the edge is fine and the view of it is not, and this build
        // does now project a circle at any other angle.
        Scenario scenario = new();
        WorldCurve.Circle circle = WorldCurve.Circle.Full(Vec3d.Zero, Vec3d.UnitX, Vec3d.UnitY, 5);

        SketchExternalReferenceResolution result = scenario.Resolve(
            scenario.ReferenceTo(circle, SketchExternalReferenceOperation.Project), Plane);

        result.Outcome.Should().Be(SketchExternalReferenceResolutionOutcome.Degenerate);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(60)]
    [InlineData(80)]
    public void Project_ACircleAtAnAngle_IsAnEllipseFlattenedByTheCosineOfThatAngle(double degrees)
    {
        // The defining property of projecting a circle orthographically: the axis along the tilt
        // shortens by the cosine of the tilt and the axis across it does not shorten at all. If
        // this holds for every angle then the axis-finding is right, since nothing else about the
        // construction could produce that relationship by accident.
        double tilt = degrees * System.Math.PI / 180;
        Scenario scenario = new();

        Vec3d normal = new(System.Math.Sin(tilt), 0, System.Math.Cos(tilt));
        WorldCurve.Circle circle = WorldCurve.Circle.Full(Vec3d.Zero, normal, Vec3d.UnitY, 5);

        SketchExternalReferenceResolution result = scenario.Resolve(
            scenario.ReferenceTo(circle, SketchExternalReferenceOperation.Project), Plane);

        result.Outcome.Should().Be(SketchExternalReferenceResolutionOutcome.Resolved, result.Reason);

        SketchEllipse ellipse = (SketchEllipse)result.Entity!;

        ellipse.MajorRadius.Should().BeApproximately(5, 1e-9, "the axis across the tilt is unforeshortened");
        ellipse.MinorRadius.Should().BeApproximately(
            5 * System.Math.Cos(tilt), 1e-9, "the axis along the tilt shortens by the cosine");
    }

    [Fact]
    public void Project_ACircleAtAnAngle_PutsItsAxesWhereTheTiltDoes()
    {
        // Tilted about the sketch's X axis, so the foreshortening is along Y and the long axis is
        // the untouched X one. A rotation that came out a quarter turn away would still satisfy the
        // lengths above.
        Scenario scenario = new();

        Vec3d normal = new(0, System.Math.Sin(System.Math.PI / 3), System.Math.Cos(System.Math.PI / 3));
        WorldCurve.Circle circle = WorldCurve.Circle.Full(new Vec3d(1, 2, 0), normal, Vec3d.UnitX, 4);

        SketchExternalReferenceResolution result = scenario.Resolve(
            scenario.ReferenceTo(circle, SketchExternalReferenceOperation.Project), Plane);

        SketchEllipse ellipse = (SketchEllipse)result.Entity!;

        // Asked of the plane rather than written as a literal: a sketch plane's own axes come from
        // Plane.CreateFrame and are not the world's, so a hand-written (1, 2) would be asserting
        // this test's idea of the frame rather than where the circle actually went.
        ellipse.Centre.Should().Be(Plane.ToLocal(new Vec3d(1, 2, 0)));
        ellipse.MajorRadius.Should().BeApproximately(4, 1e-9);

        // The unforeshortened direction is the axis the tilt turns about, which is world X. An
        // ellipse's rotation is modulo a half turn, an axis having no direction of its own, so the
        // major axis is parallel to it rather than equal to it.
        Vec2d expected = Plane.ToLocalDirection(Vec3d.UnitX);
        Vec2d major = new(System.Math.Cos(ellipse.Rotation), System.Math.Sin(ellipse.Rotation));

        System.Math.Abs(Vec2d.Cross(major, expected)).Should().BeApproximately(
            0, 1e-9, "the long axis lies along the direction the tilt does not foreshorten");
    }

    [Fact]
    public void Project_AnArcAtAnAngle_BeginsAndEndsAtTheProjectionsOfItsOwnEnds()
    {
        // The part that is easy to get subtly wrong. SketchEllipticalArc stores *eccentric* angles,
        // so the two ends are only in the right places if the circle's own parameter is carried
        // across as the ellipse's parameter rather than recovered from the projected point -- which
        // is what makes the ends land exactly rather than nearly.
        Scenario scenario = new();

        // The reference direction is deliberately *not* the axis the tilt turns about. Aligned, the
        // projected pair comes out perpendicular already, the axis search has nothing to find and
        // returns zero, and every step that depends on it passes whatever it does. Skewed, they do
        // not, and the eccentric-angle origin is somewhere rather than nowhere.
        Vec3d normal = new(System.Math.Sin(0.7), 0, System.Math.Cos(0.7));
        WorldCurve.Circle circle = new(new Vec3d(2, -1, 3), normal, new Vec3d(0.3, 1, 0.2), 6, 0.4, 2.1);

        SketchExternalReferenceResolution result = scenario.Resolve(
            scenario.ReferenceTo(circle, SketchExternalReferenceOperation.Project), Plane);

        result.Outcome.Should().Be(SketchExternalReferenceResolutionOutcome.Resolved, result.Reason);

        SketchEllipticalArc arc = (SketchEllipticalArc)result.Entity!;

        Vec2d expectedStart = Plane.ToLocal(PointOn(circle, 0.4));
        Vec2d expectedEnd = Plane.ToLocal(PointOn(circle, 2.1));

        Vec2d.Distance(arc.PointAt(0), expectedStart).Should().BeApproximately(0, 1e-9);
        Vec2d.Distance(arc.PointAt(1), expectedEnd).Should().BeApproximately(0, 1e-9);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.6)]
    [InlineData(1.9)]
    public void Project_ACircleAtAnAngle_LandsEveryPointOfItOnTheEllipse(double skew)
    {
        // The whole construction in one property, and the only one that pins the axes and the
        // rotation together: every point of the circle, projected, satisfies the ellipse equation
        // of the ellipse this produced. The skew turns the circle's own reference direction away
        // from the axis the tilt turns about, which is what stops the projected pair arriving
        // perpendicular and makes the axis search do something.
        Scenario scenario = new();

        Vec3d normal = new(0, System.Math.Sin(1.0), System.Math.Cos(1.0));
        Vec3d inPlane = new(
            System.Math.Cos(skew),
            System.Math.Sin(skew) * System.Math.Cos(1.0),
            -System.Math.Sin(skew) * System.Math.Sin(1.0));

        WorldCurve.Circle circle = WorldCurve.Circle.Full(new Vec3d(3, 1, -2), normal, inPlane, 7);

        SketchExternalReferenceResolution result = scenario.Resolve(
            scenario.ReferenceTo(circle, SketchExternalReferenceOperation.Project), Plane);

        result.Outcome.Should().Be(SketchExternalReferenceResolutionOutcome.Resolved, result.Reason);

        SketchEllipse ellipse = (SketchEllipse)result.Entity!;

        foreach (double angle in new[] { 0.0, 0.9, 2.2, 3.5, 5.1 })
        {
            Vec2d projected = Plane.ToLocal(PointOn(circle, angle)) - ellipse.Centre;

            double along = (projected.X * System.Math.Cos(ellipse.Rotation))
                + (projected.Y * System.Math.Sin(ellipse.Rotation));
            double across = (projected.Y * System.Math.Cos(ellipse.Rotation))
                - (projected.X * System.Math.Sin(ellipse.Rotation));

            double onEllipse = (along * along / (ellipse.MajorRadius * ellipse.MajorRadius))
                + (across * across / (ellipse.MinorRadius * ellipse.MinorRadius));

            onEllipse.Should().BeApproximately(
                1, 1e-9, $"the projection of the circle at {angle} lies on the ellipse");
        }
    }

    [Fact]
    public void Project_AnArcAtAnAngle_TracesTheSameGroundWhicheverSideItIsViewedFrom()
    {
        // The handedness correction. Flipping the circle's normal makes the projected frame
        // clockwise, and SketchEllipticalArc only ever sweeps anticlockwise from a minor axis a
        // quarter turn ahead of its major -- so the same physical arc, described from the other
        // side, has to come back as the *same* sketch entity. Without the correction it comes back
        // reflected about the major axis: a plausible arc over ground the edge never covered.
        Scenario scenario = new();

        Vec3d normal = new(System.Math.Sin(0.5), 0, System.Math.Cos(0.5));
        WorldCurve.Circle facing = new(Vec3d.Zero, normal, Vec3d.UnitY, 3, 0.2, 1.4);
        WorldCurve.Circle away = new(Vec3d.Zero, -normal, Vec3d.UnitY, 3, -1.4, -0.2);

        SketchEllipticalArc first = (SketchEllipticalArc)scenario.Resolve(
            scenario.ReferenceTo(facing, SketchExternalReferenceOperation.Project), Plane).Entity!;
        SketchEllipticalArc second = (SketchEllipticalArc)scenario.Resolve(
            scenario.ReferenceTo(away, SketchExternalReferenceOperation.Project), Plane).Entity!;

        first.MajorRadius.Should().BeApproximately(second.MajorRadius, 1e-9);
        first.MinorRadius.Should().BeApproximately(second.MinorRadius, 1e-9);
        first.Sweep.Should().BeApproximately(second.Sweep, 1e-9);

        Vec2d.Distance(first.PointAt(0), second.PointAt(0)).Should().BeApproximately(
            0, 1e-9, "the same ground, described the only way a sketch arc can describe it");
        Vec2d.Distance(first.PointAt(1), second.PointAt(1)).Should().BeApproximately(0, 1e-9);
    }

    /// <summary>A point on a world circle at one of its own angles.</summary>
    private static Vec3d PointOn(WorldCurve.Circle circle, double angle)
    {
        Vec3d normal = circle.Normal.Normalized();
        Vec3d x = circle.XDirection.PerpendicularTo(normal).Normalized();
        Vec3d y = Vec3d.Cross(normal, x);

        return circle.Centre
            + (x * (circle.Radius * System.Math.Cos(angle)))
            + (y * (circle.Radius * System.Math.Sin(angle)));
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
