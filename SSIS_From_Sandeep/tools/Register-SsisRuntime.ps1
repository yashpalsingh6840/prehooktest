<#
.SYNOPSIS
    Registers the SSIS 170 native COM DLLs that ship with the Visual Studio 2022
    "SQL Server Integration Services Projects" extension.

.DESCRIPTION
    The VS extension ships the SSIS runtime binaries but does NOT register their COM
    classes - registration normally happens when the Integration Services *product*
    feature is installed via SQL Server setup. Without it:

      * managed tasks (Script Task, Execute SQL Task, ...) fail with
            0xC0010026  The task has failed to load.
      * managed pipeline components (Script Component) fail with
            0xC0048021  The component is missing, not registered, ...
      * dtexec fails with
            0x80040154  Could not create DTS.Application  (REGDB_E_CLASSNOTREG)

    Native pipeline components (Flat File Source, OLE DB Source/Destination) are
    unaffected, which is why data flows already work.

    SAFETY: SSIS uses version-specific CLSIDs. The 170 CLSIDs were verified to be
    disjoint from the SQL Server 2016 (130) CLSIDs already registered on this machine,
    so this is additive - it does not repoint or remove the existing 130 registration
    used by SSMS.

    UNSUPPORTED: Microsoft ships these binaries as design-time tooling. Registering them
    manually is not a supported configuration. The supported fix is to install the
    Integration Services feature from SQL Server setup.

.NOTES
    Run from an ELEVATED PowerShell.
    To undo:  re-run with  -Unregister

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Register-SsisRuntime.ps1 -WhatIf
    powershell -ExecutionPolicy Bypass -File .\Register-SsisRuntime.ps1
    powershell -ExecutionPolicy Bypass -File .\Register-SsisRuntime.ps1 -Unregister
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$Unregister,
    [string]$Binn = "D:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\CommonExtensions\Microsoft\SSIS\170\Binn"
)

$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    throw "Run this from an ELEVATED PowerShell window (Run as administrator)."
}
if (-not (Test-Path $Binn)) { throw "Binn folder not found: $Binn" }

# CLSIDs we expect to appear once registration succeeds.
$verify = @{
    "DTS.Application (dtexec / managed API)" = "{F5D3682F-B8AF-4AE5-A7E6-5FFDFF992D4F}"
    "ManagedComponentHost (Script Component)" = "{27BDEB67-60BE-4360-AC7F-AAED6FFE4CB1}"
}

function Test-Registered {
    param([string]$Clsid)
    foreach ($p in @("HKLM:\SOFTWARE\Classes\CLSID\$Clsid", "HKLM:\SOFTWARE\Classes\Wow6432Node\CLSID\$Clsid")) {
        if (Test-Path $p) { return $true }
    }
    return $false
}

Write-Host "=== BEFORE ===" -ForegroundColor Cyan
foreach ($k in $verify.Keys) { "  {0,-42} {1}" -f $k, $(if (Test-Registered $verify[$k]) { "registered" } else { "NOT registered" }) }

# Only DLLs that actually export DllRegisterServer can be regsvr32'd.
Write-Host "`n=== self-registrable DLLs found in 170\Binn ===" -ForegroundColor Cyan
$targets = @()
foreach ($dll in (Get-ChildItem $Binn -Filter *.dll)) {
    $ascii = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($dll.FullName))
    if ($ascii.Contains("DllRegisterServer")) { $targets += $dll; "  $($dll.Name)" }
}
if ($targets.Count -eq 0) { throw "No self-registrable DLLs found - nothing to do." }

$verb = if ($Unregister) { "Unregistering" } else { "Registering" }
Write-Host "`n=== $verb $($targets.Count) DLL(s) ===" -ForegroundColor Cyan

$ok = 0; $failed = @()
foreach ($dll in $targets) {
    if (-not $PSCmdlet.ShouldProcess($dll.Name, $verb)) { continue }
    $args = @("/s")
    if ($Unregister) { $args += "/u" }
    $args += "`"$($dll.FullName)`""
    $p = Start-Process regsvr32.exe -ArgumentList $args -Wait -PassThru -WindowStyle Hidden
    if ($p.ExitCode -eq 0) { $ok++; Write-Host "  OK      $($dll.Name)" -ForegroundColor Green }
    else { $failed += "$($dll.Name) (exit $($p.ExitCode))"; Write-Host "  FAILED  $($dll.Name)  exit=$($p.ExitCode)" -ForegroundColor Yellow }
}

Write-Host "`n=== AFTER ===" -ForegroundColor Cyan
foreach ($k in $verify.Keys) { "  {0,-42} {1}" -f $k, $(if (Test-Registered $verify[$k]) { "registered" } else { "NOT registered" }) }

Write-Host "`nSucceeded: $ok   Failed: $($failed.Count)"
if ($failed) { $failed | ForEach-Object { "  $_" } }
if (-not $Unregister) {
    Write-Host "`nRestart Visual Studio, then run the package again." -ForegroundColor Green
    Write-Host "To undo:  powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Unregister" -ForegroundColor DarkGray
}
