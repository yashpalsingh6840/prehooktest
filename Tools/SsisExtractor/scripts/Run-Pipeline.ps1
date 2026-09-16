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

.PARAMETER Quiet
  Redirect every step's own verbose console output (dotnet restore/build noise, ssisx/svk's own
  per-package chatter, the full `dotnet test` transcript) to `<OutputPath>\pipeline-run.log`
  instead of the console -- only the final compact summary (exit codes + gap counts by tier,
  parsed from gaps.json, no extra file reads needed by a caller) prints to stdout. Use this when
  an AGENT is the one invoking this script: an agent's tool call captures a command's ENTIRE
  stdout as its own context regardless of what the agent is later told to "only read" -- a run
  with 60+ gaps across dotnet restore/build/test can easily be tens of thousands of tokens of
  console noise the agent never actually needed. A human running this interactively should omit
  -Quiet and keep seeing live progress; overwritten each run (it's a log, not user data -- the
  additive-only rule above still applies to -OutputPath/-FillsPath themselves).

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

    [string] $FillsPath,

    [switch] $Quiet
)

# Deliberately NOT 'Stop': a native command's stderr (e.g. dotnet test's own [FAIL] lines
# further down the pipeline, or svk/ssisx's own advisory warnings) would otherwise be upgraded
# into a terminating exception. Fatal checks below use Write-Error -ErrorAction Stop explicitly.
$ErrorActionPreference = 'Continue'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$logFilePath = $null # set once $OutputPath is known, below

function Resolve-Tool([string] $exeName, [string] $csprojRelative) {
    $exe = Join-Path $scriptDir $exeName
    if (Test-Path $exe) { return @{ Cmd = $exe; Args = @() } }
    $csproj = Join-Path $scriptDir $csprojRelative
    if (-not (Test-Path $csproj)) {
        Write-Error "Neither $exeName (next to this script) nor $csproj was found -- build the solution first (dotnet build SsisExtractor.slnx -c Release) or copy the published exe alongside this script." -ErrorAction Stop
    }
    return @{ Cmd = 'dotnet'; Args = @('run', '--project', $csproj, '-c', 'Release', '--') }
}

# Every "banner"/status line always goes to the console (cheap, a handful of lines total) --
# only the potentially-huge output of an ACTUAL command (dotnet restore/build/test chatter,
# ssisx/svk's own per-package lines) is redirected to the log file under -Quiet. This is what
# keeps -Quiet's console output small without hiding what step failed or why.
function Write-Banner([string] $text) {
    if ($logFilePath) { Add-Content -Path $logFilePath -Value $text }
    else { Write-Host $text }
}

