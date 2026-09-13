# AI evidence packet (combined) — Package_Transforms

- .dtsx file: `D:\PoC\SSIS_Packages\SSIS_From_Sandeep\RBC_Demo_ETL\RBC_Demo_ETL\Package_Transforms.dtsx`
- Generated file: `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs`
- 8 component(s), 17 distinct shared source file(s) (each shown once below)

Adjudicate every component below in ONE pass — see docs/ai-validation-prompt.md for the required output (one Component/Verdict/Rationale block per component). Each component references shared source files by path instead of repeating their content.

## Sources

### `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs (method FF_SRC_Customers)`

```csharp

        return new CsvRowSource<DFT_DerivedAndSplitCsvRow>("FF_SRC_Customers", new CsvSourceOptions { FilePath = File("DFT_DerivedAndSplit") }, new DFT_DerivedAndSplitCsvRowMap());
    
```

### `Mapping\CSPLIT_ValidityRouter.cs`

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

### `Mapping\CSPLIT_Validity_Valid.cs`

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

### `Mapping\CSPLIT_Validity_Invalid.cs`

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

### `Mapping\DER_Enrich.cs`

```csharp
using Etl.Core.Abstractions;
using Etl.Core.Ssis;
using Package_Transforms.Csv;
using Package_Transforms.Model;
using Package_Transforms.Ssis;

namespace Package_Transforms.Mapping;

internal static class DER_Enrich
{
    // (DT_WSTR,200)(TRIM(FirstName) + " " + TRIM(LastName))
    public static string FullName(DFT_DerivedAndSplitCsvRow row, in RowContext ctx) => WidthGuard.Wstr($"{SsisFn.Trim(row.FirstName)} {SsisFn.Trim(row.LastName)}", 200, nameof(CustomerEnriched.FullName), ctx.RowNumber);

    // (DT_WSTR,200)(FINDSTRING(TRIM(Email),"@",1) > 0 ? SUBSTRING(TRIM(Email),FINDSTRING(TRIM(Email),"@",1) + 1,100) : "(none)")
    public static string EmailDomain(DFT_DerivedAndSplitCsvRow row, in RowContext ctx) => WidthGuard.Wstr(((SsisFn.FindString(SsisFn.Trim(row.Email), "@", 1) > 0) ? SsisFn.Substring(SsisFn.Trim(row.Email), (SsisFn.FindString(SsisFn.Trim(row.Email), "@", 1) + 1), 100) : "(none)"), 200, nameof(CustomerEnriched.EmailDomain), ctx.RowNumber);

    // ISNULL(SignupDate_dt) ? -1 : DATEDIFF("dd",SignupDate_dt,GETDATE())
    public static int TenureDays(DFT_DerivedAndSplitCsvRow row, in RowContext ctx) => ((SsisFn.ToNullableDate(row.SignupDate) is null) ? -(1) : SsisFn.DateDiffDays(SsisFn.ToNullableDate(row.SignupDate)!.Value, ctx.LoadedAtUtc));
}

```

### `Mapping\DER_TagInvalid.cs`

```csharp
using Etl.Core.Abstractions;
using Package_Transforms.Csv;

namespace Package_Transforms.Mapping;

internal static class DER_TagInvalid
{
    // "Invalid"
    public static string RowSource(DFT_DerivedAndSplitCsvRow row, in RowContext ctx) => "Invalid";
}

```

### `Mapping\DER_TagValid.cs`

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

### `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs (method AGG_ByRegion)`

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

### `Mapping\RegionSummaryTransform.cs`

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

### `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs (method OLEDST_RegionSummary)`

```csharp

        return new SqlBulkSink<RegionSummary>(Opt<BulkCopyOptions>(), Log<SqlBulkSink<RegionSummary>>());
    
```

### `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs (method FF_SRC_Customers4)`

```csharp

        return new CsvRowSource<DFT_MergeSortedBranchesCsvRow>("FF_SRC_Customers4", new CsvSourceOptions { FilePath = File("DFT_MergeSortedBranches") }, new DFT_MergeSortedBranchesCsvRowMap());
    
```

### `Mapping\CSPLIT_ByCountryRouter.cs`

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

### `Mapping\CSPLIT_ByCountry_Canada.cs`

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

