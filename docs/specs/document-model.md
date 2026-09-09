# Spec: the document model and rebuild engine

**Status:** built and tested. Every rebuild so far has run against `FakeKernel`, and no feature type
exists yet to evaluate — `IFeatureEvaluator` is a seam Phase 5 fills, and the corpus supplies a
stand-in that behaves like a kernel.

**Tasks:** P3-T01 through P3-T07, P3-T14 through P3-T17, P3-T21, P3-T22.

This expands PLAN.md §5.4 and §5.5. It is written for someone changing the core without reading
twenty files first: what a document is, what a rebuild does, and which of the decisions here are
load-bearing enough that changing them breaks something distant.

Persistence — the OPC container, the MessagePack schema, migration — is `persistence.md`.
Topological naming is `naming.md`.

---

## 1. What is here

`OpenMCAD.Core`, in three folders.

| Piece | Where | Task |
|---|---|---|
| `Document`, `Feature`, `Body`, `ReferenceGeometry`, `DocumentMetadata` | `Documents/` | P3-T01 |
| Strong ids — `FeatureId`, `BodyId` | `Documents/FeatureId.cs`, `BodyId.cs` | P3-T01 |
| `IDocumentTransaction`, `DocumentTransaction`, `DocumentSession`, `DocumentChange` | `Documents/` | P3-T02 |
| Dependency DAG, cycle tracing, dangling inputs | `Documents/FeatureGraph.cs` | P3-T03 |
| `RebuildEngine`, `IFeatureEvaluator`, `RebuildResult` | `Rebuild/` | P3-T04 |
| Geometry cache and its key | `Rebuild/GeometryCache.cs`, `RebuildKey.cs` | P3-T05 |
| Per-feature state and the report | `Documents/RebuildReport.cs` | P3-T07 |
| `Quantity`, `Dimension`, `Dimensions`, `Unit` | `Documents/` | P3-T01, P3-T14 |
| Parser, checker, evaluator | `Expressions/` | P3-T15 |
| `ParameterGraph`, cycle rejection | `Documents/ParameterGraph.cs` | P3-T16 |
| `UndoHistory` | `Documents/UndoHistory.cs` | P3-T17 |
| Headless commands (`build`, `rebuild`, `inspect`, `save`, `diff`) | `OpenMCAD.Cli/DocumentCommands.cs` | P3-T22 |
| `ReferenceValue`, and the input an `EntityReference` satisfies | `Documents/FeatureValue.cs`, `Naming/EntityReference.cs` | P5-T03 |

---

## 2. The one decision everything else rests on

**`Document` is immutable.** §5.4 asked for internal setters and a transaction-scoped mutation API;
what was built is a value type with `internal With…` methods. The same enforcement — only this
assembly can produce a new version — but strictly stronger, because holding a reference no longer
lets anyone alter it.

Four things fall out of that, and each is somewhere the design would otherwise have needed real
work:

- **Undo is holding an earlier reference** (§8), not replaying inverses.
- **A rebuild reads a document that cannot change underneath it**, so there is no lock between
  editing and rebuilding.
- **Rollback of a transaction is free** — edits go to a private working reference, so abandoning
  one drops a pointer.
- **"Identical after undo" is a question the type can answer**, by full graph comparison
  (`Document.Matches`).

The collections share structure, so an edit copies a spine of pointers rather than the model.

**Neither `FeatureId` nor `BodyId` implements `IComparable`, deliberately.** The values are random,
so any ordering by them is stable within one process and meaningless between two — and ADR-0011
does not allow a rebuild to order differently on the next run. Wherever something must be ordered,
it is ordered by position in the tree or by name.

---

## 3. Transactions (P3-T02)

`DocumentSession` holds which document is current. The document itself is a value, so a rebuild can
read one for as long as it likes while editing continues — it holds something nobody can alter,
rather than a lock on what everyone needs.

- **One transaction at a time, rejected at open rather than at commit.** Two starting from the same
  state have no correct merge, and failing at open still has the stack that explains it.
- **A sequence that throws halfway leaves no trace**, without the transaction knowing how to reverse
  what already succeeded.
- **Commit reports what was touched, recorded as edits happen rather than diffed at commit.** A diff
  would have to guess intent, and a feature removed then re-added looks untouched.
- **Bodies do not seed a rebuild.** A body is the *result* of a rebuild, not a cause of one; seeding
  on it would not terminate.
- **The `Committed` event is raised outside the lock and after the swap**, so a handler can read the
  session and open its own transaction — which the rebuild engine must do to write back what it
  produced. There is a test for that re-entrancy, verified by announcing before releasing the slot
  and watching it fail.

