# Proposed Fix Plan — Independent Review 2026-09-25

Companion to [Independent-Review-Report-2026-09-25.md](Independent-Review-Report-2026-09-25.md).
Finding IDs (R1, C2, P1, …) refer to that report.

**Status:** draft for review. Nothing here is implemented yet.

---

## Guiding rules for this plan

1. **Safety net before surgery.** Phase 0 adds the regression checks that would have caught these
   findings, so each later fix proves itself and can't quietly regress.
2. **Honesty before capability.** Fix the places where the tool or its scripts *lie* (wrong exit
   code, silently wrong code) before improving features.
3. **Same evidence bar as the project already uses.** Every fix gets a unit test. Every fix that
   changes generated code also gets regenerate → build → run on a real fixture. Where SSIS
   semantics are involved, measure with a `dtexec` probe first.
4. **Small, separately committable steps.** Each numbered item is designed as its own commit.
5. **Commit the current 41 uncommitted changes first**, since the review ran on top of them. That
   gives this plan a clean baseline.

Sizes: **XS** < ½ day · **S** ≈ 1 day · **M** ≈ 2–5 days.

---

## Decisions needed before starting

These change contracts, so I'd rather you pick than have me guess.

| # | Decision | Options | My recommendation |
|---|---|---|---|
| D-1 | **Exit-code contract** (R6). Today 3 fires on every package, and 0 is effectively unreachable. | (a) Keep 0/1/2/3 and fix the meanings. (b) Add new codes. | **(a)**: 0 = no *blocking* gaps; 3 = blocking gaps, files written; 4 = nothing written; 2 = usage error only; 98/99 as today, documented. Advisory-only gaps (the Notification gap, for example) no longer force 3. This breaks callers that expect 3 — only our own scripts do, and they get updated in the same commit. |
| D-2 | **Stale output on regenerate** (R2). This conflicts with the standing "never delete generated output" rule. | (a) Before writing a package, delete that package's `generate/<Pkg>/` and `<Pkg>.Tests/`, always keeping `Fills/` and `TestData/`. (b) Write a manifest and only *warn* about extra files. (c) Write to a temp folder, then swap. | **(a) + a warning** for package folders the current run didn't cover. This is scoped deletion of tool-owned files only; hand-supplied data is never touched. |
| D-3 | **Duplicate `ObjectName` winner** (R5). | Prefer the file the `.dtproj` lists → then the newest write time → then path order. | As listed, and the rule is printed in the warning. |
| D-4 | **Identifier collision** (D1). | (a) Share one resolver between declaration and reference sites (fully correct). (b) Refuse a collision with a gap. | **(a)**, with (b) as a fallback if threading the resolver proves too invasive. |
| D-5 | **What a benign duplicate skip does to `extract`** (§2.8). | Exit 0 plus a warning, vs a new "warnings" code. | Exit 0 plus a warning, and the skip stays in `load-failures.md`. |
| D-6 | **LOCAL-DATA auto-close** (G1). Files made by `svk` are schema-correct but not realistic. | (a) Auto-close, tagged `Synthetic` in `fills-applied.json`. (b) Only pre-fill a draft for a human to approve. | **(a)**, marked `Synthetic` clearly so no one mistakes it for real data. |

---

## Phase 0 — Safety net (≈2 days)

| # | Change | Files | Done when |
|---|---|---|---|
| 0.1 | **CLI integration tests for the failure modes found in the review.** A duplicate-name folder; one truncated package among good ones; a missing `.conmgr`; a cycle; a dangling constraint; a zero-byte file. Each test asserts the exit code, what gets written, and the wording of the gap/warning. They start **red** and each later phase turns its own ones green. | `tests/Ssis.Extract.Cli.Tests/` (new `RobustnessTests.cs`), plus small fixtures made with the byte-preserving edit method | Tests exist and fail for the right reason |
| 0.2 | **Mutation harness checked in** as a regression metric, based on the review's `D:\tmp\…\tests\mut\Run-Mutation.ps1`, 18 mutations. It reports a kill rate and does not run in the fast suite. | `Tools/SsisExtractor/scripts/Run-Mutation.ps1` | Reproduces 4/18 today |
| 0.3 | **Tests for the pipeline scripts.** A small PowerShell test (a scratch generated tree with one failing test project) proves `Run-Coverage.ps1` returns non-zero. | `scripts/tests/` | Red today (R4) |
| 0.4 | **Minimal CI.** A GitHub Actions workflow runs `dotnet test SsisExtractor.slnx` plus 0.3 on `windows-latest`. `Etl.Core.Tests` runs against the in-repo `Tools/Etl.Core` copy, which needs a small test-project reference. | `.github/workflows/ci.yml` | Green on `main` except the intentionally-red tests, which are marked skip until their phase lands |

