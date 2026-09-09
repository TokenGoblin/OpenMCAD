using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;
using OpenMCAD.Kernel;
using OpenMCAD.Math;
using OpenMCAD.Modeling;

using Xunit;

namespace OpenMCAD.Modeling.Tests;

/// <summary>
/// Turning a <see cref="DatumDefinition"/> into the <see cref="ReferenceGeometry"/> it describes,
/// against a real <see cref="Document"/> and a real <see cref="NameResolver"/> (P5-T03).
/// </summary>
/// <remarks>
/// <para>
/// Two layers, tested separately. The role resolution — what a reference is allowed to stand for
/// when a construction asks it for a plane, a point, an axis or a curve — is shared by all thirteen
/// constructions, so it is covered once against whichever construction is the thinnest wrapper
/// around it rather than thirteen times.
/// </para>
/// <para>
/// The constructions themselves are checked against properties the definition states rather than
/// against coordinates read back out of the implementation: a midplane is the plane both sources
/// are equidistant from, an axis where two planes meet is the line lying in both. Asserting a
/// literal point instead would mostly assert this test's own arithmetic, and would keep agreeing
/// with an implementation that had drifted in the same direction.
/// </para>
/// </remarks>
public sealed class DatumResolverTests
{
    private static readonly FeatureId Owner = FeatureId.New();

    private static readonly DatumReference Front = new DatumReference.OnGeometry(FeatureId.None, "Front");

    private static readonly DatumReference Top = new DatumReference.OnGeometry(FeatureId.None, "Top");

    private static readonly DatumReference Origin = new DatumReference.OnGeometry(FeatureId.None, "Origin");

    // --- What a resolved datum carries -------------------------------------------------------

    [Fact]
    public void AResolvedDatumCarriesTheNameAndOwnerItWillBeFoundBy()
    {
        DatumDefinition definition = new DatumDefinition.PlaneOffsetFrom("Offset1", Top, 10);

        DatumResolution result = Resolve(definition);

        result.IsResolved.Should().BeTrue();

        // The pair Document.FindReference looks up by. A datum resolved under the wrong owner is
        // one nothing that names it can find.
        result.Geometry!.Owner.Should().Be(Owner);
        result.Geometry.Name.Should().Be("Offset1");
    }

    // --- The plane role ------------------------------------------------------------------------

    [Fact]
    public void APlaneRoleAcceptsADatumPlane()
    {
        DatumResolution result = Resolve(new DatumDefinition.PlaneOffsetFrom("d", Top, 0));

        Plane(result).Normal.Should().Be(Vec3d.UnitZ);
    }

    [Fact]
    public void APlaneRoleAcceptsACoordinateSystemAsItsXYPlane()
    {
        FeatureId maker = FeatureId.New();
        Document document = DocumentWith(new ReferenceGeometry.CoordinateSystem(
            maker, "CS1", new Vec3d(1, 2, 3), Vec3d.UnitX, Vec3d.UnitY));

        DatumResolution result = Resolve(
            new DatumDefinition.PlaneOffsetFrom("d", new DatumReference.OnGeometry(maker, "CS1"), 0),
            document);

        // Its XY plane: normal along the frame's Z, through the frame's origin.
        Plane(result).Normal.Should().Be(Vec3d.UnitY);
        Plane(result).Contains(new Vec3d(1, 2, 3)).Should().BeTrue();
    }

    [Fact]
    public void APlaneRoleAcceptsAPlanarFace()
    {
        Scenario scenario = new();
        SubEntity face = scenario.NewFace();

        DatumResolution result = Resolve(
            new DatumDefinition.PlaneOffsetFrom("d", scenario.Reference(face), 0),
            scenario: scenario,
            planeOf: _ => Math.Plane.FromPointNormal(new Vec3d(0, 0, 4), Vec3d.UnitZ));

        Plane(result).Contains(new Vec3d(0, 0, 4)).Should().BeTrue();
    }

    [Fact]
    public void APlaneRoleRefusesAFaceThatIsNotFlat()
    {
        Scenario scenario = new();
        SubEntity face = scenario.NewFace();

        DatumResolution result = Resolve(
            new DatumDefinition.PlaneOffsetFrom("d", scenario.Reference(face), 0),
            scenario: scenario,
            planeOf: _ => null); // e.g. the kernel reports the face is cylindrical

        // Found exactly as named, and unusable in this role: a different repair from a name that
        // no longer resolves, which is why the two are different outcomes.
        result.Outcome.Should().Be(DatumResolutionOutcome.NotUsable);
    }

    [Fact]
    public void APlaneRoleRefusesAnAxisNamedWhereAPlaneWasWanted()
    {
        FeatureId maker = FeatureId.New();
        Document document = DocumentWith(
            new ReferenceGeometry.Axis(maker, "Axis1", Vec3d.Zero, Vec3d.UnitX));

        DatumResolution result = Resolve(
            new DatumDefinition.PlaneOffsetFrom("d", new DatumReference.OnGeometry(maker, "Axis1"), 5),
            document);

        result.Outcome.Should().Be(DatumResolutionOutcome.NotFound);
        result.Reason.Should().Contain("axis");
    }