### `Mapping\CSPLIT_ByCountry_RestOfWorld.cs`

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

### `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs (method MRG_CustomerContacts)`

```csharp

        IRowSource<MRG_CustomerContactsLeftCsvRow> BuildLeft()
        {
            return new CsvRowSource<MRG_CustomerContactsLeftCsvRow>("FF_SRC_Customers3", new CsvSourceOptions { FilePath = File("MRG_CustomerContactsLeft") }, new MRG_CustomerContactsLeftCsvRowMap());
        }

        IRowSource<MRG_CustomerContactsRightCsvRow> BuildRight()
        {
            return new CsvRowSource<MRG_CustomerContactsRightCsvRow>("FF_SRC_Contacts", new CsvSourceOptions { FilePath = File("MRG_CustomerContactsRight") }, new MRG_CustomerContactsRightCsvRowMap());
        }

        return new MergeJoinRowSource<MRG_CustomerContactsLeftCsvRow, MRG_CustomerContactsRightCsvRow, int?, MRG_CustomerContactsRow>(
            "MRG_CustomerContacts", BuildLeft(), BuildRight(),
            MRG_CustomerContactsMapper.LeftKey, MRG_CustomerContactsMapper.RightKey,
            Comparer<int?>.Default,
            MergeJoinType.Inner,
            MRG_CustomerContactsMapper.Map);
    
```

### `Mapping\CustomerWithContactTransform.cs`

```csharp
using Etl.Core.Abstractions;
using Package_Transforms.Sql;
using Package_Transforms.Model;

namespace Package_Transforms.Mapping;

public sealed class CustomerWithContactTransform : IRowTransform<MRG_CustomerContactsRow, CustomerWithContact>
{
    public CustomerWithContact Map(MRG_CustomerContactsRow row, in RowContext ctx) => new()
    {
        CustomerID = row.CustomerID_i4,
        Email = row.Email,
        ContactPhone = row.ContactPhone,
        PreferredChannel = row.PreferredChannel,
    };
}

```

### `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs (method OLEDST_CustomerWithContact)`

```csharp

        return new SqlBulkSink<CustomerWithContact>(Opt<BulkCopyOptions>(), Log<SqlBulkSink<CustomerWithContact>>());
    
```

## Components

### CSPLIT_Validity (`DataFlowComponent`)

- Component path: `Package\DFT_DerivedAndSplit\CSPLIT_Validity`
- Generated method: `DFT_DerivedAndSplit`
- Related source files: `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs (method FF_SRC_Customers)`, `Mapping\CSPLIT_ValidityRouter.cs`, `Mapping\CSPLIT_Validity_Invalid.cs`, `Mapping\CSPLIT_Validity_Valid.cs`

**Tier 1 finding:** 'CSPLIT_Validity' (Microsoft.ConditionalSplit) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6). Semantic evidence attached (see `evidence`) for a human/AI reviewer to judge the generated logic against.

**Left-side evidence:**

```json
{"outputs":[{"output_name":"Valid","is_default":false,"friendly_expression":"!ISNULL(CustomerID_i4) && FINDSTRING(TRIM(Email),\"@\",1) > 0","evaluation_order":0},{"output_name":"Invalid","is_default":true,"friendly_expression":null,"evaluation_order":null}]}
```

**Right-side excerpt:**

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

### DER_Enrich (`DataFlowComponent`)

- Component path: `Package\DFT_DerivedAndSplit\DER_Enrich`
- Generated method: `DFT_DerivedAndSplit`
- Related source files: `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs (method FF_SRC_Customers)`, `Mapping\CSPLIT_ValidityRouter.cs`, `Mapping\CSPLIT_Validity_Invalid.cs`, `Mapping\CSPLIT_Validity_Valid.cs`, `Mapping\DER_Enrich.cs`

**Tier 1 finding:** 'DER_Enrich' (Microsoft.DerivedColumn) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6). Semantic evidence attached (see `evidence`) for a human/AI reviewer to judge the generated logic against.

**Left-side evidence:**

