# Gate 2 report — Package_Exports

Control-flow / data-flow graph parity. Every node/edge/concurrency-group item from both sides is listed below with a verdict and evidence — never a bare pass/fail. Data-flow-internal component checks are scoped to `Unverifiable` presence-only evidence; full per-component structural parity is not yet implemented (see SSIS-Rewrite-Validation-Plan.md and docs/dtsx-graph-notes.md §6).

| Verdict | Count |
|---|---|
| Unverifiable | 6 |
| Match | 4 |

## Unverifiable (6)

### [DataFlow:DFT_AdoNetRoundTrip] `ADO_DST_ExportLog` — **Unverifiable**

Component name found by text presence inside DFT_AdoNetRoundTrip's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_AdoNetRoundTrip] `ADO_SRC_Customers` — **Unverifiable**

Component name found by text presence inside DFT_AdoNetRoundTrip's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_ExportDelimited] `DFT_ExportDelimited_DST` — **Unverifiable**

Component name found by text presence inside DFT_ExportDelimited's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_ExportDelimited] `DFT_ExportDelimited_SRC` — **Unverifiable**

Component name found by text presence inside DFT_ExportDelimited's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_ExportFixedWidth] `DFT_ExportFixedWidth_DST` — **Unverifiable**

Component name found by text presence inside DFT_ExportFixedWidth's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

### [DataFlow:DFT_ExportFixedWidth] `DFT_ExportFixedWidth_SRC` — **Unverifiable**

Component name found by text presence inside DFT_ExportFixedWidth's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

## Match (4)

### [Concurrency] `DFT_ExportDelimited` — **Match**

Part of a matched Task.WhenAll concurrency group: ['DFT_ExportDelimited', 'DFT_ExportFixedWidth', 'SQL_TruncateExportLog'].

### [Concurrency] `DFT_ExportFixedWidth` — **Match**

Part of a matched Task.WhenAll concurrency group: ['DFT_ExportDelimited', 'DFT_ExportFixedWidth', 'SQL_TruncateExportLog'].

### [Concurrency] `SQL_TruncateExportLog` — **Match**

Part of a matched Task.WhenAll concurrency group: ['DFT_ExportDelimited', 'DFT_ExportFixedWidth', 'SQL_TruncateExportLog'].

### [ControlFlow] `DFT_AdoNetRoundTrip` — **Match**

Sequential call found in expected position in RunAsync.
