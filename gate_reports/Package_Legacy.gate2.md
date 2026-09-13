# Gate 2 report — Package_Legacy

Control-flow / data-flow graph parity. Every node/edge/concurrency-group item from both sides is listed below with a verdict and evidence — never a bare pass/fail. Data-flow-internal component checks are scoped to `Unverifiable` presence-only evidence; full per-component structural parity is not yet implemented (see SSIS-Rewrite-Validation-Plan.md and docs/dtsx-graph-notes.md §6).

| Verdict | Count |
|---|---|
| Unverifiable | 3 |
| Match | 5 |

## Unverifiable (3)

### [ControlFlow] `SQL_LegacyStep_DISABLED` — **Unverifiable**

DTS:Disabled="True" — excluded from the comparable node/edge set; confirmed to have no generated counterpart by design, not treated as a gap.

### [DataFlow:DFT_FixedWidthImport] `FFDST_AuditTrail_APPEND` — **Unverifiable**

Component name found by text presence inside DFT_FixedWidthImport's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_FixedWidthImport] `MCAST_FixedRows` — **Unverifiable**

Component name found by text presence inside DFT_FixedWidthImport's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

## Match (5)

### [ControlFlow] `SQL_AtomicSwap` — **Match**

Sequential call found in expected position in RunAsync.

### [ControlFlow] `DFT_FixedWidthImport` — **Match**

Sequential call found in expected position in RunAsync.

### [ControlFlow] `SQL_CacheSet_SecondDb` — **Match**

Sequential call found in expected position in RunAsync.

### [DataFlow:DFT_FixedWidthImport] `FF_SRC_CustomersFixed` — **Match**

Deferring to Gate 3's own per-component schema comparison for this component, which independently reached 'Match' — stronger, real evidence than this scope's plain name-presence check (see the Gate 3 report for the actual column-level comparison).

### [DataFlow:DFT_FixedWidthImport] `OLEDST_CustomerFixedImport` — **Match**

Deferring to Gate 3's own per-component schema comparison for this component, which independently reached 'Match' — stronger, real evidence than this scope's plain name-presence check (see the Gate 3 report for the actual column-level comparison).
