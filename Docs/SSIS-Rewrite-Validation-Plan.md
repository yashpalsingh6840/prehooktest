# Independent Left-vs-Right validation for the SSIS rewrite (`RewriteValidator`)

## Context

`Tools/SsisExtractor` (`ssisx`) both reads the original SSIS packages (Left) and generates the
C# rewrite (Right). Using `ssisx`'s own extraction to validate `ssisx`'s own generation grades
the tool's homework with its own answer key — a bug in `Ssis.Extract.Dtsx`'s SQL/graph reading
would agree with itself on both sides and every gate would pass. The user wants three
independent gates (SQL/schema, control-flow+data-flow graph, per-component input/output) built
so Left's ground truth comes from a source that shares **no code** with `ssisx`.

Grounded against real artifacts before designing: the generated rewrite at `D:\tmp\gen` (from
`RBC_Demo_ETL/Package_Advanced.dtsx` and `Package_Transforms.dtsx`) is a method-per-component
class (`internal async Task DFT_FlagCustomers(IUnitOfWork uow, CancellationToken ct)`, etc.) with
SQL text isolated in tiny static classes (`SQL_TruncateTargetsStatement.BuildStatement()`) —
this shape is what makes independent Right-side reading (via Roslyn, never `Ssis.Extract.Codegen`)
and independent Right-side invocation (the methods are real, callable units) both tractable.
This repo already has the right *pattern* for independent Left-side reading —
`Tools/SsisExtractor/src/Ssis.Extract.ObjectModel/PackageOracle.cs` drives the live SSIS object
model (`Application.LoadPackage` over COM) instead of parsing raw XML — but it only counts
things today; nothing in the repo reads SQL text off a *live* object-model instance (confirmed
by grep — only the reverse, `Ssis.Extract.FixtureBuilder`, which *writes* `SqlStatementSource`/
`SqlCommand`/`OpenRowset` when building synthetic fixtures). Building the new Left reader means
mirroring `FixtureBuilder`'s write calls in reverse (`Properties["SqlStatementSource"].GetValue`,
`CustomPropertyCollection` reads for `SqlCommand`/`OpenRowset`), following `PackageOracle`'s own
walk/GAC-wiring pattern exactly.

Three decisions already confirmed with the user: build **static-first** (parse both sides'
declared SQL/graph; no live SQL-capture instrumentation like Extended Events/EF interceptors in
this pass), Gate 3 stays at **Tier A (row counts) + Tier B (content at real source/sink
boundaries)** — no temporary instrumented `.dtsx` copies to tap mid-pipeline buffers — and the
tool lives at **`Tools/RewriteValidator/`**, a new sibling of `Tools/SsisExtractor`.

## Step 0 — persist this plan into the repo

Save this document as `Docs/SSIS-Rewrite-Validation-Plan.md`, matching this repo's own
convention for planning docs (`Emitter-Rewrite-Plan.md`, `Generated-Tests-Plan.md`,
`Migration-Validation-Plan.md`). Do this first, before any code, so the design is checked in
and reviewable independent of implementation progress.

## Governance boundary (the whole point of this tool)

Pin this as a doc comment on every new `.csproj`, the same way `SvkCore.csproj` already pins its
own boundary: **no project under `Tools/RewriteValidator/` may `ProjectReference` anything under
`Ssis.Extract.Model`, `Ssis.Extract.Dtsx`, `Ssis.Extract.Codegen`, or `Ssis.Extract.Cli`.** The
only sanctioned touch points on `ssisx`'s own output are: (a) reading the *generated `.cs` files
themselves* — that's the deliverable being validated, fair game; (b) reading `gaps.json` purely
as an **allow-list of already-admitted differences** (a Script Task ssisx knows it didn't
generate should not also fail Gate 2 as a graph mismatch) — never as ground truth for what's
*correct*; (c) delivering Gate-3 test content into a generated project via the **existing**
`MoreTests/` + `ssisx apply-tests` file-copy mechanism (mechanical copying, not logic reuse —
same reasoning that already lets `SvkCore` sit in the same solution as `ssisx` without crossing
the line). `spec.json`/`PackagePlan`/anything under `Ssis.Extract.Model.Analysis` is off-limits
as a Left-source, full stop.

## Layout

