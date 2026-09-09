using System.Collections.Immutable;

using FluentAssertions;

using OpenMCAD.Core.Documents;
using OpenMCAD.Core.Naming;
using OpenMCAD.Core.Rebuild;
using OpenMCAD.Kernel;
using OpenMCAD.Kernel.Threading;
using OpenMCAD.Math;

using Xunit;

namespace OpenMCAD.Core.Tests;

/// <summary>
/// The naming regression corpus (P3-T13): build a model, edit it, and check that every downstream
/// reference still points at what the user meant.
/// </summary>
/// <remarks>
/// <para>
/// §5.3 gives the shape — build, apply a parametric edit, assert every downstream feature resolves
/// to the intended entity — and lists ten mandatory categories. Six of them are about the document,
/// the rebuild and the resolution of names, which is what Phase 3 builds, and they are here. Sketch
/// topology change is the seventh: P4-T03 and P4-T04 brought a sketch to change, and the seam the
/// naming layer had always had for it -- a name whose provenance bottoms out in a sketch entity
/// rather than in another named entity -- is wired through the engine as of this scenario. The
/// remaining three need feature types that do not exist: there is nothing to pattern, nothing to
/// mirror, and no importer. <see cref="EveryMandatoryCategoryIsAccountedFor"/> is what stops those
/// being quietly forgotten.
/// </para>
/// <para>
/// These run the whole stack rather than a piece of it: a real <see cref="DocumentSession"/>, the
/// real <see cref="RebuildEngine"/> on a real kernel dispatcher, real <see cref="HistoryMap"/>s, and
/// resolution through all three tiers. Nothing else in the suite does that end to end, and the
/// interesting failures in a naming system live between the parts rather than inside them.
/// </para>
/// <para>
/// The evaluator below issues fresh entity tags on every rebuild, deliberately. A kernel does that
/// too, and a naming layer that only worked while the tags happened to stay the same would pass
/// every test here and fail on the first real edit.
/// </para>
/// </remarks>
public sealed class NamingCorpusTests
{
    /// <summary>The categories §5.3 makes mandatory, and where each one stands.</summary>
    /// <remarks>
    /// Written down so that "we forgot" and "we cannot yet" are different states, and so that the
    /// second turns into a failing test the moment its blocker lands rather than being noticed a
    /// phase later. §5.3 also requires every feature type added in any later phase to add cases
    /// here, and a list nobody maintains is how that requirement quietly stops being met.
    /// </remarks>
    private static readonly (string Category, string? BlockedBy)[] Mandatory =
    [
        ("dimension change", null),
        ("feature reorder", null),
        ("feature suppression", null),
        ("feature deletion with dependents", null),
        ("face split by a later feature", null),
        ("body split", null),
        ("sketch topology change", null),
        ("pattern instance count change", "P5 — there is no pattern feature"),
        ("mirror", "P5 — there is no mirror feature"),
        ("imported-geometry reference", "Phase 8 — there is no importer"),
    ];

    [Fact]
    public void EveryMandatoryCategoryIsAccountedFor()
    {
        // The durable part of this task. Each of §5.3's ten categories is either covered by a
        // scenario below or explicitly blocked on a named phase -- never silently absent.
        Mandatory.Should().HaveCount(10, "§5.3 lists ten and calls all of them mandatory");

        ImmutableArray<string> covered =
            [.. Mandatory.Where(m => m.BlockedBy is null).Select(m => m.Category)];

        ImmutableArray<string> blocked =
            [.. Mandatory.Where(m => m.BlockedBy is not null).Select(m => m.Category)];

        covered.Should().HaveCount(7);
        blocked.Should().HaveCount(3);

        foreach ((string category, string? blockedBy) in Mandatory)
        {
            if (blockedBy is not null)
            {
                blockedBy.Should().NotBeNullOrWhiteSpace(
                    $"'{category}' is not covered, so it has to say what it is waiting for");
            }
        }
    }

