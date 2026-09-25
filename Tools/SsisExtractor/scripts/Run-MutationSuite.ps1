<#
.SYNOPSIS
    Runs the full 18-mutation suite from the independent-review fix plan (Phase 0.3 / Phase 6,
    `C:\Users\yashpal.singh01\.claude\plans\snappy-skipping-grove.md`) against a freshly-generated
    scratch tree, and reports how many the CURRENT starter tests actually kill.

.DESCRIPTION
    The review's own plan states a 4/18 baseline; this script's own reconstruction of the same 18
    mutations (built from a real `ssisx generate` run, since the review's own exact byte-level
    Old/New pairs were not preserved -- see each row's own comment for how it was sourced)
    empirically measures 3/18 -- one row ("SUBSTRING 3->4") does not reproduce as a kill today
    for a real, confirmed reason (see that row's own comment). Phase 6's own items each target
    specific rows and the plan's overall goal is >= 11/18. This script is the reproducible harness
    that measurement is made against -- it does not itself change any emitter.

    Each mutation is a byte-exact find/replace inside one already-generated `.cs` file, applied,
    tested, and reverted by `Run-Mutation.ps1`. The 18 rows below were reconstructed from a real
    `ssisx generate` run (recorded in this script's own comments) rather than guessed -- every
    Old/New pair is the literal text `ssisx generate` produces today for the named package/fixture,
    so a mutation reported NOT-APPLIED means the generator's own output shape has changed and this
    table needs updating, not that something is broken.

.PARAMETER GenerateRoot
    A scratch directory this script populates (or reuses, with -SkipGenerate) with four generated
    trees: `le/` (LoadEmployees), `rbc/` (the RBC_Demo_ETL portfolio), `fixtures/aggregate/`
    (SyntheticAggregate.dtsx) and `fixtures/lookup/` (SyntheticLookupSingle.dtsx). Never the repo
    itself -- pass a `D:\tmp\...` style path.

.PARAMETER SsisxExe
    Path to a built `ssisx.exe`. Defaults to the Release build under this repo's own
    `src/Ssis.Extract.Cli/bin/Release/net8.0/ssisx.exe`.

.PARAMETER EtlCorePath
    Path to `Tools/Etl.Core`, copied into each generated tree. Defaults to the repo's own copy
    relative to this script.

.PARAMETER RbcInputPath
    Path to the real RBC_Demo_ETL portfolio used for end-to-end verification throughout the fix
    plan. Defaults to `D:\PoC\SSIS_Packages\SSIS_From_Sandeep\RBC_Demo_ETL\RBC_Demo_ETL` (the plan's
    own "Real portfolio for end-to-end checks" -- explicitly NOT SSIS_Packages_From_GitHub).

.PARAMETER LeInputPath
    Path to `LoadEmployees.dtsx`. Defaults to `D:\PoC\SSIS_Packages\SSIS\LoadEmployees.dtsx`.

.PARAMETER SkipGenerate
    Reuse an already-populated -GenerateRoot from a prior run instead of regenerating (faster
    iteration when only the mutation table itself changed).

.OUTPUTS
    One line per mutation (from Run-Mutation.ps1), then a summary: "N/18 killed" plus, for every
    mutation whose kill/survive result DIFFERS from the table's own ExpectedKillToday column, an
    explicit "REGRESSION" or "IMPROVEMENT" line -- so a Phase-6 fix that starts killing a
    previously-surviving mutation is visible without having to diff console output by eye.
#>
param(
    [Parameter(Mandatory)][string]$GenerateRoot,
    [string]$SsisxExe = (Join-Path $PSScriptRoot "..\src\Ssis.Extract.Cli\bin\Release\net8.0\ssisx.exe"),
    [string]$EtlCorePath = (Join-Path $PSScriptRoot "..\..\Etl.Core"),
    [string]$RbcInputPath = "D:\PoC\SSIS_Packages\SSIS_From_Sandeep\RBC_Demo_ETL\RBC_Demo_ETL",
    [string]$LeInputPath = "D:\PoC\SSIS_Packages\SSIS\LoadEmployees.dtsx",
    [switch]$SkipGenerate
)
$ErrorActionPreference = 'Stop'

$SsisxExe = (Resolve-Path $SsisxExe).Path
$EtlCorePath = (Resolve-Path $EtlCorePath).Path
$GenerateRoot = [IO.Path]::GetFullPath($GenerateRoot)
$LogDir = Join-Path $GenerateRoot "mutation-logs"

if (-not $SkipGenerate) {
    Write-Host "Generating scratch trees under $GenerateRoot ..."
    New-Item -ItemType Directory -Force -Path $GenerateRoot | Out-Null

    $leOut = Join-Path $GenerateRoot "le"
    $rbcOut = Join-Path $GenerateRoot "rbc"
    $aggOut = Join-Path $GenerateRoot "fixtures\aggregate"
    $lkpOut = Join-Path $GenerateRoot "fixtures\lookup"
    Remove-Item -Recurse -Force $leOut, $rbcOut, $aggOut, $lkpOut -ErrorAction SilentlyContinue

    $fixturesDir = Join-Path $PSScriptRoot "..\tests\Ssis.Extract.Tests\Fixtures"

    & $SsisxExe generate --input $LeInputPath --out $leOut --etl-core $EtlCorePath | Out-Null
    & $SsisxExe generate --input $RbcInputPath --out $rbcOut --recursive --etl-core $EtlCorePath | Out-Null
    & $SsisxExe generate --input (Join-Path $fixturesDir "SyntheticAggregate.dtsx") --out $aggOut --etl-core $EtlCorePath | Out-Null
    & $SsisxExe generate --input (Join-Path $fixturesDir "SyntheticLookupSingle.dtsx") --out $lkpOut --etl-core $EtlCorePath | Out-Null

    Write-Host "Generation done."
}

function P([string]$rel) { Join-Path $GenerateRoot $rel }

# --- The 18-mutation table -------------------------------------------------------------------
# ExpectedKillToday reflects the plan's own baseline (4/18: SUBSTRING, drop-Salary, TRUNCATE,
# CommitAsync). Every Old/New pair is the literal text a real `ssisx generate` run produces for
# this package/fixture as of 2026-09-25 -- reconstructed by grepping the generated output, not
# guessed. If a row goes NOT-APPLIED, the generator's shape moved and this table needs a refresh,
# not a "fix" to the emitter.
$Mutations = @(
    # --- LoadEmployees (3, all currently survive) ---
    @{ Id = "LE1-swapFirstLast"; ExpectedKillToday = $false
       TestProj = P "le\generate\LoadEmployees.Tests\LoadEmployees.Tests.csproj"
       File = P "le\generate\LoadEmployees\Mapping\DER_MergeColumns.cs"
       Old = '$"{row.FirstName} {row.LastName}"'; New = '$"{row.LastName} {row.FirstName}"' }
    @{ Id = "LE2-width101to14"; ExpectedKillToday = $false
       TestProj = P "le\generate\LoadEmployees.Tests\LoadEmployees.Tests.csproj"
       File = P "le\generate\LoadEmployees\Mapping\DER_MergeColumns.cs"
       Old = ', 101, nameof(Employee.FullName)'; New = ', 14, nameof(Employee.FullName)' }
    @{ Id = "LE3-csvMapFirstNameToLastNameHeader"; ExpectedKillToday = $false
       TestProj = P "le\generate\LoadEmployees.Tests\LoadEmployees.Tests.csproj"
       File = P "le\generate\LoadEmployees\Csv\EmployeeCsvRowMap.cs"
       Old = 'Map(m => m.FirstName).Name("FirstName");'; New = 'Map(m => m.FirstName).Name("LastName");' }

    # --- Killed today (3 measured; the plan's own review claimed a 4th, "SUBSTRING 3->4",
    # which does NOT reproduce here -- see the note on M-substring3to4 immediately below) ---
    # NOTE: measured $false, not the plan's claimed $true. DER_Enrich's own starter test uses
    # the generic representative literal "Sample" for Email, which contains no "@" at all, so
    # FINDSTRING(...) > 0 is always false and the ternary NEVER reaches the SUBSTRING branch --
    # confirmed by reading SyntheticEmailDomainTargetTransformTests.cs directly (asserts
    # EmailDomain == "(none)" for row.Email == "Sample"). This is exactly Phase 6.1's own target
    # ("distinct representative literal per column" instead of a generic "Sample"/"1" for every
    # column regardless of what the expression actually needs) -- once 6.1 lands, re-verify
    # whether this mutation starts killing, and only then should ExpectedKillToday flip to $true.
    @{ Id = "M-substring3to4"; ExpectedKillToday = $false
       TestProj = P "rbc\generate\Package_Transforms.Tests\Package_Transforms.Tests.csproj"
       File = P "rbc\generate\Package_Transforms\Mapping\DER_Enrich.cs"
       Old = '(SsisFn.FindString(SsisFn.Trim(row.Email), "@", 1) + 1), 100)'
       New = '(SsisFn.FindString(SsisFn.Trim(row.Email), "@", 1) + 1), 4)' }
    @{ Id = "M-dropSalaryAssignment"; ExpectedKillToday = $true
       TestProj = P "le\generate\LoadEmployees.Tests\LoadEmployees.Tests.csproj"
       File = P "le\generate\LoadEmployees\Mapping\EmployeeTransform.cs"
       Old = "Salary = row.Salary,`n        "; New = "" }
    @{ Id = "M-truncateToDelete"; ExpectedKillToday = $true
       TestProj = P "le\generate\LoadEmployees.Tests\LoadEmployees.Tests.csproj"
       File = P "le\generate\LoadEmployees\Mapping\SQL_TruncateTargetStatement.cs"
       Old = "TRUNCATE TABLE dbo.Employee;"; New = "DELETE FROM dbo.Employee;" }
    @{ Id = "M-removeCommitAsync"; ExpectedKillToday = $true
       TestProj = P "le\generate\LoadEmployees.Tests\LoadEmployees.Tests.csproj"
       File = P "le\generate\LoadEmployees\LoadEmployees.cs"
       Old = "await uow.CommitAsync(ct);"; New = "// mutated: commit removed" }

    # --- RBC, currently survives (9) ---
    @{ Id = "R1-routerCanadaToCanadx"; ExpectedKillToday = $false
       TestProj = P "rbc\generate\Package_Transforms.Tests\Package_Transforms.Tests.csproj"
       File = P "rbc\generate\Package_Transforms\Mapping\CSPLIT_ByCountryRouter.cs"
       Old = '"Canada", System.StringComparison.Ordinal'; New = '"Canadx", System.StringComparison.Ordinal' }
    @{ Id = "R2-validityGtToGe"; ExpectedKillToday = $false
       TestProj = P "rbc\generate\Package_Transforms.Tests\Package_Transforms.Tests.csproj"
       File = P "rbc\generate\Package_Transforms\Mapping\CSPLIT_ValidityRouter.cs"
       Old = '"@", 1) > 0)))'; New = '"@", 1) >= 0)))' }
    @{ Id = "R3-dropTrim"; ExpectedKillToday = $false
       TestProj = P "rbc\generate\Package_Transforms.Tests\Package_Transforms.Tests.csproj"
       File = P "rbc\generate\Package_Transforms\Mapping\CSPLIT_ValidityRouter.cs"
       Old = 'SsisFn.Trim(row.Email), "@"'; New = 'row.Email, "@"' }
    @{ Id = "R4-narrowR8ToI4ToPlainCast"; ExpectedKillToday = $false
       TestProj = P "rbc\generate\Package_Advanced.Tests\Package_Advanced.Tests.csproj"
       File = P "rbc\generate\Package_Advanced\Mapping\DripEligibilityTransform.cs"
       Old = "SsisFn.NarrowR8ToI4(row.CustomerID)"; New = "(int)row.CustomerID" }
    @{ Id = "R5-parseWstrToI4ToZero"; ExpectedKillToday = $false
       TestProj = P "rbc\generate\Package_Exports.Tests\Package_Exports.Tests.csproj"
       File = P "rbc\generate\Package_Exports\Mapping\CustomerExportLogTransform.cs"
       Old = "SsisFn.ParseWstrToI4(row.CustomerID)"; New = "0" }
    @{ Id = "R6-fixedWidth30to3"; ExpectedKillToday = $false
       TestProj = P "rbc\generate\Package_Exports.Tests\Package_Exports.Tests.csproj"
       File = P "rbc\generate\Package_Exports\Package_Exports.cs"
       Old = 'new FlatFileColumnFormat("FullName", 30, "")'; New = 'new FlatFileColumnFormat("FullName", 3, "")' }
    @{ Id = "R7-mapperContactPhoneFromPreferredChannel"; ExpectedKillToday = $false
       TestProj = P "rbc\generate\Package_Transforms.Tests\Package_Transforms.Tests.csproj"
       File = P "rbc\generate\Package_Transforms\Mapping\MRG_CustomerContactsMapper.cs"
       Old = "ContactPhone = right?.ContactPhone,"; New = "ContactPhone = right?.PreferredChannel," }
    @{ Id = "R8-narrowI8ToI4ToZero"; ExpectedKillToday = $false
       TestProj = P "rbc\generate\Package_Transforms.Tests\Package_Transforms.Tests.csproj"
       File = P "rbc\generate\Package_Transforms\Mapping\RegionSummaryTransform.cs"
       Old = "SsisFn.NarrowI8ToI4(row.CustomerCount)"; New = "0" }
    @{ Id = "R9-mergeJoinInnerToLeftOuter"; ExpectedKillToday = $false
       TestProj = P "rbc\generate\Package_Transforms.Tests\Package_Transforms.Tests.csproj"
       File = P "rbc\generate\Package_Transforms\Package_Transforms.cs"
       Old = "MergeJoinType.Inner,"; New = "MergeJoinType.LeftOuter," }

    # --- Fixtures, currently survive (2) ---
    @{ Id = "F1-aggregateCountAll"; ExpectedKillToday = $false
       TestProj = P "fixtures\aggregate\generate\SyntheticAggregate.Tests\SyntheticAggregate.Tests.csproj"
       File = P "fixtures\aggregate\generate\SyntheticAggregate\SyntheticAggregate.cs"
       Old = "rows.Count(r => r.CustomerID != null)"; New = "rows.Count()" }
    @{ Id = "F2-lookupCountryCodeFromRegion"; ExpectedKillToday = $false
       TestProj = P "fixtures\lookup\generate\SyntheticLookupSingle.Tests\SyntheticLookupSingle.Tests.csproj"
       File = P "fixtures\lookup\generate\SyntheticLookupSingle\Mapping\SyntheticLookupSingleTargetTransform.cs"
       Old = "CountryCode = lookup[row.Country].CountryCode,"; New = "CountryCode = lookup[row.Country].Region," }
)

# --- Run ---------------------------------------------------------------------------------------
$results = @()
foreach ($m in $Mutations) {
    $line = & (Join-Path $PSScriptRoot "Run-Mutation.ps1") -Id $m.Id -TestProj $m.TestProj -File $m.File -Old $m.Old -New $m.New -LogDir $LogDir
    Write-Host $line
    $killed = $line -match 'killed=True'
    $applied = $line -notmatch 'NOT-APPLIED'
    $results += [pscustomobject]@{ Id = $m.Id; Applied = $applied; Killed = $killed; ExpectedKillToday = $m.ExpectedKillToday }
}

Write-Host ""
Write-Host "--- Summary ---"
$killedCount = ($results | Where-Object Killed).Count
$total = $results.Count
Write-Host "$killedCount/$total killed"

foreach ($r in $results) {
    if (-not $r.Applied) { Write-Host "  NOT-APPLIED: $($r.Id) -- generator output shape changed; update the mutation table" -ForegroundColor Yellow; continue }
    if ($r.Killed -ne $r.ExpectedKillToday) {
        $kind = if ($r.Killed) { "IMPROVEMENT (now killed)" } else { "REGRESSION (used to kill, now survives)" }
        $color = if ($r.Killed) { "Green" } else { "Red" }
        Write-Host "  $kind`: $($r.Id)" -ForegroundColor $color
    }
}

exit 0
