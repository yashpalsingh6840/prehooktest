# AI evidence packet (combined) — Package

- .dtsx file: `D:\PoC\SSIS_Packages\SSIS_From_Sandeep\RBC_Demo_ETL\RBC_Demo_ETL\Package.dtsx`
- Generated file: `D:\tmp\Sandeep_Packages\gen\generate\Package\Package.cs`
- 4 component(s), 12 distinct shared source file(s) (each shown once below)

Adjudicate every component below in ONE pass — see docs/ai-validation-prompt.md for the required output (one Component/Verdict/Rationale block per component). Each component references shared source files by path instead of repeating their content.

## Sources

### `D:\tmp\Sandeep_Packages\gen\generate\Package\Package.cs (method OLESRC_OrdersAndDetails)`

```csharp

        return new SqlRowSource<SalesSummarySqlRow>("OLESRC_OrdersAndDetails", new SqlSourceOptions { ConnectionString = SqlConnectionStringFactory.Build(Db()), CommandText = "SELECT\n    o.CustomerID,\n    c.FullName,\n    c.CleanEmail AS Email,\n    COUNT(DISTINCT o.OrderID) AS TotalOrders,\n    SUM(od.Quantity * od.UnitPrice) AS TotalRevenue,\n    MAX(o.OrderDate) AS LastOrderDate\nFROM dbo.Orders o\nINNER JOIN dbo.OrderDetails od ON od.OrderID = o.OrderID\nINNER JOIN dbo.StagingCustomers c ON o.CustomerID = TRY_CAST(c.CustomerID AS INT)\nWHERE ISNULL(c.IsValidRow, 1) = 1\nGROUP BY o.CustomerID, c.FullName, c.CleanEmail" }, SalesSummarySqlRowReader.Read, uow);
    
```

### `Fills\SalesSummaryTransform.Fills.cs`

```csharp
using Etl.Core.Abstractions;
using Package.Sql;

namespace Package.Mapping;

public sealed partial class SalesSummaryTransform
{
    // ssisx-fill: GapId=SCRIPT-COLUMN:Package:SalesSummary.SCR_ComputeCustomerTier Author=GitHub Copilot Date=2026-09-09 EvidenceSha256=acb950341c43d6703f6b632a5deaa307a86c1d04914c3f382b6ea5acc05f95cf
    private partial SCR_ComputeCustomerTierResult SCR_ComputeCustomerTier(SalesSummarySqlRow row, in RowContext ctx)
    {
        var revenue = row.TotalRevenue;
        string customerTier;
        if (revenue >= 10000) customerTier = "Platinum";
        else if (revenue >= 5000) customerTier = "Gold";
        else if (revenue >= 1000) customerTier = "Silver";
        else customerTier = "Bronze";

        var processedDateTime = ctx.LoadedAtUtc;

        return new(CustomerTier: customerTier, ProcessedDateTime: processedDateTime);
    }
}

```

### `Mapping\SalesSummaryTransform.cs`

```csharp
using Etl.Core.Abstractions;
using Package.Sql;
using Package.Model;

namespace Package.Mapping;

internal readonly record struct SCR_ComputeCustomerTierResult(string CustomerTier, DateTime ProcessedDateTime);

/// <summary>Has 1 unimplemented Tier-2 seam(s): each is a Script Component, computing
/// one or more columns together, whose logic this tool does not translate. Implement each
/// one in a SECOND PART of this partial class -- a class part, not just a method body, so
/// the port can declare its own fields (a compiled Regex, a lookup table). Until then this
/// project deliberately does NOT compile: CS8795, one error per unfilled seam. Put the
/// part in fills/&lt;Package&gt;/SalesSummaryTransform.Fills.cs and run `ssisx apply-fills`.</summary>
public sealed partial class SalesSummaryTransform : IRowTransform<SalesSummarySqlRow, SalesSummary>
{
    public SalesSummary Map(SalesSummarySqlRow row, in RowContext ctx)
    {
        var sCR_ComputeCustomerTierResult = SCR_ComputeCustomerTier(row, ctx);
        return new()
        {
            CustomerID = row.CustomerID,
            FullName = row.FullName,
            Email = row.Email,
            TotalOrders = row.TotalOrders,
            TotalRevenue = row.TotalRevenue,
            LastOrderDate = row.LastOrderDate,
            CustomerTier = sCR_ComputeCustomerTierResult.CustomerTier,
            ProcessedDateTime = sCR_ComputeCustomerTierResult.ProcessedDateTime,
        };
    }

    /// <summary>Script Component 'SCR_ComputeCustomerTier', producing 2 column(s)
    /// together: CustomerTier, ProcessedDateTime. Work packet: SCRIPT-COLUMN SalesSummary.SCR_ComputeCustomerTier.</summary>
    private partial SCR_ComputeCustomerTierResult SCR_ComputeCustomerTier(SalesSummarySqlRow row, in RowContext ctx);
}

```

