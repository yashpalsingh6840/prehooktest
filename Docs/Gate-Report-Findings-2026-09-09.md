# Gate report findings — 2026-09-09 (updated: reread after the validator's own fix)

Reviewed all 15 reports in `D:\PoC\SSIS_RewriteValidationTool\gate_reports` (5 packages × 3
gates, from the `RewriteValidator` tool described in `Docs/SSIS-Rewrite-Validation-Plan.md`)
against `SSIS_From_Sandeep`'s real `.dtsx` files and the freshly-regenerated rewrite at
`D:\tmp\Sandeep_Packages\gen\generate`, **twice** — once before, once after the reports were
regenerated with a corrected validator.

**Bottom line, both times: zero real bugs found in `ssisx`/`Etl.Core`.** As of this reread, all
15 reports are now fully clean (`Match`/`NormalizedMatch`/expected `Unverifiable` only — zero
`Mismatch`, zero `LeftOnly`, anywhere).

## First pass — 6 "Mismatch" verdicts, all traced to a validator bug, none touched here

The first read of these reports showed 6 `Mismatch` verdicts, all in Gate 3 (schema parity).
Traced each one by hand against the real `.dtsx` XML:

| Package | Component | What was reported | What was actually true |
|---|---|---|---|
| `Package_Advanced` | `OLEDST_Drip` (`DripEligibility.CustomerID`) | SSIS `r8` expects `Double`, generated is `int` | Destination's own `<externalMetadataColumn dataType="i4">` — `ssisx`'s `int` was already correct. `r8` was only the Excel source's pre-write buffer type. |
| `Package_Exports` | `ADO_DST_ExportLog` (`CustomerExportLog.CustomerID`) | SSIS `wstr` expects `String`, generated is `int?` | Destination's own `<externalMetadataColumn dataType="i4">` — `ssisx`'s `int?` was already correct. |
| `Package_Transforms` | `OLEDST_RegionSummary` (`RegionSummary.CustomerCount`) | SSIS `ui8` expects `UInt64`, generated is `int` | Destination's own `<externalMetadataColumn dataType="i4">` — `ssisx`'s `int` was already correct. `ui8` was only `AGG_ByRegion`'s `Count()` output buffer type. |
| `Package_Transforms` | `OLEDST_CustomerEnriched` | `CustomerID_i4`/`SignupDate_dt` "no matching generated property"; `CustomerID`/`SignupDate` "no matching left column" | `CustomerID_i4` is `DCONV_Types`'s own renamed pipeline-buffer column; its `externalMetadataColumnId` resolves to the real destination column `CustomerID`. `ssisx` already named its property correctly. |
| `Package_Transforms` | `OLEDST_CustomerMerged` | Same shape | Same cause. |
| `Package_Transforms` | `OLEDST_CustomerWithContact` | Same shape | Same cause. |

Root cause identified (in the validator's own `ssis_validator/dtsx/component_schema.py`, not in
`ssisx`): `_extract_mapped_input_columns` was building a destination's "declared shape" from each
mapped `<inputColumn>`'s own `cachedName`/`cachedDataType` — the pipeline buffer's pre-write
identity — instead of resolving each `externalMetadataColumnId` through to the destination's
actual `<externalMetadataColumn>` (the true target-table name/type). That's exactly the field
`ssisx`'s own `PipelineResolver.ResolveDestinationInput`/`EntityEmitter` already use, which is
why every one of the 6 flagged values was already correct.

**No code in `SSIS_RewriteValidationTool` was changed by this session** — per instruction, only
this project (`D:\PoC\SSIS`) is touched here. I drafted a fix and then reverted it once asked not
to touch that repo.

## Second pass — reread after the reports were regenerated, now fully clean

Rereading all 15 files: every one of the 6 `Mismatch` findings above is now resolved —
`OLEDST_Drip`/`ADO_DST_ExportLog`/`OLEDST_RegionSummary`/`OLEDST_CustomerMerged`/
`OLEDST_CustomerWithContact` are now `Match`, and `OLEDST_CustomerEnriched` is `NormalizedMatch`
(its `SignupDate` is `dbDate` → nullable `DateOnly?`, the same accepted modern-equivalent
normalization already used elsewhere). All read the correct `i4`/`int` (or `int?`) throughout —
consistent with what the first pass already established was the correct value. The validator's
own schema-resolution issue was evidently fixed independently of this session (not by me — the
sibling repo was never touched here).

Current state, all 15 reports:

- **Gate 1 (SQL/schema parity)** — clean across all 5 packages. Every SQL statement text-matches
  or normalized-matches (an expression-driven statement's literal-segment template, or a
  content-correlated `OleDbCommandStep`). Two Script Components' embedded SQL is `Unverifiable`
  by design (not extractable as text from the outer XML).
- **Gate 2 (control-flow/data-flow graph parity)** — clean across all 5 packages. Every
  concurrency group (`Task.WhenAll`), event handler (`OnError`), disabled-task exclusion
  (`SQL_LegacyStep_DISABLED`), and ForEach-loop-absorbed child task is correctly recognized.
  Component-name-presence-only `Unverifiable` findings inside a Data Flow Task are expected —
  full data-flow-internal structural parity isn't built yet, stated as such in every report.
- **Gate 3 (schema parity)** — clean across all 5 packages. Zero `Mismatch`, zero `LeftOnly`. The
  remaining `Unverifiable` findings are all expected: pipeline-internal transforms with no
  external metadata of their own (Derived Column, Lookup, Aggregate, Sort, Merge, Conditional
  Split, Script Component, ...), and non-database destination kinds (`OLEDBCommand`,
  `FlatFileDestination`) that Gate 1's schema-correlation mechanism doesn't cover yet.

One item worth calling out: `Package.gate1.md` confirms `OLEDST_StagingErrors` (the
error-redirect destination closed in this session's own commit `853f870`) as a clean `Match` —
independent confirmation that fix is correct.

## Conclusion

Across both reads of the full 15-report validation run: **nothing to fix in `ssisx`/`Etl.Core`.**
Every generated shape this independent, separately-built validator checked against the real
`.dtsx` files is correct. The one real defect found (in the validator's own Left-side reader) has
since been corrected elsewhere, outside this session's scope.
