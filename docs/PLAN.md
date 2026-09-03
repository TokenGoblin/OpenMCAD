# PLAN.md — OpenMCAD

**A parametric, feature-based mechanical CAD system for Windows, built on .NET 10.**

> Name: **OpenMCAD** (originally drafted under the codename *Anvil*; renamed in Phase 0 per
> ADR-0017). Root namespace `OpenMCAD.*`; documents `.ompart` / `.omasm` / `.omdrw`; native
> shims `openmcad_occt.dll` / `openmcad_gcs.dll`; shell `OpenMCAD.exe`; CLI `omcad.exe`.

---

## 0. How to use this document

This is the master plan. It is written to be consumed by **Claude Code** as the standing context for a long-running build, and by a human as the product roadmap. Everything an implementing agent needs to make a correct local decision should be derivable from this file.

**Rules for the implementing agent:**

1. **This document is authoritative on architecture.** If an implementation detail contradicts a locked decision in §2, the implementation is wrong — do not silently diverge. If you believe a locked decision is wrong, write a new ADR proposing the change and stop for human review.
2. **Work phase by phase.** Do not start Phase *N+1* until Phase *N*'s exit criteria in §9 are demonstrably met. Exit criteria are written to be mechanically checkable.
3. **Every task has a stable ID** (`P3-T07`). Reference IDs in commit messages: `P3-T07: implement dirty-propagation in RebuildEngine`.
4. **Tests are not a later phase.** A task is not complete without the tests named in its acceptance criteria. The regression corpus (§8) begins in Phase 1 and grows every phase.
5. **Never break a saved file.** From Phase 3 forward, the file format is versioned and every schema change ships with a migration and a round-trip test against every prior version's corpus fixtures.
6. **When you hit an unbounded research problem** (surface–surface intersection robustness, naming heuristics, blend topology), timebox it, write down what you learned in `docs/notes/`, implement the bounded version, and file a follow-up task. Do not disappear into it.

**Repo conventions:** trunk-based with short-lived branches; Conventional Commits; every PR runs the full unit + regression suite; `main` is always buildable and always launchable.

---

## 1. What we are building, and what we are not

### 1.1 Product definition

A Windows-native desktop MCAD application providing:

- **History-based parametric part modeling** — a feature tree whose features recompute in dependency order from editable parameters.
- **A fully constrained 2D sketcher** with a numerical constraint solver and proper DOF diagnostics.
- **Assemblies** — component instancing, mates/joints, an assembly-level constraint solve, in-context references, BOM.
- **Production drawings** — associative views generated from 3D by hidden-line removal, full dimensioning, GD&T, tables, sheet templates.
- **Data exchange** — STEP AP242, IGES, DXF/DWG, 3MF/STL, glTF, plus PMI where the format supports it.
- **Specialized environments** — sheet metal, weldments, surfacing, configurations/design tables.
- **An extensibility surface** — a stable public API and plugin loading, so third parties can build on it.

### 1.2 What "feature parity with major production MCAD" actually means

Be clear-eyed. The incumbents represent, conservatively:

| System | Kernel | Approximate accumulated engineering |
|---|---|---|
| SolidWorks | Parasolid + D-Cubed DCM | ~30 years, hundreds of engineers |
| NX | Parasolid + DCM | ~40 years |
| Creo | Granite (PTC in-house) | ~35 years |
| Inventor / Fusion | ASM (ACIS fork) | ~25 years |
| Onshape | Parasolid + DCM | ~12 years, cloud-native |
| FreeCAD | OCCT + planegcs | ~20 years, volunteer |

Parity is not a milestone you reach; it is an asymptote you approach. This plan is honest about that. **Phases 0–11 produce a genuinely usable MCAD product** for mainstream mechanical design — competitive with a mid-market seat for a large fraction of real work. **Phases 12–17 close the gap on specialized domains, scale, and productization.** Everything past that is the long tail that never ends.

Rough effort, expressed in *engineer-months of focused senior work* (agent-assisted development compresses the coding but not the design, debugging, or robustness work):

| Epoch | Phases | Engineer-months | Wall clock, small team (3–5) |
|---|---|---|---|
| A — Foundations | 0–5 | 30–48 | 9–14 months |
| B — Core product | 6–11 | 90–150 | 24–36 months |
| C — Advanced domains | 12–14 | 60–100 | 18–30 months |
| D — Scale & ship | 15–17 | 40–70 | 12–20 months |

Do not compress these by wishing. Compress them by cutting scope explicitly.

### 1.3 Explicit non-goals (for now)

- Cloud/multi-user simultaneous editing (Onshape model). Architecture should not *preclude* it — the document model is already an operation log — but it is out of scope.
- CAM, CAE solvers, or electrical/routing suites. We build *hooks* (mesh export, feature recognition surface), not solvers.
- macOS/Linux. The ViewModel layer stays UI-framework-agnostic so a port is possible, but nothing else accommodates it.
- Direct/synchronous ("push-pull on dumb solids") modeling as the primary paradigm. Direct-edit *operations* on the history tree are in scope (Phase 13); a full history-free modeling mode is not.

---

## 2. Locked architectural decisions

These are settled. Each has a full ADR in §3.

| # | Decision | Choice |
|---|---|---|
| 1 | Geometry kernel | **OCCT (Open CASCADE Technology)**, sole implementation |
| 2 | Kernel coupling | **Thin operation-level abstraction** (`IGeometryKernel`), *not* entity-level; Parasolid swap remains a bounded future project |
| 3 | Interop mechanism | **Hand-authored C ABI shim** (native C++ DLL) + `[LibraryImport]` source-generated P/Invoke. **No C++/CLI.** |
| 4 | Kernel threading | **Single-threaded kernel dispatcher** (actor). All kernel calls marshalled to it. |
| 5 | Entity identity | **Own topological naming layer**, fed by OCCT history maps. Do not depend on OCCT `TNaming`/OCAF. |
| 6 | Sketch solver | **planegcs** (FreeCAD, LGPL) behind `ISketchSolver`; managed replacement or DCM license is a Phase 14+ decision |
| 7 | UI shell | **WPF**, .NET 10, with a hard rule that **no WPF type appears in a ViewModel** |
| 8 | Rendering | **Direct3D 12 via Vortice.Windows**, hosted in an `HwndHost` viewport |
| 9 | Parity sequencing | **Breadth-first vertical slice** (Phase 5 "First Light"), then deepen |
| 10 | Persistence | **Zip/OPC container**, versioned schema (MessagePack), plus cached B-rep blobs and thumbnails |
| 11 | Undo/redo | **Command log over document parameter state** + recompute; never snapshot B-rep for undo |
| 12 | Extensibility | **`AssemblyLoadContext`-isolated plugins** against a versioned public API, established Phase 2 |
| 13 | Units | **Unit-aware expression engine** in the core, not the UI. Internal storage is meters/radians/kilograms, always. |
| 14 | Language/runtime | **.NET 10 (LTS), C# 14.** Nullable enabled, warnings-as-errors, `net10.0-windows` for shell only. |

---

## 3. Architecture decision records

### ADR-0001 — Geometry kernel: OCCT

**Status:** Accepted.

**Context.** The kernel determines the product's robustness ceiling. Options considered: OCCT; a from-scratch managed kernel; a commercial kernel (Parasolid, ACIS, C3D); a multi-kernel abstraction.

**Decision.** Use OCCT as the sole geometry kernel.

**Rationale.**

- It is the only free kernel with genuine production breadth: NURBS curves and surfaces, tolerant B-rep topology, boolean operations, fillets and chamfers, offset/shell, sweeps and lofts, exact hidden-line removal (needed for drawings), triangulation, and STEP/IGES/BREP translators.
- Roughly 30 years of accumulated fixes. A from-scratch kernel is a research project; robust surface–surface intersection and tolerant boolean classification alone are multi-year problems with no shortcut. You would ship a demo, not a product.
- Commercial kernels are better but cost six figures plus royalties, are NDA-gated, and preclude open development. Parasolid remains the *upgrade path*, not the *starting point*.

**Consequences / what will hurt.**

- **Boolean and blend robustness is the known weak point.** Tangent faces, near-coincident geometry, and self-intersecting fillet chains fail in ways Parasolid does not. Mitigation: an operation-retry ladder (§5.2.4), aggressive input conditioning, a boolean fuzz corpus from Phase 1, and honest, actionable failure reporting to the user rather than silent corruption.
- **OCCT is not thread-safe.** Forced into ADR-0004.
- **No official .NET binding.** Forced into ADR-0003.
- **Large-assembly performance is mediocre.** Mitigated by never holding full B-rep for display-only components (§5.9 lightweight mode).
- **Licensing:** LGPL-2.1 with the Open CASCADE Exception, which permits linking into proprietary applications. Compliance obligations are real; see §8.6. Keep OCCT as a *separately replaceable* binary (our shim DLL links it) so the exception's conditions are trivially satisfiable.

### ADR-0002 — Thin, operation-level kernel abstraction

**Status:** Accepted.

**Context.** Full multi-kernel abstractions have a poor track record: kernels differ in topology model, tolerance semantics, and persistent-ID schemes, so an entity-level abstraction degrades to a lowest common denominator and becomes a permanent tax.

**Decision.** Define `IGeometryKernel` at the level of **modeling operations**, not geometric entities. Roughly 200–300 operations covers a complete MCAD application:

```
IGeometryKernel
  ├─ Construction:  Extrude, Revolve, Sweep, Loft, Boundary, PrimitiveBox/Cyl/Sph/Cone/Torus
  ├─ Modification:  Boolean(Union|Subtract|Intersect), Fillet, Chamfer, Shell, Draft,
  │                 Thicken, Offset, Split, Trim, Delete/HealFace, Move/Replace Face
  ├─ Patterns:      LinearPattern, CircularPattern, MirrorBody, PathPattern
  ├─ Sheet metal:   Bend, Unbend, Flatten, Rip, Miter          (Phase 12)
  ├─ Query:         MassProperties, BoundingBox, Curvature, Ray/Shape intersection,
  │                 Distance, Interference, Section, SilhouetteEdges, ProjectCurve
  ├─ Tessellation:  Triangulate(shape, tol) → MeshBuffer,  EdgeDiscretize
  ├─ HLR:           HiddenLineRemove(shapes, projection) → 2D curve set  (Phase 5/10)
  └─ Serialization: WriteBRep, ReadBRep  (opaque blobs; format is kernel's, not ours)
```

Every operation returns an `OperationResult` carrying the resulting `KernelShape` handle **and a `HistoryMap`** — the generated/modified/deleted correspondence between input and output sub-entities. The history map is the raw material for topological naming (§5.3) and is non-negotiable in the signature.

**Explicitly *not* abstracted:** face, edge, vertex, surface, curve, or tolerance types. Those cross the boundary as **opaque handles plus our own persistent IDs**. Callers above the kernel layer never touch a `TopoDS_Face`.

**Consequences.**

- The parametric engine, naming layer, undo, and document model become testable against `FakeKernel` — a fast, deterministic mock. Test suite runs in seconds, not minutes. This alone justifies the abstraction.
- Persistent naming lives in *our* code, where it belongs.
- Swapping to Parasolid becomes a bounded 6–12 month project (Parasolid is a flat C API with integer tags — genuinely easier to bind than OCCT).
- Costs ~4–6 weeks of upfront design. Accept it.

### ADR-0003 — Interop via C ABI shim + `LibraryImport`, not C++/CLI

**Status:** Accepted.

**Decision.** Build `OpenMCAD.Kernel.Native` — a native C++ DLL exposing a **flat C ABI** over the OCCT subset we need — and bind it from C# with `[LibraryImport]` source-generated P/Invoke.

**Rationale.**

- C++/CLI works on .NET 10 but is Windows-only mixed-mode, permanently blocks NativeAOT, complicates CI and cross-compilation, and makes mixed-mode debugging miserable.
- A C shim forces the discipline of exposing only the operations we actually need — a virtue against OCCT's ~10,000-class surface.
- `LibraryImport` is source-generated, AOT-friendly, and allocation-free for blittable signatures.

**Shim design constraints.**

- Handles are opaque `uint64` tags in a shim-side handle table. No pointers cross the boundary. This makes the boundary safe, debuggable, and — importantly — logically identical to how Parasolid works, easing ADR-0002's swap path.
- **No C++ exceptions cross the boundary.** Every entry point is `noexcept`, returns an `OpenMcadStatus` int, and populates a thread-local last-error record retrievable via `openmcad_last_error()`. OCCT throws `Standard_Failure`; catch all of it at the boundary.
- Bulk data (mesh buffers, curve arrays) crosses as caller-allocated spans or shim-allocated buffers freed by an explicit `openmcad_free`. Two-call pattern: query size, then fill.
- Generate the shim's declarations and the C# bindings from a **single IDL** (`kernel.api.json`) so extending the surface stays mechanical, not clerical. Build the generator in Phase 1; it pays for itself by Phase 7.

### ADR-0004 — Single-threaded kernel dispatcher

**Status:** Accepted.

**Context.** OCCT is not thread-safe in general. Some operations are re-entrant on disjoint shapes, but the guarantees are unclear, undocumented in places, and version-dependent.

**Decision.** All kernel calls are marshalled onto **one dedicated kernel thread** via `KernelDispatcher`, an actor with a priority work queue. The public C# kernel API is `async` and returns `ValueTask<OperationResult>`. Assert (in debug) that no kernel call occurs off the kernel thread.

**Rationale.** Correctness first; a class of impossible-to-reproduce heisenbugs is eliminated by construction. Retrofitting this after parallelizing rebuild would be agony.

**Consequences and mitigations.**

- The kernel is a serial resource. Rebuild parallelism therefore happens *above* it: independent DAG branches queue work concurrently, but execute serially. This is still a large win because the non-kernel work (naming resolution, expression evaluation, validation) parallelizes freely.
- **Escape hatch, Phase 15:** a pool of *N* kernel worker threads, each owning a strictly isolated shape universe with no shared `Handle` graph, used only for embarrassingly parallel work (tessellation of independent bodies, HLR of independent drawing views, batch import). Prove isolation with a stress test before enabling.
- The UI never blocks: viewport rendering reads from an immutable, versioned display snapshot (§5.10), not from live kernel state.

### ADR-0005 — Own the topological naming layer

**Status:** Accepted. **This is the highest-risk subsystem in the product.**

**Context.** When a user edits a sketch dimension and the model rebuilds, the fillet applied to "that edge" must still find that edge. Kernel indices are worthless — they change on every rebuild. This is the single clearest dividing line between production MCAD and hobby MCAD, and the reason FreeCAD models break where SolidWorks models do not.

**Decision.** Implement `OpenMCAD.Core.Naming` as a first-class subsystem. Entities are named by **generative provenance**, not by index: an entity name is a structured path recording which feature created it, from which input entity, in what role, with a geometric disambiguator for ties. OCCT's `BRepTools_History`/`BRepAlgoAPI` history output is the *raw input* to this layer; OCCT's `TNaming`/OCAF is **not** used.

**Consequences.** Full design in §5.3. It carries its own regression suite (`tests/naming-corpus`) from Phase 3, and every new feature type in every later phase must add naming cases to it. Treat a naming regression as a P0 bug.

### ADR-0006 — planegcs behind `ISketchSolver`

**Status:** Accepted.

**Decision.** Wrap FreeCAD's `planegcs` (LGPL, Eigen-based) through the same C-shim mechanism as the kernel, behind `ISketchSolver`.

**Rationale.** A sketch solver is a second numerical kernel — DOF analysis, decomposition into independently solvable subsystems, Levenberg–Marquardt/dogleg iteration, and the diagnostics (over-constrained, under-constrained, redundant, conflicting) that make a sketcher feel professional. planegcs is the only mature free implementation. D-Cubed DCM is the industry standard and is commercial.

**Consequences.** The interface is deliberately narrow: submit a parameter vector, an entity set, and a constraint set; receive a converged parameter vector plus a diagnosis. That narrowness makes a managed rewrite or a DCM license a contained swap later. Sketch *semantics* (constraint kinds, inference, drag behavior, auto-constraints) live in our code, above the solver.

### ADR-0007 — WPF shell with framework-agnostic ViewModels

**Status:** Accepted.

**Decision.** WPF for the shell. **Hard rule: no `System.Windows.*` type appears in any type under `OpenMCAD.ViewModels`.** Enforced by an architecture test (NetArchTest) in CI, not by discipline.

**Rationale.** WPF is in maintenance mode, and it is still correct here: the docking, ribbon, property-grid, and virtualized-tree ecosystem (AvalonDock, Fluent.Ribbon, and the commercial suites) saves close to a year of shell work. Airspace issues with a hosted D3D surface are a non-problem when the viewport is one large rectangle. WinUI 3 composes better via `SwapChainPanel` but its docking ecosystem is thin. Avalonia is the right answer only if cross-platform outranks Windows-native feel.

**Consequences.** The agnostic-VM rule means a future Avalonia or WinUI shell is a reskin, not a rewrite — the actual insurance policy against WPF's end of life.

### ADR-0008 — D3D12 via Vortice.Windows

**Status:** Accepted.

**Decision.** Direct3D 12 through Vortice.Windows, presented into an `HwndHost`-hosted child window.

**Rationale.** CAD rendering has needs that a general 3D engine does not serve well: a dedicated integer **ID buffer pass** for pixel-exact picking of faces/edges/vertices; depth-biased line rendering so edges sit cleanly on shaded faces without z-fighting; order-independent transparency for section views and assembly ghosting; heavy GPU instancing for assemblies with tens of thousands of occurrences; and deterministic frame pacing during drag operations. D3D12's explicit control serves all of these; a game engine would fight us on all of them.

**Consequences.** More upfront plumbing (descriptor heaps, fences, upload rings) than D3D11. Budget it in Phase 2. Abstract the RHI thinly (`IRenderDevice`) so a D3D11 fallback path is possible for old hardware if telemetry ever demands it.

### ADR-0009 — Breadth-first vertical slice

**Status:** Accepted.

**Decision.** Phase 5 delivers an end-to-end path — sketch → extrude → two-part assembly with mates → a dimensioned drawing view → STEP export — before any subsystem is deepened.

**Rationale.** Assemblies and drawings impose constraints on the document model, naming scheme, selection model, and file format that are cheap to accommodate early and brutally expensive to retrofit. Depth-first part modeling is the tempting choice and it is wrong: it hides those constraints until the cost of honoring them is a rewrite. Prove the hard interactions while the architecture is still soft.

### ADR-0010 — File format: OPC container, versioned schema, cached B-rep

**Status:** Accepted. Full spec in §5.8.

**Decision.** `.ompart` / `.omasm` / `.omdrw` are Zip/OPC containers holding: a manifest, a versioned MessagePack document graph, cached kernel B-rep blobs, tessellation caches, thumbnails, and optional external-reference metadata.

**Rationale.** The document graph must be human-inspectable in a pinch and machine-migratable forever. Cached B-rep means opening a large assembly does not require rebuilding every feature of every part. Thumbnails and metadata in a well-known container location means Explorer/PDM integration is nearly free.

### ADR-0011 — Undo over parameter state, not geometry

**Status:** Accepted.

**Decision.** Undo/redo is a command log of document *transactions* over the parametric state (features, parameters, sketch entities, mates, annotations). Undo restores parameter state and triggers a scoped recompute. B-rep results are a **cache**, keyed by feature ID + input hash, never an undo unit.

**Rationale.** B-rep snapshots are enormous; a 200-feature part would make undo unusable. Recompute is bounded by the dirty subgraph and is usually fast. Where recompute is slow, the geometry cache absorbs it — undoing to a state whose cache entries are still valid costs nothing.

**Consequences.** Recompute must be deterministic: same inputs → identical output topology and identical names. Non-determinism in the kernel (iteration order, hash ordering) must be found and eliminated in Phase 1, and guarded by a determinism test that rebuilds the corpus twice and diffs the naming output.

### ADR-0012 — Establish the plugin API in Phase 2, not later

**Status:** Accepted.

**Decision.** `OpenMCAD.Api` exists as a separate, semver-governed assembly with a checked-in API-surface baseline from Phase 2, and plugins load into isolated `AssemblyLoadContext`s.

**Rationale.** Retrofitting an extensibility surface onto a mature codebase leaves two bad options: break every existing plugin, or freeze a design that was never intended to be public. Establishing it early also imposes a useful discipline — it forces a clean separation between what the application *is* and what it *exposes*, which improves the internal design independently of whether anyone ever writes a plugin.

**Consequences.** Some churn cost early, when the API is small and churn is cheap. Plugins never receive raw kernel handles — only our abstraction — so a future kernel swap (ADR-0002) does not break the ecosystem.

### ADR-0013 — Unit-aware quantities in the core, SI internally

**Status:** Accepted.

**Decision.** Parameters carry a dimension, not a bare double. Internal storage is always SI base units (metres, radians, kilograms, seconds). Conversion happens only at the input/display boundary. The expression engine lives in `OpenMCAD.Core`, not the UI.

**Rationale.** Unit handling pushed into the UI layer is the origin of an entire genus of CAD bugs: values that drift on round-trip, mixed-unit arithmetic that silently produces nonsense, documents that mean different things depending on the viewer's settings. Making dimension part of the type turns `4 mm + 3 deg` into an error caught before evaluation. Mechanical engineering is also irreducibly bi-modal on units — metric and imperial both matter, often in the same document — so this cannot be deferred as a localization concern.

**Consequences.** Slightly more ceremony at every parameter site. A round-trip test (display → parse → store → display) is mandatory and must be exact.

### ADR-0014 — .NET 10 LTS, C# 14, nullable and warnings-as-errors

**Status:** Accepted.

**Decision.** Target .NET 10 (LTS) with C# 14. Libraries target `net10.0`; only `OpenMCAD.Shell` targets `net10.0-windows`. Nullable reference types enabled and warnings treated as errors, repository-wide, from the first commit.

**Rationale.** An LTS runtime matters for a product with a decade-long horizon and enterprise deployment. Keeping the Windows-specific TFM confined to the shell is what makes ADR-0007's portability insurance real rather than notional — if the libraries compile against plain `net10.0`, a non-Windows shell is genuinely possible. Nullable-from-day-one is essentially free at the start and prohibitively expensive to adopt at 500k lines.

**Consequences.** Occasional friction with packages that lag on nullable annotations; suppress narrowly and locally, never globally.
---

## 4. System architecture

### 4.1 Layers

Dependencies point downward only. Enforced by architecture tests in CI.

