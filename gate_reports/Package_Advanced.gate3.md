# Gate 3 report — Package_Advanced

Per-component input/output schema parity. Declared column name + SSIS-type-to-.NET-type shape only — never actual data or execution. Every pipeline component is accounted for: a component with no populated externalMetadataColumns on either side (a pure pipeline-internal transform) is `Unverifiable`, not a silent pass.

| Verdict | Count |
|---|---|
| Mismatch | 1 |
| Unverifiable | 1 |
| Match | 2 |

## Mismatch (1)

### [DestinationInput] `OLEDST_Drip` — **Mismatch**

```
vs. generated class 'DripEligibility' (Model\DripEligibility.cs):
  x CustomerID: SSIS 'r8' expects one of ('Double',), generated type is 'int'.
  = DripEligible: SSIS 'wstr' -> 'string'.
  = ReviewedBy: SSIS 'wstr' -> 'string'.
```

## Unverifiable (1)

### [DestinationInput] `OLECMD_SetFlag` — **Unverifiable**

```
'OLECMD_SetFlag' (Microsoft.OLEDBCommand) is not a database destination Gate 1 can identify by (schema, table) — no schema-correlation mechanism covers this destination kind yet (1 declared column(s) not compared).
```

## Match (2)

### [SourceOutput] `EXCEL_SRC_Drip` — **Match**

```
vs. generated class 'DripEligibilityExcelRow' (Excel\DripEligibilityExcelRow.cs):
  = CustomerID: SSIS 'r8' -> 'double'.
  = DripEligible: SSIS 'wstr' -> 'string'.
  = ReviewedBy: SSIS 'wstr' -> 'string'.
```

### [SourceOutput] `OLESRC_CustomerIds` — **Match**

```
vs. generated class 'DFT_FlagCustomersSqlRow' (Sql\DFT_FlagCustomersSqlRow.cs):
  = CustomerID: SSIS 'i4' -> 'int'.
```
