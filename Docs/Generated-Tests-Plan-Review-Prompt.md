# Independent review: Generated-Tests-Plan implementation, tool non-regression, and the build-slowness fix

You are reviewing work in `D:\PoC\SSIS` with fresh eyes — do not assume any prior claim of
"done" or "fixed" is correct. Every finding must be backed by something you actually ran or
actually read, not inferred from commit messages or `CLAUDE.md` prose. Where `CLAUDE.md` and
the actual code/behavior disagree, the code/behavior wins and the disagreement itself is a
finding.

This review has three independent parts. Do all three. Report findings as three separate
lists: **Part A gaps**, **Part B regressions**, **Part C slowness verdict** — each entry naming
the exact file/command/test and what you observed, not a summary judgement alone.

## Part A — Does the implementation actually match the plan?

Read `Docs/Generated-Tests-Plan.md` in full first. Then, for every phase (0-4), every row of
the "Test taxonomy" table, every entry in "Files touched", and every one of the 10 items under
"Verification", check the CURRENT code/repo state and confirm it's really there — don't take
`CLAUDE.md`'s own "Generated-tests plan" narrative sections as proof; they describe what was
*intended* to be verified, not a substitute for re-checking now.

Specifically:
- For each taxonomy row ("RunAsync failure path", "Execute SQL Task", "Sequence container",
  "Source — CSV/SQL/Excel", "Sink — SQL/flat file", "Router", "Transform", "Merge Join mapper",
  "OLE DB Command", "ForEach loop", "Multicast", "Aggregate source", "Script Task/Component
  seam") — find the emitter function that produces it in
  `Tools/SsisExtractor/src/Ssis.Extract.Codegen/`, and confirm it is actually WIRED IN (called
  from `PackageGenerator.cs`) for every flow shape that can produce that component, not just the
  single-destination path. The project's own history already found and fixed two real instances
  of "wired for shape X but not shape Y" (`TransformTestEmitter` not called for Conditional
  Split/Multicast branches; source tests not emitted for a Conditional Split's own source) — your
  job is to find whether a THIRD instance exists, e.g. in the Merge Join flow path, the Lookup
  flow path, the Aggregate flow path, or the ForEach-Loop-over-a-Data-Flow-Task path. For each,
  actually generate a real fixture that exercises it (see `Tools/SsisExtractor/tests/Ssis.Extract.Tests/Fixtures/`
  for existing ones — `SyntheticLookupSingle.dtsx`, `SyntheticAggregate.dtsx`,
  `SyntheticForEachDataFlowLoop.dtsx` are good candidates) via
  `dotnet <path-to-built ssisx.dll> generate --input <fixture> --etl-core Tools/Etl.Core --out <fresh D:\tmp dir>`
  and inspect the actual `.Tests/` output — do the same class of test files show up as they do
  for the single-destination case, or is something missing?
- Confirm `GapKind.TestOracle`/`GapKind.LocalFileSourceData` really route through
  `AiPacketEmitter`/`ApplyFillsCommand` exactly as documented — generate a fixture with a Script
  Task/Component seam plus a CSV source, write a real fill for each new gap kind under a
  `fills-library/` directory, run `apply-fills`, and confirm `Applied`/`Stale`/`Orphaned` behave
  as documented for all three new subfolder kinds (`Tests/`, `TestData/`, plus the pre-existing
  seam mechanism).
- Confirm `--skip-tests` actually omits the whole `{Package}.Tests` project and its own
  `TestOracle`/`LocalFileSourceData` gaps, and that it changes NOTHING else (same gap counts for
  every other gap kind, same production `.cs` byte-for-byte).
- Confirm the three new prompt files (`ssisx-walkthrough.prompt.md`, `ssisx-test.prompt.md`,
  `ssisx-rewrite.prompt.md`) under `Tools/.github/prompts/` are internally consistent with each
  other and with `ssisx-fill.prompt.md`/`ssisx-generate.prompt.md` — same bootstrap-by-reference
  discipline, no restated command lines that could drift, no dev-machine absolute path leaked
  anywhere (grep the whole `Tools/` tree for `D:\PoC\SSIS_Rewrite`, `D:\PoC\SSIS_Migration`,
  `OneDrive` — this project has been burned by this leak twice already).
- Actually run `/ssisx-rewrite`'s own documented command sequence end to end against ONE real
  package with a Script Task (e.g. `SyntheticScriptTaskSeams.dtsx` or a real package from
  `SSIS_From_Sandeep` if present) in a scratch `D:\tmp` dir, and confirm it stops at both AI
  boundaries and that re-running it resumes correctly per its own Step-0 decision tree — this
  exact resume logic had a real bug found and fixed once already (see `CLAUDE.md`'s
  "Generated-Tests-Plan Phase 4" section); confirm it's still right, don't assume the fix holds.

## Part B — Confirm `ssisx extract`/`generate`/`conformance`/`testgen` and `svk walkthrough`/`sampledata` are unaffected

The Generated-Tests-Plan work touched `PackageGenerator.cs`, `TestProjectEmitter.cs`,
`GenerateCommand.cs`, and `ApplyFillsCommand.cs` — all of which existing, pre-plan functionality
also depends on. Confirm nothing outside the test-generation feature regressed:

- `ssisx extract --input SSIS --recursive --out <fresh dir>` against this PoC's own `SSIS/`
  folder: confirm the same coverage percentages CLAUDE.md documents (100% for LoadEmployees/
  LoadReferenceData) and that `spec.json` output is structurally sane (open one, sanity-check it
  parses and has the expected top-level shape).
- `ssisx conformance --input SSIS --recursive --out <fresh dir>`: confirm it still produces
  `conformance/<Package>.rules.json` + an all-Pending claims stub, and that rule IDs are stable
  across two consecutive runs (re-run it a second time into the SAME `--out`, diff the two
  `conformance-report.json` files — should be identical, since rerunning conformance must never
  silently mutate a rules file).
- `ssisx testgen --input SSIS --recursive --out <fresh dir>`: confirm it still produces the
  expected "N expressions covered, M skipped" counts CLAUDE.md documents for this PoC's own two
  packages (5 covered, 3 skipped — the three `LoadedAtUtc` `GETUTCDATE()` columns).
- `ssisx generate` for a package with NO test-plan-relevant shape at all (plain single-destination
  CSV→SQL, e.g. `LoadEmployees.dtsx`) with `--skip-tests`: confirm the PRODUCTION `.cs`/`.csproj`
  output is byte-identical to what a pre-Generated-Tests-Plan `ssisx` would have produced — you
  can get that baseline from `git show 4c92a52:...` (the commit immediately before this plan's own
  work landed) for the relevant golden files under
  `Tools/SsisExtractor/tests/Ssis.Extract.Codegen.Tests/Golden/Generated/`, or by checking out
  that commit into a `git worktree` and generating from there, then diffing.
- `svk walkthrough --spec <a real ssisx-extract output dir> --out <fresh dir>`: confirm it
  produces the same walkthrough markdown shape CLAUDE.md documents (Seq/Component/Type/Detail/
  Inputs/Outputs/Lineage source/Rule/Status/Reviewer/Note columns, a claims JSON file) for at
  least two different fixtures, one with a Conditional Split (confirm Seq numbers match
  `ComponentInsight.Describe`'s output exactly, same as the project's own Item 6 verification did
  for `SyntheticConditionalSplitRemerge.dtsx`).
- `svk sampledata` against a real spec: confirm it still produces schema-correct CSV + a
  `schema.sql` DDL file, and that column names/types match the source `.dtsx`'s own declared
  schema.
- Grep `SvkCore.csproj`'s own `ProjectReference`s and confirm the two-project boundary
  (`Ssis.Extract.Model`, `Ssis.Extract.Dtsx` only — never `Ssis.Extract.Codegen`/
  `Ssis.Extract.Cli`) still holds after this session's edit to that file (it gained a doc comment
  pinning this boundary — confirm the boundary itself, not just the comment, is still true).
- Confirm `dotnet build Tools/SsisExtractor/SsisExtractor.slnx` still produces both `ssisx.exe`
  and `svk.exe` from one build, and that `Tools/Etl.Core` (the portable copy) still builds
  standalone (`dotnet build Tools/Etl.Core/Etl.Core.csproj`) with 0 warnings/0 errors.

## Part C — Independently re-verify the `GeneratedSolutionBuildTests` slowness fix

`CLAUDE.md`'s own "Generated-Tests-Plan Phase 4" section documents THREE consecutive attempts to
root-cause a slow/inconsistent run time for `GeneratedSolutionBuildTests` — the first two were
each believed correct and each turned out wrong or incomplete. Do not trust the third one either
just because it's the last one written down. Independently stress it:

- Run `dotnet build-server shutdown` first, then run
  `dotnet test Tools/SsisExtractor/tests/Ssis.Extract.Codegen.Tests --filter FullyQualifiedName~GeneratedSolutionBuildTests`
  in isolation THREE times in a row, each preceded by `dotnet build-server shutdown`, and record
  the wall-clock time of each. All three should land in roughly the same range (CLAUDE.md claims
  ~1-1.5 minutes); if any one run is dramatically slower with no external cause, that is a finding.
- Run the FULL suite (`Tools/SsisExtractor/scripts/Run-Tests.ps1`) three times in a row the same
  way and record each run's total wall-clock time — CLAUDE.md claims ~1 minute for the whole
  `Ssis.Extract.Codegen.Tests` project including these tests. If it is not consistently fast,
  that is a finding, and the fix is NOT actually robust.
- Read `GeneratedSolutionBuildTests.cs`'s own process-spawning helper directly. Confirm, by
  reading the code (not by assuming from CLAUDE.md's own account): (a) both `dotnet build`
  invocations really do pass `-nodeReuse:false`; (b) stdout/stderr really are read concurrently
  (`ReadToEndAsync` on both, not sequential `ReadToEnd()` calls); (c) the timeout-driven
  `Kill(entireProcessTree: true)` path is really there and would actually fire if a spawned
  process hung past its timeout — trace the exact code path, don't just grep for the string.
- Deliberately try to reproduce the ORIGINAL failure mode described in CLAUDE.md: run 2-3
  background loops of `dotnet build`/`ssisx generate` against unrelated scratch directories WHILE
  the `GeneratedSolutionBuildTests` suite runs, and confirm it still completes in a reasonable
  time (not 10+ minutes) despite that contention. This is the actual adversarial condition that
  exposed the bug the first two times; passing it once more, under real concurrent load, is much
  stronger evidence than a quiet, idle run.
- State plainly in your verdict: is this fix ROBUST (survives concurrent load, three consecutive
  clean timings, and the code reads exactly as documented), or did you find ANY daylight between
  what CLAUDE.md claims and what you actually observed? A "seems fine" without having tried to
  break it under load is not an acceptable verdict for this part.

## Reporting

For each of Part A/B/C, give a short list of findings — each one naming the exact command run
or file read, what you expected, and what you actually got. If everything checks out for a
section, say so explicitly and name what you checked (a bare "no issues found" is not
acceptable — list the checks that passed). Do not fix anything you find — just report it
precisely; a human decides what to do with each finding next.
