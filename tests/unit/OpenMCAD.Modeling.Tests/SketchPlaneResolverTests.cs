using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;
using OpenMCAD.Kernel;
using OpenMCAD.Math;
using OpenMCAD.Modeling;

using Xunit;

namespace OpenMCAD.Modeling.Tests;

/// <summary>
/// Turning a <see cref="SketchPlaneReference"/> into the <see cref="SketchPlane"/> a sketch is
/// actually drawn on, against a real <see cref="Document"/> and a real <see cref="NameResolver"/>
/// (P4-T10).
/// </summary>
/// <remarks>
/// Each source kind gets the same shape of coverage: found, not found, found as the wrong kind of
/// thing, and geometrically unusable once found. §5.3's rule that a wrong-but-plausible answer is
/// worse than an error applies here exactly as it does to the naming layer this builds on — nothing
/// below invents a plane nobody asked for.
/// </remarks>
public sealed class SketchPlaneResolverTests
{
    private static readonly FeatureId Sketch = FeatureId.New();

    [Fact]
    public void OnDatumPlane_ResolvesAStandardDatum()
    {
        Document document = Document.Empty();
        SketchPlaneReference reference = new SketchPlaneReference.OnDatumPlane(FeatureId.None, "Top");

        SketchPlaneResolution result = SketchPlaneResolver.Resolve(reference, document, Sketch);

        result.IsResolved.Should().BeTrue();
        result.Plane!.IsNear(SketchPlane.WorldXY).Should().BeTrue();
    }

    [Fact]
    public void OnDatumPlane_ResolvesAFeatureOwnedDatum()
    {
        FeatureId owner = FeatureId.New();
        ReferenceGeometry.Plane geometry =
            new(owner, "Offset1", new Vec3d(0, 0, 3), Vec3d.UnitZ);

        Document document = DocumentWith(geometry);
        SketchPlaneReference reference = new SketchPlaneReference.OnDatumPlane(owner, "Offset1");

        SketchPlaneResolution result = SketchPlaneResolver.Resolve(reference, document, Sketch);

        result.IsResolved.Should().BeTrue();
        result.Plane!.Origin.Should().Be(new Vec3d(0, 0, 3));
        result.Plane.Normal.Should().Be(Vec3d.UnitZ);
    }

    [Fact]
    public void OnDatumPlane_FailsWhenNothingAnswersToTheName()
    {
        Document document = Document.Empty();
        SketchPlaneReference reference =
            new SketchPlaneReference.OnDatumPlane(FeatureId.None, "NoSuchPlane");

        SketchPlaneResolution result = SketchPlaneResolver.Resolve(reference, document, Sketch);

        result.IsResolved.Should().BeFalse();
        result.Outcome.Should().Be(SketchPlaneResolutionOutcome.NotFound);
        result.Reason.Should().Contain("NoSuchPlane");
    }

    [Fact]
    public void OnDatumPlane_FailsWhenTheNameIsAnAxisNotAPlane()
    {
        // The commonest real mistake: a sketch plane reference surviving a rename that also
        // changed what kind of reference geometry it points at. This must not silently sketch on
        // an axis's origin with an invented normal.
        FeatureId owner = FeatureId.New();
        Document document = DocumentWith(new ReferenceGeometry.Axis(owner, "Axis1", Vec3d.Zero, Vec3d.UnitX));
        SketchPlaneReference reference = new SketchPlaneReference.OnDatumPlane(owner, "Axis1");

        SketchPlaneResolution result = SketchPlaneResolver.Resolve(reference, document, Sketch);

        result.Outcome.Should().Be(SketchPlaneResolutionOutcome.NotFound);
        result.Reason.Should().Contain("axis");
    }

    [Fact]
    public void OnDatumPlane_FailsOnADegenerateNormalRatherThanThrowing()
    {
        FeatureId owner = FeatureId.New();
        Document document = DocumentWith(new ReferenceGeometry.Plane(owner, "Bad", Vec3d.Zero, Vec3d.Zero));
        SketchPlaneReference reference = new SketchPlaneReference.OnDatumPlane(owner, "Bad");

        SketchPlaneResolution result = SketchPlaneResolver.Resolve(reference, document, Sketch);

        // A rebuild resolves every reference on every feature in the dirty set (§5.4). One
        // corrupt datum throwing would take features that have nothing to do with it down too.
        result.Outcome.Should().Be(SketchPlaneResolutionOutcome.NotPlanar);
    }

