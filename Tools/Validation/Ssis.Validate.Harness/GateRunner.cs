using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Ssis.Validate.Harness;

public sealed class GateRunResult
{
    public required string PackageName { get; init; }
    public required bool Passed { get; init; }
    public required int JobExitCode { get; init; }
    public List<TableDiffResult> TableResults { get; init; } = [];
    public required string RanAtUtc { get; init; }
}

/// <summary>
/// Gate 3 itself (Migration-Validation-Plan.md §5): schema restore, seed "before", run the
/// job under test, diff "after" against the golden corpus excluding non-determinism columns.
/// No SSIS/GAC/Windows dependency anywhere in this class -- the plan's own requirement
/// ("must run in minutes and require no SSIS, no Windows, and no production access").
/// </summary>
public static class GateRunner
{
    public static async Task<GateRunResult> Run(
        PackageConfig config, string corpusRunDir, string targetConnectionString,
        string jobCommand, List<string> jobArgs, string jobInputDir, string reportDir)
    {
        Console.WriteLine($"[gate3] restoring schema from {config.SchemaScript}...");
        // On a brand-new server (a fresh container, plan §5 step 1) the target database
        // doesn't exist yet, so a connection string that already names it as the initial
        // catalog would fail to OPEN at all -- before any batch of the script even runs.
        // The schema script itself creates the database and then `USE`s it (Sql/
        // 01_CreateTarget.sql), so this one step deliberately connects at the server level
        // (master) instead; every step after this one uses targetConnectionString as given,
        // which by then resolves fine because the schema script just created that database.
        await SqlScriptRunner.RunFile(WithInitialCatalog(targetConnectionString, "master"), config.SchemaScript);

        Console.WriteLine("[gate3] seeding BEFORE snapshots from the golden corpus...");
        foreach (var table in config.TargetTables)
        {
            var beforePath = Path.Combine(corpusRunDir, $"before.{SanitizeFileName(table.Table)}.json");
            var snapshot = SnapshotService.ReadFromFile(beforePath);
            await SnapshotService.Restore(targetConnectionString, snapshot);
        }

        Console.WriteLine($"[gate3] staging golden input files into {jobInputDir}...");
        Directory.CreateDirectory(jobInputDir);
        var corpusInputDir = Path.Combine(corpusRunDir, "input");
        if (Directory.Exists(corpusInputDir))
        {
            foreach (var file in Directory.EnumerateFiles(corpusInputDir))
            {
                File.Copy(file, Path.Combine(jobInputDir, Path.GetFileName(file)), overwrite: true);
            }
        }

        Console.WriteLine($"[gate3] running job under test: {jobCommand} {string.Join(' ', jobArgs)}");
        var jobExitCode = await RunProcess(jobCommand, jobArgs);
        Console.WriteLine($"[gate3] job exited {jobExitCode}");

        Console.WriteLine("[gate3] diffing AFTER state against the golden corpus...");
        var tableResults = new List<TableDiffResult>();
        foreach (var table in config.TargetTables)
        {
            if (table.KeyColumn is null)
            {
                throw new InvalidOperationException($"{table.Table} has no KeyColumn configured -- fill this in in the package config before running gate 3 (plan §12: generated configs are reviewed by a human)");
            }

            var afterPath = Path.Combine(corpusRunDir, $"after.{SanitizeFileName(table.Table)}.json");
            var expected = SnapshotService.ReadFromFile(afterPath);
            var actual = await SnapshotService.Capture(targetConnectionString, table.Table, table.KeyColumn);
            var diff = SnapshotDiffer.Diff(expected, actual, table.KeyColumn, table.ExcludedColumns.ToHashSet());
            tableResults.Add(diff);
        }

        var result = new GateRunResult
        {
            PackageName = config.PackageName,
            Passed = jobExitCode == 0 && tableResults.All(t => t.IsMatch),
            JobExitCode = jobExitCode,
            TableResults = tableResults,
            RanAtUtc = DateTime.UtcNow.ToString("o"),
        };

        WriteReport(result, reportDir);
        return result;
    }

    private static async Task<int> RunProcess(string command, List<string> args)
    {
        var psi = new ProcessStartInfo(command) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {command}");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static void WriteReport(GateRunResult result, string reportDir)
    {
        Directory.CreateDirectory(reportDir);
        File.WriteAllText(Path.Combine(reportDir, "report.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

        var md = new System.Text.StringBuilder();
        md.AppendLine($"# Gate 3 report -- {result.PackageName}");
        md.AppendLine();
        md.AppendLine($"Ran: {result.RanAtUtc}  |  Job exit code: {result.JobExitCode}  |  **{(result.Passed ? "PASS" : "FAIL")}**");
        md.AppendLine();
        foreach (var t in result.TableResults)
        {
            md.AppendLine($"## {t.Table} -- {(t.IsMatch ? "match" : "MISMATCH")}");
            md.AppendLine($"Expected {t.ExpectedRowCount} row(s), got {t.ActualRowCount}.");
            if (t.Differences.Count > 0)
            {
                md.AppendLine();
                md.AppendLine("| Kind | Key | Detail |");
                md.AppendLine("|---|---|---|");
                foreach (var d in t.Differences)
                {
                    var detail = d.Kind == "Changed"
                        ? string.Join("; ", d.ChangedValues.Select(kv => $"{kv.Key}: expected `{kv.Value.Expected}`, got `{kv.Value.Actual}`"))
                        : "";
                    md.AppendLine($"| {d.Kind} | {d.Key} | {detail} |");
                }
            }
            md.AppendLine();
        }
        File.WriteAllText(Path.Combine(reportDir, "report.md"), md.ToString());
    }

    private static string SanitizeFileName(string s) => s.Replace("[", "").Replace("]", "").Replace(".", "_");

    private static string WithInitialCatalog(string connectionString, string database)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database };
        return builder.ConnectionString;
    }
}
