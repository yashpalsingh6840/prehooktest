using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Ssis.Validate.Harness;

public sealed class GateRunResult
{
    public required string PackageName { get; init; }

    /// <summary>
    /// "Pass" -- the job exited 0, every configured table matched, and every declared effect
    /// has a real checker. "Fail" -- the job exited nonzero or a table mismatched: something
    /// is demonstrably wrong. "Incomplete" -- the job exited 0 and every table that WAS
    /// checked matched, but at least one real effect (a file, a procedure's own writes, an
    /// uncharacterized task) has no checker at all, so the package is NOT proven correct --
    /// it just wasn't caught wrong on the part that was actually compared. Never collapse
    /// Incomplete into Pass: that collapse is exactly the false-green this three-way split
    /// exists to prevent.
    /// </summary>
    public required string Outcome { get; init; }

    public required int JobExitCode { get; init; }
    public List<TableDiffResult> TableResults { get; init; } = [];

    /// <summary>The subset of <see cref="PackageConfig.DeclaredEffects"/> with Verifiability != "Verifiable" -- present (non-empty) exactly when Outcome is "Incomplete".</summary>
    public List<DeclaredEffectConfig> UnverifiedEffects { get; init; } = [];

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

        foreach (var script in config.AdditionalSchemaScripts)
        {
            // Runs against targetConnectionString, not the master-catalog connection above --
            // SchemaScript has already created the database by this point, and a script like
            // Sql/04_CreateLoaderCredential.sql switches database context itself (USE master;
            // ... USE <db>;) so it doesn't need or want a fixed initial catalog either.
            Console.WriteLine($"[gate3] applying additional setup script {script}...");
            await SqlScriptRunner.RunFile(targetConnectionString, script);
        }

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

        // Every table that WAS compared matching is necessary for a Pass but not sufficient --
        // a declared effect with no checker (see PackageConfig.DeclaredEffects's own doc
        // comment) means real output this package produces was never looked at, so the most
        // an all-tables-match result can honestly claim is Incomplete, not Pass.
        var unverifiedEffects = config.DeclaredEffects.Where(e => e.Verifiability != "Verifiable").ToList();
        var tablesOk = jobExitCode == 0 && tableResults.All(t => t.IsMatch);
        var outcome = !tablesOk ? "Fail" : unverifiedEffects.Count > 0 ? "Incomplete" : "Pass";

        var result = new GateRunResult
        {
            PackageName = config.PackageName,
            Outcome = outcome,
            JobExitCode = jobExitCode,
            TableResults = tableResults,
            UnverifiedEffects = unverifiedEffects,
            RanAtUtc = DateTime.UtcNow.ToString("o"),
        };

        WriteReport(result, reportDir);
        return result;
    }

    private static async Task<int> RunProcess(string command, List<string> args)
    {
        var psi = new ProcessStartInfo(command) { UseShellExecute = false };
        // Without this, the child inherits THIS process's current directory (wherever
        // ssis-validate happens to be invoked from), not the job's own folder. A .NET Generic
        // Host job resolves its "appsettings.json" relative to the current directory, and
        // AddJsonFile defaults to optional -- so a mismatch doesn't error, it just silently
        // loads none of the job's configured defaults (e.g. TargetDatabase:AuthMode falls back
        // to its hard-coded C# default instead of appsettings.json's real value). Caught for
        // real running gate 3 against the actual LoadEmployees rewrite: it silently reverted to
        // Windows-integrated auth and failed against the Linux container with a bewildering
        // "Login failed for user '<container-hostname>\Guest'" error, not the missing-password
        // error you'd expect from a config problem.
        psi.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(command));
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
        md.AppendLine($"Ran: {result.RanAtUtc}  |  Job exit code: {result.JobExitCode}  |  **{result.Outcome.ToUpperInvariant()}**");
        md.AppendLine();
        if (result.UnverifiedEffects.Count > 0)
        {
            md.AppendLine($"**{result.UnverifiedEffects.Count} declared effect(s) have no checker -- this package is NOT fully verified, regardless of the table results below:**");
            md.AppendLine();
            md.AppendLine("| Kind | Target | Verifiability | Note |");
            md.AppendLine("|---|---|---|---|");
            foreach (var e in result.UnverifiedEffects)
            {
                md.AppendLine($"| {e.Kind} | {e.Target} | {e.Verifiability} | {e.Note} |");
            }
            md.AppendLine();
        }
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
