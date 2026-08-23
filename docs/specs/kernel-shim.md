# Spec: extending the kernel shim

**Task:** P1-T16, which states the requirement plainly — *"every later phase adds operations, and
this must be a 30-minute task, not an archaeology expedition."*

The kernel surface grows from 52 operations today to the 200–300 that ADR-0002 estimates a complete
MCAD application needs. That is roughly one new operation every working week for several years. If
each one costs an afternoon of working out which five files to touch, the arithmetic alone sinks
the project.

---

## The short version

1. Add an entry to `native/kernel.api.json`.
2. Run `./build.ps1 -Generate`.
3. Write the body in `native/openmcad_occt/src/ops/`.
4. Add the managed operation to `IGeometryKernel` and `GeometryKernelBase`.
5. Implement it in `FakeKernel`.
6. Add a corpus fixture.

Steps 1–2 are five minutes. Steps 4–5 are mechanical. Step 3 is the actual work, and step 6 is
non-negotiable.

---

## What is generated and what is not

```
native/kernel.api.json                      ← you edit this
        │
        └── tools/idlgen ──┬── openmcad_occt.g.h          C declarations
                           ├── openmcad_ops.g.h           body signatures
                           ├── openmcad_dispatch.g.cpp    unpacking, null checks, firewall
                           ├── openmcad_stubs.g.cpp       not-implemented fallbacks
                           └── OcctBindings.g.cs          LibraryImport declarations

native/openmcad_occt/src/ops/*.cpp          ← you write these
native/openmcad_occt/include/openmcad_types.h  the C++ vocabulary (rarely changes)
```

Generated files are **checked in**. Two reasons: CMake would otherwise need the .NET SDK, which is
an unwelcome dependency in a C++ toolchain; and a generated diff is worth having on a compatibility
surface, because a pull request then shows exactly what happened to the ABI.

`build.ps1` verifies freshness on every build and fails if the checked-in files disagree with the
IDL. CI checks it twice, once in the managed job and once in the native job.

---

## Step 1: the IDL entry

```json
{
  "name": "shell",
  "csharp": "Shell",
  "group": "modification",
  "summary": "Hollows a solid, removing the given faces to leave openings.",
  "fragile": true,
  "parameters": [
    { "name": "body",      "type": "shape",        "summary": "The body to hollow." },
    { "name": "faces",     "type": "entity_array", "summary": "The faces to remove." },
    { "name": "thickness", "type": "f64",          "summary": "Wall thickness in metres." },
    { "name": "tolerance", "type": "f64",          "summary": "Model tolerance in metres." },
    { "name": "result",    "type": "shape_out",    "summary": "The resulting shape." },
    { "name": "history",   "type": "history_out",  "summary": "Input-to-output correspondence." },
    { "name": "rung",      "type": "i32_out",      "summary": "Which retry rung produced the result." }
  ]
}
```

Rules the generator enforces, so getting them wrong fails fast rather than at link time:

- Names are unique, in both C and C#.
- Every operation and every parameter has a summary. It becomes the public documentation on both
  sides of the boundary, and an undocumented ABI entry point is one nobody can call correctly.
- `fragile: true` requires a `rung` output. A fragile operation that does not report which rung of
  the retry ladder produced its result gives the PLAN.md 5.2.4 health metric nothing to aggregate.
- Types must be in the table (below). Unknown types are rejected with the list of known ones.

**Append; do not reorder or rename.** Once anything links against the shim, the export names are a
compatibility surface.

### The type table

| IDL type | C | C++ body sees | C# |
|---|---|---|---|
| `f64` `i32` | `double` `int32_t` | same | `double` `int` |
| `bool` | `int32_t` | `bool` | `int` |
| `utf8` | `const char*` | `const char*` | `string` |
| `shape` `entity` `history` `mesh` | `uint64_t` | `ShapeRef`, `EntityRef`, … | `ulong` |
| `vec3` `transform` | `const double*` | `const Vec3&`, `const Transform&` | `ReadOnlySpan<double>` |
| `f64_array` `entity_array` `shape_array` `byte_array` `vec2_array` | pointer + `int32_t` count | `std::span<const T>` | `ReadOnlySpan<T>` + count |
| `i32_out` `u64_out` `f64_out` | pointer | `T&` | `out T` |
| `shape_out` `history_out` `mesh_out` | `uint64_t*` | `ShapeOut`, … | `out ulong` |
| `f64_array_out` `i32_array_out` `u64_array_out` `byte_array_out` `utf8_out` | pointer + capacity + `int32_t*` required | `OutBuffer<T>` | `Span<T>` + capacity + `out int` |

