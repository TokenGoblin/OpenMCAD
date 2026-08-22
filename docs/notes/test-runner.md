# Note: why tests are not run through `dotnet test`

**Date:** 2026-08-22 · **Phase:** 0 · **Status:** worked around, worth retrying periodically

## What happens

`xunit.v3` 4.0.0 is a Microsoft.Testing.Platform (MTP) runner. On the .NET 10 SDK, running

```
dotnet test tests/unit/OpenMCAD.Math.Tests/OpenMCAD.Math.Tests.csproj
```

reports:

```
... OpenMCAD.Math.Tests.dll (net10.0) Zero tests ran
Exit code: 5
Test run summary: Zero tests ran
  error: 1
```

while running the produced test host directly works correctly:

```
./artifacts/bin/OpenMCAD.Math.Tests/debug/OpenMCAD.Math.Tests.exe
  total: 93   failed: 0   succeeded: 93
```

## What was tried

1. **VSTest path.** Referencing `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio` fails
   outright: `Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on
   .NET 10 SDK and later`. Both packages are therefore not referenced at all.
2. **`TestingPlatformDotnetTestSupport` / `UseMicrosoftTestingPlatformRunner` MSBuild properties.**
   Set; no effect on the outcome.
3. **`dotnet.config` with `[dotnet.test.runner] name = "Microsoft.Testing.Platform"`.** No effect.
   The .NET 10 opt-in is via `global.json`, not `dotnet.config`.
4. **`global.json` opt-in.** Adding

   ```json
   "test": { "runner": "Microsoft.Testing.Platform" }
   ```

   does change behaviour: `dotnet test` now finds and launches the test *application* rather than
   erroring about VSTest. But it still discovers zero tests, where the same binary run directly
   discovers 93. The most likely cause is a protocol-version mismatch between the SDK's MTP client
   and the `xunit.v3.mtp-v2` adapter these packages ship.
5. **Removing `ArtifactsPath`** to rule out the non-default output layout. No effect.

The `global.json` opt-in is kept: it is correct regardless, and it makes `dotnet test` fail
visibly rather than fail with a confusing VSTest error.

## What is done instead

`build.ps1` and `.github/workflows/ci.yml` locate each `*.Tests.csproj`, resolve its executable
test host under `artifacts/bin/<name>/<config>/<name>.exe`, and run it directly with
`--report-trx --results-directory artifacts/test-results`.

This is a supported MTP invocation, not a hack. It also has two genuine advantages: the process
exit code is the test result with nothing in between to reinterpret it, and TRX output for CI comes
from the same code path a developer runs locally.

## Cost of the workaround

Small but real, and worth naming:

- **IDE test explorers** may not discover tests through the usual mechanism. Running the host
  directly always works.
- **`dotnet test` in an unfamiliar checkout** silently reports success having run nothing, which is
  the worst possible failure mode for a test command. Anyone adding CI steps must use `build.ps1`
  or invoke the hosts directly, never bare `dotnet test`.
- **New test projects** must follow the `*.Tests` naming convention so `Directory.Build.props`
  configures them and `build.ps1` finds them.

## When to retry

On each .NET SDK feature update and each `xunit.v3` minor. The test is thirty seconds:

```powershell
dotnet test tests/unit/OpenMCAD.Math.Tests/OpenMCAD.Math.Tests.csproj
```

If it reports the real test count, delete the direct-invocation loop in `build.ps1` and CI and
simplify to `dotnet test` on the solution.
