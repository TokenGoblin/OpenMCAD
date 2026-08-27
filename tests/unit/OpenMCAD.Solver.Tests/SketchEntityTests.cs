using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Solver.Sketching;

using Xunit;

namespace OpenMCAD.Solver.Tests;

/// <summary>
/// The sketch entity model (P4-T03).
/// </summary>
/// <remarks>
/// <para>
/// The expected points below come from the geometry rather than from running this code and writing
/// down what it produced. Pinning an implementation to its own output proves only that it is
/// consistent, which a wrong implementation also is: a quarter turn round a unit circle centred at
/// the origin ends at (0, 1) whatever this code thinks.
/// </para>
/// <para>
/// The B-spline cases lean on two properties that hold for every correct implementation and for
/// almost no incorrect one: a clamped spline passes exactly through its first and last control
/// points, and a rational quadratic with the right weight is exactly a circular arc.
/// </para>
/// </remarks>
public sealed class SketchEntityTests
{
    private const double Precise = 1e-12;

    [Fact]
    public void ALineRunsFromItsStartToItsEnd()
    {
        SketchLine line = new(Id(1), new Vec2d(1, 2), new Vec2d(4, 6));

        line.PointAt(0).Should().Be(new Vec2d(1, 2));
        line.PointAt(1).Should().Be(new Vec2d(4, 6));
        Near(line.PointAt(0.5), new Vec2d(2.5, 4));

        line.Length.Should().BeApproximately(5, Precise, "3-4-5");
        Near(line.Direction, new Vec2d(0.6, 0.8));
    }

    [Fact]
    public void ACircleGoesRoundAnticlockwiseFromThePositiveXAxis()
    {
        SketchCircle circle = new(Id(1), new Vec2d(10, 0), 2);

        Near(circle.PointAt(0), new Vec2d(12, 0));
        Near(circle.PointAt(0.25), new Vec2d(10, 2));
        Near(circle.PointAt(0.5), new Vec2d(8, 0));
        Near(circle.PointAt(0.75), new Vec2d(10, -2));
        Near(circle.PointAt(1), circle.PointAt(0), "a circle closes on itself");
    }

    [Fact]
    public void AnArcSweepsAnticlockwiseEvenWhenItsEndAngleIsSmaller()
    {
        // The convention that stops "the same arc" being two entities. An arc from 350 degrees to
        // 10 degrees is a twenty-degree arc across the X axis, not a 340-degree one the other way.
        SketchArc arc = new(Id(1), Vec2d.Zero, 1, Radians(350), Radians(10));

        arc.Sweep.Should().BeApproximately(Radians(20), 1e-9);

        Near(arc.PointAt(0.5), new Vec2d(1, 0), "the middle of that arc is on the axis");
    }

    [Fact]
    public void AQuarterArcEndsWhereTheGeometrySaysItDoes()
    {
        SketchArc arc = new(Id(1), new Vec2d(3, 4), 5, 0, System.Math.PI / 2);

        Near(arc.PointOf(EntityPoint.Start)!.Value, new Vec2d(8, 4));
        Near(arc.PointOf(EntityPoint.End)!.Value, new Vec2d(3, 9));
        Near(arc.PointOf(EntityPoint.Centre)!.Value, new Vec2d(3, 4));

        double diagonal = 5 / System.Math.Sqrt(2);
        Near(arc.PointOf(EntityPoint.Middle)!.Value, new Vec2d(3 + diagonal, 4 + diagonal));
    }

    [Fact]
    public void AnEllipseWithEqualRadiiIsACircle()
    {
        // Asserted as distance from the centre, not by comparing points with a circle at the same
        // parameter, which is what this test did first and which is simply false: a rotation does
        // not change the set of points an equal-radii ellipse covers, but it does change which
        // parameter lands on which of them.
        SketchEllipse round = new(Id(1), new Vec2d(1, 1), 3, 3, Radians(37));

        for (double t = 0; t <= 1; t += 0.125)
        {
            (round.PointAt(t) - new Vec2d(1, 1)).Length
                .Should().BeApproximately(3, 1e-9, "at t = {0}", t);
        }
    }