Adding a type means adding one rule to `TypeTable` in `native/tools/idlgen/TypeTable.cs`. That
table is the entire boundary contract in one place, which is the point: a rule stated once is
applied identically to every operation, and a mistake there is one mistake rather than fifty.

---

## Step 3: the body

Create `native/openmcad_occt/src/ops/shell.cpp`. The signature is in the generated
`openmcad_ops.g.h`; copy it.

```cpp
#include "openmcad_ops.g.h"
#include "openmcad_handles.h"

#include <BRepOffsetAPI_MakeThickSolid.hxx>

namespace openmcad::ops {

void shell(
    openmcad::ShapeRef body,
    std::span<const uint64_t> faces,
    double thickness,
    double tolerance,
    openmcad::ShapeOut result,
    openmcad::HistoryOut history,
    int32_t& rung)
{
    const TopoDS_Shape& solid = handles().resolve_shape(body);
    // ... build the result, populate the history map, set the rung ...
    result.set(handles().store(built));
    history.set(handles().store(map));
    rung = 1;
}

} // namespace openmcad::ops
```

By the time this is called, the dispatch layer has already rejected null output pointers, wrapped
raw pointers in spans, and installed the exception firewall. So:

- **Throw on failure.** `invalid_input`, `invalid_handle`, or any OCCT `Standard_Failure`. The
  firewall converts it to a status and a diagnostic. Do not thread status codes through your own
  control flow.
- **Never let a body return without populating its outputs.** A body that returns normally is
  telling the caller it succeeded.
- **Populate the history map deliberately.** This is the part that is not optional and not
  mechanical — see below.

### Populating the history map

The single most important thing a body does, and the one no generator can do for you.

Every output entity needs an `OperationRole` saying what it *is* in the operation's own terms —
`SideWall`, `StartCap`, `BlendFace`, `Retained`. PLAN.md 5.1 is explicit that an operation returning
unrolled outputs is an incomplete implementation that fails review, and `HistoryMapBuilder` throws
rather than let one through.

Three relationships to record:

- **Generated** — the input caused this to exist. A profile edge generates a side wall.
- **Modified** — the same entity, altered. A face trimmed by a boolean. Note this returns a *list*:
  a face cut in two has two successors, and that multiplicity is where most naming bugs live.
- **Deleted** — no successor. Report it; a downstream feature that referenced it must be told, not
  left to guess.

For entities created from nothing, prefer `AddNewBetween` over `AddNew` wherever the entity lies
between known inputs. A fillet's blend face is created from nothing but it is not anonymous: it is
the blend between *these two faces*, and recording that is the only thing that makes it survive a
rebuild.

Ordering is part of the contract, and **sorting by tag is not ordering**. A tag is a handle: 40
bits of slot index and 24 bits of generation counter. Recycling a slot increments the generation
and moves the tag by 2^40, so an entity in a reused handle sorts nowhere near an equivalent entity
in a fresh one. Sorting a set of tags therefore orders by allocation accident. This was believed
otherwise for a while and it was wrong; see `docs/notes/determinism-audit.md`.

`enumerate` is the single authority for canonical order — measure, then quantised centroid, then
traversal as a tie-break — and every operation tags its entities through `tag_canonical` so that
authority is applied uniformly.

The `history_*` entry points are the exception, and deliberately so: they return tags in numeric
order, which is stable within a call but carries no geometric meaning. Canonicalising them here
too would put the same ordering rule on both sides of the boundary, where the two copies can
drift. The consumer re-orders history tags against `enumerate` instead, which keeps one authority.
A new consumer of the C ABI must do the same; it is not free to assume history order is meaningful.

Determinism starts here (ADR-0011). A face set returned in memory-allocation order silently makes
every downstream name unstable.

### Three things the OCCT spike found

Measured, not assumed — see `docs/notes/occt-spike.md`. Each one is a silent-wrong-answer path,
which is why they are called out here rather than left to be rediscovered.

**Untouched entities are absent from the history map.** Cutting a cylinder from a box, OCCT
reported 2 of 6 target faces as modified, none as deleted, and said *nothing at all* about the
other 4. `OperationRole.Retained` therefore cannot be read from OCCT.

After the kernel call, sweep the input entities. For each one with no history entry, **look it up
in the output** — OCCT keeps the same `TShape` for untouched entities, so `TopoDS_Shape::IsSame`
against a map of the result finds them. The lookup decides:

| Outcome | Record |
|---|---|
| Found in the output | `AddRetained(input, survivor)` |
| Not found | `AddDeleted(input)` |

