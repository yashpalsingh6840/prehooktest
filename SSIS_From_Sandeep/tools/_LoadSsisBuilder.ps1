# Dot-source this from any package-building script:  . tools\_LoadSsisBuilder.ps1
# Compiles tools\SsisBuilder.cs and exposes [SsisBuild.Ssis] plus $SsisRefs.
$ErrorActionPreference = 'Stop'

$gac = 'C:\Windows\Microsoft.NET\assembly\GAC_MSIL'

# DTSRuntimeWrap is NOT in the GAC on this machine - only the VS SSIS extension
# ships it. ManagedDTS and DTSPipelineWrap are in the GAC at 16.0.0.0.
$SsisRefs = @(
  "$gac\Microsoft.SqlServer.ManagedDTS\v4.0_16.0.0.0__89845dcd8080cc91\Microsoft.SqlServer.ManagedDTS.dll"
  "$gac\Microsoft.SqlServer.DTSPipelineWrap\v4.0_16.0.0.0__89845dcd8080cc91\Microsoft.SqlServer.DTSPipelineWrap.dll"
  "D:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\CommonExtensions\Microsoft\SSIS\160\Binn\Microsoft.SqlServer.DTSRuntimeWrap.dll"
)
foreach ($r in $SsisRefs) {
  if (-not (Test-Path -LiteralPath $r)) { throw "missing SSIS interop assembly: $r" }
}

# Load the interops into the SESSION too, not just as compile-time references -
# calling scripts need [.. .Runtime.Wrapper.DataType] and the DTSUsageType enums.
foreach ($r in $SsisRefs) { Add-Type -Path $r -ErrorAction SilentlyContinue }

if (-not ('SsisBuild.Ssis' -as [type])) {
  # All builder sources compile together into one in-memory assembly.
  $srcFiles = @('SsisBuilder.cs','BuildTransforms.cs','BuildTransforms2.cs','BuildTransforms3.cs','BuildExports.cs','BuildAdvanced.cs','BuildErrorOutput.cs','BuildLegacy.cs','BuildMerge.cs') |
              ForEach-Object { Join-Path $PSScriptRoot $_ } |
              Where-Object { Test-Path $_ }
  if (-not $srcFiles) { throw "no builder .cs files found next to this script" }
  $src = $srcFiles

  # The VS extension ships DTSRuntimeWrap 16.100.0.0 while ManagedDTS 16.0.0.0 was
  # built against 16.0.0.0. That mismatch is only a WARNING, but Add-Type defaults to
  # warnings-as-errors, so compile through explicit CompilerParameters instead.
  # (-CompilerParameters cannot be combined with -ReferencedAssemblies.)
  $cp = New-Object System.CodeDom.Compiler.CompilerParameters
  $cp.GenerateInMemory = $true
  $cp.TreatWarningsAsErrors = $false
  $cp.WarningLevel = 0
  foreach ($r in $SsisRefs) { [void]$cp.ReferencedAssemblies.Add($r) }
  foreach ($r in @('System.dll','System.Core.dll','System.Data.dll','System.Xml.dll')) {
    [void]$cp.ReferencedAssemblies.Add($r)
  }
  Add-Type -Path $src -CompilerParameters $cp
}

# Connection strings used by every showcase package (parameter-free, dtexec-runnable).
$SsisOleDb  = 'Data Source=localhost,1433;Initial Catalog=SSISDemo;Provider=MSOLEDBSQL.1;User ID=sa;Password=YourStrong!Passw0rd123;Persist Security Info=True;'
$SsisAdoNet = 'Data Source=localhost,1433;Initial Catalog=SSISDemo;User ID=sa;Password=YourStrong!Passw0rd123;TrustServerCertificate=True;'
$SsisRoot   = Split-Path $PSScriptRoot -Parent
