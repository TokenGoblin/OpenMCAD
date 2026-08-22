# ADR-0004 — Single-threaded kernel dispatcher

- **Status:** Accepted
- **Date:** 2026-08-22
- **Supersedes:** none

> Extracted from `docs/PLAN.md` section 3 by P0-T12. PLAN.md section 2 remains the index of
> locked decisions; this file is the record. Amending a decision means a NEW ADR that
> supersedes this one, never an edit in place. The value of an ADR is that it preserves the
> reasoning as it stood, including reasoning that later turned out to be wrong.

**Status:** Accepted.

**Context.** OCCT is not thread-safe in general. Some operations are re-entrant on disjoint shapes, but the guarantees are unclear, undocumented in places, and version-dependent.

**Decision.** All kernel calls are marshalled onto **one dedicated kernel thread** via `KernelDispatcher`, an actor with a priority work queue. The public C# kernel API is `async` and returns `ValueTask<OperationResult>`. Assert (in debug) that no kernel call occurs off the kernel thread.

**Rationale.** Correctness first; a class of impossible-to-reproduce heisenbugs is eliminated by construction. Retrofitting this after parallelizing rebuild would be agony.

**Consequences and mitigations.**

- The kernel is a serial resource. Rebuild parallelism therefore happens *above* it: independent DAG branches queue work concurrently, but execute serially. This is still a large win because the non-kernel work (naming resolution, expression evaluation, validation) parallelizes freely.
- **Escape hatch, Phase 15:** a pool of *N* kernel worker threads, each owning a strictly isolated shape universe with no shared `Handle` graph, used only for embarrassingly parallel work (tessellation of independent bodies, HLR of independent drawing views, batch import). Prove isolation with a stress test before enabling.
- The UI never blocks: viewport rendering reads from an immutable, versioned display snapshot (§5.10), not from live kernel state.