    [Fact]
    public void APlaneRoleRefusesADegenerateNormalRatherThanThrowing()
    {
        FeatureId maker = FeatureId.New();
        Document document = DocumentWith(
            new ReferenceGeometry.Plane(maker, "Bad", Vec3d.Zero, Vec3d.Zero));

        DatumResolution result = Resolve(
            new DatumDefinition.PlaneOffsetFrom("d", new DatumReference.OnGeometry(maker, "Bad"), 5),
            document);

        // A rebuild resolves every reference of every feature in the dirty set (§5.4); one corrupt
        // datum throwing would take features that have nothing to do with it down too.
        result.Outcome.Should().Be(DatumResolutionOutcome.Degenerate);
    }

    [Fact]
    public void ANameThatAnswersToNothingIsNotFound()
    {
        DatumResolution result = Resolve(new DatumDefinition.PlaneOffsetFrom(
            "d", new DatumReference.OnGeometry(FeatureId.None, "NoSuchPlane"), 1));

        result.Outcome.Should().Be(DatumResolutionOutcome.NotFound);
        result.Reason.Should().Contain("NoSuchPlane");
    }

    // --- The point role ------------------------------------------------------------------------

    [Fact]
    public void APointRoleAcceptsADatumPoint()
    {
        DatumResolution result = Resolve(new DatumDefinition.PointAt("d", Origin));

        Position(result).Should().Be(Vec3d.Zero);
    }

    [Fact]
    public void APointRoleAcceptsAVertex()
    {
        Scenario scenario = new();
        SubEntity vertex = scenario.NewVertex();

        DatumResolution result = Resolve(
            new DatumDefinition.PointAt("d", scenario.Reference(vertex)),
            scenario: scenario,
            pointOf: _ => new Vec3d(3, 4, 5));

        // The whole point of the construction: a vertex has no name of its own, and this gives it
        // one that a later feature can hold on to.
        Position(result).Should().Be(new Vec3d(3, 4, 5));
    }

    [Fact]
    public void APointRoleAcceptsACoordinateSystemAsItsOrigin()
    {
        FeatureId maker = FeatureId.New();
        Document document = DocumentWith(new ReferenceGeometry.CoordinateSystem(
            maker, "CS1", new Vec3d(7, 8, 9), Vec3d.UnitX, Vec3d.UnitZ));

        DatumResolution result = Resolve(
            new DatumDefinition.PointAt("d", new DatumReference.OnGeometry(maker, "CS1")),
            document);

        Position(result).Should().Be(new Vec3d(7, 8, 9));
    }

    [Fact]
    public void APointRoleRefusesAPlane()
    {
        DatumResolution result = Resolve(new DatumDefinition.PointAt("d", Top));

        result.Outcome.Should().Be(DatumResolutionOutcome.NotFound);
        result.Reason.Should().Contain("plane");
    }

    // --- The axis role -------------------------------------------------------------------------

    [Fact]
    public void AnAxisRoleAcceptsADatumAxis()
    {
        FeatureId maker = FeatureId.New();
        Document document = DocumentWith(
            new ReferenceGeometry.Axis(maker, "Axis1", new Vec3d(0, 0, 1), new Vec3d(0, 5, 0)));

        DatumResolution result = Resolve(
            new DatumDefinition.PointWhereAxisMeetsPlane(
                "d", new DatumReference.OnGeometry(maker, "Axis1"), Front),
            document);

        // Front is the world XZ plane; an axis along Y through (0, 0, 1) crosses it there.
        Position(result).Should().Be(new Vec3d(0, 0, 1));
    }

    [Fact]
    public void AnAxisRoleAcceptsAStraightEdge()
    {
        Scenario scenario = new();
        SubEntity edge = scenario.NewEdge();

        DatumResolution result = Resolve(
            new DatumDefinition.AxisAlongEdge("d", scenario.Reference(edge)),
            scenario: scenario,
            curveOf: _ => new WorldCurve.Line(new Vec3d(1, 0, 0), new Vec3d(1, 0, 6)));

        ReferenceGeometry.Axis axis = Axis(result);
        axis.Direction.IsParallelTo(Vec3d.UnitZ).Should().BeTrue();
        LiesOn(axis, new Vec3d(1, 0, 0)).Should().BeTrue();
        LiesOn(axis, new Vec3d(1, 0, 6)).Should().BeTrue();
    }

    [Fact]
    public void AnAxisRoleRefusesAnEdgeThatIsNotStraight()
    {
        Scenario scenario = new();
        SubEntity edge = scenario.NewEdge();

        DatumResolution result = Resolve(
            new DatumDefinition.AxisAlongEdge("d", scenario.Reference(edge)),
            scenario: scenario,
            curveOf: _ => WorldCurve.Circle.Full(Vec3d.Zero, Vec3d.UnitZ, Vec3d.UnitX, 3));

        result.Outcome.Should().Be(DatumResolutionOutcome.NotUsable);
        result.Reason.Should().Contain("straight");
    }

    [Fact]
    public void AnAxisRoleRefusesADatumAxisWithNoDirection()
    {
        FeatureId maker = FeatureId.New();
        Document document = DocumentWith(
            new ReferenceGeometry.Axis(maker, "Bad", Vec3d.Zero, Vec3d.Zero));

        DatumResolution result = Resolve(
            new DatumDefinition.AxisAlongEdge("d", new DatumReference.OnGeometry(maker, "Bad")),
            document);

        result.Outcome.Should().Be(DatumResolutionOutcome.Degenerate);
    }

