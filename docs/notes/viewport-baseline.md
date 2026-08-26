# Viewport performance baseline

**P2-T13.** First recorded frame times for the viewport, taken so that later changes have something
to be compared against, and so that the optimisations Phase 2 deferred can be argued from numbers
rather than from instinct.

Reproduce with:

```
dotnet build -c Release
./artifacts/bin/OpenMCAD.Render.Perf/release/OpenMCAD.Render.Perf.exe --frames=60
```

## Machine

```
AMD Ryzen 7 PRO 6850U, integrated Radeon graphics, 8 physical cores
Windows 11, .NET 10, Release
1920x1080, 4x multisampling, every frame fenced
```

An integrated GPU in a laptop, so treat these as a shape rather than a ceiling. A discrete card
would move every number down and would not change which of them is the problem.

## Results

Two sets, before and after ambient occlusion was added to the frame (P2-T12). The columns are the
median; the earlier full percentile spread is kept below.

| Scene | Bodies | Triangles | Median | With SSAO | Cost |
|---|---:|---:|---:|---:|---:|
| 100k, 1 body | 1 | 99,458 | 1.17 ms | 1.49 ms | +0.32 |
| 100k, 1k bodies | 1,000 | 98,000 | 1.21 ms | 2.12 ms | +0.91 |
| 1M, 16 bodies | 16 | 991,232 | 1.90 ms | 2.46 ms | +0.56 |
| 1M, 1k bodies | 1,000 | 968,000 | 2.72 ms | 2.80 ms | +0.08 |
| **2M, 16 bodies** | 16 | 2,000,000 | **3.36 ms** | **3.51 ms** | +0.15 |
| 2M, 1k bodies | 1,000 | 1,922,000 | 4.14 ms | 4.03 ms | −0.11 |
| 2M, 10k bodies | 10,000 | 2,000,000 | 10.67 ms | 10.93 ms | +0.26 |
| 5M, 64 bodies | 64 | 4,967,552 | 7.54 ms | 6.86 ms | −0.68 |
| 5M, 10k bodies | 10,000 | 4,500,000 | 11.50 ms | 11.91 ms | +0.41 |

PLAN.md's budget is **2M triangles rotating, under 16 ms**. It is met at 3.51 ms with occlusion, with
about four and a half times the headroom.

Occlusion is two full-screen passes over the depth buffer and a third to multiply the result in, so
its cost does not depend on the geometry: it should be a constant addition to every row. It broadly
is, at somewhere under a millisecond. It is not a clean constant — two rows came out *faster* with
the extra work — which is the honest measure of how much this machine's numbers can be trusted
between runs on an integrated GPU sharing a power budget with the CPU. Differences below about half
a millisecond here are noise, and the +0.91 on the second row should be read as "under a
millisecond" rather than as three times the cost of the row above it.

The full percentile spread, without occlusion, as first recorded:

| Scene | Median | p95 | p99 | GPU median |
|---|---:|---:|---:|---:|
| 100k, 1 body | 1.17 ms | 1.32 | 1.37 | 0.93 |
| 100k, 1k bodies | 1.21 ms | 1.74 | 2.73 | 0.57 |
| 1M, 16 bodies | 1.90 ms | 2.73 | 3.55 | 1.64 |
| 1M, 1k bodies | 2.72 ms | 3.38 | 4.54 | 1.86 |
| **2M, 16 bodies** | **3.36 ms** | 4.50 | 4.87 | 2.78 |
| 2M, 1k bodies | 4.14 ms | 6.44 | 8.09 | 3.24 |
| 2M, 10k bodies | 10.67 ms | 12.53 | 15.49 | 5.68 |
| 5M, 64 bodies | 7.54 ms | 12.28 | 13.14 | 6.82 |
| 5M, 10k bodies | 11.50 ms | 14.10 | 15.10 | 7.27 |

## What the numbers say

**Triangle count is not the constraint; body count is.** Five million triangles across 64 bodies
costs 7.54 ms. Two million across ten thousand bodies costs 10.67 ms — fewer triangles, half again
the time. Each body is a draw call in the face pass and another in the edge pass, so ten thousand
bodies is twenty thousand draws, and that is what the frame is spent on.

**The gap between wall and GPU time is where the answer lives.** At sixteen bodies the two are
within a millisecond of each other and the GPU is the ceiling. At ten thousand the GPU does about
half the work of the frame and the rest is the CPU recording commands and the driver validating
them. That distinction decides what the next optimisation is: batching, not geometry.

**So instancing is not urgent, and that is the point of measuring.** P2-T05 deferred instancing and
GPU-side culling "until the harness can justify them". The harness says a ten-thousand-body
assembly already fits the budget on integrated graphics, so the case for instancing rests on
assemblies substantially larger than that — which Phase 8 will produce, and which can be measured
when they exist rather than guessed at now.

**Level of detail (P2-T04) is unjustified by this data too.** Nothing here is limited by triangle
throughput, so reducing triangle counts would buy little. It becomes interesting when a single
model exceeds what memory can hold, which is a different problem from frame time.

## Two things about the method

**Every frame is fenced**, so the CPU cannot run ahead. Without that the measurement is of how fast
commands can be recorded, which on a scene the GPU is struggling with looks wonderful and means
nothing. Real frames pipeline, so these are slightly pessimistic — the right direction for a budget
to be wrong in.

**The device is warmed for ninety frames before anything is timed.** A laptop GPU idles at a low
clock and takes about a second of sustained load to ramp. Without the warm-up the first scene
absorbed the ramp and reported 30 ms median with a 75 ms p95 — four times slower than the same body
count at twenty times the triangles. That was the power governor being measured, not the renderer,
and it is exactly the kind of number that starts a week of optimising the wrong thing.