function Invoke-Tool([hashtable] $tool, [string[]] $toolArgs) {
    # Piped through Write-Host so the external command's own stdout streams to the console in
    # real time WITHOUT entering this function's own output stream -- otherwise PowerShell
    # merges that stdout with the trailing `return $LASTEXITCODE` into one combined array,
    # corrupting the exit code the caller captures. Under -Quiet, the same output is appended to
    # the log file instead -- an agent's tool call otherwise captures ALL of this as its own
    # context regardless of what it's later told to read, which is the real reason a mechanical,
    # tool-only pipeline run can still cost tens of thousands of tokens per invocation.
    $allArgs = @($tool.Args) + $toolArgs
    if ($logFilePath) {
        & $tool.Cmd @allArgs 2>&1 | ForEach-Object { Add-Content -Path $logFilePath -Value $_ }
    }
    else {
        & $tool.Cmd @allArgs | ForEach-Object { Write-Host $_ }
    }
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

if ($Quiet) {
    $logFilePath = Join-Path $OutputPath 'pipeline-run.log'
    Set-Content -Path $logFilePath -Value "Run-Pipeline.ps1 log -- $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    Write-Host "Running quietly -- full step-by-step output is being written to $logFilePath. Only the final summary below prints here."
}

$ssisx = Resolve-Tool 'ssisx.exe' '..\src\Ssis.Extract.Cli\Ssis.Extract.Cli.csproj'
$svk = Resolve-Tool 'svk.exe' '..\..\SsisValidationKit\src\SvkCli\SvkCli.csproj'

$packageArgs = @()
if ($Package) { $packageArgs = @('--package', ($Package -join ',')) }

$extractOut = Join-Path $OutputPath 'extract'
$svkOut = Join-Path $OutputPath 'svk'
$genOut = Join-Path $OutputPath 'gen'
$generateDir = Join-Path $genOut 'generate'

Write-Banner "==================================================================="
Write-Banner " Step 1/5 -- extract"
Write-Banner "==================================================================="
$extractExit = Invoke-Tool $ssisx (@('extract', '--input', $InputPath, '--out', $extractOut, '--recursive') + $packageArgs)
if ($extractExit -ne 0) {
    # ssisx extract returns a non-zero exit whenever ANY load failure occurred, but
    # load-failures.md's own Kind column distinguishes benign skips (duplicate-name --
    # first package with a given ObjectName wins, the rest are just omitted from the report;
    # package-not-found -- a --package filter miss) from a genuinely unreadable
    # .dtsx/.dtproj/.ispac (Kind package/project/ispac, carrying the real exception message).
    # Only the latter should abort the pipeline.
    $loadFailuresPath = Join-Path $extractOut 'load-failures.md'
    $benignKinds = @('duplicate-name', 'package-not-found')
    $hardFailureRows = @()
    if (Test-Path $loadFailuresPath) {
        $rows = Get-Content $loadFailuresPath | Where-Object { $_ -match '^\|\s*(\S[^|]*?)\s*\|' -and $_ -notmatch '^\|\s*Kind\s*\|' -and $_ -notmatch '^\|\s*---' }
        foreach ($row in $rows) {
            if ($row -match '^\|\s*(?<kind>[^|]+?)\s*\|') {
                $kind = $Matches['kind'].Trim()
                if ($benignKinds -notcontains $kind) { $hardFailureRows += $row }
            }
        }
    }
    if ($hardFailureRows.Count -gt 0) {
        Write-Error "ssisx extract failed with exit code $extractExit -- genuine load failure(s) in load-failures.md (not just duplicate-name/package-not-found skips):`n$($hardFailureRows -join "`n")" -ErrorAction Stop
    }
    elseif (Test-Path $loadFailuresPath) {
        Write-Warning "ssisx extract exited $extractExit, but load-failures.md shows only benign duplicate-name/package-not-found skips -- continuing to Step 2 anyway. See $loadFailuresPath."
    }
    else {
        # Non-zero exit with no load-failures.md at all is unexpected -- treat as fatal rather
        # than silently continuing past an error this script doesn't understand.
        Write-Error "ssisx extract failed with exit code $extractExit and no $loadFailuresPath was found to explain why." -ErrorAction Stop
    }
}

Write-Banner ""
Write-Banner "==================================================================="
Write-Banner " Step 2/5 -- svk walkthrough + sampledata (advisory, never blocks)"
Write-Banner "==================================================================="
$walkthroughExit = Invoke-Tool $svk (@('walkthrough', '--spec', $extractOut, '--out', $svkOut) + $packageArgs)
if ($walkthroughExit -ne 0) { Write-Warning "svk walkthrough exited $walkthroughExit -- continuing to Step 3 anyway (advisory only)." }
$sampledataExit = Invoke-Tool $svk (@('sampledata', '--spec', $extractOut, '--out', $svkOut) + $packageArgs)
if ($sampledataExit -ne 0) { Write-Warning "svk sampledata exited $sampledataExit -- continuing to Step 3 anyway (advisory only)." }

Write-Banner ""
Write-Banner "==================================================================="
Write-Banner " Step 3/5 -- generate (framework: $Framework)"
Write-Banner "==================================================================="
$generateExit = Invoke-Tool $ssisx (@('generate', '--input', $InputPath, '--out', $genOut, '--recursive', '--etl-core', $EtlCorePath, '--framework', $Framework, '--fills', $FillsPath) + $packageArgs)
if ($generateExit -ne 0 -and $generateExit -ne 3) { Write-Error "ssisx generate failed with exit code $generateExit." -ErrorAction Stop }
if ($generateExit -eq 3) { Write-Warning "generate reported blocking gaps -- see $genOut\generate-report.md. Steps 4/5 still run against whatever DID generate." }
if (-not (Test-Path $generateDir)) { Write-Error "generate produced no output at $generateDir." -ErrorAction Stop }

Write-Banner ""
Write-Banner "==================================================================="
Write-Banner " Step 4/5 -- apply-fills (from $FillsPath)"
Write-Banner "==================================================================="
$applyFillsExit = Invoke-Tool $ssisx @('apply-fills', '--out', $genOut, '--fills', $FillsPath)
if ($applyFillsExit -eq 3) { Write-Warning "apply-fills: some seams remain unfilled -- the build below will show CS8795 for each. Not a script failure." }
elseif ($applyFillsExit -ne 0) { Write-Warning "apply-fills exited $applyFillsExit -- see output above." }

Write-Banner ""
Write-Banner "==================================================================="
Write-Banner " Step 5/5 -- build + test + coverage"
Write-Banner "==================================================================="
$slnx = Join-Path $generateDir 'Generated.slnx'
if ($logFilePath) {
    & dotnet build $slnx -c Release *>&1 | ForEach-Object { Add-Content -Path $logFilePath -Value $_ }
}
else {
    & dotnet build $slnx -c Release
}
$buildExit = $LASTEXITCODE
if ($buildExit -ne 0) {
    Write-Warning "Build failed (exit $buildExit) -- likely unfilled Tier-1/2 seams (CS8795). Coverage step below only covers whatever DID build."
}

$coverageScript = Join-Path $scriptDir 'Run-Coverage.ps1'
$coverageArgs = @{ GeneratedRoot = $generateDir }
if ($Package) { $coverageArgs['Package'] = $Package }
if ($logFilePath) {
    & $coverageScript @coverageArgs *>&1 | ForEach-Object { Add-Content -Path $logFilePath -Value $_ }
}
else {
    & $coverageScript @coverageArgs
}
$coverageExit = $LASTEXITCODE

Write-Banner ""
Write-Banner "==================================================================="
Write-Banner " Pipeline summary"
Write-Banner "==================================================================="
Write-Host "  1. extract      : exit $extractExit"
Write-Host "  2. walkthrough  : exit $walkthroughExit (advisory)"
Write-Host "     sampledata   : exit $sampledataExit (advisory)"
Write-Host "  3. generate     : exit $generateExit $(if ($generateExit -eq 3) {'(gaps reported -- see generate-report.md)'})"
Write-Host "  4. apply-fills  : exit $applyFillsExit $(if ($applyFillsExit -eq 3) {'(seams still open)'})"
Write-Host "  5. build        : exit $buildExit"
Write-Host "     test+coverage: exit $coverageExit $(if ($coverageExit -eq 2) {'(no test projects -- 0 packages fully generated)'}) -- see $generateDir\coverage-report.md"

# Gap counts by tier, parsed once here from gaps.json -- so a caller (especially an agent) never
# needs a SEPARATE Read of that file just to get this number. MissingDatum/MissingLogic are the
# only tiers a fill can ever close; MissingToolSupport/Advisory are informational.
$gapsJsonPath = Join-Path $genOut 'gaps.json'
if (Test-Path $gapsJsonPath) {
    try {
        $gaps = Get-Content $gapsJsonPath -Raw | ConvertFrom-Json
        $byTier = $gaps | Group-Object Tier | Sort-Object Name
        Write-Host "  gaps by tier  : $(($byTier | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ', ') (total $($gaps.Count))"
    }
    catch {
        Write-Warning "Could not parse $gapsJsonPath for a gap-tier summary: $_"
    }
}

Write-Host ""
Write-Host "Nothing under -OutputPath/-FillsPath was deleted by this run -- re-run freely."
if ($logFilePath) { Write-Host "Full step-by-step output for this run: $logFilePath" }

if ($buildExit -ne 0) { exit $buildExit }
exit $coverageExit
