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

.PARAMETER SkipNative
    Skip the native build. Implied automatically when no C++ toolchain is present.

.PARAMETER SkipTests
    Build only; do not run tests.

.PARAMETER WithOcct
    Link OCCT into the native shim. Requires vcpkg with the manifest in native/vcpkg.json
    restored. Off until P1-T06.

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

    [switch]$SkipNative,
    [switch]$SkipTests,
    [switch]$WithOcct,
    [switch]$Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = $PSScriptRoot
$ArtifactsDir = Join-Path $RepoRoot 'artifacts'
$NativeBuildDir = Join-Path $RepoRoot 'native/build'
$NativeInstallDir = Join-Path $RepoRoot 'native/install'
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
    return $path
}

# --- Clean ---------------------------------------------------------------------------------
if ($Clean) {
    Write-Step 'Cleaning'
    foreach ($dir in @($ArtifactsDir, $NativeBuildDir, $NativeInstallDir)) {
        if (Test-Path $dir) {
            Remove-Item -Recurse -Force $dir
            Write-Host "    removed $dir"
        }
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
    $cmakeArgs = @(
        '-S', (Join-Path $RepoRoot 'native')
        '-B', $NativeBuildDir
        '-DCMAKE_INSTALL_PREFIX=' + $NativeInstallDir
        '-DCMAKE_BUILD_TYPE=' + $Configuration
    )

    if ($WithOcct) {
        $cmakeArgs += '-DOPENMCAD_WITH_OCCT=ON'

        $toolchain = $env:VCPKG_ROOT
        if ([string]::IsNullOrWhiteSpace($toolchain)) {
            throw 'VCPKG_ROOT is not set, but -WithOcct needs vcpkg to supply OCCT. See native/vcpkg.json.'
        }
        $cmakeArgs += '-DCMAKE_TOOLCHAIN_FILE=' + (Join-Path $toolchain 'scripts/buildsystems/vcpkg.cmake')
    }

    Write-Host "    cmake $($cmakeArgs -join ' ')"
    & cmake @cmakeArgs
    if ($LASTEXITCODE -ne 0) { throw "CMake configure failed with exit code $LASTEXITCODE." }

    & cmake --build $NativeBuildDir --config $Configuration --parallel
    if ($LASTEXITCODE -ne 0) { throw "Native build failed with exit code $LASTEXITCODE." }

    & cmake --install $NativeBuildDir --config $Configuration
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

Write-Step 'Done'
$shell = Join-Path $ArtifactsDir "bin/OpenMCAD.Shell/$($Configuration.ToLowerInvariant())/OpenMCAD.exe"
$cli = Join-Path $ArtifactsDir "bin/OpenMCAD.Cli/$($Configuration.ToLowerInvariant())/omcad.exe"
Write-Host "    shell : $shell"
Write-Host "    cli   : $cli"
