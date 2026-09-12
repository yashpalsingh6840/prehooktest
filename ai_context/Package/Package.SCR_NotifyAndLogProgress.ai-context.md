# AI evidence packet — Package / SCR_NotifyAndLogProgress

- Scope: `ScriptTask`
- Component path: (control-flow level — not a data-flow component)
- .dtsx file: `D:\PoC\SSIS_Packages\SSIS_From_Sandeep\RBC_Demo_ETL\RBC_Demo_ETL\Package.dtsx`
- Generated file: `D:\tmp\Sandeep_Packages\gen\generate\Package\Package.cs`
- Generated method: `SCR_NotifyAndLogProgress`

## Tier 1 finding

Gate 2 can only confirm this Script Task's call site exists in the expected control-flow position (or is guarded/absorbed/inlined into a failure handler) — it cannot judge whether the generated method body faithfully reproduces the original script's logic.

## Left-side (SSIS) semantic evidence

```json
{
  "files": [
    {
      "file_name": "ScriptMain.vb",
      "source_code": "Imports System\nImports System.Data\nImports System.Data.SqlClient\nImports Microsoft.SqlServer.Dts.Runtime\n\n' Control Flow Script Task (VB.NET) - runs between the two data flows.\n' 1. Reads User::BatchStartTime (a String, round-trip format) and works out elapsed time.\n' 2. Counts the rows the first data flow staged, and how many were flagged invalid.\n' 3. Writes a \"Batch Progress\" row into dbo.EtlAuditLog and publishes User::RowsLoaded.\n'ScriptMain is the entry point class of the script. Do not change the name, attributes,\n'or parent of this class.\n<Microsoft.SqlServer.Dts.Tasks.ScriptTask.SSISScriptTaskEntryPointAttribute()> _\n<System.CLSCompliantAttribute(False)> _\nPartial Public Class ScriptMain\n    Inherits Microsoft.SqlServer.Dts.Tasks.ScriptTask.VSTARTScriptObjectModelBase\n\n    Public Sub Main()\n\n        Dim startedAt As DateTime = DateTime.Now\n        Dim rawStart As Object = Dts.Variables(\"User::BatchStartTime\").Value\n\n        If rawStart IsNot Nothing AndAlso Not String.IsNullOrEmpty(rawStart.ToString()) Then\n            ' Stored by the C# task as ToString(\"o\"); parse it back the same way.\n            DateTime.TryParse(rawStart.ToString(), startedAt)\n        End If\n\n        Dim elapsed As Double = DateTime.Now.Subtract(startedAt).TotalSeconds\n\n        Dim totalRows As Integer = 0\n        Dim invalidRows As Integer = 0\n\n        Dim conn As SqlConnection = CType(Dts.Connections(\"CM_ADO_SSISDemo\").AcquireConnection(Dts.Transaction), SqlConnection)\n        Try\n            Using cmd As SqlCommand = conn.CreateCommand()\n                cmd.CommandText = \"SELECT COUNT(*), SUM(CASE WHEN IsValidRow = 0 THEN 1 ELSE 0 END) FROM dbo.StagingCustomers\"\n                Using reader As SqlDataReader = cmd.ExecuteReader()\n                    If reader.Read() Then\n                        totalRows = reader.GetInt32(0)\n                        If Not reader.IsDBNull(1) Then invalidRows = reader.GetInt32(1)\n                    End If\n                End Using\n            End Using\n\n            Using cmd As SqlCommand = conn.CreateCommand()\n                cmd.CommandText = \"INSERT INTO dbo.EtlAuditLog (PackageName, StepName, EventTime, RowsAffected, Message) \" & _\n                                  \"VALUES (@PackageName, @StepName, @EventTime, @RowsAffected, @Message)\"\n                cmd.Parameters.AddWithValue(\"@PackageName\", Dts.Variables(\"System::PackageName\").Value.ToString())\n                cmd.Parameters.AddWithValue(\"@StepName\", \"Batch Progress\")\n                cmd.Parameters.AddWithValue(\"@EventTime\", DateTime.Now)\n                cmd.Parameters.AddWithValue(\"@RowsAffected\", totalRows)\n                cmd.Parameters.AddWithValue(\"@Message\", String.Format( _\n                    \"Staged {0} row(s), {1} flagged invalid, {2:F2}s after batch start.\", _\n                    totalRows, invalidRows, elapsed))\n                cmd.ExecuteNonQuery()\n            End Using\n        Finally\n            Dts.Connections(\"CM_ADO_SSISDemo\").ReleaseConnection(conn)\n        End Try\n\n        Dts.Variables(\"User::RowsLoaded\").Value = totalRows\n\n        Dts.Events.FireInformation(0, \"SCR_NotifyAndLogProgress\", _\n            String.Format(\"Staged {0} row(s), {1} invalid.\", totalRows, invalidRows), _\n            String.Empty, 0, False)\n\n        Dts.TaskResult = CInt(ScriptResults.Success)\n\n    End Sub\n\n    Enum ScriptResults\n        Success = Microsoft.SqlServer.Dts.Runtime.DTSExecResult.Success\n        Failure = Microsoft.SqlServer.Dts.Runtime.DTSExecResult.Failure\n    End Enum\n\nEnd Class"
    }
  ]
}
```

## Right-side (generated C#) method excerpt

```csharp

        var step = new SCR_NotifyAndLogProgressScriptTask(packageVariables, services);
        _results.Add(await step.RunAsync(uow, _load, ct));
    
```

## Related generated source

The excerpt above only wires together factory calls; each referenced identifier's own definition (where found in this package's own generated files) follows, since that is typically where the actual per-row/per-script logic lives.

### `SCR_NotifyAndLogProgressScriptTask` — `Fills\SCR_NotifyAndLogProgressScriptTask.Fills.cs`

```csharp
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

```

### `SCR_NotifyAndLogProgressScriptTask` — `ScriptTasks\SCR_NotifyAndLogProgressScriptTask.cs`

```csharp
using System.Diagnostics;
using Etl.Core.Abstractions;

namespace Package.ScriptTasks;

/// <summary>
/// Ported SSIS Script Task 'SCR_NotifyAndLogProgress' (original language: VisualBasic).
///
/// The logic is NOT generated -- it lives in a hand-written second part of this class,
/// implementing <see cref="RunScriptAsync"/>. Until one exists the project does not
/// compile (CS8795), which is deliberate: a Script Task silently omitted would leave a
/// package that looks complete and does less than the original.
/// Declared ReadOnlyVariables: System::PackageName
/// Declared ReadWriteVariables: User::BatchStartTime,User::RowsLoaded
/// </summary>
public sealed partial class SCR_NotifyAndLogProgressScriptTask(PackageVariables variables, IServiceProvider services) : ILoadTask
{
    public string Name => "SCR_NotifyAndLogProgress";

    public async Task<StepResult> RunAsync(IUnitOfWork uow, LoadContext load, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        await RunScriptAsync(new ScriptTaskContext(uow, load, variables, services), ct);

        // A Script Task moves no rows through a pipeline, so the row counts are 0 rather
        // than invented. Elapsed time is real.
        return new StepResult(Name, 0, 0, Stopwatch.GetElapsedTime(started));
    }

    /// <summary>
    /// Port of 'SCR_NotifyAndLogProgress'. Implement in a second part of this class -- see the
    /// SCRIPT-TASK work packet for this task, which carries its verbatim original source.
    /// </summary>
    private partial Task RunScriptAsync(ScriptTaskContext ctx, CancellationToken ct);
}

```