### `D:\tmp\Sandeep_Packages\gen\generate\Package\Package.cs (method OLEDST_SalesSummary)`

```csharp

        return new SqlBulkSink<SalesSummary>(Opt<BulkCopyOptions>(), Log<SqlBulkSink<SalesSummary>>());
    
```

### `D:\tmp\Sandeep_Packages\gen\generate\Package\Package.cs (method FF_SRC_Customers)`

```csharp

        return new CsvRowSource<StagingCustomersCsvRow>("FF_SRC_Customers", new CsvSourceOptions { FilePath = File("StagingCustomers") }, new StagingCustomersCsvRowMap());
    
```

### `Fills\StagingCustomersTransform.Fills.cs`

```csharp
using System.Text.RegularExpressions;
using Etl.Core.Abstractions;
using Package.Csv;

namespace Package.Mapping;

public sealed partial class StagingCustomersTransform
{
    private static readonly Regex EmailPattern =
        new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    // ssisx-fill: GapId=SCRIPT-COLUMN:Package:StagingCustomers.SCR_CleanseCustomerRow Author=GitHub Copilot Date=2026-09-09 EvidenceSha256=65ea553cecb73be073ab978403716018f50f5590a3f6965e28f9dc8cfad61102
    private partial SCR_CleanseCustomerRowResult SCR_CleanseCustomerRow(StagingCustomersCsvRow row, in RowContext ctx)
    {
        var fullName = (row.FirstName.Trim() + " " + row.LastName.Trim()).Trim();
        var cleanEmail = row.Email.Trim().ToLowerInvariant();

        var email = row.Email.Trim();
        var validId = int.TryParse(row.CustomerID.Trim(), out _);
        var validEmail = email.Length > 0 && EmailPattern.IsMatch(email);
        var isValidRow = validId && validEmail;

        var loadDateTime = ctx.LoadedAtUtc;

        return new(FullName: fullName, CleanEmail: cleanEmail, IsValidRow: isValidRow, LoadDateTime: loadDateTime);
    }
}

```

### `Mapping\StagingCustomersTransform.cs`

```csharp
using Etl.Core.Abstractions;
using Package.Csv;
using Package.Model;

namespace Package.Mapping;

internal readonly record struct SCR_CleanseCustomerRowResult(string FullName, string CleanEmail, bool IsValidRow, DateTime LoadDateTime);

/// <summary>Has 1 unimplemented Tier-2 seam(s): each is a Script Component, computing
/// one or more columns together, whose logic this tool does not translate. Implement each
/// one in a SECOND PART of this partial class -- a class part, not just a method body, so
/// the port can declare its own fields (a compiled Regex, a lookup table). Until then this
/// project deliberately does NOT compile: CS8795, one error per unfilled seam. Put the
/// part in fills/&lt;Package&gt;/StagingCustomersTransform.Fills.cs and run `ssisx apply-fills`.</summary>
public sealed partial class StagingCustomersTransform : IRowTransform<StagingCustomersCsvRow, StagingCustomers>
{
    public StagingCustomers Map(StagingCustomersCsvRow row, in RowContext ctx)
    {
        var sCR_CleanseCustomerRowResult = SCR_CleanseCustomerRow(row, ctx);
        return new()
        {
            CustomerID = row.CustomerID,
            FirstName = row.FirstName,
            LastName = row.LastName,
            Email = row.Email,
            Phone = row.Phone,
            SignupDate = row.SignupDate,
            Country = row.Country,
            FullName = sCR_CleanseCustomerRowResult.FullName,
            CleanEmail = sCR_CleanseCustomerRowResult.CleanEmail,
            IsValidRow = sCR_CleanseCustomerRowResult.IsValidRow,
            LoadDateTime = sCR_CleanseCustomerRowResult.LoadDateTime,
        };
    }

    /// <summary>Script Component 'SCR_CleanseCustomerRow', producing 4 column(s)
    /// together: FullName, CleanEmail, IsValidRow, LoadDateTime. Work packet: SCRIPT-COLUMN StagingCustomers.SCR_CleanseCustomerRow.</summary>
    private partial SCR_CleanseCustomerRowResult SCR_CleanseCustomerRow(StagingCustomersCsvRow row, in RowContext ctx);
}

```

