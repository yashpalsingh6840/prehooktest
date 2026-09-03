using System.Text;
using System.Text.Json;
using Ssis.Extract.Codegen;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Serialization;

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
        // Seams are the default (see the --unsafe-skip-seams case below for why): without
        // them, a Script Task/Script Component's logic is silently omitted from otherwise
        // clean-building, clean-running generated code -- exactly the "compiles and runs but
        // is wrong" failure class this tool otherwise refuses to allow. --seams is kept as an
        // accepted no-op so any script/doc still passing it explicitly keeps working.
        var seams = true;
        string? fillsDir = null;
        var packageNames = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            try
            {
                switch (args[i])
                {
                    case "--input": input = RequireValue(args, ref i, "--input"); break;
                    case "--out": outDir = RequireValue(args, ref i, "--out"); break;
                    case "--recursive": recursive = true; break;
                    // Generation is the command a real 73-package portfolio least wants run
                    // all-at-once against -- --package (repeatable, comma-splittable) is how a
                    // caller (human or an AI coding assistant with a limited token budget) asks
                    // for one package, or a small named batch, without touching the rest.
                    case "--package":
                        var pkgArg = RequireValue(args, ref i, "--package");
                        packageNames.AddRange(pkgArg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                        break;
                    case "--namespace-prefix": namespacePrefix = RequireValue(args, ref i, "--namespace-prefix"); break;
                    case "--seams": seams = true; break;
                    // Opts back into the pre-this-flip behavior: a Script Task/Script
                    // Component's logic is silently dropped -- the generated project builds
                    // and runs cleanly with those columns/tasks missing, no compile-time
                    // signal at all. Named "unsafe" deliberately, not "--no-seams" or
                    // "--skip-seams", so the risk is visible at the call site, not just in
                    // --help.
                    case "--unsafe-skip-seams": seams = false; break;
                    case "--fills": fillsDir = RequireValue(args, ref i, "--fills"); break;
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
            loaded = PackageLoader.Load(input, noRedact: false, recursive, packageNames);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
        using var _ = loaded;

        if (loaded.Packages.Count == 0)
        {
            Console.Error.WriteLine(packageNames.Count > 0
                ? "error: --package matched no packages under this --input -- nothing to generate."
                : "error: no package could be loaded -- nothing to generate.");
            return 2;
        }

        var generateDir = Path.Combine(outDir, "generate");
        var gapsDir = Path.Combine(outDir, "gaps");
        // Fills default to a sibling of generate/, NOT a subdirectory of it: generate/ is
        // regenerated freely (this command's own report says so), while a decisions file is
        // hand-maintained work product that must survive that. Same reasoning, and the same
        // never-overwrite rule, as ConformanceCommand's claims directory.
        fillsDir = Path.GetFullPath(fillsDir ?? Path.Combine(outDir, "fills"));
        var results = new List<PackageGenerateResult>();
        var allGaps = new List<GapSpec>();
        var decisionOutcomes = new List<(string Package, GapDecisionOutcome Outcome)>();

        foreach (var package in loaded.Packages)
        {
            var decisions = LoadDecisions(fillsDir, package.ObjectName);
            var result = PackageGenerator.Generate(package, namespacePrefix, decisions, seams);
            results.Add(result);
            decisionOutcomes.AddRange(decisions.Outcomes.Concat(decisions.Orphans())
                .Select(o => (package.ObjectName, o)));

            foreach (var file in result.Files)
            {
                var path = Path.Combine(generateDir, package.ObjectName, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, file.Content);
            }

            // Work packets live under <out>/gaps/, deliberately NOT inside <out>/generate/ --
            // that tree is "the buildable solution" and nothing else, so a stray .md never ends up
            // in a project directory someone is about to `dotnet build`.
            var packetResult = AiPacketEmitter.Emit(package, result.Gaps);
            allGaps.AddRange(packetResult.Gaps);
            foreach (var packet in packetResult.Packets)
            {
                var path = Path.Combine(gapsDir, package.ObjectName, packet.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, packet.Content);
            }
        }

        var fixedFileGaps = WriteFixedFiles(generateDir, loaded.Packages, results);

        var gapsPath = Path.Combine(outDir, "gaps.json");
        StableJsonWriter.WriteToFile(allGaps, gapsPath);

        var reportPath = Path.Combine(outDir, "generate-report.md");
        WriteReport(reportPath, results, loaded.Failures, fixedFileGaps, allGaps, decisionOutcomes);

        var totalFiles = results.Sum(r => r.Files.Count);
        var totalGaps = results.Sum(r => r.Gaps.Count) + fixedFileGaps.Count;
        var packets = allGaps.Count(g => g.PacketPath is not null);
        Console.WriteLine($"generate: {totalFiles} file(s) written across {results.Count} package(s), {totalGaps} gap(s) -- see {reportPath}");
        Console.WriteLine($"  {packets} work packet(s) written to {gapsDir}  (paste one into a chat window; see gaps.json for the index)");
        if (decisionOutcomes.Count > 0)
        {
            var applied = decisionOutcomes.Count(o => o.Outcome.Status == GapDecisionStatus.Applied);
            var refused = decisionOutcomes.Count - applied;
            Console.WriteLine($"  {applied} confirmed decision(s) applied from {fillsDir}" +
                              $"{(refused > 0 ? $", {refused} NOT used -- see the report" : "")}");
        }

        return totalGaps > 0 ? 3 : 0;
    }

    /// <summary>
    /// Reads <c>fills/&lt;Package&gt;.decisions.json</c> if it exists. Deliberately does NOT write
    /// a stub when it doesn't -- unlike a conformance claims file, where a stub of all-Pending
    /// rules is a useful starting point, a decisions file is only worth creating once there is an
    /// answered work packet to record, and an empty one on disk would just be noise in every
    /// output directory.
    ///
    /// A malformed file is a hard error rather than a silent "no decisions": someone recorded a
    /// decision and it is not taking effect, which must not look like never having made one.
    /// </summary>
    private static GapDecisions LoadDecisions(string fillsDir, string packageName)
    {
        var path = Path.Combine(fillsDir, $"{packageName}.decisions.json");
        if (!File.Exists(path)) return GapDecisions.None;

        var parsed = JsonSerializer.Deserialize<Dictionary<string, GapDecisionSpec>>(File.ReadAllText(path), DecisionReadOptions)
            ?? throw new InvalidOperationException($"'{path}' parsed as null -- expected a JSON object keyed by GapId.");
        return new GapDecisions(parsed);
    }

    private static readonly JsonSerializerOptions DecisionReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Files written once per --out directory, not per package: Directory.Build.props /
    /// Directory.Packages.props (fixed content -- the exact contract Etl.Core and every
    /// generated .csproj already assume, see Tools/Etl.Core's own copies alongside this tool)
    /// and Shared/appsettings.Shared.json (TargetDatabase.Server/Database filled in from
    /// whichever generated package resolved one -- see ResolveDatabaseAuth's caller).
    ///
    /// Deliberately does NOT copy Etl.Core itself: that library is hand-written, "written
    /// once" runtime plumbing (see the generate plan's own context table), not something
    /// derivable from a .dtsx the way every file above it is. This is why a portable copy
    /// ships alongside this tool at Tools/Etl.Core (sibling of Tools/SsisExtractor) rather
    /// than the generator hardcoding a path to this PoC's own development repo -- a client
    /// site running this tool against its own 50 packages gets the same Etl.Core/ folder
    /// that ships with ssisx, not a dead reference to a repo that only exists here. The
    /// report says so plainly rather than silently producing an output that can't build.
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
            "    <PackageVersion Include=\"ExcelDataReader\" Version=\"3.7.0\" />",
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

        gaps.Add(new GenerationGap("Etl.Core", "this generator does not produce Etl.Core -- it is hand-written shared plumbing (SqlBulkCopy wrapper, CSV reader, host, email notifier), not derivable from any .dtsx. Copy the Etl.Core folder shipped alongside this tool (Tools/Etl.Core, a sibling of Tools/SsisExtractor) to generate/Etl.Core/ before building."));

        return gaps;
    }

    private static void WriteReport(string path, List<PackageGenerateResult> results, List<PackageLoader.LoadFailure> loadFailures, List<GenerationGap> fixedFileGaps, List<GapSpec> allGaps, List<(string Package, GapDecisionOutcome Outcome)> decisionOutcomes)
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
            sb.AppendLine("Each gap carries a stable `GapId` and a tier. **Tier 1/2 gaps have a work packet** under");
            sb.AppendLine("`gaps/<Package>/` -- a self-contained brief you can paste into a chat window. **Tier 3 gaps");
            sb.AppendLine("deliberately do not**: those are missing tool support, and closing them per-package by hand");
            sb.AppendLine("would hide one systemic emitter gap behind N one-off patches. See `gaps.json` for the index.");
            sb.AppendLine();
            sb.AppendLine("| Package | Tier | GapId | Reason |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var g in allGaps)
                sb.AppendLine($"| {g.Package} | {TierLabel(g)} | `{g.GapId}` | {g.Reason} |");
            foreach (var gap in fixedFileGaps)
                sb.AppendLine($"| _(shared)_ | advisory | `{gap.Location}` | {gap.Reason} |");
        }
        sb.AppendLine();

        if (decisionOutcomes.Count > 0)
        {
            sb.AppendLine("## Tier-1 decisions -- what was applied, and what was refused");
            sb.AppendLine();
            sb.AppendLine("Confirmed answers read from `fills/<Package>.decisions.json`, which `ssisx` never writes.");
            sb.AppendLine("A decision is applied only when a human confirmed it, it is complete for its gap kind, and it");
            sb.AppendLine("still matches the evidence it was made against. Anything else is listed here and NOT used --");
            sb.AppendLine("an unconfirmed proposal quietly taking effect is the one outcome this whole mechanism exists");
            sb.AppendLine("to prevent.");
            sb.AppendLine();
            sb.AppendLine("| Package | Status | GapId | Detail |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var (pkg, outcome) in decisionOutcomes)
                sb.AppendLine($"| {pkg} | {outcome.Status} | `{outcome.GapId}` | {outcome.Detail} |");
            sb.AppendLine();
        }

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

    /// <summary>Tier 1/2 read as actionable work items; tier 3 reads as an emitter feature request. The packet marker is what a reader scans for.</summary>
    private static string TierLabel(GapSpec gap) => gap.Tier switch
    {
        GapTier.MissingDatum => "1 -- datum (packet)",
        GapTier.MissingLogic => "2 -- logic (packet)",
        GapTier.MissingToolSupport => "3 -- tool",
        _ => "advisory",
    };

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
