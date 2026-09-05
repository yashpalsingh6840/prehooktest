# Gate 3 prep — golden corpus + comparison harness

Pre-work for [`Migration-Validation-Plan.md`](../../Docs/Migration-Validation-Plan.md) §5
(golden-dataset integration test in CI), built **before** a C# rewrite exists. Gate 3 itself
needs two things that don't exist yet if you wait for the rewrite to start this: a captured
golden corpus, and a comparison harness. Both are built now, against the two current SSIS
packages, so that once a rewrite lands, running gate 3 for real is "point the harness at the
new job" — a config/argument change, not new engineering.

## The two halves, and why they have different lifetimes

| | `CorpusCapture` | `GateRunner` |
|---|---|---|
| Runs against | This box's live SSISDB (Windows-only) | Whatever database the job under test writes to — the gate-3 container, in real use |
| Purpose | Record what the OLD SSIS package actually did, once per corpus entry | Replay: seed, run, diff, report — this **is** gate 3 |
| Needed after cutover? | No — the SSIS packages will be retired | Yes — this is the harness that runs on every commit forever |
| SSIS/Windows/GAC dependency | Yes (drives `SsisRunner.exe` against local SSISDB) | **None** — `Microsoft.Data.SqlClient` + `System.Diagnostics.Process`, nothing else |

This split matters for the stated direction of this engagement: all 50 packages are moving to
C# jobs, so `catalog.execution_data_statistics`/SSISDB won't exist to query once a package is
retired. `CorpusCapture` is explicitly a **one-time, pre-migration** tool per package — it
reads the old system once, before it goes away, and writes a self-contained corpus entry that
never needs SSISDB again. `GateRunner`, the part that actually runs forever, never talks to
SSISDB at all; it only needs a SQL Server connection string and a command to run.

```powershell
ssis-validate capture-corpus --package-key LOAD_REFERENCE_DATA --package-name LoadReferenceData `
  --runner-exe ..\Scripts\SsisRunner\bin\Release\net8.0\SsisRunner.exe `
  --ssispoc-connection "Server=.\SQLFORPOC;Database=SsisPoC;Integrated Security=True;TrustServerCertificate=True;" `
  --ssisdb-connection "Server=.\SQLFORPOC;Database=SSISDB;Integrated Security=True;TrustServerCertificate=True;" `
  --tables "dbo.Department:DepartmentID,dbo.Designation:DesignationID" `
  --input-files "..\Data\Input\Department\Departments.csv,..\Data\Input\Designation\Designations.csv" `
  --logging-level 3 `
  --out corpus
```

`--logging-level 3` matters: `catalog.execution_data_statistics` (the per-stage row counts,
plan §5 step 6) is only populated at a verbose enough logging level — the `ctl` framework's
own default (level 1, CLAUDE.md) writes nothing there. Confirmed empirically: the first capture
attempt at the default level produced an empty `StageRowCounts` list; re-running at level 3
populated it correctly.

## Measured on this PoC

Real corpus entries captured from real runs, not fabricated:

| Package | RunId | Rows before → after | Input files |
|---|---|---|---|
| `LoadEmployees` | 26 | `dbo.Employee`: 0 → 12 | `Employees.csv` |
| `LoadReferenceData` | 25 | `dbo.Department`: 0 → 8, `dbo.Designation`: 0 → 10 | `Departments.csv`, `Designations.csv` |

**One real finding along the way, not obvious in advance:** `catalog.execution_data_statistics`
records more rows than one-per-component-pair — it logs a row per BUFFER flush, including a
trailing `0`-row entry marking end-of-data. `LoadEmployees`'s capture shows exactly this: two
rows for `FF_SRC_Employees → DER_MergeColumns` (`0` then `12`) and two for
`DER_MergeColumns → OLEDST_Employee` (`0` then `12`), in that order. A consumer that wants a
single "rows processed by this stage" number needs to sum or take the max, not assume one row
per component pair.

## Config format — JSON, not YAML

Plan §12 illustrates configs as YAML; this tool uses JSON instead, deliberately. This repo has
no YAML dependency anywhere, and JSON is already the convention for every other generated
artifact (`spec.json`, conformance rules, the non-determinism manifest). Same design, different
serialization — not a different idea.

```powershell
ssis-validate generate-config --package LoadReferenceData `
  --schema-script ..\Sql\01_CreateTarget.sql `
  --tables "dbo.Department,dbo.Designation" `
  --nondeterministic-json <ssisx report output>\nondeterministic.json `
  --primary-keys-json <ssisx report output>\primary-keys.json `
  --out configs\LoadReferenceData.config.json
```

