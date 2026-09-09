# Spec: the sketcher

**Status:** built and tested against `FakeSolver`. The planegcs implementation does not exist —
P4-T01 is blocked on a decision recorded in `docs/notes/open-decisions.md`, and every solve so far,
local and CI, has run against `FakeSolver`.

**Tasks:** P4-T02 through P4-T16. P4-T01 and the planegcs half of P4-T02 are outstanding; P4-T11
and P4-T15 are deliberately partial and say where below.

This expands PLAN.md §5.6. It is written for someone arriving at this subsystem cold and needing to
change it without first reading every file: what is where, which invariants everything else leans
on, and which gaps are deliberate. Per `README.md` here, that is the point — these files exist
against R13, context loss across a long build, not as a write-up.

---

## 1. What is here

Everything solver-side lives in `OpenMCAD.Solver`, almost all of it under `Sketching/`.

| Piece | Where | Task |
|---|---|---|
| Entity model — 9 kinds, `EntityPoint`, degeneracy | `Sketching/SketchEntity.cs`, `SketchBSpline.cs` | P4-T03 |
| Identity — `SketchEntityId`, `SketchPointRef` | `Sketching/SketchEntityId.cs` | P4-T03 |
| Constraint model, schema table | `Sketching/SketchConstraint.cs`, `ConstraintSchema.cs` | P4-T04 |
| Collections — `Sketch`, `SketchEntitySet`, `ConstraintSet` | `Sketching/Sketch.cs`, `SketchEntitySet.cs`, `ConstraintSet.cs` | P4-T04 |
| Solver contract | `ISketchSolver.cs` | P4-T02 |
| Parameter flattening | `Sketching/SketchParameters.cs` | P4-T02 |
| Constraint equations | `Sketching/ConstraintResiduals.cs` | P4-T02 |
| `FakeSolver` — real Levenberg–Marquardt | `OpenMCAD.Solver.Fake` | P4-T02 |
| Diagnosis rules | `SolveDiagnostics.cs`, `SolveEvidence` | P4-T06 |
| Subsystem decomposition, ground, freezing | `Sketching/SketchAnalysis.cs` | P4-T05 |
| Drag coalescing | `DragSession.cs` | P4-T07 |
| Inference | `Sketching/ConstraintInference.cs` | P4-T08 |
| Snapping, and `Crossings` | `Sketching/SketchSnapping.cs` | P4-T09 |
| Editing tools | `Sketching/SketchEdit.cs`, `SketchTransform.cs`, `SketchGeometryTransform.cs`, `SketchTrim.cs`, `SketchExtend.cs`, `SketchSplit.cs`, `SketchCorner.cs`, `SketchOffset.cs` | P4-T13 |
| Profile detection | `Sketching/ProfileDetection.cs` | P4-T14 |
| Dimensions | `Sketching/SketchDimension.cs`, `SketchDimensionLayout.cs` | P4-T12 |
| JSON interchange form | `Sketching/SketchFormat.cs` | P4-T04 |
| View model — geometry, constraints, readout, every editing tool | `OpenMCAD.ViewModels/SketchEditorViewModel.cs` | P4-T15 |
| Regression corpus, 7 fixtures | `tests/regression/corpus/sketch/`, run by `SketchCorpusTests` | P4-T16 |

Two pieces live **outside** `OpenMCAD.Solver`, in `OpenMCAD.Modeling`, and section 8 says why:
`SketchPlaneReference`/`SketchPlane`/`SketchPlaneResolver` (P4-T10) and
`SketchExternalReference`/`SketchExternalReferenceResolver` (P4-T11).

---

## 2. The layering, and why the boundaries are where they are

```
OpenMCAD.Modeling      plane references, external references   (needs Core + Kernel)
        │
OpenMCAD.Solver        sketch, constraints, tools, profiles    (needs only Math)
        │
   ISketchSolver       ── the swap point ──
        │
OpenMCAD.Solver.Fake   FakeSolver                OpenMCAD.Solver.Planegcs (not built)
```

Three boundaries carry weight.

**`ISketchSolver` takes and returns a whole `Sketch`, never a parameter vector.** The vector is the
solver's business; a caller that had to scatter one back into entities would be doing the solver's
bookkeeping, and two callers would eventually do it differently. This narrowness is also what makes
ADR-0006's contingency real — swapping planegcs for a managed rewrite or a commercial licence stays
contained only while nothing above here knows how a solve is done.

