# OLE DB Destination error-row redirect + destination-identity traceability

## Context

Reviewing the generated rewrite of `Package.dtsx` (`SSIS_From_Sandeep`), `DFT_LoadCustomers`'s
own error-redirect destination (`OLEDST_StagingErrors`, fed by `OLEDST_StagingCustomers`'s error
output, `errorRowDisposition="RedirectRow"`) was silently dropped: no generated file, no gap, no
mention anywhere in `gaps.json`/`generate-report.md`. Root-caused to `PackagePlanner.PlanDataFlow`
(`Tools/SsisExtractor/src/Ssis.Extract.Codegen/PackagePlanner.cs:1926`), which resolves "the"
destination via a bare `FirstOrDefault(c => c.ComponentClassId == "Microsoft.OLEDBDestination")`
with no count check and no awareness that a destination's input can be fed by an *error* output
rather than the main pipeline. `ErrorRowDisposition` is already extracted into the model
(`PipelineInputSpec.ErrorRowDisposition`) but has zero consumers anywhere in `Ssis.Extract.Codegen`
— confirmed by grep. This is the same bug *class* the 2026-09-06 "third independent review" round
fixed for extra *source* components; no equivalent check was ever added for a second *destination*
reached via error redirect. It is the only instance of this shape anywhere in the tracked
portfolio.

The user does not want a "detect and gap" patch — they want the redirect to actually work end to
end, and separately asked why the generated code doesn't already give each SSIS pipeline component
its own clearly-named, traceable unit (their example: `FF_SRC_Customers() -> SCR_CleanseCustomerRow()
-> OLEDST_StagingCustomers() -> OLEDST_StagingErrors()`).

**Investigating the second question found a real, separate, confirmed defect worth fixing
alongside this**: `Tools/SsisExtractor/src/Ssis.Extract.Dtsx/PipelineFlowOrder.cs`'s own doc
comment claims that, "since the emitter rewrite," `PackageClassEmitter`'s per-component method
ordering already reads `PipelineFlowOrder`/`ComponentInsight` — the same functions that back the
`svk` walkthrough and the generated `README.md`'s "Data flow detail" table — so that "a walkthrough
Seq, a README Seq, and a generated method's doc-comment number can never drift apart." Grepping
`PackageClassEmitter.cs` for either name: **zero references.** Those two functions are used
*only* by `PackageReadmeEmitter.cs` (confirmed the only other consumer anywhere in `src/`). So the
claim is stale/false for the actual generated C# — `Package.cs`'s own per-component naming is
separate, ad hoc, and inconsistent: a *source* method is already named after its own SSIS component
(`FF_SRC_Customers()`, via `source.ComponentName` threaded through `FlowSourceSpec`), but a *sink*
method is named after the destination **entity**, never the component — confirmed via
`PackageClassEmitter.EmitSinkMethod`'s own doc comment: "A destination component carries no name of
its own anywhere in the plan... never threaded this far." That's the direct, confirmed answer to
"how did we miss it": destination identity was deliberately dropped from the plan back when there
was always exactly one destination per flow, and nobody revisited it. It's also *why* a second
destination was so easy to silently lose — the data structures never had room to name more than one.

The user chose (via AskUserQuestion): **row-by-row write for a redirect-configured destination**
(not bulk-copy-then-savepoint-retry), and **fix destination naming only where more than one
destination exists** in a flow (not a blanket doc-comment/Seq-number pass across every existing
fixture). ErrorCode/ErrorColumn fidelity: **best-effort, stated as approximate** — SSIS's own
`ErrorCode` is a native OLE DB HRESULT; the generated code writes via `Microsoft.Data.SqlClient`, a
different driver stack, so an exact HRESULT match isn't achievable. `ErrorCode` will carry the real
`SqlException.Number` (a genuinely meaningful SQL Server error number); `ErrorColumn` is left `0`
with a comment stating it's unresolved (parsing SQL Server's own free-text error messages for a
column name across versions was judged disproportionate for a shape evidenced exactly once in the
whole real portfolio — flagged as an easy, separately-scoped future enhancement, not silently
dropped).

## What's already confirmed (via two Explore agents + direct reads — not assumptions)

- `IBulkSink<TEntity>.WriteAsync(IUnitOfWork, IAsyncEnumerable<TEntity>, CancellationToken) -> Task<long>`
  — one method, no per-row failure signal. `SqlBulkSink<TEntity>` has zero try/catch anywhere;
  any `SqlBulkCopy` failure aborts the whole batch today.