---

## 4. The dependency graph (P3-T03)

Built from **declared feature inputs, never inferred from tree order**. Tree order is a user-facing
sequence; the DAG is the truth. `FeatureGraph.Build` orders with Kahn's algorithm.

**The tie-break is the interesting decision.** A topological order is not unique, so when several
features are ready any of them would be correct — and that freedom is spent on reproducibility.
Ties go to **position in the tree**, which is stable and survives a save; never to the id. Without
that, the same document rebuilds in a different order each run and a cache key, a regression
baseline and a bug report all stop meaning anything.

**Cycles name only the loop.** Kahn reports the leftovers, which are a superset; a depth-first walk
of just those finds a real back edge. Otherwise the two features the user has to fix are buried
under the twenty the loop spoiled. A self-referencing feature is a one-element loop and reports as
one.

**Dangling inputs are reported, not thrown.** Deleting a consumed feature is normal, and refusing to
build the graph would leave the document unopenable with no way to see what to fix. P3-T07 turns
those into per-feature state.

The graph reads **both** `Feature.Inputs` and `Feature.EntityReferences`, so a feature declaring
only references still gets its edges; a reference into a feature's own output is excluded, or it
would be a self-cycle that is an artefact of how the name is written.

**It does not read settings**, which matters now that one of them can point at something. A
`ReferenceValue` names reference geometry by `(Owner, Name)`, and the owner is a real feature
whenever the datum was not one of the standard ones — but nothing derives an edge from it, so a
feature holding one **must also declare that owner in `Inputs`** or the rebuild will not sequence
the two in order. Deriving it here instead was rejected: the graph would then have to know which
settings are pointers, which is the feature schema's business and a layer above this one.

---

## 5. The rebuild (P3-T04, P3-T06, P3-T07)

**`IFeatureEvaluator` is the seam to `OpenMCAD.Modeling`.** Core knows features have inputs, an
order and results, and must not know what an extrude is.

**It is synchronous, on purpose.** Kernel operations are blocking native calls already marshalled
onto the kernel thread (ADR-0004); making this async would either wrap a synchronous call for
nothing, or let an implementation release the kernel thread mid-operation — which is exactly what a
single-threaded actor exists to prevent.

**Publishing is all-or-nothing, in one transaction after the last feature.** A rebuild cancelled
halfway would otherwise leave new geometry for the first three features and old for the rest, which
is not a state the model was ever in.

**Supersession cancels the running rebuild before queueing, not after**, so a dimension drag does
not run fifty rebuilds to compute forty-nine documents nobody will see. `Cancelled` and `Superseded`
are distinct outcomes because they mean different things to the user: one stopped because they
asked, the other because they carried on. (`RebuildOutcome` is `Completed | NothingToDo |
Superseded | Cancelled`.)

**Every exception from a feature is caught** — deliberately every one, since a feature is arbitrary
code and in the general case a plugin's — and contained to what depended on it while independent
branches finish.

### The rollback bar came free, and cost one bug

§5.4 predicted the rollback bar would fall out of the design provided it was not special-cased, and
it did: being behind the bar became one more reason a feature is not evaluated, alongside being
suppressed and depending on something that failed. The engine change is one clause.

What it *did* need was something nothing had required before: **a feature that is not evaluated
gives up its geometry.** Dragging the bar up the tree is how a user looks at a part half-built, and
a rolled-back extrude still showing its solid makes that gesture show nothing. That turned up a bug
in P3-T04 — suppression had the same requirement and was not meeting it, so switching a feature off
left its solid on screen.

The bar's position is **nullable rather than defaulting to the feature count**, or "not rolled back"
would mean whatever the length was when it was last written, and the next feature added would appear
behind the bar. Deleting a feature above the bar moves the bar with it.

### Seven states, not built-or-not (P3-T07)

`FeatureState` is `Ok | Failed | SuppressedByError | Suppressed | RolledBack | Blocked |
UnresolvedReference | MissingInput`, and **the distinction is the whole value**:

| Kind | States | Why apart |
|---|---|---|
| Problems to fix | `Failed`, `MissingInput`, `UnresolvedReference` | These are what the user must act on |
| Consequences | `SuppressedByError`, `Blocked` | Nothing is wrong *here* |
| Asked for | `Suppressed`, `RolledBack` | The user chose this |

