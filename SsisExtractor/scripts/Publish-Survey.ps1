<#
.SYNOPSIS
  Builds the self-contained ssisx.exe that Run-Survey.ps1 / Run-Survey.bat need next to them.

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

if (-not (Test-Path $cliProject)) {
    Write-Error "Ssis.Extract.Cli.csproj not found at $cliProject -- run this from its checked-in location (Tools/SsisExtractor/scripts/)."
}

Write-Host "Publishing self-contained single-file ssisx.exe (win-x64) into $scriptDir ..."
# PublishSingleFile bundles the whole CoreCLR runtime AND every managed/native dependency
# into one .exe -- without it, --self-contained alone still produces a correct build, but
# as ~210 loose files (the runtime DLLs, ICU/locale folders, etc.) instead of one, which is
# a worse thing to zip and hand to a client. IncludeNativeLibrariesForSelfExtract folds the
# native (non-managed) dependencies in too rather than extracting them beside the exe.
# EnableCompressionInSingleFile roughly halves the result (measured: 75 MB -> ~36 MB here).
# DebugType=none skips generating the half-dozen stray .pdb files a client has no use for --
# without it, scripts/ ends up with 5 loose .pdb files sitting next to the one .exe.
& dotnet publish $cliProject -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    -o $scriptDir --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE."
}

$exe = Join-Path $scriptDir 'ssisx.exe'
if (-not (Test-Path $exe)) {
    Write-Error "Publish reported success but ssisx.exe is missing from $scriptDir -- something is wrong with the publish output path."
}

$fileCount = @(Get-ChildItem -Path $scriptDir -File -Recurse).Count
$sizeMb = [Math]::Round((Get-ChildItem -Path $scriptDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)

Write-Host ""
Write-Host "Done: $exe"
Write-Host "  ($fileCount files, $sizeMb MB total in $scriptDir)"
Write-Host ""
Write-Host "To ship to a client: zip this whole scripts/ folder (Run-Survey.ps1, Run-Survey.bat,"
Write-Host "ssisx.exe, and its runtime files) and send that. Do not send scripts/ alone without"
Write-Host "re-running this first if the extractor's source has changed since the last publish."
