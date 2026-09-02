<#
.SYNOPSIS
    Adds a VB.NET Script Task to an SSIS package from the command line.

.DESCRIPTION
    Hand-writing the XML for a VB Script Task does not work: with Language="VisualBasic",
    ScriptTask.LoadFromXML uses a different persisted layout from the C# path and rejects
    anything assembled by hand (see SESSION-HANDOFF.md 6d for the failed attempts).

    This script sidesteps that completely by using Microsoft's OWN object model:

        Application.LoadPackage
          -> Executables.Add("Microsoft.ScriptTask")
          -> task.ScriptStorage.ScriptFiles / .AssemblyCode
          -> Application.SaveToXml

    Because Microsoft's serializer writes the XML, the format is correct by construction
    and never has to be reverse-engineered.

    The VSTA project comes from Microsoft's own template,
    VSTA_SQL16_IS_ST_VB_Template.vstax, which is a plain zip. The VB source is compiled
    with vbc.exe against ManagedDTS/ScriptTask 16.0.0.0. Note VB has NO namespace
    declaration in the file - RootNamespace supplies it, so the assembly MUST be compiled
    with /rootnamespace:<projectname> or SSIS cannot resolve <RootNamespace>.ScriptMain.

.PARAMETER Package
    Path to the .dtsx to modify. A .bak_beforevb copy is taken first.

.PARAMETER VbSource
    The ScriptMain.vb to embed. Must declare no namespace and expose
    Partial Public Class ScriptMain with [SSISScriptTaskEntryPointAttribute],
    inheriting VSTARTScriptObjectModelBase.

.PARAMETER TaskName
    Name of the Script Task to create.

.PARAMETER After / .PARAMETER Before
    Optional. Existing executables to sequence the new task between. If both are given and
    a direct After -> Before constraint exists, it is replaced by After -> task -> Before.

.PARAMETER ReadOnlyVariables / .PARAMETER ReadWriteVariables
    Comma-separated SSIS variable lists, e.g. "User::RowsLoaded".

.EXAMPLE
    .\Add-VbScriptTask.ps1 -Package ..\RBC_Demo_ETL\RBC_Demo_ETL\Package.dtsx `
        -VbSource .\ScriptMain.vb -TaskName SCR_NotifyAndLogProgress `
        -After DFT_LoadCustomers -Before DFT_BuildSalesSummary `
        -ReadOnlyVariables "System::PackageName" `
        -ReadWriteVariables "User::BatchStartTime,User::RowsLoaded"

.NOTES
    Idempotent: if TaskName already exists in the package it exits without changes.
    Requires the Integration Services 2022 feature (for ScriptTask.dll + ManagedDTS 16).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$VbSource,
    [Parameter(Mandatory)][string]$TaskName,
    [string]$After,
    [string]$Before,
    [string]$ReadOnlyVariables  = '',
    [string]$ReadWriteVariables = '',
    [string]$Template = 'C:\Program Files (x86)\Microsoft SQL Server\160\DTS\Binn\VSTA_SQL16_IS_ST_VB_Template.vstax',
    [string]$ScriptTaskDll = 'C:\Program Files\Microsoft SQL Server\160\DTS\Tasks\Microsoft.SqlServer.ScriptTask.dll'
)

$ErrorActionPreference = 'Stop'

foreach ($p in @($Package, $VbSource, $Template, $ScriptTaskDll)) {
    if (-not (Test-Path -LiteralPath $p)) { throw "not found: $p" }
}

Add-Type -AssemblyName "Microsoft.SqlServer.ManagedDTS, Version=16.0.0.0, Culture=neutral, PublicKeyToken=89845dcd8080cc91"
Add-Type -Path $ScriptTaskDll -ErrorAction SilentlyContinue

$app = New-Object Microsoft.SqlServer.Dts.Runtime.Application
$pkg = $app.LoadPackage((Resolve-Path $Package), $null)

