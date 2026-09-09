using FluentAssertions;

using OpenMCAD.Core.Assemblies;
using OpenMCAD.Math;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// Component definitions against occurrences, and where an instance actually sits (P5-T04, §5.9).
/// </summary>
/// <remarks>
/// §5.9 says of this one distinction that getting it wrong makes large assemblies unusable, so what
/// is asserted below is mostly that the structure keeps its promise under the case that breaks it:
/// the same component placed many times, and the same sub-assembly reached by two routes.
/// </remarks>
public sealed class AssemblyTests
{
    [Fact]
    public void OneComponentPlacedTenThousandTimesIsStillOneComponent()
    {
        ComponentDefinition bolt = Part("Bolt", "bolt.omcad");
        Assembly assembly = Assembly.Empty.WithDefinition(bolt);

        for (int i = 0; i < 10_000; ++i)
        {
            assembly = assembly.WithOccurrence(
                new ComponentOccurrence(
                    OccurrenceId.New(), bolt.Id, Transform.FromTranslation(new Vec3d(i, 0, 0))));
        }

        // The sentence §5.9 is emphatic about, made checkable.
        assembly.Definitions.Should().ContainSingle();
        assembly.Occurrences.Should().HaveCount(10_000);
        assembly.PlacementsOf(bolt.Id).Should().Be(10_000);
    }

    [Fact]
    public void RenamingAComponentRenamesEveryPlacementOfIt()
    {
        // The consequence that makes the split worth having, rather than a tidiness argument: the
        // name lives on the definition, so this is one edit and not ten thousand.
        ComponentDefinition bolt = Part("Bolt", "bolt.omcad");
        ComponentOccurrence first = Placed(bolt, Vec3d.Zero);
        ComponentOccurrence second = Placed(bolt, new Vec3d(10, 0, 0));

        ComponentDefinition renamed = bolt with { Name = "M6 Bolt" };

        first.NameIn(renamed).Should().Be("M6 Bolt");
        second.NameIn(renamed).Should().Be("M6 Bolt");
    }

    [Fact]
    public void APlacementCanBeCalledSomethingOfItsOwn()
    {
        ComponentDefinition bracket = Part("Bracket", "bracket.omcad");
        ComponentOccurrence left = Placed(bracket, Vec3d.Zero) with { Label = "Left bracket" };
        ComponentOccurrence right = Placed(bracket, new Vec3d(50, 0, 0));

        left.NameIn(bracket).Should().Be("Left bracket");
        right.NameIn(bracket).Should().Be("Bracket", "a placement with no label uses the component's");
    }