**`OpenMCAD.Solver` depends on `OpenMCAD.Math` and nothing else.** No document, no kernel. That is
why the plane and external references had to go up a layer: a plane reference has to see both
`ReferenceGeometry` (Core) and kernel topology, and pulling either down here would put the whole
document layer beneath the solver.

**Constraint equations live with the constraints, not inside a solver** (`ConstraintResiduals`).
The equations *are* the meaning of the constraints; a second solver deriving its own would be a
second opinion about what "tangent" means.

---

## 3. Entity model (P4-T03)

Nine kinds today: `SketchPoint`, `SketchLine`, `SketchCircle`, `SketchArc`, `SketchEllipse`,
`SketchEllipticalArc`, `SketchParabola`, `SketchHyperbola`, `SketchBSpline`. Text is Phase 7.
All derive from `SketchEntity` and all are immutable records.

Invariants everything else leans on:

- **Geometry is held as values, not as indices into a vector.** A line has a `Start` and an `End`.
  Flattening happens once, at the solver boundary.
- **An arc always sweeps anticlockwise.** `Sweep` is always positive. A signed sweep would make
  "the same arc" two different entities and force every endpoint constraint to know which
  convention it was written under. Three separate pieces of work have had to reverse `Start` and
  `End` because of this rule and none of them may quietly stop: mirroring under a reflecting
  transform (P4-T13), a circular edge viewed from the far side of a sketch plane (P4-T11), and the
  direction handling in `SketchOffset`.
- **Constraints attach to *named* points, never to indices.** `EntityPoint` is
  `Self | Start | End | Centre | Focus | SecondFocus | Middle`. An index means something different
  per kind, and the first entity to gain a point would silently move every stored index after it.
- **An entity is asked which points it has** (`Points`), so a constraint pointed at one it does not
  have is caught when the constraint is made, not when the solver reads a coordinate nobody wrote.
- **Degeneracy is checked here** (`Degeneracy`, surfaced by `Sketch.Problems`), not left to the
  solver, which would report a zero-radius circle as a non-convergence somewhere unrelated.
- **`ToString` is sealed on the base.** A record regenerates its own in every derived type, which
  shadowed the override and printed the whole member list including computed properties.

Storage choices that are not arbitrary: elliptical arcs store *eccentric* angles (polar ones need a
transcendental solve to evaluate a point); parabolas store vertex and focus, not coefficients, which
are relative to whatever axes they were written in; splines are rational from the start, because a
non-rational spline is the case where every weight is one and retrofitting weights would mean
rewriting every file — and only a rational quadratic can be exactly a circular arc, which is the
test proving the weights reach the de Boor recursion rather than being corrected afterwards. Knots
are distinct values with multiplicities, so the one mistake that matters — a count disagreeing with
the repeats — cannot be written.

---

## 4. Constraint model (P4-T04)

One record with a `Kind`, not eighteen records. The solver boundary needs a uniform representation
anyway, and what each kind requires is then a table — `ConstraintSchema` — rather than a type per
kind plus a case in every switch that walks them. **Adding a constraint kind is one row.**

The 18 kinds and what the solver is actually given:

| Kind | Operands | Value | Equations |
|---|---|---|---|
| `Coincident` | point, point | — | 2 |
| `PointOnObject` | point, curve | — | 1 |
| `Distance` | point, point (or point, line) | length | 1 |
| `HorizontalDistance`, `VerticalDistance` | point, point | length | 1 |
| `Horizontal`, `Vertical` | line (or point, point) | — | 1 |
| `Parallel`, `Perpendicular` | line, line | — | 1 |
| `Tangent` | curve, curve | — | 1 |
| `Equal` | any, any | — | 1 |
| `Symmetric` | point, point, line | — | 2 |
| `Concentric` | circular, circular | — | 2 |
| `Midpoint` | point, line | — | 2 |
| `Angle` | line, line | angle | 1 |
| `Radius`, `Diameter` | circular | length | 1 |
| `Fix` | point | — | 2 |

Rules that hold across all of them:

- **Operands are point references throughout.** Where a kind wants a whole entity, the operand
  names it with `EntityPoint.Self` — which for a point entity is also its position, and that is not
  a coincidence. One operand type means one resolution path, which is what stops the sketcher and
  the solver disagreeing about what a constraint was attached to.
- **A reference dimension is a flag (`IsDriving`), not a kind.** It measures the same thing and
  differs only in whether the solver is told. A separate kind would double the table and make
  "convert to reference" a change of type.
- **`Fix` takes a point, not an entity.** A `Fix` whose meaning changed with what it was pointed at
  would remove a different number of freedoms each time and make the readout unexplainable. Fixing
  a circle is fixing its centre plus giving it a radius, which says exactly what it does.
