# Gate 1 report — Package

SQL statement & schema parity. Every SQL item from both sides is listed below with a verdict and evidence — never a bare pass/fail.

| Verdict | Count |
|---|---|
| Unverifiable | 2 |
| Match | 6 |

## Unverifiable (2)

### `SCR_ComputeCustomerTier` — **Unverifiable**

Script Component may contain SQL embedded in its source/binary blob; not extractable as text from the outer .dtsx XML (see docs/dtsx-xml-notes.md §1.3).

- Left: `Package\DFT_BuildSalesSummary\SCR_ComputeCustomerTier` (Microsoft.ScriptComponentHost, `ScriptComponentHost:SourceCode/BinaryCode`)

### `SCR_CleanseCustomerRow` — **Unverifiable**

Script Component may contain SQL embedded in its source/binary blob; not extractable as text from the outer .dtsx XML (see docs/dtsx-xml-notes.md §1.3).

- Left: `Package\DFT_LoadCustomers\SCR_CleanseCustomerRow` (Microsoft.ScriptComponentHost, `ScriptComponentHost:SourceCode/BinaryCode`)

## Match (6)

### `OLEDST_SalesSummary` — **Match**

Table identity matches (SalesSummary/SalesSummary).

- Left: `Package\DFT_BuildSalesSummary\OLEDST_SalesSummary` (Microsoft.OLEDBDestination, `property:OpenRowset`)
- Right: `Model\PackageDbContext.cs` (EfCoreTable)

### `OLESRC_OrdersAndDetails` — **Match**

Exact text match.

- Left: `Package\DFT_BuildSalesSummary\OLESRC_OrdersAndDetails` (Microsoft.OLEDBSource, `property:SqlCommand`)
- Right: `Package.cs` (InlineSqlRowSource)

### `OLEDST_StagingCustomers` — **Match**

Table identity matches (StagingCustomers/StagingCustomers).

- Left: `Package\DFT_LoadCustomers\OLEDST_StagingCustomers` (Microsoft.OLEDBDestination, `property:OpenRowset`)
- Right: `Model\PackageDbContext.cs` (EfCoreTable)

### `OLEDST_StagingErrors` — **Match**

Table identity matches (StagingCustomers_Errors/StagingCustomers_Errors).

- Left: `Package\DFT_LoadCustomers\OLEDST_StagingErrors` (Microsoft.OLEDBDestination, `property:OpenRowset`)
- Right: `Model\PackageDbContext.cs` (EfCoreTable)

### `SQL_LogLoadFailure` — **Match**

Exact text match.

- Left: `Package\SQL_LogLoadFailure` (Microsoft.ExecuteSQLTask, `SQLTask:SqlStatementSource`)
- Right: `Package.cs` (ErrorHandlerSql)

### `SQL_TruncateTargets` — **Match**

Exact text match.

- Left: `Package\SQL_TruncateTargets` (Microsoft.ExecuteSQLTask, `SQLTask:SqlStatementSource`)
- Right: `Mapping\SQL_TruncateTargetsStatement.cs` (ExecuteSqlStatement)
