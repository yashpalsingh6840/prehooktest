# Gate 1 report — Package_Exports

SQL statement & schema parity. Every SQL item from both sides is listed below with a verdict and evidence — never a bare pass/fail.

| Verdict | Count |
|---|---|
| Match | 5 |

## Match (5)

### `ADO_DST_ExportLog` — **Match**

Table identity matches (CustomerExportLog/CustomerExportLog).

- Left: `Package\DFT_AdoNetRoundTrip\ADO_DST_ExportLog` (Microsoft.ADONETDestination, `property:TableOrViewName`)
- Right: `Model\Package_ExportsDbContext.cs` (EfCoreTable)

### `ADO_SRC_Customers` — **Match**

Exact text match.

- Left: `Package\DFT_AdoNetRoundTrip\ADO_SRC_Customers` (Microsoft.DataReaderSourceAdapter, `property:SqlCommand`)
- Right: `Package_Exports.cs` (InlineSqlRowSource)

### `DFT_ExportDelimited_SRC` — **Match**

Exact text match.

- Left: `Package\DFT_ExportDelimited\DFT_ExportDelimited_SRC` (Microsoft.OLEDBSource, `property:SqlCommand`)
- Right: `Package_Exports.cs` (InlineSqlRowSource)

### `DFT_ExportFixedWidth_SRC` — **Match**

Exact text match.

- Left: `Package\DFT_ExportFixedWidth\DFT_ExportFixedWidth_SRC` (Microsoft.OLEDBSource, `property:SqlCommand`)
- Right: `Package_Exports.cs` (InlineSqlRowSource)

### `SQL_TruncateExportLog` — **Match**

Exact text match.

- Left: `Package\SQL_TruncateExportLog` (Microsoft.ExecuteSQLTask, `SQLTask:SqlStatementSource`)
- Right: `Mapping\SQL_TruncateExportLogStatement.cs` (ExecuteSqlStatement)
