# ssis-validate -- gate 3 prep (golden corpus + comparison harness)

Status: **prep work for [`Migration-Validation-Plan.md`](../Docs/Migration-Validation-Plan.md)
§5, built before a C# rewrite exists.** A golden corpus captured from both of this PoC's real
SSIS packages, a portable comparison harness verified against a self-test stand-in job (not a
real rewrite -- there isn't one yet), and a Docker Compose SQL-Server-on-Linux target proving
the harness has no Windows/SSIS dependency. Full account, including two real bugs caught while
building this, in [`docs/gate3-harness.md`](docs/gate3-harness.md).

## Why this exists before the rewrite

Gate 3 (plan §5) needs two things that don't exist yet if you wait for a rewrite to start
this work: a captured golden corpus, and a comparison harness. Both are independent of the
rewrite itself -- the corpus is a recording of what the *old* system did, and the harness's
job is generic (seed, run, diff, report) regardless of what "run" means. Building both now
means the moment a C# job exists, running gate 3 for real is pointing the harness at it, not
new engineering.

## Layout

```
Validation/
  Ssis.Validate.Harness/       the engine: TableSnapshot, SnapshotDiffer, GateRunner, CLI
  selftest/Ssis.Validate.SelfTestJob/   stand-in "job under test" -- proves the harness works,
                                         NOT a rewrite of anything
  tests/Ssis.Validate.Harness.Tests/    pure-logic unit tests (no database needed)
  configs/<Package>.config.json         generated + hand-reviewed per-package config
  corpus/<Package>/<RunId>/             captured golden data -- committed, not gitignored
  docker-compose.yml                    SQL Server on Linux, for a real containerized run
  docs/gate3-harness.md                 full account
```

## Build & test

```powershell
dotnet build Validation\SsisValidation.slnx
dotnet test  Validation\SsisValidation.slnx
```

## Generate a package config

```powershell
ssisx report --input ..\SSIS --out out   # produces nondeterministic.json + primary-keys.json

.\Ssis.Validate.Harness\bin\Debug\net8.0\ssis-validate.exe generate-config `
  --package LoadReferenceData --schema-script ..\Sql\01_CreateTarget.sql `
  --tables "dbo.Department,dbo.Designation" `
  --nondeterministic-json ..\Tools\SsisExtractor\out\nondeterministic.json `
  --primary-keys-json ..\Tools\SsisExtractor\out\primary-keys.json `
  --out configs\LoadReferenceData.config.json
```

`KeyColumn` is auto-filled when the extractor found exactly one confident candidate (a
naming-convention guess -- confirm it once, don't assume it forever); a composite or
low-confidence candidate still leaves it `null` for a human (plan §12: "reviewed by a
human"). `ExcludedColumns` is real generated data either way; don't hand-edit it.

## Capture a corpus entry (one-time, per package, against the live SSIS system)

```powershell
.\Ssis.Validate.Harness\bin\Debug\net8.0\ssis-validate.exe capture-corpus `
  --package-key LOAD_REFERENCE_DATA --package-name LoadReferenceData `
  --runner-exe ..\Scripts\SsisRunner\bin\Release\net8.0\SsisRunner.exe `
  --ssispoc-connection "Server=.\SQLFORPOC;Database=SsisPoC;Integrated Security=True;TrustServerCertificate=True;" `
  --ssisdb-connection "Server=.\SQLFORPOC;Database=SSISDB;Integrated Security=True;TrustServerCertificate=True;" `
  --tables "dbo.Department:DepartmentID,dbo.Designation:DesignationID" `
  --input-files "..\Data\Input\Department\Departments.csv,..\Data\Input\Designation\Designations.csv" `
  --logging-level 3 --out corpus
```

Windows-only (drives `SsisRunner.exe` against local SSISDB) -- this half retires along with
the SSIS packages themselves. See docs/gate3-harness.md for why `--logging-level 3` matters.

## Run gate 3

```powershell
docker compose up -d   # SQL Server on Linux -- no ".\SQLFORPOC", no SSIS, no Windows

.\Ssis.Validate.Harness\bin\Debug\net8.0\ssis-validate.exe run `
  --config configs\LoadReferenceData.config.json --corpus-run corpus\LoadReferenceData\25 `
  --connection "Server=localhost,14330;Database=SsisPoC;User Id=sa;Password=...;TrustServerCertificate=True;" `
  --job <path to the real job> --job-arg <arg> ... `
  --job-input-dir .\tmp-input --report-dir reports\LoadReferenceData\<date>

docker compose down
```

Exit 0 pass, 1 mismatch (see `reports/<Package>/.../report.md` for exactly what differed),
2 usage/setup error. Today `--job` points at `selftest/Ssis.Validate.SelfTestJob` -- verified
end to end (PASS and FAIL, both against a real containerized run) in
[`docs/gate3-harness.md`](docs/gate3-harness.md). Point it at the real rewrite once one exists.

## Honest limits

See [`docs/gate3-harness.md`](docs/gate3-harness.md)'s own section: per-stage row-count
assertion is captured but not yet compared against (no rewrite exists to define a comparable
shape); `KeyColumn` auto-fill is a naming-convention heuristic (confirm it, don't assume it);
row matching is by key, not full-row
identity; only two corpus entries exist (one run each) -- this is the mechanism, not the
10-30-day golden corpus plan §2 actually calls for.
