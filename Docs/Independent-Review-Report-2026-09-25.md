# Independent Review Report — `ssisx` / `Etl.Core` / `svk`

**Date:** 2026-09-25 · **Reviewed state:** branch `phase0-extractor` at `41fb2ca` **plus 41 uncommitted
working-tree changes** (35 files, +643/−147 per `git diff --stat`). Findings describe that working
tree, not a committed version.

**Method.** Tool binaries were built once (0 warnings/0 errors) and frozen under
`D:\tmp\independent-review-2026-09-25\bin\`. Five independent review passes then ran against copies
in `D:\tmp\independent-review-2026-09-25\{robustness,capabilities,packets,tests,logging-docs-security}\`.
Inputs: the real third-party portfolio `D:\PoC\SSIS_Packages\SSIS_From_Sandeep\RBC_Demo_ETL\RBC_Demo_ETL`
(5 packages plus a `.WORKING` duplicate, and a sibling `_trash_20260813` folder), this repo's `SSIS\`
packages, and the ~83 fixtures under `Tools/SsisExtractor/tests/Ssis.Extract.Tests/Fixtures/`. No
database was touched and nothing was fixed.

**Evidence tiers** used on every finding: **[RUN]** = built/ran it and observed the result;
**[CODE]** = reasoned from source, not executed. Severity: **H**igh / **M**edium / **L**ow.
Sizes: XS < ½ day, S ≈ 1 day, M ≈ 2–5 days.

---

## 1. Executive summary

1. **The generated starter tests look much stronger than they are.** A mutation test planted 18
   realistic bugs in generated code. Only **4/18 (22%)** made any non-Integration test fail, and
   **0/9** on the real RBC packages. The failures that went unnoticed include inverted router
   conditions, wrong numeric coercion, swapped columns, a wrong join type and a wrong Lookup column.
   On the real packages, own-generated-code coverage is **33–64%**, not the "90%+" figure the
   project's own history quotes. **[RUN]**
2. **Three silent-wrong-output paths contradict "gaps, not guesses."**
   - A precedence-constraint **cycle** or **dangling constraint** generates reordered or
     parallelized code with no gap.
   - A Script Component that **adds no columns** is dropped with no gap.
   - A `.dtsConfig` configuration is ignored with no gap.

   All three are **[RUN]**.
3. **Identifier sanitization has a latent silent-wrong-data bug.** When two column names sanitize
   to the same identifier, declarations get the `_2` suffix but references do not. A decimal price
   column then gets fed an int ID, and it compiles. **[RUN, hand-derived fixture]**
4. **The pipeline scripts under-report and over-abort.**
   - `Run-Coverage.ps1` exits **0** when exactly one test project fails (a PowerShell 5.1 `.Count`
     quirk).
   - `Run-Pipeline.ps1` aborts the whole portfolio on one unreadable package, and treats
     `generate`'s exit 98 ("one package crashed, the rest were written") as fatal.
   - Regenerating into an existing `--out` leaves stale files that get compiled (**9 build errors**
     reproduced).

   **[RUN]**
5. **The client-facing survey script is broken.** `Run-Survey.ps1` calls `ssisx report`, which is
   now a retired verb, and crashes. It is the one script "literally designed to be handed to a
   client." Separately, `portfolio-digest.md`, which states "nothing here is client data," leaks
   connection-manager, component and worksheet names through raw gap-reason text. **[RUN]**
6. **`ssisx extract` stops halfway on a harmless duplicate-name skip** (exit 2), so
   `lineage/`, `conformance/` and `testgen/` are never written. The real RBC project hits this
   every run, because SSDT's default `Package` name collides. **[RUN]**
7. **About 67% of work-packet bytes are duplicated.** 16 of the 26 packets (every ENCRYPTED-SECRET
   and LOCAL-DATA packet) need no AI reasoning at all: `svk sampledata` already produces
   schema-exact LOCAL-DATA files, and only the file names differ. The biggest token lever is not in
   the packets, though. It is the **24.6K-char `copilot-instructions.md` sent with every Copilot
   turn**, which over a session costs about 5× the whole packet corpus. **[RUN]**
8. **The work packets are not actually self-contained.**
   - Script-seam packets tell the AI to omit `using` lines and namespaces, but `apply-fills` copies
     the file byte-for-byte, so an answer that follows the packet literally does not compile.
   - The fixed-width LOCAL-DATA packet says there is no header, when the source skips one row, so a
     literal answer silently loses its first data row.
   - A Conditional Split TEST-ORACLE packet names the wrong input column and type.

   **[RUN]**
9. **The readiness headline is misleading.** RBC shows "0 of 5 generatable," but for 4 of those 5
   the only blockers are encrypted-password acknowledgements. `extract`'s readiness report also
   runs with seams **off**, so Script Tasks read as "not supported" when `generate` actually emits
   fillable seams for them. **[RUN]**
10. **Solid foundations.**
    - All 840 tests in the main suite pass, and 997 pass via `Run-Tests.ps1`. The long-running
      "2 pre-existing golden failures" are gone.
    - All generated code in scope builds with 0/0.
    - Extraction is fast and linear: 50 × 341 KB packages in 5.5 s.
    - Malformed input fails loudly and cleanly.
    - `apply-fills` refuses stale fills and orphans with clear reasons.

---

## 2. Findings by area

### 2.1 Robustness — extractor and generator

| # | Finding | Evidence | Sev | Recommendation |
|---|---|---|---|---|
| R1 | **Precedence cycles and dangling constraints are not gapped** (details below). | [RUN] | **H** | Emit a blocking planner gap on `HasCycle`, and an extract finding plus a gap for an unresolved constraint endpoint. **S** |
| R2 | **Stale output survives a regenerate into the same `--out`** (details below). | [RUN] | **H** | Before writing a package, clear its `generate/<Pkg>/`, `<Pkg>.Tests/` and `gaps/<Pkg>/`, preserving `Fills/`/`TestData/`. Or write a manifest and prune against it. Warn about folders the run does not cover. **S–M** |
| R3 | **One bad package aborts `Run-Pipeline.ps1`** (details below). | [RUN]/[CODE] | **H** | Warn and continue on package-level failures; abort only if zero packages loaded. Treat 98 like 3, loudly. **S** |
| R4 | **`Run-Coverage.ps1` exits 0 with one failing test project.** Line 183 is `($rows \| Where-Object {…}).Count -gt 0`. With a single match, PowerShell 5.1 returns an object with no `.Count`, so the test is `$null -gt 0`, which is false. Reproduced on a clean RBC run: the report shows `FAIL (exit 1)`, but the script exit is 0. | [RUN] (spot-checked) | **H** | `@(...)` wrap; audit the other `.Count` uses. **XS** |
| R5 | **Duplicate `ObjectName`: the winner is chosen by alphabetical path order.** In a loose folder, `Package.Scripts.WORKING.dtsx` beats the real `Package.dtsx`. A warning is printed, but the wrong package gets generated (`PackageLoader.cs:132-153, 221-225`). | [RUN] | M | Prefer the file the `.dtproj` claims, then the newest write time; state the rule in the warning. **S** |
| R6 | **Exit codes don't match the documented contract** (list below). | [RUN] | M | Reserve 3 for "blocking gaps, files written"; add a distinct "nothing written" code; hard-fail missing paths; document 98/99. **S** |
| R7 | **Missing or corrupt `.conmgr`.** A missing one is silent (extract exits 0; acknowledged in a `DtprojReader.cs:102` comment). A corrupt one throws from `XDocument.Load` (`:104`); the error blames the `.dtproj` at "Line 1, position 1", and the tool falls back to loose `.dtsx` files, losing project context. | [RUN] | M | Record a per-file load failure naming the `.conmgr`. **XS–S** |
| R8 | **Output is not atomic.** Killing a first run leaves an obviously broken tree, which is fine. Over a previous run, however, the old `generate-report.md`/`Generated.slnx`/`gaps.json` survive next to partially rewritten packages and look complete (`GenerateCommand.cs:207-244`). | [RUN] partial / [CODE] | M | Delete the summary files first and write them last, or stage into a temp folder and swap. **S–M** |
| R9 | **A Boolean variable defaulting to True would be seeded as false.** SSIS persists True as `-1` (`Package_Advanced.dtsx:807`); `PackagePlanner.cs:1425-1426` only accepts `"True"`. This is latent today because boolean guards are still gapped. | [CODE] | L (latent H) | Accept `-1`/`1`/`True`. **XS** |
| R10 | **Other small items.** `IntegralGuard` parses Int16/Int32 via `long.TryParse` with no range check (`PackagePlanner.cs:1431`). A missing `SamplingValue` defaults to 0% sampling, dropping every row with no gap (`PipelineReader.cs:440`). `Run-Pipeline`'s `dotnet build` lacks `-nodeReuse:false` (`:244,247`), the exact pipe-handle hang this repo already fixed once in its own test suite. `Run-Coverage` may show stale percentages next to a FAIL (`:119`). | [CODE] | L | **XS** each. |

**R1 detail.**
- **Cycle** (`DFT_FixedWidthImport ↔ SQL_AtomicSwap`): extract exits 0 and the spec records
  `Dag.HasCycle=true`, but nothing in `PackagePlanner.cs` reads `HasCycle`. The planner falls back
  to declaration order (`PackagePlanner.cs:1672-1674`), so the generated code runs the two tasks in
  **reverse** of the real order.
- **Stale comment:** `PackagePlanner.cs:1628` says no generation ever reaches a cyclic container.
  That is false.
- **Dangling constraint** (`To="Package\NoSuchTask"`): the edge is dropped silently. The package
  turns from sequential into a `Task.WhenAll` concurrent wave with a transaction per branch, which
  is a real semantic change.

**R2 detail.**
- Generate `Package_Transforms`, disable one Data Flow Task, then regenerate into the same `--out`:
  **9 compile errors** from a stale `Mapping\DER_Enrich.cs`. A fresh `--out` builds 0/0.
- `generate --package Package_Legacy` into a full `--out` leaves the other four package folders and
  their `gaps\` packets on disk. Only `Generated.slnx`/`gaps.json` are narrowed.
- Run standalone, `Run-Coverage` discovers projects by folder, so it tests those stale projects.

**R3 detail.**
- A truncated `Package_Advanced.dtsx` in an otherwise good project makes step 1 abort
  (`Run-Pipeline.ps1:180-209`). No summary is printed.
- Load-failure kind `package` also covers the common case where the `.dtproj` lists a `.dtsx` that
  is missing on disk.
- Sibling problem: `generate` exits **98** when one package crashes but the rest were written
  (`GenerateCommand.cs:273`), and `Run-Pipeline.ps1:226` treats anything other than 0/3 as fatal.
  The crashed package is also missing from `generate-report.md`.

**R6 detail.**
- `generate` returns 3 with **0 files written** on 12 of the 83 fixtures (e.g.
  `SyntheticAggregateThenOleDbCommand`). The handover doc says 3 means "code was written".
- Exit 0 is effectively unreachable, because the always-present `.Notification` advisory triggers 3
  on all 83 fixtures and every real package.
- `extract` exits 2 on a benign duplicate skip (documented meaning: "you typed something wrong").
- `--package A,Nope` gives 3 from `generate` but 2 from `extract`.
- `apply-fills --fills <typo>` exits 3 with no "folder missing" message.
- `apply-tests` against a nonexistent `--out` exits 0 and creates the folder.
- A bad `--etl-core` path produces only an advisory, and `Generated.slnx` still references a
  missing `Etl.Core`.
- Exit codes 98/99 are undocumented in `Tool-Handover-Overview.md` §2.

**Silent degradation (the "gaps, not guesses" counterexamples):** R1, and in §2.2 C2 and C3. A
precedence cycle, a column-less Script Component and a `.dtsConfig` each produce complete-looking,
wrong output. Most other `??` defaults in Codegen match SSIS's own schema defaults (checked:
`HeaderRowsToSkip`, `OverwriteDestination`, `FailOnFixedAttributeChange`, `Recurse`), and expression
parse failures degrade to gaps correctly.

### 2.2 Extractor and walkthrough (`svk`) capabilities

| # | Finding | Evidence | Sev | Recommendation |
|---|---|---|---|---|
| C1 | **The handover support table (§6) is stale in five places** (table below). | [CODE] + [RUN] | M | Update the doc and the one stale message. **XS** |
| C2 | **A Script Component that adds no columns is dropped silently.** `SyntheticScriptComponent.dtsx` generated 13 files and no gap. The script's side effects (variables, logging, FireError, external calls) vanish, and the package counts as generatable. Cause: `TransformEmitter.cs:340-364` only gaps an unknown component that declares output or read-write columns. Balanced Data Distributor and other column-less synchronous components hit the same path. | [RUN] | **H** | One blocking gap per unrecognized component, whatever its columns; a Tier-2 seam for a column-less Script Component. **S** |
| C3 | **`Package_Legacy`'s `.dtsConfig` (`ConfigurationType=1`) is ignored by `generate` with no gap.** `extract` reports it only as a Warning finding. | [RUN] | M | One gap per `DTS:Configuration`. **S** |
| C4 | **An unknown row-count-changing component (e.g. Unpivot) gets per-column gaps only.** The flow is still wired as if rows pass through 1:1, and nothing says row semantics are lost. | [RUN] | L | Make the gap per-component. **XS** |
| C5 | **The walkthrough shows structure but not logic** (list below). | [RUN] | M–H | Five small fixes, **XS–S** each. |
| C6 | **`sampledata` exercises almost none of the real branches** (details below). | [RUN] | **H** for its stated purpose | A deterministic biasing design, below. **~2–3 days** |
| C7 | **Nothing helps prioritize a portfolio** (list below). | [RUN] | M–H | One ranked table: Package / Tier-3 / Tier-2 seams / Tier-1 / prerequisites / complexity, sorted. **S**. Use `emitSeams: true` in readiness (**XS**). |

**C1 detail.**

| Handover says | Reality |
|---|---|
| Character Map is supported | It is a Tier-3 gap (`TransformEmitter.cs:358`; `cmap\gen\gaps.json`). |
| Completion and Expression-only constraints are "not yet auto-translated" | They are translated (dual-position handlers, `PackagePlanner.cs:942-1002`), within a narrow shape. |
| Aggregate supports Count only | AggregationType 1–7 are supported: Count, CountAll, CountDistinct, Sum, Avg, Min, Max (`PackagePlanner.cs:2765-2804`). |
| Excel SqlCommand mode is "whole sheet" only | A simple AND-ed WHERE clause also works (`ExcelWhereClauseTranslator.cs`). |
| Not mentioned | For Loop, Copy Map, Percentage Sampling and XML Source are supported. |

Also stale: the generic unsupported-task message (`PackagePlanner.cs:1869`) still says only Data
Flow, Execute SQL and File System tasks are supported, and it is shown for Script Tasks that are
actually fillable seams.

**C5 detail.** Package_Transforms produces a 20 KB, 183-line walkthrough. Control-flow order, the
parallelism callout and the Merge Join details are accurate, but:
- The Detail column is **empty for all four Derived Columns**, so EmailDomain/TenureDays logic is
  invisible. The expressions are already in the spec (**XS**).
- Data-flow arrows are labelled with column counts (several say `"0 cols"`) instead of output
  names, so you can't tell which of Valid/Invalid or Match/No-Match feeds what (**XS**).
- The `OnError` event handler is missing from the Package_Advanced walkthrough.
- The ForEach loop shows no enumerator, and truncates the expression-driven SQL.
- There is no "at a glance" risk header (dead-end RowCounts, coercions, gaps).
- The same boilerplate paragraph repeats four times.

**C6 detail.**
- Across the 5 Package_Transforms CSVs, 20 rows each: **0** numeric CustomerIDs, **0** emails
  containing '@', **0** rows with `Country='Canada'`, **0** parseable dates.
- So the Valid and Canada branches get **0 rows**. The INNER Merge Join joins on an all-NULL key and
  produces 0 rows. TenureDays is always −1 and EmailDomain is always "(none)".
- The Lookup's ~70% match does hold (14/20).
- CSV and SQL output is deterministic. The `.xlsx` is **not** byte-identical across runs (zip
  timestamps and a random `.psmdcp` part name), which the doc overstates.
- A SQL-source sample creates a table named after the component (`[OLESRC_CustomerIds]`) instead of
  `dbo.Orders`.

**C7 detail.**
- The headline "0 of 5 generatable" is driven by 8 `EncryptedConnectionManagerSecret` gaps. Clear
  them and **4/5 are ready**; only `Package` has real work.
- `inventory.md` is alphabetical.
- `generation-readiness.md` interleaves blocking and non-blocking items.
- Readiness is computed with seams **off** (`ReportCommand.cs:136`), so Script Tasks show as "not
  supported by this planner yet — re-run with --seams". In fact `generate` emits seams by default.
- The digest groups ADO NET and Script Components together under `ManagedComponentHost`.
- The `--out` root holds 23 loose files.

**Proposed deterministic `sampledata` biasing (no AI, stays seeded):**
- **Parse targets.** When a text column feeds a Data Conversion or cast, emit values that parse as
  the target type, plus about 1 in 10 deliberately unparseable (½ d).
- **Literal harvest.** Parse Conditional Split and ternary conditions with the existing
  `Ssis.Runtime.Expressions`, collect literals and comparison operands, and seed one satisfying and
  one non-satisfying row per case, plus c−1/c/c+1 boundaries, TRIM whitespace variants and NULLs
  (1 d).
- **Routing check.** Evaluate each row through the existing evaluator and write a "rows per output"
  table, warning on 0 (½ d).
- **Shared key pools.** Reuse the Lookup's shared key pool for Merge Join/Merge inputs (½ d).

### 2.3 `apply-fills` metadata — Tier-1/2 work packets

Measured on RBC with `generate` (seams on, the default): **42 gaps → 26 packets, 134,098 chars
(≈33.5K tokens)**. Evidence is only **26%** of that; **74%** is fixed contract/preamble text. After
line-level dedup only **44,302 chars (33%)** are unique, so **67% is duplicated across packets**.
**[RUN]**

| Kind | N | chars | ≈tok | evidence | fixed | unique |
|---|---|---|---|---|---|---|
| ENCRYPTED-SECRET | 8 | 25,112 | 6,280 | 3,128 | 21,984 | 6,111 |
| LOCAL-DATA | 8 | 21,079 | 5,270 | 4,344 | 16,735 | 7,940 |
| TEST-ORACLE | 6 | 51,829 | 12,960 | 14,780 | 37,049 | 21,252 |
| SCRIPT-TASK | 2 | 19,835 | 4,960 | 7,403 | 12,432 | 13,917 |
| SCRIPT-COLUMN | 2 | 16,243 | 4,060 | 5,231 | 11,012 | 10,403 |

| # | Finding | Evidence | Sev | Recommendation |
|---|---|---|---|---|
| P1 | **A literal packet answer doesn't compile.** Script packets say both "add your own `using` lines" and "no `using` directives (the generated file already has them)". The Script Component template is a bare method with no namespace or class, and claims the rest "is filled in for you when applied". It isn't: `apply-fills` copies byte-for-byte (`ApplyFillsCommand.cs:395`, `cmp` identical). `ssisx-fill.prompt.md` papers over this by telling the agent to grep the generated `.cs`, which shows the packet can't be answered on its own. Each literal answer costs an extra build/retry cycle. | [RUN]+[CODE] | **H** | Emit the real namespace, class and row-type `using`s into the template (all known at generation time); delete the contradictory sentence. **XS–S** |
| P2 | **The Tier-1 Lookup packet (SyntheticLookupSplit) lists no upstream columns to join on** (input and added columns both `_(none)_`), so answering it requires opening the `.dtsx` or spec. | [RUN] | M | Add the upstream component's output columns to the evidence. **XS** |
| P3 | **ENCRYPTED-SECRET: 8 packets (≈6.3K tokens) each say "nothing to propose or write."** 6 of the 8 share one connection. | [RUN] | M | Replace them with one portfolio-level human checklist table (≈0.4K tokens), outside the AI packet stream. **XS** |
| P4 | **Boilerplate repeated per packet:** the SSIS-semantics block (1,458 chars) in every Script packet, even though it says it doesn't apply to code already written in C#; the pinned testing-API block (2,046 chars) in every TEST-ORACLE packet; the all-five-shapes list (2,311 chars) regardless of which shape applies. | [RUN] | M | Emit only the applicable shape; drop the semantics block for C# sources. Saves ≈4.3K tokens per portfolio. **XS** |
| P5 | **`apply-fills` diagnostics** (tested with deliberately broken fills; table below). | [RUN] | M (case) / L | Separate the "applied" and "expected" key sets by exact name and flag case mismatches; track LOCAL-DATA per GapId; add "did you mean" nearest-match hints; preserve relative paths or fail on flattening collisions. **S** |
| P6 | **The pinned testing API block has drifted** (details below). | [CODE] | M | Generate the prompt's block from the same constant, or reference it. **XS** |

**P5 detail.**

| Broken fill | Result |
|---|---|
| Wrong `EvidenceSha256` | **STALE**, both hashes shown, whole file refused. Clear. |
| Fill nested one directory deep | Found and applied. Clear. |
| Missing `partial` | Good per-file message, but the summary mislabels it "Orphaned — implements a seam no gap asked for" and doesn't point at the unfilled seam of the same class. |
| Wrong method name | ORPHANED, with no nearest-match hint. |
| TEST-ORACLE fill with no provenance | The summary wrongly says the GapId "did not match". |
| **Wrong-case data file** (`customers.csv` vs `Customers.csv`) | Reported **APPLIED** *and* "still open". The expected-name lookup ignores case (`ApplyFillsCommand.cs:217`), the applied set doesn't (`:248, :566`). The file is copied with the wrong case, which breaks on a case-sensitive filesystem. |
| Four LOCAL-DATA gaps sharing `Customers.csv` | Keyed by filename (`:218` overwrites), so 3 of the 4 drop out of tracking. The summary said "1 applied, 5 still open" for 8 gaps. |
| Two same-named nested fills (latent, [CODE]) | Nested fills are flattened to their bare file name on copy, so the two would overwrite each other silently. |

**P6 detail.** The packet's pinned block and `COPILOT_TESTING_GUIDE.md` agree with each other, but
both omit `NewRealUnitOfWorkAsync()` (needed by the SQL Integration test), the
`useSampleDataForFileSources` constructor option, and the `ThrowOnGetBindToken` /
`ThrowOnSqlWithoutTransactionContaining` hooks. Worse, `ssisx-test.prompt.md:48-60` claims to be
"the same pinned block every packet embeds" but lists a nonexistent `ThrowOnStep`, and says
`FileSourceOptions -> SampleData/`, which has been wrong since TestData became the default.

**Does the log help diagnose a failed fill?** Mostly yes. The console and `apply-fills.log` agree,
and the stale/nested/missing-`partial` cases are explained per file. The remaining problems are the
summary-level mislabels and the case/collision bugs above.

**Batching estimate:**

| Scenario | Today | Batched | Saving |
|---|---|---|---|
| Package_Transforms LOCAL-DATA (5 packets) | ≈3.4K tokens, 5 answers | one table, ≈0.5K | ≈2.9K tokens and 4 turns, or 100% via `svk` (§2.5) |
| `Package`'s 4 Script TEST-ORACLEs | ≈9.6K tokens | contract once, evidence already in context ≈1.6K | ≈8K tokens |
| ENCRYPTED-SECRET ×8 | ≈6.3K tokens | one checklist, ≈0.4K | ≈5.9K tokens |

### 2.4 Generated unit tests — quantity and strength

**Coverage (`Run-Coverage.ps1`, non-Integration tests) [RUN]:**

| Package | Line | Branch | **Own generated code** | Etl.Core |
|---|---|---|---|---|
| LoadEmployees | 22.5% | 10.4% | **92.2%** | 14.4% |
| Package_Advanced | 19.1% | 6.4% | **41.4%** | 15.0% |
| Package_Exports | 15.7% | 8.4% | **32.9%** | 12.4% |
| Package_Legacy | 22.1% | 10.3% | **64.3%** | 16.6% |
| Package_Transforms | 33.1% | 20.0% | **59.6%** | 22.9% |

The "90%+ own-code" figure only holds for CSV-sourced packages that get the fakes-only happy-path
run. On the real packages these have **0%** coverage:
- every transform class (e.g. `DripEligibilityTransform` 0/6, `CustomerExportLogTransform` 0/5)
- every SQL row reader
- every Data Flow step method
- `SsisFn`
- **`RunBranchAsync` (0/11)**. The concurrent-branch path is exercised by no test anywhere,
  including the concurrency fixture `SyntheticFlatFileDestination`.

`RunAsync` is about 50% covered, because no SQL- or Excel-sourced package gets a happy-path test.

**Mutation test (18 hand-planted bugs in generated code, non-Integration suite) [RUN]:**

| Bug | Caught? |
|---|---|
| LoadEmployees: swap FirstName/LastName in FullName | no |
| FullName width 101→14 | no |
| SUBSTRING(…,1,3)→(1,4) | **yes** |
| Drop `Salary = row.Salary` | **yes** |
| TRUNCATE→DELETE FROM | **yes** (Statement test) |
| CSV map FirstName→"LastName" header | no |
| Remove final `CommitAsync` | **yes** (happy path) |
| Router "Canada"→"Canadx" | no |
| Router `> 0`→`>= 0` | no |
| `NarrowR8ToI4` → plain truncation | no |
| `ParseWstrToI4(...)` → 0 | no |
| Fixed-width width 30→3 | no |
| Merge Join mapper ContactPhone←PreferredChannel | no |
| `NarrowI8ToI4(count)` → 0 | no |
| `MergeJoinType.Inner`→`LeftOuter` | no |
| Router: drop TRIM | no |
| Aggregate `Count(non-null)`→`Count()` | no |
| Lookup `CountryCode ← Region` | no |

Kill rate: **4/18 (22%)**. LoadEmployees 4/7, real RBC packages **0/9**, fixtures 0/2.

| # | Finding | Evidence | Sev | Recommendation |
|---|---|---|---|---|
| T1 | **Coercion-only transforms get no test and no advisory.** `TransformTestEmitter.cs:106` returns empty when there is no Derived Column or Data Conversion. That leaves exactly the dtexec-measured semantics untested: `NarrowR8ToI4`, `ParseWstrToI4`, the flat-file `ToString()` conversions, `NarrowI8ToI4`. | [RUN]+[CODE] | **H** | Emit literal-value tests from the measured rules (`NarrowR8ToI4(2.5)==2`, `ParseWstrToI4(" 12.5 ")==12`); at minimum an advisory. **S** |
| T2 | **A plain Lookup join gets no test and no advisory** (SyntheticLookupSingle). The advisory at `PackageGenerator.cs:3321` only fires when a Derived Column or Data Conversion is present. The previously worried-about gap was the positive case; the real hole is this negative case. Multicast branches with no Derived Column have the same early return. | [RUN] | **H** | A test seeding a fake cache with two distinct reference columns. **S–M** |
| T3 | **Assertions are weak by construction** (list below). | [RUN] | **H** | Deterministic strengthening, below. |
| T4 | **Router tests cover only the default branch** (`CSPLIT_ByCountry` → RestOfWorld). The "Canada" case and every boundary are untested. Non-string Derived Column outputs (e.g. `TenureDays`) are skipped with an unclassified gap and no packet (`TransformTestEmitter.cs:229`), even though the oracle evaluator could compute them. | [RUN] | M | One row per case, plus `==literal` boundary rows, verified via the oracle. **M** |
| T5 | **Still open as documented:** Union, ForEach-over-Data-Flow, and Lookup→Aggregate grouped on a Lookup column are advisory only. Merge Join has a real mapper test. | [RUN] | L | — |

**T3 detail.**
- Every string column gets the literal `"Sample"`, so column swaps are invisible.
- The Aggregate test **re-implements** the production key selector and count inside the test, so it
  cannot catch a production bug (the Count mutation proves this).
- `FakeUnitOfWork.BulkInsertCall` records no row values.
- The flat-file sink test checks only that the file exists and its line count.
- The step tests compare `BuildStatement()` to `ExecutedSql`, i.e. production against production.
- The "StepDataFlow" tests rebuild a fresh `DataFlowStep` from parts instead of calling `DFT_X()`,
  because `_load` is set only inside `RunAsync`. A wiring bug inside `DFT_X()` is therefore
  undetectable by design.
- One Valid-branch test row has an email with no "@".

**Raising strength deterministically (no TEST-ORACLE needed):**

| Change | Catches | Size |
|---|---|---|
| (a) A distinct literal per column (`"FirstName_1"`) instead of "Sample" | swaps | ½ d |
| (b) Coercion tests from the measured rules (T1) | coercion bugs | 1 d |
| (c) Compute the exact expected padded flat-file line | width bugs | ½ d |
| (d) Call production for Aggregate/Lookup: expose the key selector and projection, or run via a recording stub source/sink | Count and Lookup bugs | 1–2 d |
| (e) One row per router case plus boundaries, oracle-checked | router bugs | 2–3 d |
| (f) Multiple rows per Derived Column (NULL, width, width+1 which should throw, non-string outputs) via the oracle engine | width and expression bugs | 2–3 d |
| (g) Make step methods callable (inject `LoadContext` instead of `_load = null!`), plus a recording `FakeBulkSink` | wiring bugs | 2 d |
| (h) A Merge Join test with unmatched left and right rows | join-type bugs | 1 d |

Genuinely TEST-ORACLE work remains the Script seams and realistic-data expectations for
Integration reads.

### 2.5 Test-gap metadata — TEST-ORACLE and LOCAL-DATA

| # | Finding | Evidence | Sev | Recommendation |
|---|---|---|---|---|
| G1 | **`svk sampledata` already closes all 8 RBC LOCAL-DATA gaps mechanically** (details below). | [RUN] | **H** (value) | A deterministic step (`svk sampledata` → rename per `ExpectedFileName` → `fills/<Pkg>/TestData/`), with the realism caveat stated. **S** |
| G2 | **Fixed-width LOCAL-DATA packet says "Header row present: False"** although the connection manager skips one row. `svk` and SampleData both write a leading SKIP line. A literal answer silently loses its first data row. | [RUN] | **H** | Render `HeaderRowsToSkip` explicitly in the packet. **XS** |
| G3 | **Four Package_Transforms LOCAL-DATA packets have byte-identical schemas and the same target (`Customers.csv`)** without saying so. `CustomerContacts.csv` is read by no generated test at all. The Excel packet says an `.xlsx` "cannot be represented as text", yet `svk` already writes a valid one. | [RUN] | M | Collapse to one packet per target file; drop gaps no test consumes. **XS–S** |
| G4 | **TEST-ORACLE packets are internally inconsistent** (details below). | [RUN] | M | Correct the banner, embed the call signature, and resolve router inputs through the generated row type. **S** |
| G5 | **Each Script packet's TEST-ORACLE twin re-ships the whole script** (3.4–4.2K chars plus framing); only the contract and hash differ. Merging each pair (or referencing by GapId) saves ≈7K tokens, 21% of the corpus, and keeps "a test is a separate work item" as a separate *answer* in the same pass. | [RUN] | M | **S** |
| G6 | **`apply-tests` (MoreTests) has no staleness tracking.** Renamed members surface as compile errors, but one stale MoreTest breaks the whole `.Tests` build, taking down the always-green baseline, with nothing pointing at the `.dtsx` change. `Targets=` is parsed but never checked. | [CODE] | L–M | Check `Targets=` against the README method table at apply time and warn. **XS** |

**G1 detail.**
- `svk`'s output matches every packet's schema exactly: column order, CRLF, header, fixed widths
  (84 = 10+30+40+4), the SKIP line, and a real `.xlsx` sheet named `DripEligibility`.
- Only the names differ: `svk` names files by component, while `gaps.json` has the real
  `ExpectedFileName`.
- The Integration read test only asserts `Assert.NotNull(rows)`, so these files satisfy it.

**G4 detail.**
- The header banner says "Tier 1 … Supply one FACT … Do not write code", then asks for an xUnit
  file. LOCAL-DATA packets carry the same banner.
- The reason text tells the reader to see the TEST-ORACLE work packet, from inside that same packet.
- A Script Task test needs `package.SCR_X(uow, ct)`, but the call is omitted and the reader is sent
  to the ~3.5K-token README.
- The `CSPLIT_Validity` packet lists input `CustomerID_i4 : int`, but the generated router takes
  `DFT_DerivedAndSplitCsvRow.CustomerID : string` via `SsisFn.ToNullableI4`, so following the
  packet gives CS0117. The router class and row type aren't named.

### 2.6 Generated code — readability and testability

| # | Finding | Evidence | Sev | Recommendation |
|---|---|---|---|---|
| D1 | **Identifier collisions produce silent wrong data** (details below). CLAUDE.md records "agrees in the overwhelmingly common (non-colliding) case", but a trade-off that compiles into wrong data is not acceptable as a known limit. | [RUN] hand-derived | **H** (latent) | Thread one per-type resolver map to reference sites, or refuse collisions with a gap. **S** |
| D2 | **The method-per-component structure is readable** (`LoadEmployees.cs` 100 lines, `Program.cs` 12, names match SSIS and the `svk` numbering). The one real testability defect is `_load = null!`, set only inside `RunAsync`, which forces tests to rebuild steps instead of calling them (T3). | [RUN] | M | Pass `LoadContext` to step methods, or add a `RunStep(ctx)` seam. **M** |
| D3 | **Concurrent-package noise** (details below). | [RUN] | L | Defer `BeginAsync` until the first sequential step; use `->` in comments. **XS** |
| D4 | **Boilerplate.** CsvRow and CsvRowMap repeat column lists; Package_Transforms produces about 37 small files. Navigable, but heavy. | [RUN] | L | — |
| D5 | **Dead code.** `PackageGenerator.cs:1835` (`GenerateMergeJoinFlow`'s downstream-transform advisory) is confirmed dead: the only Merge Join plan construction, `PackagePlanner.cs:3198-3199`, hardcodes `DerivedColumn: null, DataConversion: null`. | [CODE] | L | Remove. **XS** |
| D6 | **Duplicated helper has drifted.** `PackageGenerator.FlowSourceNeedsUow` (`:1393-1405`) lacks the `LookupNoMatchFilteredFlowSource` case that `PackageClassEmitter.SourceNeedsUow` (`:482-483`) has. It isn't reachable yet (built `SyntheticLookupNoMatchIsLive` 0/0), but it compiles wrong the day a direct-invocation test is emitted for that shape. | [RUN]/[CODE] | M | Share one helper. **XS** |

**D1 detail.**
- Renaming `ID`→`LastCostPrice` so it collides with `"Last Cost Price"` gives correct declarations:
  `int LastCostPrice`, `decimal LastCostPrice_2` with `.HasColumnName("Last Cost Price")`.
- But the reference emits `LastCostPrice_2 = row.LastCostPrice`, reading the int ID into the decimal
  price. The int→decimal conversion is implicit, so it compiles.
- Cause: declaration sites use the collision-aware resolver, reference sites use the bare
  `SanitizeIdentifier`.
- (A second line, `LastCostPrice = row.Component`, used the empty-name fallback and may be an
  artifact of the hand edit.)
- Without collisions, sanitized names stay traceable: `GetOrdinal("Last Cost Price")` and
  `HasColumnName`. Entity and row properties carry no comment naming the original column.

**D3 detail.**
- `Package_Exports.cs` begins a transaction, fetches a bind token, and commits it empty before the
  parallel wave: a wasted round trip.
- "Flow 3" appears with no Flow 1/2 heading, because the concurrent wave replaces them.
- `→` in comments renders as mojibake in PowerShell 5.1 and ANSI editors (UTF-8 without BOM).

### 2.7 Logging during `generate`

| # | Finding | Evidence | Sev | Recommendation |
|---|---|---|---|---|
| L1 | **`generate.log` doesn't explain gaps.** Each package gets one line, e.g. `done (187ms, 20 file(s), 13 gap(s))` (`GenerateCommand.cs:176,183`). `Package` has 13 gaps, of which only 6 block, and the log says neither how many block nor which kinds. Everyone has to open `gaps.json`. | [RUN] | M | One line per package, e.g. `Package: 3/4 flows wired, 13 gaps = 6 blocking [ScriptTask 2, ScriptComponentColumn 2, EncryptedSecret 2] + 7 advisory — dominant: ScriptTask`. For agent context, one `unwired: <flow> — <first blocking reason>` line per unwired flow plus the `gaps.json` path. Entirely inside `GenerateCommand`, no change to `PackageGenerator`. **S** |
| L2 | **No trace separates "simple package" from "something wasn't analyzed."** Nothing lists components visited, flows wired or skipped, or components treated as passthrough. That is exactly the blind spot that hid C2 (a Script Component silently dropped). | [CODE] | M | Emit a per-package component inventory tally ("30 components: 27 translated, 2 seams, 1 passthrough-by-default") into `generate-report.md`, and one number into the log. **S** |
| L3 | **Depth asymmetry.** `apply-fills.log` traces every folder, file, seam and decision (24 writes against `generate`'s 16). The asymmetry is backwards relative to risk: `generate` decides what exists, so a surprisingly low gap count deserves more traceability than a copy step does. | [CODE] | M | Covered by L1+L2. |
| L4 | **The benign duplicate-name warning goes to stderr**, where PowerShell renders it as a `NativeCommandError`, so it looks like a failure. | [RUN] | L | Send it to stdout. **XS** |

### 2.8 Everything else

**Security.** Passwords never land in `appsettings.json`: they go to User Secrets, and encrypted
DTS passwords are gapped, never extracted. The OLE DB Command path is parameterized. Beyond that:

- **S1 [RUN] H — the "client-safe" portfolio digest leaks names.** `portfolio-digest.md:103` says
  "Nothing here is client data", yet it contains connection-manager, component and worksheet names.
  Cause: `PortfolioDigest.cs:152` tallies `Shorten(g.Reason)`, breaking the class's own allow-list
  rule (`:21`). Fix: tally by `GapKind` or a fixed category; add a test asserting no input name
  appears. **S**
- **S2 [RUN] M — string-concatenated SQL.** `Mapping/SQL_LogFileNameStatement.cs:8` (a ForEach loop)
  builds `"… N'" + currentFile + "');"`. A file named `O'Brien.csv` breaks the statement or injects
  SQL. This faithfully mirrors the SSIS expression, but it should escape `'` or raise an advisory.
  **S**
