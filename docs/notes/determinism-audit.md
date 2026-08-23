# Determinism audit

**P1-T12.** ADR-0011 makes determinism a correctness property rather than a nicety: undo, the
geometry cache, and topological naming (ADR-0005) all assume that rebuilding the same document
produces the same entities in the same order. A violation does not announce itself. It surfaces
months later as an intermittent naming failure in an unrelated feature.

This is the register of everything considered a source of nondeterminism, what was done about it,
and — where nothing was done — why not.

## How it is enforced

The corpus runner replays every fixture twice on freshly constructed kernels and diffs a signature
built from topology counts, the role histogram, and a digest of the **ordered** sequence of
`Kind:Role` over the outputs. `omcad-regress --determinism` runs on every local build against
`FakeKernel` and nightly against both kernels.

The ordered digest is the part that earns its keep. A histogram is order-blind, so before it
existed the gate compared equal on a run that produced the same entities in a different sequence —
and the sequence is precisely what a positional name resolves against. The gate could not see the
ordering defect described below; the contract battery caught it instead. That gap is now closed.

## Resolved

### Ordering that was not derived from geometry

The largest single finding, and it was live.

`SubEntity` implements `IComparable` by kind, then owning shape, then **tag**. A tag is a handle:
40 bits of slot index and 24 bits of generation counter. When a slot is recycled its generation
increments, which moves the tag by 2^40 — so an entity in a reused handle sorts nowhere near an
equivalent entity in a fresh one. Sorting a collection of `SubEntity` therefore orders by
allocation accident, not by anything about the model.

Every ordered view of a `HistoryMap` was built by sorting. Two identical models built in one
process enumerated their outputs differently depending only on what had been allocated and freed
before them.

The fix is that `HistoryMap` preserves the order the kernel reported rather than re-deriving one:

| View | Before | Now |
| --- | --- | --- |
| `Outputs` | sorted by tag | first-sight order from the builder |
| `Inputs` | sorted by tag | first-sight order from the builder |
| `WithRole` | sorted by tag | filtered from `Outputs` |
| `UnrolledOutputs` | sorted by tag | filtered from `Outputs` |
| `Generated(e)` / `Modified(e)` | sorted by tag | ordered by position in `Outputs` |
| `NewEntities` | sorted by tag | first-sight order from the builder |

That only helps if the reported order is itself canonical, which is `OcctKernel`'s job:
`NativeHistory` indexes every entity of every shape involved through `enumerate`, which the shim
answers in canonical order, and re-orders the shim's tag-sorted history against that index before
handing it to the builder.

**The single authority for canonical order is the shim's `enumerate_canonical`** — measure, then
quantised centroid, then traversal as a tie-break. Everything else consumes it. The shim's own
`history_*` entry points still return tags in numeric order, which is stable within a call but is
*not* geometric; that is deliberate rather than overlooked, because duplicating the canonical
ordering on both sides of the boundary invites the two copies to drift. The contract is recorded
in `docs/specs/kernel-shim.md`.

### OCCT build configuration

- **Intel TBB is off.** It introduces scheduling-dependent parallelism inside OCCT. `vcpkg.json`
  omits the feature and `native/openmcad_occt/CMakeLists.txt` fails the configure with a
  `FATAL_ERROR` if `OpenCASCADE_WITH_TBB` comes back true, so this cannot regress by someone
  installing a differently-built OCCT.
- **OCCT 8.0.1, pinned** by the vcpkg manifest and a concrete `builtin-baseline` commit.
- **`/fp:precise`.** Never `/fp:fast`: geometry depends on IEEE semantics, and reassociation
  changes results.

### Parallelism inside operations

- `BRepAlgoAPI_BooleanOperation::SetRunParallel(false)`. The parallel boolean partitions work
  across threads, and the order faces merge in can decide which of several tolerance-equal results
  emerges.
- `IMeshTools_Parameters::InParallel = false`. The parallel mesher assigns faces to threads and
  per-face vertex numbering follows completion order, so the same body would tessellate to the same
  triangles in a different order — a different hash.
- ADR-0004 confines the kernel to one thread anyway. These are belt and braces against a future
  caller, and they cost nothing measurable at the sizes involved.

### Queries that could depend on unrelated history

- **`bounding_box` uses `AddOptimal` with `useTriangulation = false`.** With triangulation allowed,
  the answer comes from a cached tessellation when one happens to exist and from the geometry
  otherwise — so the bounds of a body would change depending on whether anything had rendered it.
  `useShapeTolerance = false` and `SetGap(0)` for the same reason a user asking for the extent of
  their geometry does not want the modeller's uncertainty added to it.
- **`write_brep` pins `TopTools_FormatVersion_VERSION_3` and excludes triangulation.** An unpinned
  writer means an OCCT upgrade silently changes the bytes for an unchanged model, invalidating every
  content-hashed cache entry at once; including triangulation would make a body's serialised bytes
  depend on whether it had been rendered.

### Entity identity

`store_entity` keys identity on the shape under OCCT's `TopTools_ShapeMapHasher` — TShape and
location, orientation ignored — rather than on the raw `TShape` pointer. OCCT shares one `TShape`
between an entity and its relocated copy, so pointer identity silently aliased an extruded square's
top face, four top edges and four top vertices onto the bottom ones. They vanished from the history
entirely: no role, no name, unreachable.

### Locale

`initialize()` pins `LC_NUMERIC` to `C`. OCCT formats and parses reals through the CRT, so under a
comma-decimal locale every BREP and STEP file this process writes is unreadable and every one it
reads parses to the wrong numbers. The CLR does not call `setlocale`, so the default is already
correct today; it is pinned because any native library loaded into the process can change it for
everyone, and because the failure appears only on machines configured differently from the ones the
tests run on.

## Considered and deliberately not changed

- **Floating-point results across CPU models.** OCCT compiled for x64 uses SSE2, and the operations
  in use do not dispatch on AVX availability. Determinism is asserted within a build on a
  platform, not across architectures. A cross-platform corpus comparison would need tolerance-based
  rather than exact comparison, which is a Phase 15 concern if it ever becomes one.
- **The shim's tag-sorted `history_*` results.** See above: stable within a call, canonicalised by
  the consumer, single authority preserved.
- **Handle values themselves.** Tags are not stable across sessions and are not meant to be —
  persistence uses `PersistentName` (§5.3), not tags. Nothing durable may record a tag.
- **Fixture discovery order in the corpus runner.** It affects the order results are printed, not
  the results.
- **Rung distribution as a gate.** Reported, not asserted on. A threshold would either be so loose
  it never fires or so tight that adding one deliberately hard fixture breaks the build — and the
  corpus is meant to grow toward hard cases. A human reading a falling first-rung rate is the check
  (PLAN.md 5.2.4).

## Still open

- **Cross-machine reproduction.** Everything here is verified on one machine and in CI on
  `windows-latest`. Whether two different CPUs produce bit-identical geometry is untested. It is
  the assumption behind sharing a geometry cache between machines, which nothing does yet.
- **Long-run slot recycling.** The ordering fix removed the dependence on handle values, so slot
  recycling should no longer be observable. The corpus is too short to exercise it. A soak fixture
  that builds and discards many bodies before the fixture under test would prove it; that belongs
  with the fuzz work in P15-T07.
