#Requires -Version 5.1
<#
.SYNOPSIS
    Builds OpenMCAD: native shims, then managed projects, then tests.

.DESCRIPTION
    P0-T07. The single entry point for building this repository, used identically by a developer
    on a clean machine and by CI. Keeping one script rather than two is what stops "works locally,
    fails in CI" from becoming a weekly event.

    Order matters: the native shims must exist before the managed build copies them, and the
    managed build must succeed before tests can run.

.PARAMETER Configuration
    Debug or Release. Defaults to Debug.

.PARAMETER Generate
    Regenerate the C ABI and its bindings from native/kernel.api.json, then continue. Run this
    after editing the IDL and commit the result.

.PARAMETER SkipNative
    Skip the native build. Implied automatically when no C++ toolchain is present.

.PARAMETER SkipTests
    Build only; do not run tests.

.PARAMETER SkipRegression
    Skip the regression corpus. It runs by default because PLAN.md 14 names it the single most
    important discipline in the project.

.PARAMETER WithOcct
    Link OCCT into the native shim. Requires vcpkg with the manifest in native/vcpkg.json
    restored, located through VCPKG_ROOT or VCPKG_INSTALLATION_ROOT.

.PARAMETER Clean
    Delete build outputs before building.

.EXAMPLE
    ./build.ps1
    Debug build of everything available on this machine, then the full test suite.

.EXAMPLE
    ./build.ps1 -Configuration Release -SkipTests
    Release build, no tests.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$Generate,
    [switch]$SkipNative,
    [switch]$SkipTests,
    [switch]$SkipRegression,
    [switch]$WithOcct,
    [switch]$Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = $PSScriptRoot
$ArtifactsDir = Join-Path $RepoRoot 'artifacts'
$NativeBuildDir = Join-Path $RepoRoot 'native/build'
# Per configuration, not shared. CMake installs into one prefix, so a single directory means a
# Debug build and a Release build overwrite each other's OCCT DLLs and leave both CRT variants
# side by side -- and whichever ran last is what every managed configuration then loads. Mixing
# debug and release runtimes is the kind of defect that produces crashes nobody can reproduce, and
# a Debug shim silently benchmarked as Release is a number worse than no number.
$NativeInstallRoot = Join-Path $RepoRoot 'native/install'
$NativeInstallDir = Join-Path $NativeInstallRoot $Configuration
$TestResultsDir = Join-Path $ArtifactsDir 'test-results'

function Write-Step {
    param([string]$Message)
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Skip {
    param([string]$Message)
    Write-Host "    skipped: $Message" -ForegroundColor DarkYellow
}

function Test-CommandExists {
    param([string]$Name)
    $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}

# --- Find a C++ toolchain -----------------------------------------------------------------
# Absence is not an error. A contributor working only on managed code should not be forced to
# install several gigabytes of C++ tooling to get a running window.
function Find-MsvcToolchain {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path $vswhere)) { return $null }

    $path = & $vswhere -latest -products * `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -property installationPath 2>$null

    if ([string]::IsNullOrWhiteSpace($path)) { return $null }

    $version = & $vswhere -latest -products * `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -property installationVersion 2>$null

    return [pscustomobject]@{
        Path    = $path.Trim()
        Version = $version.Trim()
        Major   = [int]($version.Split('.')[0])
    }
}

<#
.SYNOPSIS
    Maps an installed Visual Studio major version to its CMake generator.

.DESCRIPTION
    Naming the generator is not optional, and getting this wrong is silent. CMake with no
    generator picks the first compiler it finds on PATH, and on a machine that also has MinGW --
    which a WinLibs install puts there -- that is g++, not MSVC. The build then succeeds and
    produces a DLL linked against the MinGW runtime, which cannot link against the MSVC-built OCCT
    that vcpkg produces for the x64-windows triplet. That happened here, and the only reason it
    was caught is that the CMake output names the compiler it chose.
#>
function Get-CMakeGenerator {
    param([int]$Major)

    switch ($Major) {
        18      { 'Visual Studio 18 2026' }
        17      { 'Visual Studio 17 2022' }
        16      { 'Visual Studio 16 2019' }
        default { throw "Unsupported Visual Studio major version $Major. Add its generator to Get-CMakeGenerator." }
    }
}

<#
.SYNOPSIS
    Fails the build unless CMake actually configured itself to use MSVC.

.DESCRIPTION
    A belt to the generator's braces. The failure this guards against does not look like a
    failure, so it has to be asserted rather than assumed.
