# Note: decisions the project is waiting on

**Date:** 2026-08-28 · **Task:** none — a standing list · **Status:** open

Things that block real work and that nobody has decided yet, plus deferred work that is written
down nowhere else. Each entry says what is blocked, what the options are, and what a
recommendation would be, so that picking it up does not mean rediscovering the question.

Delete an entry when it is decided, and put the reasoning in the ADR or the plan entry it belongs
to. This file is a queue, not a record.

---

## 1. How planegcs gets into the tree

**Blocks:** P4-T01, and therefore the rest of P4-T02, and the Phase 4 exit criterion about a
200-entity sketch dragging in under 16 ms — `FakeSolver` is not going to meet that and is not
meant to.

planegcs is eleven files, about 455 KB, at `src/Mod/Sketcher/App/planegcs/` in FreeCAD. It is
LGPL-2.1-or-later, SPDX-tagged per file. It is not distributed on its own.

Its coupling to FreeCAD is four headers, all of which are stubs or one-liners to replace:
`SketcherGlobal.h` (a DLL-export macro), `FCConfig.h`, `Base/Console.h` (logging, used in
`GCS.cpp` only) and `boost_graph_adjacency_list.hpp` (a warning-suppression wrapper). Everything
else is the standard library, Eigen and Boost.

| Option | For | Against |
|---|---|---|
| **Vendor a pinned copy** under `native/third_party/planegcs/` | Hermetic and offline; the exact source is pinned by construction, which ADR-0011 wants; shipping the source satisfies LGPL §4's relinking obligation outright | ~455 KB of someone else's code in the history; upstream fixes have to be tracked by hand |
| Fetch at build time (`FetchContent` or a submodule) | Repository stays ours | Clones a repository well over a gigabyte to get 455 KB, and *still* needs the four stub headers vendored, so the patch has to live here either way |
| Defer | Nothing to decide today | The phase cannot be finished, and the interface stays unproven against the solver it was designed for |

**Recommendation: vendor.** Fetching buys almost nothing when the patch has to be vendored
regardless. Keep it in its own directory with its own `COPYING.LIB`, an `UPSTREAM.md` recording
the commit and every patch applied, a `linguist-vendored` attribute, and `OPENMCAD_WITH_PLANEGCS`
off by default — the same shape OCCT already has.

`THIRD-PARTY-NOTICES.md` already commits to the separately-replaceable-DLL structure this needs.

**One thing to look at while doing it:** `GCS.cpp` includes `<future>`. planegcs has an internal
parallel path, and ADR-0011 makes reproducibility a hard requirement. `native/vcpkg.json` already
made exactly this call once, deliberately excluding TBB from OCCT because "results can vary with
scheduling". Check whether that path is reachable and pin it off if so.

## 2. Boost.Graph as a native dependency

**Blocks:** the same thing, and it is written down nowhere else at all.

planegcs needs Boost.Graph — `connected_components`, for its subsystem decomposition — plus
`boost/math/constants`. Both are header-only. `native/vcpkg.json` does not mention Boost, and this
repository asks for a deliberate decision before a dependency is added rather than one appearing
inside an unrelated commit.

Nothing else here wants Boost. It arrives only because planegcs does, so decide it together with
the entry above.

## 3. Turning the geometry kernel on

**Blocks:** everything from Phase 5. Nothing can produce real geometry until it is on.

`OPENMCAD_WITH_OCCT` is `OFF` in `native/CMakeLists.txt`, and the comment beside it still says
"turn on in Phase 1 (P1-T06)" — which is done, so the comment is stale and misleading. Every build
so far, local and CI, has run against `FakeKernel`.

OCCT 8.0.1 is pinned in `native/vcpkg.json` and the spike (`docs/notes/occt-spike.md`) confirmed
the version and flags. What is left is the decision to switch it on and absorb the consequences:
a much longer cold build (the nightly workflow already carries a note putting it around 660
minutes from cold), a real dependency closure for the licence-notices step, and the corpus
starting to run against `OcctKernel` as well as the fake.

---

## Deferred work, recorded so it is not lost

**One device for the render tests — it ran on CI, and CI said no.** The assembly shares a single
device (`TestDevices.Shared`, released by `RenderTestHost` before finalizers are drained). Device
constructions went from thirty-six to eight: the seven that remain belong to the three classes that
are *about* the device lifecycle, and sharing one with them would test nothing. That part stands.

What did not stand is the half that went with it. The entry here used to say "this has not run on
CI" and name the one-line revert if it went badly, and both turned out to matter:

