using Ssis.Extract.Model.Diagnostics;
using Svk.Core;

namespace Svk.Cli;

public static class SampleDataCommand
{
    public static int Run(string[] args)
    {
        string? spec = null, outDir = null;
        var packages = new List<string>();
        var rows = 20;
        var seed = 20260903;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--spec": spec = CliArgs.Require(args, ref i); break;
                case "--out": outDir = CliArgs.Require(args, ref i); break;
                case "--package": packages.AddRange(CliArgs.SplitPackages(CliArgs.Require(args, ref i))); break;
                case "--rows": rows = int.Parse(CliArgs.Require(args, ref i)); break;
                case "--seed": seed = int.Parse(CliArgs.Require(args, ref i)); break;
                case "--help": PrintHelp(); return 0;
                default: Console.Error.WriteLine($"error: unknown option '{args[i]}'."); return 2;
            }
        }

        if (spec is null || outDir is null)
        {
            Console.Error.WriteLine("error: --spec and --out are required. Run 'svk sampledata --help'.");
            return 2;
        }

        List<Ssis.Extract.Model.Package.PackageSpec> loaded;
        try
        {
            loaded = PackageSpecLoader.Load(spec, packages);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }

        if (loaded.Count == 0)
        {
            Console.Error.WriteLine("error: no packages loaded -- check --spec and --package.");
            return 2;
        }

        var reportLines = new List<string> { "# Sample data -- portfolio report", "" };
        var crashCount = 0;
        var ordinal = 0;
        foreach (var pkg in loaded)
        {
            ordinal++;
            try
            {
                var plan = SampleDataPlanner.Plan(pkg);
                var pkgOut = Path.Combine(outDir, "sampledata", pkg.ObjectName);
                var written = SampleDataWriter.Write(plan, pkgOut, rows, seed);

                reportLines.Add($"## {pkg.ObjectName}");
                reportLines.Add($"- Sources: {plan.Sources.Count}, Destinations: {plan.Destinations.Count}, Lookups: {plan.Lookups.Count}");
                reportLines.AddRange(written.Select(w => $"- {w}"));
                reportLines.Add("");

                Console.WriteLine($"{pkg.ObjectName}: {plan.Sources.Count} source(s), {plan.Destinations.Count} destination(s), {plan.Lookups.Count} lookup(s) -> {pkgOut}");
            }
            catch (Exception ex)
            {
                // One package's crash must not cost every other package's sample data --
                // capture a client-data-free diagnostic (ordinal only, never the package name)
                // and move on to the next package.
                crashCount++;
                var scrubbed = DiagnosticReport.ScrubArgs("svk", "sampledata", args);
                var diagPath = DiagnosticReport.Capture(
                    "svk", scrubbed, "sampledata", ex,
                    context: [("Package", $"{ordinal} of {loaded.Count}")],
                    outDir: outDir);
                Console.Error.WriteLine($"error: package {ordinal} of {loaded.Count} hit an unexpected internal error generating sample data -- skipped, continuing with the rest.");
                Console.Error.WriteLine($"  A diagnostic file with no client data was written to: {diagPath}");
            }
        }

        var sampleDataRoot = Path.Combine(outDir, "sampledata");
        Directory.CreateDirectory(sampleDataRoot);
        File.WriteAllText(Path.Combine(sampleDataRoot, "sampledata-report.md"), string.Join("\n", reportLines));
        return crashCount > 0 ? 98 : 0;
    }

    private static void PrintHelp() => Console.WriteLine("""
        svk sampledata --spec <extract-out-dir> --out <dir> [--package <name>[,<name>...]] [--rows N=20] [--seed N]

        Reads packages/<Package>.spec.json under --spec (from a prior `ssisx extract` run)
        and writes deterministic, schema-correct synthetic CSV/SQL sample data under
        <out>/sampledata/<Package>/, plus a runnable schema.sql per package and (for any
        Lookup with a resolvable join key) a reference table seeded so both the match and
        no-match paths get exercised. Re-running with the same --seed reproduces byte-
        identical output.
        """);
}
