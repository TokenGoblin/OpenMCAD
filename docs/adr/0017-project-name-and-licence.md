# ADR-0017 — Project name, and the outstanding licence decision

- **Status:** Partially accepted. **The licence is NOT decided and blocks nothing in Phase 0, but
  must be settled before the first public commit.**
- **Date:** 2026-08-22
- **Supersedes:** none
- **Task:** P0-T01

## The name — decided

The codename in PLAN.md was *Anvil*. The chosen name is **OpenMCAD**, applied in Phase 0 as the
plan instructs ("do it now, in Phase 0, not later").

Consequences, all applied:

| Thing | Value |
|---|---|
| Root namespace | `OpenMCAD.*` |
| Solution | `OpenMCAD.slnx` |
| Part / assembly / drawing files | `.ompart` / `.omasm` / `.omdrw` |
| Native shims | `openmcad_occt.dll`, `openmcad_gcs.dll` |
| Native C ABI prefix | `openmcad_` |
| Shell executable | `OpenMCAD.exe` |
| CLI executable | `omcad.exe` |
| Per-user data | `%LOCALAPPDATA%\OpenMCAD\` |

The CLI is **`omcad`**, not `openmcad`. Windows paths are case-insensitive, so an `openmcad.exe`
next to `OpenMCAD.exe` collides the moment an installer places both in one directory (P17-T01).
Discovering that at packaging time would have meant renaming a command that scripts and CI already
depended on. `omcad` is also shorter to type, which matters for a command invoked constantly by
the regression harness.

Two checks that should happen before any public announcement, neither of which blocks development:
a trademark search, and confirming the name is not already in use by another CAD project.

## The licence — NOT decided

No `LICENSE` file has been committed with a real licence, and this is deliberate rather than an
oversight. Choosing a licence is a consequential, effectively irreversible act, and the plan pulls
in two directions:

- The **name** says open source.
- **PLAN.md 8.6** discusses OCCT's exception as permitting "linking into proprietary applications",
  and **P17-T03** schedules licensing and activation, node-locked and floating, with trial mode and
  offline activation. That is proprietary-product work.

These are reconcilable — open-core, or source-available with paid support — but they are different
products with different consequences, and the decision is the owner's, not the implementing
agent's.

### Options, with the constraint each one imposes

| Option | Consequence for the OCCT/planegcs dependency | Consequence for the plan |
|---|---|---|
| **GPL-3.0** | Simplest compliance posture. | Kills P17-T03 as written; no proprietary distribution. |
| **LGPL-2.1** | Matches OCCT and planegcs exactly. Fewest surprises. | Plugins (ADR-0012) must be reasoned about carefully as derived works. |
| **MPL-2.0** | Compatible; file-level copyleft. | Permits a proprietary shell over an open core. A common fit for open-core. |
| **Apache-2.0 / MIT** | Requires care: OCCT's exception permits linking, and keeping `openmcad_occt.dll` separately replaceable (already the design) satisfies its conditions, but the analysis must be done, not assumed. | Maximally permissive; makes P17-T03 straightforward. |
| **Source-available** (BSL, PolyForm) | Same OCCT analysis as permissive. | Fits P17-T03 best; is not open source, so the name would mislead. |

### What is already true regardless of the choice

The architecture does not foreclose any of these. ADR-0003 keeps OCCT behind a separately
replaceable dynamic library, which is exactly the condition the Open CASCADE Exception cares about,
and ADR-0006 does the same for planegcs. That was chosen for engineering reasons and happens to
keep every licensing door open.

### Required before first public release

PLAN.md 8.6 says it plainly, and it is worth repeating here: **get a lawyer to review the licence
posture before first public release, not after.** This ADR is engineering context to hand to that
review, not legal advice.

## Action

Replace `LICENSE` with the chosen licence text and update this ADR's status. Until then `LICENSE`
states that the licence is undetermined, which is honest and prevents anyone from assuming a
default.