A sketch that fails can leave twenty features unbuilt, and presenting them as twenty equal problems
sends the user to whichever is nearest the top of the tree. **Each consequence carries the id of the
feature that actually failed**, carried through rather than restated, so a chain ten deep still
names the one thing to fix.

`Blocked` exists so a feature downstream of a *suppressed* one is not reported as an error — nothing
went wrong, the user asked for that absence. `UnresolvedReference` is kept apart from `MissingInput`
because they break at different grain: one is a whole feature that is gone, the other a particular
face that cannot be identified (see `naming.md` §7).

**The report lives on the `Document`**, not returned from the rebuild. The tree has to keep showing
errors long after the caller has finished with its result, and holding it there means undo restores
the report belonging to the state it restored. Features outside a partial rebuild **keep their
previous diagnostics** — "nothing was said" is not "fine", and dropping them would clear the marks
off still-broken features whenever the user edited elsewhere.

A failed feature **also gives up its geometry**, or the user is shown a solid the current parameters
do not produce.

---

## 6. The geometry cache (P3-T05)

Keyed by `(FeatureId, hash(resolved inputs))`. A hit skips the kernel entirely, which is what makes
undo cheap and rollback scrubbing instant.

**The key is where this task lives or dies.** A cache that evicts badly is slow; a cache whose key
misses something is a program that shows the wrong solid and never mentions it. What the key covers,
and why:

| In the key | Because |
|---|---|
| A version tag | When the encoding changes, old entries **miss rather than are misread** |
| `FeatureId` | A cached `FeatureOutput` holds bodies that name their owner; sharing an entry between two identical features gives one of them the other's bodies. Found by the test suite after I first left it out for pure content addressing |
| Feature type, parameters (resolved values, not expressions) | Two documents typed differently that evaluate the same should hit |
| Entity references | A **repaired** reference must not hit the entry from before the repair — the one case where a stale answer is guaranteed wrong, because the user has just said so |
| **Settings** | What a feature is told that is not a dimension or a selection: direction, count, end condition, merge. Every one changes the geometry |
| Input keys | Chained, making a **Merkle chain** |

**Not in the key:** the display name, so renaming a feature does not discard its geometry.

Three encoding rules that are not decoration:

1. **SHA-256, not `string.GetHashCode`**, which .NET randomises per process. A cache that never hits
   after a restart is not a cache.
2. **Every variable-length part is length-prefixed**, or type `ab` with parameter `c` collides with
   type `a` with parameter `bc`.
3. **Settings are sorted by name, ordinally, and each value carries a kind tag.** An
   `ImmutableDictionary` promises no enumeration order; untagged, a `TextValue` and a `ChoiceValue`
   of the same string encode identically and would share an entry while meaning different things.

`AKeyIsTheSameInAnyProcess` pins the encoding, and its expected value is **computed by a second
implementation in another language** rather than pasted from what this code emits — which would pin
the behaviour without checking it against the description of it. The pinned feature carries one
setting of every kind, so each tag is pinned rather than merely written down.

**Eviction is LRU by count, not by memory** — an entry's cost is dominated by shapes living in the
kernel and is not knowable from this side. Dropping an entry raises `Evicted` so its shapes can be
released: the cache does not own them.

`--no-cache` is the same engine with `NullGeometryCache`, so the exit criterion compares the real
path against itself rather than against a second implementation.

---

## 7. Quantities, units and expressions (P3-T14, P3-T15, P3-T16)

**Storage is always SI base** — metres, radians, kilograms, seconds. Conversion happens only at the
input and display boundary, which is `Unit`'s entire job.

A parameter holds a `Quantity` with a `Dimension` (`Dimensionless | Length | Angle | Area | Volume |
Mass | Density | Time`), not a bare double.

**`Dimensions` answers on dimensions alone, never values.** That is what makes §5.5's "caught before
evaluation" true rather than a manner of speaking: the checker can type an expression tree as it
parses and tell the user while they are still looking at it.

**A closed table rather than exponent arithmetic.** The general form is right for a physics library
and would make every dimension representable while being unable to *name* one in a diagnostic. The
price is that combinations outside the list are refused rather than invented.

**Angle is its own dimension, which strict SI would disagree with.** A radian is dimensionless, so a
strict treatment lets `4 mm + 3 deg` through as a number plus a number — the exact error §5.5 opens
with. Sabotage-verified by making angle dimensionless and watching three tests fail.

### Parse, check, evaluate — three passes

Hand-written recursive descent, because the grammar is smaller than a generator's configuration and
because **the error wording is most of the value**. An expression box is where people make typing
mistakes constantly; every message names the model rather than the parser and carries a position an
editor can underline.

