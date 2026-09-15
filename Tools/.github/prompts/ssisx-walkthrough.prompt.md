---
mode: agent
description: Run svk walkthrough (+ sampledata) for one named package (or a named few) -- a human-readable execution-order review document, deterministic, read-only against ssisx's own extracted spec.
---

# svk: walkthrough (+ sampledata)

- **Extracted spec folder:** `${input:specPath:The --out folder from a prior ssisx extract run (e.g. out)}`
- **Package(s):** `${input:packages:Package ObjectName(s), comma-separated}`

This is `svk`, a **separate, standalone tool** from `ssisx` -- its own `.slnx`
(`SsisValidationKit/SsisValidationKit.slnx`), also built as project entries inside
`SsisExtractor.slnx` so one build produces both. It never edits anything under
`SsisExtractor/`, and it never generates C# -- it only reads `ssisx extract`'s own spec output
and writes review documents plus deterministic sample data.

## Known-good constraints -- do not re-derive these

- `.github/copilot-instructions.md` is already loaded.
- `svk extract`/`svk generate` do **not exist** -- `svk`'s only two commands are `sampledata`
  and `walkthrough`. Extraction is `ssisx extract`'s job (`ssisx-extract.prompt.md`); run that
  first if `<specPath>/packages/` doesn't already have the package's `.spec.json`.
- `walkthrough` and `sampledata` are both **fully deterministic** -- same input, same `--seed`,
  byte-identical output. Neither is an AI step; there is nothing to triage here.
- `walkthrough`'s own `claims/<Package>.claims.json` is **human-owned, never overwritten** once
  it exists -- re-running only adds stubs for rules that weren't in the file yet. Do not touch
  an existing entry in it.
- `sampledata`'s `<Lookup>.reference.sql` only exists for a Lookup with a resolvable join key
  (`JoinToReferenceColumn`) -- one with no resolvable key is skipped with a stated reason in the
  console output, never guessed.
- Pass the package list as one quoted comma-separated argument. This is required for package
  names containing spaces, for example `--package "DWH - FactOrders 3,ETL"`.

## Step 1 -- bootstrap

Resolve only `$svk` using the existing-binary portion of the bootstrap block in
`ssisx-extract.prompt.md` (the `SsisValidationKit/src/SvkCli/bin` search). This prompt does not
need `ssisx` or `dotnet`. If `svk` is absent, run the documented solution build once, then
resolve `svk` again; do not rebuild when it already exists.

## Step 2 -- run walkthrough, then sampledata

```powershell
& $svk walkthrough --spec "<specPath>" --out "<specPath>/walkthrough" `
  --claims "<specPath>/walkthrough/claims" --package "<packages>"
$walkthroughExit = $LASTEXITCODE
"WALKTHROUGH_EXIT=$walkthroughExit"

& $svk sampledata --spec "<specPath>" --out "<specPath>/sampledata" `
  --package "<packages>" --rows 20 --seed 42
$sampledataExit = $LASTEXITCODE
"SAMPLEDATA_EXIT=$sampledataExit"
```

Pin `--seed` to a fixed value (`42` unless told otherwise) so this step is reproducible run to
run -- do not omit it and let `svk` pick a random one.

Do not claim success until both exit codes are printed and both `<specPath>/walkthrough` and
`<specPath>/sampledata` exist. The CLI places generated files in nested subfolders:
walkthrough reports are under `<specPath>/walkthrough/walkthrough/`, and sample-data files are
under `<specPath>/sampledata/sampledata/`. If the shell is in a continuation prompt or the
output/exit codes were not captured, rerun both commands in a fresh non-interactive PowerShell
invocation before inspecting files.

## Step 3 -- summarize, then STOP

Read only `<specPath>/walkthrough/walkthrough/<Package>.walkthrough.md`'s own structure (component count,
whether any concurrent-execution callout appears) and the `sampledata` console output (which
files were written, which Lookups were skipped and why). Do not open every generated `.sql`/
`.csv` file line by line.

Report: per package, the component count, whether the walkthrough flagged any SSIS-side
concurrency, and which sample-data files were written vs. skipped (with the skip reason).

Then stop. Generating C# is `ssisx-generate.prompt.md`'s job, not this one.

## Logging

Append to `session-logs/<yyyy-MM-dd>-session.md`: this prompt, the exact commands, their
output, exit codes, and a bump of the counters. Do not reread the entire session log just to
verify the append; report the log path after the write.