```
┌──────────────────────────────────────────────────────────────────────┐
│  OpenMCAD.Shell (WPF)          ribbon · docking · dialogs · viewport host │
├──────────────────────────────────────────────────────────────────────┤
│  OpenMCAD.ViewModels           MVVM, zero WPF types, fully unit-testable  │
├──────────────────────────────────────────────────────────────────────┤
│  OpenMCAD.Interaction          tools/gestures state machines · selection  │
│                             · manipulators · snapping · inference      │
├───────────────┬──────────────────────────────┬───────────────────────┤
│ OpenMCAD.Render  │  OpenMCAD.App                   │  OpenMCAD.Plugins        │
│ D3D12 · scene │  commands · undo · documents │  ALC isolation ·      │
│ · picking ·   │  · session · settings        │  public API surface   │
│ display cache │                              │                       │
├───────────────┴──────────────────────────────┴───────────────────────┤
│  OpenMCAD.Modeling      features · sketches · assemblies · drawings ·     │
│                      sheet metal · surfacing (feature semantics)       │
├──────────────────────────────────────────────────────────────────────┤
│  OpenMCAD.Core          document graph · rebuild engine · naming ·        │
│                      expressions & units · transactions · persistence  │
├──────────────────────┬───────────────────────────────────────────────┤
│  OpenMCAD.Kernel        │  OpenMCAD.Solver                                  │
│  IGeometryKernel ·   │  ISketchSolver · IAssemblySolver ·             │
│  dispatcher · handles│  DOF analysis · diagnostics                    │
├──────────────────────┴───────────────────────────────────────────────┤
│  OpenMCAD.Kernel.Occt   │ OpenMCAD.Solver.Planegcs   (LibraryImport bindings)│
├──────────────────────┴───────────────────────────────────────────────┤
│  native: openmcad_occt.dll  ·  openmcad_gcs.dll     (C ABI shims, C++)      │
│  vendored: OCCT  ·  planegcs/Eigen                                     │
└──────────────────────────────────────────────────────────────────────┘
```

Two rules make this hold:

- **`OpenMCAD.Core` knows nothing about OCCT.** It depends on `OpenMCAD.Kernel`'s interfaces only. `FakeKernel` satisfies them.
- **`OpenMCAD.Modeling` knows nothing about the UI.** A feature definition can be created, rebuilt, serialized, and validated headlessly. Everything is scriptable and CI-testable without a window.

### 4.2 Threading model

| Thread | Owns | Rules |
|---|---|---|
| **UI thread** | WPF visual tree, input events | Never blocks on kernel work. Never touches `KernelShape`. |
| **Kernel thread** (1) | All OCCT state | The only thread that may call into `openmcad_occt.dll`. Debug-asserted. |
| **Rebuild coordinator** | Feature DAG traversal | Computes the dirty set, orders work, awaits kernel results, resolves names. Parallel where the DAG allows; kernel calls still serialize. |
| **Render thread** | D3D12 device, command lists | Reads immutable `DisplaySnapshot` objects. Never blocks on rebuild. |
| **Worker pool** | Tessellation post-processing, I/O, autosave, thumbnails, search indexing | No kernel access except via the dispatcher. |

**Data flow discipline:** rebuild produces an immutable, versioned `DisplaySnapshot` (mesh buffers, edge polylines, ID mappings, transforms). The render thread atomically swaps to the newest snapshot at frame start. There is no lock between rebuild and render — only a reference swap. This is what keeps the viewport at 60 fps while a large rebuild runs.

### 4.3 Repository layout

```
openmcad/
├─ .github/workflows/           ci.yml · release.yml · nightly-regression.yml
├─ .editorconfig                analyzers, style, nullable, warnings-as-errors
├─ Directory.Build.props        shared TFM, langversion, analyzer config
├─ Directory.Packages.props     central package management
├─ global.json                  pinned .NET 10 SDK
├─ OpenMCAD.slnx
│
├─ docs/
│  ├─ PLAN.md                   ← this file
│  ├─ adr/                      0001-kernel.md … (full ADRs; §3 is the digest)
│  ├─ specs/                    per-subsystem specs, expanded from §5
│  ├─ notes/                    research spikes, timeboxed investigations
│  └─ api/                      generated public API docs + api-surface baseline
│
├─ native/
│  ├─ openmcad_occt/               C ABI shim over OCCT
│  │  ├─ include/openmcad_occt.h   generated from kernel.api.json
│  │  ├─ src/                   handwritten operation impls
│  │  └─ CMakeLists.txt
│  ├─ openmcad_gcs/                C ABI shim over planegcs
│  ├─ third_party/              OCCT + planegcs as submodules or vcpkg manifest
│  └─ tools/idlgen/             IDL → C header + C# bindings generator
│
├─ src/
│  ├─ OpenMCAD.Kernel/             IGeometryKernel, KernelShape, HistoryMap, dispatcher
│  ├─ OpenMCAD.Kernel.Occt/        LibraryImport bindings + IGeometryKernel impl
│  ├─ OpenMCAD.Kernel.Fake/        deterministic mock kernel for tests
│  ├─ OpenMCAD.Solver/             ISketchSolver, IAssemblySolver, DOF analysis
│  ├─ OpenMCAD.Solver.Planegcs/    bindings + impl
│  ├─ OpenMCAD.Core/               document graph, rebuild, naming, expressions, persistence
│  ├─ OpenMCAD.Modeling/           feature definitions: part, sketch, assembly, drawing, sheetmetal
│  ├─ OpenMCAD.Exchange/           STEP/IGES/DXF/3MF/glTF import-export
│  ├─ OpenMCAD.Render/             D3D12 renderer, scene graph, picking, display cache
│  ├─ OpenMCAD.Interaction/        tool state machines, selection, manipulators, snapping
│  ├─ OpenMCAD.App/               commands, undo stack, document session, settings
│  ├─ OpenMCAD.ViewModels/         MVVM layer — no WPF references, ever
│  ├─ OpenMCAD.Shell/              WPF application (net10.0-windows)
│  ├─ OpenMCAD.Api/                public plugin API surface (semver-governed)
│  └─ OpenMCAD.Cli/                headless runner: rebuild, convert, regress, benchmark
│
├─ tests/
│  ├─ unit/                     one project per src project
│  ├─ integration/              document round-trips, rebuild scenarios
│  ├─ regression/
│  │  ├─ corpus/                versioned model fixtures (git-lfs)
│  │  ├─ golden/                expected mass properties, topology counts, name maps
│  │  └─ OpenMCAD.Regression/      runner
│  ├─ fuzz/                     boolean/fillet fuzzers, sketch solver fuzzers
│  ├─ perf/                     BenchmarkDotNet + scale scenarios
│  └─ arch/                     NetArchTest layering rules
│
├─ samples/                     example parts, assemblies, drawings, plugin samples
└─ build/                       packaging, installer (WiX/MSIX), signing, versioning
```

### 4.4 Baseline dependencies

| Purpose | Choice | Notes |
|---|---|---|
| Runtime | .NET 10 (LTS), C# 14 | `net10.0` for libraries, `net10.0-windows` for shell only |
| Graphics | Vortice.Windows | D3D12, DXGI, D3DCompiler, WIC |
| Math | System.Numerics + own `OpenMCAD.Math` | Own double-precision Vec3d/Mat4d — `System.Numerics` is float; CAD needs double |
| Serialization | MessagePack-CSharp | Versioned, fast, compact; JSON export for debugging |
| DI | Microsoft.Extensions.DependencyInjection | |
| Logging | Microsoft.Extensions.Logging + Serilog sink | Structured; rebuild traces are logged as structured events |
| Docking | AvalonDock (or commercial suite) | Evaluate in Phase 0; commercial is defensible here |
| Ribbon | Fluent.Ribbon (or commercial suite) | Same |
| Testing | xUnit, FluentAssertions, BenchmarkDotNet, NetArchTest | |
| Native build | CMake + vcpkg | OCCT and Eigen via vcpkg manifest, pinned versions |

**Precision rule:** all geometry is `double`. `float` appears only in GPU buffers, and only after subtracting a per-view origin to preserve precision at large coordinates.

---

## 5. Subsystem specifications

### 5.1 `OpenMCAD.Kernel` — the abstraction

**`KernelShape`** — an opaque handle (`readonly record struct KernelShape(ulong Tag)`), valid only within a `KernelSession`. Shapes are reference-counted across the boundary; the C# side owns a `SafeHandle`-derived wrapper so a dropped shape is released deterministically on the kernel thread.

**`HistoryMap`** — the critical return value. For an operation with inputs *I* and output *O*:

```csharp
sealed class HistoryMap {
    IReadOnlyList<SubEntity> Generated(SubEntity input);  // input → new entities it created
    IReadOnlyList<SubEntity> Modified(SubEntity input);   // input → its altered successors
    bool IsDeleted(SubEntity input);
    IReadOnlyList<SubEntity> NewEntities { get; }         // created ex nihilo (e.g. fillet faces)
    OperationRole RoleOf(SubEntity output);               // SideWall|StartCap|EndCap|BlendFace|…
}
```

`OperationRole` is our invention, not OCCT's, and it is what makes naming stable and human-legible. Every operation implementation must populate it deliberately. An operation that returns an unrolled or `Unknown` role is an incomplete implementation — fail the review.

**`OperationResult`** — `Success | Failed(diagnostics) | Degraded(shape, warnings)`. `Degraded` matters: a fillet that succeeded on 11 of 12 edges should report which one failed and why, and let the user decide, exactly as production MCAD does.

**`KernelDispatcher`** — the actor. Priority queue: interactive (drag preview) > rebuild > background (tessellation, thumbnails). Supports cancellation: a rebuild superseded by a newer edit cancels at the next operation boundary. Instruments every call with duration and shape complexity for the perf corpus.

### 5.2 `openmcad_occt` — the native shim

#### 5.2.1 IDL-driven generation

`native/kernel.api.json` declares each operation: name, parameters (with marshalling kind), return shape, error domain. `tools/idlgen` emits:

- `openmcad_occt.h` — C declarations
- `openmcad_occt_dispatch.cpp` — argument unpacking, handle lookup, exception firewall, status return
- `OcctBindings.g.cs` — `[LibraryImport]` declarations
- `IGeometryKernel` partial implementation stubs

Only the operation *body* is handwritten. This is the difference between a maintainable 300-operation surface and a clerical nightmare.

#### 5.2.2 Boundary rules

- Every export is `extern "C"`, `noexcept`, returns `int32_t OpenMcadStatus`.
- All OCCT exceptions (`Standard_Failure` and descendants) plus `std::exception` and `...` are caught at the boundary and converted to status + thread-local diagnostic record (message, OCCT exception type, operation name, offending entity tags).
- Handle table: `uint64` tags → `TopoDS_Shape` / `Handle(Geom_*)`. Generation counter in the high bits so a stale tag is detected rather than aliasing a recycled slot.
- Bulk transfer: two-call size-then-fill, or shim-allocated with explicit `openmcad_free`. Never return a pointer into OCCT-owned memory.
- **Set OCCT's floating-point trap and signal handling explicitly at init** (`OSD::SetSignal`) — otherwise an FPE inside OCCT takes the process down.

#### 5.2.3 Determinism

OCCT results can vary with iteration order and memory layout. Determinism is a hard requirement (ADR-0011). Actions: pin the OCCT version and build flags; disable any parallel execution inside OCCT (`BRepMesh` parallel mode off by default, enable only where proven deterministic); sort all entity collections by a stable geometric key before returning them. A nightly test rebuilds the whole corpus twice and diffs topology + names; any difference is a P0.

#### 5.2.4 The operation retry ladder

Because OCCT booleans and blends are the known weak point (ADR-0001), every fragile operation runs through a ladder rather than a single attempt:

1. Attempt at model tolerance.
2. On failure: run `ShapeFix`/`ShapeUpgrade` conditioning on inputs (sew, remove tiny edges, unify same-domain faces), retry.
3. On failure: retry at a relaxed fuzzy tolerance (`BRepAlgoAPI_BooleanOperation::SetFuzzyValue`).
4. For fillets/chamfers: retry edge-by-edge to isolate the failing subset, return `Degraded` with the specific failures.
5. On total failure: return `Failed` with a diagnostic naming the operation, the entities, the tolerance tried, and — critically — a **user-actionable message** ("the fillet radius 8 mm exceeds the available material at edge *E*; try 5 mm or reorder before the shell").

Every rung is logged. The distribution of which rung succeeded is a health metric tracked across the corpus over time; if rung-1 success rate falls, something regressed.

### 5.3 `OpenMCAD.Core.Naming` — topological naming

**The problem.** `Fillet(edge)` must find "the same edge" after the sketch that generated it changes. Kernel indices change; geometry moves; topology may split or merge.

**The scheme.** An entity reference is a `PersistentName`: an ordered path of `NameSegment`s recorded at creation time.

```
NameSegment = (FeatureId, ProvenanceKind, SourceRef, Role, Ordinal, GeoHint)

FeatureId       stable GUID of the creating feature
ProvenanceKind  Generated | Modified | Intersection | New | Imported
SourceRef       PersistentName of the input entity (recursive), or a sketch-entity ID
Role            SideWall | StartCap | EndCap | BlendFace | DraftFace | SplitLeft | …
Ordinal         disambiguator among siblings with identical (Kind, SourceRef, Role)
GeoHint         cheap geometric signature: surface type, area bucket, centroid in
                feature-local coordinates, normal direction, adjacency degree
```

Example, legible on inspection: the outer cylindrical face of a fillet applied to the edge between the side wall generated by sketch line `L3` of `Extrude1` and its end cap:

```
Fillet2 / BlendFace / from( Extrude1/SideWall/from(Sketch1.L3) ∧ Extrude1/EndCap )
```

**Resolution at rebuild.** Three tiers, in order:

1. **History replay (authoritative).** Walk the rebuild's `HistoryMap` chain forward from the source entities. If the path resolves to exactly one entity, done. This handles the overwhelming majority of edits and is exact.
2. **Constrained geometric match (fallback).** If history is ambiguous (a face split into two) or broken (a feature was reordered or deleted), score candidates against `GeoHint` — surface type must match, then rank by centroid distance in feature-local space, normal agreement, and adjacency-graph similarity. Accept only above a confidence threshold with a clear margin over the runner-up.
3. **Fail loudly.** No silent wrong answer, ever. Mark the feature **in error** with a specific, human-readable message and offer a repair UI ("Reselect the missing edge for Fillet2"). A wrong-but-plausible resolution is worse than an error; it silently corrupts downstream design intent.

**Split and merge.** A named face that splits becomes a *set*. The reference records whether the feature consumed one face or a face-region, and dependent features declare their multiplicity policy (`ExactlyOne` | `AllDescendants` | `LargestDescendant`). This is where most naming bugs live; make the policy explicit at declaration time rather than inferring it.

**Testing.** `tests/regression/naming-corpus` contains scenario fixtures of the form: *build model → apply a parametric edit → assert every downstream feature resolves to the intended entity*. Categories, all mandatory: dimension change; sketch topology change (add/remove a line); feature reorder; feature suppression; feature deletion with dependents; pattern instance count change; face split by a later feature; body split; mirror; imported-geometry reference. **Every new feature type added in any later phase must add cases here.** A naming regression is a P0.

### 5.4 `OpenMCAD.Core` — document graph and rebuild engine

**Document model.** A document is a versioned, transactional object graph:

```
Document
 ├─ Parameters      named, unit-typed values; expressions over other parameters
 ├─ Features        ordered list forming a DAG by reference (not just the list order)
 ├─ Bodies          solid/sheet/wire results, each owned by a feature
 ├─ Sketches        entities + constraints + plane reference
 ├─ Reference geo   planes, axes, points, coordinate systems
 ├─ Configurations  parameter/suppression overrides (Phase 14)
 └─ Metadata        custom properties, material, mass overrides, revision
```

**Rebuild engine.**

- Build the dependency DAG from declared feature inputs (never inferred from tree order alone — tree order is a *user-facing* sequence; the DAG is the truth).
- Mark dirty on edit; propagate transitively; topologically sort the dirty subgraph.
- Execute in order. Independent branches may be prepared concurrently; kernel calls serialize on the dispatcher (ADR-0004).
- **Geometry cache** keyed by `(FeatureId, hash(resolved inputs))`. A cache hit skips the kernel entirely. This is what makes undo cheap and rollback-bar scrubbing feel instant.
- **Rollback bar** — the user can position the rebuild point anywhere in the tree. Implemented as "evaluate the DAG prefix"; falls out of the design for free if you do not special-case it.
- **Error containment.** A failed feature does not abort the rebuild. It is marked in error; downstream features that depend on it are marked *suppressed-by-error*; independent branches continue. The user sees a rebuild report, not a modal dialog.
- **Cancellation.** A rebuild superseded by a newer edit cancels at the next operation boundary. Interactive drag issues *preview* rebuilds at reduced fidelity, coalesced so at most one is in flight.

**Transactions.** Every mutation goes through `IDocumentTransaction`. Open, mutate, commit (or roll back). Commit computes the dirty set, runs rebuild, and pushes an undo record. Nothing mutates the document outside a transaction — enforced by making setters internal and the mutation API transaction-scoped.

### 5.5 Expressions and units

Underrated, and it must be in the core, not the UI (ADR-0013).

- Internal storage is **always** SI base: meters, radians, kilograms, seconds. Conversion happens only at the input/display boundary.
- A parameter holds a quantity with a **dimension** (length, angle, mass, dimensionless, …), not a bare double. `4 mm + 3 deg` is a type error, caught before evaluation.
- Full expression language: arithmetic, the usual functions, `if`, references to other parameters (`Length * 2`), cross-document references (`Chassis:Width`), and unit literals (`25.4mm`, `1in`, `45deg`).
- The dependency graph among parameters is part of the rebuild DAG; a cyclic reference is rejected at commit with the cycle named.
- Display precision, unit system (mm/inch), and angular units are per-document with a global default, and round-tripping a value through display must not perturb it.

### 5.6 `OpenMCAD.Solver` — sketch and assembly solving

**Sketch solver.** Behind `ISketchSolver`:

```csharp
SolveResult Solve(
    SketchEntitySet entities,       // points, lines, arcs, circles, ellipses, splines, conics
    ConstraintSet constraints,      // coincident, distance, angle, parallel, perpendicular,
                                    // tangent, equal, symmetric, concentric, horizontal,
                                    // vertical, midpoint, fix, radius, diameter, curve-on-curve
    DragTarget? drag,               // interactive drag: minimal-motion objective
    SolverOptions options);
```

Returned diagnosis is as important as the solution: `WellConstrained | UnderConstrained(remainingDof, freeEntities) | OverConstrained(conflictSet) | Redundant(redundantSet) | Failed(nonConverged)`. The conflict set must name the *specific* constraints, because "over-constrained" without a list is useless to a user.

Above the solver, in our code: constraint inference while drawing (auto-horizontal/vertical/tangent/coincident with visual glyphs), snapping, construction geometry, external references (project/convert edges from 3D, with a live parametric link), dimension placement and display, and the drag experience (which requires sub-16 ms solves — decompose and solve only the affected subsystem).

**Assembly solver.** The same problem in 3D over rigid bodies with six DOF each. Mates map to constraint sets on component transforms. Requirements: subassembly rigidity (a rigid subassembly is one body, a flexible one is not), grounded components, DOF display for the selected component, drag with contact-free motion, and a proper diagnosis when the mate set is inconsistent. Large assemblies need graph decomposition into independently solvable clusters, or solve time becomes quadratic.

### 5.7 `OpenMCAD.Modeling` — features

A feature is a class implementing:

```csharp
interface IFeature {
    FeatureId Id { get; }
    IReadOnlyList<IInputRef> Inputs { get; }        // typed, resolvable, named references
    ValueTask<FeatureResult> Rebuild(IRebuildContext ctx);
    void Validate(IValidationContext ctx);          // pre-flight, before touching the kernel
    FeatureSchema Schema { get; }                   // drives property UI + serialization + API
}
```

`FeatureSchema` is the key to not writing three parallel definitions of every feature. One schema declaration drives: the property-manager UI, the serialization contract, the public API surface, and the scripting binding. Adding a feature should mean writing one class and one schema, not editing seven files.

Target feature catalogue (phased in §9): extrude/cut (blind, through-all, to-face, to-body, up-to-surface, midplane, two-direction, draft, thin), revolve, sweep (with guide curves, twist, profile orientation), loft (guide curves, centerline, start/end tangency), boundary, hole wizard (standard fastener tables, counterbore/countersink/tapped, thread callouts), fillet (constant, variable, face, full-round, setback), chamfer (distance, distance-angle, vertex), shell (multi-thickness), draft (neutral plane, parting line), rib, dome, wrap, deform, patterns (linear, circular, curve-driven, sketch-driven, table-driven, fill, variable), mirror, scale, combine/boolean, split, move/copy body, delete/keep body, imported-body operations, and the direct-edit set (move face, offset face, delete face and heal, replace face).

### 5.8 Persistence

**Container** (`.ompart`, `.omasm`, `.omdrw` — Zip/OPC):

```
/manifest.json            format version, app version, doc type, created/modified, GUIDs
/document.msgpack         the versioned document graph
/geometry/<featureId>.brep   cached kernel B-rep blobs (optional, regenerable)
/tessellation/<bodyId>.mesh  cached display meshes (optional, regenerable, LOD levels)
/thumbnail.png            for Explorer/PDM/open dialog
/preview/<config>.png     per-configuration previews
/refs/external.json       external references: paths, GUIDs, last-known-good state
/custom/                  user + plugin custom property storage
```

**Versioning rules, non-negotiable from Phase 3:**

- `document.msgpack` carries a schema version. Readers handle every version ever shipped, via a chain of migrations `v(n) → v(n+1)`.
- The corpus keeps at least one fixture *saved by every released version*. CI opens all of them on every build. **Breaking an old file fails the build.**
- Unknown fields are preserved on round-trip where possible (forward-compatibility for plugin-written data).
- Geometry and tessellation caches are always regenerable — never the source of truth. A corrupt or missing cache means "rebuild", not "data loss". Add a `--no-cache` open mode and test it.

**Autosave and crash recovery** from Phase 6: transaction-log journaling to a sidecar so a crash loses at most the in-flight operation.

### 5.9 Assemblies

- **Occurrence tree vs. definition graph.** A component *definition* (a part document) is referenced once; each placement is an *occurrence* with a transform, a configuration selection, and per-occurrence overrides (visibility, appearance, suppression). Never duplicate the definition. This distinction is structural — get it wrong and large assemblies become unusable.
- **Display modes:** Resolved (full B-rep) / Lightweight (tessellation + metadata only, no B-rep loaded) / Graphics-only (tessellation, no document loaded at all). Lightweight is what makes 10,000-component assemblies open in seconds. Design for it in Phase 9 even if it fully lands in Phase 15.
- **In-context references** — a feature in part A referencing geometry in part B. These create cross-document dependencies and the potential for cycles. Required: explicit external-reference tracking, an out-of-date indicator, lock/break/unlock, and cycle detection at commit. Treat as a hazardous feature with clear UI, because it is one in every MCAD system that has it.
- **Mates:** coincident, concentric, parallel, perpendicular, tangent, distance, angle, lock, width, symmetric, path, gear, rack-pinion, cam, limit, and mechanical joints (revolute, slider, cylindrical, ball, planar, fixed).
- **Interference detection, clearance checks, motion study hooks, exploded views, BOM generation** with per-configuration quantities.

### 5.10 Rendering and picking

**Pipeline per frame:**

