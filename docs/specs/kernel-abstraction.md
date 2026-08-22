# Spec: the geometry kernel abstraction

**Status:** implemented and tested against `FakeKernel`; **awaiting the review PLAN.md §14 asks for
before the native shim is written against it.**

**Tasks:** P1-T01, P1-T02, P1-T07, P1-T08, P1-T09, P1-T10.

PLAN.md §14 item 3 says: *"P1-T01 → P1-T03 define the shape of everything downstream. Review the
`IGeometryKernel` surface and the IDL design with a human before implementing against them."* This
document is what there is to review. It states the decisions taken beyond what the plan specified,
and the ones that are still open.

---

## 1. What was built

| Piece | Where | Task |
|---|---|---|
| `KernelShape`, `SubEntity`, `OperationRole`, `HistoryMap`, `OperationResult` | `OpenMCAD.Kernel` | P1-T02 |
| `IGeometryKernel` — the Phase 1 operation subset | `OpenMCAD.Kernel/IGeometryKernel.cs` | P1-T01 |
| `GeometryKernelBase` — dispatch, validation, exception firewall | `OpenMCAD.Kernel` | P1-T01 |
| `KernelShapeHandle` — owning `SafeHandle` | `OpenMCAD.Kernel` | P1-T07 |
| `KernelDispatcher`, `KernelThreadGuard` | `OpenMCAD.Kernel/Threading` | P1-T08 |
| `FakeKernel` — analytic, deterministic | `OpenMCAD.Kernel.Fake` | P1-T09 |
| Contract battery, 20 cases | `tests/contract/OpenMCAD.Kernel.Contract.Tests` | P1-T10 |
| Dispatcher and handle tests, 18 cases | `tests/unit/OpenMCAD.Kernel.Tests` | P1-T08 |

Operation subset, exactly as P1-T01 lists it: five primitives, a polygon profile, extrude, revolve,
boolean, fillet, chamfer, mass properties, bounds, topology counts, entity enumeration, validity,
triangulation, B-rep read and write, STEP write.

---

## 2. Decisions the plan did not settle

These are the ones worth an argument. Each states the alternative that was rejected, because a
decision recorded without its alternative is not reviewable.

### 2.1 A base class, not just an interface

`IGeometryKernel`'s async methods are sealed in `GeometryKernelBase` and forward to protected
synchronous methods. Implementations override the synchronous ones and have no way to be entered
off the kernel thread.

*Rejected:* an interface each implementation dispatches for itself. That makes ADR-0004 a
convention every future implementation must remember, and PLAN.md §12 lists calling the kernel off
the dispatcher thread **first** among the things that are always wrong. A rule stated that plainly
deserves structural enforcement.

*Cost:* the operation list appears twice, in the interface and in the base class. Accepted.

### 2.2 Two result types, not one

`OperationResult` for shape-producing operations, `KernelResult<T>` for queries.

*Rejected:* one generic type. A query has no shape to own and no history to record, and merging
them forces every call site to reason about members that cannot apply.

### 2.3 Validation lives on the definition, not in the kernel

Every operation takes an `IOperationDefinition` with a `Validate()` method.
`GeometryKernelBase` runs it before dispatching, so invalid input never reaches the kernel thread.

Three things follow: both kernels reject the same input with the same message, which is a large
part of what makes the abstraction real rather than nominal; P7-T17's pre-flight requirement is
satisfied by construction; and definitions are hashable, which is what P3-T05's geometry cache is
keyed on.

### 2.4 `SubEntity` carries its owner

`SubEntity` is `(KernelShape Owner, ulong Tag, SubEntityKind Kind)` rather than a bare tag.

Sub-entities are released with their shape — a shape with ten thousand faces must not need ten
thousand finalizable objects — and carrying the owner makes that lifetime legible at every call
site and makes use-after-release detectable.

### 2.5 `HistoryMapBuilder` is the only way to build a map

It throws on an unassigned `OperationRole`. PLAN.md §5.1 says an operation returning unrolled
outputs is an incomplete implementation that fails review; making it throw at construction means
the failure surfaces in the operation's own test rather than as a model that breaks on edit weeks
later.

`AddNewBetween` exists for the case that matters: a fillet's blend face is created from nothing but
is not anonymous — it is the blend between *these two faces*, and that relationship is the only
thing that makes it nameable.

### 2.6 Capability flags rather than uniform promises

`KernelCapabilities.ProducesExactMassProperties` lets each implementation say what it promises. The
contract battery demands exactness only where it is promised, which is what lets one battery run
against a mock and a real kernel without either lying.

### 2.7 The CLI is `omcad`, and 64-bit only

