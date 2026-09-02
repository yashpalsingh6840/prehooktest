<#
.SYNOPSIS
    Registers an existing .dtsx with the .dtproj so Visual Studio shows it and the
    project build includes it in the .ispac.

.DESCRIPTION
    Writing a .dtsx into the project FOLDER is not enough - Visual Studio lists only
    packages declared in the .dtproj manifest. Two places need an entry:

      <SSIS:Packages>      ... <SSIS:Package SSIS:Name="X.dtsx" SSIS:EntryPoint="1" />
      <SSIS:PackageInfo>   ... <SSIS:PackageMetaData SSIS:Name="X.dtsx"> ... properties

    Identity values (ID, VersionGUID, Name) are read out of the .dtsx itself so the
    manifest and the package agree; a mismatch makes the build complain.

    Idempotent - re-running for an already-registered package changes nothing.

.EXAMPLE
    .\Add-PackageToProject.ps1 -PackageFile Package_Transforms.dtsx
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageFile,
    [string]$Project = "$PSScriptRoot\..\RBC_Demo_ETL\RBC_Demo_ETL\RBC_Demo_ETL.dtproj",
    [string]$Description = ''
)
$ErrorActionPreference = 'Stop'

$projPath = (Resolve-Path $Project).Path
$projDir  = Split-Path $projPath -Parent
$pkgName  = Split-Path $PackageFile -Leaf
$pkgPath  = Join-Path $projDir $pkgName
if (-not (Test-Path $pkgPath)) { throw "package not found in project folder: $pkgPath" }

$proj   = [System.IO.File]::ReadAllText($projPath)
$pkgXml = [System.IO.File]::ReadAllText($pkgPath)

