using System.Text;
using Ssis.Extract.Codegen;
using Ssis.Extract.Model.Package;

namespace Ssis.Extract.Cli;

/// <summary>
/// <c>ssisx generate</c> -- the `ssisx generate` plan's Phase 3: the thin CLI shell over
/// <see cref="Ssis.Extract.Codegen.PackageGenerator"/> (all per-package orchestration lives
/// there now, specifically so it's unit/golden-file-testable without PackageLoader/file I/O
/// -- see <c>Ssis.Extract.Codegen.Tests/GenerateGoldenFileTests.cs</c>). This file is left
/// with only what's genuinely CLI-shaped: argument parsing, calling PackageLoader, writing
/// files to disk, and the repo-wide fixed files that only make sense once per --out
/// directory (Directory.Build.props etc. -- see WriteFixedFiles below).
///
/// Scenario coverage matches the plan's milestone 1 table exactly: Flat File Source ->
/// [Derived Column] -> OLE DB Destination (fast load only), N data flows per package,
/// Execute SQL Task as pre-load. Anything else degrades to a <see cref="GenerationGap"/>,
/// never a guess -- see PackageGenerator's own comments for exactly which gate reports which
/// gap.
/// </summary>
internal static class GenerateCommand
{
    public static int Run(string[] args)
    {
        string? input = null;
        string? outDir = null;
        var recursive = false;
        string? namespacePrefix = null;

        for (var i = 0; i < args.Length; i++)
        {
            try
            {
                switch (args[i])
                {
                    case "--input": input = RequireValue(args, ref i, "--input"); break;
                    case "--out": outDir = RequireValue(args, ref i, "--out"); break;
                    case "--recursive": recursive = true; break;
                    case "--namespace-prefix": namespacePrefix = RequireValue(args, ref i, "--namespace-prefix"); break;
                    default:
                        Console.Error.WriteLine($"error: unknown generate option '{args[i]}'");
                        return 2;
                }
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 2;
            }
        }

        if (input is null || outDir is null)
        {
            Console.Error.WriteLine("error: --input and --out are required. Run 'ssisx --help'.");
            return 2;
        }

        PackageLoader.LoadResult loaded;
        try
        {
            loaded = PackageLoader.Load(input, noRedact: false, recursive);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
        using var _ = loaded;

        if (loaded.Packages.Count == 0)
        {
            Console.Error.WriteLine("error: no package could be loaded -- nothing to generate.");
            return 2;
        }

        var generateDir = Path.Combine(outDir, "generate");
        var results = new List<PackageGenerateResult>();

        foreach (var package in loaded.Packages)
        {
            var result = PackageGenerator.Generate(package, namespacePrefix);
            results.Add(result);

            foreach (var file in result.Files)
            {
                var path = Path.Combine(generateDir, package.ObjectName, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, file.Content);
            }
        }

        var fixedFileGaps = WriteFixedFiles(generateDir, loaded.Packages, results);

        var reportPath = Path.Combine(outDir, "generate-report.md");
        WriteReport(reportPath, results, loaded.Failures, fixedFileGaps);

        var totalFiles = results.Sum(r => r.Files.Count);
        var totalGaps = results.Sum(r => r.Gaps.Count) + fixedFileGaps.Count;
        Console.WriteLine($"generate: {totalFiles} file(s) written across {results.Count} package(s), {totalGaps} gap(s) -- see {reportPath}");

        return totalGaps > 0 ? 3 : 0;
    }

    /// <summary>
    /// Files written once per --out directory, not per package: Directory.Build.props /
    /// Directory.Packages.props (fixed content -- the exact contract Etl.Core and every
    /// generated .csproj already assume, see D:\PoC\SSIS_Rewrite's own copies) and
    /// Shared/appsettings.Shared.json (TargetDatabase.Server/Database filled in from
    /// whichever generated package resolved one -- see ResolveDatabaseAuth's caller).
    ///
    /// Deliberately does NOT copy Etl.Core itself: that library is hand-written, "written
    /// once" runtime plumbing (see the generate plan's own context table), not something
    /// derivable from a .dtsx the way every file above it is. A client site running this
    /// tool against its own 50 packages has no D:\PoC\SSIS_Rewrite to copy from anyway --
    /// hardcoding a path to this PoC's own sibling repo would make the generator
    /// non-portable for the one thing it exists to be used for. The report says so plainly
    /// rather than silently producing an output that can't build.
    /// </summary>
    private static List<GenerationGap> WriteFixedFiles(string generateDir, List<PackageSpec> packages, List<PackageGenerateResult> results)
    {
        Directory.CreateDirectory(generateDir);

        File.WriteAllText(Path.Combine(generateDir, "Directory.Build.props"), JoinLines(
        [
            "<Project>",
            "",
            "  <PropertyGroup>",
            "    <TargetFramework>net10.0</TargetFramework>",
            "    <LangVersion>latest</LangVersion>",
            "    <Nullable>enable</Nullable>",
            "    <ImplicitUsings>enable</ImplicitUsings>",
            "    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>",
            "    <InvariantGlobalization>false</InvariantGlobalization>",
            "  </PropertyGroup>",
            "",
            "</Project>",
        ]));

        File.WriteAllText(Path.Combine(generateDir, "Directory.Packages.props"), JoinLines(
        [
            "<Project>",
            "",
            "  <PropertyGroup>",
            "    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>",
            "    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>",
            "  </PropertyGroup>",
            "",
            "  <ItemGroup>",
            "    <PackageVersion Include=\"CsvHelper\" Version=\"33.1.0\" />",
            "    <PackageVersion Include=\"MailKit\" Version=\"4.17.0\" />",
            "    <PackageVersion Include=\"Microsoft.EntityFrameworkCore.SqlServer\" Version=\"10.0.11\" />",
            "    <PackageVersion Include=\"Microsoft.Extensions.Hosting\" Version=\"10.0.11\" />",
            "    <PackageVersion Include=\"Microsoft.Extensions.Configuration.UserSecrets\" Version=\"10.0.11\" />",
            "    <PackageVersion Include=\"System.Text.Encoding.CodePages\" Version=\"10.0.11\" />",
            "  </ItemGroup>",
            "",
            "</Project>",
        ]));

        var gaps = new List<GenerationGap>();
        var resolved = results.FirstOrDefault(r => r.TargetServer is not null);
        var server = resolved?.TargetServer;
        var database = resolved?.TargetDatabase;
        if (server is null)
        {
            gaps.Add(new GenerationGap("Shared/appsettings.Shared.json", "no generated package resolved a target server/database -- TargetDatabase.Server/Database were written as TODO placeholders; fill in manually"));
            server = "TODO";
            database = "TODO";
        }

        Directory.CreateDirectory(Path.Combine(generateDir, "Shared"));
        File.WriteAllText(Path.Combine(generateDir, "Shared", "appsettings.Shared.json"), JoinLines(
        [
            "{",
            "  \"TargetDatabase\": {",
            $"    \"Server\": \"{server.Replace("\\", "\\\\")}\",",
            $"    \"Database\": \"{database}\",",
            "    \"Encrypt\": true,",
            "    \"TrustServerCertificate\": true,",
            "    \"ConnectTimeoutSeconds\": 15",
            "  },",
            "  \"BulkCopy\": {",
            "    \"UseTableLock\": true,",
            "    \"BatchSize\": 0,",
            "    \"TimeoutSeconds\": 0,",
            "    \"NotifyAfter\": 10000",
            "  },",
            "  \"Notification\": {",
            "    \"Enabled\": true,",
            "    \"SmtpHost\": \"localhost\",",
            "    \"SmtpPort\": 25,",
            "    \"UseSsl\": false,",
            "    \"FromAddress\": \"ssis-generated@example.local\",",
            "    \"FromName\": \"SSIS Generated\"",
            "  },",
            "  \"Logging\": {",
            "    \"LogLevel\": {",
            "      \"Default\": \"Information\",",
            "      \"Microsoft\": \"Warning\"",
            "    }",
            "  }",
            "}",
        ]));

        var slnLines = new List<string> { "<Solution>", "  <Folder Name=\"/src/\">", "    <Project Path=\"Etl.Core/Etl.Core.csproj\" />" };
        foreach (var r in results.Where(r => r.Files.Any(f => f.RelativePath == "Program.cs")).OrderBy(r => r.PackageName, StringComparer.Ordinal))
            slnLines.Add($"    <Project Path=\"{r.PackageName}/{r.PackageName}.csproj\" />");
        slnLines.Add("  </Folder>");
        slnLines.Add("</Solution>");
        File.WriteAllText(Path.Combine(generateDir, "Generated.slnx"), JoinLines(slnLines));

        gaps.Add(new GenerationGap("Etl.Core", "this generator does not produce Etl.Core -- it is hand-written shared plumbing (SqlBulkCopy wrapper, CSV reader, host, email notifier), not derivable from any .dtsx. Copy a compatible Etl.Core project to generate/Etl.Core/ before building -- see D:\\PoC\\SSIS_Rewrite\\src\\Etl.Core for the reference implementation this generator's output targets."));

        return gaps;
    }

    private static void WriteReport(string path, List<PackageGenerateResult> results, List<PackageLoader.LoadFailure> loadFailures, List<GenerationGap> fixedFileGaps)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ssisx generate -- report");
        sb.AppendLine();
        sb.AppendLine("Phase 3 of the `ssisx generate` plan: one runnable C# project per package, written under `generate/<Package>/`, plus Directory.Build.props/Directory.Packages.props/Shared/appsettings.Shared.json/Generated.slnx written once under `generate/`. Every generated file carries no hand-editing expectation -- regenerate freely; anything a human needs to add belongs in the package's own appsettings.json or a sibling file this tool never writes.");
        sb.AppendLine();

        var totalFiles = results.Sum(r => r.Files.Count);
        var totalGaps = results.Sum(r => r.Gaps.Count) + fixedFileGaps.Count;
        sb.AppendLine($"**{totalFiles} file(s) written across {results.Count} package(s), {totalGaps} gap(s).**");
        sb.AppendLine();

        sb.AppendLine("| Package | Files written | Gaps |");
        sb.AppendLine("|---|---|---|");
        foreach (var r in results.OrderBy(r => r.PackageName, StringComparer.Ordinal))
            sb.AppendLine($"| {r.PackageName} | {r.Files.Count} | {r.Gaps.Count} |");
        sb.AppendLine();

        sb.AppendLine("## Gaps -- not silently dropped, each has a reason");
        sb.AppendLine();
        var anyGaps = results.Any(r => r.Gaps.Count > 0) || fixedFileGaps.Count > 0;
        if (!anyGaps)
        {
            sb.AppendLine("_None._");
        }
        else
        {
            sb.AppendLine("| Package | Location | Reason |");
            sb.AppendLine("|---|---|---|");
            foreach (var r in results.OrderBy(r => r.PackageName, StringComparer.Ordinal))
                foreach (var gap in r.Gaps)
                    sb.AppendLine($"| {r.PackageName} | {gap.Location} | {gap.Reason} |");
            foreach (var gap in fixedFileGaps)
                sb.AppendLine($"| _(shared)_ | {gap.Location} | {gap.Reason} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Packages that failed to load -- not generated at all");
        sb.AppendLine();
        if (loadFailures.Count == 0)
        {
            sb.AppendLine("_None._");
        }
        else
        {
            sb.AppendLine("| Path | Kind | Reason |");
            sb.AppendLine("|---|---|---|");
            foreach (var f in loadFailures)
                sb.AppendLine($"| {f.Path} | {f.Kind} | {f.Reason} |");
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static string RequireValue(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"{flag} requires a value");
        i++;
        return args[i];
    }

    /// <summary>Same LF-only-with-trailing-newline rule Ssis.Extract.Codegen's own
    /// Rendering.JoinLines follows (that type is internal to that assembly, so this is a
    /// separate copy, not a shared one) -- for the repo-wide fixed files this command writes
    /// directly rather than through an emitter.</summary>
    private static string JoinLines(IEnumerable<string> lines) => string.Join('\n', lines) + "\n";
}