- **It had not run on CI because sixteen commits were sitting unpushed**, which is the same failure
  `verify-ci-after-pushing` records wearing a different hat — not a pipeline nobody reads, but a
  pipeline nobody had given anything to read. CI #39 was the first run to include it.
- **It failed**: eight tests, all `DXGI_ERROR_DEVICE_REMOVED`, all in `DeviceLossTests` and
  `SwapChainTests` — precisely the two classes that build a device of their own *alongside* the
  shared one. `RenderDeviceTests`, the third such class, passed.
- **The recorded revert was applied and did not help.** `TestDevices.Software` gates
  `EnableDebugLayer` on `CI` again, and CI #40 failed identically: the same eight tests, the same
  `DXGI_ERROR_DEVICE_REMOVED`. So the debug layer is not the cause and the gate was a red herring.
  It stays gated anyway, because that is the configuration CI was last green with and re-enabling
  it is an untested change that belongs on its own.

**The shared device is the cause, and here is the mechanism the evidence supports.** Every *pass*
test passed — nine classes, all on the shared device — so a long-lived shared device is not itself
a problem. What fails is the interaction with `DeviceLossTests`, whose entire purpose is to call
`ID3D12Device5.RemoveDevice`. The failures are the two of its own tests that need a working adapter
*after* a removal (`ARemovedDeviceReportsAReason`, `EverythingCanBeRebuiltOnAFreshDeviceAfterALoss`)
plus all six `SwapChainTests`, and the third lifecycle class, `RenderDeviceTests`, passes.

The reading that fits all of it: **removing one WARP device disturbs every WARP device in the
process, and the shared device makes that permanent.** Before the refactor every device was
short-lived, so after a removal the next test built a clean one; now a removed adapter is held open
by the shared device for the rest of the run, and anything reaching for it afterwards gets
`DEVICE_REMOVED`.

**Fixed, and the diagnosis is confirmed rather than argued.** `TestDevices.Attempted` now checks
whether the device it is holding has been removed, and builds a fresh one if so. That restores
exactly what the old arrangement did by accident — a test that asks for a device gets a live one —
without giving up the sharing that stopped the host dying on the way out.

What makes this more than another guess: **the CI failure now reproduces locally.** Removing that
one check and running the suite fails the same six `SwapChainTests` that fail on the runner, plus
the new test written for it. The mechanism is therefore established, not inferred — a removed
shared device being handed to every test that follows — and the only part that remains
environment-specific is *what* removes it. On a runner, `DeviceLossTests` removing its own device
appears to take the shared one with it; on this machine it does not, so
`SharedDeviceTests` removes the shared device directly and asserts the replacement.

**CI #42 took it from eight failures to two**, which confirmed the mechanism and exposed the rest of
it. All six `SwapChainTests` passed. The two that remained were `DeviceLossTests`' own, and their
message was the useful part: not `DEVICE_REMOVED` but `RenderDeviceUnavailableException` from the
*constructor* — "no D3D12 adapter could be created, including WARP". So on a runner, while a removed
device is still referenced, no new device can be made at all. Replacing the shared device when
somebody asks for it cannot help a class that never asks and builds its own, so `DeviceLossTests`
now drops the shared device before each of its tests. The class that breaks the world tidies up
first.

That costs a little of what the sharing bought — three extra device creations per run rather than
none — which is a fair price against thirty-six, and it is the honest shape of the problem: a suite
that deliberately destroys devices cannot share one with the suite that uses them.

**CI #43 is green, so this is settled** and the sharing is kept. The revert was never needed: it
would have been an eleven-file blind edit whose own mistakes would have been indistinguishable from
the failure it was meant to cure, where the two changes that worked come to about twenty lines
between them. Recorded only so that the next person to touch this knows the fallback existed and
why it was not taken — 36 short-lived devices plus the `CI` debug-layer gate is the configuration
that was green at #38.

**The rule this leaves behind, for whoever adds the next render test:** a class that destroys a
device must not leave it alive, and must not run with somebody else's alive either. Both halves are
enforced in one place each — `TestDevices.Attempted` replaces a device it finds removed, and
`DeviceLossTests` drops the shared one before each of its tests — and both are commented with the
run number that proved them, because none of it is reproducible on a developer's machine.

One more thing found while reading rather than running: **`TestDevices`' own remarks were wrong
about which classes make their own devices.** They named `RenderDeviceTests`, `DeviceLossTests` and
`SwapChainTests` as the three that do, but the same commit converted `SwapChainTests` to
`TestDevices.Required`. The comment described the design that was intended rather than the one that
shipped, and that mismatch is most of why the failure read as surprising. Corrected.

