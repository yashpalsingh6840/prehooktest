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
            "generate" => GenerateCommand.Run(rest),
            "apply-fills" => ApplyFillsCommand.Run(rest),
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

            Running against ONE package, or a named few, instead of a whole portfolio:
            every command below that loads packages accepts --package <name> (repeatable,
            and comma-splittable within one value: --package A,B is the same as
            --package A --package B). <name> is matched against the package's own
            ObjectName (case-insensitive exact match -- the .dtsx's own DTS:ObjectName,
            which is USUALLY but not always its filename without the extension). Point
            --input at the portfolio ROOT with --recursive as usual; --package narrows
            AFTER the sweep, so the same load-failure/duplicate-name handling applies
            either way. A name that matches nothing is a hard error (exit 2), never a
            silent no-op -- run without --package first to see every name --input
            actually contains (extract/report's own inventory.csv, or just the console
            output, lists them). This is the knob to reach for on a large portfolio: run
            'extract'/'report' once across everything (cheap, read-only), then run
            'generate' --package by name for just the packages you actually want C# for.

            extract options:
              --input <path>    A .dtproj (extracts the whole project + its packages), a
                                 single .dtsx, an .ispac (a deployed project -- it is just a
                                 zip), or a directory holding any mix of those, INCLUDING
                                 loose .dtsx files with no .dtproj beside them (add
                                 --recursive to search subdirectories). A package that
                                 cannot be read is skipped with a warning and listed in
                                 load-failures.md -- one bad package never costs you the
                                 rest of the folder.
              --out <dir>       Output directory. Writes project.spec.json,
                                 packages/<Package>.spec.json, and _meta.json.
              --package <name>  Only this package (repeatable/comma-splittable) -- see above.
              --no-redact       Do not strip Password=/Pwd= from connection strings.
                                 Off by default.
              --recursive       With a directory --input, search subdirectories for every
                                 .dtproj instead of expecting exactly one at the top level.
              --fail-under <pct> Exit 3 if any extracted package's coverage percentage
                                 (plan §7.2 -- see each spec.json's Coverage field) is
                                 below <pct>. Extraction output is still written either
                                 way; this only affects the exit code, for CI gating.

              ssisx graph --input <dir|file.dtsx|file.dtproj> --out <dir>
                           [--recursive] [--package <name>]

            graph options: same --input/--out/--recursive/--package shape as extract.
            Writes one Mermaid (.mmd) + Graphviz DOT (.dot) pair per Data Flow Task's
            column-level lineage (plan §5.1) under <out>/lineage/. Does not require --out
            from a prior 'extract' run -- lineage is derived directly from the parsed
            pipeline.

              ssisx report --input <dir|file.dtsx|file.dtproj> --out <dir>
                            [--recursive] [--package <name>] [--weights <path.json>]

            report options: same --input/--out/--recursive/--package shape as
            extract/graph, plus --weights to override the complexity-scoring weights
            (config/complexity-weights.json has the defaults -- plan §5.6). Writes inventory.csv/.md,
            findings.csv/.md, nondeterministic.json (plan §5.8), datatouch.md/.json,
            primary-keys.json/.md (best-effort naming-convention PK guesses, needs human
            confirmation -- a .dtsx carries no real PK concept), effects.json/.md (every
            table/file/procedure a package changes, PLUS every task type this tool has no
            model for, as an explicit UncharacterizedTask -- the gate-3 harness must refuse
            to report PASS while any effect here is unverified, so a package whose real
            output is e.g. an email or an SFTP drop can never look fully checked just because
            its table effects were), expressions.csv, expression-functions.md, sql/ +
            sql-analysis.json, graph/portfolio.mmd + dependencies.json, unmapped.md, and
            portfolio.md -- the "here is what you own" report (plan §5.2-§5.8).

              ssisx conformance --input <dir|file.dtsx|file.dtproj> --out <dir>
                                 [--claims <dir>] [--recursive] [--check] [--package <name>]

            conformance options: gate 1 of Migration-Validation-Plan.md -- generates the
            obligations a rewrite must satisfy (control flow, ordering, data flow, source
            columns, target columns/schema, transformations, SQL, load semantics, script
            code) and reports how many are accounted for. Writes conformance/<Package>.
            rules.json, conformance-rules.csv, conformance-report.json/.md, plus an
            all-Pending claims stub per package under --claims (default:
            <out>/conformance/claims). Claim files are HAND-MAINTAINED and never
            overwritten -- edit them as the rewrite progresses. --check exits 1 unless
            every obligation is Implemented or explained-NotApplicable, for CI gating.

              ssisx testgen --input <dir|file.dtsx|file.dtproj> --out <dir>
                             [--recursive] [--package <name>]

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

              ssisx generate --input <dir|file.dtsx|file.dtproj|file.ispac> --out <dir>
                              [--recursive] [--package <name>] [--namespace-prefix <prefix>]
                              [--unsafe-skip-seams] [--fills <dir>]

            generate options: turns a package into a runnable C# ETL project -- entity,
            DbContext, CSV row + ClassMap, Derived Column transform, the oracle-verified
            SsisFn primitives it actually uses, Program.cs, and .csproj/appsettings.json --
            under <out>/generate/<Package>/, calling Etl.Core (shipped alongside this tool
            at Tools/Etl.Core -- copy it to <out>/generate/Etl.Core/ before building) for
            plumbing: SqlBulkCopy, CSV reading, email notification. NO verification runs
            against a client's real data or database -- this tool has neither, and does not
            expect them. It only writes source code; building/running the result, and any
            comparison against the original package's real behavior, is the caller's own
            job (Validation/ in this repo's own dev environment shows the shape of that,
            but it depends on this project's own captured corpus and is not part of what
            ships here). Covers exactly milestone 1's scenario: Flat File Source ->
            [Derived Column] -> OLE DB Destination (fast load), N flows per package,
            Execute SQL Task as pre-load. Anything else (a non-fast-load destination, a
            flow with no Flat File Source, a SQL-command destination, an untranslatable
            expression) degrades that one piece to a GenerationGap rather than a guess --
            every gap, with its reason, is listed in generate-report.md alongside how many
            files each package produced.
            --namespace-prefix roots every generated namespace under a prefix (e.g.
            "Contoso.Etl") instead of just the package name. Generated code under
            <out>/generate/ has no hand-editing expectation -- rerun freely.
            Seams are ON BY DEFAULT: the two things whose logic this tool cannot translate,
            but whose source IS in the .dtsx, become `private partial` methods for a human
            to implement instead of being silently omitted -- a column produced by a Script
            Component becomes a Fill_<Column> on its transform class, and a Script Task
            becomes a whole ILoadTask class with a RunScriptAsync seam (so it keeps its real
            position in the step order, and its port gets the package's shared variables).
            This deliberately makes the generated project FAIL to build (CS8795) until every
            seam is filled -- the alternative is a package that builds clean while silently
            missing those columns and skipping those tasks entirely, which is exactly the
            failure class this tool otherwise refuses to allow. --seams is accepted as a
            no-op (it's already the default). --unsafe-skip-seams opts back into the old
            silent-omission behavior -- named to make that risk visible at the call site,
            not just here. Fills live outside <out>/generate/ and ARE hand-maintained, the
            same way 'conformance' claims are: --fills points at that directory (default
            <out>/fills/), which ssisx only ever READS -- both the Fill_* parts applied by
            'apply-fills' and <Package>.decisions.json.

              ssisx apply-fills --out <dir> [--fills <dir>] [--claims <dir>]

            apply-fills options: copies hand-maintained Tier-2 fills -- the second part of a
            generated partial class, implementing the Fill_<Column> or RunScriptAsync seams
            'generate --seams' emitted -- from <out>/fills/<Package>/*.cs into
            <out>/generate/<Package>/Fills/. Reads fills/ and never writes to it. Validation
            is the real point: a fill implementing a seam no gap asked for is reported as
            ORPHANED and deliberately not copied (it usually means the package changed and
            that seam is gone, and copying it would only turn a clear message here into a
            confusing compile error), and every seam with no fill is listed as the CS8795
            the next build will report. A Script Task fill is matched by its CLASS name, not
            its method name -- every one of those seams is called RunScriptAsync, so a
            package with two Script Tasks needs two distinctly-named class parts. A fill MAY
            carry a per-seam '// ssisx-fill: GapId=... Author=... Date=... EvidenceSha256=...'
            comment; a recorded hash that no longer matches the package's current one marks
            that seam (and, since there is no safe way to copy only part of a file, its whole
            file) STALE and NOT copied. Every seam and its outcome is written fresh to
            <out>/fills-applied.json every run. When a Script Task/Component's every seam is
            filled, and a claims file already exists at --claims (default
            <out>/conformance/claims/, the same default 'conformance' itself uses), this also
            names the gate-1 ScriptCode RuleId that translation answers and its current claim
            status -- read-only, never written; only a human updates the claims file.
            Exits 1 on an orphan, 3 while any seam is unfilled or stale.

            Exit codes: 0 success, 1 diff found (diff) / gate 1 failed (conformance
            --check), 2 usage/extraction error, 3 a package's coverage was below
            --fail-under (extract only) / generate produced one or more gaps (generate).

            Not implemented -- need SSISDB catalog access this engagement does not have
            (plan §11 decision 4); use 'diff' against a deployed .ispac instead:
              pull, enrich
            """);
    }
}