- `EntityTableMap.For(context, entityType).Columns` (`Etl.Core/Data/EntityTableMap.cs`) already
  exposes exactly what a row-by-row insert needs, per column: `PropertyName`, `ColumnName`,
  `ProviderType`, `MaxLength`, and a compiled `Func<object, object?> GetValue` (applies any EF value
  converter). This is the *same* metadata `SqlBulkSink`/`ObjectDataReader<TEntity>` already use —
  no new reflection needed.
- `IUnitOfWork.ExecuteSqlAsync(string sql, object?[] parameters, CancellationToken ct)` already
  exists (used elsewhere for `OleDbCommandStep`) and takes EF's `{0}`/`{1}` placeholder convention
  — sufficient for a parameterized per-row `INSERT`. No new `IUnitOfWork` member is needed.
- The real primary destination entity (`StagingCustomers`) is **`HasNoKey()`** in the generated
  `DbContext` (`CustomerID` is a string, so `PrimaryKeyInference`'s integer-only rule rejects it,
  and no EF bare-`Id` convention matches either) — confirmed by reading the generated
  `PackageDbContext.cs` directly. This *rules out* an EF-tracked `Context.Set<T>().Add()` +
  `SaveChangesAsync()` approach for the row-by-row insert (EF Core cannot track/insert a keyless
  entity type) and confirms the raw-parameterized-SQL approach (bypassing EF's change tracker
  entirely, exactly like `SqlBulkSink` already bypasses it) is the only generally-correct design —
  not just the simpler one.
- Resolving "what does this destination's error output connect to" needs **zero new
  extraction/model work**: `PipelineOutputSpec.IsErrorOut`, `PipelinePathSpec`, and
  `PipelineComponentSpec.Inputs/Outputs` already model an error-output path exactly like any other
  path (confirmed: `PipelineReader.Read` reads every `<path>` unconditionally, no special-casing).
  The exact resolution pattern already exists in `PackagePlanner.ResolveBranch`
  (`pipeline.Paths.FirstOrDefault(p => p.StartId == ...)` → `pipeline.Components.FirstOrDefault(c
  => c.Inputs.Any(i => i.RefId == path.EndId))`) — just never called starting from an *error*
  output (every existing `IsErrorOut` usage in Codegen only *excludes* it when picking the main
  output).