```
Tools/RewriteValidator/
  RewriteValidator.slnx
  src/
    RewriteValidator.Model/         net8.0 -- shared plain-data records only (LeftFacts/RightFacts/
                                     Gate1Result/Gate2Result/Gate3Result). No ProjectReference to
                                     anything else in this repo.
    RewriteValidator.LeftOracle/    net48, x64, GAC (mirrors Ssis.Extract.ObjectModel's wiring:
                                     <Import Project="..\..\..\..\Build\Ssis.Gac.props" />).
                                     Application.LoadPackage + object-model walk.
    RewriteValidator.RightOracle/   net8.0 -- Microsoft.CodeAnalysis.CSharp (Roslyn) reader of
                                     generated .cs files. Cross-platform, no GAC.
    RewriteValidator.Compare/       net8.0 -- Microsoft.SqlServer.TransactSql.ScriptDom 180.78.1
                                     (same version Ssis.Extract.Sql already pins; independent
                                     PackageReference, no shared code) + graph diff.
    RewriteValidator.Cli/           net8.0 -- `rvx` entry point.
  tests/
    RewriteValidator.Compare.Tests/
    RewriteValidator.LeftOracle.Tests/   (kept in a separate .ObjectModel-style slnx segment if
                                          needed, mirroring SsisExtractor.ObjectModel.slnx's own
                                          split of GAC-touching projects)
  gate3/
    MoreTests-templates/            hand/AI-authored xUnit content, applied per package into
                                     fills-library/<Package>/MoreTests/ via the EXISTING
                                     `ssisx apply-tests` command (see Ssis.Extract.Cli's
                                     ApplyFillsCommand-adjacent apply-tests path).
```

## Gate 1 — SQL & schema parity

**LeftOracle** (`RewriteValidator.LeftOracle`): `Application.LoadPackage`, recurse
`Executables`/`IDTSSequence` exactly like `PackageOracle.Walk`. For `Microsoft.ExecuteSQLTask`
task hosts: `taskHost.Properties["SqlStatementSource"].GetValue(taskHost)` +
`["Connection"]`. For pipeline components (`TaskHost.InnerObject is MainPipe pipeline` →
`ComponentMetaDataCollection`): read `CustomPropertyCollection` for `SqlCommand`/`OpenRowset`/
`TableOrViewName`/`AccessMode` (OLE DB/ADO NET/Excel Source, OLE DB Command, Lookup's reference
query), and `ExternalMetadataColumnCollection` for each destination's target table + column
list/types. Emit `left-sql-facts.json`: `{ Path, Kind, ConnectionManagerName, SqlText?,
TargetTable?, Columns[] }[]`.

**RightOracle**: `CSharpSyntaxTree.ParseText` over every `Mapping/*Statement.cs`
(`BuildStatement() => "literal"` — flag, don't guess, anything not a plain literal/concat of
literals), every source method's `SqlRowSource`/`ExcelRowSource`/`CsvRowSource` constructor
arguments, every `Model/*.cs` entity's properties, and `Model/{Package}DbContext.cs`'s
`ToTable(...)` mapping. Emit `right-sql-facts.json` in the identical shape.

**Compare**: join Left/Right facts by best-effort name/target-table match (report an unmatched
item on either side explicitly — `LeftOnly`/`RightOnly`, never silently dropped). Parse both SQL
texts with `TSql170Parser`, diff referenced-table/column sets and target schema (types included)
rather than raw text. Verdict per pair: `ExactMatch` / `NormalizedMatch` (same tables/columns,
different literal formatting) / `Mismatch` (evidence: both texts + what differs) /
`LeftOnly`/`RightOnly`.

## Gate 2 — control-flow / data-flow graph parity

**LeftOracle** extends the same walk to emit `left-graph-facts.json`: control-flow edges from
each container's own `IDTSSequence.PrecedenceConstraints` (`.PrecedenceExecutable`/
`.ConstrainedExecutable`/`.Value`/`.EvalOp`/`.Expression`), node list = every `Executable.Name` +
container path + `CreationName`; concurrency groups computed independently (nodes with no
precedence relationship to each other at the same container level — re-derived from scratch, not
ported from `ControlFlowDagSpec`). Data-flow edges from `pipeline.PathCollection` (`FromComponent`/
`FromOutput`/`ToComponent`/`ToInput`), nodes from `ComponentMetaDataCollection` (name +
discriminated component kind, re-derived independently rather than reusing
`ComponentInsight`/`PipelineFlowOrder`, which are hard-coupled to `Ssis.Extract.Model` per the
research and therefore off-limits).