    [Fact]
    public async Task ADimensionChangeDoesNotMoveAReference()
    {
        // The everyday edit, and the one naming exists for. Every entity tag is different after
        // the rebuild; the reference has to land on the same face regardless.
        using Scenario scene = new();

        FeatureId body = scene.AddBase("Extrude1");
        FeatureId consumer = scene.AddConsumer("Fillet1", body, EntityRole.SideWall);

        await scene.RebuildAsync();

        SubEntity before = scene.WhatItUsed(consumer);
        before.IsValid.Should().BeTrue();

        scene.ChangeDimension(body, 0.05);
        await scene.RebuildAsync();

        SubEntity after = scene.WhatItUsed(consumer);

        after.Should().NotBe(before, "the kernel issued new tags, as a kernel does");
        scene.RoleOf(body, after).Should().Be(OperationRole.SideWall);
        scene.Report.StateOf(consumer).Should().Be(FeatureState.Ok);
    }

    [Fact]
    public async Task AFeatureInsertedAboveAReferenceDoesNotBreakIt()
    {
        // Not one of the ten by name, but the commonest edit there is and the reason resolution
        // walks the chain forward rather than stopping where the name was written.
        using Scenario scene = new();

        FeatureId body = scene.AddBase("Extrude1");
        FeatureId consumer = scene.AddConsumer("Fillet1", body, EntityRole.SideWall);

        await scene.RebuildAsync();

        scene.AddModifier("Shell1", body);
        await scene.RebuildAsync();

        scene.Report.StateOf(consumer).Should().Be(FeatureState.Ok);
        scene.WhatItUsed(consumer).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task AddingASketchLineDoesNotDisturbAReferenceToAnotherLinesWall()
    {
        // The category §5.3 names. A reference here is anchored to a *sketch entity*, not to a
        // role, so the wall it wants is "the one L2 made" however many other lines the sketch
        // gains. Adding L4 reissues every tag and adds a wall in the middle of the list; nothing
        // about that is a fact about L2.
        using Scenario scene = new(sketch: ["L1", "L2", "L3"]);

        FeatureId body = scene.AddBase("Extrude1");
        FeatureId consumer = scene.AddSketchConsumer("Fillet1", body, "L2");

        await scene.RebuildAsync();

        SubEntity before = scene.WhatItUsed(consumer);
        before.IsValid.Should().BeTrue();
        scene.WallFor(body, "L2").Should().Be(before);

        scene.AddSketchLine("L4");
        await scene.RebuildAsync();

        SubEntity after = scene.WhatItUsed(consumer);

        after.Should().NotBe(before, "the kernel issued new tags, as a kernel does");
        after.Should().Be(
            scene.WallFor(body, "L2"), "the reference still means the wall L2 made");
        scene.RoleOf(body, after).Should().Be(OperationRole.SideWall);
        scene.Report.StateOf(consumer).Should().Be(FeatureState.Ok);
    }

    [Fact]
    public async Task RemovingASketchLineBreaksOnlyTheReferenceThatNamedIt()
    {
        // The half that matters more. Deleting L2 must not quietly re-point its consumer at L1's
        // wall or L3's -- both are side walls of the same extrude, both are plausible, and either
        // would be the silent corruption §5.3 forbids. The consumer that named L3 is untouched.
        using Scenario scene = new(sketch: ["L1", "L2", "L3"]);

        FeatureId body = scene.AddBase("Extrude1");
        FeatureId onDoomed = scene.AddSketchConsumer("Fillet1", body, "L2");
        FeatureId onSurvivor = scene.AddSketchConsumer("Fillet2", body, "L3");

        await scene.RebuildAsync();
        scene.Report.StateOf(onDoomed).Should().Be(FeatureState.Ok);

        scene.RemoveSketchLine("L2");
        await scene.RebuildAsync();

        scene.Report.StateOf(onDoomed).Should().Be(
            FeatureState.UnresolvedReference,
            "the line it was built on is gone, and no other wall is a substitute for it");
        scene.Report.Repairs.Should().ContainSingle()
            .Which.Feature.Should().Be(onDoomed);

        scene.Report.StateOf(onSurvivor).Should().Be(
            FeatureState.Ok, "nothing happened to L3");
        scene.WhatItUsed(onSurvivor).Should().Be(scene.WallFor(body, "L3"));
    }

    [Fact]
    public async Task ASketchSourceIsUnsupportedRatherThanMissingWhenNothingCanLookOneUp()
    {
        // The distinction the naming layer draws and the engine now has to preserve: a host with
        // no way to resolve a sketch entity has not discovered a broken model, it has failed to
        // answer. Reporting that as a missing reference would make every sketch-anchored name in
        // an unwired build look like user-visible damage.
        using Scenario scene = new(sketch: ["L1", "L2"], wireSketchLookup: false);

        FeatureId body = scene.AddBase("Extrude1");
        FeatureId consumer = scene.AddSketchConsumer("Fillet1", body, "L1");

        await scene.RebuildAsync();

        scene.Report.StateOf(consumer).Should().Be(FeatureState.UnresolvedReference);
        scene.Report.Repairs.Should().ContainSingle()
            .Which.Outcome.Should().Be(
                NameResolutionOutcome.Unsupported,
                "'cannot answer' and 'the answer is no' are different, and only the outcome "
                + "carries that distinction anywhere a caller can act on it");
    }

    [Fact]
    public async Task ReorderingFeaturesDoesNotBreakAReference()
    {
        using Scenario scene = new();

        FeatureId body = scene.AddBase("Extrude1");
        FeatureId unrelated = scene.AddBase("Extrude2");
        FeatureId consumer = scene.AddConsumer("Fillet1", body, EntityRole.SideWall);

        await scene.RebuildAsync();
        scene.Report.StateOf(consumer).Should().Be(FeatureState.Ok);

        // The unrelated feature moves to the top of the tree. The dependency graph is unchanged,
        // so the reference must be too -- a reorder that broke references would make the tree
        // unsafe to tidy.
        scene.Move(unrelated, 0);
        await scene.RebuildAsync();

        scene.Report.StateOf(consumer).Should().Be(FeatureState.Ok);
        scene.WhatItUsed(consumer).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task SuppressingWhatAReferencePointsIntoIsReportedAndNotGuessedAt()
    {
        using Scenario scene = new();

        FeatureId body = scene.AddBase("Extrude1");
        FeatureId consumer = scene.AddConsumer("Fillet1", body, EntityRole.SideWall);

        await scene.RebuildAsync();

        scene.Suppress(body);
        await scene.RebuildAsync();

        // The consuming feature cannot build, and this is not an error: the user asked for the
        // thing it depends on to be absent.
        scene.Report.StateOf(body).Should().Be(FeatureState.Suppressed);
        scene.Report.StateOf(consumer).Should().Be(FeatureState.Blocked);
        scene.Report.HasErrors.Should().BeFalse();

        // And unsuppressing puts it back, rather than leaving a reference that has to be repaired.
        scene.Unsuppress(body);
        await scene.RebuildAsync();

        scene.Report.StateOf(consumer).Should().Be(FeatureState.Ok);
    }

    [Fact]
    public async Task DeletingWhatAReferencePointsIntoIsAnErrorWithARepair()
    {
        using Scenario scene = new();

        FeatureId body = scene.AddBase("Extrude1");
        FeatureId consumer = scene.AddConsumer("Fillet1", body, EntityRole.SideWall);

        await scene.RebuildAsync();

        scene.Delete(body);
        await scene.RebuildAsync();

        // A dangling feature input, which P3-T03 detects and P3-T07 reports. The user gets one
        // problem to fix rather than a silent reattachment to whatever else is lying around.
        scene.Report.HasErrors.Should().BeTrue();
        scene.Report.StateOf(consumer).Should().Be(FeatureState.MissingInput);
        scene.Session.Current.BodiesOf(consumer).Should().BeEmpty();
    }

    [Fact]
    public async Task AFaceSplitByALaterFeatureFollowsTheDeclaredPolicy()
    {
        // The category §5.3 singles out as where most naming bugs live. Same split, two features,
        // two correct answers -- and neither of them is "pick one and carry on".
        using Scenario scene = new();

        FeatureId body = scene.AddBase("Extrude1");

        FeatureId strict = scene.AddConsumer(
            "Fillet1", body, EntityRole.SideWall, MultiplicityPolicy.ExactlyOne);

        FeatureId region = scene.AddConsumer(
            "Shell1", body, EntityRole.SideWall, MultiplicityPolicy.AllDescendants);

        await scene.RebuildAsync();
        scene.Report.StateOf(strict).Should().Be(FeatureState.Ok);

        // A later feature divides that wall symmetrically, so nothing distinguishes the halves.
        // Added and then moved above the consumers, because in a real tree the cut comes before
        // the features built on what it produced.
        FeatureId splitter = scene.AddSplitter("Pocket1", body);
        scene.Move(splitter, 1);

        // Inserting into a chain re-points what came after it, which is what a feature tree does:
        // each feature works on the body the one above it produced, not on the original. Without
        // that the consumers would still declare the base as their only input, the graph would not
        // know the cut had anything to do with them, and the cache would be right to hand back
        // their previous result unchanged.
        scene.Reroute(strict, splitter);
        scene.Reroute(region, splitter);

        await scene.RebuildAsync();

        scene.Report.StateOf(region).Should().Be(
            FeatureState.Ok, "a feature acting on a region wants every piece");

        scene.UsedCount(region).Should().Be(2);

        scene.Report.StateOf(strict).Should().Be(
            FeatureState.UnresolvedReference,
            "a feature that meant one face must stop and ask rather than take half of it");

        scene.Report.Repairs.Should().ContainSingle()
            .Which.Action.Should().Contain("Fillet1");
    }

    [Fact]
    public async Task ABodySplitLeavesEachPieceOwnedAndReferenceable()
    {
        using Scenario scene = new();

        FeatureId body = scene.AddBase("Extrude1");
        await scene.RebuildAsync();

        scene.Session.Current.BodiesOf(body).Should().ContainSingle();

        FeatureId splitter = scene.AddSplitter("Cut1", body);
        await scene.RebuildAsync();

        // Each half belongs to the feature that produced it. The base still owns its one body,
        // and the split owns the two it made -- nothing is orphaned and nothing is left over from
        // the rebuild before, because P3-T04 clears what a feature no longer produces.
        scene.Session.Current.BodiesOf(splitter).Should().HaveCount(2);
        scene.Session.Current.BodiesOf(body).Should().ContainSingle();
    }

    [Fact]
    public async Task RepairingAReferenceIsNotServedFromTheCache()
    {
        // The reason references are in the cache key. The user has just said the old answer was
        // wrong; handing it back would be the one guaranteed-wrong cache hit.
        using Scenario scene = new();

        FeatureId body = scene.AddBase("Extrude1");
        FeatureId consumer = scene.AddConsumer("Fillet1", body, EntityRole.SideWall);

        await scene.RebuildAsync();
        SubEntity wall = scene.WhatItUsed(consumer);

        scene.RePoint(consumer, body, EntityRole.EndCap);
        RebuildResult result = await scene.RebuildAsync();

        result.FromCache.Should().NotContain(consumer, "its reference changed, so its key did");

        SubEntity cap = scene.WhatItUsed(consumer);
        cap.Should().NotBe(wall);
        scene.RoleOf(body, cap).Should().Be(OperationRole.EndCap);
    }

    /// <summary>A document, an engine, and an evaluator that behaves like a kernel.</summary>
    private sealed class Scenario : IDisposable
    {
        private readonly KernelDispatcher _dispatcher = new("naming corpus kernel");
        private readonly ModelEvaluator _evaluator = new();
        private readonly Dictionary<FeatureId, ImmutableArray<SubEntity>> _used = [];
        private ImmutableArray<string> _lines;
        private FeatureId _sketchOwner = FeatureId.None;

        /// <summary>Sets a scenario up.</summary>
        /// <param name="sketch">
        /// The lines of the profile the base feature is built from, one wall each. One line by
        /// default, which is the shape every scenario written before sketches existed assumes.
        /// </param>
        /// <param name="wireSketchLookup">
        /// Whether the engine is given a way to resolve a sketch entity. False stands in for a host
        /// that has none, which is a different thing from a sketch entity that is gone.
        /// </param>
        public Scenario(IEnumerable<string>? sketch = null, bool wireSketchLookup = true)
        {
            Session = new DocumentSession();
            _lines = sketch is null ? ["L1"] : [.. sketch];
            _evaluator.SketchLines = _lines;

            Engine = new RebuildEngine(
                Session,
                _dispatcher,
                _evaluator,
                new GeometryCache(),
                _evaluator.Measure,
                wireSketchLookup ? _evaluator.ProfileFor : null);

            _evaluator.Used = (feature, entities) => _used[feature] = entities;
        }

        public DocumentSession Session { get; }

        public RebuildEngine Engine { get; }

        public RebuildReport Report => Session.Current.Report;

        /// <summary>A feature that produces geometry out of nothing referenceable.</summary>
        public FeatureId AddBase(string name)
        {
            FeatureId id = FeatureId.New();

            _sketchOwner = id;

            Edit($"Add {name}", t => t.AddFeature(
                Feature.Create(id, name, "Base") with
                {
                    Parameters = [new Parameter("Depth", Quantity.Metres(0.025))],
                    Settings = Lines(_lines),
                }));

            return id;
        }

        /// <summary>A feature built on one named entity of another feature.</summary>
        public FeatureId AddConsumer(
            string name,
            FeatureId source,
            EntityRole role,
            MultiplicityPolicy policy = MultiplicityPolicy.ExactlyOne)
        {
            FeatureId id = FeatureId.New();

            Edit($"Add {name}", t => t.AddFeature(
                Feature.Create(id, name, "Consumer") with
                {
                    Inputs = [source],
                    References = [Reference(source, role, policy)],
                }));

            return id;
        }

        /// <summary>A feature that passes its input through, altering what it touches.</summary>
        public FeatureId AddModifier(string name, FeatureId source)
        {
            FeatureId id = FeatureId.New();

            Edit($"Add {name}", t => t.AddFeature(
                Feature.Create(id, name, "Modifier") with { Inputs = [source] }));

            return id;
        }

        public Task<RebuildResult> RebuildAsync() => Engine.RebuildAllAsync();

        public void ChangeDimension(FeatureId id, double depth) => Edit(
            "Change depth",
            t => t.ReplaceFeature(Session.Current.FindFeature(id)! with
            {
                Parameters = [new Parameter("Depth", Quantity.Metres(depth))],
            }));

        public void Move(FeatureId id, int index) => Edit("Reorder", t => t.MoveFeature(id, index));

        public void Suppress(FeatureId id) => Edit(
            "Suppress",
            t => t.ReplaceFeature(Session.Current.FindFeature(id)! with { IsSuppressed = true }));

        public void Unsuppress(FeatureId id) => Edit(
            "Unsuppress",
            t => t.ReplaceFeature(Session.Current.FindFeature(id)! with { IsSuppressed = false }));

        public void Delete(FeatureId id) => Edit("Delete", t => t.RemoveFeature(id));

        public void RePoint(FeatureId consumer, FeatureId source, EntityRole role) => Edit(
            "Repair reference",
            t => t.ReplaceFeature(Session.Current.FindFeature(consumer)! with
            {
                References = [Reference(source, role, MultiplicityPolicy.ExactlyOne)],
            }));

        /// <summary>Points a feature at a different input, as inserting into a chain does.</summary>
        public void Reroute(FeatureId feature, FeatureId input) => Edit(
            "Reroute",
            t => t.ReplaceFeature(Session.Current.FindFeature(feature)! with { Inputs = [input] }));

        /// <summary>A feature built on the wall one named sketch line produced.</summary>
        /// <remarks>
        /// The reference bottoms out in the sketch entity rather than in a role, which is the whole
        /// point of the category: "the wall L2 made" stays meaningful when the sketch gains or loses
        /// other lines, and a role-and-ordinal reference would not.
        /// </remarks>
        public FeatureId AddSketchConsumer(string name, FeatureId source, string line)
        {
            FeatureId id = FeatureId.New();

            EntityReference reference = new(
                PersistentName.Of(new NameSegment(
                    source,
                    ProvenanceKind.Generated,
                    [new NameSource.Sketch(source, line)],
                    EntityRole.SideWall,
                    0,
                    new GeoHint(GeometryKind.Plane, 1.0, Vec3d.Zero, Vec3d.UnitZ, 4))),
                MultiplicityPolicy.ExactlyOne);

            Edit($"Add {name}", t => t.AddFeature(
                Feature.Create(id, name, "Consumer") with
                {
                    Inputs = [source],
                    References = [reference],
                }));

            return id;
        }

        public void AddSketchLine(string line) => ReplaceSketch(_lines.Add(line));

        public void RemoveSketchLine(string line) => ReplaceSketch(_lines.Remove(line));

        /// <summary>The wall a given sketch line produced in the latest rebuild.</summary>
        public SubEntity WallFor(FeatureId producer, string line)
            => _evaluator.WallFor(producer, line);

        /// <summary>A feature that divides what it is given: each face in two, and the body.</summary>
        public FeatureId AddSplitter(string name, FeatureId source)
        {
            FeatureId id = FeatureId.New();

            Edit($"Add {name}", t => t.AddFeature(
                Feature.Create(id, name, "Splitter") with { Inputs = [source] }));

            return id;
        }

        public SubEntity WhatItUsed(FeatureId id)
            => _used.TryGetValue(id, out ImmutableArray<SubEntity> used) && used.Length == 1
                ? used[0]
                : SubEntity.None;

        public int UsedCount(FeatureId id)
            => _used.TryGetValue(id, out ImmutableArray<SubEntity> used) ? used.Length : 0;

        public OperationRole RoleOf(FeatureId producer, SubEntity entity)
            => _evaluator.RoleOf(producer, entity);

        public void Dispose()
        {
            Engine.Dispose();
            _dispatcher.Dispose();
        }

        /// <summary>Edits the sketch, which is a document edit like any other.</summary>
        /// <remarks>
        /// The line list is a <see cref="Feature.Settings"/> entry rather than something held beside
        /// the document, so changing it dirties the feature and invalidates its cache entry through
        /// the ordinary machinery. A sketch edit smuggled past the document would rebuild nothing.
        /// </remarks>
        private void ReplaceSketch(ImmutableArray<string> lines)
        {
            _lines = lines;
            _evaluator.SketchLines = lines;

            Edit(
                "Edit sketch",
                t => t.ReplaceFeature(Session.Current.FindFeature(_sketchOwner)! with
                {
                    Settings = Lines(lines),
                }));
        }

        private static ImmutableDictionary<string, FeatureValue> Lines(ImmutableArray<string> lines)
            => ImmutableDictionary<string, FeatureValue>.Empty
                .Add("Lines", new TextValue(string.Join(',', lines)));

        private static EntityReference Reference(
            FeatureId source, EntityRole role, MultiplicityPolicy policy)
            => new(
                PersistentName.Of(new NameSegment(
                    source,
                    ProvenanceKind.Generated,
                    [],
                    role,
                    0,
                    new GeoHint(GeometryKind.Plane, 1.0, Vec3d.Zero, Vec3d.UnitZ, 4))),
                policy);

        private void Edit(string name, Action<IDocumentTransaction> change)
        {
            using IDocumentTransaction transaction = Session.BeginTransaction(name);
            change(transaction);
            transaction.Commit();
        }
    }

    /// <summary>
    /// Stands in for the feature types P5 will bring, and behaves like a kernel while doing it.
    /// </summary>
    /// <remarks>
    /// Issues fresh entity tags on every rebuild. That is the property that matters: a naming layer
    /// which only worked while tags stayed stable would pass everything here and fail on the first
    /// real edit, because a kernel reissues them whenever the geometry changes.
    /// </remarks>
    private sealed class ModelEvaluator : IFeatureEvaluator
    {
        private readonly Dictionary<FeatureId, Dictionary<SubEntity, OperationRole>> _roles = [];
        private readonly Dictionary<SubEntity, GeoHint> _hints = [];
        private readonly Dictionary<(FeatureId, string), SubEntity> _profiles = [];
        private readonly Dictionary<(FeatureId, string), SubEntity> _walls = [];
        private ulong _next = 1;

        public Action<FeatureId, ImmutableArray<SubEntity>>? Used { get; set; }

        /// <summary>Gets or sets the profile lines the base feature is built from.</summary>
        public ImmutableArray<string> SketchLines { get; set; } = ["L1"];

        /// <summary>What a sketch entity produced, as things stand after the latest rebuild.</summary>
        /// <remarks>
        /// This is the delegate a real host would satisfy from its sketch layer. A line that is no
        /// longer in the sketch has no edge, and saying so as <see cref="SubEntity.None"/> is what
        /// lets the resolver report a reference to it as missing rather than as unanswerable.
        /// </remarks>
        public SubEntity ProfileFor(NameSource.Sketch sketch)
            => _profiles.TryGetValue((sketch.Owner, sketch.EntityId), out SubEntity edge)
                ? edge
                : SubEntity.None;

        /// <summary>The wall a given line produced, for a test to assert against.</summary>
        public SubEntity WallFor(FeatureId producer, string line)
            => _walls.TryGetValue((producer, line), out SubEntity wall) ? wall : SubEntity.None;

        public GeoHint? Measure(SubEntity entity)
            => _hints.TryGetValue(entity, out GeoHint? hint) ? hint : null;

        public OperationRole RoleOf(FeatureId producer, SubEntity entity)
            => _roles.TryGetValue(producer, out Dictionary<SubEntity, OperationRole>? map)
                && map.TryGetValue(entity, out OperationRole role)
                    ? role
                    : OperationRole.Unknown;

        public FeatureOutput Evaluate(
            FeatureEvaluation evaluation, CancellationToken cancellationToken)
        {
            FeatureId id = evaluation.Feature.Id;

            Used?.Invoke(id, [.. evaluation.Resolved.SelectMany(r => r.Entities)]);

            return evaluation.Feature.FeatureType switch
            {
                "Base" => Build(id),
                "Modifier" => PassThrough(id, evaluation, split: false),
                "Splitter" => PassThrough(id, evaluation, split: true),
                _ => Consume(id, evaluation),
            };
        }

        /// <summary>A prism: one side wall per profile line, and a cap.</summary>
        /// <remarks>
        /// Every tag is freshly issued, and the per-line edges are re-recorded from scratch, so a
        /// line dropped from the sketch leaves nothing behind for a stale lookup to find.
        /// </remarks>
        private FeatureOutput Build(FeatureId id)
        {
            KernelShape shape = new(_next++);
            HistoryMapBuilder history = new();
            Dictionary<SubEntity, OperationRole> roles = [];

            foreach ((FeatureId owner, string line) in _profiles.Keys.Where(k => k.Item1 == id).ToArray())
            {
                _profiles.Remove((owner, line));
                _walls.Remove((owner, line));
            }

            SubEntity first = SubEntity.None;

            for (int i = 0; i < SketchLines.Length; i++)
            {
                string line = SketchLines[i];
                SubEntity profile = Fresh(shape, SubEntityKind.Edge);

                // The profile edge is the thing a name bottoms out on, so it has to be in the map.
                history.AddNew(profile, OperationRole.Retained);
                roles[profile] = OperationRole.Retained;
                _profiles[(id, line)] = profile;

                // Each wall sits somewhere different, so that if the geometric tier were ever
                // consulted about one it would have something to tell them apart by -- and so that
                // a test asserting the right wall is asserting more than "a wall".
                _walls[(id, line)] = Wall(shape, history, roles, profile, new Vec3d(i, 0, 0));

                if (i == 0)
                {
                    first = profile;
                }
            }

            Cap(shape, history, roles, first, OperationRole.EndCap);

            _roles[id] = roles;

            return new FeatureOutput([NewBody(id, shape)], [], history.Build());
        }

        /// <summary>Carries every entity of its input forward, optionally dividing each in two.</summary>
        /// <param name="id">The feature doing it.</param>
        /// <param name="evaluation">What it was given.</param>
        /// <param name="split">
        /// Whether to divide. A split is reported as two successors of one input, which is exactly
        /// what a kernel reports when a boolean cuts a face -- and what makes history ambiguous.
        /// </param>
        private FeatureOutput PassThrough(FeatureId id, FeatureEvaluation evaluation, bool split)
        {
            KernelShape shape = new(_next++);
            HistoryMapBuilder history = new();
            Dictionary<SubEntity, OperationRole> roles = [];

            foreach (FeatureId source in evaluation.Feature.Inputs)
            {
                if (!_roles.TryGetValue(source, out Dictionary<SubEntity, OperationRole>? map))
                {
                    continue;
                }

                foreach ((SubEntity entity, OperationRole role) in map)
                {
                    GeoHint was = _hints.TryGetValue(entity, out GeoHint? hint)
                        ? hint
                        : new GeoHint(GeometryKind.Plane, 1.0, Vec3d.Zero, Vec3d.UnitZ, 4);

                    if (split && entity.Kind == SubEntityKind.Face)
                    {
                        // Symmetrically, so that neither half resembles the original more than the
                        // other and no amount of geometric evidence can choose between them. That
                        // is the case a declared policy has to settle.
                        Half(shape, history, roles, entity, role, was, OperationRole.SplitPositive, 0.25);
                        Half(shape, history, roles, entity, role, was, OperationRole.SplitNegative, -0.25);

                        continue;
                    }

                    SubEntity moved = Fresh(shape, entity.Kind);

                    history.AddModified(entity, moved, OperationRole.Retained);
                    roles[moved] = role;
                    _hints[moved] = was;
                }
            }

            _roles[id] = roles;

            ImmutableArray<Body> bodies = split
                ? [NewBody(id, shape), NewBody(id, new KernelShape(_next++))]
                : [NewBody(id, shape)];

            return new FeatureOutput(bodies, [], history.Build());
        }

        /// <summary>One piece of a divided face.</summary>
        private void Half(
            KernelShape shape,
            HistoryMapBuilder history,
            Dictionary<SubEntity, OperationRole> roles,
            SubEntity original,
            OperationRole originalRole,
            GeoHint was,
            OperationRole role,
            double offset)
        {
            SubEntity piece = Fresh(shape, SubEntityKind.Face);

            history.AddModified(original, piece, role);

            // The piece keeps the part its parent played. A half of a side wall is still a side
            // wall, and a reference that asked for one has to be able to find it.
            roles[piece] = originalRole;

            _hints[piece] = was with
            {
                Measure = was.Measure / 2,
                Centroid = was.Centroid + new Vec3d(offset, 0, 0),
            };
        }

        /// <summary>A feature that uses what it was pointed at and produces one body.</summary>
        private FeatureOutput Consume(FeatureId id, FeatureEvaluation evaluation)
        {
            KernelShape shape = new(_next++);

            _roles[id] = [];

            return new FeatureOutput(
                [NewBody(id, shape)], [], HistoryMap.Empty);
        }

        private SubEntity Wall(
            KernelShape shape,
            HistoryMapBuilder history,
            Dictionary<SubEntity, OperationRole> roles,
            SubEntity profile,
            Vec3d at)
        {
            SubEntity wall = Fresh(shape, SubEntityKind.Face);

            history.AddGenerated(profile, wall, OperationRole.SideWall);
            roles[wall] = OperationRole.SideWall;
            _hints[wall] = new GeoHint(GeometryKind.Plane, 1.0, at, Vec3d.UnitZ, 4);

            return wall;
        }

        private void Cap(
            KernelShape shape,
            HistoryMapBuilder history,
            Dictionary<SubEntity, OperationRole> roles,
            SubEntity profile,
            OperationRole role)
        {
            SubEntity cap = Fresh(shape, SubEntityKind.Face);

            history.AddGenerated(profile, cap, role);
            roles[cap] = role;
            _hints[cap] = new GeoHint(GeometryKind.Plane, 0.5, new Vec3d(0, 0, 1), Vec3d.UnitZ, 4);
        }

        private static Body NewBody(FeatureId owner, KernelShape shape)
            => new(BodyId.New(), owner, BodyKind.Solid, shape);

        private SubEntity Fresh(KernelShape shape, SubEntityKind kind)
            => new(shape, _next++, kind);
    }
}
