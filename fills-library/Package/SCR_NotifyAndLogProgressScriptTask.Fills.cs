using Etl.Core.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Package.Model;

namespace Package.ScriptTasks;

// ssisx-fill: GapId=SCRIPT-TASK:Package:SCR_NotifyAndLogProgress.ScriptTask Author=GitHub Copilot Date=2026-09-03 EvidenceSha256=d4e7d94002e26ca46558d5f72c7143750195d7ac42fa6704cfda3a970bf99368
public sealed partial class SCR_NotifyAndLogProgressScriptTask
{
    private partial async Task RunScriptAsync(ScriptTaskContext ctx, CancellationToken ct)
    {
        var startedAt = DateTime.Now;
        var rawStart = ctx.Variables.Get<string>("User::BatchStartTime");

        if (!string.IsNullOrEmpty(rawStart))
        {
            DateTime.TryParse(rawStart, out startedAt);
        }

        var elapsed = DateTime.Now.Subtract(startedAt).TotalSeconds;

        var staging = ctx.Uow.Context.Set<StagingCustomers>();
        var totalRows = await staging.CountAsync(ct);
        var invalidRows = await staging.CountAsync(x => !x.IsValidRow, ct);

        var message = string.Format(
            "Staged {0} row(s), {1} flagged invalid, {2:F2}s after batch start.",
            totalRows, invalidRows, elapsed);

        await ctx.Uow.ExecuteSqlAsync(
            "INSERT INTO dbo.EtlAuditLog (PackageName, StepName, EventTime, RowsAffected, Message) VALUES ({0}, {1}, {2}, {3}, {4})",
            [ctx.Load.PackageName, "Batch Progress", DateTime.Now, totalRows, message],
            ct);

        ctx.Variables.Set("User::RowsLoaded", totalRows);

        ctx.Services.GetRequiredService<ILogger<SCR_NotifyAndLogProgressScriptTask>>()
            .LogInformation("Staged {TotalRows} row(s), {InvalidRows} invalid.", totalRows, invalidRows);
    }
}
