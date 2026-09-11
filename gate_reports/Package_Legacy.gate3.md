# Gate 3 report — Package_Legacy

Per-component input/output schema parity. Declared column name + SSIS-type-to-.NET-type shape only — never actual data or execution. Every pipeline component is accounted for: a component with no populated externalMetadataColumns on either side (a pure pipeline-internal transform) is `Unverifiable`, not a silent pass.

| Verdict | Count |
|---|---|
| Unverifiable | 2 |
| Match | 2 |

## Unverifiable (2)

### [DestinationInput] `FFDST_AuditTrail_APPEND` — **Unverifiable**

```
'FFDST_AuditTrail_APPEND' (Microsoft.FlatFileDestination) is not a database destination Gate 1 can identify by (schema, table), and no generated factory method is named after this component either — no schema-correlation mechanism matched (2 declared column(s) not compared).
```

### [InternalTransform] `MCAST_FixedRows` — **Unverifiable**

```
'MCAST_FixedRows' (Microsoft.Multicast) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6).
```

## Match (2)

### [SourceOutput] `FF_SRC_CustomersFixed` — **Match**

```
vs. generated class 'DFT_FixedWidthImportCsvRow' (Csv\DFT_FixedWidthImportCsvRow.cs):
  = CountryCode: SSIS 'wstr' -> 'string'.
  = CustomerID: SSIS 'wstr' -> 'string'.
  = Email: SSIS 'wstr' -> 'string'.
  = FullName: SSIS 'wstr' -> 'string'.
```

### [DestinationInput] `OLEDST_CustomerFixedImport` — **Match**

```
vs. generated class 'CustomerFixedImport' (Model\CustomerFixedImport.cs):
  = CountryCode: SSIS 'wstr' -> 'string'.
  = CustomerID: SSIS 'wstr' -> 'string'.
  = Email: SSIS 'wstr' -> 'string'.
  = FullName: SSIS 'wstr' -> 'string'.
```
