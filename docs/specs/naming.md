# Spec: topological naming

**Status:** built, wired into the rebuild engine, and exercised end to end against `FakeKernel`.
The regression corpus is **incomplete** — seven of §5.3's ten mandatory categories — and P3-T13 stays
open because of it. Nothing here has ever run against a real geometry kernel, because
`OPENMCAD_WITH_OCCT` is still off.

**Tasks:** P3-T08 through P3-T13. P3-T13 is the partial one.

ADR-0005 says it plainly: **this is the highest-risk subsystem in the product.** It is the clearest
dividing line between production MCAD and hobby MCAD, and the reason FreeCAD models break where
SolidWorks models do not. §5.3 is the design; this expands it into what was actually built, which
invariants everything leans on, and which failures are deliberate.

Read §14 of this file before changing anything here.

---

## 1. What is here

All of it in `OpenMCAD.Core/Naming`, 2,165 lines across eleven files.

| Piece | File | Task |
|---|---|---|
| `PersistentName`, `NameSegment`, `EntityRole`, `NameSource`, `GeoHint`, `GeometryKind`, `ProvenanceKind` | `PersistentName.cs`, `NameSegment.cs` | P3-T08 |
| Text form — versioned, length-prefixed | `PersistentNameFormat.cs` | P3-T08 |
| Tier 1, history replay | `HistoryNameResolver.cs` | P3-T09 |
| Tier 2, geometric matching | `GeometricNameResolver.cs`, `GeometricMatchSettings.cs` | P3-T10 |
| The three-tier orchestration | `NameResolver.cs` | P3-T11 |
| `NameResolution`, `NameResolutionOutcome`, `ScoredEntity` | `NameResolution.cs` | P3-T09–T11 |
| Repair contract for the Phase 6 UI | `ReferenceRepair.cs` | P3-T11 |
| `EntityReference`, `MultiplicityPolicy`, `ResolvedReference` | `EntityReference.cs` | P3-T12 |
| History collected in evaluation order | `RebuildHistory.cs` | P3-T09 |
| Unit tests and the corpus | `tests/unit/OpenMCAD.Core.Tests/` — `PersistentNameTests`, `HistoryNameResolverTests`, `GeometricNameResolverTests`, `NameResolverTests`, `NamingCorpusTests` | P3-T08–T13 |

OCCT's `BRepTools_History` / `BRepAlgoAPI` history output is the **raw input** to this layer.
OCCT's `TNaming`/OCAF is **not used** — ADR-0005.

---

## 2. The name

```csharp
PersistentName  = ordered path of NameSegment
NameSegment     = (FeatureId Feature, ProvenanceKind Provenance,
                   ImmutableArray<NameSource> Sources, EntityRole Role,
                   int Ordinal = 0, GeoHint? Hint = null)
NameSource      = NameSource.Entity(PersistentName)          // recursive
                | NameSource.Sketch(FeatureId Owner, string EntityId)
```

Entities are named by **generative provenance, never by index**. Kernel indices change on every
rebuild; that is the whole problem.

Four shape decisions that are easy to miss in §5.3 and that change what the code has to be:

- **`Sources` is a list, not a field.** §5.3's own worked example has a segment with *two* sources:
  a blend face exists because two faces meet, and naming it after either alone would not
  distinguish it from the blend on the next edge along.
- **`Role` is an open string** (`EntityRole`, a `readonly record struct` over `string`), not an
  enum. §5.3's list ends in an ellipsis; every later phase brings roles, and a plugin can bring
  ones this build has never heard of. Named statics — `SideWall`, `StartCap`, `BlendFace`,
  `Unknown` and the rest — give the common ones one spelling without closing the set.
- **`Ordinal` counts from one, and zero means "there was only one of these when this was
  written."** So a zero ordinal facing several candidates is a *split*, not a reference to the
  first of them. This distinction is load-bearing at tier 1.
- **`Hint` is optional.** A name written before a hint field existed is still a valid name; see §6
  on why missing evidence is not evidence against.