    // --- The curve role ------------------------------------------------------------------------

    [Fact]
    public void ACurveRoleRefusesReferenceGeometry()
    {
        // A datum axis is an infinite line and has no extent for "a fraction along" to mean
        // anything about, so the constructions that ask about extent want an edge specifically.
        DatumResolution result = Resolve(new DatumDefinition.PointAlongEdge("d", Top, 0.5));

        result.Outcome.Should().Be(DatumResolutionOutcome.NotFound);
        result.Reason.Should().Contain("edge");
    }

    [Fact]
    public void ACurveRoleRefusesAnEdgeThisBuildCannotDescribe()
    {
        Scenario scenario = new();
        SubEntity edge = scenario.NewEdge();

        DatumResolution result = Resolve(
            new DatumDefinition.PointAlongEdge("d", scenario.Reference(edge), 0.5),
            scenario: scenario,
            curveOf: _ => null); // e.g. a spline

        result.Outcome.Should().Be(DatumResolutionOutcome.NotUsable);
    }

    // --- Naming failures -----------------------------------------------------------------------

    [Fact]
    public void TopologyCannotResolveWithNoResolver()
    {
        Scenario scenario = new();
        SubEntity face = scenario.NewFace();

        DatumResolution result = DatumResolver.Resolve(
            new DatumDefinition.PlaneOffsetFrom("d", scenario.Reference(face), 1),
            Owner,
            Document.Empty(),
            Owner);

        result.Outcome.Should().Be(DatumResolutionOutcome.NotFound);
    }

    [Fact]
    public void ATopologyNameThatTracesToTwoCandidatesIsAmbiguous()
    {
        Scenario scenario = new();
        (PersistentName name, _, _) = scenario.NewSplitFace();

        DatumResolution result = Resolve(
            new DatumDefinition.PlaneOffsetFrom("d", new DatumReference.OnTopology(name), 1),
            scenario: scenario,
            planeOf: _ => Math.Plane.XY);

        // A datum input is always exactly one entity, so unlike EntityReference there is no
        // multiplicity policy that could turn a split into an answer.
        result.Outcome.Should().Be(DatumResolutionOutcome.Ambiguous);
    }

    [Fact]
    public void ATopologyNameOfTheWrongKindIsNotFound()
    {
        Scenario scenario = new();
        SubEntity edge = scenario.NewEdge();

        DatumResolution result = Resolve(
            new DatumDefinition.PlaneOffsetFrom("d", scenario.Reference(edge), 1),
            scenario: scenario,
            planeOf: _ => Math.Plane.XY);

        result.Outcome.Should().Be(DatumResolutionOutcome.NotFound);
        result.Reason.Should().Contain("face");
    }

    // --- Plane constructions -------------------------------------------------------------------

    [Fact]
    public void AnOffsetPlaneIsParallelToItsSourceAtTheDistanceAsked()
    {
        DatumResolution result = Resolve(new DatumDefinition.PlaneOffsetFrom("d", Top, 10));

        Plane plane = Plane(result);
        plane.Normal.Should().Be(Vec3d.UnitZ);
        plane.SignedDistanceTo(Vec3d.Zero).Should().BeApproximately(-10, 1e-12);
    }

    [Fact]
    public void ANegativeOffsetGoesTheOtherWayWithoutFlippingTheSource()
    {
        DatumResolution result = Resolve(new DatumDefinition.PlaneOffsetFrom("d", Top, -10));

        Plane plane = Plane(result);

        // Signed, so the plane can be put on either side without asking the user to flip the thing
        // they measured from -- which would also flip everything else built on it.
        plane.Normal.Should().Be(Vec3d.UnitZ);
        plane.SignedDistanceTo(Vec3d.Zero).Should().BeApproximately(10, 1e-12);
    }

    [Fact]
    public void AnOffsetPlaneRefusesADistanceThatIsNotANumber()
    {
        DatumResolution result = Resolve(new DatumDefinition.PlaneOffsetFrom("d", Top, double.NaN));

        result.Outcome.Should().Be(DatumResolutionOutcome.Degenerate);
    }

    [Fact]
    public void APlaneThroughAPointIsParallelToItsSourceAndContainsThePoint()
    {
        FeatureId maker = FeatureId.New();
        Document document = DocumentWith(
            new ReferenceGeometry.Point(maker, "P1", new Vec3d(1, 2, 3)));

        DatumResolution result = Resolve(
            new DatumDefinition.PlaneThroughPoint(
                "d", new DatumReference.OnGeometry(maker, "P1"), Top),
            document);

        Plane plane = Plane(result);
        plane.Normal.IsParallelTo(Vec3d.UnitZ).Should().BeTrue();
        plane.Contains(new Vec3d(1, 2, 3)).Should().BeTrue();
    }

