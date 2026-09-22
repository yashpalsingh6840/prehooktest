using System.IO;
using Etl.Core.Abstractions;
using Etl.Core.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Package.ScriptTasks;

// ssisx-fill: GapId=SCRIPT-TASK:Package:SCR_ValidateAndLogStart.ScriptTask Author=Copilot Date=2026-09-22 EvidenceSha256=e03a55826d8326b3e07c823b2caaa8f88a63bb857b5121bc4ecd41ce8830cdad
public sealed partial class SCR_ValidateAndLogStartScriptTask
{
    private async partial Task RunScriptAsync(ScriptTaskContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var filePath = ctx.Services.GetRequiredService<IOptions<FileSourceOptions>>().Value["StagingCustomers"].ResolvedPath;
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Source file not found: " + filePath, filePath);
        }

        var startTime = DateTime.UtcNow;
        ctx.Variables.Set("User::BatchStartTime", startTime.ToString("o"));

        await ctx.Uow.ExecuteSqlAsync(
            "INSERT INTO dbo.EtlAuditLog (PackageName, StepName, EventTime, Message) VALUES ({0}, {1}, {2}, {3})",
            new object?[]
            {
                ctx.Load.PackageName,
                "Batch Start",
                startTime,
                "Source file validated: " + filePath,
            },
            ct);
    }
}