**RightOracle**: parse `RunAsync`'s statement list — sequential `await X(uow, ct);` = sequential
edges; a `Task.WhenAll(RunBranchAsync(...), ...)` block = a concurrency group, matching the
generator's own `// Concurrent (...)` comment as corroborating (not authoritative) evidence.
Recurse into each container method (`SEQ_Prepare`) the same way. Per-Data-Flow-Task internal
graph is coarser on the Right by nature (inlined row-mapping, not a literal per-component chain)
— extract at "which source/sink/mapping files this flow method references, in what order" and
document the fidelity gap explicitly rather than pretend parity with Left's true path graph.

**Compare**: node-set diff (consult `gaps.json` as the allow-list for an admittedly-missing node,
e.g. an unfilled Script Task), edge-set diff, concurrency-group diff (set equality per wave,
order-independent).

## Gate 3 — step-level input/output parity (Tier A + B)

**Tier A (row counts).** Left: run the real package for real (dtexec or the existing SSISDB
catalog path this repo already has proven) at verbose logging, read `catalog.
execution_data_statistics` (already known in this repo to populate per buffer flush at
`LOGGING_LEVEL=3`) — or a lightweight `IDTSEvents.OnPipelineRowsSent` listener wrapping dtexec
directly, avoiding a catalog deployment. Right: **`PackageHarness.NewRealUnitOfWorkAsync()`
already exists** in the generated `{Package}.Tests/TestDoubles/PackageHarness.cs` — a real,
already-`BeginAsync`'d `Etl.Core.Data.UnitOfWork` against a real SQL Server, rolled back on
disposal. Write validator-authored xUnit tests that call each source/step method directly and
count rows; deliver them via the **existing** `MoreTests/` + `ssisx apply-tests` mechanism (no
`.csproj` edits, no new `InternalsVisibleTo` hacks — `{Package}.Tests` already has internals
access, and `MoreTests` is the sanctioned place to add more test code to it).

**Tier B (content, at real source/sink boundaries).** Seed identical known rows into a real dev
SQL Server / file both sides read. Left: run the real package for real, read back what actually
landed. Right: same `MoreTests`-delivered tests invoke the generated method(s) against the real
`NewRealUnitOfWorkAsync()` connection, capture actual rows produced. Diff row-for-row against
Left's landed content — the same "exact row-for-row match against a real probe" discipline this
project already uses throughout its dtexec-verification history, now packaged as a repeatable
tool instead of a one-off manual round each time. A component with no independently-observable
boundary this session (needs true mid-pipeline tapping) is reported `Unverifiable`, never
silently skipped — that's the explicit, disclosed Tier-C gap.

## Reporting

`rvx report --left <pkg>.dtsx --right <generated-dir> --out <dir>` aggregates all three gates
into one Markdown + JSON per package, using fresh record types in `RewriteValidator.Model`
(`Gate1Result`/`Gate2Result`/`Gate3Result`, each carrying `Verdict` + evidence) — styled after
this project's existing `GapSpec`/`ConformanceRuleSpec` "evidence, never silent pass" convention,
but independently defined, no shared reference.

## Verification plan

1. **Prove the comparator actually catches a real mismatch before trusting a green run** — the
   same discipline this project uses everywhere (e.g. the Conditional-Split test-emitter round
   deliberately corrupted an assertion to confirm red). For each gate, once it reports Pass on
   `Package_Advanced`/`Package_Transforms`, deliberately mutate one known-good fact (edit a
   literal in a generated `*Statement.cs`, or hand-edit a `left-sql-facts.json` value) and confirm
   the gate reports the mismatch with correct evidence, then revert.
2. Run Gate 1 for real against `RBC_Demo_ETL/Package_Advanced.dtsx` +
   `RBC_Demo_ETL/Package_Transforms.dtsx` vs. `D:\tmp\gen\generate\Package_Advanced` +
   `Package_Transforms` — the two packages already generated on this machine.
3. Run Gate 2 against the same pair, confirming the known concurrency wave
   (`DFT_FlagCustomers`/`DFT_ExcelImport`/`FEL_SampleFiles`, already visible in the generated
   `RunAsync`) round-trips correctly from both independent readers.
4. Run Gate 3 Tier A/B against a real SQL Server (`.\SQLFORPOC_2022` or the `ssisdemo-sqlserver`
   Docker container already used elsewhere in this repo for `SSIS_From_Sandeep`), seeding known
   rows and confirming row-for-row parity, plus one deliberately-wrong seed to confirm a real
   mismatch is caught.
5. `dotnet build Tools/RewriteValidator/RewriteValidator.slnx` clean (0 warnings/0 errors) and
   `dotnet test` green, mirroring this repo's own build/test discipline.
