<#
.SYNOPSIS
    Builds Package_Legacy.dtsx plus its .dtsConfig - the last production patterns.

.DESCRIPTION
    Covers the items still missing after the transforms/exports/advanced packages:
      * fixed-width (RaggedRight) flat file SOURCE with HeaderRowsToSkip   - CUVImport
      * append-mode Flat File Destination (Overwrite = false)              - N600ToSalesForce
      * TWO databases in one package                                       - FXRate_Import
      * atomic BEGIN TRAN / TRY / COMMIT / CATCH ROLLBACK                  - MarginReport
      * a deliberately DISABLED task (C1*)                                 - N600ToSalesForce
      * an XML .dtsConfig package configuration (C10)                      - ts_dtsBPSAFeed

    The .dtsConfig is written here rather than in C# so the file and the package that
    references it are always produced together.
#>
[CmdletBinding()]
param(
    [string]$OutFile = "$PSScriptRoot\..\RBC_Demo_ETL\RBC_Demo_ETL\Package_Legacy.dtsx"
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\_LoadSsisBuilder.ps1"

$fixedFile  = Join-Path $SsisRoot 'sample-data\CustomersFixed.txt'
$outDir     = Join-Path $SsisRoot 'output'
$configPath = Join-Path $SsisRoot 'config\Package_Legacy.dtsConfig'
if (-not (Test-Path $fixedFile)) { throw "fixed-width sample not found: $fixedFile" }
New-Item -ItemType Directory -Force $outDir                     | Out-Null
New-Item -ItemType Directory -Force (Split-Path $configPath -Parent) | Out-Null

$cacheConn = $SsisOleDb -replace 'Initial Catalog=SSISDemo;', 'Initial Catalog=SSISDemoCache;'

# ---------------------------------------------------------------- .dtsConfig
# Same shape as the production configs: a ConfiguredValue per property path.
# Production leaves the password EMPTY and lets the C# launcher supply it at run time -
# reproduced here, including that risk (C10 in the analysis).
$cfgXml = @"
<?xml version="1.0"?>
<DTSConfiguration>
  <DTSConfigurationHeading>
    <DTSConfigurationFileInfo
      GeneratedBy="RBC_Demo_ETL"
      GeneratedFromPackageName="Package_Legacy"
      Description="Mirrors BPSAFeed.dtsConfig / CUVImportConfig.dtsConfig from N600-ProdProc" />
  </DTSConfigurationHeading>
  <Configuration ConfiguredType="Property" Path="\Package.Connections[CM_SQL_SSISDemo].Properties[ConnectionString]" ValueType="String">
    <ConfiguredValue>$($SsisOleDb.Replace('&','&amp;').Replace('<','&lt;').Replace('>','&gt;'))</ConfiguredValue>
  </Configuration>
  <Configuration ConfiguredType="Property" Path="\Package.Connections[CM_SQL_SSISDemoCache].Properties[ConnectionString]" ValueType="String">
    <ConfiguredValue>$($cacheConn.Replace('&','&amp;').Replace('<','&lt;').Replace('>','&gt;'))</ConfiguredValue>
  </Configuration>
  <!--
    C10 RISK, reproduced but left INACTIVE on purpose.

    Production (BPSAFeed.dtsConfig, OptionFeed.dtsConfig) ships exactly the entry below
    with an EMPTY ConfiguredValue, relying on the C# launcher's connections dictionary to
    supply the password at run time. Enabling it here is instructive: the empty value
    overrides the password already present in the ConnectionString above, and the package
    fails with "Login failed for user 'sa'" under plain dtexec - while
    "Launcher.exe managed Package_Legacy.dtsx" still succeeds, because the launcher
    re-injects the connection string after LoadPackage.

    That is the whole C10 problem in one file: the package is not self-sufficient.
    Uncomment to see it fail; left commented so the suite stays runnable.

  <Configuration ConfiguredType="Property" Path="\Package.Connections[CM_SQL_SSISDemo].Properties[Password]" ValueType="String">
    <ConfiguredValue></ConfiguredValue>
  </Configuration>
  -->
</DTSConfiguration>
"@
[System.IO.File]::WriteAllText($configPath, $cfgXml, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "wrote $configPath"

# ------------------------------------------------------------------- package
$out = [System.IO.Path]::GetFullPath($OutFile)
[SsisBuild.LegacyPackage]::Build($out, $fixedFile, $outDir, $SsisOleDb, $cacheConn, $configPath)

$v = [SsisBuild.Ssis]::Load($out)
Write-Host "reloaded OK - executables: $(($v.Executables | ForEach-Object { $_.GetType().GetProperty('Name').GetValue($_) }) -join ', ')" -ForegroundColor Green