1. Swap to the newest `DisplaySnapshot` (immutable; reference swap only).
2. Depth pre-pass.
3. Shaded faces — instanced, GPU frustum + occlusion culling, per-body transforms in a structured buffer.
4. **ID pass** — render face/edge/vertex IDs into an R32_UINT target. Pixel-exact picking, no CPU ray casts against B-rep for hover. This is the single most important rendering decision for perceived quality: hover highlighting must be instant and exactly right.
5. Edges — polylines with depth bias and a screen-space width; silhouette edges computed per-view for curved surfaces.
6. Transparency — weighted-blended OIT for section views and ghosted assemblies.
7. Overlays — manipulators, dimensions, annotations, selection highlight, snap glyphs.
8. Post — MSAA resolve, optional SSAO, FXAA/TAA.

**Tessellation.** Adaptive by chordal deviation relative to body size, with LOD levels cached in the file. Retessellation on zoom for close inspection of curved faces, budgeted so it never stalls a frame. Coordinates are stored double and converted to float relative to a per-view origin at buffer-fill time.

**Section views, exploded views, appearances/materials, ambient occlusion, and a decent default studio environment** matter more than engineers admit — they are what makes a CAD app feel professional in the first ten seconds.

### 5.11 Drawings

The most underestimated subsystem in every plan like this. Budget accordingly.

- **View generation** by exact hidden-line removal from 3D (OCCT `HLRBRep`), producing associative 2D curve sets tagged with the 3D entity they came from — associativity is what makes dimensions survive a model change.
- View types: standard orthographic (with a projection-angle setting), isometric/trimetric, section (full, half, offset, aligned, broken-out), detail, auxiliary, crop, broken, alternate-position, exploded.
- **Performance reality:** exact HLR is slow. Cache view results in the file, invalidate on model change, and offer a draft (tessellated) mode for interactive work with exact regeneration on demand.
- **Annotation:** dimensions (linear, angular, radial, diametric, ordinate, chamfer, baseline, chain), with proper attachment to associative entities; GD&T feature control frames per ASME Y14.5 / ISO 1101; datums; surface finish; weld symbols; hole callouts; centerlines and center marks; balloons; revision clouds and tables.
- **Tables:** BOM (with configuration and quantity rules), hole tables, revision tables, general tables, weldment cut lists.
- **Sheets:** templates, formats, title blocks driven by document properties, multi-sheet documents, layers, line styles/weights.
- **Output:** PDF (vector), DXF/DWG, print with true scale.

### 5.12 Extensibility

Establish in Phase 2 — retrofitting an API onto a mature codebase means either breaking everyone or freezing bad design.

- `OpenMCAD.Api` is a separate assembly with a **semver contract** and an API-surface baseline file checked in CI. Any change to the public surface must update the baseline; removals require a major version.
- Plugins load into isolated `AssemblyLoadContext`s with a defined shared-type set, so plugin dependency versions do not collide with the host's.
- Surface: document read/write, feature creation, custom feature types, custom properties, commands and ribbon contributions, selection and picking, event hooks (pre/post rebuild, save, open), and geometry queries. **Not** raw kernel handles — plugins get our abstraction, so a future kernel swap does not break the ecosystem.
- A scripting host (Phase 16) over the same API. Same surface, same versioning.
---

## 6. Cross-cutting concerns

### 6.1 Error handling and diagnostics

CAD users forgive failures; they do not forgive failures they cannot understand or work around.

- **Three failure classes.** *User-actionable* ("radius too large for available material at edge E4") → inline, specific, with a suggested fix. *Recoverable-internal* (kernel operation failed after the full retry ladder) → feature marked in error, rebuild continues, diagnostic captured. *Fatal* (native crash, corrupt state) → crash handler, autosave recovery, minidump.
- **No modal error dialogs during rebuild.** A rebuild produces a *report* — a list of errors and warnings with jump-to-feature links.
- **Every kernel failure captures a repro bundle** (behind a setting): the input shapes as BREP, the operation and parameters, the tolerance, and the OCCT exception. This turns a bug report into a regression fixture in one step, and is how you build a robustness corpus faster than your users find bugs.
- Structured logging throughout: rebuild traces log per-feature duration, kernel rung reached, name resolution tier used, and cache hit/miss.

### 6.2 Crash resilience

Native code crashes. Plan for it rather than pretending.

- Global unhandled-exception + SEH handler writing a minidump and a session log.
- Journaling autosave (§5.8) so recovery loses at most the in-flight operation.
- **Consider, from Phase 15:** running the kernel in an out-of-process host so a native crash loses the operation, not the session. Expensive (all shape traffic becomes IPC) and only worth it if crash telemetry justifies it. The `KernelDispatcher` boundary (ADR-0004) is deliberately shaped so this remains possible without touching callers.

### 6.3 Telemetry and privacy

Opt-in, off by default, clearly disclosed. Worth collecting: operation success rates by retry rung, rebuild durations by model complexity, naming-resolution tier distribution, crash reports, feature usage counts. Never collect model geometry or file contents. Every telemetry field is enumerated in a checked-in schema so "what do you send?" has an exact answer.

### 6.4 Accessibility, localization, HiDPI

Deal with these early or pay double later.

- **HiDPI and multi-monitor DPI changes from Phase 2.** The viewport must handle per-monitor DPI v2 correctly; retrofitting this into a D3D swapchain is unpleasant.
- All user-facing strings in resource files from Phase 6. No string literals in ViewModels. Enforced by an analyzer.
- Keyboard navigability of the full command set; screen-reader labels on the tree, property manager, and dialogs. The viewport itself is not accessible, but everything around it must be.
- Configurable keyboard shortcuts and mouse gestures with importable profiles — including a SolidWorks-compatible profile, which is a serious adoption lever.

---

## 7. Performance budgets

Treat these as tests, not aspirations. `tests/perf` fails the build on regression beyond a tolerance.

| Scenario | Budget |
|---|---|
| Application cold start to usable window | < 2.5 s |
| Open a 100-feature part (cached geometry) | < 1.5 s |
| Open a 5,000-component assembly (lightweight) | < 15 s |
| Rebuild a 100-feature part after a leaf dimension change | < 400 ms |
| Full rebuild, 100-feature part, cold cache | < 8 s |
| Sketch solve during drag, 200 entities | < 16 ms per frame |
| Viewport frame time, 2M triangles, rotating | < 16 ms |
| Hover-highlight latency (ID-buffer pick) | < 1 frame |
| Selection of a face in a 5,000-component assembly | < 50 ms |
| Drawing view regeneration, exact HLR, moderate part | < 3 s |
| Undo of a leaf parameter change | < 200 ms |
| Save a 100-feature part with caches | < 1 s |
| Memory, 5,000-component assembly, lightweight | < 4 GB |

Two rules that make these achievable rather than aspirational: **the viewport never waits on the kernel** (§4.2), and **rebuild is scoped to the dirty subgraph** (§5.4). Any design that violates either is wrong regardless of how it benchmarks in isolation.

---

## 8. Testing and quality strategy

Skipping this section is how projects like this die. The modeling code is not what kills them — the absence of a regression corpus is.

### 8.1 Test categories

| Layer | What | Runtime target |
|---|---|---|
| **Unit** | Every `src` project against `FakeKernel`. Naming, DAG, expressions, transactions, serialization, VM logic. | < 30 s whole suite |
| **Kernel contract** | Same test battery run against both `FakeKernel` and `OcctKernel` to prove the abstraction holds. | < 5 min |
| **Integration** | Build a document programmatically, rebuild, save, reopen, assert identical. | < 10 min |
| **Regression corpus** | Golden-file assertions over a growing fixture library. See 8.2. | nightly, < 2 h |
| **Fuzz** | Randomized boolean/fillet/sketch inputs; assert no crash, no invalid shape, no non-determinism. | nightly, time-boxed |
| **Determinism** | Rebuild the corpus twice; diff topology and names. Any difference is P0. | nightly |
| **Performance** | BenchmarkDotNet + scale scenarios against §7 budgets. | nightly |
| **Architecture** | NetArchTest: layering, no WPF in ViewModels, no OCCT types above `OpenMCAD.Kernel`, no kernel calls off the dispatcher. | in unit run |
| **API surface** | Public surface diffed against a checked-in baseline. | in unit run |
| **UI smoke** | Automated launch, open each sample, exercise the main commands, screenshot-compare. | per PR |

### 8.2 The regression corpus

Begins in **Phase 1** with three models and grows every phase. Structure:

```
tests/regression/corpus/<category>/<name>/
  model.ompart              the fixture (git-lfs)
  expected.json              golden values
  scenario.json              optional: edits to apply, assertions after each
```

`expected.json` captures: volume, surface area, centroid, moments of inertia (to a stated tolerance); face/edge/vertex counts; the resolved persistent-name map; per-feature rebuild status; and a hash of the tessellation at a fixed tolerance.

Mandatory categories, each populated as the corresponding phase lands:

- `basic/` — primitives, single-feature parts
- `naming/` — the scenarios in §5.3, the most important directory in the repo
- `boolean/` — tangency, coincident faces, near-degenerate, many-body
- `blend/` — fillet chains, variable radius, setbacks, blends that should legitimately fail
- `sketch/` — solver convergence, over/under-constrained diagnosis, drag stability
- `assembly/` — mates, subassemblies, in-context, patterns, interference
- `drawing/` — HLR correctness, section views, annotation associativity
- `exchange/` — STEP/IGES round-trips, including deliberately malformed input
- `format/` — one file saved by every released version, opened on every build
- `pathological/` — real-world files that once broke us; every fixed bug adds one here

**Rule: every bug fix ships with a corpus fixture that reproduces it.** No exceptions. This is the mechanism by which the product gets more robust over years rather than oscillating.

### 8.3 Verification of geometric correctness

Golden mass properties are necessary but not sufficient. Also assert:

- **Shape validity** — run OCCT's `BRepCheck_Analyzer` on every result in tests; an invalid shape is a failure even if it looks right.
- **Watertightness** for solids; correct orientation of shells.
- **Cross-check volume** two ways where feasible (kernel mass properties vs. divergence-theorem integration over the tessellation, within tessellation tolerance). Catches a whole class of subtle topology bugs.
- **STEP round-trip** — export, reimport, compare mass properties and topology counts.

### 8.4 CI pipeline

- **Per PR:** build native shims (cached vcpkg), build managed, unit + kernel-contract + architecture + API-surface + UI smoke. Target under 15 minutes.
- **Nightly:** full regression corpus, fuzz, determinism, performance, and a packaged installer smoke-install in a clean VM image.
- **Weekly:** long fuzz soak; corpus reopened with `--no-cache` to prove rebuild-from-scratch fidelity.
- Native build via CMake + vcpkg manifest with pinned versions; the OCCT build is cached aggressively because it is slow.
- All builds produce a versioned artifact; `main` is always installable.

### 8.5 Definition of done (per task)

A task is complete when: the code is written; unit tests cover the new logic; corpus fixtures are added if it touches geometry, naming, or the file format; the perf budget is unaffected (or the budget is deliberately revised); public API changes update the baseline; the docs in `docs/specs/` reflect any design change; and CI is green.

### 8.6 Licensing and compliance

Not optional, and cheap to handle correctly if handled early.

- **OCCT** is LGPL-2.1 with the Open CASCADE Exception, which permits linking into proprietary applications. Keep OCCT in a *separately replaceable* dynamic library (which the `openmcad_occt.dll` design already gives you), ship the license text, state the version used, and provide the means to relink. Do not statically fold OCCT into a monolith without reading the exception's conditions carefully with counsel.
- **planegcs / FreeCAD** is LGPL-2.1. Same treatment: separate `openmcad_gcs.dll`, license text shipped, version stated.
- **Eigen** is MPL2 (with some LGPL-licensed optional components — exclude them via the appropriate build flags).
- Maintain `THIRD-PARTY-NOTICES.md`, generated from the vcpkg manifest and NuGet lock file by a CI step, so it cannot drift.
- If a Parasolid migration is ever pursued, note that it inverts these constraints entirely — nothing can be open, and the abstraction layer becomes the thing that keeps the open parts open.
- **Get a lawyer to review the license posture before first public release**, not after. This plan is engineering guidance, not legal advice.
---

## 9. Phased roadmap

Four epochs, eighteen phases. Each phase states a **goal**, **exit criteria** (mechanically checkable), and a **task list** with stable IDs.

| Epoch | Phases | Theme |
|---|---|---|
| **A — Foundations** | 0–5 | Prove the architecture end to end |
| **B — Core product** | 6–11 | Become a usable MCAD application |
| **C — Advanced domains** | 12–14 | Specialized environments |
| **D — Scale & ship** | 15–17 | Performance, ecosystem, release |

---

# EPOCH A — FOUNDATIONS

*Goal of the epoch: an end-to-end vertical slice that proves every hard architectural interaction, on a codebase that is a pleasure to extend.*

---

### Phase 0 — Repository, tooling, and ground rules

**Goal.** A repo where every subsequent phase is cheap to start. Nothing here is glamorous; all of it compounds.

**Effort:** 2–4 engineer-weeks.

**Exit criteria.**

- `git clone && ./build.ps1` produces a running (empty) WPF window on a clean Windows machine with only the .NET 10 SDK and Visual Studio Build Tools installed.
- CI builds the native shim skeleton and all managed projects, runs the (trivial) test suite, and publishes an artifact.
- Architecture tests exist and pass, even though there is almost nothing to test yet.

**Tasks.**

