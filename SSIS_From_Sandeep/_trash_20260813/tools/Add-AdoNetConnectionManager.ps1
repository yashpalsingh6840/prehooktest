# Adds an ADO.NET connection manager. A Script Task cannot use a native OLE DB connection
# manager: AcquireConnection hands back a COM object that will not cast to OleDbConnection,
# and ConnectionString omits the write-only Password property. An ADO.NET connection
# manager returns a real managed SqlConnection instead.
param(
    [Parameter(Mandatory = $true)][string]$Package,
    [string]$Name = "CM_ADO_SSISDemo",
    [string]$ConnectionString = "Data Source=localhost,1433;Initial Catalog=SSISDemo;User ID=sa;Password=YourStrong!Passw0rd123;Encrypt=False;"
)
$ErrorActionPreference = "Stop"
$DTS = "www.microsoft.com/SqlServer/Dts"

$xml = New-Object System.Xml.XmlDocument; $xml.PreserveWhitespace = $true; $xml.Load($Package)
$cms = $xml.DocumentElement.SelectNodes("*[local-name()='ConnectionManagers']")[0]
if (-not $cms) { throw "no ConnectionManagers node" }

# PreserveWhitespace means ChildNodes includes XmlWhitespace - filter to elements
foreach ($c in @($cms.ChildNodes | Where-Object { $_.NodeType -eq [System.Xml.XmlNodeType]::Element })) {
    if ($c.GetAttribute("ObjectName", $DTS) -eq $Name) { Write-Host "$Name already present"; return }
}

$cm = $xml.CreateElement("DTS:ConnectionManager", $DTS)
$cm.SetAttribute("refId", $DTS, "Package.ConnectionManagers[$Name]")
$cm.SetAttribute("CreationName", $DTS, "ADO.NET:System.Data.SqlClient.SqlConnection, System.Data, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089")
$cm.SetAttribute("DTSID", $DTS, "{$([guid]::NewGuid())}")
$cm.SetAttribute("ObjectName", $DTS, $Name)

$od = $xml.CreateElement("DTS:ObjectData", $DTS)
$inner = $xml.CreateElement("DTS:ConnectionManager", $DTS)
$inner.SetAttribute("ConnectionString", $DTS, $ConnectionString)
[void]$od.AppendChild($inner)
[void]$cm.AppendChild($od)
[void]$cms.AppendChild($cm)

$xml.Save($Package)
Write-Host "added ADO.NET connection manager '$Name'"
