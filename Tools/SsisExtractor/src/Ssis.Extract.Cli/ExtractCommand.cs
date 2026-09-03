using System.Globalization;
using Ssis.Extract.Dtsx;
using Ssis.Extract.Model.Meta;
using Ssis.Extract.Model.Package;
using Ssis.Extract.Model.Serialization;

namespace Ssis.Extract.Cli;

/// <summary>
/// <c>ssisx extract</c> -- reads one .dtsx, one .dtproj (+ its packages), or a directory
/// (optionally --recursive for multiple projects), and writes deterministic spec.json
/// files (plan §6.1). <c>--fail-under</c> checks each package's coverage percentage
/// (plan §7.2) once extraction finishes; encrypted-package support (--password, the
/// object-model fallback, plan §2.1) is still accepted as a no-op -- that one really is
/// later-slice work, and rejecting the flag now would just make the command line reject
/// on upgrade instead of silently doing nothing.
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
            // specific per-name warning(s) above.
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
