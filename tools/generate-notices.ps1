<#
.SYNOPSIS
    Generates THIRD-PARTY-NOTICES.md from what the build actually resolved.

.DESCRIPTION
    PLAN.md 8.6 requires this file to be generated rather than maintained, "so it cannot drift".
    It had drifted: Dirkster.AvalonDock was recorded as MIT when the licence in the package is
    Ms-PL, and the whole native dependency closure that ships beside the shim -- freetype, libpng,
    brotli, bzip2, zlib -- was not listed at all.

    Ground truth, in order of preference:

      native   native/install/<config>/bin  what is actually redistributed
               vcpkg_installed/vcpkg/info/*.list  which package owns each file
               share/<pkg>/vcpkg.spdx.json        its version and concluded licence

      managed  Directory.Packages.props           the versions in force
               <package>.nuspec                   the declared licence

    Nothing here guesses. A package whose nuspec declares a licence *file* rather than an SPDX
    expression cannot be classified automatically, so it must appear in $FileLicences below with a
    value read from that file by a human. An unrecognised one is an error rather than a guess,
    which is the only way this stays honest as dependencies change.

.PARAMETER Check
    Compare against the committed file and fail if they differ, instead of writing it. This is
    what CI runs.

.PARAMETER Configuration
    Which native install closure to read. Defaults to Release.
#>

[CmdletBinding()]
param(
    [switch]$Check,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot = Split-Path -Parent $PSScriptRoot
$NoticesPath = Join-Path $RepoRoot 'THIRD-PARTY-NOTICES.md'

<#
    Packages whose nuspec points at a licence file rather than naming an SPDX expression.

    Each value was read out of the file in the package. They are recorded here rather than parsed
    because licence texts are prose: a parser that recognises "The MIT License (MIT)" will one day
    meet a modified MIT and report it as MIT, which is precisely the error this file exists to
    prevent. A new file-licensed package fails the generator until someone reads its licence.
#>
$FileLicences = @{
    'CommandLineParser'                     = 'MIT'
    'Dirkster.AvalonDock'                   = 'Ms-PL'
    'Dirkster.AvalonDock.Core'              = 'Ms-PL'
    'Fluent.Ribbon'                         = 'MIT'
    'Microsoft.DotNet.PlatformAbstractions' = 'MIT'
}

<#
    Packages whose nuspec declares no licence at all, with where the licence was established and
    the date it was checked.

    Kept apart from $FileLicences on purpose. A package that ships a licence file has at least
    asserted something inside the artefact; one that asserts nothing has to be traced back to its
    source, and whoever revisits this needs to see which of the two situations they are in.
#>
$UndeclaredLicences = @{
    'NetArchTest.Rules' = @{
        Licence = 'MIT'
        Source  = 'github.com/BenMorris/NetArchTest, licence API, checked 2026-08-22'
    }
    'Mono.Cecil'        = @{
        Licence = 'MIT'
        Source  = 'github.com/jbevain/cecil, licence API, checked 2026-08-22'
    }
}

function Get-NuGetRoot {
    if ($env:NUGET_PACKAGES) { return $env:NUGET_PACKAGES }
    return Join-Path $env:USERPROFILE '.nuget/packages'
}

function Get-ManagedPackages {
    <#
        Every NuGet package that ends up in a build output, with the licence its package declares
        and whether it is redistributed or test-only.

        Read from project.assets.json -- the resolved dependency graph -- rather than from the
        PackageReference lists. Those name only direct dependencies, and a transitive package ships
        its DLL just as surely as a direct one does. Reading only the direct references left
        twenty-three packages unlisted, among them ControlzEx, Dirkster.AvalonDock.Core and
        Microsoft.Xaml.Behaviors.Wpf, none of which are ours and all of which are redistributed.
    #>
    $assetsRoot = Join-Path $RepoRoot 'artifacts/obj'
    if (-not (Test-Path -LiteralPath $assetsRoot)) {
        throw "No restore output at $assetsRoot. Run a build first."
    }

    $shipped = @{}
    $tested = @{}

    foreach ($assets in Get-ChildItem -Path $assetsRoot -Filter 'project.assets.json' -Recurse) {
        $document = Get-Content -Raw -LiteralPath $assets.FullName | ConvertFrom-Json
        $projectPath = $document.project.restore.projectPath

        # Where the consuming project lives decides whether its dependencies reach a user.
        $isShipped = $projectPath -like '*\src\*'
        $bucket = if ($isShipped) { $shipped } else { $tested }

        foreach ($target in $document.targets.PSObject.Properties) {
            foreach ($library in $target.Value.PSObject.Properties) {
                if ($library.Value.type -ne 'package') { continue }

                $parts = $library.Name -split '/'
                $bucket[$parts[0]] = $parts[1]
            }
        }
    }

    # Build-time only: PrivateAssets="all" keeps it out of the output entirely. Listed by name
    # because the assets graph records the dependency but not that nothing of it is copied.
    $buildOnly = @('Microsoft.SourceLink.GitHub', 'Microsoft.CodeAnalysis.PublicApiAnalyzers')

    $nuget = Get-NuGetRoot
    $results = @()
    $seen = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)

    foreach ($bucketName in @('ship', 'test')) {
        $bucket = if ($bucketName -eq 'ship') { $shipped } else { $tested }

        foreach ($id in $bucket.Keys) {
            # A package used by both ships, and shipping is the stronger claim.
            if (-not $seen.Add($id)) { continue }

            $number = $bucket[$id]
            $use = if ($buildOnly -contains $id) { 'build' } else { $bucketName }

            $results += [pscustomobject]@{
                Id      = $id
                Version = $number
                Licence = Get-DeclaredLicence -Id $id -Version $number -NuGetRoot $nuget
                Use     = $use
            }
        }
    }

    return $results | Sort-Object Use, Id
}

function Get-DeclaredLicence {
    <#
        The licence a package declares, or an error naming what a human has to establish.
    #>
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][string]$NuGetRoot
    )

    $nuspec = Join-Path $NuGetRoot ("{0}/{1}/{2}.nuspec" -f $Id.ToLowerInvariant(), $Version, $Id.ToLowerInvariant())
    if (-not (Test-Path -LiteralPath $nuspec)) {
        throw "No nuspec for $Id $Version at $nuspec. Run a restore first."
    }

    [xml]$spec = Get-Content -Raw -LiteralPath $nuspec

    # SelectSingleNode rather than dotted access: under Set-StrictMode a missing element throws
    # rather than returning null, and older packages carry only a licenceUrl.
    $node = $spec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]/*[local-name()="license"]')

    if ($node -and $node.type -eq 'expression') {
        return $node.InnerText
    }

    if ($node -and $FileLicences.ContainsKey($Id)) {
        return $FileLicences[$Id]
    }

    if ($node) {
        throw ("$Id $Version declares its licence as a file ('$($node.InnerText)') rather than " +
            'an SPDX expression. Read that file in the package and add the result to ' +
            '$FileLicences in tools/generate-notices.ps1. This is deliberate: guessing from the ' +
            'text is how a modified licence gets recorded as a standard one.')
    }

    if ($UndeclaredLicences.ContainsKey($Id)) {
        return $UndeclaredLicences[$Id].Licence
    }

    throw ("$Id $Version declares no licence in its package at all. Trace it to its source, then " +
        'add it to $UndeclaredLicences in tools/generate-notices.ps1 with where you established ' +
        'it and when.')
}

function Get-NativePackages {
    <#
        The vcpkg packages that own the DLLs actually installed beside the shim.

        Reads what is shipped rather than what the manifest asks for, because the manifest names
        direct dependencies and the closure is what reaches a user's machine.
    #>
    $binDir = Join-Path $RepoRoot "native/install/$Configuration/bin"
    if (-not (Test-Path -LiteralPath $binDir)) {
        return $null
    }

    $installed = Join-Path $RepoRoot 'native/vcpkg_installed'
    $infoDir = Join-Path $installed 'vcpkg/info'
    if (-not (Test-Path -LiteralPath $infoDir)) {
        return $null
    }

    # file name (lower case) -> owning package
    $owners = @{}
    foreach ($list in Get-ChildItem -Path $infoDir -Filter '*.list') {
        $package = ($list.BaseName -split '_')[0]
        foreach ($line in Get-Content -LiteralPath $list.FullName) {
            if ($line -match '/bin/(?<file>[^/]+\.dll)$') {
                $owners[$Matches.file.ToLowerInvariant()] = $package
            }
        }
    }

    $packages = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)

    foreach ($dll in Get-ChildItem -Path $binDir -Filter '*.dll') {
        # openmcad_occt.dll is ours; it is covered by the project's own licence.
        if ($dll.Name -eq 'openmcad_occt.dll') { continue }

        $owner = $owners[$dll.Name.ToLowerInvariant()]
        if (-not $owner) {
            throw ("$($dll.Name) ships in the native closure but no vcpkg package claims it. " +
                'Something is being redistributed whose licence nobody has established.')
        }

        [void]$packages.Add($owner)
    }

    $results = @()
    foreach ($package in $packages) {
        $spdxPath = Join-Path $installed "x64-windows/share/$package/vcpkg.spdx.json"
        if (-not (Test-Path -LiteralPath $spdxPath)) {
            throw "No SPDX document for vcpkg package '$package'."
        }

        $spdx = Get-Content -Raw -LiteralPath $spdxPath | ConvertFrom-Json
        $self = $spdx.packages | Where-Object { $_.name -eq $package } | Select-Object -First 1

        $results += [pscustomobject]@{
            Id      = $package
            Version = $self.versionInfo
            Licence = if ($self.licenseConcluded) { $self.licenseConcluded } else { '(none declared)' }
        }
    }

    return $results | Sort-Object Id
}

function Build-Notices {
    $managed = Get-ManagedPackages
    $native = Get-NativePackages

    $text = [System.Text.StringBuilder]::new()
    [void]$text.AppendLine('# Third-party notices')
    [void]$text.AppendLine()
    [void]$text.AppendLine('OpenMCAD incorporates the components listed below. Each is governed by its own licence,')
    [void]$text.AppendLine("which applies independently of OpenMCAD's own licence (see ``LICENSE`` and ADR-0017).")
    [void]$text.AppendLine()
    [void]$text.AppendLine('<!--')
    [void]$text.AppendLine('    Generated by tools/generate-notices.ps1. Do not edit by hand: build.ps1 and CI run the')
    [void]$text.AppendLine('    generator with -Check and fail if this file does not match what the build resolved.')
    [void]$text.AppendLine('    PLAN.md 8.6 requires exactly that, because a hand-maintained list drifts -- this one did.')
    [void]$text.AppendLine('-->')
    [void]$text.AppendLine()

    [void]$text.AppendLine('## Native components, redistributed')
    [void]$text.AppendLine()

    if ($null -eq $native) {
        [void]$text.AppendLine('_The native closure was not built, so this section could not be generated._')
        [void]$text.AppendLine()
    }
    else {
        [void]$text.AppendLine('Every native library that ships beside the application, resolved from the dependency')
        [void]$text.AppendLine('closure actually installed rather than from the direct dependencies declared in')
        [void]$text.AppendLine('`native/vcpkg.json`. Versions and licences come from vcpkg''s own SPDX documents.')
        [void]$text.AppendLine()
        [void]$text.AppendLine('| Component | Version | Licence |')
        [void]$text.AppendLine('|---|---|---|')

        foreach ($package in $native) {
            [void]$text.AppendLine("| $($package.Id) | $($package.Version) | $($package.Licence) |")
        }

        [void]$text.AppendLine()
        [void]$text.AppendLine('Notes that the table cannot carry:')
        [void]$text.AppendLine()
        [void]$text.AppendLine('- **Open CASCADE** is LGPL-2.1 **with the Open CASCADE Exception**, which vcpkg records')
        [void]$text.AppendLine('  only as `LGPL-2.1-only`. The exception permits linking into applications that are not')
        [void]$text.AppendLine('  themselves LGPL, subject to conditions. OCCT is confined to the separately replaceable')
        [void]$text.AppendLine('  `openmcad_occt.dll` (ADR-0003) so those conditions stay trivially satisfiable: the')
        [void]$text.AppendLine('  library can be replaced without rebuilding the application.')
        [void]$text.AppendLine('- **FreeType** is dual licensed, `FTL OR GPL-2.0-or-later`. OpenMCAD takes it under the')
        [void]$text.AppendLine('  FreeType Licence, which requires acknowledgement in the documentation. That')
        [void]$text.AppendLine('  acknowledgement is this entry.')
        [void]$text.AppendLine('- FreeType and its dependencies (libpng, brotli, bzip2, zlib) arrive transitively through')
        [void]$text.AppendLine('  OCCT''s visualisation modules, which are themselves pulled in by the STEP exchange')
        [void]$text.AppendLine('  module. Nothing in Phase 1 renders text. Trimming the closure would shrink both the')
        [void]$text.AppendLine('  payload and the licence surface, and is worth doing before first release.')
        [void]$text.AppendLine()
    }

    foreach ($section in @(
            @{ Use = 'ship'; Title = 'Managed components, redistributed'; Note = $null },
            @{ Use = 'test'; Title = 'Test-only components'
               Note = 'Not redistributed with the application. Listed because they are part of the build.' },
            @{ Use = 'build'; Title = 'Build-time only'
               Note = 'Consumed during compilation and absent from the output.' })) {

        $rows = $managed | Where-Object { $_.Use -eq $section.Use }
        if (-not $rows) { continue }

        [void]$text.AppendLine("## $($section.Title)")
        [void]$text.AppendLine()

        if ($section.Note) {
            [void]$text.AppendLine($section.Note)
            [void]$text.AppendLine()
        }

        [void]$text.AppendLine('| Package | Version | Licence |')
        [void]$text.AppendLine('|---|---|---|')

        foreach ($row in $rows) {
            [void]$text.AppendLine("| $($row.Id) | $($row.Version) | $($row.Licence) |")
        }

        [void]$text.AppendLine()
    }

    $traced = $managed | Where-Object { $UndeclaredLicences.ContainsKey($_.Id) }
    if ($traced) {
        [void]$text.AppendLine('### Licences established outside the package')
        [void]$text.AppendLine()
        [void]$text.AppendLine('These declare no licence in the package itself, so it was traced to the source.')
        [void]$text.AppendLine()
        [void]$text.AppendLine('| Package | Licence | Established from |')
        [void]$text.AppendLine('|---|---|---|')

        foreach ($row in $traced) {
            $note = $UndeclaredLicences[$row.Id]
            [void]$text.AppendLine("| $($row.Id) | $($note.Licence) | $($note.Source) |")
        }

        [void]$text.AppendLine()
    }

    [void]$text.AppendLine('## Not yet incorporated')
    [void]$text.AppendLine()
    [void]$text.AppendLine('Named here because the plan commits to them and their licences shape the design, not')
    [void]$text.AppendLine('because any code is present.')
    [void]$text.AppendLine()
    [void]$text.AppendLine('| Component | Licence | Where it lands |')
    [void]$text.AppendLine('|---|---|---|')
    [void]$text.AppendLine('| planegcs (from FreeCAD) | LGPL-2.1 | A separately replaceable `openmcad_gcs.dll` (ADR-0006), at P4-T01. |')
    [void]$text.AppendLine('| Eigen | MPL-2.0 | Header-only, used by planegcs. Some optional components are LGPL and must be excluded by build flag; verify at P4-T01. |')
    [void]$text.AppendLine()
    [void]$text.AppendLine('---')
    [void]$text.AppendLine()
    [void]$text.AppendLine('PLAN.md 8.6: **this is engineering guidance, not legal advice.** The licence posture needs')
    [void]$text.AppendLine('a lawyer''s review before first public release, not after.')

    return $text.ToString()
}

$generated = Build-Notices

# Normalise line endings so the comparison is about content rather than about which tool last
# touched the file.
$normalise = { param($s) ($s -replace "`r`n", "`n").TrimEnd() + "`n" }

if ($Check) {
    # Write-Host and an exit code rather than Write-Error: under -File a terminating error prints
    # a stack trace that buries the one sentence the reader needs, and under `&` it propagates as
    # an exception so the caller's own message never runs.
    if (-not (Test-Path -LiteralPath $NoticesPath)) {
        Write-Host 'THIRD-PARTY-NOTICES.md is missing. Run tools/generate-notices.ps1 to create it.' -ForegroundColor Red
        exit 1
    }

    $current = & $normalise (Get-Content -Raw -LiteralPath $NoticesPath)
    if ($current -ne (& $normalise $generated)) {
        Write-Host ('THIRD-PARTY-NOTICES.md does not match the dependencies this build resolved. ' +
            'Run tools/generate-notices.ps1 and commit the result.') -ForegroundColor Red
        exit 1
    }

    Write-Host '  ok     THIRD-PARTY-NOTICES.md matches the resolved dependencies'
    exit 0
}

Set-Content -LiteralPath $NoticesPath -Value (& $normalise $generated) -NoNewline -Encoding utf8
Write-Host "Wrote $NoticesPath"
