namespace Ssis.Extract.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        var command = args[0];
        var rest = args[1..];

        return command switch
        {
            "extract" => ExtractCommand.Run(rest),
            "graph" => GraphCommand.Run(rest),
            "report" => ReportCommand.Run(rest),
            "conformance" => ConformanceCommand.Run(rest),
            "testgen" => TestGenCommand.Run(rest),
            "diff" => DiffCommand.Run(rest),
            "pull" or "enrich" =>
                Fail($"'{command}' needs SSISDB catalog access, which this engagement does not have (Phase0-Extractor-Plan.md §11 decision 4) -- use 'ssisx diff' against a deployed .ispac instead for drift detection."),
            _ => Fail($"unknown command '{command}'. Run 'ssisx --help'."),
        };
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            ssisx - SSIS package extractor (phase 0)

            Usage:
              ssisx extract --input <dir|file.dtsx|file.dtproj> --out <dir> [options]
              ssisx --help

            extract options:
              --input <path>    A .dtproj (extracts the whole project + its packages), a
                                 single .dtsx, an .ispac (a deployed project -- it is just a
                                 zip), or a directory containing .dtproj/.ispac files (add
                                 --recursive to search subdirectories).
              --out <dir>       Output directory. Writes project.spec.json,
                                 packages/<Package>.spec.json, and _meta.json.
              --no-redact       Do not strip Password=/Pwd= from connection strings.
                                 Off by default.
              --recursive       With a directory --input, search subdirectories for every
                                 .dtproj instead of expecting exactly one at the top level.
              --fail-under <pct> Exit 3 if any extracted package's coverage percentage
                                 (plan §7.2 -- see each spec.json's Coverage field) is
                                 below <pct>. Extraction output is still written either
                                 way; this only affects the exit code, for CI gating.

              ssisx graph --input <dir|file.dtsx|file.dtproj> --out <dir> [--recursive]

            graph options: same --input/--out/--recursive shape as extract. Writes one
            Mermaid (.mmd) + Graphviz DOT (.dot) pair per Data Flow Task's column-level
            lineage (plan §5.1) under <out>/lineage/. Does not require --out from a prior
            'extract' run -- lineage is derived directly from the parsed pipeline.

              ssisx report --input <dir|file.dtsx|file.dtproj> --out <dir> [--recursive]
                            [--weights <path.json>]

            report options: same --input/--out/--recursive shape as extract/graph, plus
            --weights to override the complexity-scoring weights (config/complexity-
            weights.json has the defaults -- plan §5.6). Writes inventory.csv/.md,
            findings.csv/.md, nondeterministic.json (plan §5.8), datatouch.md/.json,
            primary-keys.json/.md (best-effort naming-convention PK guesses, needs human
            confirmation -- a .dtsx carries no real PK concept), expressions.csv,
            expression-functions.md, sql/ + sql-analysis.json, graph/portfolio.mmd +
            dependencies.json, unmapped.md, and portfolio.md -- the "here is what you own"
            report (plan §5.2-§5.8).

              ssisx conformance --input <dir|file.dtsx|file.dtproj> --out <dir>
                                 [--claims <dir>] [--recursive] [--check]

            conformance options: gate 1 of Migration-Validation-Plan.md -- generates the
            obligations a rewrite must satisfy (control flow, ordering, data flow, source
            columns, target columns/schema, transformations, SQL, load semantics, script
            code) and reports how many are accounted for. Writes conformance/<Package>.
            rules.json, conformance-rules.csv, conformance-report.json/.md, plus an
            all-Pending claims stub per package under --claims (default:
            <out>/conformance/claims). Claim files are HAND-MAINTAINED and never
            overwritten -- edit them as the rewrite progresses. --check exits 1 unless
            every obligation is Implemented or explained-NotApplicable, for CI gating.

              ssisx testgen --input <dir|file.dtsx|file.dtproj> --out <dir> [--recursive]

            testgen options: gate 2 of Migration-Validation-Plan.md §4 -- generates an xUnit
            test file per package (under <out>/testgen/) covering every Derived Column
            expression, with generated boundary cases (over-length cast, NULL/empty operand,
            a too-short SUBSTRING window, a case-folding probe) alongside the normal case.
            Every expected value is computed by evaluating the expression through
            Ssis.Runtime.Expressions (itself verified against the real SSIS evaluator) at
            generation time, not hand-authored. Writes testgen-summary.md listing exactly
            which expressions were covered and which were skipped, with a reason for each.

              ssisx diff --left <file.dtsx|file.ispac> --right <file.dtsx|file.ispac>
                          [--package <name>] [--out <report.md>]

            diff options: semantic (not textual) diff between two versions of a package --
            typically a repo .dtsx vs a deployed .ispac, to find source-vs-deployed drift.
            --package picks one package by name when a side holds several. Without --out
            the report goes to stdout. Exits 1 when any difference is found, so it works
            as a CI drift gate.

            Exit codes: 0 success, 1 diff found (diff) / gate 1 failed (conformance
            --check), 2 usage/extraction error, 3 a package's coverage was below
            --fail-under (extract only).

            Not implemented -- need SSISDB catalog access this engagement does not have
            (plan §11 decision 4); use 'diff' against a deployed .ispac instead:
              pull, enrich
            """);
    }
}
