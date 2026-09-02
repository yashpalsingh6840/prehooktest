<#
.SYNOPSIS
    Builds Package_Transforms.dtsx - the stock data-flow transforms the demo lacked.

.DESCRIPTION
    Thin wrapper. All pipeline construction lives in tools\BuildTransforms.cs, because
    COM pipeline objects cannot be marshalled back and forth through PowerShell
    (a MainPipe returns as System.__ComObject and will not convert back).

    Data flow (DFT_DerivedAndSplit):
        Flat File Source -> Data Conversion -> Derived Column
          -> Conditional Split -+-> Derived Column (tag Valid) ---+
                                |                                 +-> Union All -> OLE DB Destination
                                +-> Derived Column (tag Invalid) -+
#>
[CmdletBinding()]
param(
    [string]$OutFile = "$PSScriptRoot\..\RBC_Demo_ETL\RBC_Demo_ETL\Package_Transforms.dtsx"
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\_LoadSsisBuilder.ps1"

$csv = Join-Path $SsisRoot 'sample-data\Customers.csv'
if (-not (Test-Path $csv)) { throw "sample CSV not found: $csv" }

$outDir = Split-Path $OutFile -Parent
if (-not (Test-Path $outDir)) { throw "output folder not found: $outDir" }
$out = [System.IO.Path]::GetFullPath($OutFile)

[SsisBuild.TransformsPackage]::Build($out, $csv, $SsisOleDb)

# prove it round-trips through the runtime
$v = [SsisBuild.Ssis]::Load($out)
Write-Host "reloaded OK - executables: $(($v.Executables | ForEach-Object { $_.GetType().GetProperty('Name').GetValue($_) }) -join ', ')" -ForegroundColor Green
