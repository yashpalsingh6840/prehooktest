# AI evidence packet (combined) — Package_Legacy

- .dtsx file: `D:\PoC\SSIS_Packages\SSIS_From_Sandeep\RBC_Demo_ETL\RBC_Demo_ETL\Package_Legacy.dtsx`
- Generated file: `D:\tmp\Sandeep_Packages\gen\generate\Package_Legacy\Package_Legacy.cs`
- 0 component(s), 0 distinct shared source file(s) (each shown once below)

Adjudicate every component below in ONE pass — see docs/ai-validation-prompt.md for the required output (one Component/Verdict/Rationale block per component). Each component references shared source files by path instead of repeating their content.

## Sources

## Components

## Correlation gaps

Tier 1 knows what the SSIS side declares (`declared_columns`) for each of these but could not correlate ANY generated construct to compare it against. Your job here has two parts: (1) decide whether `known_correlate` (if present) or one of `unconfirmed_candidates` (if not) is genuinely the generated code for this component — cite why; (2) only if you're confident which one it is, judge whether it correctly implements `declared_columns`. If nothing plausible is present, that is `AiInconclusive`, stated as such — do not force a guess.

### FFDST_AuditTrail_APPEND

- Component path: `Package\DFT_FixedWidthImport\FFDST_AuditTrail_APPEND`

**Tier 1 finding:** 'FFDST_AuditTrail_APPEND' (Microsoft.FlatFileDestination) is not a database destination Gate 1 can identify by (schema, table), and no generated factory method is named after this component either — no schema-correlation mechanism matched (2 declared column(s) not compared).

**Declared columns (left/SSIS side):**

```json
[{"name":"CustomerID","ssis_type_raw":"wstr"},{"name":"CountryCode","ssis_type_raw":"wstr"}]
```

**Unconfirmed candidates** (found by a bounded name search, 3 match(es) — none of these are confirmed correlates):

- `Package_Legacy.cs`:

```csharp
            "DFT_FixedWidthImport",
            source,
            [
                new ConditionalSplitBranch<DFT_FixedWidthImportCsvRow, CustomerFixedImport>(
                    "Multicast Output 1",
                    new MCAST_FixedRows_MulticastOutput1(),
                    new SqlBulkSink<CustomerFixedImport>(Opt<BulkCopyOptions>(), Log<SqlBulkSink<CustomerFixedImport>>())),
                new ConditionalSplitBranch<DFT_FixedWidthImportCsvRow, DFT_FixedWidthImportFFDST_AuditTrail_APPEND>(
                    "Multicast Output 2",
                    new MCAST_FixedRows_MulticastOutput2(),
                    new FlatFileBulkSink<DFT_FixedWidthImportFFDST_AuditTrail_APPEND>(File("DFT_FixedWidthImportFFDST_AuditTrail_APPEND"), false, null, [new FlatFileColumnFormat("CustomerID", null, ","), new FlatFileColumnFormat("CountryCode", null, "\r\n")], Log<FlatFileBulkSink<DFT_FixedWidthImportFFDST_AuditTrail_APPEND>>()))
            ],
            Log<MulticastStep<DFT_FixedWidthImportCsvRow>>());
```

- `Mapping\MCAST_FixedRows_MulticastOutput2.cs`:

```csharp
using Etl.Core.Abstractions;
using Package_Legacy.Csv;
using Package_Legacy.Model;

namespace Package_Legacy.Mapping;

public sealed class MCAST_FixedRows_MulticastOutput2 : IRowTransform<DFT_FixedWidthImportCsvRow, DFT_FixedWidthImportFFDST_AuditTrail_APPEND>
{
    public DFT_FixedWidthImportFFDST_AuditTrail_APPEND Map(DFT_FixedWidthImportCsvRow row, in RowContext ctx) => new()
    {
        CustomerID = row.CustomerID,
        CountryCode = row.CountryCode,
```

- `Model\DFT_FixedWidthImportFFDST_AuditTrail_APPEND.cs`:

```csharp
namespace Package_Legacy.Model;

public sealed class DFT_FixedWidthImportFFDST_AuditTrail_APPEND
{
    public string CustomerID { get; set; } = "";

    public string CountryCode { get; set; } = "";
}
```