    [Fact]
    public void APlaneAtNoAngleIsThePlaneItStartedFrom()
    {
        FeatureId maker = FeatureId.New();
        Document document = DocumentWith(
            new ReferenceGeometry.Axis(maker, "X", Vec3d.Zero, Vec3d.UnitX));

        DatumResolution result = Resolve(
            new DatumDefinition.PlaneAtAngle(
                "d", Top, new DatumReference.OnGeometry(maker, "X"), 0),
            document);

        // The property the "axis must lie in the plane" precondition exists to protect. An axis
        // parallel to the plane but off it would give an offset copy here instead.
        Plane(result).IsNear(Math.Plane.XY).Should().BeTrue();
    }

    [Fact]
    public void APlaneAtAnAngleStillContainsTheAxisItTurnedAbout()
    {
        FeatureId maker = FeatureId.New();
        Document document = DocumentWith(
            new ReferenceGeometry.Axis(maker, "X", new Vec3d(2, 0, 0), Vec3d.UnitX));

        DatumResolution result = Resolve(
            new DatumDefinition.PlaneAtAngle(
                "d", Top, new DatumReference.OnGeometry(maker, "X"), System.Math.PI / 3),
            document);

        Plane plane = Plane(result);
        plane.Contains(new Vec3d(2, 0, 0)).Should().BeTrue();
        plane.Normal.IsPerpendicularTo(Vec3d.UnitX).Should().BeTrue();
        plane.Normal.AngleTo(Vec3d.UnitZ).Should().BeApproximately(System.Math.PI / 3, 1e-12);
    }

    [Fact]
    public void APlaneAtAQuarterTurnTurnsTheRightWay()
    {
        FeatureId maker = FeatureId.New();
        Document document = DocumentWith(
            new ReferenceGeometry.Axis(maker, "X", Vec3d.Zero, Vec3d.UnitX));

        DatumResolution result = Resolve(
            new DatumDefinition.PlaneAtAngle(
                "d", Top, new DatumReference.OnGeometry(maker, "X"), System.Math.PI / 2),
            document);

        // Anticlockwise about +X by the right-hand rule carries +Z onto -Y, not onto +Y.
        Plane(result).Normal.IsNear(-Vec3d.UnitY).Should().BeTrue();
    }

    [Fact]
    public void APlaneAtAnAngleRefusesAnAxisNotParallelToIt()
    {
        FeatureId maker = FeatureId.New();
        Document document = DocumentWith(
            new ReferenceGeometry.Axis(maker, "Z", Vec3d.Zero, Vec3d.UnitZ));

        DatumResolution result = Resolve(
            new DatumDefinition.PlaneAtAngle(
                "d", Top, new DatumReference.OnGeometry(maker, "Z"), 1),
            document);

        result.Outcome.Should().Be(DatumResolutionOutcome.Degenerate);
    }

    [Fact]
    public void APlaneAtAnAngleRefusesAnAxisParallelToItButOffIt()
    {
        FeatureId maker = FeatureId.New();
        Document document = DocumentWith(
            new ReferenceGeometry.Axis(maker, "X", new Vec3d(0, 0, 5), Vec3d.UnitX));

        DatumResolution result = Resolve(
            new DatumDefinition.PlaneAtAngle(
                "d", Top, new DatumReference.OnGeometry(maker, "X"), 0),
            document);

        // Rotating about it would work arithmetically and would put the result five units away
        // from the plane at an angle of zero, which is the answer disagreeing with itself.
        result.Outcome.Should().Be(DatumResolutionOutcome.Degenerate);
        result.Reason.Should().Contain("lie in it");
    }

    [Fact]
    public void APlaneThroughThreePointsContainsAllThree()
    {
        FeatureId maker = FeatureId.New();
        Vec3d a = new(1, 0, 0);
        Vec3d b = new(0, 2, 0);
        Vec3d c = new(0, 0, 3);
        Document document = DocumentWith(
            new ReferenceGeometry.Point(maker, "A", a),
            new ReferenceGeometry.Point(maker, "B", b),
            new ReferenceGeometry.Point(maker, "C", c));

        DatumResolution result = Resolve(
            new DatumDefinition.PlaneThroughThreePoints(
                "d",
                new DatumReference.OnGeometry(maker, "A"),
                new DatumReference.OnGeometry(maker, "B"),
                new DatumReference.OnGeometry(maker, "C")),
            document);

        Plane plane = Plane(result);
        plane.Contains(a).Should().BeTrue();
        plane.Contains(b).Should().BeTrue();
        plane.Contains(c).Should().BeTrue();

        // The order is a choice about which way the plane faces, not merely a listing.
        plane.Normal.IsNear(Vec3d.Cross(b - a, c - a).Normalized()).Should().BeTrue();
    }

    [Fact]
    public void APlaneThroughThreeCollinearPointsIsDegenerate()
    {
        FeatureId maker = FeatureId.New();
        Document document = DocumentWith(
            new ReferenceGeometry.Point(maker, "A", new Vec3d(0, 0, 0)),
            new ReferenceGeometry.Point(maker, "B", new Vec3d(1, 1, 1)),
            new ReferenceGeometry.Point(maker, "C", new Vec3d(2, 2, 2)));

        DatumResolution result = Resolve(
            new DatumDefinition.PlaneThroughThreePoints(
                "d",
                new DatumReference.OnGeometry(maker, "A"),
                new DatumReference.OnGeometry(maker, "B"),
                new DatumReference.OnGeometry(maker, "C")),
            document);

        result.Outcome.Should().Be(DatumResolutionOutcome.Degenerate);
    }

