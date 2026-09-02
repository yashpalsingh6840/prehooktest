<#
.SYNOPSIS
    Adds both Script Components and the C# Script Task to Package.dtsx.

.DESCRIPTION
    Package.dtsx binds its connection managers to PROJECT PARAMETERS, so the standalone
    test package cannot simply be copied over it. This script applies the same changes to
    the real project package while leaving the parameter bindings intact.

    Idempotent: re-running detects the components and does nothing.

    See SESSION-HANDOFF.md section 6d for the .dtsx contract this relies on.
#>
param(
    [Parameter(Mandatory = $true)][string]$Package,
    [Parameter(Mandatory = $true)][string]$ScriptsRoot,   # Scripts\Generated
    [Parameter(Mandatory = $true)][string]$BuildDir,      # scratch dir for compiled assemblies
    [string]$ScriptTaskSource                             # package holding the C# Script Task
)
$ErrorActionPreference = "Stop"
$DTS = "www.microsoft.com/SqlServer/Dts"

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$gac = "C:\Windows\Microsoft.NET\assembly"
$pipelineRefs = @(
    "C:\Program Files\Microsoft SQL Server\160\DTS\PipelineComponents\Microsoft.SqlServer.TxScript.dll",
    "$gac\GAC_MSIL\Microsoft.SqlServer.PipelineHost\v4.0_16.0.0.0__89845dcd8080cc91\Microsoft.SQLServer.PipelineHost.dll",
    "$gac\GAC_MSIL\Microsoft.SqlServer.DTSPipelineWrap\v4.0_16.0.0.0__89845dcd8080cc91\Microsoft.SQLServer.DTSPipelineWrap.dll",
    "$gac\GAC_64\Microsoft.SqlServer.DTSRuntimeWrap\v4.0_16.0.0.0__89845dcd8080cc91\Microsoft.SqlServer.DTSRuntimeWrap.dll",
    "$gac\GAC_MSIL\Microsoft.SqlServer.ManagedDTS\v4.0_16.0.0.0__89845dcd8080cc91\Microsoft.SqlServer.ManagedDTS.dll"
)
$taskRefs = @(
    "C:\Program Files\Microsoft SQL Server\160\DTS\Tasks\Microsoft.SqlServer.ScriptTask.dll",
    "$gac\GAC_MSIL\Microsoft.SqlServer.ManagedDTS\v4.0_16.0.0.0__89845dcd8080cc91\Microsoft.SqlServer.ManagedDTS.dll",
    "System.dll", "System.Data.dll", "System.Xml.dll"
)

New-Item -ItemType Directory -Path $BuildDir -Force | Out-Null

