# Regression corpus

PLAN.md 8.2. Every fixture is a directory containing `fixture.json`: what to build, and what the
result must be.

**The rule, and it has no exceptions: every bug fix ships with a fixture that reproduces it.**
This is the mechanism by which the product gets more robust over years rather than oscillating
forever. A fix without a fixture is a fix that will be undone by someone who does not know it
happened.

## Categories

| Directory | What belongs in it | Lands |
|---|---|---|
| `basic/` | Primitives and single-feature parts | P1 |
| `naming/` | The scenarios in PLAN.md 5.3 — **the most important directory in the repository** | P3 |
| `boolean/` | Tangency, coincident faces, near-degenerate, many-body | P1/P7 |
| `blend/` | Fillet chains, variable radius, setbacks, blends that should legitimately fail | P7 |
| `sketch/` | Solver convergence, over- and under-constrained diagnosis, drag stability | P4 |
| `assembly/` | Mates, subassemblies, in-context, patterns, interference | P9 |
| `drawing/` | Hidden-line correctness, section views, annotation associativity | P10 |
| `exchange/` | STEP and IGES round-trips, including deliberately malformed input | P11 |
| `format/` | One file saved by every released version, opened on every build | P3 |
| `pathological/` | Real-world inputs that once broke us — one per fixed bug | ongoing |

## Running it

```powershell
dotnet run --project tests/regression/OpenMCAD.Regression -- --verbose
dotnet run --project tests/regression/OpenMCAD.Regression -- --determinism
dotnet run --project tests/regression/OpenMCAD.Regression -- --filter cylinder
dotnet run --project tests/regression/OpenMCAD.Regression -- --kernel occt   # from P1-T06
```

`--determinism` runs the whole corpus twice on fresh kernels and diffs the topology and role
signatures. A difference is a P0 even when every fixture still passes: non-determinism silently
corrupts undo, caching, and naming, and it surfaces months later as unrelated intermittent bugs
(ADR-0011).

## Writing a fixture

Units are SI, always — metres, radians, kilograms (ADR-0013). A dimension written in millimetres
will pass its own assertions and be wrong by a factor of a thousand against every other fixture.

`description` is required and is not decoration. A golden value nobody can explain is a golden
value nobody dares change, so say what the fixture is for and what breaking it would mean.

`requiresExactMassProperties` (default true) skips the mass assertions on a kernel that does not
claim exactness. `FakeKernel` says so for booleans and blends. Assertions are **skipped and
reported**, never quietly relaxed — a corpus that loosens its tolerances to keep passing has
stopped being a corpus.

## Turning a repro bundle into a fixture

Kernel failures capture a bundle (P1-T13) containing the operation, its parameters, the tolerance,
the diagnostics, and the input geometry. Copy it into `pathological/`, add the expected values, and
it becomes a permanent guard. That path is the point: it is how the corpus grows faster than users
find bugs.