### `D:\tmp\Sandeep_Packages\gen\generate\Package\Package.cs (method OLEDST_StagingCustomers)`

```csharp

        return new RedirectingSqlSink<StagingCustomers, StagingCustomersError>(
            OLEDST_StagingErrors(), StagingCustomersErrorMap.Map, Log<RedirectingSqlSink<StagingCustomers, StagingCustomersError>>());
    
```

### `Fills\SCR_NotifyAndLogProgressScriptTask.Fills.cs`

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

### `ScriptTasks\SCR_NotifyAndLogProgressScriptTask.cs`

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

### `Fills\SCR_ValidateAndLogStartScriptTask.Fills.cs`

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

### `ScriptTasks\SCR_ValidateAndLogStartScriptTask.cs`

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

## Components

### SCR_ComputeCustomerTier (`DataFlowComponent`)

- Component path: `Package\DFT_BuildSalesSummary\SCR_ComputeCustomerTier`
- Generated method: `DFT_BuildSalesSummary`
- Related source files: `D:\tmp\Sandeep_Packages\gen\generate\Package\Package.cs (method OLEDST_SalesSummary)`, `D:\tmp\Sandeep_Packages\gen\generate\Package\Package.cs (method OLESRC_OrdersAndDetails)`, `Fills\SalesSummaryTransform.Fills.cs`, `Mapping\SalesSummaryTransform.cs`

**Tier 1 finding:** 'SCR_ComputeCustomerTier' (Microsoft.ScriptComponentHost) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6). Semantic evidence attached (see `evidence`) for a human/AI reviewer to judge the generated logic against.

**Left-side evidence:**

```json
{"files":[{"file_name":"main.cs","source_code":"using System;\nusing Microsoft.SqlServer.Dts.Pipeline;\n\nnamespace SC_ComputeCustomerTier\n{\n    /// <summary>\n    /// Second row-level Script Component (requirement 4), this one in\n    /// DFT_BuildSalesSummary. Assigns a loyalty tier from the aggregated revenue and\n    /// stamps the processing time.\n    ///\n    /// Thresholds match sql\\04_verify_expected_output.sql exactly, so the package output\n    /// can be diffed against the pure T-SQL harness.\n    /// </summary>\n    [SSISScriptComponentEntryPoint]\n    public class ScriptMain : UserComponent\n    {\n        public override void Input0_ProcessInputRow(Input0Buffer Row)\n        {\n            double revenue = Row.TotalRevenue;\n\n            if (revenue >= 10000)     Row.CustomerTier = \"Platinum\";\n            else if (revenue >= 5000) Row.CustomerTier = \"Gold\";\n            else if (revenue >= 1000) Row.CustomerTier = \"Silver\";\n            else                      Row.CustomerTier = \"Bronze\";\n\n            Row.ProcessedDateTime = DateTime.Now;\n        }\n    }\n}\n"}]}
```

**Right-side excerpt:**

```csharp

        var source = OLESRC_OrdersAndDetails(uow);
        var transform = new SalesSummaryTransform();
        var sink = OLEDST_SalesSummary();

        var step = new DataFlowStep<SalesSummarySqlRow, SalesSummary>("DFT_BuildSalesSummary", source, transform, sink, Log<DataFlowStep<SalesSummarySqlRow, SalesSummary>>());
        _results.Add(await step.RunAsync(uow, _load, ct));
    
```

