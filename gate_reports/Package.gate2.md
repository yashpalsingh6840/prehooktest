# Gate 2 report — Package

Control-flow / data-flow graph parity. Every node/edge/concurrency-group item from both sides is listed below with a verdict and evidence — never a bare pass/fail. Data-flow-internal component checks are scoped to `Unverifiable` presence-only evidence; full per-component structural parity is not yet implemented (see SSIS-Rewrite-Validation-Plan.md and docs/dtsx-graph-notes.md §6).

| Verdict | Count |
|---|---|
| Unverifiable | 7 |
| Match | 6 |

## Unverifiable (7)

### [DataFlow:DFT_BuildSalesSummary] `OLEDST_SalesSummary` — **Unverifiable**

Component name found by text presence inside DFT_BuildSalesSummary's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_BuildSalesSummary] `OLESRC_OrdersAndDetails` — **Unverifiable**

Component name found by text presence inside DFT_BuildSalesSummary's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_BuildSalesSummary] `SCR_ComputeCustomerTier` — **Unverifiable**

Component name NOT found by text presence inside DFT_BuildSalesSummary's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_LoadCustomers] `FF_SRC_Customers` — **Unverifiable**

Component name found by text presence inside DFT_LoadCustomers's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_LoadCustomers] `OLEDST_StagingCustomers` — **Unverifiable**

Component name found by text presence inside DFT_LoadCustomers's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_LoadCustomers] `OLEDST_StagingErrors` — **Unverifiable**

Component name NOT found by text presence inside DFT_LoadCustomers's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_LoadCustomers] `SCR_CleanseCustomerRow` — **Unverifiable**

Component name NOT found by text presence inside DFT_LoadCustomers's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

## Match (6)

### [ControlFlow] `SQL_TruncateTargets` — **Match**

Sequential call found in expected position in RunAsync.

### [ControlFlow] `SCR_ValidateAndLogStart` — **Match**

Sequential call found in expected position in RunAsync.

### [ControlFlow] `DFT_LoadCustomers` — **Match**

Sequential call found in expected position in RunAsync.

### [ControlFlow] `SCR_NotifyAndLogProgress` — **Match**

Sequential call found in expected position in RunAsync.

### [ControlFlow] `DFT_BuildSalesSummary` — **Match**

Expression-gated node found as a guarded call (guard: '@[User::RowsLoaded] > 0').

### [FailureHandler] `SQL_LogLoadFailure` — **Match**

Failure-only node found inlined in the generated catch-block handler set.
