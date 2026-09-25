<#
.SYNOPSIS
    Phase 0.4 of the independent-review fix plan (`C:\Users\yashpal.singh01\.claude\plans\
    snappy-skipping-grove.md`) -- a script-level regression guard for R4 (`Run-Coverage.ps1`'s own
    exit-code bug), since nothing under `tests/` can exercise a `.ps1` file directly.

.DESCRIPTION
    Builds a scratch "generated root" containing exactly one `{Package}.Tests.csproj` -- the shape
    `Run-Coverage.ps1` expects directly under `-GeneratedRoot` -- with a single, deliberately
    failing `[Fact]`, runs `Run-Coverage.ps1` against it, and asserts its exit code is non-zero.

    Today (before Phase 1.1 lands) this assertion FAILS: `Run-Coverage.ps1:183`'s
    `($rows | Where-Object {...}).Count -gt 0` returns a bare `PSCustomObject` (no `.Count`
    member) when exactly one row matches, so the comparison is `$null -gt 0` == `$false`, and the
    script exits 0 even though the one test project it just ran is red. This script is written to
    demonstrate that bug now and to gate its own fix later -- run it after 1.1 lands and confirm
    it goes from FAIL to PASS.

    Not an xunit test (there is no C# code path here to call into -- `Run-Coverage.ps1` shells out
    to `dotnet test` itself), so this is a plain PowerShell script with its own PASS/FAIL exit
    code, wired into CI (Phase 0.5) as its own step alongside `dotnet test`.

.OUTPUTS
    Exit 0 if `Run-Coverage.ps1` correctly reports a non-zero exit for a project with a failing
    test (the fix is in and working); exit 1 otherwise (today's bug, or a real new problem).
#>
$ErrorActionPreference = 'Stop'

$scratch = Join-Path ([IO.Path]::GetTempPath()) ("ssisx-run-coverage-test-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $scratch | Out-Null

try {
    $pkgTestsDir = Join-Path $scratch "FailingPkg.Tests"
    New-Item -ItemType Directory -Force -Path $pkgTestsDir | Out-Null

    Set-Content -Path (Join-Path $pkgTestsDir "FailingPkg.Tests.csproj") -Value @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.0" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.8.0" />
    <PackageReference Include="xunit" Version="2.5.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.5.3" />
  </ItemGroup>
</Project>
'@ -Encoding utf8

    Set-Content -Path (Join-Path $pkgTestsDir "FailingTests.cs") -Value @'
using Xunit;

namespace FailingPkg.Tests;

public class FailingTests
{
    // Deliberately red -- Run-Coverage.ps1 must report this package as FAILED and exit non-zero.
    [Fact]
    public void ThisTestAlwaysFails() => Assert.True(false, "deliberately failing -- see Run-Coverage.Tests.ps1");
}
'@ -Encoding utf8

    $runCoverage = Join-Path $PSScriptRoot "..\Run-Coverage.ps1"
    & $runCoverage -GeneratedRoot $scratch -Configuration Release
    $exitCode = $LASTEXITCODE

    $reportPath = Join-Path $scratch "coverage-report.md"
    $reportExists = Test-Path $reportPath
    $reportSaysFail = $reportExists -and ((Get-Content $reportPath -Raw) -match 'FAIL')

    Write-Host ""
    Write-Host "Run-Coverage.ps1 exit code: $exitCode"
    Write-Host "coverage-report.md written: $reportExists"
    Write-Host "coverage-report.md names the failure: $reportSaysFail"

    if ($exitCode -ne 0 -and $reportSaysFail) {
        Write-Host "PASS -- Run-Coverage.ps1 correctly reported the failing test project." -ForegroundColor Green
        exit 0
    }
    else {
        Write-Host "FAIL -- Run-Coverage.ps1 returned exit $exitCode for a package with a failing test (expected non-zero). This is R4 (Run-Coverage.ps1:183's Where-Object/.Count bug) until Phase 1.1 lands." -ForegroundColor Red
        exit 1
    }
}
finally {
    Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
}
