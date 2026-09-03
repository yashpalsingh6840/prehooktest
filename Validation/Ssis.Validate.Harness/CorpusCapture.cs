using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Ssis.Validate.Harness;

/// <summary>
/// Captures a golden corpus entry from a REAL run of a package against this dev box's live
/// SSISDB (plan §2's Gate 0, prerequisite to Gate 3 rather than Gate 3 itself -- there is no
/// rewrite yet for the harness to validate, so this half runs against the real SSIS package
/// instead, exactly once per corpus entry, to record what it actually did).
///
/// Deliberately Windows-only in spirit even though nothing here references the GAC: it drives
/// <c>SsisRunner.exe</c> against the local SSISDB catalog, which is itself Windows-only
/// infrastructure (CLAUDE.md). The REPLAY side (<see cref="GateRunner"/>) is what actually
/// needs to be portable/containerizable -- this class is a one-time authoring tool.
/// </summary>
public static class CorpusCapture
{
    public static async Task<int> Run(
        string packageKey, string packageName, string runnerExePath,
        string ssisPocConnectionString, string ssisDbConnectionString,
        List<(string Table, string KeyColumn)> tables,
        List<string> inputFiles, Dictionary<string, string> parameters,
        string outDir, string? loggingLevel = null)
    {
        var correlationId = $"corpus-capture-{Guid.NewGuid():N}";

        Console.WriteLine($"[capture] snapshotting BEFORE state for {packageName}...");
        var before = new Dictionary<string, TableSnapshot>();
        foreach (var (table, key) in tables)
        {
            before[table] = await SnapshotService.Capture(ssisPocConnectionString, table, key);
        }

        Console.WriteLine($"[capture] running {packageKey} via SsisRunner (correlation {correlationId})...");
        var runArgs = new List<string> { "--package", packageKey, "--correlation", correlationId, "--wait" };
        if (loggingLevel is not null)
        {
            // Stage row counts (catalog.execution_data_statistics) are only populated at a
            // verbose enough logging level -- level 1 (this ctl framework's default,
            // CLAUDE.md) writes nothing there. Level 3 (Verbose) is what the "34 vs 423-769
            // messages" note in CLAUDE.md was measuring; not itself pinned to a single exact
            // level by this tool, so it's opt-in via --logging-level rather than hardcoded.
            runArgs.Add("--logging-level");
            runArgs.Add(loggingLevel);
        }
        foreach (var (name, value) in parameters)
        {
            runArgs.Add("--set");
            runArgs.Add($"{name}={value}");
        }

        var exitCode = await RunProcess(runnerExePath, runArgs);
        if (exitCode != 0)
        {
            Console.Error.WriteLine($"[capture] SsisRunner exited {exitCode} -- aborting capture (run may have failed; check SsisRunner's own output above).");
            return exitCode;
        }

        Console.WriteLine("[capture] snapshotting AFTER state...");
        var after = new Dictionary<string, TableSnapshot>();
        foreach (var (table, key) in tables)
        {
            after[table] = await SnapshotService.Capture(ssisPocConnectionString, table, key);
        }

        Console.WriteLine("[capture] resolving RunId/ExecutionId via CorrelationId...");
        var (runId, executionId) = await ResolveRun(ssisPocConnectionString, correlationId);

        List<StageRowCount> stageCounts = [];
        if (executionId is long execId)
        {
            stageCounts = await CaptureStageRowCounts(ssisDbConnectionString, execId);
        }
        else
        {
            Console.Error.WriteLine("[capture] warning: could not resolve an ExecutionId -- stage row counts will be empty.");
        }

        var runDir = Path.Combine(outDir, packageName, runId?.ToString() ?? "unknown");
        Directory.CreateDirectory(runDir);
        Directory.CreateDirectory(Path.Combine(runDir, "input"));

        var copiedInputNames = new List<string>();
        foreach (var file in inputFiles)
        {
            var dest = Path.Combine(runDir, "input", Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
            copiedInputNames.Add(Path.GetFileName(file));
        }

        foreach (var (table, snapshot) in before)
        {
            SnapshotService.WriteToFile(snapshot, Path.Combine(runDir, $"before.{SanitizeFileName(table)}.json"));
        }
        foreach (var (table, snapshot) in after)
        {
            SnapshotService.WriteToFile(snapshot, Path.Combine(runDir, $"after.{SanitizeFileName(table)}.json"));
        }

        var manifest = new CorpusManifest
        {
            PackageName = packageName,
            RunId = runId ?? -1,
            ExecutionId = executionId,
            CapturedAtUtc = DateTime.UtcNow.ToString("o"),
            Parameters = parameters,
            InputFiles = copiedInputNames,
            StageRowCounts = stageCounts,
        };
        File.WriteAllText(Path.Combine(runDir, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"[capture] wrote corpus entry to {runDir}");
        foreach (var (table, snap) in after)
        {
            Console.WriteLine($"[capture]   {table}: {before[table].Rows.Count} row(s) before -> {snap.Rows.Count} row(s) after");
        }
        return 0;
    }

    private static async Task<int> RunProcess(string exePath, List<string> args)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {exePath}");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static async Task<(long? RunId, long? ExecutionId)> ResolveRun(string ssisPocConnectionString, string correlationId)
    {
        using var conn = new SqlConnection(ssisPocConnectionString);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(
            "SELECT TOP 1 r.RunId, ri.ExecutionId FROM ctl.Run r " +
            "JOIN ctl.RunItem ri ON ri.RunId = r.RunId " +
            "WHERE r.CorrelationId = @cid ORDER BY r.RunId DESC", conn);
        cmd.Parameters.AddWithValue("@cid", correlationId);
        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return (null, null);
        var runId = reader.GetInt64(0);
        var executionId = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
        return (runId, executionId);
    }

    private static async Task<List<StageRowCount>> CaptureStageRowCounts(string ssisDbConnectionString, long executionId)
    {
        var results = new List<StageRowCount>();
        using var conn = new SqlConnection(ssisDbConnectionString);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(
            "SELECT task_name, source_component_name, destination_component_name, rows_sent " +
            "FROM catalog.execution_data_statistics WHERE execution_id = @execId ORDER BY data_stats_id", conn);
        cmd.Parameters.AddWithValue("@execId", executionId);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new StageRowCount
            {
                TaskName = reader.GetString(0),
                SourceComponentName = reader.IsDBNull(1) ? null : reader.GetString(1),
                DestinationComponentName = reader.IsDBNull(2) ? null : reader.GetString(2),
                RowsSent = reader.GetInt64(3),
            });
        }
        return results;
    }

    private static string SanitizeFileName(string s) => s.Replace("[", "").Replace("]", "").Replace(".", "_");
}
