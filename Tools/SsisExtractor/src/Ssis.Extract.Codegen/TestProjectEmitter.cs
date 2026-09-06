namespace Ssis.Extract.Codegen;

/// <summary>
/// Emits <c>{PackageName}.Tests/{PackageName}.Tests.csproj</c> -- an ordinary xUnit test project,
/// SIBLING to (never nested inside) the main package's own project directory. That placement is
/// deliberate, not incidental: the main project's own SDK-style <c>**/*.cs</c> compile glob would
/// otherwise pick up any test file nested under it, and the main project never references xUnit
/// (see <c>PackageGenerateResult.SiblingFiles</c>'s own doc comment for the rest of that wiring).
///
/// No <c>&lt;TargetFramework&gt;</c>/version overrides here -- this project sits under the same
/// <c>generate/Directory.Build.props</c> as every other generated project, so it inherits the
/// same framework automatically; the xUnit/Test SDK package VERSIONS are pinned centrally in
/// <c>GenerateCommand.BuildDirectoryPackagesProps</c>, the same "versions belong to the
/// solution-wide Directory.Packages.props, not to any one emitter" rule every other package
/// reference in a generated .csproj already follows.
/// </summary>
public static class TestProjectEmitter
{
    public static GeneratedFile Emit(string packageName)
    {
        var lines = new List<string>
        {
            "<Project Sdk=\"Microsoft.NET.Sdk\">",
            "",
            "  <PropertyGroup>",
            "    <IsPackable>false</IsPackable>",
            "    <IsTestProject>true</IsTestProject>",
            "  </PropertyGroup>",
            "",
            "  <ItemGroup>",
            $"    <ProjectReference Include=\"..\\{packageName}\\{packageName}.csproj\" />",
            "  </ItemGroup>",
            "",
            "  <ItemGroup>",
            "    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" />",
            "    <PackageReference Include=\"xunit\" />",
            "    <PackageReference Include=\"xunit.runner.visualstudio\" />",
            "    <PackageReference Include=\"coverlet.collector\">",
            "      <PrivateAssets>all</PrivateAssets>",
            "      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>",
            "    </PackageReference>",
            "  </ItemGroup>",
            "",
            // Links (never copies) the main project's own TestData/ folder alongside this test
            // project's own output -- a future richer, Tier-B-driven test (Docs/Generated-Tests-
            // Plan.md's own Phase 2/3) can read real sample data the same way the main package's
            // own appsettings.Development.json does, with one physical copy on disk, not two.
            // Matches zero items (harmless) until a LOCAL-DATA fill supplies real files.
            "  <ItemGroup>",
            $"    <None Include=\"..\\{packageName}\\TestData\\**\" Link=\"TestData\\%(RecursiveDir)%(Filename)%(Extension)\" CopyToOutputDirectory=\"PreserveNewest\" />",
            "  </ItemGroup>",
            "",
            // Tier-A (Docs/Generated-Tests-Plan.md): 2-3 deterministic, synthesized rows per
            // CSV/fixed-width source, physically emitted INSIDE this test project (not linked from
            // the main one, unlike TestData/ above) -- SampleDataEmitter's own output, zero AI,
            // zero fills, what makes the starter source-read tests green immediately after a fresh
            // `ssisx generate`.
            "  <ItemGroup>",
            "    <None Include=\"SampleData\\**\" CopyToOutputDirectory=\"PreserveNewest\" />",
            "  </ItemGroup>",
            "",
            "</Project>",
        };

        return new GeneratedFile($"{packageName}.Tests/{packageName}.Tests.csproj", Rendering.JoinLines(lines));
    }
}
