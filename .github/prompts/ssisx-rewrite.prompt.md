---
mode: agent
description: The standard end-to-end pipeline, agent-driven -- one package, a named few, or a whole folder -- extract, walkthrough, generate, fill (two AI boundaries), apply, build, test, coverage. Sequences the existing per-step prompts; does not restate their command lines. Use THIS whenever a Claude/Copilot chat session is available -- Run-Pipeline.ps1 is the mechanical fallback for when one is not (see its own doc comment).
---

# ssisx: rewrite one package, a named few, or a whole folder -- end to end

- **Input folder:** `${input:inputPath:Absolute path to the SSIS folder/project}`
- **Package(s):** `${input:package:Package ObjectName(s), comma-separated -- leave blank for EVERY package in inputPath}`
- **Target framework:** `${input:framework:net8.0 or net10.0 - the CLIENT's runtime}`

If `<framework>` was left blank or looks like a placeholder, **ask before generating** -- see
`ssisx-generate.prompt.md`'s own rule about this; do not default to `net10.0`.

This prompt **orchestrates the other prompts by reference**. Every step below names the prompt
that actually does the work rather than restating its commands -- if a step's own prompt file
changes, this one does not need to change with it. `--package <package>` is threaded through
every step: this never touches any package not in scope.

**Why this is agent-driven rather than one script call**: gap-filling (Steps 4 and 6 below)
needs a human/AI actually reading a work packet and writing something, which a plain script
cannot do -- it can only run deterministic commands and report an exit code. Running this
prompt means YOU (the agent) take each step as your own tool call, so you can react mid-flight:
read the actual gap report, draft a fill, and stop for the human's confirmation, rather than a
script silently continuing past an unfilled gap into a build failure. `Run-Pipeline.ps1`
(`Tools/SsisExtractor/scripts/Run-Pipeline.ps1`) exists for the opposite case -- no AI session
available at all -- and is purely mechanical for exactly this reason: it can apply fills already
on disk, but it can never write one.

## Scope -- one package, a named few, or the whole folder

If `<package>` names one or more packages (comma-separated), run every step below once per named
package, in the order given, completing one package's own Steps 0-8 before starting the next.

If `<package>` was left blank, first run Step 1 (extract) once for the whole `<inputPath>`, read
its own summary for the full list of package names, then run Steps 0/2-8 once per package in that
list, in the order the summary lists them -- Step 1 itself does not repeat per package, since
`ssisx extract`/`report` already cover the whole folder in one call.

Either way, **complete one package fully (through whichever step it reaches, including stopping
at an AI boundary) before starting the next** -- never batch every package's Tier-1/2 gaps into
one combined review; each package's own gaps are reviewed and confirmed on their own. Give a
combined closing report (see the end of this file) once every package in scope has been run.

## Step 0 -- detect where we're picking up (filesystem-detected, not a state file)

Walk this decision tree TOP TO BOTTOM and stop at the first row that matches -- each row is an
exclusive "if this, resume HERE" check, not an independent fact to note; do not fall through to
a later row once one has matched, and do not treat an unmatched later row as "there is nothing
left to do."

1. `out/packages/<package>.spec.json` **missing** -> resume at **Step 1**.
2. `out/generate/<package>/` **missing** -> resume at **Step 3** (Step 2 is optional/advisory
   and never blocks generation, so a re-run does not need to repeat it unless asked).
