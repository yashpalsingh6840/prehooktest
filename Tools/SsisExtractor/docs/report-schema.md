# report contract

Status: **slice 5** (see `../../../Docs/Phase0-Extractor-Plan.md` §8). This document covers what
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

  # added 2026-08-19
  primary-keys.json | primary-keys.md   best-effort PK candidates, per destination table
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
| `script-component-present` | RewriteEffort | Error | `PipelineComponentSpec.ScriptComponent` is not null (pipeline transform sibling of Script Task) |
| `script-source-stripped` | RewriteEffort | Error | `ScriptTaskPayload.SourceStripped` / `ScriptComponentPayload.SourceStripped` -- source array/element(s) empty but a compiled binary cache is present |
| `execute-process-task-present` | RewriteEffort | Error | `ExecutableType` |
| `loop-present` | RewriteEffort | Warning | `ExecutableType` |
| `third-party-component` | RewriteEffort | Error | `ComponentClassId` not `Microsoft.*` (never fires for a legacy-spelled stock component -- see `unmapped-legacy-clsid` below) |
| `unmapped-legacy-clsid` | RewriteEffort | Warning | `PipelineComponentSpec.IsUnresolvedLegacyClsid` -- a bare CLSID `LegacyComponentIds` has no evidenced mapping for; deliberately NOT the same finding as `third-party-component`, since this component might well be a stock one under an old spelling |
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
| `protection-level-user-key-based` | EnvironmentCoupling | Warning | `ProtectionLevelName == "EncryptSensitiveWithUserKey"` -- a different risk framing than the password-based levels above (undecryptable outside the original author's Windows account, not just "needs the password communicated") |
| `encrypted-connection-manager-secret` | EnvironmentCoupling | Error | `ConnectionManagerSpec.EncryptedProperties` non-empty -- a DPAPI-encrypted sensitive value (e.g. `Password`) this tool can never extract; see `GapKind.EncryptedConnectionManagerSecret` in `ssisx generate`'s own gap reporting for the codegen-side handling |
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

## Primary-key candidates (added 2026-08-19)

`primary-keys.json`/`.md` -- a best-effort `PrimaryKeyCandidateSpec` per OLE DB Destination,
built by `Ssis.Extract.Dtsx.PrimaryKeyInference`. Closes a gap named in
`Validation/docs/gate3-harness.md`: gate 3's per-table `KeyColumn` used to be entirely
hand-filled because nothing detected it.

**Why this can only ever be a heuristic:** confirmed by reading this PoC's own real
`<externalMetadataColumn>` elements, a `.dtsx` carries `dataType`/`length`/`precision`/
`scale`/`codePage` for a destination column and *nothing about keys, nullability, or
identity* -- SSIS simply doesn't persist that at design time. Without a live database
connection (an `enrich`/`pull` concern, plan §11 decision 4, deliberately out of scope), the
only signals available are:

1. A destination input column whose name matches `"<last segment of the target table>ID"`
   (e.g. `[dbo].[Employee]` → `EmployeeID`), or the fallback `"ID"`.
2. It must be an integer pipeline type (`i1`/`i2`/`i4`/`i8` -- the literal short codes this
   PoC's own `.dtsx` files use, e.g. `dataType="i4"` on `EmployeeID`; a different type space
   than `SsisTypeCodeMaps`' other two tables, see that file's own doc comment).
3. It must NOT be a computed value -- traced via the same lineageId join `LineageBuilder`
   uses, so a derived business key (this PoC's own `EmployeeKey`/`DepartmentKey`/
   `DesignationKey`, each a string concatenation) is never mistaken for the real key.

**Verified against live ground truth, not just plausible-looking:** all three real PoC
candidates (`Employee.EmployeeID`, `Department.DepartmentID`, `Designation.DesignationID`)
were cross-checked against `.\SQLFORPOC`'s actual `PRIMARY KEY` constraints via
`sys.indexes`/`sys.index_columns` -- exact matches, all three. Feeding the same output into
`Validation`'s `ConfigGenerator` (via `--primary-keys-json`) reproduces byte-identical
`KeyColumn` values to the ones a human had already hand-filled in an earlier session.

## Observable effects (added 2026-08-20 -- closes a false-green risk in gate 3)

`effects.json`/`effects.md` -- one `Ssis.Extract.Model.Analysis.ObservableEffectSpec` per
real-world thing a package changes, built by `Ssis.Extract.Dtsx.ObservableEffectsBuilder`.
Exists because `Validation`'s gate-3 harness only ever compared SQL table rows -- true
for both of this PoC's packages, since both happen to end in a table, but not a general fact
about SSIS packages. A package whose real output is a file drop, an email, or a queue message
would have zero configured target tables, nothing to diff, and the harness would still print
PASS. **A partial check that reports as a full pass is worse than no check, because it gets
trusted** -- this file is what lets the harness refuse to make that mistake.

**Four kinds, three of them derived from analysis that already existed** (`SqlTable`/`File`
from `DataTouchBuilder`'s data-touch inventory, `StoredProcedure` from the SQL harvest) --
**the fourth, `UncharacterizedTask`, is the load-bearing one.** Every executable in the
package whose `ExecutableType` is not on a short allowlist of types already fully accounted
for elsewhere (`Microsoft.Pipeline`, `Microsoft.ExecuteSQLTask`, and the stock containers --
confirmed as `STOCK:SEQUENCE`/`STOCK:FOREACHLOOP`/`STOCK:FORLOOP` against real fixture XML,
not guessed) becomes an explicit, unverifiable effect instead of silent nothing. **Deliberately
an exclusion list of known-safe types, not an allow list of known-dangerous ones** -- so a
future SSIS task type this tool has never seen (a Send Mail Task, an FTP Task, a Web Service
Task) is unverifiable by default, not verifiable by default. A Script Component gets the same
treatment even though it lives *inside* an otherwise-safe `Microsoft.Pipeline` -- its
transform body can have any side effect a Script Task's can, and nothing here reads it either.

Each effect carries a `Verifiability`: `Verifiable` (a gate-3 checker exists -- true for
`SqlTable` today), `NeedsChecker` (a real, characterized effect with no checker built yet --
`File`), or `NeedsHumanReview` (this tool cannot even establish what the effect is --
`StoredProcedure`'s own writes, `UncharacterizedTask`).

**Measured on this PoC:** `LoadEmployees` reports exactly one effect (`dbo.Employee`,
`Verifiable`); `LoadReferenceData` reports exactly two (`dbo.Department`/`dbo.Designation`,
both `Verifiable`) -- both packages are straight-line data flows with nothing this catch-all
needs to flag, confirming it doesn't cry wolf on the ordinary case. Run against the user's own
in-progress `SSIS/ScriptTest.dtsx` (read-only, untouched, per this repo's own discipline), it
correctly reports both Script Tasks as `UncharacterizedTask`/`NeedsHumanReview` -- proving the
catch-all actually fires on a package with a real unmodeled effect, not just that it stays
quiet on packages without one. Pinned by `tests/Ssis.Extract.Tests/ObservableEffectsBuilderTests.cs`,
including a Script-Component-specific fixture proving the "lives inside an allowlisted
executable" case doesn't slip through.

**`Validation`'s `ConfigGenerator`** (`--effects-json`) folds this into each package's
`PackageConfig.DeclaredEffects` -- every effect NOT covered by `--tables`, including a
`SqlTable` effect that *is* real but was left off `--tables` (the "wrote to a table nobody
configured for comparison" gap, not just the non-table-effect case). `GateRunner.Run` then
computes a three-way `Outcome` instead of a boolean: `Pass` only when every table matched
*and* every declared effect is `Verifiable`; `Incomplete` when tables matched but a real
effect has no checker (exit code 3, never collapsed into `Pass`); `Fail` otherwise. See
`Validation/docs/gate3-harness.md`'s own section on this.

**A real bug was caught wiring this into `ssisx conformance`'s `TargetSchema` evidence, not
shipped:** the synthetic Lookup/Conditional Split fixture has three OLE DB Destinations all
left at SSIS's default component name ("OLE DB Destination") -- joining candidates back to
their destination by `(DataFlowTaskPath, Name)` collided and crashed with a duplicate-key
exception. Fixed by joining on the destination's own `RefId` instead (always unique), the
same identity every rule ID in this tool already relies on elsewhere. Pinned by
`PrimaryKeyInferenceTests.SyntheticLookupSplit_HandlesThreeDestinationsSharingTheSameDefaultName`.

**Also folded into `ssisx conformance`'s `TargetSchema` rule evidence** (not a separate rule
category) -- a candidate PK appears right next to the column contract a reviewer is already
reading, rather than requiring a cross-reference into a second file.

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
| `SyntheticScriptComponent.dtsx` (added later, alongside Script Component source extraction below) | OLE DB Source → Script Component (passthrough transform, `Microsoft.ManagedComponentHost`/`UserComponentTypeName=Microsoft.ScriptComponentHost`) → OLE DB Destination | `ScriptComponentPayload`, `PipelinePropertySpec.IsArray`/`.ArrayElements` |

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
  (the only kind SSIS 17 creates). By VSTA convention the entry point is always
  `ScriptMain` inside a project item named `ScriptMain.cs`/`ScriptMain.vb` -- see "Script
  Task source extraction" below for where that source actually lives (corrects an earlier,
  wrong assumption in this same file that it was locked inside the compiled binary).
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

## Script Task source extraction (plan §4.5/§6.1, built post-slice-6)

**Corrects a real, wrong assumption this doc used to make.** The synthetic-fixture pass
above built `SyntheticForEachScript.dtsx`'s Script Task via bare object-model property
sets (`Language`, `ReadOnlyVariables`, ...), never through SSDT's actual Script Task
editor -- so it never got a VSTA project written into it, and the doc note at the time
guessed the missing piece ("the actual script source lives inside the embedded VSTA
project binary") without ever looking inside a real one. That guess was wrong, discovered
by reading (read-only, never edited or committed by this tool) a genuine SSDT-authored
Script Task the repo happened to have on disk at the time
(`SSIS/ScriptTest.dtsx` -- the user's own separate in-progress work, unrelated to this
extractor):

- The real source **is plain text**: one `<ProjectItem Name="..." Encoding="...">CDATA
  text</ProjectItem>` sibling per VSTA project file -- `ScriptMain.cs`/`.vb` (the actual
  entry point), the `.csproj`/`.vbproj`, `AssemblyInfo.*`, `Resources`/`Settings` designer
  files, and one internal MSBuild-ish "Project" descriptor. `Encoding` is the *original
  on-disk file's* encoding (`UTF8` on every item observed except that internal "Project"
  descriptor, which is `UTF16LE`) -- not this XML's own encoding.
- `<BinaryItem Name="....dll">base64</BinaryItem>` is a **precompiled cache of the same
  code**, not the only copy of it and not where source "really" lives. It carries no
  independent information for a migration audience, so its content is deliberately not
  captured -- only its file name (`ScriptTaskPayload.BinaryItemNames`), enough to prove a
  cache existed without bloating `spec.json` with base64 machine code.
- Both element types were, before this fix, real pre-existing coverage-accuracy bugs of
  exactly the same shape as the two the synthetic-fixture pass found: `ReadScriptTask`
  never looked at either child, and `DtsxPackageReader.ReadExecutable`'s own comment
  claimed the Script Task `ObjectData` was "fully modeled" and therefore not counted as
  unmapped -- so a real Script Task's actual source text was silently absent from both the
  model *and* the coverage percentage's accounting of what's handled, on every package
  with one, the whole time slices 4-6 were being built and verified only against the two
  PoC packages (neither of which has a Script Task).

**What's built:** `ScriptTaskPayload.ProjectItems`/`BinaryItemNames`/`SourceStripped`
(model), `DtsxPackageReader.ReadScriptTask` reading both element types (reader), a new
`script-source-stripped` (RewriteEffort/Error) finding for the case plan §4.5 calls out --
a `<ScriptProject>` with a `<BinaryItem>` but no `<ProjectItem>` at all, i.e. source
genuinely gone -- and `ssisx report`'s `scripts/<Package>/<Task>/<relative path>` output
tree (plan §6.1), one file per `ProjectItem`, written with its own declared `Encoding`
rather than always defaulting to UTF-8, so the one `UTF16LE` item round-trips as the
correct bytes rather than being silently re-encoded.

**Not observed for real anywhere yet:** `SourceStripped` -- SSDT's default behavior keeps
source alongside the compiled cache; neither the PoC's own packages nor the one real
SSDT-authored Script Task read for ground truth here has it stripped. Verified instead via
a direct unit test constructing that XML shape by hand
(`ScriptTask_ReadScriptTask_DetectsSourceStrippedFromObjectDataDirectly`, reaching
`DtsxPackageReader.ReadScriptTask` via `InternalsVisibleTo`, the same pattern
`DagAlgorithmTests` uses for `BuildDag`) plus a `RulesEngineTests` pair proving the new
finding fires only in that case, not for an ordinary Script Task with intact source.

**Still genuinely unbuilt:** understanding what the script *does* -- no C#/VB parsing,
no detection of what APIs it calls (`Dts.Variables`, ADO.NET, file I/O, ...) beyond the
already-existing `ReadOnlyVariables`/`ReadWriteVariables` declared surface. Plan's own
words apply as-is: "Script Tasks are extracted, not understood."

## Script Component source extraction (plan §4.5's other half, built same day)

Script Component -- the Data Flow pipeline-transform sibling of Script Task -- needed its
own investigation rather than assuming it shares Script Task's exact shape, and turned out
to differ in three real, evidenced ways:

1. **Different discriminator.** Every other bespoke payload in this model is keyed off
   `PipelineComponentSpec.ComponentClassId` (`"Microsoft.Lookup"`, `"Microsoft.OLEDBSource"`,
   ...). A Script Component's own `ComponentClassId` is the generic
   `"Microsoft.ManagedComponentHost"` -- confirmed real via the object model, not a
   version-specific quirk: `ProvideComponentProperties()` reports the in-memory
   `ComponentClassID` as a completely different value (a GUID), and the *saved* XML's
   `componentClassID` attribute is `"Microsoft.ManagedComponentHost"` either way. The actual
   disambiguator is a custom property, `UserComponentTypeName`, whose value is
   `"Microsoft.ScriptComponentHost"` -- `PipelineReader.ReadComponent` checks that property,
   not `ComponentClassId`, before building `ScriptComponentPayload`.
2. **Different source storage shape.** Script Task's source lives in dedicated
   `<ProjectItem>`/`<BinaryItem>` sibling elements (see above). Script Component instead has
   two ordinary custom properties, `SourceCode`/`BinaryCode`, both `isArray="true"` --
   confirmed by dumping every custom property `ProvideComponentProperties()` declares, with
   their own self-reported `Description` text ("Stores the source code of the component" /
   "Stores the binary representation of the component"). This is what motivated
   `PipelinePropertySpec.IsArray`/`.ArrayElements` (see spec-schema.md): before that fix, an
   array property's multiple `<arrayElement>`s silently collapsed into one run-on string via
   `.Value` -- a genuine, previously-unnoticed data-fidelity bug (not a coverage-percentage
   bug like the Script Task/ForEachLoop ones -- `SourceCode`'s elements were still counted as
   "covered" correctly, just mangled together when read). The exact persisted shape
   (`<arrayElements arrayElementCount="N"><arrayElement dataType="...">CDATA</arrayElement>
   ...</arrayElements>`) was confirmed by setting a real `string[]` through
   `IDTSCustomProperty100.Value`'s own setter -- bypassing the VSTA editor entirely (which,
   same as Script Task's synthetic fixture, never writes a real project when driven by bare
   property calls) -- and reading back what the object model itself persisted, not guessed
   from documentation or blog posts.
3. **Different delimiter.** `ReadOnlyVariables`/`ReadWriteVariables` are **comma**-separated
   here, not semicolon-separated like `ScriptTaskPayload`'s -- confirmed from this property's
   own self-declared description ("Specifies a comma-separated list of read-only
   variables."), read directly off the object model rather than assumed to match Script Task
   just because the two features are conceptually the same. `PipelineReader.SplitCommaVariableList`
   is a deliberately separate helper from `DtsxPackageReader.SplitVariableList`, not a shared
   one with a parameter -- the two really are different conventions, not the same code with
   different call sites.

**What's confirmed versus not, for `ScriptComponentPayload.SourceCodeItems`:** the array
*shape* is real, direct evidence (point 2 above). What is **not** confirmed is the real
per-element *identity* a genuine SSDT-authored Script Component would produce -- unlike
Script Task, no real SSDT-authored Script Component was available anywhere in this repo to
read for ground truth (only a Script *Task* was, `SSIS/ScriptTest.dtsx`). So
`SourceCodeItems` is kept as an ordered list of opaque text blobs, never assumed to be "one
element = ScriptMain.cs" the way Script Task's named `ProjectItems` list is. `ssisx report`
reflects this honestly: each element is written to `scripts/<Package>/<Task>/<Component>/
source-<index>.cs|.vb|.txt` (extension from `ScriptLanguage`), not a real recovered file
name. `BinaryCode`'s elements carry no name at all to even capture -- only
`ScriptComponentPayload.HasBinaryCode`, a presence flag, same "name/presence only, never
content" rule as `ScriptTaskPayload.BinaryItemNames` one level more conservative (there
isn't even a name here).

**Fixture:** `SyntheticScriptComponent.dtsx` (OLE DB Source → Script Component → OLE DB
Destination, `SyntheticSourceOrder`/`SyntheticScriptOutput` staging tables, same
`synthetic-fixture-tables.sql`), built the same "ask the runtime" way as the others. Its
three input columns are marked passthrough (`UT_READONLY`) and never re-declared as new
output columns, so the Script Component's own `Output 0` has zero output columns in the
saved XML -- confirming, on a third component type, the same passthrough-lineage rule
already documented for Derived Column (CLAUDE.md, `LineageBuilder`'s own doc comment): the
OLE DB Destination downstream resolves `OrderID`/`CustomerID`/`Amount` by `lineageId`
straight back to the OLE DB Source's own output columns, never through the Script
Component's output at all.

**What's built, matching Script Task's shape:** `ScriptComponentPayload` (model);
`PipelineReader.ReadComponent`'s `UserComponentTypeName` branch (reader); a
`script-component-present` (RewriteEffort/Error) finding, parallel to `script-task-present`
but scoped to a pipeline component's own location (`<DataFlowTask RefId>/<Component Name>`);
the same `script-source-stripped` finding, reused rather than duplicated, firing from either
the executable-level Script Task branch or this component-level branch; `ssisx report`'s
`scripts/` output (above); and a new `ComplexityStats.ScriptComponentCount`, weighted
identically to `ScriptTaskCount` (`weights.ScriptTask`, no separate config key -- both
represent the same "arbitrary code, no structural model" risk) and folded into the same
unconditional `HighRisk` classification trigger.

### Synthetic parallel-shapes fixture (added 2026-08-25, ahead of `ssisx generate`)

Built to close a real risk in the generator plan (`Migration-Validation-Plan.md`/the
generator's own scope table): every prior fixture -- both PoC packages and all three earlier
synthetic ones -- is a straight-line chain, so `ControlFlowDagSpec.ParallelLevels` had never
been exercised on a real multi-node level, and no fixture had an OLE DB Source -> Destination
direct copy (no transform) or a Flat File Source's error output routed anywhere. A real
client screenshot showed all three shapes in one package (four parallel Execute-SQL/Data-
Flow/Execute-SQL chains; two independent OLE DB Source/Destination pairs sharing one Data
Flow Task; a Flat File Source's error output feeding a Script Component). Building a
sanitized structural equivalent -- generic table/column names, no client data -- was the
fastest way to prove the extractor handles what a real portfolio will contain.

**`SSIS/SyntheticParallelShapes.dtsx`**, three independent top-level branches with NO
precedence constraints between them. Deliberately moved out of this project's own `Fixtures/`
folder (where all three earlier synthetic fixtures live) into the PoC's own `SSIS/` folder,
registered in `SSIS.dtproj` alongside `LoadEmployees.dtsx`/`LoadReferenceData.dtsx`, so it is
visible in SSDT's Package Explorer / Solution Explorer the same way the real packages are.
It is still not part of the PoC's own deliverable -- see CLAUDE.md's "unrelated in-progress
files" note for the same distinction already drawn for `ScriptTest.dtsx`. Its CSV/SQL
dependencies were deliberately NOT moved (see the two paragraphs below) and its own generator
tool resolves them independently of wherever `.dtsx` output is written -- see
`Ssis.Extract.FixtureBuilder.Program`'s own doc comment for why that had to change once the
two directories diverged.

| Branch | Shape | New evidence |
|---|---|---|
| 1 | Execute SQL (create-if-missing) -> Flat File Source -> OLE DB Destination -> Execute SQL (post-load update) | SQL sequenced AFTER a Data Flow Task, not just before -- the C# rewrite's `PackageRunner` only models pre-load SQL today; this is what makes that gap concrete rather than theoretical |
| 2 | One Data Flow Task, two independent OLE DB Source -> Destination pairs, no transform | The direct-copy shape `Etl.Core` has no abstraction for |
| 3 | Flat File Source's main output -> one destination; its ERROR output -> Script Component (real, minimal passthrough) -> a second destination | Error-row redirection, completely unmodeled before this fixture |

Verified real, not assumed: extracting it produces `ParallelLevels[0]` with all three branch
roots in one level (`SQL_CreateLoadATemp`, `DFT_DirectCopy`, `DFT_ErrorRouting`), then two
singleton levels for branch 1's remaining chain -- exactly the "independent chains of
different lengths" case `ParallelLevels`' own doc comment names as a documented limit, now
backed by a real fixture instead of only prose. 100% coverage; `ssisx report`'s
`ObservableEffectsBuilder` correctly reports the Script Component as `UncharacterizedTask`/
`NeedsHumanReview` and all 5 destination tables as `Verifiable` `SqlTable` effects. Pinned by
`tests/Ssis.Extract.Tests/SyntheticParallelShapesTests.cs` (8 tests).

**Built via `src/Ssis.Extract.FixtureBuilder/`, a new checked-in console tool** -- unlike the
first three synthetic fixtures, whose builders were never committed (confirmed by searching
this repo's history for the file's own add-commit: only the `.dtsx` output survives, so none
of those three can be regenerated or extended without re-deriving every object-model call
from scratch). This one can be re-run: `ssis-fixture-builder.exe <output-path>`, against the
backing tables in `tests/Ssis.Extract.Tests/Fixtures/synthetic-parallel-shapes-tables.sql`
(run once against `.\SQLFORPOC_2022`/`SsisPoC`, same "safe to drop/recreate" contract as
`synthetic-fixture-tables.sql`) and the two CSVs under
`tests/Ssis.Extract.Tests/Fixtures/synthetic-parallel-shapes-csv/`. These stay put even
though the built `.dtsx` now lives under `SSIS/` -- they are this tool's own test data, not
part of the PoC's deliverable, and the flat-file connection managers point at them by
absolute path regardless of where the `.dtsx` itself is saved.

**Six real, evidenced gotchas hit building it, none guessable from documentation:**
- **`ProvideComponentProperties()` resets a pipeline component's `Name` back to its class
  default, discarding whatever was set beforehand.** Setting `.Name` before that call (the
  order that reads naturally) is silently overwritten -- confirmed the hard way, not caught by
  any of this repo's own tests: two OLE DB Destinations in `DFT_DirectCopy` both persisted
  `name="OLE DB Destination"` (their auto-uniquified `refId`s differed, which is why extraction
  and every test here looked completely clean), and SSDT's own "Add Existing Item" validation
  rejected the package outright with *"The package contains two objects with the duplicate
  name of 'OLE DB Destination' and 'OLE DB Destination'."* `ssisx` itself never noticed because
  it keys everything by `refId`, never by `name` -- SSDT's stricter check is what caught it.
  Fixed by moving every `.Name =` assignment to AFTER `ProvideComponentProperties()`, in
  `AddOleDbComponent` and both inline Flat File Source / Script Component builders alike.
- **A `FlatFileColumn`'s name has no property on its own COM interface.** Reflecting over
  `IDTSConnectionManagerFlatFileColumn100` (static assembly metadata, and even a live
  instance's runtime members) shows only `ColumnType`/`ColumnDelimiter`/`DataType`/
  `MaximumWidth`/`DataPrecision`/`DataScale`/`TextQualified` -- genuinely no `Name`. Naming a
  column requires casting the SAME object to `IDTSName100` and setting `.Name` there;
  confirmed by loading `LoadReferenceData.dtsx`'s own real `CM_DepartmentCsv` and reading its
  columns' names back exactly that way before trusting the construction path.
- **A Script Component's script-specific custom properties (`SourceCode`/`BinaryCode`/
  `ScriptLanguage`/...) only appear if `UserComponentTypeName` is seeded as a custom property
  BEFORE `Instantiate()`/`ProvideComponentProperties()` runs, not after.** Setting it
  afterward (the order that reads naturally from CLAUDE.md's existing Script Component notes,
  which describe populating an ALREADY-a-script component's properties) leaves the component a
  bare "Managed Component Wrapper" with none of those properties in its
  `CustomPropertyCollection` at all -- confirmed by hitting exactly that failure first, then
  finding the fix by seeding the property earlier and re-testing in isolation.
- **A Flat File Source's error output has a FIXED three-column shape** --
  `"Flat File Source Error Output Column"` (`DT_TEXT`), `ErrorCode`, `ErrorColumn` -- entirely
  unrelated to the source's own column list. A first attempt sized the error-catch
  destination table after the MAIN output's columns and failed to map; caught immediately by
  the builder's own by-name mapping check (which refuses to guess a mapping), not discovered
  later as a silent wrong-schema bug.

- **A Script Component built by seeding properties directly (no VSTA editor round-trip) has
  no cached `BinaryCode`, and SSDT's package validation demands one.** This surfaced only
  after the fixture was moved into `SSIS/` and added to the real project via SSDT's own "Add
  Existing Item" -- neither `ssisx` nor the extractor's own test suite ever opens a package in
  a designer, so it was invisible until then. SSDT rejected the package with *"The binary code
  for the script is not found. Please open the script in the designer... System.
  ArgumentException: Requested value '// this package is structural evidence for the
  extractor, not a runnable pipeline.' was not found."* -- the second half is
  `LoadScriptFromComponent` trying to use the raw `SourceCode` comment text as a lookup key
  once it can't find a real compiled binary. Fixed by setting `DTS:DelayValidation="True"` on
  the containing Data Flow Task (`TaskHost.DelayValidation` in the object model), which defers
  that task's validation to execution time -- which this fixture, documented from the start as
  never compiled/executed, never reaches. Confirmed via the persisted XML
  (`DTS:DelayValidation="True"` on `DFT_ErrorRouting`) and a clean re-run of the full test
  suite (same 121/124, no new failures).

- **A freshly constructed `Package`'s `ProtectionLevel` default does not match the project's,
  and SSDT's solution-level build fails on the mismatch before any per-package validation
  even runs.** Neither `ssisx` nor SSDT's per-package "Add Existing Item" check catches this
  -- it only surfaced on `devenv /Build`/the equivalent Execute-triggered build, with *"Project
  consistency check failed... SyntheticParallelShapes.dtsx has a different ProtectionLevel
  than the project."* All three of the project's existing packages persist
  `DTS:ProtectionLevel="0"` (`DontSaveSensitive`); the builder never set it explicitly, so the
  object model's own default won. Fixed by setting
  `pkg.ProtectionLevel = Microsoft.SqlServer.Dts.Runtime.DTSProtectionLevel.DontSaveSensitive`
  right after package construction (the type name needs full qualification --
  `Microsoft.SqlServer.Dts.Runtime.Wrapper` declares a same-named enum the interop layer also
  pulls in scope, an ambiguous-reference compile error, not a runtime one). Confirmed via a
  clean `devenv /Build SSIS.sln` afterward: `3 succeeded, 0 failed`, `.ispac` rebuilt with all
  four packages.

**Still genuinely unbuilt**, same as Script Task: understanding what the script does.

## Legacy component ID normalization, and Execute Package Task extraction (added 2026-09-02, gap-audit Phase 2)

Two structural blind spots closed from a gap audit against a real 73-package client
portfolio, neither evidenced in that portfolio itself (it is entirely SSIS 2012+ format,
zero legacy spellings, zero Execute Package Task usage) but real correctness/coverage gaps
found by inspection rather than by hitting them on real data.

**Legacy component ID normalization** (`Ssis.Extract.Dtsx.LegacyComponentIds`): before this,
a pipeline component using an SSIS 2005/2008-era `componentClassID` spelling
(`DTSAdapter.OLEDBSource.1`, `DTSTransform.Lookup.4`, ...) or a bare CLSID got no bespoke
payload at all AND was actively mis-flagged as `third-party-component` by `RulesEngine`
(fails its `StartsWith("Microsoft.")` check) -- worse than incomplete, since a client reading
the findings would budget vendor-replacement effort for what might be a plain, fully-supported
stock component. `PipelineReader.ReadComponent` now normalizes at the very top, before its own
`componentClassId ==` dispatch chain, using a small explicit table (versioned ProgIDs matched
by stripping the trailing `.N` redesign-counter suffix; bare CLSIDs matched exactly) --
**sourced from real evidence, not guessed**: the versioned-ProgID naming convention from
Microsoft's own published SSIS 2005 CreationName documentation (sqlis.com, Microsoft Learn's
`PipelineComponentInfos`/`ComponentClassID` references, real SSIS-2008-to-2014 upgrade
threads), and every CLSID entry read directly from a real, published `.dtsx` file's own
`componentClassID`/`name` attribute pair (github.com/LearningTechStuff/SSIS-Tutorial's
`Lesson 5.dtsx`) -- fetched and verified directly, not trusted from a search engine's own
paraphrase (one candidate GUID a search summary attributed to this same file turned out, on
direct fetch, not to be in the file at all, and was discarded rather than used).
`PipelineComponentSpec` gained `RawComponentClassId` (the verbatim original spelling, null
when nothing was normalized -- lossless) and `IsUnresolvedLegacyClsid` (true for a bare CLSID
this table has no evidence for). `RulesEngine`'s `third-party-component` check now runs
against the normalized `ComponentClassId` and explicitly excludes the unresolved-CLSID case,
routing it to the new `unmapped-legacy-clsid` finding instead (see the findings table above)
-- deliberately NOT the same finding as a genuine third-party component, since an unresolved
CLSID might well be a legitimate stock component under a spelling this table just doesn't
know yet, and `contactInfo` (vendor-supplied free text) is never trusted to identify it.
Verified via unit tests feeding every known legacy spelling and CLSID through `ReadComponent`
directly (`LegacyComponentIdsTests.cs`), plus a re-run of the real tracked portfolio
confirming zero `third-party-component`/`unmapped-legacy-clsid` findings either before or
after (a true no-op on real, already-current-format data, as expected).

**Execute Package Task -- extraction only, codegen deliberately deferred**
(`Ssis.Extract.Model.Package.ExecutePackageTaskPayload`): closes a total blind spot --
this task type previously fell through to the generic `UnmappedTaskPayload` with no bespoke
semantics at all. Scoped to extraction plus an honest, named structural gap in
`ssisx generate` (not full codegen) because composing two generated C# projects together is a
genuine, undecided architectural question (in-process project reference vs. shell-out vs.
macro-flatten) that needs a real client package's own usage pattern to decide against, not a
guess. **Verified against the real SSIS 16.0/2022 object model on this machine** (a live
build-and-save-XML round trip, the same "ask the runtime, don't guess" discipline as trap 12)
rather than trusted from documentation alone, because the two written sources actually
disagree: the older MS-DTSX open spec (SSIS ~2008-era) documents
`ExecutableType="SSIS.ExecutePackageTask.2"`/`"STOCK:ExecutePackageTask"` and a
`PackageID`/`VersionID`/`Connection`-keyed file/MSDB-reference shape with no project-reference
concept at all (that mode postdates the 2012 Project Deployment Model); Microsoft's current
property-grid docs describe a `PackageNameFromProjectReference` UI property that turned out
NOT to be a real, separate persisted field at all -- confirmed by reflecting the real
`IDTSExecutePackage100` COM interface (`Microsoft.SqlServer.ExecPackageTaskWrap`), which has
no such member. What is actually saved on this version: `ExecutableType`/`CreationName` are
both literally `"Microsoft.ExecutePackageTask"`; the unqualified `<ExecutePackageTask>`
wrapper element's children are elements (not attributes, unlike `<FileSystemData>`), present
only when non-default: `<ExecuteOutOfProcess>`, `<UseProjectReference>` (present, "True",
ONLY for project-reference mode), `<PackageName>` (the SAME element in BOTH modes -- a
project-relative name when `UseProjectReference` is true, a literal/expression path
otherwise; "PackageNameFromProjectReference" is purely an SSDT property-grid DISPLAY label for
this one field), and, legacy mode only, `<PackageID>`/`<VersionID>`/`<Connection>` (a
connection-manager DTSID reference, resolved the same way `ExecuteSqlTaskPayload`'s own is).
`PackagePlanner.WalkContainer` recognizes `executable.ExecutePackageTask` explicitly (ahead of
the generic "executable type ... is not supported" fallthrough) and reports a clearly-worded,
**Tier 3 (`GapKind.Unclassified`, `MissingToolSupport`)** gap naming the mode, target package,
and (legacy mode) connection manager -- deliberately no AI work packet, since composing two
generated packages is missing TOOL support, not a per-package translation job. Project-
reference parameter bindings (`ParameterAssignments`) are deliberately NOT modeled -- their
real persisted shape was never populated in this probe and so is still unconfirmed; any such
element found bumps `coverage.Unmapped` instead of being silently swept into "covered," the
same discipline the ForEach Loop enumerator/pipeline-output-property fixes established for
exactly this failure shape. New fixture,
`tests/Ssis.Extract.Tests/Fixtures/SyntheticExecutePackageTask.dtsx`
(`Ssis.Extract.FixtureBuilder`'s new `execute-package-task` mode), two independent sibling
tasks -- one per real evidenced mode. Verified via `ssisx extract` (100% coverage, both
payloads populated correctly, connection resolved to name) and `ssisx generate` (both tasks
produce the new named Tier-3 gap, not the generic fallthrough message, and no work packet is
written for either) -- no dtexec run needed, since nothing is generated to execute yet.

## OLE DB Command: real parameter binding order (gap-audit Phase 3.1, added 2026-09-02)

Closes a real, previously-silent correctness gap in `Microsoft.OLEDBCommand` support (added
2026-08-28): `ResolveOleDbCommand` bound each `?` placeholder to a declared input column by
**declaration order** (`<inputColumns>`'s own order in the saved XML), which the original doc
comment called "the well-documented default" -- true only by coincidence, since every prior
evidenced instance had exactly one parameter. Two live SSIS 16.0/2022 object-model probes
(neither checked in) proved this wrong in general and load-bearing for correctness, not cosmetic:

1. **First probe** -- a two-parameter `UPDATE ... SET x = ? WHERE y = ?`, with input columns
   deliberately attached in the OPPOSITE order from their own placeholder position. Real SSIS
   still bound each column to the CORRECT placeholder regardless of declaration order, carried
   entirely by each `<inputColumn>`'s own `externalMetadataColumnId`, resolving to an external
   column literally named `Param_0`/`Param_1` by position. `ParameterMapping` (the property this
   tool's original 2026-08-28 gap check already watched for) was confirmed to never be populated
   at all -- not merely empty, genuinely absent from the saved XML even under this deliberately
   non-default binding.
2. A first fix parsed the `Param_<N>` numeric suffix directly -- and **regressed the one real
   evidenced instance** (`SSIS_From_Sandeep`'s own `Package_Advanced.dtsx`,
   `OLECMD_SetFlag`: `EXEC dbo.usp_SetCustomerFlag ?, N'flagged'`). Caught immediately by
   re-running `ssisx generate` against the real portfolio after the first fix, not assumed safe.
   Root cause: an EXEC stored-procedure call's own external column is named after the
   PROCEDURE's own parameter (`"@CustomerID"`), with no embedded number at all.
3. **Second probe**, built to find the actually-reliable signal -- a real 2-parameter stored
   procedure, `EXEC proc ?, ?`, input columns again attached in the OPPOSITE order from the
   procedure's own declared parameters. Confirmed: the input's own
   `<externalMetadataColumns>` LIST ORDER (as `ReinitializeMetaData` itself saves it) is
   `@CustomerID` then `@NewStatus` -- the procedure's TRUE declared order -- regardless of
   `<inputColumns>` attach order and regardless of the name carrying no position at all. This
   generalizes cleanly to the `Param_N` case too (list order there already matched the numeric
   suffix), so the actual fix resolves true position from each bound external column's own INDEX
   in that list, never from parsing its name.

**What changed:** `OleDbCommandPayload` gained `ColumnMappings` (`List<PipelineColumnMappingSpec>`,
built via `PipelineReader`'s existing `BuildColumnMappings` helper -- the same construction
`OleDbSourcePayload.ColumnMappings` already uses), extracted alongside the pre-existing
declaration-order `Parameters` list (kept for backward compatibility/diagnostics only, no longer
trusted for order). `PackagePlanner.ResolveOleDbCommand` now builds an
`externalColumnPosition: Dictionary<string,int>` from the input's own `ExternalMetadataColumns`
list (index = true placeholder position), resolves each parameter's real position by looking up
its bound external column's name in that dictionary, and rejects (named gap, never guessed) a
column with no resolvable binding, a duplicate binding, non-contiguous positions, or a
placeholder count mismatch -- none of these shapes evidenced in any real package.

**Verified two ways, both against real ground truth, not just each other:**
1. `tests/Ssis.Extract.Tests/Fixtures/SyntheticOleDbCommandReordered.dtsx`
   (`Ssis.Extract.FixtureBuilder`'s new `oledb-command-reordered` mode) -- the positional
   `Param_N` case, `UPDATE ... SET StatusCode = ? WHERE CustomerID = ?` with input columns
   attached in the opposite order from their own placeholder. `StatusCode` seeded at 0;
   the source computes `NewStatus = CustomerID + 100` (101-103, never overlapping a real
   CustomerID 1-3) specifically so a declaration-order regression is unambiguously observable
   (the WHERE clause would then compare CustomerID to a NewStatus value, matching zero rows, so
   StatusCode would stay 0 for every row instead of becoming CustomerID + 100). Run via real
   `DTExec.exe` (`DTSER_SUCCESS`, `StatusCode` = 101/102/103) and via the generated C# built
   against a copy of `Etl.Core` (0 warnings/0 errors) -- **exact row-for-row match**.
2. `tests/Ssis.Extract.Tests/Fixtures/SyntheticOleDbCommandExecNamed.dtsx`
   (`Ssis.Extract.FixtureBuilder`'s new `oledb-command-exec-named` mode) -- the real evidenced
   EXEC-stored-procedure shape, `EXEC dbo.usp_SyntheticSetStatus ?, ?` against a real 2-parameter
   procedure, same reversed-attach-order/observable-regression design. Run via real `DTExec.exe`
   (`DTSER_SUCCESS`, same 101/102/103 result) and via the generated C# (0 warnings/0 errors) --
   **exact row-for-row match** again. Re-running `ssisx generate` against the real
   `SSIS_From_Sandeep` portfolio confirmed `Package_Advanced.dtsx`'s own `DFT_FlagCustomers`
   flow -- previously working, then regressed by the first (Param_N-only) fix, now correct again
   -- is the ONLY file that changed in the whole regenerated portfolio, and the regenerated
   `Package_Advanced` project builds 0 warnings/0 errors against a copy of `Etl.Core`.

`Ssis.Extract.Codegen.Tests`: 228/228 (was 225 before this round). Same 3 pre-existing unrelated
failures elsewhere.

## RowCount: a live, mid-chain component's side effect actually reproduced (gap-audit Phase 3.2, added 2026-09-02)

**A real, previously-silent gap, not a nice-to-have.** `Microsoft.RowCount` was already
special-cased in `PackagePlanner` for exactly one shape -- a DISCARDED dead end at the end of a
Multicast/Lookup branch (2026-09-02, earlier the same day) -- and `TransformEmitter`'s own
`RecognizedPassthroughComponentClassIds` already listed it as a safe passthrough for COLUMN
resolution, since it never changes a row's shape. Neither of those facts says anything about a
LIVE RowCount sitting mid-chain in an ordinary single-destination flow (`Source -> RowCount ->
Destination`): before this round, that shape generated CLEANLY, with zero gaps reported, and
silently dropped the one real thing RowCount exists for -- writing the row count into a package
variable. A reader diffing the `.dtsx` against the generated output would find no trace the
side effect was ever intended, exactly the failure class this tool's whole "gaps not guesses"
design exists to prevent.

**What needed no probing at all.** `VariableName`'s own real persisted shape (a plain
`System.String` custom property, e.g. `"User::MatchedRows"`) was already confirmed directly from
a real, SSDT-authored file -- `SSIS_From_Sandeep`'s own `Package_Transforms.dtsx` -- during the
earlier Lookup+Aggregate round the same day; no new evidence was needed. Confirmed too: RowCount
is a pure synchronous passthrough (`synchronousInputId` set on its own saved `<output>`), and a
downstream destination's own columns resolve by lineageId straight PAST it to the true upstream
producer -- the identical rule every other untouched passthrough transform in this tool already
follows. That meant no chain-walking or column-resolution work was needed at all; the only real
gap was reproducing the one genuine side effect.

**What DID need a live dtexec probe**, per the plan's own "never guess a runtime behavior"
rule: whether the count really is "rows that reached this specific component" (as opposed to,
say, some other measure), and whether a downstream step genuinely sees the TRUE final count
rather than a stale/default one. Both measured together with one fixture, not assumed.

**What was built:**
- New `RowCountPayload { string? VariableName }` (`Ssis.Extract.Model.Pipeline`), wired into
  `PipelineReader.cs` on `componentClassId == "Microsoft.RowCount"` -- generically-captured
  `Properties` already carried the raw value; this is purely a typed, documented accessor.
- `DataFlowPlan.RowCounts` (`PackagePlanner.cs`), resolved by a new `ResolveRowCounts` --
  deliberately scoped to the plain single-source/single-destination shape only (gated on
  `conditionalSplit is null && multicast is null`). A Conditional Split/Multicast branch's own
  mid-chain RowCount (as opposed to the already-handled discarded-dead-end case) is left to
  `ResolveBranch`'s existing generic "not supported" fallthrough -- unevidenced anywhere, and
  deliberately not attempted this round. A RowCount with no (or empty) `VariableName` fails the
  whole flow with a named gap rather than silently dropping the side effect, matching every
  other "no half-finished implementations" convention in this tool.
- New `Etl.Core.Data.CountingRowSource<TRow>` (`D:\PoC\SSIS_Rewrite`), a thin `IRowSource<TRow>`
  decorator mirroring `FilteringRowSource<TRow>`'s own precedent exactly: counts every row as it
  passes through, and calls `PackageVariables.Set(variableName, count)` exactly once, AFTER the
  final `yield return` -- never incrementally, since the count is only meaningful once the
  stream is fully drained (the same "must fully drain before producing a meaningful result"
  property `AggregateRowSource` already has, for the same underlying reason).
- `ProgramFlowSpec.RowCountVariableNames` (`ProgramEmitter.cs`) threads the resolved names
  through; the `case ProgramFlowStep` emission wraps the row source (nested, one
  `CountingRowSource` per RowCount) BEFORE constructing `DataFlowStep`, sharing the exact same
  `packageVariables` local the Script Task/conditional-guard machinery already established --
  deliberately NOT a new DI registration, since `IRowSource<TRow>` is registered in a separate
  factory with no access to that lambda-scoped local. `using Etl.Core.Data;` and the
  `packageVariables` declaration's own emission conditions were both widened to include "any
  flow has a RowCount to reproduce."

**Verified two ways, the strongest evidence tier this project's own philosophy asks for:**
1. **A real dtexec run of the actual pipeline** (`SyntheticRowCountVariable.dtsx`, below):
   `DTSER_SUCCESS`, exactly 3 rows landed in the destination -- confirming RowCount's own
   passthrough never drops/alters/duplicates rows for a linear chain, the structural half of
   the probe's own question.
2. **The generated code, run for real against `.\SQLFORPOC_2022`, built against a copy of
   `Etl.Core` (0 warnings/0 errors)**: the post-flow Script Task's own fill (`ctx.Variables.Get<int>
   ("User::RowsLoaded")`, inserted into a log table via `ctx.Uow.ExecuteSqlAsync`) read back
   **exactly 3** -- an exact match against the true row count, AND proof the downstream step
   sees the correct final value via the shared `PackageVariables`, not a stale/default one
   (`PackageVariables` is entirely this tool's own abstraction, so only running the generated
   code itself could ever answer that half of the question -- no amount of dtexec probing
   could).

**New fixture, `tests/Ssis.Extract.Tests/Fixtures/SyntheticRowCountVariable.dtsx`**
(`Ssis.Extract.FixtureBuilder`'s new `row-count-variable` mode): OLE DB Source (a literal
3-row `VALUES()` list, no separate source table needed) -> RowCount (`User::RowsLoaded`, live,
mid-chain) -> OLE DB Destination, then a post-flow Script Task (no compiled VSTA source, same
accepted limitation as every other Script Task fixture in this tool -- what's under test is the
wiring, and the logic arrives as a hand-written fill either way) reads the variable back.
Backing tables: `tests/Ssis.Extract.Tests/Fixtures/synthetic-row-count-variable-tables.sql`.

Two new `PackagePlannerTests`/`PackageGeneratorTests` cases (the resolved `RowCounts` list; the
exact `CountingRowSource<...>` construction in `Program.cs`) and four new
`Etl.Core.Tests.Data.CountingRowSourceTests` (every row yielded unchanged with the true count
set; the empty-source zero-count case; the variable staying unset until the stream is fully
drained -- pinning the "after the final yield, never incrementally" design decision directly;
`Name`). Regression: `Ssis.Extract.Codegen.Tests` 230/230 (was 228), `Etl.Core.Tests` 65/65 (was
61), same 3 pre-existing unrelated failures elsewhere. Re-ran `ssisx generate` against the real
`SSIS_From_Sandeep` portfolio afterward: byte-identical output -- its own two real RowCount
instances are both the already-handled discarded-dead-end shape, so this round is a genuine
zero-regression addition there, not (yet) a portfolio-visible fix.

### Gap-audit Phase 3.3 -- Aggregate: Sum/Average/Minimum/Maximum/CountDistinct/CountAll (2026-09-02)

Widens `Microsoft.Aggregate` support beyond the original GroupBy/Count-only round (2026-08-30) to
every remaining AggregationType. `AggregationType` has no CLR/SDK backing (confirmed again: every
friendly string -- `"Sum"`, `"Average"`, ..., attempted directly against the live COM property --
was REJECTED with `0xC0204006`, a pure native int enum with zero string-coercion path), so every
raw value AND every semantic rule had to be measured, not guessed.

**A live object-model probe first established the valid raw range**: values 0-7 were accepted by
the component; 8 and 9 were rejected. 0=GroupBy and 1=Count were already known. A SECOND probe
(`Ssis.Extract.FixtureBuilder`'s temporary `aggregate-type-semantics-probe` mode, since removed)
ran a real dtexec pass against deliberately distinguishing seed data -- a group with a NULL, a
duplicate value, and a wide spread (so Sum/Average/Min/Max/CountDistinct/CountAll could never
coincidentally collide), plus a genuinely ALL-NULL group -- and mapped every remaining raw value
unambiguously: **2=CountAll, 3=CountDistinct, 4=Sum, 5=Average, 6=Minimum, 7=Maximum** (all
excluding NULL from their own computation, matching ordinary SQL semantics, except CountAll which
counts every row regardless).

**Three real facts were measured that a T-SQL analogy would have gotten wrong, or simply
couldn't have answered:**
- **CountAll accepts NO `AggregationColumnId` at all** -- confirmed by setting AggregationType=2
  with no column reference and getting the identical row count as a column-qualified CountAll.
  The one function SSIS allows with no column at all, a genuine `COUNT(*)`.
- **Sum/Average/Minimum/Maximum all return NULL, not 0 or an error, when a group has zero
  non-null source values** -- confirmed via a dedicated all-NULL group. This mattered for a
  second, independently-verified reason: **.NET's own `Enumerable.Sum` over a nullable sequence
  returns 0 for an all-null/empty sequence**, a real, confirmed .NET behavior (via a standalone
  console-app probe, not assumed), which is NOT what SSIS does -- so generated code needs an
  explicit guard for Sum specifically. `Average`/`Min`/`Max`'s own nullable-sequence overloads
  already return null for the same case, matching SSIS exactly with no guard needed.
- **Output CLR type is function-dependent, not uniform**: Sum WIDENS the source type (an INT
  source produces `DT_I8`; a FLOAT source produces `DT_R8`) while Average is ALWAYS `DT_R8`
  regardless of source type, and Minimum/Maximum PRESERVE the source type exactly (confirmed for
  both an INT and a FLOAT source column). CountDistinct produces `DT_UI4` -- narrower than
  Count/CountAll's own `DT_UI8`, confirmed real rather than assumed uniform.

**What was built, all in `Ssis.Extract.Codegen`/`Ssis.Extract.Model` -- zero new `Etl.Core`
abstraction, exactly as expected going in**: `AggregateRowSource<TSourceRow,TKey,TRow>`'s own
`project` delegate was already fully generic, so every new function is pure generated-code
breadth, not new runtime machinery.
- `SsisPipelineTypeMap` gained a `"ui4"` entry, mapped to `long` (not `uint`) for the identical
  reason and by the identical convention `"ui8"` already established -- not because
  `AggregateRowEmitter`'s own row class is EF-mapped (confirmed it isn't: a plain POCO, never
  registered with `DbContextEmitter`), but because this table is SHARED, and mapping both to the
  same safe signed type means a CountDistinct value landing at a typical INT destination column
  flows through the EXISTING, already-measured `NarrowI8ToI4` coercion with zero new code.
- `PackagePlanner.AggregateCountSpec`/`AggregatePlan.Counts` were renamed/generalized to
  `AggregateFunctionSpec(OutputColumnName, SourceColumnName, AggregationTypeRaw)`/
  `AggregatePlan.Functions` -- `SourceColumnName` is nullable, true only for a column-less
  CountAll. `PlanAggregate` now accepts any raw value 1-7 as a function column (previously only
  1), validating exactly as before otherwise.
- **A real, previously-unencountered extraction fact was caught building this, not shipped as a
  guess**: SSIS persists an `AggregationColumnId` custom property UNCONDITIONALLY on every
  Aggregate output column, even one with no real column reference -- as a placeholder lineage
  string (`#{Package\...\0:invalid}`) that resolves to nothing real. `LineageBuilder.MakeEdge`'s
  own PRE-EXISTING resilience fallback (built for a hand-edited/partially-modeled package, not
  new this round) turns an unmatched lineageId into a sentinel edge with
  `FromComponentName == "(unresolved)"` rather than dropping it -- so a naive
  `edge?.FromColumnName` read would have silently returned this bogus placeholder STRING as if it
  were a real source column name for CountAll. Caught by a new unit test (`Assert.Null(...)`),
  not by the end-to-end fixture run (CountAll's own generated LINQ, `rows.Count`, never
  dereferences `SourceColumnName` at all, so the bug was functionally invisible in the one place
  that would have surfaced it) -- fixed by checking for the `"(unresolved)"` sentinel specifically
  at this one call site, scoped narrowly rather than widened generically, since every OTHER
  function type must still gap loudly on a genuinely unresolvable reference.
- `AggregateRowEmitter.NullableAggregationTypes` (`{4,5,6,7}`, `internal` so `PackageGenerator`
  reuses the identical set rather than risking two answers drifting apart) drives BOTH the
  aggregate row type's own property nullability AND (newly, this round -- the original Count-only
  round never needed it) the DESTINATION entity's own property nullability, via
  `EntityEmitter.Emit`'s existing optional `nullableColumnNames` parameter -- previously never
  passed for an Aggregate flow at all, since Count/CountAll/CountDistinct/GroupBy never needed it.
- `ProgramEmitter.EmitAggregateFunctionExpression` renders each function's own LINQ expression:
  `rows.Count(r => r.X != null)` (Count, unchanged), `rows.Count` (CountAll), `rows.Select(r =>
  r.X).Where(v => v != null).Distinct().Count()` (CountDistinct), `rows.All(r => r.X == null) ?
  null : rows.Sum(r => r.X)` (Sum, the one explicit guard), and plain `rows.Average(r => r.X)`/
  `rows.Min(r => r.X)`/`rows.Max(r => r.X)` (Average/Minimum/Maximum, relying on .NET's own
  already-correct nullable-sequence semantics).

**Verified two ways, the strongest evidence tier this project's own philosophy asks for:**
1. **A real dtexec run of the actual pipeline** (`SyntheticAggregateFunctions.dtsx`, below),
   reproducing the exact seed shape used to MEASURE the raw-value mapping: Region A (a NULL, a
   duplicate, a spread) -> `TotalRows=4, DistinctAmounts=2, SumAmount=40, AverageAmount=13.333...,
   MinAmount=10, MaxAmount=20`; Region B (a plain group) -> `3, 1, 15, 5, 5, 5`; Region C (ALL
   NULL) -> `2, 0, NULL, NULL, NULL, NULL` -- `DTSER_SUCCESS`.
2. **The generated code, built against a copy of `Etl.Core` (0 warnings/0 errors -- confirming
   the nullable row/entity/transform chain composes correctly under `<Nullable>enable</Nullable>`
   + `TreatWarningsAsErrors`), run for real against `.\SQLFORPOC_2022`**: read back afterward, not
   trusted from the exit code -- **an EXACT match against the real dtexec run above, all-NULL
   group included**, the strongest possible confirmation that generated code reproduces genuine
   SSIS semantics (and NOT .NET's own diverging `Enumerable.Sum` behavior) bit-for-bit.

**New fixture, `tests/Ssis.Extract.Tests/Fixtures/SyntheticAggregateFunctions.dtsx`**
(`Ssis.Extract.FixtureBuilder`'s new `aggregate-functions` mode, replacing the two now-removed
temporary probe modes `aggregate-type-probe`/`aggregate-type-semantics-probe`): OLE DB Source ->
Aggregate (GroupBy Region; TotalRows=CountAll with NO AggregationColumnId at all, proving that
behavior end to end and not just in the removed probe; DistinctAmounts/SumAmount/AverageAmount/
MinAmount/MaxAmount = CountDistinct/Sum/Average/Minimum/Maximum, all over Amount) -> OLE DB
Destination. Backing tables:
`tests/Ssis.Extract.Tests/Fixtures/synthetic-aggregate-functions-tables.sql`.

Two new tests (`PackagePlannerTests.Plan_ResolvesEveryAggregationType_...`,
`PackageGeneratorTests.Generate_WiresEveryAggregationType_...`, the latter asserting the exact
nullable row/entity/transform property declarations and every one of the six LINQ expressions in
`Program.cs`). One pre-existing test's own fixture was bumped, not deleted: `SyntheticAggregate
UnsupportedType.dtsx` used raw value 3 (CountDistinct) as its "still genuinely unsupported"
example -- now resolved by this very round, so a byte-preserving edit moved it to 9 (confirmed
still outside the live COM property's own 0-7 valid range), preserving the same coverage (an
unrecognized value still gaps) without asserting something now false. Regression:
`Ssis.Extract.Codegen.Tests` 232/232 (was 230), same 3 pre-existing unrelated failures elsewhere.
Re-ran `ssisx generate --seams` against the real `SSIS_From_Sandeep` portfolio afterward and built
all four non-Script-seam real packages (`Package_Advanced`/`Package_Exports`/`Package_Legacy`/
`Package_Transforms`, the last being the one real package with an Aggregate,
`DFT_LookupAndAggregate`) against a copy of `Etl.Core`: all four **0 warnings/0 errors** -- zero
regression across the whole tracked portfolio, not just the new fixture. No `Etl.Core` changes
this round (the aggregation semantics live entirely in per-package generated code, same as every
prior numeric-coercion round), so `D:\PoC\SSIS_Rewrite` has nothing to commit.

### Gap-audit Phase 3.4 -- Excel SqlCommand: WHERE clause support, and a permanent ceiling confirmed for column lists (2026-09-02)

No real package in the tracked portfolio uses Excel Source SqlCommand mode at all (the one real
instance, RBC_Demo_ETL's own `EXCEL_SRC_Drip`, is AccessMode=0/OpenRowset) -- entirely
speculative, per the plan's own framing, sign-off obtained before starting.

**A real raw `System.Data.OleDb` probe against the checked-in workbook came first, and it
narrowed this round's own scope more than the plan anticipated.** Before writing any parser: does
ACE OLEDB genuinely reorder/narrow its result set to match an explicit column list, or does it
just return the physical grid regardless of what the SELECT list says? Measured, unambiguously:
`SELECT ReviewedBy, CustomerID FROM [DripEligibility$]` returned columns in EXACTLY that stated
order (`ReviewedBy` first), not the worksheet's own physical layout (`CustomerID` first). This
settles a structural question independent of anything the probe could have measured differently:
`Etl.Core.Excel.ExcelRowSource<TRow>` has no query engine at all (its own doc comment) -- it reads
the raw physical grid via `ExcelDataReader`, always in TRUE physical column order, with zero
ability to reorder or skip columns. A `.dtsx` for a narrowed/reordered SqlCommand records only the
QUERIED (already-narrowed) output columns, never the worksheet's true physical layout -- and this
tool never opens the real data file during `ssisx generate` (every other emitter is purely
`.dtsx`-schema-driven; peeking at a live workbook would be a new, one-off dependency direction
found nowhere else in this project). So a naive "read ordinal N of the SELECT list" translation
would silently read the WRONG physical column whenever the list reorders or narrows -- a silent
correctness bug, strictly worse than a gap. **Column-list support is therefore ruled out as a
PERMANENT architectural ceiling, not a temporary limitation deferred to a future round** -- stated
explicitly in code (`ExcelWhereClauseTranslator`'s own doc comment) rather than silently dropped
from scope.

**WHERE, by contrast, composes safely**, and the same probe measured its real semantics:
- `CustomerID > 5` and `DripEligible = 'Y'` both filter genuinely server-side (4 of 9 rows; 6 of 9
  rows respectively) -- not a no-op, and not client-side-only.
- **String equality is case-INSENSITIVE** -- `DripEligible = 'y'` matched the identical 6 rows as
  `= 'Y'`. This is Jet/ACE SQL's own "Text" comparison mode (a database-level setting governing
  every string operator alike, not per-operator), genuinely different from -- and independently
  measured from -- `Ssis.Runtime.Expressions`' own oracle-verified SSIS EXPRESSION-language
  semantics (ordinal `==`, culture-aware `<`/`>`), which govern a wholly separate SSIS surface.
  Extended to the whole string operator family (`=`/`<>`/`<`/`>`/`<=`/`>=`) uniformly, since the
  measured fact is about the underlying MECHANISM (one database-level comparison mode), not one
  operator instance -- the same reasoning this project has used before (numeric `+` dispatch,
  gate-2 comparison families) once a mechanism, not just an instance, is understood.
- **`!=` is a real, measured Jet/ACE SQL syntax error** ("Syntax error (missing operator)") --
  only `<>` is valid. Since this parser only ever reads an author's own already-SSIS-validated SQL
  text (a package that saved successfully already passed SSDT's own validation), `!=` simply isn't
  part of the accepted grammar; unrecognized text degrades to the ordinary "not a simple
  comparison" gap.
- `AND`/`OR` both genuinely filter correctly (confirmed for `OR` too), but per the plan's own
  stated scope only an AND-chain is supported -- `OR` (and parentheses, and functions) are named
  gaps, never silently mistranslated as AND.

**What was built:** `ExcelSelectStarPattern` (renamed from `BareSelectStarFromSheetPattern`)
widened to capture an optional trailing `WHERE ...` clause alongside the still-mandatory bare `*`
column list. A new `ExcelWhereClauseTranslator.cs` -- a small, deliberately hand-rolled parser (not
ScriptDom -- Jet/ACE's own SQL dialect isn't guaranteed T-SQL-compatible), splitting on `AND`
(case-insensitive) and matching each condition against `<column> <op> <literal>` via one regex.
Each condition resolves its column against the Excel Source's own resolved output columns
(`PipelineResolver.Resolve`, case-insensitive by name, matching both Jet/ACE identifiers and the
worksheet header's own text origin) and dispatches on CLR type: a string column emits
`string.Equals(...)`/`string.Compare(...)` with `System.StringComparison.OrdinalIgnoreCase`
(fully-qualified, matching `ExpressionTranslator`'s own convention of never relying on the
embedding file having `using System;`); a numeric column emits a plain operator comparison
(`==`/`!=`/`<`/`>`/`<=`/`>=`) against the literal. A type mismatch (string column vs. numeric
literal or vice versa) is a named gap, not a guessed coercion. `ExcelFlowSource` gained an optional
`WhereFilter` (a C# boolean expression string); when set, `ProgramEmitter` wraps the existing
`ExcelRowSource<TRow>` construction in `new FilteringRowSource<TRow>(name, innerSource, row =>
{filter})` -- the already-existing, already-proven Lookup-miss-exclusion primitive, reused
unchanged for a wholly different purpose, needing zero `Etl.Core` changes.

**A real bug was caught by the test suite, not shipped**: the first cut of the numeric-operator
mapping passed SQL `=` straight through as C# `=` verbatim (only `<>` was special-cased to `!=`) --
`row.CustomerID = 5` is an ASSIGNMENT in C#, not a comparison, and would have been a genuine
compile-time-breaking bug the moment a real WHERE clause used numeric equality (this round's own
fixture uses `>`, which never exercised the bug -- caught only because a direct unit test for every
operator, including `=`, was written deliberately, not because the fixture happened to hit it).
Fixed: `=` -> `==`, `<>` -> `!=`, everything else passes through unchanged.

**Verified two ways, same discipline as every other round:**
1. **A byte-preserving derived fixture, not a fresh object-model build** -- the same reason
   `SyntheticExcelSourceSqlCommand.dtsx` itself was hand-derived rather than freshly built: a
   confirmed, left-unresolved SSIS object-model quirk (attaching an OLE DB Destination downstream
   of a SqlCommand-mode Excel Source fails validation in this environment). New
   `SyntheticExcelSourceSqlCommandWhere.dtsx`, a byte-preserving regex edit of
   `SyntheticExcelSourceSqlCommand.dtsx` (ObjectName + SqlCommand + destination table name only,
   confirmed via `git diff`-equivalent byte-length arithmetic, not a hand-edit): `SqlCommand =
   "SELECT * FROM [DripEligibility$] WHERE CustomerID > 5"`, the exact shape and literal measured
   by the probe. **Generated, built against a copy of `Etl.Core` (0 warnings/0 errors), and
   actually RUN against `.\SQLFORPOC_2022` and the real checked-in workbook**: read back
   afterward, not trusted from the exit code -- **an EXACT match against the real raw-OleDb probe**
   (`CustomerID`/`DripEligible`/`ReviewedBy` = `6/Y/ops`, `7/N/risk`, `9/Y/ops`, `10/Y/risk`).
2. **`SyntheticExcelSourceSqlCommandUnsupported.dtsx` repurposed, not deleted** -- its own WHERE
   clause (`CustomerID > 5`) is now a SUPPORTED shape, so a byte-preserving edit bumped its
   SqlCommand to a column-list query (`SELECT CustomerID, ReviewedBy FROM [DripEligibility$]`,
   same coincidental byte length, confirmed unchanged file size) -- the one shape now confirmed to
   be a PERMANENT ceiling, not a temporary gap. Re-ran `ssisx generate` against it: still correctly
   produces zero files and a named gap, reworded to name a column list/JOIN/range address rather
   than a WHERE clause.

New `ExcelWhereClauseTranslatorTests.cs` (24 cases against the translator directly: the full
AND-chain shape; every string operator's case-insensitive translation; every numeric operator's
translation including the `=`->`==` fix; OR/parentheses/function-call conditions degrading to
gaps; an unknown column; both type-mismatch directions; case-insensitive and bracketed column-name
resolution; a doubled-single-quote literal escape; a negative numeric literal) plus one new
`PackageGeneratorTests` case (the real fixture, end to end) and one rewrite of the existing
"unsupported" test (now asserting the column-list gap, not the WHERE gap). Regression:
`Ssis.Extract.Codegen.Tests` 256/256 (was 232), same 3 pre-existing unrelated failures elsewhere in
`Ssis.Extract.Tests`. No `Etl.Core` changes this round (`FilteringRowSource<TRow>` already
existed), so `D:\PoC\SSIS_Rewrite` has nothing to commit. Re-ran `ssisx generate --seams` against
the real `SSIS_From_Sandeep` portfolio afterward: `Package_Advanced`'s own gap list (the one real
package with an Excel Source, `EXCEL_SRC_Drip`, AccessMode=0/OpenRowset -- the branch this round
never touches) is unchanged from what's already documented above, and its regenerated project
still builds **0 warnings/0 errors** against a copy of `Etl.Core` -- confirming this round is a
genuine zero-regression addition to the real portfolio, not (yet) a portfolio-visible fix (no real
package uses Excel Source in SqlCommand mode at all).

### Gap-audit Phase 3.5 -- standalone Sort, and a real silent-drop bug fixed along the way (2026-09-02)

The sort-key extraction (`SortPayload`/`SortKeySpec`) was already correct and dtexec-proven from
the Merge Join round -- what was missing was a runtime that applies it OUTSIDE a Merge Join.

**Confirmed real, not assumed, that a standalone Sort was silently INVISIBLE to `PlanDataFlow`
before this round.** Generated a `Source -> Sort -> Flat File Destination` fixture through the
UNMODIFIED, pre-3.5 codegen and read the emitted `Program.cs`: a plain `SELECT ID, Name FROM ...`
with no `ORDER BY`, wrapped in nothing -- zero gaps reported anywhere about the Sort. Root cause:
a Sort is not a "source" (so it never trips `PlanDataFlow`'s multi-source gate) and the
destination search finds any `Microsoft.OLEDBDestination`/`Microsoft.FlatFileDestination`
component anywhere in the pipeline unconditionally, regardless of what feeds it. This is the same
silent-wrong-output failure class as the disabled-executable bug and the Script Component
passthrough bug -- generated code that builds and runs cleanly but reproduces the wrong SSIS
behavior, worse than an honest gap.

**`Etl.Core`: extracted `SortingRowSource<TRow,TKey>`** (`Data/SortingRowSource.cs`) from
`MergeJoinRowSource`'s own private `CollectSortedAsync` helper -- both consumers need the
identical "buffer fully, sort by key" behavior (a real SSIS Merge Join always sits downstream of
a Sort on each of its own two inputs). `MergeJoinRowSource` now constructs one
`SortingRowSource<TSide,TKey>` per side internally instead of duplicating the sort; its own 5
existing tests stayed green unmodified, confirming the extraction is a pure refactor. 5 new
`SortingRowSourceTests` (ascending order regardless of source order; duplicate-key handling
drops/duplicates nothing; a reversed comparer sorts descending -- proving the type is genuinely
comparer-driven, not hardcoded ascending; empty source; `Name`).

**Scope, matching the plan's own stated boundary:** single ascending key only (multi-key/
descending is a separate, currently-unevidenced probe -- `SortKeySpec`'s own doc comment already
states a negative `Position`, which SSIS's docs suggest means descending, has never been
independently confirmed). Applied only to the plain single-source/single-destination flow shape
-- a Conditional Split/Multicast branch's own Sort is the ALREADY-handled `ResolveBranch`
pass-through-hop shape (2026-08-28), and a Sort feeding a Merge Join is handled by `PlanMergeJoin`
(which returns before this round's own code is ever reached). A standalone Sort found inside a
Lookup or Aggregate flow is deliberately left alone too (no real evidenced Lookup/Aggregate+Sort
combination anywhere in the tracked portfolio) rather than guessing at an unevidenced interaction.

**`PackagePlanner.ResolveStandaloneSort`** (new): detects `Microsoft.Sort` components in the
plain flow shape. Returns `(false, null)` -- no gap, nothing to apply -- when none exists, so
every one of this project's existing 35+ fixtures with no Sort is byte-identically unaffected
(confirmed: the full suite, and a full portfolio regeneration, both came back unchanged). Returns
a GAP, never a silent no-op, for every case that can't be safely reproduced: more than one Sort in
the same flow, a key count other than exactly one, or -- the case that most directly matters,
since silently ignoring it would repeat the exact bug this round fixes -- a Sort whose own output
does not provably reach the flow's resolved destination (`ComponentReachesForward`, a plain BFS
walk over `pipeline.Paths`, deliberately generic rather than special-cased to a known list of
"pass-through" component types, since it only answers a structural reachability question, never a
value one). `PackageGenerator`/`ProgramEmitter` thread the resolved `(KeyColumnName, KeyClrType)`
through a new `ProgramFlowSpec.SortKey`, wrapping the row source in
`new SortingRowSource<TRow,TKey>(name, innerSource, row => row.{Key}, Comparer<TKey>.Default)` --
applied closest to the raw source, before any `CountingRowSource` wrap (the two never co-occur in
any evidenced shape today, so ordering between them is otherwise moot).

**Verified two ways, the strongest evidence tier this project's own philosophy asks for:**
1. **A real dtexec run, then the generated code run against the exact same seed data, diffed
   directly.** New fixture, `tests/Ssis.Extract.Tests/Fixtures/SyntheticStandaloneSort.dtsx`
   (`Ssis.Extract.FixtureBuilder`'s new `standalone-sort` mode, reusing `AddSortByKey` verbatim
   from the Merge Join round and the same raw Flat File Destination construction
   `BuildOleDbToFlatFileExport` already uses): OLE DB Source (SqlCommand, seed rows deliberately
   out of ID order: 3, 1, 2) -> Sort (`SORT_ById`, ascending) -> Flat File Destination. Chosen
   deliberately over a SQL destination -- `SqlBulkCopy` gives no storage-order guarantee, so
   ordering has no OBSERVABLE effect there; a flat file's own row order is directly readable back.
   **Real `DTExec.exe` run (`DTSER_SUCCESS`)**: `1,Alice / 2,Bob / 3,Carol` -- ascending by ID,
   despite the source rows being inserted `3,1,2`. **Generated, built against a copy of `Etl.Core`
   (0 warnings/0 errors), output file cleared first, and actually RUN against
   `.\SQLFORPOC_2022`**: **byte-for-byte the exact same file content** -- `1,Alice / 2,Bob /
   3,Carol` -- the strongest possible confirmation that the generated code reproduces genuine SSIS
   Sort behavior, not an assumption about it.
2. **`Ssis.Extract.Codegen.Tests` 262/262 (was 256)** -- one real fixture-based test
   (`Plan_ResolvesAStandaloneSort_...`, asserting the resolved key/CLR type; a matching
   `PackageGeneratorTests` case asserting the exact `SortingRowSource<...>` line in `Program.cs`)
   plus four hand-built-`PipelineSpec` negative tests (no `.dtsx`/object-model build needed --
   `PlanDataFlow`'s own gates only need `ComponentClassId`/`Outputs`/`Inputs`/`Paths` wiring, not
   a full column/type-resolution-ready graph): more than one sort key, zero sort keys, two Sort
   components in the same flow, and a Sort that exists but is never wired to the destination.
   `Etl.Core.Tests` 70/70 (was 65). `Ssis.Runtime.Expressions.Tests` 149/149 unaffected. Same 3
   pre-existing unrelated failures elsewhere in `Ssis.Extract.Tests`.

**Zero regression on the real portfolio**, confirmed rather than assumed: re-ran
`ssisx generate --seams` against `SSIS_From_Sandeep` -- identical "83 files/5 packages/33 gaps"
totals, zero occurrences of the word "Sort" anywhere in the gap report (its two real Sort-bearing
flows, `Package_Transforms`'s own `DFT_SortAndMergeJoin`/`DFT_MergeSortedBranches`, both use Sort
only in combination with Merge/Merge Join, a structurally different, already-handled path that
returns before this round's own code is ever reached). All four non-Script-seam real packages
(`Package_Advanced`/`Package_Exports`/`Package_Legacy`/`Package_Transforms`) rebuilt against a
copy of `Etl.Core`: all **0 warnings/0 errors**, `Package_Transforms` included -- direct proof the
new standalone-Sort code doesn't interfere with the existing Sort+Merge/MergeJoin paths it sits
alongside.

### Gap-audit Phase 3.6 -- true multi-independent-source Merge/UnionAll, the central probe settled by a real dtexec run (2026-09-02)

The largest, least-evidenced item in the gap-audit plan, and the only one requiring its own
explicit sign-off -- given directly by the user ("lets plan and do 3.6 next"). Unlike the already-
closed 2026-08-27/28 UnionAll work (strictly "diverge-then-reconverge from one shared upstream
source through a Conditional Split/Multicast"), this closes the genuinely different shape: TWO
INDEPENDENT top-level sources feeding a `Microsoft.Merge`/`Microsoft.UnionAll` directly, with no
split anywhere upstream -- previously hard-blocked by `PackagePlanner.PlanDataFlow`'s own
multi-source gate (Merge Join is the only carve-out ahead of it, until this round).

**The central open question -- does `Microsoft.Merge` truly interleave its two sorted inputs, or
just concatenate them after independent sorts -- was settled by a real dtexec run, not guessed.**
New fixture, `SyntheticMergeInterleaveProbe.dtsx` (`Ssis.Extract.FixtureBuilder`'s new
`merge-interleave-probe` mode): two GENUINELY INDEPENDENT OLE DB Sources (not diverged from one
shared upstream, unlike every prior Merge fixture in this repo) with a deliberately DISJOINT,
INTERLEAVED key range (Left: ID 1,3,5; Right: ID 2,4,6, both seeded out of order) -> Sort each ->
`Microsoft.Merge` -> Flat File Destination (a SQL destination gives no storage-order guarantee,
same reasoning gap-audit Phase 3.5 already established). **Real `DTExec.exe` run
(`DTSER_SUCCESS`): `1,2,3,4,5,6`** -- a TRUE sort-preserving two-pointer interleave, not
concatenation (which would have read `1,3,5,2,4,6`). This is the single most consequential
measurement in the whole gap-audit plan, and it landed unambiguously on the first clean run.

**`Microsoft.UnionAll`'s own semantics, by contrast, could NOT be independently dtexec-confirmed
for this genuinely-multi-source shape -- a real, left-unresolved SSIS object-model construction
quirk, documented rather than papered over, the same acceptable tier as this repo's own ADO NET
connection-manager and Merge Join extra-output-column quirks.** New fixture,
`SyntheticUnionTwoSources.dtsx` (`union-two-sources` mode): two independent OLE DB Sources (no
Sort at all -- UnionAll carries no "sorted input" concept), each seeded out of order, ->
`Microsoft.UnionAll` -> Flat File Destination. Two independent construction recipes were tried
(the all-at-once recipe `BuildConditionalSplitRemergeFixture` already uses, and an incremental
attach-then-resolve-per-side ordering), both producing a package that saves cleanly via the
object model but fails `dtexec`'s own XML LOAD validation -- BEFORE execution even starts -- with
"Error setting input object during XML load" / "Load error encountered near object with ID 60"
(confirmed to be dtexec's own internal load-position counter, not a real refId -- grepping the
saved XML for any "60"-bearing id finds nothing). Along the way this uncovered a real,
previously-undocumented object-model fact: attaching a path to a `Microsoft.UnionAll` input makes
it auto-provision a FRESH spare input the instant that happens -- the exact same
"auto-provisions-a-spare" behavior this repo's own Multicast section already documents for that
component's OUTPUT side, now confirmed for UnionAll's INPUT side too. Unlike Multicast's own
harmless dangling spare output, the dangling spare input was explicitly removable via
`RemoveObjectByID` (and doing so DID reduce the saved XML from 3 inputs to the intended 2) -- but
the dtexec load failure persisted identically regardless, so the spare was never the true cause.
Also confirmed: `IDTSInput100` does not support an `IDTSName100` cast (`InvalidCastException`,
`E_NOINTERFACE`) -- an input's own `Name` is not independently settable via any interface probed
here. This does NOT block using the fixture for its actual purpose -- `ssisx extract`/`generate`
parse the saved XML directly and never invoke SSIS's own execution-time validation, so this
fixture's genuinely two-independent-source UnionAll shape reads correctly regardless. UnionAll's
real row-order semantics are instead evidenced two other ways: (1) the component's own official
Microsoft-authored description text, extracted directly from this live installation's own object
model and visible verbatim in the saved XML -- "Combines rows from multiple data flows WITHOUT
SORTING" -- a real fact pulled from the runtime, not documentation guesswork; (2) a plain C# unit
test of the new `ConcatenatingRowSource<TRow>` proving its own sequential-concatenation algorithm
correct with no SSIS involved at all, the same fallback tier Merge Join's own unresolved quirk
already established for its join algorithm.

**`Etl.Core`: two new types, both decorators over `IRowSource<TRow>`, same precedent as
`SortingRowSource`/`FilteringRowSource`.** `Data/MergeInterleaveRowSource.cs` -- sorts both sides
via `SortingRowSource<TRow,TKey>` (reused, not duplicated), then a plain two-pointer merge walk,
essentially `MergeJoinRowSource` minus the key-match test: every row from BOTH sides is always
emitted, exactly once, in overall sorted order. Tied-key ordering (left-first) is a stated choice,
not independently dtexec-measured -- the real probe used disjoint key ranges specifically to
settle interleave-vs-concatenation unambiguously and never exercised a genuine tie.
`Data/ConcatenatingRowSource.cs` -- genuinely streaming (no buffering needed, unlike Sort/Merge),
reads every source in order, yielding one side's rows in full before moving to the next. 6 new
`MergeInterleaveRowSourceTests` (reproducing the real probe's own 1,2,3,4,5,6 result exactly;
sorting each side first regardless of source order; both sides fully emitted even when lengths
differ; the stated left-on-tie choice; empty-both; `Name`) and 5 new `ConcatenatingRowSourceTests`
(order preserved per side, not just overall; more than two sources; an empty side skipped
harmlessly; zero sources; `Name`). `Etl.Core.Tests` 81/81 (was 70).

**Codegen, scoped deliberately to the one measured/verified shape -- every side directly
SQL-sourced (OLE DB/ADO NET), no Data Conversion on any side, matching exactly what both probe
fixtures prove; a Flat File/Excel-sourced side, or a Data-Conversion-fed side, is a named gap for
a future round, not guessed at.** New `PackagePlanner.PlanUnion`/`ResolveUnionSide`, gated ahead
of the pre-existing multi-source rejection the same way `PlanMergeJoin` already is -- but ALSO
gated on there being NO `Microsoft.ConditionalSplit`/`Microsoft.Multicast` anywhere in the
pipeline, which is the one thing that keeps this new path from wrongly intercepting the
already-closed "diverge-then-reconverge" UnionAll shape (every instance of THAT shape always has
a split/multicast feeding it; this new shape never does). `ResolveUnionSide` walks backward from
each input -- through an optional `Microsoft.Sort` (required for Merge, confirmed real SSIS
requires sorted input there; never required for UnionAll) and an optional single
`Microsoft.DataConvert` hop (detected and named as a precise gap, not resolved through, since no
genuinely multi-independent-source Merge/UnionAll with a Data Conversion has ever been evidenced
or probed) -- to a real SQL source. `Microsoft.Merge` is additionally hard-capped at exactly 2
inputs (SSIS itself crashes attempting a 3rd, per the Sort+Merge round's own doc comment) and both
sides' sort keys must resolve to the SAME CLR type. New `UnionEmitter.cs` resolves the shared row
type/reader straight off the union component's own raw output columns (no external metadata to
join against, same reasoning `MergeJoinEmitter`/`AggregateRowEmitter` already established) -- ONE
reader is shared by every side, since real SSIS enforces matching schemas across every Merge/
UnionAll input, and the existing "buffer column resolved purely by name" convention
(`TransformEmitter`'s plain `row.ColumnName` passthrough) already works identically regardless of
which side produced a given row. New `PackageGenerator.GenerateUnionFlow` mirrors
`GenerateMergeJoinFlow`'s own shape, including the same `isFlatFileDestination` branching the
plain single-destination path already has (both probe fixtures target a Flat File Destination
deliberately, for the same row-order-observability reason Phase 3.5 already established). New
`ProgramEmitter.UnionFlowSource`/emission case builds each side via its own local function (same
"no side's own IRowSource is independently useful" reasoning `MergeJoinFlowSource`'s own Left/
Right already established), then wraps them in `MergeInterleaveRowSource<TRow,TKey>` (Merge,
exactly 2 sides) or `ConcatenatingRowSource<TRow>` (UnionAll, any number of sides).

**Verified two ways, the strongest evidence tier this project's own philosophy asks for --
though at two different confidence tiers, honestly distinguished rather than blurred together:**
1. **The Merge case: a real dtexec run, then the generated code run against the exact same seed
   data, diffed directly.** Generated `SyntheticMergeInterleaveProbe.dtsx`, built against a copy
   of `Etl.Core` (0 warnings/0 errors), and actually RUN against `.\SQLFORPOC_2022` --
   **byte-for-byte the exact same file content as the real dtexec probe**: `1,L1 / 2,R2 / 3,L3 /
   4,R4 / 5,L5 / 6,R6`. The strongest possible confirmation that the generated code reproduces
   genuine SSIS Merge behavior, not an assumption about it.
2. **The UnionAll case: the generated code run for real, confirming the codegen/runtime chain,
   though the underlying "is this really what real SSIS does" claim rests on the component's own
   official description text plus the algorithm's own isolated unit tests, not an independent
   dtexec run (stated honestly above, not hidden).** Generated `SyntheticUnionTwoSources.dtsx`,
   built against a copy of `Etl.Core` (0 warnings/0 errors), and actually RUN against
   `.\SQLFORPOC_2022`: `5,L5 / 3,L3 / 1,L1 / 6,R6 / 4,R4 / 2,R2` -- each side's own seeded
   `ORDER BY ID DESC` order preserved exactly, Left fully before Right, zero reordering -- the
   expected plain-concatenation result.
3. **`Ssis.Extract.Codegen.Tests` 270/270 (was 262)** -- 6 new `PackagePlannerTests` (both real
   fixtures resolving correctly; a regression-safety test proving the new gate does NOT intercept
   the already-closed diverge-then-reconverge `SyntheticConditionalSplitRemerge.dtsx` shape --
   `flow.Union` stays null and `flow.ConditionalSplit` resolves exactly as before; three
   hand-built-`PackageSpec` negative tests: more than 2 Merge inputs, a Merge side not fed by a
   Sort, a UnionAll side fed by a Data Conversion) plus 2 new `PackageGeneratorTests` asserting
   the exact `MergeInterleaveRowSource<...>`/`ConcatenatingRowSource<...>` lines in `Program.cs`.
   `Ssis.Runtime.Expressions.Tests` 149/149 unaffected. Same 3 pre-existing unrelated failures
   elsewhere in `Ssis.Extract.Tests`.

**Zero regression on the real portfolio**, confirmed rather than assumed: re-ran
`ssisx generate --seams` against `SSIS_From_Sandeep` -- identical "83 files/5 packages/33 gaps"
totals, byte-for-byte. None of the tracked real packages has a genuinely multi-independent-source
Merge/UnionAll (`Package_Transforms`'s own Sort/Merge/UnionAll usage is entirely the
already-closed diverge-then-reconverge or Merge-Join shape), so the new gate correctly never
fires there -- direct proof the new code is additive, not just "didn't crash."

**This closes the last item in the gap-audit plan's Phase 3.** Phase 4 (smarter duplicate-package
selection) was explicitly deferred by the user ("we should not do, I dont think we will run into
it") rather than built.