`GeoHint` is `(GeometryKind Kind, double Measure, Vec3d Centroid, Vec3d Direction, int
AdjacencyDegree)`, and `Centroid` is **in feature-local coordinates** — that is what lets a part be
moved without its faces stopping being recognisable. `MeasureBucket` is **logarithmic**, because
what matters is proportion: a face growing from one square millimetre to two is a large change and
one growing from a hundred to a hundred and one is not, and a linear bucket treats those the same.

`GeometryKind` is `Unknown, Plane, Cylinder, Cone, Sphere, Torus, FreeformSurface, Line, Circle,
Ellipse, FreeformCurve, Point`. `ProvenanceKind` is `Generated, Modified, Intersection, New,
Imported`, exactly §5.3's list.

**`PersistentName` equality is written by hand.** A record compares an `ImmutableArray` by its
underlying reference, so two names built identically would be unequal — and that would not fail
loudly. Names would simply never match, every reference would fall through to the geometric tier,
and the symptom would be a subsystem that appears to work while quietly running on its fallback.
Sabotage-verified.

---

## 3. The text form

`PersistentNameFormat`, version 1, and it **refuses a future version rather than misreading one**.

**Length-prefixed, not delimited.** Any character chosen as a delimiter is one that eventually
appears in a plugin's role name. There are round-trip tests with roles containing semicolons,
colons, spaces and emoji.

The human-readable renderer reproduces §5.3's worked example exactly:

```
Fillet2 / BlendFace / from( Extrude1/SideWall/from(Sketch1.L3) ∧ Extrude1/EndCap )
```

and **degrades to ids when no document is to hand**, because a diagnostic gets rendered into logs
and crash reports where the document is not available.

---

## 4. Resolution: three tiers, and what must not fall through

`NameResolver` runs history, then geometry, then refuses.

```
        ┌─ Resolved ────────────────────────────────► done
        │
tier 1  ├─ Ambiguous ──┐                       ┌─ above threshold + margin ─► resolved
history │              ├─► tier 2, geometric ──┤
replay  ├─ NotFound ───┘                       └─ otherwise ────────────────► tier 3
        │
        ├─ Deleted ─────────────────────────────────────────────────────────► tier 3
        │
        └─ Unsupported ─────────────────────────────────────────────────────► tier 3
```

**The decision worth recording is which failures do *not* go on to tier 2.**

`Deleted` is a settled question. History saying "it is gone" is a definite answer, and the face that
most resembles a deleted face is *a different face*. Adopting it is exactly the silent corruption
§5.3 forbids — and it would look entirely reasonable to everyone involved. Sabotage-verified by
letting `Deleted` fall through and watching the search confidently find the wrong thing.

`Ambiguous` and `NotFound` do go on, because there the question is open rather than answered.

`Unsupported` exists so that a sketch source with no sketch layer to ask does not look like a
missing entity. A host that cannot answer has not discovered a broken model.

**The host supplies that lookup through `RebuildEngine`'s `sketchEntities` parameter.** The seam had
always existed on `NameResolver` and `HistoryNameResolver`, but the engine never passed one, so every
sketch-sourced name resolved `Unsupported` in a real rebuild regardless of the document. That was the
actual blocker on the sketch-topology corpus category — not Phase 4, which the plan had recorded.

---

## 5. Tier 1 — history replay (authoritative)

`HistoryNameResolver` walks the rebuild's `HistoryMap` chains. Exact, and it handles the
overwhelming majority of edits.

**It is two walks, not one.** Resolving the name finds the entity as it stood *when its feature
finished*; a second walk then carries that entity forward through every operation that has run
since. The second walk is what makes a reference survive **a feature being inserted above it** —
which is the commonest edit there is, and which §5.3 does not list as a corpus category (the corpus
covers it anyway; see §11).

**A segment's sources intersect; they do not union.** §5.3's fillet blend is named after the two
faces whose shared edge it replaces, because either face *alone* also produced the blends on every
other edge it touches. Union would return all of them.

**Ambiguity is reported, never resolved here.** A split face comes back as a shortlist for tier 2.

