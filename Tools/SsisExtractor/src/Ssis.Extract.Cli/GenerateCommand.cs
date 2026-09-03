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
        string? etlCorePath = null;
        var packageNames = new List<string>();
        // net10.0 stays the default -- omitting --framework changes nothing for an existing
        // caller. net8.0 exists because Etl.Core's own EF Core SqlServer provider (10.0.11)
        // targets net10.0 ONLY (confirmed against the real published package, not assumed);
        // the only way to build against net8.0 at all is a DIFFERENT EF Core major version
        // (9.0.15, the newest that still targets net8.0), so this flag has to pick a package
        // version, not just a TFM string. Everything else (Microsoft.Extensions.Hosting/
        // Configuration.UserSecrets, System.Text.Encoding.CodePages, all pinned at 10.0.11)
        // already multi-targets net8.0 for real and stays unchanged either way.
        var framework = "net10.0";

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
                    // Folds the "copy Etl.Core alongside the generated output" step into this
                    // command instead of leaving it as a separate manual step someone (a human,
                    // or an AI assistant with limited budget/attention) can forget -- exactly
                    // what happened running this through GitHub Copilot: the tool correctly
                    // reported the gap, but nothing forced the follow-up copy to actually run.
                    case "--etl-core": etlCorePath = RequireValue(args, ref i, "--etl-core"); break;
                    case "--framework":
                        var fw = RequireValue(args, ref i, "--framework");
                        if (!SupportedFrameworks.Contains(fw))
                        {
                            Console.Error.WriteLine($"error: --framework '{fw}' is not supported. Supported values: {string.Join(", ", SupportedFrameworks.OrderBy(f => f, StringComparer.Ordinal))}.");
                            return 2;
                        }
                        framework = fw;
                        break;
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

        var fixedFileGaps = WriteFixedFiles(generateDir, loaded.Packages, results, etlCorePath, framework);

        var gapsPath = Path.Combine(outDir, "gaps.json");
        StableJsonWriter.WriteToFile(allGaps, gapsPath);

        var reportPath = Path.Combine(outDir, "generate-report.md");
        WriteReport(reportPath, results, loaded.Failures, fixedFileGaps, allGaps, decisionOutcomes);

        // Written unconditionally, at the --out ROOT (not inside generate/, not inside gaps/)
        // specifically so it is the first thing visible to anyone -- a person or an AI coding
        // assistant -- who opens this output folder directly, regardless of which repo/workspace
        // root their tool auto-loaded instructions from (or didn't). This exists because relying
        // solely on Tools/.github/copilot-instructions.md failed in practice: a caller who opens
        // the GENERATED solution on its own (e.g. Generated.slnx directly in an IDE) never has
        // Tools/ in view at all, so nothing there was ever going to be picked up automatically.
        WriteAiAssistantGuide(Path.Combine(outDir, "HOW-TO-FILL-GAPS.md"), allGaps, fillsDir, outDir);

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

    private static readonly HashSet<string> SupportedFrameworks = new(StringComparer.Ordinal) { "net8.0", "net10.0" };

    /// <summary>
    /// The one package version that actually depends on <paramref name="framework"/> --
    /// Microsoft.EntityFrameworkCore.SqlServer 10.0.11 targets net10.0 ONLY (confirmed against
    /// the real published package), so net8.0 needs a different EF Core MAJOR VERSION (9.0.15,
    /// the newest 9.x that still targets net8.0), not just a different TargetFramework string.
    /// Every other pinned package (Microsoft.Extensions.Hosting/Configuration.UserSecrets,
    /// System.Text.Encoding.CodePages, all at 10.0.11) already multi-targets net8.0 for real
    /// and is unaffected -- confirmed the same way, not assumed by analogy with this one.
    /// </summary>
    private static string EfCoreSqlServerVersionFor(string framework) => framework switch
    {
        "net8.0" => "9.0.15",
        "net10.0" => "10.0.11",
        _ => throw new ArgumentOutOfRangeException(nameof(framework), framework, $"unsupported framework -- expected one of: {string.Join(", ", SupportedFrameworks.OrderBy(f => f, StringComparer.Ordinal))}"),
    };

    private static string[] BuildDirectoryBuildProps(string framework) =>
    [
        "<Project>",
        "",
        "  <PropertyGroup>",
        $"    <TargetFramework>{framework}</TargetFramework>",
        "    <LangVersion>latest</LangVersion>",
        "    <Nullable>enable</Nullable>",
        "    <ImplicitUsings>enable</ImplicitUsings>",
        "    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>",
        "    <InvariantGlobalization>false</InvariantGlobalization>",
        "  </PropertyGroup>",
        "",
        "</Project>",
    ];

    private static string[] BuildDirectoryPackagesProps(string framework) =>
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
        $"    <PackageVersion Include=\"Microsoft.EntityFrameworkCore.SqlServer\" Version=\"{EfCoreSqlServerVersionFor(framework)}\" />",
        "    <PackageVersion Include=\"Microsoft.Extensions.Hosting\" Version=\"10.0.11\" />",
        "    <PackageVersion Include=\"Microsoft.Extensions.Configuration.UserSecrets\" Version=\"10.0.11\" />",
        "    <PackageVersion Include=\"System.Text.Encoding.CodePages\" Version=\"10.0.11\" />",
        "  </ItemGroup>",
        "",
        "</Project>",
    ];

    /// <summary>
    /// Files written once per --out directory, not per package: Directory.Build.props /
    /// Directory.Packages.props (content driven by <paramref name="framework"/> -- see
    /// BuildDirectoryBuildProps/BuildDirectoryPackagesProps below, and Tools/Etl.Core's own
    /// copies alongside this tool, which must match whatever this method would emit for
    /// net10.0) and Shared/appsettings.Shared.json (TargetDatabase.Server/Database filled in
    /// from whichever generated package resolved one -- see ResolveDatabaseAuth's caller).
    ///
    /// Does NOT copy Etl.Core itself UNLESS <paramref name="etlCorePath"/> is given: that
    /// library is hand-written, "written once" runtime plumbing (see the generate plan's own
    /// context table), not something derivable from a .dtsx the way every file above it is --
    /// this generator has no business assuming where a caller's copy lives, or that one even
    /// exists at all (a caller may only want to inspect the generated source, never build it).
    /// When --etl-core IS given, though, copying it here rather than leaving it as a separate
    /// step is worth doing: a manual follow-up copy is exactly the kind of thing that gets
    /// silently skipped by an agent (or a person) moving fast through a checklist -- confirmed
    /// in practice, not hypothetically, running this through GitHub Copilot. See "Sensitive
    /// credential"-style reasoning elsewhere in this tool for why a MISSING step should fail
    /// loudly rather than be assumed done.
    /// </summary>
    private static List<GenerationGap> WriteFixedFiles(string generateDir, List<PackageSpec> packages, List<PackageGenerateResult> results, string? etlCorePath, string framework)
    {
        Directory.CreateDirectory(generateDir);

        File.WriteAllText(Path.Combine(generateDir, "Directory.Build.props"), JoinLines(BuildDirectoryBuildProps(framework)));
        File.WriteAllText(Path.Combine(generateDir, "Directory.Packages.props"), JoinLines(BuildDirectoryPackagesProps(framework)));

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

        if (etlCorePath is null)
        {
            gaps.Add(new GenerationGap("Etl.Core", "this generator does not produce Etl.Core -- it is hand-written shared plumbing (SqlBulkCopy wrapper, CSV reader, host, email notifier), not derivable from any .dtsx. Pass --etl-core <path> to copy it in as part of this command, or copy the Etl.Core folder shipped alongside this tool (Tools/Etl.Core, a sibling of Tools/SsisExtractor) to generate/Etl.Core/ by hand before building."));
        }
        else
        {
            try
            {
                var etlCoreDest = Path.Combine(generateDir, "Etl.Core");
                var copied = CopyDirectoryExcludingBuildOutput(etlCorePath, etlCoreDest);
                // Overwrite the copied Etl.Core's own two props files with the SAME
                // framework-matched content just written above, regardless of what the source
                // copy's own committed props say -- these two files are the one place
                // Tools/Etl.Core and every generated .csproj must agree byte-for-byte (each
                // side's own doc comment already says "keep in sync"; this makes the OUTPUT
                // side of that promise unconditional rather than trusting a second, manually
                // maintained copy to have been kept current).
                File.WriteAllText(Path.Combine(etlCoreDest, "Directory.Build.props"), JoinLines(BuildDirectoryBuildProps(framework)));
                File.WriteAllText(Path.Combine(etlCoreDest, "Directory.Packages.props"), JoinLines(BuildDirectoryPackagesProps(framework)));
                Console.WriteLine($"copied Etl.Core ({copied} file(s)) from {Path.GetFullPath(etlCorePath)} -> {etlCoreDest} (targeting {framework})");
            }
            catch (Exception ex)
            {
                // A failed copy must still show up as a gap -- the alternative is a Generated.slnx
                // that references a project directory nothing put there, discovered only much
                // later as "Etl.Core (not found)" in an IDE's Solution Explorer.
                gaps.Add(new GenerationGap("Etl.Core", $"--etl-core '{etlCorePath}' could not be copied: {ex.Message}. Copy the Etl.Core folder shipped alongside this tool (Tools/Etl.Core, a sibling of Tools/SsisExtractor) to generate/Etl.Core/ by hand before building."));
            }
        }

        return gaps;
    }

    /// <summary>Plain recursive file copy, skipping any bin/obj directory found anywhere under
    /// the source -- the same exclusion <c>robocopy /XD bin obj</c> gives, reimplemented here
    /// (rather than shelling out to robocopy) so this stays a pure .NET dependency, portable to
    /// whatever OS this CLI itself runs on.</summary>
    private static int CopyDirectoryExcludingBuildOutput(string sourceDir, string destDir)
    {
        if (!Directory.Exists(sourceDir))
        {
            throw new DirectoryNotFoundException($"source directory not found: {Path.GetFullPath(sourceDir)}");
        }

        var fullSource = Path.GetFullPath(sourceDir);
        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(fullSource, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(fullSource, file);
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase) || s.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var destPath = Path.Combine(destDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(file, destPath, overwrite: true);
            copied++;
        }

        return copied;
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

    /// <summary>
    /// A short, fully self-contained gap-filling guide, written at the --out ROOT every run.
    /// Deliberately duplicates (rather than just linking to) the handful of facts a reader
    /// actually needs -- this file's whole reason to exist is surviving a context where nothing
    /// else this tool ships (Tools/COPILOT_GUIDE.md, Tools/.github/copilot-instructions.md) is
    /// anywhere in view, e.g. someone opened generate/Generated.slnx directly in an IDE with no
    /// idea Tools/ even exists. See GenerateCommand's own call site for the concrete failure
    /// this was built to close.
    ///
    /// <paramref name="fillsDir"/> is the ACTUAL resolved fills directory for this run (already
    /// an absolute path by the time this is called -- see the call site). Printing the real path
    /// here, rather than a generic "fills/" reference, is itself a fix for a real, repeated
    /// incident: earlier versions of this file always said "fills/" as if that were always
    /// `&lt;out&gt;/fills/`, which is exactly the disposable-by-default location whose contents
    /// were silently lost more than once across sessions. If a caller passed a durable `--fills`
    /// path outside `--out` (the recommended pattern -- see the warning below when they didn't),
    /// this file now says so explicitly and gives the exact command to use, so a reader has no
    /// way to fall back to the wrong default by omission.
    /// </summary>
    private static void WriteAiAssistantGuide(string path, List<GapSpec> allGaps, string fillsDir, string outDir)
    {
        var fillable = allGaps.Where(g => g.Tier is GapTier.MissingDatum or GapTier.MissingLogic).ToList();
        var defaultFillsDir = Path.GetFullPath(Path.Combine(outDir, "fills"));
        var fillsIsDefault = string.Equals(
            fillsDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            defaultFillsDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var applyFillsCommand = $"ssisx apply-fills --out <this folder> --fills \"{fillsDir}\"";

        var sb = new StringBuilder();
        sb.AppendLine("# How to fill the gaps in this generated output");
        sb.AppendLine();
        sb.AppendLine("Read this before touching anything under `generate/`. This file was written by");
        sb.AppendLine("`ssisx generate` itself, fresh, every run -- it will be overwritten next time, same as");
        sb.AppendLine($"everything else under this output folder except the fills directory (see below).");
        sb.AppendLine();
        sb.AppendLine("## The fills directory for THIS run -- read this even if you've done this before");
        sb.AppendLine();
        sb.AppendLine($"`--fills` was resolved to:");
        sb.AppendLine();
        sb.AppendLine($"    {fillsDir}");
        sb.AppendLine();
        if (fillsIsDefault)
        {
            sb.AppendLine("**This is the DEFAULT location, and it is INSIDE this disposable `--out` folder.**");
            sb.AppendLine("Anything you write here (a Tier-2 `.cs` fill, a Tier-1 `.decisions.json`) will be");
            sb.AppendLine("permanently lost the next time someone deletes or regenerates this `--out` folder from");
            sb.AppendLine("scratch -- this has already happened for real, more than once, and is exactly why this");
            sb.AppendLine("warning exists. **Before writing any fill, re-run `generate` with an explicit `--fills`");
            sb.AppendLine("pointing OUTSIDE this `--out` folder** -- e.g. `--fills ..\\fills-library` (a sibling of");
            sb.AppendLine("`--out`, not inside it), or ask where the durable fills location for this engagement is");
            sb.AppendLine("if you don't know. Do not write fills into the path above as-is.");
        }
        else
        {
            sb.AppendLine("Good -- this is OUTSIDE the disposable `--out` folder, so it survives `--out` being");
            sb.AppendLine("deleted or regenerated from scratch. Keep using this exact `--fills` path on every");
            sb.AppendLine("`generate`/`apply-fills` call for this engagement, and make sure it's committed to git");
            sb.AppendLine("once a fill is confirmed working (`git add` + `git commit` on that folder) -- an");
            sb.AppendLine("uncommitted fill is still one accidental delete away from being lost.");
        }
        sb.AppendLine();
        sb.AppendLine("## Where things are, relative to THIS file");
        sb.AppendLine();
        sb.AppendLine("- `gaps/<Package>/<GapId>.md` -- auto-generated work packets, one per open gap. Read the");
        sb.AppendLine("  exact one you're working on before writing anything -- it has the real question or the");
        sb.AppendLine("  real script source, not a paraphrase.");
        sb.AppendLine($"- `{fillsDir}\\<Package>\\*.cs` and `{fillsDir}\\<Package>.decisions.json` -- where YOUR");
        sb.AppendLine("  answer goes (the exact path printed above, not a generic `fills/`). **These are empty");
        sb.AppendLine("  right now on purpose.** `ssisx` never writes here, ever -- so an empty folder, or");
        sb.AppendLine("  `apply-fills` reporting \"0 fills applied\", means nobody has answered a packet yet. It");
        sb.AppendLine("  does not mean the packets are missing -- check `gaps/`. If files you expect to be here");
        sb.AppendLine("  are genuinely gone, do NOT re-port everything from scratch before checking whether they");
        sb.AppendLine("  still exist somewhere else (e.g. committed in git, or under an old `--out`'s own");
        sb.AppendLine("  `fills/` from before this path was made explicit).");
        sb.AppendLine("- `generate/<Package>/` -- the generated C# project. **Never hand-edit files here** --");
        sb.AppendLine("  regenerated/overwritten on every `ssisx generate` run. A Script Task/Component's unfilled");
        sb.AppendLine("  logic shows up here as an unimplemented `partial` method (`Fill_<Column>` or");
        sb.AppendLine("  `RunScriptAsync`) that deliberately fails to build (`CS8795`) until you supply the other");
        sb.AppendLine("  half in the fills directory above -- that failure is intentional, not something to patch");
        sb.AppendLine("  around here.");
        sb.AppendLine("- `generate-report.md` / `gaps.json` -- the full gap list, with every `GapId` and tier.");
        sb.AppendLine();

        if (fillable.Count == 0)
        {
            sb.AppendLine("## Nothing to fill right now");
            sb.AppendLine();
            sb.AppendLine("No Tier-1/2 gaps exist in this output -- either everything generated clean, or every");
            sb.AppendLine("remaining gap is Tier 3 (missing tool support, not something to fill in by hand; see");
            sb.AppendLine("`generate-report.md` for those). If a build still fails with `CS8795`, re-run");
            sb.AppendLine("`ssisx generate` for this package -- something changed since this file was written.");
        }
        else
        {
            sb.AppendLine($"## {fillable.Count} gap(s) waiting for an answer, right now");
            sb.AppendLine();
            sb.AppendLine("| Package | Tier | GapId | Work packet |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var g in fillable.OrderBy(g => g.Package, StringComparer.Ordinal).ThenBy(g => g.GapId, StringComparer.Ordinal))
            {
                var packetRef = g.PacketPath is not null ? $"`{g.PacketPath}`" : "_(none)_";
                sb.AppendLine($"| {g.Package} | {(g.Tier == GapTier.MissingDatum ? "1 -- datum" : "2 -- logic")} | `{g.GapId}` | {packetRef} |");
            }
            sb.AppendLine();
            sb.AppendLine("## What to actually do for each tier");
            sb.AppendLine();
            sb.AppendLine("**Tier 1 (a missing datum, e.g. an unresolvable Lookup join key):** open the work");
            sb.AppendLine("packet, read its exact question, do **not** guess a confident-sounding answer -- confirm");
            sb.AppendLine("it with a human if you're not certain. Write the answer into");
            sb.AppendLine($"`{fillsDir}\\<Package>.decisions.json`, in the exact JSON shape the packet shows, with a");
            sb.AppendLine("real `ConfirmedBy`.");
            sb.AppendLine();
            sb.AppendLine("**Tier 2 (a Script Task/Component whose real source IS in the packet, just needs");
            sb.AppendLine("porting to C#):** read the packet -- it includes the actual original script text and the");
            sb.AppendLine("exact seam signature you must match (do not rename or reshape it). Write the `.cs` file it");
            sb.AppendLine($"describes under `{fillsDir}\\<Package>\\`, with this comment immediately above the seam,");
            sb.AppendLine("copying `EvidenceSha256` from the packet verbatim:");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine("// ssisx-fill: GapId=<exact GapId from the table above> Author=<you> Date=<yyyy-mm-dd> EvidenceSha256=<from the packet>");
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("## After writing a fill");
            sb.AppendLine();
            sb.AppendLine($"Run `{applyFillsCommand}` -- the `--fills` value MUST match the path printed above");
            sb.AppendLine("exactly, or your fill will not be found. This validates and copies your fill into");
            sb.AppendLine("`generate/<Package>/Fills/`, and reports anything still outstanding or stale. `ssisx.exe`");
            sb.AppendLine("lives under `Tools/SsisExtractor/src/Ssis.Extract.Cli/bin/Debug/net8.0/` relative to");
            sb.AppendLine("wherever this was generated FROM -- if you're working from this output folder alone and");
            sb.AppendLine("don't have that path, ask where the `Tools/` folder is before assuming it isn't");
            sb.AppendLine("available; do not skip `apply-fills` and hand-copy a file into `generate/` instead, since");
            sb.AppendLine("that bypasses the staleness/provenance check it exists to provide.");
        }

        sb.AppendLine();
        sb.AppendLine("## One rule that applies regardless of tier");
        sb.AppendLine();
        sb.AppendLine("Do not attempt to build/run this project against a real database or real source data to");
        sb.AppendLine("\"verify\" a fill -- none is available in this environment, and inventing one (a throwaway");
        sb.AppendLine("connection string, sample files) is out of scope. `dotnet build` succeeding is the expected");
        sb.AppendLine("extent of checking here.");

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
