# Gate 3 report — Package_Exports

Per-component input/output schema parity. Declared column name + SSIS-type-to-.NET-type shape only — never actual data or execution. Every pipeline component is accounted for: a component with no populated externalMetadataColumns on either side (a pure pipeline-internal transform) is `Unverifiable`, not a silent pass.

| Verdict | Count |
|---|---|
| Mismatch | 1 |
| Unverifiable | 2 |
| Match | 3 |

## Mismatch (1)

### [DestinationInput] `ADO_DST_ExportLog` — **Mismatch**

```
vs. generated class 'CustomerExportLog' (Model\CustomerExportLog.cs):
  x CustomerID: SSIS 'wstr' expects one of ('String',), generated type is 'int?'.
  = FullName: SSIS 'wstr' -> 'string'.
```

## Unverifiable (2)

### [DestinationInput] `DFT_ExportDelimited_DST` — **Unverifiable**

```
'DFT_ExportDelimited_DST' (Microsoft.FlatFileDestination) is not a database destination Gate 1 can identify by (schema, table) — no schema-correlation mechanism covers this destination kind yet (3 declared column(s) not compared).
```

### [DestinationInput] `DFT_ExportFixedWidth_DST` — **Unverifiable**

```
'DFT_ExportFixedWidth_DST' (Microsoft.FlatFileDestination) is not a database destination Gate 1 can identify by (schema, table) — no schema-correlation mechanism covers this destination kind yet (4 declared column(s) not compared).
```

## Match (3)

### [SourceOutput] `ADO_SRC_Customers` — **Match**

```
vs. generated class 'CustomerExportLogSqlRow' (Sql\CustomerExportLogSqlRow.cs):
  = CustomerID: SSIS 'wstr' -> 'string?'.
  = FullName: SSIS 'wstr' -> 'string'.
```

### [SourceOutput] `DFT_ExportDelimited_SRC` — **Match**

```
vs. generated class 'DFT_ExportDelimitedSqlRow' (Sql\DFT_ExportDelimitedSqlRow.cs):
  = CleanEmail: SSIS 'wstr' -> 'string'.
  = CustomerID: SSIS 'wstr' -> 'string'.
  = FullName: SSIS 'wstr' -> 'string'.
```

### [SourceOutput] `DFT_ExportFixedWidth_SRC` — **Match**

```
vs. generated class 'DFT_ExportFixedWidthSqlRow' (Sql\DFT_ExportFixedWidthSqlRow.cs):
  = CleanEmail: SSIS 'wstr' -> 'string'.
  = CustomerID: SSIS 'wstr' -> 'string'.
  = FullName: SSIS 'wstr' -> 'string'.
  = RowEnd: SSIS 'str' -> 'string'.
```
