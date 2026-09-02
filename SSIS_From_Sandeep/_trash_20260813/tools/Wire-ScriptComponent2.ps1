# Inserts SCR_ComputeCustomerTier between OLESRC_OrdersAndDetails and OLEDST_SalesSummary
# in DFT_BuildSalesSummary, embedding a compiled VSTA assembly.
param(
    [Parameter(Mandatory = $true)][string]$Package,
    [Parameter(Mandatory = $true)][string]$Dll,
    [Parameter(Mandatory = $true)][string]$AssemblyName
)
$ErrorActionPreference = "Stop"

$DFT  = "Package\DFT_BuildSalesSummary"
$SRC  = "$DFT\OLESRC_OrdersAndDetails"
$DST  = "$DFT\OLEDST_SalesSummary"
$SCR  = "$DFT\SCR_ComputeCustomerTier"
$SOUT = "$SRC.Outputs[OLE DB Source Output]"
$IN   = "$SCR.Inputs[Input 0]"
$OUT  = "$SCR.Outputs[Output 0]"
$DIN  = "$DST.Inputs[OLE DB Destination Input]"

# input columns, in the order the BufferWrapper indexes them (0..5)
$inputCols = @(
    @{ n = "CustomerID";    t = "i4"     },
    @{ n = "FullName";      t = "wstr"; len = 200 },
    @{ n = "Email";         t = "wstr"; len = 200 },
    @{ n = "TotalOrders";   t = "i4"     },
    @{ n = "TotalRevenue";  t = "r8"     },
    @{ n = "LastOrderDate"; t = "dbDate" }
)
# added output columns -> BufferWrapper indexes 6..7
$outputCols = @(
    @{ n = "CustomerTier";      t = "wstr"; len = 20 },
    @{ n = "ProcessedDateTime"; t = "dbTimeStamp"    }
)

$xml = New-Object System.Xml.XmlDocument
$xml.PreserveWhitespace = $true
$xml.Load($Package)

function Get-Node([string]$xpath, [string]$refId) {
    foreach ($n in $xml.SelectNodes($xpath)) { if ($n.refId -eq $refId) { return $n } }
    throw "not found: $refId"
}

$dst = Get-Node "//component" $DST
$components = $dst.ParentNode
$pipeline = $components.ParentNode

# ------------------------------------------------------------ build the component
$b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($Dll))
$contact = "Includes and runs custom script code.;Microsoft Corporation; Microsoft SQL Server; Microsoft Corporation; All Rights Reserved; http://www.microsoft.com/sql/support;13"

# SourceCode is stored as (fileName, encoding, content) TRIPLETS - confirmed by the
# XPath literal arrayElement[(position() mod 3) = 2] in PackageFormatUpdate.dll and by
# the 30-element (10-file) array the designer produced for SCR_CleanseCustomerRow.
# Without it the component fails validation with "The script component includes no code."
$srcDir = Split-Path $Dll -Parent
$srcFiles = @("ComponentWrapper.cs", "BufferWrapper.cs", "main.cs")
$srcXml = ($srcFiles | ForEach-Object {
    $content = [IO.File]::ReadAllText((Join-Path $srcDir $_))
    "<arrayElement dataType=`"System.String`"><![CDATA[$_]]></arrayElement>" +
    "<arrayElement dataType=`"System.String`"><![CDATA[UTF8]]></arrayElement>" +
    "<arrayElement dataType=`"System.String`"><![CDATA[$content]]></arrayElement>"
}) -join ""
$srcCount = $srcFiles.Count * 3

$inColXml = ($inputCols | ForEach-Object {
    $len = if ($_.len) { " cachedLength=`"$($_.len)`"" } else { "" }
    "<inputColumn refId=`"$IN.Columns[$($_.n)]`" cachedDataType=`"$($_.t)`"$len cachedName=`"$($_.n)`" lineageId=`"$SOUT.Columns[$($_.n)]`" usageType=`"readOnly`" />"
}) -join ""

