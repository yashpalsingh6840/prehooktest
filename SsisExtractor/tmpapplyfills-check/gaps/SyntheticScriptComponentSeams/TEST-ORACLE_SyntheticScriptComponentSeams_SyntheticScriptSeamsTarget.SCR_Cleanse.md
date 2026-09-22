# Work packet: `TEST-ORACLE:SyntheticScriptComponentSeams:SyntheticScriptSeamsTarget.SCR_Cleanse`

- **Package:** `SyntheticScriptComponentSeams`
- **Location:** `SyntheticScriptSeamsTarget.SCR_Cleanse`
- **Tier:** 1 -- missing datum. Supply one FACT; the generator writes all the code. Do not write code for this.
- **Evidence SHA-256:** `0d21c5fd290f690dfae94552a818fbc65da57b68ce68e860d8c48d3b65cad55b`

## Why `ssisx generate` stopped here

Script Component 'SCR_Cleanse' (SCR_Cleanse) has no generated test at all (a filled seam is human logic -- see the TEST-ORACLE work packet to write one).

## What you need to produce

A whole, self-contained xUnit test FILE -- not a seam spliced into existing generated code
(there is nothing to splice into here; unlike a Script Task/Component gap, this one has no
`partial` method waiting for you). It will be copied verbatim into
`<Package>.Tests/Fills/` by `ssisx apply-fills`, alongside the generated starter tests.
**Known-good testing API -- do not read `TestDoubles/` to confirm any of this:**

- `using var harness = new PackageHarness();` -- one per test, disposed at the end (a `using`
  statement is enough). `harness.Package()` returns a fresh instance of the generated package
  class, wired to fakes (no database, no real file server, no real Script Task connections).
- `harness.NewUnitOfWork()` -- a standalone `FakeUnitOfWork` for calling one method directly
  (`await package.SomeMethod(uow, CancellationToken.None)`), without going through `RunAsync`.
  Its public surface: `BeginCalled`/`CommitCalled`/`RollbackCalled` (bool), `ExecutedSql`
  (`List<string>`), `ExecutedParameterizedSql` (`List<ParameterizedSqlCall>`),
  `ExecutedSqlWithoutTransaction` (`List<string>`), `BulkInserts` (`List<BulkInsertCall>`,
  each with `DestinationTable`/`ColumnMappings`/`RowsWritten`). Set `ThrowOnGetBindToken`
  before the call to make the very next `GetBindTokenAsync` throw.
- `harness.CreatedUnitsOfWork` -- every `FakeUnitOfWork` actually resolved via DI so far (one
  per scope), for asserting on a run made through `RunAsync` itself rather than a direct call.
- `harness.Notifier.Result` -- the captured `PackageResult` after a `RunAsync` call.
- `harness.SinkFilePath(key)` / `FileSystemTaskPath(key)` / `ForEachLoopFolder(key)` -- the
  real, resolved path a Flat File Destination / File System Task / ForEach Loop method keyed
  by `key` reads from or writes to, matching `FileSourceOptions` exactly.
- A test needing something the harness fakes for free CANNOT provide (a real database, a
  real `.xlsx` file, a real secondary-connection server) gets
  `[Trait("Category", "Integration")]` -- the always-green baseline is
  `dotnet test --filter Category!=Integration`, so this tag is required, not optional, on
  such a test.
- **Get every method's exact signature from `<Package>/README.md`'s own component table,
  never by reading `<Package>.cs` top to bottom.** It is generated fresh every run
  specifically so an assistant never has to.

The evidence below tells you WHICH of five shapes this is -- read it before writing anything:

