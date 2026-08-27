using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Expressions;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// The expression language (P3-T15).
/// </summary>
/// <remarks>
/// §5.5 asks for arithmetic, the usual functions, <c>if</c>, parameter references, cross-document
/// references and unit literals — and for dimensional nonsense to be caught before evaluation. The
/// last of those is what most of these tests are about, because it is the part that is easy to get
/// almost right and the part whose failure is silent.
/// </remarks>
public sealed class ExpressionTests
{
    [Theory]
    [InlineData("1", 1)]
    [InlineData("1 + 2", 3)]
    [InlineData("2 * 3 + 1", 7)]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("10 / 4", 2.5)]
    [InlineData("-3 + 1", -2)]
    [InlineData("--3", 3)]
    [InlineData("2 - 3 - 4", -5)]
    [InlineData("100 / 5 / 2", 10)]
    [InlineData("1e3", 1000)]
    [InlineData("1.5e-2", 0.015)]
    public void ArithmeticWorksAndBindsTheUsualWay(string text, double expected)
    {
        // Subtraction and division left-associate: 2 - 3 - 4 is -5, not 3. Getting that backwards
        // is the classic precedence-climbing bug and it is silent on every symmetric example.
        Evaluate(text).Value.Should().BeApproximately(expected, 1e-12);
    }

    [Fact]
    public void AUnitAfterANumberMakesItAMeasurement()
    {
        Evaluate("25.4mm").Should().Be(Unit.Millimetres.Of(25.4));
        Evaluate("1in").Should().Be(Unit.Inches.Of(1));
        Evaluate("45deg").Dimension.Should().Be(Dimension.Angle);

        // And they meet in SI, whichever was typed.
        Unit.Millimetres.From(Evaluate("1in + 1mm")).Should().BeApproximately(26.4, 1e-9);
    }

    [Fact]
    public void FourMillimetresPlusThreeDegreesIsRejectedBeforeAnythingIsComputed()
    {
        // §5.5's opening example, and the reason the checker walks dimensions rather than values.
        ParsedExpression parsed = Parse("4mm + 3deg");

        parsed.IsValid.Should().BeFalse();
        parsed.FirstError!.Message.Should().Contain("length").And.Contain("angle");

        // Reported against the operator, not the whole expression, so an editor can underline it.
        parsed.FirstError.Position.Should().Be("4mm ".Length);
    }

    [Fact]
    public void ABareNumberIsAPlainNumberAndNotALength()
    {
        // A deliberate choice. Adopting the document's units here would make the same formula mean
        // different sizes in different documents -- paste it into a part authored in inches and it
        // silently changes. The refusal says what to write instead.
        ParsedExpression parsed = Parse("Width + 5", Lengths());

        parsed.IsValid.Should().BeFalse();
        parsed.FirstError!.Message.Should().Contain("5mm rather than 5");

        Parse("Width + 5mm", Lengths()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void TheCheckNeedsNoValuesAtAll()
    {
        // What makes "before evaluation" true rather than a manner of speaking: the parameter
        // lookup here answers with a dimension and nothing else, and the expression still checks.
        ParsedExpression parsed = ExpressionParser.Parse(
            "Width * Height", _ => Dimension.Length);

        parsed.IsValid.Should().BeTrue();
        parsed.Dimension.Should().Be(Dimension.Area);
    }

    [Fact]
    public void MultiplyingLengthsGivesAnAreaAndThenAVolume()
    {
        Parse("Width * Height", Lengths()).Dimension.Should().Be(Dimension.Area);
        Parse("Width * Height * Width", Lengths()).Dimension.Should().Be(Dimension.Volume);
        Parse("Width / Height", Lengths()).Dimension.Should().Be(Dimension.Dimensionless);
    }

    [Fact]
    public void AParameterCanBeReferredToByName()
    {
        Quantity value = Evaluate("Width * 2", Lengths());

        Unit.Millimetres.From(value).Should().BeApproximately(200, 1e-9);
    }

    [Fact]
    public void AParameterInAnotherDocumentIsReferredToWithAColon()
    {
        // §5.5's Chassis:Width.
        ParsedExpression parsed = Parse("Chassis:Width * 2", Lengths());

        parsed.IsValid.Should().BeTrue();

        Expression.Reference reference =
            ExpressionParser.ReferencesIn(parsed.Root!).Should().ContainSingle().Subject;

        reference.Document.Should().Be("Chassis");
        reference.Name.Should().Be("Width");
        reference.IsCrossDocument.Should().BeTrue();
        reference.ToString().Should().Be("Chassis:Width");
    }

    [Fact]
    public void EveryReferenceAnExpressionMakesCanBeListed()
    {
        // What P3-T16 needs to fold parameters into the rebuild graph, and what a rename needs in
        // order to know what to rewrite. Duplicates collapse; order is the order they were typed.
        ParsedExpression parsed = Parse("Width + Height * Width + Chassis:Width", Lengths());

        ImmutableArray<Expression.Reference> references =
            ExpressionParser.ReferencesIn(parsed.Root!);

        references.Select(r => r.ToString())
            .Should().Equal(["Width", "Height", "Chassis:Width"]);
    }

    [Fact]
    public void AMissingParameterIsNamedInTheComplaint()
    {
        ParsedExpression parsed = Parse("Widht * 2", Lengths());

        parsed.IsValid.Should().BeFalse();
        parsed.FirstError!.Message.Should().Contain("Widht");
        parsed.FirstError.Position.Should().Be(0);
        parsed.FirstError.Length.Should().Be("Widht".Length);
    }

    [Fact]
    public void AMissingDocumentSaysSoRatherThanBlamingTheParameter()
    {
        ParsedExpression parsed = Parse("Frame:Width", Lengths());

        parsed.IsValid.Should().BeFalse();
        parsed.FirstError!.Message.Should().Contain("Frame").And.Contain("may not be open");
    }

    [Theory]
    [InlineData("abs(-3)", 3)]
    [InlineData("min(2, 5)", 2)]
    [InlineData("max(2, 5)", 5)]
    [InlineData("floor(2.7)", 2)]
    [InlineData("ceil(2.1)", 3)]
    [InlineData("round(2.5)", 3)]
    [InlineData("round(-2.5)", -3)]
    [InlineData("round(3.5)", 4)]
    public void TheUsualFunctionsBehaveAsAPersonExpects(string text, double expected)
    {
        // Round goes away from zero at a half, which is what an engineering drawing assumes.
        // Banker's rounding is the .NET default and would make round(2.5) come out as 2.
        Evaluate(text).Value.Should().BeApproximately(expected, 1e-12);
    }

    [Fact]
    public void TrigonometryTakesAnAngleAndNotAnyOldNumber()
    {
        // sin(0.5) reads as half a turn to a person and as radians to a calculator, and the two
        // disagree. Refusing means the user has to say which they meant.
        Evaluate("sin(90deg)").Value.Should().BeApproximately(1, 1e-12);
        Parse("sin(90deg)").Dimension.Should().Be(Dimension.Dimensionless);

        ParsedExpression bare = Parse("sin(0.5)");

        bare.IsValid.Should().BeFalse();
        bare.FirstError!.Message.Should().Contain("needs an angle").And.Contain("45deg");
    }

    [Fact]
    public void InverseTrigonometryGivesBackAnAngle()
    {
        Parse("atan(1)").Dimension.Should().Be(Dimension.Angle);

        Unit.Degrees.From(Evaluate("atan(1)")).Should().BeApproximately(45, 1e-9);
        Unit.Degrees.From(Evaluate("atan2(1mm, 1mm)")).Should().BeApproximately(45, 1e-9);
    }

    [Fact]
    public void RoundingAMeasurementDirectlyIsRefusedWithTheIdiomToUseInstead()
    {
        // Values are stored in metres, so round(Length) would round a part to the nearest metre --
        // which is not what anybody means and would not look wrong until it was manufactured.
        ParsedExpression parsed = Parse("round(Width)", Lengths());

        parsed.IsValid.Should().BeFalse();
        parsed.FirstError!.Message.Should().Contain("nearest metre").And.Contain("round(x / 1mm) * 1mm");

        // And the idiom itself works.
        Quantity rounded = Evaluate("round(Width / 1mm) * 1mm", Lengths());
        Unit.Millimetres.From(rounded).Should().BeApproximately(100, 1e-9);
    }

    [Fact]
    public void SquareRootOfAnAreaIsALength()
    {
        Parse("sqrt(Width * Height)", Lengths()).Dimension.Should().Be(Dimension.Length);

        Parse("sqrt(Width)", Lengths()).IsValid.Should().BeFalse(
            "the root of a length is not a dimension this system has");
    }

    [Fact]
    public void ConditionalsChooseBetweenTwoOutcomesOfTheSameKind()
    {
        Unit.Millimetres.From(Evaluate("if(Width > 50mm, 10mm, 20mm)", Lengths()))
            .Should().BeApproximately(10, 1e-9);

        Unit.Millimetres.From(Evaluate("if(Width < 50mm, 10mm, 20mm)", Lengths()))
            .Should().BeApproximately(20, 1e-9);
    }

    [Fact]
    public void TheTwoOutcomesOfAConditionalHaveToAgree()
    {
        // Otherwise the expression's own dimension would depend on a value, and could not be
        // known before evaluating -- which is the property the whole check rests on.
        ParsedExpression parsed = Parse("if(Width > 50mm, 10mm, 20deg)", Lengths());

        parsed.IsValid.Should().BeFalse();
        parsed.FirstError!.Message.Should().Contain("same kind of thing");
    }

    [Fact]
    public void TheTestInAConditionalHasToBeAComparison()
    {
        ParsedExpression parsed = Parse("if(Width, 10mm, 20mm)", Lengths());

        parsed.IsValid.Should().BeFalse();
        parsed.FirstError!.Message.Should().Contain("has to be a comparison");
    }

    [Fact]
    public void TheBranchNotTakenIsNeverEvaluated()
    {
        // What makes if(x != 0, y / x, 0) a sensible thing to write.
        //
        // Asserting on the *result* would not test this: dividing by zero yields an infinity
        // rather than throwing, so a version that evaluated both branches and then discarded one
        // would produce exactly the same answer. What has to be observed is that the branch was
        // never entered, so the parameter lookup records what it is asked for.
        //
        // Checking and evaluating are done separately here, and that is not incidental. The type
        // check has to walk both branches -- proving they measure the same kind of thing is its
        // job -- so counting across a combined parse-and-evaluate would see every name whatever
        // the evaluator did. Laziness is a property of evaluation alone.
        ParsedExpression parsed = Parse("if(Zero != 0, Width / Zero, Height)", Lengths());

        parsed.IsValid.Should().BeTrue();

        List<string> read = [];

        Quantity value = ExpressionEvaluator.Evaluate(
            parsed.Root!,
            reference =>
            {
                read.Add(reference.Name);
                return Lookup(Lengths(), reference);
            });

        Unit.Millimetres.From(value).Should().BeApproximately(60, 1e-9);

        read.Should().NotContain("Width", "the branch that was not taken must not be entered");
        read.Should().Contain("Height").And.Contain("Zero");
    }

    [Fact]
    public void ComparingDifferentKindsOfThingIsRefused()
    {
        Parse("Width > 45deg", Lengths()).IsValid.Should().BeFalse();
        Parse("Width > 45mm", Lengths()).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("1 +", "stops before it is finished")]
    [InlineData("(1 + 2", "never closed")]
    [InlineData("1 2", "left over")]
    [InlineData("1 @ 2", "does not belong")]
    [InlineData("max(1, 2", "never closed")]
    [InlineData("Chassis:", "no parameter name")]
    [InlineData("5furlong", "not a unit")]
    public void ABadlyTypedExpressionSaysWhatIsWrongWithIt(string text, string expected)
    {
        // An expression box is somewhere people make typing mistakes constantly, so the quality of
        // the complaint is the feature. None of these mention tokens, productions or the parser.
        ParsedExpression parsed = Parse(text);

        parsed.IsValid.Should().BeFalse();
        parsed.FirstError!.Message.Should().Contain(expected);
    }

    [Fact]
    public void AnUnknownFunctionListsTheOnesThatExist()
    {
        ParsedExpression parsed = Parse("sine(45deg)");

        parsed.IsValid.Should().BeFalse();
        parsed.FirstError!.Message.Should().Contain("sine").And.Contain("sin").And.Contain("sqrt");
    }

    [Fact]
    public void AFunctionGivenTheWrongNumberOfArgumentsSaysHowMany()
    {
        ParsedExpression parsed = Parse("min(1)");

        parsed.IsValid.Should().BeFalse();
        parsed.FirstError!.Message.Should().Contain("takes 2 arguments").And.Contain("given 1");
    }

    [Fact]
    public void OneMistakeProducesOneComplaint()
    {
        // A sub-expression that failed is not reported again by every operator above it. Otherwise
        // a single typo buries the user in consequences of itself.
        ParsedExpression parsed = Parse("Missing * 2 + Missing * 3", Lengths());

        parsed.Errors.Should().HaveCount(2, "there are two bad references and nothing else");
        parsed.Errors.Should().OnlyContain(e => e.Message.Contains("Missing"));
    }

    [Fact]
    public void CaseDoesNotMatterForFunctionsOrUnits()
    {
        Evaluate("ABS(-2)").Value.Should().Be(2);
        Evaluate("25.4MM").Should().Be(Unit.Millimetres.Of(25.4));
    }

    [Fact]
    public void WhitespaceIsIgnoredWhereverItFalls()
    {
        Evaluate("  1   +\t2 * ( 3 - 1 )  ").Value.Should().Be(5);
    }

    [Fact]
    public void DividingByZeroGivesAnInfinityRatherThanStopping()
    {
        // The caller decides what to do about it. A parameter that has gone infinite is a problem
        // for whoever is about to build geometry from it, and they can say so in their own terms.
        Quantity result = Evaluate("1 / 0");

        result.IsFinite.Should().BeFalse();
        double.IsPositiveInfinity(result.Value).Should().BeTrue();
    }

    private static ParsedExpression Parse(
        string text, ImmutableDictionary<string, Quantity>? parameters = null)
        => ExpressionParser.Parse(text, r => Lookup(parameters, r)?.Dimension);

    private static Quantity Evaluate(
        string text, ImmutableDictionary<string, Quantity>? parameters = null)
    {
        (Quantity? value, ImmutableArray<ExpressionError> errors) =
            ExpressionEvaluator.Evaluate(text, r => Lookup(parameters, r));

        errors.Should().BeEmpty($"'{text}' should be a valid expression");

        return value!.Value;
    }

    private static Quantity? Lookup(
        ImmutableDictionary<string, Quantity>? parameters, Expression.Reference reference)
    {
        // Only one other document is known, so a reference to any other is a reference to
        // something that is not open -- which is a different complaint from a bad parameter name.
        if (reference.Document is not null and not "Chassis")
        {
            return null;
        }

        return parameters is not null && parameters.TryGetValue(reference.Name, out Quantity found)
            ? found
            : null;
    }

    private static ImmutableDictionary<string, Quantity> Lengths()
        => ImmutableDictionary.CreateRange(Parameter.NameComparer,
        [
            KeyValuePair.Create("Width", Unit.Millimetres.Of(100)),
            KeyValuePair.Create("Height", Unit.Millimetres.Of(60)),
            KeyValuePair.Create("Zero", Quantity.Number(0)),
        ]);
}