$outColXml = ($outputCols | ForEach-Object {
    $len = if ($_.len) { " length=`"$($_.len)`"" } else { "" }
    "<outputColumn refId=`"$OUT.Columns[$($_.n)]`" dataType=`"$($_.t)`"$len errorRowDisposition=`"NotUsed`" truncationRowDisposition=`"NotUsed`" lineageId=`"$OUT.Columns[$($_.n)]`" name=`"$($_.n)`" />"
}) -join ""

$compXml = @"
<component refId="$SCR" componentClassID="Microsoft.ManagedComponentHost" contactInfo="$contact" description="Includes and runs custom script code." name="SCR_ComputeCustomerTier" version="13">
<properties>
<property dataType="System.String" description="Stores the source code of the component" isArray="true" name="SourceCode" state="cdata"><arrayElements arrayElementCount="$srcCount">$srcXml</arrayElements></property>
<property dataType="System.String" description="Stores the binary representation of the component" isArray="true" name="BinaryCode" state="cdata"><arrayElements arrayElementCount="2"><arrayElement><![CDATA[$AssemblyName]]></arrayElement><arrayElement><![CDATA[$b64]]></arrayElement></arrayElements></property>
<property dataType="System.String" description="Specifies the name of the Microsoft Visual Studio Tools for Applications project." name="VSTAProjectName" typeConverter="NOTBROWSABLE">$AssemblyName</property>
<property dataType="System.String" description="Specifies the programming language used by the script." name="ScriptLanguage">CSharp</property>
<property dataType="System.String" description="Specifies a comma-separated list of read-only variables." name="ReadOnlyVariables"></property>
<property dataType="System.String" description="Specifies a comma-separated list of read/write variables." name="ReadWriteVariables"></property>
<property dataType="System.String" description="List of breakpoints present on the script." isArray="true" name="BreakpointCollection" typeConverter="NOTBROWSABLE"><arrayElements arrayElementCount="0" /></property>
<property dataType="System.String" name="UserComponentTypeName">Microsoft.ScriptComponentHost</property>
</properties>
<inputs>
<input refId="$IN" name="Input 0"><inputColumns>$inColXml</inputColumns><externalMetadataColumns />
</input>
</inputs>
<outputs>
<output refId="$OUT" name="Output 0" synchronousInputId="$IN"><outputColumns>$outColXml</outputColumns><externalMetadataColumns />
</output>
</outputs>
</component>
"@

$frag = $xml.CreateDocumentFragment()
$frag.InnerXml = $compXml
[void]$components.AppendChild($frag)

# --------------------------------------------------------------- rewire the paths
$paths = $pipeline.SelectSingleNode("paths")
$rewired = $false
foreach ($p in $paths.SelectNodes("path")) {
    if ($p.startId -eq $SOUT -and $p.endId -eq $DIN) { $p.SetAttribute("endId", $IN); $rewired = $true }
}
if (-not $rewired) { throw "could not find the OLESRC -> OLEDST path to rewire" }

$np = $xml.CreateElement("path")
$np.SetAttribute("refId", "$DFT.Paths[Tier Output]")
$np.SetAttribute("startId", $OUT)
$np.SetAttribute("endId", $DIN)
$np.SetAttribute("name", "Tier Output")
[void]$paths.AppendChild($np)

# ------------------------------------------------- destination: two new columns
$dinput = $dst.SelectSingleNode("inputs/input")
$dExt = $dinput.SelectSingleNode("externalMetadataColumns")
$dIn  = $dinput.SelectSingleNode("inputColumns")

$destNew = @(
    @{ n = "CustomerTier";      ext = "wstr";         len = 20; cached = "wstr" },
    @{ n = "ProcessedDateTime"; ext = "dbTimeStamp2"; scale = 7; cached = "dbTimeStamp" }
)
foreach ($c in $destNew) {
    $extRef = "$DIN.ExternalColumns[$($c.n)]"
    $e = $xml.CreateElement("externalMetadataColumn")
    $e.SetAttribute("refId", $extRef)
    $e.SetAttribute("dataType", $c.ext)
    if ($c.len)   { $e.SetAttribute("length", "$($c.len)") }
    if ($c.scale) { $e.SetAttribute("scale", "$($c.scale)") }
    $e.SetAttribute("name", $c.n)
    [void]$dExt.AppendChild($e)

    $i = $xml.CreateElement("inputColumn")
    $i.SetAttribute("refId", "$DIN.Columns[$($c.n)]")
    $i.SetAttribute("cachedDataType", $c.cached)
    if ($c.len) { $i.SetAttribute("cachedLength", "$($c.len)") }
    $i.SetAttribute("cachedName", $c.n)
    $i.SetAttribute("externalMetadataColumnId", $extRef)
    $i.SetAttribute("lineageId", "$OUT.Columns[$($c.n)]")
    [void]$dIn.AppendChild($i)
}

$xml.Save($Package)
Write-Host "inserted SCR_ComputeCustomerTier: $($inputCols.Count) in, $($outputCols.Count) out, path rewired, $($destNew.Count) destination mappings"