- **The equation counts are what a solver actually gets**, not what feels right. Getting one wrong
  does not break a solve; it breaks the degree-of-freedom readout, which is worse, because the
  number looks authoritative and is quietly false.
- **A kind may accept more than one operand shape.** Horizontal is one line or two points, and both
  say the same thing to a user. The complaint reported comes from the shape with the right number
  of operands.
- **Validation is against the geometry**, not in isolation: most of what can be wrong with a
  constraint is a fact about what it names — a radius on a line, a tangency between two points, a
  reference to something deleted, an operand named twice.

`Sketch` holds geometry and constraints together because deleting an entity has to take its
constraints with it (`Sketch.Without`). One left pointing at deleted geometry names a coordinate
nobody will write, and the failure surfaces as a solve that does not converge for no visible reason.

`Sketch.RemainingFreedom` is **an upper bound and says so**. Two constraints saying the same thing
both subtract, which is exactly what makes a sketch redundant rather than over-constrained; telling
those apart needs the rank of the Jacobian (section 6).

---

## 5. The solver contract (P4-T02)

```csharp
SolveResult Solve(
    Sketch sketch,
    DragTarget? drag = null,
    SolverOptions? options = null,
    CancellationToken cancellationToken = default);
```

`SolverOptions.Default` is 100 iterations to 1e-10, no budget. `SolverOptions.ForDrag` is 25
iterations to 1e-8 with a 12 ms budget — a budget rather than a promise, because §5.6 requires a
200-entity sketch to drag in under 16 ms and a solve going badly must be abandoned rather than
finished. A dropped frame is worse than a solve that stops one iteration early and says so.

**`SketchParameters` is the single flattening.** Its order is by entity in sketch order, because the
vector's order decides the Jacobian's columns and therefore which of several equally valid answers a
least-squares step finds. Widths: point 2, line 4, circle 3, arc 5, ellipse 5, elliptical arc 7,
parabola 6, hyperbola 7, B-spline 3 per pole. **Knots are deliberately not parameters** — moving one
changes a spline's parameterisation rather than its placement, no constraint acts on one, and
including them would report degrees of freedom nobody could use up. `SketchParameters.WidthOf` is
also what `Sketch.Freedom` counts with; they were once two tables and drifted.

**`FakeSolver` is a real solver, not a stub** — Levenberg–Marquardt over a numerically differentiated
Jacobian with a dense factorisation — for the same reason `FakeKernel` is. A fake returning its
input unchanged lets every test above it pass without anything being solved.

Fixed points are **held out of the Jacobian** rather than expressed as residuals; a least-squares
step would otherwise trade a little of them away against another constraint. Which parameters are
frozen is decided by `SketchAnalysis`, not by the solver, so that the decomposition and the solve
cannot hold different opinions about which numbers may move.

Two things in `FakeSolver` are documented as *not* observably load-bearing, and say so in their own
comments rather than claiming a virtue no test demonstrates: the signed point-to-line distance, and
the step-acceptance check. Least squares over an absolute distance still converges, and Gauss–Newton
solves everything a unit test writes. Do not "restore" them on the assumption they were forgotten.

Angles must be compared **unwrapped across atan2's branch cut**. Wrapped, a sketch already at its
target takes nine iterations instead of three, which a 16 ms budget cannot afford.

---

## 6. Diagnosis (P4-T06)

Five outcomes: `WellConstrained`, `UnderConstrained(dof, entities)`, `OverConstrained(conflictSet)`,
`Redundant(set)`, `Failed`.

The classification lives in `SolveDiagnostics`, **not in any solver**, because which situation a
sketch is in is a statement about sketches rather than about numerical methods. Two solvers deciding
it separately would give a user two different diagnoses of one drawing and only one could be right.

What a solver hands over is `SolveEvidence` — a residual, a rank, two counts and two lists —
deliberately the *intersection* of what any solver can produce rather than either one's native
output. planegcs reports dependent and conflicting groups directly; a least-squares implementation
gets them from eliminating its own Jacobian.

**The order of the questions is the design:**

1. **Rank first.** A rank short of the equation count means some constraint is implied by the
   others, whether or not the sketch happens to be satisfied. Counting equations against unknowns
   cannot do this — it calls a sketch with two identical constraints fully determined, and says
   nothing about which constraints are at fault.
2. **Then the residual**, which decides *which kind* of implied: a duplicate that agrees is
   `Redundant`, one that disagrees is `OverConstrained`.
