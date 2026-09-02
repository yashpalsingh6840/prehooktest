<#
.SYNOPSIS
    Creates the SSISDB catalog (and a folder inside it) on a SQL Server instance,
    without SSMS and without the Integration Services Catalogs GUI.

.DESCRIPTION
    The SSMS GUI path for catalog admin is unavailable on this box (CLAUDE.md
    trap 8: SSMS does not render the "Integration Services Catalogs" node), so
    catalog creation has to go through the SMO IntegrationServices object model.
    That stack does not load out of the box - it needs four things, each of which
    was a separate failure when this was first worked out by hand:

      1. A version-bridging AssemblyResolve handler. Microsoft.SqlServer.
         Management.IntegrationServices binds against ConnectionInfo /
         Management.Sdk.Sfc at versions that are NOT in the GAC, and its own
         IntegrationServicesEnum dependency lives in yet another folder. Both
         folders must be probed BY SIMPLE ASSEMBLY NAME, ignoring the version -
         that is what "version-bridging" means here.
      2. Microsoft.Data.SqlClient (v5+), not System.Data.SqlClient. The SMO
         build in these folders will not accept the older client.
      3. TrustServerCertificate=True. Microsoft.Data.SqlClient v5 defaults to
         Encrypt=True with strict validation, and a local instance's
         self-signed certificate fails the chain check without it.
      4. 'clr enabled' = 1 on the server BEFORE Catalog.Create(). SSISDB is
         implemented in CLR stored procedures; without it Create() fails with
         "Integration Services uses CLR stored procedures."

    WHY THIS IS A SCRIPT AND NOT PROSE: the folder layout above is not stable
    across machines. It moved once already - on the old dev box the assemblies
    were under "C:\Program Files\Microsoft SQL Server\170\Shared\MDS5xSMO" and
    "...\170\DTS\Binn"; on the current box those folders do not exist and the
    same assemblies ship inside the Visual Studio SSIS extension instead. So
    this script DISCOVERS the folders rather than naming them, and the next
    machine change costs nothing.

.PARAMETER ServerInstance
    e.g. ".\SQLFORPOC_2022". Defaults to the PoC's instance.

.PARAMETER MasterKeyPassword
    Catalog master key password. NOTE: this is the disaster-recovery key for
    every sensitive value in the catalog - see CLAUDE.md's warning. Fine for a
    throwaway local PoC, belongs in a password manager anywhere else.

.PARAMETER FolderName
    Catalog folder to create if missing. Pass '' to skip folder creation.

.EXAMPLE
    .\Scripts\New-SsisCatalog.ps1
    .\Scripts\New-SsisCatalog.ps1 -ServerInstance ".\OTHER" -FolderName 'SSIS PoC'