### SCR_CleanseCustomerRow (`DataFlowComponent`)

- Component path: `Package\DFT_LoadCustomers\SCR_CleanseCustomerRow`
- Generated method: `DFT_LoadCustomers`
- Related source files: `D:\tmp\Sandeep_Packages\gen\generate\Package\Package.cs (method FF_SRC_Customers)`, `D:\tmp\Sandeep_Packages\gen\generate\Package\Package.cs (method OLEDST_StagingCustomers)`, `Fills\StagingCustomersTransform.Fills.cs`, `Mapping\StagingCustomersTransform.cs`

**Tier 1 finding:** 'SCR_CleanseCustomerRow' (Microsoft.ScriptComponentHost) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6). Semantic evidence attached (see `evidence`) for a human/AI reviewer to judge the generated logic against.

**Left-side evidence:**

```json
{"files":[{"file_name":"main.cs","source_code":"using System;\nusing System.Text.RegularExpressions;\nusing Microsoft.SqlServer.Dts.Pipeline;\n\nnamespace SC_CleanseCustomerRow\n{\n    /// <summary>\n    /// Row-level cleansing performed inside the data flow (requirement 4).\n    /// Trims and normalises the incoming CSV values, derives FullName, validates the\n    /// e-mail address and the customer id, and stamps a load timestamp.\n    /// </summary>\n    [SSISScriptComponentEntryPoint]\n    public class ScriptMain : UserComponent\n    {\n        // Deliberately simple - enough to reject \"not-an-email\" while accepting the rest.\n        private static readonly Regex EmailPattern =\n            new Regex(@\"^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$\", RegexOptions.Compiled);\n\n        public override void Input0_ProcessInputRow(Input0Buffer Row)\n        {\n            string first = (Row.FirstName ?? string.Empty).Trim();\n            string last = (Row.LastName ?? string.Empty).Trim();\n            string email = (Row.Email ?? string.Empty).Trim();\n            string customerId = (Row.CustomerID ?? string.Empty).Trim();\n\n            Row.FullName = (first + \" \" + last).Trim();\n\n            Row.CleanEmail = email.ToLowerInvariant();\n\n            // A row is valid only when the customer id parses as an integer AND the\n            // e-mail looks like an address. int.TryParse(\"\") is false - which is why the\n            // T-SQL harness has to use NULLIF(...,'') to match this behaviour.\n            int parsedId;\n            bool validId = int.TryParse(customerId, out parsedId);\n            bool validEmail = email.Length > 0 && EmailPattern.IsMatch(email);\n\n            Row.IsValidRow = validId && validEmail;\n\n            Row.LoadDateTime = DateTime.Now;\n        }\n    }\n}\n"}]}
```

**Right-side excerpt:**

```csharp

        var source = FF_SRC_Customers();
        var transform = new StagingCustomersTransform();
        var sink = OLEDST_StagingCustomers();

        var step = new DataFlowStep<StagingCustomersCsvRow, StagingCustomers>("DFT_LoadCustomers", source, transform, sink, Log<DataFlowStep<StagingCustomersCsvRow, StagingCustomers>>());
        _results.Add(await step.RunAsync(uow, _load, ct));
    
```

### SCR_NotifyAndLogProgress (`ScriptTask`)

- Component path: (control-flow level)
- Generated method: `SCR_NotifyAndLogProgress`
- Related source files: `Fills\SCR_NotifyAndLogProgressScriptTask.Fills.cs`, `ScriptTasks\SCR_NotifyAndLogProgressScriptTask.cs`

**Tier 1 finding:** Gate 2 can only confirm this Script Task's call site exists in the expected control-flow position (or is guarded/absorbed/inlined into a failure handler) — it cannot judge whether the generated method body faithfully reproduces the original script's logic.

**Left-side evidence:**

