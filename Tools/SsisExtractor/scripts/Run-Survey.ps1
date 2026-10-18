<#
.SYNOPSIS
  Runs the ssisx portfolio survey over a folder of SSIS packages and zips the result.

.DESCRIPTION
  This is the script that ships TO a client machine alongside ssisx.exe. It exists so the
  person at the keyboard runs one command instead of remembering five flags, and so the run
  is the same every time regardless of who does it.

  It needs nothing installed: ssisx.exe is published self-contained, so there is no .NET
  runtime, no Visual Studio, no SSIS, no SQL Server, and no network dependency. It only
  reads files -- it never connects to SSISDB, a database, or anything else.

  Exit code 2 means "some packages could not be read"; the survey still completed for the
  rest and load-failures.md names each one. That is a partial result worth keeping, not a
  failed run -- send the zip either way.

.PARAMETER PackageFolder
  Folder holding the SSIS packages. Any mix of .dtproj, .ispac, and loose .dtsx works, at
  any nesting depth.

.PARAMETER OutputFolder
  Where to write the survey. Defaults to .\ssis-survey next to this script.

.EXAMPLE
  .\Run-Survey.ps1 -PackageFolder "D:\SSIS_Projects"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackageFolder,

    [string] $OutputFolder
)

# Deliberately NOT 'Stop': ssisx.exe (a native command) writes its own benign warnings --
# "N input(s) could not be read", a duplicate-name skip -- to stderr, and under 'Stop' PowerShell
# upgrades that into a terminating NativeCommandError even though nothing here actually failed
# (the same "a native command's own stderr is not a script error" lesson this repo's own
# Run-Pipeline.ps1 already applies). Every genuinely fatal condition below uses an explicit
# `Write-Error ... -ErrorAction Stop` instead.
$ErrorActionPreference = 'Continue'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $scriptDir 'ssisx.exe'

if (-not (Test-Path $exe)) {
    Write-Error "ssisx.exe not found next to this script ($scriptDir). Keep them in the same folder." -ErrorAction Stop
}
if (-not (Test-Path $PackageFolder)) {
    Write-Error "Package folder not found: $PackageFolder" -ErrorAction Stop
}
if (-not $OutputFolder) { $OutputFolder = Join-Path $scriptDir 'ssis-survey' }

$dtsxCount = @(Get-ChildItem -Path $PackageFolder -Filter *.dtsx -Recurse -File -ErrorAction SilentlyContinue).Count
Write-Host "Scanning : $PackageFolder"
Write-Host "Found    : $dtsxCount .dtsx file(s)"
Write-Host "Writing  : $OutputFolder"
Write-Host ""

# `report`/`conformance` were retired as separate verbs (folded into `extract` -- see
# ExtractCommand.cs's own header comment): one call now does the portfolio report, column
# lineage, gate-1 conformance, and gate-2 testgen together. --recursive always: a portfolio is
# essentially never flat, and the flag is harmless on one that is. Redaction is on by default
# and is not overridable here on purpose -- the whole point of this wrapper is that the run
# that leaves the client site cannot accidentally be the one with --no-redact on it.
& $exe extract --input $PackageFolder --out $OutputFolder --recursive
$reportExit = $LASTEXITCODE

Write-Host ""
$digestMd = Join-Path $OutputFolder 'metadata\report\portfolio-digest.md'
$digestJson = Join-Path $OutputFolder 'metadata\report\portfolio-digest.json'
$digestExists = (Test-Path $digestMd) -and (Test-Path $digestJson)

if ($reportExit -eq 0) {
    # Nothing to warn about.
}
elseif ($reportExit -eq 2 -and $digestExists) {
    # Exit 2 now means a genuine (non-benign) load failure occurred somewhere in the portfolio
    # (see ExtractCommand's own Phase-1.2 fix -- a benign duplicate-name/package-not-found skip
    # no longer produces this exit at all), but extract still ran every stage regardless and the
    # digest still exists -- a partial result worth keeping, not a failed run.
    Write-Warning "Some packages could not be read -- see load-failures.md in the output. The survey is still valid for the rest."
}
elseif ($reportExit -eq 2) {
    Write-Error "ssisx extract exited 2 (genuine load failure) and produced no portfolio-digest.md -- see any console output above and $OutputFolder\metadata\report\load-failures.md. Nothing to export." -ErrorAction Stop
}
else {
    Write-Error "ssisx extract failed with exit code $reportExit." -ErrorAction Stop
}

# Two tiers on purpose, because the usual situation is that nothing may leave the site.
#
#  - The FULL survey stays put. It holds SQL text, script source, table/column names, file
#    paths and server names; it is the useful artifact, and it is the untransferable one.
#  - The DIGEST is counts only, built from an allow-list of Microsoft/tool vocabulary, with
#    package names reduced to PKG-nnn. It is also printed to the console above, so it can
#    leave as a screenshot or a phone photo when no file may leave at all.
if (-not $digestExists) {
    Write-Error "portfolio-digest.md/.json not found under $OutputFolder -- nothing to export. See any warnings above." -ErrorAction Stop
}

$exportDir = "$OutputFolder-EXPORT"
if (Test-Path $exportDir) { Remove-Item $exportDir -Recurse -Force }
New-Item -ItemType Directory -Path $exportDir | Out-Null
Copy-Item $digestMd, $digestJson -Destination $exportDir

$zip = "$exportDir.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$exportDir\*" -DestinationPath $zip -CompressionLevel Optimal
$zipKb = [Math]::Round((Get-Item $zip).Length / 1KB, 1)

Write-Host ""
Write-Host "=============================================================================="
Write-Host " FULL SURVEY (stays here -- contains SQL, script source, table and server names)"
Write-Host "   $OutputFolder"
Write-Host ""
Write-Host " CLEARED FOR EXPORT (counts only, no names, no code)"
Write-Host "   $zip  ($zipKb KB)"
Write-Host "   $digestMd"
Write-Host ""
Write-Host " If no file may leave: screenshot or photograph the digest printed above."
Write-Host " It is the whole picture needed to size the migration."
Write-Host "=============================================================================="

exit $reportExit
