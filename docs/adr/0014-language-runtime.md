# ADR-0014 — .NET 10 LTS, C# 14, nullable and warnings-as-errors

- **Status:** Accepted
- **Date:** 2026-08-22
- **Supersedes:** none

> Extracted from `docs/PLAN.md` section 3 by P0-T12. PLAN.md section 2 remains the index of
> locked decisions; this file is the record. Amending a decision means a NEW ADR that
> supersedes this one, never an edit in place. The value of an ADR is that it preserves the
> reasoning as it stood, including reasoning that later turned out to be wrong.

**Status:** Accepted.

**Decision.** Target .NET 10 (LTS) with C# 14. Libraries target `net10.0`; only `OpenMCAD.Shell` targets `net10.0-windows`. Nullable reference types enabled and warnings treated as errors, repository-wide, from the first commit.

**Rationale.** An LTS runtime matters for a product with a decade-long horizon and enterprise deployment. Keeping the Windows-specific TFM confined to the shell is what makes ADR-0007's portability insurance real rather than notional — if the libraries compile against plain `net10.0`, a non-Windows shell is genuinely possible. Nullable-from-day-one is essentially free at the start and prohibitively expensive to adopt at 500k lines.

**Consequences.** Occasional friction with packages that lag on nullable annotations; suppress narrowly and locally, never globally.
---
