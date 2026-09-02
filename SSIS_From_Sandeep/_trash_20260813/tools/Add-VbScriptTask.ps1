<#
.SYNOPSIS
    Adds the VB.NET Script Task (SCR_NotifyAndLogProgress) to a package.

.DESCRIPTION
    A VB Script Task cannot be produced by flipping Language="VisualBasic" on a cloned C#
    project - LoadFromXML then throws FormatException during base64 decode. VB expects its
    own project layout: "My Project\*.vb" rather than "Properties\*.cs", a .vbproj, and a
    matching descriptor.

    This builds a genuine VB VSTA project from Microsoft's own template, which ships at
      ...\CommonExtensions\Microsoft\SSIS\160\Binn\VSTA_SQL16_IS_ST_VB_Template.vstax
    (a zip), compiles ScriptMain.vb with vbc.exe against the 16.0.0.0 assemblies, and
    embeds both the sources and the assembly.

    Idempotent: re-running detects the task and does nothing.
#>
param(
    [Parameter(Mandatory = $true)][string]$Package,
    [Parameter(Mandatory = $true)][string]$ScriptMainVb,
    [Parameter(Mandatory = $true)][string]$BuildDir,
    [string]$TaskName = "SCR_NotifyAndLogProgress",
    [string]$TemplateVstax = "D:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\CommonExtensions\Microsoft\SSIS\160\Binn\VSTA_SQL16_IS_ST_VB_Template.vstax",
    [string]$After  = "Package\DFT_LoadCustomers",
    [string]$Before = "Package\DFT_BuildSalesSummary"
)
$ErrorActionPreference = "Stop"
$DTS = "www.microsoft.com/SqlServer/Dts"

$xml = New-Object System.Xml.XmlDocument
$xml.PreserveWhitespace = $true
$xml.Load($Package)

foreach ($e in $xml.SelectNodes("//*[local-name()='Executable']")) {
    if ($e.GetAttribute("ObjectName", $DTS) -eq $TaskName) {
        Write-Host "$TaskName already present - skipped"; return
    }
}

$asm = "ST_vb" + [guid]::NewGuid().ToString("N")
New-Item -ItemType Directory -Path $BuildDir -Force | Out-Null

# ---- unpack Microsoft's VB template -------------------------------------------------
$tpl = Join-Path $BuildDir "vbtemplate"
Remove-Item $tpl -Recurse -Force -ErrorAction SilentlyContinue
$zip = Join-Path $BuildDir "vbtemplate.zip"
Copy-Item $TemplateVstax $zip -Force
Expand-Archive -Path $zip -DestinationPath $tpl -Force

function Get-Template([string]$name) {
    $p = Join-Path $tpl $name
    if (-not (Test-Path $p)) { throw "template file missing: $name" }
    $t = [IO.File]::ReadAllText($p)
    $t = $t -replace '\$safeprojectname\$', $asm
    $t = $t -replace '\$projectname\$', $asm
    $t = $t -replace '\$registeredorganization\$', 'RBC Demo'
    $t = $t -replace '\$year\$', (Get-Date).Year
    $t = $t -replace '\$guid1\$', [guid]::NewGuid().ToString()
    $t = $t -replace '\$guid2\$', "{$([guid]::NewGuid())}"
    $t
}

$vbproj      = Get-Template "IS%20Script%20Task%20Project.vbproj"
$assemblyInf = Get-Template "AssemblyInfo.vb"
$resourcesRx = Get-Template "Resources.resx"
$resourcesDs = Get-Template "Resources.Designer.vb"
$settingsSt  = Get-Template "Settings.settings"
$settingsDs  = Get-Template "Settings.Designer.vb"
$scriptMain  = [IO.File]::ReadAllText($ScriptMainVb)

# ---- compile ------------------------------------------------------------------------
$gac = "C:\Windows\Microsoft.NET\assembly"
$refs = @(
    "C:\Program Files\Microsoft SQL Server\160\DTS\Tasks\Microsoft.SqlServer.ScriptTask.dll",
    "$gac\GAC_MSIL\Microsoft.SqlServer.ManagedDTS\v4.0_16.0.0.0__89845dcd8080cc91\Microsoft.SqlServer.ManagedDTS.dll",
    "System.dll", "System.Data.dll", "System.Xml.dll", "System.Core.dll"
) | ForEach-Object { "/r:$_" }

$dll = Join-Path $BuildDir "$asm.dll"
$out = & "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\vbc.exe" `
        /nologo /target:library /platform:x64 "/rootnamespace:$asm" "/out:$dll" `
        $refs $ScriptMainVb 2>&1
