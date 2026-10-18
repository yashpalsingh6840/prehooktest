# Gate 3 report — Package_Exports

Per-component input/output schema parity. Declared column name + SSIS-type-to-.NET-type shape only — never actual data or execution. Every pipeline component is accounted for: a component with no populated externalMetadataColumns on either side (a pure pipeline-internal transform) is `Unverifiable`, not a silent pass.

| Verdict | Count |
|---|---|
| Match | 6 |

## Match (6)

### [DestinationInput] `ADO_DST_ExportLog` — **Match**

```
vs. generated class 'CustomerExportLog' (Model\CustomerExportLog.cs):
  = CustomerID: SSIS 'i4' -> 'int?'.
  = FullName: SSIS 'wstr' -> 'string'.
```

### [SourceOutput] `ADO_SRC_Customers` — **Match**

```
vs. generated class 'CustomerExportLogSqlRow' (Sql\CustomerExportLogSqlRow.cs):
  = CustomerID: SSIS 'wstr' -> 'string?'.
  = FullName: SSIS 'wstr' -> 'string'.
```

### [DestinationInput] `DFT_ExportDelimited_DST` — **Match**

```
vs. generated class 'DFT_ExportDelimited' (Model\DFT_ExportDelimited.cs):
  = CleanEmail: SSIS 'wstr' -> 'string'.
  = CustomerID: SSIS 'wstr' -> 'string'.
  = FullName: SSIS 'wstr' -> 'string'.
```

### [SourceOutput] `DFT_ExportDelimited_SRC` — **Match**

```
vs. generated class 'DFT_ExportDelimitedSqlRow' (Sql\DFT_ExportDelimitedSqlRow.cs):
  = CleanEmail: SSIS 'wstr' -> 'string'.
  = CustomerID: SSIS 'wstr' -> 'string'.
  = FullName: SSIS 'wstr' -> 'string'.
```

### [DestinationInput] `DFT_ExportFixedWidth_DST` — **Match**

```
vs. generated class 'DFT_ExportFixedWidth' (Model\DFT_ExportFixedWidth.cs):
  = CleanEmail: SSIS 'wstr' -> 'string'.
  = CustomerID: SSIS 'wstr' -> 'string'.
  = FullName: SSIS 'wstr' -> 'string'.
  = RowEnd: SSIS 'wstr' -> 'string'.
```

### [SourceOutput] `DFT_ExportFixedWidth_SRC` — **Match**

```
vs. generated class 'DFT_ExportFixedWidthSqlRow' (Sql\DFT_ExportFixedWidthSqlRow.cs):
  = CleanEmail: SSIS 'wstr' -> 'string'.
  = CustomerID: SSIS 'wstr' -> 'string'.
  = FullName: SSIS 'wstr' -> 'string'.
  = RowEnd: SSIS 'str' -> 'string'.
```
