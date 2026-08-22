# ADR-0010 — File format: OPC container, versioned schema, cached B-rep

- **Status:** Accepted
- **Date:** 2026-08-22
- **Supersedes:** none

> Extracted from `docs/PLAN.md` section 3 by P0-T12. PLAN.md section 2 remains the index of
> locked decisions; this file is the record. Amending a decision means a NEW ADR that
> supersedes this one, never an edit in place. The value of an ADR is that it preserves the
> reasoning as it stood, including reasoning that later turned out to be wrong.

**Status:** Accepted. Full spec in §5.8.

**Decision.** `.ompart` / `.omasm` / `.omdrw` are Zip/OPC containers holding: a manifest, a versioned MessagePack document graph, cached kernel B-rep blobs, tessellation caches, thumbnails, and optional external-reference metadata.

**Rationale.** The document graph must be human-inspectable in a pinch and machine-migratable forever. Cached B-rep means opening a large assembly does not require rebuilding every feature of every part. Thumbnails and metadata in a well-known container location means Explorer/PDM integration is nearly free.
