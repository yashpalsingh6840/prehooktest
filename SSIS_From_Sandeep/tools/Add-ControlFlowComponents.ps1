<#
.SYNOPSIS
    Adds the missing CONTROL-FLOW components to Package.dtsx.

.DESCRIPTION
    Closes three gaps found by comparing against the N600-ProdProc SSIS analysis:

      1. Execute SQL Task  - the single most common task in the production suite
                             (present in nearly all 44 packages, absent here).
                             Also fixes a real bug: with no truncate step the
                             package APPENDS on every re-run.
      2. Failure precedence - production CUVImport has an "Error - File is out of
                             date" alternate path; this package was Success-only.
      3. Expression precedence - constraint guarded by both a result and an
                             expression (DTSPrecedenceEvalOp.ExpressionAndConstraint).

    Idempotent: re-running makes no further changes.
#>
[CmdletBinding()]
param(
    [string]$Package = "$PSScriptRoot\..\RBC_Demo_ETL\RBC_Demo_ETL\Package.dtsx"
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\_LoadSsisBuilder.ps1"

$pkgPath = (Resolve-Path $Package).Path
$pkg = [SsisBuild.Ssis]::Load($pkgPath)

if ($pkg.Executables.Contains('SQL_TruncateTargets')) {
    Write-Host "already applied - no changes" -ForegroundColor Yellow
    return
}
Copy-Item $pkgPath "$pkgPath.bak_beforecontrolflow" -Force
Write-Host "backup -> Package.dtsx.bak_beforecontrolflow"

$cmSql = $pkg.Connections['CM_SQL_SSISDemo']

# --- 1. Execute SQL Task: reset targets ---------------------------------
$truncSql = @'
TRUNCATE TABLE dbo.StagingCustomers;
TRUNCATE TABLE dbo.SalesSummary;
TRUNCATE TABLE dbo.StagingCustomers_Errors;
DELETE FROM dbo.EtlAuditLog;
'@
$trunc = [SsisBuild.Ssis]::ExecSql($pkg, 'SQL_TruncateTargets', $cmSql, $truncSql)
$trunc.Description = 'Resets all target tables so the package is re-runnable'
Write-Host "added Execute SQL Task 'SQL_TruncateTargets'"

# --- 2. Execute SQL Task on the FAILURE path ----------------------------
# Parameterless insert so no parameter binding is required.
$failSql = @'
INSERT INTO dbo.EtlRunLog (PackageName, SourceName, EventType, MessageText)
VALUES (N'RBC_Demo_ETL', N'DFT_LoadCustomers', N'OnFailure',
        N'Customer load failed - staging may be incomplete.');
'@
$fail = [SsisBuild.Ssis]::ExecSql($pkg, 'SQL_LogLoadFailure', $cmSql, $failSql)
$fail.Description = 'Runs only when DFT_LoadCustomers fails (Failure constraint)'
Write-Host "added Execute SQL Task 'SQL_LogLoadFailure'"

# --- 3. rewire the control flow -----------------------------------------
# Currently:  SCR_ValidateAndLogStart -> DFT_LoadCustomers -> SCR_Notify -> DFT_BuildSalesSummary
# Wanted:     SQL_TruncateTargets -> SCR_ValidateAndLogStart -> ... (unchanged)
#             DFT_LoadCustomers --Failure--> SQL_LogLoadFailure
$validate = $pkg.Executables['SCR_ValidateAndLogStart']
$load     = $pkg.Executables['DFT_LoadCustomers']
$summary  = $pkg.Executables['DFT_BuildSalesSummary']

$null = [SsisBuild.Ssis]::Link($pkg, $trunc, $validate,
            [Microsoft.SqlServer.Dts.Runtime.DTSExecResult]::Success)
Write-Host "wired   SQL_TruncateTargets -> SCR_ValidateAndLogStart (Success)"

$null = [SsisBuild.Ssis]::Link($pkg, $load, $fail,
            [Microsoft.SqlServer.Dts.Runtime.DTSExecResult]::Failure)
Write-Host "wired   DFT_LoadCustomers -> SQL_LogLoadFailure (FAILURE)"

# --- 4. expression + constraint on the summary step ---------------------
# Only build the summary when the VB task actually reported staged rows.
$notify = $pkg.Executables['SCR_NotifyAndLogProgress']
$old = @($pkg.PrecedenceConstraints | Where-Object {
    $_.PrecedenceExecutable.GetType().GetProperty('Name').GetValue($_.PrecedenceExecutable) -eq 'SCR_NotifyAndLogProgress' -and
    $_.ConstrainedExecutable.GetType().GetProperty('Name').GetValue($_.ConstrainedExecutable) -eq 'DFT_BuildSalesSummary' })
foreach ($c in $old) { $pkg.PrecedenceConstraints.Remove($c) }
$null = [SsisBuild.Ssis]::LinkExpr($pkg, $notify, $summary,
            [Microsoft.SqlServer.Dts.Runtime.DTSExecResult]::Success,
            '@[User::RowsLoaded] > 0')
Write-Host "wired   SCR_NotifyAndLogProgress -> DFT_BuildSalesSummary (Success AND @RowsLoaded > 0)"

[SsisBuild.Ssis]::Save($pkg, $pkgPath)
Write-Host "saved $pkgPath" -ForegroundColor Green

# --- verify by reloading -------------------------------------------------
$v = [SsisBuild.Ssis]::Load($pkgPath)
Write-Host ""
Write-Host "executables now:" -ForegroundColor Cyan
foreach ($e in $v.Executables) { "   " + $e.GetType().GetProperty('Name').GetValue($e) }
Write-Host "precedence constraints:" -ForegroundColor Cyan
foreach ($c in $v.PrecedenceConstraints) {
    $f = $c.PrecedenceExecutable.GetType().GetProperty('Name').GetValue($c.PrecedenceExecutable)
    $t = $c.ConstrainedExecutable.GetType().GetProperty('Name').GetValue($c.ConstrainedExecutable)
    $x = if ($c.Expression) { "  [$($c.Expression)]" } else { '' }
    "   $f -> $t ($($c.Value))$x"
}
