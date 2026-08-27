using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Rebuild;
using OpenMCAD.Kernel;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// The geometry cache and its key (P3-T05).
/// </summary>
/// <remarks>
/// Most of these are about the key rather than the container. A cache that evicts badly is slow;
/// a cache whose key misses something that affects the result is a program that shows the user the
/// wrong solid and never mentions it. The container is worth a few tests and the key is worth the
/// rest.
/// </remarks>
public sealed class GeometryCacheTests
{
    [Fact]
    public void TheSameFeatureKeysTheSameWay()
    {
        Feature feature = Extrude("Extrude1", 0.010);

        RebuildKey.For(feature, []).Should().Be(RebuildKey.For(feature, []));
    }

    [Fact]
    public void ChangingAParameterChangesTheKey()
    {
        Feature feature = Extrude("Extrude1", 0.010);

        Feature deeper = feature with
        {
            Parameters = [new Parameter("Depth", Quantity.Metres(0.011))],
        };

        // The same feature, deepened -- not two features that happen to differ. Comparing two
        // freshly created features would pass on the difference in their ids alone and say nothing
        // about whether the depth reached the key at all.
        RebuildKey.For(feature, []).Should().NotBe(RebuildKey.For(deeper, []));
    }

    [Fact]
    public void ChangingTheFeatureTypeChangesTheKey()
    {
        Feature extrude = Extrude("Feature1", 0.010);
        Feature revolve = extrude with { FeatureType = "Revolve" };

        RebuildKey.For(extrude, []).Should().NotBe(RebuildKey.For(revolve, []));
    }

    [Fact]
    public void TheDisplayNameDoesNotChangeTheKey()
    {
        // Renaming a feature in the tree is not a modelling change and must not throw away its
        // geometry. This is a real gesture -- tidying up a tree before sending a file -- and one
        // that would otherwise rebuild the whole model.
        Feature before = Extrude("Extrude1", 0.010);
        Feature after = before with { Name = "Main body" };

        RebuildKey.For(before, []).Should().Be(RebuildKey.For(after, []));
    }

    [Fact]
    public void ChangingAUnitChangesTheKeyEvenAtTheSameNumber()
    {
        // Ten metres and ten radians are not the same thing, and a key built from the magnitude
        // alone would say they were.
        Feature length = Extrude("Feature1", 0.010);

        Feature angle = length with
        {
            Parameters = [new Parameter("Depth", new Quantity(0.010, Dimension.Angle))],
        };

        RebuildKey.For(length, []).Should().NotBe(RebuildKey.For(angle, []));
    }

    [Fact]
    public void AKeyFoldsInWhatItsInputsProduced()
    {
        Feature fillet = Extrude("Fillet1", 0.002);

        RebuildKey first = new(1, 2);
        RebuildKey second = new(3, 4);

        RebuildKey.For(fillet, [first]).Should().NotBe(RebuildKey.For(fillet, [second]));
    }

    [Fact]
    public void InputOrderIsPartOfTheKey()
    {
        // Subtracting A from B is not subtracting B from A. A key that treated inputs as a set
        // would serve one for the other.
        Feature boolean = Extrude("Cut1", 0.0);

        RebuildKey a = new(1, 2);
        RebuildKey b = new(3, 4);

        RebuildKey.For(boolean, [a, b]).Should().NotBe(RebuildKey.For(boolean, [b, a]));
    }

    [Fact]
    public void AMissingInputIsNotTheSameAsNoInput()
    {
        Feature feature = Extrude("Fillet1", 0.002);

        RebuildKey.For(feature, [RebuildKey.None]).Should().NotBe(RebuildKey.For(feature, []));
    }

    [Fact]
    public void TextIsLengthPrefixedSoItCannotBeReadTwoWays()
    {
        // Without a length prefix, a feature type of "ab" with a parameter named "c" encodes to the
        // same bytes as one of "a" with a parameter named "bc". A cache collision is not a slow
        // lookup; it is the wrong solid, served confidently.
        // One id, so the only thing that differs is where the boundary between the two strings
        // falls. With separate ids this would pass without the length prefixes existing.
        FeatureId id = FeatureId.New();

        Feature first = new(
            id, "F", "ab",
            [],
            [new Parameter("c", Quantity.Metres(1))]);

        Feature second = new(
            id, "F", "a",
            [],
            [new Parameter("bc", Quantity.Metres(1))]);

        RebuildKey.For(first, []).Should().NotBe(RebuildKey.For(second, []));
    }

    [Fact]
    public void TwoParametersCannotBeConfusedForOne()
    {
        Feature two = new(
            FeatureId.New(), "F", "Extrude",
            [],
            [new Parameter("A", Quantity.Metres(1)), new Parameter("B", Quantity.Metres(2))]);

        Feature swapped = two with
        {
            Parameters = [new Parameter("B", Quantity.Metres(1)), new Parameter("A", Quantity.Metres(2))],
        };

        RebuildKey.For(two, []).Should().NotBe(RebuildKey.For(swapped, []));
    }