- **S3 [RUN] M — default redaction only strips passwords.** `spec.json` still carries `User ID=sa`,
  server names and full author paths, including a corporate OneDrive path (e.g.
  `Package_Advanced.spec.json:10`). This matches `--help`, but spec output isn't safe to send
  off-site; say so prominently. Generated `appsettings.json` also carries `UserId "sa"` and the
  author's OneDrive folders. `scripts/` holds full script source (75 files): expected, but it must
  stay on the client's machine.
- **S4 [CODE] M — plaintext secrets in the repo:** the SSISDB master-key password in `CLAUDE.md`
  (~line 485; the file itself warns about this) and a self-test password in
  `Validation/docs/gate3-harness.md:207,225`. Rotate them and move them to a vault.
- **S5 [CODE] L — dev material in the client-shipped `Tools\` folder:** dev-machine example paths in
  `Tools/README.md` (lines 311, 326, 419, 423-424, 444, 464), tracked dev session logs
  `Tools/session-logs/*.md`, an untracked stray folder `Tools/SsisExtractor/tmpapplyfills-check/`,
  and `D:\PoC\SSIS_Rewrite` in code comments.

**Broken client script and a stopped extract.** `Run-Survey.ps1:58,68` calls the retired
`report`/`conformance` verbs. It prints a misleading "packages could not be read", crashes at line
88 and exits 1 (**H**, [RUN], spot-checked). Separately, `ExtractCommand.cs:213-214` returns early
on `reportExit == 2`, which `ReportCommand.cs:288-292` produces for any skip, including a benign
duplicate. Downstream stages never run, and `apply-fills` loses its conformance linkage (**H**,
[RUN]). **S** each.

**Doc drift.**

| Doc | Claim | Reality |
|---|---|---|
| Handover §6 | Completion/Expression-only constraints not translated; Aggregate Count only; Character Map supported | See C1. |
| Handover §11 (line 464) | Survey runs `ssisx report` | The verb is retired and the script crashes. |
| Handover §2 | Exit 3 means "code was written"; codes 0/1/2/3 only | See R6; 98/99 are undocumented. |
| `ssisx --help` (extract) | Lineage/conformance/testgen written "unconditionally" | Skipped on any load skip. |
| `ssisx --help`, `Program.cs:204` | `generate` covers "exactly milestone 1's scenario" | Long obsolete. |
| `ssisx --help` (`--notifications`) | The `.Notification` gap only appears with the flag | Reported without the flag on all packages. |
| `ssisx --help` (apply-fills), `generation-readiness.md`, `COPILOT_TESTING_GUIDE.md:11` | Seams come from `--seams` | Seams are on by default; readiness contradicts `generate`. |
| `Tools/SsisExtractor/README.md` (17 hits), `SsisValidationKit/README.md:74,79`, `docs/*.md` | `report`/`graph`/`conformance`/`testgen`/`diff` are live verbs | All exit 2. |
| Handover §13 point 3 | "Five columns → five packets; read one per component" | The generator already emits one per component; the real duplication is the TEST-ORACLE twins (G5). |
| Repo-root `.github/copilot-instructions.md` | — | A stale copy, 46 lines different from `Tools/.github/`. |
| Repo-root `README.md` | `SSIS.sln`, `Data\Input` | Those paths don't exist. |

The Claude `.claude/commands/*.md` files are thin pointers to the Copilot prompts, so the "1:1"
claim holds in behaviour. Copilot's `ssisx-batch-generate` has no Claude command file (the handover
calls it a skill instead).

**Test-suite health and maintainability.**
- `dotnet test SsisExtractor.slnx`: **840/840** (181+177+25+457; Codegen 1m31s). `Run-Tests.ps1`:
  **997** including Etl.Core 139 and LoadEmployees/LoadReferenceData 11/7, all green.
- **There is no CI at all.**
- No test covers `extract` on a folder with a duplicate-name package, which is why the stopped
  extract survived.
- `PackageGenerator.cs` is **4,677 lines**, and its `Generate()` method alone is ~1,630 lines
  (≈111–1745). It has 13 `Generate*Flow` helpers and about 47 optional `= null` parameters threaded
  through. Next largest: `PackagePlanner.cs` 3,789, `ComponentTestEmitter` 1,452,
  `PackageClassEmitter` 1,254, `TransformEmitter` 1,158.
- The D6 drift is the predictable consequence. Recommend (**M**, incremental) splitting `Generate()`
  into a per-flow-shape strategy table, and replacing optional-parameter threading with one
  per-package context object.

---

## 3. Cost/effectiveness assessment

Against `Docs/Tool-Handover-Overview.md` §13:

- **Claim 1 (the AI only runs where a decision is needed): holds for the pipeline, not for the
  packet stream.** Extract, generate, apply, build and test make no LLM calls. But **16 of 26 RBC
  packets** (all ENCRYPTED-SECRET and LOCAL-DATA) require no reasoning and still go to the AI.
- **Claim 2 (each prompt names one thing to read): holds, but is undermined by P1/G4.** The packet
  can't be answered on its own, so `ssisx-fill.prompt.md` has to tell the agent to grep generated
  code, and TEST-ORACLE packets send it to a 3.5K-token README.
- **Claim 3 (packets deduplicate evidence): does not hold.** 67% of packet bytes are duplicated;
  TEST-ORACLE twins re-ship whole scripts; every packet embeds blocks that don't apply to it.
- **Claims 4 and 5 (`-Quiet`, the mechanical/judgment split): hold.** One caveat: R4 means the
  `-Quiet` summary can report "exit 0" for a failed test.
- **Correctness costs tokens too.** A literal answer to a Script packet doesn't compile (P1), and a
  literal fixed-width answer silently loses a row (G2). Each costs at least one extra
  build-diagnose-retry loop, typically more tokens than the packet itself.

**The biggest lever nobody has pulled is `Tools/.github/copilot-instructions.md`.** It is 24.6K
chars (≈6.2K tokens), and Copilot attaches it to every chat turn. Over a ~26-turn portfolio session
that is ≈160K tokens, about 5× the entire packet corpus (33.5K). Cutting it to a ≈1.5K-token core
(the hard rules) and moving the rest into the per-step prompt files saves ≈120K tokens per session,
with no behavioural change.

The second and third levers:
- Take LOCAL-DATA (via `svk`) and ENCRYPTED-SECRET (via a checklist) out of the AI loop entirely:
  −16 packets, ≈11.5K tokens, plus their turns.
- Merge the Script/TEST-ORACLE twins and trim per-packet boilerplate: ≈−11K tokens.

Together, the packet changes remove about 60% of packet traffic, and the instructions-file change
dominates everything.

---

## 4. Top 5 recommendations (ranked by impact × cheapness)

1. **Fix the three pipeline-honesty bugs.** These are the ones that make automation lie or quit.
   - The `@(...)` wrap in `Run-Coverage.ps1:183` (R4).
   - Continue past benign skips in `extract` (§2.8) and past single-package failures and exit 98 in
     `Run-Pipeline.ps1` (R3).
   - Repair `Run-Survey.ps1` (§2.8).

   **XS–S, about 1 day total.** Everything else in an unattended run depends on these exit codes
   being true.
2. **Close the silent-wrong-output holes.** A blocking gap on `HasCycle` and on dangling constraints
   (R1); a per-component gap for any unrecognized component, including column-less Script Components
   (C2); a gap per `.dtsConfig` (C3); the collision-aware reference resolver (D1); Boolean `-1`
   seeding (R9). **S each, ~3 days.** These restore the project's core promise.
3. **Shrink the AI's standing context and remove the no-judgment packets.** Cut
   `copilot-instructions.md` to a ~1.5K-token core; auto-close LOCAL-DATA via
   `svk sampledata` + rename (G1); fold ENCRYPTED-SECRET into one checklist (P3). **S, ~2 days,
   ≈130K+ tokens saved per portfolio session.**
4. **Make packets actually self-contained.** Real namespace, class and `using`s in the seam
   templates (P1); the header-skip line in fixed-width LOCAL-DATA (G2); the correct router
   type/columns and call signature in TEST-ORACLE (G4); upstream columns in the Tier-1 Lookup packet
   (P2). **XS–S, ~1.5 days.** This removes retry loops and one silent-data-loss path.
5. **Raise starter-test strength deterministically.** Distinct literals per column, coercion-rule
   tests, exact flat-file lines, and a recording `FakeBulkSink` plus a `LoadContext` seam so tests
   call production (T3 (a)–(c), (g)). **~4 days.** Target the kill rate from 22% to over 60% on the
   same 18 mutations, which gives a re-runnable metric. Add the mutation set as a regression harness.

Honourable mentions: the ranked readiness table with secrets split out and seams on (C7), the
`generate.log` per-package gap summary (L1), the digest leak (S1), clearing stale output per
package (R2), and adding any CI at all.

---

## 5. What's genuinely solid

- **Extraction is robust.** Truncated, zero-byte, Latin-1, UTF-16-BOM and double-BOM `.dtsx` files,
  empty folders and folders with no `.dtsx` all fail per file with clear load-failure rows and no
  stack traces. The recursive run from the portfolio root correctly filtered `obj\` copies and
  reported the `_trash` duplicates. [RUN]
- **Scale.** 50 × 341 KB packages: extract 5.5 s, generate 4.8 s, linear. [RUN]
- **Coverage of the real portfolio.** Every component and task type in RBC is modelled and
  generated; extraction coverage is 100% on 4 of 5 packages (98.67% on the fifth, with the missing
  piece named). Unknown tasks and column-adding unknown components get specific, blocking Tier-3
  gaps, and the Execute Package / Transfer SQL Server Objects gaps usefully describe what the task
  does. [RUN]
- **Build and suite health.** All generated code in scope built 0 warnings/0 errors; 840/840 and
  997/997 tests pass; the golden-file failures CLAUDE.md long carried are resolved. [RUN]
- **`apply-fills`' core safety.** Stale fills (hash mismatch) and orphans are refused with both
  hashes and reasons; the recursive scan finds nested fills; the console and log agree. [RUN]
- **Compile-time seam enforcement works as advertised,** and `--unsafe-skip-seams` can no longer
  bypass it. [RUN]
- **Secrets handling in generated code.** No passwords in `appsettings.json`; encrypted DTS
  passwords are never extracted or guessed; OLE DB Command is parameterized. [RUN]
- **Test design in places.** Derived Column expected values come from the oracle-verified
  evaluator, SQL Statement tests assert literal text (they caught the TRUNCATE mutation), and the
  failure-path `RunAsync` test caught the missing commit. [RUN]
- **Readable generated code.** Method-per-component, names match the `.dtsx` and the `svk`
  walkthrough numbering; the walkthrough's control-flow order, parallelism callout and Merge Join
  details are accurate; `sampledata` CSV/SQL is deterministic and the Lookup 70/30 split holds.
  [RUN]
- **CLI usage errors** (unknown flag, bad `--framework`, missing `--out`, unmatched `--package`) all
  exit 2 with clear messages, and retired verbs point at `extract`. [RUN]

---

*Scratch artifacts backing every [RUN] finding (inputs, mutated copies, logs, generated trees) are
under `D:\tmp\independent-review-2026-09-25\`, one folder per area. The mutation harness is
`tests\mut\Run-Mutation.ps1`.*
