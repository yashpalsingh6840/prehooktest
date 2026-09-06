# How generated tests actually work (the deeper reference)

This is the "why" and the full API behind what `COPILOT_GUIDE.md` and a `TEST-ORACLE`/
`LOCAL-DATA` work packet already tell you in short form. **You should not usually need to read
this file** -- a work packet embeds everything its own answer needs, verbatim, specifically so
you never have to come here. Read this only when a packet's own template genuinely doesn't cover
what you're looking at.

## The acceptance bar

Right after a fresh `ssisx generate --seams --etl-core Etl.Core --fills fills-library`, with
**zero fills applied**, on a machine with **no database reachable**:

```powershell
dotnet build out\generate\Generated.slnx          # 0 warnings / 0 errors
dotnet test out\generate\Generated.slnx --filter Category!=Integration   # all green
```

If either of those isn't true for a package with no open gaps, that's a real bug in the
generator, not something to patch around by hand-editing the generated test.

## Why no SQLite, no Testcontainers, no throwaway database

`Etl.Core`'s SQL path is SQL-Server-only by construction (`UnitOfWork` casts to `SqlConnection`
and throws otherwise, `GetBindTokenAsync` runs `sp_getbindtoken`, `SqlRowSource`/
`SecondaryConnectionSqlStep` hardcode `new SqlConnection`). A SQLite-backed test would prove a
*different dialect* works, not this one -- the misleading-green failure class this whole
generator's own "gaps not guesses" discipline exists to avoid. So there is no second database
implementation anywhere in this test story. Instead: `FakeUnitOfWork` (no database at all,
records what it was called with) is the default for everything that doesn't strictly need a real
server, and `[Trait("Category", "Integration")]` marks the handful of tests that do.

## `PackageHarness` -- the one type every generated test goes through

Every generated `{Package}.Tests` project carries its own `TestDoubles/` folder
(`FakeUnitOfWork.cs`, `RecordingNotifier.cs`, `PackageHarness.cs`) -- self-contained, disposable,
regenerated fresh every `generate` run, the same pattern `Ssis/SsisFn.cs` already uses. Its full
public surface:

- `using var harness = new PackageHarness();` -- one per test, disposed at the end.
- `harness.Package()` -- a fresh instance of the generated `{Package}Package` class, wired to
  fakes: no database, no real file server, no real Script Task connections, no real secondary
  connection.
- `harness.NewUnitOfWork()` -- a standalone `FakeUnitOfWork` for calling one generated method
  directly (`await package.SomeMethod(uow, CancellationToken.None)`), without going through
  `RunAsync` at all. Its own public surface:
  - `BeginCalled` / `CommitCalled` / `RollbackCalled` (`bool`)
  - `ExecutedSql` (`List<string>`) -- statements run inside the shared transaction
  - `ExecutedParameterizedSql` (`List<ParameterizedSqlCall>`, each with `Sql`/`Parameters`) --
    an OLE DB Command's own per-row calls
  - `ExecutedSqlWithoutTransaction` (`List<string>`) -- a failure handler's own statement,
    run AFTER rollback
  - `BulkInserts` (`List<BulkInsertCall>`, each with `DestinationTable`/`ColumnMappings`/
    `RowsWritten`) -- what a sink actually wrote
  - `ThrowOnGetBindToken` (settable) -- makes the very next `GetBindTokenAsync` call throw; the
    universal, package-shape-agnostic fault-injection point `RunAsync`'s own failure-path test
    uses, since every generated `RunAsync` calls it unconditionally, right after `BeginAsync`,
    before any step runs.
- `harness.CreatedUnitsOfWork` (`ConcurrentBag<FakeUnitOfWork>`) -- every `FakeUnitOfWork`
  actually resolved from DI so far, one per scope. Only relevant when asserting on a run made
  through `RunAsync` itself (not a direct method call): a concurrent wave resolves more than one
  scope's own `IUnitOfWork` in parallel, so this is what lets each branch's own
  commit/rollback be inspected independently after `RunAsync` returns.
- `harness.Notifier.Result` -- the captured `PackageResult` after a `RunAsync` call (a
  `RecordingNotifier` stands in for the real email notifier).
- `harness.SinkFilePath(key)` -- the real, resolved path a Flat File Destination method keyed by
  `key` writes to (a real, writable, per-instance temp path -- not a checked-in file). Read it
  back after the write to assert real widths/delimiters/padding.
- `harness.FileSystemTaskPath(key)` -- the real path a File System Task's own source/destination
  connection-manager key reads from or writes to. Write a source file here BEFORE calling the
  method directly (a starter test does this for you already; write your own if you're extending
  it).
- `harness.ForEachLoopFolder(key)` -- the real, writable FOLDER a ForEach Loop method keyed by
  `key` enumerates (a folder-only entry, not a single file) -- write your own files into it
  before calling the loop method.

## Get every method's exact signature from `<Package>/README.md`, never by reading `<Package>.cs`