---

## Phase 1 — Pipeline honesty (≈2 days)

The automation must never report success on failure, or quit on something benign.

| # | Fixes | Change | Files | Done when |
|---|---|---|---|---|
| 1.1 | R4 | `@(...)` wrap on line 183; audit every `.Count` in `scripts/*.ps1` | `Run-Coverage.ps1` | 0.3 green |
| 1.2 | §2.8 (extract stopped early) | `ReportCommand` returns a distinct result for benign-only skips; `ExtractCommand.cs:213` continues. `lineage/`, `conformance/` and `testgen/` are always written. | `ReportCommand.cs:288-292`, `ExtractCommand.cs:200-214` | The RBC extract has all folders and exits 0 (D-5) |
| 1.3 | R3 | `Run-Pipeline.ps1`: warn and continue on package-level load failures, abort only if zero packages loaded; treat 98 as "partial, continue, loud"; add `-nodeReuse:false` to its builds (R10). `generate-report.md` lists crashed packages. | `Run-Pipeline.ps1:180-247`, `GenerateCommand.cs:273` | Truncated-package test passes with the other 4 built |
| 1.4 | §2.8 Run-Survey | Call `extract` (redaction forced), drop the `conformance` call, and tell usage errors apart from load failures | `Run-Survey.ps1`, `Publish-Survey.ps1` smoke test | Survey zip produced for RBC |
| 1.5 | R6 / D-1 | Apply the exit-code contract across `extract`/`generate`/`apply-fills`/`apply-tests`. Hard-fail (2) on a missing `--fills`, a missing `--out` for apply-tests, and a missing `--etl-core`. Make `--package A,Nope` consistent. | `GenerateCommand.cs`, `ApplyFillsCommand.cs`, `ApplyTestsCommand`, `Program.cs` help, handover §2 | Matrix test over all commands × outcomes |
| 1.6 | L4 | The benign duplicate warning goes to stdout, not stderr | `PackageLoader`/CLI | No `NativeCommandError` in PowerShell |

---

## Phase 2 — Close the silent-wrong-output holes (≈4 days)

These restore "gaps, not guesses". Each fix makes a previously silent case produce a *gap*; none
of them tries to translate anything new.

