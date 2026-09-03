using System.Text.Json;
using Ssis.Validate.Harness;

var opts = new Dictionary<string, string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i].StartsWith("--") && i + 1 < args.Length)
    {
        opts[args[i][2..]] = args[++i];
    }
}

string Require(string key) => opts.TryGetValue(key, out var v) ? v : throw new ArgumentException($"--{key} is required");

var mode = Require("mode"); // "pass" or "fail"
var connectionString = Require("connection");
var corpusRunDir = Require("corpus-run");
var configPath = Require("config");
var inputDir = Require("input-dir"); // unused deliberately -- see note below

var config = ConfigGenerator.ReadFromFile(configPath);

// A real job reads its inputs from inputDir (staged there by the harness from the golden
// corpus, plan §5 step 3) and derives its output. This stand-in has no transformation logic
// at all -- it exists only to feed the harness a known-correct or known-wrong result, so it
// reads the corpus's own expected "after" state directly instead. inputDir is accepted and
// intentionally ignored, to keep this job's CLI shape parallel to what a real job's would be.
_ = inputDir;

var corrupted = false;
foreach (var table in config.TargetTables)
{
    var afterPath = Path.Combine(corpusRunDir, $"after.{table.Table.Replace("[", "").Replace("]", "").Replace(".", "_")}.json");
    var snapshot = SnapshotService.ReadFromFile(afterPath);

    if (mode == "fail" && !corrupted && snapshot.Rows.Count > 0)
    {
        // Corrupt exactly one value in the first row, in the first column that is neither
        // the key nor an excluded (non-deterministic) column -- so the harness's diff is
        // exercised on a column it actually compares, not one it would have ignored anyway.
        var candidateColumn = snapshot.Columns.FindIndex(c => c != table.KeyColumn && !table.ExcludedColumns.Contains(c));
        if (candidateColumn >= 0)
        {
            var original = snapshot.Rows[0][candidateColumn];
            var corruptedValue = JsonSerializer.SerializeToElement("SELFTEST-CORRUPTED-VALUE");
            snapshot.Rows[0][candidateColumn] = corruptedValue;
            Console.WriteLine($"[selftest-job] mode=fail: corrupted {table.Table}.{snapshot.Columns[candidateColumn]} on row 0 (was {original}, now \"SELFTEST-CORRUPTED-VALUE\")");
            corrupted = true;
        }
    }

    await SnapshotService.Restore(connectionString, snapshot);
    Console.WriteLine($"[selftest-job] wrote {snapshot.Rows.Count} row(s) to {table.Table}");
}

Console.WriteLine($"[selftest-job] mode={mode} complete.");
return 0;