    [Fact]
    public void AnEllipseSitsOnItsOwnAxes()
    {
        SketchEllipse ellipse = new(Id(1), Vec2d.Zero, 5, 3, Radians(90));

        // Rotated a quarter turn, the major axis points up.
        Near(ellipse.PointAt(0), new Vec2d(0, 5));
        Near(ellipse.PointAt(0.25), new Vec2d(-3, 0));
    }

    [Fact]
    public void AnEllipseKnowsWhereItsFociAre()
    {
        // 3-4-5 again: semi-axes 5 and 4 put the foci 3 from the centre.
        SketchEllipse ellipse = new(Id(1), Vec2d.Zero, 5, 4);

        ellipse.FocalDistance.Should().BeApproximately(3, Precise);
        Near(ellipse.PointOf(EntityPoint.Focus)!.Value, new Vec2d(3, 0));
        Near(ellipse.PointOf(EntityPoint.SecondFocus)!.Value, new Vec2d(-3, 0));
    }

    [Fact]
    public void EveryPointOfAParabolaIsAsFarFromTheFocusAsFromTheDirectrix()
    {
        // The definition of the curve, which is a stronger check than any single coordinate: it
        // fails for any focal length, orientation or parameterisation that is subtly wrong.
        SketchParabola parabola = new(Id(1), new Vec2d(2, 1), new Vec2d(2, 3), -2, 2);

        double focal = parabola.FocalLength;
        focal.Should().BeApproximately(2, Precise);

        for (double t = 0; t <= 1; t += 0.1)
        {
            Vec2d point = parabola.PointAt(t);

            // Axis points up, so the directrix is the horizontal line one focal length below the
            // vertex.
            double toDirectrix = point.Y - (1 - focal);
            double toFocus = (point - new Vec2d(2, 3)).Length;

            toFocus.Should().BeApproximately(toDirectrix, 1e-9, "at t = {0}", t);
        }
    }

    [Fact]
    public void EveryPointOfAHyperbolaSatisfiesItsEquation()
    {
        SketchHyperbola hyperbola = new(Id(1), new Vec2d(1, 2), 3, 4, 0, -1, 1);

        for (double t = 0; t <= 1; t += 0.1)
        {
            Vec2d point = hyperbola.PointAt(t) - new Vec2d(1, 2);

            double left = (point.X * point.X / 9) - (point.Y * point.Y / 16);

            left.Should().BeApproximately(1, 1e-9, "at t = {0}", t);
        }
    }

    [Fact]
    public void AClampedSplinePassesThroughItsFirstAndLastPoles()
    {
        // True of every correct clamped spline and of almost no incorrect one. A knot vector that
        // is not clamped leaves the curve short of both ends, which is the commonest spline bug and
        // is invisible to a test that only checks the middle.
        SketchBSpline spline = SketchBSpline.Through(
            Id(1), 3, [new Vec2d(0, 0), new Vec2d(1, 3), new Vec2d(3, 3), new Vec2d(4, 0)]);

        spline.Degeneracy.Should().BeNull();
        Near(spline.PointAt(0), new Vec2d(0, 0));
        Near(spline.PointAt(1), new Vec2d(4, 0));
    }

    [Fact]
    public void ASplineStaysInsideTheHullOfItsPoles()
    {
        // The convex-hull property, which every B-spline has and which a wrong basis function
        // breaks immediately.
        SketchBSpline spline = SketchBSpline.Through(
            Id(1), 3, [new Vec2d(0, 0), new Vec2d(1, 3), new Vec2d(3, 3), new Vec2d(4, 0)]);

        for (double t = 0; t <= 1; t += 0.05)
        {
            Vec2d point = spline.PointAt(t);

            point.X.Should().BeInRange(-1e-9, 4 + 1e-9);
            point.Y.Should().BeInRange(-1e-9, 3 + 1e-9);
        }
    }

