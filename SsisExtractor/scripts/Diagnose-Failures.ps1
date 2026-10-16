<#
.SYNOPSIS
  Runs each failing {Package}.Tests project with full build+test output captured, and writes one
  consolidated failures-summary.md naming exactly what went wrong per package -- so every real
  error can be read and fixed in one pass instead of re-running `dotnet test` by hand per package.

.DESCRIPTION
  A package can fail in `Run-Coverage.ps1`'s own table two different ways, and they need different
  fixes:
    1. Blank Line%/Branch% ("FAIL" with no numbers) -- the BUILD failed before any test ever ran
       (most commonly CS8795 from an unfilled Tier-2 seam, but could be any compiler error). The
       real error is in the compiler output, never in a .trx file (none gets produced).
    2. Real percentages shown but still "FAIL" -- the build succeeded and at least one test
       assertion genuinely failed at runtime. The real error is in the test's own Assert
       message/stack trace, captured in the .trx this script also parses.
  This script captures BOTH: the full raw `dotnet test` console transcript (always, so a build
  error is never lost) AND, when the build succeeded, each failing test's own Message/StackTrace
  parsed out of the project's own .trx result file.

  Only ever ADDS files under -OutDir and each project's own TestResults\ folder -- never deletes
  anything, matching Run-Coverage.ps1's own convention.

.PARAMETER GeneratedRoot
  The `generate` folder produced by `ssisx generate --out <dir>` (i.e. `<out>\generate`, the
  folder containing `Generated.slnx`).

.PARAMETER Package
  Optional: only diagnose these packages (repeatable, comma-splittable). Default: every package
  whose `Run-Coverage.ps1` row would read FAIL -- i.e. every {Package}.Tests project is run, and
  only the ones that actually fail get a section in the summary. Pass this to narrow a re-run to
  just the packages you're actively fixing, so you don't re-run everything else each time.

.PARAMETER Configuration
  Build configuration to test. Default: Release.

.PARAMETER OutDir
  Where to write the per-package .log files and failures-summary.md. Default:
  `<GeneratedRoot>\diagnostics`.

.EXAMPLE
  .\Diagnose-Failures.ps1 -GeneratedRoot D:\PoC\Tools\out\generate

.EXAMPLE
  # Narrow to just the packages still failing after a round of fixes.
  .\Diagnose-Failures.ps1 -GeneratedRoot D:\PoC\Tools\out\generate -Package CUVImport,RORPackage

.NOTES
  Exit codes: 0 = every tested package passed; 1 = at least one failed (summary written either
  way). Never a terminating error on a per-package failure -- that's the expected, normal case
  this script exists to report on.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $GeneratedRoot,

    [string[]] $Package,

    [string] $Configuration = 'Release',

    [string] $OutDir
)

$ErrorActionPreference = 'Continue'

if (-not (Test-Path $GeneratedRoot)) { Write-Error "GeneratedRoot not found: $GeneratedRoot" -ErrorAction Stop }
$GeneratedRoot = (Resolve-Path $GeneratedRoot).Path

$slnxPath = Join-Path $GeneratedRoot 'Generated.slnx'
if (-not (Test-Path $slnxPath)) { Write-Error "Generated.slnx not found under $GeneratedRoot -- run 'ssisx generate' first." -ErrorAction Stop }