```json
{"columns":[{"output_name":"FullName","friendly_expression":"(DT_WSTR,200)(TRIM(FirstName) + \" \" + TRIM(LastName))"},{"output_name":"EmailDomain","friendly_expression":"(DT_WSTR,200)(FINDSTRING(TRIM(Email),\"@\",1) > 0 ? SUBSTRING(TRIM(Email),FINDSTRING(TRIM(Email),\"@\",1) + 1,100) : \"(none)\")"},{"output_name":"TenureDays","friendly_expression":"ISNULL(SignupDate_dt) ? -1 : DATEDIFF(\"dd\",SignupDate_dt,GETDATE())"}]}
```

**Right-side excerpt:**

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

### DER_TagInvalid (`DataFlowComponent`)

- Component path: `Package\DFT_DerivedAndSplit\DER_TagInvalid`
- Generated method: `DFT_DerivedAndSplit`
- Related source files: `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs (method FF_SRC_Customers)`, `Mapping\CSPLIT_ValidityRouter.cs`, `Mapping\CSPLIT_Validity_Invalid.cs`, `Mapping\CSPLIT_Validity_Valid.cs`, `Mapping\DER_TagInvalid.cs`

**Tier 1 finding:** 'DER_TagInvalid' (Microsoft.DerivedColumn) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6). Semantic evidence attached (see `evidence`) for a human/AI reviewer to judge the generated logic against.

**Left-side evidence:**

```json
{"columns":[{"output_name":"RowSource","friendly_expression":"\"Invalid\""}]}
```

**Right-side excerpt:**

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

### DER_TagValid (`DataFlowComponent`)

- Component path: `Package\DFT_DerivedAndSplit\DER_TagValid`
- Generated method: `DFT_DerivedAndSplit`
- Related source files: `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs (method FF_SRC_Customers)`, `Mapping\CSPLIT_ValidityRouter.cs`, `Mapping\CSPLIT_Validity_Invalid.cs`, `Mapping\CSPLIT_Validity_Valid.cs`, `Mapping\DER_TagValid.cs`

**Tier 1 finding:** 'DER_TagValid' (Microsoft.DerivedColumn) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6). Semantic evidence attached (see `evidence`) for a human/AI reviewer to judge the generated logic against.

**Left-side evidence:**

```json
{"columns":[{"output_name":"RowSource","friendly_expression":"\"Valid\""}]}
```

**Right-side excerpt:**

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

### AGG_ByRegion (`DataFlowComponent`)

- Component path: `Package\DFT_LookupAndAggregate\AGG_ByRegion`
- Generated method: `DFT_LookupAndAggregate`
- Related source files: `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs (method AGG_ByRegion)`, `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs (method OLEDST_RegionSummary)`, `Mapping\RegionSummaryTransform.cs`

**Tier 1 finding:** 'AGG_ByRegion' (Microsoft.Aggregate) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6). Semantic evidence attached (see `evidence`) for a human/AI reviewer to judge the generated logic against.

**Left-side evidence:**

```json
{"columns":[{"name":"Region","aggregation_type":{"property_name":"AggregationType","raw_value":"0","description":"Specifies the type of aggregation."},"source_component":"LKP_Country","source_column":"Region"},{"name":"CustomerCount","aggregation_type":{"property_name":"AggregationType","raw_value":"1","description":"Specifies the type of aggregation."},"source_component":"FF_SRC_Customers2","source_column":"CustomerID"}]}
```

**Right-side excerpt:**

```csharp

        var source = AGG_ByRegion();
        var transform = new RegionSummaryTransform();
        var sink = OLEDST_RegionSummary();

        var step = new DataFlowStep<RegionSummaryAggregateRow, RegionSummary>("DFT_LookupAndAggregate", source, transform, sink, Log<DataFlowStep<RegionSummaryAggregateRow, RegionSummary>>());
        _results.Add(await step.RunAsync(uow, _load, ct));
    
```

### LKP_Country (`DataFlowComponent`)

- Component path: `Package\DFT_LookupAndAggregate\LKP_Country`
- Generated method: `DFT_LookupAndAggregate`
- Related source files: `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs (method AGG_ByRegion)`, `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs (method OLEDST_RegionSummary)`, `Mapping\RegionSummaryTransform.cs`

**Tier 1 finding:** 'LKP_Country' (Microsoft.Lookup) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6). Semantic evidence attached (see `evidence`) for a human/AI reviewer to judge the generated logic against.

**Left-side evidence:**

