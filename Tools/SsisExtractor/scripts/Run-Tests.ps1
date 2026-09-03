<#
.SYNOPSIS
    Runs every test suite this tool depends on and exits non-zero if ANY of them fails.

.DESCRIPTION
    This exists because of a specific finding. Three tests were failing in COMMITTED state --
    a stale project golden, a stale LoadReferenceData golden, and an .ispac test pinning a
    package count that the SSIS project had legitimately outgrown -- and they had been failing
    long enough that CLAUDE.md described them as "someone's in-progress SSDT edits" rather than
    what they were: committed, permanent failures.

    The reason they survived is simpler than a swallowed exit code. There is no CI in this repo
    at all: no workflow file, no pipeline definition, and (before this script) nothing that ran
    `dotnet test` for the extractor. Nothing was ever going to catch them.

    So this is the missing piece: one command that runs everything and fails loudly. Point a CI
    job at it, or run it before committing.

    Etl.Core the LIBRARY has a portable, in-repo copy at Tools\Etl.Core (a sibling of
    Tools\SsisExtractor) -- that copy is what ships with this tool and what generated packages
    build against on a client site with no access to this PoC's own dev repos. Etl.Core the TEST
    SUITE (Etl.Core.Tests, plus the hand-written LoadEmployees.Tests/LoadReferenceData.Tests it
    ships alongside) still lives only in the sibling development repository (D:\PoC\SSIS_Rewrite),
    since a client site has no reason to run this repo's own dev-time regression tests. This
    script runs that suite when present, and skips it with a warning when it is not, rather than
    silently passing on a machine that only has this repo. If Etl.Core the library changes,
    refresh Tools\Etl.Core from D:\PoC\SSIS_Rewrite\src\Etl.Core (excluding bin/obj) so the two
    copies do not drift apart.

.PARAMETER SkipRewrite
    Skip the sibling Etl.Core suite even if the repo is present.

.PARAMETER RewritePath
    Where the Etl.Core repo lives. Defaults to D:\PoC\SSIS_Rewrite.

.EXAMPLE
    .\Run-Tests.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipRewrite,
    [string]$RewritePath = "D:\PoC\SSIS_Rewrite"
)

$ErrorActionPreference = "Continue"
$extractorRoot = Split-Path -Parent $PSScriptRoot
$failures = New-Object System.Collections.Generic.List[string]
$ran = 0

function Invoke-Suite {
    param([string]$Label, [string]$Target, [string]$WorkingDirectory)

    Write-Host ""
    Write-Host "=== $Label ===" -ForegroundColor Cyan
    Push-Location $WorkingDirectory
    try {
        # Deliberately NOT piping through Select-String: a filter would discard the failure
        # detail that makes a red run actionable, which is the whole point of running this.
        dotnet test $Target --nologo -v minimal
        $code = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    $script:ran++
    if ($code -ne 0) {
        $script:failures.Add("$Label (dotnet test exited $code)")
        Write-Host "FAILED: $Label" -ForegroundColor Red
    }
    else {
        Write-Host "ok: $Label" -ForegroundColor Green
    }
}

Invoke-Suite -Label "ssisx (extractor, codegen, expression runtime)" `
             -Target "SsisExtractor.slnx" -WorkingDirectory $extractorRoot

if ($SkipRewrite) {
    Write-Host ""
    Write-Host "skipped: Etl.Core suite (-SkipRewrite)" -ForegroundColor Yellow
}
elseif (Test-Path (Join-Path $RewritePath "src\Etl.Core\Etl.Core.csproj")) {
    Invoke-Suite -Label "Etl.Core (runtime the generated packages target)" `
                 -Target "" -WorkingDirectory $RewritePath
}
else {
    # A warning, not silence: a green run here would otherwise imply coverage that did not run.
    Write-Host ""
    Write-Host "WARNING: Etl.Core dev repo not found at $RewritePath -- its TEST SUITE did NOT run." -ForegroundColor Yellow
    Write-Host "         (The library itself is unaffected -- generated packages target the portable" -ForegroundColor Yellow
    Write-Host "         copy at Tools\Etl.Core, not this repo. Only its own regression tests live here.)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "======================================================" -ForegroundColor Cyan
if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) of $ran suite(s) FAILED:" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host "  - $f" -ForegroundColor Red }
    exit 1
}

Write-Host "all $ran suite(s) passed" -ForegroundColor Green
exit 0