# --- package parameters -> manifest parameters ---------------------------
# The manifest MUST list the same parameters the package declares. If it does not,
# Visual Studio fails to open the package ("parameters are out of sync"), even though
# dtexec runs it happily - dtexec never reads the .dtproj.
function Get-ParametersXml([string]$packageXml) {
    $out = New-Object System.Text.StringBuilder
    $rx = [regex]'(?s)<DTS:PackageParameter\b(.*?)</DTS:PackageParameter>'
    foreach ($m in $rx.Matches($packageXml)) {
        $blk  = $m.Groups[1].Value
        $name = ([regex]::Match($blk, 'DTS:ObjectName="([^"]*)"')).Groups[1].Value
        $dt   = ([regex]::Match($blk, 'DTS:DataType="([^"]*)"')).Groups[1].Value
        $val  = ([regex]::Match($blk, '(?s)DTS:Name="ParameterValue">(.*?)</DTS:Property>')).Groups[1].Value
        if (-not $name) { continue }
        [void]$out.Append(@"

                <SSIS:Parameter SSIS:Name="$name">
                  <SSIS:Properties>
                    <SSIS:Property SSIS:Name="ID">
                    </SSIS:Property>
                    <SSIS:Property SSIS:Name="CreationName">
                    </SSIS:Property>
                    <SSIS:Property SSIS:Name="Description">
                    </SSIS:Property>
                    <SSIS:Property SSIS:Name="IncludeInDebugDump">0</SSIS:Property>
                    <SSIS:Property SSIS:Name="Required">0</SSIS:Property>
                    <SSIS:Property SSIS:Name="Sensitive">0</SSIS:Property>
                    <SSIS:Property SSIS:Name="Value">$val</SSIS:Property>
                    <SSIS:Property SSIS:Name="DataType">$dt</SSIS:Property>
                  </SSIS:Properties>
                </SSIS:Parameter>
"@)
    }
    if ($out.Length -eq 0) { return '<SSIS:Parameters />' }
    return "<SSIS:Parameters>$($out.ToString())`r`n              </SSIS:Parameters>"
}

if ($proj -match [regex]::Escape("SSIS:Name=`"$pkgName`"")) {
    # Already registered. Re-sync the parts that drift whenever the package is rebuilt:
    # its identity GUIDs and its parameter list. A stale ID/VersionGUID or a parameter
    # list that disagrees with the package stops Visual Studio opening it.
    Copy-Item $projPath "$projPath.bak_beforeadd" -Force
    $newId  = ([regex]::Match($pkgXml, 'DTS:DTSID="([^"]*)"')).Groups[1].Value
    $newVer = ([regex]::Match($pkgXml, 'DTS:VersionGUID="([^"]*)"')).Groups[1].Value
    $paramsXml = Get-ParametersXml $pkgXml

    $metaRx = [regex]("(?s)<SSIS:PackageMetaData SSIS:Name=`"" + [regex]::Escape($pkgName) + "`">.*?</SSIS:PackageMetaData>")
    $mm = $metaRx.Match($proj)
    if (-not $mm.Success) { throw "metadata block for '$pkgName' not found" }
    $block = $mm.Value

    if ($newId)  { $block = [regex]::Replace($block, '(?s)(<SSIS:Property SSIS:Name="ID">)[^<]*(</SSIS:Property>)',          "`${1}$newId`${2}", 1) }
    if ($newVer) { $block = [regex]::Replace($block, '(?s)(<SSIS:Property SSIS:Name="VersionGUID">)[^<]*(</SSIS:Property>)', "`${1}$newVer`${2}", 1) }
    $block = [regex]::Replace($block, '(?s)<SSIS:Parameters\s*/>|<SSIS:Parameters>.*?</SSIS:Parameters>', { $paramsXml }, 1)

    if ($block -eq $mm.Value) {
        Write-Host "'$pkgName' already registered and in sync - no changes" -ForegroundColor Yellow
        return
    }
    $proj = $proj.Remove($mm.Index, $mm.Length).Insert($mm.Index, $block)
    [System.IO.File]::WriteAllText($projPath, $proj, (New-Object System.Text.UTF8Encoding($true)))
    $n = ([regex]'<SSIS:Parameter ').Matches($paramsXml).Count
    Write-Host "'$pkgName' re-synced: ID=$newId, $n parameter(s)" -ForegroundColor Green
    return
}

# --- identity, read from the package itself ------------------------------
function Get-Attr([string]$name, [string]$fallback) {
    $m = [regex]::Match($pkgXml, "DTS:$name=`"([^`"]*)`"")
    if ($m.Success -and $m.Groups[1].Value) { return $m.Groups[1].Value }
    return $fallback
}
$id      = Get-Attr 'DTSID'       ('{' + [guid]::NewGuid().ToString().ToUpper() + '}')
$name    = Get-Attr 'ObjectName'  ([IO.Path]::GetFileNameWithoutExtension($pkgName))
$verGuid = Get-Attr 'VersionGUID' ('{' + [guid]::NewGuid().ToString().ToUpper() + '}')
$fmt     = Get-Attr 'PackageFormatVersion' '8'
$desc    = if ($Description) { $Description } else { Get-Attr 'Description' '' }

# Match the project's own protection level, otherwise the build objects.
$projProt = ([regex]::Match($proj, 'SSIS:ProtectionLevel="([^"]*)"')).Groups[1].Value
$protNum  = if ($projProt -eq 'DontSaveSensitive') { '0' } else { '1' }

Copy-Item $projPath "$projPath.bak_beforeadd" -Force
Write-Host "backup -> $(Split-Path $projPath -Leaf).bak_beforeadd"

# --- 1. <SSIS:Packages> --------------------------------------------------
$pkgEntry = "          <SSIS:Package SSIS:Name=`"$pkgName`" SSIS:EntryPoint=`"1`" />`r`n        </SSIS:Packages>"
$proj = [regex]::Replace($proj, '\s*</SSIS:Packages>', "`r`n$pkgEntry", 1)
Write-Host "added <SSIS:Package> entry for $pkgName"

# --- 2. <SSIS:PackageMetaData> ------------------------------------------
# Placed immediately before </SSIS:PackageInfo>. No <SSIS:Parameters> content:
# these showcase packages use literal connection strings, not project parameters.
$meta = @"
            <SSIS:PackageMetaData SSIS:Name="$pkgName">
              <SSIS:Properties>
                <SSIS:Property SSIS:Name="ID">$id</SSIS:Property>
                <SSIS:Property SSIS:Name="Name">$name</SSIS:Property>
                <SSIS:Property SSIS:Name="VersionMajor">1</SSIS:Property>
                <SSIS:Property SSIS:Name="VersionMinor">0</SSIS:Property>
                <SSIS:Property SSIS:Name="VersionBuild">1</SSIS:Property>
                <SSIS:Property SSIS:Name="VersionComments">
                </SSIS:Property>
                <SSIS:Property SSIS:Name="VersionGUID">$verGuid</SSIS:Property>
                <SSIS:Property SSIS:Name="PackageFormatVersion">$fmt</SSIS:Property>
                <SSIS:Property SSIS:Name="Description">$desc</SSIS:Property>
                <SSIS:Property SSIS:Name="ProtectionLevel">$protNum</SSIS:Property>
              </SSIS:Properties>
              $(Get-ParametersXml $pkgXml)
            </SSIS:PackageMetaData>
          </SSIS:PackageInfo>
"@
$proj = [regex]::Replace($proj, '\s*</SSIS:PackageInfo>', "`r`n$meta", 1)
Write-Host "added <SSIS:PackageMetaData> entry for $pkgName"

[System.IO.File]::WriteAllText($projPath, $proj, (New-Object System.Text.UTF8Encoding($true)))

# --- verify it is well-formed and now lists both -------------------------
[xml]$check = Get-Content $projPath -Raw
$ns = New-Object System.Xml.XmlNamespaceManager($check.NameTable)
$ns.AddNamespace('SSIS', 'www.microsoft.com/SqlServer/SSIS')
$listed = $check.SelectNodes('//SSIS:Packages/SSIS:Package', $ns) | ForEach-Object { $_.GetAttribute('Name', 'www.microsoft.com/SqlServer/SSIS') }
Write-Host ""
Write-Host "packages now in the project:" -ForegroundColor Green
$listed | ForEach-Object { "   $_" }
