# Repo custom instructions -- read this before doing anything

This workspace holds **`ssisx`** (an SSIS-to-C# extractor/generator) and **`Etl.Core`** (the
runtime library generated code depends on). Full reference: [`COPILOT_GUIDE.md`](../COPILOT_GUIDE.md)
-- read it before any non-trivial task. This file is the short hard-rule list; workflow DETAIL
lives in the prompt files (table below), which only load when invoked.

**Inside a GENERATED output folder instead** (e.g. `Generated.slnx` opened directly)? Stop, read
that folder's own `HOW-TO-FILL-GAPS.md`. This file assumes `Tools\` itself is open.

## Hard rules

1. **Never re-implement, patch, or "improve" `ssisx`/`Etl.Core`.** Run the CLI; fill only the
   human-facing gaps it explicitly asks for. If a command errors, read the message first -- don't
   guess a source-level fix.
2. **Scope `generate`/`report`/`conformance`/`testgen` to `--package <name>`** (repeatable/
   comma-separated) unless told to run the whole portfolio. `extract` is cheap and read-only, so
   an unscoped survey is fine.
3. **`--etl-core Etl.Core` and `--fills fills-library` are mandatory on every `generate`/
   `apply-fills` call, no exceptions.** Omitting `--etl-core` leaves the solution unable to build;
   omitting `--fills` sends fill work into the disposable `out/`, where it is lost on regen.
4. **`--framework` is not sticky -- pass it every call, matching the client's real .NET runtime.**
   Ask if unknown; never silently default to `net10.0` (it also pins a different EF Core major
   version).
4a. **`--storage s3` only when the client wants S3 -- and pass it every call, like `--framework`.**
   Default is local folders. In S3 mode leave `Storage:Bucket`/`Region` for the human; never invent
   a bucket or put AWS credentials in any file.
5. **Never verify generated code against a live DB, real client files, or a real SSIS run** --
   none exist here. The limit is `dotnet build` + `dotnet test --filter Category!=Integration`.
   One deliberate exception: a `LocalFileSourceData` packet is explicitly asking for a realistic
   sample file under `TestData/<exact name>` -- that's the job, do it when asked.
6. **A `GenerationGap` is not a bug to route around.** Read its work packet under
   `out/gaps/<Package>/`; never invent a value/logic it says is missing. Tier table below.