if ($LASTEXITCODE -ne 0) { throw "vbc failed:`n$($out -join "`n")" }
Write-Host "compiled $asm.dll ($((Get-Item $dll).Length) bytes)"

# ---- VSTA project descriptor (UTF16LE item) -----------------------------------------
$descriptor = @"
<?xml version="1.0" encoding="UTF-16" standalone="yes"?>
<c:Project xmlns:c="http://schemas.microsoft.com/codeprojectml/2010/08/main" xmlns:msb="http://schemas.microsoft.com/developer/msbuild/2003" runtimeVersion="4.0" schemaVersion="1.0">
  <msb:PropertyGroup>
    <msb:CodeName>$asm</msb:CodeName>
    <msb:Language>msBuild</msb:Language>
    <msb:DisplayName>$asm</msb:DisplayName>
    <msb:ProjectId>{$([guid]::NewGuid())}</msb:ProjectId>
  </msb:PropertyGroup>
  <msb:ItemGroup>
    <msb:Project Include="$asm.vbproj" />
    <msb:File Include="My Project\Resources.resx" />
    <msb:File Include="My Project\Settings.settings" />
    <msb:File Include="My Project\AssemblyInfo.vb" />
    <msb:File Include="My Project\Resources.Designer.vb" />
    <msb:File Include="My Project\Settings.Designer.vb" />
    <msb:File Include="ScriptMain.vb" />
  </msb:ItemGroup>
</c:Project>
"@

# ---- build the task node -------------------------------------------------------------
# Item order and the "My Project\" prefix mirror what VSTA itself emits for VB.
$items = @(
    @{ n = "My Project\Resources.resx";        e = "UTF8";    c = $resourcesRx },
    @{ n = "My Project\Settings.Designer.vb";  e = "UTF8";    c = $settingsDs  },
    @{ n = "$asm.vbproj";                      e = "UTF8";    c = $vbproj      },
    @{ n = "ScriptMain.vb";                    e = "UTF8";    c = $scriptMain  },
    # TrimStart matters: a PowerShell here-string begins with a newline, and an XML
    # declaration is only legal at the very first character. A leading blank line makes the
    # descriptor unparseable and LoadFromXML then fails with a misleading
    # "not a valid Base-64 string" FormatException.
    @{ n = "Project";                          e = "UTF16LE"; c = $descriptor.TrimStart() },
    @{ n = "My Project\AssemblyInfo.vb";       e = "UTF8";    c = $assemblyInf },
    @{ n = "My Project\Settings.settings";     e = "UTF8";    c = $settingsSt  },
    @{ n = "My Project\Resources.Designer.vb"; e = "UTF8";    c = $resourcesDs }
)

$taskId = "{$([guid]::NewGuid())}"
$exec = $xml.CreateElement("DTS:Executable", $DTS)
$exec.SetAttribute("refId", $DTS, "Package\$TaskName")
$exec.SetAttribute("CreationName", $DTS, "Microsoft.ScriptTask")
$exec.SetAttribute("Description", $DTS, "VB.NET Script Task - logs staging progress between the two data flows")
$exec.SetAttribute("DTSID", $DTS, $taskId)
$exec.SetAttribute("ExecutableType", $DTS, "Microsoft.ScriptTask")
$exec.SetAttribute("LocaleID", $DTS, "-1")
$exec.SetAttribute("ObjectName", $DTS, $TaskName)
$exec.SetAttribute("ThreadHint", $DTS, "0")

$od = $xml.CreateElement("DTS:ObjectData", $DTS)
$sp = $xml.CreateElement("ScriptProject")
$sp.SetAttribute("Name", $asm)
$sp.SetAttribute("VSTAMajorVersion", "16")
$sp.SetAttribute("VSTAMinorVersion", "0")
$sp.SetAttribute("Language", "VisualBasic")
$sp.SetAttribute("ReadOnlyVariables", "System::PackageName")
$sp.SetAttribute("ReadWriteVariables", "User::BatchStartTime,User::RowsLoaded")

foreach ($it in $items) {
    $pi = $xml.CreateElement("ProjectItem")
    $pi.SetAttribute("Name", $it.n)
    $pi.SetAttribute("Encoding", $it.e)
    [void]$pi.AppendChild($xml.CreateCDataSection($it.c))
    [void]$sp.AppendChild($pi)
}
$bi = $xml.CreateElement("BinaryItem")
$bi.SetAttribute("Name", "$asm.dll")
[void]$bi.AppendChild($xml.CreateCDataSection([Convert]::ToBase64String([IO.File]::ReadAllBytes($dll))))
[void]$sp.AppendChild($bi)

[void]$od.AppendChild($sp)
[void]$exec.AppendChild($od)
[void]$xml.DocumentElement.SelectNodes("*[local-name()='Executables']")[0].AppendChild($exec)

# ---- splice into the control flow: After -> task -> Before ---------------------------
$pcNode = $xml.DocumentElement.SelectNodes("*[local-name()='PrecedenceConstraints']")[0]
$rewired = $false
foreach ($pc in @($pcNode.ChildNodes | Where-Object { $_.NodeType -eq [System.Xml.XmlNodeType]::Element })) {
    if ($pc.GetAttribute("From", $DTS) -eq $After -and $pc.GetAttribute("To", $DTS) -eq $Before) {
        $pc.SetAttribute("To", $DTS, "Package\$TaskName"); $rewired = $true
    }
}
if (-not $rewired) { throw "no precedence constraint $After -> $Before to rewire" }

$pc2 = $xml.CreateElement("DTS:PrecedenceConstraint", $DTS)
$pc2.SetAttribute("refId", $DTS, "Package.PrecedenceConstraints[PC_${TaskName}_to_Summary]")
$pc2.SetAttribute("CreationName", $DTS, "")
$pc2.SetAttribute("DTSID", $DTS, "{$([guid]::NewGuid())}")
$pc2.SetAttribute("From", $DTS, "Package\$TaskName")
$pc2.SetAttribute("LogicalAnd", $DTS, "True")
$pc2.SetAttribute("ObjectName", $DTS, "PC_${TaskName}_to_Summary")
$pc2.SetAttribute("To", $DTS, $Before)
[void]$pcNode.AppendChild($pc2)

$xml.Save($Package)
Write-Host "added $TaskName (VB.NET, VSTA 16.0) between $After and $Before"