    [Fact]
    public void AKeyIsTheSameInAnyProcess()
    {
        // The trap this exists to avoid: .NET randomises string hashing per process, so a key built
        // from string.GetHashCode differs between two runs of the same program -- and a cache that
        // never hits after a restart is not a cache.
        //
        // The expected value is written down rather than computed, so a change to the encoding has
        // to be a deliberate one. It was produced by a second implementation of the encoding, in
        // another language, rather than by pasting in whatever this code happened to emit -- which
        // would pin the behaviour without checking it against the description of it.
        Feature feature = new(
            new FeatureId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            "Extrude1",
            "Extrude",
            [],
            [new Parameter("Depth", Quantity.Metres(0.01))]);

        RebuildKey key = RebuildKey.For(feature, []);

        key.ToString().Should().Be(
            "key(21A6E8BE13588B3C905B3AA1322642A4)",
            "the encoding is a compatibility surface: when it changes, every cached entry has to "
            + "miss rather than be misread, which is what the version tag in the key is for");
    }

    [Fact]
    public void TwoIdenticalFeaturesDoNotShareAnEntry()
    {
        // They would produce the same geometry, so a purely content-addressed key would be sound
        // in principle. What is cached is a FeatureOutput, and its bodies each name the feature
        // that owns them -- so handing one feature the other's entry gives it bodies belonging to
        // its neighbour, and the document then reports that neither feature produced anything.
        // Sharing would mean canonicalising the output first, which is a separate optimisation
        // nobody has asked for.
        Feature first = Extrude("Extrude1", 0.010);
        Feature second = first with { Id = FeatureId.New() };

        RebuildKey.For(first, []).Should().NotBe(RebuildKey.For(second, []));
    }

    [Fact]
    public void TheCacheReturnsWhatItWasGiven()
    {
        GeometryCache cache = new();
        RebuildKey key = new(1, 2);
        FeatureOutput output = Output();

        cache.TryGet(key, out _).Should().BeFalse();

        cache.Store(key, output);

        cache.TryGet(key, out FeatureOutput found).Should().BeTrue();
        found.Should().BeSameAs(output);

        cache.Hits.Should().Be(1);
        cache.Misses.Should().Be(1);
    }

    [Fact]
    public void TheLeastRecentlyUsedEntryIsDropped()
    {
        GeometryCache cache = new(capacity: 3);

        RebuildKey a = new(1, 0);
        RebuildKey b = new(2, 0);
        RebuildKey c = new(3, 0);
        RebuildKey d = new(4, 0);

        cache.Store(a, Output());
        cache.Store(b, Output());
        cache.Store(c, Output());

        // Reading A makes it the most recent, so B becomes the oldest. Ordering by when an entry
        // was written rather than last read would evict A -- which is exactly the entry being
        // returned to, since returning to old states is what this cache is for.
        cache.TryGet(a, out _).Should().BeTrue();

        cache.Store(d, Output());

        cache.Count.Should().Be(3);
        cache.TryGet(a, out _).Should().BeTrue();
        cache.TryGet(b, out _).Should().BeFalse();
        cache.TryGet(c, out _).Should().BeTrue();
        cache.TryGet(d, out _).Should().BeTrue();
    }

    [Fact]
    public void DroppingAnEntryAnnouncesIt()
    {
        // A cached output names shapes living inside the kernel. Dropping it without telling
        // anyone leaks them for the life of the process.
        GeometryCache cache = new(capacity: 1);

        List<FeatureOutput> evicted = [];
        cache.Evicted += evicted.Add;

        FeatureOutput first = Output();
        FeatureOutput second = Output();

        cache.Store(new RebuildKey(1, 0), first);
        cache.Store(new RebuildKey(2, 0), second);

        evicted.Should().ContainSingle().Which.Should().BeSameAs(first);

        cache.Clear();
        evicted.Should().HaveCount(2).And.Contain(second);
    }

    [Fact]
    public void ReplacingAnEntryAnnouncesTheOldOne()
    {
        GeometryCache cache = new();

        List<FeatureOutput> evicted = [];
        cache.Evicted += evicted.Add;

        FeatureOutput first = Output();
        RebuildKey key = new(1, 0);

        cache.Store(key, first);
        cache.Store(key, Output());

        evicted.Should().ContainSingle().Which.Should().BeSameAs(
            first, "the shapes it named are no longer reachable from the cache either");
    }

    [Fact]
    public void TheNullCacheNeverRemembersAnything()
    {
        NullGeometryCache cache = NullGeometryCache.Instance;

        cache.Store(new RebuildKey(1, 2), Output());

        cache.TryGet(new RebuildKey(1, 2), out _).Should().BeFalse();
        cache.Count.Should().Be(0);
    }

    private static Feature Extrude(string name, double depth) => new(
        FeatureId.New(),
        name,
        "Extrude",
        [],
        [new Parameter("Depth", Quantity.Metres(depth))]);

    private static FeatureOutput Output() => new(
        [new Body(BodyId.New(), FeatureId.New(), BodyKind.Solid, new KernelShape(1))],
        []);
}