3. **The tolerance has a floor**, or a sketch solved to 1e-9 against a 1e-10 ask is reported failed
   for having been solved slightly less hard than it might have been.
4. **A solve stopped by its budget or cancelled says so**, rather than advising the user to move the
   geometry — advice that is only good when the solver actually tried.

Elimination runs **in the order the user made the constraints and without row pivoting**, so the
constraint named as at fault is the later of a dependent pair — the one they just added — rather
than whichever row happened to have the largest entry.

`SolveDiagnosis.IsUsable` counts `UnderConstrained` as usable. A sketch being drawn is
under-constrained almost all the time, and treating that as a failure would report an error against
every sketch in progress.

The whole of `SolveDiagnostics` is testable without solving anything, which matters: the rules are
where the subtlety is, reaching a particular rank and residual through a real solve is slow and
indirect, and it cannot reach the combinations this solver never happens to produce but a different
one will.

---

## 7. Subsystems and dragging (P4-T05, P4-T07)

`SketchAnalysis` is union-find over the entities, joined by every **driving** constraint naming more
than one of them. Reference dimensions join nothing, for the same reason they remove no freedom.

**Fully fixed geometry is ground and is left out of the graph.** This is the decision the whole
decomposition rests on: dimensioning from the origin is how sketches are drawn, and a decomposition
that joined groups through ground would report one subsystem for every sketch anyone ever made,
which is the same as having none. A *partly* fixed entity is not ground — a line with one end pinned
still has an end that moves.

Groups come back largest first, ties broken **on position in the sketch, never on an id**, whose
value is random and orders differently in the next process. ADR-0011 does not allow two runs to
decompose a sketch differently.

Restricting a group brings along the ground its constraints refer to, and whatever fixes that
ground — otherwise the sub-solve gets a free point where the sketch has a pinned one.

Verdicts combine with **the worst outcome winning and the freedom adding up**: one contradicting
group makes a contradicting sketch however well the others solved, and two loose points are four
degrees of freedom, not two.

Four traps here, all of which were live bugs and all of which have tests:

- A constraint acting **only on ground** belongs to no group of movable geometry and was evaluated
  by nobody — a contradictory distance between two fixed points reported as fully defined. Such
  constraints now get a group with no entities: nothing to move, everything still checked.
- A drag naming geometry **not in the sketch** resolved to no group exactly as a fixed point does.
  A stale drag id — what a drag begun before a delete looks like by the time it arrives — skipped
  the solve entirely. Either case now falls back to solving everything.
- A drag reporting **only its own group's verdict** flipped the status to "fully defined" while the
  mouse was down over a sketch whose other feature contradicted itself. An untouched group is now
  judged on its residual alone and escalates to a full rank analysis only when it cannot be
  satisfied — giving up only the re-detection of redundancy in a group nothing has happened to.
  Judging every group properly every frame costs a Jacobian per group per frame, which is the exact
  cost the decomposition exists to avoid.
- The drag seed **checks for ground before moving a point**: a lone fixed point has no equation to
  pull it back, so the drag was relocating the one thing the user had said must not move.

### Drag (P4-T07)

**The minimal-motion objective is a second pass, not a weight.** Two wrong turns are recorded here
because both look reasonable:

- Weighting the objective against the constraints inside one least-squares problem *bends them* —
  a dimension of 4 came out as 4.0036 with the cursor thirty units away. There is no weight that
  both breaks ties and never bends anything, because a constraint is not a preference.
- Weighting the pointer against the rest of the sketch has the same shape of problem one level down:
  a point tied by a coincidence came to rest at exactly `(8·target + 1·origin)/9` — the sketch
  lagging behind the cursor by an amount nobody chose.

So the two wishes are **ordered, not weighed**: follow the pointer, then move as little else as
possible. Each pass solves `(w·J'J + W)δ = W·d` for a large `w`, which is the projection of the wish
onto the directions the constraints do not pin — exact, and incapable of touching a constrained
direction. The first pass carries a **billionth**-scale ridge on everything but the dragged point;
at a millionth it held the pointer back by three microns on a three-unit drag, which a test at a
part in a million caught.

The drag seed puts the point at the pointer and **leaves it free**. An earlier version froze it,
which let a drag violate a dimension outright.

`DragSession` **coalesces**: every pointer position but the newest is stale by the time it could be
worked on, and a queue would make the geometry lag further behind the longer the drag went on. It
counts what it skipped, because a drag dropping most of its frames is worth measuring rather than
discovering from a video. Every frame solves from the sketch as it was at mouse-down, so the drag is
reversible by construction rather than by luck.

