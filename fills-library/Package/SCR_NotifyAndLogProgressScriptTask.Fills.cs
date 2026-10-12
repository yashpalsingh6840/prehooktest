using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Etl.Core.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Package.ScriptTasks;

// ssisx-fill: GapId=SCRIPT-TASK:Package:SCR_NotifyAndLogProgress.ScriptTask Author=GitHub Copilot Date=2026-10-09 EvidenceSha256=d4e7d94002e26ca46558d5f72c7143750195d7ac42fa6704cfda3a970bf99368
public sealed partial class SCR_NotifyAndLogProgressScriptTask
{
    private partial async Task RunScriptAsync(ScriptTaskContext ctx, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var rawStart = ctx.Variables.Get<string>("User::BatchStartTime", string.Empty);
        if (!string.IsNullOrEmpty(rawStart))
        {
            DateTime.TryParse(rawStart, out startedAt);
        }

        var elapsedSeconds = (DateTime.UtcNow - startedAt).TotalSeconds;
        var counts = await ctx.Uow.Context.Database
            .SqlQueryRaw<BatchProgressCounts>(
                "SELECT COUNT(*) AS TotalRows, SUM(CASE WHEN IsValidRow = 0 THEN 1 ELSE 0 END) AS InvalidRows FROM dbo.StagingCustomers")
            .SingleAsync(ct);
        var invalidRows = counts.InvalidRows ?? 0;

        await ctx.Uow.ExecuteSqlAsync(
            "INSERT INTO dbo.EtlAuditLog (PackageName, StepName, EventTime, RowsAffected, Message) VALUES ({0}, {1}, {2}, {3}, {4})",
            new object?[]
            {
                ctx.Load.PackageName,
                "Batch Progress",
                DateTime.UtcNow,
                counts.TotalRows,
                string.Format(
                    "Staged {0} row(s), {1} flagged invalid, {2:F2}s after batch start.",
                    counts.TotalRows,
                    invalidRows,
                    elapsedSeconds)
            },
            ct);

        ctx.Variables.Set("User::RowsLoaded", counts.TotalRows);

        var logger = ctx.Services.GetRequiredService<ILogger<SCR_NotifyAndLogProgressScriptTask>>();
        logger.LogInformation(
            "Staged {TotalRows} row(s), {InvalidRows} invalid.",
            counts.TotalRows,
            invalidRows);
    }

    private sealed class BatchProgressCounts
    {
        public int TotalRows { get; set; }

        public int? InvalidRows { get; set; }
    }
}