using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Kernel;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// The document graph (P3-T01): what a document holds and what it refuses to hold.
/// </summary>
/// <remarks>
/// The interesting assertions here are the refusals. A document that accepts a body whose producer
/// is absent, or two features under one id, is not broken at the moment it accepts them — it breaks
/// later, during a rebuild, in a way that names the rebuild rather than the edit that caused it.
/// </remarks>
public sealed class DocumentTests
{
    [Fact]
    public void ANewDocumentHasSomewhereToStartSketching()
    {
        Document document = Document.Empty();

        document.Features.Should().BeEmpty();
        document.Bodies.Should().BeEmpty();
        document.Version.Should().Be(0);

        // Three planes and an origin, present before the user has done anything. A document whose
        // first required action is "create somewhere to work" makes the user do the modeller's job.
        document.References.Should().HaveCount(4);

        document.References.OfType<ReferenceGeometry.Plane>().Should().HaveCount(3);
        document.References.OfType<ReferenceGeometry.Point>().Should().ContainSingle();

        document.References.Should().OnlyContain(
            r => r.Owner == FeatureId.None, "origin geometry is not the output of any feature");
    }

    [Fact]
    public void EditingADocumentLeavesTheOriginalAlone()
    {
        // The property everything else in the phase is built on. Undo is holding an earlier
        // reference (P3-T17), a rebuild reads a document that cannot change underneath it, and
        // both are only true if an edit genuinely produces a different object.
        Document original = Document.Empty();
        Feature feature = Feature.Create(FeatureId.New(), "Extrude1", "Extrude");

        Document edited = original.WithFeatureAdded(feature);

        original.Features.Should().BeEmpty("the document that was edited must be untouched");
        original.Version.Should().Be(0);

        edited.Features.Should().ContainSingle();
        edited.Version.Should().Be(1);
    }

    [Fact]
    public void EveryKindOfChangeAdvancesTheVersion()
    {
        // Claimed in the code: every mutator routes through one place so the version cannot be
        // advanced by some of them and forgotten by others. Two documents that differ but share a
        // version would let a geometry cache serve one's result for the other.
        FeatureId id = FeatureId.New();

        Document document = Document.Empty().WithFeatureAdded(Feature.Create(id, "Extrude1", "Extrude"));

        List<Func<Document, Document>> changes =
        [
            d => d.WithFeatureAdded(Feature.Create(FeatureId.New(), "Extrude2", "Extrude")),
            d => d.WithFeatureReplaced(d.Features[0] with { Name = "Renamed" }),
            d => d.WithParameter(new Parameter("Length", Quantity.Metres(0.1))),
            d => d.WithParameterRemoved("Length"),
            d => d.WithBody(new Body(BodyId.New(), id, BodyKind.Solid, new KernelShape(1))),
            d => d.WithReference(new ReferenceGeometry.Point(id, "Hole centre", Math.Vec3d.Zero)),
            d => d.WithMetadata(DocumentMetadata.Empty with { PartNumber = "A-1" }),
            d => d.WithFeatureRemoved(id),
        ];

        foreach (Func<Document, Document> change in changes)
        {
            long before = document.Version;
            document = change(document);

            document.Version.Should().BeGreaterThan(
                before, "every change has to be distinguishable from the state before it");
        }
    }

    [Fact]
    public void TwoFeaturesCannotShareAnId()
    {
        FeatureId id = FeatureId.New();
        Document document = Document.Empty().WithFeatureAdded(Feature.Create(id, "Extrude1", "Extrude"));

        Action second = () => document.WithFeatureAdded(Feature.Create(id, "Extrude2", "Extrude"));

        second.Should().Throw<ArgumentException>(
            "an id that names two features makes every reference to it undecidable");
    }

    [Fact]
    public void ReplacingAFeatureKeepsItsPlaceInTheTree()
    {
        FeatureId middle = FeatureId.New();

        Document document = Document.Empty()
            .WithFeatureAdded(Feature.Create(FeatureId.New(), "First", "Extrude"))
            .WithFeatureAdded(Feature.Create(middle, "Second", "Extrude"))
            .WithFeatureAdded(Feature.Create(FeatureId.New(), "Third", "Extrude"));

        Document edited = document.WithFeatureReplaced(
            document.FindFeature(middle)! with { Name = "Renamed" });

        edited.Features.Select(f => f.Name).Should().Equal(
            ["First", "Renamed", "Third"],
            "the tree is an order the user arranged, and editing a feature is not reordering it");
    }

