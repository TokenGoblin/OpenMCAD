using FluentAssertions;

using OpenMCAD.Core.Documents;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// Unit-typed quantities and dimension algebra (P3-T14).
/// </summary>
/// <remarks>
/// §5.5 opens with the case that matters: <c>4 mm + 3 deg</c> is a type error and must be caught
/// before evaluation. Most of what follows is about refusals, because a system that adds a length
/// to an angle does not produce a wrong number — it produces a number where there should have been
/// a question.
/// </remarks>
public sealed class QuantityTests
{
    [Fact]
    public void FourMillimetresPlusThreeDegreesIsRefused()
    {
        // The example §5.5 leads with.
        Quantity length = Unit.Millimetres.Of(4);
        Quantity angle = Unit.Degrees.Of(3);

        Action sum = () => _ = length + angle;

        sum.Should().Throw<DimensionException>().WithMessage("*length*angle*");
    }

    [Fact]
    public void TheRefusalNeedsNoValuesAtAll()
    {
        // The property that makes P3-T15 able to reject an expression while parsing it, rather
        // than at rebuild time an hour later: the check runs on dimensions alone.
        Dimensions.Add(Dimension.Length, Dimension.Angle).Should().BeNull();
        Dimensions.Add(Dimension.Length, Dimension.Length).Should().Be(Dimension.Length);
    }

    [Theory]
    [InlineData(Dimension.Length, Dimension.Length, Dimension.Area)]
    [InlineData(Dimension.Length, Dimension.Area, Dimension.Volume)]
    [InlineData(Dimension.Area, Dimension.Length, Dimension.Volume)]
    [InlineData(Dimension.Density, Dimension.Volume, Dimension.Mass)]
    [InlineData(Dimension.Dimensionless, Dimension.Length, Dimension.Length)]
    [InlineData(Dimension.Length, Dimension.Dimensionless, Dimension.Length)]
    public void MultiplyingCombinesDimensions(Dimension left, Dimension right, Dimension expected)
        => Dimensions.Multiply(left, right).Should().Be(expected);

    [Theory]
    [InlineData(Dimension.Area, Dimension.Length, Dimension.Length)]
    [InlineData(Dimension.Volume, Dimension.Area, Dimension.Length)]
    [InlineData(Dimension.Volume, Dimension.Length, Dimension.Area)]
    [InlineData(Dimension.Mass, Dimension.Volume, Dimension.Density)]
    [InlineData(Dimension.Length, Dimension.Length, Dimension.Dimensionless)]
    [InlineData(Dimension.Angle, Dimension.Angle, Dimension.Dimensionless)]
    public void DividingCombinesDimensions(Dimension left, Dimension right, Dimension expected)
        => Dimensions.Divide(left, right).Should().Be(expected);

    [Fact]
    public void ACombinationOutsideTheListIsRefusedRatherThanInvented()
    {
        // A length times a mass is a real physical quantity and not one a part document has any
        // use for. The closed list is the point -- it is what lets a dimension be named in a
        // message and switched on -- and the price is that its edges are refusals.
        Dimensions.Multiply(Dimension.Length, Dimension.Mass).Should().BeNull();
        Dimensions.Divide(Dimension.Length, Dimension.Time).Should().BeNull();

        Action nonsense = () => _ = Unit.Millimetres.Of(4) * Unit.Kilograms.Of(2);

        nonsense.Should().Throw<DimensionException>();
    }

    [Fact]
    public void AnAngleIsNotAPlainNumberEvenThoughPhysicsSaysItIs()
    {
        // A radian is dimensionless in SI, so a strict treatment would let 4 mm + 3 deg through as
        // a number plus a number -- which is exactly the error being guarded against. Angle is
        // kept separate and the physics is what gives way.
        Dimensions.Add(Dimension.Angle, Dimension.Dimensionless).Should().BeNull();
        Dimensions.Multiply(Dimension.Angle, Dimension.Length).Should().BeNull();

        // The way out is a ratio, which is explicit about what it is doing.
        Dimensions.Divide(Dimension.Angle, Dimension.Angle).Should().Be(Dimension.Dimensionless);
    }

    [Fact]
    public void ScalingByAPlainNumberKeepsWhatTheThingMeasures()
    {
        // By far the commonest case in a real document: Thickness * 2, Depth * Count.
        Quantity doubled = Unit.Millimetres.Of(4) * Quantity.Number(2);

        doubled.Dimension.Should().Be(Dimension.Length);
        Unit.Millimetres.From(doubled).Should().BeApproximately(8, 1e-12);
    }

    [Fact]
    public void ArithmeticHappensInTheBaseUnitWhateverWasTyped()
    {
        // §5.5: storage is always SI, and conversion happens only at the boundary. An inch plus a
        // millimetre is a length, and nothing in the middle has to know which was which.
        Quantity total = Unit.Inches.Of(1) + Unit.Millimetres.Of(1);

        Unit.Millimetres.From(total).Should().BeApproximately(26.4, 1e-9);
        total.Dimension.Should().Be(Dimension.Length);
    }

    [Fact]
    public void ComparingAcrossDimensionsIsRefusedRatherThanAnsweredFalse()
    {
        // False is an answer, and there is no answer to whether a length exceeds an angle.
        Action compare = () => Unit.Millimetres.Of(4).CompareTo(Unit.Degrees.Of(3));

        compare.Should().Throw<DimensionException>();

        Unit.Millimetres.Of(5).CompareTo(Unit.Millimetres.Of(4)).Should().BePositive();
    }

    [Fact]
    public void TheSquareRootOfAnAreaIsALength()
    {
        Quantity area = Unit.SquareMillimetres.Of(100);

        Quantity side = area.SquareRoot();

        side.Dimension.Should().Be(Dimension.Length);
        Unit.Millimetres.From(side).Should().BeApproximately(10, 1e-9);
    }

