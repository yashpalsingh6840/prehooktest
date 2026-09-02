<#
.SYNOPSIS
    Adds the missing Microsoft.SqlServer.ScriptTask assembly binding redirect to the
    SSIS 170 (SQL Server 2025) debug host config.

.DESCRIPTION
    The SSIS 2025 preview tooling compiles Script Tasks against the reference assembly
    PublicAssemblies\SSIS\170\Microsoft.SqlServer.ScriptTask.dll (version 17.0.0.0)
    but ships only SSIS\170\Tasks\Microsoft.SqlServer.ScriptTask.dll (version 17.100.0.0)
    at runtime. DtsDebugHost.exe.config for 170 redirects ManagedDTS, DTSRuntimeWrap,
    DTSPipelineWrap, PipelineHost, Controls and DataProfiler - but NOT ScriptTask.

    The result is that any Script Task fails at validation with:
        0xC0010026  The task has failed to load.

    Microsoft's own SSIS 140 config contains exactly these redirects
    (ScriptTask / ScriptTaskUI / VSTAScriptingLib 14.0.0.0 -> 14.100.0.0); this script
    adds the 17.x equivalents that were omitted from the 170 config.

.NOTES
    MUST be run from an ELEVATED PowerShell (the target file lives under Program Files).
    A timestamped .bak of the original config is written next to it.
    Re-running is safe - existing redirects are left alone.

.EXAMPLE
    # In an elevated PowerShell window:
    powershell -ExecutionPolicy Bypass -File "D:\OneDrive\OneDrive - Iris Software Inc\KT\RBC\SSIS\tools\Fix-ScriptTaskBinding.ps1"
#>

$ErrorActionPreference = "Stop"

$config = "D:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\CommonExtensions\Microsoft\SSIS\170\Binn\DtsDebugHost.exe.config"

# Assemblies that need a 17.0.0.0 -> 17.100.0.0 redirect.
#
# The first three mirror the SSIS 140 config. The last three were found by walking the
# full dependency closure of 170\Tasks\Microsoft.SqlServer.ScriptTask.dll: each is
# referenced at 17.0.0.0 but only ships as 17.100.0.0, with no redirect to bridge them.
# Diagnostics.STrace is referenced directly by ScriptTask and is the one that keeps the
# task from loading (the same assembly that broke the SQL Server 2022 retarget attempt).
$needed = @(
    "Microsoft.SqlServer.ScriptTask",
    "Microsoft.SqlServer.ScriptTaskUI",
    "Microsoft.SqlServer.VSTAScriptingLib",
    "Microsoft.SqlServer.Diagnostics.STrace",
    "Microsoft.SqlServer.DtsMsg",
    "Microsoft.SqlServer.Msxml6_interop"
)

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    throw "This script must be run from an ELEVATED PowerShell window (Run as administrator)."
}

if (-not (Test-Path $config)) {
    throw "Config not found: $config"
}

$backup = "$config.$(Get-Date -Format 'yyyyMMdd-HHmmss').bak"
Copy-Item $config $backup
Write-Host "Backed up original to:`n  $backup" -ForegroundColor Cyan

[xml]$doc = Get-Content $config -Raw
$asmNs = "urn:schemas-microsoft-com:asm.v1"

$binding = $doc.configuration.runtime.assemblyBinding
if (-not $binding) { throw "No <assemblyBinding> element found - unexpected config layout." }

$added = 0
foreach ($name in $needed) {

    $exists = @($binding.dependentAssembly | Where-Object {
        $_.assemblyIdentity -and $_.assemblyIdentity.name -eq $name
    }).Count -gt 0

    if ($exists) {
        Write-Host "  already present, skipping : $name" -ForegroundColor DarkGray
        continue
    }

    $dep = $doc.CreateElement("dependentAssembly", $asmNs)

    $ident = $doc.CreateElement("assemblyIdentity", $asmNs)
    $ident.SetAttribute("name", $name)
    $ident.SetAttribute("publicKeyToken", "89845dcd8080cc91")
    $ident.SetAttribute("culture", "neutral")
    [void]$dep.AppendChild($ident)

    $redir = $doc.CreateElement("bindingRedirect", $asmNs)
    $redir.SetAttribute("oldVersion", "17.0.0.0")
    $redir.SetAttribute("newVersion", "17.100.0.0")
    [void]$dep.AppendChild($redir)

    [void]$binding.AppendChild($dep)
    Write-Host "  added redirect 17.0.0.0 -> 17.100.0.0 : $name" -ForegroundColor Green
    $added++
}

if ($added -eq 0) {
    Write-Host "`nNothing to do - all redirects were already present." -ForegroundColor Yellow
} else {
    $doc.Save($config)
    Write-Host "`nWrote $added redirect(s) to:`n  $config" -ForegroundColor Green
    Write-Host "Restart Visual Studio, then run the package again." -ForegroundColor Green
}
