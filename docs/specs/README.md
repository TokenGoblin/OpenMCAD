# Subsystem specifications

Expanded from PLAN.md section 5, one file per subsystem, written as each subsystem is built.

Keeping these current is part of the definition of done (PLAN.md 8.5), not a documentation chore
deferred to the end. The reason is in the risk register: R13, team and agent context loss across a
multi-year build, is rated high likelihood. These files are a mitigation for it.

| Spec | Covers | Written in |
|---|---|---|
| `kernel-shim.md` | Extending the C ABI surface. PLAN.md P1-T16 requires this to be a 30-minute task, not an archaeology expedition. | P1-T16 |
| `naming.md` | The topological naming scheme, PLAN.md 5.3. The highest-risk subsystem in the product. | P3 |
| `document-model.md` | Document graph, rebuild engine, transactions. | P3 |
| `persistence.md` | Container layout, schema versioning, migration procedure. | P3-T18 |
| `sketch.md` | Solver contract, diagnosis mapping, drag behaviour. | P4 |
| `rendering.md` | Frame pipeline, display snapshot, picking. | P2 |
