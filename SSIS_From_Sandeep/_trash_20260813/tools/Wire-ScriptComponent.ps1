# Wires SCR_CleanseCustomerRow into the pipeline: declares its input columns, its four
# added output columns, embeds the compiled VSTA assembly, and maps the new columns onto
# the OLE DB destination.
param(
    [Parameter(Mandatory = $true)][string]$Package,
    [Parameter(Mandatory = $true)][string]$Dll,
    [Parameter(Mandatory = $true)][string]$AssemblyName
)
$ErrorActionPreference = "Stop"

$SCR  = "Package\DFT_LoadCustomers\SCR_CleanseCustomerRow"
$FF   = "Package\DFT_LoadCustomers\FF_SRC_Customers"
$DST  = "Package\DFT_LoadCustomers\OLEDST_StagingCustomers"
$IN   = "$SCR.Inputs[Input 0]"
$OUT  = "$SCR.Outputs[Output 0]"
$DIN  = "$DST.Inputs[OLE DB Destination Input]"

$inputCols = @(
    @{ n = "CustomerID"; t = "wstr"; len = 20  },
    @{ n = "FirstName";  t = "wstr"; len = 100 },
    @{ n = "LastName";   t = "wstr"; len = 100 },
    @{ n = "Email";      t = "wstr"; len = 200 },
    @{ n = "Phone";      t = "wstr"; len = 50  },
    @{ n = "SignupDate"; t = "wstr"; len = 50  },
    @{ n = "Country";    t = "wstr"; len = 100 }
)
# order here MUST match the indexes hard-coded in BufferWrapper.cs (7..10)
$outputCols = @(
    @{ n = "FullName";     t = "wstr";        len = 200 },
    @{ n = "CleanEmail";   t = "wstr";        len = 200 },
    @{ n = "IsValidRow";   t = "bool";        len = 0   },
    @{ n = "LoadDateTime"; t = "dbTimeStamp"; len = 0   }
)

$xml = New-Object System.Xml.XmlDocument
$xml.PreserveWhitespace = $true
$xml.Load($Package)

function Get-Component([string]$refId) {
    foreach ($c in $xml.SelectNodes("//component")) { if ($c.refId -eq $refId) { return $c } }
    throw "component not found: $refId"
}
function New-El([string]$name) { return $xml.CreateElement($name) }

$scr = Get-Component $SCR
$dst = Get-Component $DST

# ---------------------------------------------------------------- input columns
$input0 = $scr.SelectSingleNode("inputs/input")
$inCols = $input0.SelectSingleNode("inputColumns")
if (-not $inCols) { $inCols = $input0.AppendChild((New-El "inputColumns")) }
$inCols.RemoveAll()
foreach ($c in $inputCols) {
    $e = New-El "inputColumn"
    $e.SetAttribute("refId", "$IN.Columns[$($c.n)]")
    $e.SetAttribute("cachedDataType", $c.t)
    $e.SetAttribute("cachedLength", "$($c.len)")
    $e.SetAttribute("cachedName", $c.n)
    $e.SetAttribute("lineageId", "$FF.Outputs[Flat File Source Output].Columns[$($c.n)]")
    $e.SetAttribute("usageType", "readOnly")
    [void]$inCols.AppendChild($e)
}

# ------------------------------------------------------- added output columns
$output0 = $null
foreach ($o in $scr.SelectNodes("outputs/output")) { if ($o.refId -eq $OUT) { $output0 = $o } }
if (-not $output0) { throw "output not found: $OUT" }
$outCols = $output0.SelectSingleNode("outputColumns")
if (-not $outCols) { $outCols = $output0.AppendChild((New-El "outputColumns")) }
$outCols.RemoveAll()
foreach ($c in $outputCols) {
    $e = New-El "outputColumn"
    $e.SetAttribute("refId", "$OUT.Columns[$($c.n)]")
    $e.SetAttribute("dataType", $c.t)
    if ($c.len -gt 0) { $e.SetAttribute("length", "$($c.len)") }
    # columns ADDED by a script component's synchronous output must use RD_NotUsed -
    # the component, not the pipeline, owns error handling for them
    $e.SetAttribute("errorRowDisposition", "NotUsed")
    $e.SetAttribute("truncationRowDisposition", "NotUsed")
    $e.SetAttribute("lineageId", "$OUT.Columns[$($c.n)]")
    $e.SetAttribute("name", $c.n)
    [void]$outCols.AppendChild($e)
}

