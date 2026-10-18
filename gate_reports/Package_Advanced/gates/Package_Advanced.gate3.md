# Gate 3 report — Package_Advanced

Per-component input/output schema parity. Declared column name + SSIS-type-to-.NET-type shape only — never actual data or execution. Every pipeline component is accounted for: a component with no populated externalMetadataColumns on either side (a pure pipeline-internal transform) is `Unverifiable`, not a silent pass.

| Verdict | Count |
|---|---|
| Unverifiable | 1 |
| Match | 3 |

## Unverifiable (1)

### [DestinationInput] `OLECMD_SetFlag` — **Unverifiable**

```
'OLECMD_SetFlag' (Microsoft.OLEDBCommand) is not a database destination Gate 1 can identify by (schema, table), and no generated factory method is named after this component either — no schema-correlation mechanism matched (1 declared column(s) not compared).
```

<details><summary>Semantic evidence</summary>

```json
[
  {
    "name": "@CustomerID",
    "ssis_type_raw": "i4"
  }
]
```

</details>

## Match (3)

### [SourceOutput] `EXCEL_SRC_Drip` — **Match**

```
vs. generated class 'DripEligibilityExcelRow' (Excel\DripEligibilityExcelRow.cs):
  = CustomerID: SSIS 'r8' -> 'double'.
  = DripEligible: SSIS 'wstr' -> 'string'.
  = ReviewedBy: SSIS 'wstr' -> 'string'.
```

### [DestinationInput] `OLEDST_Drip` — **Match**

```
vs. generated class 'DripEligibility' (Model\DripEligibility.cs):
  = CustomerID: SSIS 'i4' -> 'int'.
  = DripEligible: SSIS 'wstr' -> 'string'.
  = ReviewedBy: SSIS 'wstr' -> 'string'.
```

### [SourceOutput] `OLESRC_CustomerIds` — **Match**

```
vs. generated class 'DFT_FlagCustomersSqlRow' (Sql\DFT_FlagCustomersSqlRow.cs):
  = CustomerID: SSIS 'i4' -> 'int'.
```