```json
{"files":[{"file_name":"ScriptMain.vb","source_code":"Imports System\nImports System.Data\nImports System.Data.SqlClient\nImports Microsoft.SqlServer.Dts.Runtime\n\n' Control Flow Script Task (VB.NET) - runs between the two data flows.\n' 1. Reads User::BatchStartTime (a String, round-trip format) and works out elapsed time.\n' 2. Counts the rows the first data flow staged, and how many were flagged invalid.\n' 3. Writes a \"Batch Progress\" row into dbo.EtlAuditLog and publishes User::RowsLoaded.\n'ScriptMain is the entry point class of the script. Do not change the name, attributes,\n'or parent of this class.\n<Microsoft.SqlServer.Dts.Tasks.ScriptTask.SSISScriptTaskEntryPointAttribute()> _\n<System.CLSCompliantAttribute(False)> _\nPartial Public Class ScriptMain\n    Inherits Microsoft.SqlServer.Dts.Tasks.ScriptTask.VSTARTScriptObjectModelBase\n\n    Public Sub Main()\n\n        Dim startedAt As DateTime = DateTime.Now\n        Dim rawStart As Object = Dts.Variables(\"User::BatchStartTime\").Value\n\n        If rawStart IsNot Nothing AndAlso Not String.IsNullOrEmpty(rawStart.ToString()) Then\n            ' Stored by the C# task as ToString(\"o\"); parse it back the same way.\n            DateTime.TryParse(rawStart.ToString(), startedAt)\n        End If\n\n        Dim elapsed As Double = DateTime.Now.Subtract(startedAt).TotalSeconds\n\n        Dim totalRows As Integer = 0\n        Dim invalidRows As Integer = 0\n\n        Dim conn As SqlConnection = CType(Dts.Connections(\"CM_ADO_SSISDemo\").AcquireConnection(Dts.Transaction), SqlConnection)\n        Try\n            Using cmd As SqlCommand = conn.CreateCommand()\n                cmd.CommandText = \"SELECT COUNT(*), SUM(CASE WHEN IsValidRow = 0 THEN 1 ELSE 0 END) FROM dbo.StagingCustomers\"\n                Using reader As SqlDataReader = cmd.ExecuteReader()\n                    If reader.Read() Then\n                        totalRows = reader.GetInt32(0)\n                        If Not reader.IsDBNull(1) Then invalidRows = reader.GetInt32(1)\n                    End If\n                End Using\n            End Using\n\n            Using cmd As SqlCommand = conn.CreateCommand()\n                cmd.CommandText = \"INSERT INTO dbo.EtlAuditLog (PackageName, StepName, EventTime, RowsAffected, Message) \" & _\n                                  \"VALUES (@PackageName, @StepName, @EventTime, @RowsAffected, @Message)\"\n                cmd.Parameters.AddWithValue(\"@PackageName\", Dts.Variables(\"System::PackageName\").Value.ToString())\n                cmd.Parameters.AddWithValue(\"@StepName\", \"Batch Progress\")\n                cmd.Parameters.AddWithValue(\"@EventTime\", DateTime.Now)\n                cmd.Parameters.AddWithValue(\"@RowsAffected\", totalRows)\n                cmd.Parameters.AddWithValue(\"@Message\", String.Format( _\n                    \"Staged {0} row(s), {1} flagged invalid, {2:F2}s after batch start.\", _\n                    totalRows, invalidRows, elapsed))\n                cmd.ExecuteNonQuery()\n            End Using\n        Finally\n            Dts.Connections(\"CM_ADO_SSISDemo\").ReleaseConnection(conn)\n        End Try\n\n        Dts.Variables(\"User::RowsLoaded\").Value = totalRows\n\n        Dts.Events.FireInformation(0, \"SCR_NotifyAndLogProgress\", _\n            String.Format(\"Staged {0} row(s), {1} invalid.\", totalRows, invalidRows), _\n            String.Empty, 0, False)\n\n        Dts.TaskResult = CInt(ScriptResults.Success)\n\n    End Sub\n\n    Enum ScriptResults\n        Success = Microsoft.SqlServer.Dts.Runtime.DTSExecResult.Success\n        Failure = Microsoft.SqlServer.Dts.Runtime.DTSExecResult.Failure\n    End Enum\n\nEnd Class"}]}
```

**Right-side excerpt:**

```csharp

        var step = new SCR_NotifyAndLogProgressScriptTask(packageVariables, services);
        _results.Add(await step.RunAsync(uow, _load, ct));
    
```