P3-T09 had to close a real gap in P3-T04 before any of this could work: `FeatureOutput` carried no
`HistoryMap`, so every evaluator was discarding the one thing ADR-0002 makes non-negotiable, and
naming had nothing to work with. `RebuildHistory` now collects the maps in evaluation order onto
`RebuildResult`.

---

## 6. Tier 2 — constrained geometric matching (fallback)

`GeometricNameResolver`, tuned by `GeometricMatchSettings`:

| Term | Weight |
|---|---|
| Centroid distance | 0.45 |
| Direction agreement | 0.25 |
| Measure bucket | 0.20 |
| Adjacency degree | 0.10 |
| **Confidence threshold** | **0.60** |
| **Margin over runner-up** | **0.15** |

**These are set to refuse in doubtful cases, not to maximise how many references resolve
automatically.** Most of the tests here check that it *refuses*, not that it succeeds. This is the
tier most able to be confidently wrong.

Five things about the scoring are deliberate:

- **Surface kind is a gate, not a term.** As one score among several, a strong centroid match could
  outvote it — and a plane is never the face a cylinder became.
- **Distance is measured against the entity's own size.** CAD spans watch parts to airframes; any
  absolute tolerance is wrong at one end. The same proportional displacement scores identically at
  1e-6 and at 1e6.
- **The two thresholds do different jobs and both are needed.** Confidence rejects a poor match;
  the margin rejects a good one that is not *distinctive*. Both are sabotage-verified.
- **Missing evidence is left out of the score, not counted against.** Otherwise every reference
  written before a hint field existed would drop below the threshold at once. The score is
  normalised over the weights actually present.
- **A reversed normal costs a quarter of the score, and is not disqualifying.** A test written the
  other way was wrong: a boolean subtract routinely returns the same face with its orientation
  flipped. A quarter is enough to lose to the right face when it is present, not enough to fail
  when it is the only candidate.

**Every candidate's score is kept and reported even when nothing is accepted** (`ScoredEntity`,
`NameResolution.Ranking`). Tier 3 has to tell the user something actionable, and "the closest match
scored 0.58 and the next scored 0.55" says the answer was nearly there and nearly ambiguous —
which points at what to look for. "Could not resolve" does not.

---

## 7. Tier 3 — fail loudly

§5.3: **no silent wrong answer, ever.** A wrong-but-plausible resolution silently corrupts
downstream design intent, and is worse than an error.

`ReferenceRepair` is the contract the Phase 6 repair UI binds to. It is produced **now**, long
before that UI exists, because the information only exists at the moment resolution fails: which
candidates were weighed, and how closely each fitted, cannot be recovered afterwards.

Its wording is specified by §5.3's own example — a verb, the thing, and the feature — and the thing
is called *face*, *edge* or *vertex* from the recorded `GeometryKind`, because "reselect the missing
edge for Fillet2" is actionable and "reselect the missing entity" is not.

Two separations that look like duplication and are not:

- **`FeatureState.UnresolvedReference` is kept apart from `MissingInput`.** They break at different
  grain and are repaired differently: one is a whole feature that is gone, the other a particular
  face that cannot be identified.
- **`RebuildReport.Repairs` is separate from `Errors`**, so a feature whose operation simply failed
  is not offered a reselect button for a reference it does not have.

---

## 8. Multiplicity — and the insight that ties it together

```csharp
EntityReference(PersistentName Name, MultiplicityPolicy Multiplicity = ExactlyOne)
```

**The policy decides whether tier 2 is consulted at all.** This is the thing to keep hold of. Tier 2
exists to arbitrate an ambiguity — and for two of the three policies, a split is not an ambiguity:

| Policy | On a split | Reaches tier 2? |
|---|---|---|
| `ExactlyOne` (default) | ambiguous — one piece must be identified | **yes** |
| `AllDescendants` | every piece is wanted | no |
| `LargestDescendant` | the tie-break is stated outright | no |

