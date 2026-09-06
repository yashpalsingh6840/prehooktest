---
mode: agent
description: One-command pipeline for rewriting a single named package -- extract, walkthrough, generate, fill (two AI boundaries), apply, build, test. Sequences the existing per-step prompts; does not restate their command lines.
---

# ssisx: rewrite one package, end to end

- **Input folder:** `${input:inputPath:Absolute path to the SSIS folder/project}`
- **Package:** `${input:package:Package ObjectName, e.g. LoadEmployees}`
- **Target framework:** `${input:framework:net8.0 or net10.0 - the CLIENT's runtime}`

If `<framework>` was left blank or looks like a placeholder, **ask before generating** -- see
`ssisx-generate.prompt.md`'s own rule about this; do not default to `net10.0`.

This prompt **orchestrates the other prompts by reference**. Every step below names the prompt
that actually does the work rather than restating its commands -- if a step's own prompt file
changes, this one does not need to change with it. `--package <package>` is threaded through
every step: this never touches any other package in `<inputPath>`.

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

## Closing report

Summarize the whole run: which steps executed vs. were skipped as already-done, both AI
boundaries' outcomes, the final build/test result, and the reminder to `git add fills-library &&
git commit` if anything was written to it.

## Logging

Each step already logs itself per its own prompt's `## Logging` section -- do not duplicate
that here. Append one closing line to `session-logs/<yyyy-MM-dd>-session.md` summarizing this
whole `/ssisx-rewrite <package>` run and where it stopped (if it stopped at an AI boundary).
