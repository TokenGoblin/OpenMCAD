using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Solver.Sketching;

using Xunit;

namespace OpenMCAD.Solver.Tests;

/// <summary>
/// The sketch interchange form (P4-T04).
/// </summary>
/// <remarks>
/// What a fixture in the sketch corpus (P4-T16) is written in. Not the file format: when a sketch
/// becomes a feature of a document in Phase 5 it will be MessagePack with everything else, because
/// §5.8's first exit criterion is a bit-identical re-save. The same split already exists one layer
/// up, where <c>omcad build</c> takes a JSON spec and writes a MessagePack document.
/// </remarks>
public sealed class SketchFormatTests
{
    [Fact]
    public void EveryKindOfGeometrySurvivesARoundTrip()
    {
        Sketch original = OneOfEverything();

        Sketch read = SketchFormat.Read(SketchFormat.Write(original));

        read.Should().Be(original);
        read.Entities.Ordered.Select(e => e.Kind)
            .Should().Equal(original.Entities.Ordered.Select(e => e.Kind));
    }

    [Fact]
    public void ConstraintsSurviveWithTheirOperandsAndValues()
    {
        Sketch original = ConstraintTests.Rectangleish();

        Sketch read = SketchFormat.Read(SketchFormat.Write(original));

        read.Should().Be(original);
        read.Constraints.Removes.Should().Be(original.Constraints.Removes);
    }

    [Fact]
    public void AReferenceDimensionStaysOne()
    {
        // The one flag whose loss would be silent: a reference dimension read back as driving
        // over-constrains the sketch, and the diagnosis blames a constraint the user never made
        // driving.
        Sketch original = Sketch.Empty
            .With(new SketchCircle(ConstraintTests.Entity(1), Vec2d.Zero, 2))
            .With(ConstraintTests.Constraint(
                ConstraintKind.Radius, 1, [ConstraintTests.Whole(1)], 2) with
            { IsDriving = false });

        SketchFormat.Read(SketchFormat.Write(original)).Constraints.Ordered[0]
            .IsDriving.Should().BeFalse();
    }

    [Fact]
    public void ConstructionGeometryStaysConstruction()
    {
        Sketch original = Sketch.Empty.With(
            new SketchLine(ConstraintTests.Entity(1), Vec2d.Zero, Vec2d.One, IsConstruction: true));

        SketchFormat.Read(SketchFormat.Write(original)).Entities.Ordered[0]
            .IsConstruction.Should().BeTrue();
    }

    [Fact]
    public void OrderIsKept()
    {
        // The solver reads them in this order. A form that did not preserve it would make a sketch
        // converge differently after a save and a reload.
        Sketch original = Sketch.Empty
            .With(new SketchPoint(ConstraintTests.Entity(3), Vec2d.Zero))
            .With(new SketchPoint(ConstraintTests.Entity(1), Vec2d.One))
            .With(new SketchPoint(ConstraintTests.Entity(2), new Vec2d(2, 2)));

        SketchFormat.Read(SketchFormat.Write(original)).Entities.Ordered.Select(e => e.Id)
            .Should().Equal(
                ConstraintTests.Entity(3), ConstraintTests.Entity(1), ConstraintTests.Entity(2));
    }

    [Fact]
    public void WritingIsStable()
    {
        // A corpus is diffed. A form whose output moved between runs would show every fixture as
        // changed on every commit and hide the one that really did.
        Sketch sketch = OneOfEverything();

        SketchFormat.Write(sketch).Should().Be(SketchFormat.Write(sketch));
        SketchFormat.Write(SketchFormat.Read(SketchFormat.Write(sketch)))
            .Should().Be(SketchFormat.Write(sketch), "and a trip through the reader changes nothing");
    }

    [Fact]
    public void ConstraintKindsAreWrittenByNameAndNotByNumber()
    {
        // An ordinal would change meaning the moment a kind was inserted, and a fixture corpus
        // exists precisely to be read years later.
        string json = SketchFormat.Write(ConstraintTests.Rectangleish());

        json.Should().Contain("\"Coincident\"").And.Contain("\"Horizontal\"");
    }

    [Fact]
    public void ASketchFromANewerVersionIsRefusedRatherThanMisread()
    {
        Action read = () => SketchFormat.Read("""{ "version": 99, "entities": [] }""");

        read.Should().Throw<SketchFormatException>().WithMessage("*version 99*");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("""{ "entities": [ { "id": "nonsense", "kind": "point", "at": [0, 0] } ] }""")]
    [InlineData("""{ "entities": [ { "id": "00000000-0000-0000-0000-000000000001", "kind": "trapezium" } ] }""")]
    [InlineData("""{ "entities": [ { "id": "00000000-0000-0000-0000-000000000001", "kind": "point", "at": [0] } ] }""")]
    [InlineData("""{ "constraints": [ { "id": "00000000-0000-0000-0000-000000000001", "kind": "Sideways", "on": [] } ] }""")]
    public void SomethingThatIsNotASketchIsRefusedWithAReason(string json)
    {
        Action read = () => SketchFormat.Read(json);

        read.Should().Throw<SketchFormatException>()
            .Which.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void AnEmptySketchRoundTrips()
    {
        SketchFormat.Read(SketchFormat.Write(Sketch.Empty)).Should().Be(Sketch.Empty);
    }

    [Fact]
    public void ASplinesKnotsAndWeightsSurvive()
    {
        // The entity with the most to lose: four parallel arrays, any of which could be dropped
        // without the others noticing.
        double weight = System.Math.Cos(System.Math.PI / 4);

        SketchBSpline quarter = new(
            ConstraintTests.Entity(1),
            2,
            [new Vec2d(1, 0), new Vec2d(1, 1), new Vec2d(0, 1)],
            [1, weight, 1],
            [0, 1],
            [3, 3]);

        SketchBSpline read = (SketchBSpline)SketchFormat
            .Read(SketchFormat.Write(Sketch.Empty.With(quarter)))
            .Entities.Ordered[0];

        read.Should().Be(quarter);
        read.PoleWeights[1].Should().Be(weight, "an exact circular arc depends on it");
        read.KnotMultiplicities.Should().Equal(3, 3);
    }

    private static Sketch OneOfEverything() => Sketch.Empty
        .With(new SketchPoint(ConstraintTests.Entity(1), new Vec2d(1, 2)))
        .With(new SketchLine(ConstraintTests.Entity(2), Vec2d.Zero, new Vec2d(3, 4)))
        .With(new SketchCircle(ConstraintTests.Entity(3), new Vec2d(1, 1), 2))
        .With(new SketchArc(ConstraintTests.Entity(4), Vec2d.Zero, 3, 0.25, 1.75))
        .With(new SketchEllipse(ConstraintTests.Entity(5), Vec2d.Zero, 5, 3, 0.4))
        .With(new SketchEllipticalArc(ConstraintTests.Entity(6), Vec2d.Zero, 5, 3, 0.4, 0.1, 2.1))
        .With(new SketchParabola(ConstraintTests.Entity(7), Vec2d.Zero, new Vec2d(0, 2), -1, 1))
        .With(new SketchHyperbola(ConstraintTests.Entity(8), Vec2d.Zero, 2, 1, 0.2, -1, 1))
        .With(SketchBSpline.Through(
            ConstraintTests.Entity(9), 3,
            [Vec2d.Zero, new Vec2d(1, 3), new Vec2d(3, 3), new Vec2d(4, 0)]));
}