if ($pkg.Executables.Contains($TaskName)) {
    Write-Host "'$TaskName' already present - no changes made." -ForegroundColor Yellow
    return
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("vbtask_" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force (Join-Path $work 'src\My Project') | Out-Null
try {
    # ---- 1. unpack Microsoft's VB template (a zip) --------------------------
    $zip = Join-Path $work 'tpl.zip'
    Copy-Item $Template $zip -Force
    $tpl = Join-Path $work 'tpl'
    Expand-Archive -Path $zip -DestinationPath $tpl -Force

    $projName = 'ST_' + ([guid]::NewGuid().ToString('N'))
    $g1 = [guid]::NewGuid().ToString().ToUpper()
    $g2 = [guid]::NewGuid().ToString().ToUpper()
    $src = Join-Path $work 'src'
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)

    $map = [ordered]@{
        'IS%20Script%20Task%20Project.vbproj' = "$projName.vbproj"
        'AssemblyInfo.vb'                     = 'My Project\AssemblyInfo.vb'
        'Resources.resx'                      = 'My Project\Resources.resx'
        'Resources.Designer.vb'               = 'My Project\Resources.Designer.vb'
        'Settings.settings'                   = 'My Project\Settings.settings'
        'Settings.Designer.vb'                = 'My Project\Settings.Designer.vb'
    }
    foreach ($k in $map.Keys) {
        $text = (Get-Content (Join-Path $tpl $k) -Raw).
            Replace('$projectname$', $projName).Replace('$safeprojectname$', $projName).
            Replace('$guid1$', $g1).Replace('$guid2$', $g2).
            Replace('$year$', (Get-Date).Year.ToString()).
            Replace('$registeredorganization$', 'IRISSOFTWARE.COM')
        [System.IO.File]::WriteAllText((Join-Path $src $map[$k]), $text, $utf8NoBom)
    }
    [System.IO.File]::WriteAllText((Join-Path $src 'ScriptMain.vb'), (Get-Content $VbSource -Raw), $utf8NoBom)

    # ---- 2. compile (rootnamespace is mandatory - see NOTES) ----------------
    $gac = 'C:\Windows\Microsoft.NET\assembly\GAC_MSIL'
    $fw  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
    $dll = Join-Path $work "$projName.dll"
    $vbcArgs = @(
        '/nologo', '/target:library', "/out:$dll", "/rootnamespace:$projName",
        '/optioncompare:binary', '/optionexplicit+', '/optionstrict-',
        '/imports:Microsoft.VisualBasic,System,System.Collections,System.Data,System.Diagnostics',
        "/reference:$gac\Microsoft.SqlServer.ManagedDTS\v4.0_16.0.0.0__89845dcd8080cc91\Microsoft.SqlServer.ManagedDTS.dll",
        "/reference:$ScriptTaskDll",
        "/reference:$fw\System.dll", "/reference:$fw\System.Data.dll", "/reference:$fw\System.Xml.dll",
        (Join-Path $src 'ScriptMain.vb'),
        (Join-Path $src 'My Project\AssemblyInfo.vb'),
        (Join-Path $src 'My Project\Resources.Designer.vb'),
        (Join-Path $src 'My Project\Settings.Designer.vb')
    )
    & "$fw\vbc.exe" $vbcArgs
    if ($LASTEXITCODE -ne 0) { throw "vbc.exe failed with exit code $LASTEXITCODE" }
    Write-Host ("compiled {0:N0} bytes" -f (Get-Item $dll).Length)

    # ---- 3. create the task via the object model ---------------------------
    Copy-Item (Resolve-Path $Package) "$Package.bak_beforevb" -Force

    $exec = $pkg.Executables.Add('Microsoft.ScriptTask')
    $exec.Name = $TaskName
    $task = $exec.InnerObject
    $task.ScriptLanguage     = 'Microsoft Visual Basic 2019'
    $task.ScriptProjectName  = $projName
    $task.EntryPoint         = 'Main'
    $task.ReadOnlyVariables  = $ReadOnlyVariables
    $task.ReadWriteVariables = $ReadWriteVariables

    $stor = $task.ScriptStorage
    $stor.AssemblyName = "$projName.dll"
    $stor.AssemblyCode = [System.IO.File]::ReadAllBytes($dll)

    $ft   = [Microsoft.SqlServer.VSTAHosting.VSTAScriptProjectStorage].GetNestedType('VSTAScriptFile', 'Public,NonPublic')
    $utf8 = [Enum]::Parse($ft.GetField('Encoding').FieldType, 'UTF8')
    $files = @("$projName.vbproj", 'My Project\AssemblyInfo.vb', 'My Project\Resources.resx',
               'My Project\Resources.Designer.vb', 'My Project\Settings.settings',
               'My Project\Settings.Designer.vb', 'ScriptMain.vb')
    foreach ($rel in $files) {
        $content = [System.IO.File]::ReadAllText((Join-Path $src $rel))
        $stor.ScriptFiles[$rel] = [Activator]::CreateInstance($ft, @($utf8, $content))
    }
    Write-Host "added '$TaskName' (VB, $($stor.ScriptFiles.Count) project files)"

    # ---- 4. sequence it ----------------------------------------------------
    if ($After -and $Before) {
        $a = $pkg.Executables[$After]; $b = $pkg.Executables[$Before]
        $existing = @($pkg.PrecedenceConstraints | Where-Object {
            $_.PrecedenceExecutable.GetType().GetProperty('Name').GetValue($_.PrecedenceExecutable) -eq $After -and
            $_.ConstrainedExecutable.GetType().GetProperty('Name').GetValue($_.ConstrainedExecutable) -eq $Before })
        foreach ($c in $existing) { $pkg.PrecedenceConstraints.Remove($c) }
        $c1 = $pkg.PrecedenceConstraints.Add($a, $exec)
        $c1.Value = [Microsoft.SqlServer.Dts.Runtime.DTSExecResult]::Success
        $c2 = $pkg.PrecedenceConstraints.Add($exec, $b)
        $c2.Value = [Microsoft.SqlServer.Dts.Runtime.DTSExecResult]::Success
        Write-Host "sequenced $After -> $TaskName -> $Before"
    }

    $app.SaveToXml((Resolve-Path $Package), $pkg, $null)
    Write-Host "saved $Package" -ForegroundColor Green

    # ---- 5. prove it reloads (the step that historically failed) -----------
    $check = (New-Object Microsoft.SqlServer.Dts.Runtime.Application).LoadPackage((Resolve-Path $Package), $null)
    $ct = $check.Executables[$TaskName].InnerObject
    Write-Host ("verified reload: language={0}, files={1}, assembly={2:N0} bytes" -f `
        $ct.ScriptLanguage, $ct.ScriptStorage.ScriptFiles.Count, $ct.ScriptStorage.AssemblyCode.Length) -ForegroundColor Green
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
