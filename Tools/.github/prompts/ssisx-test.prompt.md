---
mode: agent
description: Port TEST-ORACLE and LOCAL-DATA gaps into fills-library, apply them, and run dotnet test. The AI half of the generated-tests story -- reads packets per gap, same discipline as ssisx-fill.
---

# ssisx: test-oracle + local-data gaps

- **Package:** `${input:package:Package name, e.g. the folder name under out/gaps/}`

## Step 0 -- check for existing fills FIRST

List `fills-library/<package>/Tests/` and `fills-library/<package>/TestData/`. If files already
exist, **read and verify them against the work packets -- never overwrite them.** Prior,
already-reviewed work may be sitting there.

Also check `out/fills/<package>/Tests/` and `out/fills/<package>/TestData/` -- if real work is
stranded in that disposable location, move it into `fills-library/<package>/` and re-apply.

## Step 1 -- read the packets economically

Work packets live in `out/gaps/<package>/`. Two kinds, and they are NOT interchangeable --
check the `GapId`'s own prefix (`TEST-ORACLE:...` / `LOCAL-DATA:...`) or the gap's `Kind` in
`gaps.json` before writing anything:

- **`TEST-ORACLE`** -- the deterministic emitter could not itself derive an expected value (a
  Conditional Split case that isn't a bare comparison against a literal, or a test for a Script
  Task/Component seam once it's been filled via `ssisx-fill.prompt.md`). The packet embeds
  **everything** you need: the pinned testing-API block (`FakeUnitOfWork`'s public surface,
  `PackageHarness` usage, the `Category=Integration` convention), and either the split's own
  case expressions or the seam's real script source. **Do not open
  `TestDoubles/PackageHarness.cs` or `COPILOT_TESTING_GUIDE.md` to confirm any of it** -- the
  packet's own copy is authoritative and already verbatim.
- **`LOCAL-DATA`** -- a realistic sample file, because the `.dtsx`'s own connection manager
  points at the ORIGINAL SSIS environment's file path (which does not exist here). The packet
  names the **exact file name** in its own "Save as" line and gives the exact column
  schema/widths/delimiter. Use that name verbatim -- a mismatched name means `apply-fills` can
  never match it to the gap (there is no provenance comment possible on a raw data file, so the
  filename **is** the only identity it has).

Read one packet per gap here (unlike Tier-2 seams, these do not usually share evidence across
multiple gaps the way one Script Component's several columns do). Even so, do not read a
packet whole if you don't need its own copy of the pinned testing-API block — a `TEST-ORACLE`
packet's evidence/case-expression content is what varies per gap; its testing-API block is
identical boilerplate.

## Step 2 -- write the fills

Known-good testing API -- **do not read `TestDoubles/PackageHarness.cs` to confirm any of
this** (same pinned block every `TEST-ORACLE` packet already embeds):

```
FakeUnitOfWork      : ExecutedSql | ExecutedParameterizedSql | ExecutedSqlWithoutTransaction
                    | BulkInserts | fault injection (ThrowOnStep) | mirrors real preconditions
                      (ExecuteSqlWithoutTransactionAsync throws unless RollbackCalled;
                      GetBindTokenAsync caches)
RecordingNotifier   : captures the PackageResult
PackageHarness      : a ServiceProvider with a fresh FakeUnitOfWork per scope, NullLogger, a
                      fixed TimeProvider, FileSourceOptions -> SampleData/, BulkCopyOptions,
                      DatabaseOptions, IConfiguration (secondary connections only).
                      Exposes .Package() plus the recorded uows.
[Trait("Category", "Integration")] on any test that genuinely needs a real database, a real
   .xlsx file, or a real secondary-connection server -- everything else must pass with
   `dotnet test --filter Category!=Integration` and NO server reachable.
```

Generated projects build with `Nullable=enable` **and `TreatWarningsAsErrors=true`** -- an
unused `using` or an unreachable warning fails the build here.

**File conventions, per gap kind:**

```
TEST-ORACLE : fills-library/<package>/Tests/<Name>Tests.cs
LOCAL-DATA  : fills-library/<package>/TestData/<exact name from the packet's "Save as" line>
```

A `TEST-ORACLE` file is a brand-new xUnit test file, not a partial-class seam -- put the
provenance comment as the file's own **first line** (there is no seam declaration to place it
above):

```
// ssisx-fill: GapId=<from packet> Author=<name-or-email> Date=<yyyy-mm-dd> EvidenceSha256=<from packet>
```

Copy `GapId`/`EvidenceSha256` **verbatim** from the packet -- never compute or guess them.

A `LOCAL-DATA` file gets **no comment of any kind** -- a raw sample file has no provenance
convention, and `apply-fills` matches it purely by filename against the gap's own expected name.

If a `TEST-ORACLE` packet's own evidence cannot actually be resolved to a concrete expected
value (e.g. a split condition this tool's expression evaluator still can't evaluate), **say so
plainly instead of inventing a plausible assertion.**

## Step 3 -- apply, build, and test

Resolve `$ssisx`/`$dotnet` via the bootstrap block in `ssisx-extract.prompt.md` (Step 1) —
**reuse that same resolved path here; never `dotnet run --project ...` for the CLI**, which
pays a full restore/build check on every call — then:

```powershell
& $ssisx apply-fills --out out --fills fills-library
& $dotnet build out/generate/Generated.slnx --nologo -v minimal
& $dotnet test out/generate/Generated.slnx --filter Category!=Integration --nologo -v minimal
```

Read only the final outcome lines (`Build succeeded`/`Build FAILED`, the test pass/fail
summary) plus any `error`/`FAILED` lines — do not tail the full restore/compile transcript.

`apply-fills` reports `Applied`/`Stale`/`Orphaned` per fill -- same discipline as a Tier-2 seam.
A `TestOracle`/`LocalFileSourceData` gap left unfilled is **non-blocking** (the package still
builds and its other tests still run), so don't treat "some tests still gapped" as a failure --
report it as remaining work, not a broken build.

**Never** invent a connection string, seed data, or sample input beyond what a `LOCAL-DATA`
packet explicitly asked for, to make an Integration-tagged test "pass" here -- there is no real
server/file in this environment, and that is the correct, expected state.

## Step 4 -- report

- Confirm the build result, the `dotnet test` summary (passed/failed/skipped), and which gaps
  were filled vs. still outstanding.
- List every **non-mechanical judgement call** for human review (an inferred expected value, a
  representative row chosen for a boundary condition, a schema/format assumption about a
  `LOCAL-DATA` file).
- List remaining Tier-1/Tier-2/Tier-3 gaps, if any (this prompt only closes `TEST-ORACLE`/
  `LOCAL-DATA` -- it does not touch the others).
- Remind me to `git add fills-library && git commit`.

## Logging

Append to `session-logs/<yyyy-MM-dd>-session.md`: this prompt, the commands, their output, the
judgement calls, and a bump of the counters.