#>
function Assert-MsvcConfigured {
    param([string]$BuildDirectory)

    # CMakeCXXCompiler.cmake rather than CMakeCache.txt: the Visual Studio generator does not put
    # CMAKE_CXX_COMPILER in the cache at all, so an assertion reading the cache passes vacuously
    # under Ninja and throws under VS. This file is written by both.
    $record = Get-ChildItem -Path $BuildDirectory -Recurse -Filter 'CMakeCXXCompiler.cmake' `
        -ErrorAction SilentlyContinue | Select-Object -First 1

    if (-not $record) {
        throw "CMake wrote no CMakeCXXCompiler.cmake under $BuildDirectory."
    }

    $content = Get-Content $record.FullName -Raw

    if ($content -notmatch 'set\(CMAKE_CXX_COMPILER_ID "([^"]+)"\)') {
        throw 'CMakeCXXCompiler.cmake does not record a compiler id.'
    }

    $id = $Matches[1]
    $path = if ($content -match 'set\(CMAKE_CXX_COMPILER "([^"]+)"\)') { $Matches[1] } else { '(unknown)' }

    if ($id -ne 'MSVC') {
        throw ("CMake configured the '$id' compiler at $path rather than MSVC. " +
               'The native shim must be built with MSVC: vcpkg builds OCCT with it on the ' +
               'x64-windows triplet, and the two runtimes cannot be linked together.')
    }

    Write-Host "    compiler: $id at $path"
}


# --- Clean ---------------------------------------------------------------------------------
if ($Clean) {
    Write-Step 'Cleaning'
    foreach ($dir in @($ArtifactsDir, $NativeBuildDir, $NativeInstallRoot)) {
        if (Test-Path $dir) {
            Remove-Item -Recurse -Force $dir
            Write-Host "    removed $dir"
        }
    }
}

# --- Code generation -------------------------------------------------------------------------
# The generated C ABI is checked in (see native/tools/idlgen). Every build verifies it still
# matches the IDL, because a stale binding is a crash at the boundary rather than a compile error.
Write-Step 'Code generation'

$idlgen = Join-Path $ArtifactsDir "bin/OpenMCAD.IdlGen/$($Configuration.ToLowerInvariant())/idlgen.exe"

& dotnet build (Join-Path $RepoRoot 'native/tools/idlgen/OpenMCAD.IdlGen.csproj') `
    --configuration $Configuration --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw "Building idlgen failed with exit code $LASTEXITCODE." }

if ($Generate) {
    & $idlgen $RepoRoot
    if ($LASTEXITCODE -ne 0) { throw "Code generation failed with exit code $LASTEXITCODE." }
}
else {
    & $idlgen $RepoRoot --check
    if ($LASTEXITCODE -ne 0) {
        throw 'Generated bindings are out of step with native/kernel.api.json. Run ./build.ps1 -Generate.'
    }
}

# --- Native ---------------------------------------------------------------------------------
Write-Step 'Native shims'

