using System.Globalization;
using System.Text;
using System.Text.Json;
using Ssis.Extract.Codegen;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Analysis;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Serialization;
using Ssis.Extract.Sql;

namespace Ssis.Extract.Cli;

/// <summary>
/// <c>ssisx report</c> -- the plan §5.6/§5.7/§5.8 derived analysis (complexity scoring,
/// findings, non-determinism manifest) plus the portfolio-level roll-up (plan §6.1's
/// <c>inventory.*</c>/<c>findings.*</c>/<c>nondeterministic.json</c>/<c>unmapped.md</c>/
/// <c>portfolio.md</c>). Like <c>graph</c>, this re-derives everything from freshly parsed
/// packages rather than reading a prior <c>extract</c> run's spec.json back in -- the plan's
/// own §6.2 CLI sketch shows <c>report --in &lt;out-dir&gt;</c>, but round-tripping through
/// JSON buys nothing extraction-correctness-wise here and adds a second (de)serialization
/// surface to keep in sync; revisit if a later slice (e.g. <c>pull</c>, where the original
/// .dtsx may not be available at report time) actually needs it.
/// </summary>
internal static class ReportCommand
{
    public static int Run(string[] args)
    {
        string? input = null;
        string? outDir = null;
        var recursive = false;
        string? weightsPath = null;
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
                    case "--weights": weightsPath = RequireValue(args, ref i, "--weights"); break;
                    case "--package":
                        var pkgArg = RequireValue(args, ref i, "--package");
                        packageNames.AddRange(pkgArg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                        break;
                    default:
                        Console.Error.WriteLine($"error: unknown report option '{args[i]}'");
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
        if (!Path.Exists(input))
        {
            Console.Error.WriteLine($"error: input path not found: {input}");
            return 2;
        }

        ComplexityWeights weights;
        if (weightsPath is not null)
        {
            if (!File.Exists(weightsPath))
            {
                Console.Error.WriteLine($"error: --weights file not found: {weightsPath}");
                return 2;
            }
            try
            {
                weights = JsonSerializer.Deserialize<ComplexityWeights>(File.ReadAllText(weightsPath))
                    ?? throw new InvalidDataException("empty weights file");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: could not read --weights file: {ex.Message}");
                return 2;
            }
        }
        else
        {
            weights = ComplexityWeights.Default;
        }

        input = Path.GetFullPath(input);
        outDir = Path.GetFullPath(outDir);

        using var loaded = LoadOrNull(input, recursive, packageNames);
        if (loaded is null) return 2;
        var packages = loaded.Packages;

        if (packageNames.Count > 0 && packages.Count == 0)
        {
            Console.Error.WriteLine("error: --package matched no packages under this --input -- nothing to report.");
            return 2;
        }

        Directory.CreateDirectory(outDir);

        var perPackageFindings = new Dictionary<string, List<FindingSpec>>();
        var perPackageComplexity = new Dictionary<string, ComplexityStats>();
        var perPackageNonDeterministic = new Dictionary<string, List<NonDeterministicColumnSpec>>();
        var perPackageDataTouch = new Dictionary<string, DataTouchSpec>();
        var perPackageGenerationGaps = new Dictionary<string, List<GenerationGap>>();
        var allSqlAnalyses = new List<SqlAnalysisSpec>();
        var allExpressions = new List<HarvestedExpressionSpec>();
        var allPrimaryKeyCandidates = new List<PrimaryKeyCandidateSpec>();
        var allObservableEffects = new List<ObservableEffectSpec>();

        var sqlDir = Path.Combine(outDir, "sql");
        var scriptFilesWritten = 0;
        var scriptItemsStripped = 0;

        foreach (var pkg in packages)
        {
            var allExecutables = PackageTree.AllExecutables(pkg).ToList();
            perPackageComplexity[pkg.ObjectName] = ComplexityScorer.Score(pkg, weights);
            perPackageFindings[pkg.ObjectName] = RulesEngine.Evaluate(pkg);
            perPackageNonDeterministic[pkg.ObjectName] = NonDeterminismAnalyzer.Analyze(pkg, allExecutables);
            allPrimaryKeyCandidates.AddRange(PrimaryKeyInference.Infer(pkg, allExecutables));

            // Pure, in-memory -- no files written here. This is what actually answers "how
            // much of this portfolio can ssisx generate produce today", the sizing number
            // CLAUDE.md names as still unknown; run `ssisx generate` itself for the real
            // per-package C# output.
            perPackageGenerationGaps[pkg.ObjectName] = PackageGenerator.Generate(pkg, namespacePrefix: null).Gaps;

            // SQL harvest (plan §5.4): write each statement to its own addressable .sql
            // file, then parse it. The file is written before the parse so a statement that
            // fails to parse is still on disk to look at -- that's when you most want it.
            var sqlAnalyses = new List<SqlAnalysisSpec>();
            foreach (var harvested in SqlHarvester.Harvest(pkg))
            {
                var fileName = SanitizeFileName($"{harvested.Location}.sql");
                var relativePath = Path.Combine("sql", pkg.ObjectName, fileName);
                var fullPath = Path.Combine(outDir, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, harvested.Sql);

                sqlAnalyses.Add(SqlAnalyzer.Analyze(pkg.ObjectName, harvested.Location, harvested.Sql, relativePath.Replace('\\', '/')));
            }
            allSqlAnalyses.AddRange(sqlAnalyses);

            var dataTouch = DataTouchBuilder.Build(pkg, sqlAnalyses);
            perPackageDataTouch[pkg.ObjectName] = dataTouch;
            allObservableEffects.AddRange(ObservableEffectsBuilder.Build(pkg, dataTouch));
            allExpressions.AddRange(ExpressionHarvester.Harvest(pkg));

            // Script Task source extraction (plan §6.1's scripts/<Package>/<Task>/*): every
            // <ProjectItem> is the VSTA project's own plain-text source/support file, written
            // out verbatim under a folder per task -- see ScriptTaskPayload's doc comment for
            // why this is safe (source is plain CDATA text, never inside the opaque compiled
            // <BinaryItem>). A task with SourceStripped=true writes nothing here; RulesEngine's
            // "script-source-stripped" finding is what surfaces that loudly instead of a silent
            // empty folder.
            foreach (var ex in allExecutables)
            {
                if (ex.ScriptTask is null) continue;
                if (ex.ScriptTask.SourceStripped) { scriptItemsStripped++; continue; }

                var taskDir = Path.Combine(outDir, "scripts", pkg.ObjectName, SanitizeFileName(ex.RefId));
                foreach (var item in ex.ScriptTask.ProjectItems)
                {
                    var relativePath = SanitizeRelativePath(item.Name);
                    var fullPath = Path.Combine(taskDir, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                    File.WriteAllText(fullPath, item.Content, EncodingFor(item.Encoding));
                    scriptFilesWritten++;
                }
            }

            // Script Component source extraction -- same idea, different shape: SourceCode's
            // array elements carry no per-element file name (see ScriptComponentPayload's own
            // doc comment), so each is written as an index-numbered file rather than by its
            // real project file name, which this property doesn't preserve.
            foreach (var ex in allExecutables)
            {
                if (ex.DataFlowTask is null) continue;
                foreach (var comp in ex.DataFlowTask.Pipeline.Components)
                {
                    if (comp.ScriptComponent is null) continue;
                    if (comp.ScriptComponent.SourceStripped) { scriptItemsStripped++; continue; }

                    var componentDir = Path.Combine(outDir, "scripts", pkg.ObjectName, SanitizeFileName(ex.RefId), SanitizeFileName(comp.Name));
                    var extension = comp.ScriptComponent.Language switch
                    {
                        "CSharp" => ".cs",
                        "VisualBasic" => ".vb",
                        _ => ".txt",
                    };
                    for (var i = 0; i < comp.ScriptComponent.SourceCodeItems.Count; i++)
                    {
                        Directory.CreateDirectory(componentDir);
                        File.WriteAllText(Path.Combine(componentDir, $"source-{i}{extension}"), comp.ScriptComponent.SourceCodeItems[i], EncodingFor(null));
                        scriptFilesWritten++;
                    }
                }
            }
        }

        var dependencyEdges = DependencyGraphBuilder.Build(packages, perPackageDataTouch);

        var portfolioFindings = RulesEngine.EvaluatePortfolio(packages);
        foreach (var f in portfolioFindings)
        {
            perPackageFindings[f.PackageName].Add(f);
        }

        var allFindings = perPackageFindings.Values.SelectMany(f => f).ToList();
        var allNonDeterministic = perPackageNonDeterministic.Values.SelectMany(n => n).ToList();

        var inventory = packages.Select(pkg => new PortfolioInventoryRow
        {
            PackageName = pkg.ObjectName,
            Complexity = perPackageComplexity[pkg.ObjectName],
            CoveragePercent = pkg.Coverage.CoveragePercent,
            FindingsCount = perPackageFindings[pkg.ObjectName].Count,
            HighestSeverity = HighestSeverity(perPackageFindings[pkg.ObjectName]),
        }).OrderBy(r => r.PackageName, StringComparer.Ordinal).ToList();

        WriteInventoryCsv(inventory, Path.Combine(outDir, "inventory.csv"));
        WriteInventoryMd(inventory, Path.Combine(outDir, "inventory.md"));
        WriteFindingsCsv(allFindings, Path.Combine(outDir, "findings.csv"));
        WriteFindingsMd(allFindings, Path.Combine(outDir, "findings.md"));
        StableJsonWriter.WriteToFile(allNonDeterministic.OrderBy(n => n.PackageName, StringComparer.Ordinal).ToList(), Path.Combine(outDir, "nondeterministic.json"));
        StableJsonWriter.WriteToFile(
            allPrimaryKeyCandidates.OrderBy(p => p.PackageName, StringComparer.Ordinal).ThenBy(p => p.DataFlowTaskPath, StringComparer.Ordinal).ToList(),
            Path.Combine(outDir, "primary-keys.json"));
        WriteUnmappedMd(packages, Path.Combine(outDir, "unmapped.md"));

        // Slice 5 outputs (plan §5.2-§5.5)
        WriteExpressionsCsv(allExpressions, Path.Combine(outDir, "expressions.csv"));
        WriteExpressionFunctionsMd(allExpressions, Path.Combine(outDir, "expression-functions.md"));
        StableJsonWriter.WriteToFile(allSqlAnalyses, Path.Combine(outDir, "sql-analysis.json"));
        StableJsonWriter.WriteToFile(perPackageDataTouch.Values.OrderBy(d => d.PackageName, StringComparer.Ordinal).ToList(), Path.Combine(outDir, "datatouch.json"));
        WriteDataTouchMd(perPackageDataTouch, Path.Combine(outDir, "datatouch.md"));
        WritePrimaryKeysMd(allPrimaryKeyCandidates, Path.Combine(outDir, "primary-keys.md"));
        WriteGenerationReadinessMd(perPackageGenerationGaps, Path.Combine(outDir, "generation-readiness.md"));
        StableJsonWriter.WriteToFile(
            allObservableEffects.OrderBy(e => e.PackageName, StringComparer.Ordinal).ThenBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.Target, StringComparer.Ordinal).ToList(),
            Path.Combine(outDir, "effects.json"));
        WriteEffectsMd(allObservableEffects, Path.Combine(outDir, "effects.md"));
        var graphDir = Path.Combine(outDir, "graph");
        Directory.CreateDirectory(graphDir);
        File.WriteAllText(Path.Combine(graphDir, "portfolio.mmd"), DependencyGraphBuilder.ToMermaid(packages, dependencyEdges));
        StableJsonWriter.WriteToFile(dependencyEdges, Path.Combine(outDir, "dependencies.json"));

        WritePortfolioMd(inventory, allFindings, allNonDeterministic, allExpressions, allSqlAnalyses, dependencyEdges, perPackageGenerationGaps, Path.Combine(outDir, "portfolio.md"));

        var sqlFileCount = allSqlAnalyses.Count(s => s.SqlFilePath is not null);
        var parseFailures = allSqlAnalyses.Count(s => !s.ParsedSuccessfully);

        Console.WriteLine($"wrote report for {packages.Count} package(s) -> {outDir}");
        Console.WriteLine($"  {allFindings.Count} finding(s), {allNonDeterministic.Count} non-deterministic column(s)");
        Console.WriteLine($"  {sqlFileCount} SQL statement(s) harvested ({parseFailures} failed to parse), {allExpressions.Count} expression(s), {dependencyEdges.Count} cross-package dependency edge(s)");
        Console.WriteLine($"  {scriptFilesWritten} Script Task/Component source file(s) harvested to `scripts/`{(scriptItemsStripped > 0 ? $", {scriptItemsStripped} script(s) had NO source (binary only -- see findings)" : "")}");
        foreach (var row in inventory)
        {
            var gapCount = perPackageGenerationGaps.TryGetValue(row.PackageName, out var gaps) ? gaps.Count(PortfolioDigest.IsBlockingGap) : 0;
            Console.WriteLine($"  {row.PackageName}: {row.Complexity.Classification} (score {row.Complexity.Score:0.##}), {row.FindingsCount} finding(s), {(gapCount == 0 ? "generatable (0 blocking gaps)" : $"{gapCount} blocking generation gap(s)")}");
        }

        // The digest is written LAST and printed to the console, because on a locked-down
        // client site the console is frequently the only export channel that exists -- a
        // screenshot or a phone photo of the terminal. See PortfolioDigest for why it is an
        // allow-list of safe fields rather than a redaction of the fuller report.
        var digest = PortfolioDigest.Build(packages, inventory, allFindings, allExpressions, allObservableEffects, allNonDeterministic, loaded.Failures.Count, perPackageGenerationGaps);
        File.WriteAllText(Path.Combine(outDir, "portfolio-digest.md"), PortfolioDigest.ToMarkdown(digest));
        StableJsonWriter.WriteToFile(digest, Path.Combine(outDir, "portfolio-digest.json"));
        Console.WriteLine();
        Console.WriteLine(PortfolioDigest.ToConsoleDigest(digest));

        // Anything that could not be read is persisted next to the results, not just printed:
        // a survey run on a machine we do not control is usually reduced to whatever ends up
        // in the out directory, and "which packages are missing from this report, and why"
        // is exactly the question that cannot be answered later from console scrollback.
        WriteLoadFailuresMd(loaded.Failures, Path.Combine(outDir, "load-failures.md"));
        if (loaded.Failures.Count > 0)
        {
            Console.Error.WriteLine($"warning: {loaded.Failures.Count} input(s) could not be read and are ABSENT from this report -- see load-failures.md");
            return 2;
        }

        return 0;
    }

    /// <summary>Always written, including on a clean run, so its absence means the run died before finishing rather than "nothing failed".</summary>
    private static void WriteLoadFailuresMd(List<PackageLoader.LoadFailure> failures, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Inputs that could not be read");
        sb.AppendLine();
        if (failures.Count == 0)
        {
            sb.AppendLine("None -- every .dtproj/.ispac/.dtsx found under `--input` was read successfully.");
        }
        else
        {
            sb.AppendLine($"{failures.Count} input(s) were skipped. They are **absent from every other file in this report**, so");
            sb.AppendLine("treat the inventory as covering the packages listed there, not the whole folder.");
            sb.AppendLine();
            sb.AppendLine("| Kind | Path | Reason |");
            sb.AppendLine("|---|---|---|");
            foreach (var f in failures.OrderBy(f => f.Path, StringComparer.Ordinal))
            {
                sb.AppendLine($"| {f.Kind} | `{f.Path}` | {f.Reason.Replace("|", "\\|")} |");
            }
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static PackageLoader.LoadResult? LoadOrNull(string input, bool recursive, IReadOnlyList<string>? packageNames = null)
    {
        try
        {
            return PackageLoader.Load(input, noRedact: false, recursive, packageNames);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return null;
        }
    }

    private static string SanitizeFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    /// <summary>Unlike <see cref="SanitizeFileName"/> (used for the SQL harvest, where the whole location string collapses to one flat file name), a ScriptProjectItemSpec.Name like "Properties\Resources.resx" must keep its directory structure -- so each backslash/forward-slash-delimited segment is sanitized individually, not the whole string, and rejoined with the current OS's own separator.</summary>
    private static string SanitizeRelativePath(string name)
    {
        var segments = name.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Select(SanitizeFileName);
        return Path.Combine(segments.ToArray());
    }

    /// <summary>Maps a ScriptProjectItemSpec.Encoding attribute (the *original on-disk file's* encoding, e.g. "UTF8"/"UTF16LE" -- observed values only) to the .NET Encoding to write the extracted file back out with, so it round-trips as the same bytes rather than always defaulting to UTF-8.</summary>
    private static System.Text.Encoding EncodingFor(string? encodingName) => encodingName switch
    {
        "UTF16LE" => System.Text.Encoding.Unicode,
        _ => new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
    };

    private static string? HighestSeverity(List<FindingSpec> findings)
    {
        if (findings.Any(f => f.Severity == "Error")) return "Error";
        if (findings.Any(f => f.Severity == "Warning")) return "Warning";
        if (findings.Any(f => f.Severity == "Info")) return "Info";
        return null;
    }

    private static void WriteInventoryCsv(List<PortfolioInventoryRow> rows, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Package,Classification,Score,Tasks,Containers,DataFlows,Components,Expressions,SqlStatements,ScriptTasks,ScriptComponents,Loops,EventHandlers,UnmappedNodes,CoveragePercent,Findings,HighestSeverity");
        foreach (var r in rows)
        {
            var c = r.Complexity;
            sb.AppendLine(string.Join(",",
                Csv(r.PackageName), Csv(c.Classification), c.Score.ToString(CultureInfo.InvariantCulture),
                c.TaskCount, c.ContainerCount, c.DataFlowTaskCount, c.PipelineComponentCount, c.ExpressionCount,
                c.SqlStatementCount, c.ScriptTaskCount, c.ScriptComponentCount, c.LoopCount, c.EventHandlerCount, c.UnmappedNodeCount,
                r.CoveragePercent.ToString(CultureInfo.InvariantCulture), r.FindingsCount, Csv(r.HighestSeverity ?? "")));
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteInventoryMd(List<PortfolioInventoryRow> rows, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Inventory");
        sb.AppendLine();
        sb.AppendLine("| Package | Class | Score | Tasks | DFTs | Components | SQL | Coverage | Findings | Highest |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in rows)
        {
            var c = r.Complexity;
            sb.AppendLine($"| {r.PackageName} | {c.Classification} | {c.Score:0.##} | {c.TaskCount} | {c.DataFlowTaskCount} | {c.PipelineComponentCount} | {c.SqlStatementCount} | {r.CoveragePercent:0.##}% | {r.FindingsCount} | {r.HighestSeverity ?? "-"} |");
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteFindingsCsv(List<FindingSpec> findings, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Package,RuleId,Category,Severity,Location,Message");
        foreach (var f in findings.OrderBy(f => f.PackageName, StringComparer.Ordinal).ThenBy(f => f.RuleId, StringComparer.Ordinal))
        {
            sb.AppendLine(string.Join(",", Csv(f.PackageName), Csv(f.RuleId), Csv(f.Category), Csv(f.Severity), Csv(f.Location), Csv(f.Message)));
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteFindingsMd(List<FindingSpec> findings, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Findings");
        sb.AppendLine();
        if (findings.Count == 0)
        {
            sb.AppendLine("No findings.");
        }
        foreach (var group in findings.GroupBy(f => f.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"## {group.Key}");
            sb.AppendLine();
            sb.AppendLine("| Package | Severity | Rule | Location | Message |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var f in group.OrderBy(f => f.PackageName, StringComparer.Ordinal).ThenBy(f => f.Severity, StringComparer.Ordinal))
            {
                sb.AppendLine($"| {f.PackageName} | {f.Severity} | {f.RuleId} | {MdEscape(f.Location)} | {MdEscape(f.Message)} |");
            }
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteUnmappedMd(List<PackageSpec> packages, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Unmapped");
        sb.AppendLine();
        sb.AppendLine("Everything this extraction did not (or, for `Package/DesignTimeProperties`, deliberately never will) model, per package -- plan §7.2's coverage metric made visible as a to-do list.");
        sb.AppendLine();
        foreach (var pkg in packages.OrderBy(p => p.ObjectName, StringComparer.Ordinal))
        {
            sb.AppendLine($"## {pkg.ObjectName} -- {pkg.Coverage.CoveragePercent:0.##}% coverage");
            sb.AppendLine();
            if (pkg.Unmapped.Count == 0)
            {
                sb.AppendLine("Nothing unmapped.");
            }
            else
            {
                foreach (var u in pkg.Unmapped)
                {
                    sb.AppendLine($"- **{u.Location}** -- {MdEscape(u.Reason)}");
                }
            }

            var unmappedTasks = PackageTree.AllExecutables(pkg).Where(e => e.UnmappedTask is not null).ToList();
            foreach (var ex in unmappedTasks)
            {
                sb.AppendLine($"- **{ex.RefId}** (ExecutableType={ex.ExecutableType}) -- unrecognized task type, captured as raw XML only.");
            }
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteExpressionsCsv(List<HarvestedExpressionSpec> expressions, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Package,Kind,Location,TargetProperty,Expression,FriendlyExpression,ReferencedVariables,ReferencedColumns,Functions,Casts");
        foreach (var e in expressions.OrderBy(e => e.PackageName, StringComparer.Ordinal).ThenBy(e => e.Location, StringComparer.Ordinal))
        {
            sb.AppendLine(string.Join(",",
                Csv(e.PackageName), Csv(e.Kind), Csv(e.Location), Csv(e.TargetProperty ?? ""),
                Csv(e.Expression), Csv(e.FriendlyExpression ?? ""),
                Csv(string.Join(" ", e.ReferencedVariables)), Csv(string.Join(" ", e.ReferencedColumns)),
                Csv(string.Join(" ", e.Functions)), Csv(string.Join(" ", e.Casts))));
        }
        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>The plan §5.5 payoff: which slice of the SSIS expression language this portfolio actually uses, so a replacement engine's required surface area is a measured number rather than a guess.</summary>
    private static void WriteExpressionFunctionsMd(List<HarvestedExpressionSpec> expressions, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# SSIS expression language usage");
        sb.AppendLine();
        sb.AppendLine($"{expressions.Count} expression(s) harvested across the portfolio. This is the list a replacement engine has to support -- measured, not guessed (plan §5.5).");
        sb.AppendLine();

        var functionUsage = expressions
            .SelectMany(e => e.Functions.Select(f => (Function: f, e.PackageName)))
            .GroupBy(x => x.Function)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        sb.AppendLine("## Functions");
        sb.AppendLine();
        if (functionUsage.Count == 0)
        {
            sb.AppendLine("No functions used.");
        }
        else
        {
            sb.AppendLine("| Function | Uses | Packages |");
            sb.AppendLine("|---|---|---|");
            foreach (var g in functionUsage)
            {
                var pkgs = string.Join(", ", g.Select(x => x.PackageName).Distinct().OrderBy(p => p, StringComparer.Ordinal));
                sb.AppendLine($"| {g.Key} | {g.Count()} | {pkgs} |");
            }
        }
        sb.AppendLine();

        var castUsage = expressions
            .SelectMany(e => e.Casts.Select(c => (Cast: c, e.PackageName)))
            .GroupBy(x => x.Cast)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        sb.AppendLine("## Cast operators");
        sb.AppendLine();
        sb.AppendLine("Kept separate from functions deliberately -- `(DT_WSTR,50)` is a syntactic cast, not a function call, and the plan (§5.5) calls out `(DT_STR,n,codepage)` specifically as one with no clean .NET equivalent.");
        sb.AppendLine();
        if (castUsage.Count == 0)
        {
            sb.AppendLine("No cast operators used.");
        }
        else
        {
            sb.AppendLine("| Cast | Uses | Packages |");
            sb.AppendLine("|---|---|---|");
            foreach (var g in castUsage)
            {
                var pkgs = string.Join(", ", g.Select(x => x.PackageName).Distinct().OrderBy(p => p, StringComparer.Ordinal));
                sb.AppendLine($"| {g.Key} | {g.Count()} | {pkgs} |");
            }
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteDataTouchMd(Dictionary<string, DataTouchSpec> dataTouch, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Data touch inventory");
        sb.AppendLine();
        sb.AppendLine("What each package actually reads and writes (plan §5.3) -- tables from SQL analysis and destination components, file paths from connection managers resolved against the components that use them.");
        sb.AppendLine();
        foreach (var dt in dataTouch.Values.OrderBy(d => d.PackageName, StringComparer.Ordinal))
        {
            sb.AppendLine($"## {dt.PackageName}");
            sb.AppendLine();
            AppendList(sb, "Tables read", dt.TablesRead);
            AppendList(sb, "Tables written", dt.TablesWritten);
            AppendList(sb, "Procedures executed", dt.ProceduresExecuted);
            AppendList(sb, "Files read", dt.FilePathsRead);
            AppendList(sb, "Files written", dt.FilePathsWritten);
            AppendList(sb, "Files (direction undetermined)", dt.FilePathsUnknownDirection);
            AppendList(sb, "Files (path computed at run time)", dt.FilePathsFromExpression);
            AppendList(sb, "Servers", dt.Servers);
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static void WritePrimaryKeysMd(List<PrimaryKeyCandidateSpec> candidates, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Primary-key candidates (best-effort, needs human confirmation)");
        sb.AppendLine();
        sb.AppendLine("A `.dtsx` file carries no primary-key concept anywhere -- confirmed by reading this ");
        sb.AppendLine("PoC's own `<externalMetadataColumn>` elements, which carry only dataType/length/");
        sb.AppendLine("precision/scale/codePage, nothing about keys. Every row below is a naming-convention ");
        sb.AppendLine("guess (`\"<table>ID\"` or a fallback `\"ID\"`, integer-typed, not a computed value), ");
        sb.AppendLine("not a fact -- confirm each one before relying on it (e.g. filling in gate 3's `KeyColumn`).");
        sb.AppendLine();
        sb.AppendLine("| Package | Data Flow Task | Destination | Target table | Candidate | Confidence | Reason |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var c in candidates.OrderBy(c => c.PackageName, StringComparer.Ordinal).ThenBy(c => c.DataFlowTaskPath, StringComparer.Ordinal))
        {
            var columns = c.Columns.Count > 0 ? string.Join(", ", c.Columns) : "(none)";
            sb.AppendLine($"| {MdEscape(c.PackageName)} | {MdEscape(c.DataFlowTaskPath)} | {MdEscape(c.DestinationComponentName)} | {MdEscape(c.TargetTable ?? "(unknown)")} | {MdEscape(columns)} | {c.Confidence} | {MdEscape(c.Reason)} |");
        }
        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>
    /// Whether `ssisx generate` would produce a fully-generated C# project for each package --
    /// computed without writing any code to disk (that's `ssisx generate` itself); this is
    /// the analysis-layer readout of the same gaps that command's own report would show.
    /// </summary>
    private static void WriteGenerationReadinessMd(Dictionary<string, List<GenerationGap>> perPackageGenerationGaps, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Generation readiness (`ssisx generate`)");
        sb.AppendLine();
        sb.AppendLine("Whether `ssisx generate` would produce a fully-generated C# project for each package below, with zero BLOCKING gaps. This is the migration-sizing question a complexity score alone cannot answer -- run `ssisx generate --input <dir> --out <dir>` for the real generated output.");
        sb.AppendLine();
        sb.AppendLine("A `{Package}.Notification` gap is listed per package below but never counted as blocking: `PackageGenerator` reports it unconditionally (a `.dtsx` has no notion of who to email on success/failure), the generated project still compiles and runs with empty recipient lists, and no real client package could ever satisfy it -- counting it would make \"0 gaps\" permanently unreachable. A SqlCommand-mode OLE DB Source's column-name-assumption gap is non-blocking for the same reason: the flow is fully generated, the gap is a \"verify before running\" note, not missing output.");
        sb.AppendLine();
        var readyCount = perPackageGenerationGaps.Count(kv => !kv.Value.Any(PortfolioDigest.IsBlockingGap));
        sb.AppendLine($"**{readyCount} of {perPackageGenerationGaps.Count} package(s) are fully generatable today (0 blocking gaps).**");
        sb.AppendLine();
        foreach (var (packageName, gaps) in perPackageGenerationGaps.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var blockingCount = gaps.Count(PortfolioDigest.IsBlockingGap);
            sb.AppendLine($"## {packageName} -- {(blockingCount == 0 ? "fully generatable" : $"{blockingCount} blocking gap(s)")}");
            sb.AppendLine();
            if (gaps.Count == 0)
            {
                sb.AppendLine("No gaps.");
            }
            else
            {
                foreach (var g in gaps)
                {
                    var tag = PortfolioDigest.IsBlockingGap(g) ? "" : " _(non-blocking -- informational, see reason above)_";
                    sb.AppendLine($"- **{MdEscape(g.Location)}** -- {MdEscape(g.Reason)}{tag}");
                }
            }
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteEffectsMd(List<ObservableEffectSpec> effects, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Observable effects -- what a replacement must be checked against");
        sb.AppendLine();
        sb.AppendLine("Every table, file, and procedure this package changes -- plus, deliberately, every ");
        sb.AppendLine("task type this extractor has no semantic model for, listed as `UncharacterizedTask` ");
        sb.AppendLine("rather than silently omitted. This is the input to gate 3's own effect check: a ");
        sb.AppendLine("package is fully verified only once every row below is `Verifiable`, never by the ");
        sb.AppendLine("row count of one effect kind alone.");
        sb.AppendLine();
        var needsWork = effects.Count(e => e.Verifiability != "Verifiable");
        sb.AppendLine(needsWork == 0
            ? "**Every effect below has a gate-3 checker.**"
            : $"**{needsWork} of {effects.Count} effect(s) have no gate-3 checker yet** -- see `Verifiability` per row.");
        sb.AppendLine();
        sb.AppendLine("| Package | Kind | Target | Origin | Verifiability | Note |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var e in effects.OrderBy(e => e.PackageName, StringComparer.Ordinal).ThenBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.Target, StringComparer.Ordinal))
        {
            sb.AppendLine($"| {MdEscape(e.PackageName)} | {e.Kind} | {MdEscape(e.Target)} | {MdEscape(e.Origin)} | {e.Verifiability} | {MdEscape(e.Note)} |");
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static void AppendList(StringBuilder sb, string label, List<string> items)
    {
        if (items.Count == 0) return;
        sb.AppendLine($"- **{label}**: {string.Join(", ", items.Select(MdEscape))}");
    }

    private static void WritePortfolioMd(
        List<PortfolioInventoryRow> inventory,
        List<FindingSpec> findings,
        List<NonDeterministicColumnSpec> nonDeterministic,
        List<HarvestedExpressionSpec> expressions,
        List<SqlAnalysisSpec> sqlAnalyses,
        List<DependencyEdgeSpec> dependencyEdges,
        Dictionary<string, List<GenerationGap>> perPackageGenerationGaps,
        string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Portfolio report");
        sb.AppendLine();
        sb.AppendLine($"{inventory.Count} package(s) extracted and analyzed.");
        sb.AppendLine();

        var byClass = inventory.GroupBy(r => r.Complexity.Classification).OrderBy(g => g.Key, StringComparer.Ordinal);
        sb.AppendLine("## Classification breakdown");
        sb.AppendLine();
        foreach (var g in byClass)
        {
            sb.AppendLine($"- **{g.Key}**: {g.Count()} -- {string.Join(", ", g.Select(r => r.PackageName).OrderBy(n => n, StringComparer.Ordinal))}");
        }
        sb.AppendLine();

        var readyCount = perPackageGenerationGaps.Count(kv => !kv.Value.Any(PortfolioDigest.IsBlockingGap));
        sb.AppendLine("## Generation readiness (`ssisx generate`)");
        sb.AppendLine();
        sb.AppendLine($"**{readyCount} of {perPackageGenerationGaps.Count} package(s) are fully generatable today (0 blocking gaps).** Full per-package gap detail in `generation-readiness.md`.");
        sb.AppendLine();
        var notReady = perPackageGenerationGaps
            .Select(kv => (kv.Key, BlockingCount: kv.Value.Count(PortfolioDigest.IsBlockingGap)))
            .Where(x => x.BlockingCount > 0)
            .OrderBy(x => x.Key, StringComparer.Ordinal);
        foreach (var (packageName, blockingCount) in notReady)
        {
            sb.AppendLine($"- **{packageName}**: {blockingCount} blocking gap(s)");
        }
        sb.AppendLine();

        var bySeverity = findings.GroupBy(f => f.Severity).OrderByDescending(g => g.Key == "Error" ? 2 : g.Key == "Warning" ? 1 : 0);
        sb.AppendLine("## Findings by severity");
        sb.AppendLine();
        if (findings.Count == 0)
        {
            sb.AppendLine("No findings across the portfolio.");
        }
        foreach (var g in bySeverity)
        {
            sb.AppendLine($"- **{g.Key}**: {g.Count()}");
        }
        sb.AppendLine();

        sb.AppendLine("## Non-determinism manifest summary");
        sb.AppendLine();
        if (nonDeterministic.Count == 0)
        {
            sb.AppendLine("No non-deterministic columns found.");
        }
        else
        {
            sb.AppendLine("Exclude these columns from any parallel-run row-hash comparison (plan §5.8) -- full detail in `nondeterministic.json`.");
            sb.AppendLine();
            sb.AppendLine("| Package | Target | Reason |");
            sb.AppendLine("|---|---|---|");
            foreach (var n in nonDeterministic.OrderBy(n => n.PackageName, StringComparer.Ordinal))
            {
                var target = n.TargetTable is null ? "(unresolved)" : $"{n.TargetTable}.{n.TargetColumn}";
                sb.AppendLine($"| {n.PackageName} | {target} | {n.Reason} |");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## Cross-package dependencies");
        sb.AppendLine();
        if (dependencyEdges.Count == 0)
        {
            sb.AppendLine("No cross-package dependencies found -- no Execute Package Tasks, and no table written by one package and read by another.");
        }
        else
        {
            var explicitCount = dependencyEdges.Count(e => e.Kind == "ExecutePackageTask");
            var implicitCount = dependencyEdges.Count(e => e.Kind == "SharedTable");
            sb.AppendLine($"{explicitCount} explicit (Execute Package Task) and {implicitCount} implicit (shared table) edge(s). The implicit ones are the important half -- they exist only in the schedule, not in any package (plan §5.2). See `graph/portfolio.mmd` and `dependencies.json`.");
            sb.AppendLine();
            sb.AppendLine("| From | To | Kind | Shared object |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var e in dependencyEdges)
            {
                sb.AppendLine($"| {e.FromPackage} | {e.ToPackage} | {e.Kind} | {e.SharedObject ?? "-"} |");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## SQL harvest");
        sb.AppendLine();
        var parseFailures = sqlAnalyses.Where(s => !s.ParsedSuccessfully).ToList();
        sb.AppendLine($"{sqlAnalyses.Count} SQL statement(s) harvested to `sql/`, {parseFailures.Count} of which failed to parse.");
        if (parseFailures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("A parse failure usually means dialect drift or a non-T-SQL provider (an Execute SQL Task can target Oracle/DB2/ODBC) -- worth looking at, not a tool bug:");
            sb.AppendLine();
            foreach (var f in parseFailures)
            {
                sb.AppendLine($"- `{f.PackageName}` / `{MdEscape(f.Location)}`: {MdEscape(string.Join("; ", f.ParseErrors))}");
            }
        }
        var destructive = sqlAnalyses.Where(s => s.HasTruncate || s.HasDelete || s.HasMerge || s.HasDynamicSql).ToList();
        if (destructive.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Statements with TRUNCATE / DELETE / MERGE / dynamic SQL:");
            sb.AppendLine();
            sb.AppendLine("| Package | Location | Flags |");
            sb.AppendLine("|---|---|---|");
            foreach (var s in destructive)
            {
                var flags = new List<string>();
                if (s.HasTruncate) flags.Add("TRUNCATE");
                if (s.HasDelete) flags.Add("DELETE");
                if (s.HasMerge) flags.Add("MERGE");
                if (s.HasDynamicSql) flags.Add("dynamic");
                sb.AppendLine($"| {s.PackageName} | {MdEscape(s.Location)} | {string.Join(", ", flags)} |");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## Expression language usage");
        sb.AppendLine();
        var distinctFunctions = expressions.SelectMany(e => e.Functions).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();
        var distinctCasts = expressions.SelectMany(e => e.Casts).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList();
        sb.AppendLine($"{expressions.Count} expression(s) using {distinctFunctions.Count} distinct function(s) and {distinctCasts.Count} distinct cast operator(s) -- the surface area a replacement engine must support (plan §5.5). Full breakdown in `expression-functions.md`; every expression, with its referenced variables/columns, in `expressions.csv`.");
        if (distinctFunctions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Functions: {string.Join(", ", distinctFunctions)}");
        }
        if (distinctCasts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Casts: {string.Join(", ", distinctCasts)}");
        }
        sb.AppendLine();

        sb.AppendLine("## Detail files");
        sb.AppendLine();
        sb.AppendLine("- `inventory.md` / `inventory.csv` -- per-package counts, complexity score, classification");
        sb.AppendLine("- `findings.md` / `findings.csv` -- every rule hit with its location");
        sb.AppendLine("- `datatouch.md` / `datatouch.json` -- what each package reads and writes");
        sb.AppendLine("- `expressions.csv` / `expression-functions.md` -- every expression, and the language surface used");
        sb.AppendLine("- `sql/` / `sql-analysis.json` -- every SQL statement, individually addressable, with its parse result");
        sb.AppendLine("- `graph/portfolio.mmd` / `dependencies.json` -- cross-package dependency graph");
        sb.AppendLine("- `nondeterministic.json` -- columns to exclude from parallel-run comparison");
        sb.AppendLine("- `unmapped.md` -- what this extraction did not model, per package");
        sb.AppendLine("- `generation-readiness.md` -- whether `ssisx generate` would produce each package with zero gaps");

        File.WriteAllText(path, sb.ToString());
    }

    private static string Csv(object value)
    {
        var s = value.ToString() ?? "";
        return s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
    }

    private static string MdEscape(string s) => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static string RequireValue(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"{flag} requires a value");
        }
        i++;
        return args[i];
    }
}
