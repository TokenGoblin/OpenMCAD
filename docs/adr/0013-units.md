# ADR-0013 — Unit-aware quantities in the core, SI internally

- **Status:** Accepted
- **Date:** 2026-08-22
- **Supersedes:** none

> Extracted from `docs/PLAN.md` section 3 by P0-T12. PLAN.md section 2 remains the index of
> locked decisions; this file is the record. Amending a decision means a NEW ADR that
> supersedes this one, never an edit in place. The value of an ADR is that it preserves the
> reasoning as it stood, including reasoning that later turned out to be wrong.

**Status:** Accepted.

**Decision.** Parameters carry a dimension, not a bare double. Internal storage is always SI base units (metres, radians, kilograms, seconds). Conversion happens only at the input/display boundary. The expression engine lives in `OpenMCAD.Core`, not the UI.

**Rationale.** Unit handling pushed into the UI layer is the origin of an entire genus of CAD bugs: values that drift on round-trip, mixed-unit arithmetic that silently produces nonsense, documents that mean different things depending on the viewer's settings. Making dimension part of the type turns `4 mm + 3 deg` into an error caught before evaluation. Mechanical engineering is also irreducibly bi-modal on units — metric and imperial both matter, often in the same document — so this cannot be deferred as a localization concern.

**Consequences.** Slightly more ceremony at every parameter site. A round-trip test (display → parse → store → display) is mandatory and must be exact.
