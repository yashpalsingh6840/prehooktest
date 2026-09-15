---
description: Batch-run Agent 1 (ssisx-batch-phase-a / ssisx-batch-phase-b) across every project found under an input root, in parallel, with a generic gap-count threshold gating Phase B and one consolidated summary report at the end.
---

# ssisx-batch: run Agent 1 across an input root

Arguments given: `$ARGUMENTS`

Parse as free text: **input root** (a folder containing one project, or several project
folders), **output root**, optional **target framework** (`net8.0`/`net10.0`, default
`net10.0` -- ask first if a specific client runtime clearly matters here and wasn't given),
optional **gap threshold** (default **20**, same meaning as in
`Tools/.github/prompts/ssisx-batch-generate.prompt.md`).

This command is the orchestration layer that `ssisx-batch-generate.prompt.md` and its two
agents (`ssisx-batch-phase-a`, `ssisx-batch-phase-b`) don't provide on their own: deciding how
many separate project invocations the input root actually contains, launching Phase A for each
in parallel, applying the gap-count threshold per project once its own Phase A reports back, and
writing one consolidated summary at the end. It does not duplicate either agent's own logic --
it only decides *what* to call them with and *when*. Never encode anything specific to any
particular project found this way; the discovery/threshold rules below apply identically no
matter what the input root contains.

## Step 1 -- discover projects under the input root

- If the **input root** itself directly contains a `.dtproj`, `.dtsx`, or `.ispac` file (at any
  depth), treat it as **one single project** -- go to Step 2 with exactly one project:
  `(InputPath = <input root>, OutputPath = <output root>)`.
- Otherwise, list its **immediate child directories only** (one level -- do not recurse further
  for this decision). For each child that contains a `.dtproj`, `.dtsx`, or `.ispac` **anywhere
  within it** (search recursively just to detect this, not to enumerate anything further), treat
  that whole child directory as **one project**:
  `(InputPath = <input root>\<ChildName>, OutputPath = <output root>\<ChildName>)`.
  A single project's own `generate --recursive` already walks however many nested `.dtproj`s or
  packages live inside it -- never spawn a separate agent per nested sub-project.
- Report the resulting project list (name + InputPath, and package/file count if cheap to get)
  before continuing. If one child looks enormous and mostly unrelated (e.g. the real package is
  buried a few levels deep inside a much larger, mostly-irrelevant repo), say so and suggest
  narrowing that one project's InputPath to the actual subfolder instead of silently pointing
  `ssisx extract` at everything.

## Step 2 -- Phase A, one agent per project, in parallel

Launch one `ssisx-batch-phase-a` agent per project from Step 1, **all in a single message**
(multiple Agent tool calls together) so they run concurrently. Each agent's prompt must give it
that project's own InputPath/OutputPath and the resolved Framework.

They run in the background -- do not poll; you'll be notified as each one completes. Do not
start Step 3 for a project until its own Phase A has actually reported back.

## Step 3 -- apply the threshold per project

For each project's Phase A report, compare its Tier-1 (`MissingDatum`) + Tier-2 (`MissingLogic`)
gap count against the threshold:

- **Zero such gaps:** no Phase B needed -- already fully generated, either clean or blocked only
  on Tier-3 (`MissingToolSupport`)/Advisory gaps that no fill can touch anyway.
- **Within the threshold:** launch a `ssisx-batch-phase-b` agent for that project (same
  InputPath/OutputPath/Framework as its own Phase A run).
- **Exceeds the threshold:** do **not** launch Phase B automatically. Hold it, and once every
  project's Phase A has reported, ask the user explicitly (naming the gap count) whether to
  proceed with Phase B for that specific project -- never guess or auto-proceed past the
  threshold.

As with Step 2, launch every Phase-B-eligible project's agent together in one message, not one
at a time.

## Step 4 -- consolidated summary

Once every project has either completed Phase B or been resolved (skipped as zero-gap, or held
pending the user's threshold decision), write one `<output root>\BATCH-SUMMARY.md`: a table of
project -> packages found -> build result -> test result -> coverage -> gap counts by tier ->
fills written (flagged UNREVIEWED, per `ssisx-batch-generate.prompt.md`'s own report format) ->
notable issues. Report the same summary back in chat, not only to the file.

## Logging

Each Phase-A/Phase-B agent already logs its own run per
`ssisx-batch-generate.prompt.md`'s own Logging section -- do not duplicate that here. Append one
closing line to `session-logs/<yyyy-MM-dd>-session.md` summarizing this whole `/ssisx-batch` run:
every project discovered, which got Phase B, which are held on the threshold, and the summary
file's path.