3. `out/generate/<package>/` exists, and `dotnet build out/generate/Generated.slnx` for this
   package still fails (`CS8795` or `generate-report.md` lists an open Tier-1/2 gap):
   - `fills-library/<package>/` (the `.cs`/`.decisions.json` files directly under it, **not**
     its `Tests/`/`TestData/` subfolders) is **empty** -> resume at **Step 4** (nobody has
     written the Tier-1/2 fills yet).
   - It is **non-empty** -> resume at **Step 5** (fills already exist on disk; apply and
     rebuild rather than re-triaging or re-porting work that's already done).
4. The build above is clean (every Tier-1/2 gap resolved), and `dotnet test --filter
   Category!=Integration` still shows an open `TEST-ORACLE`/`LOCAL-DATA` gap for `<package>`:
   - `fills-library/<package>/Tests/` and `fills-library/<package>/TestData/` are both
     **empty** -> resume at **Step 6**.
   - Either has content -> resume at **Step 7** (apply and re-test rather than rewriting fills
     that already exist).
5. Build clean AND no open `TEST-ORACLE`/`LOCAL-DATA` gap -> everything is already done. Report
   that and stop; do not regenerate or re-apply anything that already succeeded.

Re-running this prompt after any partial progress is always safe: each step below is itself
idempotent (re-reads what's on disk rather than assuming prior state).

## Step 1 -- extract + survey

Follow `ssisx-extract.prompt.md` in full, scoped to `<inputPath>`. Confirm `<package>` appears
in its summary before continuing -- if it doesn't, stop and report the mismatch rather than
guessing a different package name.

## Step 2 -- walkthrough (advisory, never blocks)

Follow `ssisx-walkthrough.prompt.md` in full, scoped to `<package>`. This is read-only review
material for a human, not a gate -- if it fails for an unexpected reason, note it and continue
to Step 3 anyway rather than treating it as a pipeline failure.

## Step 3 -- generate

Follow `ssisx-generate.prompt.md` in full, scoped to `<package>` and `<framework>`. Its own
Step 3 already triages gaps by tier -- do not re-triage them here.

## Step 4 -- Tier-1/Tier-2 gap fills — **AI boundary, stop here**

If `generate-report.md` shows any Tier-1 (`MissingDatum`) or Tier-2 (`MissingLogic`) gap for
`<package>`, follow `ssisx-fill.prompt.md` Steps 0-2 (read the packets, write the fills). Then
**stop and report** exactly what was written and what still needs a human decision (a Tier-1
answer needs a real `ConfirmedBy` -- never fabricate one). Do not proceed to Step 5 until told
to continue, even if every fill looks confidently correct -- an AI's own confidence is never
sign-off (this is the property the whole gap taxonomy exists to protect).

If there are no Tier-1/2 gaps for this package, report that and continue straight to Step 5.

## Step 5 -- apply + build

Follow `ssisx-fill.prompt.md` Step 3 (apply-fills, then build). Report the result. A remaining
`CS8795` means Step 4's fills are incomplete, not that this step failed -- go back to Step 4.

## Step 6 -- test-oracle fills + local data — **AI boundary, stop here**

If `dotnet test --filter Category!=Integration` (from `ssisx-test.prompt.md` Step 3) shows any
`TEST-ORACLE`/`LOCAL-DATA` gap still open for `<package>`, follow `ssisx-test.prompt.md`
Steps 0-2. Then **stop and report** exactly what was written, same reasoning as Step 4 -- a
test's expected value or a sample file's realism is still a judgement call, not something to
wave through unreviewed.

If there are no such gaps, report that and continue straight to Step 7.

## Step 7 -- apply + build + test

Follow `ssisx-test.prompt.md` Step 3 in full (apply-fills, build, `dotnet test --filter
Category!=Integration`). Report the final result: build status, test pass/fail/skip counts, and
any gap of any tier still outstanding for `<package>`.

## Step 8 -- code coverage

Run `Tools/SsisExtractor/scripts/Run-Coverage.ps1 -GeneratedRoot <out>/generate -Package
<package>` -- this is a purely mechanical utility (parses `coverage.cobertura.xml`, no AI
involved), so it's fine to call directly even though the rest of this pipeline is agent-driven.
Read the row it prints/writes to `coverage-report.md` for `<package>` and report both figures:
"own generated code" (the honest one for a starter-test baseline) and the blended "Line %"
(lower, dragged down by the shared `Etl.Core` library's own infrastructure -- see
`Tools/COPILOT_TESTING_GUIDE.md`'s coverage section for why). Never deletes a prior
`TestResults\` folder -- safe to re-run any time.

## Closing report

If this ran for more than one package (Scope section above), give ONE combined summary at the
end covering every package: which fully completed vs. which stopped at an AI boundary and is
waiting on a human, plus per-package build/test/coverage results. For a single package, or as
the per-package summary within that combined report: which steps executed vs. were skipped as
already-done, both AI boundaries' outcomes, the final build/test/coverage result, and the
reminder to `git add fills-library && git commit` if anything was written to it.

## Logging

Each step already logs itself per its own prompt's `## Logging` section -- do not duplicate
that here. Append one closing line to `session-logs/<yyyy-MM-dd>-session.md` summarizing this
whole `/ssisx-rewrite` run (every package in scope) and where each stopped (if it stopped at an
AI boundary).
