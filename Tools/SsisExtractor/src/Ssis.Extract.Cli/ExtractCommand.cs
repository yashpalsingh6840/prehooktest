using System.Globalization;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Meta;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Serialization;

namespace Ssis.Extract.Cli;

/// <summary>
/// <c>ssisx extract</c> -- the ONE survey command. Reads one <c>.dtsx</c>, one <c>.dtproj</c>
/// (+ its packages), an <c>.ispac</c>, or a directory (optionally <c>--recursive</c>), and
/// writes deterministic <c>spec.json</c> files (plan §6.1), a human-readable portfolio report
/// (inventory/findings/complexity -- former <c>ssisx report</c>), column-lineage diagrams
/// (former <c>ssisx graph</c>), gate-1 migration-conformance obligations (former
/// <c>ssisx conformance</c>), gate-2 expression unit tests (former <c>ssisx testgen</c>), and,
/// when <c>--diff-against</c> is given, a semantic diff against a second package (former
/// <c>ssisx diff</c>).
///
/// <b>Why these five were merged into one command, not kept as five verbs (2026-09):</b> all
/// five independently re-parse the same <c>--input</c> from raw XML -- none of them reads a
/// PRIOR command's own output back in (confirmed by inspecting each one's own body before this
/// merge) -- so there was never a real pipeline dependency between them, only a shared "describe
/// this input" purpose. Every documented client workflow already ran <c>extract</c>+<c>report</c>
/// back to back as one inseparable step; a portfolio with six top-level command NAMES to learn
/// (plus <c>generate</c>/<c>apply-fills</c>/<c>apply-tests</c>) was judged more confusing to
/// explain than useful as a modularity boundary. <c>diff</c>'s own shape (compare TWO packages,
/// not survey one input) is genuinely different from the other four, which is why it stays
/// OPT-IN behind <c>--diff-against</c> rather than running unconditionally like the rest.
///
/// <b>Output paths are UNCHANGED from before the merge</b> -- <c>packages/*.spec.json</c>,
/// <c>inventory.*</c>, <c>lineage/</c>, <c>conformance/</c> (incl. its claims files, still never
/// overwritten once they exist), <c>testgen/</c>, all land exactly where they always did, so
/// nothing downstream (<c>svk</c> reading <c>packages/*.spec.json</c>, <c>apply-fills</c>'s
/// default <c>--claims</c> lookup) needed to change.
///
/// <b>One real cost of the merge, stated plainly rather than hidden:</b> each of the five
/// underlying pieces still calls <see cref="PackageLoader.Load"/> independently (reusing their
/// own, already-tested bodies unchanged rather than risking a rewrite), so a single
/// <c>ssisx extract</c> invocation now re-parses the input up to five times where it used to be
/// parsed once or twice. For a realistic client portfolio (tens of packages) this is not
/// expected to be user-noticeable; it is a real, deliberate trade against a bigger single-pass
/// rewrite of all five bodies, made in favor of keeping every one of them exactly as tested.
/// </summary>
internal static class ExtractCommand
{
    public static int Run(string[] args)
    {
        string? input = null;
        string? outDir = null;
        var noRedact = false;
        var recursive = false;
        double? failUnder = null;
        var packageNames = new List<string>();
        string? weightsPath = null;
        string? claimsDir = null;
        var check = false;
        string? diffAgainst = null;

        for (var i = 0; i < args.Length; i++)
        {
            try
            {
                switch (args[i])
                {
                    case "--input": input = RequireValue(args, ref i, "--input"); break;
                    case "--out": outDir = RequireValue(args, ref i, "--out"); break;
                    case "--no-redact": noRedact = true; break;
                    case "--recursive": recursive = true; break;
                    // Repeatable, and comma-splittable within one value, so both
                    // `--package A --package B` and `--package A,B` work -- the one
                    // "run just this one, or this handful, not all N" knob every command
                    // that loads packages shares. Matched against ObjectName, see PackageLoader.
                    case "--package":
                        var pkgArg = RequireValue(args, ref i, "--package");
                        packageNames.AddRange(pkgArg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                        break;
                    case "--fail-under":
                        var failUnderRaw = RequireValue(args, ref i, "--fail-under");
                        if (!double.TryParse(failUnderRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedFailUnder))
                        {
                            Console.Error.WriteLine($"error: --fail-under expects a number (percent), got '{failUnderRaw}'");
                            return 2;
                        }
                        failUnder = parsedFailUnder;
                        break;
                    case "--password": RequireValue(args, ref i, "--password"); break;
                    case "--weights": weightsPath = RequireValue(args, ref i, "--weights"); break;
                    case "--claims": claimsDir = RequireValue(args, ref i, "--claims"); break;
                    case "--check": check = true; break;
                    case "--diff-against": diffAgainst = RequireValue(args, ref i, "--diff-against"); break;
                    default:
                        Console.Error.WriteLine($"error: unknown extract option '{args[i]}'");
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
        if (diffAgainst is not null && !Path.Exists(diffAgainst))
        {
            Console.Error.WriteLine($"error: --diff-against path not found: {diffAgainst}");
            return 2;
        }
        // Normalize separators up front -- otherwise a forward-slash path typed on the
        // command line survives verbatim into every path recorded in the output (source
        // paths, _meta.json's Input), mixing '/' and '\' in the same string.
        input = Path.GetFullPath(input);
        outDir = Path.GetFullPath(outDir);

        List<PackageSpec> packageSpecs;
        var projectCount = 0;

        try
        {
            using var loaded = PackageLoader.Load(input, noRedact, recursive, packageNames);
            packageSpecs = loaded.Packages;
            projectCount = loaded.Projects.Count;

            // A --package filter that matched nothing must fail loudly, not silently write
            // an empty extraction that "succeeds" -- PackageLoader already printed the
            // specific per-name warning(s) above. Nothing below (report/graph/conformance/
            // testgen/diff) runs either -- there is nothing for any of them to describe.
            if (packageNames.Count > 0 && packageSpecs.Count == 0)
            {
                Console.Error.WriteLine("error: --package matched no packages under this --input -- nothing extracted.");
                return 2;
            }

            foreach (var (dtprojPath, project) in loaded.Projects)
            {
                // With more than one project in scope, each gets its own subdirectory so
                // their project.spec.json files don't overwrite each other.
                var projectOutDir = loaded.Projects.Count == 1
                    ? outDir
                    : Path.Combine(outDir, Path.GetFileNameWithoutExtension(dtprojPath));
                var projectOutPath = Path.Combine(projectOutDir, "project.spec.json");
                StableJsonWriter.WriteToFile(project, projectOutPath);
                Console.WriteLine($"wrote {projectOutPath}");
            }

            foreach (var pkg in packageSpecs)
            {
                var pkgOutPath = Path.Combine(outDir, "packages", $"{pkg.ObjectName}.spec.json");
                StableJsonWriter.WriteToFile(pkg, pkgOutPath);
                Console.WriteLine($"wrote {pkgOutPath}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }

        var packageCoverage = packageSpecs
            .Select(p => new PackageCoverageSummary { PackageName = p.ObjectName, CoveragePercent = p.Coverage.CoveragePercent })
            .ToList();

        var meta = new RunMeta
        {
            ToolVersion = ToolVersion.Current,
            RunTimeUtc = DateTime.UtcNow,
            Input = Path.GetFullPath(input),
            Redacted = !noRedact,
            PackagesProcessed = packageSpecs.Select(p => p.ObjectName).ToList(),
            PackageCoverage = packageCoverage,
        };
        StableJsonWriter.WriteToFile(meta, Path.Combine(outDir, "_meta.json"));

        Console.WriteLine($"extracted {packageSpecs.Count} package(s) from {Math.Max(projectCount, 1)} project/input(s) -> {Path.GetFullPath(outDir)}");
        foreach (var c in packageCoverage)
        {
            Console.WriteLine($"  {c.PackageName}: {c.CoveragePercent:0.##}% coverage");
        }

        // --- everything below is the former report/graph/conformance/testgen/diff commands,
        // called with the same --input/--out/--recursive/--package this step already validated,
        // so a hard failure here (exit 2) genuinely means one of THEIR OWN checks failed
        // (e.g. a malformed --weights file), not a repeat of a check already passed above. ---

        var packageArgs = BuildPackageArgs(packageNames);

        Console.WriteLine();
        Console.WriteLine("--- portfolio report (former `ssisx report`) ---");
        var reportArgs = new List<string> { "--input", input, "--out", outDir };
        reportArgs.AddRange(packageArgs);
        if (recursive) reportArgs.Add("--recursive");
        if (weightsPath is not null) reportArgs.AddRange(["--weights", weightsPath]);
        var reportExit = ReportCommand.Run([.. reportArgs]);
        if (reportExit == 2) return 2;

        Console.WriteLine();
        Console.WriteLine("--- column lineage diagrams (former `ssisx graph`) ---");
        var graphArgs = new List<string> { "--input", input, "--out", outDir };
        graphArgs.AddRange(packageArgs);
        if (recursive) graphArgs.Add("--recursive");
        var graphExit = GraphCommand.Run([.. graphArgs]);
        if (graphExit == 2) return 2;

        Console.WriteLine();
        Console.WriteLine("--- gate 1: migration conformance (former `ssisx conformance`) ---");
        var conformanceArgs = new List<string> { "--input", input, "--out", outDir };
        conformanceArgs.AddRange(packageArgs);
        if (recursive) conformanceArgs.Add("--recursive");
        if (claimsDir is not null) conformanceArgs.AddRange(["--claims", claimsDir]);
        if (check) conformanceArgs.Add("--check");
        var conformanceExit = ConformanceCommand.Run([.. conformanceArgs]);
        if (conformanceExit == 2) return 2;
        var gateFailed = conformanceExit == 1;

        Console.WriteLine();
        Console.WriteLine("--- gate 2: expression tests (former `ssisx testgen`) ---");
        var testgenArgs = new List<string> { "--input", input, "--out", outDir };
        testgenArgs.AddRange(packageArgs);
        if (recursive) testgenArgs.Add("--recursive");
        var testgenExit = TestGenCommand.Run([.. testgenArgs]);
        if (testgenExit == 2) return 2;

        if (diffAgainst is not null)
        {
            Console.WriteLine();
            Console.WriteLine("--- semantic diff against --diff-against (former `ssisx diff`) ---");
            var diffArgs = new List<string> { "--left", input, "--right", Path.GetFullPath(diffAgainst) };
            diffArgs.AddRange(packageArgs);
            diffArgs.AddRange(["--out", Path.Combine(outDir, "diff-report.md")]);
            var diffExit = DiffCommand.Run([.. diffArgs]);
            if (diffExit == 2) return 2;
            if (diffExit == 1) gateFailed = true;
        }

        if (gateFailed) return 1;

        if (failUnder is not null)
        {
            var failures = packageCoverage.Where(c => c.CoveragePercent < failUnder.Value).ToList();
            if (failures.Count > 0)
            {
                Console.Error.WriteLine($"error: {failures.Count} package(s) below --fail-under {failUnder.Value:0.##}%:");
                foreach (var f in failures)
                {
                    Console.Error.WriteLine($"  {f.PackageName}: {f.CoveragePercent:0.##}%");
                }
                // Extraction itself succeeded and its output is still on disk (a CI gate
                // wants both: the artifacts to inspect, and a failing exit code) -- this
                // is a threshold failure, not an extraction error, hence its own exit code.
                return 3;
            }
        }

        return 0;
    }

    private static List<string> BuildPackageArgs(List<string> packageNames)
    {
        if (packageNames.Count == 0) return [];
        return ["--package", string.Join(",", packageNames)];
    }

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
