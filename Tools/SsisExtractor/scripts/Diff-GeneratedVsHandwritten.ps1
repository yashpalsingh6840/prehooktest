<#
.SYNOPSIS
  Runs `ssisx generate` then diffs its output against a hand-written reference project --
  the `ssisx generate` plan's Phase 4 layer 3, made trivially re-runnable instead of the
  copy-pasted `git diff --no-index` command in the plan's own Verification section.

.DESCRIPTION
  NOT a pass/fail gate, unlike the golden-file/differential/build checks -- several
  differences are KNOWN and INTENTIONAL (see Ssis.Extract.Codegen's own doc comments):
  EntityEmitter's property order follows pipeline column order, not the hand-written file's
  cosmetic order; ProgramEmitter always uses the multi-flow DI shape, even for one flow;
  DbContextEmitter's type name is "{Package}DbContext", not the hand-written
  "ReferenceDataDbContext"-style name. This script exists to make the comparison a single
  command a human reviews, per the plan's own framing: "every difference is either a
  generator bug or something the metadata doesn't capture."

.PARAMETER PackageFolder
  Folder holding the SSIS packages to generate from.

.PARAMETER HandwrittenPath
  Path to the hand-written reference project root, containing one subfolder per package
  (e.g. D:\PoC\SSIS_Rewrite\src, holding src\LoadEmployees, src\LoadReferenceData).

.PARAMETER OutputFolder
  Where to write generated output. Defaults to .\out\generate-diff next to this script.

.EXAMPLE
  .\Diff-GeneratedVsHandwritten.ps1 -PackageFolder ..\..\..\SSIS -HandwrittenPath D:\PoC\SSIS_Rewrite\src
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackageFolder,

    [Parameter(Mandatory = $true)]
    [string] $HandwrittenPath,

    [string] $OutputFolder
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$cliProject = Join-Path $scriptDir '..\src\Ssis.Extract.Cli\Ssis.Extract.Cli.csproj'

if (-not (Test-Path $PackageFolder)) { Write-Error "Package folder not found: $PackageFolder" }
if (-not (Test-Path $HandwrittenPath)) { Write-Error "Handwritten reference path not found: $HandwrittenPath" }
if (-not $OutputFolder) { $OutputFolder = Join-Path $scriptDir 'out\generate-diff' }
if (Test-Path $OutputFolder) { Remove-Item $OutputFolder -Recurse -Force }

& dotnet run --project $cliProject -c Release -- generate --input $PackageFolder --out $OutputFolder --recursive | Out-Null
$generateDir = Join-Path $OutputFolder 'generate'

$packages = Get-ChildItem -Path $generateDir -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'Program.cs') }

foreach ($pkg in $packages) {
    $handwrittenPkgDir = Join-Path $HandwrittenPath $pkg.Name
    Write-Host "=============================================================================="
    Write-Host " $($pkg.Name)"
    Write-Host "=============================================================================="

    if (-not (Test-Path $handwrittenPkgDir)) {
        Write-Warning "No hand-written reference at $handwrittenPkgDir -- skipping (this package has no hand-written counterpart, which is the normal case for a real client package)."
        continue
    }

    # Diff file-by-file against the generated side's OWN file list, not the whole handwritten
    # directory -- a whole-directory `git diff --no-index` also picks up bin/obj build output
    # on the handwritten side (--no-index bypasses .gitignore entirely), which is noise, not
    # signal.
    $generatedFiles = Get-ChildItem -Path $pkg.FullName -Recurse -File
    foreach ($genFile in $generatedFiles) {
        $relativePath = $genFile.FullName.Substring($pkg.FullName.Length + 1)
        $handwrittenFile = Join-Path $handwrittenPkgDir $relativePath

        if (-not (Test-Path $handwrittenFile)) {
            Write-Host "-- $relativePath : no hand-written counterpart"
            continue
        }

        # git writes routine things (e.g. "LF will be replaced by CRLF") to stderr, and under
        # $ErrorActionPreference = 'Stop' PowerShell 5.1 turns ANY native stderr line into a
        # terminating error regardless of exit code -- redirecting alone doesn't stop that,
        # the preference itself has to be relaxed for this call too. Not a real failure here
        # (a non-zero `git diff` exit just means "differences found").
        $previousEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $diff = & git diff --no-index -- $handwrittenFile $genFile.FullName 2>$null
        $ErrorActionPreference = $previousEap
        if ($diff) {
            Write-Host "-- $relativePath : DIFFERS"
            $diff | ForEach-Object { Write-Host "   $_" }
        } else {
            Write-Host "-- $relativePath : identical"
        }
    }
    Write-Host ""
}

Write-Host "Done. This is a review aid, not a pass/fail gate -- known intentional divergences are documented in each emitter's own doc comment (see Ssis.Extract.Codegen)."
exit 0