    [Fact]
    public void OnCoordinateSystem_ResolvesToItsXYPlane()
    {
        FeatureId owner = FeatureId.New();
        ReferenceGeometry.CoordinateSystem geometry = new(
            owner, "CS1", new Vec3d(1, 2, 3), Vec3d.UnitX, Vec3d.UnitZ);

        Document document = DocumentWith(geometry);
        SketchPlaneReference reference = new SketchPlaneReference.OnCoordinateSystem(owner, "CS1");

        SketchPlaneResolution result = SketchPlaneResolver.Resolve(reference, document, Sketch);

        result.IsResolved.Should().BeTrue();
        result.Plane!.Origin.Should().Be(new Vec3d(1, 2, 3));
        result.Plane.XAxis.Should().Be(Vec3d.UnitX);
        result.Plane.Normal.Should().Be(Vec3d.UnitZ);
    }

    [Fact]
    public void OnCoordinateSystem_FailsWhenTheNameIsAPlaneNotACoordinateSystem()
    {
        FeatureId owner = FeatureId.None;
        Document document = Document.Empty();
        SketchPlaneReference reference = new SketchPlaneReference.OnCoordinateSystem(owner, "Front");

        SketchPlaneResolution result = SketchPlaneResolver.Resolve(reference, document, Sketch);

        result.Outcome.Should().Be(SketchPlaneResolutionOutcome.NotFound);
        result.Reason.Should().Contain("plane");
    }

    [Fact]
    public void OnCoordinateSystem_FailsOnDegenerateAxesRatherThanThrowing()
    {
        FeatureId owner = FeatureId.New();
        Document document = DocumentWith(
            new ReferenceGeometry.CoordinateSystem(owner, "Bad", Vec3d.Zero, Vec3d.UnitZ, Vec3d.UnitZ));
        SketchPlaneReference reference = new SketchPlaneReference.OnCoordinateSystem(owner, "Bad");

        SketchPlaneResolution result = SketchPlaneResolver.Resolve(reference, document, Sketch);

        result.Outcome.Should().Be(SketchPlaneResolutionOutcome.NotPlanar);
    }

    [Fact]
    public void OnFace_ResolvesAgainstTheSuppliedNameResolverAndPlaneQuery()
    {
        Scenario scenario = new();
        SubEntity face = scenario.NewFace();

        // The point has to lie along the normal itself: Plane.Origin reconstructs the point on the
        // plane closest to the world origin, so any component off the normal would be a component
        // this test could not actually observe.
        Plane worldPlane = Plane.FromPointNormal(new Vec3d(0, 5, 0), Vec3d.UnitY);

        SketchPlaneReference reference = new SketchPlaneReference.OnFace(scenario.NameOf(face));

        SketchPlaneResolution result = SketchPlaneResolver.Resolve(
            reference,
            Document.Empty(),
            Sketch,
            scenario.Resolver(),
            entity => entity == face ? worldPlane : null);

        result.IsResolved.Should().BeTrue();
        result.Plane!.Origin.Should().Be(new Vec3d(0, 5, 0));
        result.Plane.Normal.Should().Be(Vec3d.UnitY);
    }

    [Fact]
    public void OnFace_FailsWithoutThrowingWhenNoFaceResolverIsConfigured()
    {
        Scenario scenario = new();
        SubEntity face = scenario.NewFace();
        SketchPlaneReference reference = new SketchPlaneReference.OnFace(scenario.NameOf(face));

        SketchPlaneResolution result = SketchPlaneResolver.Resolve(
            reference, Document.Empty(), Sketch);

        result.Outcome.Should().Be(SketchPlaneResolutionOutcome.NotFound);
    }

    [Fact]
    public void OnFace_FailsWhenHistoryHasNoRecordOfTheFace()
    {
        PersistentName nameOfNothingRecorded = PersistentName.Of(
            NameSegment.Of(FeatureId.New(), ProvenanceKind.New, EntityRole.SideWall));

        SketchPlaneReference reference = new SketchPlaneReference.OnFace(nameOfNothingRecorded);

        SketchPlaneResolution result = SketchPlaneResolver.Resolve(
            reference, Document.Empty(), Sketch, new NameResolver(RebuildHistory.Empty));

        result.Outcome.Should().Be(SketchPlaneResolutionOutcome.NotFound);
    }

