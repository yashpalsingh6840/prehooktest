# Emitter rewrite: a method per SSIS task, mapping 1:1 to the walkthrough

> **First implementation step: copy this file to `D:\PoC\SSIS\Docs\Emitter-Rewrite-Plan.md`**
> (plan mode only permits writing the plan file itself; the user asked for it under `Docs/`).

## Context

`ssisx generate` produces a correct but structurally opaque `Program.cs`. Reading
`Out\walkthrough\walkthrough\Package_Advanced.walkthrough.md` beside
`out-verify-2026-09-04\generate\Package_Advanced\Program.cs`, there is no anchor connecting the
two: `SEQ_Prepare` does not appear in the code at all (its two children were hoisted into
`PreLoadStatements`/`PreLoadFileActions`), the three concurrent pipelines are three
indistinguishable `results.Add(await …)` lines, and every component is buried in a
`builder.Services.AddScoped<IRowSource<T>>(sp => …)` lambda far from where it runs. Mapping a
walkthrough row to code is manual and error-prone — which is exactly the review step this whole
migration depends on.

**Goal:** generated code whose structure *is* the walkthrough. One method per SSIS executable, one
method per pipeline component, container nesting preserved, and SSIS's own concurrency expressed as
real `Task.WhenAll`. Readable, reviewable, unit-testable, and diffable against the human-authored
walkthrough.

**Decisions taken (user-confirmed):**
1. **Method per component, streaming.** Source/sink/router methods return the runtime object; the
   existing verified `DataFlowStep<TRow,TEntity>` still drives the streaming loop. No buffering, no
   change to row-level behaviour.
2. **Real parallelism, one transaction per concurrent branch.** Faithful to SSIS (none of these
   packages declares `DTS:TransactionOption`, so SSIS itself auto-commits per task). Whole-package
   rollback is deliberately given up *only where SSIS was concurrent* — see §4.
3. **Untranslatable tasks become `partial` method seams**, reusing the existing
   `--seams` / `ssisx apply-fills` machinery, so an unported task is a **compile error (CS8795)**,
   never a silent no-op.
4. **The acceptance bar is: the smallest possible unit of work per SSIS component, each one
   independently testable and verifiable end to end.** This drives visibility (`internal`, not
   `private`) and generated per-component tests — see §8, which is the section to satisfy first if
   anything else has to be traded off.

---

## Target output (Package_Advanced, abridged but literal)

`Program.cs` — bootstrap only:

```csharp
using Etl.Core.Hosting;
using Package_Advanced.Model;
using Microsoft.Extensions.DependencyInjection;

const string PackageName = "Package_Advanced";

var builder = EtlHost.Create(args, PackageName);
builder.Services.AddEtlDbContext<Package_AdvancedDbContext>();
builder.Services.AddBulkSink<DripEligibility>();
builder.Services.AddEmailNotifications(builder.Configuration);

using var host = builder.Build();
return await new Package_AdvancedPackage(host.Services).RunAsync(CancellationToken.None);
```

`Package_Advanced.cs` — the ported package, 1:1 with the walkthrough:

```csharp
/// <summary>
/// Ported from SSIS package 'Package_Advanced'.
/// Control flow (from the package's own precedence constraints):
///   SEQ_Prepare -> { DFT_FlagCustomers | DFT_ExcelImport | FEL_SampleFiles }
/// The three in braces ran CONCURRENTLY under SSIS and do so here.
/// </summary>
internal sealed partial class Package_AdvancedPackage(IServiceProvider services)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        // ... timing / logging / notifier / try-catch, see §4 ...

        // Wave 1 -- one transaction.
        await InOwnTransactionAsync(SEQ_Prepare, ct);

        // Wave 2 -- SSIS ran these 3 concurrently (no precedence constraint between them).
        // Each runs in its OWN transaction, matching SSIS's per-task auto-commit.
        await Task.WhenAll(
            InOwnTransactionAsync(DFT_FlagCustomers, ct),
            InOwnTransactionAsync(DFT_ExcelImport,   ct),
            InOwnTransactionAsync(FEL_SampleFiles,   ct));
    }

    // ===== SEQ_Prepare (STOCK:SEQUENCE) ==================================

    internal async Task SEQ_Prepare(IUnitOfWork uow, CancellationToken ct)
    {
        await SQL_TruncateTargets(uow, ct);
        await FST_ArchiveWorkbook(uow, ct);
    }

    /// <summary>Execute SQL Task 'SQL_TruncateTargets' on CM_SQL_SSISDemo.</summary>
    internal async Task SQL_TruncateTargets(IUnitOfWork uow, CancellationToken ct) =>
        await Step(new ExecuteSqlStep("SQL_TruncateTargets",
            SQL_TruncateTargetsStatement.BuildStatement(), Log<ExecuteSqlStep>()), uow, ct);

    /// <summary>File System Task 'FST_ArchiveWorkbook'
    /// -- CopyFile CM_FILE_SourceWorkbook -> CM_FILE_ArchivedWorkbook (overwrite).</summary>
    internal async Task FST_ArchiveWorkbook(IUnitOfWork uow, CancellationToken ct) =>
        await FileSystemActionRunner.RunAsync(new FileSystemPreLoadAction(
            FileSystemOperation.Copy,
            File("CM_FILE_SourceWorkbook"),        // from appsettings.json -- see §5
            File("CM_FILE_ArchivedWorkbook"),
            OverwriteDestination: true), ct);

    // ===== DFT_FlagCustomers (Data Flow Task) ============================

    /// <summary>Data Flow Task 'DFT_FlagCustomers'.
    ///   1. OLESRC_CustomerIds -> 2. OLECMD_SetFlag</summary>
    internal async Task DFT_FlagCustomers(IUnitOfWork uow, CancellationToken ct) =>
        await Step(new OleDbCommandStep<DFT_FlagCustomersSqlRow>(
            "DFT_FlagCustomers",
            OLESRC_CustomerIds(uow),
            OLECMD_SetFlagSql,
            row => new object?[] { row.CustomerID },
            Log<OleDbCommandStep<DFT_FlagCustomersSqlRow>>()), uow, ct);

    /// <summary>1. OLE DB Source 'OLESRC_CustomerIds'
    /// -- reads `SELECT DISTINCT CustomerID FROM dbo.Orders` via CM_SQL_SSISDemo.</summary>
    internal IRowSource<DFT_FlagCustomersSqlRow> OLESRC_CustomerIds(IUnitOfWork uow) =>
        new SqlRowSource<DFT_FlagCustomersSqlRow>(
            "OLESRC_CustomerIds",
            new SqlSourceOptions {
                ConnectionString = SqlConnectionStringFactory.Build(Db()),
                CommandText      = "SELECT DISTINCT CustomerID FROM dbo.Orders" },
            DFT_FlagCustomersSqlRowReader.Read,
            uow);

    /// <summary>2. OLE DB Command 'OLECMD_SetFlag'
    /// -- runs once per row via CM_SQL_SSISDemo. `?` -> `{0}` (EF raw-SQL placeholder).</summary>
    internal const string OLECMD_SetFlagSql = "EXEC dbo.usp_SetCustomerFlag {0}, N'flagged'";

    // ===== DFT_ExcelImport (Data Flow Task) ==============================

    /// <summary>Data Flow Task 'DFT_ExcelImport'.
    ///   1. EXCEL_SRC_Drip -> 2. OLEDST_Drip</summary>
    internal async Task DFT_ExcelImport(IUnitOfWork uow, CancellationToken ct) =>
        await Step(new DataFlowStep<DripEligibilityExcelRow, DripEligibility>(
            "DFT_ExcelImport",
            EXCEL_SRC_Drip(),
            new DripEligibilityTransform(),
            OLEDST_Drip(),
            Log<DataFlowStep<DripEligibilityExcelRow, DripEligibility>>()), uow, ct);

    /// <summary>1. Excel Source 'EXCEL_SRC_Drip'
    /// -- reads worksheet 'DripEligibility$' via CM_EXCEL_Drip.</summary>
    internal IRowSource<DripEligibilityExcelRow> EXCEL_SRC_Drip() =>
        new ExcelRowSource<DripEligibilityExcelRow>(
            "EXCEL_SRC_Drip",
            new ExcelSourceOptions {
                FilePath      = File("EXCEL_SRC_Drip"),
                WorksheetName = "DripEligibility$",
                HasHeaderRow  = true },
            DripEligibilityExcelRowReader.Read);

    /// <summary>2. OLE DB Destination 'OLEDST_Drip'
    /// -- writes [dbo].[DripEligibility] via CM_SQL_SSISDemo (fast load).</summary>
    internal IBulkSink<DripEligibility> OLEDST_Drip() =>
        new SqlBulkSink<DripEligibility>(Opt<BulkCopyOptions>(), Log<SqlBulkSink<DripEligibility>>());

    // ===== FEL_SampleFiles (STOCK:FOREACHLOOP) ===========================
    // ... same pattern ...
}
```