### SCR_ValidateAndLogStart (`ScriptTask`)

- Component path: (control-flow level)
- Generated method: `SCR_ValidateAndLogStart`
- Related source files: `Fills\SCR_ValidateAndLogStartScriptTask.Fills.cs`, `ScriptTasks\SCR_ValidateAndLogStartScriptTask.cs`

**Tier 1 finding:** Gate 2 can only confirm this Script Task's call site exists in the expected control-flow position (or is guarded/absorbed/inlined into a failure handler) — it cannot judge whether the generated method body faithfully reproduces the original script's logic.

**Left-side evidence:**

```json
{"files":[{"file_name":"ScriptMain.cs","source_code":"#region Namespaces\nusing System;\nusing System.Data;\nusing System.IO;\nusing System.Data.SqlClient;\nusing Microsoft.SqlServer.Dts.Runtime;\n#endregion\n\nnamespace ST_8d84581b262c4e39a00b720e2c6678e0\n{\n    [Microsoft.SqlServer.Dts.Tasks.ScriptTask.SSISScriptTaskEntryPointAttribute]\n    public partial class ScriptMain : Microsoft.SqlServer.Dts.Tasks.ScriptTask.VSTARTScriptObjectModelBase\n    {\n        // Control Flow Script Task (C#).\n        // 1. Confirms the source CSV referenced by CM_FF_Customers actually exists.\n        // 2. Stamps User::BatchStartTime.\n        // 3. Writes a \"Batch Start\" row into dbo.EtlAuditLog via the OLE DB connection manager.\n        public void Main()\n        {\n            string filePath = Dts.Connections[\"CM_FF_Customers\"].ConnectionString;\n\n            if (!File.Exists(filePath))\n            {\n                Dts.Events.FireError(0, \"SCR - Validate Source And Log Start\",\n                    \"Source file not found: \" + filePath, string.Empty, 0);\n                Dts.TaskResult = (int)ScriptResults.Failure;\n                return;\n            }\n\n            DateTime startTime = DateTime.Now;\n\n            // User::BatchStartTime is a STRING variable (VARTYPE 8), deliberately - DateTime\n            // literals in SSIS are locale-sensitive. Round-trip format keeps it parseable.\n            Dts.Variables[\"User::BatchStartTime\"].Value = startTime.ToString(\"o\");\n\n            // A Script Task must use an ADO.NET connection manager: AcquireConnection on a\n            // native OLE DB manager returns a COM object that will not cast to\n            // OleDbConnection, and its ConnectionString omits the write-only Password.\n            SqlConnection conn = (SqlConnection)Dts.Connections[\"CM_ADO_SSISDemo\"].AcquireConnection(Dts.Transaction);\n            try\n            {                using (SqlCommand cmd = conn.CreateCommand())\n                {\n                    cmd.CommandText = \"INSERT INTO dbo.EtlAuditLog (PackageName, StepName, EventTime, Message) VALUES (@PackageName, @StepName, @EventTime, @Message)\";\n                    cmd.Parameters.AddWithValue(\"@PackageName\", Dts.Variables[\"System::PackageName\"].Value.ToString());\n                    cmd.Parameters.AddWithValue(\"@StepName\", \"Batch Start\");\n                    cmd.Parameters.AddWithValue(\"@EventTime\", startTime);\n                    cmd.Parameters.AddWithValue(\"@Message\", \"Source file validated: \" + filePath);\n                    cmd.ExecuteNonQuery();\n                }\n            }\n            finally\n            {\n                Dts.Connections[\"CM_ADO_SSISDemo\"].ReleaseConnection(conn);\n            }\n\n            Dts.TaskResult = (int)ScriptResults.Success;\n        }\n\n        enum ScriptResults\n        {\n            Success = Microsoft.SqlServer.Dts.Runtime.DTSExecResult.Success,\n            Failure = Microsoft.SqlServer.Dts.Runtime.DTSExecResult.Failure\n        };\n    }\n}"}]}
```

**Right-side excerpt:**

```csharp

        var step = new SCR_ValidateAndLogStartScriptTask(packageVariables, services);
        _results.Add(await step.RunAsync(uow, _load, ct));
    
```
