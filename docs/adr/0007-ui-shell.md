# ADR-0007 — WPF shell with framework-agnostic ViewModels

- **Status:** Accepted
- **Date:** 2026-08-22
- **Supersedes:** none

> Extracted from `docs/PLAN.md` section 3 by P0-T12. PLAN.md section 2 remains the index of
> locked decisions; this file is the record. Amending a decision means a NEW ADR that
> supersedes this one, never an edit in place. The value of an ADR is that it preserves the
> reasoning as it stood, including reasoning that later turned out to be wrong.

**Status:** Accepted.

**Decision.** WPF for the shell. **Hard rule: no `System.Windows.*` type appears in any type under `OpenMCAD.ViewModels`.** Enforced by an architecture test (NetArchTest) in CI, not by discipline.

**Rationale.** WPF is in maintenance mode, and it is still correct here: the docking, ribbon, property-grid, and virtualized-tree ecosystem (AvalonDock, Fluent.Ribbon, and the commercial suites) saves close to a year of shell work. Airspace issues with a hosted D3D surface are a non-problem when the viewport is one large rectangle. WinUI 3 composes better via `SwapChainPanel` but its docking ecosystem is thin. Avalonia is the right answer only if cross-platform outranks Windows-native feel.

**Consequences.** The agnostic-VM rule means a future Avalonia or WinUI shell is a reskin, not a rewrite — the actual insurance policy against WPF's end of life.