Under `AllDescendants` every piece is the answer. Under `LargestDescendant` the rule is declared, so
a resemblance argument could only *disagree* with it. Only `ExactlyOne` reaches tier 2, where it
resolves when one piece clearly matches and stops to ask when none does.

**`ExactlyOne` is the default** because a feature that has not thought about splitting will be wrong
when one happens. §5.3 is explicit that this is where most naming bugs live, and that the policy
must be explicit at declaration time rather than inferred.

**`LargestDescendant` refuses on a symmetric split** rather than taking whichever piece the kernel
reported first — that would resolve differently between runs while looking decisive.

---

## 9. How the rebuild engine uses it

Features carry `EntityReference`s alongside their coarse `Inputs`, and `FeatureGraph` reads **both**,
so a feature declaring only references still gets its graph edges. A reference into a feature's own
output is excluded, or it would be a self-cycle that is an artefact of how the name is written.

The engine resolves each feature's references **before** evaluating it, hands the resolved entities
to the evaluator, and marks the feature `UnresolvedReference` with a `ReferenceRepair` when one
breaks. (Verified by disabling that wiring and watching five corpus scenarios fail.)

**References are folded into the geometry cache key — encoding version 2.** A repaired reference
hitting a stale cache entry would return the geometry from *before* the repair, which is the one
case where a stale answer is guaranteed wrong: the user has just said so.

One determinism hazard fixed here, from P3-T08: `ReferencedFeatures` returned a `HashSet`, which
promises no order, and it feeds the graph's tie-break. ADR-0011 does not allow two runs to order a
rebuild differently.

---

## 10. Invariants worth not breaking

Each of these has cost real debugging, or is guarded by a sabotage test that will fail if you
"simplify" it.

1. **`PersistentName` equality stays hand-written.** Record-generated equality compares
   `ImmutableArray` by reference; the failure is silent and looks like a working system.
2. **`Deleted` never falls through to geometric matching.** The face most resembling a deleted face
   is a different face.
3. **Sources intersect. They do not union.**
4. **Ordinal 0 means "there was only one", not "the first one."**
5. **Surface kind gates; it does not score.**
6. **Distances are relative to entity size.** No absolute tolerance is correct across the range CAD
   spans.
7. **Missing hint evidence is excluded from the score, never counted against.**
8. **Both thresholds stay.** Confidence and margin reject different things.
9. **Losing candidates' scores are kept.** They are the whole content of a repair prompt.
10. **`ExactlyOne` stays the default.** Silence about splitting is not consent to guess.
11. **Nothing here is ordered by an unordered collection.** ADR-0011.
12. **References stay in the cache key.**
13. **A resolution failure is data.** It marks a feature; it does not throw across the rebuild.

---

## 11. The corpus — and why P3-T13 is still open

§5.3 mandates ten categories. **Seven are covered; three are blocked**, and the honest accounting of
that is the durable part.

