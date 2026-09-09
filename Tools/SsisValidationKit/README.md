# SsisValidationKit (`svk`)

Validation tooling for the SSIS-to-C# rewrite. A **standalone sibling** of `Tools/SsisExtractor`
-- its own `.slnx`, its own console app -- that only ever *reads* `ssisx extract`'s output.
Nothing under `Tools/SsisExtractor` is ever edited, and its own `.slnx` is never touched;
`SvkCore` takes build-time `ProjectReference`s into `Ssis.Extract.Model` (the parsed spec
shape) and `Ssis.Extract.Dtsx` (pure functions -- `PackageTree`, `PrimaryKeyInference`,
`ConformanceRulesBuilder` -- that already operate on that parsed shape, not the XML parser).

Full design and rationale: `Docs/SsisValidationKit-Plan.md`.

## What it answers

| Question | Command |
|---|---|
| Starting input data (CSV/SQL seed, incl. a Lookup's own reference table) | `svk sampledata` |
| Runnable DDL for every table a package touches | `svk sampledata` (`schema.sql`) |
| Per-component inputs/outputs, real execution order, parallel branches | `svk walkthrough` |
| A place to record "I checked this component" per component | `svk walkthrough` (claims file) |

## Usage

```
# 1. Extract, using the UNMODIFIED ssisx tool -- this is svk's only input.
dotnet run --project ../SsisExtractor/src/Ssis.Extract.Cli -- extract --input <dtsx-folder> --out <spec-dir> --recursive

# 2. Synthetic sample data + schema, deterministic (same --seed -> byte-identical output).
dotnet run --project src/SvkCli -- sampledata --spec <spec-dir> --out <out-dir> [--package Name1,Name2] [--rows 20] [--seed N]

# 3. Execution-order review document, one per package, with a linked conformance rule
#    and a human-owned validation-status claims file per package.
dotnet run --project src/SvkCli -- walkthrough --spec <spec-dir> --out <out-dir> [--claims <dir>]
```

Build the whole thing with `dotnet build SsisValidationKit.slnx` (or open the `.slnx`);
publish a single exe the same way `ssisx` itself does if it needs to travel to a client site.

## `sampledata` output, per package (`<out>/sampledata/<Package>/`)

- `<Component>.csv` -- one per DELIMITED Flat File source, header + N synthetic rows, type/
  length-correct against the source's own declared external-metadata schema.
- `<Component>.txt` -- one per FIXED-WIDTH/RaggedRight Flat File source, instead of the `.csv`
  above: a positional layout (no delimiter), each value padded/truncated to its own connection-
  manager-declared column width (right-pad with spaces, silently truncate an over-length value --
  the measured real SSIS write convention), with `HeaderRowsToSkip` control records written as
  `SKIP` placeholder lines.
- `<Component>.xlsx` -- one per Excel source: a real, openable workbook (via ClosedXML), with the
  worksheet name and header-row setting (`HDR=YES/NO`) resolved from the EXCEL connection
  manager. Columns are written in the exact ordinal order the generated reader expects (an Excel
  source is read purely by position, never by column name).
- `<Component>.sql` -- one per SQL-shaped source (OLE DB/ADO NET), `CREATE TABLE` + `INSERT`.
- `<Component>.expected.sql` -- one per destination, `CREATE TABLE` only (load the generated
  C#'s own output here for comparison).
- `<Lookup>.reference.sql` -- one per Lookup with a resolvable join key (`JoinToReferenceColumn`,
  the same property `ssisx generate` itself reads -- never guessed). Seeded from a key pool
  shared with the main flow's own join column, so ~70% of rows match a reference row (exercises
  the match path) and the rest deliberately don't (exercises the no-match path). A Lookup with
  no resolvable key is skipped with a stated reason, never guessed.
- `schema.sql` -- every destination table in the package, one file, PK lines marked `INFERRED`
  (a `.dtsx` carries no real primary-key concept -- see `PrimaryKeyCandidateSpec`'s own doc
  comment in `Ssis.Extract.Model`).

Verified end to end against this PoC's own 4 real packages plus a real Lookup-bearing package
from the `SSIS_From_Sandeep` portfolio (`Package_Transforms`'s `LKP_Country`): output is
byte-identical across repeated runs with the same seed, and the Lookup's own reference table
and main-flow source genuinely share key values in the intended ~70/30 match/no-match split.

## `walkthrough` output, per package (`<out>/walkthrough/`)

- `<Package>.walkthrough.md` -- regenerated every run. Real SSIS execution order (not document
  order), with an explicit callout wherever `ssisx extract`'s own `Dag.ParallelLevels` says SSIS
  ran executables concurrently -- confirm the rewrite's own sequencing there is acceptable, or
  flag it for redesign. Under each Data Flow Task, every component's inputs/outputs/lineage
  source plus its matching `ssisx conformance` `DATA-FLOW:<RefId>` rule ID. Also embeds a Mermaid
  control-flow diagram (execution order/parallel branches at the package/container level) and a
  Mermaid data-flow diagram per Data Flow Task (component-to-component edges) -- both render
  inline wherever the `.md` is viewed (GitHub, most Markdown-aware editors).
- `claims/<Package>.claims.json` -- **human-owned, never overwritten** once it exists (same
  lifecycle as `ssisx conformance`'s own claim files): `Status` (`NotValidated`/`Validated`/
  `Blocked`), `Reviewer`, `ReviewedDate`, `Note`, keyed by the linked rule ID. Re-running
  `walkthrough` only adds stubs for rules that didn't exist in the file yet; an existing entry
  is never touched. Verified: hand-editing an entry and re-running survives the edit and shows
  up correctly in the regenerated `.md`.

## Known limitations, stated rather than hidden

- A Lookup reference query's table name is resolved by a plain regex (`FROM`/`JOIN <table>`),
  not a real SQL parser -- deliberately avoiding a `Microsoft.SqlServer.TransactSql.ScriptDom`
  dependency for this generator. A query with more than one `FROM`/`JOIN` falls back to the
  Lookup's own component name; the generated SQL still runs, the table name just may need a
  rename before use.
- Synthetic values are schema-correct (type/length/precision/scale), not business-realistic --
  there is no attempt to bias values toward boundary conditions a Conditional Split or
  expression might branch on.
- The Lookup key-overlap match is by column NAME (case-insensitive) against the join input
  column's own name, not a full lineage trace -- correct for the common case (a Lookup fed
  directly or through a passthrough), not guaranteed when a same-named column exists
  legitimately elsewhere with unrelated meaning.
- Synthetic values are NEVER null for an Excel source specifically (unlike every other source
  kind, which injects a null in ~10% of rows) -- the generated Excel reader has no null-handling
  at all (`IExcelDataReader.GetDouble`/`.GetString` unconditionally), so a blank cell would crash
  it. A directly-usable sample was judged more valuable than exercising a null path this reader
  can't tolerate yet.
