# Gate 1 report — Package_Legacy

SQL statement & schema parity. Every SQL item from both sides is listed below with a verdict and evidence — never a bare pass/fail.

| Verdict | Count |
|---|---|
| Match | 3 |

## Match (3)

### `OLEDST_CustomerFixedImport` — **Match**

Table identity matches (CustomerFixedImport/CustomerFixedImport).

- Left: `Package\DFT_FixedWidthImport\OLEDST_CustomerFixedImport` (Microsoft.OLEDBDestination, `property:OpenRowset`)
- Right: `Model\Package_LegacyDbContext.cs` (EfCoreTable)

### `SQL_AtomicSwap` — **Match**

Exact text match.

- Left: `Package\SQL_AtomicSwap` (Microsoft.ExecuteSQLTask, `SQLTask:SqlStatementSource`)
- Right: `Mapping\SQL_AtomicSwapStatement.cs` (ExecuteSqlStatement)

### `SQL_CacheSet_SecondDb` — **Match**

Exact text match.

- Left: `Package\SQL_CacheSet_SecondDb` (Microsoft.ExecuteSQLTask, `SQLTask:SqlStatementSource`)
- Right: `Mapping\SQL_CacheSet_SecondDbStatement.cs` (ExecuteSqlStatement)