Do **not** shortcut this to "no entry and `IsDeleted()` is false ⇒ retained". `IsDeleted()` returns
false for entities OCCT says nothing about, so a genuinely dropped entity passes that test — and
`AddRetained` needs a real output entity to point at, leaving you to fabricate one (a history map
that lies) or crash. Write the sweep once as a helper; omitting it produces maps missing most of
their entries and names that fail through operations that never touched them.

**A blend face is not reachable from the faces it joins.** `Generated(filleted edge)` gives the
blend face; `Generated(adjacent face)` gives nothing. Since `AddNewBetween` wants the two faces,
capture edge-to-face adjacency from the **input** shape with `TopExp::MapShapesAndAncestors`
*before* building — afterwards the input edge is gone and the relationship cannot be recovered.

Record both relationships, which the builder permits:

```cpp
history.AddNewBetween(blend, BlendFace, faceA, faceB);  // what survives a rebuild
history.AddGenerated(edge, blend, BlendFace);           // what SourceOf answers with
history.AddDeleted(edge);
```

Deleted-and-generating is legal and is the ordinary fillet: the edge is consumed *and* is why the
blend exists. Only deleted-and-**modified** is rejected, because `Modified` asserts the entity
survived.

**`Build()` failing does not always throw.** A 50 mm fillet on a 10 mm box returned
`IsDone() == false` and left the shape unmodified. Reading `Shape()` without checking would hand
back the input as a Success. Check `IsDone()` after every `Build()`; the firewall only catches the
failures that throw.

**Parallelism is a run-time setting, not just a build flag.** OCCT is built without TBB, but it
falls back to its own `OSD_ThreadPool` — it is not single-threaded by construction. Set
`BOPAlgo_Options::SetRunParallel(false)` on booleans and `InParallel = false` on tessellation
explicitly at every call site. They default off today; §5.2.3 wants them off by contract.

### The retry ladder

For anything marked `fragile`, work through PLAN.md 5.2.4 rather than attempting once:

1. Model tolerance.
2. Condition the inputs — sew, remove tiny edges, unify same-domain faces — and retry.
3. Relax to a fuzzy tolerance and retry.
4. For blends, isolate the failing subset edge by edge and return a `Degraded` result naming what
   failed.
5. Give up with a diagnostic that names the operation, the entities, the tolerance tried, and a
   **user-actionable** suggestion.

Set `rung` to whichever succeeded. The distribution across the corpus is a health metric: if the
rung-1 share falls between releases, something regressed even though every test still passes.

---

## Steps 4 and 5: the managed side

Add to `IGeometryKernel` (async, returns `ValueTask<OperationResult>`), then to
`GeometryKernelBase` — the async method forwards to a new protected abstract synchronous one. That
indirection is what makes ADR-0004 unbypassable: there is no public path to an implementation that
does not go through the dispatcher.

Add a definition record implementing `IOperationDefinition`, with `Validate()` and `InputShapes()`.
Validation lives on the definition rather than in the kernel so both implementations reject the
same input with the same message, which is a large part of what makes the abstraction real.

Then implement in `FakeKernel`. Not optional: the contract battery runs every kernel through the
same tests, and an unimplemented operation there means the abstraction is untested for that
operation until OCCT is available.

---

## Step 6: the fixture

`tests/regression/corpus/`, per PLAN.md 8.2. A geometry change without corpus coverage is not done.

If the operation is fragile, add a case that *should* legitimately fail as well as one that
succeeds. A blend that succeeds is easy; the interesting behaviour is what it says when it cannot.

---

## Checking your work

```powershell
./build.ps1 -Generate     # regenerate after an IDL change
./build.ps1               # verifies freshness, builds, tests, runs the corpus
dotnet run --project tests/regression/OpenMCAD.Regression -- --determinism
```

CI additionally confirms that every IDL operation is exported by the built DLL, and that a stubbed
operation returns a status code through the firewall rather than unwinding across the C ABI.

---

## Things that are always wrong here

Beyond PLAN.md 12's list, specific to this boundary:

- Letting a C++ exception escape an entry point. Undefined behaviour, not a bad error message.
- Returning a pointer into kernel-owned memory. Use the two-call pattern.
- Hand-editing a `.g.h`, `.g.cpp`, or `.g.cs` file. It will be overwritten and CI will notice.
- Adding an OCCT type to the generator or to `openmcad_types.h`. The generator must not know what
  kernel is underneath; that is what keeps ADR-0002's swap path honest.
- Returning `OperationRole.Unknown`. The value exists so omission is detectable, not so it can be
  used.
