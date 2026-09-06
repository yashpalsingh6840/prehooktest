# Generated unit tests, per-package context, and a one-command rewrite pipeline

> **Step 0 of execution:** re-copy this file to `D:\PoC\SSIS\Docs\Generated-Tests-Plan.md`
> (same convention as `Emitter-Rewrite-Plan.md`), then work from there.

## Context

Three asks, in the order they arrived, all pulling in the same direction.

**1. The generated tests were specified but never delivered.** The 8-phase emitter rewrite
(`Docs/Emitter-Rewrite-Plan.md`, committed `6797beb` + `d564e92`) made every SSIS task and
pipeline component its own `internal` method on a `{Package}Package` class explicitly so each
could be tested in isolation. Its §8 called a starter `[Fact]` per component method **"the
acceptance bar, not a nice-to-have."** Measured, not assumed — across all five regenerated
`SSIS_From_Sandeep` packages, the only generated tests that exist are Execute SQL statement-text
assertions: `Package_Advanced.Tests` has **1 test file for a package with 10 methods**.
`TransformTestEmitter` is called from exactly one code path (`PackageGenerator.cs:330`) and
returns empty without a Derived Column or Data Conversion, so every Conditional Split / Merge
Join / Lookup / Aggregate / Multicast flow gets nothing. Nothing tests `RunAsync`, any task
method, any source or sink method, a router, a mapper, a reader, or `SsisFn`.

**2. `appsettings.json` bakes in a real developer's OneDrive path.** Not a new bug — `ssisx`
faithfully carries over whatever path the original `.dtsx` connection manager declared, so
`Package_Advanced`'s config points at `D:\OneDrive\...\KT\RBC\SSIS\sample-data`. That folder
exists on exactly one machine. Any test or local run that reads "the real configured file" is
broken everywhere else, and a client site inherits a path that was never theirs.

**3. There is no pipeline.** Rewriting one package today means remembering five separate
commands in the right order with the right flags, twice interrupted by AI work. It should be one
command — and still be runnable step by step.

Underneath all three is a fourth, standing concern: **token economy in the client environment,
which has GitHub Copilot only.** Every artifact this plan adds is designed so an assistant reads
one small, self-sufficient thing rather than re-deriving facts from the `.dtsx` or re-reading a
generated `.cs` file top to bottom.

**Confirmed constraint that shapes the testing design:** `Etl.Core`'s SQL path is SQL-Server-only
by construction — `UnitOfWork` casts `GetDbConnection() as SqlConnection` and throws otherwise,
`GetBindTokenAsync` runs `sp_getbindtoken`, `SqlRowSource` and `SecondaryConnectionSqlStep`
hardcode `new SqlConnection`. **SQLite is therefore not viable** and is deliberately not pursued:
it needs a second `IUnitOfWork` implementation and still cannot execute the real
`TRUNCATE`/fast-load/bind-token paths, so a green SQLite test would prove a *different dialect*
works — the misleading-green failure mode this project has repeatedly been burned by.

It is also **not needed**. `IUnitOfWork.BulkInsertAsync` is already the seam (its own doc comment
says so), `SqlBulkSink<T>` is provider-agnostic, and `Etl.Core.Tests` already proves the whole
pipeline with zero database via `FakeUnitOfWork` + a `TestDbContext` using a real SQL Server
provider with a fake connection string **that is never opened**. That is the incumbent pattern
and it costs nothing.

---

## Decisions taken (user-confirmed)

1. **Fakes by default; a real server is opt-in.** No SQLite, no Testcontainers, no new
   dependency. Server-needing tests carry `[Trait("Category", "Integration")]` so
   `dotnet test --filter Category!=Integration` is green on any machine.
2. **Deterministic skeleton, AI only for semantics.** `ssisx` emits every assertion it can derive
   with certainty; where only judgement supplies the expected value it emits a **work packet**
   through the existing gap machinery — never a guess.
3. **AI-authored artifacts reuse the `fills-library` mechanism** — same `// ssisx-fill:`
   provenance comment, same `apply-fills` Applied/Unattributed/Stale/Orphaned reporting, same
   staleness check. One mechanism, already built and already known to Copilot.
