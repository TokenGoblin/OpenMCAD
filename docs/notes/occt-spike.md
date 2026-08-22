# Note: the OCCT spike

**Date:** 2026-08-22 · **Task:** PLAN.md §14 item 2 · **Status:** complete

> *"Before Phase 1, spend a timeboxed three days on an OCCT spike outside the repo: build it via
> vcpkg, call five operations from a throwaway C++ program, and confirm the version, build flags,
> and behavior you are about to design around. Assumptions verified here are worth ten times what
> they cost."*

The programme lived at `C:\spike`, outside the repository as the plan asks. It is not checked in;
this note is the deliverable. What follows is what the spike **measured**, not what it hoped.

---

## What was built

| | |
|---|---|
| OCCT | **8.0.1** (`OCC_VERSION_COMPLETE`), from vcpkg port `opencascade[core,freetype]` |
| Triplet | `x64-windows`, shared libraries |
| Compiler | MSVC 14.51.36231 (Build Tools 2026, 18.9.12112.369) |
| Eigen | 5.0.1 |
| vcpkg baseline | `127402f1c75bb3d5ff6bce04b285faa4930a5aca` |
| Build time | 42 minutes, ~12 GB of build trees, 1.88 GB installed |

`OpenCASCADE_WITH_TBB = OFF` and `HAVE_TBB` is undefined. **PLAN.md §5.2.3's requirement that
OCCT's internal parallelism be off is satisfied by the manifest as written**, not by accident —
`tbb` is a non-default feature and `native/vcpkg.json` selects features explicitly.

`OpenCASCADE_BUILD_SHARED_LIBS = ON`. OCCT is 57 DLLs totalling 52 MB, of which the Phase 1
operation set pulls in ten (`TKernel`, `TKMath`, `TKGeomBase`, `TKBRep`, `TKTopAlgo`, `TKPrim`,
`TKBO`, `TKFillet`, `TKMesh`, `TKDESTEP`). Shared linkage matters beyond convenience: ADR-0003 and
§8.6 want OCCT separately replaceable so the LGPL relinking condition is trivially satisfied, and
this build is exactly that.

---

## The assumptions, and what actually happened

### ✅ A2 — Determinism holds, within and across processes

The central assumption behind ADR-0011, the geometry cache, undo, and the whole naming layer.

Two identical `BRepAlgoAPI_Cut` operations produced identical topology counts, identical face
ordering, and a volume equal **to the last bit** (`3497.34517543…`). Running the whole spike as two
separate processes produced byte-identical output.

This is the single most important result here. It was not guaranteed — PLAN.md §5.2.3 explicitly
warns that "OCCT results can vary with iteration order and memory layout" — and the nightly
determinism gate now has a defensible basis rather than a hope.

**Caveat:** confirmed for one machine, one build, one small model. The gate must keep running.

### ✅ A5 — Iteration order is stable

`TopExp_Explorer` traversed two identically-built boxes in the same order, and repeated traversal
of one shape was stable across 20 iterations. Canonical ordering can be built on top of OCCT's
traversal rather than having to sort everything by geometry first.

**Still sort anyway.** Stability here is empirical, not contractual, and §5.2.3 asks for a stable
geometric key. Treat OCCT's order as a good default that the shim confirms, not as the contract.

### ⚠️ A1a — Untouched faces are absent from the history map

**This changes shim implementation, though not the design.**

Cutting a cylinder out of a box: of the box's 6 faces, OCCT reported **2 modified, 0 deleted, and
4 with no entry at all**. `Modified()` returns an empty list for a face the operation did not
touch, and `IsDeleted()` is false for it.

So `OperationRole.Retained` — which `HistoryMapBuilder.AddRetained` exists for, and which is the
majority of any boolean — **cannot be populated from OCCT's history map**. The shim must, for each
input entity with no history entry and not deleted, locate the survivor in the output itself.
OCCT preserves the same `TShape` for untouched entities, so `TopoDS_Shape::IsSame` against a map of
the output is the mechanism.

Getting this wrong would not throw. It would produce history maps missing two thirds of their
entries, and names that fail to resolve through operations that did not affect them — which is
precisely the "models break on edit for no reason" failure ADR-0005 is written to prevent.

**Action for P1-T06:** every operation body must run a retained-entity sweep after the kernel call.
Write it once as a helper, not once per operation.

### ⚠️ A1b — A blend face is reachable from the edge, but not from the faces it joins

`Generated(filleted edge)` returned 1 entity: the blend face. Good.

`Generated(adjacent faces)` returned **0**. OCCT does not relate the blend face to the two faces it
lies between.

`HistoryMapBuilder.AddNewBetween(blendFace, BlendFace, faceA, faceB)` was written specifically for
this case, because "the blend between these two faces" is what survives a rebuild whereas "the
blend of edge #7" does not. The spike says OCCT will not hand us that relationship.

