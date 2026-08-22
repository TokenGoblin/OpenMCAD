# ADR-0009 — Breadth-first vertical slice

- **Status:** Accepted
- **Date:** 2026-08-22
- **Supersedes:** none

> Extracted from `docs/PLAN.md` section 3 by P0-T12. PLAN.md section 2 remains the index of
> locked decisions; this file is the record. Amending a decision means a NEW ADR that
> supersedes this one, never an edit in place. The value of an ADR is that it preserves the
> reasoning as it stood, including reasoning that later turned out to be wrong.

**Status:** Accepted.

**Decision.** Phase 5 delivers an end-to-end path — sketch → extrude → two-part assembly with mates → a dimensioned drawing view → STEP export — before any subsystem is deepened.

**Rationale.** Assemblies and drawings impose constraints on the document model, naming scheme, selection model, and file format that are cheap to accommodate early and brutally expensive to retrofit. Depth-first part modeling is the tempting choice and it is wrong: it hides those constraints until the cost of honoring them is a rewrite. Prove the hard interactions while the architecture is still soft.
