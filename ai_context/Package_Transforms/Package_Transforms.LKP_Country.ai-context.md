# AI evidence packet — Package_Transforms / LKP_Country

- Scope: `DataFlowComponent`
- Component path: `Package\DFT_LookupAndAggregate\LKP_Country`
- .dtsx file: `D:\PoC\SSIS_Packages\SSIS_From_Sandeep\RBC_Demo_ETL\RBC_Demo_ETL\Package_Transforms.dtsx`
- Generated file: `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs`
- Generated method: `DFT_LookupAndAggregate`

## Tier 1 finding

'LKP_Country' (Microsoft.Lookup) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6). Semantic evidence attached (see `evidence`) for a human/AI reviewer to judge the generated logic against.

## Left-side (SSIS) semantic evidence

```json
{
  "sql_command": "SELECT CountryName, CountryCode, Region FROM dbo.CountryLookup",
  "connection_type": {
    "property_name": "ConnectionType",
    "raw_value": "0",
    "description": "Specifies the type of connection used to access the reference dataset."
  },
  "cache_type": {
    "property_name": "CacheType",
    "raw_value": "0",
    "description": "Specifies the cache type of the lookup table."
  },
  "no_match_behavior": {
    "property_name": "NoMatchBehavior",
    "raw_value": "1",
    "description": "Specifies how the Lookup transformation handles rows without matching entries in the reference data set."
  },
  "join_columns": [
    {
      "input_column": "Country",
      "join_to_reference_column": "CountryName"
    }
  ],
  "output_columns": [
    {
      "name": "CountryCode",
      "copy_from_reference_column": "CountryCode"
    },
    {
      "name": "Region",
      "copy_from_reference_column": "Region"
    }
  ],
  "match_output_destination": "MCAST_Matched",
  "no_match_output_destination": "RC_UnmatchedRows"
}
```

## Right-side (generated C#) method excerpt

```csharp

        var source = AGG_ByRegion();
        var transform = new RegionSummaryTransform();
        var sink = OLEDST_RegionSummary();

        var step = new DataFlowStep<RegionSummaryAggregateRow, RegionSummary>("DFT_LookupAndAggregate", source, transform, sink, Log<DataFlowStep<RegionSummaryAggregateRow, RegionSummary>>());
        _results.Add(await step.RunAsync(uow, _load, ct));
    
```

## Related generated source

The excerpt above only wires together factory calls; each referenced identifier's own definition (where found in this package's own generated files) follows, since that is typically where the actual per-row/per-script logic lives.

### `AGG_ByRegion` — `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs (method AGG_ByRegion)`

```csharp

        IRowSource<AGG_ByRegionSourceCsvRow> BuildAggregateSource()
        {
            IRowSource<AGG_ByRegionSourceCsvRow> BuildRawSource()
            {
                return new CsvRowSource<AGG_ByRegionSourceCsvRow>("FF_SRC_Customers2", new CsvSourceOptions { FilePath = File("AGG_ByRegionSource") }, new AGG_ByRegionSourceCsvRowMap());
            }

            return new FilteringRowSource<AGG_ByRegionSourceCsvRow>("AGG_ByRegion", BuildRawSource(), row => lKP_CountryCache.ContainsKey(row.Country));
        }

        return new AggregateRowSource<AGG_ByRegionSourceCsvRow, string, RegionSummaryAggregateRow>(
            "AGG_ByRegion",
            BuildAggregateSource(),
            row => lKP_CountryCache[row.Country].Region,
            (key, rows) => new RegionSummaryAggregateRow
            {
                Region = key,
                CustomerCount = rows.Count(r => r.CustomerID != null)
            });
    
```

### `RegionSummaryTransform` — `Mapping\RegionSummaryTransform.cs`

```csharp
using Etl.Core.Abstractions;
using Package_Transforms.Sql;
using Package_Transforms.Model;
using Package_Transforms.Ssis;

namespace Package_Transforms.Mapping;

public sealed class RegionSummaryTransform : IRowTransform<RegionSummaryAggregateRow, RegionSummary>
{
    public RegionSummary Map(RegionSummaryAggregateRow row, in RowContext ctx) => new()
    {
        Region = row.Region,
        CustomerCount = SsisFn.NarrowI8ToI4(row.CustomerCount),
    };
}

```

### `OLEDST_RegionSummary` — `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs (method OLEDST_RegionSummary)`

```csharp

        return new SqlBulkSink<RegionSummary>(Opt<BulkCopyOptions>(), Log<SqlBulkSink<RegionSummary>>());
    
```