---

## 8. Inference and snapping (P4-T08, P4-T09)

**These are two answers to one proximity search and are deliberately apart.** Snapping moves the
cursor; inference proposes a constraint. A user dropping a point on a line wants it *on* the line
whether or not a constraint follows, and a sketcher offering only the constraint would leave the
geometry visibly off by however far the cursor missed.

`ConstraintInference` **proposes and never applies**, in a fixed order. That is what lets the same
code drive the glyphs shown before the click and the constraints added after it, and lets a test
check the guess without a UI. The glyph is a *name*, not a drawing — what a coincidence looks like
is the UI's business and this layer has no idea how big a pixel is. Tolerance is a model distance
the caller works out from pixels, because inference has to feel the same at every zoom.

Most of the work is in not being annoying:

- Nothing already true is offered, compared as an unordered pair — "A parallel to B" and "B parallel
  to A" are the same sentence.
- At most one direction constraint per entity: horizontal, vertical, parallel and perpendicular all
  say where a line points, and two at once is a contradiction.
- A named point beats the curve it belongs to. Someone aiming at the end of a line wants the end.
- `Equal` is the weakest guess and comes last.
- The count is capped. A cloud of glyphs is noise a user learns to ignore.

`SketchSnapping` returns **one** candidate, not a list — a cursor is in one place, and a caller given
several would have to choose, which is this code's job done once rather than every caller's done
differently. `SnapKind` is **ordered by how much a catch means**, and nothing else decides
preference. The grid therefore rounds regardless of distance and always loses to anything real; a
grid that needed the cursor to be near it already would work only sometimes. Guides are offered only
while something is being drawn — a guide from nowhere is a line across the whole sketch catching the
cursor at random.

**`SketchSnapping.Crossings` is public on purpose.** Trimming (P4-T13) and profile detection
(P4-T14) both need it, and three answers to "where do these cross" would eventually be three
different answers. It works on the curves **as drawn**: two segments that would meet if they were
longer do not meet, and a crossing outside an arc's sweep is not one. `SketchExtend` and
`SketchOffset` therefore do *not* reuse it — see section 9.

---

## 9. Editing tools (P4-T13)

Eleven tools, in five files, grouped by what they actually are rather than by what a toolbar calls
them.

| File | Tools | Scope |
|---|---|---|
| `SketchEdit` + `SketchTransform` | move, rotate, scale, copy, mirror, linear pattern, circular pattern, convert to construction | point, line, circle, arc |
| `SketchTrim` | trim | line, circle, arc |
| `SketchExtend` | extend | line only |
| `SketchSplit` | split | line, arc |
| `SketchCorner` | fillet, chamfer | line–line |
| `SketchOffset` | offset | chain of lines and arcs, or one circle |

**Seven of them are one operation.** Move, rotate and scale edit a selection in place; copy, mirror
and the two patterns add a transformed *copy*. That is not a detail — it is the difference between
"drag this" and "place another one of these" — and both go through `SketchGeometryTransform` rather
than one deciding what the other means. `SketchTransform` is scale, optional reflection, rotation,
translation, in that order, with reflection kept as a **flag rather than a negative scale** so
`ScaleAbout` can reject a nonsensical factor instead of quietly also mirroring.

`Duplicate` — what copy, mirror and each pattern instance all call — **keeps a copied selection's
internal constraints and drops the rest**. A constraint duplicates, remapped, only when *every*
entity it names is in the copied set. Duplicating "concentric with that fixed hole" onto every
pattern instance would point them all at the one hole, which is a contradiction the moment there is
more than one instance, not a pattern.

Pattern `count` is the **total including the original**, matching what a dialog asks for, and the
circular one spaces at `totalAngle / count` rather than `/ (count − 1)`, so a full-circle bolt
pattern lands evenly rather than leaving a gap.

### The question every curve-topology tool has to answer

Trim, split, corner and offset all move or destroy geometry that constraints already name. Each
answers **"which constraints follow what"** explicitly, and the answers differ because the
situations do:

- **`SketchTrim` never splits an entity into two**, even though producing the second piece is easy.
  Deciding which of the original's constraints travel with which piece is not, and `WouldSplit`
  reports that honestly rather than guessing. A circle never needs the refusal: removing one arc
  from a closed loop always leaves exactly one connected piece.
