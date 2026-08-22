# Third-party notices

OpenMCAD incorporates the components listed below. Each is governed by its own licence, which
applies independently of OpenMCAD's own licence (see `LICENSE` and ADR-0017).

> **This file is maintained by hand today, which means it will drift.** PLAN.md 8.6 requires it to
> be generated in CI from the vcpkg manifest and the NuGet lock file precisely so that it cannot.
> Wiring that generation is a Phase 1 follow-up; until then, anyone adding a dependency must add a
> row here in the same commit.

## Native components

| Component | Version | Licence | Notes |
|---|---|---|---|
| Open CASCADE Technology (OCCT) | pinned in `native/vcpkg.json` | LGPL-2.1 with the Open CASCADE Exception | The exception permits linking into applications that are not themselves LGPL, subject to conditions. OCCT is confined to the separately replaceable `openmcad_occt.dll` (ADR-0003) so those conditions stay trivially satisfiable. Not yet linked; see P1-T06. |
| planegcs (from FreeCAD) | not yet vendored | LGPL-2.1 | Same treatment, in `openmcad_gcs.dll` (ADR-0006). Lands at P4-T01. |
| Eigen | pinned in `native/vcpkg.json` | MPL-2.0 | Some optional components are LGPL-licensed and must be excluded via build flags. Verify at P4-T01. |

## Managed components

| Package | Version | Licence |
|---|---|---|
| Microsoft.Extensions.* (DependencyInjection, Hosting, Logging, Options) | 10.0.11 | MIT |
| Serilog | 4.4.0 | Apache-2.0 |
| Serilog.Extensions.Logging | 10.0.0 | Apache-2.0 |
| Serilog.Sinks.Console | 6.1.1 | Apache-2.0 |
| Serilog.Sinks.File | 7.0.0 | Apache-2.0 |
| System.CommandLine | 2.0.11 | MIT |
| Fluent.Ribbon | 11.0.2 | MIT |
| Dirkster.AvalonDock | 5.0.0 | MIT |
| Microsoft.SourceLink.GitHub | 10.0.400 | MIT |

## Test-only components

Not redistributed with the application, but listed for completeness.

| Package | Version | Licence |
|---|---|---|
| xunit.v3 | 4.0.0 | Apache-2.0 |
| Microsoft.Testing.Extensions.TrxReport | 2.3.3 | MIT |
| FluentAssertions | 7.2.2 | Apache-2.0 — **pinned deliberately**; 8.x moved to a paid Xceed licence. See ADR-0016. |
| NetArchTest.Rules | 1.3.2 | MIT |
