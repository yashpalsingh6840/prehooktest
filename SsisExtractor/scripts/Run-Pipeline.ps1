<#
.SYNOPSIS
  A mechanical, always-do-these-5-steps pipeline for running this tool against one package or a
  whole folder: extract, walkthrough/sample data (svk), generate, apply-fills, then build + test
  + code coverage. Never deletes any existing output, test data, or fill -- every step below only
  ever reads what's already there and adds to it, so re-running this after manually editing a
  fill or dropping in real TestData is always safe.

.DESCRIPTION
  USE THIS ONLY WHEN NO CLAUDE CODE / GITHUB COPILOT CHAT SESSION IS AVAILABLE -- CI, a scripted
  regression check, or an ops person with no AI session at hand. It is a plain script: it can run
  `ssisx apply-fills` to pick up fills ALREADY on disk, but it can never write one, because a
  script has no way to read a work packet and reason about an answer the way an LLM can. If a
  Claude/Copilot chat session IS available, use `.github/prompts/ssisx-rewrite.prompt.md`
  (`/ssisx-rewrite`) instead -- it does the same 5 steps but AGENT-DRIVEN (each step is the
  agent's own tool call), so it can actually stop, read a gap, draft a fill, and wait for a
  human's confirmation instead of silently continuing past an unfilled gap into a build failure.
  See `Tools/COPILOT_GUIDE.md` for the full write-up of both. Five steps, always in this order:

    1. ssisx extract   -- packages/<Package>.spec.json for everything downstream to read
    2. svk walkthrough + svk sampledata -- human-readable review + schema-correct reference data
       (advisory only -- never blocks; a failure here is reported and the pipeline continues)
    3. ssisx generate   -- the C# project + starter tests + TestData/ wiring (seams on by default).
       Passed -FillsPath too (a real, previously-shipped bug: an earlier version of this script
       only forwarded -FillsPath to step 4, so a Tier-1 <Package>.decisions.json acknowledgment
       sitting there was silently never read -- generate has its own --fills flag specifically for
       this, and defaults to <out>/fills if omitted, which this script's own -FillsPath is
       deliberately NOT).
    4. ssisx apply-fills -- applies whatever is ALREADY under -FillsPath (this step does not
       write fills itself -- see ssisx-fill.prompt.md/ssisx-test.prompt.md for the AI-assisted
       gap-filling flow that produces them)
    5. dotnet build, then Run-Coverage.ps1 (dotnet test --filter Category!=Integration with code
       coverage collection, per package)

  Every path this script writes to is additive: -OutputPath/-FillsPath are created if missing,
  never recreated if present, and nothing under them is ever deleted by this script. If you need
  a truly clean run, remove the old output yourself first -- this script will not do it for you.

.PARAMETER InputPath
  A .dtproj, a single .dtsx, an .ispac, or a directory holding any mix of those (with
  --recursive, always passed). Passed straight through to `ssisx extract`/`generate`.

.PARAMETER OutputPath
  Where extract/svk/generate/gaps output all live -- `<OutputPath>\extract`, `\svk`, `\gen`.
  Reused as-is if it already exists (never deleted/recreated).

.PARAMETER Package
  Optional: only these package(s) (repeatable, comma-splittable within one value -- same
  convention as every ssisx command's own --package). Omit to process the whole -InputPath.

.PARAMETER Framework
  net8.0 or net10.0 -- the CLIENT's actual runtime, not a default to assume. If you don't know,
  ask before running this with a guessed value (see ssisx-generate.prompt.md's own rule).
  Defaults to net10.0 ONLY when not supplied -- pass it explicitly whenever the target is known.

.PARAMETER EtlCorePath
  Path to a compatible Etl.Core project directory. Defaults to the copy shipped alongside this
  tool (Tools\Etl.Core, a sibling of Tools\SsisExtractor).

.PARAMETER FillsPath
  Where hand/AI-written fills already live -- `fills-library\<Package>\...` in the usual
  Copilot-chat convention. Defaults to `<OutputPath>\fills`. This script only ever READS from
  here (via `ssisx apply-fills`); it never writes a fill itself.

.EXAMPLE
  .\Run-Pipeline.ps1 -InputPath D:\Client\SSIS -OutputPath D:\Client\ssisx-out -Framework net10.0

.EXAMPLE
  .\Run-Pipeline.ps1 -InputPath D:\Client\SSIS -OutputPath D:\Client\ssisx-out -Package Package_Advanced -Framework net8.0 -FillsPath D:\Client\fills-library
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $InputPath,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath,

    [string[]] $Package,

    [ValidateSet('net8.0', 'net10.0')]
    [string] $Framework = 'net10.0',

    [string] $EtlCorePath,

    [string] $FillsPath
)

# Deliberately NOT 'Stop': a native command's stderr (e.g. dotnet test's own [FAIL] lines
# further down the pipeline, or svk/ssisx's own advisory warnings) would otherwise be upgraded
# into a terminating exception. Fatal checks below use Write-Error -ErrorAction Stop explicitly.
$ErrorActionPreference = 'Continue'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

function Resolve-Tool([string] $exeName, [string] $csprojRelative) {
    $exe = Join-Path $scriptDir $exeName
    if (Test-Path $exe) { return @{ Cmd = $exe; Args = @() } }
    $csproj = Join-Path $scriptDir $csprojRelative
    if (-not (Test-Path $csproj)) {
        Write-Error "Neither $exeName (next to this script) nor $csproj was found -- build the solution first (dotnet build SsisExtractor.slnx -c Release) or copy the published exe alongside this script." -ErrorAction Stop
    }
    return @{ Cmd = 'dotnet'; Args = @('run', '--project', $csproj, '-c', 'Release', '--') }
}

function Invoke-Tool([hashtable] $tool, [string[]] $toolArgs) {
    # Piped through Write-Host so the external command's own stdout streams to the console in
    # real time WITHOUT entering this function's own output stream -- otherwise PowerShell
    # merges that stdout with the trailing `return $LASTEXITCODE` into one combined array,
    # corrupting the exit code the caller captures.
    $allArgs = @($tool.Args) + $toolArgs
    & $tool.Cmd @allArgs | ForEach-Object { Write-Host $_ }
    return $LASTEXITCODE
}

if (-not (Test-Path $InputPath)) { Write-Error "InputPath not found: $InputPath" -ErrorAction Stop }
if (-not $EtlCorePath) { $EtlCorePath = Join-Path $scriptDir '..\..\Etl.Core' }
if (-not (Test-Path (Join-Path $EtlCorePath 'Etl.Core.csproj'))) {
    Write-Error "No Etl.Core.csproj found under -EtlCorePath ($EtlCorePath)." -ErrorAction Stop
}
if (-not $FillsPath) { $FillsPath = Join-Path $OutputPath 'fills' }

# Additive only -- create if missing, never touch if present.
foreach ($dir in @($OutputPath, $FillsPath)) {
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
}

$ssisx = Resolve-Tool 'ssisx.exe' '..\src\Ssis.Extract.Cli\Ssis.Extract.Cli.csproj'
$svk = Resolve-Tool 'svk.exe' '..\..\SsisValidationKit\src\SvkCli\SvkCli.csproj'

$packageArgs = @()
if ($Package) { $packageArgs = @('--package', ($Package -join ',')) }

$extractOut = Join-Path $OutputPath 'extract'
$svkOut = Join-Path $OutputPath 'svk'
$genOut = Join-Path $OutputPath 'gen'
$generateDir = Join-Path $genOut 'generate'

Write-Host "==================================================================="
Write-Host " Step 1/5 -- extract"
Write-Host "==================================================================="
$extractExit = Invoke-Tool $ssisx (@('extract', '--input', $InputPath, '--out', $extractOut, '--recursive') + $packageArgs)
if ($extractExit -ne 0) { Write-Error "ssisx extract failed with exit code $extractExit." -ErrorAction Stop }

Write-Host ""
Write-Host "==================================================================="
Write-Host " Step 2/5 -- svk walkthrough + sampledata (advisory, never blocks)"
Write-Host "==================================================================="
$walkthroughExit = Invoke-Tool $svk (@('walkthrough', '--spec', $extractOut, '--out', $svkOut) + $packageArgs)
if ($walkthroughExit -ne 0) { Write-Warning "svk walkthrough exited $walkthroughExit -- continuing to Step 3 anyway (advisory only)." }
$sampledataExit = Invoke-Tool $svk (@('sampledata', '--spec', $extractOut, '--out', $svkOut) + $packageArgs)
if ($sampledataExit -ne 0) { Write-Warning "svk sampledata exited $sampledataExit -- continuing to Step 3 anyway (advisory only)." }

Write-Host ""
Write-Host "==================================================================="
Write-Host " Step 3/5 -- generate (framework: $Framework)"
Write-Host "==================================================================="
$generateExit = Invoke-Tool $ssisx (@('generate', '--input', $InputPath, '--out', $genOut, '--recursive', '--etl-core', $EtlCorePath, '--framework', $Framework, '--fills', $FillsPath) + $packageArgs)
if ($generateExit -ne 0 -and $generateExit -ne 3) { Write-Error "ssisx generate failed with exit code $generateExit." -ErrorAction Stop }
if ($generateExit -eq 3) { Write-Warning "generate reported blocking gaps -- see $genOut\generate-report.md. Steps 4/5 still run against whatever DID generate." }
if (-not (Test-Path $generateDir)) { Write-Error "generate produced no output at $generateDir." -ErrorAction Stop }

Write-Host ""
Write-Host "==================================================================="
Write-Host " Step 4/5 -- apply-fills (from $FillsPath)"
Write-Host "==================================================================="
$applyFillsExit = Invoke-Tool $ssisx @('apply-fills', '--out', $genOut, '--fills', $FillsPath)
if ($applyFillsExit -eq 3) { Write-Warning "apply-fills: some seams remain unfilled -- the build below will show CS8795 for each. Not a script failure." }
elseif ($applyFillsExit -ne 0) { Write-Warning "apply-fills exited $applyFillsExit -- see output above." }

Write-Host ""
Write-Host "==================================================================="
Write-Host " Step 5/5 -- build + test + coverage"
Write-Host "==================================================================="
$slnx = Join-Path $generateDir 'Generated.slnx'
& dotnet build $slnx -c Release
$buildExit = $LASTEXITCODE
if ($buildExit -ne 0) {
    Write-Warning "Build failed (exit $buildExit) -- likely unfilled Tier-1/2 seams (CS8795). Coverage step below only covers whatever DID build."
}

$coverageScript = Join-Path $scriptDir 'Run-Coverage.ps1'
$coverageArgs = @{ GeneratedRoot = $generateDir }
if ($Package) { $coverageArgs['Package'] = $Package }
& $coverageScript @coverageArgs
$coverageExit = $LASTEXITCODE

Write-Host ""
Write-Host "==================================================================="
Write-Host " Pipeline summary"
Write-Host "==================================================================="
Write-Host "  1. extract      : exit $extractExit"
Write-Host "  2. walkthrough  : exit $walkthroughExit (advisory)"
Write-Host "     sampledata   : exit $sampledataExit (advisory)"
Write-Host "  3. generate     : exit $generateExit $(if ($generateExit -eq 3) {'(gaps reported -- see generate-report.md)'})"
Write-Host "  4. apply-fills  : exit $applyFillsExit $(if ($applyFillsExit -eq 3) {'(seams still open)'})"
Write-Host "  5. build        : exit $buildExit"
Write-Host "     test+coverage: exit $coverageExit -- see $generateDir\coverage-report.md"
Write-Host ""
Write-Host "Nothing under -OutputPath/-FillsPath was deleted by this run -- re-run freely."

if ($buildExit -ne 0) { exit $buildExit }
exit $coverageExit