- **`SketchSplit` exists precisely because it does not share that problem.** Nothing is deleted, so
  every point of the original provably belongs to exactly one result. `Start` needs no change; `End`
  is remapped onto the new piece; `Self` and `Centre` are facts about the whole original that stay
  true of both halves, so they are kept *and duplicated*; `Middle` — the midpoint of the *original* —
  has no such rule and refuses the whole split with `ConstraintNotTransferable`.
- **`SketchCorner` removes the corner's own `Coincident`, and that is the operation.** After a blend
  the two ends demonstrably are not in the same place. Two coincidences onto the blend's ends
  replace it, plus a tangency per leg for a fillet. Any *other* constraint naming a moving end
  refuses with `ConstraintNotTransferable`. Constraints naming a whole leg are untouched — a
  shortened line is no less horizontal.
- **`SketchOffset` only ever adds**, so the question does not arise at all. Its new pieces get a
  coincidence at each join for the same reason a blend does.

### Why three tools rewrite intersection maths instead of calling `Crossings`

`Crossings` bounds **both** curves to what is drawn (section 8). That is exactly backwards for
extending, where the whole point is that one of the two curves is not there yet, and for offsetting,
where neither piece is finished. `SketchExtend` bounds only the target and expresses everything as a
signed distance along the extending line's own direction, so line, circle and arc candidates compete
for "nearest" on one scale. `SketchOffset` bounds neither. This duplication is deliberate; do not
consolidate it.

### `SketchOffset`, specifically

The distance is **signed — positive is left of travel — and not taken from a click**, unlike every
other tool here. A click says which side of *one* curve was meant, but a chain must be offset to the
same side all the way along or its pieces will not join. Converting a cursor into that sign is one
cross product and belongs to whatever draws the preview. The convention makes a circle's inside its
left, since arcs run anticlockwise, so a positive circle offset is the smaller one.

The chain is **discovered, not asserted** — a wrong selection producing plausible geometry joined in
the wrong sequence is far worse than a refusal — and its **direction is pinned to the entity named
first**, traversed in that entity's own Start-to-End direction. Letting direction fall out of
whichever loose end the walk reached first was a real bug: the same selection, reordered, offset to
the opposite side, silently.

A piece consumed by its own two joins is refused, detected by **how far each end moved** rather than
by what is left — a collapsed arc and one sweeping nearly the whole way round are indistinguishable
after the fact, since a sweep is only ever reported as a positive angle.

### Not attempted, and why

Ellipse, elliptical arc, parabola and hyperbola report `Unsupported` from the transform tools rather
than producing silently wrong geometry — each needs its own angle handling worked out with the same
care an arc's got. `SketchExtend` refuses circles and arcs because nothing here yet has an answer
for which end a click means once a curve bends back over itself. `SketchSplit` refuses circles:
cutting a closed loop at one point gives one open curve, not two. `SketchCorner` refuses arc legs
because the blend centre is then an intersection of *offset* curves existing in up to four places,
and a click alone does not settle which. `SketchOffset` does not detect an offset running into
another part of the same chain, which is what makes offsetting genuinely hard.

---

## 10. Profile detection (P4-T14)

The sketch is treated as a **planar arrangement**: every curve is cut where anything crosses it, the
pieces become graph edges, and the bounded faces are the regions. That is more work than following
chains of coincident endpoints, and it is the only thing that gets the case users actually draw —
two overlapping rectangles, where none of the three regions is a shape anybody drew and all three
are extrudable.

The face walk is the standard rule: arriving at a vertex, leave by the edge turning most sharply
clockwise from the way you came. **Tangents come from the curve, not the chord**, which matters
exactly where two curves meet tangentially — a fillet against the line it was made from, the
commonest join in a real sketch.

**Areas carry their sign.** The sign is what tells an outer boundary from the circuit running round
the outside of everything, and nobody can extrude the outside of a drawing. An arc contributes the
sliver it cuts off its own chord; a *major* arc contributes the rest of the circle instead, and
taking the small piece there does not merely get the area wrong, it makes the region negative and it
disappears.

Segments remember which curve they came from and how much of it, because a profile goes to a kernel
that needs to build a curve rather than a polyline through the same places.

Geometry in no region is **reported**, not ignored — "why is my extrude not offering this" is the
commonest question a sketcher has to answer. Construction geometry is not reported; it was never a
candidate. Splines and conics are named as **untraceable** rather than dropped: they can bound a
region in principle, and cutting them needs an intersector `Crossings` does not have.