- **A Conditional Split case** this pilot's own oracle-based evaluator could not resolve to a
  boolean for a representative row (see the reason above). Construct your OWN representative
  row (you are not limited to the pilot's single hard-coded row) and assert
  `new <RouterClass>().SelectBranch(row, ctx)` lands in the branch you expect, with a comment
  explaining WHY that branch is correct for that input.
- **A Script Task seam**, once it is filled. Call the generated task directly through a
  `PackageHarness`-backed `IUnitOfWork` and assert its real, observable effect (a row it wrote
  via `uow.ExecutedSql`, a variable it set via `ctx.Variables` if you construct the context
  yourself, etc.) -- not merely that it does not throw.
- **A Script Component seam**, once it is filled. The seam is `private`, so it is never
  directly callable from a separate `.Tests` project -- construct a representative row and
  call `new <TransformClass>().Map(row, ctx)` instead, then assert the exact expected value(s)
  on the returned entity for however many of the component's own columns you can verify --
  state your reasoning for what "correct" means for each, since nothing here computes it for
  you the way `TransformTestEmitter`'s own oracle-verified assertions do.
- **An Aggregate GroupBy/count source**, whose own starter test could not be generated
  because its GroupBy key resolves through a Lookup cache rather than a plain row property.
  Construct a real `AggregateRowSource<TSourceRow,TKey,TRow>` directly (the same class
  production code uses), feeding it a small in-memory fake `IRowSource<TSourceRow>` and a
  key-selector function using a plain dictionary as a stand-in for the Lookup cache (no real
  database needed) -- then assert the grouped/counted output rows.
- **A ForEach-Loop-over-a-Data-Flow-Task's own loop body**, whose per-iteration source has no
  static Tier-A sample file to point a test at (its path is computed fresh each iteration, not
  a fixed location). Write two small temp files into `harness.ForEachLoopFolder(key)` (the
  evidence below names the real folder/file-spec/destination the real package uses) and assert
  the destination sink receives rows from both.

**If you cannot determine a correct expected value with confidence, say so instead of
guessing one** -- a test asserting a wrong value is worse than no test at all, since it looks
like proof of something that was never actually checked.

## Evidence from the package

_This is the companion test for the column ported in its own SCRIPT-COLUMN work packet -- the source below is that SAME Script Component; write a test that exercises the PORTED C# once it exists, not the original SSIS script itself._

- **Data Flow Task:** `DFT_Load`
- **Component:** `SCR_Cleanse`
- **Language:** `CSharp`
- **Reads variables:** _(none)_
- **Writes variables:** _(none)_

### Input columns available on `row`

| Column | SSIS type | Length | C# type |
|---|---|---|---|
| `ID` | `i4` | - | `int` |
| `FirstName` | `wstr` | 50 | `string` |
| `LastName` | `wstr` | 50 | `string` |

### Columns this component produces

The gap above is for this WHOLE component -- return every one of these columns together from the one combined method.

| Column | SSIS type | Length | C# type |
|---|---|---|---|
| `FullName` | `wstr` | 100 | `string` |
| `IsValid` | `bool` | - | `bool` |

### `main.cs`

```csharp
using System;
using Microsoft.SqlServer.Dts.Pipeline;

[SSISScriptComponentEntryPoint]
public class ScriptMain : UserComponent
{
    public override void Input0_ProcessInputRow(Input0Buffer Row)
    {
        // Trim both parts, join with a single space, and drop a trailing space
        // when LastName is empty -- the same shape as the real
        // SCR_CleanseCustomerRow this fixture stands in for.
        Row.FullName = ((Row.FirstName ?? "").Trim() + " " + (Row.LastName ?? "").Trim()).Trim();

        // A row is valid only when BOTH name parts are non-empty after trimming.
        Row.IsValid = (Row.FirstName ?? "").Trim().Length > 0
                   && (Row.LastName ?? "").Trim().Length > 0;
    }
}
```


## Respond with

A single fenced `csharp` block containing the WHOLE test file (usings, namespace, class,
one or more `[Fact]`s) -- this is a new, self-contained file, not a snippet to splice in.

As the FIRST line of the file, include:

```
// ssisx-fill: GapId=<this gap's id, from the heading above> Author=<your name or email> Date=<yyyy-mm-dd> EvidenceSha256=<the Evidence SHA-256 from the heading above>
```

This is what lets `ssisx apply-fills` tell this file apart from a stale one on a later run.
Copy the id and hash verbatim from the heading above; do not compute or guess either.