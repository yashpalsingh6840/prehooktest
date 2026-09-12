# AI evidence packet — Package_Transforms / CSPLIT_ByCountry

- Scope: `DataFlowComponent`
- Component path: `Package\DFT_MergeSortedBranches\CSPLIT_ByCountry`
- .dtsx file: `D:\PoC\SSIS_Packages\SSIS_From_Sandeep\RBC_Demo_ETL\RBC_Demo_ETL\Package_Transforms.dtsx`
- Generated file: `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs`
- Generated method: `DFT_MergeSortedBranches`

## Tier 1 finding

'CSPLIT_ByCountry' (Microsoft.ConditionalSplit) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6). Semantic evidence attached (see `evidence`) for a human/AI reviewer to judge the generated logic against.

## Left-side (SSIS) semantic evidence

```json
{
  "outputs": [
    {
      "output_name": "Canada",
      "is_default": false,
      "friendly_expression": "TRIM(Country) == \"Canada\"",
      "evaluation_order": 0
    },
    {
      "output_name": "RestOfWorld",
      "is_default": true,
      "friendly_expression": null,
      "evaluation_order": null
    }
  ]
}
```

## Right-side (generated C#) method excerpt

```csharp

        var source = FF_SRC_Customers4();
        var step = new ConditionalSplitStep<DFT_MergeSortedBranchesCsvRow>(
            "DFT_MergeSortedBranches",
            source,
            new CSPLIT_ByCountryRouter(),
            [
                new ConditionalSplitBranch<DFT_MergeSortedBranchesCsvRow, CustomerMerged>(
                    "Canada",
                    new CSPLIT_ByCountry_Canada(),
                    new SqlBulkSink<CustomerMerged>(Opt<BulkCopyOptions>(), Log<SqlBulkSink<CustomerMerged>>())),
                new ConditionalSplitBranch<DFT_MergeSortedBranchesCsvRow, CustomerMerged>(
                    "RestOfWorld",
                    new CSPLIT_ByCountry_RestOfWorld(),
                    new SqlBulkSink<CustomerMerged>(Opt<BulkCopyOptions>(), Log<SqlBulkSink<CustomerMerged>>()))
            ],
            Log<ConditionalSplitStep<DFT_MergeSortedBranchesCsvRow>>());
        _results.Add(await step.RunAsync(uow, _load, ct));
    
```

## Related generated source

The excerpt above only wires together factory calls; each referenced identifier's own definition (where found in this package's own generated files) follows, since that is typically where the actual per-row/per-script logic lives.

### `FF_SRC_Customers4` — `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs (method FF_SRC_Customers4)`

```csharp

        return new CsvRowSource<DFT_MergeSortedBranchesCsvRow>("FF_SRC_Customers4", new CsvSourceOptions { FilePath = File("DFT_MergeSortedBranches") }, new DFT_MergeSortedBranchesCsvRowMap());
    
```

### `CSPLIT_ByCountryRouter` — `Mapping\CSPLIT_ByCountryRouter.cs`

```csharp
using Package_Transforms.Csv;
using Etl.Core.Abstractions;
using Package_Transforms.Ssis;

namespace Package_Transforms.Mapping;

public sealed class CSPLIT_ByCountryRouter : IRowRouter<DFT_MergeSortedBranchesCsvRow>
{
    public int SelectBranch(DFT_MergeSortedBranchesCsvRow row, in RowContext ctx)
    {
        if (string.Equals(SsisFn.Trim(row.Country), "Canada", System.StringComparison.Ordinal)) return 0;
        return 1; // RestOfWorld
    }
}

```

### `CSPLIT_ByCountry_Canada` — `Mapping\CSPLIT_ByCountry_Canada.cs`

```csharp
using Etl.Core.Abstractions;
using Package_Transforms.Csv;
using Package_Transforms.Model;
using Package_Transforms.Ssis;

namespace Package_Transforms.Mapping;

public sealed class CSPLIT_ByCountry_Canada : IRowTransform<DFT_MergeSortedBranchesCsvRow, CustomerMerged>
{
    public CustomerMerged Map(DFT_MergeSortedBranchesCsvRow row, in RowContext ctx) => new()
    {
        CustomerID = SsisFn.ToNullableI4(row.CustomerID),
        Email = row.Email,
    };
}

```

### `CSPLIT_ByCountry_RestOfWorld` — `Mapping\CSPLIT_ByCountry_RestOfWorld.cs`

```csharp
using Etl.Core.Abstractions;
using Package_Transforms.Csv;
using Package_Transforms.Model;
using Package_Transforms.Ssis;

namespace Package_Transforms.Mapping;

public sealed class CSPLIT_ByCountry_RestOfWorld : IRowTransform<DFT_MergeSortedBranchesCsvRow, CustomerMerged>
{
    public CustomerMerged Map(DFT_MergeSortedBranchesCsvRow row, in RowContext ctx) => new()
    {
        CustomerID = SsisFn.ToNullableI4(row.CustomerID),
        Email = row.Email,
    };
}

```