    [Fact]
    public void AskingAPlacementForItsNameUnderTheWrongComponentIsRefused()
    {
        ComponentDefinition bracket = Part("Bracket", "bracket.omcad");
        ComponentDefinition bolt = Part("Bolt", "bolt.omcad");

        // Silent, otherwise: a tree that labelled every instance after the first with its
        // neighbour's name would look plausible and be wrong.
        FluentActions.Invoking(() => Placed(bracket, Vec3d.Zero).NameIn(bolt))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void APlacementOfSomethingTheAssemblyDoesNotHaveIsRefused()
    {
        ComponentDefinition absent = Part("Ghost", "ghost.omcad");

        // The invariant everything else is written against. A structure that can hold a dangling
        // placement will be found holding one, and then a tree walk has to invent an answer.
        FluentActions.Invoking(() => Assembly.Empty.WithOccurrence(Placed(absent, Vec3d.Zero)))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TheSameComponentCannotBeAddedTwice()
    {
        ComponentDefinition bolt = Part("Bolt", "bolt.omcad");

        FluentActions.Invoking(() => Assembly.Empty.WithDefinition(bolt).WithDefinition(bolt))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RemovingThePlacementKeepsTheComponent()
    {
        ComponentDefinition bolt = Part("Bolt", "bolt.omcad");
        ComponentOccurrence only = Placed(bolt, Vec3d.Zero);

        Assembly assembly = Assembly.Empty.WithDefinition(bolt).WithOccurrence(only);
        Assembly without = assembly.WithoutOccurrence(only.Id);

        // Deleting the last bolt is not a statement that this assembly is no longer built from
        // bolts, and re-inserting one should not have to find the document again.
        without.Occurrences.Should().BeEmpty();
        without.Definitions.Should().ContainSingle();

        without.WithoutUnusedDefinitions().Definitions.Should().BeEmpty();
    }

    [Fact]
    public void ClearingUnusedComponentsKeepsTheOnesStillPlaced()
    {
        ComponentDefinition kept = Part("Bracket", "bracket.omcad");
        ComponentDefinition dropped = Part("Bolt", "bolt.omcad");

        Assembly assembly = Assembly.Empty
            .WithDefinition(kept)
            .WithDefinition(dropped)
            .WithOccurrence(Placed(kept, Vec3d.Zero))
            .WithoutUnusedDefinitions();

        assembly.Definitions.Should().ContainSingle().Which.Should().Be(kept);
    }

    [Fact]
    public void EditingAPlacementLeavesItWhereItWasInTheTree()
    {
        ComponentDefinition bolt = Part("Bolt", "bolt.omcad");
        ComponentOccurrence first = Placed(bolt, Vec3d.Zero);
        ComponentOccurrence second = Placed(bolt, new Vec3d(10, 0, 0));
        ComponentOccurrence third = Placed(bolt, new Vec3d(20, 0, 0));

        Assembly assembly = Assembly.Empty
            .WithDefinition(bolt)
            .WithOccurrence(first)
            .WithOccurrence(second)
            .WithOccurrence(third)
            .ReplaceOccurrence(second with { IsHidden = true });

        // The tree is an order the user arranged. Hiding the middle bolt must not move it to the
        // end, which is what removing and re-adding would do.
        assembly.Occurrences.Select(o => o.Id).Should().Equal(first.Id, second.Id, third.Id);
        assembly.FindOccurrence(second.Id)!.IsHidden.Should().BeTrue();
    }

    [Fact]
    public void HidingAPlacementDoesNotTakeItOutOfTheProduct()
    {
        ComponentOccurrence hidden = Placed(Part("Cover", "cover.omcad"), Vec3d.Zero)
            with { IsHidden = true };

        ComponentOccurrence suppressed = Placed(Part("Cover", "cover.omcad"), Vec3d.Zero)
            with { IsSuppressed = true };

        // Folding the two into one flag would make "let me see past this cover" silently change
        // the assembly's mass.
        hidden.IsActive.Should().BeTrue();
        suppressed.IsActive.Should().BeFalse();
    }

    // --- Where an instance sits ------------------------------------------------------------------

    [Fact]
    public void ThePlacementOfATopLevelComponentIsWhereItSits()
    {
        ComponentDefinition bolt = Part("Bolt", "bolt.omcad");
        ComponentOccurrence only = Placed(bolt, new Vec3d(1, 2, 3));

        Assembly assembly = Assembly.Empty.WithDefinition(bolt).WithOccurrence(only);

        OccurrenceResolution result = assembly.WorldTransformOf(OccurrencePath.Of(only.Id));

        result.IsResolved.Should().BeTrue();
        result.World.TransformPoint(Vec3d.Zero).Should().Be(new Vec3d(1, 2, 3));
        result.Occurrence.Should().Be(only);
    }

    [Fact]
    public void ANestedInstanceSitsWhereBothPlacementsPutIt()
    {
        Product product = new();

        OccurrenceResolution result = product.Assembly.WorldTransformOf(
            OccurrencePath.Of(product.LeftBracket, product.BoltInBracket), product.Load);

        // The bracket is at x = 100 and the bolt is 5 along the bracket's own x, so the bolt is at
        // 105 -- the composition, not either placement on its own.
        result.IsResolved.Should().BeTrue();
        result.World.TransformPoint(Vec3d.Zero).Should().Be(new Vec3d(105, 0, 0));
    }

    [Fact]
    public void TheSameSubAssemblyPlacedTwiceGivesTwoInstancesFromOneStructure()
    {
        Product product = new();

        Vec3d left = product.Assembly.WorldTransformOf(
            OccurrencePath.Of(product.LeftBracket, product.BoltInBracket), product.Load)
            .World.TransformPoint(Vec3d.Zero);

        Vec3d right = product.Assembly.WorldTransformOf(
            OccurrencePath.Of(product.RightBracket, product.BoltInBracket), product.Load)
            .World.TransformPoint(Vec3d.Zero);

        // One bracket document, one bolt inside it, two places in the world. The two paths differ
        // only in their first step, and the bolt's own occurrence id is the same in both -- which
        // is exactly why an instance is named by a path and not by an id.
        left.Should().Be(new Vec3d(105, 0, 0));
        right.Should().Be(new Vec3d(-95, 0, 0));

        product.Loads.Should().Be(2, "the sub-assembly's structure is read, not copied");
    }

    [Fact]
    public void RotationCompoundsThroughTheTree()
    {
        // A translation-only test passes whichever order the transforms are multiplied in. This
        // one does not: the bolt is offset along the bracket's x, and the bracket is turned a
        // quarter turn about z, so the bolt ends up along the world's y.
        ComponentDefinition bracket = SubAssembly("Bracket", "bracket.omcad");
        ComponentDefinition bolt = Part("Bolt", "bolt.omcad");

        OccurrenceId placedBracket = OccurrenceId.New();
        OccurrenceId placedBolt = OccurrenceId.New();

        Assembly inner = Assembly.Empty
            .WithDefinition(bolt)
            .WithOccurrence(new ComponentOccurrence(
                placedBolt, bolt.Id, Transform.FromTranslation(new Vec3d(10, 0, 0))));

        Assembly outer = Assembly.Empty
            .WithDefinition(bracket)
            .WithOccurrence(new ComponentOccurrence(
                placedBracket,
                bracket.Id,
                Transform.FromRotation(Quatd.FromAxisAngle(Vec3d.UnitZ, System.Math.PI / 2))));

        Vec3d where = outer.WorldTransformOf(
            OccurrencePath.Of(placedBracket, placedBolt), _ => inner)
            .World.TransformPoint(Vec3d.Zero);

        where.IsNear(new Vec3d(0, 10, 0)).Should().BeTrue();
    }

    [Fact]
    public void TheRootPathIsTheAssemblyItself()
    {
        Product product = new();

        OccurrenceResolution result = product.Assembly.WorldTransformOf(OccurrencePath.Root);

        result.IsResolved.Should().BeTrue();
        result.World.Should().Be(Transform.Identity);
        result.Occurrence.Should().BeNull("the root names the assembly, not a placement in it");
    }

    [Fact]
    public void APathNamingSomethingThatIsNotThereIsNotFound()
    {
        Product product = new();

        OccurrenceResolution result =
            product.Assembly.WorldTransformOf(OccurrencePath.Of(OccurrenceId.New()), product.Load);

        result.Outcome.Should().Be(OccurrenceOutcome.NotFound);
    }

    [Fact]
    public void APathThatDescendsIntoAPartHasNowhereToGo()
    {
        ComponentDefinition bolt = Part("Bolt", "bolt.omcad");
        ComponentOccurrence only = Placed(bolt, Vec3d.Zero);

        Assembly assembly = Assembly.Empty.WithDefinition(bolt).WithOccurrence(only);

        OccurrenceResolution result = assembly.WorldTransformOf(
            OccurrencePath.Of(only.Id, OccurrenceId.New()), _ => Assembly.Empty);

        // Told apart from NotFound because the repair differs: the path is not wrong about a
        // missing placement, it is wrong about what kind of thing it descended into.
        result.Outcome.Should().Be(OccurrenceOutcome.NotAnAssembly);
    }

    [Fact]
    public void ASubAssemblyThatIsNotLoadedIsSaidToBeUnloaded()
    {
        Product product = new();

        OccurrenceResolution result = product.Assembly.WorldTransformOf(
            OccurrencePath.Of(product.LeftBracket, product.BoltInBracket));

        // Not a failure of the model. §5.9's lightweight modes exist so a large assembly need not
        // load every document it names, so this is an ordinary answer.
        result.Outcome.Should().Be(OccurrenceOutcome.Unloaded);
    }

    // --- Paths -----------------------------------------------------------------------------------

    [Fact]
    public void TwoPathsBuiltSeparatelyFromTheSameStepsAreTheSamePath()
    {
        OccurrenceId first = OccurrenceId.New();
        OccurrenceId second = OccurrenceId.New();

        // The equality trap this codebase has met three times: an ImmutableArray compares by
        // reference, so a path used as a dictionary key would never find itself.
        OccurrencePath.Of(first, second).Should().Be(OccurrencePath.Of(first, second));

        OccurrencePath.Of(first, second).GetHashCode()
            .Should().Be(OccurrencePath.Of(first, second).GetHashCode());

        new HashSet<OccurrencePath> { OccurrencePath.Of(first, second) }
            .Should().Contain(OccurrencePath.Of(first, second));
    }

    [Fact]
    public void PathsDifferingOnlyInOrderAreDifferentPaths()
    {
        OccurrenceId first = OccurrenceId.New();
        OccurrenceId second = OccurrenceId.New();

        OccurrencePath.Of(first, second).Should().NotBe(OccurrencePath.Of(second, first));
    }

    [Fact]
    public void APathKnowsWhatItIsUnder()
    {
        OccurrenceId chassis = OccurrenceId.New();
        OccurrenceId bracket = OccurrenceId.New();
        OccurrenceId bolt = OccurrenceId.New();

        OccurrencePath deep = OccurrencePath.Of(chassis, bracket, bolt);

        deep.IsUnder(OccurrencePath.Of(chassis, bracket)).Should().BeTrue();
        deep.IsUnder(OccurrencePath.Root).Should().BeTrue();
        deep.IsUnder(deep).Should().BeTrue("a path is under itself");
        deep.IsUnder(OccurrencePath.Of(bracket, bolt)).Should().BeFalse();

        // A prefix test rather than a "does it contain this id" test, and that is the point: the
        // same sub-assembly placed elsewhere shares its children's ids, and hiding one must not
        // hide the other.
        OccurrencePath elsewhere = OccurrencePath.Of(OccurrenceId.New(), bracket, bolt);
        elsewhere.IsUnder(OccurrencePath.Of(chassis, bracket)).Should().BeFalse();
    }

    [Fact]
    public void APathReportsItsLeafAndItsParent()
    {
        OccurrenceId bracket = OccurrenceId.New();
        OccurrenceId bolt = OccurrenceId.New();

        OccurrencePath path = OccurrencePath.Of(bracket, bolt);

        path.Leaf.Should().Be(bolt);
        path.Depth.Should().Be(2);
        path.Parent().Should().Be(OccurrencePath.Of(bracket));
        path.Parent().Parent().Should().Be(OccurrencePath.Root);
    }

    [Fact]
    public void TheRootPathHasNoLeafAndNoParent()
    {
        OccurrencePath.Root.IsRoot.Should().BeTrue();

        FluentActions.Invoking(() => OccurrencePath.Root.Leaf)
            .Should().Throw<InvalidOperationException>();

        FluentActions.Invoking(() => OccurrencePath.Root.Parent())
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void APathStepMustNameSomething()
    {
        // A default id compares equal to every other default, so two unrelated instances would
        // look like one.
        FluentActions.Invoking(() => OccurrencePath.Of(OccurrenceId.None))
            .Should().Throw<ArgumentException>();

        FluentActions.Invoking(() => OccurrencePath.Root.Then(OccurrenceId.None))
            .Should().Throw<ArgumentException>();
    }

    // --- Problems --------------------------------------------------------------------------------

    [Fact]
    public void AnAssemblyThatIsFineHasNoProblems()
    {
        new Product().Assembly.Problems().Should().BeEmpty();
    }

    [Fact]
    public void AComponentWithNoSourceIsAProblem()
    {
        Assembly assembly = Assembly.Empty.WithDefinition(
            new ComponentDefinition(ComponentDefinitionId.New(), "  ", "Nameless"));

        assembly.Problems().Should().ContainSingle().Which.Should().Contain("Nameless");
    }

    [Fact]
    public void APlacementWithAnUnusableTransformIsAProblem()
    {
        ComponentDefinition bolt = Part("Bolt", "bolt.omcad");

        Assembly assembly = Assembly.Empty
            .WithDefinition(bolt)
            .WithOccurrence(new ComponentOccurrence(
                OccurrenceId.New(),
                bolt.Id,
                new Transform(Quatd.Identity, Vec3d.Zero, 0)));

        // Reported rather than refused at insertion, the same call Sketch.Problems makes: a
        // document that will not open is a worse answer than one that says what is wrong with it.
        assembly.Problems().Should().ContainSingle().Which.Should().Contain("Bolt");
    }

    // --- Helpers ---------------------------------------------------------------------------------

    private static ComponentDefinition Part(string name, string source)
        => new(ComponentDefinitionId.New(), source, name);

    private static ComponentDefinition SubAssembly(string name, string source)
        => new(ComponentDefinitionId.New(), source, name, ComponentKind.SubAssembly);

    private static ComponentOccurrence Placed(ComponentDefinition definition, Vec3d at)
        => new(OccurrenceId.New(), definition.Id, Transform.FromTranslation(at));

    /// <summary>
    /// A chassis holding one bracket document twice, each holding one bolt: the smallest shape in
    /// which "the same structure reached by two routes" is a real question.
    /// </summary>
    private sealed class Product
    {
        private readonly ComponentDefinition _bracket = SubAssembly("Bracket", "bracket.omcad");
        private readonly Assembly _inner;

        public Product()
        {
            ComponentDefinition bolt = Part("Bolt", "bolt.omcad");

            BoltInBracket = OccurrenceId.New();
            LeftBracket = OccurrenceId.New();
            RightBracket = OccurrenceId.New();

            _inner = Assembly.Empty
                .WithDefinition(bolt)
                .WithOccurrence(new ComponentOccurrence(
                    BoltInBracket, bolt.Id, Transform.FromTranslation(new Vec3d(5, 0, 0))));

            Assembly = Assembly.Empty
                .WithDefinition(_bracket)
                .WithOccurrence(new ComponentOccurrence(
                    LeftBracket, _bracket.Id, Transform.FromTranslation(new Vec3d(100, 0, 0))))
                .WithOccurrence(new ComponentOccurrence(
                    RightBracket, _bracket.Id, Transform.FromTranslation(new Vec3d(-100, 0, 0))));
        }

        public Assembly Assembly { get; }

        public OccurrenceId LeftBracket { get; }

        public OccurrenceId RightBracket { get; }

        public OccurrenceId BoltInBracket { get; }

        /// <summary>How many times the sub-assembly's structure has been asked for.</summary>
        public int Loads { get; private set; }

        public Assembly? Load(ComponentDefinition definition)
        {
            if (definition.Id != _bracket.Id)
            {
                return null;
            }

            ++Loads;

            return _inner;
        }
    }
}
