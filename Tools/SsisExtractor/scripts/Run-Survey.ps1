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

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $scriptDir 'ssisx.exe'

if (-not (Test-Path $exe)) {
    Write-Error "ssisx.exe not found next to this script ($scriptDir). Keep them in the same folder."
}
if (-not (Test-Path $PackageFolder)) {
    Write-Error "Package folder not found: $PackageFolder"
}
if (-not $OutputFolder) { $OutputFolder = Join-Path $scriptDir 'ssis-survey' }

$dtsxCount = @(Get-ChildItem -Path $PackageFolder -Filter *.dtsx -Recurse -File -ErrorAction SilentlyContinue).Count
Write-Host "Scanning : $PackageFolder"
Write-Host "Found    : $dtsxCount .dtsx file(s)"
Write-Host "Writing  : $OutputFolder"
Write-Host ""

# --recursive always: a portfolio is essentially never flat, and the flag is harmless on one
# that is. Redaction is on by default and is not overridable here on purpose -- the whole
# point of this wrapper is that the run that leaves the client site cannot accidentally be
# the one with --no-redact on it.
& $exe report --input $PackageFolder --out $OutputFolder --recursive
$reportExit = $LASTEXITCODE

Write-Host ""
if ($reportExit -eq 2) {
    Write-Warning "Some packages could not be read -- see load-failures.md in the output. The survey is still valid for the rest."
} elseif ($reportExit -ne 0) {
    Write-Error "ssisx report failed with exit code $reportExit."
}

# The conformance pass is cheap and additive: it turns each package into the list of
# obligations a rewrite has to satisfy, which is the thing that actually sizes the work.
& $exe conformance --input $PackageFolder --out $OutputFolder --recursive
if ($LASTEXITCODE -eq 2) {
    Write-Warning "conformance pass reported a usage/extraction error; the report output above is unaffected."
}

# Two tiers on purpose, because the usual situation is that nothing may leave the site.
#
#  - The FULL survey stays put. It holds SQL text, script source, table/column names, file
#    paths and server names; it is the useful artifact, and it is the untransferable one.
#  - The DIGEST is counts only, built from an allow-list of Microsoft/tool vocabulary, with
#    package names reduced to PKG-nnn. It is also printed to the console above, so it can
#    leave as a screenshot or a phone photo when no file may leave at all.
$digestMd = Join-Path $OutputFolder 'portfolio-digest.md'
$digestJson = Join-Path $OutputFolder 'portfolio-digest.json'

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
