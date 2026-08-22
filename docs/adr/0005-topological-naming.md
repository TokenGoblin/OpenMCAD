# ADR-0005 — Own the topological naming layer

- **Status:** Accepted
- **Date:** 2026-08-22
- **Supersedes:** none

> Extracted from `docs/PLAN.md` section 3 by P0-T12. PLAN.md section 2 remains the index of
> locked decisions; this file is the record. Amending a decision means a NEW ADR that
> supersedes this one, never an edit in place. The value of an ADR is that it preserves the
> reasoning as it stood, including reasoning that later turned out to be wrong.

**Status:** Accepted. **This is the highest-risk subsystem in the product.**

**Context.** When a user edits a sketch dimension and the model rebuilds, the fillet applied to "that edge" must still find that edge. Kernel indices are worthless — they change on every rebuild. This is the single clearest dividing line between production MCAD and hobby MCAD, and the reason FreeCAD models break where SolidWorks models do not.

**Decision.** Implement `OpenMCAD.Core.Naming` as a first-class subsystem. Entities are named by **generative provenance**, not by index: an entity name is a structured path recording which feature created it, from which input entity, in what role, with a geometric disambiguator for ties. OCCT's `BRepTools_History`/`BRepAlgoAPI` history output is the *raw input* to this layer; OCCT's `TNaming`/OCAF is **not** used.

**Consequences.** Full design in §5.3. It carries its own regression suite (`tests/naming-corpus`) from Phase 3, and every new feature type in every later phase must add naming cases to it. Treat a naming regression as a P0 bug.