| # | Fixes | Change | Files | Done when |
|---|---|---|---|---|
| 2.1 | R1 cycle | Blocking planner gap when `ControlFlowDagSpec.HasCycle`; fix the stale comment at `PackagePlanner.cs:1628`. SSIS itself refuses cyclic packages at validation, so a gap is the faithful answer — confirm with one `dtexec` run of the cycle fixture. | `PackagePlanner.cs:1628,1672` | Cycle fixture → blocking gap, no `Program.cs` step reordering |
| 2.2 | R1 dangling | Extract finding plus a blocking gap for a precedence-constraint endpoint that doesn't resolve (today the edge is dropped, turning sequential into concurrent) | `DtsxPackageReader.cs` (DAG build), `PackagePlanner.cs` | Dangling fixture → gap; no `Task.WhenAll` emitted |
| 2.3 | C2 | Any component not on the recognized list gets **one per-component** blocking gap, whatever its columns. A column-less Script Component gets a Tier-2 seam (a `RunScriptAsync`-style per-row hook) if that's straightforward; otherwise it gets the gap. This also covers the Unpivot case (C4, gap message says "row semantics"). | `TransformEmitter.cs:340-364`, `PackagePlanner.PlanDataFlow` | `SyntheticScriptComponent.dtsx` no longer "generatable" silently |
| 2.4 | C3 | One blocking gap per `DTS:Configuration` (the `.dtsConfig` overrides aren't applied) | `PackagePlanner.cs` | `Package_Legacy` shows the gap |
| 2.5 | D1 / D-4 | Collision-aware reference resolution: build one per-row-type `raw → identifier` map at the declaration site and pass it to `TransformEmitter`/`RouterEmitter`/`ColumnExpressionBuilders`/test emitters (or the fallback: a collision is a blocking gap). Also investigate the stray `row.Component` fallback seen in the review's hand-made fixture. | `PackageGenerator.MakeColumnIdentifierResolver` + reference sites | New `SyntheticIdentifierCollision.dtsx` builds and **runs** against `.\SQLFORPOC_2022` with correct values |
| 2.6 | R9, R10 | Boolean default `-1`/`1`/`True` → true; range-check Int16/Int32 defaults (gap on overflow); missing `SamplingValue` → gap, not 0% | `PackagePlanner.cs:1425-1431`, `PipelineReader.cs:440` | Unit tests |
| 2.7 | S2 | ForEach SQL splice: escape `'` → `''` when a variable is spliced inside a string literal, and emit an advisory noting the expression was string-built. Needs a `dtexec` check of how SSIS itself behaves with `O'Brien.csv` — if SSIS also breaks, keep the faithful translation but make it a visible advisory rather than silently "fixing" it. | `ForEachLoopEmitter.cs` | Probe recorded; test with an apostrophe file name |
| 2.8 | R5 / D-3 | Duplicate-winner rule, printed in the warning | `PackageLoader.cs:132-153,221-225` | Loose-folder test picks `Package.dtsx` |
| 2.9 | R7 | Missing or corrupt `.conmgr` → a per-file load failure naming the `.conmgr`; keep project context | `DtprojReader.cs:102-104` | Two tests |

---

## Phase 3 — Output hygiene (≈2 days)

| # | Fixes | Change | Files | Done when |
|---|---|---|---|---|
| 3.1 | R2 / D-2 | Scoped clean of `generate/<Pkg>/`, `<Pkg>.Tests/` and `gaps/<Pkg>/` for each package being regenerated, preserving `Fills/`/`TestData/`; warn about package folders on disk this run didn't cover. Update copilot-instructions rule 14 to state the scoped exception explicitly. | `GenerateCommand.cs:207-244`, `Tools/.github/copilot-instructions.md` | The disable-a-DFT-then-regenerate repro builds 0/0 |
| 3.2 | R8 | Delete `generate-report.md`/`Generated.slnx`/`gaps.json` at the start of a run and write them last, so a killed run can never look complete | `GenerateCommand.cs` | Kill-mid-run test: no summary files present |
| 3.3 | R10 | `Run-Coverage` only reads a cobertura file newer than its own start time | `Run-Coverage.ps1:119` | No stale percentages next to a FAIL |

---

## Phase 4 — Make work packets truly self-contained (≈2 days)

Goal: a literal answer to any packet compiles or loads first time.

| # | Fixes | Change | Files | Done when |
|---|---|---|---|---|
| 4.1 | P1 | Seam templates carry the real `namespace`, `partial class` name and needed `using`s (all known at generation time); remove the contradictory "no using" / "filled in for you" text | `AiPacketEmitter.cs` | Test: paste the template verbatim, fill a trivial body, `apply-fills` → builds |
| 4.2 | G2 | Fixed-width/CSV LOCAL-DATA packets state `HeaderRowsToSkip` and show the exact first lines expected | `AiPacketEmitter.cs` | `Package_Legacy` packet matches the `svk` output |
| 4.3 | G4 | TEST-ORACLE packets: correct Tier banner; embed the exact call (`package.SCR_X(uow, ct)`, router class name, row type); resolve router input columns through the *generated* row type, not the pipeline name | `AiPacketEmitter.cs` | `CSPLIT_Validity` packet names `DFT_DerivedAndSplitCsvRow.CustomerID : string` |
| 4.4 | P2 | Tier-1 Lookup packet lists the upstream output columns available to join on | `AiPacketEmitter.cs` | SyntheticLookupSplit packet answerable without the spec |
| 4.5 | P6 | Generate the prompt's pinned testing-API block from the same source as the packet's (or replace it with a reference); add the missing members; fix `ThrowOnStep`/`SampleData` | `AiPacketEmitter.cs`, `ssisx-test.prompt.md`, `COPILOT_TESTING_GUIDE.md` | A drift test compares the blocks |
| 4.6 | P5 | `apply-fills`: exact-case name matching with a clear case-mismatch message; LOCAL-DATA tracked per GapId (one file can answer several gaps); "did you mean" nearest-match hints; correct summary categories (missing `partial` ≠ orphan; missing provenance ≠ GapId mismatch); preserve relative paths or fail on a flattening collision | `ApplyFillsCommand.cs:217-248,395,566` | Each of the review's 6 broken-fill cases gets a precise message |

---

## Phase 5 — Cut AI token cost (≈3 days)

| # | Fixes | Change | Est. saving | Files |
|---|---|---|---|---|
| 5.1 | §3 biggest lever | Split `Tools/.github/copilot-instructions.md` (24.6K chars) into a ≈1.5K-token core of hard rules; move the per-step detail into each `.prompt.md`. Delete or sync the stale root `.github/copilot-instructions.md`. | ≈120K tokens/session | `.github/` |
| 5.2 | G1 / D-6 | New `ssisx apply-fills --synthesize-local-data` (or a `svk sampledata --for-gaps <gaps.json>` mode) writes the `svk` output under each gap's `ExpectedFileName`, tagged `Synthetic` in `fills-applied.json`. Keep the dependency direction: svk reads `gaps.json`; svk never references Codegen. | −8 packets on RBC | `SvkCore`, `ApplyFillsCommand.cs` |
| 5.3 | P3 | ENCRYPTED-SECRET gaps become one `secrets-checklist.md` per `--out` (a human table), with no AI packet; still blocking | ≈−6K tokens | `AiPacketEmitter.cs`, `GenerateCommand.cs` |
| 5.4 | G5 | Merge each Script seam packet with its TEST-ORACLE twin into one packet with two answer sections (keeps two separate fills and two GapIds) | ≈−7K tokens | `AiPacketEmitter.cs`, `ssisx-fill`/`ssisx-test` prompts |
| 5.5 | P4, G3 | Emit only the applicable TEST-ORACLE shape; drop the SSIS-semantics block for sources that are already C#; one LOCAL-DATA packet per target file; no LOCAL-DATA gap for a file no test reads | ≈−5K tokens | `AiPacketEmitter.cs`, `PackageGenerator.cs` |
| 5.6 | — | A packet-size regression test: total packet bytes for RBC under a budget, and a duplicate-line ratio under 30% | — | Codegen tests |

---

## Phase 6 — Stronger generated tests (≈8–10 days, incremental)

Target: mutation kill rate **≥ 60%** on the Phase 0.2 set (4/18 today), and a real `RunBranchAsync`
test. Re-run 0.2 after each item.

| # | Fixes | Change | Size |
|---|---|---|---|
| 6.1 | T3(a) | A distinct representative literal per column (`"FirstName_1"`) instead of `"Sample"` | XS |
| 6.2 | T1 | Tests for coercion-only transforms using the measured rules (`NarrowR8ToI4(2.5)==2`, `ParseWstrToI4(" 12.5 ")==12`, …); at minimum an advisory instead of the silent return at `TransformTestEmitter.cs:106` | S |
| 6.3 | T3(c) | Flat-file sink tests assert the exact expected padded/delimited line | XS |
| 6.4 | D2, T3(g) | Pass `LoadContext` to step methods (or add a `RunStep(ctx)` seam) so tests call production `DFT_X()`; add a recording `FakeBulkSink` and a `BulkInsertCall` that captures row values | M |
| 6.5 | T2, T3(d) | Lookup test with a fake cache holding two distinct reference columns; the Aggregate test calls the production key selector/projection | S–M |
| 6.6 | T4 | One synthesized row per router case, plus an `== literal` boundary row, verified via the oracle evaluator | M |
| 6.7 | T3(f) | Multiple rows per Derived Column (NULL, at width, width+1 → throws) and non-string outputs via the oracle engine | M |
| 6.8 | T3(h) | Merge Join algorithm test with unmatched left and right rows | S |
| 6.9 | coverage | Happy-path `RunAsync` for SQL/Excel-sourced packages using a fake row source, so SQL-sourced packages aren't stuck at ~50% | S |
| 6.10 | G6 | `apply-tests` checks `Targets=` against the README method table and warns | XS |

---

## Phase 7 — Reporting, readiness, logging, walkthrough, sample data (≈5 days)

| # | Fixes | Change | Size |
|---|---|---|---|
| 7.1 | C7 | `extract` readiness runs with seams **on** (`ReportCommand.cs:136`); one ranked table (Package / Tier-3 / Tier-2 seams / Tier-1 / prerequisites / complexity); "generatable once N secrets are supplied" as its own status | S |
| 7.2 | S1 | Digest tallies gaps by `GapKind`/a fixed category, never raw `Reason` text; a test asserts no input identifier appears in digest output | S |
| 7.3 | L1, L2 | `generate.log` per-package line with blocking/advisory split and the dominant kind; `unwired: <flow> — <reason>` lines; a component-disposition tally (translated / seam / passthrough / gapped) in `generate-report.md` | S |
| 7.4 | C5 | Walkthrough: Derived Column expressions in the Detail column; arrows labelled with output names; event handlers; the ForEach enumerator; an "at a glance" risk header from `gaps.json` when present; deduped boilerplate | S |
| 7.5 | C6 | `sampledata` biasing: parse-target values for Data Conversion inputs; literal harvest from Conditional Split/ternary conditions (satisfying + boundary + NULL rows); a routing check reporting rows per output (warn on 0); shared key pools for Merge Join/Merge. Also a deterministic `.xlsx` (fixed zip timestamps/part names) and SQL-source samples named after the real queried table | M (2–3 d) |

---

## Phase 8 — Docs, hygiene, maintainability (≈3 days + ongoing)

| # | Fixes | Change | Size |
|---|---|---|---|
| 8.1 | C1, doc-drift table | Fix every row of the report's doc-drift table: handover §2/§6/§11/§13, `--help` (milestone-1 text, `--notifications`, `--seams`), `COPILOT_TESTING_GUIDE.md:11`, retired verbs in `Tools/SsisExtractor/README.md`/`docs/*.md`/`SsisValidationKit/README.md`, the stale task message at `PackagePlanner.cs:1869`, repo-root `README.md` | S |
| 8.2 | S3 | State prominently in `--help`/README that `spec.json` is not safe to send off-site (default redaction strips passwords only); consider an `--redact-strict` mode for user IDs, servers and paths | XS (doc) / S (mode) |
| 8.3 | S4 | Remove plaintext secrets from `CLAUDE.md` and `Validation/docs/gate3-harness.md`; rotate those credentials; they're in git history, so treat them as compromised | XS |
| 8.4 | S5 | Remove dev paths from `Tools/README.md`; untrack `Tools/session-logs/`; delete `Tools/SsisExtractor/tmpapplyfills-check/`; scrub `D:\PoC\SSIS_Rewrite` from shipped comments | XS |
| 8.5 | D5, D6 | Delete the dead branch at `PackageGenerator.cs:1835`; share one `SourceNeedsUow` helper between `PackageGenerator` and `PackageClassEmitter` | XS |
| 8.6 | D3 | Concurrent packages: skip the empty begin/commit before a parallel wave; consistent Flow headings; ASCII `->` in generated comments | S |
| 8.7 | maintainability | **Incremental**, after Phases 1–6 land: split `PackageGenerator.Generate()` (~1,630 lines) into a per-flow-shape strategy table, and replace the ~47 threaded optional parameters with one per-package context object. Pure refactor, verified byte-identical against every fixture and the RBC portfolio before and after. | M |

---

## Sequencing and effort

```
Phase 0 (2d) ─► Phase 1 (2d) ─► Phase 2 (4d) ─► Phase 3 (2d)
                                   │
                                   ├─► Phase 4 (2d) ─► Phase 5 (3d)
                                   └─► Phase 6 (8–10d, incremental)
Phase 7 (5d) and Phase 8.1–8.6 (2d) can run alongside Phases 4–6.
Phase 8.7 last.
```

**Total ≈ 30–35 working days**, but value is heavily front-loaded:

| Milestone | Cumulative effort | Outcome |
|---|---|---|
| After Phase 1 | ≈4 d | Unattended runs report the truth and don't quit on benign input |
| After Phase 2 | ≈8 d | No known path produces silently wrong code |
| After Phase 5 | ≈15 d | ≈60% fewer AI packets, ≈120K+ fewer tokens per Copilot session, packets answerable first time |
| After Phase 6 | ≈25 d | Starter tests actually catch the classes of bug found here (kill rate ≥ 60%) |

## Verification for the whole plan

- `Run-Tests.ps1` green after every commit.
- Phase 0 robustness tests all green by the end of Phase 3.
- Mutation kill rate recorded after each Phase 6 item.
- Regenerate the full RBC portfolio after every phase that touches Codegen. Diff it against the
  previous phase's output and explain every changed file.
- Re-run the independent review prompt (`Docs/Independent-Tool-Review-Prompt.md`) after Phase 6,
  as a fresh-eyes check.
