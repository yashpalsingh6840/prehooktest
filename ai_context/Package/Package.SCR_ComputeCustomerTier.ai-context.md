# AI evidence packet — Package / SCR_ComputeCustomerTier

- Scope: `DataFlowComponent`
- Component path: `Package\DFT_BuildSalesSummary\SCR_ComputeCustomerTier`
- .dtsx file: `D:\PoC\SSIS_Packages\SSIS_From_Sandeep\RBC_Demo_ETL\RBC_Demo_ETL\Package.dtsx`
- Generated file: `D:\tmp\Sandeep_Packages\gen\generate\Package\Package.cs`
- Generated method: `DFT_BuildSalesSummary`

## Tier 1 finding

'SCR_ComputeCustomerTier' (Microsoft.ScriptComponentHost) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6). Semantic evidence attached (see `evidence`) for a human/AI reviewer to judge the generated logic against.

## Left-side (SSIS) semantic evidence

```json
{
  "files": [
    {
      "file_name": "main.cs",
      "source_code": "using System;\nusing Microsoft.SqlServer.Dts.Pipeline;\n\nnamespace SC_ComputeCustomerTier\n{\n    /// <summary>\n    /// Second row-level Script Component (requirement 4), this one in\n    /// DFT_BuildSalesSummary. Assigns a loyalty tier from the aggregated revenue and\n    /// stamps the processing time.\n    ///\n    /// Thresholds match sql\\04_verify_expected_output.sql exactly, so the package output\n    /// can be diffed against the pure T-SQL harness.\n    /// </summary>\n    [SSISScriptComponentEntryPoint]\n    public class ScriptMain : UserComponent\n    {\n        public override void Input0_ProcessInputRow(Input0Buffer Row)\n        {\n            double revenue = Row.TotalRevenue;\n\n            if (revenue >= 10000)     Row.CustomerTier = \"Platinum\";\n            else if (revenue >= 5000) Row.CustomerTier = \"Gold\";\n            else if (revenue >= 1000) Row.CustomerTier = \"Silver\";\n            else                      Row.CustomerTier = \"Bronze\";\n\n            Row.ProcessedDateTime = DateTime.Now;\n        }\n    }\n}\n"
    }
  ]
}
```

## Right-side (generated C#) method excerpt

```csharp

        var source = OLESRC_OrdersAndDetails(uow);
        var transform = new SalesSummaryTransform();
        var sink = OLEDST_SalesSummary();

        var step = new DataFlowStep<SalesSummarySqlRow, SalesSummary>("DFT_BuildSalesSummary", source, transform, sink, Log<DataFlowStep<SalesSummarySqlRow, SalesSummary>>());
        _results.Add(await step.RunAsync(uow, _load, ct));
    
```

## Related generated source

The excerpt above only wires together factory calls; each referenced identifier's own definition (where found in this package's own generated files) follows, since that is typically where the actual per-row/per-script logic lives.

### `OLESRC_OrdersAndDetails` — `D:\tmp\Sandeep_Packages\gen\generate\Package\Package.cs (method OLESRC_OrdersAndDetails)`

```csharp

        return new SqlRowSource<SalesSummarySqlRow>("OLESRC_OrdersAndDetails", new SqlSourceOptions { ConnectionString = SqlConnectionStringFactory.Build(Db()), CommandText = "SELECT\n    o.CustomerID,\n    c.FullName,\n    c.CleanEmail AS Email,\n    COUNT(DISTINCT o.OrderID) AS TotalOrders,\n    SUM(od.Quantity * od.UnitPrice) AS TotalRevenue,\n    MAX(o.OrderDate) AS LastOrderDate\nFROM dbo.Orders o\nINNER JOIN dbo.OrderDetails od ON od.OrderID = o.OrderID\nINNER JOIN dbo.StagingCustomers c ON o.CustomerID = TRY_CAST(c.CustomerID AS INT)\nWHERE ISNULL(c.IsValidRow, 1) = 1\nGROUP BY o.CustomerID, c.FullName, c.CleanEmail" }, SalesSummarySqlRowReader.Read, uow);
    
```

### `SalesSummaryTransform` — `Fills\SalesSummaryTransform.Fills.cs`

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

### `SalesSummaryTransform` — `Mapping\SalesSummaryTransform.cs`

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

### `OLEDST_SalesSummary` — `D:\tmp\Sandeep_Packages\gen\generate\Package\Package.cs (method OLEDST_SalesSummary)`

```csharp

        return new SqlBulkSink<SalesSummary>(Opt<BulkCopyOptions>(), Log<SqlBulkSink<SalesSummary>>());
    
```
