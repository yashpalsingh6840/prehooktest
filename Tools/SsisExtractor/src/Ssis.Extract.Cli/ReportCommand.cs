using System.Globalization;
using System.Text;
using System.Text.Json;
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

        using var loaded = LoadOrNull(input, recursive);
        if (loaded is null) return 2;
        var packages = loaded.Packages;

        Directory.CreateDirectory(outDir);

        var perPackageFindings = new Dictionary<string, List<FindingSpec>>();
        var perPackageComplexity = new Dictionary<string, ComplexityStats>();
        var perPackageNonDeterministic = new Dictionary<string, List<NonDeterministicColumnSpec>>();
        var perPackageDataTouch = new Dictionary<string, DataTouchSpec>();
        var allSqlAnalyses = new List<SqlAnalysisSpec>();
        var allExpressions = new List<HarvestedExpressionSpec>();

        var sqlDir = Path.Combine(outDir, "sql");

        foreach (var pkg in packages)
        {
            var allExecutables = PackageTree.AllExecutables(pkg).ToList();
            perPackageComplexity[pkg.ObjectName] = ComplexityScorer.Score(pkg, weights);
            perPackageFindings[pkg.ObjectName] = RulesEngine.Evaluate(pkg);
            perPackageNonDeterministic[pkg.ObjectName] = NonDeterminismAnalyzer.Analyze(pkg, allExecutables);

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

            perPackageDataTouch[pkg.ObjectName] = DataTouchBuilder.Build(pkg, sqlAnalyses);
            allExpressions.AddRange(ExpressionHarvester.Harvest(pkg));
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
        WriteUnmappedMd(packages, Path.Combine(outDir, "unmapped.md"));

        // Slice 5 outputs (plan §5.2-§5.5)
        WriteExpressionsCsv(allExpressions, Path.Combine(outDir, "expressions.csv"));
        WriteExpressionFunctionsMd(allExpressions, Path.Combine(outDir, "expression-functions.md"));
        StableJsonWriter.WriteToFile(allSqlAnalyses, Path.Combine(outDir, "sql-analysis.json"));
        StableJsonWriter.WriteToFile(perPackageDataTouch.Values.OrderBy(d => d.PackageName, StringComparer.Ordinal).ToList(), Path.Combine(outDir, "datatouch.json"));
        WriteDataTouchMd(perPackageDataTouch, Path.Combine(outDir, "datatouch.md"));
        var graphDir = Path.Combine(outDir, "graph");
        Directory.CreateDirectory(graphDir);
        File.WriteAllText(Path.Combine(graphDir, "portfolio.mmd"), DependencyGraphBuilder.ToMermaid(packages, dependencyEdges));
        StableJsonWriter.WriteToFile(dependencyEdges, Path.Combine(outDir, "dependencies.json"));

        WritePortfolioMd(inventory, allFindings, allNonDeterministic, allExpressions, allSqlAnalyses, dependencyEdges, Path.Combine(outDir, "portfolio.md"));

        var sqlFileCount = allSqlAnalyses.Count(s => s.SqlFilePath is not null);
        var parseFailures = allSqlAnalyses.Count(s => !s.ParsedSuccessfully);

        Console.WriteLine($"wrote report for {packages.Count} package(s) -> {outDir}");
        Console.WriteLine($"  {allFindings.Count} finding(s), {allNonDeterministic.Count} non-deterministic column(s)");
        Console.WriteLine($"  {sqlFileCount} SQL statement(s) harvested ({parseFailures} failed to parse), {allExpressions.Count} expression(s), {dependencyEdges.Count} cross-package dependency edge(s)");
        foreach (var row in inventory)
        {
            Console.WriteLine($"  {row.PackageName}: {row.Complexity.Classification} (score {row.Complexity.Score:0.##}), {row.FindingsCount} finding(s)");
        }

        return 0;
    }

    private static PackageLoader.LoadResult? LoadOrNull(string input, bool recursive)
    {
        try
        {
            return PackageLoader.Load(input, noRedact: false, recursive);
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
        sb.AppendLine("Package,Classification,Score,Tasks,Containers,DataFlows,Components,Expressions,SqlStatements,ScriptTasks,Loops,EventHandlers,UnmappedNodes,CoveragePercent,Findings,HighestSeverity");
        foreach (var r in rows)
        {
            var c = r.Complexity;
            sb.AppendLine(string.Join(",",
                Csv(r.PackageName), Csv(c.Classification), c.Score.ToString(CultureInfo.InvariantCulture),
                c.TaskCount, c.ContainerCount, c.DataFlowTaskCount, c.PipelineComponentCount, c.ExpressionCount,
                c.SqlStatementCount, c.ScriptTaskCount, c.LoopCount, c.EventHandlerCount, c.UnmappedNodeCount,
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
