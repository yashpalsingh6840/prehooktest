<#
.SYNOPSIS
  Runs `ssisx generate` then actually compiles the result -- the `ssisx generate` plan's
  Phase 4 layer 4 ("compile"), turned into a one-command, repeatable check instead of the
  one-off manual sequence used to first verify it (2026-08-25: LoadEmployees/
  LoadReferenceData both built 0 warnings/0 errors against a copy of Etl.Core).

.DESCRIPTION
  `ssisx generate` deliberately does NOT produce Etl.Core -- it's hand-written shared
  plumbing, not derivable from a .dtsx (see GenerateCommand.cs's own WriteFixedFiles
  comment), so generated output can never build on its own. This script supplies a
  compatible Etl.Core (copied from wherever the caller points -EtlCorePath, e.g.
  ..\..\Etl.Core -- the portable copy shipped alongside this tool, a sibling of
  Tools\SsisExtractor) so the rest of the pipeline -- generate, then dotnet build -- can be
  re-run in one command whenever the emitters change, instead of redoing the manual
  copy/build dance by hand.

  Neither this script nor `ssisx generate` itself hardcodes a path to any particular
  Etl.Core: pass whatever Etl.Core you want the generated output verified against. A client
  site just points -EtlCorePath at the Tools\Etl.Core folder that ships alongside ssisx (or
  its own copy, if it has one).

.PARAMETER PackageFolder
  Folder holding the SSIS packages to generate from (passed through to `ssisx generate
  --input`).

.PARAMETER EtlCorePath
  Path to a compatible Etl.Core project directory (must contain Etl.Core.csproj) to copy
  into the generated output before building.

.PARAMETER OutputFolder
  Where to write generated output. Defaults to .\out\generate-verify next to this script;
  deleted and recreated on each run so a stale prior run never masquerades as this one.

.EXAMPLE
  .\Verify-GeneratedBuild.ps1 -PackageFolder ..\..\..\SSIS -EtlCorePath ..\..\Etl.Core
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackageFolder,

    [Parameter(Mandatory = $true)]
    [string] $EtlCorePath,

    [string] $OutputFolder
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$cliProject = Join-Path $scriptDir '..\src\Ssis.Extract.Cli\Ssis.Extract.Cli.csproj'

if (-not (Test-Path $PackageFolder)) { Write-Error "Package folder not found: $PackageFolder" }
if (-not (Test-Path (Join-Path $EtlCorePath 'Etl.Core.csproj'))) {
    Write-Error "No Etl.Core.csproj found under -EtlCorePath ($EtlCorePath). Point it at an Etl.Core PROJECT directory, not its parent."
}
if (-not $OutputFolder) { $OutputFolder = Join-Path $scriptDir 'out\generate-verify' }

if (Test-Path $OutputFolder) { Remove-Item $OutputFolder -Recurse -Force }

Write-Host "Generating : $PackageFolder -> $OutputFolder"
& dotnet run --project $cliProject -c Release -- generate --input $PackageFolder --out $OutputFolder --recursive
$generateExit = $LASTEXITCODE

$generateDir = Join-Path $OutputFolder 'generate'
if (-not (Test-Path $generateDir)) {
    Write-Error "generate produced no output at all (exit $generateExit) -- see the command output above."
}

Write-Host ""
if ($generateExit -eq 3) {
    Write-Warning "generate reported gaps -- see generate-report.md. Build below only covers whatever DID generate."
} elseif ($generateExit -ne 0) {
    Write-Error "ssisx generate failed with exit code $generateExit -- see the command output above."
}

$etlCoreDest = Join-Path $generateDir 'Etl.Core'
Write-Host "Staging Etl.Core from $EtlCorePath"
Copy-Item -Path $EtlCorePath -Destination $etlCoreDest -Recurse -Force
Get-ChildItem -Path $etlCoreDest -Recurse -Directory -Include 'bin', 'obj' -ErrorAction SilentlyContinue |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

$slnx = Join-Path $generateDir 'Generated.slnx'
Write-Host ""
Write-Host "Building   : $slnx"
& dotnet build $slnx -c Release
$buildExit = $LASTEXITCODE

Write-Host ""
if ($buildExit -eq 0) {
    Write-Host "PASS -- every generated package compiled clean against the supplied Etl.Core." -ForegroundColor Green
} else {
    Write-Host "FAIL -- generated output did not compile (exit $buildExit). See build output above." -ForegroundColor Red
}

if ($buildExit -ne 0) { exit $buildExit }
exit $generateExit
