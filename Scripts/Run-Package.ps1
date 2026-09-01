<#
.SYNOPSIS
    Runs SSIS/bin/Development/SSIS.ispac -> LoadEmployees.dtsx and
    LoadReferenceData.dtsx (both, by default), optionally overriding one or
    more $Project::/$Package:: parameters for this run only.

.DESCRIPTION
    Executes the packages in-process via the SSIS 17 managed API
    (Microsoft.SqlServer.ManagedDTS), the same engine devenv/dtexec sit on
    top of. Use this instead of `dtexec /Project ... /Par ...`: dtexec's
    local (/Project) execution mode does not support overriding project
    parameters at all -- /Par is only valid together with /ISServer against
    a package already deployed to an SSISDB catalog. This script gives you
    the same override capability without deploying to a catalog.

    Packages run in sequence, each in its own try/catch (see PkgRun.cs) --
    one package failing to launch does not stop the rest of the batch. As of
    2026-08-13, LoadEmployees is EXPECTED to fail here: its CM_TargetDb
    password only exists inside SSISDB's encrypted environment variable
    store (see CLAUDE.md "Sensitive credential"), so a local run reports
    "Login failed for user 'ssis_loader'" and the script still continues on
    to LoadReferenceData. Exit code is non-zero whenever any package in the
    batch didn't succeed.

    Each run writes a per-package text log under
    SSIS/bin/Development/logs/<PackageName>.log via the native SSIS log
    provider (includes pipeline row counts), in addition to the console
    output.

.PARAMETER Override
    Zero or more "ParameterName=Value" strings, one per parameter to
    override, applied to every package in the batch. Matched against the
    package's own parameters first, then the project's -- e.g.
    TargetConnectionString (project-scoped) applies to both packages,
    SourceFileName (project-scoped, LoadEmployees-only) or
    DepartmentFileName/DesignationFileName (package-scoped, LoadReferenceData)
    only apply where they exist; a name matching neither is a warning, not
    an error.

.PARAMETER IspacPath
    Path to the built .ispac. Defaults to SSIS/bin/Development/SSIS.ispac
    next to this script.

.PARAMETER PackageName
    Package(s) to run inside the project. Defaults to both
    LoadEmployees.dtsx and LoadReferenceData.dtsx.

.EXAMPLE
    # run both packages with the defaults baked into Project.params
    .\Run-Package.ps1

.EXAMPLE
    # just one package
    .\Run-Package.ps1 -PackageName LoadReferenceData.dtsx

.EXAMPLE
    # redirect destination database for this run only, both packages
    .\Run-Package.ps1 -Override @(
        'TargetConnectionString=Data Source=.\SQLFORPOC_2022;Initial Catalog=SsisPoC;Integrated Security=SSPI;Provider=MSOLEDBSQL.1;'
    )
#>
param(
    [string[]] $Override = @(),
    [string]   $IspacPath,
    [string[]] $PackageName = @('LoadEmployees.dtsx', 'LoadReferenceData.dtsx')
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $IspacPath) {
    $IspacPath = Join-Path $here '..\SSIS\bin\Development\SSIS.ispac'
}
$IspacPath = [System.IO.Path]::GetFullPath($IspacPath)
if (-not (Test-Path $IspacPath)) {
    throw "ispac not found at '$IspacPath'. Build the project first (devenv SSIS.sln /Build Development)."
}

$refs = @()
foreach ($n in 'Microsoft.SqlServer.ManagedDTS','Microsoft.SqlServer.DTSPipelineWrap','Microsoft.SqlServer.DTSRuntimeWrap') {
    $asm = [System.Reflection.Assembly]::LoadWithPartialName($n)
    if ($null -eq $asm) { throw "Required SSIS assembly '$n' is not installed on this machine." }
    $refs += $asm.Location
}
Add-Type -TypeDefinition (Get-Content (Join-Path $here 'PkgRun.cs') -Raw) -ReferencedAssemblies $refs -Language CSharp

$rc = [PkgRun]::RunAll($IspacPath, $PackageName, $Override)
Write-Host ("`nexit: {0}" -f $rc)
exit $rc