    [Fact]
    public void OnFace_IsAmbiguousWhenHistoryReportsASplitAndNothingArbitrates()
    {
        Scenario scenario = new();
        (PersistentName splitName, _, _) = scenario.NewSplitFace();

        SketchPlaneReference reference = new SketchPlaneReference.OnFace(splitName);

        SketchPlaneResolution result = SketchPlaneResolver.Resolve(
            reference, Document.Empty(), Sketch, scenario.Resolver());

        // No geometric tier was supplied, so tier two never gets a chance to arbitrate the split --
        // exactly the same "no silent wrong answer" refusal §5.3 asks of every other consumer of
        // persistent names.
        result.Outcome.Should().Be(SketchPlaneResolutionOutcome.Ambiguous);
    }

    [Fact]
    public void OnFace_FailsWhenTheReferenceNamesAnEdgeNotAFace()
    {
        Scenario scenario = new();
        SubEntity edge = scenario.NewEdge();
        SketchPlaneReference reference = new SketchPlaneReference.OnFace(scenario.NameOf(edge));

        SketchPlaneResolution result = SketchPlaneResolver.Resolve(
            reference,
            Document.Empty(),
            Sketch,
            scenario.Resolver(),
            _ => Plane.XY);

        result.Outcome.Should().Be(SketchPlaneResolutionOutcome.NotPlanar);
    }

    [Fact]
    public void OnFace_FailsWhenTheResolvedFaceIsNotFlat()
    {
        Scenario scenario = new();
        SubEntity face = scenario.NewFace();
        SketchPlaneReference reference = new SketchPlaneReference.OnFace(scenario.NameOf(face));

        SketchPlaneResolution result = SketchPlaneResolver.Resolve(
            reference,
            Document.Empty(),
            Sketch,
            scenario.Resolver(),
            planeOf: _ => null); // e.g. the kernel reports the face is cylindrical

        result.Outcome.Should().Be(SketchPlaneResolutionOutcome.NotPlanar);
    }

    private static Document DocumentWith(ReferenceGeometry reference)
    {
        DocumentSession session = new();

        using (IDocumentTransaction tx = session.BeginTransaction("Add reference"))
        {
            tx.AddReference(reference);
            tx.Commit();
        }

        return session.Current;
    }

    /// <summary>
    /// Builds just enough rebuild history for a face reference to resolve through the real naming
    /// tiers, the same shape <see cref="NameResolverTests"/> in <c>OpenMCAD.Core.Tests</c> uses.
    /// </summary>
    private sealed class Scenario
    {
        private readonly RebuildHistory.Builder _history = new();
        private readonly KernelShape _shape = new(1);
        private ulong _nextTag = 1;

        public SubEntity NewFace() => new(_shape, _nextTag++, SubEntityKind.Face);

        public SubEntity NewEdge() => new(_shape, _nextTag++, SubEntityKind.Edge);

        /// <summary>A persistent name for an entity created out of nothing by its own feature.</summary>
        public PersistentName NameOf(SubEntity entity)
        {
            FeatureId feature = FeatureId.New();
            EntityRole role = EntityRole.From(OperationRole.SideWall);

            _history.Add(feature, new HistoryMapBuilder().AddNew(entity, OperationRole.SideWall).Build());

            return PersistentName.Of(NameSegment.Of(feature, ProvenanceKind.New, role));
        }

        /// <summary>
        /// A name that resolves ambiguously: one feature generated two faces from the same input,
        /// under the same role, and nothing here says which was meant.
        /// </summary>
        public (PersistentName Name, SubEntity Left, SubEntity Right) NewSplitFace()
        {
            FeatureId source = FeatureId.New();
            SubEntity edge = new(_shape, _nextTag++, SubEntityKind.Edge);
            _history.Add(source, new HistoryMapBuilder().AddNew(edge, OperationRole.Retained).Build());

            FeatureId extrude = FeatureId.New();
            SubEntity left = new(_shape, _nextTag++, SubEntityKind.Face);
            SubEntity right = new(_shape, _nextTag++, SubEntityKind.Face);

            _history.Add(extrude, new HistoryMapBuilder()
                .AddGenerated(edge, left, OperationRole.SideWall)
                .AddGenerated(edge, right, OperationRole.SideWall)
                .Build());

            PersistentName origin = PersistentName.Of(
                NameSegment.Of(source, ProvenanceKind.New, EntityRole.From(OperationRole.Retained)));

            PersistentName name = PersistentName.Of(new NameSegment(
                extrude,
                ProvenanceKind.Generated,
                [new NameSource.Entity(origin)],
                EntityRole.From(OperationRole.SideWall)));

            return (name, left, right);
        }

        public NameResolver Resolver() => new(_history.Build());
    }
}