`ssisx generate` writes `<out>\generate\<Package>\README.md` fresh every run, from the same
component ordering (`PipelineFlowOrder`) and description text (`ComponentInsight`) the walkthrough
tool and the generator's own method ordering already read -- Seq numbers, generated method
signatures, and which test/gap corresponds to each are all in one table there. Read that table
before writing or reviewing a test; do not re-derive the component-to-method map from the
`.dtsx` or by reading `<Package>.cs` top to bottom. A source/sink method's own parameter list is
NOT the same for every one -- a SQL-reading source takes `(IUnitOfWork uow)` (it binds its own
connection to the package's active transaction, so a later flow reading a table an earlier one
just wrote doesn't deadlock); a file-reading source, or any sink, takes no `uow` parameter at
all. The README's own signature column is the authoritative answer -- never assume by analogy
from a different component.

## The two sample-data tiers -- what each answers, and why they don't replace each other

**Tier A -- deterministic, synthesized, ships with every `generate` run, zero AI, zero fills.**
`ssisx` emits a 2-3 row sample per CSV/fixed-width source into
`{Package}.Tests/SampleData/{Component}.csv`, built straight from the connection manager's own
declared column names, widths, delimiters and types -- the same representative-value convention
(`RepresentativeLiteral`) transform/sink starter tests already use, so a source test, a
transform test, and a sink test for the same package all agree on what a plain string/int/etc.
looks like. This is what makes `dotnet test --filter Category!=Integration` pass immediately
after a fresh `generate`, with nothing filled in yet.

**Tier B -- realistic, human/AI-supplied, and the actual fix for a baked-in original-author
path.** `appsettings.Development.json` (generated, unconditionally, gap or not) overrides every
`FileSource:Files:<Key>:SourceFolder` to a package-relative `TestData` folder -- the wiring is
fully derivable, so it's never gapped. Only the DATA is missing (the original connection
manager's own configured path belongs to the original author's machine, not this one), and THAT
becomes a `LocalFileSourceData` gap/work packet. Answering one is a **`LOCAL-DATA`** fill under
`fills-library\<Package>\TestData\<exact name from the packet's own "Save as" line>` -- **not**
the Tier-A sample's own `{FileSourceKey}.csv` name, which is frequently different (the packet
always states the real name explicitly; never guess it from the gap's own `Location`). This
serves two purposes at once: a real local `dotnet run` with `DOTNET_ENVIRONMENT=Development`
reads realistic data instead of the original machine-specific path, and an optional, richer
no-server test tier can use realistic values when present.

**Excel is the one shape where Tier A does not exist at all** -- this generator's own
`ExcelRowSource` has no `.xlsx` WRITER anywhere, so an Excel source's own `LOCAL-DATA` gap is not
an upgrade over something already working, it is the ONLY way its own
`[Trait("Category", "Integration")]`-tagged real read test can ever actually run.

## The `Category=Integration` convention

A generated test tagged `[Trait("Category", "Integration")]` needs something
`PackageHarness`/`FakeUnitOfWork` cannot fake for free: a real SQL Server (a SQL/ADO NET source's
own real read, a secondary connection's own real connection attempt), or a real `.xlsx` file on
disk (Excel, until a `LOCAL-DATA` fill supplies one). `dotnet test --filter Category!=Integration`
is the baseline every other section of this guide assumes; running without that filter attempts
these tests too and will fail with a real connection/file error on a machine with neither
available -- expected, not a sign anything is broken.

One thing worth knowing rather than assuming: `PackageHarness`'s own `IOptions<DatabaseOptions>`
is hardcoded to an unreachable fake server (`"(fake)"`) by design, matching this generator's own
"fakes by default, a real server is opt-in" philosophy -- pointing an Integration-tagged test at
a REAL server means editing that one line in the generated (but human-editable, unlike
`generate/`'s main tree) `TestDoubles/PackageHarness.cs` for a local run, not something this
guide automates for you.

## `RunAsync`'s two starter tests, and why they look different per package

**Failure path -- always emittable, every package, no exceptions.** Sets
`harness.ThrowOnGetBindToken` on the very next `FakeUnitOfWork`, calls `package.RunAsync(services,
ct)` (or the harness's own equivalent), and asserts: the transaction rolled back, any failure
handlers ran via `ExecutedSqlWithoutTransaction` (which mirrors the real `UnitOfWork`'s own
precondition -- it throws if called while a transaction is still active, which is what lets
handler-runs-AFTER-rollback ordering be pinned with no database at all), the notifier saw
`Succeeded: false`, and the process would report `ExitCode.LoadFailed`.

**Happy path -- only when the WHOLE package is fake-safe.** A package qualifies only if
`PackageClassEmitter.Emit`'s own `supportsFakeHappyPath` says so: no source is SQL/ADO NET
(a real read attempt), no Excel source (a real file read), no Lookup preload (its own
`LookupCache.LoadAsync` takes a raw connection string, never `IUnitOfWork`, so it's never faked
regardless of what the rest of the flow does), no secondary connection, no Script Task (an
arbitrary fill could do anything, including opening a real connection), and no File System Task
(needs a real source file with real content on disk, which only a dedicated File System Task
starter test itself provides). A CSV/fixed-width source writing to a SQL or Flat File sink is the
one shape proven safe. If a package doesn't qualify, only the failure-path test is emitted --
that is correct, not a missing feature.

## `GetBindTokenAsync` / `ExecuteSqlWithoutTransactionAsync` / `NotifyAsync`

These three are `Etl.Core` LIBRARY methods, tested once for the whole portfolio in the dev
repo's own `Etl.Core.Tests` (not regenerated per package) -- if you're looking at a generated
test and wondering why nothing directly exercises these beyond the failure-path test's own
indirect use, that's why: they're already proven correct once, upstream of every package this
generator produces.
