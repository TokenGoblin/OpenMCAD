# Spec: rendering and picking

**Status:** built and running. Every claim here is verified by reading pixels back from a WARP
software device — there is no reference-image comparison and no GPU in CI. Two P2 tasks are
deliberately incomplete (P2-T04's LOD, P2-T06's silhouettes) and say so in section 11.

**Tasks:** P2-T01 through P2-T13. P2-T04 is the open one.

This expands PLAN.md §5.10 and records ADR-0008. It is written for someone changing the renderer
without first reading twenty-two files: the frame, why the passes are in that order, which
decisions are load-bearing, and which of §5.10's list is not built.

---

## 1. What is here

`OpenMCAD.Render`, with the D3D12 backend under `Direct3D12/`.

| Piece | Where | Task |
|---|---|---|
| RHI contract — `IRenderDevice`, `IGpuBuffer`, `RenderDeviceInfo` | `IRenderDevice.cs` | P2-T01 |
| Device, adapter choice, queue, fence | `Direct3D12/D3D12RenderDevice.cs` | P2-T01 |
| Descriptor heaps, upload ring | `Direct3D12/DescriptorHeapAllocator.cs`, `UploadRing.cs` | P2-T01 |
| Swapchain and resize | `Direct3D12/SwapChainTarget.cs` | P2-T02 |
| The immutable scene — `DisplaySnapshot`, `DisplayBody`, `DisplayMesh`, `DisplayEdges`, `DisplayId` | `DisplaySnapshot.cs` | P2-T03 |
| Atomic swap | `SnapshotHolder.cs` | P2-T03 |
| double→float against a sticky origin, per-face ids | `SnapshotBuilder.cs` | P2-T04 |
| GPU-side scene | `Direct3D12/SceneGeometry.cs` | P2-T05 |
| Shaded faces | `Direct3D12/FacePass.cs`, `Shaders/Surface.hlsl` | P2-T05 |
| Edges as screen-space quads | `Direct3D12/EdgePass.cs`, `Shaders/Edges.hlsl` | P2-T06 |
| ID pass and readback | `Direct3D12/IdPass.cs`, `IdTarget.cs`, `PickReadback.cs`, `PickResolver.cs` | P2-T07 |
| Camera, standard views, orientation gizmo | `Camera.cs`, `Direct3D12/AxisOverlayPass.cs` | P2-T08 |
| Highlight states | `HighlightTable.cs`, `Direct3D12/HighlightBuffer.cs` | P2-T09 |
| Weighted-blended OIT | `Direct3D12/TransparencyPass.cs`, `TransparencyTarget.cs`, `Shaders/Composite.hlsl` | P2-T10 |
| Grid, background | `Direct3D12/EnvironmentPass.cs`, `Shaders/Environment.hlsl` | P2-T11 |
| MSAA, SSAO, default material | `Direct3D12/MsaaTarget.cs`, `AmbientOcclusionPass.cs`, `SurfaceMaterial.cs` | P2-T12 |
| The frame itself | `Direct3D12/ViewportRenderer.cs` | P2-T02, P2-T05 |
| Perf harness and its numbers | `tests/perf/OpenMCAD.Render.Perf`, `docs/notes/viewport-baseline.md` | P2-T13 |

ADR-0008: **D3D12 through Vortice.Windows**, chosen for the things a general 3D engine serves
badly — an integer ID buffer for pixel-exact picking, depth-biased lines, order-independent
transparency, heavy instancing, and deterministic frame pacing during a drag. The RHI is abstracted
*thinly* so a D3D11 fallback stays possible; descriptor heaps, upload rings and fences are D3D12
concepts and are deliberately **absent** from `IRenderDevice` — they are how this backend meets the
contract, not part of it.

---

## 2. The frame

`ViewportRenderer.RenderFrame`, in order, with the reason for each position:

```
wait on this slot's fence          ← one value, not idle: waiting for idle halves the frame rate
reclaim + begin upload ring
sync scene, update highlights
resize MSAA target
clear the multisampled target      ← never the back buffer; the resolve owns that
  environment                      ← writes no depth, sits at the far plane, so it never sorts
  faces  (or transparent accumulate)
  ambient occlusion                ← before edges: it darkens surfaces, and after would darken lines
  edges
  transparent composite
axes                               ← after the scene, so the triad can be occluded by it
id pass                            ← only when a pick is outstanding
resolve MSAA → back buffer
present
```

**Everything is drawn multisampled and resolved at the end**, rather than post-filtered. The
aliasing that matters in CAD is on geometric silhouettes; a post-process works from the finished
image, can only guess where an edge was, and softens text and fine detail while never quite fixing
the staircase. This is why FXAA is unlikely to be worth adding despite §5.10 listing it.

**The camera is shifted by the snapshot origin, not the geometry** (section 4). Translating a matrix
built around a point a kilometre away would put the large number straight back into the transform
that the origin exists to keep it out of.

Culling is per body, on the CPU, in **world** space against a frustum built from the *unshifted*
matrices — because a body's bounds are in world space too.

---

## 3. `DisplaySnapshot` and the swap (P2-T03)

The render-side model is immutable, and `SnapshotHolder` is **the one place a swap happens**, so
§4.2's "no lock between rebuild and render" is enforced rather than hoped for.

**It rejects a snapshot older than the one it holds.** Rebuilds run concurrently where the graph
allows, so two can finish out of order; a plain assignment would leave the viewport showing a
superseded scene, intermittently and unreproducibly.

`DisplaySnapshot` carries geometry and identity and **no appearance**. That is why scene opacity is
one figure rather than per body, and why the default material is a scene-wide constant: per-body
materials arrive with the document model, not here.

---

## 4. Coordinates: the sticky origin (P2-T04)

The kernel works in double; the GPU works in float. `SnapshotBuilder` converts relative to a per-view
origin so that a part a kilometre from the world origin still has millimetre detail.

**Rounding the origin to a grid was not enough**, and this is the decision worth keeping. A scene
centred on a grid line flips between two origins on a millimetre edit, and every buffer re-uploads.
The origin is therefore *carried forward* until the scene genuinely drifts away from it — sticky,
not snapped.

---

## 5. Picking (P2-T07)

An R32_UINT target, one integer per pixel, resolved on the CPU afterwards. §5.10 calls this the
single most important rendering decision for perceived quality, and the implementation has three
properties that matter:

- **The ID pass shares its vertex shaders with the visible passes, byte for byte.** What is picked
  is what is drawn. Any divergence would show up as a hover highlight that is subtly wrong near
  silhouettes, which is exactly where users notice.
- **Readback never blocks.** A pick is tagged with the fence value that will retire it and collected
  frames later. A request arriving with every slot busy is **dropped, not queued**, so a drag cannot
  build a backlog of stale cursor positions.
- **The ID buffer is single-sampled on purpose.** Resolving indices would average them into a number
  naming an entity that is under the cursor nowhere.

`PickResolver` biases towards edges and vertices over faces, so thin entities stay pickable.

**Vertices are missing for an upstream reason**: the kernel's mesh reports faces and edges as
entities but not vertices, so there is nothing to give an id to. Not a gap in the renderer.

---

## 6. Highlighting (P2-T09)

States travel to the GPU as **one array indexed by the same display id the ID pass writes**, so a
highlight costs no extra draw and no extra geometry — the shaded pass tints what it was already
drawing.

- **Faces are tinted; edges take the colour outright.** A flat-filled selection stops reading as a
  shape; a hairline has no shading to protect.
- **Pre-selection is kept apart from selection**, so hover cannot destroy what the user chose. Error
  outranks both.
- **Selection holds `SubEntity`, not `DisplayId`.** Ids are snapshot-scoped and would migrate to
  whatever entity inherited the number. That still does not survive a rebuild that renumbers
  topology — which is what §5.3's persistent naming is for.