7. **`out/gaps/` (auto-written questions) and `fills-library/` (hand-authored answers) are
   opposite things.** Every fill -- Tier-1 decisions, Tier-2 ports, TEST-ORACLE/LOCAL-DATA --
   goes in `fills-library/<Package>/...` at the `Tools\` root, **never** `out/fills/...` (real
   work has been silently lost there before, more than once). Tell me to
   `git add fills-library && git commit` after writing to it. If you find work stranded under
   `out/fills/`, move it into `fills-library/` and re-run `apply-fills`.
8. **Never hand-edit anything under `out/generate/`.** It is disposable and rebuilt every run.
   `generate` itself now cleans a package's own previous output before rewriting it (preserving
   `Fills/`/`TestData/` always) -- that is the tool's own scoped exception, not something you do
   yourself; a whole stale package folder is only ever reported, never deleted.
9. **Never invent an internal/example path** for `--input`/`--out`/`--fills`/`--etl-core`. Ask if
   unsure what folder to point at.
10. **Don't report a CLI step done from intent alone** -- capture the exit code and confirm the
    expected output exists before summarizing success.
11. **Budget your reads.** Never open `CLAUDE.md`, `Docs\*`, or `report-schema.md` -- internal dev
    history. `COPILOT_GUIDE.md` + `ssisx --help` are everything you need; trust `--help` on any
    disagreement.
12. **Read `<Package>\README.md` before writing/reviewing a generated test** -- the
    component-to-method map, regenerated fresh every run. `dotnet test --filter
    Category!=Integration` is the always-green baseline right after a fresh `generate`.
13. **A `TEST-ORACLE` packet is self-contained** -- don't open `PackageHarness.cs`/
    `COPILOT_TESTING_GUIDE.md` to confirm its testing-API block; the packet embeds it verbatim.
14. **`svk` is separate, optional, never a gate** -- two deterministic read-only commands
    (`walkthrough`, `sampledata`); it never generates C# or touches `generate`/`apply-fills`.
15. **`/ssisx-rewrite` is the standard command for "run the tool"/"generate this package".** It
    is DESIGNED to stop twice for a human (after Tier-1/2 fills, after test-oracle/local-data
    fills) -- never continue past either stop on your own judgement. Re-running it resumes from
    what's on disk. `Run-Pipeline.ps1` is the same steps as a script, only for when no chat
    session exists (CI); it applies fills already on disk but never writes one.

## Also

- Exit 98/99 = a genuine tool bug, not yours to fix -- point me at the written
  `ssisx-diagnostic-*.txt`/`svk-diagnostic-*.txt`.
- `apply-tests`/`/ssisx-more-tests` (coverage on an already-green package) is not `apply-fills`
  (closes gaps) -- don't confuse them.
- Never tail a full build/test transcript -- pipe `--nologo -v minimal`, read only the outcome +
  `error`/`FAILED` lines; resolve `$ssisx`/`$dotnet` once and reuse it.
- Never open a work packet whole -- its "SSIS semantics"/"Respond with" sections are boilerplate
  repeated across every packet in a run; read only the evidence/what-to-produce parts.
- `generate`/`apply-fills` each write a durable log (`out/generate.log`, `out/apply-fills.log`,
  fresh every run) -- check one before assuming a run "did nothing."

## Tier table -- full detail lives in the matching prompt file

| Tier | Kind | What to do |
|---|---|---|
| 3 `MissingToolSupport` | -- | Nothing to fill. Report "needs a tool enhancement" + `GapId`. |
| 1 `MissingDatum` | `LOOKUP-JOIN-KEY:` | Confirm in `fills-library/<Pkg>.decisions.json`. Never guess. |
| 1 `MissingDatum` | `ENCRYPTED-SECRET:` | No per-gap packet -- read `secrets-checklist.md` (one table, all of them); acknowledge in `fills-library/<Pkg>.decisions.json`. |
| 1 `MissingDatum` | `TEST-ORACLE:`/`LOCAL-DATA:` | `/ssisx-test` -- new test file / sample data file. |
| 2 `MissingLogic` | Script Task/Component | `/ssisx-fill` -- port to a partial-class seam. |

## Prompts (`.github/prompts/*.prompt.md`; `.claude/commands/*.md` mirrors each for Claude)

| Prompt | Does |
|---|---|
| `ssisx-extract` | Survey a folder/portfolio; has the `$ssisx`/`$dotnet` bootstrap. |
| `ssisx-walkthrough` | `svk walkthrough`/`sampledata` for one or a few packages. |
| `ssisx-generate` | `generate` one package (or a few); triage gaps by tier. |
| `ssisx-fill` | Port Tier-2 script seams into `fills-library`, apply, build. |
| `ssisx-test` | Port TEST-ORACLE/LOCAL-DATA gaps into `fills-library`, apply, build, test. |
| `ssisx-more-tests` | Add coverage to an already-green package (not a gap workflow). |
| `ssisx-rewrite` | The standard end-to-end pipeline (rule 15). |
| `ssisx-batch-generate` | Phase-A-only batch run across every project under an input root. |

## Getting unstuck

Rerun the failing command alone and read its own error text. `ssisx --help` is always accurate.
`CS8795` = an unfilled Tier-2 seam (working as designed) -- run `apply-fills` and check whether
`fills-library/<Package>/*.cs` still exists before assuming the port needs redoing. Still stuck:
stop and ask rather than editing generated code or the tool's own source.
