# Architecture decision records

Every non-obvious decision gets a record here. PLAN.md section 2 is the index of *locked* decisions;
these files are the reasoning behind them.

**Amend by supersession, never in place.** An ADR is valuable precisely because it preserves what
was known and believed at the time, including beliefs that later turned out to be wrong. Editing
one to match current thinking destroys the only thing it was for. To change a decision, write a new
ADR that supersedes the old one and set the old one's status to `Superseded by ADR-NNNN`.

| # | Decision | Status |
|---|---|---|
| [0001](0001-geometry-kernel.md) | Geometry kernel: OCCT | Accepted |
| [0002](0002-kernel-abstraction.md) | Thin, operation-level kernel abstraction | Accepted |
| [0003](0003-interop.md) | Interop via C ABI shim and `LibraryImport`, not C++/CLI | Accepted |
| [0004](0004-kernel-dispatcher.md) | Single-threaded kernel dispatcher | Accepted |
| [0005](0005-topological-naming.md) | Own the topological naming layer | Accepted — **highest-risk subsystem** |
| [0006](0006-sketch-solver.md) | planegcs behind `ISketchSolver` | Accepted |
| [0007](0007-ui-shell.md) | WPF shell with framework-agnostic view models | Accepted |
| [0008](0008-rendering.md) | Direct3D 12 via Vortice.Windows | Accepted |
| [0009](0009-parity-sequencing.md) | Breadth-first vertical slice | Accepted |
| [0010](0010-file-format.md) | OPC container, versioned schema, cached B-rep | Accepted |
| [0011](0011-undo.md) | Undo over parameter state, not geometry | Accepted |
| [0012](0012-plugin-api.md) | Establish the plugin API in Phase 2 | Accepted |
| [0013](0013-units.md) | Unit-aware quantities in the core, SI internally | Accepted |
| [0014](0014-language-runtime.md) | .NET 10 LTS, C# 14, nullable, warnings-as-errors | Accepted |
| [0015](0015-shell-libraries.md) | Shell libraries: Fluent.Ribbon and AvalonDock | Accepted |
| [0016](0016-phase0-dependencies.md) | The Phase 0 dependency set | Accepted |
| [0017](0017-project-name-and-licence.md) | Project name and licence (MIT) | Accepted |

## Scheduled revisits

The plan builds in decision points where real data should change or confirm a choice. They are
listed here so they are not forgotten between phases.

| When | Revisit | Why |
|---|---|---|
| P6-T02 | ADR-0015 docking library | Judge it against the real layout, not a skeleton. |
| P6-T04 | The hand-rolled `ObservableObject` (ADR-0016) | The schema-driven property manager reveals the actual MVVM requirements. |
| P14-T07 | ADR-0006 sketch solver | Decide with usage data whether to write a managed solver, licence DCM, or stay on planegcs. |
| P15-T04 | ADR-0004 out-of-process kernel | Implement only if crash telemetry justifies it. |
| P13-T09 | ADR-0001 kernel | Surfacing is where OCCT is weakest; the results are evidence for or against a Parasolid migration. |