`File(key)`, `Db()`, `Opt<T>()`, `Log<T>()`, `Step(...)`, `InOwnTransactionAsync(...)` are ~25 lines
of emitted private helpers at the bottom of the class (§6).

---

## 1. Planner: preserve container nesting, stop hoisting

`PackagePlanner.WalkContainer` currently passes the caller's `steps`/`preLoadStatements` lists
straight down into a `STOCK:SEQUENCE` recursion, so **`SEQ_Prepare` leaves no trace in the plan**;
and any pre-flow Execute SQL / File System task is hoisted into package-global
`PreLoadStatements`/`PreLoadFileActions`, losing its position, its container *and* its guard.

**File:** `Tools/SsisExtractor/src/Ssis.Extract.Codegen/PackagePlanner.cs`

- Add `IReadOnlyList<string> ContainerPath { get; init; } = []` to the abstract `PackageStep`
  (line ~331), alongside the existing `Guard`/`FlowGroup` init-properties. Stamp it in the existing
  `Gated(refId, step)` local — that is already the single choke point every `steps.Add` passes
  through. Thread the current path down the `STOCK:SEQUENCE` recursion.
- **Delete the pre-load hoisting entirely.** A pre-flow Execute SQL Task becomes an ordinary
  `SqlStep`, a pre-flow File System Task an ordinary `FileSystemStep`, both at their true
  topological position inside their container. `PackagePlan.PreLoadStatements` and
  `PreLoadFileActions` are removed.
- Consequently **delete `ReportHoistingInversion`** — the hazard it guards (a hoisted statement
  overtaking a Script Task that precedes it) cannot occur once nothing is hoisted. This also
  removes the main real cause behind `ReportUnappliedGuards`, which stays but should now fire only
  on genuinely unreachable guards.
- Add per-container **wave** output so the emitter can schedule: for each container, expose its
  `ControlFlowDagSpec.ParallelLevels` filtered to non-disabled, non-failure-handler refIds, as an
  ordered `IReadOnlyList<IReadOnlyList<PackageStep>>`. Longest-path layering already guarantees
  every node's predecessors sit in a strictly lower level, so wave-by-wave execution is a valid
  schedule.
- **`ReportParallelismFlattening` is deleted** — parallelism is no longer flattened, so the
  advisory is obsolete. Replace with a short informational line in `generate-report.md` naming
  which waves are concurrent and the per-branch-transaction consequence.

**Behaviour check:** for `LoadEmployees`, `LoadReferenceData` and `Package_Transforms`, un-hoisting
changes nothing observable — their pre-load SQL is already topologically first, and it still runs
inside the same transaction.

## 2. New `PackageClassEmitter`, slimmed `ProgramEmitter`

**New file:** `Tools/SsisExtractor/src/Ssis.Extract.Codegen/PackageClassEmitter.cs`
**Rewrite:** `ProgramEmitter.cs` (1218 lines) shrinks to the ~20-line bootstrap above.

`ProgramEmitter`'s existing DTOs (`ProgramFlowSpec`, all ten `ProgramStep` subtypes, the
`FlowSourceSpec`/`FlowSinkSpec` hierarchies) are **kept as-is** — they already carry everything
needed; only the rendering changes. `PackageGenerator.cs`'s assembly of them is largely untouched.

Key mechanical work, because the current emitter is a flat `List<string>` with `"    "` hard-coded
in ~40 places and only `EmitSourceConstruction` takes an `indent` parameter:

- Introduce a tiny `CodeWriter` (indent level + `Line()`/`Open()`/`Close()`) and port every emission
  site to it. This is the bulk of the diff and is mechanical.