`KernelShapeHandle` stores the tag in the `SafeHandle` pointer field, and the tag uses the full
64-bit range because the generation counter sits in the high bits. A 32-bit process would truncate
it, so the type refuses to load in one. OpenMCAD does not target 32-bit.

---

## 3. Open questions for the review

Answering these before P1-T06 is cheaper than answering them after.

1. **Is the operation subset right for Phase 1?** In particular, `CreatePolygonProfileAsync` is
   admitted scaffolding — extrude needs something to sweep and the sketcher is Phase 4. It is
   deliberately the crudest thing that suffices so no design effort is spent on a representation
   P4 will replace. Alternative: defer extrude to Phase 4 and make Phase 1 primitives-only.

2. **Should `OperationRole` be an enum or an open string?** An enum is checkable and compact but
   every new feature type needs a new value, and the values are persisted inside names, so they can
   never be renumbered. An open vocabulary would not have that constraint but would lose the
   compiler's help. Current answer: enum, append-only, documented as ABI.

3. **Is `Degraded` pulling its weight on queries?** It is clearly right for operations — a fillet
   that did eleven of twelve edges. For queries it currently only carries "these mass properties
   are approximate". That may be better as a field on `MassProperties` alone, which it already is.

4. **Should the retry ladder be visible in the abstraction at all?** `RetryRung` is on every
   result, and PLAN.md §5.2.4 wants the rung distribution as a health metric. But rungs are an OCCT
   coping mechanism; a Parasolid implementation would report `ModelTolerance` forever. Is a
   kernel-specific concept in the kernel-agnostic result type a leak, or the honest reporting of
   something the user genuinely needs to know?

5. **`WriteStepAsync` takes a `Stream`.** Convenient and testable, but OCCT's STEP writer wants a
   file path and will need a temporary file. Worth changing to a path before the shim is written.

---

## 4. What `FakeKernel` promises

Stated precisely, because tests depend on the distinction.

**Exact:** topology, entity identity, provenance, roles, ordering, determinism. Mass properties for
box, cylinder, sphere, cone (by three-point Gauss-Legendre quadrature, which is exact for the
degree-four integrands a frustum produces), torus, profile, and right prism (closed-form polygon
second moments).

**Approximate, and said so:** booleans and blends do no real geometry. They synthesise plausible
topology and adjust volume analytically. Revolve is modelled as an equivalent torus via Pappus's
theorem. Oblique prisms report approximate inertia. All of these return `Degraded` with a warning,
and `Capabilities.ProducesExactMassProperties` is false.

**Deliberately reproduced from the shim design:** the handle table with generation counters, so a
stale tag is detected rather than aliasing a recycled slot. Lifetime bugs surface against the fast
mock instead of against OCCT.

---

## 5. Not yet done in Phase 1

Everything remaining is native, and blocked on the same thing.

| Task | State |
|---|---|
| P1-T04 shim handle table | Blocked on the C++ toolchain — see section 6. |
| P1-T05 exception firewall, `OSD::SetSignal` | The pattern is written (`openmcad_types.h`, `openmcad_occt.cpp`) and generated into all 49 entry points; it has never been compiled. |
| P1-T06 OCCT operations | Blocked, and also wants the OCCT spike PLAN.md §14 asks for. |
| P1-T11 retry ladder | Blocked on P1-T06. The observable contract — `RetryRung` on every result, `Degraded` for partial success, `rung` threaded through every fragile IDL operation — is done and tested. |
| P1-T12 determinism audit | Blocked on P1-T06. The gate itself exists and runs on every build against `FakeKernel`. |
| P1-T15 benchmarks | Blocked on P1-T06; benchmarking `FakeKernel` would measure the dispatcher, not the kernel. |

Done since this document was first written: P1-T03 (IDL and generator), P1-T13 (repro bundles),
P1-T14 (corpus runner, three fixtures, determinism gate), P1-T16 (`docs/specs/kernel-shim.md`).

## 6. The toolchain blocker

The development machine has .NET 10, git, and CMake, but **no MSVC C++ toolchain and no vcpkg**.
Everything from P1-T04 onward is native, so none of it can be compiled or run locally. `build.ps1`
detects this and skips the native step; CI builds the shim skeleton.

Before Phase 1 can continue past this review:

1. Install Visual Studio Build Tools with the C++ workload.
2. Bootstrap vcpkg and set `VCPKG_ROOT`.
3. Replace the placeholder `builtin-baseline` in `native/vcpkg.json`
   (`vcpkg x-update-baseline --add-initial-baseline`).
4. Run the timeboxed OCCT spike PLAN.md §14 item 2 asks for, and write up
   `docs/notes/occt-spike.md`.

Step 4 matters more than it looks. The spike is where assumptions about OCCT's version, build
flags, threading, and determinism get verified — and this abstraction was designed around those
assumptions without any of them having been tested.
