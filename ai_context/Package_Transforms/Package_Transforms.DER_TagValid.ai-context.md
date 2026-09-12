# AI evidence packet — Package_Transforms / DER_TagValid

- Scope: `DataFlowComponent`
- Component path: `Package\DFT_DerivedAndSplit\DER_TagValid`
- .dtsx file: `D:\PoC\SSIS_Packages\SSIS_From_Sandeep\RBC_Demo_ETL\RBC_Demo_ETL\Package_Transforms.dtsx`
- Generated file: `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs`
- Generated method: `DFT_DerivedAndSplit`

## Tier 1 finding

'DER_TagValid' (Microsoft.DerivedColumn) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6). Semantic evidence attached (see `evidence`) for a human/AI reviewer to judge the generated logic against.

## Left-side (SSIS) semantic evidence

```json
{
  "columns": [
    {
      "output_name": "RowSource",
      "friendly_expression": "\"Valid\""
    }
  ]
}
```

## Right-side (generated C#) method excerpt

```csharp

        var source = FF_SRC_Customers();
        var step = new ConditionalSplitStep<DFT_DerivedAndSplitCsvRow>(
            "DFT_DerivedAndSplit",
            source,
            new CSPLIT_ValidityRouter(),
            [
                new ConditionalSplitBranch<DFT_DerivedAndSplitCsvRow, CustomerEnriched>(
                    "Valid",
                    new CSPLIT_Validity_Valid(),
                    new SqlBulkSink<CustomerEnriched>(Opt<BulkCopyOptions>(), Log<SqlBulkSink<CustomerEnriched>>())),
                new ConditionalSplitBranch<DFT_DerivedAndSplitCsvRow, CustomerEnriched>(
                    "Invalid",
                    new CSPLIT_Validity_Invalid(),
                    new SqlBulkSink<CustomerEnriched>(Opt<BulkCopyOptions>(), Log<SqlBulkSink<CustomerEnriched>>()))
            ],
            Log<ConditionalSplitStep<DFT_DerivedAndSplitCsvRow>>());
        _results.Add(await step.RunAsync(uow, _load, ct));
    
```

## Related generated source

The excerpt above only wires together factory calls; each referenced identifier's own definition (where found in this package's own generated files) follows, since that is typically where the actual per-row/per-script logic lives.

### `FF_SRC_Customers` — `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs (method FF_SRC_Customers)`

```csharp

        return new CsvRowSource<DFT_DerivedAndSplitCsvRow>("FF_SRC_Customers", new CsvSourceOptions { FilePath = File("DFT_DerivedAndSplit") }, new DFT_DerivedAndSplitCsvRowMap());
    
```

### `CSPLIT_ValidityRouter` — `Mapping\CSPLIT_ValidityRouter.cs`

```csharp
using Package_Transforms.Csv;
using Etl.Core.Abstractions;
using Package_Transforms.Ssis;

namespace Package_Transforms.Mapping;

public sealed class CSPLIT_ValidityRouter : IRowRouter<DFT_DerivedAndSplitCsvRow>
{
    public int SelectBranch(DFT_DerivedAndSplitCsvRow row, in RowContext ctx)
    {
        if ((!((SsisFn.ToNullableI4(row.CustomerID) is null)) && (SsisFn.FindString(SsisFn.Trim(row.Email), "@", 1) > 0))) return 0;
        return 1; // Invalid
    }
}

```

### `CSPLIT_Validity_Valid` — `Mapping\CSPLIT_Validity_Valid.cs`

```csharp
using Etl.Core.Abstractions;
using Package_Transforms.Csv;
using Package_Transforms.Model;
using Package_Transforms.Ssis;

namespace Package_Transforms.Mapping;

public sealed class CSPLIT_Validity_Valid : IRowTransform<DFT_DerivedAndSplitCsvRow, CustomerEnriched>
{
    public CustomerEnriched Map(DFT_DerivedAndSplitCsvRow row, in RowContext ctx) => new()
    {
        CustomerID = SsisFn.ToNullableI4(row.CustomerID),
        FullName = DER_Enrich.FullName(row, ctx),
        EmailDomain = DER_Enrich.EmailDomain(row, ctx),
        SignupDate = SsisFn.ToNullableDate(row.SignupDate),
        TenureDays = DER_Enrich.TenureDays(row, ctx),
        RowSource = DER_TagValid.RowSource(row, ctx),
    };
}

```

### `CSPLIT_Validity_Invalid` — `Mapping\CSPLIT_Validity_Invalid.cs`

```csharp
using Etl.Core.Abstractions;
using Package_Transforms.Csv;
using Package_Transforms.Model;
using Package_Transforms.Ssis;

namespace Package_Transforms.Mapping;

public sealed class CSPLIT_Validity_Invalid : IRowTransform<DFT_DerivedAndSplitCsvRow, CustomerEnriched>
{
    public CustomerEnriched Map(DFT_DerivedAndSplitCsvRow row, in RowContext ctx) => new()
    {
        CustomerID = SsisFn.ToNullableI4(row.CustomerID),
        FullName = DER_Enrich.FullName(row, ctx),
        EmailDomain = DER_Enrich.EmailDomain(row, ctx),
        SignupDate = SsisFn.ToNullableDate(row.SignupDate),
        TenureDays = DER_Enrich.TenureDays(row, ctx),
        RowSource = DER_TagInvalid.RowSource(row, ctx),
    };
}

```

### `DER_TagValid` — `Mapping\DER_TagValid.cs`

```csharp
using Etl.Core.Abstractions;
using Package_Transforms.Csv;

namespace Package_Transforms.Mapping;

internal static class DER_TagValid
{
    // "Valid"
    public static string RowSource(DFT_DerivedAndSplitCsvRow row, in RowContext ctx) => "Valid";
}

```
