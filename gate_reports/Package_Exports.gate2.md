# Gate 2 report — Package_Exports

Control-flow / data-flow graph parity. Every node/edge/concurrency-group item from both sides is listed below with a verdict and evidence — never a bare pass/fail. Data-flow-internal component checks are scoped to `Unverifiable` presence-only evidence; full per-component structural parity is not yet implemented (see SSIS-Rewrite-Validation-Plan.md and docs/dtsx-graph-notes.md §6).

| Verdict | Count |
|---|---|
| Match | 10 |

## Match (10)

### [Concurrency] `DFT_ExportDelimited` — **Match**

Part of a matched Task.WhenAll concurrency group: ['DFT_ExportDelimited', 'DFT_ExportFixedWidth', 'SQL_TruncateExportLog'].

### [Concurrency] `DFT_ExportFixedWidth` — **Match**

Part of a matched Task.WhenAll concurrency group: ['DFT_ExportDelimited', 'DFT_ExportFixedWidth', 'SQL_TruncateExportLog'].

### [Concurrency] `SQL_TruncateExportLog` — **Match**

Part of a matched Task.WhenAll concurrency group: ['DFT_ExportDelimited', 'DFT_ExportFixedWidth', 'SQL_TruncateExportLog'].

### [ControlFlow] `DFT_AdoNetRoundTrip` — **Match**

Sequential call found in expected position in RunAsync.

### [DataFlow:DFT_AdoNetRoundTrip] `ADO_DST_ExportLog` — **Match**

Deferring to Gate 3's own per-component schema comparison for this component, which independently reached 'Match' — stronger, real evidence than this scope's plain name-presence check (see the Gate 3 report for the actual column-level comparison).

### [DataFlow:DFT_AdoNetRoundTrip] `ADO_SRC_Customers` — **Match**

Deferring to Gate 3's own per-component schema comparison for this component, which independently reached 'Match' — stronger, real evidence than this scope's plain name-presence check (see the Gate 3 report for the actual column-level comparison).

### [DataFlow:DFT_ExportDelimited] `DFT_ExportDelimited_DST` — **Match**

Deferring to Gate 3's own per-component schema comparison for this component, which independently reached 'Match' — stronger, real evidence than this scope's plain name-presence check (see the Gate 3 report for the actual column-level comparison).

### [DataFlow:DFT_ExportDelimited] `DFT_ExportDelimited_SRC` — **Match**

Deferring to Gate 3's own per-component schema comparison for this component, which independently reached 'Match' — stronger, real evidence than this scope's plain name-presence check (see the Gate 3 report for the actual column-level comparison).

### [DataFlow:DFT_ExportFixedWidth] `DFT_ExportFixedWidth_DST` — **Match**

Deferring to Gate 3's own per-component schema comparison for this component, which independently reached 'Match' — stronger, real evidence than this scope's plain name-presence check (see the Gate 3 report for the actual column-level comparison).

### [DataFlow:DFT_ExportFixedWidth] `DFT_ExportFixedWidth_SRC` — **Match**

Deferring to Gate 3's own per-component schema comparison for this component, which independently reached 'Match' — stronger, real evidence than this scope's plain name-presence check (see the Gate 3 report for the actual column-level comparison).
