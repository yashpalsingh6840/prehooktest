using Etl.Core.Abstractions;
using Etl.Core.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Package.ScriptTasks;

// ssisx-fill: GapId=SCRIPT-TASK:Package:SCR_ValidateAndLogStart.ScriptTask Author=GitHub Copilot Date=2026-09-03 EvidenceSha256=e03a55826d8326b3e07c823b2caaa8f88a63bb857b5121bc4ecd41ce8830cdad
public sealed partial class SCR_ValidateAndLogStartScriptTask
{
    private partial async Task RunScriptAsync(ScriptTaskContext ctx, CancellationToken ct)
    {
        var filePath = ctx.Services
            .GetRequiredService<IOptions<FileSourceOptions>>()
            .Value["StagingCustomers"].ResolvedPath;

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Source file not found: {filePath}", filePath);

        var startTime = DateTime.Now;
        ctx.Variables.Set("User::BatchStartTime", startTime.ToString("o"));

        await ctx.Uow.ExecuteSqlAsync(
            "INSERT INTO dbo.EtlAuditLog (PackageName, StepName, EventTime, Message) VALUES ({0}, {1}, {2}, {3})",
            [ctx.Load.PackageName, "Batch Start", startTime, "Source file validated: " + filePath],
            ct);
    }
}