One bug worth remembering, found by a failing area rather than by reasoning: containment was asked
about a *corner*, and two regions sharing an edge share its corners, so a point-in-polygon test
about a point on its own boundary answered by rounding. The shared region of two overlapping squares
became a hole of one of them and four units of area vanished. It asks about a point stepped off the
middle of an edge now.

---

## 11. Plane and external references (P4-T10, P4-T11)

Both are **split into a durable half and a resolved half**, and both halves live in
`OpenMCAD.Modeling`:

| Durable — a name | Resolved — one rebuild's answer |
|---|---|
| `SketchPlaneReference` | `SketchPlane` (a plain orthonormal frame, no name) |
| `SketchExternalReference` (`PersistentName` + operation) | `SketchExternalReferenceResolution` (fresh `SketchEntity` geometry) |

Storing the frame instead of the reference would be storing **coordinates rather than intent**
(§5.3): a sketch on a datum plane nobody had moved yet would freeze there the moment it was cached
and stop following the datum the first time someone dragged it. Likewise an external reference is a
live parametric link rather than a one-shot copy — the whole point of "project" over "draw the same
shape by hand" is that it keeps following the edge.

A datum plane and a custom coordinate system are addressed by `(Owner, Name)` against
`Document.References`. A planar face is kernel topology and gets no name of its own, so it goes
through a bare `PersistentName` resolved by the real `NameResolver` (ADR-0005, §5.3) rather than a
parallel lookup invented for this caller. Always **exactly one** face — a sketch plane naming "every
piece of a split face" has no meaning, so unlike `EntityReference` there is no `MultiplicityPolicy`
and an ambiguous split is a refusal, never arbitrated by size.

**Geometry arrives through a caller-supplied delegate**, not from the naming layer's `GeoHint`:
`Func<SubEntity, Plane?>` for a face, `Func<SubEntity, WorldCurve?>` for a curve. The naming layer's
evidence is deliberately in coordinates local to the producing feature, so that moving a part does
not stop its faces being recognised; reusing it here would place a sketch at the wrong point the
first time that feature moved. Nothing in `OpenMCAD.Kernel` exposes either query yet — Phase 5 and
the OCCT decision supply them — and this is the shape a real one will satisfy.

**A circle whose plane is transverse to the sketch's projects to an ellipse**, and a partial one to
an elliptical arc whose eccentric angles are the circle's own parameters shifted by the axis angle —
which is what keeps evaluating a point free of a transcendental solve. Exactly edge-on is refused,
by the angle between the planes rather than by measuring the result.

**`SketchExternalReference.Produces` is assigned once and never afterwards**, so a constraint
attached to projected geometry, or a later feature naming it, keeps pointing at the same entity
while its geometry is replaced every rebuild.

**Every failure is data, never an exception across the resolution boundary.** A rebuild resolves
every reference on every feature in the dirty set, and one corrupt datum throwing would take down
features that have nothing to do with it (§5.4). `SketchPlane.FromNormal`/`FromFrame` still throw on
a genuinely degenerate normal or axis set — they are geometry constructors with a real precondition —
but the resolvers report.

Deliberately incomplete: only straight and circular edges are handled; `Intersect` is straight edges
only,
because a line crosses a plane at zero or one point while a circular edge can cross at two and "one
reference produces one entity" has nowhere to put a second; `Convert` is `Project` plus an in-plane
precondition, refusing with `NotInPlane` rather than silently doing what `Project` would have.

---

## 12. Dimensions (P4-T12)

Most of "aligned", "radial" and "diametric" **already existed** as `Distance`, `Radius` and
`Diameter`, driving or reference, and needed nothing new. Two things were genuinely missing:

- **A distinct linear measurement.** `HorizontalDistance`/`VerticalDistance` measure one axis
  regardless of which way the two points actually lie from each other — a different *equation* from
  `Distance`'s hypotenuse whenever they are not already axis-aligned, not a different display of the
  same number. Both are unsigned, like `Distance`: which point was clicked first is an accident of
  drawing order.
- **Placement.** `SketchDimension` (which constraint, where the witness point is) is durable;
  `SketchDimensionLayout` (witness lines, dimension line, text position) is resolved fresh from the
  current geometry every time — the same durable/resolved split as section 11.

`SketchDimensionLayout` reads its displayed value from the **live geometry**, not from
`SketchConstraint.Value`. That matters for a reference dimension, and a driving one's value and its
geometry agree only once the solver has run since the value last changed. "Display" asks for the
current truth, not the target.

"Editing" needed no new mechanism: a driving dimension's value is `SketchConstraint.Value`, already
changeable through `ConstraintSet.With`, and a value edit is a rebuild like any other.

