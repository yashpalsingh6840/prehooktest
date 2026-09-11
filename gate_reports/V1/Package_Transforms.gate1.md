# Gate 1 report — Package_Transforms

SQL statement & schema parity. Every SQL item from both sides is listed below with a verdict and evidence — never a bare pass/fail.

| Verdict | Count |
|---|---|
| Match | 9 |

## Match (9)

### `OLEDST_CustomerEnriched` — **Match**

Table identity matches (CustomerEnriched/CustomerEnriched).

- Left: `Package\DFT_DerivedAndSplit\OLEDST_CustomerEnriched` (Microsoft.OLEDBDestination, `property:OpenRowset`)
- Right: `Model\Package_TransformsDbContext.cs` (EfCoreTable)

### `LKP_Country` — **Match**

Exact text match.

- Left: `Package\DFT_LookupAndAggregate\LKP_Country` (Microsoft.Lookup, `property:SqlCommand`)
- Right: `Mapping\LKP_CountryCache.cs` (LookupCache)

### `OLEDST_RegionSummary` — **Match**

Table identity matches (RegionSummary/RegionSummary).

- Left: `Package\DFT_LookupAndAggregate\OLEDST_RegionSummary` (Microsoft.OLEDBDestination, `property:OpenRowset`)
- Right: `Model\Package_TransformsDbContext.cs` (EfCoreTable)

### `OLEDST_CustomerMerged` — **Match**

Table identity matches (CustomerMerged/CustomerMerged).

- Left: `Package\DFT_MergeSortedBranches\OLEDST_CustomerMerged` (Microsoft.OLEDBDestination, `property:OpenRowset`)
- Right: `Model\Package_TransformsDbContext.cs` (EfCoreTable)

### `OLEDST_CustomerWithContact` — **Match**

Table identity matches (CustomerWithContact/CustomerWithContact).

- Left: `Package\DFT_SortAndMergeJoin\OLEDST_CustomerWithContact` (Microsoft.OLEDBDestination, `property:OpenRowset`)
- Right: `Model\Package_TransformsDbContext.cs` (EfCoreTable)

### `SQL_TruncateEnriched` — **Match**

Exact text match.

- Left: `Package\SQL_TruncateEnriched` (Microsoft.ExecuteSQLTask, `SQLTask:SqlStatementSource`)
- Right: `Mapping\SQL_TruncateEnrichedStatement.cs` (ExecuteSqlStatement)

### `SQL_TruncateMerged` — **Match**

Exact text match.

- Left: `Package\SQL_TruncateMerged` (Microsoft.ExecuteSQLTask, `SQLTask:SqlStatementSource`)
- Right: `Mapping\SQL_TruncateMergedStatement.cs` (ExecuteSqlStatement)

### `SQL_TruncateRegionSummary` — **Match**

Exact text match.

- Left: `Package\SQL_TruncateRegionSummary` (Microsoft.ExecuteSQLTask, `SQLTask:SqlStatementSource`)
- Right: `Mapping\SQL_TruncateRegionSummaryStatement.cs` (ExecuteSqlStatement)

### `SQL_TruncateWithContact` — **Match**

Exact text match.

- Left: `Package\SQL_TruncateWithContact` (Microsoft.ExecuteSQLTask, `SQLTask:SqlStatementSource`)
- Right: `Mapping\SQL_TruncateWithContactStatement.cs` (ExecuteSqlStatement)