Decisions worth keeping:

- **A bare number is a plain number, not a length.** `Width + 5` is refused, with the fix in the
  message. Adopting document units there would make one formula mean different sizes in different
  documents.
- **`sin` takes an angle**, not any number: `sin(0.5)` reads as half a turn to a person and as
  radians to a calculator.
- **`round`/`floor`/`ceil` take plain numbers only.** Values are stored in metres, so `round(Length)`
  would quietly round a part to the nearest metre. The message gives the idiom
  `round(x / 1mm) * 1mm`.
- **`round` goes away from zero at a half**, not banker's.
- **Comparisons yield 1 and 0**, not a boolean type — which would be a second kind of value running
  through everything for the sake of one function.
- **`if` is a function, not syntax**, evaluating only the branch it needs, so `if(x != 0, y / x, 0)`
  is sensible to write.

### Parameters in the rebuild DAG (P3-T16)

**Commit is where values are brought up to date and loops rejected.** Earlier means recomputing on
every keystroke of a multi-step edit; later means a document existing whose stored values disagree
with its own formulas. The rejection happens **before the transaction is marked finished**, so a
caller holding a cycle still has an open transaction to correct rather than a spent one and a
document they cannot fix.

Ties in evaluation order break **alphabetically** — a document holds parameters in a dictionary,
which has no order to inherit.

**A formula that cannot be evaluated keeps its last known value**, which is why a `Parameter` stores
both; a badly typed one does not stop the document being ordered, or one typo would hide every other
problem.

A sabotage run here found *redundant code* rather than a weak test: features were seeded both by
naming a changed parameter and by their own value moving, and the first is a wasteful superset of
the second — a depth of `min(Thickness, 5mm)` does not move when `Thickness` goes 8 to 9, so its
inputs to the kernel are identical. That path is gone, with a test either way.

---

## 8. Undo (P3-T17)

**A stack of document references, not a log of inverse commands.** This is §2's immutability paying
for itself: to undo an edit against a mutable document you must know how to reverse it, every kind
of edit needs its own inverse, and an inverse that is subtly wrong corrupts the model in a way that
surfaces later. **Restoring a reference cannot be subtly wrong, because it is not a computation** —
and it brings back the bodies, the rebuild report and the rollback bar exactly as they were, so no
scoped recompute is needed at all.

Grouping is inherited rather than invented: §5.4 already makes a transaction the unit of edit, so
**one commit is one undo**.

`Document.Matches` is a deep comparison excluding `Version`, which counts edits rather than
describing the model. Writing it turned up an equality trap that has now bitten three times in this
codebase: **`Feature` holds three `ImmutableArray`s and `DocumentMetadata` a dictionary, all
compared by reference under generated record equality** — so every document would have reported as
different from itself the moment any feature had an input. Both compare structurally now,
sabotage-verified.

---

## 9. Feature schemas (P3-T21)

One declaration drives the property UI, serialization, API surface and scripting (§5.7).

**Where a value lives depends on its kind, and one place knows**: a dimension is a `Parameter` so an
expression can drive it and the parameter graph can see it; a selection is an `EntityReference` so
persistent naming can repair it; everything else is a `Feature.Settings` entry. A caller that had to
know which would be a fifth description of the feature — the thing §5.7 exists to prevent.

**A reference input is satisfied either way, and only one of the two may answer** (P5-T03).
`PropertyKind.Reference` is an input the user answers by picking something that is already in the
document, and there are two kinds of such thing. Reference geometry is a `ReferenceValue` setting:
it carries a name it keeps, `Document.FindReference` finds it in one step, and it cannot split, so
giving it the naming layer's repair, ranking and multiplicity machinery would be carrying all of
that for a lookup that either finds a datum plane called "Front" or does not. Kernel topology stays
an `EntityReference`, because it has no name of its own and because that is where the dependency
graph reads its edges. `FeatureSchema.SatisfiedBy` reports which answered; a file naming **both** is
an error rather than a preference, since whichever were preferred the other would be silently
ignored, and a datum built on the face the user last picked while the file still names a datum plane
is the kind of quietly wrong geometry §5.3 would rather refuse than guess at.

**A reference says which input it satisfies.** Until a feature had more than one selection, position
in `Feature.EntityReferences` was the only link to the property it answered — and position stops
being a link the moment an input is optional or can be answered the other way instead. So
`EntityReference` carries the property's stable name, and `Feature.FindSelection` is how an
evaluator turns "the plane this datum is offset from" into the entity it resolved to this time
round. The field is defaulted to empty and is only written when present: a feature with one
selection has nothing to say, and every reference written before the field existed says nothing
either. A datum plane through three points is the first feature where getting two inputs the wrong
way round produces geometry rather than an error, which is what makes the field worth its cost.

