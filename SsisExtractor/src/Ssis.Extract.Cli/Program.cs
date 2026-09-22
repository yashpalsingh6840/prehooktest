using Ssis.Extract.Model.Diagnostics;

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

        try
        {
            return Dispatch(command, rest);
        }
        catch (Exception ex)
        {
            // Every command's own Run() already catches the failures it expects (bad flags, an
            // unreadable input, a load error) and returns exit 2/3 with a short message -- this
            // is only reached by something genuinely unexpected (a real ssisx bug), which is
            // exactly what a client site cannot just email a repro for: nothing can leave that
            // machine. Write a compact, client-data-free crash report instead, so the person
            // running this can screenshot ONE small file rather than nothing at all.
            var scrubbed = DiagnosticReport.ScrubArgs("ssisx", command, rest);
            var path = DiagnosticReport.Capture("ssisx", scrubbed, "top-level (uncaught)", ex,
                outDir: DiagnosticReport.TryFindOutDir(rest));
            Console.Error.WriteLine($"error: ssisx hit an unexpected internal error and stopped.");
            Console.Error.WriteLine($"A diagnostic file with no client data was written to: {path}");
            Console.Error.WriteLine("Please share that file (e.g. a screenshot of it) so this can be fixed.");
            return 99;
        }
    }

    private static int Dispatch(string command, string[] rest) => command switch
        {
            "extract" => ExtractCommand.Run(rest),
            "generate" => GenerateCommand.Run(rest),
            "apply-fills" => ApplyFillsCommand.Run(rest),
            "apply-tests" => ApplyTestsCommand.Run(rest),
            // Merged into 'extract' (2026-09) -- all five always re-parsed the same --input
            // independently and never depended on one another, so they no longer exist as
            // separate top-level verbs. Redirect rather than a bare "unknown command", since
            // an old script/muscle memory/prompt file typing one of these should be told
            // exactly what changed, not left guessing.
            "graph" or "report" or "conformance" or "testgen" or "diff" =>
                Fail($"'{command}' was merged into 'extract' -- run 'ssisx extract --help' to see the equivalent flags" +
                     (command == "diff" ? " (--diff-against)." : ".")),
            "pull" or "enrich" =>
                Fail($"'{command}' needs SSISDB catalog access, which this engagement does not have (Phase0-Extractor-Plan.md §11 decision 4) -- use 'ssisx extract --diff-against' against a deployed .ispac instead for drift detection."),
            _ => Fail($"unknown command '{command}'. Run 'ssisx --help'."),
        };

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

            Four commands, in the order you'd normally use them: 'extract' (survey),
            'generate' (produce C#), 'apply-fills' (bring in hand-ported Tier-1/2 work),
            'apply-tests' (optional coverage enrichment). 'extract' alone covers what used
            to be five separate verbs (graph/report/conformance/testgen/diff, merged 2026-09
            because none of them ever depended on another's output -- each independently
            re-parsed the same --input, so there was no real pipeline between them, only a
            shared "describe this input" purpose) -- see below for how their old flags map
            onto it.

            Running against ONE package, or a named few, instead of a whole portfolio:
            both 'extract' and 'generate' accept --package <name> (repeatable, and
            comma-splittable within one value: --package A,B is the same as
            --package A --package B). <name> is matched against the package's own
            ObjectName (case-insensitive exact match -- the .dtsx's own DTS:ObjectName,
            which is USUALLY but not always its filename without the extension). Point
            --input at the portfolio ROOT with --recursive as usual; --package narrows
            AFTER the sweep, so the same load-failure/duplicate-name handling applies
            either way. A name that matches nothing is a hard error (exit 2), never a
            silent no-op -- run without --package first to see every name --input
            actually contains (inventory.csv, or just the console output, lists them).
            This is the knob to reach for on a large portfolio: run 'extract' once across
            everything (read-only), then run 'generate' --package by name for just the
            packages you actually want C# for.

              ssisx extract --input <dir|file.dtsx|file.dtproj|file.ispac> --out <dir>
                             [--recursive] [--package <name>] [--no-redact]
                             [--fail-under <pct>] [--weights <path.json>]
                             [--claims <dir>] [--check] [--diff-against <file>]

            extract options:
              --input <path>    A .dtproj (extracts the whole project + its packages), a
                                 single .dtsx, an .ispac (a deployed project -- it is just a
                                 zip), or a directory holding any mix of those, INCLUDING
                                 loose .dtsx files with no .dtproj beside them (add
                                 --recursive to search subdirectories). A package that
                                 cannot be read is skipped with a warning and listed in
                                 load-failures.md -- one bad package never costs you the
                                 rest of the folder.
              --out <dir>       Output directory (everything below lands here).
              --package <name>  Only this package (repeatable/comma-splittable) -- see above.
              --no-redact       Do not strip Password=/Pwd= from connection strings.
                                 Off by default.
              --recursive       With a directory --input, search subdirectories for every
                                 .dtproj instead of expecting exactly one at the top level.
              --fail-under <pct> Exit 3 if any extracted package's coverage percentage
                                 (plan §7.2 -- see each spec.json's Coverage field) is
                                 below <pct>. Output is still written either way; this
                                 only affects the exit code, for CI gating.
              --weights <path>  Override the complexity-scoring weights (config/
                                 complexity-weights.json has the defaults -- plan §5.6).
              --claims <dir>    Where the gate-1 claim files live (default: <out>/
                                 conformance/claims). Claim files are HAND-MAINTAINED and
                                 NEVER overwritten -- edit them as the rewrite progresses.
              --check           Exit 1 unless every gate-1 obligation is Implemented or
                                 explained-NotApplicable, for CI gating.
              --diff-against <file.dtsx|file.ispac>
                                 Also run a semantic (not textual) diff between the
                                 package(s) selected above and whatever's in this file --
                                 typically a repo .dtsx vs. a deployed .ispac, to find
                                 source-vs-deployed drift. Off by default: this is the one
                                 piece with a genuinely different shape (compare TWO
                                 things, not survey one), so it only runs when asked.
                                 Writes diff-report.md/.json; exits 1 if any difference is
                                 found (a CI drift gate).

            Every 'extract' run writes, unconditionally:
              packages/<Package>.spec.json, project.spec.json, _meta.json  -- the
                canonical machine-readable spec every other command/tool reads
              inventory.csv/.md, findings.csv/.md                         -- per-package
                complexity/classification and every RulesEngine finding, with location
              nondeterministic.json                                       -- columns to
                exclude from a parallel-run row-hash comparison (plan §5.8)
              datatouch.md/.json, effects.json/.md                        -- every table/
                file/procedure a package reads or writes, PLUS every task type this tool
                has no model for as an explicit UncharacterizedTask (never silently
                dropped -- gate 3 must refuse PASS while any effect here is unverified)
              primary-keys.json/.md                                       -- best-effort
                naming-convention PK guesses, needs human confirmation (a .dtsx carries
                no real PK concept)
              expressions.csv, expression-functions.md                    -- every
                expression and the SSIS expression-language surface actually used
              sql/ + sql-analysis.json                                    -- every SQL
                statement, individually addressable, with its parse result
              lineage/*.mmd + *.dot                                       -- one Mermaid +
                Graphviz DOT pair per Data Flow Task's column-level lineage (plan §5.1)
              graph/portfolio.mmd + dependencies.json                     -- cross-package
                dependency graph
              conformance/<Package>.rules.json, conformance-rules.csv,
              conformance-report.json/.md                                 -- gate 1
                (Migration-Validation-Plan.md): the obligations a rewrite must satisfy
                (control flow, ordering, data flow, columns, schema, SQL, load semantics,
                script code) and how many are accounted for, joined against --claims
              testgen/<Package>.ExpressionTests.cs, testgen-summary.md     -- gate 2
                (Migration-Validation-Plan.md §4): an xUnit test per Derived Column
                expression, boundary cases included, every expected value COMPUTED by
                evaluating it through Ssis.Runtime.Expressions (itself verified against
                the real SSIS evaluator) at generation time, never hand-authored
              unmapped.md, portfolio.md, portfolio-digest.md/.json,
              generation-readiness.md, load-failures.md                   -- portfolio
                roll-ups, incl. the client-safe digest and whether 'generate' would
                produce each package with zero blocking gaps

            Exit codes for 'extract': 0 clean. 2 a real usage/load problem (bad flags, an
            unreadable input, a --package name matching nothing). 1 --check was given and
            gate 1 is incomplete, OR --diff-against found a semantic difference. 3
            --fail-under was given and not met. Precedence when more than one applies:
            2, then 1, then 3.

              ssisx generate --input <dir|file.dtsx|file.dtproj|file.ispac> --out <dir>
                              [--recursive] [--package <name>] [--namespace-prefix <prefix>]
                              [--unsafe-skip-seams] [--skip-tests] [--notifications]
                              [--fills <dir>] [--etl-core <path>] [--framework net8.0|net10.0]

            generate options: turns a package into a runnable C# ETL project -- entity,
            DbContext, CSV row + ClassMap, Derived Column transform, the oracle-verified
            SsisFn primitives it actually uses, Program.cs, and .csproj/appsettings.json --
            under <out>/generate/<Package>/, calling Etl.Core (shipped alongside this tool
            at Tools/Etl.Core) for plumbing: SqlBulkCopy, CSV reading, and (only with
            --notifications) email notification.
            --etl-core <path> copies that folder into <out>/generate/Etl.Core/ as part of
            THIS command (e.g. --etl-core Tools/Etl.Core, or a relative ../Etl.Core if you're
            already inside Tools/SsisExtractor) -- do this every time unless you have a
            reason not to: without it, the generated solution will not build at all (every
            project references Etl.Core.csproj), and forgetting the copy as a separate manual
            step is an easy, real mistake to make. NO verification runs
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
            Every package also gets a STARTER xUnit test project at
            <out>/generate/<Package>.Tests/ -- a sibling directory, never nested inside
            <Package>/, so the main project's own compile glob never picks up a test file.
            Covers every generated method, not just transforms: RunAsync's own failure/happy
            paths, every Execute SQL/File System Task, every source and sink (CSV, fixed-width,
            Excel, SQL, both destination kinds), a Conditional Split's router, Merge Join
            mappers, OLE DB Command, ForEach loops, Multicast, Aggregate. Every expected value
            is computed (not guessed) -- transform/router assertions go through the same
            oracle-verified expression evaluator gate 2 (now part of `ssisx extract`) already
            uses. A test needing something no fake can provide (a real database, a real .xlsx
            file, a real secondary-connection server) is tagged [Trait("Category",
            "Integration")] -- `dotnet test --filter Category!=Integration` is the always-green
            baseline, and should pass with ZERO fills applied right after a fresh generate. A
            component outside this pilot's own scope is a named, non-blocking gap (often a
            TEST-ORACLE/LOCAL-DATA one, both answered the same way as any other Tier-1 gap --
            see "Working a gap" in Tools/.github/copilot-instructions.md), not a silent gap.
            --skip-tests omits the whole {Package}.Tests project (and its own TEST-ORACLE/
            LOCAL-DATA gaps, which would otherwise ask for a test/sample file that no longer
            exists) for this generate call -- named plainly, unlike --unsafe-skip-seams,
            because skipping tests only loses coverage and can never make this tool produce
            silently WRONG code. appsettings.Development.json is unaffected either way -- it
            also serves a real, non-test `dotnet run --environment Development`.
            --notifications wires IPackageResultNotifier/AddEmailNotifications (Etl.Core's
            email-on-success/-failure hook) into the generated RunAsync. OFF by default: a
            .dtsx carries no notification-recipient information at all, so emitting this call
            on every package would add a capability the original SSIS package never had any
            equivalent of, on the strength of nothing but "maybe someone configures it
            later" -- with it off, no notifier is resolved, no email code is emitted, and
            appsettings.json carries no Notification section. Pass it once real
            recipients/SMTP settings exist to configure (a {Package}.Notification gap is then
            reported, same non-blocking shape as every other "fill this in by hand" gap).
            --framework selects the generated solution's target framework -- net10.0
            (default, unchanged) or net8.0. This is not just a TargetFramework string: Etl.Core's
            EF Core SqlServer provider (10.0.11) targets net10.0 ONLY, so net8.0 pins a
            different EF Core MAJOR VERSION (9.0.15, the newest that still targets net8.0)
            instead. --etl-core copies the matching Directory.Build.props/Directory.Packages.props
            into the copied Etl.Core too, so the two always agree -- pass --framework net8.0
            on every generate call for a client stuck on .NET 8, not just once.
            --namespace-prefix roots every generated namespace under a prefix (e.g.
            "Contoso.Etl") instead of just the package name. Generated code under
            <out>/generate/ has no hand-editing expectation -- rerun freely.
            Seams are ON BY DEFAULT: the two things whose logic this tool cannot translate,
            but whose source IS in the .dtsx, become `private partial` methods for a human
            to implement instead of being silently omitted -- a Script Component becomes ONE
            combined method (named after the component, returning a small record with every
            column it produces together) on its transform class, and a Script Task
            becomes a whole ILoadTask class with a RunScriptAsync seam (so it keeps its real
            position in the step order, and its port gets the package's shared variables).
            This deliberately makes the generated project FAIL to build (CS8795) until every
            seam is filled -- the alternative is a package that builds clean while silently
            missing those columns and skipping those tasks entirely, which is exactly the
            failure class this tool otherwise refuses to allow. --seams is accepted as a
            no-op (it's already the default). --unsafe-skip-seams opts back into the old
            silent-omission behavior -- named to make that risk visible at the call site,
            not just here. Fills live outside <out>/generate/ and ARE hand-maintained, the
            same way `extract`'s own gate-1 claims are: --fills points at that directory
            (default <out>/fills/), which ssisx only ever READS -- both the seam parts
            applied by 'apply-fills' and <Package>.decisions.json.

              ssisx apply-fills --out <dir> [--fills <dir>] [--claims <dir>]

            apply-fills options: copies hand-maintained Tier-2 fills -- the second part of a
            generated partial class, implementing the Script-Component or RunScriptAsync seams
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
            <out>/conformance/claims/, the same default `extract` itself uses), this also
            names the gate-1 ScriptCode RuleId that translation answers and its current claim
            status -- read-only, never written; only a human updates the claims file.
            Exits 1 on an orphan, 3 while any seam is unfilled or stale.

              ssisx apply-tests --out <dir> [--fills <dir>]

            apply-tests options: NOT part of the gap-fill workflow above -- see
            Docs/AI-Test-Enrichment-Plan.md. Voluntary enrichment of an already-green, already-
            fully-generatable package, raising coverage past the deterministic starter tests
            'generate' already wrote; never appears in gaps.json and never affects whether a
            package counts as generatable. Copies every
            <out>/fills[-library]/<Package>/MoreTests/*.cs into
            <out>/generate/<Package>.Tests/MoreTests/ -- a deliberately separate folder from
            apply-fills' own Fills/Tests folders, so the two mechanisms never collide. Every
            file present is copied UNCONDITIONALLY: there is no GapId to check a "more test"
            against, so unlike a Tier-1/2 fill there is nothing to be Stale/Orphaned about --
            a test that references a renamed/removed method or column simply fails to compile
            the next time the package is regenerated, which is the self-check this relies on
            instead. A file MAY carry a '// ssisx-more-test: Author=... Date=... Targets=...'
            comment as its first line, purely for audit -- one missing is flagged, not refused.
            Every file and its outcome is written fresh to <out>/tests-applied.json every run
            (a separate manifest from fills-applied.json, on purpose). Exits 1 only when a
            package under fills[-library]/ was never generated into --out at all.

            Exit codes: 0 success. 1 a gate found a real problem (extract --check, or
            --diff-against) / an orphaned fill (apply-fills). 2 usage/extraction error. 3 a
            package's coverage was below --fail-under (extract) / generate produced one or
            more gaps (generate) / a seam is unfilled or stale (apply-fills). See each
            command's own section above for its exact precedence when more than one applies.
            98 (generate only) one or more packages hit a genuine, unexpected internal ssisx
            error during generation and were skipped -- every OTHER package still generated
            normally. 99 an unexpected internal error crashed the whole run before it could
            finish at all. Both 98 and 99 write a compact ssisx-diagnostic-<timestamp>.txt
            containing no package/column/file names or other client data -- only the exception
            type, a stack trace filtered to this tool's own source, and which package ordinal
            (never its name) was being processed. If this ever happens on a client site (where
            nothing else can leave the machine), that file is what to screenshot and share back
            so the tool itself can be fixed.

            Not implemented -- need SSISDB catalog access this engagement does not have
            (plan §11 decision 4); use 'extract --diff-against' against a deployed .ispac
            instead:
              pull, enrich
            """);
    }
}