**The nightly regression has never once passed.** Thirteen runs since it was created on
2026-08-28, thirteen failures, and the cause is the same every time: the *Licence notices* step
reports that `THIRD-PARTY-NOTICES.md` does not match the dependency closure the build resolved.
Two of its three jobs fail on it; "Rebuild from scratch" passes.

This is read from the job log rather than inferred. The log shows the OCCT build *succeeding* --
that is what the 76 to 104 minutes buy -- then `Build succeeded, 0 Error(s)` for the managed
solution in 38 seconds, and then:

    ==> Licence notices
    THIRD-PARTY-NOTICES.md does not match the dependencies this build resolved.

Why it went unnoticed for a fortnight is the interesting half. The check only runs in an OCCT
build — `build.ps1` skips it otherwise, deliberately, because a stub build has no native closure to
compare — so neither a local `./build.ps1` nor the fast CI ever executes it. The nightly is the only
thing that does, and nobody was reading it.

**What is not yet known is why it disagrees with a developer machine.** A full OCCT build here --
`./build.ps1 -Configuration Release -WithOcct`, real closure, `openmcad_occt.dll` beside forty-odd
`TK*.dll` -- reports `ok  THIRD-PARTY-NOTICES.md matches the resolved dependencies`. Same commit,
same committed file (it has not been touched since 2026-08-23), opposite answers.

The obvious suspect has been ruled out: every native version the runner resolved matches the one
installed here, checked package by package out of the log --
`opencascade[core,freetype]@8.0.1`, `freetype 2.14.3`, `brotli 1.2.0`, `bzip2 1.0.8#6`,
`libpng 1.6.58`, `zlib 1.3.2#2` -- and those are exactly the six rows in the native table. So the
drift is not a version difference in the native section, which is what this entry previously
assumed.

**What has been ruled out**, so nobody spends the afternoon on it again. Each of these was
checked against the runner's own log rather than assumed:

| Suspect | How it was checked | Result |
|---|---|---|
| Native version drift | All twelve vcpkg packages read out of the install plan in the log, with their feature sets | Identical to this machine, `opencascade[core,freetype]@8.0.1` included |
| The shipped native closure | Every `Installing: *.dll` line in the log against `native/install/Release/bin` here | Identical, 32 DLLs each, no difference either way |
| The notices file having moved | `git log` on the file | Untouched since 2026-08-23; byte-identical at the nightly's commit and at `main` |
| The commit being older than `main` | Worktree at `cc70c6c`, restored, package graph compared with `main`'s | Identical, 89 packages both |
| Stale `artifacts/obj` masking it here | `./build.ps1 -Clean -Configuration Release -WithOcct` from scratch | Still reports the notices match |

So every input that can be compared from here is the same on both machines, and the check still
answers differently. What is left is something about the runner that the log does not print --
most likely inside `Get-ManagedPackages`, which is the one half whose *output* has never been seen
from the runner: the native table is derived from files that have now been compared directly,
while the managed table's licence strings are read from the NuGet cache at generation time.

**The check that would answer this has never run.** Naming the drifted rows landed in `70ebfe5`,
at 10:06 on 2026-09-09; the most recent nightly started at 07:37, on a commit from 2026-09-03. The
next run is the first to include it, and it prints the rows present on one side and not the other.
**That is the thing to read after the next nightly** -- or after a manual `workflow_dispatch`,
which this workflow offers and which would answer it in an hour rather than a day. Until then
anything said about *which* rows differ is a guess, and this entry has already paid for two.

**A note on how this entry got wrong twice, because the method matters more than the answer.**
It first said the drift had to be in the native section, reasoning that the managed half had been
checked and cleared so the other half must be at fault. That is elimination standing in for
evidence. It was then "corrected" to say the notices were not involved at all and the OCCT compile
was failing -- inferred from step durations and from a local build passing, and wrong in the
opposite direction; the log plainly shows the compile succeeding.

Both were reached without reading the failure, and the log was readable the whole time: job logs
need authentication even on a public repository, and the token in the machine's git credential
manager supplies it. That was already written down in this project's own notes on checking CI.
The lesson is not about licences or vcpkg -- it is that a diagnosis nothing has tested is a guess,
and that a guess stated in a committed file acquires an authority it never earned. Two commits
now exist saying contradictory things about this failure; this is the one that read the log.

**P3-T13, the naming corpus.** Seven of the ten mandatory §5.3 categories are covered. The other
three — pattern instance count, mirror, imported geometry — need feature types that do not exist
until Phases 5 and 8. It closes in Phase 8, not before, and Phase 3's third exit criterion is unmet
until it does.