| Category | State |
|---|---|
| Dimension change | covered |
| Feature reorder | covered |
| Feature suppression | covered |
| Feature deletion with dependents | covered |
| Face split by a later feature | covered |
| Body split | covered |
| *Feature inserted above* (not in §5.3's list) | covered — the commonest edit there is |
| Sketch topology change | covered |
| Pattern instance count | blocked on Phase 5 feature types |
| Mirror | blocked on Phase 5 feature types |
| Imported geometry | blocked on Phase 8 |

Everything covered runs **end to end** — a real `DocumentSession`, `RebuildEngine`, dispatcher,
`HistoryMap`s and all three resolution tiers — not against stubs.

`NamingCorpusTests.EveryMandatoryCategoryIsAccountedFor` is the piece that matters most. Each of the
ten is either covered or explicitly blocked on a **named** phase, never silently absent. That is
what makes §5.3's "every new feature type added in any later phase must add cases here" enforceable
rather than aspirational.

**The corpus is in the wrong place.** §5.3 asks for `tests/regression/naming-corpus` with scenario
fixtures; it currently lives as code in `tests/unit/OpenMCAD.Core.Tests/NamingCorpusTests.cs`. That
move is part of closing P3-T13.

---

## 11a. Writing a name — `NameMinter`

Resolution is only half of it. Something has to turn a picked `SubEntity` into a name in the first
place, and until P3-T08..P3-T12's minter that direction did not exist: the only ways to construct a
`PersistentName` were `PersistentName.Of` by hand and the text form of §3. A selection could
therefore not survive a rebuild, because a kernel tag is a handle into one rebuild and means
nothing in the next.

`NameMinter` walks the same `RebuildHistory` the resolver walks, and inverts it:

| Field | Where it comes from |
|---|---|
| `Feature` | The **last** feature whose outputs include the entity, searching back from the consumer |
| `Provenance` | `Generated` if the source generated it, `Modified` if it altered it, `New` if there is no source |
| `Sources` | The source entity, named by the same procedure — recursion that terminates because a source is always an output of an earlier feature |
| `Role` | The operation's own role for that output |
| `Ordinal` | Its place among the candidates *its own name will offer*, counting from one; `0` when unique |
| `Hint` | Nothing. See below. |

Two of those rows are easy to get wrong in ways nothing complains about.

**The ordinal must be counted over the candidate set that will resolve it.** §5 narrows by source
before it applies role and ordinal when a segment has sources, and starts from every output when it
does not. An ordinal counted over the wider set and then read against the narrower one selects the
wrong sibling — and both sets are internally consistent, so the mistake is silent. The minter
therefore builds the same candidate set the resolver will and counts within it.

**Name an entity where it was last left, not where it began.** An entity can be an output of
several features in turn. Naming it at the first is not merely less precise: where a later feature
split it and one half kept its identity — which is what a kernel usually does — a name anchored
before the split resolves to one face and is then carried into a feature that made two, which is
ambiguous by §8 and correctly so. Anchored at the split, the halves are siblings the role and
ordinal separate.

**A minted name has tiers 1 and 3 but not tier 2.** `GeoHint` describes geometry, and the minter
holds a rebuild's history rather than the shapes it produced, so it leaves the hint empty. Names
minted this way survive by replay alone; §6's geometric fallback has nothing to match on. Filling
the hint in belongs to the picking code, which is holding the geometry at the moment the user
clicks.

A selection crosses a rebuild through this pair (`SelectionAcrossRebuild`, in
`OpenMCAD.Interaction`): names are minted before the rebuild and resolved after. `SelectionSet`
goes on holding kernel entities between those points, because it is read every frame to decide
what is highlighted and a name would have to be resolved to answer that.

A minted name is `null` rather than approximate when the history does not account for the entity.
A name that cannot be resolved is worse than no name: it would be written into a document and fail
later, at a distance from whatever caused it.

## 12. Not yet done

| Gap | Why it is open |
|---|---|
| A geometric hint on minted names | The minter has no geometry; §6's tier cannot help a name it wrote. Wants the picking code — P6-T06. |
| Selecting every candidate when a name became ambiguous | `SelectionAcrossRebuild` drops it, per tier 3. For a selection specifically, being wrong costs a click rather than a broken feature, so offering both halves of a split face is defensible; wants the UI that will surface it. |
| Pattern instance count, mirror | Need Phase 5 feature types. |
| Imported geometry | Needs Phase 8. |
| Moving the corpus to `tests/regression/naming-corpus` as fixtures | §5.3's stated shape; §11. |
| Running the corpus against `OcctKernel` nightly | Needs `OPENMCAD_WITH_OCCT` on — a decision in `docs/notes/open-decisions.md`. Until then nothing here has met a real kernel's history output, which is what this layer was designed around. |
| P3's third exit criterion | Unmet until the four categories close. Per the P3-T13 note, that is Phase 7, not before. |

The last row is worth stating plainly: **every claim in this document is verified against
`FakeKernel` only.** `FakeKernel` produces history maps this layer was written to consume, which
means the tests confirm the design is self-consistent — not that it survives contact with
`BRepTools_History`. ADR-0005 rates this the highest-risk subsystem in the product, and that risk is
not retired.