$nativeBuilt = $false
if ($SkipNative) {
    Write-Skip '-SkipNative was passed'
}
elseif (-not (Test-CommandExists 'cmake')) {
    Write-Skip 'cmake is not on PATH'
}
elseif ($null -eq (Find-MsvcToolchain)) {
    Write-Skip 'no MSVC C++ toolchain found (install Visual Studio Build Tools with the C++ workload)'
}
else {
    $msvc = Find-MsvcToolchain
    $generator = Get-CMakeGenerator -Major $msvc.Major
    Write-Host "    toolchain: $($msvc.Version) at $($msvc.Path)"
    Write-Host "    generator: $generator"

    $cmakeArgs = @(
        '-S', (Join-Path $RepoRoot 'native')
        '-B', $NativeBuildDir

        # Named explicitly. Without it CMake takes the first compiler on PATH, which on a machine
        # that also has MinGW is g++ -- see Get-CMakeGenerator.
        '-G', $generator
        '-A', 'x64'
        '-DCMAKE_INSTALL_PREFIX=' + $NativeInstallDir
    )

    if ($WithOcct) {
        $cmakeArgs += '-DOPENMCAD_WITH_OCCT=ON'

        # VCPKG_INSTALLATION_ROOT is what the GitHub Windows runners set; VCPKG_ROOT is the
        # convention everywhere else. Accepting both keeps the workflow files free of a variable
        # whose only job is to rename another one.
        $toolchain = $env:VCPKG_ROOT
        if ([string]::IsNullOrWhiteSpace($toolchain)) {
            $toolchain = $env:VCPKG_INSTALLATION_ROOT
        }

        if ([string]::IsNullOrWhiteSpace($toolchain)) {
            throw 'Neither VCPKG_ROOT nor VCPKG_INSTALLATION_ROOT is set, but -WithOcct needs vcpkg to supply OCCT. See native/vcpkg.json.'
        }
        $cmakeArgs += '-DCMAKE_TOOLCHAIN_FILE=' + (Join-Path $toolchain 'scripts/buildsystems/vcpkg.cmake')

        # vcpkg resolves every dependency version from the commit named by builtin-baseline, so a
        # vcpkg clone that does not contain that commit fails with a message about
        # versions/baseline.json that says nothing about the actual cause. A shallow clone is the
        # usual reason -- the GitHub runner images ship one -- and it is worth naming here, because
        # the vcpkg error sends people to look at their manifest instead of their clone.
        $baseline = (Get-Content (Join-Path $RepoRoot 'native/vcpkg.json') -Raw |
            ConvertFrom-Json).'builtin-baseline'

        if ($baseline) {
            & git -C $toolchain cat-file -e "$baseline^{commit}" 2>$null
            if ($LASTEXITCODE -ne 0) {
                throw (
                    "The vcpkg clone at $toolchain does not contain the baseline commit " +
                    "$baseline that native/vcpkg.json pins, so dependency versions cannot be " +
                    "resolved. It is probably a shallow clone. Run: " +
                    "git -C $toolchain fetch --depth 1 origin $baseline")
            }
        }
    }

    # A CMake cache remembers the toolchain file and ignores a later attempt to change it, so
    # switching between a stub build and an OCCT one in the same directory leaves vcpkg unused and
    # fails with "Could not find OpenCASCADE" -- which points at the dependency rather than at the
    # stale cache that actually caused it. Reconfiguring from scratch is the only reliable fix, and
    # it costs a configure rather than a rebuild: the compiled objects are not in the cache.
    $cachePath = Join-Path $NativeBuildDir 'CMakeCache.txt'
    if (Test-Path -LiteralPath $cachePath) {
        $cache = Get-Content -LiteralPath $cachePath -Raw
        $cachedOcct = [bool]($cache -match 'OPENMCAD_WITH_OCCT:BOOL=ON')

        # A toolchain that actually took effect is recorded as FILEPATH. One passed to an
        # already-configured tree is recorded as UNINITIALIZED and ignored -- CMake honours
        # CMAKE_TOOLCHAIN_FILE only on a build tree's first configure. That is the case worth
        # detecting: the cache then claims OCCT is on while vcpkg never ran, and the build fails
        # with "Could not find OpenCASCADE", pointing at the dependency instead of the stale tree.
        $toolchainApplied = [bool]($cache -match 'CMAKE_TOOLCHAIN_FILE:FILEPATH=')

        $reason = $null
        if ($cachedOcct -ne [bool]$WithOcct) {
            $reason = "it was configured $(if ($cachedOcct) { 'with' } else { 'without' }) OCCT"
        }
        elseif ($WithOcct -and -not $toolchainApplied) {
            $reason = 'the vcpkg toolchain was never applied to it'
        }

        if ($reason) {
            Write-Host "    discarding the native build tree: $reason"
            Remove-Item -LiteralPath $NativeBuildDir -Recurse -Force
        }
    }

    Write-Host "    cmake $($cmakeArgs -join ' ')"
    & cmake @cmakeArgs
    if ($LASTEXITCODE -ne 0) { throw "CMake configure failed with exit code $LASTEXITCODE." }

    Assert-MsvcConfigured -BuildDirectory $NativeBuildDir

    & cmake --build $NativeBuildDir --config $Configuration --parallel
    if ($LASTEXITCODE -ne 0) { throw "Native build failed with exit code $LASTEXITCODE." }

    # Cleared first, because CMake installs additively and never removes. Switching between an
    # OCCT build and a stub one otherwise leaves the stub shim sitting beside the previous build's
    # OCCT libraries -- and the managed build then copies that stub into the test output, where
    # every kernel call fails as NOT_IMPLEMENTED and looks like a kernel bug rather than a stale
    # file. The directory must describe exactly one build.
    if (Test-Path -LiteralPath $NativeInstallDir) {
        Remove-Item -LiteralPath $NativeInstallDir -Recurse -Force
    }

    & cmake --install $NativeBuildDir --config $Configuration --prefix $NativeInstallDir
    if ($LASTEXITCODE -ne 0) { throw "Native install failed with exit code $LASTEXITCODE." }

    $nativeBuilt = $true
    Write-Host '    native shims built' -ForegroundColor Green
}

