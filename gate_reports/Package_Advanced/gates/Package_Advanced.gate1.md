# Gate 1 report — Package_Advanced

SQL statement & schema parity. Every SQL item from both sides is listed below with a verdict and evidence — never a bare pass/fail.

| Verdict | Count |
|---|---|
| NormalizedMatch | 2 |
| Match | 4 |

## NormalizedMatch (2)

### `OLECMD_SetFlag` — **NormalizedMatch**

SQL text match, correlated by content rather than name: left object 'OLECMD_SetFlag' vs. generated artifact 'DFT_FlagCustomers' (OleDbCommandStep).

- Left: `Package\DFT_FlagCustomers\OLECMD_SetFlag` (Microsoft.OLEDBCommand, `property:SqlCommand`)
- Right: `Package_Advanced.cs` (OleDbCommandStep)

### `SQL_LogFileName` — **NormalizedMatch**

Literal-segment template match (expression-driven statement).

- Left: `Package\FEL_SampleFiles\SQL_LogFileName` (Microsoft.ExecuteSQLTask, `DTS:PropertyExpression[SqlStatementSource]`)
- Right: `Mapping\SQL_LogFileNameStatement.cs` (ExecuteSqlStatement)

## Match (4)

### `OLEDST_Drip` — **Match**

Table identity matches (DripEligibility/DripEligibility).

- Left: `Package\DFT_ExcelImport\OLEDST_Drip` (Microsoft.OLEDBDestination, `property:OpenRowset`)
- Right: `Model\Package_AdvancedDbContext.cs` (EfCoreTable)

### `OLESRC_CustomerIds` — **Match**

Exact text match.

- Left: `Package\DFT_FlagCustomers\OLESRC_CustomerIds` (Microsoft.OLEDBSource, `property:SqlCommand`)
- Right: `Package_Advanced.cs` (InlineSqlRowSource)

### `SQL_TruncateTargets` — **Match**

Exact text match.

- Left: `Package\SEQ_Prepare\SQL_TruncateTargets` (Microsoft.ExecuteSQLTask, `SQLTask:SqlStatementSource`)
- Right: `Mapping\SQL_TruncateTargetsStatement.cs` (ExecuteSqlStatement)

### `SQL_LogOnError` — **Match**

Exact text match.

- Left: `Package.EventHandlers[OnError]\SQL_LogOnError` (Microsoft.ExecuteSQLTask, `SQLTask:SqlStatementSource`)
- Right: `Package_Advanced.cs` (ErrorHandlerSql)
