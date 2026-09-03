using FluentAssertions;

using OpenMCAD.Math;
using OpenMCAD.Solver.Sketching;

using Xunit;

namespace OpenMCAD.Solver.Tests;

/// <summary>Mapping one piece of sketch geometry through a transform (P4-T13).</summary>
public sealed class SketchGeometryTransformTests
{
    private static readonly SketchEntityId Id = SketchEntityId.New();

    [Fact]
    public void Point_MovesByTheTransform()
    {
        SketchPoint point = new(Id, new Vec2d(1, 1));
        SketchTransform transform = SketchTransform.Translate(new Vec2d(2, 3));

        SketchPoint result = (SketchPoint)SketchGeometryTransform.Apply(transform, point)!;

        result.Position.Should().Be(new Vec2d(3, 4));
        result.Id.Should().Be(Id, "this maps geometry, not identity -- Duplicate reassigns Id separately");
    }

    [Fact]
    public void Line_MovesBothEnds()
    {
        SketchLine line = new(Id, Vec2d.Zero, new Vec2d(4, 0));
        SketchTransform transform = SketchTransform.RotateAbout(Vec2d.Zero, System.Math.PI / 2);

        SketchLine result = (SketchLine)SketchGeometryTransform.Apply(transform, line)!;

        result.Start.X.Should().BeApproximately(0, 1e-12);
        result.Start.Y.Should().BeApproximately(0, 1e-12);
        result.End.X.Should().BeApproximately(0, 1e-12);
        result.End.Y.Should().BeApproximately(4, 1e-12);
    }

    [Fact]
    public void Circle_ScalesTheRadius()
    {
        SketchCircle circle = new(Id, new Vec2d(1, 0), 2);
        SketchTransform transform = SketchTransform.ScaleAbout(Vec2d.Zero, 3);

        SketchCircle result = (SketchCircle)SketchGeometryTransform.Apply(transform, circle)!;

        result.Centre.Should().Be(new Vec2d(3, 0));
        result.Radius.Should().Be(6);
    }

    [Fact]
    public void Arc_RotatesBothEndsTogetherWhenNotReflected()
    {
        SketchArc arc = new(Id, Vec2d.Zero, 5, 0, System.Math.PI / 2);
        SketchTransform transform = SketchTransform.RotateAbout(Vec2d.Zero, System.Math.PI / 2);

        SketchArc result = (SketchArc)SketchGeometryTransform.Apply(transform, arc)!;

        result.Sweep.Should().BeApproximately(System.Math.PI / 2, 1e-9, "a rotation does not change how far the arc sweeps");
        result.PointAt(0).X.Should().BeApproximately(0, 1e-9);
        result.PointAt(0).Y.Should().BeApproximately(5, 1e-9);
    }

    [Fact]
    public void Arc_SwapsStartAndEndUnderAMirrorButKeepsTheSameSweep()
    {
        // The same conclusion P4-T11 reaches for a circular edge viewed from the reflected side:
        // a mirror reverses which way increasing angle turns, so the only way to keep describing
        // the same physical 90-degree arc anticlockwise is to swap which point is Start and which
        // is End.
        SketchArc arc = new(Id, Vec2d.Zero, 5, 0, System.Math.PI / 2);
        SketchTransform transform = SketchTransform.MirrorAbout(Vec2d.Zero, Vec2d.UnitX);

        SketchArc result = (SketchArc)SketchGeometryTransform.Apply(transform, arc)!;

        result.Sweep.Should().BeApproximately(System.Math.PI / 2, 1e-9);

        // Original Start (angle 0) is (5, 0); original End (angle pi/2) is (0, 5). Mirrored about
        // the X axis those become (5, 0) and (0, -5) respectively -- and since the sweep sense
        // flipped, the mirrored arc's own Start is the image of the original End.
        result.PointAt(0).X.Should().BeApproximately(0, 1e-9);
        result.PointAt(0).Y.Should().BeApproximately(-5, 1e-9);
        result.PointAt(1).X.Should().BeApproximately(5, 1e-9);
        result.PointAt(1).Y.Should().BeApproximately(0, 1e-9);
    }

    [Fact]
    public void UnsupportedEntityKinds_ReturnNullRatherThanWrongGeometry()
    {
        SketchEllipse ellipse = new(Id, Vec2d.Zero, 5, 3);

        SketchGeometryTransform.Apply(SketchTransform.Identity, ellipse).Should().BeNull();
    }
}
