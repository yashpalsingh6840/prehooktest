# Client SSIS portfolio — migration assets

Working files for the client SSIS-to-C# (.NET 10) migration. Unrelated to this repo's own
PoC packages under `SSIS/`.

## Files

| File | What it is |
|---|---|
| `ssis-fidelity-checklist.html` | 145-item fidelity checklist. Open in a browser; ticks save to that browser's local storage. |
| `ssis_scan.py` | Offline portfolio scanner. Pure XML, stdlib only — **no SSIS, SQL Server or Visual Studio needed**, so it can run inside a locked-down client environment. |

Artifact copy of the checklist:
<https://claude.ai/code/artifact/54080e0a-6d9b-4e39-9caa-33acbe5a14d6>

## Running the scanner

```
python ssis_scan.py <root-folder> [-c ssis_scan.csv] [-s scripts_out]
```

Point it at **one** canonical folder. Scanning a tree that contains repo copies or git
worktrees inflates every metric — the client's 216 files on disk are 73 real packages.

Outputs: a console report (the part worth screenshotting), one tidy CSV filterable by its
`category` column, and every script's real source extracted under `scripts_out/`.

Verified 2026-09-02 against a test corpus built with deliberate duplicates and drift. Two
real bugs were found and fixed during that verification:

- a Script Component `SourceCode` array whose element count divides by 3 but is *not*
  (name, encoding, content) triples — a line of source code was being used as a filename;
- the extension-less `Project` MSBuild descriptor being counted as ~40 lines of authored
  code, inflating the LOC figure for every Script Task.

## Scan results (client portfolio, 2026-09-02)

| Metric | Value |
|---|---|
| .dtsx files on disk | 216 |
| **Distinct packages** | **73** (143 were duplicate copies) |
| Data flow tasks | 101 |
| Distinct component types | 24 reported → **~20 real** (see caveat) |
| Distinct task types | 9 reported → likely ~5–6 real (same caveat) |
| Script units | 38 — C# and VB.NET, roughly half each |
| Distinct script bodies | 135 files, ~8,750 authored LOC |
| Script source missing | **0** — all recoverable from the XML |
| Third-party / custom components | **0** — all stock Microsoft |
| Packages using `TransactionOption=Required` | **0** — per-task autocommit throughout |
| Components per flow | median 3, p90 8, max 21 |
| Protection levels | 51 `DontSaveSensitive`, 22 absent (verify) |

### Known caveat in the reported counts

`ssis_scan.py` folds legacy ProgID spellings (`DTSTransform.*`, `DTSAdapter.*`) into their
canonical `Microsoft.*` ids, but does **not** yet fold CLSID-resolved friendly names. So
"Flat File Source", "Flat File Destination", "OLE DB Source" and "OLE DB Destination"
appear as four extra entries alongside their `Microsoft.*` equivalents. Subtract those
four. The same gap likely inflates the task-type count.

### Component types confirmed present

OLEDBSource, OLEDBDestination, FlatFileSource, FlatFileDestination, ADONETDestination,
DataReaderSourceAdapter (ADO NET Source), ExcelSource, DerivedColumn, DataConvert,
ConditionalSplit, Multicast, Lookup, Aggregate, Sort, Merge, MergeJoin, UnionAll,
RowCount, OLEDBCommand, ScriptComponentHost.

### Task types confirmed present

ExecuteSQLTask, ScriptTask, FileSystemTask, Pipeline, Package. Some appear in both short
(`Microsoft.Pipeline`) and aliased or assembly-qualified spellings (`SSIS.Pipeline.3`).

The console list was truncated when captured, so **Sequence Container, ForEach Loop
Container and Execute Package Task are neither confirmed present nor absent** — check the
CSV before sizing control-flow work.

## Open items

1. **Probe VM** — SQL Server Developer Edition + VS Community + the SSIS projects
   extension. Both free. This blocks every "Measure" item on the checklist. Probes run on
   synthetic 5-row packages written for the purpose, so no client data or data-governance
   approval is involved.
2. **7 drifted packages** need a written call on which copy is production-current:
   `balance`, `cpc2n600`, `cuvimport`, `fxrate_import`, `n600tosalesforce`, `position`,
   `robocashflow`.
3. **22 packages** with an absent `ProtectionLevel` — if any resolve to
   `EncryptSensitiveWithUserKey` they cannot be opened or built off the original author's
   Windows account.
4. **5 script units** with a non-standard `SourceCode` layout need a human to confirm the
   extracted source is coherent rather than fragmented: `CUVImport` (×2), `CPC2N600`,
   `PracticeAccBalance`, `PracticeAccHoldings`.
5. **Shadow / parallel-run window** — still unanswered, and the largest residual
   programme risk.
