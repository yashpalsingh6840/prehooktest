# Grafts the C# Script Task from the .bak into a standalone package, retargeting its VSTA
# script from 17.0 to 16.0 and swapping in a freshly compiled assembly.
param(
    [Parameter(Mandatory = $true)][string]$Package,
    [Parameter(Mandatory = $true)][string]$Source,   # package holding the Script Task
    [Parameter(Mandatory = $true)][string]$Dll,
    [Parameter(Mandatory = $true)][string]$ScriptMainCs
)
$ErrorActionPreference = "Stop"
$DTS = "www.microsoft.com/SqlServer/Dts"

$src = New-Object System.Xml.XmlDocument; $src.PreserveWhitespace = $true; $src.Load($Source)
$dst = New-Object System.Xml.XmlDocument; $dst.PreserveWhitespace = $true; $dst.Load($Package)

# ---- find the Script Task executable in the source package
$taskNode = $null
foreach ($e in $src.SelectNodes("//*[local-name()='Executable']")) {
    if ($e.GetAttribute("ExecutableType", $DTS) -like "*ScriptTask*") { $taskNode = $e; break }
}
if (-not $taskNode) { throw "no Script Task executable found in $Source" }
Write-Host "found task: $($taskNode.GetAttribute('ObjectName', $DTS))  type=$($taskNode.GetAttribute('ExecutableType', $DTS))"

# ---- import into the destination document
$imported = $dst.ImportNode($taskNode, $true)

# ---- retarget VSTA 17.0 -> 16.0 and swap source + binary
$sp = $imported.SelectNodes(".//*[local-name()='ScriptProject']")[0]
if (-not $sp) { throw "ScriptProject node missing" }
$sp.SetAttribute("VSTAMajorVersion", "16")
$sp.SetAttribute("VSTAMinorVersion", "0")

$newSource = [IO.File]::ReadAllText($ScriptMainCs)
$b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($Dll))
$didSrc = $false; $didBin = $false
foreach ($ch in @($sp.ChildNodes)) {
    if ($ch.LocalName -eq "ProjectItem" -and $ch.Name -eq "ScriptMain.cs") {
        $ch.RemoveAll() | Out-Null
        $ch.SetAttribute("Name", "ScriptMain.cs"); $ch.SetAttribute("Encoding", "UTF8")
        [void]$ch.AppendChild($dst.CreateCDataSection($newSource)); $didSrc = $true
    }
    if ($ch.LocalName -eq "BinaryItem") {
        $nm = $ch.Name
        $ch.RemoveAll() | Out-Null
        $ch.SetAttribute("Name", $nm)
        [void]$ch.AppendChild($dst.CreateCDataSection($b64)); $didBin = $true
    }
}
if (-not ($didSrc -and $didBin)) { throw "source swapped=$didSrc binary swapped=$didBin" }

# ---- attach to the destination package
$pkgExec = $dst.DocumentElement
$execsNode = $pkgExec.SelectNodes("*[local-name()='Executables']")[0]
if (-not $execsNode) { throw "no Executables node in destination" }
[void]$execsNode.AppendChild($imported)

# ---- run it before DFT_LoadCustomers
$pcNode = $pkgExec.SelectNodes("*[local-name()='PrecedenceConstraints']")[0]
if (-not $pcNode) {
    $pcNode = $dst.CreateElement("DTS:PrecedenceConstraints", $DTS)
    [void]$pkgExec.AppendChild($pcNode)
}
$taskName = $imported.GetAttribute("ObjectName", $DTS)
# NOTE: XmlElement.SetAttribute takes (localName, namespaceURI, value) - not (name, value, ns).
$pc = $dst.CreateElement("DTS:PrecedenceConstraint", $DTS)
$pc.SetAttribute("refId", $DTS, "Package.PrecedenceConstraints[PC_ScriptTask_to_Load]")
$pc.SetAttribute("CreationName", $DTS, "")
$pc.SetAttribute("DTSID", $DTS, "{$([guid]::NewGuid())}")
$pc.SetAttribute("From", $DTS, "Package\$taskName")
$pc.SetAttribute("LogicalAnd", $DTS, "True")
$pc.SetAttribute("ObjectName", $DTS, "PC_ScriptTask_to_Load")
$pc.SetAttribute("To", $DTS, "Package\DFT_LoadCustomers")
[void]$pcNode.AppendChild($pc)

$dst.Save($Package)
Write-Host "grafted '$taskName' at VSTA 16.0, assembly $([Math]::Round($b64.Length/1KB,1))KB base64, constrained before DFT_LoadCustomers"
