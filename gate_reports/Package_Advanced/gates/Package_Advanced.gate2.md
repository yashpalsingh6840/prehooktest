# Gate 2 report — Package_Advanced

Control-flow / data-flow graph parity. Every node/edge/concurrency-group item from both sides is listed below with a verdict and evidence — never a bare pass/fail. Data-flow-internal component checks are scoped to `Unverifiable` presence-only evidence; full per-component structural parity is not yet implemented (see SSIS-Rewrite-Validation-Plan.md and docs/dtsx-graph-notes.md §6).

| Verdict | Count |
|---|---|
| Unverifiable | 1 |
| Match | 11 |

## Unverifiable (1)

### [DataFlow:DFT_FlagCustomers] `OLECMD_SetFlag` — **Unverifiable**

Component name NOT found by text presence inside DFT_FlagCustomers's generated method — not a structural check of its data-flow shape; the generator may legitimately name this component's generated counterpart after its target table instead. Full data-flow-internal parity is not yet implemented.

## Match (11)

### [ControlFlow] `SEQ_Prepare` — **Match**

Sequential call found in expected position in RunAsync.

### [Concurrency] `DFT_ExcelImport` — **Match**

Part of a matched Task.WhenAll concurrency group: ['DFT_ExcelImport', 'DFT_FlagCustomers', 'FEL_SampleFiles'].

### [Concurrency] `DFT_FlagCustomers` — **Match**

Part of a matched Task.WhenAll concurrency group: ['DFT_ExcelImport', 'DFT_FlagCustomers', 'FEL_SampleFiles'].

### [Concurrency] `FEL_SampleFiles` — **Match**

Part of a matched Task.WhenAll concurrency group: ['DFT_ExcelImport', 'DFT_FlagCustomers', 'FEL_SampleFiles'].

### [ControlFlow] `SQL_TruncateTargets` — **Match**

Sequential call found in expected position in SEQ_Prepare.

### [ControlFlow] `FST_ArchiveWorkbook` — **Match**

Sequential call found in expected position in SEQ_Prepare.

### [ControlFlow] `SQL_LogFileName` — **Match**

ForEach Loop's single child task is absorbed into FEL_SampleFiles's ForEachLoopStep lambda rather than getting its own call site — matched by the referenced BuildStatement class name.

### [EventHandler] `SQL_LogOnError` — **Match**

OnError event-handler task found inlined in the generated catch-block handler set.

### [DataFlow:DFT_ExcelImport] `EXCEL_SRC_Drip` — **Match**

Deferring to Gate 3's own per-component schema comparison for this component, which independently reached 'Match' — stronger, real evidence than this scope's plain name-presence check (see the Gate 3 report for the actual column-level comparison).

### [DataFlow:DFT_ExcelImport] `OLEDST_Drip` — **Match**

Deferring to Gate 3's own per-component schema comparison for this component, which independently reached 'Match' — stronger, real evidence than this scope's plain name-presence check (see the Gate 3 report for the actual column-level comparison).

### [DataFlow:DFT_FlagCustomers] `OLESRC_CustomerIds` — **Match**

Deferring to Gate 3's own per-component schema comparison for this component, which independently reached 'Match' — stronger, real evidence than this scope's plain name-presence check (see the Gate 3 report for the actual column-level comparison).