4. **`ssisx generate` emits `<Package>/README.md`** from the same `PipelineFlowOrder` /
   `ComponentInsight` the walkthrough reads, so walkthrough Seq ↔ generated method ↔ generated
   test are one numbering by construction.
5. **No change to `RunAsync`'s signature.** The class is already
   `internal sealed partial class {Package}Package(IServiceProvider services)` and every method
   takes `IUnitOfWork uow, CancellationToken ct` — it is already fully mockable. A generated
   `PackageHarness` composes a fake-backed `ServiceProvider` in one line. Zero churn on the
   just-committed emitter.
6. **Both sample-data tiers, not one** (see Design) — deterministic synthesis is the always-green
   baseline; AI-supplied realistic data is the optional richer tier that also fixes the OneDrive
   path.
7. **`svk` (SsisValidationKit) joins the client deliverable** as a third `Tools/` folder, so
   `svk walkthrough` and `svk sampledata` are real pipeline steps everywhere.
8. **The pipeline is prompt-only — no shell script.** A new `/ssisx-rewrite` prompt sequences the
   existing per-step prompts rather than restating their command lines.
9. **The pipeline always stops at an AI boundary** and reports; the human resumes. This preserves
   the property the whole taxonomy rests on: a Tier-1 answer is used only when a human set
   `ConfirmedBy`, and an AI's own confidence is never sign-off.
10. **Phased delivery**, each phase independently verifiable.

### Clarifying the originally-listed methods

The list mixed two different things; keeping them apart is what stops per-package test bloat.

| Named method | Where it belongs |
|---|---|
| `SEQ_Prepare`, `SQL_TruncateTargets`, `FST_ArchiveWorkbook`, `DFT_FlagCustomers`, `DFT_ExcelImport`, `FEL_SampleFiles`, `OLESRC_CustomerIds`, `RunAsync` | **Generated per package** — this plan |
| `GetBindTokenAsync`, `ExecuteSqlWithoutTransactionAsync`, `NotifyAsync` | **`Etl.Core` library methods.** Tested once for the whole portfolio in `Etl.Core.Tests` (90 tests today), never regenerated per package. Phase 1 audits and fills real holes there. |
| `RunBranchAsync` | Generated but `private` plumbing. Asserted **through** `RunAsync` — the harness hands out a fresh recording `FakeUnitOfWork` per DI scope, so per-branch `Begin`/`Commit`/`Rollback` is observable without widening visibility. |

`Program.cs` is a 14-line bootstrap with nothing to test; `RunAsync` lives on the package class.

---

## Test taxonomy — what gets generated per method kind

| Method kind | Deterministic starter test | Server? |
|---|---|---|
| `RunAsync` — **failure path** | fake `IUnitOfWork` throws on a chosen step → assert rollback, failure handlers ran via `ExecutedSqlWithoutTransaction`, notifier saw `Succeeded: false`, returns `ExitCode.LoadFailed` | **no — always emittable** |
| `RunAsync` — happy path | full run through the harness; assert step count, order, per-branch transactions | no if every source is file-based; else Integration |
| Execute SQL Task | existing `BuildStatement()` text test **plus** `SQL_X(fakeUow, ct)` → assert `uow.ExecutedSql` | no |
| Secondary-connection SQL | assert it never touches the passed `uow` (mirrors the existing `Etl.Core` test) | **no** -- `PackageHarness` wires a deliberately unreachable, fast-failing `SecondaryConnections` config entry, so the connection attempt itself (a real `SqlException`) is what proves `uow` was untouched |
| File System Task | run against a temp directory; assert the destination file exists | no |
| Sequence container | assert children ran, in order, via the fake's recorders + file effects | no |
| Data Flow — file source | `DFT_X(fakeUow, ct)` → `uow.BulkInserts.Single()` row count + destination table | no |
| Data Flow — SQL source | same assertions | Integration |
| Source — CSV / fixed-width | enumerate `ReadAsync` over the **emitted sample file**; assert row count + first row's columns | no |
| Source — Excel | assert `.Name`; full read tagged (no `.xlsx` writer in this stack) | tagged |
| Source — SQL | assert `.Name`; full read tagged | Integration |
| Sink — SQL | write 2 synthesized entities through `FakeUnitOfWork`; assert `DestinationTable` + `ColumnMappings` | no |
| Sink — flat file | write to a temp path; assert real widths / delimiters / padding | no |
| Router (Conditional Split) | one case per branch + the default, asserting the branch index | no when each condition compares against a literal; else a **test-oracle packet** |
| Transform | existing per-column assertions, **extended to every flow path**, not just single-destination | no |
| Merge Join mapper | `LeftKey` / `RightKey` / `Map` over synthesized rows | no |
| OLE DB Command | assert `uow.ExecutedParameterizedSql` — template + one parameter array per row | no |
| ForEach loop | temp dir with 2 files; assert 2 substituted statements in `ExecutedSql`, ordinal file order | no |
| Multicast | assert every branch received every row | no |
| Aggregate source | synthesized rows → assert grouped keys and counts | no |
| Script Task / Script Component seam | **none generated.** A filled seam is human logic; a test for it is a test-oracle packet | n/a |

