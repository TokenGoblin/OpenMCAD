# Sketch corpus

PLAN.md 8.2's `sketch/` category: solver convergence, over- and under-constrained diagnosis, and
drag stability (P4-T16). Run by `SketchCorpusTests` in `OpenMCAD.Solver.Fake.Tests`, not by
`OpenMCAD.Regression` — that runner's fixture schema is kernel operations (box, extrude, fillet,
...) and mass properties, which has nothing in it for a sketch: no kernel, no body, no boolean.

Each fixture is a directory containing two files:

- `sketch.json` — the sketch, in `SketchFormat`'s own interchange form (P4-T04). Entity and
  constraint ids are small, sequential GUIDs (`00000000-0000-0000-0000-00000000000N`) rather than
  random ones, so `expected.json` can refer to them by hand.
- `expected.json` — what solving it (or, for a degenerate fixture, merely validating it) must show.
  See `SketchFixtureExpectation` in `SketchCorpusTests.cs` for every field it can hold; not every
  fixture uses all of them.

**Rule, same as the rest of the corpus:** every bug fix in the solver or the diagnosis ships with a
fixture that reproduces it.