`ExcludedColumns` is populated automatically from `ssisx report`'s non-determinism manifest —
real, generated data, the same file gate 0/1 already produce. **`KeyColumn` is now
auto-filled too** (added 2026-08-19, `ssisx report`'s new `primary-keys.json` --
`PrimaryKeyInference`'s naming-convention heuristic), but only when the extractor found
exactly one confident candidate column — a composite key or "Unknown" confidence still leaves
it `null` for a human, and even an auto-filled value is printed back with a reminder to
confirm it (a `.dtsx` carries no real primary-key concept, so this is still a guess, not a
fact). **Verified, not assumed:** regenerating both of this PoC's configs with
`--primary-keys-json` produces byte-identical output to the configs a human had already
hand-filled in an earlier session (`EmployeeID`/`DepartmentID`/`DesignationID`) — confirmed
with a plain `diff`, zero differences.

## Self-test: proving the harness works before a rewrite exists to test

There is no C# rewrite yet, so `GateRunner` has nothing real to validate. Rather than leave
that unverified, `selftest/Ssis.Validate.SelfTestJob/` is a tiny stand-in "job under test" —
**explicitly not a rewrite of anything** — used only to prove the comparison logic itself is
correct, the same way the extractor's synthetic fixtures prove an algorithm before a real
package can exercise it:

- `--mode pass` loads the corpus's own golden "after" snapshot verbatim → the harness should
  report **PASS**.
- `--mode fail` deliberately corrupts one value first → the harness should report **FAIL**,
  naming exactly what differs.

Both verified end to end against this PoC's real captured corpus:

```
[gate3] PASS -- LoadReferenceData
```
```
[gate3] FAIL -- LoadReferenceData, see reports/LoadReferenceData/fail
## dbo.Department -- MISMATCH
| Kind | Key | Detail |
|---|---|---|
| Changed | 1 | DepartmentKey: expected `ENG-1`, got `SELFTEST-CORRUPTED-VALUE` |
```

**A gap in that first self-test round was caught and closed, not left implicit.** The
`--mode pass` job copies the corpus's own captured values verbatim, so an excluded column
(`LoadedAtUtc`) is identical between expected and actual by construction — that run never
actually exercised the EXCLUSION logic, only the "everything matches" path. Closed with a
focused unit suite (`tests/Ssis.Validate.Harness.Tests/SnapshotDifferTests.cs`, no database
needed) that constructs two snapshots with a genuinely differing excluded column and asserts
the differ still reports a match — plus the inverse (same inputs, column NOT excluded, must
fail) so the exclusion test proves the exclusion, not a differ that ignores that column by
default.

## Running against a container

`docker-compose.yml` brings up SQL Server on Linux (`mcr.microsoft.com/mssql/server:2022-latest`),
not this dev box's Windows-only `.\SQLFORPOC` — proving the comparison side of gate 3 has no
Windows/SSIS dependency for real, not by assumption because the C# happens to be portable.

```powershell
docker compose up -d
ssis-validate run --config configs\LoadReferenceData.config.json --corpus-run corpus\LoadReferenceData\25 `
  --connection "Server=localhost,14330;Database=SsisPoC;User Id=sa;Password=...;TrustServerCertificate=True;" `
  --job ...\selftest-job.exe --job-arg --mode --job-arg pass ... `
  --job-input-dir .\tmp-input --report-dir reports\LoadReferenceData\container-pass
```

**A real environment bug was caught wiring this up, not shipped:** on a brand-new container
the target database doesn't exist yet, so a connection string that already names it as the
initial catalog fails to *open* at all — before the schema script's own
`CREATE DATABASE`/`USE` batches ever run. Fixed by having `GateRunner` connect at the server
level (`master`) for the schema-restore step specifically, then switch to the real
connection string (which resolves fine once that step has created the database) for
everything after.

**Verified against a real, freshly-created container, both packages:** `LoadReferenceData`
PASS, `LoadReferenceData` FAIL (correctly, with the exact corrupted value named), and
`LoadEmployees` PASS — all three against a `mcr.microsoft.com/mssql/server:2022-latest`
container with no prior state, torn down afterward (`docker compose down`). This is the
strongest evidence available without a real rewrite: the gate-3 comparison engine runs
correctly against a brand-new, non-Windows, non-SSIS SQL Server, end to end.

## 2026-08-20 — first real gate 3 run against the actual rewrite

A C# rewrite of both packages now exists at `D:\PoC\SSIS_Migration` (a separate repo/folder,
`net10.0`, `Etl.Core` + `LoadEmployees` + `LoadReferenceData` console apps, `Host.
CreateApplicationBuilder`-based config, EF Core against the *same* `Sql/01_CreateTarget.sql`
schema this repo already uses). This is the moment gate 3 stops being prep and starts being
gate 3: **both packages verified PASS**, for real, against a freshly created
`mcr.microsoft.com/mssql/server:2022-latest` container — not the `selftest` stand-in job.

Getting there surfaced two real bugs in the harness itself, neither visible against the
stand-in job because it never went through a real `Host.CreateApplicationBuilder` config
chain or a real SQL-auth login:

- **`GateRunner` never set the job process's working directory**, so the child inherited
  wherever `ssis-validate` itself was invoked from (`Tools/Validation` at the time, now `Validation`), not the job exe's own
  folder. `Host.CreateApplicationBuilder`'s `AddJsonFile("appsettings.json", optional: true,
  ...)` resolves that path relative to the current directory — a mismatch doesn't error, it
  silently loads *none* of the job's configured defaults. Symptom: `LoadEmployees` reverted to
  `DatabaseOptions.AuthMode`'s hard C# default (`Windows`) instead of `appsettings.json`'s real
  `SqlServer`, and failed against the Linux container with a bewildering `Login failed for
  user '<container-hostname>\Guest'` — nothing about that error suggests "wrong working
  directory." Fixed by setting `ProcessStartInfo.WorkingDirectory` to the job command's own
  directory.
- **A package's schema restore can need more than one script.** `LoadEmployees`' SQL-auth
  login (`ssis_loader`) and its `GRANT`s live in `Sql/04_CreateLoaderCredential.sql`, separate
  from `Sql/01_CreateTarget.sql` — and `01`'s `DROP TABLE`/recreate wipes any `GRANT`s a prior
  run of `04` made on that table (the same interaction CLAUDE.md's own trap notes for local
  runs), so `04` must be re-applied *after* `01`, every gate-3 run, not once. Added
  `PackageConfig.AdditionalSchemaScripts` (run in order, against the real target connection,
  right after `SchemaScript`) rather than folding this into `SchemaScript` itself, which stays
  a plain schema-DDL script shared with the rest of this repo.

**One more real finding, confirmed empirically rather than assumed** (a scratch probe
replicating `EtlHost.Create`'s exact configuration chain, no database involved): the rewrite's
`EtlHost.Create` calls `builder.Configuration.AddUserSecrets(...)` *after*
`Host.CreateApplicationBuilder(args)` has already wired in the command-line provider — and
.NET configuration precedence is "last provider added wins" for a given key. So for
`LoadEmployees`, which already has a real `TargetDatabase:Password` stored in User Secrets,
**that secret cannot be overridden via `--job-arg` or an environment variable** — the probe
proved this concretely: overriding `TargetDatabase:UserId` via the command line worked
(nothing in secrets shadows it), but overriding `TargetDatabase:Password` did not — the
resolved value stayed the secret's, verbatim. Gate 3 doesn't fight this: instead of overriding
`LoadEmployees`' password, the run relies on `04_CreateLoaderCredential.sql` creating the
*same* `ssis_loader` login with the *same* password already stored in the rewrite's secrets
(`ZMNRgL4kUp5iyHi+*wjhuMRer-6*` — confirmed matching by direct comparison, not by design) —
so the container ends up with a login that authenticates correctly with zero credential
overrides, exercising the real SQL-auth path rather than substituting `sa` for it.

**The exact commands that produced the PASS**, both against `docker compose up -d`'s
container (`localhost,14330`, `sa`/`Gate3-SelfTest-2026!` from `docker-compose.yml`):

```powershell
.\Ssis.Validate.Harness\bin\Debug\net8.0\ssis-validate.exe run `
  --config configs\LoadReferenceData.config.json --corpus-run corpus\LoadReferenceData\25 `
  --connection "Server=localhost,14330;Database=SsisPoC;User Id=sa;Password=Gate3-SelfTest-2026!;TrustServerCertificate=True;Encrypt=False;" `
  --job "D:\PoC\SSIS_Migration\src\LoadReferenceData\bin\Debug\net10.0\LoadReferenceData.exe" `
  --job-arg --ReferenceDataFiles:DepartmentFolder --job-arg <jobInputDir> `
  --job-arg --ReferenceDataFiles:DepartmentFileName --job-arg Departments.csv `
  --job-arg --ReferenceDataFiles:DesignationFolder --job-arg <jobInputDir> `
  --job-arg --ReferenceDataFiles:DesignationFileName --job-arg Designations.csv `
  --job-arg --TargetDatabase:Server --job-arg "localhost,14330" `
  --job-arg --TargetDatabase:Database --job-arg SsisPoC `
  --job-arg --TargetDatabase:AuthMode --job-arg SqlServer `
  --job-arg --TargetDatabase:UserId --job-arg sa `
  --job-arg --TargetDatabase:Password --job-arg "Gate3-SelfTest-2026!" `
  --job-arg --TargetDatabase:Encrypt --job-arg false `
  --job-arg --TargetDatabase:TrustServerCertificate --job-arg true `
  --job-input-dir <jobInputDir> --report-dir reports\LoadReferenceData\real-rewrite
# [gate3] PASS -- LoadReferenceData

.\Ssis.Validate.Harness\bin\Debug\net8.0\ssis-validate.exe run `
  --config configs\LoadEmployees.config.json --corpus-run corpus\LoadEmployees\26 `
  --connection "Server=localhost,14330;Database=SsisPoC;User Id=sa;Password=Gate3-SelfTest-2026!;TrustServerCertificate=True;Encrypt=False;" `
  --job "D:\PoC\SSIS_Migration\src\LoadEmployees\bin\Debug\net10.0\LoadEmployees.exe" `
  --job-arg --FileSource:SourceFolder --job-arg <jobInputDir> `
  --job-arg --FileSource:SourceFileName --job-arg Employees.csv `
  --job-arg --TargetDatabase:Server --job-arg "localhost,14330" `
  --job-arg --TargetDatabase:Database --job-arg SsisPoC `
  --job-arg --TargetDatabase:Encrypt --job-arg false `
  --job-arg --TargetDatabase:TrustServerCertificate --job-arg true `
  --job-input-dir <jobInputDir> --report-dir reports\LoadEmployees\real-rewrite
# [gate3] PASS -- LoadEmployees -- note: no AuthMode/UserId/Password override at all, relying
# on appsettings.json's own SqlServer/ssis_loader default plus the secret described above.
```

Note `LoadEmployees` needed no `AuthMode`/`UserId`/`Password` override at all — its own
`appsettings.json` default (`SqlServer`/`ssis_loader`) plus the matching secret handled it.
`LoadReferenceData` needed the full `SqlServer`/`sa` override because its default
(`AuthMode: Windows`) has no meaning against a Linux container with no domain to integrate
with.

## 2026-08-20 — the false-green gap: gate 3 only ever compared SQL tables

Raised directly by the user, reviewing this document: *what happens when a package's real
output isn't a database table -- an email notification, a file dropped to SFTP, or several
of these plus a table in one package?* The honest answer, before this section, was that gate 3
would compare whatever `TargetTables` the config listed and print PASS regardless of what else
the package did -- a package whose only real output is an SFTP file would have zero
`TargetTables`, compare nothing, and still report success. **A partial check reporting as a
full pass is worse than no check, because it gets trusted.**

Closed with `ssisx report`'s new `effects.json` (full account in
`Tools/SsisExtractor/docs/report-schema.md`'s own "Observable effects" section) and two
harness-side changes:

- **`PackageConfig.DeclaredEffects`** -- every effect `effects.json` names for a package that
  isn't one of its `TargetTables`, snapshotted at `generate-config` time via the new
  `--effects-json` flag (same pattern as `ExcludedColumns`/`KeyColumn`). This also catches a
  `SqlTable` effect that's real but was left off `--tables` by mistake -- not just the
  file/procedure/uncharacterized-task case.
- **`GateRunResult.Outcome`** replaces the old boolean `Passed` with three states: `Pass` (every
  table matched AND every declared effect is `Verifiable`), `Fail` (a table mismatched or the
  job errored), and `Incomplete` (every checked table matched, but a declared effect has no
  checker -- exit code **3**, distinct from both 0 and 1, and never silently folded into
  either). `ssis-validate run`'s own help text now spells out why 3 must never be treated like
  0.

**What this does NOT yet do -- the honest remaining gap:** there is still no actual checker
for a `File`/email/queue effect, only the mechanism that refuses to claim PASS while one is
unverified. Building a real file/SFTP-output checker (capture: drop into a directory or local
SFTP server; compare: filename pattern + content diff, skip-list for a date-stamped filename
or trailer row count -- the same three-part shape `TargetTables` already has) is the next
piece, and per the user's own framing this has a deadline: corpus capture for a non-table
effect needs a **staging run of the still-live SSIS package with its endpoint redirected**
(SMTP → a local catcher, SFTP → a local server), which has to happen before that package is
retired, not after.

**Multi-step packages** (load a table, call a stored proc that validates and transfers to
another table, send a notification) are now at least honestly represented: each step's own
table becomes a `TargetTables`/`Verifiable` entry if declared, the notification becomes a
`DeclaredEffects`/`NeedsChecker` entry blocking `Pass`, and a stored procedure's own writes are
`NeedsHumanReview` specifically because static analysis cannot see what a procedure body
writes -- its real targets must be identified and added to `--tables` by a person, or the
`Incomplete` outcome is the honest and correct one to leave standing.

## Honest limits

- **Per-stage row counts are captured, not yet asserted against.** Plan §5 step 6 wants gate 3
  to assert per-stage row counts; `CorpusCapture` records them from
  `catalog.execution_data_statistics`, but there's no rewrite yet to define what a comparable
  "stage" even means in C# (a method call? a batch insert?), so `GateRunner` doesn't try to
  compare against them yet. The data is there in the corpus manifest for whenever that
  convention exists.
- **`KeyColumn` auto-fill is a naming-convention heuristic, not a fact (closed 2026-08-19,
  was previously hand-filled only).** `PrimaryKeyInference` (`Tools/SsisExtractor`) has no
  live database to check against, so it can be wrong — a table whose real key doesn't follow
  the `"<table>ID"`/`"ID"` convention gets `Confidence: "Unknown"` and still needs a human,
  and even a confident guess should be spot-checked once, not assumed correct forever. Still
  narrower than before: composite keys and low-confidence guesses are still left `null`.
- **Row matching is by key, not by full-row identity.** A column not in the key and not
  excluded that legitimately has multiple valid representations (e.g. floating-point
  formatting) would show as a false mismatch. Not an issue for either of this PoC's packages
  (no such columns), but worth knowing before onboarding package #12.
- **Only two corpus entries exist.** One run each, captured once. A real gate 3 needs the
  golden corpus described in plan §2 (10–30 days, spanning a month-end) — this is the
  mechanism, not that corpus.
- **This proves the harness works, not that any rewrite is correct.** Same framing as gates
  1 and 2: a self-test passing means the comparison logic is trustworthy, not that a C#
  job satisfies it — that's what pointing this at a real rewrite is for.
