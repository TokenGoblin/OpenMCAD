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
        //
        // It carries one setting of every kind there is, so that each kind tag is pinned and not
        // merely written down: without a tag two kinds can encode identically, and the pair that
        // does -- text and a choice -- would then share a cache entry while meaning different
        // things. The settings also pin the by-name ordering. That last guard is only as good as
        // the enumeration order this process happens to produce, since an ImmutableDictionary
        // orders by hash and .NET randomises string hashing per run; it is a real guard on the runs
        // where the two differ, which are exactly the runs the sort exists for.
        Feature feature = new(
            new FeatureId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
            "Extrude1",
            "Extrude",
            [],
            [new Parameter("Depth", Quantity.Metres(0.01))],
            Settings: ImmutableDictionary<string, FeatureValue>.Empty
                .Add("Merge", new FlagValue(true))
                .Add("Direction", new ChoiceValue("Symmetric"))
                .Add("Angle", new QuantityValue(Quantity.Radians(0.5)))
                .Add("Count", new NumberValue(3))
                .Add("Label", new TextValue("Blind")));

        RebuildKey key = RebuildKey.For(feature, []);

        key.ToString().Should().Be(
            "key(C80EF14FED81A9036EC9534FAA20CAB4)",
            "the encoding is a compatibility surface: when it changes, every cached entry has to "
            + "miss rather than be misread, which is what the version tag in the key is for");
    }

    [Fact]
    public void ChangingASettingChangesTheKey()
    {
        // A setting is not decoration: which direction, how many, which end condition, whether to
        // merge (P3-T21) all decide what the feature produces. Leaving them out of the key let a
        // feature whose only edit was a setting hit the cache and hand back the geometry from
        // before it -- and unlike a wrong parameter, nothing downstream would look wrong either.
        Feature before = Extrude("Extrude1", 0.010) with { Settings = Setting("Merge", new FlagValue(false)) };
        Feature after = before with { Settings = Setting("Merge", new FlagValue(true)) };

        RebuildKey.For(before, []).Should().NotBe(RebuildKey.For(after, []));
    }

    [Fact]
    public void TwoSettingValuesOfDifferentKindsDoNotShareAKey()
    {
        // Text and a choice encode identically once you take the kind tag away -- both are just a
        // string -- and they mean different things: a free-text setting and one picked from a list
        // are not interchangeable. Without the tag these two features share a cache entry.
        Feature free = Extrude("Extrude1", 0.010) with { Settings = Setting("End", new TextValue("Blind")) };
        Feature picked = free with { Settings = Setting("End", new ChoiceValue("Blind")) };

        RebuildKey.For(free, []).Should().NotBe(RebuildKey.For(picked, []));
    }

    [Fact]
    public void TheOrderSettingsWereAddedInDoesNotChangeTheKey()
    {
        // An ImmutableDictionary promises no enumeration order. Hashing it as it comes would give
        // the same feature different keys in different processes -- a cache that mysteriously
        // stops hitting, and a determinism failure ADR-0011 does not allow.
        ImmutableDictionary<string, FeatureValue> oneWay = ImmutableDictionary<string, FeatureValue>.Empty
            .Add("Depth", new NumberValue(3))
            .Add("Angle", new NumberValue(4))
            .Add("Merge", new FlagValue(true));

        ImmutableDictionary<string, FeatureValue> theOther = ImmutableDictionary<string, FeatureValue>.Empty
            .Add("Merge", new FlagValue(true))
            .Add("Angle", new NumberValue(4))
            .Add("Depth", new NumberValue(3));

        Feature first = Extrude("Extrude1", 0.010) with { Settings = oneWay };
        Feature second = first with { Settings = theOther };

        RebuildKey.For(first, []).Should().Be(RebuildKey.For(second, []));
    }

    private static ImmutableDictionary<string, FeatureValue> Setting(string name, FeatureValue value)
        => ImmutableDictionary<string, FeatureValue>.Empty.Add(name, value);

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
        [],
        HistoryMap.Empty);
}
