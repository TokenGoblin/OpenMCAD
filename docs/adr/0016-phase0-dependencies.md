# ADR-0016 — The Phase 0 dependency set

- **Status:** Accepted
- **Date:** 2026-08-22
- **Supersedes:** none
- **Task:** P0-T03

## Context

PLAN.md 12 requires an ADR for every NuGet dependency, on the grounds that dependency creep in a
decade-long project is a real cost. This ADR covers the whole set introduced in Phase 0 so that
later phases add one entry each rather than re-litigating the baseline.

All versions are exact-pinned in `Directory.Packages.props`. Floating versions are banned: a
product that must reproduce a build from three years ago cannot have a restore that resolves
differently each time.

## Decision

| Package | Version | Why | Licence |
|---|---|---|---|
| `Microsoft.Extensions.DependencyInjection` | 10.0.11 | Named in PLAN.md 4.4. Composition root for shell and CLI alike. | MIT |
| `Microsoft.Extensions.Hosting` | 10.0.11 | Lifetime and configuration plumbing for the shell. | MIT |
| `Microsoft.Extensions.Logging(.Abstractions)` | 10.0.11 | The logging abstraction everything above the sink codes against. | MIT |
| `Microsoft.Extensions.Options` | 10.0.11 | Settings infrastructure, needed from P6-T10. | MIT |
| `Serilog` + `Extensions.Logging` + `Sinks.Console` + `Sinks.File` | 4.4.0 / 10.0.0 / 6.1.1 / 7.0.0 | Named in PLAN.md 4.4. Structured logging is required by PLAN.md 6.1; rebuild traces are structured events, not prose. | Apache-2.0 |
| `System.CommandLine` | 2.0.11 | The headless runner (P0-T11) is the entry point every later test harness uses; hand-rolled parsing there would be re-implemented badly. | MIT |
| `Fluent.Ribbon` | 11.0.2 | See ADR-0015. | MIT |
| `Dirkster.AvalonDock` | 5.0.0 | See ADR-0015. | MIT |
| `Microsoft.SourceLink.GitHub` | 10.0.400 | Debuggable release builds. A crash report from a user is worth far more with sources resolvable. | MIT |
| `xunit.v3` | 4.0.0 | Named in PLAN.md 4.4. See the note on the test platform below. | Apache-2.0 |
| `Microsoft.Testing.Extensions.TrxReport` | 2.3.3 | Machine-readable test results for CI. Not optional once nightly regression runs exist. | MIT |
| `FluentAssertions` | **7.2.2** | Named in PLAN.md 4.4. See the version note below. | Apache-2.0 |
| `NetArchTest.Rules` | 1.3.2 | Named in PLAN.md 4.4 and required by P0-T05 for the type-level rules. | MIT |

Deliberately **not** taken in Phase 0:

- **Vortice.Windows** — named in PLAN.md 4.4 but not needed until P2-T01. Adding it now would mean
  pinning a version against a renderer that does not exist.
- **MessagePack-CSharp** — named in PLAN.md 4.4, needed at P3-T18. Same reasoning. The file format
  is the most consequential pin in the project and deserves to be chosen alongside the schema.
- **An MVVM toolkit** (CommunityToolkit.Mvvm or similar) — `OpenMCAD.ViewModels.ObservableObject`
  is forty lines. Revisit at P6-T04, when the schema-driven property manager makes the real
  requirements visible.

## Two decisions worth spelling out

### FluentAssertions is pinned to 7.2.2, not the latest

FluentAssertions 8.0 moved to a paid Xceed licence for commercial use. 7.2.2 is the last Apache-2.0
release. Pinning it is deliberate, and the pin must not be "helpfully" bumped by a dependency-update
bot; anyone raising the version needs to answer the licensing question first.

The alternatives, if the 7.x line ever becomes untenable: Shouldly (BSD), or plain xUnit assertions.
Neither is urgent. 7.2.2 is feature-complete for what the test suite does, and an assertion library
does not need to keep changing.

### Tests run on Microsoft.Testing.Platform, invoked directly

`xunit.v3` is an MTP runner, not a VSTest one, and the .NET 10 SDK has removed VSTest support for
MTP-based projects. Consequently:

- `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio` are **not** referenced. They are the
  VSTest path and would only produce a build error.
- Each test project builds as its own executable test host, and `build.ps1` and CI invoke those
  hosts directly rather than going through `dotnet test`. The SDK's bridge does not currently
  discover the MTP protocol version these packages speak; running the host is the supported path
  and it also yields honest exit codes. Details and the retest procedure are in
  `docs/notes/test-runner.md`.

## Consequences

- Adding a package means adding a row above and a pin in `Directory.Packages.props`. The
  architecture test `NoProjectPinsItsOwnPackageVersions` enforces that versions cannot be set
  per-project, so central management cannot be quietly bypassed.
- `THIRD-PARTY-NOTICES.md` must stay in step. PLAN.md 8.6 requires it to be CI-generated so it
  cannot drift; wiring that generation is a Phase 1 follow-up.