```json
{"sql_command":"SELECT CountryName, CountryCode, Region FROM dbo.CountryLookup","connection_type":{"property_name":"ConnectionType","raw_value":"0","description":"Specifies the type of connection used to access the reference dataset."},"cache_type":{"property_name":"CacheType","raw_value":"0","description":"Specifies the cache type of the lookup table."},"no_match_behavior":{"property_name":"NoMatchBehavior","raw_value":"1","description":"Specifies how the Lookup transformation handles rows without matching entries in the reference data set."},"join_columns":[{"input_column":"Country","join_to_reference_column":"CountryName"}],"output_columns":[{"name":"CountryCode","copy_from_reference_column":"CountryCode"},{"name":"Region","copy_from_reference_column":"Region"}],"match_output_destination":"MCAST_Matched","no_match_output_destination":"RC_UnmatchedRows"}
```

**Right-side excerpt:**

```csharp

        var source = AGG_ByRegion();
        var transform = new RegionSummaryTransform();
        var sink = OLEDST_RegionSummary();

        var step = new DataFlowStep<RegionSummaryAggregateRow, RegionSummary>("DFT_LookupAndAggregate", source, transform, sink, Log<DataFlowStep<RegionSummaryAggregateRow, RegionSummary>>());
        _results.Add(await step.RunAsync(uow, _load, ct));
    
```

### CSPLIT_ByCountry (`DataFlowComponent`)

- Component path: `Package\DFT_MergeSortedBranches\CSPLIT_ByCountry`
- Generated method: `DFT_MergeSortedBranches`
- Related source files: `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs (method FF_SRC_Customers4)`, `Mapping\CSPLIT_ByCountryRouter.cs`, `Mapping\CSPLIT_ByCountry_Canada.cs`, `Mapping\CSPLIT_ByCountry_RestOfWorld.cs`

**Tier 1 finding:** 'CSPLIT_ByCountry' (Microsoft.ConditionalSplit) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6). Semantic evidence attached (see `evidence`) for a human/AI reviewer to judge the generated logic against.

**Left-side evidence:**

```json
{"outputs":[{"output_name":"Canada","is_default":false,"friendly_expression":"TRIM(Country) == \"Canada\"","evaluation_order":0},{"output_name":"RestOfWorld","is_default":true,"friendly_expression":null,"evaluation_order":null}]}
```

**Right-side excerpt:**

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

### MRG_CustomerContacts (`DataFlowComponent`)

- Component path: `Package\DFT_SortAndMergeJoin\MRG_CustomerContacts`
- Generated method: `DFT_SortAndMergeJoin`
- Related source files: `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs (method MRG_CustomerContacts)`, `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms\Package_Transforms.cs (method OLEDST_CustomerWithContact)`, `Mapping\CustomerWithContactTransform.cs`

**Tier 1 finding:** 'MRG_CustomerContacts' (Microsoft.MergeJoin) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6). Semantic evidence attached (see `evidence`) for a human/AI reviewer to judge the generated logic against.

**Left-side evidence:**

```json
{"join_type":{"property_name":"JoinType","raw_value":"2","description":"Specifies the type of join to perform."},"num_key_columns":1,"treat_nulls_as_equal":true,"input_columns":[{"name":"CustomerID_i4","side":"left","sort_key_position":1},{"name":"Email","side":"left","sort_key_position":null},{"name":"CustomerID_i4","side":"right","sort_key_position":1},{"name":"ContactPhone","side":"right","sort_key_position":null},{"name":"PreferredChannel","side":"right","sort_key_position":null}],"output_columns":[{"name":"Email","source_column":"Email"},{"name":"CustomerID_i4","source_column":"CustomerID_i4"},{"name":"ContactPhone","source_column":"ContactPhone"},{"name":"PreferredChannel","source_column":"PreferredChannel"}]}
```

**Right-side excerpt:**

```csharp

        var source = MRG_CustomerContacts();
        var transform = new CustomerWithContactTransform();
        var sink = OLEDST_CustomerWithContact();

        var step = new DataFlowStep<MRG_CustomerContactsRow, CustomerWithContact>("DFT_SortAndMergeJoin", source, transform, sink, Log<DataFlowStep<MRG_CustomerContactsRow, CustomerWithContact>>());
        _results.Add(await step.RunAsync(uow, _load, ct));
    
```