    [Fact]
    public void AMidplaneIsEquidistantFromBothSources()
    {
        FeatureId maker = FeatureId.New();
        Document document = DocumentWith(
            new ReferenceGeometry.Plane(maker, "Low", new Vec3d(0, 0, 2), Vec3d.UnitZ),
            new ReferenceGeometry.Plane(maker, "High", new Vec3d(0, 0, 10), Vec3d.UnitZ));

        DatumResolution result = Resolve(
            new DatumDefinition.PlaneMidwayBetween(
                "d",
                new DatumReference.OnGeometry(maker, "Low"),
                new DatumReference.OnGeometry(maker, "High")),
            document);

        Plane plane = Plane(result);
        plane.SignedDistanceTo(new Vec3d(0, 0, 2)).Should().BeApproximately(-4, 1e-12);
        plane.SignedDistanceTo(new Vec3d(0, 0, 10)).Should().BeApproximately(4, 1e-12);
    }

    [Fact]
    public void AMidplaneIsTheSameWhenTheSecondPlaneFacesTheOtherWay()
    {
        FeatureId maker = FeatureId.New();
        Document document = DocumentWith(
            new ReferenceGeometry.Plane(maker, "Low", new Vec3d(0, 0, 2), Vec3d.UnitZ),
            new ReferenceGeometry.Plane(maker, "High", new Vec3d(0, 0, 10), -Vec3d.UnitZ));

        DatumResolution result = Resolve(
            new DatumDefinition.PlaneMidwayBetween(
                "d",
                new DatumReference.OnGeometry(maker, "Low"),
                new DatumReference.OnGeometry(maker, "High")),
            document);

        // Two planes that face opposite ways are still parallel, and the plane halfway between
        // them is the same one either way round.
        Plane(result).Contains(new Vec3d(0, 0, 6)).Should().BeTrue();
    }

    [Fact]
    public void AMidplaneBetweenPlanesThatAreNotParallelIsDegenerate()
    {
        DatumResolution result = Resolve(new DatumDefinition.PlaneMidwayBetween("d", Top, Front));

        // A pair that meet has two bisecting planes at right angles to each other, and nothing in
        // the reference says which was meant.
        result.Outcome.Should().Be(DatumResolutionOutcome.Degenerate);
    }

    // --- Axis constructions --------------------------------------------------------------------

    [Fact]
    public void AnAxisThroughTwoPointsPassesThroughBoth()
    {
        FeatureId maker = FeatureId.New();
        Vec3d from = new(1, 1, 1);
        Vec3d to = new(4, 5, 6);
        Document document = DocumentWith(
            new ReferenceGeometry.Point(maker, "A", from),
            new ReferenceGeometry.Point(maker, "B", to));

        DatumResolution result = Resolve(
            new DatumDefinition.AxisThroughTwoPoints(
                "d",
                new DatumReference.OnGeometry(maker, "A"),
                new DatumReference.OnGeometry(maker, "B")),
            document);

        ReferenceGeometry.Axis axis = Axis(result);
        LiesOn(axis, from).Should().BeTrue();
        LiesOn(axis, to).Should().BeTrue();
    }

    [Fact]
    public void AnAxisThroughTwoCoincidentPointsIsDegenerate()
    {
        FeatureId maker = FeatureId.New();
        Document document = DocumentWith(
            new ReferenceGeometry.Point(maker, "A", new Vec3d(1, 1, 1)),
            new ReferenceGeometry.Point(maker, "B", new Vec3d(1, 1, 1)));

        DatumResolution result = Resolve(
            new DatumDefinition.AxisThroughTwoPoints(
                "d",
                new DatumReference.OnGeometry(maker, "A"),
                new DatumReference.OnGeometry(maker, "B")),
            document);

        result.Outcome.Should().Be(DatumResolutionOutcome.Degenerate);
    }

    [Fact]
    public void TheAxisWhereTwoPlanesMeetLiesInBoth()
    {
        FeatureId maker = FeatureId.New();
        Document document = DocumentWith(
            new ReferenceGeometry.Plane(maker, "A", new Vec3d(0, 0, 3), Vec3d.UnitZ),
            new ReferenceGeometry.Plane(maker, "B", new Vec3d(2, 0, 0), Vec3d.UnitX));

        DatumResolution result = Resolve(
            new DatumDefinition.AxisWherePlanesMeet(
                "d",
                new DatumReference.OnGeometry(maker, "A"),
                new DatumReference.OnGeometry(maker, "B")),
            document);

        ReferenceGeometry.Axis axis = Axis(result);
        Plane first = Math.Plane.FromPointNormal(new Vec3d(0, 0, 3), Vec3d.UnitZ);
        Plane second = Math.Plane.FromPointNormal(new Vec3d(2, 0, 0), Vec3d.UnitX);

        first.Contains(axis.Origin).Should().BeTrue();
        second.Contains(axis.Origin).Should().BeTrue();
        axis.Direction.IsPerpendicularTo(first.Normal).Should().BeTrue();
        axis.Direction.IsPerpendicularTo(second.Normal).Should().BeTrue();
    }