Sketch topology change was the seventh, and it is worth recording *why* it sat here so long: the
blocker written down was Phase 4, and that was simply wrong. The seam had always existed on
`HistoryNameResolver`; `RebuildEngine` just never passed it one, so every sketch-sourced name
resolved `Unsupported` in a real rebuild whatever the document said. One optional constructor
parameter was the whole of it. A recorded blocker is a claim like any other and goes stale like
one — `NamingCorpusTests.EveryMandatoryCategoryIsAccountedFor` is what eventually surfaced it, and
nothing plays that role for the entries below.


## Ready to build, currently recorded only inside finished tasks

Both of these live in the note on a task marked `[x]`, which is where a gap goes to be forgotten.
Neither is blocked on anything any more. Sizes are rough and are there to make them comparable
rather than to promise anything.

**Reference plane display (P2-T11) — done.** The blocker was stale: P2-T11 landed at commit 45 and the P2-T10 transparency it said it was waiting for arrived at 53. Built as `ReferencePlanePass` with `DisplayPlane` on the snapshot; see the P2-T11 note in PLAN.md for the decisions.

**Selection does not survive a rebuild, and no task owns finishing it.** P2-T09's note and `SelectionSet.cs` both say a selection surviving a topology change "needs the persistent naming of PLAN.md 5.3", and the source says it "does not exist yet". It does: the whole of P3-T08 to P3-T12 shipped it, and `OpenMCAD.Interaction` already reaches `OpenMCAD.Core` through its existing references, so nothing structural is in the way.

But this is only half a stale blocker, and the other half is the point. The **resolve** direction exists -- `NameResolver.Resolve(PersistentName, FeatureId)` -- while nothing anywhere **mints** a `PersistentName` from a picked `SubEntity`. The only two ways to construct one are `PersistentName.Of` and the text parser; there is no name generator. `RebuildHistory` carries the per-feature `HistoryMap`s such a pass would walk, so the material is there, but the pass is not.

Worth deciding where that belongs. P6-T06 is selection *filters* and box-select, not rebuild survival, so on the current plan nobody is scheduled to do it -- which is how a half-cleared blocker stays invisible: the note that would have flagged it still says the thing it is waiting for does not exist.

**Silhouette edges on curved surfaces (P2-T06).** "Everything but silhouettes." A cylinder currently
shows its end circles and its seam but nothing where its wall turns away, which reads as a drawing
error rather than as a style. Not blocked, but genuinely harder than the above: a silhouette is a
property of the view rather than of the model, so it has to be found per frame, and that needs face
adjacency the display mesh does not currently carry. Sizing it honestly needs a look at what the
tessellation actually produces first.

---

## Decisions smaller than the three above, but still decisions

**P2-T04, LOD — build it or strike it.** Everything else in the tessellation pipeline is done.
P2-T13's measurements say nothing is limited by triangle throughput (2M triangles across 16 bodies
costs 3.36 ms; 2M across ten thousand costs 10.67 ms), so reducing triangle counts buys little and
the next optimisation is batching. LOD becomes interesting when a model exceeds what memory can
hold, which is a different problem from frame time. Striking it and reopening it in Phase 15, where
`P15-T01` already names occurrence-level LOD, may be righter than building it now.

**P4-T11, `Intersect` against a circular edge.** A line crosses a plane at zero or one point; a
circle crosses at zero, one or two. "One external reference produces one sketch entity" is
deliberate, and there is nowhere to put a second point without giving `SketchExternalReference` a
multiplicity policy the way `EntityReference` has one for kernel topology. That is a real design
decision rather than missing code, and it should be made with the assembly work that will exercise
it rather than invented here.

**The subsystem specs are written — and the habit that lost them is not fixed.** All six
`docs/specs/README.md` lists now exist: `kernel-abstraction`, `kernel-shim`, `naming`,
`rendering`, `sketch`, `document-model`, `persistence`. Four of them were written retroactively, in
one go, long after the subsystems they describe — `rendering.md` was due in P2 and `naming.md` in
P3, and the entry that used to sit here only ever noticed the sketch one was missing.

Keeping a spec current is PLAN.md 8.5's definition of done rather than a chore for the end of a
phase, and writing four at once is the evidence that it was not being treated that way. Nothing
enforces it: a phase can close with its spec unwritten and only a reader comparing the directory
against the index would know. Worth deciding whether that check belongs in CI, in the same spirit
as `EveryMandatoryCategoryIsAccountedFor` — a phase's exit criteria could name its spec, and a spec
listed in the index but absent from the directory could simply fail the build.