Row counts, column names, statement text, table names, widths and delimiters are already in the
spec — those assertions are facts, not guesses. Everything else becomes a packet.

---

## Design

### Sample data — two tiers, deliberately

They answer different questions, and collapsing them into one breaks a guarantee.

> **2026-09-06 UPDATE, superseding this section's own "hard requirement" below**: on the user's
> explicit request (a CSV/fixed-width source silently passing on synthetic data was inconsistent
> with SQL/Excel, which have always needed a real server/workbook), `PackageHarness` no longer
> points at Tier A for ANY file-based source's real-read test — every one (CSV, fixed-width,
> Excel) now resolves through `TestData/`, the same folder `appsettings.Development.json` already
> overrides, and is `[Trait("Category", "Integration")]`-tagged until its own `LOCAL-DATA` fill is
> supplied. Tier A still ships (unchanged: `SampleDataEmitter`, `SampleData/*.csv`, `Copy
> ToOutputDirectory`) but purely as a schema-correct reference for writing that fill — nothing at
> runtime reads it any more. The suite-is-green-with-zero-fills guarantee still holds, exactly as
> stated below, but now via the `.Name`-only test (never opens a file) plus the retagging, not via
> Tier A being what the real read consumes. See `Tools/COPILOT_TESTING_GUIDE.md`'s own updated
> "two sample-data tiers" section for the full account.

**Tier A — deterministic, synthesized, ships with every `generate` run, zero AI, zero fills.**
`ssisx` emits a 2–3 row sample per CSV / fixed-width source into
`{Package}.Tests/SampleData/{Component}.csv` (`CopyToOutputDirectory`), built from the connection
manager's own column names, widths, delimiters and types, reusing `TransformTestEmitter`'s
existing `RepresentativeLiteral` / `RepresentativeOracleValue` helpers so the values agree with
the transform assertions already emitted. The `PackageHarness` points `FileSourceOptions` here
directly, bypassing config-file loading entirely. **This is what makes the suite green
immediately after a fresh `ssisx generate` with nothing filled in** — a hard requirement of
Verification §4, and the reason Tier B cannot replace it.

**Tier B — realistic, human/AI-supplied, and the fix for the OneDrive path.** `ssisx`
deterministically emits `<Package>/appsettings.Development.json` overriding every
`FileSource:Files:<Key>:SourceFolder` to a package-relative `TestData` folder. The *wiring* is
derivable so it is generated, never gapped; only the *data* is missing, so that becomes a
Tier-1-shaped gap (below). Content lives durably in `fills-library/<Package>/TestData/` and
`apply-fills` copies it to `generate/<Package>/TestData/`, `CopyToOutputDirectory` puts it beside
the built exe, and `FileSourceEntry.ResolvedPath` (`Path.Combine(SourceFolder, SourceFileName)`)
resolves the relative folder against the run's working directory. This serves both a real local
`dotnet run` (with `DOTNET_ENVIRONMENT=Development`) **and** an optional richer no-server test
tier that uses realistic data when present and skips cleanly when absent.

