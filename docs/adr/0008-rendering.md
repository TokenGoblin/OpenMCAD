# ADR-0008 — D3D12 via Vortice.Windows

- **Status:** Accepted
- **Date:** 2026-08-22
- **Supersedes:** none

> Extracted from `docs/PLAN.md` section 3 by P0-T12. PLAN.md section 2 remains the index of
> locked decisions; this file is the record. Amending a decision means a NEW ADR that
> supersedes this one, never an edit in place. The value of an ADR is that it preserves the
> reasoning as it stood, including reasoning that later turned out to be wrong.

**Status:** Accepted.

**Decision.** Direct3D 12 through Vortice.Windows, presented into an `HwndHost`-hosted child window.

**Rationale.** CAD rendering has needs that a general 3D engine does not serve well: a dedicated integer **ID buffer pass** for pixel-exact picking of faces/edges/vertices; depth-biased line rendering so edges sit cleanly on shaded faces without z-fighting; order-independent transparency for section views and assembly ghosting; heavy GPU instancing for assemblies with tens of thousands of occurrences; and deterministic frame pacing during drag operations. D3D12's explicit control serves all of these; a game engine would fight us on all of them.

**Consequences.** More upfront plumbing (descriptor heaps, fences, upload rings) than D3D11. Budget it in Phase 2. Abstract the RHI thinly (`IRenderDevice`) so a D3D11 fallback path is possible for old hardware if telemetry ever demands it.
