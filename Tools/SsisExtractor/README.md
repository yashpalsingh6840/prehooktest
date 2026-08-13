# ssisx -- SSIS package extractor (phase 0)

Status: **slice 5 of 6 complete, minus `pull`** (build plan: `../../Phase0-Extractor-Plan.md`
§8). Slice 6 and slice 5's `pull`/`enrich` both need SSISDB catalog access this engagement
does not have (that plan's §11 decision 4), so they are **out of scope rather than pending** --
`ssisx diff` against a deployed `.ispac` covers the drift-detection use case `pull` existed
to serve, offline. Zero coupling to the PoC's own `SSIS.sln`/build (plan §2.2) -- this is its
own solution, built with plain `dotnet build`, and never referenced by anything under
`../../Scripts` or `../../SSIS`.

## What's done (slices 1-5)

Given a `.dtproj`, a single `.dtsx`, or a directory containing one, `ssisx extract` reads
the raw XML directly (no SSIS install, no GAC, no Windows-only object model on the main
path -- plan §2.1) and writes deterministic, redacted, lossless JSON:

- Project level: deployment model, provenance, package manifest, project parameters, build configurations.
- Package level: identity/provenance/versioning/protection, execution semantics,
  checkpoints, logging, property expressions, parameters, connection managers (including
  full flat-file column/delimiter format), variables.
- **Control flow (slice 2):** the full recursive executable tree, `Microsoft.ExecuteSQLTask`
  payloads (SQL text, connection resolved by name, parameter/result bindings),
  precedence constraints, a derived DAG per container (topological order + parallel-level
  grouping), and event handlers (wired generically, unverified -- neither PoC package has one).
- **Data Flow / pipeline model (slice 3, plan §4.7):** every `Microsoft.Pipeline` task is now
  fully parsed -- components, properties, connections, inputs/outputs and their columns,
  paths -- generically, for any component type, plus bespoke semantic extraction
  (`FlatFileSource`/`OleDbDestination` payloads with resolved column mappings) for the two
  component types this PoC has real evidence for. Derived Column's `Expression`/
  `FriendlyExpression` are promoted onto every output column generically.
- **Column-level lineage (slice 3, plan §5.1):** derived from the pipeline model by
  `Ssis.Extract.Dtsx.LineageBuilder` -- source-to-target edges (including expression-derived
  hops), constant columns (no upstream source, e.g. `GETUTCDATE()`), and unused columns
  (produced, never consumed). `ssisx graph` renders it as Mermaid (`.mmd`) and Graphviz
  (`.dot`) diagrams, one per Data Flow Task.
- **Coverage percentage (plan §7.2):** `ssisx extract --fail-under <pct>` gates the exit
  code on it. Both PoC packages sat around ~19-20% through slice 2 (pipeline internals were
  still raw XML back then) and now score **100%** as of slice 3 -- see `docs/spec-schema.md`.
- Everything not yet typed -- legacy Package-Deployment-Model configurations, an
  unrecognized task type -- is still captured verbatim rather than dropped, in
  `PackageSpec.Unmapped` / `ExecutableSpec.UnmappedTask`.
- **Complexity scoring, findings, non-determinism manifest (slice 4, plan §5.6-§5.8):**
  `ssisx report` produces `inventory.csv/.md` (per-package counts + weighted complexity
  score + Simple/SqlHeavy/Procedural/HighRisk classification), `findings.csv/.md` (23 named
  rules across rewrite-effort/semantic-risk/environment-coupling/dead-or-suspicious/non-
  determinism categories), `nondeterministic.json` (the phase-0 → phase-2 diff-harness
  handoff -- verified to find exactly the three `GETUTCDATE()` columns this repo's own
  CLAUDE.md already names), `unmapped.md`, and `portfolio.md` -- the "here is what you own"
  report the plan calls the actual phase-0 deliverable.

- **SQL harvest + ScriptDom analysis (slice 5, plan §5.4):** every SQL statement (Execute SQL
  Task, OLE DB Source/Command, Lookup) written to its own addressable `.sql` file and parsed
  into referenced objects, statement types, and TRUNCATE/DELETE/MERGE/dynamic-SQL flags.
- **Expression harvest + tokenization (slice 5, plan §5.5):** every SSIS expression in
  `expressions.csv` with its referenced variables/columns, plus `expression-functions.md` --
  which slice of the SSIS expression language the portfolio actually uses, measured.
- **Data-touch inventory + cross-package dependency graph (slice 5, plan §5.3, §5.2):** what
  each package reads and writes, and the implicit "A writes a table B reads" edges that exist
  only in the schedule and nobody's documentation.
- **`.ispac` input (slice 5):** an `.ispac` is a zip, so `extract`/`graph`/`report`/`diff` all
  accept one directly -- no SSIS install, no catalog.
- **`ssisx diff` (slice 5, plan §6.2):** semantic (not textual) diff between two versions of a
  package -- typically repo source vs a deployed `.ispac`, answering "which packages can I not
  trust the source of". Exits 1 on *semantic* drift only, so it survives as a CI gate.

See `docs/spec-schema.md` for the `extract`/`graph` JSON contract, and
`docs/report-schema.md` for `report` + `diff` output (rule catalog, scoring, SQL/expression
analysis, manifest format).

## Build

```powershell
dotnet build SsisExtractor.slnx -c Debug
```

(`dotnet new sln` on this machine's SDK, 10.0.301, generates the newer `.slnx` XML format,
not `.sln` -- same `dotnet build`/`dotnet test` commands work on either.)

## Run

```powershell
# whole project (SSIS.dtproj + both packages)
.\src\Ssis.Extract.Cli\bin\Debug\net8.0\ssisx.exe extract --input ..\..\SSIS --out out

# a single package, unredacted
.\src\Ssis.Extract.Cli\bin\Debug\net8.0\ssisx.exe extract --input ..\..\SSIS\LoadEmployees.dtsx --out out --no-redact

# gate the exit code on coverage (both PoC packages currently 100% -- see docs/spec-schema.md)
.\src\Ssis.Extract.Cli\bin\Debug\net8.0\ssisx.exe extract --input ..\..\SSIS --out out --fail-under 90

# column-level lineage diagrams (one .mmd + .dot pair per Data Flow Task) -- doesn't need
# a prior 'extract' run
.\src\Ssis.Extract.Cli\bin\Debug\net8.0\ssisx.exe graph --input ..\..\SSIS --out out

# inventory + findings + non-determinism manifest + SQL/expression harvest + dependency
# graph + portfolio report -- also doesn't need a prior 'extract' run (docs/report-schema.md)
.\src\Ssis.Extract.Cli\bin\Debug\net8.0\ssisx.exe report --input ..\..\SSIS --out out

# an .ispac works as --input anywhere a .dtsx/.dtproj does (it's just a zip)
.\src\Ssis.Extract.Cli\bin\Debug\net8.0\ssisx.exe extract --input ..\..\SSIS\bin\Development\SSIS.ispac --out out-deployed

# source-vs-deployed drift: exits 1 only on SEMANTIC differences, not on bytes
.\src\Ssis.Extract.Cli\bin\Debug\net8.0\ssisx.exe diff --left ..\..\SSIS\LoadEmployees.dtsx `
    --right ..\..\SSIS\bin\Development\SSIS.ispac --package LoadEmployees
```

`out/` is gitignored (this project's own `.gitignore`) -- it's a run artifact, not source.

## Test

```powershell
dotnet test SsisExtractor.slnx
```

Golden-file tests (`tests/Ssis.Extract.Tests/GoldenFileTests.cs`, plan §7.1) run the
extractor against the PoC's actual two packages -- referenced by relative path, not
copied (plan §2.2) -- and assert byte-equality against `tests/Ssis.Extract.Tests/Golden/*.json`,
modulo a couple of environment-specific fields (see that file's `NormalizeVolatileFields`).
One side effect worth knowing: editing a PoC package's XML directly will make these tests
fail even though nothing about the extractor changed -- that's the intended drift signal,
not a bug.

`tests/Ssis.Extract.Tests/DagAlgorithmTests.cs` covers the DAG algorithm (topological
order, parallel-level grouping, cycle detection, dangling-reference handling) against
synthetic graphs, separately from the golden files -- neither PoC package's control flow
actually branches (both are straight-line chains), so golden testing alone can't exercise
that logic. Reaches `DtsxPackageReader.BuildDag` via `InternalsVisibleTo`, not a public API.

`tests/Ssis.Extract.Tests/LineageBuilderTests.cs` covers `Ssis.Extract.Dtsx.LineageBuilder`
(a public type) the same way, against synthetic pipelines -- an unused column, an
expression referencing more than two upstream columns, and a dangling `lineageId` are none
of them present in either PoC package.

`ComplexityScorerTests.cs`/`RulesEngineTests.cs`/`NonDeterminismAnalyzerTests.cs` cover the
slice 4 derived-analysis layer the same way: neither PoC package has a script task, a
disabled task, an unused variable, a duplicate sibling, or a `NEWID()`/system-variable
non-determinism source, so those branches are proven synthetically too. The parts that
*are* exercised by the real packages (Simple/Procedural classification, the
`error-output-unrouted`/`hardcoded-connection-string` findings, the `GETUTCDATE()` manifest
rows) are checked by actually running `ssisx report` against the PoC and reading its
output, not (yet) a golden-file test the way `extract`'s spec.json is -- worth adding if
this report output needs to stay pinned byte-for-byte the way spec.json does.

`SqlAnalyzerTests.cs`/`ExpressionHarvesterTests.cs` cover the slice 5 parsers. The PoC's two
packages contain exactly one shape of SQL (`TRUNCATE TABLE`), so the read/write
disambiguation, MERGE, dynamic SQL, and parse-failure cases are synthetic -- necessarily, since
that disambiguation exists precisely to avoid a ScriptDom trap (an `INSERT INTO x` target is a
`NamedTableReference` just like a `FROM` source) that one statement type can't demonstrate.

`PackageDifferTests.cs`/`IspacReaderTests.cs` run against **real** artifacts: the differ tests
byte-edit a copy of the PoC's own `LoadEmployees.dtsx` (so a semantic change and an
inconsequential designer-layout change are both genuine), and the `.ispac` tests open the
PoC's actual built archive. The latter **self-skip when `SSIS/bin/Development/SSIS.ispac` is
absent** -- it's a build output, not source, and won't exist on a fresh clone until someone
builds the SSIS project (which needs Windows + SSDT, per the repo's CLAUDE.md).

To regenerate golden files after an intentional model/output change:

```powershell
$env:SSISX_REGENERATE_GOLDEN = "1"
dotnet test SsisExtractor.slnx
Remove-Item Env:\SSISX_REGENERATE_GOLDEN
```

Then review the diff under `tests/Ssis.Extract.Tests/Golden/` like any other code change --
an unreviewed regenerate defeats the point of a golden-file test.

## Out of scope / not implemented

**`pull` and `enrich` are out of scope, not pending.** Both need read access to the client's
SSISDB catalog, which this engagement does not have (plan §11 decision 4). They exit 2 with a
message saying so and pointing at `ssisx diff` against a deployed `.ispac`, which covers the
source-vs-deployed drift-detection use case `pull` existed to serve, entirely offline. If
catalog access ever appears, plan §5/§6 has both designs ready.

Still genuinely not built:

- **Script Task/Component source extraction** (plan §4.5) -- writing out each `ProjectItem`'s
  `.cs`/`.vb`. Neither PoC package has a script task, so there is no real example to model
  against; the `script-task-present` finding fires, but the source isn't extracted.
- **Type/length-narrowing detection** along a lineage chain (plan §5.1) -- a truncation risk
  the package tolerates today; the core edge/constant/unused lineage analysis is done.
- **The object-model oracle** (plan §7.3) -- a `net48`, Windows-only test project asserting
  the XML-derived model agrees with `Application.LoadPackage`. Worth having before trusting
  this on a client portfolio.
- **`--password`** on `extract` is accepted but a no-op (the encrypted-package object-model
  fallback, plan §2.1) so the command line won't change shape when it lands.
- **Bespoke component semantics** exist only for the types this PoC uses (Flat File Source,
  Derived Column, OLE DB Destination). Lookup, Conditional Split, Script Component, async
  transforms and so on are captured *fully* but generically, without a typed payload.
- **`unused-variable`/`unused-connection-manager`** still use a best-effort text search rather
  than a real reference resolver; each such finding says so in its own message.

Plan §8's own risk note is the thing to weigh before going further: **component and task-type
coverage on real client packages, not code volume, is what actually tests this.** Both PoC
packages sit at 100% extraction coverage, which says the model handles *these two*, not that
it handles an unseen portfolio -- getting 3-5 representative real packages (plan §11 decision
2) is still the single biggest accuracy lever available.