- Reuse `EmitSourceConstruction`'s existing per-`FlowSourceSpec` bodies verbatim — they move from
  inside a DI lambda into a named method body. `MergeJoinFlowSource`/`UnionFlowSource`/
  `AggregateFlowSource` already emit local functions (`BuildLeft()`, `BuildSide0()`,
  `BuildAggregateSource()`); those become sibling private methods named after the real components.
- Add the `default:` cases both source switches currently lack — today an unhandled
  `FlowSourceSpec` silently emits an empty body (a compile error in generated code, not a gap).

Class/file naming: `{PackageName}Package` in `{RootNamespace}`, file `{PackageName}.cs` — matching
the existing `{PackageName}DbContext` convention. `ILogger<Program>` becomes
`ILogger<{PackageName}Package>`.

Lookup preloads currently `await` at top level before `builder.Build()`; they move into `RunAsync`
ahead of the first wave, reading `DatabaseOptions` from `services`. Also add the missing
de-duplication — two flows sharing one Lookup cache currently emit two identical locals (CS0128).

## 3. Component methods ordered exactly like the walkthrough

`Svk.Core.PipelineFlowOrder.Compute(PipelineSpec)` already produces the walkthrough's per-component
`Seq` numbering (1-based longest-path layering over `PipelineSpec.Paths`, ties broken by name).
`WalkthroughBuilder` and `DataFlowDiagramBuilder` both read it, which is why a diagram node number
and a table row can never drift.

**Move `PipelineFlowOrder.cs` (and `ComponentFlowEdge`/`PipelineFlow`) from
`Tools/SsisValidationKit/src/SvkCore/` to `Tools/SsisExtractor/src/Ssis.Extract.Dtsx/`.** Both
projects already reference `Ssis.Extract.Dtsx`, so `Svk.Core` keeps working with an updated `using`
and the emitter gains the identical ordering. Optionally also move `ComponentInsight.cs` (155 lines)
so the emitted `/// <summary>` text is literally the walkthrough's own "Detail" column — recommended,
same reuse argument.

`PackageClassEmitter` then emits component methods in `PipelineFlow.OrderedComponentRefIds` order,
prefixing each doc comment with its `LevelByRefId` number, so walkthrough row "Seq 2 |
OLECMD_SetFlag" and the code comment `/// 2. OLE DB Command 'OLECMD_SetFlag'` are the same number by
construction. Components folded into a transform (Derived Column, Data Conversion, Union All, Sort,
Row Count) get no method of their own — they are named in the owning method's doc comment, which is
honest and still traceable.

## 4. Concurrency and transaction segmentation

**The rule.** Walk the root container's waves in order:
- Consecutive waves of width 1 accumulate into **one segment** = one transaction, sequential awaits.
- A wave of width > 1 is its own segment: **each branch gets its own `IUnitOfWork`/transaction** and
  they run under `Task.WhenAll`.

Consequences, all deliberate:
- A package with no parallelism (`LoadEmployees`, `LoadReferenceData`, `Package_Transforms`) yields
  exactly **one** transaction — byte-equivalent behaviour to today, so every existing verified
  end-to-end result stands.
- `Package_Advanced` yields segment 1 (`SEQ_Prepare`, one transaction, committed) then segment 2
  (three concurrent transactions).
- **The segment boundary must commit before a concurrent wave starts.** Otherwise
  `SQL_TruncateTargets`' uncommitted `TRUNCATE` holds a Sch-M lock that blocks the concurrent
  branches' own independent connections — the exact hazard `sp_bindsession` exists for, and one that
  cannot be bridged across independent transactions.
- **Rollback guarantee changes only where SSIS was concurrent.** If one branch fails after a sibling
  committed, the sibling is not undone. This is what SSIS itself does. State it in the generated
  class doc comment, in `generate-report.md`, and in `HOW-TO-FILL-GAPS.md`.

**Per-branch `IUnitOfWork` needs no new Etl.Core abstraction.** `AddEtlDbContext<T>` and
`AddScoped<IUnitOfWork, UnitOfWork>()` are both scoped, so `services.CreateAsyncScope()` already
yields a fresh `DbContext` → fresh `SqlConnection` → independent transaction. The emitted helper:

```csharp
private async Task InOwnTransactionAsync(Func<IUnitOfWork, CancellationToken, Task> body, CancellationToken ct)
{
    await using var scope = services.CreateAsyncScope();
    var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
    await uow.BeginAsync(IsolationLevel.ReadCommitted, ct);
    // Fetched while this transaction's connection is guaranteed idle -- a lazy fetch from inside a
    // step reproduced a real deadlock (SqlRowSource reading while SqlBulkCopy streams).
    await uow.GetBindTokenAsync(ct);
    try { await body(uow, ct); await uow.CommitAsync(ct); }
    catch { await uow.RollbackAsync(ct); throw; }
}
```

`IUnitOfWork uow` is therefore an explicit parameter on every task method — which is also what makes
each one independently unit-testable with a fake.

**Etl.Core changes required before any concurrency lands** (`Tools/Etl.Core/`, mirrored to
`D:\PoC\SSIS_Rewrite\src\Etl.Core\`):
- `Abstractions/PackageVariables.cs` — plain `Dictionary` → `ConcurrentDictionary`. Its own doc
  comment already flags this as the precondition for concurrency.
- `Data/CountingRowSource.cs` writes into that bag on completion; covered by the above.
- Nothing else: `SqlRowSource`, `CsvRowSource`, `FixedWidthRowSource`, `ExcelRowSource`,
  `FlatFileBulkSink`, `SecondaryConnectionSqlStep`, `FileSystemActionRunner` and
  `EntityTableMap` (already a `ConcurrentDictionary`) are all connection-independent or
  already thread-safe.
- `StepResult` accumulation across concurrent branches: collect into a `ConcurrentBag`, order by
  wave for the final report.

Failure handling moves into `RunAsync`: catch, then run each `FailureHandlerPlan` via a **fresh
scope**'s `ExecuteSqlWithoutTransactionAsync` (it throws while a transaction is active, so it needs a
UoW that has none). The existing dual-position `…Reached` flag logic ports unchanged.

## 5. File paths come from `appsettings.json`, not string literals

`FileSystemActionPlan.SourcePath`/`DestinationPath` and `ForEachFileLoopPlan.Folder` are emitted as
absolute literals today (`"D:\\OneDrive\\OneDrive - Iris Software Inc\\…"` appears three times in
`Package_Advanced`'s `Program.cs`), which makes the generated project unrunnable anywhere else.

- `PackagePlanner.ResolveFileSystemAction` already resolves a FILE connection manager to its
  `ConnectionString`; carry the **connection-manager name** through on `FileSystemActionPlan`
  alongside the resolved path.
- `PackageGenerator` registers a `FileSourceEntryRequest` per FILE/ForEach connection manager, keyed
  by CM name, splitting the literal into `SourceFolder` + `SourceFileName` the same way flat-file
  sources already do.
- Emit `File("CM_FILE_SourceWorkbook")` (→ `_files["CM_FILE_SourceWorkbook"].ResolvedPath`).
- Note: the Excel Source (`CM_EXCEL_Drip`, keyed `EXCEL_SRC_Drip`) and the File System Task's source
  (`CM_FILE_SourceWorkbook`) are two different connection managers that happen to name the same
  file. They get two entries, exactly as the `.dtsx` has two connection managers. Do not collapse
  them.
- A ForEach enumerator's `Folder` becomes a folder-only entry.

## 6. Emitted private helpers (bottom of the generated class)

```csharp
private ILogger<T> Log<T>() => services.GetRequiredService<ILogger<T>>();
private IOptions<T> Opt<T>() where T : class => services.GetRequiredService<IOptions<T>>();
private DatabaseOptions Db() => Opt<DatabaseOptions>().Value;
private string File(string key) => Opt<FileSourceOptions>().Value[key].ResolvedPath;
private async Task Step(ILoadTask task, IUnitOfWork uow, CancellationToken ct) =>
    _results.Add(await task.RunAsync(uow, _load, ct));
private async Task InOwnTransactionAsync(...)  // see §4
```

Guards (conditional precedence constraints) keep today's emitted shape, moved inside the caller
method:

```csharp
// Guard: @[User::RowsLoaded] > 0
if (_variables.GetRequired<int>("User::RowsLoaded") > 0) await DFT_BuildSalesSummary(uow, ct);
else { logger…; _results.Add(new StepResult("DFT_BuildSalesSummary", 0, 0, TimeSpan.Zero) { Skipped = true }); }
```

## 7. Task-level seams

Extend the existing `--seams` / `ssisx apply-fills` mechanism from columns and Script Tasks to whole
tasks, **without widening the Tier taxonomy**:

- **Tier 2 (`GapKind` = `MissingLogic`)** — the logic is in the `.dtsx` and a human can port it
  (Script Task today; any future per-task port). Emits
  `internal partial Task {TaskName}(IUnitOfWork uow, CancellationToken ct);` plus the call site, so
  an unfilled seam is **CS8795** at build time. The class is already `partial`, and `internal`
  (not `private`) so a filled seam is testable like every other task — see §8.
- **Tier 3 (`MissingToolSupport`)** — stays a blocking gap with no work packet, exactly as today.
  A per-package hand-patch here would hide one systemic emitter gap behind N one-off fixes, which is
  the whole reason the taxonomy exists.
- `ApplyFillsCommand`'s seam regex must accept the new signature (it already allows an optional
  `async`; verify it matches a two-parameter `Task` method).
- Fills are keyed by **class + method name**, matching the existing Script Task rule (every Script
  Task seam is `RunScriptAsync`, so method name alone is ambiguous).

## 8. Every component is the smallest testable unit — and gets a generated test

This is the acceptance bar, not a nice-to-have. Three things make it real:

**8a. Visibility.** Every task and component method is `internal`, never `private`; only the ~6
plumbing helpers in §6 stay `private`. The generated project emits
`[assembly: InternalsVisibleTo("{Package}.Tests")]`, so the sibling test project
`TestProjectEmitter` already produces can call any of them directly. Nothing becomes `public` — the
surface stays closed to everything except its own tests.

**8b. Each method's return type is chosen so it can be asserted on in isolation.** That is why a
source method returns `IRowSource<TRow>` rather than being `void`/`async`:

```csharp
// Read the real configured worksheet, with no database and no pipeline involved.
[Fact]
public async Task EXCEL_SRC_Drip_ReadsTheConfiguredWorksheet()
{
    var rows = new List<DripEligibilityExcelRow>();
    await foreach (var row in Package().EXCEL_SRC_Drip().ReadAsync(default)) rows.Add(row);

    Assert.Equal(9, rows.Count);
    Assert.Equal(1001d, rows[0].CustomerID);
}

// The whole flow, against a fake unit of work -- no server needed.
[Fact]
public async Task DFT_ExcelImport_MapsEveryRowToTheDestinationEntity()
{
    var uow = new FakeUnitOfWork();
    await Package().DFT_ExcelImport(uow, default);
    Assert.Equal(9, uow.BulkInsertCalls.Single().Rows.Count);
}

// One Execute SQL Task, in isolation, statement text asserted without running it.
[Fact]
public void SQL_TruncateTargets_EmitsTheStatementTheDtsxDeclared() =>
    Assert.Equal("TRUNCATE TABLE dbo.DripEligibility; UPDATE dbo.CustomerFlag SET FlagValue='unset';",
                 SQL_TruncateTargetsStatement.BuildStatement());
```

**8c. `TestProjectEmitter` generates a starter test per component**, not just per transform as
today. Extend it with one `[Fact]` per emitted component method, following the shape and the
`// <auto-generated>` "extend by hand, regenerating discards edits" header the existing
`TransformTestEmitter` already uses:

| Component kind | Generated starter test | Needs a server? |
|---|---|---|
| Source (Csv / FixedWidth / Excel / Sql / MergeJoin / Union / Aggregate) | enumerate `ReadAsync`, assert row count + first row's columns | file sources no; SQL sources yes — emitted `[Trait("Category","Integration")]` |
| Destination (Sql / FlatFile) | write a synthesized row through a `FakeUnitOfWork`, assert the mapped columns | no |
| Router (Conditional Split) | one case per branch + the default, asserting the branch index | no |
| Execute SQL / ForEach body | assert the `{Task}Statement.BuildStatement()` text (and, for ForEach, per-file substitution) | no |
| Data Flow / container / package | compose the above against a `FakeUnitOfWork`, assert rows in and rows out | no |
| Transform (Derived Column, Data Conversion, …) | **already generated today**, one assertion per output column — finer than per-component | no |

Integration-flavoured tests are tagged so `dotnet test --filter Category!=Integration` runs the
whole suite with no database, and the full suite runs it end to end against a real one.

**8d. Components folded into a transform stay traceable.** Derived Column, Data Conversion, Union
All, Sort and Row Count do not become methods on the package class — they are already compiled into
the transform's per-column `Compute*` statics, which is a *finer* granularity than per-component and
already has generated assertions. Add a banner comment above each group naming the SSIS component
that produced those columns, so the walkthrough row still lands on a specific block of code:

```csharp
// --- DCONV_Types (Data Conversion) -- 2 conversion(s) ---
public static int? ConvertCustomerID_i4(...) => SsisFn.ToNullableI4(row.CustomerID);
// --- DER_Enrich (Derived Column) ---
public static string ComputeFullName(...) => ...
```

## Files touched

| File | Change |
|---|---|
| `src/Ssis.Extract.Codegen/PackagePlanner.cs` | `ContainerPath`; drop hoisting + `ReportHoistingInversion` + `ReportParallelismFlattening`; expose waves |
| `src/Ssis.Extract.Codegen/ProgramEmitter.cs` | shrink to bootstrap; keep all DTOs |
| `src/Ssis.Extract.Codegen/PackageClassEmitter.cs` | **new** — the whole class body |
| `src/Ssis.Extract.Codegen/CodeWriter.cs` | **new** — indent-aware emission |
| `src/Ssis.Extract.Codegen/PackageGenerator.cs` | drop `PreLoadStatements` reshaping; register FILE/ForEach `FileSourceEntryRequest`s; pass waves |
| `src/Ssis.Extract.Codegen/ScriptTaskEmitter.cs`, `AiPacketEmitter.cs` | task-seam signature + packet contract text |
| `src/Ssis.Extract.Codegen/TestProjectEmitter.cs` | **§8c** — a starter `[Fact]` per component method, `Category=Integration` traits |
| `src/Ssis.Extract.Codegen/TransformTestEmitter.cs` | **§8d** — component banner comments over the `Compute*` groups |
| `src/Ssis.Extract.Codegen/ProjectEmitter.cs` | `InternalsVisibleTo("{Package}.Tests")`; FILE/ForEach `FileSource` entries in `appsettings.json` |
| `src/Ssis.Extract.Cli/ApplyFillsCommand.cs` | seam regex must match the new two-parameter task signature |
| `src/Ssis.Extract.Dtsx/PipelineFlowOrder.cs` (+ `ComponentInsight.cs`) | **moved** from `SvkCore` |
| `Tools/SsisValidationKit/src/SvkCore/*` | update `using`s after the move |
| `Tools/Etl.Core/Abstractions/PackageVariables.cs` | `ConcurrentDictionary` |
| `Tools/README.md`, `COPILOT_GUIDE.md`, `HOW-TO-FILL-GAPS.md` | new output shape + rollback semantics |

## Phasing

Each phase ends green (build + suites) before the next starts, so the change is bisectable.

| # | Phase | Ends when |
|---|---|---|
| 0 | Save this plan to `D:\PoC\SSIS\Docs\Emitter-Rewrite-Plan.md` | file exists |
| 1 | `CodeWriter`; move `PipelineFlowOrder` (+`ComponentInsight`) to `Ssis.Extract.Dtsx`, repoint `SvkCore` | both solutions build; `svk walkthrough` output byte-identical |
| 2 | Planner: `ContainerPath`, drop hoisting, expose waves, delete the two obsolete report passes | planner tests green; gap counts explained |
| 3 | `PackageClassEmitter` + slim `Program.cs`, **sequential only** (one transaction, as today) | whole portfolio builds 0/0; live runs match §Verification 5-6 exactly |
| 4 | Per-component methods, `internal`, §8a-8b/8d | walkthrough correspondence check (§Verification 4) passes |
| 5 | `TestProjectEmitter` per-component tests (§8c) | §Verification 3 passes |
| 6 | File paths from `appsettings.json` (§5) | no absolute literal remains in any generated `.cs` |
| 7 | Concurrency: `PackageVariables` → `ConcurrentDictionary`, wave segmentation, `Task.WhenAll` (§4) | §Verification 7-8 pass |
| 8 | Task-level seams (§7) + doc updates | §Verification 9 passes |

Phases 1-6 change no runtime behaviour at all; phase 7 is the only one that does.

## Verification

Run in this order; do not skip the live runs — every past emitter round that trusted a green gap
report shipped a real bug (`DFT_SortAndMergeJoin`'s silent multi-source, the Script Component
passthrough, the `Package_Legacy` disabled-task INSERT).

1. **Unit suites.** `Tools/SsisExtractor/scripts/Run-Tests.ps1`. Expect ~15 test files asserting
   `Program.cs` content to need rewriting (`ProgramEmitterTests`, `PackageGeneratorTests`,
   `ConditionalConstraintTests`, `LookupFlowTests`, `ParallelismAdvisoryTests` — that last one is
   deleted with the advisory). Two golden `Program.cs.txt` files regenerate; review the diff by eye
   rather than blessing it.
2. **Whole-portfolio build.** Regenerate all 5 `SSIS_From_Sandeep` packages plus this PoC's own 4
   into a **fresh** output directory (never `D:\PoC\Tools\out`, which the user inspects by hand),
   with `--seams --etl-core Tools/Etl.Core`, apply the existing `fills-library/Package` fills, and
   build `Generated.slnx`: **0 warnings / 0 errors**.
3. **The generated per-component tests actually run and pass** (§8, the acceptance bar). For
   `Package_Advanced` and `Package_Transforms`, build the emitted `{Package}.Tests` project and run
   `dotnet test --filter Category!=Integration` with **no database reachable** — every non-integration
   test must pass. Then run the full suite against the live container. Confirm there is at least one
   test per component method by diffing the test method names against the walkthrough's component
   table; a component with no test is a bug in `TestProjectEmitter`, not an acceptable gap.
4. **Walkthrough correspondence, checked by hand.** Open
   `Out/walkthrough/walkthrough/Package_Advanced.walkthrough.md` beside the generated
   `Package_Advanced.cs` and confirm every executable and every per-component table row has a
   correspondingly-named and -numbered method. Repeat for `Package_Transforms` (the dense one:
   Conditional Split, Data Conversion, Lookup+Multicast+Aggregate, Sort+Merge Join, Union remerge).
5. **Live no-regression run, sequential packages.** `LoadEmployees` + `LoadReferenceData` against
   `.\SQLFORPOC_2022`: expect **12 / 8 / 10** rows, and confirm a single transaction (one
   `BeginAsync`) in the log.
6. **Live run, the concurrent package.** Bring up `ssisdemo-sqlserver` (docker-compose in
   `SSIS_From_Sandeep`), run real `dtexec` for `Package_Advanced` and `Package_Transforms`, then the
   generated `.exe` against the same freshly-seeded database, and **diff the target tables
   row-for-row** — `DripEligibility`, `CustomerFlag`, `EtlRunLog`, `CustomerEnriched`,
   `RegionSummary`, `CustomerWithContact`, `CustomerMerged`. Read the tables back; do not trust exit
   codes.
7. **Prove the concurrency is real, not cosmetic.** In the `Package_Advanced` run, assert from the
   log that the three branches' start timestamps interleave, and that three separate `BeginAsync`
   calls occur.
8. **Prove the failure semantics.** Force one concurrent branch to fail (point the Excel source at a
   missing file) and confirm: that branch rolls back, the siblings' committed work stands, the
   `OnError` handler row lands in `EtlRunLog`, and the process exits 1.
9. **Prove the seam still bites.** Regenerate `Package.dtsx` with `--seams` and no fills: expect
   exactly one CS8795 per unfilled seam; then `apply-fills` and rebuild to 0/0.

## Risks

- **Largest single change to the generator so far.** `ProgramEmitter` is rewritten and `PackagePlanner`
  loses two report passes. Mitigation: the plan DTOs and every `Generate*Flow` helper in
  `PackageGenerator` are untouched, so the *content* of what is emitted is unchanged — only its
  arrangement.
- **Per-branch transactions weaken atomicity** for concurrent packages. Accepted deliberately (it is
  what SSIS does); must be stated in generated doc comments and in `generate-report.md`, never
  implicit.
- **`ConcurrentDictionary` for `PackageVariables` is necessary but not sufficient** for a Script Task
  that reads-modifies-writes a variable across concurrent branches. No tracked package does this; if
  one appears, it needs a gap, not a lock.
- **Un-hoisting changes emitted order** for any package whose pre-flow SQL was not already first.
  None in the tracked corpus — confirm during step 2 by diffing generated SQL call order against the
  previous output.