One D3D12 trap recorded here: the state buffer is bound as a **root descriptor**, which carries an
address and no length. `GetDimensions` on one is meaningless and reading past the end was an access
violation in the test host, so **the count travels in the frame constants**.

---

## 7. Transparency (P2-T10)

**Sorting back to front fails on exactly what CAD produces** — a housing containing its own contents,
two interpenetrating parts, a body whose faces overlap from the current angle. Sorting is per object
and the failure is per pixel, so no ordering of objects fixes an object that overlaps itself. The
symptom is faces popping in front of one another during an orbit, which reads as *the model changing*.

Weighted blending accumulates with a depth-dependent weight and keeps the product of what each
fragment let through. Both are commutative, so order cannot matter. It is an approximation, but a
uniform and stable one — which beats occasional exactness that flips as the camera moves.

The test that matters renders two overlapping bodies both ways round and requires the same pixel,
**having first checked both actually reach it**.

---

## 8. Environment and overlays (P2-T11)

**The grid is computed per pixel from a ray–plane intersection, not drawn as lines.** Line geometry
needs an extent and a spacing fixed in advance, and a CAD user zooms across six orders of magnitude
in a session. Spacing snaps to a power of ten below a tenth of the scene, and is taken **from the
scene rather than the camera** — a reference that changes density as you zoom is worse than one at
the wrong scale.

**The triad is depth-tested and the orientation gizmo is not**, which is the difference between a
landmark and an overlay: the eye reads "drawn over a solid" as "in front of the solid", and no colour
choice argues it out of that.

The gizmo reports the camera's **rotation only** and deliberately ignores pan and zoom. There are
tests for what it must *not* follow as well as what it must — a gizmo that drifted while panning
would be actively misleading about the one thing it exists to report.

A clickable labelled view cube needs text rendering, which does not exist until Phase 6.

---

## 9. Shading, MSAA and SSAO (P2-T05, P2-T12)

- **Two-sided shading**, because CAD models are routinely viewed from inside.
- **D32_Float depth**, because a mechanical scene outruns 24-bit precision.
- **A facet normal reconstructed from derivatives** when a mesh carries none.
- **The key light is offset from the eye.** A pure headlight shades all three visible faces of an
  isometric cube identically — found by a test, not by looking.
- **MSAA at four samples**, negotiated with the device and falling back through two to one.
- **SSAO from the depth buffer alone**, not from a normal target: the renderer is forward-shaded and
  a G-buffer would cost more bandwidth every frame than the whole pass costs. Under a millisecond at
  1920×1080, and it is what makes a pocket or an inside corner readable.

`SurfaceMaterial` is a type rather than four literals in a shader expression, pushed as root
constants and shared by the shaded and transparent paths. It is held to **one rule that can be
stated and tested: ambient plus diffuse must not exceed one**, so no surface is drawn brighter than
its own colour and the highlight is the only thing that can exceed it. The numbers this shader
carried before totalled 1.25, which blows ~2,300 pixels of a white cube to pure white and throws
away the shading they were carrying; a test renders both materials to show it.

### Three defects no D3D12 call reported

Each is now a test that fails if reintroduced. They are recorded because all three were silent:

1. **Vortice follows the CD3DX12 convention where `Offset` mutates the handle it is called on.**
   Four chained calls wrote descriptors to slots 0, 1, 3 and 6 of a four-slot heap, and the apply
   pass silently read the depth buffer as its occlusion.
2. **A normal reconstructed as `cross(right, down)` faces away from the camera** in this right-handed
   view space, which flipped the SSAO sampling hemisphere into the solid and darkened the model
   almost to black.
3. **The range cutoff is what stops a foreground object shadowing the distant background** behind it.

---

## 10. Device loss (P2-T02)

The **policy** lives one layer up, in `OpenMCAD.Shell/ViewportHost.cs` — it is a question about a
window that has stopped drawing, not about a device. What the render assembly contributes is the
rebuild path and `D3D12RenderDevice.IsRemoved`.

