# ADR-0012 — Establish the plugin API in Phase 2, not later

- **Status:** Accepted
- **Date:** 2026-08-22
- **Supersedes:** none

> Extracted from `docs/PLAN.md` section 3 by P0-T12. PLAN.md section 2 remains the index of
> locked decisions; this file is the record. Amending a decision means a NEW ADR that
> supersedes this one, never an edit in place. The value of an ADR is that it preserves the
> reasoning as it stood, including reasoning that later turned out to be wrong.

**Status:** Accepted.

**Decision.** `OpenMCAD.Api` exists as a separate, semver-governed assembly with a checked-in API-surface baseline from Phase 2, and plugins load into isolated `AssemblyLoadContext`s.

**Rationale.** Retrofitting an extensibility surface onto a mature codebase leaves two bad options: break every existing plugin, or freeze a design that was never intended to be public. Establishing it early also imposes a useful discipline — it forces a clean separation between what the application *is* and what it *exposes*, which improves the internal design independently of whether anyone ever writes a plugin.

**Consequences.** Some churn cost early, when the API is small and churn is cheap. Plugins never receive raw kernel handles — only our abstraction — so a future kernel swap (ADR-0002) does not break the ecosystem.