    [Fact]
    public void ReplacingAFeatureThatIsNotThereIsRefused()
    {
        Document document = Document.Empty();

        Action replace = () => document.WithFeatureReplaced(
            Feature.Create(FeatureId.New(), "Ghost", "Extrude"));

        // Not silently treated as an addition. A mistyped id would then insert a feature the caller
        // believed it was editing, and the document would be quietly wrong rather than loudly.
        replace.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RemovingAFeatureTakesItsBodiesWithIt()
    {
        FeatureId owner = FeatureId.New();
        BodyId body = BodyId.New();

        Document document = Document.Empty()
            .WithFeatureAdded(Feature.Create(owner, "Extrude1", "Extrude"))
            .WithBody(new Body(body, owner, BodyKind.Solid, new KernelShape(1)));

        document.BodiesOf(owner).Should().ContainSingle();

        Document without = document.WithFeatureRemoved(owner);

        // A body names its producer. One left behind would point at a feature that is gone, and
        // could never be rebuilt, because the thing that knew how to build it no longer exists.
        without.FindBody(body).Should().BeNull();
        without.Bodies.Should().BeEmpty();
    }

    [Fact]
    public void ABodyWithNoProducerIsRefused()
    {
        Document document = Document.Empty();

        Action orphan = () => document.WithBody(
            new Body(BodyId.New(), FeatureId.New(), BodyKind.Solid, new KernelShape(1)));

        orphan.Should().Throw<ArgumentException>(
            "every body is the result of a feature, and one whose producer is absent can never be "
            + "rebuilt");
    }

    [Fact]
    public void MovingAFeatureChangesOrderAndNothingElse()
    {
        FeatureId first = FeatureId.New();
        FeatureId second = FeatureId.New();
        FeatureId third = FeatureId.New();

        // The third consumes the first, so the move below puts a consumer before what it consumes.
        Document document = Document.Empty()
            .WithFeatureAdded(Feature.Create(first, "First", "Extrude"))
            .WithFeatureAdded(Feature.Create(second, "Second", "Extrude"))
            .WithFeatureAdded(Feature.Create(third, "Third", "Fillet") with { Inputs = [first] });

        Document moved = document.WithFeatureMoved(third, 0);

        moved.Features.Select(f => f.Name).Should().Equal(["Third", "First", "Second"]);

        // Whether that move is legal is a question about the dependency graph, which P3-T03 builds
        // and P3-T02's commit is where it gets asked. This method does not know and must not guess:
        // silently reordering or rejecting here would put graph policy in the wrong layer.
        moved.FindFeature(third)!.Inputs.Should().Equal(
            [first], "moving a feature must not rewrite what it consumes");
    }

    [Fact]
    public void ParametersAreFoundWhateverTheCaseTheyAreTypedIn()
    {
        Document document = Document.Empty()
            .WithParameter(new Parameter("Length", Quantity.Metres(0.025)));

        document.FindParameter("length").Should().NotBeNull();
        document.FindParameter("LENGTH").Should().NotBeNull();

        // The declared spelling survives: only comparison ignores case, because the name is shown
        // back to the person who chose it.
        document.FindParameter("length")!.Name.Should().Be("Length");
    }

    [Fact]
    public void SettingAParameterTwiceReplacesRatherThanDuplicates()
    {
        Document document = Document.Empty()
            .WithParameter(new Parameter("Length", Quantity.Metres(0.025)))
            .WithParameter(new Parameter("length", Quantity.Metres(0.050)));

        document.Parameters.Should().ContainSingle(
            "the two names differ only in case, so they are the same parameter");

        document.FindParameter("Length")!.Value.Value.Should().Be(0.050);
    }

    [Fact]
    public void BodiesOfReturnsOnlyTheOnesThatFeatureProduced()
    {
        FeatureId mine = FeatureId.New();
        FeatureId theirs = FeatureId.New();

        Document document = Document.Empty()
            .WithFeatureAdded(Feature.Create(mine, "Mine", "Extrude"))
            .WithFeatureAdded(Feature.Create(theirs, "Theirs", "Extrude"))
            .WithBody(new Body(BodyId.New(), mine, BodyKind.Solid, new KernelShape(1)))
            .WithBody(new Body(BodyId.New(), mine, BodyKind.Sheet, new KernelShape(2)))
            .WithBody(new Body(BodyId.New(), theirs, BodyKind.Solid, new KernelShape(3)));

        document.BodiesOf(mine).Should().HaveCount(2, "one feature may produce several bodies");
        document.BodiesOf(theirs).Should().ContainSingle();
        document.BodiesOf(FeatureId.New()).Should().BeEmpty();
    }

    [Fact]
    public void AFeatureKnowsWhetherItConsumesAnything()
    {
        Feature root = Feature.Create(FeatureId.New(), "Extrude1", "Extrude");
        Feature dependent = root with { Inputs = [FeatureId.New()] };

        root.IsRoot.Should().BeTrue("a feature that consumes nothing can always evaluate first");
        dependent.IsRoot.Should().BeFalse();
    }

    [Fact]
    public void AFeatureFindsItsOwnParametersWithoutRegardToCase()
    {
        Feature feature = Feature.Create(FeatureId.New(), "Extrude1", "Extrude") with
        {
            Parameters = [new Parameter("Depth", Quantity.Metres(0.01))],
        };

        feature.FindParameter("depth").Should().NotBeNull();
        feature.FindParameter("width").Should().BeNull();
    }

    [Fact]
    public void ImmutableCollectionsAreSharedRatherThanCopied()
    {
        // The claim that makes immutability affordable: an edit to one feature copies a spine of
        // pointers, not the document. Asserted by identity — the reference geometry and parameters
        // of the edited document are the very same objects, not equal copies.
        Document document = Document.Empty()
            .WithParameter(new Parameter("Length", Quantity.Metres(0.025)));

        Document edited = document.WithFeatureAdded(
            Feature.Create(FeatureId.New(), "Extrude1", "Extrude"));

        edited.References.Should().Equal(document.References);

        edited.FindParameter("Length").Should().BeSameAs(
            document.FindParameter("Length"),
            "an edit that touches features has no reason to rebuild the parameters");
    }

    [Fact]
    public void ACoordinateSystemCannotDisagreeWithItself()
    {
        // The second axis is derived rather than stored, so there is no way to record a frame whose
        // three axes are inconsistent — which a three-field record would permit and nothing would
        // notice until geometry came out mirrored.
        ReferenceGeometry.CoordinateSystem frame = new(
            FeatureId.None, "Frame", Math.Vec3d.Zero, Math.Vec3d.UnitX, Math.Vec3d.UnitZ);

        frame.YAxis.Should().Be(Math.Vec3d.Cross(Math.Vec3d.UnitZ, Math.Vec3d.UnitX));
        frame.YAxis.Should().Be(Math.Vec3d.UnitY);
    }
}