**`svk sampledata` seeds Tier B.** Now that svk ships (decision 7), its existing synthesizer
already produces schema-correct CSV, lookup reference tables seeded from a shared key pool, and
`schema.sql` DDL — deterministically, from a `--seed`. So the flow is *generate schema-correct
files with `svk sampledata`, then have the AI make the values realistic*, not *have the AI invent
files from nothing*. Strictly better on determinism, review effort and tokens. This is also the
answer to the original "target DB objects in SQLite" question: `svk sampledata`'s `schema.sql`
already emits the CREATE TABLE DDL, against real SQL Server, with no second dialect involved.

### Test doubles — emitted per package, not a new shared library

`FakeUnitOfWork` and `TestDbContext` exist today only inside `Etl.Core.Tests`, which the
generator does not ship. Rather than add an `Etl.Core.Testing` project and a flag to carry it,
emit them into each test project — the same pattern as `Ssis/SsisFn.cs`, which is already emitted
per package containing only what that package uses. Self-contained, disposable, no new flag.

New `TestDoublesEmitter` → `{Package}.Tests/TestDoubles/`:
- `FakeUnitOfWork.cs` — ported from `Etl.Core.Tests`, keeping its recorders (`ExecutedSql`,
  `ExecutedParameterizedSql`, `ExecutedSqlWithoutTransaction`, `BulkInserts`), its fault
  injection, and — importantly — its mirroring of the real preconditions
  (`ExecuteSqlWithoutTransactionAsync` throws unless `RollbackCalled`; `GetBindTokenAsync`
  caches). Those mirrors are what let failure ordering be pinned with no database.
