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

**P3-T13, the naming corpus.** Six of the ten mandatory §5.3 categories are covered. The other
four — sketch topology change, pattern instance count, mirror, imported geometry — need features
that do not exist until Phases 4, 5 and 7. It closes in Phase 7, not before, and Phase 3's third
exit criterion is unmet until it does.

**P2-T04, LOD.** Everything else in the tessellation pipeline is done. The measurements say
nothing is triangle-throughput-bound, so this is speculative optimisation with no evidence behind
it. It may be right to strike it rather than build it.

**`docs/specs/sketch.md` does not exist.** `docs/specs/README.md` lists it as "written in P4" and
P4 is six tasks into being done (T03–T09, T10). Keeping a subsystem spec current is PLAN.md 8.5's
definition of done, not a chore for the end of the phase, and this has been silently skipped every
time so far. Writing it retroactively is a task on its own — it has to cover the entity model, the
constraint model, the solver contract and diagnosis mapping, drag behaviour, inference, snapping,
profile detection, and now the plane reference — and picking a moment to stop and write it before
it grows further is worth doing before Phase 4 closes, not after.
