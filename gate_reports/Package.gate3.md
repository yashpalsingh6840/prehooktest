# Gate 3 report — Package

Per-component input/output schema parity. Declared column name + SSIS-type-to-.NET-type shape only — never actual data or execution. Every pipeline component is accounted for: a component with no populated externalMetadataColumns on either side (a pure pipeline-internal transform) is `Unverifiable`, not a silent pass.

| Verdict | Count |
|---|---|
| Unverifiable | 2 |
| NormalizedMatch | 2 |
| Match | 3 |

## Unverifiable (2)

### [InternalTransform] `SCR_ComputeCustomerTier` — **Unverifiable**

```
'SCR_ComputeCustomerTier' (Microsoft.ScriptComponentHost) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6).
```

### [InternalTransform] `SCR_CleanseCustomerRow` — **Unverifiable**

```
'SCR_CleanseCustomerRow' (Microsoft.ScriptComponentHost) has no populated externalMetadataColumns on either side — a pipeline-internal component with no generated declared-shape counterpart of its own (see docs/dtsx-graph-notes.md §6).
```

## NormalizedMatch (2)

### [DestinationInput] `OLEDST_SalesSummary` — **NormalizedMatch**

```
vs. generated class 'SalesSummary' (Model\SalesSummary.cs):
  = CustomerID: SSIS 'i4' -> 'int'.
  = CustomerTier: SSIS 'wstr' -> 'string'.
  = Email: SSIS 'wstr' -> 'string'.
  = FullName: SSIS 'wstr' -> 'string'.
  ~ LastOrderDate: SSIS 'dbDate' -> 'DateOnly' (accepted modern equivalent of 'DateTime').
  = ProcessedDateTime: SSIS 'dbTimeStamp' -> 'DateTime'.
  = TotalOrders: SSIS 'i4' -> 'int'.
  = TotalRevenue: SSIS 'r8' -> 'double'.
```

### [SourceOutput] `OLESRC_OrdersAndDetails` — **NormalizedMatch**

```
vs. generated class 'SalesSummarySqlRow' (Sql\SalesSummarySqlRow.cs):
  = CustomerID: SSIS 'i4' -> 'int'.
  = Email: SSIS 'wstr' -> 'string'.
  = FullName: SSIS 'wstr' -> 'string'.
  ~ LastOrderDate: SSIS 'dbDate' -> 'DateOnly' (accepted modern equivalent of 'DateTime').
  = TotalOrders: SSIS 'i4' -> 'int'.
  = TotalRevenue: SSIS 'r8' -> 'double'.
```

## Match (3)

### [SourceOutput] `FF_SRC_Customers` — **Match**

```
vs. generated class 'StagingCustomersCsvRow' (Csv\StagingCustomersCsvRow.cs):
  = Country: SSIS 'wstr' -> 'string'.
  = CustomerID: SSIS 'wstr' -> 'string'.
  = Email: SSIS 'wstr' -> 'string'.
  = FirstName: SSIS 'wstr' -> 'string'.
  = LastName: SSIS 'wstr' -> 'string'.
  = Phone: SSIS 'wstr' -> 'string'.
  = SignupDate: SSIS 'wstr' -> 'string'.
```

### [DestinationInput] `OLEDST_StagingCustomers` — **Match**

```
vs. generated class 'StagingCustomers' (Model\StagingCustomers.cs):
  = CleanEmail: SSIS 'wstr' -> 'string'.
  = Country: SSIS 'wstr' -> 'string'.
  = CustomerID: SSIS 'wstr' -> 'string'.
  = Email: SSIS 'wstr' -> 'string'.
  = FirstName: SSIS 'wstr' -> 'string'.
  = FullName: SSIS 'wstr' -> 'string'.
  = IsValidRow: SSIS 'bool' -> 'bool'.
  = LastName: SSIS 'wstr' -> 'string'.
  = LoadDateTime: SSIS 'dbTimeStamp' -> 'DateTime'.
  = Phone: SSIS 'wstr' -> 'string'.
  = SignupDate: SSIS 'wstr' -> 'string'.
```

### [DestinationInput] `OLEDST_StagingErrors` — **Match**

```
vs. generated class 'StagingCustomersError' (Model\StagingCustomersError.cs):
  = CustomerID: SSIS 'wstr' -> 'string'.
  = Email: SSIS 'wstr' -> 'string'.
  = ErrorCode: SSIS 'i4' -> 'int'.
  = ErrorColumn: SSIS 'i4' -> 'int'.
  = FirstName: SSIS 'wstr' -> 'string'.
  = LastName: SSIS 'wstr' -> 'string'.
  i FailedAt: generated property has no matching left-side column. Not blocking — a destination may legitimately model an unmapped column.
```
