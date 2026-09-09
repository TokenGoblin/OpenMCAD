# Note: decisions the project is waiting on

**Date:** 2026-08-28 · **Task:** none — a standing list · **Status:** open

Things that block real work and that nobody has decided yet, plus deferred work that is written
down nowhere else. Each entry says what is blocked, what the options are, and what a
recommendation would be, so that picking it up does not mean rediscovering the question.

Delete an entry when it is decided, and put the reasoning in the ADR or the plan entry it belongs
to. This file is a queue, not a record.

---

## 1. How planegcs gets into the tree

**Blocks:** P4-T01, and therefore the rest of P4-T02, and the Phase 4 exit criterion about a
200-entity sketch dragging in under 16 ms — `FakeSolver` is not going to meet that and is not
meant to.

planegcs is eleven files, about 455 KB, at `src/Mod/Sketcher/App/planegcs/` in FreeCAD. It is
LGPL-2.1-or-later, SPDX-tagged per file. It is not distributed on its own.

Its coupling to FreeCAD is four headers, all of which are stubs or one-liners to replace:
`SketcherGlobal.h` (a DLL-export macro), `FCConfig.h`, `Base/Console.h` (logging, used in
`GCS.cpp` only) and `boost_graph_adjacency_list.hpp` (a warning-suppression wrapper). Everything
else is the standard library, Eigen and Boost.

| Option | For | Against |
|---|---|---|
| **Vendor a pinned copy** under `native/third_party/planegcs/` | Hermetic and offline; the exact source is pinned by construction, which ADR-0011 wants; shipping the source satisfies LGPL §4's relinking obligation outright | ~455 KB of someone else's code in the history; upstream fixes have to be tracked by hand |
| Fetch at build time (`FetchContent` or a submodule) | Repository stays ours | Clones a repository well over a gigabyte to get 455 KB, and *still* needs the four stub headers vendored, so the patch has to live here either way |
| Defer | Nothing to decide today | The phase cannot be finished, and the interface stays unproven against the solver it was designed for |

**Recommendation: vendor.** Fetching buys almost nothing when the patch has to be vendored
regardless. Keep it in its own directory with its own `COPYING.LIB`, an `UPSTREAM.md` recording
the commit and every patch applied, a `linguist-vendored` attribute, and `OPENMCAD_WITH_PLANEGCS`
off by default — the same shape OCCT already has.

`THIRD-PARTY-NOTICES.md` already commits to the separately-replaceable-DLL structure this needs.

**One thing to look at while doing it:** `GCS.cpp` includes `<future>`. planegcs has an internal
parallel path, and ADR-0011 makes reproducibility a hard requirement. `native/vcpkg.json` already
made exactly this call once, deliberately excluding TBB from OCCT because "results can vary with
scheduling". Check whether that path is reachable and pin it off if so.

## 2. Boost.Graph as a native dependency

**Blocks:** the same thing, and it is written down nowhere else at all.

planegcs needs Boost.Graph — `connected_components`, for its subsystem decomposition — plus
`boost/math/constants`. Both are header-only. `native/vcpkg.json` does not mention Boost, and this
repository asks for a deliberate decision before a dependency is added rather than one appearing
inside an unrelated commit.

Nothing else here wants Boost. It arrives only because planegcs does, so decide it together with
the entry above.

## 3. Turning the geometry kernel on

**Blocks:** everything from Phase 5. Nothing can produce real geometry until it is on.

`OPENMCAD_WITH_OCCT` is `OFF` in `native/CMakeLists.txt`, and the comment beside it still says
"turn on in Phase 1 (P1-T06)" — which is done, so the comment is stale and misleading. Every build
so far, local and CI, has run against `FakeKernel`.

OCCT 8.0.1 is pinned in `native/vcpkg.json` and the spike (`docs/notes/occt-spike.md`) confirmed
the version and flags. What is left is the decision to switch it on and absorb the consequences:
a much longer cold build (the nightly workflow already carries a note putting it around 660
minutes from cold), a real dependency closure for the licence-notices step, and the corpus
starting to run against `OcctKernel` as well as the fake.

---

## Deferred work, recorded so it is not lost

**One device for the render tests — done, but wanting a CI run to confirm.** The assembly now
shares a single device (`TestDevices.Shared`, released by `RenderTestHost` before finalizers are
drained). Device constructions went from thirty-six to eight: the seven that remain belong to the
three classes that are *about* the device lifecycle, and sharing one with them would test nothing.
The `CI` environment variable no longer decides whether validation is attached, so the laptop and
the build machine now run the same thing.

Two things to know before trusting that:

- **This has not run on CI.** The failure it addresses only ever appeared there, so a green local
  run is not evidence — the same mistake `verify-ci-after-pushing` records. If the host still dies
  on the way out of a run, the one-line revert is to make `TestDevices.Software` gate
  `EnableDebugLayer` on `CI` again, exactly as it did before.
