using Etl.Core.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Package.ScriptTasks;

// ssisx-fill: GapId=SCRIPT-TASK:Package:SCR_NotifyAndLogProgress.ScriptTask Author=Copilot Date=2026-09-22 EvidenceSha256=d4e7d94002e26ca46558d5f72c7143750195d7ac42fa6704cfda3a970bf99368
public sealed partial class SCR_NotifyAndLogProgressScriptTask
{
    private async partial Task RunScriptAsync(ScriptTaskContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var startedAt = DateTime.UtcNow;
        if (ctx.Variables.TryGet<string>("User::BatchStartTime", out var rawStart)
            && !string.IsNullOrEmpty(rawStart)
            && DateTime.TryParse(rawStart, out var parsedStart))
        {
            startedAt = parsedStart;
        }

        var elapsed = (DateTime.UtcNow - startedAt).TotalSeconds;
        var customers = ctx.Uow.Context.Set<Package.Model.StagingCustomers>();
        var totalRows = await customers.CountAsync(ct);
        var invalidRows = await customers.CountAsync(row => !row.IsValidRow, ct);

        var message = string.Format(
            "Staged {0} row(s), {1} flagged invalid, {2:F2}s after batch start.",
            totalRows,
            invalidRows,
            elapsed);

        await ctx.Uow.ExecuteSqlAsync(
            "INSERT INTO dbo.EtlAuditLog (PackageName, StepName, EventTime, RowsAffected, Message) VALUES ({0}, {1}, {2}, {3}, {4})",
            new object?[]
            {
                ctx.Load.PackageName,
                "Batch Progress",
                DateTime.UtcNow,
                totalRows,
                message,
            },
            ct);

        ctx.Variables.Set("User::RowsLoaded", totalRows);
        ctx.Services.GetRequiredService<ILogger<SCR_NotifyAndLogProgressScriptTask>>()
            .LogInformation("Staged {TotalRows} row(s), {InvalidRows} invalid.", totalRows, invalidRows);
    }
}
