# Kernel performance baseline

**P1-T15.** First recorded measurements of the kernel operations, taken so that later changes have
something to be compared against. A baseline without its hardware is a rumour, so the machine is
recorded with the numbers.

Reproduce with:

```
./build.ps1 -Configuration Release -WithOcct -SkipTests -SkipRegression
dotnet run -c Release --project tests/perf/OpenMCAD.Kernel.Benchmarks -- --filter '*'
```

## Machine

```
BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9168/25H2)
AMD Ryzen 7 PRO 6850U with Radeon Graphics 2.70GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.302, .NET 10.0.10, X64 RyuJIT x86-64-v3
OCCT 8.0.1 via vcpkg, TBB off, shared, Release
```

A laptop on battery-capable silicon, so treat these as an order of magnitude rather than a
precision figure. What matters is the shape of the distribution and how it moves.

## Measured, 2026-08-22

Each figure is one operation *including its history map*, because that is what a rebuild pays.
Timing the geometry alone would flatter the shim and hide the cost of the thing ADR-0005 depends
on.

| Operation | FakeKernel | OcctKernel | OCCT allocated |
|---|---:|---:|---:|
| create box | 15.0 µs | 854 µs | 25.4 KB |
| create cylinder | 4.7 µs | 628 µs | 12.9 KB |
| extrude profile | 20.3 µs | 923 µs | 53.1 KB |
| boolean subtract | 38.9 µs | 5,251 µs | 86.8 KB |
| fillet, 4 edges | 34.7 µs | 13,563 µs | 97.3 KB |
| triangulate (display) | 2.7 µs | 1,372 µs | 4.6 KB |
| mass properties | 1.9 µs | 979 µs | 1.2 KB |
| write BREP | 1.5 µs | 463 µs | 5.4 KB |

`FakeKernel` is not a control group — it computes different things — but it is the floor. It shows
that the dispatcher, the handle plumbing and the history machinery cost single-digit to low tens of
microseconds with the geometry taken out, so everything above that in the OCCT column is geometry
and the shim's mapping of it.

## Against the §7 budgets

The relevant budget is *full rebuild, 100-feature part, cold cache, under 8 s* — an average of
80 ms a feature. The slowest operation measured is a four-edge fillet at 13.6 ms, which leaves
roughly a factor of six in hand, and a boolean at 5.3 ms leaves fifteen. **The budget is met with
headroom at this scale**, on the understanding that these are small bodies: a fillet on a body with
a thousand faces is not a fillet on a box, and the corpus does not yet contain one.

Nothing here is close enough to the budget to justify optimising now. The number worth watching is
the fillet, both because it is the largest and because ADR-0001 already names blends as the weak
point.

## Where the time goes

The one structural observation worth recording, because it will be the first thing to look at when
these numbers stop being comfortable:

**Canonical ordering is recomputed, repeatedly, per operation.** `enumerate_canonical` sorts by a
geometric key, and building that key calls `BRepGProp::SurfaceProperties` or `LinearProperties` on
every entity — a real integration each time. Creating a box does eighteen of them for six faces and
twelve edges, which at roughly 47 µs apiece accounts for most of its 854 µs. Worse, the ordering is
computed more than once per operation: once per entity kind while mapping history, again in the
created-entity sweep, and again for each managed `Enumerate` call.

Caching the canonical order per (shape, kind) in the handle table would remove most of that. It is
not done here because P1-T15 is a baseline rather than an optimisation, the budget is met, and a
cache is exactly the kind of change that should be made against a measurement rather than a
suspicion. This is the measurement.

Secondary note: `create box` has the widest spread of anything measured (σ = 161 µs on a mean of
854 µs, median 764 µs). It is the first benchmark to run and it grows the handle table, so some of
that is warm-up rather than the operation.

## What this baseline does not cover

- **Scale.** Every fixture is a body with tens of entities. The budgets are written for parts with
  a hundred features and assemblies with five thousand components, and nothing here says how any of
  this behaves at that size. Scale scenarios belong with the perf work in later phases.
- **Rebuild as a whole.** These are individual operations. A rebuild also pays for the dependency
  graph, the cache, and naming resolution, none of which exist yet.
- **The retry ladder.** Every operation here succeeds on the first rung. A body that needs
  conditioning pays for a failed attempt first, and that cost is unmeasured.
- **Cold start.** Constructing a kernel starts a thread and initialises OCCT. It is excluded
  deliberately — it is a real cost but not a per-operation one — and it is the other half of the
  cold-start budget.