- [x] **P0-T01** Initialize the repo, `.gitignore`, `.gitattributes` (git-lfs for corpus binaries), `LICENSE`, `README.md`. *(`LICENSE` is an explicit placeholder: the choice is open and is the owner's to make. See ADR-0017.)*
- [x] **P0-T02** `global.json` pinning the .NET 10 SDK; `Directory.Build.props` with `net10.0`, C# 14, `Nullable=enable`, `TreatWarningsAsErrors=true`, deterministic builds, source link.
- [x] **P0-T03** `Directory.Packages.props` for central package management; `.editorconfig` with the full analyzer ruleset.
- [x] **P0-T04** Create the solution and all `src/` project skeletons per §4.3 with correct project references encoding the layering.
- [x] **P0-T05** `tests/arch` with NetArchTest rules: layering, no WPF types in `OpenMCAD.ViewModels`, no OCCT types outside `OpenMCAD.Kernel.Occt`.
- [x] **P0-T06** vcpkg manifest pinning OCCT and Eigen; CMake skeleton for `native/openmcad_occt` producing a DLL with a single `openmcad_version()` export. *(Built in CI. The vcpkg `builtin-baseline` is a placeholder pending the OCCT spike required by section 14; `OPENMCAD_WITH_OCCT` stays off until P1-T06.)*
- [x] **P0-T07** `build.ps1` orchestrating native → managed → test, with a vcpkg binary cache.
- [x] **P0-T08** `ci.yml`: restore, native build (cached), managed build, unit tests, architecture tests, artifact publish.
- [x] **P0-T09** Serilog + `Microsoft.Extensions.Logging` wiring; DI container bootstrap in `OpenMCAD.App`.
- [x] **P0-T10** Minimal `OpenMCAD.Shell` WPF app: main window, docking library evaluated and chosen, ribbon library evaluated and chosen, empty viewport placeholder. **Record the choice in an ADR.** *(AvalonDock + Fluent.Ribbon; ADR-0015.)*
- [x] **P0-T11** `OpenMCAD.Cli` skeleton with `--version`; this is the headless entry point every later test harness uses.
- [x] **P0-T12** Write the full ADR files in `docs/adr/` from §3; commit `PLAN.md` to `docs/`.
- [x] **P0-T13** `OpenMCAD.Math`: double-precision `Vec2d`, `Vec3d`, `Mat4d`, `Quatd`, `Plane`, `Bounds3d`, `Transform`, with tests. Do not use `System.Numerics` for geometry.
- [x] **P0-T14** Decide and document the versioning scheme (semver + build metadata) and wire it into assembly attributes.

---

### Phase 1 — Kernel spine

**Goal.** C# can drive OCCT through a clean, safe, deterministic, single-threaded boundary. This phase is where the riskiest technical assumptions get validated; do not rush it.

**Effort:** 6–10 engineer-weeks.

**Exit criteria.**

- `OpenMCAD.Cli kernel-smoke` builds a box, a cylinder, subtracts them, fillets the result, computes mass properties, tessellates, writes STEP — all through `IGeometryKernel`, with no OCCT type visible above `OpenMCAD.Kernel.Occt`.
- The same test battery passes against `FakeKernel` and `OcctKernel`.
- A debug assertion fires if any kernel call is made off the dispatcher thread.
- Running the smoke suite twice produces byte-identical topology and name output (determinism gate).
- Three corpus fixtures exist and are asserted nightly.

**Tasks.**

- [x] **P1-T01** Define `IGeometryKernel` with the Phase 1 operation subset: primitives, extrude, revolve, boolean, fillet, chamfer, mass properties, bounding box, triangulate, BREP read/write, STEP write.
- [x] **P1-T02** Define `KernelShape`, `SubEntity`, `HistoryMap`, `OperationRole`, `OperationResult` (`Success`/`Failed`/`Degraded`) per §5.1.
- [x] **P1-T03** Design `kernel.api.json` IDL schema; write `tools/idlgen` emitting the C header, the C dispatch layer, and `[LibraryImport]` C# bindings. *(49 operations; five artefacts, checked in and verified fresh on every build. The generator is what makes a surface change cheap, so building it before the §14 review lowers the cost of that review rather than raising it.)*
- [x] **P1-T04** Implement the shim handle table: `uint64` tags with generation counters, stale-tag detection, reference counting, explicit release. *(Slots live in a `std::deque`, not a `std::vector`: operations hold a resolved `const TopoDS_Shape&` across the `store()` that saves their result, and vector growth turned that into an access violation inside the fillet. Sub-entity identity is keyed by OCCT's shape hasher rather than the raw `TShape` pointer — OCCT shares a `TShape` between an entity and its relocated copy, which silently aliased an extrusion's nine top entities onto its bottom ones.)*
- [x] **P1-T05** Implement the exception firewall: catch `Standard_Failure`, `std::exception`, `...`; thread-local diagnostic record; `openmcad_last_error()`. Call `OSD::SetSignal` at init. *(The shim is compiled `/EHa`, not `/EHsc`, and that is the load-bearing half: `OSD::SetSignal` installs a structured-exception translator that MSVC only runs under `/EHa`. With `/EHsc` the handlers were installed and inert — a fillet with an impossible radius took the whole test host down with `0xC0000005`. It now returns a diagnosable failure.)*
- [x] **P1-T06** Implement the Phase 1 operations in the shim, each returning a populated `HistoryMap` with deliberate `OperationRole` values. **A missing or `Unknown` role fails review.** *(All 39 generated entry points implemented; `not_yet_implemented.cpp` is gone. Every operation maps reported provenance, then sweeps for retained inputs, then sweeps for created outputs — in that order, with the created-sweep deferred until every entity kind is mapped, because generation crosses kinds and an early sweep claimed the fillet's blend face as an unexplained corner face. Role assignment is first-wins so the specific name beats the general one.)*
- [x] **P1-T07** `SafeHandle`-derived C# shape wrapper with deterministic release marshalled to the kernel thread.
- [x] **P1-T08** `KernelDispatcher`: dedicated thread, priority queue (interactive > rebuild > background), cancellation at operation boundaries, per-call instrumentation, off-thread debug assertion.
- [x] **P1-T09** `OpenMCAD.Kernel.Fake`: deterministic mock implementing the full interface with simple analytic geometry and a synthetic but *realistic* history map. This is a first-class deliverable, not a stub.
- [x] **P1-T10** Kernel contract test suite, parameterized over both implementations. *(`OcctKernel` is in the factory list, so the same battery runs against the real kernel. It immediately earned its keep: it found that `TessellationOptions.Display` was written `new()`, which on a record struct zeroes every field instead of running the primary constructor, so the display preset had asked for zero chordal deviation all along — `FakeKernel` ignores the deviation, so nothing else could have caught it.)*
- [x] **P1-T11** Implement the retry ladder (§5.2.4) for boolean and fillet, with per-rung logging and the health metric. *(Booleans climb model tolerance → conditioned inputs → relaxed tolerance; blends climb model tolerance → conditioned inputs → edge-by-edge, returning `Degraded` naming the edges it had to skip. Rung 3 is deliberately absent for blends: a fuzzy value is a boolean concept and `BRepFilletAPI` has no equivalent. Conditioning runs on a deep copy and carries a correspondence back to the caller's entities — `ShapeFix` raises sub-shape tolerances in place, and without the correspondence a rung-2 result reports an empty history and every entity in it looks newly created. Each rung that fires is logged; the corpus runner prints the distribution and the first-rung success rate.)*
- [x] **P1-T12** Determinism audit: pin OCCT build flags, disable nondeterministic parallelism, sort all returned collections by a stable geometric key. Add the double-rebuild diff test. *(Written up in `docs/notes/determinism-audit.md`. The live finding was that nothing was ordered geometrically: every ordered view of a `HistoryMap` sorted `SubEntity`, which sorts by tag, and a tag carries a slot generation — so two identical models enumerated differently depending only on what had been allocated before them. `HistoryMap` now preserves the kernel's reported order throughout, and `enumerate_canonical` is the single authority. The double-rebuild gate runs nightly against both kernels; its signature now includes an ordered digest, because the histogram it compared before was order-blind and could not have caught the defect above.)*
- [x] **P1-T13** Repro-bundle capture: on kernel failure, serialize inputs + operation + parameters to a bundle directory. *(Bundles are named by content hash, so a rebuild loop that fails identically two hundred times produces one bundle and a recurrence count.)*
- [x] **P1-T14** `tests/regression` runner and the first three corpus fixtures; wire into `nightly-regression.yml`. *(Runner, three `basic/` fixtures, and the determinism gate. Runs on every build against `FakeKernel`; nightly replays the same fixtures against both kernels. All three pass against OCCT on golden values written against `FakeKernel` — agreeing to 1e-12 on volume, area and centroid, and exactly on the topology and role histograms. That is ADR-0002's abstraction demonstrating itself rather than being asserted.)*
- [x] **P1-T15** Benchmark harness for kernel operations; record a baseline. *(BenchmarkDotNet in `tests/perf`, parameterised over both kernels, measuring each operation including its history map because that is what a rebuild pays. Baseline in `docs/notes/kernel-baseline.md`. The §7 rebuild budget is met with roughly a factor of six in hand at this scale; the slowest operation is a four-edge fillet at 13.6 ms. Recording it turned up a real defect: Debug and Release were installing the native closure into one directory, so a Release benchmark was loading a Debug OCCT and reporting a box at 6 ms rather than 621 µs.)*
- [x] **P1-T16** Document the shim extension procedure in `docs/specs/kernel-shim.md` — every later phase adds operations, and this must be a 30-minute task, not an archaeology expedition.

---

### Phase 2 — Viewport spine

**Goal.** A D3D12 viewport that displays kernel output, navigates well, and picks pixel-exactly. Everything the user will ever *feel* about this product starts here.

**Effort:** 6–10 engineer-weeks.

**Exit criteria.**

- A tessellated body renders shaded with edges at 60 fps while orbiting; 2M triangles hold the frame budget.
- Hovering a face highlights it within one frame via the ID buffer; clicking selects it and reports its `SubEntity`.
- Per-monitor DPI changes and window resizes are handled without artifacts or leaks.
- The viewport continues rendering at full rate while a synthetic 10-second kernel operation runs.

*Where each is checked.* The frame budget is `OpenMCAD.Render.Perf` and `docs/notes/viewport-baseline.md`
(2M triangles at 3.51 ms against 16). Picking is `PickTests` and `IdPassTests`. Scaling and resize are
`ViewportScalingTests` and `MsaaTargetTests`, and device loss is `DeviceLossTests`. The fourth was the
one with nothing behind it, and is now `ViewportResponsivenessTests` plus a NetArchTest rule that the
render layer cannot reference `OpenMCAD.Kernel.Threading` — which is what makes it structural rather
than a property of nobody having made the call yet. The synthetic operation blocks the kernel thread
rather than burning processor time: the failure being guarded against is a lock or a synchronous
marshal, and modelling it as processor contention would only measure how many cores the machine has.

**Tasks.**

- [x] **P2-T01** `IRenderDevice` thin RHI abstraction; D3D12 implementation via Vortice: device, swapchain, descriptor heaps, fence-paced upload ring. *(Adapter selection prefers high performance and falls back to WARP, which is also what the headless tests run on. The upload ring is a true ring with per-frame reclamation; two bugs in its full-versus-empty handling were found by tests rather than by rendering.)*
- [x] **P2-T02** `HwndHost`-based viewport control; per-monitor DPI v2; resize, occlusion, device-loss recovery. *(Recovery rebuilds through the same code path as start-up, so the two cannot drift — a separate recovery routine runs perhaps once a year on somebody else's machine and silently falls behind every new piece of viewport state. The camera is carried across rather than rebuilt, because it is the one piece that is not a GPU resource and a view snapping back to default on a driver update would be a worse failure than the one being recovered from. Attempts are counted within a five-minute window and give up after three, since a genuinely broken device fails again immediately and retrying inside the frame loop would spin the machine. `ID3D12Device5.RemoveDevice` makes the whole thing testable rather than merely reasoned about — and doing so established that submitting work to a removed device does **not** fail: recording, executing and waiting on a fence all report success while nothing happens, because a removed device signals every fence to its maximum. A viewport notices via the present; anything rendering off-screen has to ask, which is why `D3D12RenderDevice.IsRemoved` exists and why the perf harness checks it.)*
- [x] **P2-T03** `DisplaySnapshot`: immutable render-side model (mesh buffers, edge polylines, per-entity ID map, transforms, appearance). Atomic reference swap; no locks (§4.2). *(`SnapshotHolder` is the one place the swap happens, so "no lock between rebuild and render" is enforced rather than hoped for. It rejects a snapshot older than the one held: rebuilds run concurrently where the graph allows, so two can finish out of order and a plain assignment would leave the viewport showing a superseded scene. Appearance is not modelled yet — nothing produces it until P2-T12.)*
- [ ] **P2-T04** Tessellation pipeline: kernel triangulation → GPU buffers, double→float relative to a per-view origin, per-face ID attribution, LOD levels. *(Everything but LOD. `SnapshotBuilder` does the double→float conversion against a sticky render origin and the per-face attribution; rounding the origin to a grid was not enough on its own, because a scene centred on a grid line flips between two origins on a millimetre edit and re-uploads every buffer, so the origin is carried forward until the scene genuinely drifts. GPU buffers landed with P2-T05. LOD is not started and P2-T13's numbers do not justify it: nothing measured is limited by triangle throughput, so reducing triangle counts would buy little. It becomes interesting when a model exceeds what memory can hold, which is a different problem from frame time.)*
- [x] **P2-T05** Shaded face pass with instancing and GPU frustum culling. *(The pass is done and verified by reading pixels back on WARP: two-sided shading because CAD models are routinely viewed from inside, D32_Float depth because a mechanical scene outruns 24-bit precision, and a facet normal reconstructed from derivatives when a mesh carries none. Culling is per body on the CPU, which is where most of the saving is in an assembly; instancing and a GPU culling pass are not started and want P2-T13's harness to justify them. The tests caught the lighting: an isometric view of a cube shades all three visible faces identically under a pure headlight, so the key light is offset.)*
- [x] **P2-T06** Edge rendering: depth-biased polylines, screen-space width, silhouette edges for curved surfaces. *(Everything but silhouettes. Edges are expanded to screen-space quads rather than drawn as line primitives, which are stuck at one pixel and cannot be anti-aliased; the quad also gives somewhere to put a coverage ramp. Depth bias and near-plane clipping each have a test that fails when the fix is removed rather than merely present. Silhouettes on curved surfaces are a property of the view rather than of the model and have to be found per frame — the cylinder shows its end circles and its seam, but not the lines where its wall turns away.)*
- [x] **P2-T07** ID pass into R32_UINT; readback path with a staged, latency-hiding queue; face/edge/vertex resolution with an edge/vertex proximity bias so thin entities are pickable. *(Faces and edges only. The ID pass shares its vertex shaders with the visible passes byte for byte, so what is picked is what is drawn. Readback never blocks: a pick is tagged with the fence value that will retire it and collected frames later, and a request arriving with every slot busy is dropped rather than queued so a drag cannot build a backlog of stale positions. Vertices rank above edges and are missing for an upstream reason: the kernel's mesh reports faces and edges as entities but not vertices, so there is nothing to give an id to.)*
- [x] **P2-T08** Camera and navigation: orbit, pan, zoom, zoom-to-fit, zoom-to-selection, standard views, view cube, perspective/orthographic toggle, configurable mouse profiles. *(A corner orientation gizmo rather than a clickable view cube. The gizmo reports the camera's rotation and deliberately ignores pan and zoom — there are tests for what it must not follow as well as what it must, since a gizmo that drifted while panning would be actively misleading about the one thing it exists to report. A cube with clickable labelled faces additionally needs text rendering, which does not exist yet and arrives with dimensions and annotations in Phase 6. Mouse profiles are a rebindable table with presets modelled on SolidWorks, Fusion and Onshape; modifiers match exactly, so a modified drag cannot silently fall back to the unmodified gesture. The wheel zooms towards the pointer. Two direction tests were written against points on the silhouette, which are turning points of the projection, and passed with the drag deliberately inverted until rewritten.)*
- [x] **P2-T09** Selection and highlight rendering: hover, selected, pre-selected, error states; selection sets in `OpenMCAD.Interaction`. *(States travel to the GPU as one array indexed by the same display id the ID pass writes, so a highlight costs no extra draw and no extra geometry — the shaded pass simply tints what it was already drawing. Faces are tinted rather than replaced, because a flat-filled selection stops reading as a shape, and edges take the colour outright because a hairline has no shading to protect. Selection holds `SubEntity` rather than `DisplayId`: ids are snapshot-scoped and would migrate to whatever entity inherited the number. That still does not survive a rebuild that renumbers topology, which needs the persistent naming of 5.3. Pre-selection is kept apart from selection so hover cannot destroy what the user has chosen, and error outranks both. The state buffer is bound as a root descriptor, which carries an address and no length — `GetDimensions` on one is meaningless and reading past the end was an access violation in the test host, so the count travels in the frame constants.)*
- [x] **P2-T10** Weighted-blended OIT for transparency. *(Sorting back to front fails on exactly what CAD produces — a housing containing its own contents, two parts interpenetrating, a body whose faces overlap from the current angle. Sorting is per object and the failure is per pixel, so no ordering of objects fixes an object that overlaps itself; the symptom is faces popping in front of one another during an orbit, which reads as the model changing. Weighted blending accumulates with a depth-dependent weight and keeps the product of what each fragment let through — both commutative, so order cannot matter. It is an approximation, but a uniform and stable one, which beats occasional exactness that flips as the camera moves. The test that matters renders two overlapping bodies both ways round and requires the same pixel, having first checked both actually reach it. Opacity is a single figure for the scene because a `DisplaySnapshot` carries geometry and identity but no appearance; per-body materials arrive with the document model.)*
- [x] **P2-T11** Grid, origin triad, reference plane display, background gradient/environment. *(All but reference planes, which want the transparency of P2-T10 to look like planes rather than like walls. The grid is computed per pixel from a ray-plane intersection rather than drawn as lines: line geometry needs an extent and a spacing fixed in advance, and a CAD user zooms across six orders of magnitude in a session. Spacing snaps to a power of ten below a tenth of the scene and is taken from the scene rather than the camera, because a reference that changes density as you zoom is worse than one at the wrong scale. The triad is depth-tested and the gizmo is not, which is the difference between a landmark and an overlay — the eye reads 'drawn over a solid' as 'in front of the solid' and no colour choice argues it out of that.)*
- [x] **P2-T12** MSAA, FXAA, optional SSAO; a defensible default material and lighting setup. *(MSAA done at four samples, negotiated with the device and falling back through two to one. The whole scene is drawn multisampled and resolved into the back buffer, rather than filtered afterwards: the aliasing that matters in CAD is on geometric silhouettes, and a post-process works from the finished image and can only guess where an edge was — softening text and fine detail while never quite fixing the staircase. The ID buffer is deliberately left single-sampled, since resolving indices would average them into a number naming an entity that is under the cursor nowhere. FXAA is therefore unlikely to be worth adding. SSAO is done, from the depth buffer alone rather than from a normal target: the renderer is forward-shaded, and adding a G-buffer to carry normals would cost more bandwidth every frame than the whole pass costs. It measures at well under a millisecond at 1920x1080 and is what makes a pocket or an inside corner readable, since a directional light gives those almost the same shade as the surface around them. Three defects worth recording, none of which any D3D12 call reported: Vortice follows the CD3DX12 convention where `Offset` mutates the handle it is called on, so four chained calls wrote descriptors to slots 0, 1, 3 and 6 of a four-slot heap and the apply pass silently read the depth buffer as its occlusion; the normal reconstructed as `cross(right, down)` faces away from the camera in this right-handed view space, which flipped the sampling hemisphere into the solid and darkened the model almost to black; and the range cutoff is what stops a foreground object shadowing the distant background behind it. Each is now the subject of a test that fails when it is reintroduced. The default material is now a type rather than four literals inside a shader expression, pushed as root constants alongside the body colour and shared by the shaded and transparent paths. Its numbers are held to one rule that can be stated and tested: ambient plus diffuse must not exceed one, so no surface is ever drawn brighter than its own colour and the highlight is the only thing that can go above it. That is not decoration -- the numbers this shader carried before totalled 1.25, which blows out about 2,300 pixels of a white cube to pure white and throws away the shading they were carrying, and there is a test that renders both materials to show it. The lighting remains the offset headlight from P2-T05 plus the hemisphere fill, which is a defensible rig for a viewport: a second directional fill on top of a hemisphere ambient would be redundant.)*
- [x] **P2-T13** Perf harness: synthetic scenes at 100k / 1M / 5M triangles and 1k / 10k instances, against the frame budget. *(`tests/perf/OpenMCAD.Render.Perf`, with the first numbers in `docs/notes/viewport-baseline.md`. The budget — 2M triangles rotating under 16 ms — is met at 3.36 ms on integrated graphics. The useful finding is the shape rather than the headline: triangle count is not the constraint, body count is. 5M triangles across 64 bodies costs 7.5 ms; 2M across ten thousand costs 10.7 ms, because each body is a draw in the face pass and another in the edge pass. Where wall time and GPU time diverge is the CPU recording and the driver validating, which is what says the next optimisation would be batching rather than geometry. Every frame is fenced, and the device is warmed for ninety frames first — without that the first scene measured the laptop's power governor ramping and reported four times its true cost.)*
- [x] **P2-T14** `OpenMCAD.Api` skeleton with the API-surface baseline tooling in CI (**establish now**, per §5.12). *(`Microsoft.CodeAnalysis.PublicApiAnalyzers` against a checked-in `PublicAPI.Unshipped.txt`, applied to `OpenMCAD.Api` alone — making every assembly track a baseline would be ceremony that teaches people to ignore the errors. Verified in both directions: an unrecorded public member fails the build (RS0016) and a removed one fails it too (RS0017). The surface is deliberately tiny: `ApiVersion` and the plugin contract P2-T15 needs. §5.12's full list — documents, features, commands, event hooks — is not declared, because designing it against an implementation that does not exist is how you end up choosing between breaking plugins and freezing bad design, which is the trade §5.12 opens by naming. `LayerInfo` was made internal here so a marker type does not become a permanent, unremovable member of the plugin surface.)*
- [x] **P2-T15** `AssemblyLoadContext` plugin loader + a hello-world sample plugin that adds a ribbon button. *(The loader was already done; what was missing was an API a plugin could use. `IPluginHost` gains a command registry — the first capability on a surface deliberately left almost empty — taking descriptions of commands rather than controls, so a plugin never names a UI framework and the shell stays free to present the same command as a button, a palette entry and a shortcut. Ids are namespaced and claimed first-come, because two plugins offering `export` is not hypothetical and letting the second overwrite the first would make one plugin's buttons vanish depending on directory enumeration order. Registration closes when loading finishes. Contributions land on their own Add-Ins tab, so a plugin can never displace a built-in command, and the tab is hidden when nothing contributed. A command that throws is caught and attributed to its plugin rather than taking the application down. The sample is the existing loader-test fixture rather than a separate project: it has to be separately compiled to prove isolation, which makes it both the worked example and the thing the tests load.)*

---

### Phase 3 — Document spine

**Goal.** The parametric core: DAG, rebuild, naming, expressions, transactions, undo, persistence. The most intellectually demanding phase and the one that determines whether the product is real.

**Effort:** 10–16 engineer-weeks.

**Exit criteria.**

- A document built programmatically from a chain of ten features rebuilds correctly, saves, reopens, and is bit-identical on re-save.
- Changing a parameter rebuilds only the dirty subgraph, verified by instrumentation.
- The naming corpus passes every scenario category in §5.3.
- Undo/redo across 100 mixed operations returns the document to an identical state (asserted by full graph comparison).
- Opening a fixture with `--no-cache` produces identical results to opening it with caches.

**Tasks.**

- [x] **P3-T01** `Document`, `Feature`, `Body`, `Parameter`, `ReferenceGeometry` core types; `FeatureId`/`BodyId` as GUID-backed strong IDs. *(One deviation from 5.4, stated here rather than buried: 5.4 says nothing mutates outside a transaction and names internal setters as the mechanism. `Document` is immutable instead, with `internal` `With…` methods -- the same enforcement, since only this assembly can produce a new version, but strictly stronger, because holding a reference no longer lets anyone alter it. That makes undo a matter of holding an earlier reference rather than replaying inverses (P3-T17), lets a rebuild read a document that cannot change underneath it, and makes the fourth exit criterion -- identical after undo, by full graph comparison -- something the type can answer itself. The collections share structure, so an edit copies a spine of pointers. `Quantity` and `Dimension` exist here because 5.5 requires a parameter to hold a dimensioned value rather than a double, and building `Parameter` on a double would mean every caller written before P3-T14 is written against the wrong type; the algebra and the parser are still T14 and T15. Inputs are declared per feature and never inferred from tree order, so P3-T03 has a real edge set to build from. Neither id implements `IComparable`, deliberately: the values are random, so any ordering by them is stable in one process and meaningless between two.)*
- [x] **P3-T02** `IDocumentTransaction`: open/mutate/commit/rollback; internal setters so nothing mutates outside a transaction. *(`DocumentSession` holds which document is current; the document itself is a value, so a rebuild can read one for as long as it likes while editing continues -- it holds something nobody can alter rather than a lock on what everyone needs. Rollback is free: edits go to a private working reference, so abandoning one drops a pointer and a sequence that throws halfway leaves no trace, without the transaction knowing how to reverse what already succeeded. One transaction at a time, rejected at open rather than at commit, because two starting from the same state have no correct merge and failing at open still has the stack that explains it. Commit reports the features and parameters touched -- recorded as edits happen rather than diffed at commit, since a diff would have to guess intent and a feature removed then re-added looks untouched. Bodies deliberately do not seed: a body is the result of a rebuild, not a cause of one, and seeding on it would not terminate. The `Committed` event is the seam P3-T04 and P3-T17 attach to, raised outside the lock and after the swap, so a handler can read the session and open its own transaction -- which the rebuild engine must do to write back what it produced. There is a test for that re-entrancy, verified by announcing before releasing the slot and watching it fail.)*
- [x] **P3-T03** Dependency DAG construction from declared feature inputs; cycle detection with a named cycle in the error. *(`FeatureGraph.Build` walks the declared inputs and orders with Kahn's algorithm. The interesting decision is the tie-break: a topological order is not unique, so when several features are ready any of them would be correct, and that freedom is spent on reproducibility -- ties go to position in the tree, which is stable and survives a save, never to the id, which is random and would order differently in the next process. Without that the same document rebuilds in a different order each run and a cache key, a regression baseline and a bug report all stop meaning anything. Cycles name only the loop, not everything downstream of it: Kahn reports the leftovers, which are a superset, so a depth-first walk of just those finds a real back edge -- otherwise the two features the user has to fix are buried under the twenty the loop spoiled. A self-referencing feature is a one-element loop and reports as one. Dangling inputs are reported rather than thrown, because deleting a consumed feature is normal and refusing to build the graph would leave the document unopenable with no way to see what to fix; P3-T07 turns those into per-feature error state. `AffectedBy` does the dirty propagation and returns the result in evaluation order, since a caller given a set would have to sort it against this same graph anyway. Both the tie-break and the cycle-tracing have tests verified by sabotage.)*
- [x] **P3-T04** `RebuildEngine`: dirty marking, transitive propagation, topological ordering, execution against the dispatcher, cancellation, coalescing of superseded rebuilds. *(`IFeatureEvaluator` is the seam to `OpenMCAD.Modeling`: Core knows features have inputs, an order and results, and must not know what an extrude is. It is synchronous, because kernel operations are blocking native calls already marshalled onto the kernel thread -- making it async would either wrap a synchronous call for nothing or let an implementation release the kernel thread mid-operation, which is what a single-threaded actor exists to prevent. Publishing is all-or-nothing, in one transaction after the last feature: a rebuild cancelled halfway would otherwise leave new geometry for the first three features and old for the rest, which is not a state the model was ever in. Supersession cancels the running rebuild before queueing rather than after, so a dimension drag does not run fifty rebuilds to compute forty-nine documents nobody will see. Cancelled and Superseded are distinct outcomes because they mean different things to the user: one stopped because they asked, the other because they carried on. A failing feature is caught -- every exception, deliberately, since a feature is arbitrary code and in the general case a plugin's -- and contains itself to what depended on it while independent branches finish; P3-T07 turns that into the error state and report the user sees. Two flaws found while writing it and fixed: publishing threw if the user had a transaction open, which is ordinary rather than exceptional and is now `TryBeginTransaction` returning null and treating it as superseded; and a cancelled wait released a semaphore it never acquired, which would have let two rebuilds run at once from then on. Coalescing and dispatcher marshalling both have tests verified by sabotage.)*
- [x] **P3-T05** Geometry cache keyed by `(FeatureId, hash(resolved inputs))`; eviction policy; `--no-cache` mode. *(The key is where this task lives or dies, so most of the tests are about it rather than about the container: a cache that evicts badly is slow, a cache whose key misses something is a program that shows the wrong solid and never mentions it. Keys are chained -- a feature folds in the keys of what it consumes, making a Merkle chain -- because keying on input `FeatureId`s would call two chains identical when their parameters differ, and keying on input `KernelShape`s would be correct but never hit, a shape tag being a fresh handle every rebuild. SHA-256 rather than `string.GetHashCode`, which .NET randomises per process: a cache that never hits after a restart is not a cache. Every variable-length part is length-prefixed, or type `ab` with parameter `c` collides with type `a` with parameter `bc`. The display name is deliberately absent so renaming a feature does not discard its geometry. I first left `FeatureId` out in favour of pure content addressing and the test suite caught it: a cached `FeatureOutput` holds bodies that name their owner, so sharing an entry between two identical features gives one of them bodies belonging to the other. `--no-cache` is the same engine with a cache that never hits, so the fifth exit criterion compares the real path against itself rather than against a second implementation. Eviction is LRU by count -- not by memory, which is not knowable from this side since an entry's cost is dominated by shapes living in the kernel -- and dropping an entry raises `Evicted` so its shapes can be released, because the cache does not own them.)*
- [x] **P3-T06** Rollback bar as DAG-prefix evaluation. *(§5.4 predicted this would fall out of the design for free provided it was not special-cased, and it did: being behind the bar became one more reason a feature is not evaluated, alongside being suppressed and depending on something that failed. The propagation, the ordering and the skipping were already there, and the engine change is one clause. What it did need was something nothing had required before -- that a feature which is not evaluated gives up its geometry, since dragging the bar up the tree is how the user looks at a part half-built and a rolled-back extrude still showing its solid makes that gesture show nothing. That turned up a bug in P3-T04: suppression had the same requirement and was not meeting it, so switching a feature off left its solid on screen. The position is nullable rather than defaulting to the feature count, or 'not rolled back' would mean whatever the length was when it was last written and the next feature added would appear behind the bar. Deleting a feature above the bar moves the bar with it, so the same features stay active. Scrubbing the bar back and forth is free the second time, because every position is one the document has already been in and P3-T05's keys are already computed -- which is the claim that made the cache worth building.)*
- [x] **P3-T07** Error containment: per-feature error state, `suppressed-by-error` propagation, rebuild report object. *(Seven states rather than built-or-not, because the distinction is the whole value: `Failed` and `MissingInput` are problems to fix, `SuppressedByError` and `Blocked` are consequences, `Suppressed` and `RolledBack` are what the user asked for. A sketch that fails can leave twenty features unbuilt, and presenting them as twenty equal problems sends the user to whichever is nearest the top of the tree. Each consequence carries the id of the feature that actually failed, carried through rather than restated, so a chain ten deep still names the one thing to fix. `Blocked` exists so that a feature downstream of a *suppressed* one is not reported as an error: nothing went wrong, the user asked for that absence. The report lives on the `Document` rather than being returned from the rebuild -- the tree has to keep showing errors long after the caller has finished with its result, and holding it there means undo restores the report belonging to the state it restored instead of leaving marks from a model that no longer exists. Features outside a partial rebuild keep their previous diagnostics, since 'nothing was said' is not 'fine' and dropping them would clear the marks off still-broken features whenever the user edited elsewhere. A failed feature also gives up its geometry, or the user is shown a solid the current parameters do not produce. P3-T03's dangling-input report, computed and discarded until now, is what `MissingInput` reports.)*
- [x] **P3-T08** **`PersistentName` and `NameSegment`** per §5.3 — structure, serialization, human-readable rendering for diagnostics. *(Structure only; resolution is T09-T11 and nothing here resolves anything. Two details of §5.3 that are easy to miss on a first reading and change the shape: the worked example has one segment with **two** sources, so sources are a list rather than a field -- a blend face exists because two faces meet, and naming it after either alone would not distinguish it from the blend on the next edge -- and `Role` ends in an ellipsis, so it is an open string rather than an enum, because every later phase brings roles and a plugin can bring ones this build has never heard of. Equality is written by hand: a record compares an `ImmutableArray` by its underlying reference, so two names built identically would be unequal, and that would not fail loudly during resolution -- names would simply never match and every reference would fall through to the geometric tier. Sabotage-verified. The text form is length-prefixed rather than delimited, since any character used as a delimiter is one that eventually appears in a plugin's role name; there are round-trip tests with roles containing semicolons, colons, spaces and emoji. It is versioned and refuses a future version rather than misreading it. The renderer reproduces §5.3's worked example exactly, and degrades to ids when no document is to hand, because a diagnostic gets rendered in logs and crash reports.)*
- [x] **P3-T09** Name resolution tier 1: history replay over `HistoryMap` chains. *(Closed a real gap in P3-T04 first: `FeatureOutput` carried no `HistoryMap`, so every evaluator was discarding the one thing ADR-0002 makes non-negotiable and naming had nothing to work with. Resolution is two walks rather than one -- resolving the name finds the entity as it stood when its feature finished, and a second walk carries it through every operation that ran since, which is what makes a reference survive a feature being inserted above it, the commonest edit there is. A segment's sources intersect rather than union: §5.3's fillet blend is named after the two faces whose edge it replaces because either face alone also produced the blends on every other edge it touches. Ambiguity is reported rather than resolved -- a split face comes back as a shortlist for tier two, since a wrong-but-plausible answer silently corrupts design intent and is worse than an error. Deleted is kept apart from NotFound because history saying 'it is gone' is a definite answer no geometric search should override, and a sketch source with no sketch layer to ask is `Unsupported` rather than NotFound, so Phase 4's arrival does not look like a pile of newly broken models. Ordinals count from one, leaving zero to mean 'there was only one of these when this was written' -- so a zero ordinal facing several candidates is a split rather than a reference to the first of them. The engine now collects the maps in evaluation order onto `RebuildResult`.)*
- [x] **P3-T10** Name resolution tier 2: constrained geometric matching with `GeoHint` scoring, confidence threshold, and required margin over the runner-up. *(Most of the tests check that it refuses rather than that it succeeds: this is the tier most able to be confidently wrong, and §5.3 is explicit that a wrong-but-plausible answer is worse than an error. Surface kind is a gate, not a term -- as one score among several a strong centroid match could outvote it, and a plane is never the face a cylinder became. Distance is measured against the entity's own size, because CAD spans watch parts to airframes and any absolute tolerance is wrong at one end; the same proportional displacement scores identically at 1e-6 and 1e6. The two thresholds do different jobs and both are needed: confidence rejects a poor match, the margin rejects a good one that is not distinctive, and both are sabotage-verified. Missing evidence is left out of the score rather than counted as evidence against, or every reference written before a hint field existed would drop below the threshold at once. One test I wrote asserted that a reversed face normal should be disqualifying, and that was wrong: a boolean subtract routinely returns the same face with its orientation flipped, so a reversal costs a quarter of the score -- enough to lose to the right face when it is present, not enough to fail when it is the only candidate. Every candidate's score is reported even when nothing is accepted, because P3-T11 has to tell the user something they can act on.)*
- [x] **P3-T11** Name resolution tier 3: explicit failure, feature error state, and the repair-UI contract (UI itself lands Phase 6). *(`NameResolver` runs history, then geometry, then refuses. The decision worth recording is which failures do **not** go on to tier two: an entity history reports as deleted is a settled question, and the face that most resembles a deleted face is a different face -- adopting it is exactly the silent corruption §5.3 forbids, and it would look entirely reasonable. Sabotage-verified by letting Deleted fall through to the search. Ambiguity and a broken chain do go on, because there the question is open rather than answered. `ReferenceRepair` is the contract Phase 6 binds to, produced now because the information exists only at the moment resolution fails -- which candidates were weighed and how closely each fitted cannot be recovered afterwards. Its wording is specified by §5.3's own example: a verb, the thing, the feature, and the thing is called face, edge or vertex from the recorded geometry, because 'reselect the missing edge' is actionable and 'reselect the missing entity' is not. `FeatureState.UnresolvedReference` is kept apart from `MissingInput` because they break at different grain and are repaired differently: one is a whole feature that is gone, the other a particular face that cannot be identified. `RebuildReport.Repairs` is separate from `Errors` so a feature whose operation simply failed is not offered a reselect button for a reference it does not have. Wiring this into evaluation waits for features to carry entity references, which is P3-T12.)*
- [x] **P3-T12** Split/merge multiplicity policies (`ExactlyOne` | `AllDescendants` | `LargestDescendant`) declared per input reference. *(The insight worth keeping: the policy decides whether the geometric tier is consulted at all. Tier two exists to arbitrate an ambiguity, and for two of the three policies a split is not an ambiguity -- under `AllDescendants` every piece is wanted, and under `LargestDescendant` the tie-break is stated outright so a resemblance argument could only disagree with it. Only `ExactlyOne` reaches tier two, and it resolves when one piece clearly matches and stops to ask when none does. `ExactlyOne` is the default because a feature that has not thought about splitting will be wrong when one happens. `LargestDescendant` refuses on a symmetric split rather than taking whichever the kernel reported first -- that would resolve differently between runs while looking decisive. Features now carry `EntityReference`s alongside their coarse `Inputs`, and the graph reads both, so a feature that declares only references still gets its edges; a reference into a feature's own output is excluded, or it would be a self-cycle that is an artefact of how the name is written. References are folded into the cache key (encoding version 2), because a repaired reference hitting the cache would return the geometry from before the repair -- the one case where a stale answer is guaranteed wrong, since the user has just said so. Also fixed a determinism hazard from P3-T08: `ReferencedFeatures` returned a `HashSet`, which promises no order, and it now feeds the graph's tie-break.)*
- [ ] **P3-T13** **Naming regression corpus** with all categories from §5.3, run against `FakeKernel` (fast) and `OcctKernel` (nightly). *(Six of the ten mandatory categories are covered and the other four are blocked, so this stays open. Covered, end to end through a real `DocumentSession`, `RebuildEngine`, dispatcher, `HistoryMap`s and all three resolution tiers: dimension change, feature reorder, suppression, deletion with dependents, face split by a later feature, body split -- plus feature-inserted-above, which §5.3 does not list but is the commonest edit there is. Blocked: sketch topology change needs Phase 4, pattern instance count and mirror need P5 feature types, imported geometry needs Phase 8. `EveryMandatoryCategoryIsAccountedFor` is the durable part: each of the ten is either covered or explicitly blocked on a named phase, never silently absent, which is what makes §5.3's 'every new feature type must add cases here' enforceable rather than aspirational. Still to do when the blockers clear: the four remaining categories, moving these into `tests/regression/naming-corpus` as fixtures, and running them against `OcctKernel` nightly. Also completed the P3-T11/T12 wiring while here -- the engine now resolves each feature's references before evaluating, hands the resolved entities to the evaluator, and marks the feature `UnresolvedReference` with a repair when one breaks; verified by disabling it and watching five scenarios fail.)*
- [x] **P3-T14** Unit-typed `Quantity` and dimension algebra; reject dimensionally invalid operations at parse time. *(The design point that makes "at parse time" possible: `Dimensions` answers on dimensions alone, never values, so P3-T15 can type-check an expression tree as it parses and tell the user while they are still looking at it rather than when a rebuild fails an hour later. A closed table rather than exponent arithmetic, matching the closed `Dimension` list -- the general form is right for a physics library and would make every dimension representable while being unable to name one in a diagnostic; the price is that combinations outside the list, such as length times mass, are refused rather than invented. Angle is kept as its own dimension, which SI would disagree with: a radian is dimensionless, so a strict treatment would let `4 mm + 3 deg` through as a number plus a number, which is the exact error §5.5 opens with. Sabotage-verified by making angle dimensionless and watching three tests fail. `Unit` is the input and display boundary and has no other job. Two corrections to my own assumptions while testing: round-tripping cannot be exact for every unit and no implementation could make it so -- a degree is a factor of π and a pound is 0.45359237 kg, neither exactly representable -- so the universal property asserted is that a value **settles**, with exactness pinned separately for the power-of-ten units where it is achievable and easy to lose by using a precomputed reciprocal.)*
- [x] **P3-T15** Expression parser and evaluator: arithmetic, functions, conditionals, unit literals, parameter references, cross-document references. *(Parse, check and evaluate are three passes, and the separation is what makes §5.5's "caught before evaluation" true rather than a manner of speaking: the checker walks dimensions only, reads no parameter values and computes nothing, so it can answer the moment the text is syntactically whole. Hand-written recursive descent, because the grammar is smaller than a generator's configuration and because the error wording is most of the value -- an expression box is where people make typing mistakes constantly, and every message names the model rather than the parser and carries a position an editor can underline. Decisions worth recording: a bare number is a plain number, not a length, so `Width + 5` is refused with the fix in the message -- adopting document units there would make one formula mean different sizes in different documents; `sin` takes an angle rather than any number, since `sin(0.5)` reads as half a turn to a person and as radians to a calculator; `round`/`floor`/`ceil` take plain numbers only, because values are stored in metres and `round(Length)` would quietly round a part to the nearest metre, so the message gives the idiom `round(x / 1mm) * 1mm`; `round` goes away from zero at a half, not banker's; comparisons yield 1 and 0 rather than a boolean type, which would be a second kind of value running through everything for the sake of one function; and `if` is a function rather than syntax, evaluating only the branch it needs so `if(x != 0, y / x, 0)` is sensible to write.)*
- [x] **P3-T16** Parameter dependency graph integrated into the rebuild DAG; cycle rejection at commit. *(Commit is where values are brought up to date and where loops are rejected, both because §5.5 says so and because the alternatives are worse: earlier means recomputing on every keystroke of a multi-step edit, later means a document existing whose stored values disagree with its own formulas. The rejection happens before the transaction is marked finished, so a caller holding a cycle still has an open transaction to correct rather than a spent one and a document they cannot fix. Ties in evaluation order break alphabetically -- a document holds parameters in a dictionary, which has no order to inherit, and the same reasoning as P3-T03 applies. A formula that cannot be evaluated keeps its last known value rather than losing it, which is why a `Parameter` stores both; a badly typed one does not stop the document being ordered, or one typo would hide every other problem. A sabotage run found redundant code rather than a weak test: I had features seeded both by naming a changed parameter and by their own value moving, and the first is a wasteful superset of the second -- a depth of `min(Thickness, 5mm)` does not move when Thickness goes 8 to 9, so its inputs to the kernel are identical and rebuilding recomputes the same solid. That path is gone and the rule now has a test either way.)*
- [x] **P3-T17** Undo/redo: command log over parameter state, scoped recompute, grouped transactions, named undo entries. *(A stack of document references rather than a log of inverse commands, which is the P3-T01 immutability decision paying for itself: to undo an edit against a mutable document you must know how to reverse it, every kind of edit needs its own inverse, and an inverse that is subtly wrong corrupts the model in a way that surfaces later. Restoring a reference cannot be subtly wrong because it is not a computation -- and it brings back the bodies, the rebuild report and the rollback bar exactly as they were, so no scoped recompute is needed at all. Grouping is inherited rather than invented: §5.4 already makes a transaction the unit of edit, so one commit is one undo. Phase 3's fourth exit criterion is met and checked at every intermediate state rather than only the endpoints, since an undo that skipped one and an undo that mis-restored two both land correctly at the start. It needed `Document.Matches` -- a deep comparison excluding `Version`, which counts edits rather than describing the model -- and that turned up the P3-T08 equality trap again: `Feature` holds three `ImmutableArray`s and `DocumentMetadata` a dictionary, all compared by reference under generated record equality, so every document would have reported as different from itself the moment any feature had an input. Both now compare structurally, sabotage-verified.)*
- [x] **P3-T18** Persistence: OPC container writer/reader, MessagePack document schema v1, geometry and tessellation cache blobs, thumbnails, manifest. *(The encoder is hand-written -- `Directory.Packages.props` requires an ADR for any new dependency, and the one thing a MessagePack library would have given, attribute-driven serialisation, is unusable because P3-T20 needs explicit read and write code anyway. Encoding is canonical and every collection without an order of its own is written sorted, because the first exit criterion needs the bytes to be a function of the document alone. Zip entries carry a fixed timestamp for the same reason; when a file was written is the manifest's business, and the manifest takes its timestamps as inputs rather than from the clock -- otherwise no save could ever match another. What is deliberately not written: a `KernelShape`, which is a handle into a kernel that is not running any more, and the rebuild report, which §5.8 makes regenerable. `--no-cache` is the same reader with the caches ignored, so the two can be compared. Two of my determinism tests were vacuous and sabotage found it: an `ImmutableDictionary` enumerates by content rather than insertion order, so building the document 'differently' proved nothing, and a Zip stores DOS time to a two-second resolution, so two saves in one test land in the same tick. Both are now asserted directly -- the written order is read back out of the bytes, and each entry's stamp is checked -- and both catch their sabotage. A review afterwards found six more things worth fixing, four of which the tests had no opinion about. The reader applied each field to the document as it arrived, but a MessagePack map has no defined order, so a legal file that put its bodies before its features -- or its rollback bar before either -- threw on open; every field is now collected before anything is assembled. `Read` promised `DocumentFormatException` and let `FormatException` and `ArgumentException` out of the same call, so a caller had to catch three types to catch one contract. The datums were cleared unconditionally, which meant a file that simply had nothing to say about reference geometry opened with no origin and no planes -- only a file that states what its geometry is, including that it has none, replaces them now. `Skip` threw on the extension family, and MessagePack's standard timestamp is an extension type, so a field another implementation could reasonably write failed the whole open. Two more were quieter: `WriteIndented` takes its newline from `Environment.NewLine`, so the same document saved on Windows and on Linux differed in the manifest -- exactly what the fixed timestamps and the canonical encoder exist to prevent, and invisible to a Windows-only CI; and `Save` persisted whatever versions the caller's manifest carried, so the natural re-save recorded the old file's schema beside a payload written at the current one. Container parts this build has no name for are now kept and written back, which is the whole of what forward compatibility means for something nobody can interpret, and loading a document builds it in one pass rather than allocating a document per feature. Each fix has a test, and each test fails against the code as reviewed. Still open for T19 and T20: the migration chain, the format-fixture corpus, and unknown-field preservation inside the document itself.)*
- [x] **P3-T19** Schema migration framework `v(n) → v(n+1)`; the format-fixture corpus and the CI gate that opens every historical fixture. *(A migration reads a document this build's codec by definition cannot read, so it cannot be handed a `Document`, and asking it to patch raw bytes would make every migration a hand-written parser. It gets a `MessagePackValue` tree instead: the shape is visible, nothing is validated, and re-encoding produces bytes the current reader takes. Maps keep their order, because a tree that reordered fields would break P3-T18's bit-identical re-save the moment a migration touched a file -- which is why `With` on a key that is already there replaces it in place and `Renamed` exists at all, rather than leaving every migration to remove-and-add and move the field to the end. Extension values are kept verbatim; re-encoding a value nothing here understands is how a migration would silently corrupt one. Steps are single-version only: a migration that jumped two could not be composed with the ones around it, and the first version inserted between them would invalidate every such shortcut. A gap in the chain is refused rather than skipped, because skipping produces a file that opens, looks right, and is wrong in whatever way the missing step existed to fix; two migrations claiming one version are refused rather than resolved by declaration order. The chain stamps the version each step reached, so a migration that forgot does not surface as a confusing complaint about the file. The reader checks the version before parsing and only pays for the tree when a document is actually old. A file with no `schema` field at all is read as current -- a guess either way, and the one that changes nothing. The registry is empty because schema 1 is the only version there has ever been, so the chain is exercised by migrations declared in the tests; ten sabotages each fail the right one. The corpus is real packages, never regenerated, each with a JSON description beside it covering every field the schema carries -- feature suppression and a derived parameter's expression included, since an expectation checking only names would let a migration drop the rest unnoticed. Values are recorded round-trippable rather than through `Quantity.ToString`, which rounds thirty degrees to 0.523599 and would let a migration move a dimension by a part in ten thousand and still pass. Raising `SchemaVersion` without adding a fixture fails, and the failing run writes the candidate package and says where to put it. The corpus is exempted from git-lfs that the root `.gitattributes` would otherwise apply: these are two kilobytes each and the build fails without them, and a clone without the filter would get pointer files and report that OpenMCAD cannot open its own documents.)*
- [x] **P3-T20** Unknown-field preservation on round-trip. *(The case that matters is not a corrupt file. It is a colleague on a newer build sending a part, someone here opening it to check a dimension, and saving out of habit: a reader that dropped what it could not read would delete that colleague's work with no error and nothing to notice until much later. Kept fields live on the `Document` rather than at the file boundary, because the boundary is exactly where they would be lost -- anything held beside the document is dropped by the first edit, and editing is what the person who opened the file does. Preservation reaches every level the schema has: the document, each feature, each parameter, each parameter inside a feature, each body, each piece of reference geometry, and the properties. A field is read before its owner necessarily has a name -- a map has no defined order, so a parameter's unknown field can arrive before the parameter's name and a feature's before its id -- so fields are collected with owners relative to whatever is being read and prefixed on the way out. Getting that wrong does not lose data, it doubles it: a nested field filed under its container is written at both levels, and the test asserts the key appears exactly once, which the obvious assertion did not catch. Values go back verbatim; re-encoding a value whose meaning is by definition unavailable is how a preserving reader corrupts what it set out to preserve. Fields are written after the known ones in the order they were read, so two saves agree; their original position among the known fields is not kept, which would mean recording an index that stops meaning anything the moment the schema gains a field of its own. A field belonging to a feature the user deletes is not written back, but stays on the document, so undoing the deletion brings it back with the feature. Reference geometry is keyed by name for want of an id, so renaming a datum loses what a newer build attached to it -- the honest limitation, since keying by position would move one object's data onto another the first time a datum was inserted. Seven sabotages, each failing the right test.)*
- [x] **P3-T21** `FeatureSchema` — the single declaration driving property UI, serialization, API surface, and scripting (§5.7). *(A property declares a stable name, which files and scripts use and which must never change, and a label, which is shown to a person and can. Where a value lives depends on its kind and one place knows: a dimension is a `Parameter` so an expression can drive it and the parameter graph can see it, a selection is an `EntityReference` so persistent naming can repair it, and everything else is a new `Feature.Settings`. A caller that had to know which would be a fifth description of the feature, which is the thing §5.7 exists to prevent. `VisibleWhen` is part of the declaration rather than a hint for the UI: an extrude's draft angle with draft switched off is not a value the user declined to give, it does not apply, and if the property manager and validation each decided that for themselves they would disagree — so one method answers it, and it follows the chain, because a property behind a property that does not apply does not apply either. A schema checks itself when built, so a duplicate name, an empty choice list, a default of the wrong kind or a condition on a property that does not exist is caught the first time the feature is registered rather than the first time a user opens that panel. Validation reports `SchemaViolation` rather than `FeatureDiagnostic`: a rebuild diagnostic says what happened when a feature ran, this says why one should not be run at all, and only this one can name the property at fault. A setting nothing understands is a warning, not a refusal — it may be from a newer build, which P3-T20 keeps and this build has no business deleting. So is a feature whose kind is unknown: an uninstalled plugin costing one feature is survivable, costing the whole file is not. `WithDefaults` is what makes adding a property to an existing feature kind safe, since a file written before it existed says nothing about it and the schema already says what it should be. The codec stores settings with a tag per kind and skips tags it does not know, so one unrecognised switch cannot make a document unopenable; settings are written sorted, because the bytes have to be a function of the document. Two of my tests were vacuous in exactly the way P3-T18's were — an `ImmutableDictionary` enumerates by content, so building a catalogue or a settings bag 'in a different order' proves nothing — and both are now asserted as the order itself. Eleven sabotages, each failing the right test. The schemas exercising all this are a real extrude and a real fillet, because a mechanism proved only against toy declarations is one nobody has checked is expressive enough. Adding `settings` to the feature schema without a version bump is deliberate: nothing has been released, so schema 1 is still being defined, and the field is optional in both directions. After the first release the same change would need a bump and a migration.)*
- [x] **P3-T22** Headless document API in `OpenMCAD.Cli`: build, rebuild, inspect, save, diff. Every later phase tests through this. *(`build` takes a JSON document spec -- features, parameters, settings, rollback -- and is deliberately not the regression corpus's fixture format, which describes kernel operations: these are different layers and one file trying to be both would serve neither. Building the same spec twice produces the same bytes, because feature ids come from the feature's name rather than a fresh guid and the manifest carries a fixed timestamp. That is not tidiness -- a document built by a later phase's test could otherwise never be compared with a stored one, which is the whole reason those tests build through this tool. Units are stated in the spec and converted, never assumed; the one guess nobody should ever make about a CAD dimension is which unit it is in. Every command does its work in `DocumentCommands`, returning an exit code and writing to a `TextWriter`, so a test calls it rather than starting a process -- and every command can answer in JSON, so a test asserts on a field rather than on wording that will be rephrased. Exit codes follow the shell's conventions rather than inventing any: 0 for yes, 1 for a negative answer (documents differ, a rebuild has errors), 2 for could-not-be-done. A script that treated a missing file the same as a genuine difference would report success when the file was never there. `diff` compares documents rather than bytes, because two files can differ byte for byte and describe the same model, and it reports a reorder as a difference, because tree order is what the user arranged and what a rebuild follows. `rebuild` is the pre-flight half only -- all of it that exists before Phase 5 gives features something to do -- and is wired now so its shape, output and exit codes are settled before anything depends on them. Writing the tests found a real bug in Core: `DocumentPackage.Open` promised `DocumentFormatException` and let `InvalidDataException` out when the file was not a Zip at all, which is the commonest way to arrive with something wrong. Ten sabotages, each failing the right test; the one that found nothing exposed a missing test rather than a redundant check, and `inspect` now has one for reporting that a file carries fields from a newer build.)*

---

### Phase 4 — Sketch spine

**Goal.** A 2D sketcher with a real constraint solver, good diagnostics, and a drag experience that feels right.

**Effort:** 8–12 engineer-weeks.

**Exit criteria.**

- A 200-entity sketch drags at under 16 ms per solve.
- Over-constrained and redundant cases are diagnosed with the specific conflicting constraints named.
- Sketch state round-trips through save/open with identical solver results.
- Auto-constraint inference produces the expected constraints across a scripted fixture set.

**Tasks.**

- [ ] **P4-T01** `native/openmcad_gcs`: C ABI shim over planegcs, using the same IDL generator as the kernel shim.
- [~] **P4-T02** `ISketchSolver` interface per §5.6; `OpenMCAD.Solver.Planegcs` implementation; a `FakeSolver` for unit tests. *(Interface, diagnosis vocabulary, parameter flattening and `FakeSolver` are done; the planegcs implementation waits on P4-T01, which waits on a decision about vendoring LGPL source into a public repository. The interface takes and returns a whole `Sketch` rather than a parameter vector: the vector is the solver's business, and a caller that had to scatter one back into entities would be doing the solver's bookkeeping, which two callers would eventually do differently. `SketchParameters` is that boundary, in one place so a second implementation cannot lay the numbers out differently and quietly change which entity a constraint acts on; its order is by entity in sketch order, because the vector's order decides the Jacobian's columns and therefore which of several equally valid answers a least-squares step finds. Knots are deliberately not parameters -- moving one changes a spline's parameterisation rather than its placement, no constraint acts on one, and including them would report degrees of freedom nobody could use up. Residuals live with the constraints rather than inside a solver, because the equations are the meaning of the constraints and a second solver deriving its own would be a second opinion about what 'tangent' means. `FakeSolver` is a real solver, not a stub -- Levenberg-Marquardt over a numerically differentiated Jacobian with a dense factorisation -- for the same reason `FakeKernel` is: a fake returning its input unchanged lets every test above it pass without anything being solved. Fixed points are held out of the Jacobian rather than expressed as residuals, or a least-squares step would trade a little of them away against another constraint. The diagnosis comes from the rank of the Jacobian, which is the only thing that can tell the four cases apart: counting equations against unknowns calls a sketch with two identical constraints fully determined, and says nothing about which constraints are at fault. A rank below the equation count means something is implied by the rest; whether that is harmless duplication or a contradiction is decided by whether the residual actually came down. Elimination runs in the order the user made the constraints and without row pivoting, so the constraint named is the later of a dependent pair -- the one they just added -- rather than whichever row happened to have the largest entry. A drag seeds the point at the pointer and leaves it free: an earlier version froze it, which let a drag violate a dimension outright, and the test caught it. Fifteen sabotages: eleven failed the right test immediately, four found nothing and each one was a real finding. Two were missing tests -- there was no parallel case at all, and no case where the measured angle and the target sat either side of atan2's branch cut (unwrapped, that costs nine iterations instead of three for a sketch already right, which a 16 ms drag budget cannot afford). The other two showed that the signed point-to-line distance and the step-acceptance check, both standard and both correct, are not observably load-bearing in this solver: least squares over an absolute distance still converges, and Gauss-Newton solves everything a unit test writes. Their comments now say that rather than claiming a virtue no test demonstrates.)*
- [x] **P4-T03** Sketch entity model: point, line, arc, circle, ellipse, elliptical arc, parabola, hyperbola, B-spline, construction variants, text (Phase 7). *(Entities hold their geometry as values -- a line has a start and an end, not two indices into a parameter vector. planegcs wants a flat vector of doubles and will get one, but that flattening belongs at the solver boundary (P4-T02); everything above it, which is the whole sketcher, reads and writes points, and a model built the solver's way would make every piece of UI, inference and serialization code do arithmetic on offsets. Constraints attach to named points rather than to whole entities, because "this line's end meets that arc's centre" is two point references and a model that named only the entities could not express it; named and not indexed, since an index means something different per entity kind and the first entity to gain a point silently moves every stored index after it. An entity is asked which points it has, so a constraint pointed at one it does not have is caught when the constraint is made rather than when the solver reads a coordinate nobody wrote. Arcs sweep anticlockwise always: a signed sweep makes "the same arc" two entities and every endpoint constraint has to know which convention it was written under. Elliptical arcs store eccentric angles, not polar ones, which agree only on a circle -- the polar form needs a transcendental solve to evaluate a point. Parabolas are stored by vertex and focus rather than by coefficients, which are relative to whatever axes they were written in. Splines are rational from the start: a non-rational spline is the case where every weight is one, and retrofitting weights later would mean rewriting every file, and only a rational quadratic can be exactly a circular arc -- which is the test that proves the weights reach the de Boor recursion rather than being corrected afterwards. Knots are distinct values with multiplicities rather than a repeated vector, so the one mistake that matters -- a count that disagrees with the repeats -- cannot be written. Degeneracy is checked here rather than left to the solver, which would report a circle of zero radius as a non-convergence somewhere unrelated. `ToString` is sealed on the base: a record regenerates its own in every derived type, which shadowed the override and printed the entire member list including the computed properties. My equal-radii ellipse test was wrong and the code was right -- rotating an ellipse with equal radii does not change the set of points it covers but does change which parameter lands on which of them -- so it now asserts distance from the centre. Twelve sabotages, each failing the right test.)*
- [x] **P4-T04** Constraint model: the full set in §5.6, with serialization and schema. *(One record with a kind rather than sixteen records. The solver boundary needs a uniform representation anyway -- planegcs takes tagged constraints over a parameter vector -- and what each kind requires is then a table rather than a type per kind plus a case in every switch that walks them, so adding a constraint kind is one row. The table is the schema the task asks for, and it is the P3-T21 idea again: one declaration driving validation, the eventual constraint palette, serialization and the degree-of-freedom count, so they cannot drift apart. Operands are point references throughout; where a kind wants a whole entity the operand names it with `Self`, which for a point entity is also its position, and that is not a coincidence -- a point entity is its position. One operand type means one resolution path, which is what stops the sketcher and the solver disagreeing about what a constraint was attached to. A reference dimension is a flag, not a kind: it measures the same thing and differs only in whether the solver is told, so a separate kind would double the table and make 'convert to reference' a change of type. The equation counts are what a solver actually gets rather than what feels right -- a coincidence is two because it fixes both coordinates, a distance is one because it leaves the direction free -- since getting them wrong does not break a solve, it breaks the degree-of-freedom readout, which is worse because the number looks authoritative and is quietly false. `Fix` takes a point and not an entity: a Fix whose meaning changed with what it was pointed at would remove a different number of freedoms each time and make the readout unexplainable, so fixing a circle is fixing its centre plus a radius, which says exactly what it does. A kind can accept more than one operand shape (horizontal is one line or two points, and both say the same thing to a user), and the complaint reported comes from the shape with the right number of operands, or horizontal given one bad line is told it is not two points. Validation is against the geometry rather than in isolation, because most of what can be wrong with a constraint is a fact about what it names -- a radius on a line, a tangency between two points, a reference to something deleted, an operand named twice -- and §5.6 is blunt that a diagnosis naming no specific constraint is useless. `Sketch` holds geometry and constraints together because deleting an entity has to take its constraints with it: one left pointing at deleted geometry names a coordinate nobody will write, and the failure surfaces as a solve that does not converge. `RemainingFreedom` is an upper bound and says so -- two constraints saying the same thing both subtract, which is exactly what makes a sketch redundant rather than over-constrained, and telling those apart needs the rank of the Jacobian at P4-T06. Serialization is JSON and is the interchange form, not the file format: it is what the P4-T16 corpus is written in and what a bug report can be pasted into, while a sketch inside a document will be MessagePack with everything else at Phase 5 -- the same split `omcad build` already has one layer up. Everything is named and never positional, because an ordinal changes meaning the moment a kind is inserted and a corpus exists to be read years later. Fourteen sabotages, each failing the right test.)*
- [x] **P4-T05** DOF analysis and subsystem decomposition so drag solves touch only the affected cluster. *(Union-find over the entities, joined by every driving constraint that names more than one of them. The decision the whole thing rests on is that fully fixed geometry is ground and is left out of the graph: dimensioning from the origin is how sketches are drawn, and a decomposition that joined groups through ground would report one subsystem for every sketch anyone ever made, which is the same as having none. A partly fixed entity is not ground -- a line with one end pinned still has an end that moves, and calling it ground would leave that end unsolvable. Reference dimensions join nothing, for the same reason they remove no freedom. Groups come back largest first with ties broken on position in the sketch, never on an id, whose value is random and orders differently in the next process; two runs that decomposed a sketch differently would solve it differently and ADR-0011 does not allow it. Restricting a group brings the ground its constraints refer to along with whatever fixes that ground, or the sub-solve gets a free point where the sketch has a pinned one and moves geometry that cannot move. The frozen-parameter set moved out of `FakeSolver` and into the analysis, because a solver working out 'fixed' for itself would be a second opinion about which numbers may move and the decomposition depends on the same answer. A full solve now runs group by group and a drag runs only the group holding what is dragged -- nothing outside it can have moved, by the definition of a subsystem. Verdicts combine with the worst outcome winning and the freedom adding up: a sketch with one contradicting group is a contradicting sketch however well the others solved, and two loose points are four degrees of freedom rather than two. Ten sabotages: six failed the right test at once, three exposed that no solver test had ever used a sketch with more than one subsystem -- so a drag solving everything, the best verdict winning, and freedom not being summed all went unnoticed -- and the tenth is honestly a no-op, since ground is frozen in the sub-solve too and writing it back writes the same numbers. Its comment says so rather than claiming a virtue no test can show. A review afterwards found three genuine regressions the decomposition had introduced. A constraint acting only on ground belonged to no group of movable geometry and so was never evaluated by anybody -- a contradictory distance between two fixed points reported as fully defined, which is the worst kind of wrong; such constraints now get a group with no entities, nothing to move and everything still checked. A drag naming geometry that is not in the sketch resolved to no group exactly as a fixed point does, and the two were treated as one, so a stale drag id -- what a drag begun before a delete looks like by the time it arrives -- skipped the solve and reported a broken sketch as healthy; either now falls back to solving everything. A drag reported only its own group's verdict, flipping the status to 'fully defined' while the mouse was held down over a sketch whose other feature contradicted itself. Fixing that naively cost a Jacobian per group per frame, which is the very cost the decomposition exists to avoid, so an untouched group is now judged on its residual alone and only escalates to a full rank analysis when it cannot be satisfied -- giving up only the re-detection of redundancy in a group nothing has happened to. Also from the review: the drag seed now checks for ground before moving a point, since a lone fixed point has no equation to pull it back and the drag was relocating the one thing the user had said must not move; the time budget and the cancellation token are the whole solve's rather than each group's; freedom and the free-entity list are reported only for the under-constrained case, because 'conflicting, four degrees of freedom left' is two answers to two questions; the entity-width table that had been duplicated between `Sketch.Freedom` and the analysis is now one table, tested across every kind of geometry; and an unused public `Merge` that disagreed with the write-back rule is gone. Seven more sabotages, six failing the right test; the seventh, the shared clock, is right on the reasoning rather than on a measurement, and says so.)*
- [x] **P4-T06** Diagnosis mapping: solver output → `WellConstrained` / `UnderConstrained(dof, entities)` / `OverConstrained(conflictSet)` / `Redundant(set)` / `Failed`. *(The classification moved out of `FakeSolver` into a shared `SolveDiagnostics`, because which of the five situations a sketch is in is a statement about sketches and not about numerical methods: two solvers deciding it separately would give a user two different diagnoses of one drawing, and only one of them could be right. What a solver hands over is `SolveEvidence` -- a residual, a rank, two counts and two lists -- deliberately the intersection of what any solver can produce rather than either one's native output, since planegcs reports dependent and conflicting groups directly and a least-squares implementation gets them from eliminating its own Jacobian. The order of the questions is the design: rank first, because a rank short of the equation count means some constraint is implied by the others whether or not the sketch happens to be satisfied, and only then does the residual decide which kind of implied it is -- a duplicate that agrees is redundant, one that disagrees is a contradiction. The tolerance has a floor under it, or a sketch solved to 1e-9 against a 1e-10 ask is reported as failed for having been solved slightly less hard than it might have been. A solve that was stopped by its time budget or cancelled now says so rather than advising the user to move the geometry, which is only good advice when the solver actually tried. The whole thing is testable without solving anything, which matters because the rules are where the subtlety is and reaching a particular rank and residual through a real solve is a slow and indirect way to check one -- and cannot reach the combinations this solver never happens to produce but a different one will. Ten sabotages, each failing the right test.)*
- [x] **P4-T07** Drag solving with a minimal-motion objective; frame-rate-budgeted, coalesced. *(The objective is a second pass, run once the constraints already hold, and that took two wrong turns to arrive at. Weighting it against the constraints inside one least-squares problem bends them -- its pull grows with how far the pointer is from where the geometry can reach, so a dimension of four came out as 4.0036 with the cursor thirty units away, and there is no weight that both breaks ties and never bends anything, because a constraint is not a preference. Weighting the pointer against the rest of the sketch has the same shape of problem one level down: a point tied by a coincidence came to rest at exactly `(8·target + 1·origin)/9`, the compromise the weights asked for, which is a sketch lagging behind the cursor by an amount nobody chose. The two wishes are ordered, not weighed, so there are two passes: follow the pointer, then move as little else as possible. Each solves `(w·J'J + W)δ = W·d` for a large `w`, which is the projection of the wish onto the directions the constraints do not pin -- exact, and incapable of touching a constrained direction. The first pass carries a millionth-scale ridge on everything but the dragged point so the system stays solvable; at a millionth that held the pointer back by three microns on a three-unit drag, which a test at a part in a million saw, so it is a billionth now. `DragSession` coalesces: every pointer position but the newest is stale by the time it could be worked on, and a queue would make the geometry lag further behind the longer the drag went on. It counts what it skipped, because a drag dropping most of its frames is worth being able to measure rather than discover from a video. Every frame solves from the sketch as it was at mouse-down, so the drag is reversible by construction rather than by luck -- though with an exact projection chaining from the previous frame turns out not to creep either, and no test tells the two apart. Ten sabotages, six failing the right test. Of the other four: one showed that restoring the sketch before measuring the anchor was code that could not change an answer, since the seed moves only the held point and that point's anchor is the pointer -- removed. One showed the same of returning null for a drag on a computed point: the passes would ask everything to stay where it is and agree, so it is a saving rather than a correction. The guard that waits for the constraints before applying a preference, and the choice of baseline, are both right on the reasoning rather than on a measurement, and say so.)*
- [x] **P4-T08** Constraint inference while drawing: coincident, horizontal, vertical, tangent, perpendicular, parallel, midpoint, concentric, equal — with live glyphs and a suppression modifier key. *(Nothing here applies anything: it proposes, in a fixed order, and the caller decides. That is what lets the same code drive the glyphs shown before the click and the constraints added after it, and lets a test check the guess without a UI. The tolerance is a model distance the caller works out from pixels, because inference has to feel the same at every zoom -- a fixed one would snap to everything zoomed out and to nothing zoomed in. The glyph is a name, not a drawing, since what a coincidence looks like is the UI's business and this layer has no idea how big a pixel is. Most of the work is in not being annoying. Nothing already true is offered, compared as an unordered pair, because 'A is parallel to B' and 'B is parallel to A' are the same sentence and offering the second is telling the user you have not been paying attention. At most one direction constraint is offered per entity: horizontal, vertical, parallel and perpendicular all say where a line points, and two at once is a contradiction. A named point beats the curve it belongs to, because someone aiming at the end of a line wants the end. Equal is the weakest guess and comes last -- two circles the same size and nearly concentric are both, and only one is what someone drawing a bolt circle meant. The count is capped, because a cloud of glyphs is noise a user learns to ignore. Two real bugs came out of the tests rather than the reasoning. A point past the end of a segment sits on the infinite line through it at a distance of nothing, and was being offered 'on this line' for a position the user can see is off it; the same in angle for a point outside an arc's sweep. And the tie-break ordered by the largest operand index, which is the entity just drawn in every proposal about it, so it tied everything and broke nothing -- twelve sabotages, eleven failing the right test at once and the twelfth finding that.)*
- [x] **P4-T09** Snapping: grid, entity, midpoint, quadrant, intersection, extension, parallel/perpendicular guides. *(Snapping and inference are two answers to one proximity search and are deliberately apart: snapping moves the cursor, inference proposes a constraint. A user dropping a point on a line wants it on the line whether or not a constraint follows, and a sketcher that offered only the constraint would leave the geometry visibly off by however far the cursor missed. One candidate comes back rather than a list, because a cursor is in one place and a caller given several would have to choose -- which is this code's job, done once, rather than every caller's, done differently. The `SnapKind` enum is ordered by how much a catch means and nothing else decides preference: a named point of real geometry is what the user aimed at, a grid intersection is what they get when they aimed at nothing. The grid therefore rounds regardless of distance and always loses to anything real -- a grid that needed the cursor to be near it already would be one that worked sometimes. Guides need somewhere to run through, so none are offered when nothing is being drawn: a guide from nowhere is a line across the whole sketch catching the cursor at random. `Crossings` is public because trimming (P4-T13) and profile detection (P4-T14) both need it, and three answers to 'where do these cross' would eventually be three different answers; it works on the curves as drawn, so two segments that would meet if they were longer do not meet, and a crossing outside an arc's sweep is not one. Twelve sabotages. Seven failed the right test at once; four of the misses were my test data rather than the code -- a grid point that happened to sit inside the tolerance, a line through the origin whose guide and extension coincided, an extension case the curve snap won anyway -- and one was a real gap, since nothing covered one circle wholly inside another, which without its check invents a crossing at a place neither circle passes through. The twelfth could not be compiled at all: warnings-as-errors noticed the sabotaged method had stopped reading instance data, which is the analyser doing the sabotage's job for it. `SnapCandidate` needed hand-written equality for the usual reason, found by the usual test.)*
- [x] **P4-T10** Sketch plane definition: on a datum plane, on a planar face, or on a custom coordinate system, with a **named, resolvable** reference (this is a naming dependency — test it). *(Split the way P4-T02's solver boundary is split: `SketchPlaneReference` is the durable half -- a name, in `OpenMCAD.Modeling` because it has to see both `ReferenceGeometry` (Core) and kernel topology (Kernel), which `OpenMCAD.Solver` cannot -- and `SketchPlane` is what one resolves to for a single rebuild, a plain orthonormal frame with no name attached. Storing the frame instead of the reference would be storing coordinates rather than intent (§5.3): a sketch on a datum plane nobody had moved yet would freeze there the moment it was cached and stop following the datum the first time someone dragged it. A datum plane and a custom coordinate system are both addressed by `(Owner, Name)` against `Document.References` -- new surface, `Document.FindReference`, since nothing needed it before this; a planar face is kernel topology and gets no name of its own, so it is addressed the way every other feature addresses kernel topology, through a bare `PersistentName` resolved by the real `NameResolver` (ADR-0005, §5.3) rather than a parallel lookup invented for this one caller. Always exactly one face -- a sketch plane naming "every piece of a split face" has no meaning, so unlike `EntityReference` this offers no `MultiplicityPolicy`, and an ambiguous split is always a refusal, never arbitrated by size. A resolved face's plane comes from a caller-supplied `Func<SubEntity, Plane?>` rather than from `GeoHint`: the naming layer's evidence is deliberately in coordinates local to the producing feature so that moving a part does not stop its faces being recognised, and reusing it here would place a sketch at the wrong point the first time that feature moved. Nothing in `OpenMCAD.Kernel` exposes a planar-face query yet -- Phase 5 and the OCCT decision are what will supply one -- so this takes it as a delegate, the same shape `NameResolver` already takes its own geometric evidence as, rather than inventing a kernel operation ahead of the decision to turn a real kernel on at all. The canonical basis for a bare normal is `OpenMCAD.Math.Plane.CreateFrame`, already written and already tested for exactly this (its own doc comment names "a sketch plane" as the reason it exists) -- `SketchPlane` adds nothing on top of it beyond an origin. Every failure is data, never an exception across the resolution boundary: a rebuild resolves every reference on every feature in the dirty set, and one corrupt datum throwing would take features that have nothing to do with it down too (§5.4) -- `SketchPlane.FromNormal`/`FromFrame` still throw on a genuinely degenerate normal or axis set, because they are geometry constructors with a real precondition, but the resolver catches that at its two call sites and reports `NotPlanar` instead. Nineteen sabotages: fourteen in the tests as written -- caught before running, not after -- by hand-tracing `OpenMCAD.Math.Plane.Origin`'s reconstruction (the closest point to the world origin, not the point originally given to `FromPointNormal`) against two assertions that had assumed the argument came back unchanged, and by hand-tracing `Vec3d.AnyPerpendicular`'s actual axis choice against a test that had assumed the world Z normal produces the identity frame it does not; five by literally reverting a guard and confirming the intended test fails: `Document.FindReference` ignoring `Owner`, `SketchPlaneResolver` accepting reference geometry of the wrong kind, `SketchPlane.FromFrame` skipping the projection that re-orthogonalises a caller's X axis, `SketchPlaneResolver` accepting a resolved entity that is not a face, and `SketchPlaneReference.ReferencedFeatures` reporting a dependency on `FeatureId.None`. All five failed the intended test and nothing else.)*
- [~] **P4-T11** External references: project/convert/intersect 3D edges into the sketch as parametric links that update on rebuild. *(Split the same way P4-T10 is: `SketchExternalReference` is the durable half -- a `PersistentName` plus which of the three operations, in `OpenMCAD.Modeling` -- and `SketchExternalReferenceResolver` turns one into fresh `SketchEntity` geometry against an already-resolved `SketchPlane` for a single rebuild. It is a live parametric link rather than a one-shot copy for the same reason `SketchPlaneReference` is a name and not coordinates (§5.3): the whole point of "project" over "draw the same shape by hand" is that it keeps following the edge. `Produces` -- the target `SketchEntityId` -- is assigned once and never afterwards, so a constraint attached to the produced geometry, or a later feature naming it, keeps pointing at the same entity while its geometry is replaced every rebuild. Curve geometry comes from a caller-supplied `Func<SubEntity, WorldCurve?>`, the same shape P4-T10's `Func<SubEntity, Plane?>` takes its face geometry from and for the same reason: nothing in `OpenMCAD.Kernel` exposes a curve query yet, and this is what a real one will eventually satisfy. `WorldCurve` itself lives in `OpenMCAD.Math` alongside `Plane`, as the bare geometric fact a kernel curve query would report -- an analytic shape plus a parameter range -- rather than under `OpenMCAD.Modeling` with the naming machinery that consumes it, because nothing about "here is a line" or "here is a circular arc" needs a document or a name in order to be meaningful. Deliberately not every curve: only straight and circular edges are handled, and everything else -- ellipses, splines, conics -- reports `Unsupported` rather than being silently approximated, the same choice P4-T14 already made for profile detection's untraceable curves. A circle only projects or converts when its own plane is parallel (or antiparallel) to the sketch plane; a circle projected onto a transverse plane is an ellipse in general, which is a real operation but a second one this does not attempt. `Intersect` is scoped to straight edges only, because a line crosses a plane at zero or one point (unambiguous) while a circular edge can cross at zero, one or two, and "one external reference produces one sketch entity" -- deliberately, for the same reason `EntityReference`'s `MultiplicityPolicy` exists for kernel topology -- has nowhere to put a second point without inventing that policy here too; left for when it is needed rather than half-built now. `Convert` is `Project` plus a precondition: the edge must already lie on the sketch plane (checked against `OpenMCAD.Math.Plane.Contains`), or it refuses with `NotInPlane` rather than silently doing what `Project` would have done -- the two tools promise different things to a user, and `Convert` accepting an off-plane edge would make that promise false. The trickiest piece is a circular edge whose plane is antiparallel to the sketch plane's: viewing the same physical arc from the opposite side reverses which rotational sense reads as anticlockwise, and `SketchArc` can only ever describe an anticlockwise sweep (P4-T03), so the only way to represent the same short physical arc is for the produced entity's Start and End to swap relative to the edge's own -- confirmed by tracing the same two physical points as the parallel case and checking they land in the opposite order with the same (not reflex) sweep, rather than trusting the algebra alone. One real bug the tests caught before they ever ran against production code: `WorldCurve.Circle.Full` reports its sweep as a full turn via `(0, 2*pi)`, and `Sweep`'s modulo -- copied from `SketchArc.Sweep` on the reasonable assumption it was the same problem -- wraps a full turn to exactly zero, indistinguishable from a zero-length arc reported the same way; `IsFull` now checks the raw angular span instead of the wrapped one. Five sabotages, each failing the intended test and nothing else: the antiparallel Start/End swap, `Convert`'s in-plane check for both a line and a circle, `Intersect`'s own-extent check, and the edge-not-vertex kind check.)*
- [~] **P4-T12** Sketch dimensions: linear, aligned, angular, radial, diametric, ordinate; placement, display, and editing. *(Most of "aligned", "radial" and "diametric" already existed -- P4-T04's `Distance`, `Radius` and `Diameter` constraints measure exactly those, driving or reference, and needed nothing new. What was actually missing were the two pieces the constraint model alone cannot answer: a distinct "linear" measurement, and where a dimension's line and text actually go. `ConstraintKind.HorizontalDistance`/`VerticalDistance` are the first -- a linear dimension measures one axis regardless of which way the two points actually lie from each other, which is a different equation from `Distance`'s hypotenuse whenever they are not already axis-aligned, not merely a different display of the same number. Both are unsigned, the same convention `Distance` already uses: which point a user clicked first is an accident of drawing order, not a claim about which side of the other it sits on. `SketchDimension` (a placement: which constraint, where the witness point is) and `SketchDimensionLayout` (witness lines, dimension line and text position, resolved fresh from the current geometry every time) are the second, split the same way `SketchPlaneReference`/`SketchPlane` are in `OpenMCAD.Modeling` (P4-T10): a placement choice is durable, where the line ends up today is not. `SketchDimensionLayout` reads its displayed value from the live geometry rather than from `SketchConstraint.Value`, which matters for a reference dimension (a driving one's value and its geometry agree only once the solver has actually run since the value last changed) and is what "display" asks for -- the current truth, not the target. "Editing" needed no new mechanism: a driving dimension's value is `SketchConstraint.Value`, already changeable via `ConstraintSet.With` (P4-T04), and a value edit is a rebuild like any other. Deliberately incomplete, and marked so rather than claimed: layout covers only the point-to-point kinds (`Distance`, `HorizontalDistance`, `VerticalDistance`) -- `Distance`'s point-to-line operand shape, `Angle`, `Radius` and `Diameter` each need real, separate geometry (an arc radius picked from the witness point for an angle; a leader direction and an inside-or-outside decision for a radius or diameter) that three-sevenths of a general solution would not have been better than not building. Ordinate dimensioning is not a fourth layout at all -- the number it shows against a shared baseline is exactly what `HorizontalDistance` or `VerticalDistance` already measures from that baseline point, and only the presentation (one shared extension line, several dimension lines stacked to avoid colliding text) differs; that stacking is a layout problem across several dimensions at once, which a single dimension's signature has nowhere to put, and is left for when several exist together rather than half-built now. Two sabotages, both caught while writing the tests rather than needing to run them wrong first: an early version checked "do both operands resolve to a point" before checking the constraint's kind, which reported `Angle` and `Radius` -- whose operands name whole entities via `EntityPoint.Self` and so never resolve to a point at all -- as their geometry having vanished rather than as the unsupported kinds they are; fixed by dispatching on kind first. Two more sabotages by reverting an actual guard: the entity-exists check that tells a deleted point apart from `Distance`'s unsupported point-to-line shape, and the coincident-points check in the aligned layout, which without it does not throw -- it silently returns NaN geometry as `Resolved`.)*
- [~] **P4-T13** Sketch editing tools: trim, extend, offset, mirror, linear/circular sketch pattern, fillet/chamfer corner, split, convert to construction, scale, move, rotate, copy. *(Seven of the eleven tools are, underneath, the same operation: apply one `SketchTransform` to a selection. Move, rotate and scale edit the selected entities in place; copy, mirror and the two patterns instead add a transformed *copy*, keeping the original -- which is not a detail, it is the actual difference between "drag this" and "place another one of these", and the two behaviours share `SketchGeometryTransform` (maps one entity's geometry through a transform) rather than one deciding what the other means. `SketchTransform` is the sketch-plane counterpart of `OpenMCAD.Math.Transform`: scale, optional reflection, rotation, translation, in that order, with reflection kept as a flag rather than a negative scale so `ScaleAbout` can reject a nonsensical factor instead of quietly also mirroring. `MirrorAbout` rests on the standard identity that reflection about a line through the origin at angle θ is `Rotate(2θ) ∘ Flip` -- checked against three independent axis-aligned cases before trusting it for the general one. `Duplicate` -- what copy, mirror and each pattern instance all call -- keeps a copied selection's own internal constraints (remapped onto the new entities, when every entity a constraint names is in the copied set) and drops the rest: duplicating "concentric with that fixed hole" onto every pattern instance would point all of them at the one fixed hole, which is a contradiction the moment there is more than one instance, not a pattern. `LinearPattern`/`CircularPattern`'s `count` is the total number of instances including the original, matching what a pattern dialog actually asks for, and the circular one spaces instances at `totalAngle / count` rather than `/ (count − 1)`, so a full-circle bolt-circle pattern lands evenly rather than leaving a gap where an unrequested closing instance would sit. An arc under a transform that reflects needs its Start and End swapped to stay describable at all -- `SketchArc` can only ever sweep anticlockwise (P4-T03), a reflection reverses which way increasing angle turns, and the identical conclusion P4-T11 already reached for a circular edge viewed from the far side of a sketch plane is reused here rather than re-derived. Scoped to point, line, circle and arc; ellipse, elliptical arc, parabola and hyperbola each need their own angle handling worked out with that same care and report `Unsupported` rather than silently wrong geometry, the same choice P4-T11 made for a curve kind it does not project. Offset, split, fillet and chamfer are not attempted -- each is a curve-topology operation (building and re-trimming a parallel curve at every corner, inserting a blend whose neighbours' endpoints move to meet it) with essentially nothing in common with "map a transform over a selection". Five sabotages, each failing the test that names the exact property it removed: the doubled-angle identity in `MirrorAbout` (caught by three of five mirror tests at once), the Start/End swap on a reflected arc, `Duplicate`'s all-operands-copied requirement (weakened to any, which throws trying to remap an entity never copied -- itself a finding, since the honest failure here is louder than a silently wrong constraint would have been), and an off-by-one in each pattern's loop bound.

`SketchTrim` (added later) is the eighth: it stands on `Crossings` (P4-T09), already built with this in mind. It shortens an entity to the nearest crossing(s) on the side of a click and, deliberately, never splits one into two -- a real trim deletes the clicked segment and keeps what remains, which is two separate pieces when there is a crossing on both sides of the click, and producing that second piece is not the hard part; deciding which of the original entity's constraints travel with which piece is, since a split is not a copied set the way `Duplicate`'s callers are and a constraint on the far end plainly belongs with only one resulting piece. `WouldSplit` reports that honestly rather than guessing, and the common case -- cutting back an overshoot to the nearest intersection -- has only one side to keep and works today. A circle never needs that refusal at all, and not as a bolted-on special case: removing one arc from a closed loop always leaves exactly one connected piece however many other crossings sit on it, so trimming a circle at two or more crossings resolves every time and becomes the one `SketchArc` that was not clicked on. Getting "which side of the click survives" right took a genuine correction: the first line-trim tests were written backwards (asserting that clicking *before* a crossing shortens the far end, when a real trim tool deletes the segment the click is *on* and so shortens the *near* end) -- caught because the tests failed, not because the code did; the arc tests, written with the same care the second time round, matched the always-correct code from the start. Two more sabotages, both caught by reverting a real guard: swapping which bracket side sets `Start` versus `End` on a line, and swapping `Min`/`Max` in the circle's wrap-around bracket search, each failing exactly the tests for the case it broke.

`SketchExtend` is the ninth, and scoped to lines only -- circle and arc are a genuinely open question here, not merely unimplemented: growing an arc's sweep changes what it looks like everywhere along it while its radius stays put, and nothing here yet has an answer for which end a click should mean once a curve bends back over itself, so it stays `Unsupported` rather than guessed at. A line's own intersection maths deliberately does not reuse `SketchSnapping`'s `LineLine`/`LineCircle` -- those bound *both* curves to what is actually drawn, which is exactly backwards for extending, where the whole point is that one of the two curves is not there yet -- so both are rewritten here bounding only the target, as a signed distance along the extending line's own direction so every candidate (line, circle, or the subset of a circle's crossings that an arc's sweep actually covers) competes for "nearest" on one scale. Three sabotages, each caught by reverting the exact comparison it depended on: swapping `Min`/`Max` for which end extends forward versus backward, and dropping the arc-sweep filter entirely -- which the same circle two candidates away catches immediately, since without the filter the nearer-but-off-the-arc point wins instead of the further-but-actually-on-it one.)*
- [x] **P4-T14** Profile detection: closed-region finding for extrudes, with region selection for multi-region sketches. *(The sketch is treated as a planar arrangement: every curve is cut where anything crosses it, the pieces become the edges of a graph, and the bounded faces of that graph are the regions. That is more work than following chains of coincident endpoints and it is the only way to get the case users actually draw -- two overlapping rectangles, where none of the three regions is a shape anybody drew and all three are things a user can extrude. The face walk is the standard rule: arriving at a vertex, leave by the edge that turns most sharply clockwise from the way you came. Tangents at a join come from the curve rather than the chord, which matters exactly where two curves meet tangentially -- a fillet against the line it was made from, the commonest join in a real sketch. Areas carry their sign, because the sign is what tells an outer boundary from the circuit that runs round the outside of everything, and nobody can extrude the outside of a drawing. An arc contributes the sliver it cuts off its own chord, or a circle has an area of zero, being a polyline through the two points it got cut at; a major arc contributes the rest of the circle instead, and taking the small piece there does not merely get the area wrong, it makes the region negative and it disappears. Segments remember which curve they came from and how much of it, because a profile goes to a kernel that needs to build a curve rather than a polyline passing through the same places. Geometry in no region is reported rather than ignored, since 'why is my extrude not offering this' is the commonest question a sketcher has to answer; construction geometry is not reported, because it was never a candidate. Splines and conics are named as untraceable rather than dropped: they can bound a region in principle and cutting them needs an intersector P4-T09 does not have. One real bug, found by a failing area rather than by reasoning: containment was asked about a corner, and two regions that share an edge share its corners, so a point-in-polygon test about a point on its own boundary answered by rounding -- the shared region of two overlapping squares became a hole of one of them and four units of area vanished. It asks about a point stepped off the middle of an edge now. Thirteen sabotages, eleven failing the right test at once and two exposing that no test had a segment covering more than half a circle, nor a sketch whose walk order differed from its area order.)*
- [~] **P4-T15** Sketch UI: entity toolbar, constraint palette, DOF readout, conflict highlighting, "fully defined" indication. *(The view model, in `OpenMCAD.ViewModels`, and only the view model -- no XAML, no canvas, no mouse handling. That split is not a shortcut; it follows `MainWindowViewModel`'s own established pattern exactly: the property manager is a placeholder until P6-T04 and the viewport was one until P2-T01/T02, both because ADR-0007's framework-agnostic view-model layer is meant to be built and tested against a real requirement before the shell chrome it needs (docking, the ribbon, P6-T01/T02) exists to host it, and a bespoke sketch canvas has that same dependency. `SketchEditorViewModel` is what a toolbar button or a palette click actually *does*, independent of anything drawing it: `AddPoint`/`AddLine`/`AddCircle`/`AddArc` add geometry and re-solve; `ApplyConstraint` builds a constraint from the current selection, validates it the same way `Sketch.Problems` would (so the palette and a saved file can never disagree about what is valid), and rolls back rather than leaving a rejected constraint dangling; `StatusText` and `IsFullyDefined` are the DOF readout and the "fully defined" indication, the second true for `Redundant` as well as `WellConstrained` because a redundant sketch is geometrically exactly as defined as a well-constrained one -- it merely has a constraint doing nothing, which `RedundantConstraints` is what names; `ConflictingConstraints`/`RedundantConstraints`/`FreeEntities` are what a future canvas would style as highlighted, read straight off `SolveDiagnosis` rather than recomputed. No `System.Windows` type appears anywhere in it, which `tests/arch`'s `NoWpfTypeAppearsInViewModels` already enforces and this is simply one more type it now covers; commands are plain methods rather than `ICommand`, the same choice `PluginCommandItem.Invoke` already made as a bare delegate. `OpenMCAD.ViewModels` gained one new project reference, to `OpenMCAD.Solver` (layer 1, well below `OpenMCAD.ViewModels` at layer 8) -- the solver itself is injected as `ISketchSolver` rather than defaulted to `FakeSolver`, so this assembly commits to the interface ADR-0006 promises a swap behind and not to which implementation answers it today. Three sabotages, each failing exactly the test that named the property removed: dropping `Redundant` from `IsFullyDefined`'s definition, committing a rejected constraint instead of rolling it back, and leaving a removed entity in the selection.)*
- [x] **P4-T16** Sketch corpus fixtures: convergence, diagnosis, drag stability, degenerate inputs. *(`tests/regression/corpus/sketch/`, seven fixtures, run by a new `SketchCorpusTests` in `OpenMCAD.Solver.Fake.Tests` rather than by `OpenMCAD.Regression` -- that runner's fixture schema is kernel operations and mass properties, which has nothing in it for a sketch: no kernel, no body, no boolean. Each fixture is `SketchFormat`'s own JSON interchange form (P4-T04, whose own remarks already named this corpus as the reason that form exists) plus a small `expected.json` this class reads directly. Entity and constraint ids are small sequential GUIDs rather than random ones, so `expected.json` can name them by hand and stay legible to whoever reads the fixture next -- a corpus fixture is read years later and a random id would mean nothing by then either way. One convergence fixture (a 3-4-5 triangle, closed by three coincidences with the third side left undimensioned so its shape has to fall out of the other two closing the loop, solved from a badly perturbed starting guess) and four diagnosis fixtures, one per outcome that can be produced reliably by hand: under-constrained (one point fixed, a second held five units off by a bare Distance), over- and redundant (the same two points, two Distance constraints agreeing or not), and Failed -- forced deterministically by giving an ordinarily solvable sketch zero iterations to work with, rather than needing a genuinely pathological configuration nobody could reason about. `SolveOutcome.WellConstrained`'s neighbour that is not attempted here as a distinct fixture: a real non-convergence from a merely bad initial guess would be at the mercy of exactly how well this solver happens to cope with bad guesses, which is a property of the implementation rather than of the sketch, and the zero-iteration fixture asks the honest question instead. One drag-stability fixture mirrors P4-T07's own tests: a line with its start fixed and its length dimensioned, dragged a long way off, checked for the dimension holding rather than for where the free end lands (which the minimal-motion objective decides, not something this corpus can predict by hand). One degenerate-input fixture -- a zero-radius circle -- is caught by `Sketch.Problems` before anything is solved, and asks for no solve at all. One real, if narrow, finding while writing the under-constrained fixture: `SolveDiagnosis.Free` lists every entity in the affected group, including a fixed one that individually has no freedom left -- confirmed by reading both places `FakeSolver` builds it, not assumed, since asserting the wrong thing here would have been the corpus lying about what it had verified. Four sabotages, one per corpus-checking code path (a position, a distance, a `Sketch.Problems` substring, and a conflicting-constraint id), each failing exactly the fixture whose expectation was corrupted and no other.)*

---

### Phase 5 — First Light (the vertical slice)

**Goal.** ADR-0009 made real. A thin but genuinely end-to-end path through the entire product. **This is the phase that proves the architecture.** Depth is explicitly not the objective; connectivity is.

**Effort:** 6–10 engineer-weeks.

**Exit criteria — a single scripted demo, run in CI:**

1. Create a part, sketch a constrained profile, extrude it.
2. Add a second feature that references a face of the first (a fillet), then change a sketch dimension and confirm the fillet survives (naming).
3. Create a second part; create an assembly; insert both; add two mates; solve.
4. Create a drawing; place a front view of the assembly; add one dimension; confirm the dimension updates when the part changes.
5. Export the assembly to STEP; reimport; confirm mass properties match within tolerance.
6. Save everything, close, reopen, confirm identical state.

**Tasks.**

- [ ] **P5-T01** Part document type, feature tree UI (basic), and the extrude and revolve features end to end (sketch → kernel → named result → display).
- [ ] **P5-T02** Constant-radius fillet and chamfer features with named edge references.
- [ ] **P5-T03** Datum planes, axes, and points as named, resolvable reference geometry.
- [ ] **P5-T04** Assembly document type: component definitions vs. occurrences (§5.9), occurrence tree, transforms.
- [ ] **P5-T05** `IAssemblySolver` and the minimal mate set: coincident, concentric, distance; grounded components; DOF reporting.
- [ ] **P5-T06** Component insertion, placement, and drag with live mate solving.
- [ ] **P5-T07** Drawing document type: sheet, sheet format, a single standard orthographic view via OCCT HLR.
- [ ] **P5-T08** Associative 2D curve output tagged with source 3D entities; one dimension type attached associatively.
- [ ] **P5-T09** Drawing view caching and invalidation on model change.
- [ ] **P5-T10** STEP AP242 export and import through `OpenMCAD.Exchange`, with unit handling and assembly structure preservation.
- [ ] **P5-T11** Cross-document references: assembly → part, drawing → model, with external-reference tracking and out-of-date detection.
- [ ] **P5-T12** File open/save/new/recent UI; document session management; dirty tracking.
- [ ] **P5-T13** The scripted end-to-end demo as a CI integration test, plus the corresponding corpus fixtures.
- [ ] **P5-T14** **Architecture retrospective.** Write `docs/notes/phase5-retro.md`: what the slice exposed, what must change before depth work begins. Fix those things *now*, before Epoch B. This task is the entire point of the phase — do not skip it.
---

# EPOCH B — CORE PRODUCT

*Goal of the epoch: stop being a demo. Become an application a mechanical engineer would choose to use for real work.*

---

### Phase 6 — Shell productization

**Goal.** The application around the geometry. Users judge the product here long before they judge the kernel.

**Effort:** 10–14 engineer-weeks.

**Exit criteria.** A new user can discover and run every implemented command without documentation; a crash loses at most the in-flight operation; the UI is fully keyboard-navigable and localizable.

**Tasks.**

- [ ] **P6-T01** Ribbon with contextual tabs, a searchable command palette, and a full `ICommand` registry (id, name, icon, shortcut, enablement predicate, undo grouping).
- [ ] **P6-T02** Docking layout: feature tree, property manager, viewport, task panes, output/rebuild report; persisted per-user layouts with reset.
- [ ] **P6-T03** **Feature tree**: virtualized, drag-to-reorder (with dependency validation), suppress/unsuppress, rollback bar, folders, search, error and warning badges, multi-select.
- [ ] **P6-T04** **Property manager** generated from `FeatureSchema` — this is why P3-T21 exists. Live preview, apply/cancel semantics, unit-aware numeric input with expression entry.
- [ ] **P6-T05** Name-repair UI for tier-3 naming failures (P3-T11): "Reselect the missing edge", with the failed reference described in human terms.
- [ ] **P6-T06** Selection system: filters (face/edge/vertex/body/feature/component), box/lasso select, select-through, select-tangent-chain, select-loop, selection sets, and a selection breadcrumb.
- [ ] **P6-T07** Measure tool (point-to-point, edge length, angle, radius, area, min-distance between entities), section view, mass-properties dialog with material assignment.
- [ ] **P6-T08** Appearances and materials: library, per-body/per-face assignment, density driving mass properties.
- [ ] **P6-T09** Autosave with transaction journaling, crash handler with minidump, session recovery on next launch (§6.2).
- [ ] **P6-T10** Settings and options infrastructure: per-user and per-document, with import/export.
- [ ] **P6-T11** Configurable shortcuts and mouse gestures; ship a SolidWorks-compatible profile (§6.4).
- [ ] **P6-T12** Localization infrastructure: all strings in resources, an analyzer banning literals in VMs, one non-English locale wired up to prove the pipeline.
- [ ] **P6-T13** Accessibility pass: keyboard navigation, screen-reader labels, high-contrast theme, focus visuals.
- [ ] **P6-T14** Light/dark theming and a document-tab/window management model (multi-document, multi-monitor).
- [ ] **P6-T15** UI smoke test harness: launch, open every sample, run the command set, screenshot-compare.

---

### Phase 7 — Part modeling depth

**Goal.** The full solid-modeling feature catalogue from §5.7. The largest single body of work in the plan.

**Effort:** 20–30 engineer-weeks.

**Exit criteria.** Every feature in the catalogue exists, has a schema-driven property UI, has naming corpus coverage, and appears in the regression corpus with golden mass properties.

**Tasks.** *Each task means: kernel operation(s) in the shim, feature class + schema, naming roles, property UI, corpus fixtures, docs.*

- [ ] **P7-T01** Extrude/cut end conditions: blind, through-all (both directions), up-to-vertex, up-to-surface, up-to-body, offset-from-surface, midplane, two-direction, draft-while-extruding, thin feature.
- [ ] **P7-T02** Revolve: angle, midplane, two-direction, thin; axis selection rules; degenerate-axis validation.
- [ ] **P7-T03** Sweep: path + profile, guide curves, twist control, profile orientation (follow path / keep normal), path alignment.
- [ ] **P7-T04** Loft: multiple profiles, guide curves, centerline, start/end tangency and curvature continuity, ruled/smooth options.
- [ ] **P7-T05** Boundary/boundary-blend feature (two-direction curve network).
- [ ] **P7-T06** Hole feature with a standard fastener table: simple, counterbore, countersink, tapped, tapered; ANSI/ISO/DIN/JIS series; thread callout metadata for drawings; positioning sketch.
- [ ] **P7-T07** Fillet depth: variable radius, face fillet, full-round, setback corners, tangent propagation, multi-radius, conic/curvature-continuous. **Exercise the retry ladder hard here.**
- [ ] **P7-T08** Chamfer depth: distance-distance, distance-angle, vertex chamfer, tangent propagation.
- [ ] **P7-T09** Shell: uniform and multi-thickness, faces to remove, inward/outward, shell-before/after-fillet ordering guidance.
- [ ] **P7-T10** Draft: neutral plane, parting line, step draft.
- [ ] **P7-T11** Rib, dome, wrap (emboss/deboss/scribe), flex/deform, indent.
- [ ] **P7-T12** Patterns: linear (two-direction, instances-to-skip, vary-instance), circular, curve-driven, sketch-driven, table-driven, fill pattern, pattern-of-pattern, pattern seed reuse.
- [ ] **P7-T13** Mirror (feature, face, body), scale (uniform and non-uniform).
- [ ] **P7-T14** Multi-body: combine (add/subtract/common), split, move/copy body, delete/keep body, local operations scoped to selected bodies, body folders.
- [ ] **P7-T15** Direct-edit operations on the history tree: move face, offset face, delete face and heal, replace face.
- [ ] **P7-T16** Feature reordering, suppression, and rollback interactions across the whole catalogue — with naming corpus cases for every one.
- [ ] **P7-T17** Feature validation pre-flight (`IFeature.Validate`) for every feature, so obviously invalid input is rejected with a good message before the kernel is touched.
- [ ] **P7-T18** Performance pass on rebuild with 200+ feature parts against §7 budgets.

---

### Phase 8 — Sketcher depth

**Goal.** A sketcher people prefer. Comparatively small, disproportionately visible.

**Effort:** 6–10 engineer-weeks.

**Tasks.**

- [ ] **P8-T01** Spline tooling: control-polygon and through-point editing, curvature combs, tangency/curvature handles, fit-spline, simplify.
- [ ] **P8-T02** Conics, slots (straight/arc, centerpoint/3-point), polygons, text (with font outlines converted to curves).
- [ ] **P8-T03** Advanced constraints: symmetry about a line, curve-on-curve, equal curvature, pattern constraints, driven (reference) dimensions.
- [ ] **P8-T04** Sketch blocks: reusable, instanceable, with their own internal solve.
- [ ] **P8-T05** Solver robustness: better initial guesses, restart strategies on non-convergence, redundancy elimination that suggests which constraint to drop.
- [ ] **P8-T06** 3D sketch: entities in space, plane-relative constraints, path creation for sweeps and routing.
- [ ] **P8-T07** Sketch diagnostics UI: what-is-under-defined visualization, drag-to-discover-DOF, conflict resolution assistant.
- [ ] **P8-T08** Import sketch geometry from DXF/DWG with cleanup (dedupe, gap healing, auto-constrain).

---

### Phase 9 — Assembly depth

**Goal.** Assemblies that hold up at real scale and real complexity.

**Effort:** 16–24 engineer-weeks.

**Exit criteria.** A 5,000-component assembly opens in lightweight mode within budget, mates solve interactively, and interference detection completes without exhausting memory.

**Tasks.**

- [ ] **P9-T01** Full mate set: parallel, perpendicular, tangent, angle, lock, width, symmetric, path, profile-center, plus limit mates.
- [ ] **P9-T02** Mechanical mates and joints: revolute, slider, cylindrical, ball, planar, fixed, gear, rack-and-pinion, screw, cam-follower, universal.
- [ ] **P9-T03** Subassemblies: rigid vs. flexible, nested transforms, subassembly-level solve, promoting/demoting components.
- [ ] **P9-T04** Assembly-solver decomposition into independent clusters; performance work so solve time is not quadratic in component count.
- [ ] **P9-T05** Component patterns (linear, circular, feature-driven, mirror components with proper handedness) and derived/mirrored parts.
- [ ] **P9-T06** In-context design: external references, out-of-date indication, lock/break/unlock, cycle detection at commit, an explicit hazard UI (§5.9).
- [ ] **P9-T07** **Display modes:** Resolved / Lightweight / Graphics-only; large-design-review mode; selective resolve.
- [ ] **P9-T08** Interference detection, clearance verification, hole alignment check, collision detection during drag.
- [ ] **P9-T09** Exploded views with animated steps and explode lines; assembly motion/mechanism preview.
- [ ] **P9-T10** Assembly features (cuts and holes applied at assembly level, scoped to selected components).
- [ ] **P9-T11** BOM data model: item numbers, quantities, per-configuration rules, custom columns, roll-up through subassemblies.
- [ ] **P9-T12** Envelope/reference components, virtual components, and component visibility/appearance overrides per occurrence.
- [ ] **P9-T13** Assembly corpus fixtures at 100 / 1,000 / 5,000 components with perf gates.

---

### Phase 10 — Drawings and detailing depth

**Goal.** Drawings a manufacturer would accept. Budget generously; this is always underestimated.

**Effort:** 20–30 engineer-weeks.

**Tasks.**

- [ ] **P10-T01** Full view set: section (full, half, offset, aligned, broken-out, section-of-section), detail, auxiliary, crop, broken, alternate-position, exploded, empty.
- [ ] **P10-T02** Projection-angle handling (first/third), view alignment rules, view scale inheritance, view arrangement tools.
- [ ] **P10-T03** Draft (tessellated) view mode for interactive work with exact HLR regeneration on demand; view caching and invalidation.
- [ ] **P10-T04** Tangent-edge display options; thread and cosmetic-thread rendering; hidden-line display modes.
- [ ] **P10-T05** Dimension suite: linear, aligned, angular, radial, diametric, ordinate, baseline, chain, chamfer, arc-length; auto-dimension; dimension palette; tolerance display (bilateral, limit, symmetric, fit).
- [ ] **P10-T06** Associativity hardening: dimensions survive model changes, or fail visibly. Corpus cases for every view and dimension type.
- [ ] **P10-T07** GD&T: feature control frames per ASME Y14.5 and ISO 1101, datums and datum targets, composite frames, validation of frame syntax.
- [ ] **P10-T08** Annotations: notes with leaders, surface finish, weld symbols, hole callouts, balloons and stacked balloons, centerlines, center marks, revision clouds, blocks.
- [ ] **P10-T09** Tables: BOM (with the Phase 9 data model), hole table, revision table, general table, weldment cut list, design table view.
- [ ] **P10-T10** Sheets and formats: templates, title blocks bound to document properties, multi-sheet, layers, line fonts and weights, drawing standards (ASME/ISO) as a document setting.
- [ ] **P10-T11** Output: vector PDF, DXF/DWG export, true-scale printing, batch publish.
- [ ] **P10-T12** Drawing performance: large multi-sheet documents, view regeneration budgets.

---

### Phase 11 — Data exchange and PMI

**Goal.** Interoperate well enough that a customer can actually adopt the product.

**Effort:** 12–18 engineer-weeks.

**Exit criteria.** A round-trip corpus of third-party STEP files imports, rebuilds display, exports, and reimports with mass properties within tolerance and no invalid shapes.

**Tasks.**

- [ ] **P11-T01** STEP AP203/AP214/AP242: import and export with assembly structure, units, colors, names, and layers.
- [ ] **P11-T02** PMI: semantic and graphical, on STEP AP242 export and import.
- [ ] **P11-T03** IGES import/export; surface-model handling and sewing into solids.
- [ ] **P11-T04** DXF/DWG import/export for 2D (sketches and drawings), with layer and block mapping.
- [ ] **P11-T05** Mesh formats: STL (binary/ASCII), 3MF, OBJ, PLY — import as mesh bodies, export with tessellation control.
- [ ] **P11-T06** glTF/GLB and USD export for visualization and downstream use.
- [ ] **P11-T07** Parasolid (X_T/X_B) and ACIS (SAT) import via OCCT where licensing permits; otherwise document the gap honestly.
- [ ] **P11-T08** Imported-geometry workflow: healing, sewing, gap analysis, face merging, solidification, and **feature recognition** (identify holes, fillets, extrudes in dumb solids) — a genuine differentiator, worth real investment.
- [ ] **P11-T09** Imported bodies as feature-tree citizens with stable naming, so downstream features can reference them.
- [ ] **P11-T10** Exchange corpus: third-party files including deliberately malformed ones; assert no crash, useful diagnostics, and validity of results.

---

# EPOCH C — ADVANCED DOMAINS

---

### Phase 12 — Sheet metal

**Effort:** 12–18 engineer-weeks.

- [ ] **P12-T01** Sheet-metal document context: material thickness, bend radius, K-factor / bend-allowance / bend-deduction tables.
- [ ] **P12-T02** Base flange, edge flange, miter flange, hem, jog, tab, closed corner.
- [ ] **P12-T03** Sketched bend, cross-break, forming tools/punches, louvers and lances.
- [ ] **P12-T04** Corner treatments, bend reliefs (rectangular, obround, tear), rip.
- [ ] **P12-T05** **Flat pattern** generation with bend lines, bend notes, and bend order; flat-pattern-only features.
- [ ] **P12-T06** Convert-to-sheet-metal from a solid body; unfold/fold for machining features across bends.
- [ ] **P12-T07** Sheet-metal drawing views: flat pattern view, bend table, DXF export for laser/punch.
- [ ] **P12-T08** Sheet-metal corpus with flat-pattern area/perimeter golden values.

---

### Phase 13 — Surfacing and advanced modeling

**Effort:** 16–24 engineer-weeks.

- [ ] **P13-T01** Surface creation: extruded, revolved, swept, lofted, boundary, planar, offset, ruled, radiate, filled.
- [ ] **P13-T02** Surface editing: trim, untrim, extend, knit/sew, delete-and-patch, move face, replace face.
- [ ] **P13-T03** Thicken, cut-with-surface, replace-face-with-surface, solid↔surface conversion.
- [ ] **P13-T04** Curve tooling: 3D curves, projected curves, composite curves, intersection curves, helix/spiral, curve-through-points/XYZ.
- [ ] **P13-T05** Continuity control (G0/G1/G2) across surface boundaries, with curvature and zebra analysis.
- [ ] **P13-T06** Analysis tools: zebra stripes, curvature map, draft analysis, deviation analysis, undercut detection, thickness analysis.
- [ ] **P13-T07** Mold tooling: parting lines, shut-off surfaces, parting surfaces, core/cavity split, side cores.
- [ ] **P13-T08** Weldments: structural member library, trim/extend, gussets, end caps, weld beads, cut lists.
- [ ] **P13-T09** Surfacing corpus with continuity and validity assertions. **Expect OCCT weakness here** — document what fails and consider it evidence for or against a future Parasolid migration.

---

### Phase 14 — Configurations, families, and design automation

**Effort:** 10–16 engineer-weeks.

- [ ] **P14-T01** Configurations: parameter overrides, feature/component suppression, per-configuration properties, derived configurations.
- [ ] **P14-T02** Design tables (spreadsheet-driven configurations) with import/export and live linking.
- [ ] **P14-T03** Global variables, equations manager, cross-document parameter links with a dependency viewer.
- [ ] **P14-T04** Part/assembly families and a library of standard components (fasteners, bearings, profiles) with configuration-driven sizing.
- [ ] **P14-T05** Custom and configuration-specific properties, driving drawings and BOM.
- [ ] **P14-T06** Design-table and configuration effects on drawings, BOMs, and exchange.
- [ ] **P14-T07** **Sketch-solver decision point** (ADR-0006 revisit): with real usage data, decide whether to write a managed solver, license DCM, or stay on planegcs. Write the ADR either way.

---

# EPOCH D — SCALE AND SHIP

---

### Phase 15 — Performance, scale, and robustness

**Effort:** 14–20 engineer-weeks.

- [ ] **P15-T01** Large-assembly work: on-demand loading, occurrence-level LOD, graphics-only mode hardening, memory profiling against the §7 budget.
- [ ] **P15-T02** Multi-threaded rebuild above the dispatcher; measure how much of the remaining time is kernel-serial.
- [ ] **P15-T03** **Kernel worker pool** (ADR-0004 escape hatch): N isolated kernel threads for embarrassingly parallel work — tessellation, HLR of independent views, batch import. Gate on a passing isolation stress test.
- [ ] **P15-T04** Evaluate out-of-process kernel hosting (§6.2) against crash telemetry; implement only if justified.
- [ ] **P15-T05** GPU work: occlusion culling, mesh LOD streaming, instancing improvements, frame-time profiling HUD.
- [ ] **P15-T06** Startup time: trimming, ReadyToRun/AOT where the TFM allows, lazy subsystem initialization.
- [ ] **P15-T07** Robustness sweep: run the full fuzz suite long-soak, fix the top failure clusters, expand the retry ladder where the data says it pays.
- [ ] **P15-T08** Memory: pooling for mesh buffers, cache eviction tuning, leak detection in the native shim under sustained load.
- [ ] **P15-T09** Formalize the perf gate in CI with historical trend tracking, not just pass/fail.

---

### Phase 16 — Extensibility and ecosystem

**Effort:** 10–14 engineer-weeks.

- [ ] **P16-T01** Public API v1.0: freeze the surface, publish the baseline, write the semver policy, ship reference documentation.
- [ ] **P16-T02** Custom feature types from plugins — third parties can add features that participate fully in the rebuild DAG and naming.
- [ ] **P16-T03** UI extensibility: ribbon and menu contributions, task panes, property-manager pages, custom viewport overlays.
- [ ] **P16-T04** Event model: pre/post rebuild, open, save, selection change, document lifecycle; with cancellation where sensible.
- [ ] **P16-T05** Scripting host (C# scripting and/or Python via a bridge) over the same public API.
- [ ] **P16-T06** Batch/headless mode maturity in `OpenMCAD.Cli`: convert, rebuild, publish drawings, run design tables, generate BOMs.
- [ ] **P16-T07** PDM integration hooks: file metadata, check-in/out awareness, where-used queries, reference resolution through a provider interface.
- [ ] **P16-T08** Sample plugins, a plugin project template, and an SDK package.

---

### Phase 17 — Productization and release

**Effort:** 12–18 engineer-weeks.

- [ ] **P17-T01** Installer (MSIX and/or WiX), silent/enterprise deployment, per-machine and per-user modes, file associations, Explorer thumbnail and preview handlers.
- [ ] **P17-T02** Update mechanism with staged rollout and rollback.
- [ ] ~~**P17-T03** Licensing and activation (node-locked and floating), trial mode, offline activation.~~ **Out of scope** — the project is MIT licensed (ADR-0017); there is no proprietary distribution to gate.
- [ ] **P17-T04** Crash reporting service and telemetry backend (§6.3), with the disclosed schema.
- [ ] **P17-T05** In-app onboarding, tutorials, sample documents, contextual help; user documentation.
- [ ] **P17-T06** Localization: complete the string catalogue, ship two to three locales, verify layout under text expansion.
- [ ] **P17-T07** Code signing, SBOM generation, `THIRD-PARTY-NOTICES.md` verification, supply-chain scanning in CI.
- [ ] **P17-T08** Security review: plugin trust model, file-parsing hardening (every importer is an attack surface — fuzz them as untrusted input), update integrity.
- [ ] **P17-T09** Beta program, feedback pipeline, and a triage process that turns every reported failure into a corpus fixture.
- [ ] **P17-T10** Release checklist, support runbook, and a documented process for shipping a patch within 48 hours.
---

## 10. Risk register

Ordered by expected damage, not probability.

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R1 | **Topological naming proves fragile in real use** — models break on edit, the product feels amateur | High | Fatal | ADR-0005; naming is a first-class subsystem with its own corpus from Phase 3; tier-3 fails loudly rather than guessing; every new feature adds naming cases; naming regressions are P0 |
| R2 | **OCCT boolean/blend robustness caps product quality** | High | Severe | Retry ladder (§5.2.4); input conditioning; `Degraded` results with actionable messages; fuzz corpus; track rung-1 success rate as a health metric; ADR-0002 keeps a Parasolid exit viable |
| R3 | **Scope collapse under the weight of "parity"** — years pass with nothing shippable | High | Severe | ADR-0009 vertical slice at Phase 5; every phase has mechanically checkable exit criteria; Epoch B is defined to produce something adoptable before Epoch C's specialized domains |
| R4 | **Drawings/detailing consumes far more than budgeted** | High | Major | Phase 10 is deliberately the largest single phase; draft-mode HLR to hide exact-HLR latency; associativity has dedicated corpus coverage |
| R5 | **Performance at assembly scale never arrives** | Medium | Severe | Lightweight modes designed in Phase 9 not retrofitted; §7 budgets enforced as tests from Phase 2; snapshot-based rendering decouples viewport from rebuild |
| R6 | **Native/managed boundary becomes a bug farm** | Medium | Major | IDL-generated bindings; opaque tagged handles; exception firewall; no pointers cross; generation-counted handle table; boundary has its own test suite |
| R7 | **Nondeterministic rebuild** silently corrupts undo, caching, and naming | Medium | Severe | Determinism gate from Phase 1: rebuild the corpus twice and diff; stable sorting of all kernel outputs; pinned OCCT build |
| R8 | **File-format lock-in mistakes** force a breaking change later | Medium | Major | Versioned schema + migrations from Phase 3; every released version's fixtures reopened in CI; unknown-field preservation; caches always regenerable |
| R9 | **WPF end-of-life** forces a UI rewrite | Low near-term, certain long-term | Major | ADR-0007's agnostic-ViewModel rule, enforced by architecture tests — makes a reskin, not a rewrite |
| R10 | **Sketch solver hits a wall** on large or pathological sketches | Medium | Moderate | `ISketchSolver` narrowness; Phase 14 decision point with real data; DCM licensing as a known escape |
| R11 | **LGPL compliance error** in a proprietary release | Low | Severe | Separate replaceable DLLs by design; generated notices; counsel review before first public release (§8.6) |
| R12 | **Native crashes destroy user trust** | Medium | Major | Journaling autosave; minidumps; repro-bundle capture; out-of-process kernel as a designed-in option (§6.2) |
| R13 | **Team/agent context loss** across a multi-year build | High | Moderate | This document; ADRs for every non-obvious decision; `docs/specs/` kept current as a definition-of-done requirement; stable task IDs in commits |
| R14 | **Import of malformed third-party files** as a security and stability hole | Medium | Major | Every importer fuzzed as untrusted input (P17-T08); parse in a constrained context; never trust declared sizes |

---

## 11. Parity tracker

Maintain this table in `docs/parity.md` and update it every phase. It is the honest answer to "how far along are we?" — far better than a percentage.

| Capability area | Target | Lands in |
|---|---|---|
| Sketching (2D) | Full constraint solver, inference, splines, blocks, 3D sketch | P4, P8 |
| Part modeling | Full catalogue §5.7 | P5, P7 |
| Multi-body | Combine, split, local ops, body management | P7 |
| Direct editing | Move/offset/delete/replace face on history tree | P7, P13 |
| Assemblies | Mates, joints, subassemblies, patterns, in-context, BOM | P5, P9 |
| Large assembly performance | Lightweight/graphics-only, 5k+ components | P9, P15 |
| Drawings | Full view set, GD&T, tables, standards | P5, P10 |
| Data exchange | STEP AP242 + PMI, IGES, DXF/DWG, mesh, glTF | P5, P11 |
| Feature recognition on imports | Holes, fillets, extrudes from dumb solids | P11 |
| Sheet metal | Full environment + flat pattern | P12 |
| Surfacing | Creation, editing, continuity, analysis | P13 |
| Mold tools | Parting/core/cavity | P13 |
| Weldments | Structural members, cut lists | P13 |
| Configurations / design tables | Full | P14 |
| Simulation | *Out of scope* — mesh export and hooks only | P16 |
| CAM | *Out of scope* | — |
| Rendering / visualization | Studio-quality stills | P6, P15 |
| PDM | Provider hooks only | P16 |
| API / scripting | Stable public API, custom features, scripting | P2, P16 |
| Localization | Multi-locale | P6, P17 |

---

## 12. Working agreement for the implementing agent

Read this before starting any session.

**Starting a session.**

1. Read `docs/PLAN.md` §2 (locked decisions) and the current phase's section in §9.
2. Read `docs/specs/` for the subsystem you are touching.
3. `git log --oneline -20` to see where the last session stopped.
4. Pick the next unblocked task in phase order. Announce which task ID you are doing.

**During work.**

- Stay inside the phase. If a task requires something from a later phase, implement the minimum stub, file a task, and note it in the code with `// TODO(P9-T07):` referencing the real task ID.
- Prefer adding to the corpus over adding to the argument. When unsure whether behavior is correct, write the fixture that pins it down.
- When a kernel operation misbehaves, capture a repro bundle first, then debug. The bundle becomes the fixture.
- Do not add a NuGet dependency without recording why in `docs/adr/` — dependency creep in a decade-long project is a real cost.
- Do not "improve" architecture opportunistically. Write an ADR proposal and stop.

**Finishing.**

- Meet §8.5's definition of done.
- Commit with `P<n>-T<nn>: <imperative summary>`.
- Update the task checkbox in `docs/PLAN.md` in the same commit.
- If the phase's exit criteria are now met, say so explicitly and stop for human review before starting the next phase.

**Things that are always wrong, no matter how convenient.**

- Calling the kernel off the dispatcher thread.
- Referencing a kernel entity by index rather than by `PersistentName`.
- A `System.Windows.*` type in `OpenMCAD.ViewModels`.
- An OCCT type outside `OpenMCAD.Kernel.Occt`.
- Mutating a document outside a transaction.
- A schema change without a migration and a fixture.
- Silently resolving an ambiguous name to a "probably right" entity.
- A geometry feature merged without corpus coverage.

---

## 13. Glossary

**B-rep** — boundary representation; a solid defined by its bounding faces, edges, and vertices with topology relating them.

**Blend** — a fillet or chamfer; the term used when discussing them as a class of kernel operation.

**DAG** — directed acyclic graph; here, the feature dependency graph, which is the truth about rebuild order (tree order is only the user-facing sequence).

**DOF** — degrees of freedom; the count of independent parameters remaining unfixed in a sketch or assembly.

**HLR** — hidden line removal; computing the visible and hidden 2D curves of a 3D model from a viewpoint. The basis of drawing views.

**In-context** — a feature in one document that references geometry in another, typically a part referencing a mating part inside an assembly.

**Lightweight** — an assembly display mode holding tessellation and metadata but not full B-rep, so very large assemblies open quickly.

**Occurrence** — a single placement of a component definition in an assembly, with its own transform and overrides. Distinct from the definition itself.

**OCCT** — Open CASCADE Technology, the geometry kernel this project uses.

**PMI** — product manufacturing information; GD&T and annotation carried in the 3D model rather than only on drawings.

**planegcs** — FreeCAD's 2D geometric constraint solver.

**Retry ladder** — the escalating sequence of conditioning and tolerance strategies applied when a kernel operation fails (§5.2.4).

**Rollback bar** — a user-positioned marker in the feature tree; the model is evaluated only up to that point.

**Topological naming** — the problem of, and the scheme for, referring to a specific face/edge/vertex in a way that survives parametric rebuild (§5.3). The hardest problem in parametric CAD.

**Vertical slice** — a thin path through every layer of the system, built early to prove the architecture (ADR-0009).

---

## 14. Immediate next actions

For the first Claude Code session on an empty repository, in order:

1. **P0-T01 → P0-T14.** Finish Phase 0 completely. Do not start Phase 1 with a half-configured repo — everything after compounds on it.
2. ~~Before Phase 1, spend a timeboxed **three days on an OCCT spike** outside the repo~~ — **done**, see `docs/notes/occt-spike.md`. OCCT 8.0.1 via vcpkg, TBB off, six operations exercised across five assumptions. Determinism confirmed bit-for-bit within and across processes. Three findings change how P1-T06 must be written: untouched entities are absent from the history map, a blend face is not reachable from the faces it joins, and `Build()` failure does not always throw.
3. **P1-T01 → P1-T03** define the shape of everything downstream. Review the `IGeometryKernel` surface and the IDL design with a human before implementing against them.

The single most important thing this plan asks for: **build the regression corpus from Phase 1, and never let a fix ship without a fixture.** Everything else is recoverable. That is the discipline that separates a CAD system that gets better every year from one that oscillates forever.
