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
/// the rebuild and the resolution of names, which is what Phase 3 builds, and they are here. The
/// other four need feature types that do not exist yet: there is nothing to pattern, nothing to
/// mirror, no sketch to change the topology of, and no importer. <see cref="EveryMandatoryCategoryIsAccountedFor"/>
/// is what stops those being quietly forgotten.
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
        ("sketch topology change", "Phase 4 — there is no sketch to add or remove a line from"),
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

        covered.Should().HaveCount(6);
        blocked.Should().HaveCount(4);

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

        public Scenario()
        {
            Session = new DocumentSession();

            Engine = new RebuildEngine(
                Session, _dispatcher, _evaluator, new GeometryCache(), _evaluator.Measure);

            _evaluator.Used = (feature, entities) => _used[feature] = entities;
        }

        public DocumentSession Session { get; }

        public RebuildEngine Engine { get; }

        public RebuildReport Report => Session.Current.Report;

        /// <summary>A feature that produces geometry out of nothing referenceable.</summary>
        public FeatureId AddBase(string name)
        {
            FeatureId id = FeatureId.New();

            Edit($"Add {name}", t => t.AddFeature(
                Feature.Create(id, name, "Base") with
                {
                    Parameters = [new Parameter("Depth", Quantity.Metres(0.025))],
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
        private ulong _next = 1;

        public Action<FeatureId, ImmutableArray<SubEntity>>? Used { get; set; }

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

        /// <summary>A prism: side walls and two caps, all tags freshly issued.</summary>
        private FeatureOutput Build(FeatureId id)
        {
            KernelShape shape = new(_next++);
            HistoryMapBuilder history = new();
            Dictionary<SubEntity, OperationRole> roles = [];

            SubEntity profile = Fresh(shape, SubEntityKind.Edge);

            // The profile is the thing a name bottoms out on, so it has to exist in the map.
            history.AddNew(profile, OperationRole.Retained);
            roles[profile] = OperationRole.Retained;

            Wall(shape, history, roles, profile, Vec3d.Zero);

            Cap(shape, history, roles, profile, OperationRole.EndCap);

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

        private void Wall(
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
