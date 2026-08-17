# report contract

Status: **slice 5** (see `../../../Phase0-Extractor-Plan.md` §8). This document covers what
`ssisx report` and `ssisx diff` write; `docs/spec-schema.md` covers `ssisx extract`'s
`spec.json` contract separately -- `report` derives everything below from the same parsed
package model `extract`/`graph` use, but doesn't read `extract`'s own JSON output back in
(see `ReportCommand`'s doc comment for why).

## Files written by `ssisx report --out <dir>`

```
<dir>/
  inventory.csv | inventory.md      one row per package: counts, class, score, coverage, findings
  findings.csv  | findings.md       every rule hit, grouped by category, with severity + location
  nondeterministic.json             the plan §5.8 manifest
  unmapped.md                       everything extraction did not model, per package
  portfolio.md                      the "here is what you own" summary (plan §5.7)

  # slice 5 additions
  datatouch.md | datatouch.json     what each package reads/writes (plan §5.3)
  expressions.csv                   every SSIS expression, for test seeding (plan §5.5)
  expression-functions.md           which SSIS functions/casts the portfolio actually uses
  sql/<Package>/<Location>.sql      every SQL statement, individually addressable (plan §5.4)
  sql-analysis.json                 per-statement parse result: objects, statement types, flags
  graph/portfolio.mmd               cross-package dependency graph (plan §5.2)
  dependencies.json                 the same edges as data
```

CSV files are plain comma-separated with a header row, RFC4180-style quoting (a field is
quoted only if it contains a comma/quote/newline). MD files are for reading; CSV files are
for feeding into something else (a spreadsheet, another tool). Both carry the same rows.

## Complexity scoring (plan §5.6)

`ComplexityStats` (`Ssis.Extract.Dtsx.ComplexityScorer`) counts, recursively across the
whole control-flow tree (containers, nested executables, event handlers) and every Data
Flow Task's pipeline: tasks, containers, data flow tasks, pipeline components, expressions
(property expressions + precedence-constraint expressions + pipeline output-column
expressions -- see the type's own doc comment for the exact list), SQL statements, script
tasks, loops, event handlers, and unmapped nodes. Each count is multiplied by a weight
(`config/complexity-weights.json`, `ComplexityWeights.Default` mirrors it, override with
`--weights <path.json>`) and summed into `Score`, then classified:

1. Any script task or unmapped node present → **HighRisk**, regardless of score. Custom or
   unrecognized code can't be scored away.
2. `Score >= HighRiskThreshold` (default 60) → **HighRisk**.
3. SQL statements present with zero Data Flow Tasks → **SqlHeavy**. A content-shape rule,
   not a score threshold -- pure Execute SQL Task orchestration is a fundamentally
   different rewrite shape than one with real pipeline transforms.
4. `Score <= SimpleThreshold` (default 15) → **Simple**.
5. Otherwise → **Procedural**.

Both PoC packages score in the **Simple**/**Procedural** range (14 and 22 respectively, as
of this slice) -- neither has a script task, an unmapped node, or a pure-SQL data flow, so
the **HighRisk**/**SqlHeavy** branches are proven only by
`tests/Ssis.Extract.Tests/ComplexityScorerTests.cs`'s synthetic packages, the same
"real fixtures don't branch, so a synthetic unit test does" pattern already used for
`DagAlgorithmTests`/`LineageBuilderTests`.

## Findings (plan §5.7)

`FindingSpec` (`RuleId`, `Category`, `Severity`, `PackageName`, `Location`, `Message`) from
`Ssis.Extract.Dtsx.RulesEngine`. `Severity` is `Error` (blocks a mechanical rewrite / will
silently produce wrong output) / `Warning` (works today, is a known landmine) / `Info`
(worth knowing, not inherently a problem) -- a vocabulary chosen for this build slice, not
quoted from the plan, which doesn't specify one.

Every rule below is answerable from the model slices 1-3 already built. Two categories the
plan lists (unused variables/connection managers) fall back to a **best-effort substring
search** over the free text this build slice already captures (property expressions, SQL
statement text, pipeline expressions, parameter bindings) rather than waiting for the real
expression harvest/tokenizer (plan §5.5, slice 5) -- flagged in each such finding's own
message, not silently treated as authoritative.

| RuleId | Category | Severity | Evidence used |
|---|---|---|---|
| `script-task-present` | RewriteEffort | Error | `ExecutableType` |
| `execute-process-task-present` | RewriteEffort | Error | `ExecutableType` |
| `loop-present` | RewriteEffort | Warning | `ExecutableType` |
| `third-party-component` | RewriteEffort | Error | `ComponentClassId` not `Microsoft.*` |
| `scd-component-present` | RewriteEffort | Error | `ComponentClassId` contains "SCD"/"SlowlyChangingDimension" |
| `oledb-command-present` | RewriteEffort | Warning | `ComponentClassId == Microsoft.OLEDBCommand` |
| `async-transform-present` | RewriteEffort | Warning | a non-source component with a non-error output whose `SynchronousInputId` is null |
| `checkpoints-enabled` | SemanticRisk | Warning | `Checkpoints.SaveCheckpoints` |
| `transaction-required` | SemanticRisk | Warning | `TransactionOption == "Required"` (package or executable) |
| `forced-execution-result` | SemanticRisk | Warning | `ForceExecutionResult`/`ForcedExecutionValue` set |
| `max-error-count-tolerant` | SemanticRisk | Warning | `MaximumErrorCount > 1` |
| `validate-external-metadata-false` | SemanticRisk | Info | `ValidateExternalMetadata == false` |
| `error-output-unrouted` | SemanticRisk | Warning | an error output whose `RefId` is never a `<path>`'s `StartId` |
| `protection-level-password-based` | EnvironmentCoupling | Warning | `ProtectionLevelName` is a password-based level |
| `legacy-package-configurations` | EnvironmentCoupling | Warning | `Package/Configurations` present in `Unmapped` |
| `password-present-in-connection-string` | EnvironmentCoupling | Info | `ConnectionManagerSpec.WasRedacted` |
| `hardcoded-connection-string` | EnvironmentCoupling | Warning | connection string is a literal, not expression-driven |
| `disabled-task` | DeadOrSuspicious | Info | `Disabled == true` |
| `unused-connection-manager` | DeadOrSuspicious | Info | never referenced by DtsId/RefId anywhere in the package (best-effort) |
| `unused-variable` | DeadOrSuspicious | Info | `Namespace::Name` never appears in any expression/SQL text (best-effort) |
| `unconnected-pipeline-component` | DeadOrSuspicious | Warning | declared inputs/outputs with no matching `<path>` |
| `duplicate-package` | DeadOrSuspicious | Warning | portfolio-only -- identical `Sha256` across two or more packages |
| `non-deterministic-column` | NonDeterminism | Info | see below |

`RulesEngine.Evaluate` runs the per-package rules; `RulesEngine.EvaluatePortfolio` runs
`duplicate-package` across the whole set (meaningless for one package in isolation) --
`ReportCommand` calls both and merges the results per package.

## Non-determinism manifest (plan §5.8)

`nondeterministic.json` is a flat array of `NonDeterministicColumnSpec` -- the explicit
phase-0 → phase-2 handoff so a later parallel-run diff harness can exclude these columns
from its row hash automatically. Built by `Ssis.Extract.Dtsx.NonDeterminismAnalyzer` from
each Data Flow Task's `LineageSpec.ConstantColumns` (already computed in slice 3), filtered
to the specific patterns the plan names -- `GETDATE()`, `GETUTCDATE()`, `NEWID()`,
`@[System::...]` -- then traced forward through "PathFlow" lineage edges only (see
`NonDeterministicColumnSpec`'s doc comment for why "ExpressionDerived" edges aren't
crossed) to whichever `OleDbDestination` ultimately lands the value, resolving
`TargetTable`/`TargetColumn` from that component's own `OpenRowset`/`ColumnMappings`.

**Verified against this repo's own packages, not just synthetic fixtures**: `ssisx report`
against the PoC currently finds exactly the three columns this repo's own root
`CLAUDE.md` (§5.8's worked example) names --
`dbo.Employee.LoadedAtUtc`, `dbo.Department.LoadedAtUtc`, `dbo.Designation.LoadedAtUtc`,
all `GETUTCDATE()` -- an independent confirmation the tracing logic is right, not just that
it runs without crashing.

**Known gap, stated rather than silently absent**: the plan also names identity columns as
a non-determinism source. Identity is a destination-table property, not anything present in
a package's own XML, so it isn't (and can't be) detected here -- it needs the live database
schema, a later-slice concern (plan §5.3/§6's `enrich`).

## SQL harvest and analysis (plan §5.4)

`SqlHarvester` (in `Ssis.Extract.Dtsx`) locates every SQL statement -- Execute SQL Task
`SqlStatementSource`, plus any pipeline component property named `SqlCommand`/
`SqlCommandParam`, which covers OLE DB Source, OLE DB Command, and Lookup generically rather
than per component type. An Execute SQL Task whose `SqlStatementSourceType` is
`FileConnection`/`Variable` is **skipped**: its `SqlStatementSource` holds a connection
manager or variable *name*, not SQL, and harvesting it would feed the parser a filename.

Each statement is written to its own file under `sql/` **before** being parsed (so a
statement that fails to parse is still on disk to look at -- which is when you most want
it), then parsed by `Ssis.Extract.Sql.SqlAnalyzer` into a `SqlAnalysisSpec`:
statement types (raw ScriptDom AST node names, so an unanticipated type shows up honestly),
`ReadsFrom`/`WritesTo`/`ExecutesProcedures`, and the `HasTruncate`/`HasDelete`/`HasMerge`/
`HasDynamicSql` flags the plan names.

Two behaviours worth knowing, both verified rather than assumed:

- **Write targets are subtracted from reads.** ScriptDom models an `INSERT INTO x` target as
  a `NamedTableReference` exactly like a `FROM` source, so a naive "every table reference is
  a read" reports every written table as also read. Each mutating statement type is visited
  explicitly to capture *its own* target.
- **Unqualified names stay unqualified.** `SELECT * FROM Employee` yields `Employee`, not
  `dbo.Employee` -- defaulting the schema would fabricate a fact, since the real default
  depends on the executing login.

Parser: `TSql170Parser`. A newer parser accepts a superset of older syntax, so this is the
tolerant choice for an unknown portfolio, not a claim about the client's server version. A
parse failure is reported in `ParseErrors` and surfaced in `portfolio.md`, never swallowed --
on a real portfolio it usually means dialect drift or a non-T-SQL provider (an Execute SQL
Task can target Oracle/DB2/ODBC), which is itself a finding.

## Expression harvest (plan §5.5)

`ExpressionHarvester` collects every expression -- package/executable/connection-manager
property expressions, variable expressions, precedence-constraint conditions, and Derived
Column output expressions -- into `expressions.csv`, each with its location, referenced
variables (`@[Namespace::Name]`), referenced columns (`#{lineageId}`), and the functions and
casts it uses.

**Functions and casts are counted separately, deliberately.** `(DT_WSTR,50)` is a cast
operator, not a function call, but looks identical to a naive `NAME(` scan -- conflating
them would inflate the "which functions must a replacement engine support" number that this
output exists to answer. The tokenizer also handles SSIS's bracketed form (`[UPPER](`), which
is how a Derived Column's *raw* expression stores function names -- confirmed against this
PoC's own `LoadEmployees.dtsx`.

`expression-functions.md` is the roll-up: each function/cast with its use count and the
packages using it. On this PoC that's `GETUTCDATE` (3), `UPPER` (3), `SUBSTRING` (1), and the
`DT_WSTR` cast (5) -- a small enough surface to read at a glance, which is exactly the point
of measuring it rather than guessing.

## Data touch and dependency graph (plan §5.3, §5.2)

`datatouch.json`/`.md` is one `DataTouchSpec` per package: tables read/written (from SQL
analysis plus OLE DB Destination `OpenRowset`), procedures executed, file paths, and servers.

Two honesty details:

- **Table names are bracket-normalized.** An OLE DB Destination writes `[dbo].[Employee]`
  while SQL text writes `dbo.Employee`; without normalizing, a package that truncates a table
  *and* writes it would look like it touches two different tables, and the shared-table
  dependency edges below would silently miss.
- **Expression-driven paths get their own list** (`FilePathsFromExpression`), not a fabricated
  path. When a connection manager's `ConnectionString` is owned by a property expression, the
  path genuinely isn't in the package file -- any static value present is only a design-time
  default the runtime replaces. In this PoC **all three** CSV connection managers are
  expression-driven (verified in the raw `.dtsx`), so reporting the expression is the only
  honest answer; resolving it for real needs the environment's parameter values, i.e. exactly
  what the unavailable `enrich` command would have provided.

`dependencies.json`/`graph/portfolio.mmd` carry `DependencyEdgeSpec` edges of two kinds:
`ExecutePackageTask` (explicit, solid in the diagram) and `SharedTable` (implicit -- A writes
a table B reads, dotted and labelled). The implicit half is the valuable one: it exists only
in the schedule and in nobody's documentation. This PoC produces **zero** edges of either
kind, correctly -- its two packages write disjoint tables and neither calls the other.

## `ssisx diff` (plan §6.2)

Semantic diff between two versions of a package, each side a `.dtsx` or an `.ispac`. Writes
markdown (stdout, or `--out` plus a sibling `.json`). Compares `PackageProperties`,
`Parameters`, `Variables`, `ConnectionManagers`, `Executables` (including SQL text),
`PrecedenceConstraints`, and `Pipeline` (including Derived Column expressions -- same
components, same columns, different transformation is the drift most worth catching).

**Exit code 1 signals semantic differences, never merely differing bytes.** Verified on this
PoC: its repo `.dtsx` and the `.ispac` built from it differ byte-for-byte with *zero*
semantic differences, because a build rewrites version stamps and designer layout without
changing behaviour. Gating CI on bytes would fire on every single build and teach everyone to
ignore it. Connection strings are compared in redacted form -- a diff report is exactly the
kind of artifact that gets pasted into a ticket.

## Inventory and portfolio.md

`inventory.csv`/`.md` is one row per package: `ComplexityStats` plus `CoveragePercent`,
`FindingsCount`, and `HighestSeverity` (the most severe finding's `Severity`, or null).
`portfolio.md` is the readable roll-up: classification breakdown, findings-by-severity, the
non-determinism summary, cross-package dependencies, the SQL harvest (including any parse
failures and every TRUNCATE/DELETE/MERGE/dynamic statement), the expression-language surface
area, then pointers to the detail files -- the "here is what you own" report the plan (§4
build-slice table) calls the actual phase-0 deliverable.

## unmapped.md

One section per package: `PackageSpec.Unmapped` (verbatim) plus every executable with a
populated `UnmappedTask` (an unrecognized `ExecutableType`, not caught by `Unmapped` since
that field is package-level free text, not walked recursively). As of slice 3, both PoC
packages are at 100% coverage, so `LoadReferenceData`'s section reads "Nothing unmapped."
and `LoadEmployees`' only entry is the permanently-excluded `Package/DesignTimeProperties`.

## Synthetic component-coverage fixtures (added post-slice-5, no client packages yet)

Plan §8's own risk note: "the estimate's real risk is component coverage, not code
volume" -- both PoC packages only ever exercise four component/task types (Flat File
Source, Derived Column, OLE DB Destination, Execute SQL). With no client packages
available yet (plan §11 decision 2, still unresolved), two small synthetic `.dtsx` files
were built to attack that risk directly, covering the specific gap components plan §8
calls out: Lookup, Conditional Split, a ForEach Loop Container, and a Script Task. They
live at `tests/Ssis.Extract.Tests/Fixtures/` (not under the PoC's own `SSIS/` folder --
they aren't part of the PoC's deliverable), with a `synthetic-fixture-tables.sql` script
to (re)create the two tiny staging tables `SyntheticLookupSplit.dtsx` reads/writes on
`.\SQLFORPOC`.

**Built via the real SSIS 17 object model, not hand-written XML** -- CLAUDE.md trap 12's
"ask the runtime, don't guess the XML/API" discipline, extended from single-property
probing to whole-package construction: `Application`/`Package`/`MainPipe`/
`IDTSDesigntimeComponent100` calls (same GAC assemblies this PoC already uses for local
execution), `SaveToXML`, then the *saved XML itself* read back as ground truth rather than
assumed from memory or documentation. Every componentClassID, custom property name, and
fixed output name named in the model below was confirmed this way, not guessed:

| Fixture | Contents | What it's real evidence for |
|---|---|---|
| `SyntheticForEachScript.dtsx` | ForEach File Enumerator (`STOCK:FOREACHLOOP` / `Microsoft.ForEachFileEnumerator`) wrapping a Script Task (`Microsoft.ScriptTask`) | `ForEachLoopPayload`, `ScriptTaskPayload` |
| `SyntheticLookupSplit.dtsx` | OLE DB Source → Lookup → Conditional Split → 3 OLE DB Destinations (match/no-match/business-rule branches -- the Lookup+Conditional Split combination that underlies most hand-rolled SCD Type-1 upserts, not the deprecated SCD wizard component itself) | `OleDbSourcePayload`, `LookupPayload`, `ConditionalSplitPayload` |

**A real, pre-existing coverage-accuracy bug was found and fixed along the way, not just
new payload types added.** Two elements were silently invisible to *both* the model and
the coverage percentage before this pass:
- A ForEach Loop's `<ForEachEnumerator>`/`<ForEachVariableMappings>` -- these are sibling
  elements next to `<Executables>`, not inside `<ObjectData>` the way every task's payload
  is, so `DtsxPackageReader.ReadExecutable`'s `ObjectData`-gated branch never even looked
  at them. Neither counted as unmapped nor modeled -- a silent overstatement of coverage
  for any package containing this extremely common container.
- A pipeline component's output-level `<properties>` -- Conditional Split puts its whole
  case expression (`Expression`/`FriendlyExpression`/`EvaluationOrder`, plus
  `IsDefaultOut` on the default case) at the *output* level, not on an output column the
  way Derived Column does. `PipelineOutputSpec`/`PipelineInputSpec` had no `Properties`
  field at all before this pass, and `DtsxPackageReader`'s "everything under `<pipeline>`
  is modeled generically, don't count it as unmapped" assumption meant this was invisible
  rather than flagged.

Both fixtures now extract at genuinely-earned 100% coverage (verified, not assumed --
`SyntheticFixtureTests.cs`), and a third bug was caught the same way: `RulesEngine`'s
`unused-variable` rule wrongly flagged the ForEach Loop's own iteration variable
(`User::CurrentFile`) as unused, because its text-collection pass didn't know
`ForEachLoopPayload.VariableMappings` or `ScriptTaskPayload.ReadOnlyVariables`/
`ReadWriteVariables` count as real usage -- fixed in the same pass, with a regression
test guarding it (`UnusedVariableRule_DoesNotFlagAForEachLoopsOwnIterationVariable`).

**What's evidenced versus best-effort, so the next reader doesn't have to re-derive it:**
- `LookupPayload.MatchOutputName`/`NoMatchOutputName` are SSIS's own fixed names for this
  component ("Lookup Match Output"/"Lookup No Match Output") -- confirmed from the
  fixture's saved XML, and (unlike a Conditional Split case) not something a package
  author typically renames.
- `LookupPayload.ReferenceColumns` is a best-effort parse of the component's own
  `ReferenceMetadataXml` custom property -- confirmed real and populated once a join key
  is set, but it's an internal, undocumented nested-XML-in-a-string format, **not** the
  standard `externalMetadataColumns` mechanism OLE DB Source/Destination use (that
  collection stayed empty on this fixture even after the join key was set and the
  component fully reinitialized -- checked directly, not assumed). Parsed defensively; a
  future SSIS version changing this internal shape degrades to an empty list, not a crash.
- `ScriptTaskPayload` deliberately has no `EntryPoint` field even though the object
  model's own `TaskHost.Properties["EntryPoint"]` defaults to `"Main"` at design time --
  confirmed that value is not persisted to the saved `.dtsx` for a VSTA-hosted Script Task
  (the only kind SSIS 17 creates). The real entry point lives inside the embedded VSTA
  project binary, which this reader does not unpack (plan's own "Script Task source
  extraction" remains unbuilt, tracked separately in the README).
- The Lookup fixture's join key (`CustomerID`) is genuinely marked as a used input column
  in the live pipeline object (confirmed via `SetUsageType` + a second `ReinitializeMetaData`
  round-trip, without which the reference query's own columns never resolve), but that
  marking does **not** appear as an `<inputColumns>` entry in the saved XML -- a real,
  checked object-model quirk of this component, not a gap in the reader.

**Still not attempted:** wiring a Lookup's match output to add an actual new column from
the reference table (e.g. `CustomerName`) end-to-end -- the modern hash-match Lookup's
mechanism for this isn't exposed via a documented public API, and reverse-engineering it
was judged not worth the time against the actual goal (proving the *reader* handles real
Lookup/Conditional Split XML, which the fixture as built already demonstrates). A real
client package with this exact pattern would still parse correctly today: the generic
model captures whatever output columns and mappings exist regardless of how they got
there, same as every other component.