function Invoke-Csc {
    param([string]$OutDll, [string[]]$Refs, [string[]]$Sources)
    $args = @("/nologo", "/target:library", "/platform:x64", "/out:$OutDll")
    $args += ($Refs | ForEach-Object { "/r:$_" })
    $args += $Sources
    $out = & $csc $args 2>&1
    if ($LASTEXITCODE -ne 0) { throw "csc failed for $OutDll`n$($out -join "`n")" }
}

$xml = New-Object System.Xml.XmlDocument
$xml.PreserveWhitespace = $true
$xml.Load($Package)

function Get-Component([string]$refId) {
    foreach ($c in $xml.SelectNodes("//component")) { if ($c.refId -eq $refId) { return $c } }
    $null
}

# ---------------------------------------------------------------------------
# Inserts a synchronous Script Component between $SourceRef and $DestRef.
# ---------------------------------------------------------------------------
function Add-ScriptComponent {
    param(
        [string]$Dft, [string]$Name, [string]$AssemblyName, [string]$Dll, [string]$SrcDir,
        [string]$SourceOutputRef, [string]$DestInputRef,
        [array]$InputCols, [array]$OutputCols, [array]$DestCols
    )
    $scr = "$Dft\$Name"
    $in  = "$scr.Inputs[Input 0]"
    $out = "$scr.Outputs[Output 0]"

    if (Get-Component $scr) { Write-Host "  $Name already present - skipped"; return }

    $dest = Get-Component $DestInputRef.Split('.')[0]
    $components = $dest.ParentNode
    $pipeline = $components.ParentNode

    $b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($Dll))

    # SourceCode persists as (fileName, encoding, content) triplets and is NOT optional.
    $files = @("ComponentWrapper.cs", "BufferWrapper.cs", "main.cs")
    $srcXml = ($files | ForEach-Object {
        $c = [IO.File]::ReadAllText((Join-Path $SrcDir $_))
        "<arrayElement dataType=`"System.String`"><![CDATA[$_]]></arrayElement>" +
        "<arrayElement dataType=`"System.String`"><![CDATA[UTF8]]></arrayElement>" +
        "<arrayElement dataType=`"System.String`"><![CDATA[$c]]></arrayElement>"
    }) -join ""

    $inColXml = ($InputCols | ForEach-Object {
        $len = if ($_.len) { " cachedLength=`"$($_.len)`"" } else { "" }
        "<inputColumn refId=`"$in.Columns[$($_.n)]`" cachedDataType=`"$($_.t)`"$len cachedName=`"$($_.n)`" lineageId=`"$SourceOutputRef.Columns[$($_.n)]`" usageType=`"readOnly`" />"
    }) -join ""

    # added output columns must use NotUsed dispositions (XML value, not the RD_ enum name)
    $outColXml = ($OutputCols | ForEach-Object {
        $len = if ($_.len) { " length=`"$($_.len)`"" } else { "" }
        "<outputColumn refId=`"$out.Columns[$($_.n)]`" dataType=`"$($_.t)`"$len errorRowDisposition=`"NotUsed`" truncationRowDisposition=`"NotUsed`" lineageId=`"$out.Columns[$($_.n)]`" name=`"$($_.n)`" />"
    }) -join ""

    $contact = "Includes and runs custom script code.;Microsoft Corporation; Microsoft SQL Server; Microsoft Corporation; All Rights Reserved; http://www.microsoft.com/sql/support;13"

    # NOTE: no MetadataChecksum140 - it can only be computed by the designer, and a stale
    # value fails validation. BinaryCode is a (assemblyName, base64) PAIR.
    $compXml = @"
<component refId="$scr" componentClassID="Microsoft.ManagedComponentHost" contactInfo="$contact" description="Includes and runs custom script code." name="$Name" version="13">
<properties>
<property dataType="System.String" description="Stores the source code of the component" isArray="true" name="SourceCode" state="cdata"><arrayElements arrayElementCount="$($files.Count * 3)">$srcXml</arrayElements></property>
<property dataType="System.String" description="Stores the binary representation of the component" isArray="true" name="BinaryCode" state="cdata"><arrayElements arrayElementCount="2"><arrayElement><![CDATA[$AssemblyName]]></arrayElement><arrayElement><![CDATA[$b64]]></arrayElement></arrayElements></property>
<property dataType="System.String" description="Specifies the name of the Microsoft Visual Studio Tools for Applications project." name="VSTAProjectName" typeConverter="NOTBROWSABLE">$AssemblyName</property>
<property dataType="System.String" description="Specifies the programming language used by the script." name="ScriptLanguage">CSharp</property>
<property dataType="System.String" description="Specifies a comma-separated list of read-only variables." name="ReadOnlyVariables"></property>
<property dataType="System.String" description="Specifies a comma-separated list of read/write variables." name="ReadWriteVariables"></property>
<property dataType="System.String" description="List of breakpoints present on the script." isArray="true" name="BreakpointCollection" typeConverter="NOTBROWSABLE"><arrayElements arrayElementCount="0" /></property>
<property dataType="System.String" name="UserComponentTypeName">Microsoft.ScriptComponentHost</property>
</properties>
<inputs><input refId="$in" name="Input 0"><inputColumns>$inColXml</inputColumns><externalMetadataColumns /></input></inputs>
<outputs><output refId="$out" name="Output 0" synchronousInputId="$in"><outputColumns>$outColXml</outputColumns><externalMetadataColumns /></output></outputs>
</component>
"@
    $frag = $xml.CreateDocumentFragment()
    $frag.InnerXml = $compXml
    [void]$components.AppendChild($frag)

    # rewire source -> component -> destination
    $paths = $pipeline.SelectSingleNode("paths")
    $rewired = $false
    foreach ($p in $paths.SelectNodes("path")) {
        if ($p.startId -eq $SourceOutputRef -and $p.endId -eq $DestInputRef) {
            $p.SetAttribute("endId", $in); $rewired = $true
        }
    }
    if (-not $rewired) { throw "no path $SourceOutputRef -> $DestInputRef to rewire" }

    $np = $xml.CreateElement("path")
    $np.SetAttribute("refId", "$Dft.Paths[$Name Output]")
    $np.SetAttribute("startId", $out)
    $np.SetAttribute("endId", $DestInputRef)
    $np.SetAttribute("name", "$Name Output")
    [void]$paths.AppendChild($np)

    # map the new columns on the destination
    $dinput = $dest.SelectSingleNode("inputs/input")
    $dExt = $dinput.SelectSingleNode("externalMetadataColumns")
    $dIn  = $dinput.SelectSingleNode("inputColumns")
    foreach ($c in $DestCols) {
        $extRef = "$DestInputRef.ExternalColumns[$($c.n)]"
        $e = $xml.CreateElement("externalMetadataColumn")
        $e.SetAttribute("refId", $extRef)
        $e.SetAttribute("dataType", $c.ext)
        if ($c.len)   { $e.SetAttribute("length", "$($c.len)") }
        if ($c.scale) { $e.SetAttribute("scale", "$($c.scale)") }
        $e.SetAttribute("name", $c.n)
        [void]$dExt.AppendChild($e)

        $i = $xml.CreateElement("inputColumn")
        $i.SetAttribute("refId", "$DestInputRef.Columns[$($c.n)]")
        $i.SetAttribute("cachedDataType", $c.cached)
        if ($c.len) { $i.SetAttribute("cachedLength", "$($c.len)") }
        $i.SetAttribute("cachedName", $c.n)
        $i.SetAttribute("externalMetadataColumnId", $extRef)
        $i.SetAttribute("lineageId", "$out.Columns[$($c.n)]")
        [void]$dIn.AppendChild($i)
    }
    Write-Host "  added $Name"
}

# ===========================================================================
Write-Host "compiling script assemblies..."
$asm1 = "SC_CleanseCustomerRow_gen"
$asm2 = "SC_ComputeCustomerTier_gen"
$dir1 = Join-Path $ScriptsRoot "CleanseCustomerRow"
$dir2 = Join-Path $ScriptsRoot "ComputeCustomerTier"
Invoke-Csc "$BuildDir\$asm1.dll" $pipelineRefs @("$dir1\BufferWrapper.cs", "$dir1\ComponentWrapper.cs", "$dir1\main.cs")
Invoke-Csc "$BuildDir\$asm2.dll" $pipelineRefs @("$dir2\BufferWrapper.cs", "$dir2\ComponentWrapper.cs", "$dir2\main.cs")

Write-Host "adding script components..."
Add-ScriptComponent -Dft "Package\DFT_LoadCustomers" -Name "SCR_CleanseCustomerRow" `
    -AssemblyName $asm1 -Dll "$BuildDir\$asm1.dll" -SrcDir $dir1 `
    -SourceOutputRef "Package\DFT_LoadCustomers\FF_SRC_Customers.Outputs[Flat File Source Output]" `
    -DestInputRef "Package\DFT_LoadCustomers\OLEDST_StagingCustomers.Inputs[OLE DB Destination Input]" `
    -InputCols @(
        @{n="CustomerID";t="wstr";len=20}, @{n="FirstName";t="wstr";len=100},
        @{n="LastName";t="wstr";len=100}, @{n="Email";t="wstr";len=200},
        @{n="Phone";t="wstr";len=50}, @{n="SignupDate";t="wstr";len=50},
        @{n="Country";t="wstr";len=100}) `
    -OutputCols @(
        @{n="FullName";t="wstr";len=200}, @{n="CleanEmail";t="wstr";len=200},
        @{n="IsValidRow";t="bool"}, @{n="LoadDateTime";t="dbTimeStamp"}) `
    -DestCols @(
        @{n="FullName";ext="wstr";len=200;cached="wstr"}, @{n="CleanEmail";ext="wstr";len=200;cached="wstr"},
        @{n="IsValidRow";ext="bool";cached="bool"}, @{n="LoadDateTime";ext="dbTimeStamp2";scale=7;cached="dbTimeStamp"})

Add-ScriptComponent -Dft "Package\DFT_BuildSalesSummary" -Name "SCR_ComputeCustomerTier" `
    -AssemblyName $asm2 -Dll "$BuildDir\$asm2.dll" -SrcDir $dir2 `
    -SourceOutputRef "Package\DFT_BuildSalesSummary\OLESRC_OrdersAndDetails.Outputs[OLE DB Source Output]" `
    -DestInputRef "Package\DFT_BuildSalesSummary\OLEDST_SalesSummary.Inputs[OLE DB Destination Input]" `
    -InputCols @(
        @{n="CustomerID";t="i4"}, @{n="FullName";t="wstr";len=200}, @{n="Email";t="wstr";len=200},
        @{n="TotalOrders";t="i4"}, @{n="TotalRevenue";t="r8"}, @{n="LastOrderDate";t="dbDate"}) `
    -OutputCols @(
        @{n="CustomerTier";t="wstr";len=20}, @{n="ProcessedDateTime";t="dbTimeStamp"}) `
    -DestCols @(
        @{n="CustomerTier";ext="wstr";len=20;cached="wstr"},
        @{n="ProcessedDateTime";ext="dbTimeStamp2";scale=7;cached="dbTimeStamp"})

# ---------------------------------------------------------------- ADO.NET CM
# A Script Task cannot use the native OLE DB manager: AcquireConnection returns a COM
# object, and ConnectionString omits the write-only Password. Bound to the same project
# parameters as CM_SQL_SSISDemo.
$cms = $xml.DocumentElement.SelectNodes("*[local-name()='ConnectionManagers']")[0]
$exists = $false
foreach ($c in @($cms.ChildNodes | Where-Object { $_.NodeType -eq [System.Xml.XmlNodeType]::Element })) {
    if ($c.GetAttribute("ObjectName", $DTS) -eq "CM_ADO_SSISDemo") { $exists = $true }
}
if ($exists) { Write-Host "  CM_ADO_SSISDemo already present - skipped" }
else {
    $cm = $xml.CreateElement("DTS:ConnectionManager", $DTS)
    $cm.SetAttribute("refId", $DTS, "Package.ConnectionManagers[CM_ADO_SSISDemo]")
    $cm.SetAttribute("CreationName", $DTS, "ADO.NET:System.Data.SqlClient.SqlConnection, System.Data, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089")
    $cm.SetAttribute("Description", $DTS, "ADO.NET view of the same database, for Script Tasks.")
    $cm.SetAttribute("DTSID", $DTS, "{$([guid]::NewGuid())}")
    $cm.SetAttribute("ObjectName", $DTS, "CM_ADO_SSISDemo")

    $pe = $xml.CreateElement("DTS:PropertyExpression", $DTS)
    $pe.SetAttribute("Name", $DTS, "ConnectionString")
    $pe.InnerText = '"Data Source=" + @[$Project::ServerName] + ";Initial Catalog=" + @[$Project::DatabaseName] + ";User ID=" + @[$Project::SqlLogin] + ";Password=" + @[$Project::SqlPassword] + ";Encrypt=False;"'
    [void]$cm.AppendChild($pe)

    $od = $xml.CreateElement("DTS:ObjectData", $DTS)
    $inner = $xml.CreateElement("DTS:ConnectionManager", $DTS)
    $inner.SetAttribute("ConnectionString", $DTS, "Data Source=localhost,1433;Initial Catalog=SSISDemo;User ID=sa;Encrypt=False;")
    [void]$od.AppendChild($inner)
    [void]$cm.AppendChild($od)
    [void]$cms.AppendChild($cm)
    Write-Host "  added CM_ADO_SSISDemo"
}

# ------------------------------------------------------------- C# Script Task
$hasTask = $false
foreach ($e in $xml.SelectNodes("//*[local-name()='Executable']")) {
    if ($e.GetAttribute("ExecutableType", $DTS) -like "*ScriptTask*") { $hasTask = $true }
}
if ($hasTask) { Write-Host "  Script Task already present - skipped" }
elseif ($ScriptTaskSource) {
    $stAsm = "ST_8d84581b262c4e39a00b720e2c6678e0"
    $stSrc = Join-Path $ScriptsRoot "ValidateAndLogStart\ScriptMain.cs"
    Invoke-Csc "$BuildDir\$stAsm.dll" $taskRefs @($stSrc)

    $sdoc = New-Object System.Xml.XmlDocument; $sdoc.PreserveWhitespace = $true; $sdoc.Load($ScriptTaskSource)
    $tn = $null
    foreach ($e in $sdoc.SelectNodes("//*[local-name()='Executable']")) {
        if ($e.GetAttribute("ExecutableType", $DTS) -like "*ScriptTask*") { $tn = $e; break }
    }
    if (-not $tn) { throw "no Script Task in $ScriptTaskSource" }
    $imported = $xml.ImportNode($tn, $true)

    # retarget the VSTA script from 17.0 to 16.0 and swap in the fresh assembly.
    # Keep the whole ProjectItem set - dropping any of it breaks LoadFromXML.
    $sp = $imported.SelectNodes(".//*[local-name()='ScriptProject']")[0]
    $sp.SetAttribute("VSTAMajorVersion", "16")
    $sp.SetAttribute("VSTAMinorVersion", "0")
    foreach ($ch in @($sp.ChildNodes | Where-Object { $_.NodeType -eq [System.Xml.XmlNodeType]::Element })) {
        if ($ch.LocalName -eq "ProjectItem" -and $ch.Name -eq "ScriptMain.cs") {
            $ch.RemoveAll() | Out-Null
            $ch.SetAttribute("Name", "ScriptMain.cs"); $ch.SetAttribute("Encoding", "UTF8")
            [void]$ch.AppendChild($xml.CreateCDataSection([IO.File]::ReadAllText($stSrc)))
        }
        elseif ($ch.LocalName -eq "BinaryItem") {
            $n = $ch.Name; $ch.RemoveAll() | Out-Null; $ch.SetAttribute("Name", $n)
            [void]$ch.AppendChild($xml.CreateCDataSection([Convert]::ToBase64String([IO.File]::ReadAllBytes("$BuildDir\$stAsm.dll"))))
        }
    }
    [void]$xml.DocumentElement.SelectNodes("*[local-name()='Executables']")[0].AppendChild($imported)

    $taskName = $imported.GetAttribute("ObjectName", $DTS)
    $pcNode = $xml.DocumentElement.SelectNodes("*[local-name()='PrecedenceConstraints']")[0]
    $pc = $xml.CreateElement("DTS:PrecedenceConstraint", $DTS)
    $pc.SetAttribute("refId", $DTS, "Package.PrecedenceConstraints[PC_ScriptTask_to_Load]")
    $pc.SetAttribute("CreationName", $DTS, "")
    $pc.SetAttribute("DTSID", $DTS, "{$([guid]::NewGuid())}")
    $pc.SetAttribute("From", $DTS, "Package\$taskName")
    $pc.SetAttribute("LogicalAnd", $DTS, "True")
    $pc.SetAttribute("ObjectName", $DTS, "PC_ScriptTask_to_Load")
    $pc.SetAttribute("To", $DTS, "Package\DFT_LoadCustomers")
    [void]$pcNode.AppendChild($pc)
    Write-Host "  added $taskName (VSTA 16.0)"
}

$xml.Save($Package)
Write-Host "saved $Package"