if (-not $OutDir) { $OutDir = Join-Path $GeneratedRoot 'diagnostics' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

[xml]$slnx = Get-Content $slnxPath -Raw
$testProjects = $slnx.SelectNodes('//Project') |
    Where-Object { $_.Path -like '*.Tests/*.Tests.csproj' } |
    ForEach-Object { Get-Item (Join-Path $GeneratedRoot ($_.Path -replace '/', '\')) }

if ($Package) {
    # Same defensive comma-split as Run-Coverage.ps1 -- a comma-joined -Package value passed via
    # `powershell -File` does not reliably bind as a multi-element array otherwise.
    $requestedPackages = $Package | ForEach-Object { $_ -split ',' } | Where-Object { $_ }
    $testProjects = $testProjects | Where-Object {
        $pkgName = $_.BaseName -replace '\.Tests$', ''
        $requestedPackages -contains $pkgName
    }
}

if (-not $testProjects) {
    Write-Warning "No {Package}.Tests.csproj found under $GeneratedRoot (or none matched -Package)."
    exit 2
}

function Get-FailureFromTrx {
    param([string] $TrxPath)
    # Returns an array of @{ TestName; Message; StackTrace } for every non-Passed result.
    $failures = @()
    if (-not (Test-Path $TrxPath)) { return $failures }
    [xml]$trx = Get-Content $TrxPath -Raw
    $ns = New-Object System.Xml.XmlNamespaceManager($trx.NameTable)
    $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    foreach ($result in $trx.SelectNodes('//t:UnitTestResult', $ns)) {
        if ($result.outcome -eq 'Passed') { continue }
        $msgNode = $result.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $ns)
        $stackNode = $result.SelectSingleNode('t:Output/t:ErrorInfo/t:StackTrace', $ns)
        $failures += [pscustomobject]@{
            TestName   = $result.testName
            Outcome    = $result.outcome
            Message    = if ($msgNode) { $msgNode.InnerText.Trim() } else { '(no message captured)' }
            StackTrace = if ($stackNode) { $stackNode.InnerText.Trim() } else { $null }
        }
    }
    return $failures
}

function Get-CompilerErrors {
    param([string[]] $ConsoleLines)
    # A build-time compiler error line looks like: "...\Foo.cs(12,34): error CS1002: ..."
    return $ConsoleLines | Where-Object { $_ -match ':\s*error\s+(CS|MSB|NU)\d+:' }
}

$summarySections = @()
$anyFailed = $false

foreach ($proj in $testProjects) {
    $pkgName = $proj.BaseName -replace '\.Tests$', ''
    Write-Host ""
    Write-Host "=== $pkgName ===" -ForegroundColor Cyan

    $runStartUtc = [DateTime]::UtcNow
    $logPath = Join-Path $OutDir "$pkgName.log"
    $trxName = "$pkgName-$(Get-Date -Format 'yyyyMMdd-HHmmss').trx"

    $consoleLines = & dotnet test $proj.FullName -c $Configuration -nodeReuse:false `
        --filter 'Category!=Integration' `
        --logger "trx;LogFileName=$trxName" `
        --logger "console;verbosity=normal" 2>&1
    $testExit = $LASTEXITCODE
    $consoleLines | Out-File -FilePath $logPath -Encoding utf8
    $consoleLines | ForEach-Object { Write-Host $_ }

    if ($testExit -eq 0) { continue }
    $anyFailed = $true

    $resultsDir = Join-Path $proj.Directory.FullName 'TestResults'
    $trxFile = $null
    if (Test-Path $resultsDir) {
        $trxFile = Get-ChildItem -Path $resultsDir -Filter "$pkgName-*.trx" -Recurse |
            Where-Object { $_.LastWriteTimeUtc -ge $runStartUtc } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
    }

    $section = @("## $pkgName", '', "Full log: ``$logPath``", '')

    $trxFailures = if ($trxFile) { Get-FailureFromTrx -TrxPath $trxFile.FullName } else { @() }

    if ($trxFailures.Count -gt 0) {
        $section += "**Build succeeded; $($trxFailures.Count) test(s) failed:**"
        $section += ''
        foreach ($f in $trxFailures) {
            $section += "- ``$($f.TestName)`` ($($f.Outcome))"
            $section += '  ```'
            $section += "  $($f.Message)"
            if ($f.StackTrace) {
                $firstFrame = ($f.StackTrace -split "`n") | Select-Object -First 3
                $section += ($firstFrame | ForEach-Object { "  $_" })
            }
            $section += '  ```'
        }
    } else {
        $compilerErrors = Get-CompilerErrors -ConsoleLines $consoleLines
        if ($compilerErrors.Count -gt 0) {
            $section += "**Build FAILED before any test ran -- $($compilerErrors.Count) compiler error(s):**"
            $section += ''
            $section += '```'
            $section += ($compilerErrors | Select-Object -Unique)
            $section += '```'
        } else {
            $section += "**Test run failed (exit $testExit) with no .trx and no recognizable compiler error -- see the full log.**"
            $section += ''
            $section += '```'
            $section += ($consoleLines | Select-Object -Last 40)
            $section += '```'
        }
    }

    $section += ''
    $summarySections += ($section -join "`r`n")
}

$summaryPath = Join-Path $OutDir 'failures-summary.md'
$header = @(
    '# Failure diagnostics',
    '',
    "Generated by `Diagnose-Failures.ps1` against ``$GeneratedRoot``.",
    ''
)
if ($summarySections.Count -eq 0) {
    $header += 'No failures -- every tested package passed.'
} else {
    $header += "$($summarySections.Count) package(s) failed. Each section below names the real error; fix each, then re-run:"
    $header += ''
    $header += '```powershell'
    $header += ".\Diagnose-Failures.ps1 -GeneratedRoot `"$GeneratedRoot`" -Package <comma,separated,names>"
    $header += '```'
}
Set-Content -Path $summaryPath -Value (($header + $summarySections) -join "`r`n") -Encoding utf8

Write-Host ""
Write-Host "Failure summary written: $summaryPath" -ForegroundColor Green
if ($anyFailed) {
    Write-Host "Read $summaryPath, fix each package, then re-run with -Package to re-check just those." -ForegroundColor Yellow
    exit 1
}
exit 0