#>
[CmdletBinding()]
param(
    [string] $ServerInstance     = '.\SQLFORPOC_2022',
    [string] $MasterKeyPassword  = 'Password@2233',
    [string] $FolderName         = 'SSIS PoC',
    [string] $FolderDescription  = 'SSIS PoC packages'
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# 1. Discover the probe folders instead of hardcoding them.
#
# Two distinct assembly sets are needed and they do NOT live together:
#   * Microsoft.SqlServer.Management.IntegrationServices[Enum]  -> a "Binn" dir
#   * ConnectionInfo / Sdk.Sfc / Smo / Microsoft.Data.SqlClient -> a "SQLCommon"
#     (or SSMS IDE) dir
# Candidate roots are ordered newest-first; every directory that actually
# contains one of the marker files is added to the probe list, so a partial
# install on one root is covered by another.
# ---------------------------------------------------------------------------
$markers = @(
    'Microsoft.SqlServer.Management.IntegrationServices.dll',
    'Microsoft.SqlServer.Management.IntegrationServicesEnum.dll',
    'Microsoft.SqlServer.ConnectionInfo.dll',
    'Microsoft.SqlServer.Management.Sdk.Sfc.dll',
    'Microsoft.SqlServer.Smo.dll',
    'Microsoft.Data.SqlClient.dll'
)

$candidateRoots = @(
    # Visual Studio SSIS extension (where these live on the current box)
    "${env:ProgramFiles}\Microsoft Visual Studio\*\*\Common7\IDE\CommonExtensions\Microsoft\SSIS",
    "${env:ProgramFiles}\Microsoft Visual Studio\*\*\Common7\IDE\Extensions\Microsoft",
    # SSMS
    "${env:ProgramFiles}\Microsoft SQL Server Management Studio*\Release\Common7\IDE",
    "${env:ProgramFiles(x86)}\Microsoft SQL Server Management Studio*\Common7\IDE",
    # SQL Server shared components (where these lived on the previous box)
    "${env:ProgramFiles}\Microsoft SQL Server\*\Shared",
    "${env:ProgramFiles}\Microsoft SQL Server\*\DTS\Binn"
)

$probeDirs = New-Object System.Collections.Generic.List[string]
foreach ($root in $candidateRoots) {
    foreach ($base in (Get-Item -Path $root -ErrorAction SilentlyContinue)) {
        # the marker may be directly in $base or nested a couple of levels down
        # (e.g. SSIS\170\Binn, SSIS\170\Extensions\SQLCommon)
        Get-ChildItem -Path $base.FullName -Recurse -Depth 3 -Include $markers -File -ErrorAction SilentlyContinue |
            ForEach-Object {
                if (-not $probeDirs.Contains($_.DirectoryName)) { $probeDirs.Add($_.DirectoryName) }
            }
    }
}

if ($probeDirs.Count -eq 0) {
    throw "Found none of [$($markers -join ', ')] under any known root. Install the Visual Studio 'SQL Server Integration Services Projects' extension or SSMS, then re-run."
}

# Sort so higher version folders (170 > 160 > 150) win when the resolver walks
# the list in order. Plain descending string sort is enough for these names.
$probeList = ($probeDirs | Sort-Object -Descending)
Write-Host "Probing $($probeList.Count) folder(s) for SMO assemblies:" -ForegroundColor Cyan
$probeList | ForEach-Object { Write-Host "  $_" }

# ---------------------------------------------------------------------------
# 2. Version-bridging AssemblyResolve handler.
#
# Compiled via Add-Type rather than a PowerShell scriptblock: an inline
# scriptblock handler is not reliably invoked during the CLR's fusion probe,
# and Add-Type -Path on these DLLs throws ReflectionTypeLoadException
# (CLAUDE.md trap 7) - LoadFrom by full path is what works.
# ---------------------------------------------------------------------------
if (-not ('SsisAsmResolver' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;

public static class SsisAsmResolver
{
    private static string[] _dirs = new string[0];
    private static bool _hooked;

    public static void Install(string[] dirs)
    {
        _dirs = dirs;
        if (_hooked) return;
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        _hooked = true;
    }

    private static Assembly Resolve(object sender, ResolveEventArgs e)
    {
        // Match on SIMPLE NAME ONLY. The requested version is deliberately
        // ignored: the whole point is that the requesting assembly asks for a
        // version that is not installed anywhere, and the on-disk copy that
        // satisfies it carries a different version number.
        string simple = new AssemblyName(e.Name).Name;
        foreach (string dir in _dirs)
        {
            string path = Path.Combine(dir, simple + ".dll");
            if (File.Exists(path))
            {
                try { return Assembly.LoadFrom(path); } catch { }
            }
        }
        return null;
    }
}
'@
}
[SsisAsmResolver]::Install([string[]]$probeList)

# ---------------------------------------------------------------------------
# 3. Load the stack and connect.
# ---------------------------------------------------------------------------
function Import-FromProbe([string] $simpleName) {
    foreach ($d in $probeList) {
        $p = Join-Path $d "$simpleName.dll"
        if (Test-Path $p) { return [Reflection.Assembly]::LoadFrom($p) }
    }
    throw "Could not find $simpleName.dll in any probe folder."
}

$sqlClientAsm = Import-FromProbe 'Microsoft.Data.SqlClient'
$null         = Import-FromProbe 'Microsoft.SqlServer.ConnectionInfo'
$null         = Import-FromProbe 'Microsoft.SqlServer.Management.Sdk.Sfc'
$isAsm        = Import-FromProbe 'Microsoft.SqlServer.Management.IntegrationServices'

Write-Host "IntegrationServices assembly: $($isAsm.GetName().Version)" -ForegroundColor Cyan

# TrustServerCertificate is mandatory, not optional - see .DESCRIPTION item 3.
$connStr = "Data Source=$ServerInstance;Integrated Security=SSPI;TrustServerCertificate=True;"
$conn = $sqlClientAsm.GetType('Microsoft.Data.SqlClient.SqlConnection').GetConstructor([Type[]]@([string])).Invoke(@($connStr))
$conn.Open()
Write-Host "Connected to $ServerInstance" -ForegroundColor Green

$isType = $isAsm.GetType('Microsoft.SqlServer.Management.IntegrationServices.IntegrationServices')
$is     = [Activator]::CreateInstance($isType, @($conn))

# ---------------------------------------------------------------------------
# 4. Create the catalog if it is not already there.
# ---------------------------------------------------------------------------
$catalog = $is.Catalogs['SSISDB']
if ($null -ne $catalog) {
    Write-Host "Catalog SSISDB already exists - leaving it alone." -ForegroundColor Yellow
}
else {
    $catalogType = $isAsm.GetType('Microsoft.SqlServer.Management.IntegrationServices.Catalog')
    # (parent, name, masterKeyPassword)
    $catalog = [Activator]::CreateInstance($catalogType, @($is, 'SSISDB', $MasterKeyPassword))
    $catalog.Create()
    Write-Host "Created catalog SSISDB." -ForegroundColor Green
    $is.Catalogs.Refresh()
    $catalog = $is.Catalogs['SSISDB']
}

# ---------------------------------------------------------------------------
# 5. Create the folder. The deployment path is /SSISDB/<folder>/<project> and
#    neither the wizard nor catalog.deploy_project will create the folder for
#    you (CLAUDE.md trap 9), so it has to exist first.
# ---------------------------------------------------------------------------
if ($FolderName) {
    if ($null -ne $catalog.Folders[$FolderName]) {
        Write-Host "Folder '$FolderName' already exists." -ForegroundColor Yellow
    }
    else {
        $folderType = $isAsm.GetType('Microsoft.SqlServer.Management.IntegrationServices.CatalogFolder')
        $folder = [Activator]::CreateInstance($folderType, @($catalog, $FolderName, $FolderDescription))
        $folder.Create()
        Write-Host "Created folder '$FolderName'." -ForegroundColor Green
    }
}

$conn.Close()
Write-Host "Done." -ForegroundColor Green