    [Fact]
    public void TheSquareRootOfAVolumeIsRefused()
    {
        // It would be a length to the power of three halves, which is not in the list. The honest
        // place to meet a limit of the closed table is here, with a message.
        Action root = () => Unit.CubicMillimetres.Of(8).SquareRoot();

        root.Should().Throw<DimensionException>().WithMessage("*volume*");
    }

    [Theory]
    [InlineData(25.4)]
    [InlineData(0.001)]
    [InlineData(1234.5678)]
    [InlineData(1e-9)]
    [InlineData(1e9)]
    [InlineData(0)]
    [InlineData(-17.25)]
    public void EveryUnitSettlesRatherThanDriftingFurtherEachTime(double typed)
    {
        // The universal property, and the one §5.5's requirement is really protecting. A user who
        // opens a dialog showing a value and closes it without typing must not have edited their
        // model -- and more importantly, a value that moved a little on every such open would
        // eventually be wrong by an amount someone could measure.
        //
        // Exactness is not achievable for every unit and no implementation could make it so: a
        // degree is a factor of pi and a pound is 0.45359237 kg, neither of which has an exact
        // binary representation, so something has to be lost in the last place. Settling is what
        // can be promised: one round trip may move the value by an ulp, and every one after it
        // must land exactly where the first did.
        foreach (Unit unit in Unit.All)
        {
            double once = unit.From(unit.Of(typed));
            double twice = unit.From(unit.Of(once));

            once.Should().BeApproximately(
                typed,
                (System.Math.Abs(typed) * 1e-15) + 1e-15,
                $"{typed} {unit.Symbol} should survive being shown and read back");

            twice.Should().Be(once, $"{unit.Symbol} must not drift further on a second round trip");
        }
    }

    [Theory]
    [InlineData(25.4)]
    [InlineData(0.001)]
    [InlineData(1234.5678)]
    [InlineData(1e-9)]
    [InlineData(1e9)]
    [InlineData(-17.25)]
    public void AUnitScaledByAPowerOfTenRoundTripsExactly(double typed)
    {
        // Where exactness *is* achievable it is worth pinning, because it is easy to lose: using a
        // precomputed reciprocal would break every one of these. x * (1/1000) and x / 1000 do not
        // agree in floating point, and only the second comes back to where it started.
        foreach (Unit unit in new[]
        {
            Unit.Metres, Unit.Millimetres, Unit.Centimetres, Unit.Micrometres,
            Unit.SquareMillimetres, Unit.CubicMillimetres, Unit.Grams,
            Unit.GramsPerCubicCentimetre, Unit.None,
        })
        {
            unit.From(unit.Of(typed)).Should().Be(
                typed, $"{unit.Symbol} scales by a power of ten and has no excuse");
        }
    }

    [Fact]
    public void AnInchIsExactlyTwentyFivePointFourMillimetres()
    {
        // Defined exactly since 1959, and worth pinning: a unit table nobody proofreads is a unit
        // table with a wrong entry in it.
        Unit.Millimetres.From(Unit.Inches.Of(1)).Should().BeApproximately(25.4, 1e-12);
        Unit.Thou.From(Unit.Inches.Of(1)).Should().BeApproximately(1000, 1e-9);
        Unit.Millimetres.From(Unit.Feet.Of(1)).Should().BeApproximately(304.8, 1e-9);
    }

    [Fact]
    public void ARightAngleIsAQuarterTurn()
    {
        Unit.Radians.From(Unit.Degrees.Of(90))
            .Should().BeApproximately(System.Math.PI / 2, 1e-12);

        Unit.Degrees.From(Unit.Radians.Of(System.Math.PI)).Should().BeApproximately(180, 1e-12);
    }

    [Fact]
    public void ShowingAQuantityInTheWrongUnitIsRefused()
    {
        Action wrong = () => Unit.Millimetres.From(Unit.Degrees.Of(90));

        wrong.Should().Throw<DimensionException>();
    }

    [Fact]
    public void AUnitIsFoundHoweverItIsCapitalised()
    {
        Unit.Find("mm").Should().Be(Unit.Millimetres);
        Unit.Find("MM").Should().Be(Unit.Millimetres);
        Unit.Find("Deg").Should().Be(Unit.Degrees);
        Unit.Find("furlong").Should().BeNull();
        Unit.Find(null).Should().BeNull();
    }

    [Fact]
    public void FormattingIsExactUnlessAskedToRound()
    {
        // A default of two decimal places would silently turn a 0.001 mm tolerance into nothing.
        Quantity tolerance = Unit.Millimetres.Of(0.001);

        Unit.Millimetres.Format(tolerance).Should().Be("0.001 mm");
        Unit.Millimetres.Format(tolerance, decimals: 2).Should().Be("0.00 mm");
        Unit.None.Format(Quantity.Number(3.5)).Should().Be("3.5");
    }

    [Fact]
    public void EveryKnownUnitAgreesWithItsOwnDimension()
    {
        foreach (Unit unit in Unit.All)
        {
            unit.Of(1).Dimension.Should().Be(
                unit.Dimension, $"{unit.Symbol} should produce what it claims to measure");

            unit.PerBase.Should().BePositive($"{unit.Symbol} needs a positive conversion");
        }
    }

    [Fact]
    public void ADefaultQuantityIsTheDimensionlessZero()
    {
        // The only default that is not a lie. A default of "length" would claim that an unset
        // value is a distance of zero metres.
        Quantity.Zero.Dimension.Should().Be(Dimension.Dimensionless);
        Quantity.Zero.Value.Should().Be(0);
        default(Quantity).Should().Be(Quantity.Zero);
    }
}
