# SSIS PoC — agent context

Project purpose, layout, and full usage docs are in [README.md](README.md) —
read that first. This file is operational notes for an agent picking this
project back up in a fresh session: environment specifics, exact commands
known to work, and traps already hit once (don't re-discover them).

## What this project is

Two SSIS Project-Deployment-Model packages that read CSVs, merge columns with
Derived Column transforms, and load SQL Server tables. Every path/connection
string is a parameter, not hardcoded — that was the core requirement.

| Package | Loads | Parameter scope |
|---|---|---|
| `SSIS/LoadEmployees.dtsx` | `dbo.Employee` | project params only |
| `SSIS/LoadReferenceData.dtsx` | `dbo.Department`, `dbo.Designation` | **package** params + shared project param |

The split of parameter scopes is deliberate, not incidental: it forces the
control framework to resolve `object_type` (20 vs 30) from catalog metadata
rather than being told, which is what lets callers pass a flat name/value bag.

On top of that sits **`SsisPoC.ctl`** — a control framework
(`Sql/03_CreateControlFramework.sql`) exposing four procedures
(`usp_StartPackage` / `usp_StartRun` / `usp_PollRun` / `usp_PollCorrelation`)
that let an application run one package or any number of them, with per-run
parameters, in **one round trip each**, without referencing SSISDB at all.
There is no retry procedure — calling `usp_StartPackage`/`usp_StartRun` again
**is** the retry (`@RetryOfRunId` for lineage, `@LoggingLevel` for verbosity).
`Scripts/SsisRunner/` (.NET 8) is the client. See README for the design and
what was verified.

Ways to execute with parameter overrides:
- `Scripts/Run-Package.ps1` (PowerShell, compiles `PkgRun.cs` via `Add-Type`)
- `Scripts/PkgRun.Console/` (C# console app, `PkgRun.Console.exe`)
- **SSISDB catalog** — project deployed to `.\SQLFORPOC`, parameters bound
  to an Environment, executed server-side (see "SSISDB catalog" below)

The first two are in-process wrappers around the same `Scripts/PkgRun.cs`
logic; the third runs inside SQL Server and is independent of them.

## This machine's environment (facts, not guesses — re-verify if stale)

- SQL Server 2025 Developer Edition, named instance **`.\SQLFORPOC`**
  (services `MSSQL$SQLFORPOC`, `SQLAgent$SQLFORPOC`, `SQLBrowser`,
  `MsDtsServer170`). This is what `Project.params` defaults point at.
- SSIS managed assemblies live in the **GAC**, version `17.0.0.0`,
  PublicKeyToken `89845dcd8080cc91`:
  - `Microsoft.SqlServer.ManagedDTS` — `GAC_MSIL`
  - `Microsoft.SqlServer.DTSPipelineWrap` — `GAC_MSIL`
  - `Microsoft.SqlServer.DTSRuntimeWrap` — `GAC_64` (platform-specific —
    anything referencing it must build/run as **x64**, not AnyCPU/x86)
  - Resolve current paths with:
    `[System.Reflection.Assembly]::LoadWithPartialName('Microsoft.SqlServer.ManagedDTS').Location`
- VS 2022 Professional at
  `C:\Program Files\Microsoft Visual Studio\2022\Professional\`
  (`devenv.com`, `MSBuild\Current\Bin\amd64\MSBuild.exe`).
- `dotnet` CLI has SDKs 9.x/10.x (Core/.NET 5+ only, **no .NET Framework
  SDK** installed) but full `net48` **reference assemblies** are present
  (`C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\
  .NETFramework\v4.8`), so `dotnet build` on an SDK-style `net48` csproj
  works fine even though `dotnet run`/execution of the resulting exe needs
  the classic CLR, not the `dotnet` muxer — run the built `.exe` directly.
- Local `dtexec.exe` (both the SSDT `...\SSIS\170\Binn\` copy and a
  leftover SQL 2016 v13 copy) **cannot launch any package**:
  `Could not create DTS.Application` (`0x80040154`, COM class not
  registered). **Still broken — and now known NOT to be a missing-feature
  problem.** SQL Server Setup shows "Integration Services" as already
  installed (greyed/checked under Shared Features), and a full
  uninstall → reinstall of that feature plus two reboots did *not* fix it.
  Presumed stuck/mismatched COM registration from this box having hosted
  both SQL 2016 (v13) and 2025 (v17) shared DTS components. Not worth
  chasing further — **catalog execution works and does not touch this COM
  layer** (see below).
- **An SSISDB catalog now exists** on `.\SQLFORPOC`, with the PoC project
  deployed and running. See "SSISDB catalog" section below. This supersedes
  the earlier note that no catalog existed.

## Commands known to work

```powershell
# build everything (SSIS project + console app together)
& "C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\devenv.com" SSIS.sln /Build Development

# build just the console app (no Visual Studio needed)
dotnet build Scripts\PkgRun.Console\PkgRun.Console.csproj -c Release

# reset target table
sqlcmd -S ".\SQLFORPOC" -E -i Sql\01_CreateTarget.sql

# (re)create the SSISDB Environment + bind project params to it
sqlcmd -S ".\SQLFORPOC" -E -i Sql\02_SetupCatalogEnvironment.sql

# (re)create the ctl control framework in SsisPoC
sqlcmd -S ".\SQLFORPOC" -E -i Sql\03_CreateControlFramework.sql

# deploy the .ispac WITHOUT Visual Studio or the wizard (works from anywhere,
# including Linux/OpenShift, since it is just a proc call over TDS)
$bytes = [IO.File]::ReadAllBytes("SSIS\bin\Development\SSIS.ispac")
# ...pass as @project_stream varbinary(max) to SSISDB.catalog.deploy_project

# build + run the .NET 8 runner (also built by the devenv line above)
dotnet build Scripts\SsisRunner\SsisRunner.csproj -c Release
.\Scripts\SsisRunner\bin\Release\net8.0\SsisRunner.exe --package LOAD_EMPLOYEES --set SourceFileName=Employees.csv --wait
.\Scripts\SsisRunner\bin\Release\net8.0\SsisRunner.exe --manifest Scripts\SsisRunner\manifests\nightly.json --wait
.\Scripts\SsisRunner\bin\Release\net8.0\SsisRunner.exe --status <RunId>

# run with Project.params defaults
.\Scripts\Run-Package.ps1
.\Scripts\PkgRun.Console\bin\Release\PkgRun.Console.exe

# run with overrides
.\Scripts\Run-Package.ps1 -Override @('SourceFolder=...', 'SourceFileName=...', 'TargetConnectionString=...')
.\Scripts\PkgRun.Console\bin\Release\PkgRun.Console.exe --set "SourceFolder=..." --set "TargetConnectionString=..."
```

The two script entry points execute in-process via `Project.OpenProject()` +
`Package.Execute()`. They remain the simplest way to do a quick local run.
Raw local `dtexec` is still broken (COM) — don't suggest it.

**Tool-choice note:** capturing `devenv.com` output through the Bash tool
here has repeatedly returned an empty file even on success (exit 0); the
PowerShell tool with `*> logfile` redirection captured it reliably. Prefer
PowerShell for any `devenv.com`/MSBuild invocation whose output matters.

## SSISDB catalog (set up 2026-08-12 — working)

Current state on `.\SQLFORPOC`:

| Thing | Value |
|---|---|
| Catalog | `SSISDB` (master key password: `Password@2233` — see warning below) |
| Folder | `SSIS PoC` |
| Project | `SSIS` (from `SSIS.dtproj`; packages `LoadEmployees.dtsx`, `LoadReferenceData.dtsx`) |
| Environment | `Dev` — `SourceFolder`, `TargetConnectionString`, `AuditToken` (sensitive) |
| Bindings | `SourceFolder` + `TargetConnectionString` → `'R'`; `SourceFileName` and all 4 package params left at design defaults so callers can override per run; `AuditToken` intentionally unbound (trap 13) |

Verified working (2026-08-12): both packages executed server-side and loaded
12 / 8 / 10 rows, driven end to end from the .NET runner.

> **That master key password is not just a PoC secret — it is the disaster
> recovery key for every sensitive value in the catalog.** SSISDB's DMK is
> encrypted by both the password *and* the instance Service Master Key, so
> SQL Server opens it automatically and you never type it at run time. But
> restore SSISDB onto a *different* instance and the new SMK cannot open it;
> recovery is `OPEN MASTER KEY DECRYPTION BY PASSWORD = '...'` then
> `ALTER MASTER KEY ADD ENCRYPTION BY SERVICE MASTER KEY`. Without the
> password those values are gone permanently. It is written here in plaintext
> in a file that lives in the repo — fine for a throwaway local PoC, wrong
> anywhere else. Move it to a password manager before this goes further.

**Catalog execution is unaffected by the broken `DTS.Application` COM
registration** — confirmed empirically, not assumed. Server-side execution
runs inside SQL Server via CLR stored procedures; it never loads the local
DTS COM layer. So a broken local `dtexec` says nothing about catalog health.

### Creating/managing the catalog from PowerShell

The SSMS GUI path is unavailable here (see trap 8), so catalog admin is
scripted. `Catalog.Create()` needs a **four-part** workaround — each part
was a separate failure, in this order:

```powershell
# 1. Two folders must be probed for dependencies (neither is enough alone)
$folders = @(
  "C:\Program Files\Microsoft SQL Server\170\Shared\MDS5xSMO",  # SMO v17.100 + SqlClient v5
  "C:\Program Files\Microsoft SQL Server\170\DTS\Binn"          # IntegrationServicesEnum
)
# ...register an AppDomain.CurrentDomain.AssemblyResolve handler (compiled via
#    Add-Type -TypeDefinition) that probes those folders by simple assembly name...

# 2. Connection MUST be Microsoft.Data.SqlClient (v5), NOT System.Data.SqlClient
$sqlClientAsm = [Reflection.Assembly]::LoadFrom("$($folders[0])\Microsoft.Data.SqlClient.dll")

# 3. TrustServerCertificate=True is REQUIRED (see trap 10)
"Data Source=.\SQLFORPOC;Integrated Security=SSPI;TrustServerCertificate=True;"

# 4. Server must have CLR enabled BEFORE Catalog.Create() (see trap 11)
sqlcmd -S ".\SQLFORPOC" -E -Q "EXEC sp_configure 'clr enabled', 1; RECONFIGURE;"
```

Once `$is` (an `IntegrationServices` instance) exists, the object model is
ordinary: `$is.Catalogs["SSISDB"]`, `$catalog.Folders`, and
`[Activator]::CreateInstance($catalogFolderType, @($catalog, $name, $desc))`
+ `.Create()` to add a folder.

### Deploying and executing

- **Deploy — three ways, in increasing order of usefulness:**
  1. Visual Studio → right-click SSIS project → *Deploy...*, or
     `ISDeploymentWizard.exe` in `...\170\DTS\Binn\`. Destination path format
     is **`/SSISDB/<folder>/<project>`** (see trap 9). The folder must already
     exist; the wizard will not create it.
  2. **`SSISDB.catalog.deploy_project` — proven working 2026-08-12** and the
     one to reach for. It takes the `.ispac` as `varbinary(max)`, so it is
     just a stored-proc call over TDS: no Visual Studio, no wizard, no
     Windows. That makes it the CI/CD path, including from a Linux/OpenShift
     pipeline. Reading the bytes and calling it from PowerShell:
     ```powershell
     $bytes = [IO.File]::ReadAllBytes("SSIS\bin\Development\SSIS.ispac")
     # System.Data.SqlClient is fine here (no forced encryption, unlike the
     # Microsoft.Data.SqlClient v5 needed for the SMO catalog admin path)
     $cmd.CommandText = "catalog.deploy_project"; $cmd.CommandType = 'StoredProcedure'
     # @folder_name, @project_name, @project_stream (VarBinary,-1), @operation_id OUTPUT
     ```
     Note **building** the `.ispac` still needs Windows + SSDT; only the
     deployment step is portable.
- **Execute:** `catalog.create_execution` → optionally
  `set_execution_parameter_value` → `catalog.start_execution`. Set
  `@object_type=50, @parameter_name='SYNCHRONIZED', @parameter_value=1` to
  make `start_execution` block so status is readable immediately after.
- **Diagnose:** `catalog.executions` (status 4=failed, 7=succeeded) and
  `catalog.operation_messages` (`message_type` 120=error, 110=warning),
  joined on `execution_id = operation_id`.
- A benign warning always appears: *"Could not open global shared memory to
  communicate with performance DLL"*. Cosmetic (perf counters need an
  admin/console session); it does not affect the load.

### Changing parameter values

```sql
-- change a value for all future runs
EXEC SSISDB.catalog.set_environment_variable_value
     @folder_name='SSIS PoC', @environment_name='Dev',
     @variable_name='SourceFolder', @value=N'D:\Some\Other\Path';
```

For a one-run-only override use `catalog.set_execution_parameter_value`
with `@object_type=20` between `create_execution` and `start_execution`.
To add a `Prod` environment, re-run `Sql/02_SetupCatalogEnvironment.sql`'s
steps with a different `@environment_name` and pass that `reference_id`.

## The `ctl` control framework (built 2026-08-12, reworked 2026-08-13 — working)

Lives in **`SsisPoC`**, schema `ctl`, created by
`Sql/03_CreateControlFramework.sql`. Purpose: let an application run one
package or N packages with per-run parameters without knowing any SSIS
vocabulary. The application-facing API is four procedures; two more
(`usp_DispatchRunItems`, `usp_RefreshRunItems`) are internal and shared by
the public ones.

| Object | Role |
|---|---|
| `ctl.PackageRegistry` | logical `PackageKey` → folder/project/package/environment |
| `ctl.Run` / `ctl.RunItem` | run header + one row per package, with the exact params used |
| `ctl.usp_StartPackage` | start **one** package, fire-and-forget — thin wrapper around usp_StartRun |
| `ctl.usp_StartRun` | validate manifest, create+start every execution — **1 round trip** |
| `ctl.usp_PollRun` | status of one run's items + error text — 1 round trip per poll |
| `ctl.usp_PollCorrelation` | status of every item across every run sharing a CorrelationId |

**2026-08-13 rework, in one sentence:** single-package calls became the
primitive (`usp_StartPackage`), `usp_RetryRun` was deleted in favor of
`@RetryOfRunId`/`@LoggingLevel` params on a fresh call, a concurrency guard
was added, and per-item `EnvironmentName` moved off `ctl.PackageRegistry`
onto `ctl.RunItem` (see traps below). `usp_StartRun`'s manifest shape and
`usp_PollRun` are unchanged and still the batch path.

**Adding a package is a registry row, not a code change:**

```sql
INSERT ctl.PackageRegistry (PackageKey, FolderName, ProjectName, PackageName, EnvironmentName)
VALUES ('LOAD_X', N'SSIS PoC', N'SSIS', N'LoadX.dtsx', N'Dev');
```

### Design decisions that are load-bearing (don't "simplify" these away)

- **Executions start asynchronously** — no `SYNCHRONIZED` setting. That is
  what stops a caller's connection being pinned for the package duration and
  lets a killed pod resume by polling the RunId. Adding `SYNCHRONIZED=1`
  would break the whole restart story. (Note it also makes `start_execution`
  *raise* on package failure, which would abort the dispatcher mid-batch.)
- **`object_type` and `data_type` are looked up per parameter** from
  `catalog.object_parameters`, package-level (30) shadowing project-level
  (20) via `ORDER BY object_type DESC`. This is why callers pass a flat
  name/value bag. Hardcoding 20 breaks `LoadReferenceData`.
- **Type casts are `IF`/`ELSE`, not `CASE`** — SQL Server may evaluate every
  branch of a `CASE`, throwing on `CAST('abc' AS int)` in a branch that was
  never selected.
- **Validation runs before anything starts.** Unknown package key → 50003,
  unknown parameter → 50004, package already running (unless
  `@AllowConcurrent=1`) → 50012, all with the offending names. Without this
  a typo becomes a package failure minutes into a run.
- **Each item is dispatched in its own TRY/CATCH** so one package failing to
  start doesn't strand the rest of the batch.
- **The concurrency guard refreshes before it checks.** A `ctl.RunItem` only
  ever leaves `Running` via `usp_RefreshRunItems`, so a run nobody has polled
  since it finished would still read `Running`. The guard in `usp_StartRun`
  therefore calls `usp_RefreshRunItems` on every busy candidate RunId first —
  skip that and a finished-but-unpolled run permanently blocks reuse of that
  PackageKey. Verified empirically: back-to-back starts in one batch were
  correctly rejected; the same call after the earlier run had actually
  finished was correctly allowed.
- **`usp_StartPackage` builds a one-item manifest and calls `usp_StartRun`**
  rather than duplicating validation/dispatch/the concurrency guard. Keep it
  that way — a second copy of any of those is exactly the kind of drift this
  rework was meant to prevent.
- **Per-item `EnvironmentName` lives on `ctl.RunItem`, not
  `ctl.PackageRegistry`.** The original manifest implementation wrote a
  manifest item's environment override back onto the registry row — a
  permanent mutation from what was meant to be a one-off override, so a
  later unrelated call for the same PackageKey would silently inherit it.
  `usp_DispatchRunItems` now resolves `COALESCE(RunItem.EnvironmentName,
  PackageRegistry.EnvironmentName)` per item instead.
- **T-SQL's `STRING_AGG` has no inline `DISTINCT`** (unlike Postgres) — hit
  this writing the concurrency guard's error message. Dedupe in a subquery
  first: `SELECT STRING_AGG(x, ', ') FROM (SELECT DISTINCT ... ) t`.
- **A filtered index needs `QUOTED_IDENTIFIER ON`**, which `sqlcmd` does not
  set by default — `IX_Run_CorrelationId ... WHERE CorrelationId IS NOT
  NULL` failed deployment (`Msg 1934`) until `SET QUOTED_IDENTIFIER ON;`
  was added near the top of the script.

### CorrelationId

Opaque, caller-supplied `nvarchar(100)`, purely for the caller's own
traceability — the framework never reads it back except to group by it. Not
auto-generated server-side; the *client* (`SsisRunner`) generates a fallback
(`ssisrunner-<timestamp>-<random>`) only when neither `--correlation` nor
(for the manifest path) the manifest's own `correlationId` field supplies
one. It matters more now than before the rework: with `usp_StartPackage`,
independent calls have no RunId in common, so an identical `CorrelationId`
passed to each is the *only* way left to group them —
`ctl.usp_PollCorrelation` is what answers "did last night's load succeed"
in that world. Indexed via `IX_Run_CorrelationId` (filtered, `WHERE
CorrelationId IS NOT NULL`).

### Debugging a run

```sql
SELECT * FROM ctl.RunItem WHERE RunId = <n>;            -- exact params + execution_id
EXEC ctl.usp_PollRun @RunId = <n>;                      -- refresh + error text
EXEC ctl.usp_PollCorrelation @CorrelationId = '<id>';   -- refresh + error text, every run sharing it
-- then drop to the catalog for detail:
SELECT * FROM SSISDB.catalog.operation_messages
WHERE operation_id = <execution_id> AND message_type IN (110,120) ORDER BY message_time;
```

`LOGGING_LEVEL` is set per execution (object_type 50) from `@LoggingLevel`,
default 1: pass 3 (Verbose) on a retry call. Measured difference on one small
package: 34 messages at level 1 vs 423–769 at level 3 (varies by run). That
is why verbose is opt-in per call — at 50 packages every night it would
bloat SSISDB fast.

### Client

`Scripts/SsisRunner/` (net8.0, `Microsoft.Data.SqlClient`). Connects to
**`SsisPoC`**, never SSISDB. `--package <key> [--set K=V]...` or
`--manifest <path>`, plus `--status <runId>`; `--wait` opts into blocking
(default is fire-and-forget: prints the RunId and returns). No `--retry` —
use `--retry-of <runId>` on a fresh `--package`/`--manifest` call. Exit 0
all-succeeded (or merely started, without `--wait`), 1 some-failed, 2 runner
error, 3 still-running (`--status` without `--wait` only). In `SSIS.sln`
mapped to `Release|Any CPU` (trap 6 — any real platform label makes devenv
silently skip it); `dotnet build` on the csproj alone works too.

## Traps already hit once — don't re-derive these

1. **`Project.params` has a strict, undocumented-by-XML-validity schema.**
   Property order matters (`ID, CreationName, Description,
   IncludeInDebugDump, Required, Sensitive, Value, DataType`),
   `Required`/`Sensitive` must be `0`/`1` not `False`/`True`, no `Name`
   sub-property. A well-formed but wrong-schema file breaks `devenv /Build`
   with a misleading `LoadStream`/"projects not loaded correctly" error, no
   pointer to the actual file. If this ever needs regenerating, extract a
   canonical copy from inside a working `.ispac` (rename to `.zip` — Windows
   `Expand-Archive` rejects the `.ispac` extension directly) rather than
   hand-writing it.
2. **CSV/connection-manager line-ending mismatch causes silent 0-row runs,
   no error.** The Flat File connection manager's row delimiter is CRLF;
   source files must be saved with `\r\n`, not `\n`.
3. **Unicode/non-unicode mismatch** between Derived Column `DT_WSTR` outputs
   and `VARCHAR` destination columns throws a conversion error at the OLE DB
   destination. Destination columns for any derived string output must be
   `NVARCHAR`.
4. **`dtexec /Par` only works with `/ISServer`** (i.e. only against a
   package deployed to an SSISDB catalog). Local `/Project` mode rejects
   `/Par` outright ("The Parameter option can only be specified with the
   ISServer option."). Separately, dtexec's own parser breaks on any `/Par`
   value containing more than one embedded `;` (confirmed empirically),
   which is fatal for connection strings even under `/ISServer` — needs the
   extra escaped-quote-wrapping shown in README. **A catalog now exists, so
   `/ISServer` is finally testable — but it has not been tried yet**, and
   local `dtexec` is independently broken by the COM issue, which may block
   it regardless. Prefer `catalog.start_execution` from T-SQL; it avoids
   both the parser quirk and the COM problem entirely.
5. **PowerShell + `dtexec` argument breakage on paste** was two compounding
   issues: backtick line-continuation is fragile on copy/paste (trailing
   whitespace after a backtick silently ends the "continuation"), and
   `$Project::Name` inside a double-quoted PowerShell string silently
   expands `$Project` to empty. Fix: single-quoted, splatted arrays
   (`$args = @('...')`; `& $exe @args`), or just avoid dtexec entirely (see
   above).
6. **`devenv /Build` silently skips a newly added C# project** in this
   dtproj-only solution ("`Skipped Build: Project: X`", exit 0, no error)
   when its `ProjectConfigurationPlatforms` mapping targets a real platform
   like `x64`. This solution only declares a custom `Default` platform
   (an SSDT/dtproj quirk), and devenv's solution build manager refuses to
   build a project cross-mapped to `x64` under it. Fix: map the project to
   `Release|Any CPU` instead — actual compiled bitness is still forced to
   x64 by the project's own `PlatformTarget`, unaffected by this
   solution-level label.
7. **Reflection-based `Add-Type -Path` on the SSIS interop DLLs** throws
   `ReflectionTypeLoadException`; load via
   `[System.Reflection.Assembly]::LoadWithPartialName()` from the GAC
   instead. And PowerShell can't directly cast a `System.__ComObject` to
   `MainPipe` — that construction has to happen in compiled C#
   (`Add-Type -TypeDefinition`), not inline PowerShell.
8. **SSMS 22.9.0 does not render the "Integration Services Catalogs" node**
   in Object Explorer on this machine — not before the catalog existed, and
   *still not after* a working SSISDB was created and verified. Connect →
   *Integration Services...* is also greyed out. So the entire GUI path for
   catalog admin (Create Catalog, Deploy Project, Environments, Execute) is
   unavailable; **SSISDB under `Databases` is just the plain database node
   and has only the generic database context menu** — it is not the catalog
   UI. Don't send anyone down the Object Explorer path. Deploy from Visual
   Studio's project right-click → *Deploy...*, and do everything else via
   T-SQL/SMO. Not diagnosed further; likely an SSMS 22 bug and/or fallout
   from the same broken COM registration.
9. **Deployment-wizard destination path is `/SSISDB/<folder>/<project>`** —
   the catalog name is a required *first* segment. Omitting it gives the
   misleading `The path does not exist. The catalog '<yourfolder>' was not
   found.` (it parsed your folder name as the catalog name). Once the
   catalog segment is right, a missing folder gives the clearer `The folder
   'X' was not found in catalog 'SSISDB'` — **the wizard cannot create the
   folder**; create it first via SMO (`CatalogFolder(...).Create()`).
10. **The SMO IntegrationServices stack needs a version-bridging
    `AssemblyResolve` handler.** GAC has `Microsoft.SqlServer.Management.
    IntegrationServices` at `17.0.0.0`, but it binds against
    `ConnectionInfo` / `Management.Sdk.Sfc` at **`17.100.0.0`**, which are
    *not* in the GAC — only in `...\170\Shared\MDS5xSMO\`. That folder's
    copies in turn need **`Microsoft.Data.SqlClient` v5** (not
    `System.Data.SqlClient`), and one more dependency
    (`Management.IntegrationServicesEnum`) lives only in `...\170\DTS\Binn\`.
    Net effect: probe **both** folders by simple assembly name. Symptom if
    you skip it: `Cannot find type [...IntegrationServices]` from
    `New-Object`, or a `ReflectionTypeLoadException` whose real cause is
    only visible in `LoaderExceptions`. Also: `Microsoft.Data.SqlClient` v5
    defaults to `Encrypt=True` with strict validation, so a local
    self-signed cert fails with *"The certificate chain was issued by an
    authority that is not trusted"* — **`TrustServerCertificate=True` is
    mandatory** in the connection string.
11. **`Catalog.Create()` fails until `clr enabled` is 1**, with
    *"Integration Services uses CLR stored procedures. Enable 'clr enabled'
    configuration option."* This is exactly what the SSMS Create Catalog
    dialog's "Enable CLR Integration" checkbox does. Fix:
    `EXEC sp_configure 'clr enabled', 1; RECONFIGURE;`
12. **`.dtsx` and `Project.params` use DIFFERENT type-code enums for the same
    concept.** In a `.dtsx`, `DTS:DataType="8"` means String (the SSIS
    `DT_WSTR` code). In `Project.params` and the `.dtproj` manifest,
    `DataType 18` means String (the .NET `TypeCode` value). Using 18 inside a
    `.dtsx` fails the load with `0xC0011008 "Error loading from XML"` and no
    detail. Package parameters also take **no** `DTS:IncludeInDebugDump`
    attribute (variables do). **Do not hand-guess this XML** — get ground
    truth from the runtime itself:
    ```powershell
    # build the thing in memory, serialize it, read the canonical shape
    $p = New-Object Microsoft.SqlServer.Dts.Runtime.Package
    $par = $p.Parameters.Add("X", [TypeCode]::String); $par.Sensitive = $true
    $p.SaveToXML([ref]$xml, $null)     # do this inside Add-Type -TypeDefinition
    ```
    Equally, to debug a `.dtsx` that won't load, call
    `Package.LoadFromXML(xml, sink)` with a `DefaultEvents` subclass that
    overrides `OnError`. `devenv` swallows the detail; the sink prints the
    exact offending node. This turns a guessing game into a 5-second loop.
13. **A sensitive value can essentially only be a connection-manager
    password.** Two rules combine, and both are enforced at different times:
    - a sensitive *variable* may only map to a sensitive *parameter* —
      `set_object_parameter_value` **accepts** the mismatch and it fails at
      EXECUTION with *"Unable to map an environment variable with the
      sensitive property set to True, to a parameter with the sensitive
      property set to False"*. Binding-time success proves nothing.
    - a sensitive *parameter's* value cannot be read by package logic at all:
      not by a property expression (*"The expression will not be evaluated
      because it contains sensitive parameter variable..."*) and not by an
      Execute SQL Task parameter mapping (*"Accessing value of the parameter
      variable for the sensitive parameter ... is not allowed"*).

    So any parameter consumed via an expression — which is how a connection
    manager normally gets a parameterised connection string — **can never be
    sensitive**. The only consumer SSIS allows is the runtime applying a
    project connection manager's `CM.<name>.Password`, which never passes
    through package code. Marking `Sensitive=1` also makes the build strip
    the value from the `.ispac` entirely (`design_default_value` = NULL).
14. **SSISDB's collation is `Latin1_General_BIN2`**, so every *column-to-column*
    comparison between SSISDB and a normal database fails with *"Cannot
    resolve the collation conflict"*. Add `COLLATE DATABASE_DEFAULT` to the
    SSISDB side. Comparisons against a *variable* need nothing — a variable is
    coercible and adopts the column's collation, which is why only some
    queries break. **`OPENJSON` also emits its `key`/`value` columns as
    `Latin1_General_BIN2`** (JSON keys are case-sensitive by spec), so
    concatenating an OPENJSON key with a local column hits the same error in
    `CONCAT`/`STRING_AGG` — `Cannot resolve collation conflict for string_agg
    operation`.
15. **Environment references and parameter bindings belong to the PROJECT**,
    so they survive both `catalog.delete_environment` and a project redeploy.
    Two consequences: `create_environment_reference` fails on a re-run with
    *"This project already has an reference to the specified environment"*
    (check for it first), and a re-runnable setup script must explicitly
    `catalog.clear_object_parameter_value` for anything it no longer binds —
    otherwise a stale `value_type='R'` binding points at a variable that no
    longer exists and every execution fails at run time.
16. **`internal.environments` uses `environment_name`, not `name`** — unlike
    `internal.folders`, which does use `name`. Only relevant when reading the
    `internal` tables directly to prove sensitive values are encrypted; the
    public `catalog.*` views are consistent.
17. **`<InvariantGlobalization>true</InvariantGlobalization>` breaks
    `Microsoft.Data.SqlClient`** — the process dies at startup with
    `NotSupportedException: Globalization Invariant Mode is not supported`.
    Tempting on a container-targeted console app; don't.

## Not done (only pursue if asked again)

- `dtexec /ISServer` has never been run against the new catalog (see
  trap 4). T-SQL `catalog.start_execution` is the proven path.
- No SQL Agent job wraps the catalog execution.
- Only a `Dev` environment exists — no `Prod`/second environment, so the
  "same package, different values" story is set up but only demonstrated
  with one environment.
- **The `ctl` procedures are unsigned and rely on the caller being sysadmin.**
  They live in `SsisPoC` and call across into `SSISDB`; ownership chaining
  does not cross databases. Production needs certificate module signing (NOT
  `TRUSTWORTHY ON`). This is the single biggest gap between the PoC and a
  deployable framework.
- **No concurrency limit and no per-package timeout.** SSISDB provides
  neither natively. The seam for both is marked in `ctl.usp_RefreshRunItems`;
  with two packages there was nothing to exercise, but 50 packages started at
  once would flatten the server. (A same-package concurrency *guard* — reject
  a second start of one already-`Running` PackageKey — was added in the
  2026-08-13 rework; that is a correctness check, not the missing
  cross-package throughput limiter described here.)
- **A genuinely protected credential is not demonstrated.** Encryption at
  rest and masking are proven (`AuditToken`: ciphertext in
  `internal.environment_variables.sensitive_value`, NULL in the public view),
  but trap 13 means nothing in these packages can legally consume it. Doing
  it properly needs a project-level connection manager on SQL authentication
  with `CM.<name>.Password` bound to a sensitive variable — which needs a SQL
  login and both packages restructured onto a shared project connection
  manager.
- SSISDB retention is still at the 365-day default and the
  `SSIS Server Maintenance Job` has not been checked. A verbose retry
  generated **769 messages vs 34** for one small package, so this matters
  sooner than it looks.
- The root cause of local `dtexec.exe`'s COM registration failure is still
  unresolved: the "missing Integration Services feature" theory was
  **disproven** (feature is installed; uninstall/reinstall + reboots did
  not help). Not worth more effort — catalog execution works without it.