if (-not $nativeBuilt) {
    Write-Host '    the managed build will run without a geometry kernel (expected until P1-T06)' -ForegroundColor DarkGray
}

# --- Managed --------------------------------------------------------------------------------
Write-Step 'Restore'
& dotnet restore (Join-Path $RepoRoot 'OpenMCAD.slnx')
if ($LASTEXITCODE -ne 0) { throw "Restore failed with exit code $LASTEXITCODE." }

Write-Step "Build ($Configuration)"
& dotnet build (Join-Path $RepoRoot 'OpenMCAD.slnx') `
    --configuration $Configuration `
    --no-restore `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "Managed build failed with exit code $LASTEXITCODE." }

# --- Tests ----------------------------------------------------------------------------------
# xunit.v3 runs on Microsoft.Testing.Platform, and each test project builds as its own executable
# test host. Those hosts are invoked directly rather than through `dotnet test`, because the SDK's
# VSTest bridge does not discover the MTP v2 protocol these packages speak. See
# docs/notes/test-runner.md.
Write-Step 'Tests'

if ($SkipTests) {
    Write-Skip '-SkipTests was passed'
}
else {
    New-Item -ItemType Directory -Force -Path $TestResultsDir | Out-Null

    $testProjects = Get-ChildItem -Path (Join-Path $RepoRoot 'tests') -Filter '*.Tests.csproj' -Recurse
    if ($testProjects.Count -eq 0) {
        Write-Skip 'no test projects found'
    }

    $failed = @()
    foreach ($project in $testProjects) {
        $name = [System.IO.Path]::GetFileNameWithoutExtension($project.Name)
        $hostPath = Join-Path $ArtifactsDir "bin/$name/$($Configuration.ToLowerInvariant())/$name.exe"

        if (-not (Test-Path $hostPath)) {
            throw "Test host not found for ${name}: expected $hostPath"
        }

        Write-Host "    running $name"
        & $hostPath --report-trx --results-directory $TestResultsDir
        if ($LASTEXITCODE -ne 0) {
            $failed += "$name (exit $LASTEXITCODE)"
        }
    }

    if ($failed.Count -gt 0) {
        throw "Test failures: $($failed -join ', ')"
    }

    Write-Host '    all tests passed' -ForegroundColor Green
}

# --- Licence notices ----------------------------------------------------------------------------
# PLAN.md 8.6 requires THIRD-PARTY-NOTICES.md to be generated so it cannot drift. Checked here as
# well as in CI, because the failure mode is adding a dependency and not noticing -- which is
# exactly what happens locally, not in CI.
Write-Step 'Licence notices'

if (-not $WithOcct -or -not (Test-Path -LiteralPath (Join-Path $NativeInstallDir 'bin'))) {
    # The committed file describes the shipping configuration, which links OCCT. A build without it
    # installs the stub shim and nothing else, so the comparison would fail on a native section
    # that was never built rather than on anything having drifted. Testing only for the directory
    # was not enough -- the stub build creates one too, which is how CI's shim-only job failed.
    Write-Skip 'not an OCCT build, so the shipping dependency closure is not present'
}
else {
    & (Join-Path $RepoRoot 'tools/generate-notices.ps1') -Check -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw 'THIRD-PARTY-NOTICES.md is out of date. Run tools/generate-notices.ps1 and commit it.'
    }
}

# --- Regression corpus -------------------------------------------------------------------------
# PLAN.md 8.2. Fast enough against FakeKernel to run on every build; the nightly workflow replays
# the same fixtures against OCCT and adds the determinism gate.
Write-Step 'Regression corpus'

if ($SkipTests -or $SkipRegression) {
    Write-Skip 'regression run was skipped'
}
else {
    $regress = Join-Path $ArtifactsDir "bin/OpenMCAD.Regression/$($Configuration.ToLowerInvariant())/omcad-regress.exe"

    if (-not (Test-Path $regress)) {
        throw "Regression runner not found at $regress"
    }

    & $regress --determinism
    if ($LASTEXITCODE -ne 0) {
        throw "Regression corpus failed with exit code $LASTEXITCODE."
    }
}

Write-Step 'Done'
$shell = Join-Path $ArtifactsDir "bin/OpenMCAD.Shell/$($Configuration.ToLowerInvariant())/OpenMCAD.exe"
$cli = Join-Path $ArtifactsDir "bin/OpenMCAD.Cli/$($Configuration.ToLowerInvariant())/omcad.exe"
Write-Host "    shell : $shell"
Write-Host "    cli   : $cli"
