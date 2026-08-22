# ADR-0015 — Shell libraries: Fluent.Ribbon and AvalonDock

- **Status:** Accepted
- **Date:** 2026-08-22
- **Supersedes:** none
- **Task:** P0-T10 (which requires that this choice be recorded in an ADR)

## Context

ADR-0007 chose WPF for the shell, and its central argument was the maturity of the WPF docking,
ribbon, and property-grid ecosystem — roughly a year of shell work that can be bought rather than
built. That argument is only cashed in if specific libraries are actually chosen, and the choice
is load-bearing enough to record: a docking library is close to unremovable once layouts are
persisted and every tool window depends on its content model.

Candidates considered, in two groups.

**Open source**

| Library | Role | Licence | Notes |
|---|---|---|---|
| AvalonDock (Dirkster99 fork) | Docking | MIT | The maintained fork; the original is dormant. Ships `net10.0-windows` assets. MVVM-friendly via `AnchorablesSource`/`DocumentsSource`. |
| Fluent.Ribbon | Ribbon | MIT | Office-style ribbon with backstage, contextual tabs, QAT, and a screen-tip model. Ships `net8.0-windows`, which loads cleanly on `net10.0-windows`. |
| Dragablz | Docking | MIT | Tab-tearing focused; no tool-window/anchorable model, so it does not cover the feature tree and property manager. |
| MahApps.Metro | Chrome | MIT | Complementary, not a substitute. Deferred to the theming work at P6-T14. |

**Commercial** (DevExpress, Telerik, Syncfusion, Actipro). All ship stronger docking, ribbon, and
property-grid controls than the open-source options, and PLAN.md 4.4 explicitly calls commercial
"defensible here".

## Decision

Use **AvalonDock (Dirkster99)** for docking and **Fluent.Ribbon** for the ribbon.

## Rationale

The deciding factor is the project name. This is OpenMCAD; a per-seat commercial UI licence in the
shell would make the repository un-buildable by anyone without that licence, which contradicts the
premise more damagingly than any control-quality gap justifies. Both chosen libraries are MIT,
which composes with the LGPL-plus-exception posture of OCCT and planegcs (PLAN.md 8.6) without
adding a third licence regime to reason about.

On the technical merits the gap is real but narrow, and it falls mostly in areas OpenMCAD does not
lean on. The commercial suites' strongest cards are their property grids and data grids; OpenMCAD
generates its property manager from `FeatureSchema` (P3-T21, P6-T04) rather than reflecting over
objects, so a general-purpose property grid is not on the critical path. Docking and ribbon are
the parts actually needed, and both libraries are mature there.

The named risk — that AvalonDock and Fluent.Ribbon are volunteer-maintained and could stall — is
mitigated by the same rule that mitigates WPF's own end of life. Under ADR-0007 no view model may
reference a UI type, so the shell is a replaceable layer. Both libraries are MIT and vendorable if
maintenance ever stops.

## Consequences

- Layout persistence (P6-T02) will use AvalonDock's serialiser, and its `ContentId` values become
  a compatibility surface. Treat a `ContentId` as shipped-and-frozen once released, exactly like a
  file-format field: changing one silently discards a user's saved layout.
- Fluent.Ribbon's theming is separate from any application theme adopted at P6-T14. Verify both
  light and dark against it before committing to a theming approach.
- AvalonDock applies `LayoutItemContainerStyle` to documents and anchorables alike, so container
  styles must be supplied through a `StyleSelector`. `OpenMCAD.Shell.LayoutItemStyleSelector`
  exists for this; a single style targeting one item kind throws at load time.
- Revisit at P6-T02 with the real docking layout built. If the open-source docking model turns out
  to block a required interaction, the cost of switching is bounded to `OpenMCAD.Shell`, and that
  boundedness is the property worth protecting.
