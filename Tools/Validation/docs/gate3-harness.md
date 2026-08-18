# Gate 3 prep — golden corpus + comparison harness

Pre-work for [`Migration-Validation-Plan.md`](../../../Migration-Validation-Plan.md) §5
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
  --runner-exe ..\..\Scripts\SsisRunner\bin\Release\net8.0\SsisRunner.exe `
  --ssispoc-connection "Server=.\SQLFORPOC;Database=SsisPoC;Integrated Security=True;TrustServerCertificate=True;" `
  --ssisdb-connection "Server=.\SQLFORPOC;Database=SSISDB;Integrated Security=True;TrustServerCertificate=True;" `
  --tables "dbo.Department:DepartmentID,dbo.Designation:DesignationID" `
  --input-files "..\..\Data\Input\Department\Departments.csv,..\..\Data\Input\Designation\Designations.csv" `
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
  --schema-script ..\..\Sql\01_CreateTarget.sql `
  --tables "dbo.Department,dbo.Designation" `
  --nondeterministic-json <ssisx report output>\nondeterministic.json `
  --out configs\LoadReferenceData.config.json
```

`ExcludedColumns` is populated automatically from `ssisx report`'s non-determinism manifest —
real, generated data, the same file gate 0/1 already produce. `KeyColumn` is deliberately left
`null` by the generator: the extractor doesn't detect primary keys from a `.dtsx` today, so
this is the one field a human fills in per plan §12's own framing ("reviewed by a human"). Both
of this PoC's configs have been filled in by hand from the known target schema
(`EmployeeID`/`DepartmentID`/`DesignationID`) and committed.

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

## Honest limits

- **Per-stage row counts are captured, not yet asserted against.** Plan §5 step 6 wants gate 3
  to assert per-stage row counts; `CorpusCapture` records them from
  `catalog.execution_data_statistics`, but there's no rewrite yet to define what a comparable
  "stage" even means in C# (a method call? a batch insert?), so `GateRunner` doesn't try to
  compare against them yet. The data is there in the corpus manifest for whenever that
  convention exists.
- **`KeyColumn` is hand-filled, not detected.** Same honest gap `ssisx conformance`'s
  `TargetSchema` rules already have — primary-key detection from a `.dtsx` alone isn't built.
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
