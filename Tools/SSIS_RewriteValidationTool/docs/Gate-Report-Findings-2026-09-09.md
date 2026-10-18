# Gate report findings — 2026-09-09

Reviewed all 15 reports in `D:\PoC\SSIS_RewriteValidationTool\gate_reports` (5 packages ×
3 gates, from the `RewriteValidator` tool described in `Docs/SSIS-Rewrite-Validation-Plan.md`)
against `SSIS_From_Sandeep`'s real `.dtsx` files and the freshly-regenerated rewrite at
`D:\tmp\Sandeep_Packages\gen\generate`.

**Bottom line: zero real bugs found in `ssisx`/`Etl.Core`.** Every value the reports flagged as
wrong is actually correct — traced by hand against the real `.dtsx` XML, not assumed. The one
real, verified defect found is in the validator tool itself (`SSIS_RewriteValidationTool`, its
own separate repo) — **not touched**, per instruction; described below for whoever picks it up.

## Gate 1 (SQL/schema parity) and Gate 2 (control-flow/data-flow graph parity)

Clean across all 5 packages — every SQL statement text-matches or normalized-matches, every
concurrency group (`Task.WhenAll`), event handler (`OnError`), disabled-task exclusion, and
ForEach-loop-absorbed child task is correctly recognized. Nothing to report.

One item worth calling out: `Package.gate1.md` confirms `OLEDST_StagingErrors` (the
error-redirect destination closed in this session's own commit `853f870`) as a clean `Match` —
independent confirmation that fix is correct.

## Gate 3 (per-component input/output schema parity) — 6 "Mismatch" verdicts, all false positives

| Package | Component | What Gate 3 reported | What's actually true |
|---|---|---|---|
| `Package_Advanced` | `OLEDST_Drip` (`DripEligibility.CustomerID`) | SSIS `r8` expects `Double`, generated is `int` | Destination's own `<externalMetadataColumn dataType="i4">` — `ssisx`'s `int` matches the real target column. `r8` is only the Excel source's pre-write buffer type. |
| `Package_Exports` | `ADO_DST_ExportLog` (`CustomerExportLog.CustomerID`) | SSIS `wstr` expects `String`, generated is `int?` | Destination's own `<externalMetadataColumn dataType="i4">` — `ssisx`'s `int?` matches. `wstr` is only the ADO.NET source's pre-write buffer type. |
| `Package_Transforms` | `OLEDST_RegionSummary` (`RegionSummary.CustomerCount`) | SSIS `ui8` expects `UInt64`, generated is `int` | Destination's own `<externalMetadataColumn dataType="i4">` — `ssisx`'s `int` matches. `ui8` is only `AGG_ByRegion`'s `Count()` output buffer type. |
| `Package_Transforms` | `OLEDST_CustomerEnriched` | `CustomerID_i4`/`SignupDate_dt` "declared on the left, no matching generated property"; `CustomerID`/`SignupDate` "no matching left column" | `CustomerID_i4` is `DCONV_Types`'s own renamed pipeline-buffer column; its `externalMetadataColumnId` resolves to the real destination column `CustomerID`. `ssisx` correctly names its property after the real column. |
| `Package_Transforms` | `OLEDST_CustomerMerged` | Same shape, `CustomerID_i4` vs `CustomerID` | Same cause. |
| `Package_Transforms` | `OLEDST_CustomerWithContact` | Same shape, `CustomerID_i4` vs `CustomerID` | Same cause. |

### Root cause (in the validator, not in `ssisx`)

`ssis_validator/dtsx/component_schema.py`'s `_extract_mapped_input_columns` builds a
destination's "declared shape" from each mapped `<inputColumn>`'s own `cachedName`/
`cachedDataType` — the **pipeline buffer's pre-write** name/type. It should instead resolve each
`<inputColumn>`'s `externalMetadataColumnId` through to the matching `<externalMetadataColumn>`
element and use **that** element's `name`/`dataType` — the destination's actual declared
target-table identity. This is exactly the field `ssisx`'s own `PipelineResolver.
ResolveDestinationInput`/`EntityEmitter` already use, which is why every one of the 6 flagged
values is actually correct.

One field-selection bug produces two symptoms:
- A **type** mismatch whenever `ssisx` performs an implicit-coercion translation that the real
  OLE DB/ADO.NET provider also performs at insert time (Excel `r8`→`i4`, ADO.NET `wstr`→`i4`,
  Aggregate `ui8`→`i4` — all three deliberately built and dtexec-verified in earlier sessions,
  per `CLAUDE.md`'s own numeric-passthrough-coercion history).
- A **name** mismatch whenever a Data Conversion component upstream renames a column with SSIS's
  own default type-suffix convention (`CustomerID` → `CustomerID_i4`).

Worth noting: the name-mismatch variant was already flagged as a known, deliberately-deferred
limitation in `ssis_validator/compare/gate3_schema.py`'s own docstring ("an honest but imprecise
degradation, not yet worth a heuristic fix from a single observed case"). It's now been observed
6/6 times across a real portfolio run (4 name-mismatch instances plus 2 related type-mismatch
instances from the same root field-selection bug) — enough evidence to justify fixing for real,
if/when someone picks up work on that tool.

### Suggested fix, if this gets picked up later

In `_extract_mapped_input_columns` (`component_schema.py`): for each mapped `<inputColumn>`,
resolve its `externalMetadataColumnId` to the matching `<externalMetadataColumn>` (already
present in the same `<input>` element, keyed by `refId`) and use that element's `name`/`dataType`
instead of the `<inputColumn>`'s own `cachedName`/`cachedDataType`. This preserves the existing,
correct design intent (only compare the genuinely-*mapped* subset of external columns, excluding
DB-defaulted/unmapped ones like `ErrorRowID` or a `*At` audit timestamp) while fixing which
field's name/type gets compared. `gate3_schema.py`'s "Known naming-correlation limitation"
docstring paragraph would need updating to reflect the fix, and
`tests/test_gate3.py::test_mutated_left_column_type_is_reported_as_mismatch` would need its
mutation target changed from the `<inputColumn>`'s `cachedDataType` to the corresponding
`<externalMetadataColumn>`'s `dataType` (the field that would actually be compared post-fix) to
keep proving the mismatch-detection path still works. A real regression test could point directly
at the already-generated `D:\tmp\Sandeep_Packages\gen\generate\Package_Transforms` pair, since it
reproduces all three of the shapes above (Data Conversion rename ×2, Aggregate `ui8`→`i4` ×1) for
real.

**No code was changed in `SSIS_RewriteValidationTool` — this file is a write-up only, per
instruction.**