- The real `OLEDST_StagingErrors` component's own mapped `<inputColumn>` list already includes
  `CustomerID`/`FirstName`/`LastName`/`Email` (lineage-traced to the raw source, same as the
  primary destination's own passthrough columns) **and** `ErrorCode`/`ErrorColumn` (lineage-traced
  to `OLEDST_StagingCustomers`'s own error output) — confirmed by reading the raw XML. Only
  `FailedAt` (present in the real target table's external metadata, never mapped as an
  `<inputColumn>` at all) has no pipeline-derived value. `ErrorRowID` (the real table's own
  identity PK) is likewise never mapped — correctly invisible to `EntityEmitter`/
  `PrimaryKeyInference` already, no change needed there.
- Because a row that fails to insert at the primary destination was **never inserted**, SSIS's own
  error-output buffer for that row is identical to the primary destination's own input buffer for
  that same row. So an error-redirect column that traces back to the raw source is, in every case,
  also just an ordinary property already present on the *failed* primary entity instance
  (`StagingCustomers`) — meaning the error row can be built by copying matching-named properties
  off the failed entity, **not** by re-deriving lineage back through the transform. This
  eliminates the need to touch `TransformEmitter`'s lineage machinery at all.
- `Ssis.Extract.FixtureBuilder` has never built this shape before (`RD_RedirectRow` — confirmed via
  grep — appears nowhere in `Program.cs`); `IDTSInput100.ErrorRowDisposition`/
  `.TruncationRowDisposition` are ordinary settable `DTSRowDisposition` enum properties (already
  documented in that project's own doc comments: `RD_RedirectRow=2`), and `AttachPath` (existing
  helper) is sufficient to wire a destination's own error output to a second destination's input.

## Design

### 1. Etl.Core (`D:\PoC\SSIS_Rewrite\src\Etl.Core`, mirrored after to `Tools\Etl.Core`)

New `Data/RedirectingSqlSink.cs`:

```csharp
public sealed class RedirectingSqlSink<TEntity, TErrorEntity>(
    IBulkSink<TErrorEntity> errorSink,
    Func<TEntity, SqlException, TErrorEntity> mapToError,
    ILogger<RedirectingSqlSink<TEntity, TErrorEntity>> logger)
    : IBulkSink<TEntity>
    where TEntity : class
    where TErrorEntity : class
{
    public async Task<long> WriteAsync(IUnitOfWork uow, IAsyncEnumerable<TEntity> rows, CancellationToken ct)
    {
        var map = EntityTableMap.For(uow.Context, typeof(TEntity));
        var insertSql = BuildInsertSql(map); // "INSERT INTO [schema].[table] (...) VALUES ({0}, {1}, ...)"

        long written = 0;
        List<TErrorEntity>? errorRows = null;

        await foreach (var entity in rows.WithCancellation(ct))
        {
            var values = map.Columns.Select(c => c.GetValue(entity)).ToArray();
            try
            {
                await uow.ExecuteSqlAsync(insertSql, values, ct);
                written++;
            }
            catch (SqlException ex)
            {
                logger.LogWarning(ex, "{Table}: row redirected on insert failure ({Message})", map.Table, ex.Message);
                (errorRows ??= []).Add(mapToError(entity, ex));
            }
        }

        if (errorRows is { Count: > 0 })
        {
            var redirected = await errorSink.WriteAsync(uow, ToAsyncEnumerable(errorRows), ct);
            logger.LogInformation("{Table}: {Count:N0} row(s) redirected", map.Table, redirected);
        }

        logger.LogInformation("{Table}: row-by-row insert complete, {Rows:N0} row(s), {Errors:N0} redirected",
            map.Table, written, errorRows?.Count ?? 0);
        return written;
    }
}
```

Deliberate design points, stated so they can be reviewed, not just discovered later:
- **Row-by-row for the whole destination, always, once redirect is configured** — not a
  bulk-copy-fast-path-then-retry hybrid (the option not chosen). Trades `SqlBulkCopy` speed for
  *this one destination only*; every other destination in the package is completely unaffected.
  Matches this project's own precedent (Conditional Split's buffer-then-flush note: "trades true
  end-to-end streaming for correctness").
- **Bypasses EF's change tracker entirely** (raw parameterized SQL via `IUnitOfWork.ExecuteSqlAsync`,
  not `Context.Set<T>().Add()`) — required, not optional, since the real primary entity is
  `HasNoKey()` and EF Core cannot track/insert a keyless entity type at all.
- **If the error-table insert itself throws, that exception propagates and aborts the run** —
  matching SSIS's own behavior when a row can't even be redirected. Not treated as a second level
  of redirect (unevidenced, and would need a second error table with no obvious target).
- `mapToError` is a plain delegate the GENERATED code supplies — `RedirectingSqlSink` stays entity-
  agnostic, matching this project's established separation (generic runtime, package-specific glue
  in generated code).

### 2. `Ssis.Extract.Codegen` — planning

`PackagePlanner.cs`, in `PlanDataFlow`'s plain single-source/single-destination path (the
`conditionalSplit is null && multicast is null` shape — this round is scoped to exactly that shape;
a Conditional Split/Multicast branch that *also* has an error-redirect destination is unevidenced
and stays a named gap, not guessed at):

- New `ErrorRedirectPlan(PipelineComponentSpec ErrorDestinationComponent)` record.
- After resolving `destination` (existing code, unchanged): if `destination`'s own input has
  `ErrorRowDisposition == "RedirectRow"`, walk forward from
  `destination.Outputs.FirstOrDefault(o => o.IsErrorOut == true)` via `pipeline.Paths`/
  `pipeline.Components` (the exact `ResolveBranch`-style two-step lookup already confirmed above)
  to find the error destination. Require it be a supported destination type (OLE DB/ADO NET/Flat
  File) — anything else is a named gap, not a guess.
- **General defensive fix, closing the bug *class* not just this shape**: after resolving
  `destination` and (if present) the error-redirect destination, collect every remaining
  `Microsoft.OLEDBDestination`/`AdoNetDestination`/`Microsoft.FlatFileDestination` component in the
  pipeline. Any that's neither `destination` nor the error-redirect destination is now a named,
  blocking gap ("this Data Flow Task has N destination components ... only M are accounted for")
  — mirroring the wording style of the existing multi-*source* gates
  (`PackagePlanner.cs:1859-1865`) exactly. This is what prevents a THIRD, unrelated, future
  destination shape from silently vanishing the same way this one did.