- **The debug layer is not installed on this machine** (`D3D12SDKLayers.dll` is absent), so it was
  never attaching locally either, and the old comment claiming a developer got validation was
  wrong. `RenderDeviceInfo.ValidationEnabled` now reports what actually happened rather than what
  was asked for, and the device logs a warning when validation is requested and unavailable. Until
  a machine with the Graphics Tools feature runs this, nobody has observed the debug layer's
  behaviour under the shared-device arrangement at all.

**P3-T13, the naming corpus.** Seven of the ten mandatory §5.3 categories are covered. The other
three — pattern instance count, mirror, imported geometry — need feature types that do not exist
until Phases 5 and 8. It closes in Phase 8, not before, and Phase 3's third exit criterion is unmet
until it does.

Sketch topology change was the seventh, and it is worth recording *why* it sat here so long: the
blocker written down was Phase 4, and that was simply wrong. The seam had always existed on
`HistoryNameResolver`; `RebuildEngine` just never passed it one, so every sketch-sourced name
resolved `Unsupported` in a real rebuild whatever the document said. One optional constructor
parameter was the whole of it. A recorded blocker is a claim like any other and goes stale like
one — `NamingCorpusTests.EveryMandatoryCategoryIsAccountedFor` is what eventually surfaced it, and
nothing plays that role for the entries below.


## Ready to build, currently recorded only inside finished tasks

Both of these live in the note on a task marked `[x]`, which is where a gap goes to be forgotten.
Neither is blocked on anything any more. Sizes are rough and are there to make them comparable
rather than to promise anything.

**Reference plane display (P2-T11) — done.** The blocker was stale: P2-T11 landed at commit 45 and the P2-T10 transparency it said it was waiting for arrived at 53. Built as `ReferencePlanePass` with `DisplayPlane` on the snapshot; see the P2-T11 note in PLAN.md for the decisions.

**Selection does not survive a rebuild, and no task owns finishing it.** P2-T09's note and `SelectionSet.cs` both say a selection surviving a topology change "needs the persistent naming of PLAN.md 5.3", and the source says it "does not exist yet". It does: the whole of P3-T08 to P3-T12 shipped it, and `OpenMCAD.Interaction` already reaches `OpenMCAD.Core` through its existing references, so nothing structural is in the way.

But this is only half a stale blocker, and the other half is the point. The **resolve** direction exists -- `NameResolver.Resolve(PersistentName, FeatureId)` -- while nothing anywhere **mints** a `PersistentName` from a picked `SubEntity`. The only two ways to construct one are `PersistentName.Of` and the text parser; there is no name generator. `RebuildHistory` carries the per-feature `HistoryMap`s such a pass would walk, so the material is there, but the pass is not.

Worth deciding where that belongs. P6-T06 is selection *filters* and box-select, not rebuild survival, so on the current plan nobody is scheduled to do it -- which is how a half-cleared blocker stays invisible: the note that would have flagged it still says the thing it is waiting for does not exist.

**Silhouette edges on curved surfaces (P2-T06).** "Everything but silhouettes." A cylinder currently
shows its end circles and its seam but nothing where its wall turns away, which reads as a drawing
error rather than as a style. Not blocked, but genuinely harder than the above: a silhouette is a
property of the view rather than of the model, so it has to be found per frame, and that needs face
adjacency the display mesh does not currently carry. Sizing it honestly needs a look at what the
tessellation actually produces first.

---

## Decisions smaller than the three above, but still decisions

**P2-T04, LOD — build it or strike it.** Everything else in the tessellation pipeline is done.
P2-T13's measurements say nothing is limited by triangle throughput (2M triangles across 16 bodies
costs 3.36 ms; 2M across ten thousand costs 10.67 ms), so reducing triangle counts buys little and
the next optimisation is batching. LOD becomes interesting when a model exceeds what memory can
hold, which is a different problem from frame time. Striking it and reopening it in Phase 15, where
`P15-T01` already names occurrence-level LOD, may be righter than building it now.

**P4-T11, `Intersect` against a circular edge.** A line crosses a plane at zero or one point; a
circle crosses at zero, one or two. "One external reference produces one sketch entity" is
deliberate, and there is nowhere to put a second point without giving `SketchExternalReference` a
multiplicity policy the way `EntityReference` has one for kernel topology. That is a real design
decision rather than missing code, and it should be made with the assembly work that will exercise
it rather than invented here.

**The subsystem specs are written — and the habit that lost them is not fixed.** All six
`docs/specs/README.md` lists now exist: `kernel-abstraction`, `kernel-shim`, `naming`,
`rendering`, `sketch`, `document-model`, `persistence`. Four of them were written retroactively, in
one go, long after the subsystems they describe — `rendering.md` was due in P2 and `naming.md` in
P3, and the entry that used to sit here only ever noticed the sketch one was missing.

Keeping a spec current is PLAN.md 8.5's definition of done rather than a chore for the end of a
phase, and writing four at once is the evidence that it was not being treated that way. Nothing
enforces it: a phase can close with its spec unwritten and only a reader comparing the directory
against the index would know. Worth deciding whether that check belongs in CI, in the same spirit
as `EveryMandatoryCategoryIsAccountedFor` — a phase's exit criteria could name its spec, and a spec
listed in the index but absent from the directory could simply fail the build.