    [Fact]
    public void TheAxisWhereTwoObliquePlanesMeetLiesInBoth()
    {
        // Both planes orthogonal makes the cosine term of the origin formula vanish, so it would
        // pass with that term dropped entirely. This pair does not.
        FeatureId maker = FeatureId.New();
        Plane first = Math.Plane.XY;
        Plane second = Math.Plane.FromPointNormal(new Vec3d(0, 2, 0), new Vec3d(0, 1, 1));
        Document document = DocumentWith(
            new ReferenceGeometry.Plane(maker, "A", first.Origin, first.Normal),
            new ReferenceGeometry.Plane(maker, "B", second.Origin, second.Normal));

        DatumResolution result = Resolve(
            new DatumDefinition.AxisWherePlanesMeet(
                "d",
                new DatumReference.OnGeometry(maker, "A"),
                new DatumReference.OnGeometry(maker, "B")),
            document);

        ReferenceGeometry.Axis axis = Axis(result);
        first.Contains(axis.Origin).Should().BeTrue();
        second.Contains(axis.Origin).Should().BeTrue();

        // The origin recorded is the point of the line closest to the world origin, which is what
        // makes a rebuild that changed nothing report the same axis (ADR-0011).
        Vec3d.Dot(axis.Origin, axis.Direction).Should().BeApproximately(0, 1e-12);
    }

    [Fact]
    public void TwoParallelPlanesHaveNoAxisWhereTheyMeet()
    {
        FeatureId maker = FeatureId.New();
        Document document = DocumentWith(
            new ReferenceGeometry.Plane(maker, "A", Vec3d.Zero, Vec3d.UnitZ),
            new ReferenceGeometry.Plane(maker, "B", new Vec3d(0, 0, 5), Vec3d.UnitZ));

        DatumResolution result = Resolve(
            new DatumDefinition.AxisWherePlanesMeet(
                "d",
                new DatumReference.OnGeometry(maker, "A"),
                new DatumReference.OnGeometry(maker, "B")),
            document);

        result.Outcome.Should().Be(DatumResolutionOutcome.Degenerate);
    }

    [Fact]
    public void AnAxisNormalToAPlaneKeepsThePointItWasGiven()
    {
        FeatureId maker = FeatureId.New();
        Document document = DocumentWith(
            new ReferenceGeometry.Point(maker, "P", new Vec3d(1, 2, 7)));

        DatumResolution result = Resolve(
            new DatumDefinition.AxisNormalToPlane(
                "d", Top, new DatumReference.OnGeometry(maker, "P")),
            document);

        ReferenceGeometry.Axis axis = Axis(result);
        axis.Direction.IsParallelTo(Vec3d.UnitZ).Should().BeTrue();

        // Not projected onto the plane: quietly moving the axis to the foot of the perpendicular
        // would put it somewhere the user did not point at.
        LiesOn(axis, new Vec3d(1, 2, 7)).Should().BeTrue();
        axis.Origin.Should().Be(new Vec3d(1, 2, 7));
    }

    // --- Point constructions -------------------------------------------------------------------

    [Fact]
    public void ThePointAtTheCentreOfACircularEdgeIsItsCentre()
    {
        Scenario scenario = new();
        SubEntity edge = scenario.NewEdge();

        DatumResolution result = Resolve(
            new DatumDefinition.PointAtCentreOf("d", scenario.Reference(edge)),
            scenario: scenario,
            curveOf: _ => WorldCurve.Circle.Full(new Vec3d(5, 5, 0), Vec3d.UnitZ, Vec3d.UnitX, 3));

        // The centre is not on the curve at all, which is why this is not the midpoint.
        Position(result).Should().Be(new Vec3d(5, 5, 0));
    }

    [Fact]
    public void AStraightEdgeHasNoCentre()
    {
        Scenario scenario = new();
        SubEntity edge = scenario.NewEdge();

        DatumResolution result = Resolve(
            new DatumDefinition.PointAtCentreOf("d", scenario.Reference(edge)),
            scenario: scenario,
            curveOf: _ => new WorldCurve.Line(Vec3d.Zero, new Vec3d(4, 0, 0)));

        result.Outcome.Should().Be(DatumResolutionOutcome.NotUsable);
        result.Reason.Should().Contain("midpoint");
    }

    [Fact]
    public void AnAxisMeetsAPlaneWhereItCrossesIt()
    {
        FeatureId maker = FeatureId.New();
        Document document = DocumentWith(
            new ReferenceGeometry.Axis(maker, "A", new Vec3d(1, 2, 0), Vec3d.UnitZ),
            new ReferenceGeometry.Plane(maker, "P", new Vec3d(0, 0, 4), Vec3d.UnitZ));

        DatumResolution result = Resolve(
            new DatumDefinition.PointWhereAxisMeetsPlane(
                "d",
                new DatumReference.OnGeometry(maker, "A"),
                new DatumReference.OnGeometry(maker, "P")),
            document);

        Position(result).Should().Be(new Vec3d(1, 2, 4));
    }