- `RecordingNotifier.cs` — captures the `PackageResult`.
- `PackageHarness.cs` — a `ServiceProvider` with a **fresh `FakeUnitOfWork` per scope** (so
  `RunBranchAsync`'s per-branch transactions are observable), `NullLogger`, a fixed
  `TimeProvider`, `FileSourceOptions` → `SampleData/`, `BulkCopyOptions`, `DatabaseOptions`, and
  `IConfiguration` only when the package has a secondary connection. Exposes `Package()` plus the
  recorded uows.

### Two new gap kinds — no new tier

Both reuse the existing `GapTier.MissingDatum`, which already grants a work packet
(`HasWorkPacket` keys off tier only, **not** `IsBlocking`) while `PortfolioDigest.IsBlockingGap`
keeps non-blocking gaps out of readiness counts. So both are `IsBlocking: false` and still get a
packet — exactly the semantics needed, with no change to `GapTier` at all.

| New `GapKind` | Token | Asks for | Answer lands in |
|---|---|---|---|
| `LocalFileSourceData` | `LOCAL-DATA` | representative sample files matching the declared column schema, because the `.dtsx` path points at the original SSIS environment | `fills-library/<Package>/TestData/*.csv` |
| `TestOracle` | `TEST-ORACLE` | the expected value for an assertion only judgement can supply (a Script-Component-produced column, a non-literal split condition) | `fills-library/<Package>/Tests/*.cs` |

`apply-fills` routes `fills-library/<Package>/Tests/*.cs` → `generate/<Package>.Tests/Fills/` and
`fills-library/<Package>/TestData/*` → `generate/<Package>/TestData/`. Two new subfolder
conventions; the existing per-package enumeration finds both, and provenance / staleness /
orphan reporting applies unchanged.

### Per-package README — the primary token-saving artifact

`generate/<Package>/README.md`, written every run, **landing in Phase 1** rather than last
because everything downstream reads it:
- what this package is, which `.dtsx` it came from, which flags generated it;
- **the component table** — `Seq | SSIS component | Type | What it does | Generated method
  (full signature) | Test | Gap` — where Seq and ordering come from
  `PipelineFlowOrder.LevelByRefId` and "what it does" from `ComponentInsight.Describe`, the same
  two functions the walkthrough and `PackageClassEmitter`'s own method ordering already read.
  The **full signature** is carried so an assistant never opens `<Package>.cs` just to learn
  whether a source method takes `uow`;
- the control-flow step order with container nesting and concurrent waves;
- where gaps, fills, test data and tests live, and the standing rule that generated code is never
  verified against a real database or real data in this environment.

To keep it from drifting, `PackageReadmeEmitter` and `PackageClassEmitter` share one
signature-building helper rather than each computing signatures independently.

Placed inside the package directory for the same reason `HOW-TO-FILL-GAPS.md` sits at `<out>`
root: whoever opens `Generated.slnx` directly must find it without `Tools/` in view.

### Knowledge documents and the discipline that makes them work

The project already has a proven pattern for this, and the win comes from **self-sufficiency, not
volume**: `ssisx-fill.prompt.md` pins a *"Known-good `Etl.Core` API — do not read `Etl.Core/` to
confirm any of this"* block inline, and `AiPacketEmitter`'s `ScriptTaskContract` embeds the whole
partial-class template in the packet so one read is enough. Proliferating documents works
*against* the goal — `copilot-instructions.md` rule 9 already has to tell Copilot which docs
**not** to open.

So: one new pinned block, defined **once as a C# string constant** and reused verbatim by the
`TEST-ORACLE` packet contract, the `/ssisx-test` prompt, and the testing guide — so all three
cannot drift. It covers `FakeUnitOfWork`'s public surface, `PackageHarness` usage, the
`Category=Integration` convention, and the instruction to get method signatures from the
package README rather than the `.cs` file.

Exactly three new documents, each precisely scoped:

| Document | Role |
|---|---|
| `<Package>/README.md` | per-package facts (above) — generated, always current |
| `Tools/COPILOT_TESTING_GUIDE.md` | the deeper "how testing works here" reference, for cases a packet's own template doesn't cover. Deliberately *not* read in the common case |
| `Tools/.github/prompts/ssisx-test.prompt.md` | the runnable test-writing workflow |

Plus new numbered rules in `copilot-instructions.md`, matching its existing style:
- *Before writing or reviewing a generated test, read `<Package>/README.md` first — never
  re-derive the component-to-method map from the `.dtsx` or by reading `<Package>.cs` top to
  bottom.*
- *A `TEST-ORACLE` packet is self-contained — do not open `TestDoubles/PackageHarness.cs` or
  `COPILOT_TESTING_GUIDE.md` to confirm its pinned API block.*

`HOW-TO-FILL-GAPS.md` is **extended**, not duplicated, to cover the two new gap kinds.

### The pipeline — prompt-only, sequencing existing prompts

No shell script. `/ssisx-rewrite` orchestrates by **referencing the per-step prompts rather than
restating their command lines** — the same anti-duplication discipline
`ssisx-generate.prompt.md` and `ssisx-fill.prompt.md` already use for
`ssisx-extract.prompt.md`'s bootstrap block. Each step remains independently runnable.

| # | Step | Prompt | Kind |
|---|---|---|---|
| 1 | extract + survey | `/ssisx-extract` (exists) | deterministic |
| 2 | walkthrough | `/ssisx-walkthrough` (**new**, `svk`) | deterministic |
| 3 | generate the rewrite | `/ssisx-generate` (exists) | deterministic |
| 4 | Tier-1/2 gap fills | `/ssisx-fill` (exists) | **AI — stop** |
| 5 | apply + build | `/ssisx-fill` step 3 (exists) | deterministic |
| 6 | test-oracle fills + local data | `/ssisx-test` (**new**) | **AI — stop** |
| 7 | apply + build + `dotnet test` | `/ssisx-test` closing step | deterministic |

**Resume is filesystem-detected, not state-file-tracked** — more robust for a prompt-driven flow
and one less artifact to keep in sync. `/ssisx-rewrite` step 0 checks what already exists
(`out/packages/<P>.spec.json` → extracted; `out/generate/<P>/` → generated;
`fills-library/<P>/` non-empty → fills present) and reports where it is picking up, so re-running
it is safe and idempotent.

`--package <P>` is threaded through every step, so "rewrite Package1" never touches the rest of
the portfolio — the discipline `copilot-instructions.md` rule 2 already mandates.

### Shipping `svk`, and folding it into one build

`Tools/README.md`'s "two things live here" table becomes three, with `SsisValidationKit/`
documented alongside `SsisExtractor/` and `Etl.Core/`. `SvkCore` already `ProjectReference`s only
`Ssis.Extract.Model` and `Ssis.Extract.Dtsx`, both already in `SsisExtractor.slnx`, so this adds
**no new dependency**.

Rather than requiring a second `dotnet build SsisValidationKit.slnx` in the common path,
`SvkCore.csproj`/`SvkCli.csproj` are **added as project references into `SsisExtractor.slnx`
too** — a `.csproj` can belong to more than one solution file with zero physical move, so
`Tools/SsisValidationKit/` stays exactly where it is and `SsisValidationKit.slnx` stays valid for
anyone iterating on `svk` in isolation. One `dotnet build SsisExtractor.slnx` now produces both
`ssisx.exe` and `svk.exe`, which is what actually makes "one build step for the pipeline" true —
the bootstrap block in `ssisx-extract.prompt.md` gains an `$svk` resolution (newest `svk.exe`
under `SsisValidationKit/src/SvkCli/bin`) alongside `$ssisx`, and both are satisfied by the same
build call.

**The governance boundary this is meant to protect stays a rule, not a folder wall.** `SvkCore`'s
own README already states it as a hard constraint ("nothing under `Tools/SsisExtractor` is ever
edited, and its own `.slnx` is never touched") — that "never touched" clause is now literally
false (its `.slnx` gains two project entries), so the constraint is restated precisely:
**`SvkCore`/`SvkCli` may reference only `Ssis.Extract.Model` and `Ssis.Extract.Dtsx`, never
`Ssis.Extract.Codegen`/`Ssis.Extract.Cli`, regardless of shared solution membership** — pinned as
a doc comment on `SvkCore.csproj` itself, not just in a README, so it survives someone editing
the `.slnx` without reading prose first. Verification §7 checks this by grepping `SvkCore`'s own
`ProjectReference`s after the change, not just running it once.

---

## Phases

| # | Deliverable | Done when |
|---|---|---|
| 0 | Re-copy this plan to `Docs/Generated-Tests-Plan.md` | file exists |
| 1 | **Foundation.** `TestDoublesEmitter` + `PackageHarness`; `RunAsync` failure-path test; Execute SQL step-level test; `PackageReadmeEmitter`; `appsettings.Development.json` + `TestData/` wiring; `TestProjectEmitter` sample-data copy rules; xUnit/Test.Sdk drift fixed (`2.5.3`/`17.8.0` → `2.9.3`/`17.14.1`); audit and fill real `Etl.Core.Tests` holes in `GetBindTokenAsync` / `ExecuteSqlWithoutTransactionAsync` / `NotifyAsync` | `Package_Advanced.Tests` builds and `dotnet test --filter Category!=Integration` is green **with no database reachable and zero fills applied** |
| 2 | **Per-component deterministic tests.** Every row of the taxonomy table; Tier-A sample synthesis; `Integration` traits; `TransformTestEmitter` called from every flow path | every component method in all 9 packages has ≥1 test; Verification §4 and §6 pass |
| 3 | **AI tier + knowledge docs.** `GapKind.TestOracle` and `GapKind.LocalFileSourceData` + packets; `apply-fills` routing for `Tests/` and `TestData/`; the shared pinned-API constant; `COPILOT_TESTING_GUIDE.md`; `HOW-TO-FILL-GAPS.md` extension; new `copilot-instructions.md` rules | a real Copilot round trip writes a test fill and a data fill, `apply-fills` reports both `Applied`, and a corrupted hash reports `Stale` without copying |
| 4 | **Pipeline + `svk` shipped.** `SsisExtractor.slnx` gains `SvkCore`/`SvkCli` project entries; `Tools/README.md` 3-folder layout; `/ssisx-walkthrough`, `/ssisx-test`, `/ssisx-rewrite` prompt files; bootstrap block gains `$svk`; `COPILOT_GUIDE.md` + `CLAUDE.md` updates | `dotnet build SsisExtractor.slnx` produces both `ssisx.exe` and `svk.exe`; `/ssisx-rewrite Package_Advanced` runs end to end from a clean `out/`, stopping at both AI boundaries, and re-running resumes correctly |

**Opt-out:** a new `--skip-tests` switch on `ssisx generate` (default: tests on). Named plainly —
unlike `--unsafe-skip-seams`, skipping tests loses coverage but cannot produce silently wrong code.

---

## Files touched

| File | Change |
|---|---|
| `src/Ssis.Extract.Codegen/TestDoublesEmitter.cs` | **new** — `FakeUnitOfWork`, `RecordingNotifier`, `PackageHarness` |
| `src/Ssis.Extract.Codegen/ComponentTestEmitter.cs` | **new** — the per-method starter `[Fact]`s of the taxonomy table |
| `src/Ssis.Extract.Codegen/SampleDataEmitter.cs` | **new** — Tier-A deterministic CSV / fixed-width fixtures |
| `src/Ssis.Extract.Codegen/PackageReadmeEmitter.cs` | **new** — `PipelineFlowOrder` + `ComponentInsight`; shares the signature helper with `PackageClassEmitter` |
| `src/Ssis.Extract.Codegen/PackageClassEmitter.cs` | extract the component-method signature helper for reuse; no emitted-code change |
| `src/Ssis.Extract.Codegen/TestProjectEmitter.cs` | `SampleData` / `TestData` content items; xUnit version alignment |
| `src/Ssis.Extract.Codegen/TransformTestEmitter.cs` | call from every flow path; widen past the string-output-only pilot scope where the oracle can answer |
| `src/Ssis.Extract.Codegen/ProjectEmitter.cs` | `appsettings.Development.json`; `TestData/` copy rules |
| `src/Ssis.Extract.Codegen/PackageGenerator.cs` | drive the new emitters; keep the existing `SiblingFiles` + `$"{Package}.Tests/{path}"` rewrite |
| `src/Ssis.Extract.Codegen/AiPacketEmitter.cs`, `GapIdentity.cs` | two new kinds: tokens, contracts, evidence, response formats; the shared pinned-API constant |
| `src/Ssis.Extract.Model/Analysis/GapSpec.cs` | `GapKind.TestOracle`, `GapKind.LocalFileSourceData` (**no new `GapTier`**) |
| `src/Ssis.Extract.Cli/GenerateCommand.cs` | `--skip-tests`; emitted `Directory.Packages.props` versions; `HOW-TO-FILL-GAPS.md` extension |
| `src/Ssis.Extract.Cli/ApplyFillsCommand.cs` | route `Tests/` → `{Package}.Tests/Fills/` and `TestData/` → `{Package}/TestData/` |
| `Tools/.github/prompts/ssisx-{walkthrough,test,rewrite}.prompt.md` | **new** — existing template: front matter, known-good constraints, bootstrap by reference, `## Logging` |
| `Tools/.github/prompts/ssisx-extract.prompt.md` | bootstrap block gains `$svk` resolution |
| `Tools/COPILOT_TESTING_GUIDE.md` | **new** — the deeper testing reference |
| `SsisExtractor/SsisExtractor.slnx` | add `SvkCore`/`SvkCli` project entries (no source changes to either) |
| `SsisValidationKit/src/SvkCore/SvkCore.csproj` | doc comment pinning the two-project reference boundary |
| `Tools/README.md` | three-folder layout; one combined build step |
| `Tools/COPILOT_GUIDE.md`, `Tools/.github/copilot-instructions.md`, `CLAUDE.md` | the tiers, `--skip-tests`, `svk`'s two commands, the new numbered rules, and that `dotnet test --filter Category!=Integration` is a blessed verification step |
| golden `.cs.txt` files under `tests/Ssis.Extract.Codegen.Tests/Golden/` | regenerate; **review the diff by eye, never bless it** |

Nothing under `Tools/Etl.Core` changes shape — only `Etl.Core.Tests` (dev repo) gains tests.

---

## Verification

Every prior round in this project shipped a real bug a green suite alone did not catch, so these
are checks against real output.

1. **`scripts/Run-Tests.ps1` green** — all suites, both repos, zero failures, plus new emitter
   tests in `Ssis.Extract.Codegen.Tests`.
2. **Byte-diff proof that production code is untouched.** Build the CLI from a `git worktree` at
   HEAD, generate all 9 packages both ways, and confirm every file under `generate/<Package>/` is
   byte-identical apart from the new `README.md` and `appsettings.Development.json`. This must not
   alter one line of generated production C#.
3. **The OneDrive path is genuinely fixed.** `appsettings.Development.json` resolves to the local
   `TestData/` folder; run the built exe with `DOTNET_ENVIRONMENT=Development` on a machine where
   the OneDrive path does not exist and confirm it reads local data.
4. **The acceptance bar — generated tests run and pass, with zero fills.** Regenerate the whole
   portfolio into a fresh directory under `D:\tmp` (never `D:\PoC\Tools\out`),
   `--seams --etl-core Tools/Etl.Core --fills fills-library`, `dotnet build Generated.slnx` →
   **0 warnings / 0 errors** (`TreatWarningsAsErrors=true` means an unused `using` in a generated
   test fails the build), then `dotnet test --filter Category!=Integration` **with no database
   reachable and before any fill is applied** → all green.
5. **Then the integration tier for real**, against `.\SQLFORPOC_2022`, for this PoC's own
   `LoadEmployees` / `LoadReferenceData`. Row counts read back from the tables, not trusted from
   the exit code.
6. **Walkthrough correspondence, by hand.** Open
   `Out/walkthrough/walkthrough/Package_Advanced.walkthrough.md` beside `Package_Advanced.Tests`
   and the new README, and confirm every component-table row has a test method whose number
   matches its Seq. A component with no test is a bug in the emitter, not an acceptable gap.
7. **`svk` runs as a shipped tool, built once.** From a clean clone, `dotnet build
   SsisExtractor.slnx` following only `Tools/README.md` and confirm `svk.exe` exists alongside
   `ssisx.exe`; run both `svk` commands; grep `SvkCore.csproj`'s `ProjectReference`s to confirm the
   two-project boundary held; grep the shipped tree for dev-machine absolute paths (`D:\PoC`,
   `OneDrive`) — the same leak class already caught twice in this project.
8. **A real failure is genuinely caught.** Deliberately corrupt one generated method (change a
   statement's table name) and confirm the generated test goes red. A suite that passes against
   broken code is worse than none.
9. **A real Copilot round trip.** Point Copilot at a `TEST-ORACLE` packet and a `LOCAL-DATA`
   packet, have it write both fills into `fills-library`, run `apply-fills`, confirm `Applied` in
   `fills-applied.json`, then corrupt one `EvidenceSha256` and confirm `Stale` with the file
   **not** copied.
10. **The pipeline end to end.** `/ssisx-rewrite Package_Advanced` from an empty `out/`: stops at
    both AI boundaries, resumes correctly when re-run, and touches no other package.

## Out of scope, deliberately

- **SQLite / Testcontainers / any new test dependency** — reasoned through above.
- **Changing `RunAsync`'s signature or any generated production-code shape.**
- **Tests for Script Task / Script Component seams** — a filled seam is human logic; a test for it
  is a test-oracle packet, not something `ssisx` can derive.
- **A Tier-3 AI path.** `MissingToolSupport` still gets no packet: a per-package hand-written test
  around a systemic emitter gap would hide the one thing `generation-readiness.md` exists to
  surface.
- **A non-interactive / CI mode for the pipeline.** The AI boundaries are gated by design
  (decision 9); a `-Yes` equivalent can come later once the gated flow is proven.
- **Gate 3 corpus comparison.** That is `Validation/`'s job and needs a captured corpus a client
  site does not have.
