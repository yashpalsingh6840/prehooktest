# Validation tooling for the SSIS rewrite — a new, standalone sibling tool

## Context

You asked five concrete questions about how to gain confidence in the SSIS-to-C# rewrite:
how to extract/prepare starting input data, how to see every component/task's inputs and
outputs plus execution order, how to extract parallel-execution structure, how to know the
database tables/columns a package touches, and how to get a traceability view to validate
every component. You also said not to make any changes to `D:\PoC\SSIS\Tools\SsisExtractor`.

Two rounds of research against the real `ssisx` tool (`Tools/SsisExtractor`) found that most
of the raw data you're asking for **already exists** — it just isn't packaged into anything a
human can read top-to-bottom, and two pieces (synthetic test data, DDL for the tables touched)
don't exist in any form yet. The plan below builds exactly the missing pieces, as a new
project that sits *next to* `Tools/SsisExtractor` and only ever *reads* its output — never
edits, never adds a command to it, never touches its `.slnx`.

| Your ask | Already exists (in `ssisx extract`'s `spec.json`) | What's genuinely new |
|---|---|---|
| 1. Starting input data (CSV/flat file/Excel) | Full source-side schema (column names/types/lengths) | **Yes — no generator exists today.** New: `sampledata` |
| 2. Per-component I/O + call sequence | `PackageSpec.Executables`, `Dag.TopologicalOrder`, per-component `Inputs`/`Outputs`/`Lineage.Edges` — complete | No new extraction; needs one readable rendering |
| 3. Parallel execution | `Dag.ParallelLevels` (already derived, already consumed by `ssisx generate`'s advisory) | No new extraction; needs surfacing to a reviewer |
| 4. DB tables/columns touched | `TargetSchema` conformance rule + `spec.json`'s `ExternalMetadataColumns` (full column contract) + `PrimaryKeyInference` (best-guess PK) | **Yes — no runnable DDL exists today.** New: `schema.sql` (part of `sampledata`) |
| 5. Traceability/validation view per component | Nothing combines this into one document; `ssisx conformance` already has the right *pattern* (regenerated rules + a human claims file, never overwritten) but not this content | **Yes.** New: `walkthrough` |

So there are two genuinely new pieces of work, both consumers of the extractor's existing
`spec.json` contract, never editors of it.

## Governing rule

**Nothing under `Tools/SsisExtractor` is ever modified — no new file, no edit, no addition to
`SsisExtractor.slnx`.** The new tool is a separate solution. It may take build-time
`ProjectReference`s into `Ssis.Extract.Model` (the plain record/POCO types — `PipelineSpec`,
`PipelineComponentSpec`, `LookupPayload`, `SsisPipelineTypeMap`, etc.) and `Ssis.Extract.Dtsx`
(pure functions that already operate on those parsed types — `ConformanceRulesBuilder`,
`PrimaryKeyInference` — not the XML parser itself), since a `ProjectReference` only adds a line
to the *new* project's own `.csproj` and changes nothing in the referenced project. Anywhere
existing logic isn't already public/reusable this way (e.g. `Ssis.Extract.Codegen`'s private
`TryDeriveLookupJoinKey`, or `Ssis.Extract.Sql`'s FROM-table parsing if it turns out not to be
public), the few lines involved get **reimplemented in the new tool**, never added upstream.

The new tool never re-parses `.dtsx` XML itself. Its only input is the `spec.json` files that
running the existing, unmodified `ssisx extract` already produces — that's the stable contract
(`docs/spec-schema.md`) to build against.

## New project layout

```
Tools/
  SsisExtractor/        <- untouched
  SsisValidationKit/     <- new, its own .slnx, its own git history going forward
    src/
      SvkCli/                       (console entry point, command dispatch)
        Program.cs
        SampleDataCommand.cs
        WalkthroughCommand.cs
      SvkCore/                      (pure logic, no I/O)
        SampleDataPlanner.cs        (discovers sources/destinations per package)
        ValueSynthesizer.cs         (seeded, type-directed synthetic values)
        SharedKeyPool.cs            (Lookup match/no-match key overlap)
        SchemaEmitter.cs            (CREATE TABLE from ExternalMetadataColumns + PrimaryKeyInference)
        ExecutionOrderBuilder.cs    (walks Dag.TopologicalOrder/ParallelLevels per container)
        WalkthroughClaimSpec.cs     (RuleId, Status, Reviewer, ReviewedDate, Note)
    SsisValidationKit.slnx
```

`SvkCore`/`SvkCli` reference `Ssis.Extract.Model.csproj` and `Ssis.Extract.Dtsx.csproj` via
relative `ProjectReference`s (`..\..\SsisExtractor\src\...`). No file inside `SsisExtractor/`
changes; its own `.slnx` is never touched — MSBuild resolves the referenced projects
transitively without them needing to share a solution file.

## Piece 1 — `svk sampledata` (answers asks 1 and 4)

For every package: reads `<out>/packages/<Package>.spec.json` (from a prior `ssisx extract`
run), finds every source and destination by which payload is populated on each
`PipelineComponentSpec` (`FlatFileSource`/`ExcelSource`/`OleDbSource`/`AdoNetSource` for
sources; `OleDbDestination`/`AdoNetDestination`/`FlatFileDestination` for destinations —
covers everything the extractor already models, per your answer), and generates:

- `schema.sql` — one `CREATE TABLE` per destination, columns from `ExternalMetadataColumns`,
  PK line from `PrimaryKeyInference` where confident (marked "inferred, verify" otherwise).
- `<Component>.csv` — synthetic rows for a Flat File or Excel source (Excel defaults to CSV in
  v1; no `.xlsx`-writing dependency exists anywhere in this codebase today, so a real `.xlsx`
  writer is a separate, explicitly-opt-in follow-up, not silently substituted).
- `<Component>.sql` — `CREATE TABLE` + `INSERT` for a SQL-shaped source/reference table.
- `<Component>.expected.sql` — `CREATE TABLE` only, for each destination (so a reviewer can
  stand up an empty target and load the generated C#'s own output into it for comparison).

**Value synthesis**: every column resolves through `SsisPipelineTypeMap`'s canonical type
vocabulary (with a small adapter for flat-file columns, which use `"DT_WSTR"`-style names
instead of the pipeline's own `"wstr"`). Values respect declared length/precision/scale.
Generation is **seeded** (`--seed`, fixed default) so two runs produce byte-identical files —
this has to be diffable to be trustworthy.

**Lookup-aware key overlap**: a Lookup's join key (`JoinToReferenceColumn`/
`CopyFromReferenceColumn`, already-extracted generic properties — the same pair
`PackageGenerator.TryDeriveLookupJoinKey` reads, reimplemented here in a few lines) drives a
shared key pool per `(package, Lookup RefId, column)`: some values land in both the main flow
and the reference table (exercises the match path), some only in the main flow (exercises the
no-match path). Without this, two independently-random key columns would almost never collide,
and the generated code's no-match handling would never be exercised. When the reference query
is anything more than a plain `SELECT ... FROM <one table>`, this is skipped with a stated
reason rather than guessed.

CLI: `svk sampledata --spec <extract-out-dir> --out <dir> [--package <name>] [--rows N=20] [--seed N]`

## Piece 2 — `svk walkthrough` (answers asks 2, 3, and 5)

One document per package, read top to bottom in real SSIS execution order:

- Walks `ExecutionOrderBuilder` over each container's own `Dag.TopologicalOrder` (the `Dag` is
  scoped per-container, not global, so this recurses container by container).
- Calls out every `Dag.ParallelLevels` group explicitly in the text — *"SSIS ran these N
  executables concurrently: X, Y"* — right where a reviewer would otherwise assume strict
  sequencing. This is the direct answer to ask 3: the data already exists
  (`Dag.ParallelLevels`), it just needs to be visible instead of buried in JSON.
- Under each Data Flow Task, lists its pipeline components in order, each with a compact table
  of inputs/outputs (name + type) and its lineage source, read straight from
  `Lineage.Edges` (already keyed by `ToColumnRefId` — no new traversal needed).
- Each component row inlines the matching `ssisx conformance` rule ID, computed in-process by
  calling `ConformanceRulesBuilder.Build(pkg)` (pure, cheap, no dependency on `conformance`
  having been run separately) and filtering to the `DataFlow` category indexed by
  `RefId` — so a reviewer sees the formal obligation next to the plain-English row.

**The claims file — same pattern as `ssisx conformance`, deliberately borrowed:** rules/tables
regenerate on every run; a separate, human-owned file never gets overwritten by the tool, only
stubbed once if absent. Per your answers: one claims file per package
(`<out>/walkthrough/claims/<Package>.claims.json`), each entry carrying `Status`
(`Validated`/`NotValidated`/`Blocked`), `Reviewer`, `ReviewedDate`, and a free-text `Note`
alongside the linked conformance `RuleId` — so a reviewer records both "did I check this" and
"what I found" in the one place they're already reading.

CLI: `svk walkthrough --spec <extract-out-dir> --out <dir> [--claims <dir>] [--package <name>]`

## How this actually gets you confidence (not just files)

Two usable tiers, worth stating so synthetic data isn't mistaken for the whole answer:

1. **Available now, no probe SSIS environment needed.** Run `ssisx generate` (existing,
   unmodified) against a package → build the generated project against `Tools/Etl.Core` → load
   `sampledata`'s CSV/SQL output into a local test database created from its own `schema.sql`
   → run the generated exe → confirm it doesn't crash and lands the expected row counts. This
   catches structural/type bugs today, cheaply, repeatably.
2. **Needs the probe VM (already an open item from the ClientMigration scan).** The repo
   already has a proven, separate gate-3 harness at `Validation/` (`CorpusCapture` +
   `GateRunner`) that runs real SSIS and the generated C# against *identical* input and diffs
   row-for-row — this is the real semantic-fidelity gate. Point it at the same seeded sample
   data `sampledata` produces (reusing one seed across both tiers, rather than inventing two
   separate datasets) once a probe environment exists.

## Open items to resolve during implementation, not before

- Confirm `Ssis.Extract.Sql`'s public surface for extracting a lookup query's single `FROM`
  table; if nothing public covers it, write a small local parser in `SvkCore` rather than
  changing `Ssis.Extract.Sql`.
- Validate `--rows` defaults (especially Lookup reference-table sizing) against a real flow —
  this repo's own `DFT_LookupAndAggregate`-shaped package is a good target.
- Whether `sampledata`'s SQL output should stay plain, human-runnable `.sql` scripts (the
  recommended default — matches this project's own `Sql/*.sql` convention, zero new
  dependency) or gain an optional `--load <connection-string>` to execute them directly.

## Verification

- Run unmodified `ssisx extract` against this repo's own real packages (`SSIS/`, or the
  `RBC_Demo_ETL`/`SSIS_From_Sandeep` portfolio already checked out) to produce `spec.json`.
- Run `svk sampledata` twice with the same `--seed`; diff the output — must be byte-identical.
- Run `svk sampledata` against a package with a real Lookup (`DFT_LookupAndAggregate`-shaped);
  confirm the reference table and main-flow key columns actually share some values.
- Run `svk walkthrough`, hand-edit one claim, re-run — confirm the edit survives.
- End-to-end smoke test: `ssisx generate` a simple package (e.g. `LoadEmployees`), build it,
  load `sampledata`'s output into a schema built from its own `schema.sql`, run the generated
  exe, read the target table back — confirms the whole chain works together, not just each
  piece in isolation.