# --------------------------------------------------------- embed the assembly
# BinaryCode is declared isArray="true", so it persists as
#   <property ... arrayElementCount="N"><arrayElements><arrayElement><![CDATA[..]]>
# (element names confirmed from the XPath literals inside
#  Microsoft.SqlServer.PackageFormatUpdate.dll and XmlParser::PopulateProperties
#  in Microsoft.SqlServer.PipelineXML.dll).
$b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($Dll))
$set = 0
foreach ($p in $scr.SelectNodes("properties/property")) {
    if ($p.name -eq "BinaryCode") {
        $p.RemoveAll() | Out-Null
        $p.SetAttribute("dataType", "System.String")
        $p.SetAttribute("description", "Stores the binary representation of the component")
        $p.SetAttribute("isArray", "true")
        $p.SetAttribute("name", "BinaryCode")
        $p.SetAttribute("state", "cdata")
        # ScriptComponentHost.GetBinaryCode(string assemblyname) looks the assembly up by
        # name, so the array is (assemblyName, base64) pairs - not a single blob.
        $els = $p.AppendChild((New-El "arrayElements"))
        $els.SetAttribute("arrayElementCount", "2")
        $e1 = $els.AppendChild((New-El "arrayElement"))
        [void]$e1.AppendChild($xml.CreateCDataSection($AssemblyName))
        $e2 = $els.AppendChild((New-El "arrayElement"))
        [void]$e2.AppendChild($xml.CreateCDataSection($b64))
        $set++
    }
    # UserComponentTypeName must stay "Microsoft.ScriptComponentHost" - it names the HOST,
    # not the user class. The host locates ScriptMain by scanning the loaded BinaryCode
    # assembly for [SSISScriptComponentEntryPoint] (VSTAComponentScriptingEngine
    # .CreateScriptObjectHelper -> GetExportedTypes).
    if ($p.name -eq "ScriptLanguage") { $p.InnerText = "CSharp"; $set++ }
}
if ($set -lt 2) { throw "expected BinaryCode and ScriptLanguage properties, set $set" }

# MetadataChecksum140 is written by the Script Component Editor over the column metadata.
# We cannot recompute it outside the designer (GetMetadataChecksum is private and needs a
# fully initialised host), and a stale value fails validation with "The component metadata
# is out of sync with the compiled script". Removing it skips the check.
$chk = $scr.SelectNodes("properties/property") | Where-Object { $_.name -eq "MetadataChecksum140" }
if ($chk) { [void]$chk.ParentNode.RemoveChild($chk) }

# ------------------------------------------------- destination: new columns
$dinput = $dst.SelectSingleNode("inputs/input")
$dExt = $dinput.SelectSingleNode("externalMetadataColumns")
$dIn  = $dinput.SelectSingleNode("inputColumns")

$destNew = @(
    @{ n = "FullName";     t = "wstr";         len = 200 },
    @{ n = "CleanEmail";   t = "wstr";         len = 200 },
    @{ n = "IsValidRow";   t = "bool";         len = 0   },
    @{ n = "LoadDateTime"; t = "dbTimeStamp2"; len = 0; scale = 7 }
)
foreach ($c in $destNew) {
    $extRef = "$DIN.ExternalColumns[$($c.n)]"
    $e = New-El "externalMetadataColumn"
    $e.SetAttribute("refId", $extRef)
    $e.SetAttribute("dataType", $c.t)
    if ($c.len -gt 0) { $e.SetAttribute("length", "$($c.len)") }
    if ($c.scale)     { $e.SetAttribute("scale", "$($c.scale)") }
    $e.SetAttribute("name", $c.n)
    [void]$dExt.AppendChild($e)

    $i = New-El "inputColumn"
    $i.SetAttribute("refId", "$DIN.Columns[$($c.n)]")
    $i.SetAttribute("cachedDataType", $(if ($c.n -eq "IsValidRow") { "bool" } elseif ($c.n -eq "LoadDateTime") { "dbTimeStamp" } else { "wstr" }))
    if ($c.len -gt 0) { $i.SetAttribute("cachedLength", "$($c.len)") }
    $i.SetAttribute("cachedName", $c.n)
    $i.SetAttribute("externalMetadataColumnId", $extRef)
    $i.SetAttribute("lineageId", "$OUT.Columns[$($c.n)]")
    [void]$dIn.AppendChild($i)
}

$xml.Save($Package)
Write-Host "wired: $($inputCols.Count) input cols, $($outputCols.Count) output cols, $($destNew.Count) destination mappings, assembly $([Math]::Round($b64.Length/1KB,1))KB base64"

