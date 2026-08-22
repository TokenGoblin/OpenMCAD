# ADR-0001 — Geometry kernel: OCCT

- **Status:** Accepted
- **Date:** 2026-08-22
- **Supersedes:** none

> Extracted from `docs/PLAN.md` section 3 by P0-T12. PLAN.md section 2 remains the index of
> locked decisions; this file is the record. Amending a decision means a NEW ADR that
> supersedes this one, never an edit in place. The value of an ADR is that it preserves the
> reasoning as it stood, including reasoning that later turned out to be wrong.

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
