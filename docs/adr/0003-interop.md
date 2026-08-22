# ADR-0003 — Interop via C ABI shim + `LibraryImport`, not C++/CLI

- **Status:** Accepted
- **Date:** 2026-08-22
- **Supersedes:** none

> Extracted from `docs/PLAN.md` section 3 by P0-T12. PLAN.md section 2 remains the index of
> locked decisions; this file is the record. Amending a decision means a NEW ADR that
> supersedes this one, never an edit in place. The value of an ADR is that it preserves the
> reasoning as it stood, including reasoning that later turned out to be wrong.

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