    [Fact]
    public void AnEdgeUsedAsAnAxisReachesAPlaneItStopsShortOf()
    {
        Scenario scenario = new();
        SubEntity edge = scenario.NewEdge();
        FeatureId maker = FeatureId.New();
        Document document = DocumentWith(
            new ReferenceGeometry.Plane(maker, "P", new Vec3d(0, 0, 100), Vec3d.UnitZ));

        DatumResolution result = Resolve(
            new DatumDefinition.PointWhereAxisMeetsPlane(
                "d", scenario.Reference(edge), new DatumReference.OnGeometry(maker, "P")),
            document,
            scenario,
            curveOf: _ => new WorldCurve.Line(Vec3d.Zero, new Vec3d(0, 0, 1)));

        // What separates this from a sketch's Intersect, which reports nothing when the edge stops
        // short: a datum is a construction, and an axis is by definition unbounded.
        Position(result).Should().Be(new Vec3d(0, 0, 100));
    }

    [Fact]
    public void AnAxisParallelToAPlaneNeverMeetsItAtOnePoint()
    {
        FeatureId maker = FeatureId.New();
        Document document = DocumentWith(
            new ReferenceGeometry.Axis(maker, "A", new Vec3d(0, 0, 1), Vec3d.UnitX));

        DatumResolution result = Resolve(
            new DatumDefinition.PointWhereAxisMeetsPlane(
                "d", new DatumReference.OnGeometry(maker, "A"), Top),
            document);

        result.Outcome.Should().Be(DatumResolutionOutcome.Degenerate);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.5, 2.0)]
    [InlineData(1.0, 4.0)]
    public void APointAlongAStraightEdgeIsThatFractionOfTheWay(double fraction, double expected)
    {
        Scenario scenario = new();
        SubEntity edge = scenario.NewEdge();

        DatumResolution result = Resolve(
            new DatumDefinition.PointAlongEdge("d", scenario.Reference(edge), fraction),
            scenario: scenario,
            curveOf: _ => new WorldCurve.Line(Vec3d.Zero, new Vec3d(4, 0, 0)));

        Position(result).Should().Be(new Vec3d(expected, 0, 0));
    }

    [Fact]
    public void APointAlongAnArcRunsFromItsStartToItsEnd()
    {
        Scenario scenario = new();
        SubEntity edge = scenario.NewEdge();
        WorldCurve.Circle arc = new(
            Vec3d.Zero, Vec3d.UnitZ, Vec3d.UnitX, 2, 0, System.Math.PI / 2);

        At(0).Should().BeTrue();
        Position(Along(0)).IsNear(new Vec3d(2, 0, 0)).Should().BeTrue();
        Position(Along(1)).IsNear(new Vec3d(0, 2, 0)).Should().BeTrue();
        Position(Along(0.5)).IsNear(new Vec3d(System.Math.Sqrt(2), System.Math.Sqrt(2), 0))
            .Should().BeTrue();

        bool At(double f) => Along(f).IsResolved;

        DatumResolution Along(double f) => Resolve(
            new DatumDefinition.PointAlongEdge("d", scenario.Reference(edge), f),
            scenario: scenario,
            curveOf: _ => arc);
    }

    [Fact]
    public void APointAlongAnArcFollowsItsOwnSenseWhenItsEndAngleWrapsPastZero()
    {
        Scenario scenario = new();
        SubEntity edge = scenario.NewEdge();

        // Three quarters of a turn anticlockwise from 270 degrees round to 90, so the halfway
        // point is at 0 degrees -- not at 180, which is where the shorter reading would put it.
        WorldCurve.Circle arc = new(
            Vec3d.Zero,
            Vec3d.UnitZ,
            Vec3d.UnitX,
            2,
            3 * System.Math.PI / 2,
            System.Math.PI / 2);

        DatumResolution result = Resolve(
            new DatumDefinition.PointAlongEdge("d", scenario.Reference(edge), 0.5),
            scenario: scenario,
            curveOf: _ => arc);

        Position(result).IsNear(new Vec3d(2, 0, 0)).Should().BeTrue();
    }

    [Fact]
    public void HalfwayAlongAFullCircleIsTheOtherSideOfIt()
    {
        Scenario scenario = new();
        SubEntity edge = scenario.NewEdge();

        // A full circle reports its sweep as exactly zero once wrapped, which would put every
        // fraction at the start angle. IsFull is what tells a whole turn from a zero-length arc.
        DatumResolution result = Resolve(
            new DatumDefinition.PointAlongEdge("d", scenario.Reference(edge), 0.5),
            scenario: scenario,
            curveOf: _ => WorldCurve.Circle.Full(Vec3d.Zero, Vec3d.UnitZ, Vec3d.UnitX, 2));

        Position(result).IsNear(new Vec3d(-2, 0, 0)).Should().BeTrue();
    }

    [Fact]
    public void APointAlongAnArcUsesTheCirclesOwnReferenceDirection()
    {
        Scenario scenario = new();
        SubEntity edge = scenario.NewEdge();

        // The reference direction need not be unit length or already perpendicular to the normal,
        // and where angle zero sits has to come from the edge rather than be invented -- two
        // queries against the same edge must agree about where its start angle is.
        WorldCurve.Circle arc = new(
            Vec3d.Zero, Vec3d.UnitZ, new Vec3d(0, 7, 9), 2, 0, System.Math.PI / 2);

        DatumResolution result = Resolve(
            new DatumDefinition.PointAlongEdge("d", scenario.Reference(edge), 0),
            scenario: scenario,
            curveOf: _ => arc);

        Position(result).IsNear(new Vec3d(0, 2, 0)).Should().BeTrue();
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void APointOutsideAnEdgeIsRefusedRatherThanExtrapolated(double fraction)
    {
        Scenario scenario = new();
        SubEntity edge = scenario.NewEdge();

        DatumResolution result = Resolve(
            new DatumDefinition.PointAlongEdge("d", scenario.Reference(edge), fraction),
            scenario: scenario,
            curveOf: _ => new WorldCurve.Line(Vec3d.Zero, new Vec3d(4, 0, 0)));

        result.Outcome.Should().Be(DatumResolutionOutcome.Degenerate);
    }

    [Fact]
    public void ACircularEdgeWithNoUsablePlaneIsDegenerate()
    {
        Scenario scenario = new();
        SubEntity edge = scenario.NewEdge();

        DatumResolution result = Resolve(
            new DatumDefinition.PointAlongEdge("d", scenario.Reference(edge), 0.5),
            scenario: scenario,
            curveOf: _ => new WorldCurve.Circle(
                Vec3d.Zero, Vec3d.UnitZ, Vec3d.UnitZ, 2, 0, System.Math.PI));

        // The reference direction is parallel to the normal, so the edge does not describe a
        // circle at all -- degenerate rather than unusable, since nothing is being declined.
        result.Outcome.Should().Be(DatumResolutionOutcome.Degenerate);
    }

    [Fact]
    public void ACircularEdgeWithNoUsableRadiusIsDegenerate()
    {
        Scenario scenario = new();
        SubEntity edge = scenario.NewEdge();

        DatumResolution result = Resolve(
            new DatumDefinition.PointAlongEdge("d", scenario.Reference(edge), 0.5),
            scenario: scenario,
            curveOf: _ => WorldCurve.Circle.Full(Vec3d.Zero, Vec3d.UnitZ, Vec3d.UnitX, 0));

        result.Outcome.Should().Be(DatumResolutionOutcome.Degenerate);
    }

    // --- Helpers -------------------------------------------------------------------------------

    private static DatumResolution Resolve(
        DatumDefinition definition,
        Document? document = null,
        Scenario? scenario = null,
        Func<SubEntity, Plane?>? planeOf = null,
        Func<SubEntity, Vec3d?>? pointOf = null,
        Func<SubEntity, WorldCurve?>? curveOf = null)
        => DatumResolver.Resolve(
            definition,
            Owner,
            document ?? Document.Empty(),
            Owner,
            scenario?.Resolver(),
            planeOf,
            pointOf,
            curveOf);

    private static Plane Plane(DatumResolution result)
    {
        result.IsResolved.Should().BeTrue(result.Reason);
        ReferenceGeometry.Plane plane = result.Geometry.Should().BeOfType<ReferenceGeometry.Plane>().Subject;

        return Math.Plane.FromPointNormal(plane.Origin, plane.Normal);
    }

    private static ReferenceGeometry.Axis Axis(DatumResolution result)
    {
        result.IsResolved.Should().BeTrue(result.Reason);

        return result.Geometry.Should().BeOfType<ReferenceGeometry.Axis>().Subject;
    }

    private static Vec3d Position(DatumResolution result)
    {
        result.IsResolved.Should().BeTrue(result.Reason);

        return result.Geometry.Should().BeOfType<ReferenceGeometry.Point>().Subject.Position;
    }

    /// <summary>Whether a point lies on an axis's infinite line.</summary>
    private static bool LiesOn(ReferenceGeometry.Axis axis, Vec3d point)
        => (point - axis.Origin).PerpendicularTo(axis.Direction).Length <= Tolerance.Linear;

    private static Document DocumentWith(params ReferenceGeometry[] references)
    {
        DocumentSession session = new();

        using (IDocumentTransaction tx = session.BeginTransaction("Add reference geometry"))
        {
            foreach (ReferenceGeometry reference in references)
            {
                tx.AddReference(reference);
            }

            tx.Commit();
        }

        return session.Current;
    }

    /// <summary>
    /// Just enough rebuild history for a topology reference to resolve through the real naming
    /// tiers — the same shape <see cref="SketchPlaneResolverTests"/> uses.
    /// </summary>
    private sealed class Scenario
    {
        private readonly RebuildHistory.Builder _history = new();
        private readonly KernelShape _shape = new(1);
        private readonly Dictionary<SubEntity, PersistentName> _names = [];
        private ulong _nextTag = 1;

        public SubEntity NewFace() => Named(SubEntityKind.Face);

        public SubEntity NewEdge() => Named(SubEntityKind.Edge);

        public SubEntity NewVertex() => Named(SubEntityKind.Vertex);

        public DatumReference.OnTopology Reference(SubEntity entity) => new(_names[entity]);

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

        private SubEntity Named(SubEntityKind kind)
        {
            SubEntity entity = new(_shape, _nextTag++, kind);
            FeatureId feature = FeatureId.New();

            _history.Add(feature, new HistoryMapBuilder().AddNew(entity, OperationRole.SideWall).Build());
            _names[entity] = PersistentName.Of(
                NameSegment.Of(feature, ProvenanceKind.New, EntityRole.From(OperationRole.SideWall)));

            return entity;
        }
    }
}
