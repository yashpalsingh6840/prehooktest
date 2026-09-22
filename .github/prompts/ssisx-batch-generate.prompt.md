---
mode: agent
description: Unattended, auto-confirming variant of /ssisx-rewrite for background/batch runs against any InputPath -- no human present mid-run, so both AI boundaries are auto-confirmed and flagged UNREVIEWED instead of pausing. Two phases, invoked separately so the mechanical half (Phase A) can run independently of the judgment-requiring half (Phase B). Phase A uses -Quiet on Run-Pipeline.ps1 (see its own Step 2) to keep its own token cost small regardless of model.
---

# ssisx: batch-generate (unattended two-phase pipeline)

- **Input folder:** `<inputPath>` -- any folder, `.dtproj`, `.dtsx`, or `.ispac`.
- **Output folder:** `<outputPath>` -- created if missing, never touched if present.
- **Target framework:** `<framework>` -- `net8.0` or `net10.0`. If not given, ask before
  generating -- do not default to `net10.0` silently (same rule as `ssisx-generate.prompt.md`).
- **Gap threshold:** `<threshold>` -- default **20**. The total count of Tier-1
  (`MissingDatum`) + Tier-2 (`MissingLogic`) gaps found in Phase A above which Phase B stops and
  reports instead of proceeding, so a very large amount of unattended AI-fill work is never
  started without it being visible first.

This is **not** `Tools/.github/prompts/ssisx-rewrite.prompt.md`. That prompt is for an
interactive chat session and deliberately stops at two AI boundaries to wait for a human. This
prompt is for the opposite situation -- a background/batch run with no human watching -- so both
boundaries are auto-confirmed instead, with every such decision stamped and flagged so a human
can review it later. It reuses `ssisx-rewrite.prompt.md`'s sibling prompts (`ssisx-fill.prompt.md`,
`ssisx-test.prompt.md`) for the actual mechanics of writing fills; only the "stop and wait"
behavior at each boundary is replaced.

This prompt is generic: it must never encode anything about a specific package, project, or
component type. Whatever `<inputPath>` contains is discovered by the tools themselves
(`ssisx extract --recursive`/`generate --recursive`, both already invoked by `Run-Pipeline.ps1`)
-- this prompt's own logic is identical regardless of what's inside.

Split into two phases so they can be run by two different callers/models -- **do not run both
phases from a single invocation of this prompt**; each phase below is its own separate step.

## Phase A -- mechanical run (no judgment required)

1. Do nothing here -- `Run-Pipeline.ps1`'s own `Resolve-Tool` already handles this: it uses
   `Tools/SsisExtractor/scripts/ssisx.exe`/`svk.exe` if one happens to already be sitting there,
   and transparently falls back to `dotnet run --project` (against source, no publish step) if
   not. **Never run `dotnet publish`/`dotnet build -o ...` into
   `Tools/SsisExtractor/scripts/` yourself** to "speed this up" -- that folder holds only the
   handful of tracked `.ps1`/`.bat` files (`git ls-files Tools/SsisExtractor/scripts` is the exact
   list); publishing a whole framework-dependent build (the exe, its DLLs/PDBs, and a dozen
   localized satellite-resource subfolders like `cs/`/`de/`/`ja/`) into it is real clutter a human
   then has to notice and clean up, for a speed-up `Resolve-Tool` doesn't need any help with.
2. Run exactly one command, **always with `-Quiet`**:
   ```powershell
   Tools/SsisExtractor/scripts/Run-Pipeline.ps1 -InputPath "<inputPath>" -OutputPath "<outputPath>" -Framework <framework> -Quiet
   ```
   This already runs, in order: `ssisx extract` -> `svk walkthrough`/`svk sampledata` (advisory)
   -> `ssisx generate` -> `ssisx apply-fills` (a no-op the first time -- nothing exists under
   `<outputPath>\fills` yet) -> `dotnet build` -> `Run-Coverage.ps1` (`dotnet test --filter
   Category!=Integration` + coverage). It never deletes anything under `<outputPath>` -- safe to
   re-run. **`-Quiet` is not optional here** -- without it, this ONE command's own console output
   (dotnet restore/build/test chatter across every step) can be tens of thousands of tokens, which
   is exactly what makes an otherwise mechanical, tool-only run expensive; `-Quiet` redirects all
   of that to `<outputPath>\pipeline-run.log` and prints only a compact final summary (every step's
   exit code, plus gap counts by tier already parsed from `gaps.json`) to the console -- this
   command's own result already gives you everything Step 3 below asks for.
