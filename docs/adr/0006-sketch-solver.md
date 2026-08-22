# ADR-0006 — planegcs behind `ISketchSolver`

- **Status:** Accepted
- **Date:** 2026-08-22
- **Supersedes:** none

> Extracted from `docs/PLAN.md` section 3 by P0-T12. PLAN.md section 2 remains the index of
> locked decisions; this file is the record. Amending a decision means a NEW ADR that
> supersedes this one, never an edit in place. The value of an ADR is that it preserves the
> reasoning as it stood, including reasoning that later turned out to be wrong.

**Status:** Accepted.

**Decision.** Wrap FreeCAD's `planegcs` (LGPL, Eigen-based) through the same C-shim mechanism as the kernel, behind `ISketchSolver`.

**Rationale.** A sketch solver is a second numerical kernel — DOF analysis, decomposition into independently solvable subsystems, Levenberg–Marquardt/dogleg iteration, and the diagnostics (over-constrained, under-constrained, redundant, conflicting) that make a sketcher feel professional. planegcs is the only mature free implementation. D-Cubed DCM is the industry standard and is commercial.

**Consequences.** The interface is deliberately narrow: submit a parameter vector, an entity set, and a constraint set; receive a converged parameter vector plus a diagnosis. That narrowness makes a managed rewrite or a DCM license a contained swap later. Sketch *semantics* (constraint kinds, inference, drag behavior, auto-constraints) live in our code, above the solver.