    [Fact]
    public void ARationalQuadraticWithTheRightWeightIsExactlyACircularArc()
    {
        // Why weights are in the model from the start. A quarter circle is a degree-two rational
        // spline over three poles with the middle weight cos(45 degrees); nothing non-rational can
        // represent it, and this is the case that proves the weights reach the recursion rather
        // than being applied as an afterthought.
        double weight = System.Math.Cos(System.Math.PI / 4);

        SketchBSpline quarter = new(
            Id(1),
            2,
            [new Vec2d(1, 0), new Vec2d(1, 1), new Vec2d(0, 1)],
            [1, weight, 1],
            [0, 1],
            [3, 3]);

        quarter.Degeneracy.Should().BeNull();
        quarter.IsRational.Should().BeTrue();

        for (double t = 0; t <= 1; t += 0.05)
        {
            quarter.PointAt(t).Length.Should().BeApproximately(
                1, 1e-9, "every point of a unit arc is one from the centre (t = {0})", t);
        }
    }

    [Theory]
    [InlineData("degree below one")]
    [InlineData("too few poles")]
    [InlineData("a weight per pole missing")]
    [InlineData("a zero weight")]
    [InlineData("knots that repeat instead of carrying a multiplicity")]
    [InlineData("a knot vector of the wrong length")]
    public void ASplineThatCannotBeEvaluatedSaysWhy(string fault)
    {
        SketchBSpline spline = fault switch
        {
            "degree below one" => new SketchBSpline(
                Id(1), 0, [new Vec2d(0, 0)], [1], [0, 1], [1, 1]),

            "too few poles" => new SketchBSpline(
                Id(1), 3, [new Vec2d(0, 0), new Vec2d(1, 1)], [1, 1], [0, 1], [4, 4]),

            "a weight per pole missing" => SketchBSpline.Through(
                Id(1), 1, [new Vec2d(0, 0), new Vec2d(1, 1)]) with
            { Weights = [1] },

            "a zero weight" => SketchBSpline.Through(
                Id(1), 1, [new Vec2d(0, 0), new Vec2d(1, 1)]) with
            { Weights = [1, 0] },

            "knots that repeat instead of carrying a multiplicity" => new SketchBSpline(
                Id(1), 1, [new Vec2d(0, 0), new Vec2d(1, 1)], [1, 1], [0, 0, 1], [1, 1, 2]),

            _ => SketchBSpline.Through(Id(1), 1, [new Vec2d(0, 0), new Vec2d(1, 1)]) with
            { Multiplicities = [1, 1] },
        };

        spline.Degeneracy.Should().NotBeNull();
        spline.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("a line with no length")]
    [InlineData("a circle with no radius")]
    [InlineData("an arc that does not sweep")]
    [InlineData("an ellipse whose axes are swapped")]
    [InlineData("a parabola whose focus is at its vertex")]
    [InlineData("a point at infinity")]
    public void GeometryThatCannotBeSolvedIsCaughtBeforeTheSolverSeesIt(string fault)
    {
        // A degenerate entity does not make the solver fail where the problem is. It makes it fail
        // to converge somewhere else, and the message is then about an iteration count.
        SketchEntity entity = fault switch
        {
            "a line with no length" => new SketchLine(Id(1), new Vec2d(1, 1), new Vec2d(1, 1)),
            "a circle with no radius" => new SketchCircle(Id(1), Vec2d.Zero, 0),
            "an arc that does not sweep" => new SketchArc(Id(1), Vec2d.Zero, 1, 0.5, 0.5),
            "an ellipse whose axes are swapped" => new SketchEllipse(Id(1), Vec2d.Zero, 1, 4),
            "a parabola whose focus is at its vertex" =>
                new SketchParabola(Id(1), Vec2d.Zero, Vec2d.Zero, 0, 1),
            _ => new SketchPoint(Id(1), new Vec2d(double.PositiveInfinity, 0)),
        };

        entity.IsValid.Should().BeFalse();
        entity.Degeneracy.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void SoundGeometryIsNotReportedAsDegenerate()
    {
        // The other half, and the one a sabotage of the checks would otherwise pass: a validator
        // that refused everything would satisfy every test above.
        SketchEntity[] sound =
        [
            new SketchPoint(Id(1), new Vec2d(1, 2)),
            new SketchLine(Id(2), Vec2d.Zero, new Vec2d(1, 0)),
            new SketchCircle(Id(3), Vec2d.Zero, 2),
            new SketchArc(Id(4), Vec2d.Zero, 2, 0, 1),
            new SketchEllipse(Id(5), Vec2d.Zero, 4, 1),
            new SketchEllipticalArc(Id(6), Vec2d.Zero, 4, 1, 0, 0, 1),
            new SketchParabola(Id(7), Vec2d.Zero, new Vec2d(0, 1), -1, 1),
            new SketchHyperbola(Id(8), Vec2d.Zero, 2, 1, 0, -1, 1),
            SketchBSpline.Through(Id(9), 2, [Vec2d.Zero, new Vec2d(1, 1), new Vec2d(2, 0)]),
        ];

        sound.Should().OnlyContain(e => e.IsValid);
    }

    [Fact]
    public void AnEntityOnlyOffersThePointsItActuallyHas()
    {
        // A constraint pointed at a point an entity does not have is a mistake worth catching when
        // the constraint is made, not when the solver reads a coordinate nobody wrote.
        SketchCircle circle = new(Id(1), Vec2d.Zero, 1);

        circle.PointOf(EntityPoint.Centre).Should().NotBeNull();
        circle.PointOf(EntityPoint.Start).Should().BeNull("a full circle has no start");

        SketchLine line = new(Id(2), Vec2d.Zero, new Vec2d(1, 0));

        line.PointOf(EntityPoint.Centre).Should().BeNull("a line has no centre");
    }

    [Fact]
    public void APeriodicSplineHasNoEnds()
    {
        SketchBSpline closed = SketchBSpline.Through(
            Id(1), 2, [Vec2d.Zero, new Vec2d(1, 1), new Vec2d(2, 0)]) with
        { IsPeriodic = true };

        closed.IsClosed.Should().BeTrue();
        closed.Points.Should().NotContain(EntityPoint.Start).And.NotContain(EntityPoint.End);
    }

    [Fact]
    public void TwoSplinesBuiltTheSameWayAreEqual()
    {
        // A record compares an ImmutableArray by reference, and this holds four of them. The trap
        // has caught this project five times now.
        SketchBSpline one = SketchBSpline.Through(
            Id(1), 2, [Vec2d.Zero, new Vec2d(1, 1), new Vec2d(2, 0)]);

        SketchBSpline other = SketchBSpline.Through(
            Id(1), 2, [Vec2d.Zero, new Vec2d(1, 1), new Vec2d(2, 0)]);

        one.Should().Be(other);

        one.Should().NotBe(SketchBSpline.Through(
            Id(1), 2, [Vec2d.Zero, new Vec2d(1, 2), new Vec2d(2, 0)]));
    }

    [Fact]
    public void ConstructionGeometryIsMarkedRatherThanKeptApart()
    {
        // It changes what the geometry is for, never how it is solved: a construction line
        // constrains exactly like any other, which is the point of it.
        SketchLine line = new(Id(1), Vec2d.Zero, new Vec2d(1, 0), IsConstruction: true);

        line.IsConstruction.Should().BeTrue();
        line.PointOf(EntityPoint.End).Should().Be(new Vec2d(1, 0));
        line.ToString().Should().Contain("construction");
    }

    private static void Near(Vec2d actual, Vec2d expected, string because = "")
    {
        actual.X.Should().BeApproximately(expected.X, 1e-9, because);
        actual.Y.Should().BeApproximately(expected.Y, 1e-9, because);
    }

    private static double Radians(double degrees) => degrees * System.Math.PI / 180;

    private static SketchEntityId Id(int n)
        => new(new Guid($"00000000-0000-0000-0000-{n:D12}"));
}