3. The `-Quiet` summary already gives you everything to report: package count (from the extract
   step's own console line, still shown), every step's exit code, and gap counts broken down by
   tier (`MissingDatum` = Tier 1, `MissingLogic` = Tier 2, `MissingToolSupport` = Tier 3 -- nothing
   to fill, ever -- and Advisory = non-blocking). Only fall back to reading
   `<outputPath>\gen\generate\generate-report.md` (never the generated `.cs` files, never
   `pipeline-run.log` in full) if you need the PER-PACKAGE breakdown the compact summary doesn't
   carry, or if the summary's own gap-tier parse failed for some reason.
4. **Stop here.** Do not decide whether to proceed to Phase B and do not write any fill --
   that decision (the threshold check below) belongs to whoever invoked you, using this report.

## Phase B -- gap-fill + rebuild (auto-confirmed, flagged UNREVIEWED)

Only run this once Phase A's report for this exact `<outputPath>` already exists, and once the
Tier-1 + Tier-2 gap count from that report is `<= <threshold>` -- if it is not, do not proceed;
report the count back and stop (this rule is generic, not about any specific package/project).

1. If there are any Tier-1/Tier-2 gaps: follow `ssisx-fill.prompt.md` Steps 0-2 **exactly**, with
   one substitution -- wherever that prompt says "stop and report... do not proceed until told
   to continue," instead:
   - write the fill,
   - for a Tier-1 decision, set `ConfirmedBy` to the literal string
     `"automated-batch-run — UNREVIEWED"` -- **never** a fabricated human name, and never invent
     a plausible-sounding value for the decision itself if the packet's own evidence doesn't
     support one; if no confident answer exists, leave that one gap unfilled and say so in the
     final report rather than guessing,
   - for a Tier-2 script port, write the most faithful, literal translation you can against the
     pinned `Etl.Core` API in that prompt's own Step 2 -- if something genuinely cannot be
     reproduced against those abstractions, state that plainly in the fill/report instead of
     inventing a substitute (identical rule to the interactive prompt, just without a human to
     tell first),
   - continue to the next gap/component rather than stopping.
2. Follow `ssisx-fill.prompt.md` Step 3 (apply-fills, `dotnet build`).
3. If `dotnet build` succeeds and `dotnet test --filter Category!=Integration` still shows any
   open `TEST-ORACLE`/`LOCAL-DATA` gap, follow `ssisx-test.prompt.md` Steps 0-2 the same
   auto-confirmed way (provenance comment written as normal, no `ConfirmedBy` concept there --
   just proceed rather than stopping), then Step 3 (apply-fills, build, test).
4. Run `Tools/SsisExtractor/scripts/Run-Coverage.ps1 -GeneratedRoot <outputPath>/gen/generate`
   for the final coverage figures (mirrors `ssisx-rewrite.prompt.md` Step 8).

### Report format

End with:
- Final build/test/coverage result (exit codes, pass/fail/skip counts, the "own generated code"
  and blended "Line %" figures from `coverage-report.md`).
- **"Auto-confirmed, needs human review"** -- an explicit list of every Tier-1 decision (its
  `GapId` and what was decided) and every Tier-2 script port (its `GapId`/class/method) written
  this run. This section must never be empty if any fill was written, and must be easy to find --
  it is the whole reason this prompt exists instead of quietly reusing `ssisx-rewrite.prompt.md`.
- Any gap still open afterward, with its tier and why (Tier-3 `MissingToolSupport` gaps are
  expected to remain open always -- no fill can close them).

## Logging

Append to `session-logs/<yyyy-MM-dd>-session.md`: which phase ran, the exact commands, their
output/exit codes, and (for Phase B) the full auto-confirmed list above -- this is the audit
trail a human reviews later, so do not summarize it away.