**Action for P1-T06:** the fillet body must compute edge→adjacent-face adjacency from the **input**
shape, before the operation runs, via `TopExp::MapShapesAndAncestors`. Afterwards the input edge is
gone and the relationship is unrecoverable. `FakeKernel` already does this — `FakeEntity.AdjacentFaces`
is populated at construction — so the two kernels will agree, which is what the contract battery
checks.

The filleted edge *was* correctly reported as deleted, so that half of the design needs nothing.

### ⚠️ A3 — Failures do not always throw

A 50 mm fillet on a 10 mm box — geometrically impossible — did **not** throw `Standard_Failure`. It
returned `IsDone() == false`.

That is a silent-wrong-answer path. An operation body that calls `Build()` and then reads `Shape()`
without checking `IsDone()` gets the *unmodified input shape* back and would report Success. The
user would see a fillet that did nothing and no diagnostic explaining why.

**Action for P1-T06:** `IsDone()` is mandatory after every `Build()`. The exception firewall is
necessary but not sufficient; it only catches the failures that throw.

`BRepCheck_Analyzer` accepted a well-formed box, so PLAN.md §8.3's validity assertion is usable.

### ❔ A4 — The retry ladder is unproven

A cylinder exactly tangent to a box face — ADR-0001 names this class as where OCCT differs most
from Parasolid — fused successfully at model tolerance **and** at a 1e-4 fuzzy tolerance, with
identical results. `SetFuzzyValue` exists and works.

So the ladder's mechanism is available, but this input did not discriminate between rungs. **The
spike neither confirms nor refutes that the ladder earns its complexity.**

**Action:** do not treat the ladder as validated. P1-T11 should be built against a corpus of
genuinely hard cases — near-coincident faces, self-intersecting blend chains, near-degenerate
overlaps — and the rung-distribution metric will say whether rungs 2 to 4 ever fire in practice. If
they never do, that is worth knowing too.

### ⚠️ STEP export is not byte-reproducible

Two runs produced STEP files differing in exactly one line:

```
FILE_NAME('Open CASCADE Shape Model','2026-08-22T17:44:21',...)
FILE_NAME('Open CASCADE Shape Model','2026-08-22T17:44:22',...)
```

OCCT stamps the current time into the header. The `exchange/` corpus category cannot compare STEP
files byte-for-byte; it must normalise the header or compare semantically after a re-import.

Worth noting `FakeStepWriter` already writes a fixed epoch timestamp for this reason — that
instinct was right, and `OcctKernel` will need the same treatment or the normalisation must live in
the corpus runner.

---

## The five operations

All succeeded on a 30×20×10 box with a Ø8 hole and four 1 mm fillets:

| | Result |
|---|---|
| Box | `1so 1sh 6f 6w 12e 8v`, volume 6000 |
| Cylinder | `1so 1sh 3f 3w 3e 2v`, volume 1507.964… |
| Cut | `1so 1sh 7f 9w 15e 10v`, volume 5497.345… |
| Fillet ×4 | `1so 1sh 11f 13w 23e 14v` |
| Tessellate | `BRepMesh_IncrementalMesh` at 0.1 mm, done |
| STEP write | 694 entities |

The drilled volume matched the analytic value to **1.65e-16 relative** — floating-point exact. OCCT's
boolean is doing real work correctly on this input, and `BRepCheck_Analyzer` passed on the final
shape.

Note the cylinder is `3f 3e 2v`, which matches the topology `FakeKernel` synthesises for a cylinder
exactly (lateral, two caps; two circles and a seam; two seam vertices). That was a guess when it was
written and it turns out to be right.

---

## What this changes

Nothing in the architecture. Three things in the implementation, all in P1-T06:

1. A **retained-entity sweep** helper, applied after every operation.
2. **Edge→face adjacency captured from the input** before any blend runs.
3. **`IsDone()` checked** after every `Build()`, without exception.

And one thing in the plan's confidence: the determinism assumption that ADR-0011 rests on is now
measured rather than assumed, and it held.

## What was not tested

Stated so nobody mistakes silence for evidence:

- **Thread safety.** ADR-0004 assumes OCCT is not thread-safe and marshals everything onto one
  thread. The spike did not try to violate that, because the design does not depend on knowing
  *how* it fails.
- **Large models.** Everything here is a handful of faces. Nothing is known about the performance
  budgets in §7.
- **Blend robustness at scale.** Four 1 mm fillets on a simple solid is not the fillet-chain case
  P7-T07 will face.
- **Cross-machine determinism.** One machine, one compiler, one OCCT build. The nightly gate covers
  same-machine drift; cross-machine agreement is untested and matters for CI versus local.
