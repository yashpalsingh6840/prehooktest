<#
.SYNOPSIS
    Runs SSIS/bin/Development/SSIS.ispac -> LoadEmployees.dtsx, optionally
    overriding one or more $Project:: parameters for this run only.

.DESCRIPTION
    Executes the package in-process via the SSIS 17 managed API
    (Microsoft.SqlServer.ManagedDTS), the same engine devenv/dtexec sit on
    top of. Use this instead of `dtexec /Project ... /Par ...`: dtexec's
    local (/Project) execution mode does not support overriding project
    parameters at all -- /Par is only valid together with /ISServer against
    a package already deployed to an SSISDB catalog. This script gives you
    the same override capability without deploying to a catalog.

.PARAMETER Override
    Zero or more "ParameterName=Value" strings, one per parameter to
    override. Parameter names are the project parameter names as defined in
    SSIS/Project.params (SourceFolder, SourceFileName,
    TargetConnectionString) -- no "$Project::" prefix, no datatype suffix.

.PARAMETER IspacPath
    Path to the built .ispac. Defaults to SSIS/bin/Development/SSIS.ispac
    next to this script.

.PARAMETER PackageName
    Package to run inside the project. Defaults to LoadEmployees.dtsx.

.EXAMPLE
    # run with the defaults baked into Project.params
    .\Run-Package.ps1

.EXAMPLE
    # redirect source file and destination database for this run only
    .\Run-Package.ps1 -Override @(
        'SourceFolder=C:\Drop\NewHires',
        'SourceFileName=NewHires.csv',
        'TargetConnectionString=Data Source=.\SQLFORPOC;Initial Catalog=SsisPoC;Integrated Security=SSPI;Provider=MSOLEDBSQL.1;'
    )
#>
param(
    [string[]] $Override = @(),
    [string]   $IspacPath,
    [string]   $PackageName = 'LoadEmployees.dtsx'
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

$rc = [PkgRun]::Run($IspacPath, $PackageName, $Override)
Write-Host ("`nexit: {0}" -f $rc)
exit $rc