**Every type §5.6 names is laid out**: aligned, linear, point-to-line, angular, radial, diametric.
Each needed one choice the geometry cannot make, and **the witness point makes all of them** — the
offset for a length, the arc's radius and *which of the four angles* for an angular one, the leader
direction and inside-or-outside for a radial one. A point-to-line distance is not a fourth layout:
once the foot of the perpendicular is found it *is* an aligned dimension between two points, and the
foot comes from the infinite line, or the dimension would depend on how far the line happens to be
drawn. `DimensionLayout` carries two shapes for its line — a segment or, for an angular dimension
alone, an arc — with exactly one set when resolved.

Ordinate dimensioning is *not* a fourth layout either: the number it shows is exactly what
`HorizontalDistance`/`VerticalDistance` already measures from the baseline point, and only the
presentation differs — one shared extension line with dimension lines stacked to avoid colliding
text. That stacking is a layout problem across several dimensions at once, which a single
dimension's signature has nowhere to put, and is the only part left.

---

## 13. Serialization and the corpus (P4-T04, P4-T16)

`SketchFormat` is **JSON, and it is the interchange form, not the file format**. It is what the
corpus is written in and what a bug report can be pasted into; a sketch inside a document will be
MessagePack with everything else at Phase 5 — the same split `omcad build` already has one layer up.

Everything is **named, never positional**. An ordinal changes meaning the moment a kind is inserted,
and a corpus exists to be read years later.

The corpus is `tests/regression/corpus/sketch/`, seven fixtures, run by `SketchCorpusTests` in
`OpenMCAD.Solver.Fake.Tests` — **not** by `OpenMCAD.Regression`, whose fixture schema is kernel
operations and mass properties and has nothing in it for a sketch. Ids are small sequential GUIDs so
`expected.json` can name them by hand and stay legible.

One convergence fixture (a 3-4-5 triangle closed by three coincidences with the third side
undimensioned, solved from a badly perturbed guess); four diagnosis fixtures, one per outcome
producible reliably by hand; one drag-stability fixture, which checks **the dimension holding**
rather than where the free end lands — that is the minimal-motion objective's business, not
something a corpus can predict; one degenerate-input fixture caught by `Sketch.Problems` before
anything is solved.

`Failed` is forced by giving a solvable sketch **zero iterations**, deliberately. A real
non-convergence from a bad initial guess would be at the mercy of how well this particular solver
copes with bad guesses — a property of the implementation, not of the sketch.

---

## 14. Invariants worth not breaking

A checklist for a change in this subsystem. Each of these has cost real debugging at least once.

1. An arc sweeps anticlockwise. Anything that reflects, reverses, or views from the other side must
   swap `Start` and `End`.
2. Constraints name points, and the points are named, not indexed.
3. Equation counts in `ConstraintSchema` are what the solver gets. They drive a readout the user
   trusts.
4. One flattening (`SketchParameters`), one width table, one set of residuals.
5. Which parameters are frozen is `SketchAnalysis`'s answer, not a solver's.
6. Ground is excluded from the decomposition graph — but constraints acting only on ground still get
   evaluated.
7. Ordering is never by GUID. Ids are random and reorder between processes; ADR-0011 forbids it.
8. A resolver reports failure as data. Only geometry constructors with real preconditions throw.
9. Durable references store names; resolved values store coordinates and are never cached across
   rebuilds.
10. An operation that cannot decide where a constraint should go refuses. It does not guess.

---

## 15. Not yet done

| Gap | Why it is open |
|---|---|
| `native/openmcad_gcs`, and `OpenMCAD.Solver.Planegcs` (P4-T01, half of P4-T02) | Blocked on whether to vendor LGPL source into a public repository, and on Boost.Graph as a native dependency. Both in `docs/notes/open-decisions.md`. |
| The 16 ms / 200-entity drag exit criterion | `FakeSolver` is not going to meet it and is not meant to. It needs planegcs. |
| Ordinate dimension stacking across several dimensions at once (P4-T12) | Section 12. |
| External references: conic and spline edges, multi-point `Intersect` (P4-T11) | Section 11. |
| Sketch UI beyond the view model (P4-T15) | `SketchEditorViewModel` exposes the whole of sections 4 and 9 — geometry, constraints, the DOF readout and every editing tool — and is fully tested. The editor itself does not exist: it needs the shell chrome Phase 6 builds. |
| Offset self-intersection against another part of the same chain | Section 9. |
| Naming corpus categories that need sketch topology change (P3-T13) | Closes in Phase 7, not here. |
