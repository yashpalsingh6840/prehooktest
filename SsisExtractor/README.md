# ssisx -- SSIS package extractor (phase 0)

Status: **slice 5 of 6 complete, minus `pull`; slice 6's object-model oracle also done;
Script Task AND Script Component source extraction (plan §4.5, both halves) also done;
`ssisx conformance` (gate 1) and `ssisx testgen` + `Ssis.Runtime.Expressions` (gate 2) of the
migration-validation plan — also done**
(build plan: `../../Docs/Phase0-Extractor-Plan.md` §8). Slice 6 bundles two things: `enrich`
(needs SSISDB catalog access this engagement does not have, plan §11 decision 4 -- **out
of scope rather than pending**, same as slice 5's `pull`; `ssisx diff` against a deployed
`.ispac` covers the drift-detection use case `pull` existed to serve, offline) and the
object-model oracle (plan §7.3, needs only local SSIS/GAC, not catalog access -- built,
see below). Zero coupling to the PoC's own `SSIS.sln`/build (plan §2.2) -- the main tool is
its own solution, built with plain `dotnet build`, and never referenced by anything under
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
  fully parsed -- components, properties (including output-level `<properties>`, e.g. a
  Conditional Split case's whole expression), connections, inputs/outputs and their
  columns, paths -- generically, for any component type, plus bespoke semantic extraction
  for five component types: `FlatFileSource`/`OleDbDestination` (this PoC's own two
  packages), and `OleDbSource`/`Lookup`/`ConditionalSplit` (synthetic object-model-built
  fixtures, no client evidence yet -- see "Synthetic component-coverage fixtures" below).
  Derived Column's `Expression`/`FriendlyExpression` are promoted onto every output column
  generically.
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

### Synthetic component-coverage fixtures (post-slice-5)

Plan §8's own risk note is that component coverage, not code volume, is what actually
tests this tool -- and both PoC packages only exercise four component/task types. With no
client packages available yet (plan §11 decision 2, still unresolved), two small `.dtsx`
fixtures were built via the real SSIS 17 object model (not hand-written XML -- same
"ask the runtime" discipline as everywhere else in this project) to cover the specific gap
components plan §8 names: a ForEach Loop Container + Script Task
(`SyntheticForEachScript.dtsx`), and an OLE DB Source → Lookup → Conditional Split → 3 OLE
DB Destinations flow (`SyntheticLookupSplit.dtsx`). Both live under
`tests/Ssis.Extract.Tests/Fixtures/` and extract at genuinely-earned 100% coverage
(`SyntheticFixtureTests.cs`). This pass also caught and fixed two real coverage-accuracy
bugs (a ForEach Loop's enumerator config and a pipeline component's output-level
`<properties>` were both silently invisible before) and one `RulesEngine` false positive
(`unused-variable` wrongly flagging a ForEach Loop's own iteration variable). Full detail,
including what's object-model-verified versus best-effort, in `docs/report-schema.md`.

A third fixture, `SyntheticScriptComponent.dtsx`, was added later alongside Script
Component source extraction (below) -- same build discipline, same directory.

### Object-model oracle (plan §7.3, built post-slice-5)

A second, independent way to gain confidence without client packages: `Ssis.Extract.ObjectModel`
(`net48`, Windows-only, `src/Ssis.Extract.ObjectModel/`) loads a package via
`Application.LoadPackage` -- the real SSIS object model, never this tool's own production
path -- and derives the same structural facts plan §7.3 names (executable count/names,
connection manager count/types, variable count, parameter count/types, pipeline
component/path counts, precedence constraint count). `Ssis.Extract.ObjectModel.Tests`
cross-checks those against the already-golden-tested XML-derived `spec.json` for both real
PoC packages, plus two hand-verified checks against the synthetic fixtures above (proving
the oracle's own recursive container-walk and multi-component-pipeline counting, neither
exercised by the two straight-line PoC packages).

Deliberately **not** part of `SsisExtractor.slnx` or referenced by anything under `src/` --
a separate `SsisExtractor.ObjectModel.slnx`, so the main tool's "works on any OS, no GAC"
promise (plan §2.1) stays true. `src/Ssis.Expression.Oracle/` (gate 2, `docs/gate2-schema.md`)
lives in this same slnx for the same reason -- it's the ground-truth generator behind
`Ssis.Runtime.Expressions`'s test corpus, not itself part of the portable main tool. Build/run
explicitly:

```powershell
dotnet build Tools/SsisExtractor/SsisExtractor.ObjectModel.slnx
dotnet test  Tools/SsisExtractor/SsisExtractor.ObjectModel.slnx
```

Needs the SSIS 17 GAC assemblies (this machine's paths are hardcoded as `HintPath`s in the
`.csproj` -- resolve fresh elsewhere via
`[System.Reflection.Assembly]::LoadWithPartialName('Microsoft.SqlServer.ManagedDTS').Location`).
Getting this working surfaced three real object-model quirks along the way, each worth
knowing before extending it further (full detail + fix in `PackageOracle.cs`'s own comments):
- `DtsContainer.Variables` returns every variable *visible* at that scope -- every
  `System::` variable at every level, plus every ancestor's own declared variables
  inherited into view -- not "declared directly here". Comparing `Variable.Parent.ID`
  against the container's own `.ID` is what actually answers that; `ReferenceEquals` does
  not, because COM interop hands back a fresh RCW on each property access.
- Project/package **parameters** are exposed as synthetic variables too, namespaced
  `$Package::`/`$Project::` -- excluded from the variable count (they're a different XML
  element entirely, covered by the parameter count instead); only `Namespace == "User"` is
  a real declared variable.

Because the two runtimes (net8.0 main extractor, net48 oracle) can't share a process, the
comparison is file-based: the oracle test reads the *committed* golden `spec.json` directly
via `JObject` (not deserialized into `PackageSpec`, which lives in a net8.0-only project a
.NET Framework runtime cannot load), rather than shelling out to `dotnet run -- extract`.

### Script Task source extraction (plan §4.5, built post-slice-6)

`ScriptTaskPayload` now captures the real script source, not just the task's declared
surface. **Corrects a wrong assumption made when the synthetic-fixture pass above was
built:** that pass constructed its Script Task via bare object-model property sets, which
never writes a VSTA project into the saved XML at all, so this doc guessed the source must
be "inside the embedded VSTA project binary" -- a guess never checked against a real one.
Reading (read-only -- never edited, never committed by this tool) a genuine SSDT-authored
Script Task that happened to be on disk showed the opposite: the source is plain text, one
`<ProjectItem Name="..." Encoding="...">CDATA</ProjectItem>` sibling per VSTA project file
(`ScriptMain.cs`/`.vb` -- the real entry point by VSTA convention -- plus the
`.csproj`/`.vbproj`, `AssemblyInfo.*`, designer files). `<BinaryItem>` is a precompiled
cache of that same code, not the only copy -- captured by name only
(`ScriptTaskPayload.BinaryItemNames`), never by content.

`ssisx report` now writes every `ProjectItem` to `scripts/<Package>/<Task>/<relative path>`
(plan §6.1), honoring each item's own declared `Encoding` rather than always defaulting to
UTF-8 (one internal item observed is `UTF16LE`). A new `script-source-stripped`
(RewriteEffort/Error) finding fires for the one case plan §4.5 asks to be flagged loudly --
a `<ScriptProject>` with a `<BinaryItem>` but no `<ProjectItem>` at all, i.e. the source is
genuinely gone -- not yet observed on any real package, verified instead with a direct unit
test constructing that XML shape by hand. Full detail in `docs/report-schema.md`'s own
"Script Task source extraction" section.

### Gate 1 — spec conformance (`ssisx conformance`)

**The first output this tool produces that something else consumes rather than a human
reads.** Everything above describes the old package; this turns the spec into the list of
obligations a replacement must satisfy, and an exit code CI can gate on — gate 1 of
`../../Docs/Migration-Validation-Plan.md` §3.

```powershell
ssisx conformance --input ..\..\SSIS --out out          # generate rules + claim stubs
ssisx conformance --input ..\..\SSIS --out out --check   # exit 1 unless all accounted for
```

Ten rule categories (control flow, ordering, data flow, source/target columns, target
schema, transformations, SQL, load semantics, script code), each carrying the concrete
evidence behind it — the declared column type, the fast-load commit size, the friendly
expression text — so the report is reviewable without opening the `.dtsx` beside it.
Measured on this PoC: 31 obligations for `LoadEmployees`, 49 for `LoadReferenceData`.

Two design points that are load-bearing rather than incidental:

- **Claim files are hand-maintained and never overwritten.** Rules are regenerated from
  the `.dtsx` every run and are disposable; the claims recorded against them are the
  migration's audit trail. A stub is written only when none exists.
- **Rule ids must stay stable**, because claim files are keyed by them — so ids derive only
  from meaning-bearing identifiers (refIds, object/column names), never document order or
  GUIDs, and a test guards it.

Gate 1 proves nothing is *missing*; it does not prove anything is *correct* (that's gates
2 and 4). `ScriptCode` rules are explicitly flagged as un-verifiable by any tool and
reported separately, so a green percentage is never mistaken for a working rewrite. Full
detail in `docs/conformance-schema.md`.

### Gate 2 — expression semantics + generated tests (`Ssis.Runtime.Expressions` + `ssisx testgen`)

**The first output built on top of the extractor's own separately-verified test oracle rather
than the extractor's own model.** Gate 1 proves nothing is missing; gate 2 (plan §4) starts
proving things are *correct*, one Derived Column expression at a time.

Two pieces:

- **`Ssis.Runtime.Expressions`** (`src/Ssis.Runtime.Expressions/`, net8.0, no GAC/Windows
  dependency) — a parser + evaluator for the SSIS expression language, verified against a
  136-row corpus captured by actually running the real SSIS 17 expression evaluator
  (`src/Ssis.Expression.Oracle/`, Windows-only) — the same "ask the runtime" discipline as
  trap 12, extended from object-model properties to expression *behaviour*. Encodes real,
  measured divergences from what a naive C# rewrite would assume: NULL propagates through
  string concat and comparisons (three-valued, not `false`); `==` is ordinal but `<`/`>` are
  culture-aware; `(DT_WSTR,n)` truncation is a hard error, never silent; `(DT_I4)` rounds
  half-to-even and casts `TRUE` to **-1**; `SUBSTRING`/`TOKEN`/`FINDSTRING` all have edge
  cases that don't match .NET's own string APIs.
- **`ssisx testgen`** — generates an xUnit test file per package from every Derived Column
  expression, with boundary cases (NULL/empty operand, over-length cast, too-short
  `SUBSTRING` window, case-folding probe) computed by evaluating the expression through
  the library **at generation time** rather than hand-authored.

```powershell
ssisx testgen --input ..\..\SSIS --out out
```

Measured on this PoC: **5 expressions covered, 3 skipped** (the three `LoadedAtUtc` columns'
`GETUTCDATE()` calls — correctly excluded as non-deterministic, same three columns the
non-determinism manifest already names). 30 generated `[Fact]` methods, verified to actually
compile and pass against a real xUnit project referencing the library, not just eyeballed.

A real measurement bug was caught building the oracle, not shipped: an early version reused
one `Package` across every evaluation and produced an internally contradictory reading (both
`"a" < "A"` and `"A" < "a"` came back `True` in the same run). Root-caused and fixed by
isolating every evaluation completely — full account in `docs/gate2-schema.md`.

Full semantics catalog (every measured rule, with the naive-rewrite mistake it prevents), the
one deliberately-unreproduced formatting quirk (float arithmetic → string, not worth reverse-
engineering since no harvested expression in this PoC does it), and testgen's exact
case-generation logic are in `docs/gate2-schema.md`.

### Script Component source extraction (plan §4.5's other half, built same day)

Script Component -- the Data Flow pipeline-transform sibling of Script Task -- turned out
to differ from it in three real, evidenced ways, not just "same feature, different host":
- Its own `ComponentClassId` is the generic `"Microsoft.ManagedComponentHost"`, never a
  Script-Component-specific ID; the actual discriminator is a custom property,
  `UserComponentTypeName` (`"Microsoft.ScriptComponentHost"`).
- Its source lives in two ordinary `isArray="true"` custom properties (`SourceCode`/
  `BinaryCode`), not dedicated `<ProjectItem>`/`<BinaryItem>` elements -- which exposed a
  real, previously-unnoticed bug: `PipelinePropertySpec` had no way to represent an array
  property's multiple `<arrayElement>`s, so they silently collapsed into one run-on string.
  Fixed generically (`PipelinePropertySpec.IsArray`/`.ArrayElements`), not just for this one
  component.
- `ReadOnlyVariables`/`ReadWriteVariables` are **comma**-separated here, confirmed from the
  property's own self-declared description -- genuinely different from `ScriptTaskPayload`'s
  semicolon convention, not assumed to match just because the features are conceptually the
  same.

Unlike Script Task, no real SSDT-authored Script Component was available anywhere in this
repo to read for ground truth, so `ScriptComponentPayload.SourceCodeItems` is kept as an
ordered list of opaque text blobs -- the array *shape* is confirmed real (set through the
object model's own property system and read back), but per-element file identity is not,
so `ssisx report` writes `scripts/<Package>/<Task>/<Component>/source-<index>.cs|.vb|.txt`
rather than a recovered real file name. Full evidence, the `SyntheticScriptComponent.dtsx`
fixture (which also reconfirms Derived Column's passthrough-lineage rule on a third
component type), and the new `script-component-present` finding + `ComplexityStats.
ScriptComponentCount` are in `docs/report-schema.md`'s own "Script Component source
extraction" section.

See `docs/spec-schema.md` for the `extract`/`graph` JSON contract,
`docs/report-schema.md` for `report` + `diff` output (rule catalog, scoring, SQL/expression
analysis, manifest format), `docs/conformance-schema.md` for `conformance` (rule
categories, claim statuses, the rules/claims split), and `docs/gate2-schema.md` for
`Ssis.Runtime.Expressions` + `testgen` (semantics catalog, oracle corpus, case generation).

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

# gate 1: what the rewrite owes, + progress against hand-maintained claim files.
# First run writes an all-Pending claims stub per package; later runs read it back.
# --check exits 1 unless every obligation is accounted for (docs/conformance-schema.md)
.\src\Ssis.Extract.Cli\bin\Debug\net8.0\ssisx.exe conformance --input ..\..\SSIS --out out
.\src\Ssis.Extract.Cli\bin\Debug\net8.0\ssisx.exe conformance --input ..\..\SSIS --out out --check

# gate 2: generated xUnit tests per Derived Column expression, expected values computed
# against Ssis.Runtime.Expressions at generation time (docs/gate2-schema.md)
.\src\Ssis.Extract.Cli\bin\Debug\net8.0\ssisx.exe testgen --input ..\..\SSIS --out out

# an .ispac works as --input anywhere a .dtsx/.dtproj does (it's just a zip)
.\src\Ssis.Extract.Cli\bin\Debug\net8.0\ssisx.exe extract --input ..\..\SSIS\bin\Development\SSIS.ispac --out out-deployed

# source-vs-deployed drift: exits 1 only on SEMANTIC differences, not on bytes
.\src\Ssis.Extract.Cli\bin\Debug\net8.0\ssisx.exe diff --left ..\..\SSIS\LoadEmployees.dtsx `
    --right ..\..\SSIS\bin\Development\SSIS.ispac --package LoadEmployees
```

`out/` is gitignored (this project's own `.gitignore`) -- it's a run artifact, not source.

## Portfolio survey (`scripts/`, added 2026-08-25) -- what ships to a client machine

**The digest now answers the actual sizing question, not just complexity (added 2026-08-26):**
alongside coverage/complexity/findings, `ssisx report` calls `ssisx generate`'s own planner
per package (in-memory, no C# written to disk) and reports how many packages would generate
with zero BLOCKING gaps -- see `generation-readiness.md`, the digest's "Generatable" line, and
its "GENERATION GAP REASONS" section. A `{Package}.Notification` gap is never counted as
blocking (every `.dtsx` gets one; no real package could ever avoid it, and it doesn't stop the
generated project from compiling/running) -- see `PortfolioDigest.IsBlockingGap`'s own comment.

`scripts/Run-Survey.ps1` (and `Run-Survey.bat`, a PowerShell-execution-policy-proof twin
for locked-down machines) is the thing that actually leaves this repo: a wrapper that runs
`ssisx report` + `ssisx conformance` over a client's own package folder and produces two
tiers of output -- the full survey (SQL text, script source, table/server names; stays on
their machine) and a counts-only digest (`portfolio-digest.md/json`, package names reduced
to `PKG-nnn`) that's small enough and sanitized enough to actually send back.

`ssisx.exe` itself is not checked in (`scripts/*` is gitignored except the `.ps1`/`.bat`
wrappers -- see that file's own comment). Build it fresh before shipping:

```powershell
.\scripts\Publish-Survey.ps1
```

This must be a **self-contained, single-file** publish
(`-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
-p:EnableCompressionInSingleFile=true -p:DebugType=none`, all baked into the script) --
plain `dotnet build`/`dotnet publish` without those flags produces either a exe that needs
.NET installed on the target machine, or a correct-but-unwieldy ~210-file/~80 MB tree
instead of one ~36 MB `ssisx.exe`. The target machine needs nothing installed at all: no
.NET runtime, no Visual Studio, no SSIS, no SQL Server, no network -- confirmed by running
the published exe completely standalone. Then zip the whole `scripts/` folder (just those
4 files) and send that; a 30 MB zip is often too large for email as-is, so split it into
parts with plain binary concatenation (`copy /b part1+part2+... out.zip` rejoins them --
built into Windows, no extra software needed on the client end).

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
non-determinism source, so those branches are proven synthetically (via
`TestFixtures.MinimalPackage`) too. `SyntheticFixtureTests.cs` layers a second, more
realistic check on top for `script-task-present`/`loop-present`: run against a *real*
object-model-built fixture rather than a hand-built `PackageSpec`, which is what caught the
`unused-variable` false positive on a ForEach Loop's own iteration variable -- a bug the
synthetic-`PackageSpec` tests couldn't have found, since they never modeled
`ForEachLoopPayload.VariableMappings` in the first place. The parts that *are* exercised by
the real PoC packages (Simple/Procedural classification, the
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

`tests/Ssis.Runtime.Expressions.Tests/` (also run by `dotnet test SsisExtractor.slnx` above)
is gate 2's own suite: `OracleCorpusTests.MatchesOracle` is a `[Theory]` over every row of
`Fixtures/ssis-expression-oracle.tsv`, so the library's spec IS the corpus, not a
hand-written assertion list. `ReferenceEnvironmentTests.cs` covers identifier/`@[...]`
resolution separately, since the oracle corpus (self-contained literals only, evaluated via
an SSIS package variable) can't exercise that path at all. To regenerate the corpus itself
(needs Windows + the SSIS 17 GAC, `SsisExtractor.ObjectModel.slnx`):

```powershell
dotnet build Tools/SsisExtractor/SsisExtractor.ObjectModel.slnx
Tools\SsisExtractor\src\Ssis.Expression.Oracle\bin\Debug\net48\ssis-expr-oracle.exe `
    > Tools\SsisExtractor\tests\Ssis.Runtime.Expressions.Tests\Fixtures\ssis-expression-oracle.tsv
```

Diff before committing -- a changed row means either the corpus grew or this machine's SSIS
evaluates something differently than what was originally measured (docs/gate2-schema.md).

## Out of scope / not implemented

**`pull` and `enrich` are out of scope, not pending.** Both need read access to the client's
SSISDB catalog, which this engagement does not have (plan §11 decision 4). They exit 2 with a
message saying so and pointing at `ssisx diff` against a deployed `.ispac`, which covers the
source-vs-deployed drift-detection use case `pull` existed to serve, entirely offline. If
catalog access ever appears, plan §5/§6 has both designs ready.

Still genuinely not built:

- **Understanding what a Script Task's/Component's code does** -- no C#/VB parsing, no
  detection of which APIs it calls beyond the already-captured `ReadOnlyVariables`/
  `ReadWriteVariables` declared surface. The source is extracted verbatim, not analyzed;
  plan's own words: "Script Tasks are extracted, not understood."
- **Type/length-narrowing detection** along a lineage chain (plan §5.1) -- a truncation risk
  the package tolerates today; the core edge/constant/unused lineage analysis is done.
- **`--password`** on `extract` is accepted but a no-op (the encrypted-package object-model
  fallback, plan §2.1) so the command line won't change shape when it lands.
- **Bespoke component semantics** now also exist for OLE DB Source, Lookup, Conditional
  Split, and Script Component (synthetic fixtures, no client evidence yet -- see above), on
  top of the two types this PoC's own packages use (Flat File Source, OLE DB Destination).
  Async transforms (Sort/Aggregate/Union All), Fuzzy Lookup, and the rest are still captured
  *fully* but generically, without a typed payload -- true of anything without a real
  example behind it, per this project's own "read one first" rule.
- **`unused-variable`/`unused-connection-manager`** still use a best-effort text search rather
  than a real reference resolver; each such finding says so in its own message.

Plan §8's own risk note is the thing to weigh before going further: **component and task-type
coverage on real client packages, not code volume, is what actually tests this.** Both PoC
packages sit at 100% extraction coverage, which says the model handles *these two*, not that
it handles an unseen portfolio -- getting 3-5 representative real packages (plan §11 decision
2) is still the single biggest accuracy lever available.
