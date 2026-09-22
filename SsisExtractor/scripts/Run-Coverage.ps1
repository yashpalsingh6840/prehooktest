<#
.SYNOPSIS
  Runs every generated `{Package}.Tests` project's non-Integration suite with code-coverage
  collection, and writes one combined coverage-report.md next to the generated solution.

.DESCRIPTION
  Every generated `{Package}.Tests.csproj` already references `coverlet.collector` (wired in by
  `ssisx generate` itself, `TestProjectEmitter.cs`/`GenerateCommand.BuildDirectoryPackagesProps`),
  so `dotnet test --collect:"XPlat Code Coverage"` works out of the box with no manual patching.
  This script just runs it per package, parses each `coverage.cobertura.xml` (plain XML, no
  external tool needed), and reports line/branch coverage split into "this package's own
  generated code" vs. "the shared Etl.Core library" -- the blended total is dragged down by
  Etl.Core's own infrastructure (SQL bulk-copy paths, Excel readers, ...) that a starter test
  suite was never meant to fully exercise, so the split is the more honest number.

  Only ever ADDS files under each project's own `TestResults\` folder -- never deletes anything.
  If a project has been tested before, `TestResults\` may already hold older runs; this script
  picks the MOST RECENTLY WRITTEN `coverage.cobertura.xml` per project rather than clearing
  anything out first, so re-running this is always safe and never destructive.

.PARAMETER GeneratedRoot
  The `generate` folder produced by `ssisx generate --out <dir>` (i.e. `<out>\generate`,
  the folder containing `Generated.slnx`).

.PARAMETER Package
  Optional: only these packages' own `.Tests` projects (repeatable). Default: every
  `*.Tests\*.Tests.csproj` found directly under -GeneratedRoot.

.PARAMETER Configuration
  Build configuration to test. Default: Release.

.EXAMPLE
  .\Run-Coverage.ps1 -GeneratedRoot D:\tmp\sandeep-e2e\gen\generate

.EXAMPLE
  .\Run-Coverage.ps1 -GeneratedRoot D:\tmp\sandeep-e2e\gen\generate -Package Package_Advanced,Package_Legacy

.NOTES
  Exit codes: 0 = every test project passed; 1 = at least one test project failed; 2 = no
  `{Package}.Tests.csproj` exists at all (or none matched -Package) -- expected, not an error,
  whenever 0 packages fully generated a wired Program.cs (every flow in the portfolio was blocked
  on a Tier-3/unfilled-seam gap). This is a plain exit code, never a terminating error -- a caller
  invoking this via `& Run-Coverage.ps1 ...` from another script must not have that script's own
  execution aborted just because there was nothing to cover.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $GeneratedRoot,

    [string[]] $Package,

    [string] $Configuration = 'Release'
)

# Deliberately NOT 'Stop': a native command's stderr (e.g. dotnet test's own [FAIL] lines,
# which are expected, handled output here, not a script error) would otherwise be upgraded
# into a terminating exception. Fatal checks below use Write-Error -ErrorAction Stop explicitly.
$ErrorActionPreference = 'Continue'

if (-not (Test-Path $GeneratedRoot)) { Write-Error "GeneratedRoot not found: $GeneratedRoot" -ErrorAction Stop }
$GeneratedRoot = (Resolve-Path $GeneratedRoot).Path

$testProjects = Get-ChildItem -Path $GeneratedRoot -Filter '*.Tests.csproj' -Recurse -Depth 1 |
    Where-Object { $_.Directory.Parent.FullName -eq $GeneratedRoot }

if ($Package) {
    # Normalize by splitting every element on ',' too, not just joining -- when this script (or
    # Run-Pipeline.ps1, which forwards -Package straight through) is invoked via `powershell -File`
    # with a comma-joined value (e.g. `-Package Package_Advanced,Package_Transforms`), PowerShell's
    # own argument parser does NOT reliably split that into a multi-element array for a [string[]]
    # parameter the way it would from an interactive prompt -- it can bind the whole comma-joined
    # text as ONE array element instead. An exact `-contains` match against that then silently
    # matches nothing. Splitting defensively here handles both shapes correctly either way.
    $requestedPackages = $Package | ForEach-Object { $_ -split ',' } | Where-Object { $_ }
    $testProjects = $testProjects | Where-Object {
        $pkgName = $_.BaseName -replace '\.Tests$', ''
        $requestedPackages -contains $pkgName
    }
}

if (-not $testProjects) {
    # Deliberately NOT -ErrorAction Stop, and deliberately not treated as a failure (exit 1) --
    # this happens whenever 0 packages in the portfolio fully generated a wired Program.cs (every
    # flow blocked on a Tier-3 gap or an unfilled seam), which is a normal, expected outcome for a
    # real-world portfolio, not a script error. A caller invoking this script via `& ...` (e.g.
    # Run-Pipeline.ps1) must be able to read this exit code and continue -- a terminating error
    # here would otherwise unwind out of that `&` call and kill the CALLER's own script too,
    # before it ever reaches its own final summary. Still writes a coverage-report.md (below,
    # with an empty $rows) so a reader has one consistent file to check regardless of outcome.
    Write-Warning "No {Package}.Tests.csproj found directly under $GeneratedRoot (or none matched -Package) -- nothing to test or cover. Expected when 0 packages fully generated; see generate-report.md for why."
    $reportPath = Join-Path $GeneratedRoot 'coverage-report.md'
    Set-Content -Path $reportPath -Value (@(
        '# Code coverage report',
        '',
        "No ``{Package}.Tests.csproj`` was found under ``$GeneratedRoot``$(if ($Package) { " (or none matched -Package $($Package -join ','))" }) -- nothing to test or cover.",
        '',
        'This means 0 packages in this portfolio fully generated a wired `Program.cs` (every flow was',
        'blocked on a Tier-3 gap or an unfilled Tier-1/2 seam). See `generate-report.md` in this same',
        'folder for why. This is not a script failure.'
    ) -join "`r`n") -Encoding utf8
    Write-Host "Coverage report written: $reportPath" -ForegroundColor Green
    exit 2
}

$rows = @()
foreach ($proj in $testProjects) {
    $pkgName = $proj.BaseName -replace '\.Tests$', ''
    Write-Host ""
    Write-Host "=== $pkgName ===" -ForegroundColor Cyan

    & dotnet test $proj.FullName -c $Configuration --filter 'Category!=Integration' --collect:"XPlat Code Coverage" |
        ForEach-Object { Write-Host $_ }
    $testExit = $LASTEXITCODE

    $resultsDir = Join-Path $proj.Directory.FullName 'TestResults'
    $coverageFile = $null
    if (Test-Path $resultsDir) {
        $coverageFile = Get-ChildItem -Path $resultsDir -Filter 'coverage.cobertura.xml' -Recurse |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
    }

    if (-not $coverageFile) {
        Write-Warning "$pkgName -- no coverage.cobertura.xml produced (test run may have failed before collection)."
        $rows += [pscustomobject]@{ Package = $pkgName; TestExit = $testExit; LinePct = $null; BranchPct = $null; OwnPct = $null; CorePct = $null }
        continue
    }

    [xml]$cov = Get-Content $coverageFile.FullName -Raw
    $lineRate = [double]$cov.coverage.'line-rate'
    $branchRate = [double]$cov.coverage.'branch-rate'

    $ownCovered = 0; $ownValid = 0; $coreCovered = 0; $coreValid = 0
    foreach ($class in $cov.SelectNodes('//class')) {
        $isCore = $class.filename -match 'Etl\.Core'
        $lines = $class.SelectNodes('lines/line')
        $valid = $lines.Count
        $covered = ($lines | Where-Object { [int]$_.hits -gt 0 }).Count
        if ($isCore) { $coreCovered += $covered; $coreValid += $valid }
        else { $ownCovered += $covered; $ownValid += $valid }
    }
    $ownPct = if ($ownValid -gt 0) { [math]::Round(100.0 * $ownCovered / $ownValid, 1) } else { $null }
    $corePct = if ($coreValid -gt 0) { [math]::Round(100.0 * $coreCovered / $coreValid, 1) } else { $null }

    $rows += [pscustomobject]@{
        Package   = $pkgName
        TestExit  = $testExit
        LinePct   = [math]::Round($lineRate * 100, 1)
        BranchPct = [math]::Round($branchRate * 100, 1)
        OwnPct    = $ownPct
        CorePct   = $corePct
    }
}

Write-Host ""
Write-Host "=== Coverage summary (Category!=Integration) ===" -ForegroundColor Cyan
$rows | Format-Table Package, @{L='Test'; E={ if ($_.TestExit -eq 0) {'PASS'} else {'FAIL'} }}, @{L='Line %'; E={$_.LinePct}}, @{L='Branch %'; E={$_.BranchPct}}, @{L='Own code %'; E={$_.OwnPct}}, @{L='Etl.Core %'; E={$_.CorePct}} -AutoSize

$reportPath = Join-Path $GeneratedRoot 'coverage-report.md'
$lines = @(
    '# Code coverage report',
    '',
    "Generated by `Run-Coverage.ps1` against ``$GeneratedRoot``, `dotnet test --filter Category!=Integration --collect:""XPlat Code Coverage""` per package.",
    '',
    '| Package | Test result | Line % | Branch % | Own generated code | Shared Etl.Core |',
    '|---|---|---|---|---|---|'
)
foreach ($r in $rows) {
    $testResult = if ($r.TestExit -eq 0) { 'PASS' } else { "FAIL (exit $($r.TestExit))" }
    $lines += "| $($r.Package) | $testResult | $($r.LinePct)% | $($r.BranchPct)% | $($r.OwnPct)% | $($r.CorePct)% |"
}
$lines += ''
$lines += '"Own generated code" is the more honest figure for a starter-test baseline -- the blended'
$lines += '"Line %" column is dragged down by Etl.Core, a large shared library whose own Integration-only'
$lines += 'code paths (SQL bulk-copy, Excel/fixed-width readers, ...) a starter suite was never meant to'
$lines += 'fully exercise. This report never deletes any prior `TestResults\` output -- each run adds a'
$lines += 'fresh, GUID-named result folder alongside whatever was already there.'
Set-Content -Path $reportPath -Value ($lines -join "`r`n") -Encoding utf8

Write-Host ""
Write-Host "Coverage report written: $reportPath" -ForegroundColor Green

$anyTestFailed = ($rows | Where-Object { $_.TestExit -ne 0 }).Count -gt 0
if ($anyTestFailed) { exit 1 }
exit 0
