# ADR-0002 — Thin, operation-level kernel abstraction

- **Status:** Accepted
- **Date:** 2026-08-22
- **Supersedes:** none

> Extracted from `docs/PLAN.md` section 3 by P0-T12. PLAN.md section 2 remains the index of
> locked decisions; this file is the record. Amending a decision means a NEW ADR that
> supersedes this one, never an edit in place. The value of an ADR is that it preserves the
> reasoning as it stood, including reasoning that later turned out to be wrong.

**Status:** Accepted.

**Context.** Full multi-kernel abstractions have a poor track record: kernels differ in topology model, tolerance semantics, and persistent-ID schemes, so an entity-level abstraction degrades to a lowest common denominator and becomes a permanent tax.

**Decision.** Define `IGeometryKernel` at the level of **modeling operations**, not geometric entities. Roughly 200–300 operations covers a complete MCAD application:

```
IGeometryKernel
  ├─ Construction:  Extrude, Revolve, Sweep, Loft, Boundary, PrimitiveBox/Cyl/Sph/Cone/Torus
  ├─ Modification:  Boolean(Union|Subtract|Intersect), Fillet, Chamfer, Shell, Draft,
  │                 Thicken, Offset, Split, Trim, Delete/HealFace, Move/Replace Face
  ├─ Patterns:      LinearPattern, CircularPattern, MirrorBody, PathPattern
  ├─ Sheet metal:   Bend, Unbend, Flatten, Rip, Miter          (Phase 12)
  ├─ Query:         MassProperties, BoundingBox, Curvature, Ray/Shape intersection,
  │                 Distance, Interference, Section, SilhouetteEdges, ProjectCurve
  ├─ Tessellation:  Triangulate(shape, tol) → MeshBuffer,  EdgeDiscretize
  ├─ HLR:           HiddenLineRemove(shapes, projection) → 2D curve set  (Phase 5/10)
  └─ Serialization: WriteBRep, ReadBRep  (opaque blobs; format is kernel's, not ours)
```

Every operation returns an `OperationResult` carrying the resulting `KernelShape` handle **and a `HistoryMap`** — the generated/modified/deleted correspondence between input and output sub-entities. The history map is the raw material for topological naming (§5.3) and is non-negotiable in the signature.

**Explicitly *not* abstracted:** face, edge, vertex, surface, curve, or tolerance types. Those cross the boundary as **opaque handles plus our own persistent IDs**. Callers above the kernel layer never touch a `TopoDS_Face`.

**Consequences.**

- The parametric engine, naming layer, undo, and document model become testable against `FakeKernel` — a fast, deterministic mock. Test suite runs in seconds, not minutes. This alone justifies the abstraction.
- Persistent naming lives in *our* code, where it belongs.
- Swapping to Parasolid becomes a bounded 6–12 month project (Parasolid is a flat C API with integer tags — genuinely easier to bind than OCCT).
- Costs ~4–6 weeks of upfront design. Accept it.
