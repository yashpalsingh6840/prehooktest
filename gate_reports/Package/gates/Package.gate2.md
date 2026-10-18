# Gate 2 report — Package

Control-flow / data-flow graph parity. Every node/edge/concurrency-group item from both sides is listed below with a verdict and evidence — never a bare pass/fail. Data-flow-internal component checks are scoped to `Unverifiable` presence-only evidence; full per-component structural parity is not yet implemented (see SSIS-Rewrite-Validation-Plan.md and docs/dtsx-graph-notes.md §6).

| Verdict | Count |
|---|---|
| Unverifiable | 2 |
| Match | 11 |

## Unverifiable (2)

### [DataFlow:DFT_BuildSalesSummary] `SCR_ComputeCustomerTier` — **Unverifiable**

Component name NOT found by text presence inside DFT_BuildSalesSummary's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_LoadCustomers] `SCR_CleanseCustomerRow` — **Unverifiable**

Component name NOT found by text presence inside DFT_LoadCustomers's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

## Match (11)

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

### [DataFlow:DFT_BuildSalesSummary] `OLEDST_SalesSummary` — **Match**

Deferring to Gate 3's own per-component schema comparison for this component, which independently reached 'NormalizedMatch' — stronger, real evidence than this scope's plain name-presence check (see the Gate 3 report for the actual column-level comparison).

### [DataFlow:DFT_BuildSalesSummary] `OLESRC_OrdersAndDetails` — **Match**

Deferring to Gate 3's own per-component schema comparison for this component, which independently reached 'NormalizedMatch' — stronger, real evidence than this scope's plain name-presence check (see the Gate 3 report for the actual column-level comparison).

### [DataFlow:DFT_LoadCustomers] `FF_SRC_Customers` — **Match**

Deferring to Gate 3's own per-component schema comparison for this component, which independently reached 'Match' — stronger, real evidence than this scope's plain name-presence check (see the Gate 3 report for the actual column-level comparison).

### [DataFlow:DFT_LoadCustomers] `OLEDST_StagingCustomers` — **Match**

Deferring to Gate 3's own per-component schema comparison for this component, which independently reached 'Match' — stronger, real evidence than this scope's plain name-presence check (see the Gate 3 report for the actual column-level comparison).

### [DataFlow:DFT_LoadCustomers] `OLEDST_StagingErrors` — **Match**

Deferring to Gate 3's own per-component schema comparison for this component, which independently reached 'Match' — stronger, real evidence than this scope's plain name-presence check (see the Gate 3 report for the actual column-level comparison).
