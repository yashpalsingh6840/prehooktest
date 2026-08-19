namespace Ssis.Validate.Harness;

internal static class Program
{
    private static async Task<int> Main(string[] args)
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
            return command switch
            {
                "generate-config" => RunGenerateConfig(rest),
                "capture-corpus" => await RunCaptureCorpus(rest),
                "run" => await RunGate(rest),
                _ => Fail($"unknown command '{command}'. Run 'ssis-validate --help'."),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static int RunGenerateConfig(string[] args)
    {
        var opts = ParseOptions(args);
        var packageName = Require(opts, "package");
        var schemaScript = Require(opts, "schema-script");
        var tables = Require(opts, "tables").Split(',').Select(t => t.Trim()).ToList();
        var nonDetPath = Require(opts, "nondeterministic-json");
        var primaryKeysPath = opts.TryGetValue("primary-keys-json", out var pkp) ? pkp : null;
        var outPath = Require(opts, "out");

        var config = ConfigGenerator.Generate(packageName, schemaScript, tables, nonDetPath, primaryKeysPath);
        ConfigGenerator.WriteToFile(config, outPath);
        var unfilled = config.TargetTables.Where(t => t.KeyColumn is null).Select(t => t.Table).ToList();
        Console.WriteLine(unfilled.Count > 0
            ? $"wrote {outPath}. KeyColumn still needs a human fill-in for: {string.Join(", ", unfilled)} (plan §12: generated configs are reviewed by a human)."
            : $"wrote {outPath}. Every KeyColumn was auto-filled from primary-keys.json -- still confirm each one before trusting it (see the per-table messages above).");
        return 0;
    }

    private static async Task<int> RunCaptureCorpus(string[] args)
    {
        var opts = ParseOptions(args);
        var packageKey = Require(opts, "package-key");
        var packageName = Require(opts, "package-name");
        var runnerExe = Require(opts, "runner-exe");
        var ssisPocConn = Require(opts, "ssispoc-connection");
        var ssisDbConn = Require(opts, "ssisdb-connection");
        var tables = Require(opts, "tables").Split(',').Select(t =>
        {
            var parts = t.Trim().Split(':');
            return (Table: parts[0], KeyColumn: parts[1]);
        }).ToList();
        var inputFiles = opts.TryGetValue("input-files", out var f) ? f.Split(',').Select(x => x.Trim()).ToList() : [];
        var parameters = opts.TryGetValue("set", out var s)
            ? s.Split(',').Select(kv => kv.Split('=', 2)).ToDictionary(kv => kv[0].Trim(), kv => kv[1].Trim())
            : new Dictionary<string, string>();
        var outDir = Require(opts, "out");
        var loggingLevel = opts.TryGetValue("logging-level", out var ll) ? ll : null;

        return await CorpusCapture.Run(packageKey, packageName, runnerExe, ssisPocConn, ssisDbConn, tables, inputFiles, parameters, outDir, loggingLevel);
    }

    private static async Task<int> RunGate(string[] args)
    {
        // --job-arg is repeatable (one value each) rather than a single space-joined
        // --job-args string -- a connection string routinely contains spaces ("Integrated
        // Security=True"), which a naive Split(' ') would silently cut in the wrong place.
        var jobArgs = new List<string>();
        var remaining = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--job-arg" && i + 1 < args.Length)
            {
                jobArgs.Add(args[++i]);
            }
            else
            {
                remaining.Add(args[i]);
            }
        }

        var opts = ParseOptions(remaining.ToArray());
        var configPath = Require(opts, "config");
        var config = ConfigGenerator.ReadFromFile(configPath);
        // SchemaScript is stored relative to the config file itself (portable across
        // machines/CI), so it must be resolved relative to the config's own directory --
        // not the caller's current working directory, which varies by how 'run' is invoked.
        var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        var resolvedSchemaScript = Path.IsPathRooted(config.SchemaScript) ? config.SchemaScript : Path.GetFullPath(Path.Combine(configDir, config.SchemaScript));
        config = new PackageConfig { PackageName = config.PackageName, SchemaScript = resolvedSchemaScript, TargetTables = config.TargetTables };
        var corpusRunDir = Require(opts, "corpus-run");
        var targetConn = Require(opts, "connection");
        var jobCommand = Require(opts, "job");
        var jobInputDir = Require(opts, "job-input-dir");
        var reportDir = Require(opts, "report-dir");

        var result = await GateRunner.Run(config, corpusRunDir, targetConn, jobCommand, jobArgs, jobInputDir, reportDir);
        Console.WriteLine(result.Passed ? $"[gate3] PASS -- {config.PackageName}" : $"[gate3] FAIL -- {config.PackageName}, see {reportDir}");
        return result.Passed ? 0 : 1;
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var opts = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var key = args[i][2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                opts[key] = args[++i];
            }
            else
            {
                opts[key] = "true";
            }
        }
        return opts;
    }

    private static string Require(Dictionary<string, string> opts, string key) =>
        opts.TryGetValue(key, out var v) ? v : throw new ArgumentException($"--{key} is required");

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            ssis-validate -- gate 3 harness (Migration-Validation-Plan.md §5)

            generate-config --package <name> --schema-script <path> --tables <t1,t2,...>
                             --nondeterministic-json <path> [--primary-keys-json <path>]
                             --out <config.json>
              Generates a starter config from `ssisx report`'s non-determinism manifest.
              ExcludedColumns is real generated data. KeyColumn is auto-filled from
              --primary-keys-json (ssisx report's own best-effort naming-convention guess)
              when exactly one confident candidate column exists; still confirm it before
              trusting it -- a .dtsx carries no real primary-key concept, see
              PrimaryKeyCandidateSpec's doc comment. Left null (for a human to fill in) when
              --primary-keys-json is omitted or has no confident candidate for that table.

            capture-corpus --package-key <ctl key> --package-name <name>
                            --runner-exe <path to SsisRunner.exe>
                            --ssispoc-connection <conn str> --ssisdb-connection <conn str>
                            --tables <table:keycol,table:keycol,...>
                            [--input-files <path,path,...>] [--set NAME=VALUE,...]
                            --out <corpus dir>
              Runs a REAL package execution against this box's live SSISDB and records the
              before/after table state, input files, parameters, and per-stage row counts
              as one corpus entry. Windows-only (drives SsisRunner against local SSISDB).

            run --config <config.json> --corpus-run <corpus/<Package>/<RunId> dir>
                --connection <target SQL Server connection string>
                --job <command> [--job-arg <arg>]... --job-input-dir <dir>
                --report-dir <dir>
              Gate 3 itself: restores schema, seeds the "before" snapshot, stages the golden
              input files, runs the job under test, diffs the resulting tables against the
              golden "after" snapshot (excluding the config's ExcludedColumns), and writes
              report.json/report.md. Exit 0 pass, 1 mismatch, 2 usage/setup error.
              No SSIS/GAC dependency -- runs anywhere .NET 8 does, including in a container.
            """);
    }
}