- `DataFlowPlan` gains `ErrorRedirectPlan? ErrorRedirect = null` (additive, every existing call
  site unaffected).

### 3. `Ssis.Extract.Codegen` — generation

- `EntityEmitter.Emit` gains one new optional parameter,
  `IReadOnlyList<(string Name, string ClrType)>? extraProperties = null` — appended as plain
  properties with no pipeline/nullability resolution at all. Used here only for
  `[("FailedAt", "DateTime")]` on the error entity. `DbContextEmitter.EmitOneTable` gets the same
  list threaded through (an extra property needs no explicit EF configuration beyond what
  convention already gives a plain `DateTime`, but the property must be *known about* so it's not
  silently absent from `OnModelCreating`'s property list if that method enumerates explicitly —
  verify against the real emitted shape before assuming convention alone is enough).
- `PackageGenerator`: when `flow.ErrorRedirect` is present —
  - `EntityEmitter.Emit(ns, errorEntityName, errorRedirect.ErrorDestinationComponent,
    extraProperties: [("FailedAt", "DateTime")])` — this alone already produces
    `CustomerID`/`FirstName`/`LastName`/`Email`/`ErrorCode`/`ErrorColumn`/`FailedAt` on the error
    entity, since `ErrorCode`/`ErrorColumn` are genuine mapped `<inputColumn>`s (confirmed above) —
    **no `TransformEmitter` change needed at all.**
  - Register the error entity's own `DbContextEmitter.TableSpec`.
  - Build the error-map: for every business column on the error entity (i.e. every resolved column
    except `ErrorCode`/`ErrorColumn`, identified structurally by their lineage terminating at the
    primary destination's own error output — not by name — and `FailedAt`, identified as an
    `extraProperties` entry), look for a same-named property on the primary entity's own already-
    resolved column set. A match emits `{Col} = failed.{Col}`; no match is a named gap ("error-
    redirect destination column 'X' has no matching column on the primary destination — cannot
    resolve its value"), never guessed.
  - Emit a small dedicated file, `Mapping/{PrimaryEntity}ErrorMap.cs`:
    ```csharp
    internal static class StagingCustomersErrorMap
    {
        internal static StagingCustomersError Map(StagingCustomers failed, SqlException ex) => new()
        {
            CustomerID = failed.CustomerID,
            FirstName = failed.FirstName,
            LastName = failed.LastName,
            Email = failed.Email,
            ErrorCode = ex.Number,
            ErrorColumn = 0, // not resolved -- see ex.Message in the log for the real SQL Server error text
            FailedAt = DateTime.UtcNow,
        };
    }
    ```
  - Thread a new `RedirectingSqlFlowSink(string PrimaryComponentName, string ErrorComponentName,
    string ErrorEntityName, string ErrorMapClassName) : FlowSinkSpec` onto `ProgramFlowSpec`
    (parallel to the existing `SqlFlowSink`/`FlatFileFlowSink`).

### 4. `PackageClassEmitter.cs` — the naming fix, scoped exactly as chosen

`EmitSinkMethod` currently names every sink `{entityName}Destination`. When
`flow.Sink is RedirectingSqlFlowSink redirect` (i.e. only when more than one destination exists in
the flow — every ordinary single-destination flow is completely untouched, so none of the existing
golden files change), name both methods after their own SSIS component identity instead — mirroring
`EmitSourceMethod`'s existing `Reserve(PackageGenerator.SanitizeIdentifier(source.ComponentName))`
pattern exactly:

```csharp
internal async Task DFT_LoadCustomers(IUnitOfWork uow, CancellationToken ct)
{
    var source = FF_SRC_Customers();
    var transform = new StagingCustomersTransform();
    var sink = OLEDST_StagingCustomers();
    var step = new DataFlowStep<StagingCustomersCsvRow, StagingCustomers>("DFT_LoadCustomers", source, transform, sink, Log<...>());
    await Step(step, uow, ct);
}

/// <summary>Pipeline component 'OLEDST_StagingCustomers' (Microsoft.OLEDBDestination). Its own SSIS
/// input is configured ErrorRowDisposition=RedirectRow, so a per-row insert failure here does not
/// abort the load -- the row is mapped and sent to OLEDST_StagingErrors() instead.</summary>
internal IBulkSink<StagingCustomers> OLEDST_StagingCustomers()
{
    return new RedirectingSqlSink<StagingCustomers, StagingCustomersError>(
        OLEDST_StagingErrors(), StagingCustomersErrorMap.Map,
        Log<RedirectingSqlSink<StagingCustomers, StagingCustomersError>>());
}

/// <summary>Pipeline component 'OLEDST_StagingErrors' (Microsoft.OLEDBDestination). Receives rows
/// redirected from OLEDST_StagingCustomers's own error output.</summary>
internal IBulkSink<StagingCustomersError> OLEDST_StagingErrors()
{
    return new SqlBulkSink<StagingCustomersError>(Opt<BulkCopyOptions>(), Log<SqlBulkSink<StagingCustomersError>>());
}
```

This gives exactly the call chain asked for: `FF_SRC_Customers() -> [StagingCustomersTransform] ->
OLEDST_StagingCustomers() -> OLEDST_StagingErrors()`. `SCR_CleanseCustomerRow` deliberately stays
folded into `StagingCustomersTransform` — matching this project's own established, stated design
("a component folded into a transform... gets no method of its own — named in the owning method's
doc comment instead") — its four Script-Component-produced columns already each carry a doc comment
naming it (`Fill_FullName`'s: "produced by Script Component 'SCR_CleanseCustomerRow'"). This is not
touched, per the naming-scope answer chosen (fix it only where more than one *destination* exists).

### Not built this round, named rather than silently skipped
- `PipelineFlowOrder.cs`'s stale doc comment (claims `PackageClassEmitter` already uses it) — will
  be corrected to state the truth (only `PackageReadmeEmitter` consumes it today), not expanded
  into a full Seq/description doc-comment pass across every generated method — per the naming-scope
  answer chosen.
- Conditional Split/Multicast branch + error-redirect combined — unevidenced, stays a named gap if
  ever encountered.
- A more faithful `ErrorColumn` (parsing SQL Server's own error-message text for a column name) —
  named as a follow-up, not attempted this round.

## Verification (same discipline as every other round in this project)

1. **Real dtexec probe first.** New `Ssis.Extract.FixtureBuilder` mode, `error-redirect`: OLE DB
   Source -> OLE DB Destination (`ErrorRowDisposition=RD_RedirectRow`, fast load) -> (error output)
   -> second OLE DB Destination writing to a table with a real constraint that a seeded row will
   violate (e.g. a `UNIQUE`/`NOT NULL` column), landing `ErrorCode`/`ErrorColumn`/business columns
   plus a DB-side `FailedAt DEFAULT`/`ErrorRowID IDENTITY`. Run via real `dtexec` against
   `.\SQLFORPOC_2022`, read both tables back — this fixes the exact expected values the generated
   code must reproduce, and confirms real SSIS's own `ErrorCode`/`ErrorColumn` values so the "not
   reproduced" comment can honestly say what's actually being approximated.
2. **Generated code, run against the same seed data.** `ssisx generate`, build against a copy of
   `Etl.Core`, run against `.\SQLFORPOC_2022`, read both tables back: every good row lands in the
   primary table, every bad row lands in the error table with a real `SqlException.Number` as
   `ErrorCode` and the row's own business columns intact.
3. **The real motivating case.** Regenerate `SSIS_From_Sandeep`'s actual `Package.dtsx`:
   `DFT_LoadCustomers`'s own gap (if the general multi-destination check fires before this feature
   existed) is gone entirely; `OLEDST_StagingCustomers()`/`OLEDST_StagingErrors()` both appear as
   real, separately-callable methods; the whole regenerated `Package` project builds with the same
   0 warnings/0 errors it already does today for its Script-seam-filled state.
4. Unit tests: `PackagePlannerTests` (the resolved `ErrorRedirectPlan`; the new general
   multi-destination gap, both with and without a redirect present), `PackageGeneratorTests` (the
   emitted error entity/map/`RedirectingSqlFlowSink` wiring), `Etl.Core.Tests`
   (`RedirectingSqlSinkTests` — a failing row redirects and the good rows still land, using
   `FakeUnitOfWork`/an in-memory provider consistent with this project's existing test style; no
   real database needed for these).
5. Full regression: `Ssis.Extract.Codegen.Tests`, `Ssis.Extract.Tests`, `Etl.Core.Tests` all green,
   no change to any existing golden file outside the two touched by this round's own new fixture.
