# Parity tracker

Per PLAN.md section 11. Update every phase. This is the honest answer to "how far along are we?" —
far better than a percentage, because a percentage of an asymptote is meaningless.

**Legend:** ⬜ not started · 🟨 partial · ✅ target met for the stated scope

_Last updated: 2026-08-22, end of Phase 0._

| Capability area | Target | Lands in | Status |
|---|---|---|---|
| Sketching (2D) | Full constraint solver, inference, splines, blocks, 3D sketch | P4, P8 | ⬜ |
| Part modelling | Full catalogue, PLAN.md 5.7 | P5, P7 | ⬜ |
| Multi-body | Combine, split, local ops, body management | P7 | ⬜ |
| Direct editing | Move/offset/delete/replace face on the history tree | P7, P13 | ⬜ |
| Assemblies | Mates, joints, subassemblies, patterns, in-context, BOM | P5, P9 | ⬜ |
| Large-assembly performance | Lightweight and graphics-only, 5k+ components | P9, P15 | ⬜ |
| Drawings | Full view set, GD&T, tables, standards | P5, P10 | ⬜ |
| Data exchange | STEP AP242 + PMI, IGES, DXF/DWG, mesh, glTF | P5, P11 | ⬜ |
| Feature recognition on imports | Holes, fillets, extrudes from dumb solids | P11 | ⬜ |
| Sheet metal | Full environment plus flat pattern | P12 | ⬜ |
| Surfacing | Creation, editing, continuity, analysis | P13 | ⬜ |
| Mould tools | Parting, core, cavity | P13 | ⬜ |
| Weldments | Structural members, cut lists | P13 | ⬜ |
| Configurations and design tables | Full | P14 | ⬜ |
| Simulation | *Out of scope* — mesh export and hooks only | P16 | ⬜ |
| CAM | *Out of scope* | — | — |
| Rendering and visualisation | Studio-quality stills | P6, P15 | ⬜ |
| PDM | Provider hooks only | P16 | ⬜ |
| API and scripting | Stable public API, custom features, scripting | P2, P16 | ⬜ |
| Localisation | Multi-locale | P6, P17 | ⬜ |

## Foundations

Not in PLAN.md section 11, but tracked here because Phase 0 delivers nothing from the table above
and "0% of everything" would misrepresent the state of the repository.

| Foundation | Lands in | Status |
|---|---|---|
| Repository, build, CI | P0 | ✅ |
| Layering enforced by tests | P0-T05 | ✅ |
| Double-precision geometry primitives | P0-T13 | ✅ |
| Versioning scheme wired into assemblies | P0-T14 | ✅ |
| Structured logging and DI composition root | P0-T09 | ✅ |
| Shell that launches (ribbon, docking, placeholder viewport) | P0-T10 | ✅ |
| Headless CLI | P0-T11 | ✅ |
| Native shim skeleton (C ABI, exception firewall, two-call pattern) | P0-T06 | 🟨 authored and CI-built; OCCT not yet linked |
| Geometry kernel behind `IGeometryKernel` | P1 | ⬜ |
| Regression corpus | P1-T14 | ⬜ |
| Topological naming | P3 | ⬜ |
