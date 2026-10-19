<#
.SYNOPSIS
  Builds the self-contained ssisx.exe (extractor) and svk.exe (walkthrough/sample data) next to these scripts.

.DESCRIPTION
  Run-Survey.ps1's whole premise is that a client machine needs nothing installed --
  no .NET runtime, no Visual Studio, no network access. That only holds if ssisx.exe is
  actually a self-contained win-x64 publish (~200 files, ~80 MB: the CLR itself plus every
  managed dependency), not the small framework-dependent exe an ordinary `dotnet build`
  produces. This script is the one command that produces the right kind of build, in the
  right place -- directly into this scripts/ folder, alongside the two wrapper scripts --
  so nobody has to remember the -r/--self-contained flags or the target directory by hand.

  Safe to re-run any time the extractor's source changes; it always publishes fresh. The
  published output is intentionally NOT committed to git (see .gitignore in this tool's
  root) -- it is ~80 MB of rebuildable binary, not source.

.EXAMPLE
  .\Publish-Survey.ps1
  .\Run-Survey.ps1 -PackageFolder "D:\Some\Client\Packages"
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$cliProject = Join-Path $scriptDir '..\src\Ssis.Extract.Cli\Ssis.Extract.Cli.csproj'
$svkProject = Join-Path $scriptDir '..\..\SsisValidationKit\src\SvkCli\SvkCli.csproj'

if (-not (Test-Path $cliProject)) {
    Write-Error "Ssis.Extract.Cli.csproj not found at $cliProject -- run this from its checked-in location (Tools/SsisExtractor/scripts/)."
}
if (-not (Test-Path $svkProject)) {
    Write-Error "SvkCli.csproj not found at $svkProject -- SsisValidationKit must sit beside SsisExtractor under Tools/."
}

# Publishes one CLI as a self-contained single-file win-x64 exe directly into this scripts/
# folder. PublishSingleFile bundles the CoreCLR runtime and every managed/native dependency
# into one .exe; EnableCompressionInSingleFile roughly halves it; DebugType=none skips the
# stray .pdb files a client has no use for.
function Publish-Cli {
    param([string]$Project, [string]$ExeName)

    Write-Host "Publishing self-contained single-file $ExeName (win-x64) into $scriptDir ..."
    & dotnet publish $Project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=none `
        -o $scriptDir --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish of $ExeName failed with exit code $LASTEXITCODE."
    }

    $exe = Join-Path $scriptDir $ExeName
    if (-not (Test-Path $exe)) {
        Write-Error "Publish reported success but $ExeName is missing from $scriptDir -- something is wrong with the publish output path."
    }
    return $exe
}

$exe = Publish-Cli -Project $cliProject -ExeName 'ssisx.exe'
$svkExe = Publish-Cli -Project $svkProject -ExeName 'svk.exe'
$fileCount = @(Get-ChildItem -Path $scriptDir -File -Recurse).Count
$sizeMb = [Math]::Round((Get-ChildItem -Path $scriptDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)

Write-Host ""
Write-Host "Done: $exe, $svkExe"
Write-Host "  ($fileCount files, $sizeMb MB total in $scriptDir)"
Write-Host ""
Write-Host "To ship to a client: zip this whole scripts/ folder (Run-Survey.ps1, Run-Survey.bat,"
Write-Host "ssisx.exe, and its runtime files) and send that. Do not send scripts/ alone without"
Write-Host "re-running this first if the extractor's source has changed since the last publish."