**A new kind of `FeatureValue` has three homes, not one.** The codec must learn to write it, and
`RebuildKey` must learn to hash it — the latter refuses an unknown kind outright, on the grounds
that hashing only a type name would make two different values look identical, so a value added
without a case there fails loudly at the first rebuild rather than silently returning a stale
cached result. That refusal is what caught `ReferenceValue`'s missing case.

A property declares a **stable name**, which files and scripts use and which must never change, and
a **label**, which is shown to a person and can.

**`VisibleWhen` is part of the declaration, not a hint for the UI.** An extrude's draft angle with
draft switched off is not a value the user declined to give — it does not apply. If the property
manager and validation each decided that for themselves they would disagree, so one method answers
it, and it follows the chain: a property behind a property that does not apply does not apply either.

**A schema checks itself when built**, so a duplicate name, an empty choice list, a default of the
wrong kind or a condition on a property that does not exist is caught the first time the feature is
registered rather than the first time a user opens that panel.

**Unknown is a warning, not a refusal** — for a setting (it may be from a newer build, which P3-T20
preserves) and for a whole feature kind (an uninstalled plugin costing one feature is survivable;
costing the whole file is not).

---

## 10. The headless API (P3-T22)

`OpenMCAD.Cli`: `build`, `rebuild`, `inspect`, `save`, `diff`. **Every later phase tests through
this.**

- **`build`'s JSON spec is deliberately not the regression corpus's fixture format**, which
  describes kernel operations. Different layers; one file trying to be both would serve neither.
- **Building the same spec twice produces the same bytes** — feature ids come from the feature's
  name rather than a fresh GUID, and the manifest carries a fixed timestamp. Not tidiness: a
  document built by a later phase's test could otherwise never be compared with a stored one.
- **Units are stated in the spec and converted, never assumed.** The one guess nobody should make
  about a CAD dimension is which unit it is in.
- **Every command works in `DocumentCommands`**, returning an exit code and writing to a
  `TextWriter`, so a test calls it rather than starting a process — and every command can answer in
  JSON, so a test asserts on a field rather than on wording that will be rephrased.
- **Exit codes follow shell convention**: 0 yes, 1 a negative answer (documents differ, a rebuild
  has errors), 2 could-not-be-done. A script treating a missing file the same as a genuine
  difference would report success when the file was never there.
- **`diff` compares documents, not bytes** — two files can differ byte for byte and describe the
  same model — and reports a reorder as a difference, because tree order is what the user arranged.

---

## 11. Invariants worth not breaking

1. **`Document` stays immutable.** Undo, lock-free rebuild and `Matches` all depend on it.
2. **Nothing is ordered by a GUID.** Tree position or name; never an id, never a `HashSet`.
3. **A record holding an `ImmutableArray` or a dictionary needs hand-written equality.** Generated
   equality compares by reference, and the failure is silent.
4. **The cache key covers everything that changes the geometry.** Adding a field to `Feature` means
   adding it to `RebuildKey` *and* bumping the version tag.
5. **A feature that is not evaluated gives up its geometry** — failed, suppressed or rolled back.
6. **Failures are contained and reported, never thrown across the rebuild.**
7. **A consequence names the cause**, carried through rather than restated.
8. **The report lives on the document**, so undo restores it too.
9. **Dimensional errors are caught by the checker, before any value is read.**
10. **Publishing is all-or-nothing.**

---

## 12. Not yet done

| Gap | Why |
|---|---|
| Feature evaluation that reaches a kernel | `DatumFeatureEvaluator` (P5-T03) is the first production `IFeatureEvaluator`, and it deliberately touches no kernel — a datum is naming and arithmetic. Everything that makes a body is still exercised against test evaluators that behave like a kernel, including reissuing entity tags every rebuild. |
| Concurrent preparation of independent branches | §5.4 allows it; the engine executes serially. Nothing has needed it, and kernel calls serialise on the dispatcher regardless (ADR-0004). |
| Preview rebuilds at reduced fidelity | §5.4 names them for interactive drag. Coalescing and cancellation are built; fidelity reduction has nothing to reduce yet. |
| Configurations | Phase 14. |
| Cross-document parameter references | Parsed and dimensioned (`Chassis:Width`); resolution across documents needs assemblies, Phase 9. |
