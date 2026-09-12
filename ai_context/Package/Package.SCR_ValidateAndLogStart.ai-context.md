# AI evidence packet — Package / SCR_ValidateAndLogStart

- Scope: `ScriptTask`
- Component path: (control-flow level — not a data-flow component)
- .dtsx file: `D:\PoC\SSIS_Packages\SSIS_From_Sandeep\RBC_Demo_ETL\RBC_Demo_ETL\Package.dtsx`
- Generated file: `D:\tmp\Sandeep_Packages\gen\generate\Package\Package.cs`
- Generated method: `SCR_ValidateAndLogStart`

## Tier 1 finding

Gate 2 can only confirm this Script Task's call site exists in the expected control-flow position (or is guarded/absorbed/inlined into a failure handler) — it cannot judge whether the generated method body faithfully reproduces the original script's logic.

## Left-side (SSIS) semantic evidence

```json
{
  "files": [
    {
      "file_name": "ScriptMain.cs",
      "source_code": "#region Namespaces\nusing System;\nusing System.Data;\nusing System.IO;\nusing System.Data.SqlClient;\nusing Microsoft.SqlServer.Dts.Runtime;\n#endregion\n\nnamespace ST_8d84581b262c4e39a00b720e2c6678e0\n{\n    [Microsoft.SqlServer.Dts.Tasks.ScriptTask.SSISScriptTaskEntryPointAttribute]\n    public partial class ScriptMain : Microsoft.SqlServer.Dts.Tasks.ScriptTask.VSTARTScriptObjectModelBase\n    {\n        // Control Flow Script Task (C#).\n        // 1. Confirms the source CSV referenced by CM_FF_Customers actually exists.\n        // 2. Stamps User::BatchStartTime.\n        // 3. Writes a \"Batch Start\" row into dbo.EtlAuditLog via the OLE DB connection manager.\n        public void Main()\n        {\n            string filePath = Dts.Connections[\"CM_FF_Customers\"].ConnectionString;\n\n            if (!File.Exists(filePath))\n            {\n                Dts.Events.FireError(0, \"SCR - Validate Source And Log Start\",\n                    \"Source file not found: \" + filePath, string.Empty, 0);\n                Dts.TaskResult = (int)ScriptResults.Failure;\n                return;\n            }\n\n            DateTime startTime = DateTime.Now;\n\n            // User::BatchStartTime is a STRING variable (VARTYPE 8), deliberately - DateTime\n            // literals in SSIS are locale-sensitive. Round-trip format keeps it parseable.\n            Dts.Variables[\"User::BatchStartTime\"].Value = startTime.ToString(\"o\");\n\n            // A Script Task must use an ADO.NET connection manager: AcquireConnection on a\n            // native OLE DB manager returns a COM object that will not cast to\n            // OleDbConnection, and its ConnectionString omits the write-only Password.\n            SqlConnection conn = (SqlConnection)Dts.Connections[\"CM_ADO_SSISDemo\"].AcquireConnection(Dts.Transaction);\n            try\n            {                using (SqlCommand cmd = conn.CreateCommand())\n                {\n                    cmd.CommandText = \"INSERT INTO dbo.EtlAuditLog (PackageName, StepName, EventTime, Message) VALUES (@PackageName, @StepName, @EventTime, @Message)\";\n                    cmd.Parameters.AddWithValue(\"@PackageName\", Dts.Variables[\"System::PackageName\"].Value.ToString());\n                    cmd.Parameters.AddWithValue(\"@StepName\", \"Batch Start\");\n                    cmd.Parameters.AddWithValue(\"@EventTime\", startTime);\n                    cmd.Parameters.AddWithValue(\"@Message\", \"Source file validated: \" + filePath);\n                    cmd.ExecuteNonQuery();\n                }\n            }\n            finally\n            {\n                Dts.Connections[\"CM_ADO_SSISDemo\"].ReleaseConnection(conn);\n            }\n\n            Dts.TaskResult = (int)ScriptResults.Success;\n        }\n\n        enum ScriptResults\n        {\n            Success = Microsoft.SqlServer.Dts.Runtime.DTSExecResult.Success,\n            Failure = Microsoft.SqlServer.Dts.Runtime.DTSExecResult.Failure\n        };\n    }\n}"
    }
  ]
}
```

## Right-side (generated C#) method excerpt

```csharp

        var step = new SCR_ValidateAndLogStartScriptTask(packageVariables, services);
        _results.Add(await step.RunAsync(uow, _load, ct));
    
```

## Related generated source

The excerpt above only wires together factory calls; each referenced identifier's own definition (where found in this package's own generated files) follows, since that is typically where the actual per-row/per-script logic lives.

### `SCR_ValidateAndLogStartScriptTask` — `Fills\SCR_ValidateAndLogStartScriptTask.Fills.cs`

```csharp
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

```

### `SCR_ValidateAndLogStartScriptTask` — `ScriptTasks\SCR_ValidateAndLogStartScriptTask.cs`

```csharp
using System.Diagnostics;
using Etl.Core.Abstractions;

namespace Package.ScriptTasks;

/// <summary>
/// Ported SSIS Script Task 'SCR_ValidateAndLogStart' (original language: CSharp).
///
/// The logic is NOT generated -- it lives in a hand-written second part of this class,
/// implementing <see cref="RunScriptAsync"/>. Until one exists the project does not
/// compile (CS8795), which is deliberate: a Script Task silently omitted would leave a
/// package that looks complete and does less than the original.
/// Declared ReadOnlyVariables: System::PackageName
/// Declared ReadWriteVariables: User::BatchStartTime
/// </summary>
public sealed partial class SCR_ValidateAndLogStartScriptTask(PackageVariables variables, IServiceProvider services) : ILoadTask
{
    public string Name => "SCR_ValidateAndLogStart";

    public async Task<StepResult> RunAsync(IUnitOfWork uow, LoadContext load, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        await RunScriptAsync(new ScriptTaskContext(uow, load, variables, services), ct);

        // A Script Task moves no rows through a pipeline, so the row counts are 0 rather
        // than invented. Elapsed time is real.
        return new StepResult(Name, 0, 0, Stopwatch.GetElapsedTime(started));
    }

    /// <summary>
    /// Port of 'SCR_ValidateAndLogStart'. Implement in a second part of this class -- see the
    /// SCRIPT-TASK work packet for this task, which carries its verbatim original source.
    /// </summary>
    private partial Task RunScriptAsync(ScriptTaskContext ctx, CancellationToken ct);
}

```