**Recovery rebuilds through the same code path as start-up**, so the two cannot drift. A separate
recovery routine runs perhaps once a year on somebody else's machine and silently falls behind every
new piece of viewport state.

**The camera is carried across rather than rebuilt** — it is the one piece that is not a GPU resource,
and a view snapping back to default on a driver update would be a worse failure than the one being
recovered from.

Attempts are counted within a five-minute window and give up after `MaxDeviceLossAttempts`: a
genuinely broken device fails again immediately, and retrying inside the frame loop would spin the
machine. The window is what lets a machine that loses its device twice in a year recover both times
rather than being one failure closer to giving up for good.

`ID3D12Device5.RemoveDevice` makes this testable rather than merely reasoned about — and doing so
established something worth knowing: **submitting work to a removed device does not fail.** Recording,
executing and waiting on a fence all report success while nothing happens, because a removed device
signals every fence to its maximum. A viewport notices via the present; anything rendering off-screen
has to ask, which is why `D3D12RenderDevice.IsRemoved` exists and why the perf harness checks it.

---

## 11. What the numbers say, and what is not built (P2-T13, P2-T04)

`docs/notes/viewport-baseline.md` has the measurements. The budget — 2M triangles rotating under
16 ms — is met at **3.36 ms on integrated graphics**.

**The useful finding is the shape, not the headline: triangle count is not the constraint, body
count is.**

| Scene | Cost |
|---|---|
| 5M triangles across 64 bodies | 7.54 ms |
| 2M triangles across 10,000 bodies | 10.67 ms |

Each body is a draw in the face pass and another in the edge pass. Where wall time and GPU time
diverge is the CPU recording and the driver validating — which says **the next optimisation is
batching, not geometry**.

Every frame is fenced, and the device is warmed for ninety frames first: without that the first
scene measured the laptop's power governor ramping and reported four times its true cost.

| Not built | Why |
|---|---|
| **LOD** (P2-T04, and why it is still open) | Nothing measured is limited by triangle throughput, so reducing triangle counts would buy little. It becomes interesting when a model exceeds what memory can hold, which is a *different problem* from frame time. `open-decisions.md` records that striking it may be righter than building it. |
| Instancing, GPU frustum/occlusion culling | §5.10 asks for them; the harness says body count is the cost and batching is the answer. Not started, deliberately, so the work is aimed by numbers. |
| Silhouette edges on curved surfaces (P2-T06) | A property of the view rather than of the model, so they must be found per frame. A cylinder shows its end circles and its seam but not where its wall turns away. |
| Reference plane display (P2-T11) | Wants transparency to look like planes rather than walls. |
| Vertex picking (P2-T07) | Upstream: the kernel's mesh has no vertex entities. |
| FXAA/TAA | Section 2 — likely not worth adding given the MSAA arrangement. |
| Per-body appearance and materials | `DisplaySnapshot` carries no appearance; arrives with the document model. |
| Section and exploded views | §5.10 names them; Phase 5 and later. |

---

## 12. Testing, and what it does not cover

`tests/unit/OpenMCAD.Render.Tests`, 200 tests, **all against WARP** — a software rasteriser, which
is also all a build machine has. Correctness is asserted by **reading pixels back**, not by
comparing reference images: a pass that draws a red cube is checked by finding red pixels where the
cube is and background where it is not.

The assembly shares **one device** for the whole run (`TestDevices.Shared`), released before
finalizers are drained. Only the three classes that are *about* the device lifecycle create their
own. Collections run one at a time; concurrent device creation crashed the test host one run in
three.

Two things this suite genuinely cannot tell you:

1. **Whether it looks right.** Pixel readback proves a face was drawn and roughly what colour. It
   cannot catch a subtly wrong highlight falloff or a material that reads as plastic.
2. **Whether validation is on.** The D3D12 debug layer ships with the optional Graphics Tools
   feature rather than with Windows, so requesting it on a machine without it succeeds and validates
   nothing. `RenderDeviceInfo.ValidationEnabled` reports what actually happened rather than what was
   asked for — and on the machine this spec was written on, it is **false**.
