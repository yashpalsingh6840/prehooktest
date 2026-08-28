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
- **SSISDB catalog** — project deployed to `.\SQLFORPOC_2022`, parameters bound
  to an Environment, executed server-side (see "SSISDB catalog" below)

The first two are in-process wrappers around the same `Scripts/PkgRun.cs`
logic; the third runs inside SQL Server and is independent of them. Both
default to running **both** packages in one invocation. `LoadEmployees`
needs a SQL-auth password that is never persisted to the `.ispac` (see
"Sensitive credential" below), so it fails locally unless supplied
explicitly via `--set CM.CM_TargetDb.Password=<password>` — see "Running
LoadEmployees locally" further down for the override syntax and two traps
hit getting it working end to end. Without that override, pass `--package
LoadReferenceData.dtsx` to skip `LoadEmployees` entirely rather than see it
fail.

## This machine's environment (facts, not guesses — re-verify if stale)

> **The dev box was replaced on 2026-08-21 and every fact in this section
> changed.** What follows is the CURRENT machine (`IRIS-CSG-8214`, SQL Server
> **2022**). The previous box (`IRIS-CSG-7247`, SQL Server 2025) is described
> only where the contrast is load-bearing. See "Machine migration 2026-08-21"
> below for what broke, what it looked like, and what was done about it —
> that section is the one to read before re-deriving anything here.

- SQL Server **2022** Developer Edition (`16.0.1000.6`), named instance
  **`.\SQLFORPOC_2022`** (services `MSSQL$SQLFORPOC_2022`,
  `SQLAgent$SQLFORPOC_2022`, `SQLBrowser`). This is what `Project.params`
  defaults point at. **The instance name is not `.\SQLFORPOC` any more** —
  that was the old box, and everything in the repo was repointed.
- SSIS managed assemblies live in the **GAC**, version **`16.0.0.0`** (SQL
  2022; a stale `13.0.0.0`/SQL 2016 set is also present side by side, which is
  why "newest wins" is the right resolution rule and not "the only one there"),
  PublicKeyToken `89845dcd8080cc91`:
  - `Microsoft.SqlServer.ManagedDTS` — `GAC_MSIL`
  - `Microsoft.SqlServer.DTSPipelineWrap` — `GAC_MSIL`
  - `Microsoft.SqlServer.DTSRuntimeWrap` — `GAC_64` (platform-specific —
    anything referencing it must build/run as **x64**, not AnyCPU/x86)
  - Resolve current paths with:
    `[System.Reflection.Assembly]::LoadWithPartialName('Microsoft.SqlServer.ManagedDTS').Location`
  - **No csproj hardcodes any of this any more.** `Build/Ssis.Gac.props`
    detects the installed version (newest of 17/16/15/14/13 present) and every
    consuming project just imports it — `Scripts/PkgRun.Console`,
    `Ssis.Extract.ObjectModel`, `Ssis.Expression.Oracle`. Pin deliberately with
    `dotnet build -p:SsisGacVersion=13.0.0.0`. A missing GAC gives one clear
    `SSISGAC001` error instead of MSB3245 plus ~30 misleading CS0234
    "the namespace 'Dts' does not exist" errors.
- The **text-file log-provider ProgID suffix is version-specific**:
  `DTS.LogProviderTextFile.8` on SSIS 16.0, `.9` on 17.0, `.3`/`.4` in older
  docs. `Scripts/PkgRun.cs` no longer hardcodes it — `TextFileLogProviderCreationName()`
  asks `Application.LogProviderInfos` at run time. It has to be resolved, not
  guessed: the wrong suffix fails only at RUN time, and that failure counts
  toward `MaximumErrorCount`, flipping an otherwise-successful run to `Failure`.
- VS 2022 Professional at
  `C:\Program Files\Microsoft Visual Studio\2022\Professional\`
  (`devenv.com`, `MSBuild\Current\Bin\amd64\MSBuild.exe`), **with the SSIS
  projects extension installed** (`Common7\IDE\Extensions\Microsoft\SSIS`), so
  `devenv /Build SSIS.sln` builds the `.dtproj` → `.ispac` normally. VS 18
  Community is also present but has no SSIS extension — use the 2022 path.
- **The SMO `IntegrationServices` assemblies moved.** On the old box they were
  in `...\170\Shared\MDS5xSMO` + `...\170\DTS\Binn`; neither exists here. They
  now ship inside the VS SSIS extension:
  `...\Common7\IDE\CommonExtensions\Microsoft\SSIS\170\Binn` (IntegrationServices
  + IntegrationServicesEnum) and `...\SSIS\170\Extensions\SQLCommon`
  (ConnectionInfo, Sdk.Sfc, Smo, Microsoft.Data.SqlClient). Do not hardcode
  these either — `Scripts/New-SsisCatalog.ps1` discovers them (see below).
- `dotnet` CLI has SDKs 9.x/10.x (Core/.NET 5+ only, **no .NET Framework
  SDK** installed) but full `net48` **reference assemblies** are present
  (`C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\
  .NETFramework\v4.8`), so `dotnet build` on an SDK-style `net48` csproj
  works fine even though `dotnet run`/execution of the resulting exe needs
  the classic CLR, not the `dotnet` muxer — run the built `.exe` directly.
- **`DTS.Application` COM now WORKS — the old box's blocker is gone.** On
  `IRIS-CSG-7247` every local `dtexec`/object-model launch died with
  `Could not create DTS.Application` (`0x80040154`, COM class not registered),
  and that was never root-caused (a full uninstall/reinstall of the
  Integration Services feature plus two reboots did not fix it; it was written
  off as stuck registration from that box having hosted both SQL 2016 v13 and
  2025 v17 shared DTS components). On the current box
  `New-Object Microsoft.SqlServer.Dts.Runtime.Application` succeeds and
  `LogProviderInfos` enumerates — verified 2026-08-21. So the old advice
  "never suggest local dtexec, it cannot work here" **no longer applies**, and
  the long-standing "Not done" item about chasing that COM failure is closed by
  the machine change rather than by any fix.
- The SSISDB catalog and every database (`SsisPoC`, `SSISDB`) were on the OLD
  box and **do not exist on this one** until rebuilt — see "Machine migration
  2026-08-21" for the rebuild order. The "SSISDB catalog" section below still
  describes the intended end state and the design; treat its "current state"
  table as the target, not as a live inventory.

## Machine migration 2026-08-21 (laptop replaced: SQL 2025 box → SQL 2022 box)

The dev box was swapped. The reported symptom was "I don't have
`Microsoft.SqlServer.Dts.Runtime` any more", which was **wrong as a diagnosis
but right as an observation** — the assembly was installed and loadable the
whole time, at `16.0.0.0` instead of `17.0.0.0`, and every csproj in the repo
pinned `v4.0_17.0.0.0__89845dcd8080cc91` in a `HintPath`. That produced
`MSB3245: Could not locate the assembly "Microsoft.SqlServer.ManagedDTS"`
followed by ~30 `CS0234: the namespace 'Dts' does not exist`, which reads
exactly like "SSIS isn't installed".

**Lesson worth keeping: a pinned absolute path to a versioned GAC folder is a
time bomb, and the error it produces actively misleads.** Everything that could
be made self-detecting was:

| Was pinned | Now |
|---|---|
| 3× csproj `HintPath` on `v4.0_17.0.0.0` | `Build/Ssis.Gac.props`, probes 17→16→15→14→13 |
| `DTS.LogProviderTextFile.9` in `PkgRun.cs` | resolved from `Application.LogProviderInfos` |
| SMO probe folders in CLAUDE.md prose | `Scripts/New-SsisCatalog.ps1`, discovers them |

**What actually changed on the machine** (all verified, not assumed): instance
`.\SQLFORPOC` → `.\SQLFORPOC_2022`; GAC assemblies `17.0.0.0` → `16.0.0.0`;
log provider `.9` → `.8`; SMO assemblies moved out of `...\170\Shared\MDS5xSMO`
into the VS SSIS extension; `DTS.Application` COM went from permanently broken
to working; all databases gone.

**Rebuild order** (each step verified 2026-08-21):

```powershell
# 0. SQL 2022 setup was missing the Integration Services SHARED FEATURE.
#    Without it: no SSISDB (catalog.Create fails on a missing SSISDBBackup.bak)
#    AND no Data Flow execution at all, even locally -- every package fails with
#    "To run a SSIS package outside of SQL Server Data Tools you must install
#    Standard Edition (64-bit) of Integration Services or higher."
#    Diagnose by absence of ALL THREE: the MsDtsServer160 service,
#    ...\160\DTS\Binn\SSISDBBackup.bak, and ...\160\DTS\Binn\ISServerExec.exe.
#    A present ...\160\DTS\Binn\DTExec.exe proves nothing -- it ships with the
#    VS extension/client tools, so the folder looks installed when it is not.
#    Needs elevation; media was already on disk at C:\SQL2022\Evaluation_ENU.
C:\SQL2022\Evaluation_ENU\SETUP.EXE /ACTION=Install /FEATURES=IS /IACCEPTSQLSERVERLICENSETERMS /QS

sqlcmd -S ".\SQLFORPOC_2022" -E -i Sql\01_CreateTarget.sql          # SsisPoC + tables
sqlcmd -S ".\SQLFORPOC_2022" -E -i Sql\04_CreateLoaderCredential.sql # ssis_loader + GRANTs
sqlcmd -S ".\SQLFORPOC_2022" -E -Q "EXEC sp_configure 'clr enabled', 1; RECONFIGURE;"
.\Scripts\New-SsisCatalog.ps1                                        # SSISDB + 'SSIS PoC' folder
& "C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\devenv.com" SSIS.sln /Build Development
# deploy the .ispac via catalog.deploy_project (see "Deploying and executing")
sqlcmd -S ".\SQLFORPOC_2022" -E -i Sql\02_SetupCatalogEnvironment.sql # Dev env + bindings
sqlcmd -S ".\SQLFORPOC_2022" -E -i Sql\03_CreateControlFramework.sql  # ctl schema
```

`Sql\03` emits four *"The module 'usp_DispatchRunItems' depends on the missing
object 'SSISDB.catalog.…'"* warnings if run before SSISDB exists. Those are
deferred-name-resolution notices, **not** failures — the procs are created and
work once the catalog is there. Order is therefore flexible, but running `02`
before the catalog exists genuinely fails.

## Commands known to work

```powershell
# build everything (SSIS project + console app together)
& "C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\devenv.com" SSIS.sln /Build Development

# build just the console app (no Visual Studio needed)
dotnet build Scripts\PkgRun.Console\PkgRun.Console.csproj -c Release

# reset target table
sqlcmd -S ".\SQLFORPOC_2022" -E -i Sql\01_CreateTarget.sql

# (re)create the SSISDB Environment + bind project params to it
sqlcmd -S ".\SQLFORPOC_2022" -E -i Sql\02_SetupCatalogEnvironment.sql

# (re)create the ctl control framework in SsisPoC
sqlcmd -S ".\SQLFORPOC_2022" -E -i Sql\03_CreateControlFramework.sql

# deploy the .ispac WITHOUT Visual Studio or the wizard (works from anywhere,
# including Linux/OpenShift, since it is just a proc call over TDS)
$bytes = [IO.File]::ReadAllBytes("SSIS\bin\Development\SSIS.ispac")
# ...pass as @project_stream varbinary(max) to SSISDB.catalog.deploy_project

# build + run the .NET 8 runner (also built by the devenv line above)
dotnet build Scripts\SsisRunner\SsisRunner.csproj -c Release
.\Scripts\SsisRunner\bin\Release\net8.0\SsisRunner.exe --package LOAD_EMPLOYEES --set SourceFileName=Employees.csv --wait
.\Scripts\SsisRunner\bin\Release\net8.0\SsisRunner.exe --manifest Scripts\SsisRunner\manifests\nightly.json --wait
.\Scripts\SsisRunner\bin\Release\net8.0\SsisRunner.exe --status <RunId>

# run both packages (the default, as of 2026-08-13 rework) with Project.params defaults
.\Scripts\Run-Package.ps1
.\Scripts\PkgRun.Console\bin\Release\PkgRun.Console.exe

# run just one
.\Scripts\Run-Package.ps1 -PackageName LoadReferenceData.dtsx
.\Scripts\PkgRun.Console\bin\Release\PkgRun.Console.exe --package LoadReferenceData.dtsx

# run with overrides (applied across every package in the batch; matched
# against package scope first, then project scope -- see PkgRun.cs)
.\Scripts\Run-Package.ps1 -Override @('TargetConnectionString=...')
.\Scripts\PkgRun.Console\bin\Release\PkgRun.Console.exe --set "TargetConnectionString=..."

# run LoadEmployees locally too -- needs its connection-manager password
# supplied explicitly, every run (see "Running LoadEmployees locally" below)
.\Scripts\PkgRun.Console\bin\Release\PkgRun.Console.exe --set "CM.CM_TargetDb.Password=<the real ssis_loader password>"
```

The two script entry points execute in-process via `Project.OpenProject()` +
`Package.Execute()` and remain the simplest way to do a quick local run.
**Since the 2026-08-13 logging rework, both default to running BOTH
packages** in one invocation (`LoadEmployees.dtsx` then
`LoadReferenceData.dtsx`), each in its own try/catch so one failing to
launch doesn't stop the other — mirrors `ctl.usp_DispatchRunItems`'s
per-item isolation. **Without an override, `LoadEmployees` still fails
here, by design** — it needs `ssis_loader`'s password, which is a
write-only connection-manager property never persisted to the `.ispac`
(see trap 13), so there is no default to silently fall back on. The
failure (*"Login failed for user 'ssis_loader'"*) is the credential
protection working as intended, not a bug. See the next section for how to
supply the password explicitly and actually run it locally when you have
it. Overriding `TargetConnectionString` no longer affects `LoadEmployees`
either, for the same reason its connection manager stopped reading that
parameter. Raw local `dtexec` is still broken (COM) — don't suggest it.

### Running `LoadEmployees` locally (added 2026-08-13, same day as the credential switch)

`PkgRun.cs`'s override syntax has a second form:
`CM.<ConnectionManagerName>.<PropertyName>=Value` (same `CM.*` naming
SSISDB itself uses for its auto-split connection-manager parameters — see
"Sensitive credential"). It routes to
`pkg.Connections[name].Properties[property].SetValue(cm, value)` instead of
a project/package parameter, which is the same object-model call CLAUDE.md
already documented SSISDB's execution engine makes internally before
`AcquireConnection`. This makes `--set
CM.CM_TargetDb.Password=<real password>` work for a local run — verified
end to end 2026-08-13: `LoadEmployees` loaded 12 rows locally with no
SSISDB involved. Nothing about SSISDB's protection changed: no password is
stored by this file, a run without the override still fails exactly as
documented, and the value is masked (`***`) in console output regardless of
source. Values are still `${var}` free text on the command line/PowerShell
history, same caveat as any CLI secret — fine for this throwaway local PoC,
not a pattern to carry into anything real.

**Two traps hit getting a real end-to-end run working, neither about the
override mechanism itself (which worked correctly on the first try) —
both about drift in supporting state that had nothing to do with today's
code change:**
- **The login was locked out**, not just failing on a wrong password.
  `CHECK_POLICY=ON` (set in `Sql/04_CreateLoaderCredential.sql`) enforces
  Windows-style lockout after repeated bad attempts — every earlier local
  `LoadEmployees` run before this session's fix authenticated with an
  *empty* password (write-only property, never persisted), which was
  apparently enough failed attempts to lock the account. Symptom:
  `Login failed for user 'ssis_loader'` even with the objectively-correct
  password from the SQL script. Diagnose with
  `LOGINPROPERTY('ssis_loader','IsLocked')`; fix with
  `ALTER LOGIN ssis_loader WITH PASSWORD = N'<pwd>' UNLOCK;` — the same
  password, not a new one.
- **`Sql/01_CreateTarget.sql` does `DROP TABLE IF EXISTS dbo.Employee`
  before recreating it**, and `GRANT` is scoped to the specific object, so
  any re-run of that script silently wipes `ssis_loader`'s `SELECT`/
  `INSERT`/`ALTER` grants — the new table is a different securable even
  though it has the same name. Symptom (only after fixing the lockout
  above): login succeeds, then *"The SELECT permission was denied on the
  object 'Employee'"*, same wording as trap 13's original discovery of this
  requirement. Fix: re-run `Sql\04_CreateLoaderCredential.sql` (its GRANTs
  are idempotent) any time `01_CreateTarget.sql` has been re-run since.

**Logging (added 2026-08-13):** every run now wires up 3 of the 4 SSIS
logging channels (see `Scripts/PkgRun.cs` header for the full breakdown of
all four and why the 4th — custom `IDTSLogging` — is deliberately not
wired here). Console output comes from an `IDTSEvents` sink
(`CaptureEvents`); each package additionally gets a native text-file log
provider cascaded to every container (`LoggingOptions` do **not** inherit
from parent to child — confirmed empirically, must walk `IDTSSequence.
Executables` recursively), written to
`SSIS/bin/Development/logs/<PackageName>.log`. That file is the only
channel carrying pipeline-level events (`OnPipelineRowsSent`, `Diagnostic`)
— i.e. real per-component row counts, not just task pass/fail.

Two traps hit wiring this up, both invisible until actually executed
(compiled clean, failed at runtime):
- **The log-provider ProgID suffix is version-specific and undocumented in
  most blog posts/older docs**: this SSIS version (17.0) needs
  `DTS.LogProviderTextFile.9`, not the `.3`/`.4` seen elsewhere. Get ground
  truth from `Application.LogProviderInfos` (enumerate `.CreationName`)
  rather than guessing — same "ask the runtime, don't guess the XML/API"
  lesson as trap 12.
- **`LogProvider.ConfigString` is the NAME of a connection manager, not a
  raw file path.** Passing a path directly fails with *"The connection
  '\<path\>' is not found"* — and that failure itself counts toward the
  package's `MaximumErrorCount`, which silently flipped an otherwise-
  successful `LoadReferenceData` run (8+10 rows genuinely loaded) to
  `Failure`. Reproduced and confirmed before fixing: create a `FILE`
  connection manager (`pkg.Connections.Add("FILE")`), set its
  `ConnectionString` to the actual path, and point `ConfigString` at that
  connection manager's `Name` instead.

**Tool-choice note:** capturing `devenv.com` output through the Bash tool
here has repeatedly returned an empty file even on success (exit 0); the
PowerShell tool with `*> logfile` redirection captured it reliably. Prefer
PowerShell for any `devenv.com`/MSBuild invocation whose output matters.

## SSISDB catalog (set up 2026-08-12 — working)

Current state on `.\SQLFORPOC_2022` (rebuilt from scratch 2026-08-21 - see "Machine migration"):

| Thing | Value |
|---|---|
| Catalog | `SSISDB` (master key password: `Password@2233` — see warning below) |
| Folder | `SSIS PoC` |
| Project | `SSIS` (from `SSIS.dtproj`; packages `LoadEmployees.dtsx`, `LoadReferenceData.dtsx`, **`ScriptTest.dtsx`** — the third came from an in-progress SSDT session and is now in the deployed project) |
| Environment | `Dev` — `SourceFolder`, `TargetConnectionString`, `AuditToken` (sensitive), `TargetDbPassword` (sensitive) |
| Bindings | `SourceFolder` + `TargetConnectionString` → `'R'`; `CM.CM_TargetDb.Password` → `TargetDbPassword` `'R'` on `LoadEmployees.dtsx`; `SourceFileName` and all 4 package params left at design defaults so callers can override per run; `AuditToken` intentionally unbound (trap 13) |

Re-verified end to end on the new box (2026-08-21) after a from-scratch
rebuild: RunId 1 `LOAD_EMPLOYEES` and RunId 2 `LOAD_REFERENCE_DATA` both
`Succeeded` through `ctl.usp_StartPackage` via `SsisRunner --wait`, loading
**12 / 8 / 10** rows — the same counts as the original 2026-08-12 run, and
including the sensitive-credential path (`ssis_loader` authenticated from the
encrypted `TargetDbPassword`, 116-byte ciphertext confirmed in
`internal.environment_variables`).

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
"Data Source=.\SQLFORPOC_2022;Integrated Security=SSPI;TrustServerCertificate=True;"

# 4. Server must have CLR enabled BEFORE Catalog.Create() (see trap 11)
sqlcmd -S ".\SQLFORPOC_2022" -E -Q "EXEC sp_configure 'clr enabled', 1; RECONFIGURE;"
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

## Sensitive credential (built 2026-08-13 — working)

Every other connection in this PoC uses Integrated Security, which has no
password to protect — that's *why* `TargetConnectionString` can never be
sensitive (trap 13) and why `AuditToken`, the one sensitive value that
existed before this, demonstrates encryption/masking with no live consumer.
`LoadEmployees`' `CM_TargetDb` connection manager was switched to SQL Server
Authentication specifically to give the catalog's sensitive-value machinery
something real to protect end to end. `LoadReferenceData` is untouched —
this is a one-package demonstration, not a project-wide credential rework.

| Thing | Value |
|---|---|
| SQL login | `ssis_loader` (`Sql/04_CreateLoaderCredential.sql`) — `SELECT, INSERT, ALTER` on `dbo.Employee` only |
| Connection manager | `LoadEmployees.dtsx` → `CM_TargetDb`, package-scoped, `OLEDB`, static `ConnectionString` with `User ID=ssis_loader;Password=;` (no expression) |
| Auto-split parameter | `CM.CM_TargetDb.Password` — `object_type=30`, `sensitive=1`, `design_default_value` NULL |
| Environment variable | `TargetDbPassword` in the `Dev` environment, `@sensitive=1`, value must match the login's real password — rotate both together |
| Binding | `catalog.set_object_parameter_value @object_type=30, @object_name='LoadEmployees.dtsx', @parameter_name='CM.CM_TargetDb.Password', @parameter_value='TargetDbPassword', @value_type='R'` |

Verified 2026-08-13: `LoadEmployees` ran successfully through `ctl.usp_StartPackage`
authenticating as `ssis_loader` with the password pulled from the encrypted
environment variable; 12 rows loaded; the real password string does not
appear anywhere in `catalog.operation_messages` for that execution.

**How this actually works, since it contradicted an earlier assumption in
this file** — no project-level connection manager or package restructuring
was needed. Every OLEDB connection manager exposes a distinct, write-only
`Password` property in the object model (separate from `ConnectionString`,
confirmed by enumerating `cm.Properties`) — setting it, or simply having a
literal `Password=` in a *static* `ConnectionString` (i.e. not overridden by
a whole-string property expression), makes SSIS persist a
`<DTS:Password Sensitive="1">` node in the `.dtsx` and makes the project
deployment step auto-split it into its own `CM.<name>.Password` parameter at
whatever scope the connection manager lives at — package-scoped worked
exactly the same as project-scoped would have. A connection manager driven
entirely by a whole-string `PropertyExpression` (as `CM_TargetDb` was
before, and as `LoadReferenceData`'s still is) never gets this treatment,
because the expression already owns `ConnectionString` outright — this is
*why* switching required removing the expression, not adding to it.

**The full value-flow, end to end — four stages, only one of which is ours.**
Traced and confirmed empirically 2026-08-13 by querying
`catalog.execution_parameter_values`/`internal.execution_parameter_values`
for the actual successful run; don't re-derive this from scratch next time:

1. **Deploy-time split — Microsoft's code, not ours.** `catalog.deploy_project`
   (the stock proc we call with raw `.ispac` bytes) inspects every OLEDB/
   ADO.NET connection manager in the project and unconditionally creates a
   decomposed parameter set for it: `CM.<name>.ConnectionString` (rewritten
   with any `Password=` stripped out), `.ServerName`, `.InitialCatalog`,
   `.UserName`, `.Password` (`sensitive=1`), etc. — at whatever `object_type`
   the connection manager lives at (30 for package-scoped, as here). This
   happens for *every* OLEDB connection manager, used or not — confirmed:
   `LoadReferenceData`'s `CM_TargetDb` got the same `CM.CM_TargetDb.Password`
   parameter despite never touching it, sitting inert at `value_type='V'`,
   default NULL.
2. **Binding a value — ours, one-time, at setup.** The single
   `catalog.set_object_parameter_value @value_type='R'` call in
   `Sql/02_SetupCatalogEnvironment.sql` (see table above). This is the *only*
   step that requires project-specific code, and it's a design-time binding,
   not a per-run action.
3. **Per-execution resolution — SSISDB's code, not ours.** `ctl.usp_DispatchRunItems`
   (`Sql/03_CreateControlFramework.sql`) calls `catalog.create_execution` with
   `@reference_id` set to the package's environment reference — that's it, no
   `CM.*` awareness anywhere in our T-SQL. `create_execution` itself, given
   that reference, resolves the current value of *every* `value_type='R'`
   parameter bound to that project/package for that environment (`SourceFolder`,
   `TargetConnectionString` on `LoadReferenceData`, `CM.CM_TargetDb.Password`
   on `LoadEmployees` — all three, uniformly) and stages them for the
   execution. Proven directly: RunId 21's manifest only ever named
   `SourceFileName`; `CM.CM_TargetDb.Password` still resolved correctly
   because it rode in via `@reference_id`, never via our parameter loop.
4. **Runtime application — the SSIS execution engine, not ours.** When the
   package actually runs, the engine recognizes the `CM.<ConnectionManagerName>.
   <PropertyName>` name pattern specifically and sets that resolved value onto
   `Connections[<name>].Properties[<property>]` on the live connection manager
   object before `AcquireConnection` — for *any* of the five auto-split
   properties, not just `Password`. The OLE DB provider then builds the actual
   login request from whatever combination of properties the connection
   manager was left with. `ConnectionString` and `Password` are applied as
   independent properties and never merged back into one visible string —
   confirmed by reading back `catalog.execution_parameter_values` for the
   successful run: `ConnectionString` still shows the literal password-free
   design value, while `Password` carried its own separate 116-byte
   ciphertext in `internal.execution_parameter_values`, matching
   `TargetDbPassword`'s own ciphertext size exactly.

**Recipe for protecting another credential later — don't reinvent this:**
1. Give the target connection manager a **static** `ConnectionString` (no
   whole-string `PropertyExpression` — that's what suppresses the auto-split,
   per the paragraph above) containing `User ID=...;Password=;` for SQL auth
   (or just leave `Password` set via the object model for other auth shapes).
2. Rebuild (`devenv /Build`) and redeploy (`catalog.deploy_project`) — confirm
   `CM.<name>.Password` now exists in `catalog.object_parameters`,
   `sensitive=1`, before doing anything else.
3. `catalog.create_environment_variable @sensitive=1` for the real secret.
4. `catalog.set_object_parameter_value @parameter_name='CM.<name>.Password',
   @parameter_value='<env var name>', @value_type='R'` — `@object_type=20`
   with `@object_name` omitted for a project-scoped CM, `@object_type=30`
   with `@object_name='<Package>.dtsx'` for package-scoped (either works
   identically, see above).
5. Nothing else changes. `ctl`/`SsisRunner` need no new code — they already
   resolve every parameter generically by name via `catalog.object_parameters`
   and never special-case `CM.*`; the environment reference already in play
   for that package/environment pulls the new binding in automatically via
   stage 3 above.
6. Remember the one-way trip: once bound, the value cannot be read back by
   us or by package logic (trap 13) — only applied by the runtime. Verify by
   checking ciphertext length in `internal.environment_variables`/
   `internal.execution_parameter_values`, not by trying to print it anywhere.

**Traps hit getting here, beyond what's in the numbered list below:**
- `TRUNCATE TABLE` needs `ALTER` on the table, not just `SELECT`/`INSERT` —
  the package's `SQL_TruncateTarget` task failed with a misleading "Cannot
  find the object 'Employee'" (SQL Server's deliberately vague permission-
  denied phrasing) until `ALTER` was granted.
- Opening the OLE DB Destination's fast-load rowset resolves column metadata
  up front, which needs `SELECT` even though the task only ever inserts —
  `INSERT`-only failed validation with *"The SELECT permission was denied on
  the object 'Employee'"*.
- Editing the connection manager was done via the SSIS object model (per
  trap 12's ethos), not hand-edited XML: `Application.LoadPackage`, clear
  the expression via `cm.Properties["ConnectionString"].SetExpression(cm,
  $null)`, set `cm.ConnectionString`, `Application.SaveToXml`. Reflection
  loaded from the GAC as usual (trap 7).
- The machine's SQL Server instance was already in mixed-mode authentication
  (`SERVERPROPERTY('IsIntegratedSecurityOnly') = 0`) — no auth-mode change
  or service restart was needed, which would otherwise have been the
  riskiest part of this change.
- **This broke local (non-catalog) execution of `LoadEmployees`** —
  `Run-Package.ps1` / `PkgRun.Console.exe` both run it as part of their
  default (both-packages) run and fail with *"Login failed for user
  'ssis_loader'"* unless the caller supplies the password (empty password:
  `Password` is write-only and SSIS never persists it in the `.ispac`).
  Verified, not theoretical. This is inherent, not a bug: those entry
  points run in-process via `Project.OpenProject()`, entirely outside
  SSISDB, and by design there is no default value to fall back on — a
  local run only succeeds if the real password is supplied explicitly
  every time via `--set CM.CM_TargetDb.Password=<password>` (added
  2026-08-13, see "Running LoadEmployees locally"), which sets the
  connection manager's property directly rather than reading anything back
  out of SSISDB. Without that override, pass `--package
  LoadReferenceData.dtsx` to skip it, or drive `LoadEmployees` through
  `ctl.usp_StartPackage`/`SsisRunner` instead.

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

## Gate 3 prep (`Tools/Validation/`, added 2026-08-18 — golden corpus + comparison harness, built before a C# rewrite exists; first real run against the actual rewrite 2026-08-20)

A separate tool from the extractor, `ssis-validate` (`Tools/Validation/Ssis.Validate.Harness/`),
built ahead of need: `Migration-Validation-Plan.md` §5 (gate 3, golden-dataset integration
test) needs a captured golden corpus and a comparison harness, neither of which depend on a
rewrite existing yet. Building both now means gate 3 is "point the harness at the new job"
the moment a C# job does exist, not new engineering at that point.

**The user's own framing, stated explicitly and worth repeating here:** all 50 packages are
migrating to C# jobs, so anything referencing SSISDB/`catalog.*` needs to be considered with
that end state in mind — SSISDB won't exist once a package is retired. This is already how
the tool is split:

- **`CorpusCapture`** — Windows-only, drives `SsisRunner.exe` against this box's live SSISDB.
  Reads the *old* system exactly once per corpus entry, before it goes away. Retires with the
  SSIS packages.
- **`GateRunner`** — zero SSISDB/catalog dependency, ever. Talks only to whatever SQL Server
  connection string and job command it's given. This is gate 3 itself, and it doesn't change
  when the SSIS packages disappear.

**Real corpus captured from real runs** (not fabricated): `LoadEmployees` RunId 26 (0→12 rows
in `dbo.Employee`), `LoadReferenceData` RunId 25 (0→8 `dbo.Department`, 0→10 `dbo.Designation`).
**One real finding along the way:** `catalog.execution_data_statistics` only gets populated at
a verbose enough `LOGGING_LEVEL` — the `ctl` framework's own default (1) writes nothing there;
level 3 does. Confirmed empirically (first capture attempt: empty `StageRowCounts`; re-run at
level 3: populated) rather than assumed from the LOGGING_LEVEL docs. It also logs one row per
BUFFER FLUSH, not one per component pair — `LoadEmployees`'s capture shows two rows per
component pair (a `0`-row end-of-data marker, then the real count).

**No rewrite exists to validate gate 3 against**, so a tiny stand-in job
(`Tools/Validation/selftest/Ssis.Validate.SelfTestJob/`, explicitly NOT a rewrite of
anything) proves the harness's own comparison logic instead — `--mode pass` loads the
corpus's golden output verbatim (harness should report PASS), `--mode fail` deliberately
corrupts one value (harness should report FAIL, naming exactly what differs). Verified against
**a real, freshly-created `mcr.microsoft.com/mssql/server:2022-latest` Linux container** (via
the new `docker-compose.yml`) — not just this box's local `.\SQLFORPOC` — for both packages:
`LoadReferenceData` PASS, `LoadReferenceData` FAIL (correct row/column named), `LoadEmployees`
PASS, container torn down afterward.

**A real gap in the first self-test round was caught and closed**, the same discipline as
everywhere else in this file: the `--mode pass` run copies the corpus's own captured values
verbatim, so an excluded (non-deterministic) column like `LoadedAtUtc` is identical between
expected and actual *by construction* — that run never actually exercised the EXCLUSION path,
only "everything matches." Closed with a focused unit suite
(`tests/Ssis.Validate.Harness.Tests/SnapshotDifferTests.cs`, no database needed) asserting the
differ still passes when an excluded column genuinely differs, and fails when the same
scenario's column is NOT excluded — so the exclusion test actually proves the exclusion.

**A real environment bug was caught wiring up the container, not shipped:** on a brand-new
container the target database doesn't exist yet, so a connection string that already names
it as the initial catalog fails to open at all, before the schema script's own
`CREATE DATABASE`/`USE` batches run. Fixed by having `GateRunner` connect at the server level
(`master`) for the schema-restore step specifically.

Config format is JSON, not the YAML the plan's prose illustrates — this repo has no YAML
dependency anywhere, and JSON already matches every other generated artifact
(`spec.json`, conformance rules, the non-determinism manifest itself). A deliberate,
documented deviation, not a different design. `ExcludedColumns` is generated automatically
from `ssisx report`'s `nondeterministic.json`; `KeyColumn` was originally left for a human to
fill in every time — **closed 2026-08-19 by primary-key detection in the extractor**, see the
Phase 0 extractor section below. Both PoC configs, hand-filled in an earlier session, were
independently reproduced byte-for-byte by regenerating with `--primary-keys-json` — the new
heuristic agrees with the human review exactly, on this PoC.

Full account, every command, and the honest-limits list (per-stage row counts captured but
not yet asserted against; `KeyColumn` auto-fill is a heuristic, not a fact, and a composite/
low-confidence candidate still needs a human; only two corpus entries exist) in
`Tools/Validation/docs/gate3-harness.md` and `Tools/Validation/README.md`.

### The C# rewrite (`D:\PoC\SSIS_Migration`, its own separate repo) and the first real gate 3 pass (2026-08-20)

A C# rewrite now exists — **not inside this repo**, at `D:\PoC\SSIS_Migration` (its own git
repo, one commit `93bf360` as of this note). `net10.0`, three projects (`Etl.Core` shared
library, `LoadEmployees`, `LoadReferenceData` console apps), `Host.CreateApplicationBuilder`
for config (appsettings.json → env vars → command line → **User Secrets, added last, see the
precedence gotcha below**), EF Core against the *same* `Sql/01_CreateTarget.sql` this repo
already uses (no independent schema/migrations). Exit codes mirror SSIS's: 0
success/1 load failed/2 config invalid. Root-level docs there (`MIGRATION_SUMMARY.md`,
`VALIDATION_STRATEGY.md`, etc.) are earlier planning scaffolding, mostly aspirational — the
actual `src/` code is more complete/precise than the plans (real EF Core mappings, a
transactional truncate+load — a deliberate improvement over SSIS's separate auto-commit — and
width-guard truncation checks matching the original Flat File Source columns exactly). No test
project exists there.

**Pointing `Tools/Validation`'s gate 3 harness at it, for real, produced two genuine PASSes**
against a freshly created container — full account and exact commands in
`Tools/Validation/docs/gate3-harness.md`'s own "first real gate 3 run" section, one fact worth
repeating here because it generalizes beyond this PoC: **`Host.CreateApplicationBuilder(args)`
already wires appsettings → env vars → command line internally; calling `AddUserSecrets`
afterward (as `EtlHost.Create` does) makes secrets the highest-precedence source of all** —
confirmed with an isolated scratch probe (no database touched), not assumed. So `LoadEmployees`'
stored `TargetDatabase:Password` secret can never be overridden via `--job-arg`/env var; gate 3
works around this by re-applying `Sql/04_CreateLoaderCredential.sql` (now
`PackageConfig.AdditionalSchemaScripts`, new field) against the container so its `ssis_loader`
login matches the same password already in the secret, rather than fighting the precedence.
A second, unrelated real bug (`GateRunner` not setting the job process's working directory,
which silently broke the job's own `appsettings.json` lookup and surfaced as an
unrelated-looking Windows-auth failure) was also caught and fixed the same session.

## Phase 0 extractor (`Tools/SsisExtractor/`, started 2026-08-13 — slice 5 complete, minus `pull`; synthetic component-coverage pass + slice 6's object-model oracle added 2026-08-14; Script Task AND Script Component source extraction — plan §4.5's both halves — plus `ssisx conformance` (migration-validation gate 1) added 2026-08-17; `Ssis.Runtime.Expressions` + `ssisx testgen` (migration-validation gate 2) added 2026-08-18; primary-key candidate detection added 2026-08-19; parallel-control-flow/direct-copy/error-routing synthetic fixture + client-facing portfolio survey added 2026-08-25; `ssisx generate` — SSIS-to-C# code generator, Phases 0-4 — built 2026-08-25, Gate 3 verified against its own output + nested-container support added 2026-08-26; portfolio survey now reports `ssisx generate` readiness too, added 2026-08-26; non-preload Execute SQL Task, OLE DB Source, and full-cache Lookup support added 2026-08-26, each Gate-3-or-equivalent verified; Conditional Split multi-destination routing — a new Etl.Core row-router abstraction plus condition translation — added 2026-08-26, Gate 3 verified; tested against a real 5-package/~30-component third-party portfolio (`SSIS_From_Sandeep`) 2026-08-27, one generation-gap classification bug fixed, Conditional Split multi-hop Union-All-remerge support added and end-to-end verified, File System Task pre-load/post-flow support added -- touching a new Etl.Core abstraction in the sibling SSIS_Rewrite repo -- and end-to-end verified; ADO NET Source/Destination support added, verified against the real package though not via a dedicated fixture -- an SSIS-object-model ADO.NET connection-manager creation issue in this environment blocked one, documented rather than resolved; FINDSTRING/TRIM support added to ExpressionTranslator 2026-08-27, closing a real router-level SsisFn-tracking bug along the way, verified against the real package and a dedicated fixture end-to-end; ternary and unary-negate support added to ExpressionTranslator 2026-08-27, same session, both already oracle-verified so no new semantics work needed, verified against the real package (gap reasons sharpened across two re-generations) and a dedicated fixture end-to-end; numeric '+' dispatch plus a computed SUBSTRING start argument added 2026-08-27, closing CustomerEnriched.EmailDomain fully -- verified with a fixture that reproduces the real package's own expression verbatim, run end-to-end; DATEDIFF("dd",...) support added 2026-08-27 with a new Ssis.Expression.Oracle corpus block (the first genuinely new semantics derivation this session, not just plumbing), closing CustomerEnriched.TenureDays fully -- found and named a new, separate, unfixed gap along the way (no SQL row/entity emitter tracks column nullability, so ISNULL on a nullable value-typed column fails to compile); nullable value-typed column support added 2026-08-27, same session -- an evidence-based minimal version (infer nullable from ISNULL(x) usage, unwrap with .Value everywhere else, a real named tradeoff vs. full null-flow analysis) scoped to SQL sources/Derived Columns only, verified end-to-end with a genuine NULL row against .\SQLFORPOC_2022; Data Conversion component support added 2026-08-28, first for SQL-source/single-destination flows only (a real dtexec probe run first to measure IgnoreFailure's actual behavior -- never drops a row, nulls the one failing column -- then LineageBuilder extended with a new "DataConversion" edge kind, TransformEmitter wired to emit SsisFn.ToNullableI4/ToNullableDate against the raw source column, and a real, previously-latent SqlRowEmitter/SqlRowReaderEmitter bug fixed along the way), then same day extended to the Conditional-Split-cross-reference case that round's own closing claim had missed -- RouterEmitter and a Derived-Column-referencing-a-Data-Conversion-column path both wired to the same TranslateDataConversion resolution, retiring both "Conditional Split condition nullability" and "CSV-source nullability" as gaps (the latter turned out not to exist at all for this feature), fixing two more real bugs found only by building/running the real package's whole regenerated solution for the first time (a missing nullableColumnNames thread into EntityEmitter, and a Roslyn nullable-flow-analysis CS8629 needing a targeted null-forgiving `!`), and closing a third, previously-SILENT PackagePlanner gap (a multi-source Merge Join flow used to generate broken code with zero reported gaps) -- `DFT_DerivedAndSplit` now closes fully end-to-end, verified against both the real package (0 warnings/0 errors across its whole generated solution) and a new dedicated fixture run against `.\SQLFORPOC_2022`; `EntityEmitter`'s own string-nullability exclusion fixed 2026-08-28, mirroring `SqlRowEmitter`'s earlier fix, verified with a unit test rather than a new fixture since it's mechanical parity with an already-proven fix; Flat File Destination support added 2026-08-28 -- a new Etl.Core FlatFileBulkSink write-side abstraction, a codegen bypass for the whole EF Core/DbContext path (a flat file has no table), and a narrow direct-copy exemption scoped to this destination type only -- closing RBC_Demo_ETL's own DFT_ExportDelimited/DFT_ExportFixedWidth fully (0 warnings/0 errors building the real package's whole regenerated solution) plus a dedicated fixture run end-to-end against `.\SQLFORPOC_2022` that empirically confirmed real fixed-width padding/truncation behavior, fixing two more real bugs along the way (BuildSqlFlowSource's server/database check wrongly assumed every destination has one, and a "zero SQL tables in the whole package" case that left Program.cs referencing a DbContext type that was never generated); Sort + Merge Join support added 2026-08-28 -- a new Etl.Core MergeJoinRowSource abstraction (Merge Join modeled as an IRowSource, not a new step type, so it needed zero changes to DataFlowStep), a corrected-after-one-wrong-reading SortPayload (key designation lives on Sort's own INPUT columns, not output), and a hand-walked (not LineageBuilder-based, which would be ambiguous here) backward resolution from Merge Join's own output through Sort to each side's real source -- closing RBC_Demo_ETL's own DFT_SortAndMergeJoin fully (0 warnings/0 errors building the real package's whole regenerated solution), with the join algorithm itself verified via 5 genuine unit tests since a dedicated dtexec-runnable fixture hit an unresolved SSIS object-model quirk (Microsoft.MergeJoin auto-generating extra output columns, documented rather than resolved))

### Portfolio survey now reports generation readiness (2026-08-26)

`ssisx report`'s portfolio digest (`Tools/SsisExtractor/src/Ssis.Extract.Cli/PortfolioDigest.cs`)
answered complexity/findings/coverage questions but had nothing to say about the actual
migration-sizing question: *how much of this portfolio can `ssisx generate` produce today?*
Closed by calling `PackageGenerator.Generate` per package inside `ReportCommand` — pure,
in-memory, no files written (`ssisx generate` itself is still what a caller runs for the real
output) — and folding the resulting `GenerationGap`s into the digest (console/markdown/JSON)
and a new `generation-readiness.md`. No `Run-Survey.ps1` changes needed: it already wraps
`ssisx report`, so this flows through automatically.

**One classification decision, discovered by actually running it, not designed up front:**
`PackageGenerator` reports a `{Package}.Notification` gap unconditionally for every package
with at least one flow ("a .dtsx carries no notification-recipient information") — no real
`.dtsx` could ever satisfy it, and the generated project still compiles and runs fine with
empty recipient lists. Counting it as blocking made "0 gaps" permanently unreachable: the
first run against this PoC's own 4 packages showed **0/4 generatable**, including the two real,
fully-supported packages. `PortfolioDigest.IsBlockingGap` excludes it from the headline
"generatable" count (still listed per-package, tagged non-blocking, in `generation-readiness.md`)
— re-run, this PoC now correctly reads **2/4 generatable** (`LoadEmployees`/`LoadReferenceData`;
`ScriptTest`/`SyntheticParallelShapes` correctly still blocked on Script Task / direct-copy).

**A second, unrelated real bug was caught building this, pre-existing and not something this
change introduced:** every tuple-typed field in `PortfolioDigest.Digest` (`List<(string Key,
int Count)>`, including the pre-existing `Packages` list) silently serialized to
`portfolio-digest.json` as a list of empty `{}` objects — `System.Text.Json` only serializes
PROPERTIES by default, and a `ValueTuple`'s `Key`/`Count` are public FIELDS. Invisible in the
console/markdown output (both render via direct string interpolation over the tuples, never
through JSON), only caught by actually reading the generated JSON file. Fixed by replacing
every tuple field with a named record (`KeyCount`, `PackageDigestRow`) — this JSON file is one
of the two artifacts this tool's whole design intends to leave a locked-down client site, so a
silently empty export defeated the entire point.

### `ssisx generate` (built 2026-08-25/26 — a new command, not covered elsewhere in this file)

Turns a package's already-extracted spec into a runnable C# ETL project (`Ssis.Extract.Codegen`,
8 pure emitters + `PackagePlanner`/`PackageGenerator` orchestration, wired into
`ssisx generate --input <dir|.dtsx> --out <dir> [--recursive]`), targeting the hand-written
`Etl.Core` skeleton at `D:\PoC\SSIS_Rewrite` (a sibling repo, never hardcoded into the
generator itself — portability to a real client site with no such repo was a hard requirement
throughout). Full design, all 5 build phases, and the 4-layer validation strategy (golden
files, differential expression execution against the oracle, diff vs. hand-written, compile +
Gate 3) are recorded in this session's own plan
(`C:\Users\yashpal.singh01\.claude\plans\peppy-tumbling-tome.md`) and in this repo's agent
memory under `ssis-generator-phase0-pause` —**not duplicated here**; this section is only the
pointer plus the two facts most likely to matter to a future session picking this back up:

- **Gate 3 (`Tools/Validation/Ssis.Validate.Harness`) has been run against `ssisx generate`'s
  own output, not just the hand-written rewrite.** Both real PoC packages PASS end to end
  (8/10/12 rows, exact match against the golden corpus) when generated, built against a copy
  of `Etl.Core`, and pointed at `.\SQLFORPOC_2022` — proof, not just a clean `dotnet build`.
- **Nested Sequence Containers are now supported (2026-08-26), closing the "root-level only"
  limitation `PackagePlanner`'s own doc comment used to state.** Verified via an isolated
  object-model probe first (confirmed `STOCK:SEQUENCE`'s real saved-XML shape before writing
  any planner code against it, same discipline as trap 12) and a new synthetic fixture,
  `tests/Ssis.Extract.Tests/Fixtures/SyntheticNestedContainer.dtsx` (a Sequence Container
  wrapping an Execute SQL Task + Data Flow Task, plus a second root-level Data Flow Task after
  it) — the fixture deliberately uses a Derived Column (`LoadedAtUtc <- GETUTCDATE()`, the same
  shape both real packages already use) rather than a direct copy, since a direct-copy
  pipeline hits a separate, already-known, unrelated generator gap that would have masked
  whether nesting itself worked. Generating and building this fixture end to end (`dotnet
  build`, 0 warnings/errors against `TreatWarningsAsErrors=true`) is what actually proves it,
  not just the unit tests. A ForEach/For Loop container (`STOCK:FOREACHLOOP`) is deliberately
  NOT walked the same way — it still reports its own explicit gap, since `Etl.Core` has no
  per-iteration loop abstraction and silently flattening its children would generate code that
  runs them once each instead of per iteration (wrong, not just incomplete).

### Three new component types (2026-08-26): non-preload Execute SQL Task, OLE DB Source, full-cache Lookup

Conditional Split was deliberately deferred — asked the user first, since it needs a genuinely
new multi-sink/row-router abstraction in `Etl.Core` (`DataFlowStep`/`IBulkSink` are one-source/
one-sink by construction), a real architectural decision, not just new emitter code. The other
three were scoped and built this round, each Gate-3-verified (build + run against
`.\SQLFORPOC_2022`, not just a clean `dotnet build`) except Lookup, which by design never
reaches a runnable `Program.cs` at all (see below) — nothing to Gate 3 there.

**Execute SQL Task after a Data Flow Task.** `PackagePlanner` used to route *every* Execute SQL
Task into `PreLoadStatements` unconditionally, regardless of position — the exact gap
`SyntheticParallelShapes.dtsx`'s own Branch 1 (`SQL_CreateLoadATemp → DFT_LoadA →
SQL_UpdateLoadA`) was built to make concrete. Fixed with a new `PackageStep`/`FlowStep`/`SqlStep`
sequence (`PackagePlan.Steps`, alongside the unchanged `Flows`) and a real correctness fix along
the way: **a naive single global "have we seen any flow yet" flag was tried first and produced a
wrong answer on that very fixture** — `SQL_CreateLoadATemp` has no precedence relationship at
all to the package's other two independent top-level branches (`DFT_DirectCopy`/
`DFT_ErrorRouting`), but the flag still misclassified it as post-flow purely because those two
unrelated branches happened to sort earlier in the merged topological order. Caught by actually
running the planner against the real fixture and reading the output (`PreLoadStatements` came
back empty), not assumed correct from the design. Replaced with real precedence-constraint-based
reachability: a per-container predecessor map (`ExecutableSpec.PrecedenceConstraints`/
`PackageSpec.PrecedenceConstraints`, never walked before this) computed in one forward pass over
`dag.TopologicalOrder`, correctly scoped to actual ordering edges instead of declaration order. A
Sequence Container's own "did a flow happen anywhere inside me" result propagates to its
siblings in the parent, the same way a bare Data Flow Task would. `Etl.Core.Pipeline.
ExecuteSqlStep : ILoadTask` needed no interface change (`ILoadTask.RunAsync(uow, load, ct)` was
already generic enough) and runs inside the same transaction as everything else, unlike a
pre-load statement. **No new fixture for the planner-level fix** — `SyntheticParallelShapes.dtsx`
already had the right shape — but Gate 3 needed one: that fixture's own post-flow Execute SQL
Task follows a Data Flow Task with no Derived Column (a separate, unrelated, already-known gap),
so it never reaches a wired `Program.cs`. Built a new, minimal, dedicated fixture instead
(`tests/Ssis.Extract.Tests/Fixtures/SyntheticPostFlowSql.dtsx`: `SQL_PreLoad → DFT_Load
(Flat File Source → Derived Column → OLE DB Destination) → SQL_PostLoad`) — generated, built
0 warnings/0 errors against a copy of `Etl.Core`, and actually run against `.\SQLFORPOC_2022`:
loaded 3 rows lowercase, `SQL_PostLoad`'s own `UPDATE ... SET Name = UPPER(Name)` genuinely ran
afterward, confirmed by reading the table back (`ALICE`/`BOB`/`CAROL`).

**OLE DB Source.** `DataFlowPlan` gained an `OleDbSource` field alongside `FlatFileSource` (no
new enum — same side-by-side-nullable-fields shape the record already used).  Two new emitters,
`SqlRowEmitter`/`SqlRowReaderEmitter` (the SQL-source mirror of `CsvRowEmitter`/
`ClassMapEmitter`), name row-type properties from the pipeline BUFFER's own output-column name
(`PipelineResolver.Resolve(PipelineOutputSpec)`, not the destination-input overload
`EntityEmitter` uses) — this is what `TransformEmitter`'s `row.ColumnName` passthrough
references downstream, so it has to match. `SqlRowReaderEmitter` reads every column by name
(`reader.GetOrdinal(name)`), never by position. `PackageGenerator.BuildSqlFlowSource` handles the
two evidenced `OleDbSourcePayload.AccessMode` values differently: `0` (OpenRowset) — the
generator BUILDS the SELECT itself (`SELECT [Ext] AS [Comp], ... FROM {OpenRowset}`), guaranteeing
every `GetOrdinal` call resolves; `2` (SqlCommand) — the author's own SQL is emitted verbatim
(this tool never rewrites recorded SQL text) plus a non-fatal `GenerationGap` stating the
generated code ASSUMES the result set's column names/aliases already match the buffer's own
column names — an honest, stated assumption, not a silent guess. A source targeting a different
server/database than its own flow's destination is a fatal gap (no second connection is wired
up) rather than an invented, unevidenced config shape. New `Etl.Core` types:
`Data/SqlRowSource.cs` (`IRowSource<TRow>`, owns its own `SqlConnection` — `IRowSource.ReadAsync`
gets no `IUnitOfWork`/connection to reuse, same separation a real OLE DB Source's read
connection has from the destination's transactional one) and `Data/SqlSourceOptions.cs`. New
fixture, `tests/Ssis.Extract.Tests/Fixtures/SyntheticOleDbSourceTransform.dtsx` (OLE DB Source,
SqlCommand mode → Derived Column → OLE DB Destination) — hit one real, unrelated, pre-existing
limitation building it: the destination table's key column was named `OrderID`, and
`PrimaryKeyInference`'s naming-convention heuristic only matches `"<table>ID"` or the fallback
`"ID"`, so EF Core refused to build the model at RUN time (`dotnet build` was clean) with
"requires a primary key to be defined." Fixed by renaming the fixture's own column to `ID`, not
by touching the heuristic — a real client table either already follows this convention (as both
this PoC's own tables do) or gets a documented `Unknown`-confidence gap for a human to fill in,
same as always. Verified end to end: generated, built 0 warnings/errors, ran against
`.\SQLFORPOC_2022`, read 3 real rows via `SqlRowSource<TRow>` over an actual SQL query, computed
the Derived Column audit stamp, bulk-inserted — confirmed by reading the target table back.

**Lookup, full-cache only — the central design decision was what NOT to guess.** Confirmed
(again, via a real object-model round-trip, documented under "Synthetic component-coverage
fixtures" below) that SSIS does not persist a Lookup's join key anywhere in the saved `.dtsx` at
all. A guessed join key would compile, run, and silently produce wrong joined data on every row
— exactly the failure category "gaps not guesses" exists to prevent, and strictly worse than not
generating the flow. A `NotImplementedException`-stub `Map()` was considered and also rejected
for the same reason: it ships a `Program.cs` that LOOKS complete and only fails the first time it
actually runs. **What's generated instead:** everything that's 100% derivable and independently
compiling — a new `LookupCacheEmitter` produces `Mapping/{Lookup}Cache.cs`, a `ReferenceRow` type
(from `LookupPayload.ReferenceColumns`, read by POSITION since the SQL's own SELECT list order is
the only ordering this tool has evidence for) plus a generic `LoadAsync<TKey>(connectionString,
keySelector, ct)` that runs the Lookup's own `SqlCommand` verbatim — deferring "what's the key"
entirely to hand-written caller code, so the scaffold needs zero guessed information. This flow's
`TransformEmitter`/`Program.cs` wiring are deliberately NOT generated (extends the existing
"no Derived Column found → flow not wired" precedent to a new trigger), with one clear
flow-level `GenerationGap` explaining exactly what a human needs to do to finish it by hand. A
`CacheType` other than full cache (`0`) tightens the gap instead of emitting a mismatched
scaffold — no per-row query semantics are generated. **No new fixture needed** —
`tests/Ssis.Extract.Tests/Fixtures/SyntheticLookupSplit.dtsx` (already checked in: OLE DB Source
→ Lookup → Conditional Split → 3 destinations) already exercises this correctly: the flow reaches
`PackageGenerator`, hits the Lookup gate, and is skipped; Conditional Split's mere presence causes
no separate gap since nothing inspects `ConditionalSplitPayload` yet. **No Gate 3 run either, by
design** — nothing is wired into a runnable `Program.cs`, so there's nothing to execute end to
end; verified instead via unit tests plus a one-time throwaway `dotnet build` of the generated
`LookupCache.cs` against the real `Microsoft.Data.SqlClient` package (not just a Roslyn syntax
check), confirming it compiles against real types.

Full extractor suite after all three: same 3 pre-existing unrelated failures (see
[[ssis-poc-unrelated-inprogress-files]]), 54/54 in `Ssis.Extract.Codegen.Tests` (was 44),
nothing regressed. `Etl.Core.Tests`/`LoadEmployees.Tests`/`LoadReferenceData.Tests` (sibling
`SSIS_Rewrite` repo) also all green after the three new `Etl.Core` files (`ExecuteSqlStep`,
`SqlRowSource`, `SqlSourceOptions`).

**2026-08-25 — a fourth synthetic fixture (`SyntheticParallelShapes.dtsx`, real parallel
control flow + a direct-copy pipeline + Flat File error-output routing through a Script
Component) plus a client-facing portfolio survey (`Tools/SsisExtractor/scripts/`).** The
fixture is checked into `SSIS/` itself (not `tests/.../Fixtures/`, unlike the three earlier
ones) so it shows up in SSDT's Solution Explorer and actually builds/executes — full gotcha
catalog (six of them: `ProvideComponentProperties()` resets `.Name`, `DelayValidation`
needed for an uncompiled Script Component, `ProtectionLevel` must match the project
explicitly) in `Tools/SsisExtractor/docs/report-schema.md`'s "Synthetic parallel-shapes
fixture" section. The survey — `Run-Survey.ps1`/`.bat` + `Publish-Survey.ps1`, full usage in
`Tools/SsisExtractor/README.md`'s "Portfolio survey" section — is what actually leaves this
repo for a client's own machine; do not rediscover its self-contained-single-file publish
requirement from scratch, it's documented there.

**2026-08-19 — primary-key candidate detection, closing a gate-3 gap.** `ssisx report` now
writes `primary-keys.json`/`.md` (`Ssis.Extract.Dtsx.PrimaryKeyInference`), a best-effort
guess per OLE DB Destination, and folds the same candidate into `ssisx conformance`'s
`TargetSchema` rule evidence. Built because it directly closes a named gap in
`Tools/Validation/docs/gate3-harness.md` ("`KeyColumn` is hand-filled, not detected"), not
speculatively.

**Confirmed, before writing a line of code, that a `.dtsx` genuinely carries no primary-key
concept anywhere** — read this PoC's own real `<externalMetadataColumn>` elements directly
(`SSIS/LoadEmployees.dtsx`) and found only `dataType`/`length`/`precision`/`scale`/
`codePage`, nothing about keys/nullability/identity. So the only signals available without a
live database (an `enrich`/`pull` concern, deliberately out of scope, plan §11 decision 4)
are a naming convention (`"<table>ID"`, fallback `"ID"`) plus two guards: the column must be
an integer pipeline type, and it must NOT be a computed value — traced via the same lineageId
join `LineageBuilder` already uses, specifically so a derived business key (`EmployeeKey`,
`DepartmentKey`, `DesignationKey` — each a string concatenation) is never mistaken for the
real one. Every candidate carries its `Confidence` and is reported even when it's
`"Unknown"` (no match), so a human knows to fill it in rather than assuming it was
overlooked.

**Verified against live ground truth, not just plausible-looking:** all three real PoC
candidates were cross-checked against `.\SQLFORPOC`'s actual `PRIMARY KEY` constraints via
`sys.indexes`/`sys.index_columns` — `Employee.EmployeeID`, `Department.DepartmentID`,
`Designation.DesignationID`, exact matches, all three. Feeding the output into
`Tools/Validation`'s `ConfigGenerator` (new `--primary-keys-json` flag) reproduced the two PoC
configs' `KeyColumn` values byte-for-byte against what a human had already hand-filled in an
earlier session — confirmed with a plain `diff`, zero differences.

**A real bug was caught before it shipped, not after:** the synthetic Lookup/Conditional
Split fixture has three OLE DB Destinations all left at SSIS's default component name ("OLE
DB Destination") — never renamed, matching the plan's own worked example. Joining a
destination's inferred candidate back to it by `(DataFlowTaskPath, Name)` collided across
those three and crashed `ssisx conformance` with a duplicate-dictionary-key exception,
caught immediately by the existing `ConformanceTests` suite (not a test written for this
feature). Fixed by joining on the destination's own `RefId` instead — always unique, the same
identity every rule ID in this tool already relies on. Now pinned by its own regression test.

A separate tool (`ssisx`), planned in `Phase0-Extractor-Plan.md` (also see
`Migration-Validation-Plan.md` and `PoC-Architecture-Overview.md` for the
surrounding phases). Turns a folder of `.dtsx`/`.dtproj` files into
deterministic, redacted, lossless spec JSON by parsing the raw XML directly
— deliberately **not** the SSIS object model (plan §2.1: version tolerance,
no GAC/Windows dependency, works on packages built by any SSIS version).
Session-agreed decisions (plan §11, all defaults accepted): lives inside
this repo at `Tools/SsisExtractor/` rather than a separate repo; build
straight through slices 1–4 then stop and reassess with real client
packages before slices 5–6; `pull`/`enrich` (catalog-backed, slice 6)
treated as unavailable/low-priority for now — no client SSISDB access.

**Full detail (build/run/test commands, JSON contract, gotchas already hit)
is in `Tools/SsisExtractor/README.md` and `Tools/SsisExtractor/docs/spec-schema.md`
— this section is only the pointer + the one fact worth surfacing here.**

Own solution (`SsisExtractor.slnx` — this SDK's `dotnet new sln` emits the
newer XML `.slnx` format, not `.sln`; `dotnet build`/`dotnet test` work on
either), zero coupling to `SSIS.sln`/this PoC's own build. Verified
end-to-end 2026-08-13: `ssisx extract --input SSIS --out <dir>` against
both this PoC's packages produces byte-identical output across repeated
runs (aside from `_meta.json`'s timestamp), and golden-file tests
(`tests/Ssis.Extract.Tests/`) pass against the PoC's actual `.dtsx` files,
referenced by relative path rather than copied — so those tests double as
an unplanned drift check on this PoC's own packages, a useful side effect
worth knowing about before wondering why an extractor test broke.

**Slice 2 (same day):** recursive control-flow tree, `Microsoft.ExecuteSQLTask`
payloads (SQL text, connection resolved by name from the task's DTSID
reference against the package's own connection managers), a derived DAG per
container (topological order + a longest-path "parallel level" grouping —
sound but deliberately incomplete, documented in `ControlFlowDagSpec`'s doc
comment), and the coverage-percentage metric (plan §7.2) wired for real,
including a working `--fail-under` that exits 3. Both PoC packages currently
score **~19–20% coverage** — expected, not a regression: each package's Data
Flow Task pipeline internals (components/columns/expressions) are still the
dominant share of the file by element count, and that's slice 3, not yet
done. **Neither PoC package's control flow branches** (both are straight
`A → B` or `A → B → C` chains — no parallel paths to demonstrate), so the
DAG algorithm itself is proven by a synthetic unit test
(`tests/Ssis.Extract.Tests/DagAlgorithmTests.cs`, reaches
`DtsxPackageReader.BuildDag` via `InternalsVisibleTo`), not the golden files.
Event handlers are wired generically (same recursive shape as the control-
flow tree) but are **unverified** — neither PoC package has one.

**Slice 3 (same day):** the full Data Flow Task pipeline model
(`PipelineReader.cs`) — every component parsed generically (properties,
connections resolved to CM names, inputs/outputs/columns, paths) regardless
of `componentClassID`, plus bespoke semantic payloads
(`FlatFileSourcePayload`/`OleDbDestinationPayload`, with a derived
column↔external-column mapping) for the two component types this PoC
actually has — confirmed by grepping every `componentClassID` in both
`.dtsx` files rather than guessing from the plan's table. On top of that,
`Ssis.Extract.Dtsx.LineageBuilder` derives column-level lineage (plan §5.1)
purely from the typed pipeline model, no XML involved. **Key discovery,
evidenced from `LoadEmployees`'s own pipeline, not assumed:** a `<path>`
element is a physical buffer connection, not what determines column
lineage — a synchronous transform's untouched passthrough columns (e.g.
Derived Column's own `EmployeeID`/`Department` inputs, never re-emitted as
its own output columns) are consumed downstream by matching `lineageId`
directly against the column's true original producer, skipping the
transform's output entirely; there is no `<path>` straight from the flat
file source to the OLE DB destination for those two columns, yet that is
exactly where their value comes from. So lineage edges are built by
matching `lineageId` globally, never by walking `<paths>` — which turns out
to reproduce the plan's own §5.1 example diagram exactly (direct arrows
bypassing the transform's subgraph for passthrough columns), a validation
that happened after the fact, not a design borrowed from the plan's prose.
**Net effect on the coverage metric:** both PoC packages jumped from
~19–20% (slice 2, pipeline internals still raw XML) to **100%** (slice 3) —
predicted in the slice 2 note above, now confirmed, not a fluke: every
distinct element name found under `<pipeline>` in both packages was
enumerated and checked against what `PipelineReader` actually reads before
trusting the number. `ssisx graph --input <path> --out <dir>` renders the
lineage as Mermaid/DOT, one file pair per Data Flow Task, using the same
"only produced columns + sink-component terminal columns get nodes" rule
the plan's own diagram uses (a plain passthrough input on a non-sink
transform gets no separate node). `LineageBuilder` is proven against
synthetic pipelines (`tests/Ssis.Extract.Tests/LineageBuilderTests.cs`,
public type, no `InternalsVisibleTo` needed) the same way `BuildDag` is,
since neither PoC package's lineage produces an unused column or a
multi-reference expression to exercise those paths for real.

**Slice 4 (2026-08-13, per plan §8 the actual phase-0 deliverable):**
`ssisx report` — complexity scoring (`ComplexityScorer.cs`, weighted counts
→ Simple/SqlHeavy/Procedural/HighRisk, weights in
`config/complexity-weights.json` not code, per plan §5.6), a 23-rule
findings engine (`RulesEngine.cs`, covering plan §5.7's four categories —
answerable entirely from the slice 1–3 model, no ScriptDom/expression
tokenizer needed yet), and the non-determinism manifest (`NonDeterminism-
Analyzer.cs`, plan §5.8). Writes `inventory.csv/.md`, `findings.csv/.md`,
`nondeterministic.json`, `unmapped.md`, `portfolio.md`. Full rule catalog
and scoring detail in `Tools/SsisExtractor/docs/report-schema.md` — this
note is only the one fact worth surfacing here.

**That one fact:** the non-determinism manifest was cross-checked against
this very file, not just synthetic tests — `ssisx report` against both PoC
packages finds *exactly* the three columns the "Sensitive credential"
section above's sibling note (plan §5.8's worked example) already names:
`dbo.Employee.LoadedAtUtc`, `dbo.Department.LoadedAtUtc`,
`dbo.Designation.LoadedAtUtc`, all `GETUTCDATE()`, all correctly traced
through the Derived Column that produces them to the OLE DB Destination
that lands them. That's an independent confirmation the lineage-tracing
logic (slice 3) and the manifest logic built on top of it (slice 4) are
actually right, not just that the tool runs without crashing. Also
confirmed real on this PoC (not just synthetic): the `hardcoded-connection-
string` finding fires on exactly `LoadEmployees`' `CM_TargetDb` — the one
connection manager in the whole PoC deliberately switched to a static
`ConnectionString` for the sensitive-credential demo (see that section
above) — and nowhere else, matching what CLAUDE.md already documents about
which connection managers are expression-driven vs literal.

**Slice 5 (2026-08-13, everything except `pull`/`enrich`):** SQL harvest +
ScriptDom parsing (new `Ssis.Extract.Sql` project — the only one with a
third-party parser dependency, kept out of the core readers deliberately),
expression harvest + tokenization, `.ispac` input, cross-package dependency
graph, data-touch inventory, and `ssisx diff`. **`pull`/`enrich` are now
formally out of scope, not pending** — both need SSISDB catalog access this
engagement doesn't have (plan §11 decision 4), and `ssisx diff` against a
deployed `.ispac` covers the source-vs-deployed drift case `pull` existed
for, entirely offline. They exit 2 with a message saying exactly that.
Detail in `Tools/SsisExtractor/docs/report-schema.md`.

**Facts worth surfacing here, each verified rather than assumed:**
- **ScriptDom's API was probed before use**, not guessed (same discipline as
  trap 12): `TSql170Parser`, `Parse(TextReader, out IList<ParseError>)`,
  visitor overloads confirmed from the package's own XML docs, then the
  analyzer was run against 10 real SQL shapes before being wired in.
  Package: `Microsoft.SqlServer.TransactSql.ScriptDom` 180.78.1, net8.0.
- **The read/write trap:** ScriptDom models an `INSERT INTO x` target as a
  `NamedTableReference` exactly like a `FROM` source, so "every table
  reference is a read" reports every written table as also read. Each
  mutating statement type is visited explicitly to capture its own target,
  and writes are subtracted from reads.
- **`ssisx diff` exits 1 on SEMANTIC differences only, never on bytes.**
  Proven necessary on this very PoC: its repo `.dtsx` and the `.ispac` built
  from it differ byte-for-byte with **zero** semantic differences (a build
  rewrites version stamps and designer layout). Gating CI on bytes would
  fire on every build and train everyone to ignore it.
- **All three of this PoC's CSV connection managers are expression-driven** —
  `LoadReferenceData`'s two carry no static `ConnectionString` attribute at
  all. So the data-touch inventory reports the *expression*
  (`CM_DepartmentCsv = @[User::DepartmentFilePath]`) in its own
  `FilePathsFromExpression` list rather than a design-time default the
  runtime replaces. Resolving those for real needs environment parameter
  values — i.e. exactly what the unavailable `enrich` would have provided.
- **Measured expression-language surface for this PoC:** `GETUTCDATE` (3),
  `UPPER` (3), `SUBSTRING` (1), plus the `DT_WSTR` cast (5). Casts are
  counted separately from functions on purpose — `(DT_WSTR,50)` looks like a
  function call to a naive scan, and conflating them inflates the "what must
  a replacement engine support" number this output exists to answer.
- **Zero cross-package dependency edges**, correctly: the two packages write
  disjoint tables and neither calls the other. Table names are
  bracket-normalized first (`[dbo].[Employee]` from a destination vs
  `dbo.Employee` from SQL text) or that comparison would silently miss.

**Where slice 5 left the extractor:** slices 1–5 done, 54 tests green, both
PoC packages at 100% extraction coverage. That number said the model handles
*these two packages*, **not** that it handles an unseen portfolio — plan §8's
own risk note is that component/task-type coverage on real client packages,
not code volume, is what actually tests this. Getting 3–5 representative real
client packages (plan §11 decision 2) is still the single biggest accuracy
lever and hasn't happened yet.

**2026-08-14 — synthetic component-coverage pass, no client packages yet.**
With decision 2 still unresolved, attacked plan §8's stated risk directly
instead of waiting: built two small `.dtsx` fixtures via the real SSIS 17
object model (same "ask the runtime, don't guess" discipline as trap 12,
extended from single-property probing to whole-package construction —
`Application`/`Package`/`MainPipe`/`IDTSDesigntimeComponent100` calls,
`SaveToXML`, then the saved XML read back as ground truth) covering exactly
the gap components plan §8 names: a ForEach Loop Container + Script Task
(`SyntheticForEachScript.dtsx`), and an OLE DB Source → Lookup → Conditional
Split → 3 OLE DB Destinations flow (`SyntheticLookupSplit.dtsx`, the
Lookup+Conditional Split combination that underlies a hand-rolled SCD
Type-1 upsert, not the deprecated SCD wizard component). Both fixtures now
extract at 100% coverage; full detail (exact componentClassIDs/property
names confirmed, what's object-model-verified vs. best-effort) is in
`Tools/SsisExtractor/docs/report-schema.md`'s own "Synthetic
component-coverage fixtures" section, not repeated here.

**This pass earned its keep beyond just adding types — it found and fixed
two real, pre-existing coverage-accuracy bugs and one rules false positive,
none visible with only two straight-line PoC packages to test against:**
- A ForEach Loop's `<ForEachEnumerator>`/`<ForEachVariableMappings>` are
  siblings of `<Executables>`, not inside `<ObjectData>` — invisible to
  both the model and the coverage percentage before this pass (never
  counted unmapped, never modeled — a silent overstatement for any package
  with this extremely common container).
- A pipeline component's *output-level* `<properties>` (distinct from an
  output column's own) had no field to land in at all — Conditional Split's
  whole case expression lives there, not on a column the way Derived
  Column's does. Same silent-overstatement shape as above.
- `RulesEngine`'s `unused-variable` rule wrongly flagged a ForEach Loop's
  own iteration variable as unused, because its best-effort text-collection
  pass didn't know a `ForEachVariableMapping`/a Script Task's
  `ReadOnlyVariables` count as real usage. Fixed with a regression test.

Now 65 tests total (62 green as of this note — see below), both PoC
packages still at genuine 100%, both synthetic fixtures too. Still
genuinely unbuilt, unchanged by this pass: Script Task *source* extraction
(the declared surface — language, read/write variables — is now captured;
the actual script lives inside the embedded VSTA project binary, still
unread) and type/length-narrowing detection — see the extractor's own
README "Out of scope / not implemented" section.

**2026-08-14 (same day) — the plan §7.3 object-model oracle, built.**
`Ssis.Extract.ObjectModel` (`net48`, Windows-only, its own
`SsisExtractor.ObjectModel.slnx` — deliberately not part of the main
`SsisExtractor.slnx`, keeping the main tool's no-GAC promise true) loads a
package via `Application.LoadPackage` and independently derives the
counts plan §7.3 names, cross-checked against the already-golden-tested
XML-derived `spec.json` for both real PoC packages (read generically via
`JObject`, since a net48 test project cannot reference the net8.0 model
project at all — the two runtimes never share a process; the comparison is
file-based on purpose). All green, plus two hand-verified checks against
the synthetic fixtures above (the oracle's own recursive-container-walk
and multi-component-pipeline counting, neither exercised by either
straight-line PoC package).

Getting the *counts* to actually agree took three real, evidenced fixes to
`PackageOracle.cs` — the object model has sharp edges here worth knowing
before extending this further, not obvious from the docs:
1. `DtsContainer.Variables` returns every variable *visible* at that scope
   (every `System::` variable at every container level, plus every
   ancestor's own declared variables inherited into view), not "declared
   directly here." An unfiltered count came back 100+ against a package
   declaring exactly one.
2. Filtering `System::` still 3x-overcounted a single real variable, because
   it's inherited into every child container's own enumeration too.
   `ReferenceEquals(v.Parent, container)` was tried next and *undercounted
   to zero* — COM interop hands back a fresh RCW on each `.Parent` access,
   so it's never the same .NET reference even for the same underlying
   object. Comparing `.ID` (a stable GUID string) is what actually works.
3. Project/package parameters are *also* exposed as synthetic variables,
   namespaced `$Package::`/`$Project::` — a different XML element entirely
   (`<DTS:PackageParameters>`, already counted separately); only
   `Namespace == "User"` is a real declared variable.

None of these three were guessed — each was root-caused by dumping the
live object model's actual variable list (namespace, `SystemVariable`
flag, `Parent.Name`/`.ID`) for a real PoC package and reading what came
back, the same evidence-first discipline as everywhere else in this file.

**Unrelated, found while running this pass's tests, not caused by it:** as
of 2026-08-17 this repo's working tree has independent, uncommitted changes
to `SSIS/LoadReferenceData.dtsx` and `SSIS/SSIS.dtproj` (plus new untracked
`SSIS/ScriptTest.dtsx` and `SSIS_V1.zip`) — looks like an in-progress SSDT
session adding a Script Task package to the actual PoC project. Three of
the extractor's 65 tests fail against that in-progress state (a golden-file
mismatch on `LoadReferenceData`'s now-cached CSV connection-string values,
`ProjectSpec`'s own golden file, and `IspacReaderTests` expecting 2 packages
in the built `.ispac` where there are now 3) — confirmed unrelated to the
extractor's own code by stashing just those two files and re-running (62/62
of the extractor's *own* changes pass clean against the last-committed PoC
state). Not touched, not fixed — this is someone else's in-progress edit to
resolve, not an extractor bug.

**2026-08-17 — Script Task source extraction (plan §4.5), built; corrects a
wrong guess from the 2026-08-14 note above.** That note assumed the real
script source "lives inside the embedded VSTA project binary" — a guess,
never checked against a real SSDT-authored Script Task, because the
synthetic fixture built that same day only ever set object-model
properties directly and never went through SSDT's own Script Task editor,
so it never got a real VSTA project written into it at all. Reading (read-
only — never edited, never committed by this tool) the in-progress
`SSIS/ScriptTest.dtsx` mentioned in the note right above this one — a real
SSDT-authored Script Task that happened to already be on disk — showed the
opposite is true: the actual source is plain text, one `<ProjectItem
Name="..." Encoding="...">CDATA</ProjectItem>` sibling per VSTA project
file (`ScriptMain.cs`/`.vb` — the real entry point by VSTA convention —
plus the `.csproj`/`.vbproj`, `AssemblyInfo.*`, and designer files).
`<BinaryItem Name="....dll">base64</BinaryItem>` is a precompiled *cache*
of that same code, not the only copy of it.

Before this fix, both element types were silently invisible to the reader
**and** to the coverage percentage, the exact same shape as the
ForEachLoop/pipeline-output-property bugs the 2026-08-14 synthetic pass
found — `DtsxPackageReader.ReadExecutable`'s own comment claimed a Script
Task's `ObjectData` was "fully modeled" and so never counted anything
under it as unmapped, which was only true of the four attributes on
`<ScriptProject>` itself, never of its `<ProjectItem>`/`<BinaryItem>`
children. Neither PoC package has a Script Task, so this was invisible to
every golden-file test run against them the whole time slices 4-6 were
being built and verified.

**What's built:** `ScriptTaskPayload.ProjectItems`/`BinaryItemNames`/
`SourceStripped`; `DtsxPackageReader.ReadScriptTask` now reads both element
types; a new `script-source-stripped` (RewriteEffort/Error) finding for
the case plan §4.5 calls out to flag loudly — a `<ScriptProject>` with a
`<BinaryItem>` but no `<ProjectItem>` at all; and `ssisx report`'s
`scripts/<Package>/<Task>/<relative path>` output (plan §6.1), writing
each `ProjectItem` back out with its own declared `Encoding` (`UTF8` on
every item observed except one internal `UTF16LE` descriptor) rather than
always defaulting to UTF-8. `SourceStripped` has not been observed on any
real package yet — SSDT's default behavior keeps source alongside the
binary cache — so that path is verified by a direct unit test constructing
the XML shape by hand, not a real fixture. Full detail (including the
`SyntheticForEachScript.dtsx` fixture's `ScriptProject` element, extended
with a small `ProjectItem`/`BinaryItem` pair to cover the source-present
path) in `Tools/SsisExtractor/docs/report-schema.md`'s own "Script Task
source extraction" section.

**2026-08-17 (same day) — Script Component source extraction, plan §4.5's
other half, built.** Investigated rather than assumed to share Script
Task's shape, and found three real differences, each confirmed via the
object model, not guessed from how Script Task works:
- **Different discriminator.** A Script Component's own `componentClassID`
  in the saved XML is the generic `Microsoft.ManagedComponentHost` — not a
  Script-Component-specific ID the way every other bespoke pipeline
  payload in this extractor is keyed. The real disambiguator is a custom
  property, `UserComponentTypeName`, value `Microsoft.ScriptComponentHost`.
- **Different source storage.** No `<ProjectItem>`/`<BinaryItem>` here —
  instead two ordinary custom properties, `SourceCode`/`BinaryCode`, both
  `isArray="true"`. This exposed a real, previously-unnoticed bug one
  level below Script Component itself: the extractor's generic pipeline
  property reader had no representation for an array-valued property at
  all, so an `isArray` property's multiple `<arrayElement>`s were silently
  collapsed into one run-on string through `XElement.Value`. Fixed
  generically (`PipelinePropertySpec.IsArray`/`.ArrayElements`), confirmed
  correct by setting a real `string[]` through
  `IDTSCustomProperty100.Value`'s own setter (bypassing the VSTA editor
  entirely, same limitation as Script Task's synthetic fixture never
  getting a real project) and reading back exactly what the object model
  persisted: `<arrayElements arrayElementCount="N"><arrayElement
  dataType="...">CDATA</arrayElement>...</arrayElements>`.
- **Different delimiter.** `ReadOnlyVariables`/`ReadWriteVariables` are
  **comma**-separated for Script Component (confirmed from the property's
  own self-declared description text), not semicolon-separated like
  Script Task's — a genuinely different convention between two features
  that look identical from the SSIS UI.

**What's NOT confirmed, stated as such rather than guessed:** the real
per-element file identity inside `SourceCode`'s array. No real
SSDT-authored Script Component was available anywhere on this machine to
read for ground truth (only a Script *Task* was — `SSIS/ScriptTest.dtsx`,
per the note above). So `ScriptComponentPayload.SourceCodeItems` stays an
ordered list of opaque text blobs, and `ssisx report` writes them as
`scripts/<Package>/<Task>/<Component>/source-<index>.cs|.vb|.txt` — an
index, not a recovered file name.

Also built: a `script-component-present` (RewriteEffort/Error) finding
paired with the same `script-source-stripped` finding Script Task uses
(reused, not duplicated); `ComplexityStats.ScriptComponentCount`, weighted
identically to `ScriptTaskCount` and folded into the same unconditional
`HighRisk` trigger; and a third synthetic fixture,
`SyntheticScriptComponent.dtsx` (OLE DB Source → Script Component → OLE DB
Destination), which incidentally reconfirmed Derived Column's own
passthrough-lineage rule (CLAUDE.md's `LineageBuilder` note above) on a
third component type — its three passthrough input columns were never
re-declared as output columns, so the downstream OLE DB Destination
resolves them by `lineageId` straight back to the OLE DB Source, skipping
the Script Component's own output entirely, same as documented. Full
detail in `Tools/SsisExtractor/docs/report-schema.md`'s own "Script
Component source extraction" section.

**2026-08-17 (same day) — `ssisx conformance`: migration-validation gate 1,
and the first thing that CONSUMES the extractor's output rather than adding
to it.** Up to this point every command described the old package; nothing
turned that description into work the rewrite is measured against, which
meant the whole "generate the gates instead of hand-writing 50 test suites"
payoff in `Migration-Validation-Plan.md` was still theoretical. This closes
that gap for gate 1 (§3 of that plan). Gates 2–5 remain unbuilt.

Ten rule categories, one per bullet of that plan's §3 — `ControlFlow`,
`Ordering`, `DataFlow`, `SourceColumn`, `TargetColumn`, `TargetSchema`,
`Transformation`, `SqlStatement`, `LoadSemantics`, `ScriptCode` — each rule
carrying both a `Requirement` (phrased as an obligation) and `Evidence` (the
concrete fact: the declared column type, the fast-load commit size, the
friendly expression text). Measured on this PoC: **31 obligations for
`LoadEmployees`, 49 for `LoadReferenceData`**, both correctly 0% accounted
for since no replacement exists. Full detail in
`Tools/SsisExtractor/docs/conformance-schema.md`.

**Three design points that are load-bearing — don't "simplify" these away:**
- **Rules and claims are separate files with different lifecycles, and
  `ssisx` NEVER overwrites a claim file.** Rules are regenerated from the
  `.dtsx` every run and are disposable; claims are human work product
  accumulated across a migration (which decisions were made and why). A stub
  is written only when none exists. Losing that property would mean
  regenerating rules after a package edit silently discards the audit trail
  behind a go-live decision.
- **Rule ids must stay stable, and that's correctness, not tidiness.** Claim
  files are keyed by id, so an id that changed when someone nudged a package
  in the designer would orphan every claim against it. Ids derive only from
  meaning-bearing identifiers (refIds, object/column names) — never document
  order, GUIDs, or counts — and are ordinal-sorted on emit;
  `ConformanceTests.RuleIds_AreStableAcrossRepeatedBuilds` guards all three.
- **An unexplained `NotApplicable` is rejected as invalid, not counted.**
  Dropping an obligation is legitimate; dropping one with no written reason
  is how a real requirement gets quietly deleted. Same reasoning rejects an
  unrecognised status rather than charitably reading it as done, and reports
  "no claim at all" separately from `Pending` (unclaimed usually means the
  package changed and nobody re-reviewed — staler than known-outstanding
  work).

**Verified against a real package I didn't author**, not just the PoC's two:
run against the in-progress `SSIS/ScriptTest.dtsx` (read-only, untouched) it
correctly produced both Script Tasks' `ScriptCode` rules with real evidence
(`language=CSharp; reads User::InputName; writes User::OutputMessage; 8
source file(s) extracted`), the precedence constraint between them, and
flagged both as `MachineVerifiable: false` — no tool can satisfy those, a
human must read the source and port it (that plan's §13).

**One reality-check worth recording**, since the first version of the test
asserted otherwise from an assumption: `LoadReferenceData` truncates both
its tables from a **single batched Execute SQL Task**
(`TRUNCATE ...Department; TRUNCATE ...Designation;`), not two tasks. So it
yields **one** `SqlStatement` rule whose evidence holds both statements —
rules are per *task* (the addressable unit the SQL harvest already writes to
`sql/`), not per statement.

**2026-08-18 — `Ssis.Runtime.Expressions` + `ssisx testgen`: migration-validation
gate 2, built.** Gate 1 proves nothing is missing; gate 2 (plan §4) starts
proving things are *correct* — the first extractor output that runs
generated assertions against real SSIS *behaviour*, not just its structure.
Two pieces: a portable (no GAC, net8.0) SSIS expression-language parser +
evaluator (`Tools/SsisExtractor/src/Ssis.Runtime.Expressions/`), and
`ssisx testgen`, which generates an xUnit test file per package from every
Derived Column expression, with boundary cases computed by evaluating the
expression through the library **at generation time** rather than
hand-authored.

**Ground truth came from the real SSIS 17 evaluator, same discipline as
trap 12** — extended from probing single object-model properties to probing
expression *behaviour*. A new Windows-only project,
`Tools/SsisExtractor/src/Ssis.Expression.Oracle/` (lives in
`SsisExtractor.ObjectModel.slnx` alongside the plan §7.3 oracle, same GAC
dependency), evaluates a fixed 136-expression corpus via a package
`Variable` with `EvaluateAsExpression=true` (reading `.Value` triggers real
evaluation) and writes the result to
`tests/Ssis.Runtime.Expressions.Tests/Fixtures/ssis-expression-oracle.tsv`.
`OracleCorpusTests.MatchesOracle` is a `[Theory]` over every row — the
corpus **is** the library's spec, not a hand-written assertion list.

**A real measurement bug was caught building the oracle, not shipped.** An
early version reused one `Package` and one variable name across all ~130
evaluations in a single process and produced an internally *contradictory*
reading: both `"a" < "A"` and `"A" < "a"` came back `True` in the same run,
which cannot both be correct under any consistent ordering. Root-caused by
re-running the pair in total isolation (fresh `Application`+`Package`+
uniquely-named variable each) and getting one consistent answer both
times — confirming the shared state leaked stale evaluator results across
iterations, not a real SSIS behaviour. Fixed by giving every evaluation in
the corpus its own `Application`/`Package`/variable name, nothing shared.
The corrected, isolated corpus is what's checked in.

**The measured semantics diverge from a naive C# rewrite in specific,
now-documented ways** (full catalog in
`Tools/SsisExtractor/docs/gate2-schema.md`) — the master rule almost
everything else follows from is **SSIS never implicitly coerces between
types** (`1 + "a"`, `"1" + 1`, `LEN(123)` are all measured errors, not
auto-converted):
- NULL propagates through string concat and comparisons as **three-valued
  logic** (SQL-style) — `NULL(DT_I4) == 1` is NULL, not `False`; `&&`/`||`
  follow Kleene logic (`FALSE && NULL` → `False`, `TRUE || NULL` → `True`,
  `TRUE && NULL` → NULL).
- String `==`/`!=` are **ordinal** (case-sensitive); `<`/`>`/`<=`/`>=` are
  **culture-aware** (lowercase sorts before its own uppercase) — verified to
  match .NET 8's `CultureInfo.InvariantCulture` comparer exactly, despite
  running on a different globalization stack (ICU) than the SSIS process
  (Windows NLS) that produced the corpus. Checked directly, not assumed.
- `(DT_WSTR,n)`/`(DT_STR,n)` casts are a **hard error** on overflow, never
  silent truncation.
- `(DT_I4)` casts round **half-to-even** on a float input, and cast `TRUE`
  to **-1** (the classic VB/COM Automation convention), not `1`.
- `SUBSTRING` requires `start >= 1` (0 errors exactly like a negative
  start — an assumption I got wrong on first pass, caught immediately by
  the corpus-driven test suite, not hand-review); out-of-range clamps to
  `""` instead. `TOKEN`/`TOKENCOUNT`'s delimiter argument is a **character
  set** (`strtok`-style), not a substring, and an empty input string is
  exactly one token (itself), not zero.

**One deliberate, documented gap:** SSIS's `(DT_WSTR,n)` cast of a float
produced by *arithmetic* (not a plain cast/literal) sometimes pads to a
fixed decimal-place count that varies by expression shape (`5.0/2` →
`"2.500000000000"`, 12 decimals) — looks like an internal "scale" tracked
through the expression compiler, similar to `DT_NUMERIC`'s own
precision/scale. Not reverse-engineered: no harvested expression in this
PoC does float arithmetic before a string cast (the measured surface is
string concat, `UPPER`, `SUBSTRING`, `GETUTCDATE`, `DT_WSTR` casts only).
The two affected oracle rows are named explicitly in
`OracleCorpusTests.KnownDivergences` rather than silently passed.

**Measured on this PoC:** `ssisx testgen` against both packages produces
**5 expressions covered, 3 skipped** — the three `LoadedAtUtc` columns'
`GETUTCDATE()` calls, correctly excluded as non-deterministic (the same
three columns the slice-4 non-determinism manifest already names). 30
generated `[Fact]` methods, verified to actually **compile and pass**
against a real xUnit project referencing the library — copied out and run
end to end, not just eyeballed.

### Conditional Split — multi-destination routing (2026-08-26)

The last of the four component types scoped after the "three new component types" round
(deliberately deferred there — see that section's own opening note — because it needed a
genuinely new multi-sink abstraction in `Etl.Core`, not just a new emitter). Planned via
`EnterPlanMode`/`ExitPlanMode`, plan written to and preserved at
`C:\Users\yashpal.singh01\.claude\plans\spicy-conjuring-cocke.md` for the exact record shapes.
The extraction/model side needed **zero changes** — `Ssis.Extract.Dtsx.PipelineReader.cs`
already fully parsed `Microsoft.ConditionalSplit` into `ConditionalSplitPayload`
(`DefaultOutputName`, ordered `Cases`) in an earlier round; the entire gap was in
`Ssis.Extract.Codegen` (zero references to `ConditionalSplit` anywhere, confirmed by grep) and
in `Etl.Core` (`DataFlowStep<TRow,TEntity>` is single-sink by construction).

**`Etl.Core` additions** (`Abstractions/IRowRouter.cs`, `Abstractions/IConditionalSplitBranch.cs`,
`Pipeline/ConditionalSplitBranch.cs`, `Pipeline/ConditionalSplitStep.cs`), parallel to
`DataFlowStep<TRow,TEntity>` — that type is untouched, every existing single-sink flow is
unaffected. `IRowRouter<TRow>.SelectBranch(row, in ctx)` returns a 0-based branch index,
evaluated in `EvaluationOrder`; the last branch is always the default. `IConditionalSplitBranch<TRow>`
is deliberately **not generic in TEntity** — `ConditionalSplitStep<TRow>` holds a list of
branches whose destination tables (and so `TEntity` types) can differ, and only a
non-generic-in-TEntity interface can live in one heterogeneous list.

**Buffer-then-sequential-flush, not streaming — a deliberate, accepted limitation.**
`IUnitOfWork` exposes one shared `SqlConnection`/`SqlTransaction` for the whole package run
(`D:\PoC\SSIS_Rewrite\src\Etl.Core\Data\UnitOfWork.cs`), and two `SqlBulkCopy` operations can't
safely run concurrently against it. So `ConditionalSplitStep<TRow>` reads the source once,
routes each row into its branch's own in-memory `List<TEntity>` buffer
(`ConditionalSplitBranch<TRow,TEntity>.Add`), and only once the source is exhausted does each
branch bulk-insert its own buffer, one at a time (`FlushAsync`). A branch that received zero
rows skips its own flush entirely (no empty `SqlBulkCopy` call). This trades true end-to-end
streaming for correctness under the existing single-connection design.

**Codegen additions**, in `Tools/SsisExtractor/src/Ssis.Extract.Codegen/`:
- `PackagePlanner.cs`: `DataFlowPlan` gains `ConditionalSplitPlan? ConditionalSplit`
  (`PipelineComponentSpec Component, List<ConditionalSplitBranchPlan> Branches` — cases first in
  `EvaluationOrder`, default branch always last). Each branch's downstream `OLEDBDestination` is
  resolved by a **one-hop walk of the pipeline's own `<paths>`** from the case/default output's
  own RefId (`pipeline.Paths.FirstOrDefault(p => p.StartId == output.RefId)`, then match the
  destination whose input RefId equals that path's `EndId`) — the same evidenced
  `StartId`/`EndId` pattern `RulesEngine.cs` already used for wiring checks, not new machinery.
  Any branch that fails to resolve is **fatal for the whole flow**, not partial generation — one
  `GenerationGap`, `PlanDataFlow` returns `null`.
- **The Lookup gate must run BEFORE the ConditionalSplit check**, and this is a real ordering
  bug caught by the existing test suite, not designed up front: `SyntheticLookupSplit.dtsx` has
  BOTH a Lookup and a Conditional Split in the same flow (OLE DB Source → Lookup → Conditional
  Split → 2 destinations, plus the Lookup's own No-Match output bypassing the split entirely to
  a 3rd destination). Checking `ConditionalSplit` first made `PackageGenerator` try to wire the
  whole flow via the split, silently ignoring the Lookup's own unrecoverable-join-key problem —
  caught immediately by `Generate_ProducesAReferenceTableCacheScaffold_ForALookup_...` (a
  pre-existing test, not one written for this feature) failing. Fixed by hoisting the Lookup
  check to the very top of the per-flow loop, ahead of both `ConditionalSplit` and
  entity-name/source resolution — safe because `LookupCacheEmitter` only ever needed the Lookup
  component itself, never `entityName`/`DestinationComponent`.
- `PackageGenerator.GenerateConditionalSplitFlow`: buffers every branch's `EntityEmitter`/
  `TransformEmitter`/`DbContextEmitter.TableSpec` output locally and only commits it to the real
  `files`/`tables` lists once **every** branch and the router succeed — no half-wired split
  scaffolding for a flow that ultimately fails. One exception, explicitly accepted: the flow's
  own source-row-type file(s) (`Sql/{X}Row.cs` etc.) are emitted eagerly, before any branch is
  attempted, since they describe the source, not any one branch, and are valid output even if a
  branch later fails — an orphaned-but-harmless row-type file is not "half-wired" the way a
  partially-wired `Program.cs` would be.
- New `ExpressionTranslator.TranslateCondition` (alongside the existing `TranslateColumn`),
  translating a case's `FriendlyExpression` into a C# boolean expression: `&&`/`||` recurse and
  join with the same C# operators (**two-valued**, not SSIS's three-valued NULL-aware logic — a
  NULL-producing operand is `NotTranslatable`, never guessed); numeric comparisons map straight
  to C# operators; string `==`/`!=` become `string.Equals(..., StringComparison.Ordinal)`,
  string `<`/`>`/`<=`/`>=` become `string.Compare(..., StringComparison.InvariantCulture)` —
  reusing the exact, already-oracle-verified semantics from `Ssis.Runtime.Expressions`' own gate-2
  corpus, not re-derived. Comparison kind is decided from whichever operand resolves a known
  `SsisType` (a `Reference` via the caller's dictionary, or a literal); an `IntLiteral` value
  translation (`"1000"`) was also added to the shared `TranslateScalar` switch — no evidenced
  Derived Column expression had ever needed a bare integer literal as a value before this.
- New `RouterEmitter.cs`, emitting `Mapping/{Name}Router.cs` (an `IRowRouter<TRow>` if/else-if
  chain). References are resolved directly from the Conditional Split component's own single
  input's columns — `PipelineInputColumnSpec.CachedName`/`CachedDataType`, no `LineageBuilder`
  join needed — **not** the same lineage-based approach `TransformEmitter` uses for Derived
  Column, because a routing condition runs on the flow's own source row exactly as the split
  sees it, before any per-branch transform. This is also where the central scope boundary lives:
  **a case referencing a value only a Derived Column computes (never a real, named buffer
  column at the split's own input) has no entry in this dictionary and degrades to a translation
  gap, never a guess** — the flow's shared, upstream Derived Column (if any) still runs and
  still feeds every branch's own transform unchanged; only the ROUTING CONDITION itself is
  restricted to passthrough columns.
- `ProgramEmitter.cs`: new `ProgramConditionalSplitStep`/`ProgramConditionalSplitBranch`
  records. The existing per-flow `IRowSource<TRow>` registration switch was factored into a
  shared `EmitRowSourceRegistration` helper (a Conditional Split reads from exactly the same
  kind of source a single-destination flow does) so it's written once, called from both paths.
  `Program.cs`'s `IEtlPackage` factory wires one `ConditionalSplitBranch<TRow,TEntity>` local per
  branch plus one router resolve plus one `ConditionalSplitStep<TRow>` local, following the same
  `sp.GetRequiredService<...>()` DI-registration style `DataFlowStep`'s locals already use.

**New fixture, `SyntheticConditionalSplit.dtsx`** (`Ssis.Extract.FixtureBuilder`'s new
`conditional-split` mode) — needed because `SyntheticLookupSplit.dtsx`'s own Conditional Split
is masked by its Lookup (proves the READER, never codegen, per the ordering bug above). OLE DB
Source (SqlCommand mode) → Derived Column (`LoadedAtUtc <- GETUTCDATE()`, upstream of the split,
shared by both branches) → Conditional Split (one case, `Amount > 1000` → `HighValue`; default →
`LowValue`) → 2 OLE DB Destinations. Building it surfaced three real, non-obvious object-model
facts, none guessed — confirmed via reflection and empirically before/while writing the fixture
code, same "ask the runtime, don't guess" discipline as trap 12:
- `IDTSDesigntimeComponent100.SetOutputProperty`/`.InsertOutput` both exist (reflected off this
  GAC's DTSPipelineWrap 16.0.0.0 first), and `IDTSOutput100.Name` has a public setter (no
  `IDTSName100` cast needed, unlike a flat-file column).
- **`IsDefaultOut` is READ-ONLY** — calling `SetOutputProperty(id, "IsDefaultOut", true)` throws
  `COMException 0xC0204006`. The component manages it itself: the one non-error output
  `ReinitializeMetaData()` creates automatically is marked `IsDefaultOut="true"` in the saved
  XML purely by virtue of never being given an `Expression` of its own, once a real case exists
  alongside it — nothing needs to (or can) set this property directly.
- **`InsertOutput`'s second parameter is an anchor output ID, not a "no anchor" sentinel** —
  passing an existing output's own ID (to insert "before" it) also threw `0xC0204006`; `0`
  (append at the end) is what actually works.
- `SetOutputProperty("Expression", ...)` is **not stored verbatim**, unlike Derived Column's own
  Expression/FriendlyExpression pair — the component validates the literal text against the
  input's real columns and rewrites it into the `#{lineageId}` form at save time (confirmed by
  reading the saved XML), the exact same lineageId-substituted shape
  `SyntheticLookupSplit.dtsx`'s own real Conditional Split already has.
  `FriendlyExpression` is stored as the literal text given, unchanged — the only one this
  tool's reader/`RouterEmitter` ever consumes.

**Verified end to end against real seeded rows on `.\SQLFORPOC_2022`, boundary case included**
(`ID=3, Amount=1000.00` — `Amount > 1000` is strictly-greater, so this row must land in
`LowValue`, not `HighValue`): `ssisx generate` + `dotnet build` against a copy of `Etl.Core` (0
warnings/0 errors) + run, then read both tables back directly rather than trusting the exit
code. Exact result: `SyntheticHighValue` got `ID=2 (1500.00)`/`ID=4 (2500.50)`,
`SyntheticLowValue` got `ID=1 (500.00)`/`ID=3 (1000.00)` — every row in its correct branch,
including the boundary. `LoadEmployees`/`LoadReferenceData` goldens confirmed byte-identical
after regenerating (neither package has a Conditional Split). Full regression: 67/67 in
`Ssis.Extract.Codegen.Tests` (was 54), same 3 pre-existing unrelated failures elsewhere; 23/23
in `Etl.Core.Tests` (was 20) plus `LoadEmployees.Tests`/`LoadReferenceData.Tests` unaffected
(7/7, 11/11).

**Not done, deliberately out of scope for this round:** per-branch Derived Columns (only a
single, shared, upstream-of-the-split Derived Column is supported — matches every fixture built
so far, but a branch-specific transform would need new machinery); true streaming for a
Conditional Split (see the buffer-then-flush limitation above); three-valued NULL-aware
condition logic.

### Testing against a real third-party portfolio (`SSIS_From_Sandeep`, 2026-08-27) — a genuine outside test, not another synthetic fixture

A colleague's own KT-reference project, `SSIS_From_Sandeep/RBC_Demo_ETL/` (its own nested git
repo, untouched by this repo's own git history — the same caution as the "Unrelated in-progress
PoC files" note elsewhere in this file, though this one is a deliberate deliverable, not
in-progress SSDT drift): 5
packages, ~30 SSIS component types across roughly the SAME vocabulary the extractor's own PoC
never exercised (File System Task, ForEach Loop, Excel Source, ADO NET Source/Destination,
Merge, Sort, Merge Join, Aggregate, Multicast, Row Count, OLE DB Command, Conditional Split,
Lookup, Data Conversion). This is exactly the "3-5 representative real client packages" plan §11
decision 2 named as the biggest unresolved accuracy lever, back when only the PoC's own 2
straight-line packages existed to test against — closed here for real, not with another
synthetic fixture.

**`ssisx extract`/`report` — clean, no crashes, 98.67%-100% coverage across all 5 packages.**
One genuinely new modeling gap surfaced: File System Task has no bespoke payload yet, falls into
generic `UnmappedTask` (captured verbatim, not semantically parsed) — everything else, including
Package_Legacy's own `.dtsConfig`, was already-documented/out-of-scope. Duplicate-package
handling worked correctly on `Package.Scripts.WORKING.dtsx` (a parameter-inlined duplicate of
`Package.dtsx`, reported in `load-failures.md`, not silently dropped). One expected, non-bug SQL
parse failure: an OLE DB Command's `?` ODBC-style placeholder isn't valid standalone T-SQL, so
ScriptDom correctly rejects it read in isolation.

**`ssisx generate` — 0/5 packages fully generatable, and every reason is either already-known or
newly real:** Script Task/ForEach Loop/direct-copy-pipeline/Lookup's refuse-to-guess-a-join-key
all confirmed on real data as designed. New, previously-undiscovered generator gaps: File System
Task, Excel Source, and ADO NET destinations are all unsupported (no fixture had ever exercised
them); and — the one actually closed this session — a Conditional Split whose branches feed a
further transform before remerging to ONE shared destination (`Package_Transforms.dtsx`'s
`DFT_DerivedAndSplit`: `CSPLIT_Validity` → `DER_TagValid`/`DER_TagInvalid` → `UNION_Recombine` →
one `OLEDST_CustomerEnriched`) was a materially different, arguably more common shape than the
"each branch keeps its own destination" pattern the original Conditional Split support
(2026-08-26, above) was built for.

**A real classification bug was also caught and fixed**, not just a missing feature: the OLE DB
Source SqlCommand-mode advisory gap ("verify column names before running" — see the "Three new
component types" section above) is meant to be non-fatal, same category as the `.Notification`
gap, but `PortfolioDigest.IsBlockingGap` only special-cased `.Notification`'s `Location` suffix,
so any package with a SqlCommand-mode source always read as blocked in the readiness digest —
the exact miscount class the `.Notification` exclusion was created to prevent, just missed for
the newer gap. Fixed by adding `GenerationGap.IsBlocking` (`bool`, defaults `true`) instead of
string-matching `Location`/`Reason`, so `PortfolioDigest`/`ReportCommand` no longer need to
recognize each non-fatal gap shape by name. Verified via the full test suite (unaffected — only
2 call sites ever needed `IsBlocking: false`) and by re-running the digest against
`SSIS_From_Sandeep`: `Package`'s blocking-gap count corrected from 5 to 4, with the SqlCommand
gap now tagged `_(non-blocking)_` in `generation-readiness.md`.

### Conditional Split — multi-hop Union-All remerge (2026-08-27)

Closes the `DFT_DerivedAndSplit` gap above. `PackagePlanner.ResolveBranch` no longer requires a
case/default output to connect to an OLE DB Destination in exactly one hop — it now walks
forward through any chain of `Microsoft.DerivedColumn` (recorded, in order, as the branch's own
`ConditionalSplitBranchPlan.DerivedColumns`) and/or `Microsoft.UnionAll` (a pure physical
merge point, walked through transparently) components until it reaches a destination, capped at
8 hops. Two branches that both pass through the SAME Union All naturally resolve to the SAME
destination instance — no special-casing of the convergence needed, it falls out of the walk
structurally. Any OTHER component type mid-chain (Sort, Merge, Lookup, a nested Conditional
Split, …) is still a fatal gap for that branch, named explicitly — this deliberately leaves
`DFT_MergeSortedBranches`'s own `Split → Sort → Merge` shape unsupported, a different, harder
gap for another round (Merge needs matched sort keys, not just a physical pass-through).

`TransformEmitter.TransformRequest.DerivedColumn` (singular) became `DerivedColumns`
(`IReadOnlyList<PipelineComponentSpec>`) — `computedByName` now merges every list entry's own
computed output columns (first-registered wins on a name collision, not evidenced in any real
package). `PackageGenerator.GenerateConditionalSplitFlow` was restructured into two phases:
Phase A resolves every branch's entity name up front (needed to detect convergence before
deciding anything); Phase B emits, deduping `EntityEmitter`/`DbContextEmitter.TableSpec` to once
per DISTINCT destination `RefId` (not once per branch), and disambiguating the transform class
name (`{Entity}{OutputName}Transform` instead of the collision-prone plain `{Entity}Transform`)
only for branches whose entity name is genuinely shared — every non-colliding branch keeps
today's plain naming unchanged, so no prior fixture's golden output moved. The direct-copy gate
(`no Derived Column found`) now fires only when NEITHER the flow's shared upstream Derived
Column NOR any branch's own per-branch chain contributed anything — a split whose only transform
work happens per-branch (this fixture's own shape) is not a direct-copy flow.

**A second, real bug was caught wiring this into `Program.cs`, not shipped:** two branches
sharing an entity would both register `IRowTransform<TRow,TEntity>` for the exact same closed
generic type via `builder.Services.AddScoped<...>()`. The built-in DI container doesn't throw on
a duplicated registration — `GetRequiredService<T>()` silently resolves to whichever was
registered LAST, so every colliding branch would have run the SAME (wrong, for at least one of
them) transform. Fixed by dropping the whole `AddScoped<IRowTransform<...>>`/
`GetRequiredService<IRowTransform<...>>()` pair for Conditional Split branches — a generated
transform class never has constructor dependencies (`TransformEmitter.BuildFile` emits none), so
each `ConditionalSplitBranch<...>` now constructs its own transform directly (`new
{TransformClassName}()`) instead, for EVERY branch uniformly, not just colliding ones — simpler
generated code and the whole footgun class removed at once, matching the same "don't special-
case a problem you can eliminate structurally" instinct as the Union-All-convergence walk above.

**Verified two ways, not one:**
1. **The real motivating case** — re-running `ssisx generate` against `SSIS_From_Sandeep`'s
   actual `Package_Transforms.dtsx` after this fix: `DFT_DerivedAndSplit`'s own gap disappeared
   entirely at the structural level, moving to a purely orthogonal expression-translation gap
   (`FINDSTRING`/`TRIM` inside the split's OWN case condition — see below) — proof the
   planner/generator layer now works correctly on genuine, real, SSDT-authored XML, which is
   stronger evidence than a hand-built fixture since there's no question the shape is legal.
2. **A new dedicated fixture**, `tests/Ssis.Extract.Tests/Fixtures/SyntheticConditionalSplitRemerge.dtsx`
   (`Ssis.Extract.FixtureBuilder`'s new `conditional-split-remerge` mode): OLE DB Source →
   Derived Column (`LoadedAtUtc`, shared) → Conditional Split (`Amount > 1000` → High; default →
   Low) → each branch through its OWN literal-tag Derived Column (`Segment <- "High"`/`"Low"`) →
   Union All → one `dbo.SyntheticRemergeTarget`. Kept deliberately simpler than the real
   package's own condition (no FINDSTRING/TRIM dependency) so it proves the STRUCTURAL remerge
   support in isolation. **Generated, built against a copy of `Etl.Core` (0 warnings/errors), and
   actually RUN against `.\SQLFORPOC_2022`**: `ID=1 (500.00)/Low`, `ID=2 (1500.00)/High`, `ID=3
   (1000.00)/Low` — the exact boundary, `>` is strictly-greater — `ID=4 (2500.50)/High`, every
   row correct including the boundary, both branches' own `SqlBulkCopy` landing in the same
   table with no NULLs. `Model/SyntheticRemergeTarget.cs` emitted once (not twice);
   `Mapping/SyntheticRemergeTargetHighTransform.cs`/`...LowTransform.cs` each correctly combine
   the shared `LoadedAtUtc` with their own `Segment` literal; `Program.cs` shows exactly one
   `AddBulkSink<SyntheticRemergeTarget>()`, no per-branch `AddScoped<IRowTransform<...>>`, and
   both `new {Branch}Transform()` calls sharing one `GetRequiredService<IBulkSink<...>>()`. This
   is genuinely stronger evidence than a dtexec pass would have been — it proves the actual
   GENERATED code is correct, which is the real deliverable.

**One object-model quirk chased and ultimately left unresolved, stated rather than hidden:** the
fixture itself is NOT confirmed dtexec-executable. A real run fails SSIS's own pipeline
validation with `"CSPLIT_Amount.Inputs[Conditional Split Input].Columns[...] has lineage ID N
that was not previously used in the Data Flow task"` — a metadata inconsistency on the SPLIT's
own input, introduced somewhere by attaching a Derived Column directly downstream of one of its
case/default outputs (something no prior fixture in this file does; every earlier Conditional
Split output goes straight to a destination). Two hypotheses were tried and ruled out by
comparing real runs, not assumed: re-running the split's own `ReinitializeMetaData()` after
adding the case output, and resolving the Union All's two inputs in one pass vs. incrementally —
neither changed the outcome. Left unresolved rather than papered over with an unverified guess,
same "a gap must be visible" discipline as everywhere else in this tool — and it doesn't matter
for `ssisx`'s own correctness: `extract`/`generate` parse the saved XML directly (plan §2.1) and
never invoke SSIS's own object model, so they read this fixture correctly regardless, and the
feature's real-world runtime correctness is independently proven by the real Sandeep package
(SSDT-designer-built, so guaranteed valid) plus the fixture's own generated-code run above. Two
real API facts WERE nailed down for anyone extending Union All support further: a second input
is created via the raw `unionMeta.InputCollection.New()`, NOT the component-level
`IDTSDesigntimeComponent100.InsertInput(...)` Conditional Split's own `InsertOutput` made a
reasonable pattern to try first (`InsertInput` throws `COMException 0xC020800E` unconditionally
for `Microsoft.UnionAll`); and `DT_WSTR`'s own `SetOutputColumnDataTypeProperties` codepage
argument must be `0`, not `1252` (that's `DT_STR`'s non-Unicode convention — passing 1252 for
`DT_WSTR` throws `0xC0204025`).

**Real-world condition-translation gap found and partly closed along the way:**
`DFT_DerivedAndSplit`'s own `CSPLIT_Validity` case is `!ISNULL(CustomerID_i4) &&
FINDSTRING(TRIM(Email),"@",1) > 0` — the first real Conditional Split condition this tool has
seen that isn't a bare comparison. `ExpressionTranslator.TranslateCondition` gained two new
cases: `UnaryExpr{Op: Not}` (recurses through `TranslateCondition`, negates with `!(...)` — SSIS
`!` only ever negates something boolean-shaped) and `FunctionCall{Name: "ISNULL", Args.Count: 1}`
used directly as a condition (translates its argument as a value, tests it for C# `null` —
correct regardless of whether the generated row property ends up a reference type or a nullable
value type). This fully unblocks `!ISNULL(...)`. `FINDSTRING`/`TRIM` as VALUE-producing function
calls remain unsupported (`TranslateScalar`'s switch still only knows `UPPER`/`SUBSTRING`) —
deliberately NOT added this round; expanding `ExpressionTranslator`'s general SSIS function
coverage is a separate, larger body of work (these same functions are also needed inside
ordinary Derived Column expressions, not just conditions), scoped out to keep this round to the
Conditional Split structural fix plus its one directly-blocking condition-shape gap.

**Regression:** 69/69 in `Ssis.Extract.Codegen.Tests` (was 67), same 3 pre-existing unrelated
failures elsewhere (someone's own in-progress SSDT edits to `SSIS/LoadReferenceData.dtsx`/
`SSIS/SSIS.dtproj`, not this session's or the extractor's); `Ssis.Extract.ObjectModel.Tests`
unaffected (4/4).

### File System Task -- pre-load and post-flow support (2026-08-27)

The third gap closed from testing `ssisx` against `SSIS_From_Sandeep`: `Package_Advanced.dtsx`'s
`FST_ArchiveWorkbook` (archives the source workbook before `DFT_ExcelImport` reads it, inside
`SEQ_Prepare` alongside `SQL_TruncateTargets`) had no bespoke payload at all and fell into
generic `UnmappedTask`. Turned out to need more than a codegen change: `FST_ArchiveWorkbook`
runs BEFORE any Data Flow Task, and `Etl.Core`'s `IEtlPackage.PreLoadStatements` was SQL-text-
only, run inside the same transaction as everything else (`uow.ExecuteSqlAsync`) -- a file copy
categorically can't join that list. Scoped, with the user's explicit sign-off, as "full support
(pre-load and post-flow)" rather than post-flow-only (which wouldn't have closed the actual
discovered case).

**Extraction (`Ssis.Extract.Model`/`Ssis.Extract.Dtsx`):** `FileSystemTaskPayload` added --
`OperationRaw`/`SourcePathRaw`/`SourceIsVariable`/`SourceConnectionName`/`DestinationPathRaw`/
`DestinationIsVariable`/`DestinationConnectionName`/`OverwriteDestination`. Confirmed via a live
object-model probe before writing any reader code (same trap-12 discipline), not assumed from
the one real example: `<FileSystemData>` and its attributes carry no namespace prefix (same as
`<ScriptProject>`); `TaskOperationType` is genuinely OMITTED from the XML for the schema's
default (`CopyFile`) -- present as the literal enum name (`MoveFile`, `DeleteFile`, ...)
otherwise; `TaskSourcePath`/`TaskDestinationPath` can independently be a literal path, a
`Namespace::VariableName` reference (`TaskIsSourceVariable`/`TaskIsDestinationVariable`), or --
the real evidenced shape -- a FILE connection manager's DTSID, resolved the same way
`ExecuteSqlTaskPayload.ConnectionRefRaw`/`ConnectionName` already are. Verified against the real
package: `SourceConnectionName: "CM_FILE_SourceWorkbook"`, `DestinationConnectionName:
"CM_FILE_ArchivedWorkbook"`, `OverwriteDestination: true`, `OperationRaw: null` -- Package_Advanced
went from 99% to 100% coverage.

**Etl.Core (`D:\PoC\SSIS_Rewrite`, a separate repo -- see "First commit" note below):**
`FileSystemPreLoadAction`/`FileSystemOperation` (Copy/Move/Delete/Rename/CreateDirectory -- the
subset of SSIS's own `DTSFileSystemOperation` this generator translates; SetAttributes/
CopyDirectory/MoveDirectory/DeleteDirectory/DeleteDirectoryContent are unevidenced, left out
rather than guessed) in `Etl.Core.Abstractions`; `FileSystemActionRunner` (the one place an
action is actually executed -- a thin `File.Copy`/`File.Move`/`File.Delete`/
`Directory.CreateDirectory` switch, shared by both positions so the mapping exists exactly
once) and `FileSystemStep : ILoadTask` (post-flow position, symmetric with `ExecuteSqlStep`) in
`Etl.Core.Pipeline`. `IEtlPackage` gained `PreLoadFileActions` (pre-load position) as a
**separate list from `PreLoadStatements`**, not a unified/renamed one -- deliberately additive:
a C# default parameter value (`= null!` folded to `[]` in `EtlPackage`'s own init) means every
existing `IEtlPackage` implementer/caller (the hand-written `LoadEmployees`/`LoadReferenceData`
programs, their tests, `PackageRunnerTests`) needed ZERO changes, since neither package has a
File System Task. `PackageRunner` runs `PreLoadStatements`, then `PreLoadFileActions`, then
`Steps` -- matching the one real evidenced order (`SQL_TruncateTargets` always precedes
`FST_ArchiveWorkbook`).

**Codegen (`Ssis.Extract.Codegen`):** `PackagePlanner.WalkContainer` reuses the EXACT SAME
pre-load-vs-post-flow position tracking Execute SQL Task already established (no new logic
needed there) -- a File System Task in pre-load position lands in a new
`PackagePlan.PreLoadFileActions: List<FileSystemActionPlan>`; one in post-flow position becomes
a new `FileSystemStep : PackageStep`, in `Steps` at its real position. `ResolveFileSystemAction`
maps the raw SSIS operation name to the supported subset (an unrecognized one is a named gap,
never guessed) and resolves source/destination paths -- a connection-manager reference resolves
to that CM's own `ConnectionString` (already a literal path for a FILE-type CM, no
folder+filename split the way FLATFILE needs); a variable-driven path is always a fatal gap (no
runtime-config mapping for an SSIS variable exists). `ProgramEmitter`/`ProgramRequest` gained
`PreLoadFileActions`/`ProgramFileSystemStep`, reusing `FileSystemActionPlan` verbatim rather than
a parallel `Program*`-namespaced DTO (unlike `ProgramFlowSpec`, there's no generator-owned NAMING
decision on top of the planner's own fully-resolved literal operation/paths).

**Verified two ways, same discipline as the Union-All-remerge fix above:**
1. **The real motivating case** -- re-running `ssisx generate` against `SSIS_From_Sandeep`'s
   actual `Package_Advanced.dtsx`: `FST_ArchiveWorkbook`'s own gap is gone entirely; the
   package's remaining 3 blocking gaps (Excel Source, ForEach Loop, an OLE DB Command flow with
   no destination) are all pre-existing and unrelated.
2. **A new dedicated fixture**, `tests/Ssis.Extract.Tests/Fixtures/SyntheticFileSystemTask.dtsx`
   (`Ssis.Extract.FixtureBuilder`'s new `file-system-task` mode): `SQL_PreLoad` (TRUNCATE) ->
   `FST_PreLoadCopy` (pre-load) -> `DFT_Load` (Flat File Source -> Derived Column -> OLE DB
   Destination) -> `FST_PostLoadCopy` (post-flow) -- both File System Tasks referencing FILE
   connection managers by name, matching the real evidence. **Generated, built against a copy of
   `Etl.Core` (0 warnings/errors), and actually RUN against `.\SQLFORPOC_2022` and the real
   filesystem**: pre-load SQL truncated the table, the pre-load copy landed
   `archived-preload.txt`, the Data Flow Task loaded 2 rows, and the post-flow copy landed
   `archived-postflow.txt` -- all four steps in the correct order, confirmed by reading the
   table and both files back afterward, not by trusting the exit code.

**Two real object-model facts nailed down building the fixture** (`Ssis.Extract.FixtureBuilder`'s
new `BuildFileSystemTaskFixture`/`AddFileSystemTask`), neither assumed:
- `Microsoft.SqlServer.Dts.Tasks.FileSystemTask.FileSystemTask` lives in its own GAC assembly,
  `Microsoft.SqlServer.FileSystemTask` -- not one of the three `Ssis.Gac.props` already
  references for every other object-model project in this repo. Confirmed via
  `InnerObject.GetType().Assembly` on a live probe task rather than guessed by analogy with
  `Microsoft.SqlServer.ScriptTask`'s own separate-assembly precedent; added as FixtureBuilder's
  own additional `<Reference>`, reusing `Ssis.Gac.props`'s already-detected `$(SsisGacVersion)`
  rather than re-probing.
- A FILE connection manager's `FileUsageType` has no strongly-typed interface to cast
  `.InnerObject` to (confirmed by reflecting the whole assembly for anything matching
  `*ConnectionManagerFile*` -- nothing exists) -- set via the same `.Properties["..."].SetValue(...)`
  dynamic-property pattern `Microsoft.ExecuteSQLTask`'s own `Connection`/`SqlStatementSource`
  already use elsewhere in this file, passing the cast `int` value per CLAUDE.md's own
  pre-existing trap note ("`FileUsageType` wants the int enum, not its name").

**A real byte-level mistake, caught and fixed rather than shipped:** patching
`tests/Ssis.Extract.Tests/Golden/{LoadEmployees,LoadReferenceData}.spec.json` (both need a new
`"FileSystemTask": null,` field, since `ExecutableSpec` gained the field and these are
byte-exact golden comparisons) via the ordinary Edit tool introduced CRLF where the committed
files used bare LF, ballooning a 2-line intended change into a ~3900-line diff -- exactly the
class of bug CLAUDE.md's own trap 18 already documents for `.dtsx`/XML files, reproduced here
for `.json` (these specific golden files carry `-text` in `.gitattributes` specifically because
of a past incident of this same kind, per that file's own comment). Reverted and redone with a
byte-preserving PowerShell read/regex-replace/write, confirmed by `git diff --stat` showing only
the 2-3 intended lines per file afterward.

**First commit note:** `D:\PoC\SSIS_Rewrite` (the Etl.Core rewrite repo) had never been
committed at all since it was built 2026-08-25 -- discovered while trying to commit this
feature's own Etl.Core changes. With the user's explicit sign-off, committed as that repo's
first-ever commit (`d8f6231`), bundling the full skeleton (Etl.Core, LoadEmployees,
LoadReferenceData, all tests) together with the File System Task additions -- there was no
prior baseline to separate them from. Checked for secrets first (none; `ssis_loader`'s password
lives in .NET User Secrets, never in the committed `appsettings.json` files).

### ADO NET Source/Destination (2026-08-27)

The fourth gap closed testing `ssisx` against `SSIS_From_Sandeep`: `Package_Exports.dtsx`'s
`DFT_AdoNetRoundTrip` (`ADO_SRC_Customers` → `ADO_DST_ExportLog`) had no bespoke extraction
payload -- structurally covered generically (100% coverage even before this round, same as
every gap this session started from), but codegen had no idea either component was a SQL
source/destination at all. Unlike File System Task, this one needed **no new Etl.Core
abstraction** -- an ADO NET Source/Destination is, underneath, still a SQL Server read/write,
so the existing `SqlRowSource<T>`/`SqlBulkSink<T>` machinery already covers it once the
extraction/codegen layers recognize the component.

**The real discrimination shape, confirmed from the real package's own XML, not guessed:**
both components' own `componentClassID` is the generic `Microsoft.ManagedComponentHost` --
same shape Script Component already uses (`ScriptComponentPayload`'s own doc comment) --
disambiguated by a `UserComponentTypeName` custom property: `Microsoft.ADONETDestination` for
the destination, and (genuinely surprising -- NOT the naming-symmetric `Microsoft.ADONETSource`
a reader might guess) `Microsoft.DataReaderSourceAdapter` for the source. Property names differ
from OLE DB's own: `TableOrViewName` instead of `OpenRowset`, and -- the one genuinely new
quoting convention in this whole project -- **double-quoted** (`"dbo"."CustomerExportLog"`),
not bracketed (`[dbo].[CustomerExportLog]`). The destination's own `UseBulkInsertWhenPossible`
property (observed `true` on the real evidenced instance) plays the same "is this fast-load
configured" role `FastLoadMaxInsertCommitSize`/`FastLoadOptions` do for OLE DB. The source's own
`AccessMode` is deliberately left unresolved/unused by codegen: the one real evidenced value
(`1`, with `SqlCommand` populated and `TableOrViewName` empty) doesn't obviously match either of
OLE DB Source's own evidenced values (`0`/`2`), and guessing the enum mapping from one data
point would violate this tool's own "don't hand-guess an enum, ask the runtime" rule (trap 12).
Codegen branches on which of `SqlCommand`/`TableOrViewName` is actually populated instead,
sidestepping the need to know the enum at all.

**Model/reader (`AdoNetSourcePayload`/`AdoNetDestinationPayload` in `Ssis.Extract.Model`,
wired in `PipelineReader.cs` alongside the existing `UserComponentTypeName ==
"Microsoft.ScriptComponentHost"` branch) and codegen (new `AdoNetSupport.cs`:
`DestinationInfo`/`SourceInfo`/`AdoNetSupport` normalize an OLE DB OR ADO NET
destination/source's connection name, table name (bracket-normalized), and fast-load-configured
flag into one shape) touch only ~10 call sites total across `PackagePlanner.cs`,
`PackageGenerator.cs`, `DbContextEmitter.cs`, and `PrimaryKeyInference.cs`** -- confirmed by
grepping every `.OleDbDestination`/`.OleDbSource` reference in `Ssis.Extract.Codegen` before
touching anything, not assumed. `BuildSqlFlowSource` split into `BuildOleDbFlowSource` (the
original AccessMode-number switch, unchanged) and `BuildAdoNetFlowSource` (the new field-
presence branch), dispatched from one shared connection-manager-resolution preamble.
`PrimaryKeyInference` (a separate, pre-existing `ssisx report` feature, not core to `generate`)
was extended too, for completeness, since it was a 5-line addition once the same normalization
existed.

**Verified against the real motivating case, the same first check every gap this session used:**
re-running `ssisx generate` against `SSIS_From_Sandeep`'s actual `Package_Exports.dtsx`:
`DFT_AdoNetRoundTrip`'s own structural gap ("no OLE DB Destination found") is gone entirely,
replaced by the SAME "no Derived Column found -- direct-copy pipeline" gate every OTHER
unwired real-package flow already hits (a pre-existing, cross-cutting limitation, not specific
to ADO NET) plus the expected non-fatal SqlCommand-column-naming advisory. The other two
`Package_Exports.dtsx` flows (`DFT_ExportDelimited`/`DFT_ExportFixedWidth`, both writing to a
Flat File Destination) remain unsupported -- a different, unrelated destination type, out of
scope for this round.

**Unlike every other gap closed this session, no dedicated fixture was built and actually run
end to end** -- stated honestly rather than glossed over, since this is a genuinely lower-
confidence verification tier than File System Task's or Conditional Split's. Constructing an
ADO.NET connection manager through the real SSIS object model fails outright in this
environment: `cm.AcquireConnection(null)` throws `Microsoft.SqlServer.Dts.Runtime.
DtsCouldNotCreateManagedConnectionException: "Could not create a managed connection manager"`,
reproduced identically across three independent variations -- `ADO.NET:System.Data.SqlClient.
SqlConnection` (the real package's own provider) and `ADO.NET:Microsoft.Data.SqlClient.
SqlConnection` (already working elsewhere on this machine per the SMO catalog-admin notes,
pre-loaded from `...\SSIS\170\Extensions\SQLCommon\Microsoft.Data.SqlClient.dll` before
retrying), and `Integrated Security=True` vs `=SSPI` -- with **zero further diagnostic detail**
available via the .NET exception chain (`InnerException` is always null; the SSIS runtime
swallows whatever the real root cause is). Left unresolved rather than papered over with an
unverified guess, same discipline as the Union-All-remerge fixture's own unresolved quirk.
Compensated for with direct unit tests against hand-constructed `PipelineComponentSpec` objects
matching the REAL extracted evidence exactly (`AdoNetSupportTests.cs` -- table-name
normalization, and `DestinationInfo`/`SourceInfo`'s OLE DB-vs-ADO-NET dispatch), plus the real-
package structural verification above; what's NOT independently proven is that the generated
`SqlRowSource<T>`/`SqlBulkSink<T>` wiring actually reads/writes correctly against a live ADO.NET
connection at runtime, since no generated ADO NET flow has ever actually been run. A future
session attempting this fixture again should treat the connection-manager-creation failure as
the starting point, not re-derive it -- try capturing SSIS's own COM error stream/event
listener rather than a bare try/catch, since the plain exception chain carries no detail at all.

### FINDSTRING/TRIM in ExpressionTranslator (2026-08-27)

The last of the gaps discovered testing against `SSIS_From_Sandeep`, and deliberately the
smallest picked up this round: unlike the other four (Flat File Destination, Excel Source,
ForEach Loop, Conditional Split -> Sort -> Merge), FINDSTRING/TRIM needed no new runtime
abstraction and no reverse-engineering -- both functions' semantics were already oracle-verified
and implemented in `Ssis.Runtime.Expressions/Functions.cs` (gate 2, 2026-08-18), so this was
purely wiring `ExpressionTranslator`/`SsisFnEmitter` up to call through to C# equivalents of
already-proven behavior.

**What was added:** `ExpressionTranslator.TranslateScalar` gained `TRIM(x)` (via the existing
`WrapOneArgFunction` helper, same shape as `UPPER`) and `FINDSTRING(x, search, occurrence)` (a
new `TranslateFindString`, translating all three args recursively via `TranslateScalar` rather
than requiring literals the way `SUBSTRING`'s start/length do -- necessary because the one real
evidenced call always nests another function, `FINDSTRING(TRIM(Email),"@",1)`). Both map to new
`SsisFn.Trim`/`SsisFn.FindString` bodies in `SsisFnEmitter`, ported directly from
`Functions.cs`'s own already-tested algorithm (empty search string never matches, occurrence < 1
is a hard error, matches may overlap). `TransformEmitter.CollectSsisFunctions` (made `internal`,
not `private`, matching `MapPipelineTypeToSsisType`'s existing sharing precedent) detects both
via the same `SsisFn.X(` string-match convention as `Upper`/`Substring`/`Str`.

**A real, previously-latent bug was caught and fixed along the way, not shipped:**
`RouterEmitter` (Conditional Split condition translation) never tracked or returned which
`SsisFn.*` helpers a condition used, and never emitted a `using {ns}.Ssis;` line -- invisible
until now because `ISNULL`/`!`/comparisons (the only condition shapes supported before this
round) are all plain C# operators, never `SsisFn.*` calls. The moment a condition could contain
`FINDSTRING(TRIM(...))`, this would have silently generated code calling an unqualified
`SsisFn.FindString(...)` with no `using` and no guarantee `SsisFnEmitter` even emitted the
`FindString`/`Trim` bodies at all (since `PackageGenerator`'s `functionsUsed` set was fed only
from `TransformEmitter`, never from `RouterEmitter`). Fixed by giving `RouterEmitter.Emit` the
same `TransformEmitResult`-shaped return (`RouterEmitResult(EmitResult, SsisFunctionsUsed)`),
reusing `TransformEmitter.CollectSsisFunctions` on the condition's own emitted text, adding the
`using` line when non-empty, and threading `routerResult.SsisFunctionsUsed` into
`PackageGenerator`'s `branchFunctionsUsed` set -- committed only once every branch AND the router
succeed, same all-or-nothing discipline `GenerateConditionalSplitFlow`'s own doc comment already
established for `branchFiles`/`branchTables`.

**Verified against the real motivating case, same first check every gap this session used:**
re-running `ssisx generate` against `SSIS_From_Sandeep`'s actual `Package_Transforms.dtsx`:
`DFT_DerivedAndSplit`'s Conditional Split (`CSPLIT_Validity`) and its `FullName` Derived Column
(`(DT_WSTR,200)(TRIM(FirstName) + " " + TRIM(LastName))`) both now generate correctly --
confirmed by reading the emitted `CSPLIT_ValidityRouter.cs`/`CustomerEnrichedValidTransform.cs`
directly: `SsisFn.FindString(SsisFn.Trim(row.Email), "@", 1) > 0` and
`WidthGuard.Wstr($"{SsisFn.Trim(row.FirstName)} {SsisFn.Trim(row.LastName)}", 200, ...)`, both
with a correct `using Package_Transforms.Ssis;`, and `Ssis/SsisFn.cs` contains exactly `Trim`/
`FindString` (not `Upper`/`Substring`, unused by this component) -- proof the whole plumbing
chain (translate -> collect -> emit -> wire) works end to end, not just the isolated translator.

**Honest limit, found by the same regeneration, not hidden:** `DFT_DerivedAndSplit`'s OTHER two
computed columns (`CustomerEnriched.EmailDomain`, a ternary `FINDSTRING(...) > 0 ? SUBSTRING(...)
: "(none)"`, and `.TenureDays`, `ISNULL(...) ? -1 : DATEDIFF(...)`) still correctly degrade to
`GenerationGap`s -- `ExpressionTranslator` has no ternary/ConditionalExpr support at all, and
`DATEDIFF` isn't in the function switch. Both are separate, unrelated gaps (a value-level `? :`
translator, and a new function), correctly out of scope for this round; `TransformEmitter`
degrades per-COLUMN not per-component (confirmed by reading its own `Emit` loop), which is why
`FullName` succeeding and `EmailDomain`/`TenureDays` failing coexist in the same Derived Column
without one blocking the other.

**A second, genuinely separate gap was also newly exposed by this same regeneration, not caused
by it, and deliberately NOT fixed this round:** `DCONV_Types` (`Microsoft.DataConvert`, a Data
Conversion component upstream of `CSPLIT_Validity`) converts raw CSV columns (`CustomerID`,
`SignupDate`) into renamed/retyped ones (`CustomerID_i4`, `SignupDate_dt`) that downstream
expressions/passthrough columns reference by their NEW name. Building the full generated project
against a copy of `Etl.Core` failed with `CS1061` ("`DFT_DerivedAndSplitCsvRow` does not contain
a definition for `CustomerID_i4`") -- the generated CSV row type is built only from the Flat File
source's own raw declared columns; nothing in `PipelineResolver`/`LineageBuilder`/the row-type
emitters accounts for a Data Conversion component sitting in between. This is a new, unmodeled
component type (never evidenced before `SSIS_From_Sandeep`), unrelated to expression translation
-- left as a named, separate gap for a future round rather than folded into this one, the same
"don't let one fix become two" discipline as Conditional Split's own Lookup-ordering fix.

**Own dedicated fixture, built to prove FINDSTRING/TRIM in isolation, unblocked by the Data
Conversion gap above:** `tests/Ssis.Extract.Tests/Fixtures/SyntheticFindStringTrim.dtsx`
(`Ssis.Extract.FixtureBuilder`'s new `findstring-trim` mode) -- OLE DB Source (SqlCommand) ->
Derived Column (`TrimmedEmail <- TRIM(Email)`, shared, proving TRIM as a VALUE-producing
expression, not just inside a condition) -> Conditional Split (case
`FINDSTRING(TRIM(Email),"@",1) > 0` -> Valid; default -> Invalid, the exact nested shape from the
real package, minus the `ISNULL`/`CustomerID_i4` half already proven working last session) -> 2
OLE DB Destinations. **Generated, built against a copy of `Etl.Core` (0 warnings/0 errors), and
actually RUN against `.\SQLFORPOC_2022`**: 3 seed rows (one with leading+trailing whitespace
around a real `@`, one with leading whitespace only, one with no `@` at all) -- read back
afterward, not just trusted from the exit code: `SyntheticFindStringTrimValid` got
`ID=1 ("  alice@example.com  " -> "alice@example.com")` and
`ID=3 (" carol@sample.org" -> "carol@sample.org")`, `SyntheticFindStringTrimInvalid` got
`ID=2 ("bobexample.com", no "@")` -- TRIM's own whitespace-stripping effect visible in the stored
`TrimmedEmail` column, FINDSTRING's own routing decision correct, both proven by real row content
rather than an assumption. Backing tables:
`tests/Ssis.Extract.Tests/Fixtures/synthetic-findstring-trim-tables.sql`.

Also added: two `ExpressionTranslatorTests` (TRIM as a value expression; the exact real-case
`FINDSTRING(TRIM(...))>0` nested condition) and one `RouterEmitterTests` proving the router-level
fix specifically -- that emitting a condition using both functions produces a correct `using`
line and reports `{"FindString", "Trim"}` as the functions used, not just that
`ExpressionTranslator` alone gets the right string. Regression: 86/86 in
`Ssis.Extract.Codegen.Tests` (was 83), same 3 pre-existing unrelated failures elsewhere (someone
else's in-progress SSDT edits to `SSIS/LoadReferenceData.dtsx`/`SSIS/SSIS.dtproj`, not this
session's or the extractor's).

**Remaining gaps from the `SSIS_From_Sandeep` sweep, unchanged by this round:** Flat File
Destination, Excel Source, ForEach Loop containers, Conditional Split -> Sort -> Merge (all
pre-existing), plus the two newly-named ones above (ternary/`DATEDIFF` value expressions, Data
Conversion component support).

### Ternary (`cond ? whenTrue : whenFalse`) and unary negate in ExpressionTranslator (2026-08-27)

Picked up immediately after FINDSTRING/TRIM, same session, same discipline: check whether the
semantics are already oracle-verified before doing any new derivation work. They were --
`Ssis.Runtime.Expressions.Ast.Conditional` and `UnaryExpr{Op: Negate}` already exist, are already
parsed, and are already evaluated by `Evaluator.cs` (`EvaluateConditional`'s own "Measured:"
comment documents the NULL-condition rule; `-5 % 3 => -2` in the gate-2 oracle corpus exercises
Negate). Nothing needed re-deriving via the Windows-only `Ssis.Expression.Oracle` -- this was
purely wiring `ExpressionTranslator` through to already-proven behavior, the same shape of task
as FINDSTRING/TRIM.

**What was added:** `ExpressionTranslator.TranslateScalar` gained `Conditional` (a new
`TranslateTernary`: condition translated via the existing `TranslateCondition` verbatim -- same
comparisons/&&/||/!/ISNULL surface a Conditional Split case already gets, nothing new; both
branches translated via `TranslateValue`, not `TranslateScalar` directly, so a branch that's
itself a concat chain would work too, though that's not evidenced anywhere) and
`UnaryExpr{Op: Negate}` (a new `TranslateNegate`, wrapping the operand in `-(...)`). Both are
pure, self-contained value-context translations -- no `SsisFn.*` helper needed for either (a C#
ternary and C# unary minus ARE the semantics, unlike FINDSTRING/TRIM which needed a helper
method), so no `SsisFnEmitter`/`CollectSsisFunctions` changes were needed this round.

**Verified against the real motivating case, regenerating `Package_Transforms.dtsx` twice (once
per fix) to watch the gap reason sharpen with each addition -- confirms real progress, not just
"stopped erroring":**
1. Before this round: `CustomerEnriched.EmailDomain`/`.TenureDays` both reported "unsupported
   expression shape: Conditional" -- the ternary itself was the very first thing blocking either
   column, nothing past it was even attempted.
2. After adding ternary alone: `EmailDomain` sharpened to "unsupported expression shape:
   FunctionCall" (its `whenTrue` branch, `SUBSTRING(TRIM(Email), FINDSTRING(TRIM(Email),"@",1) +
   1, 100)`, has a non-literal SUBSTRING start argument -- `ExpressionTranslator`'s SUBSTRING
   case still requires literal `IntLiteral` start/length, a real, SEPARATE, deliberately
   untouched gap this round, see below). `TenureDays` sharpened to "unsupported expression
   shape: UnaryExpr" -- its own `-1` literal, confirming the parser genuinely never folds unary
   minus into a literal (`Parser.cs`'s own `ParseUnary`, verified by reading it directly rather
   than assumed).
3. After adding negate too: `TenureDays` sharpened again, to "unsupported expression shape:
   FunctionCall" -- now cleanly isolated on `DATEDIFF` itself (never oracle-verified, a genuinely
   new, separate function to prove before it can be wired up), with nothing else in its way.

**Two deliberately untouched, newly-precise gaps, named rather than folded into this round:**
- **`SUBSTRING` with a non-literal start argument is a real, DEEPER problem than it looks, not
  just "loosen a literal requirement."** The real evidenced call needs `FINDSTRING(...) + 1` as
  the start -- a `BinaryExpr(Add)` between a `FunctionCall` and an `IntLiteral`. But
  `TranslateValue`'s own dispatcher treats EVERY `BinaryOp.Add` as string concatenation
  unconditionally (`TranslateConcat`, wrapping the result in `$"...{...}..."`) -- correct for
  every evidenced case so far (all string operands), but flatly wrong for integer arithmetic:
  routing `FINDSTRING(...) + 1` through `TranslateConcat` would silently produce
  `$"{SsisFn.FindString(...)}1"` (string concatenation of the count and the literal "1") instead
  of `SsisFn.FindString(...) + 1` (integer addition) -- a real semantic bug, not a translation
  gap, if guessed rather than designed. Fixing this properly needs operand-type-aware `+`
  dispatch (numeric-vs-string), the same kind of resolution `TranslateComparison` already does
  for `==`/`<` -- a real design decision, correctly out of scope for this round's "wire up
  already-proven semantics" scope.
- **`DATEDIFF` has never been oracle-verified** -- absent from `Functions.cs`'s `ReturnTypes`
  dictionary entirely, unlike every other function `ExpressionTranslator` already supports.
  Needs new Windows-only `Ssis.Expression.Oracle` work (date-part sign convention, which
  `"dd"`/`"yyyy"`-style literals are supported) before it can be wired up at all -- a
  new-semantics task, not a plumbing task, so a different shape of work than everything else
  this round.

**Own dedicated fixture, built to prove ternary/negate in isolation, unblocked by both gaps
above:** `tests/Ssis.Extract.Tests/Fixtures/SyntheticTernary.dtsx`
(`Ssis.Extract.FixtureBuilder`'s new `ternary` mode) -- OLE DB Source (SqlCommand) -> Derived
Column with two computed columns (`Category <- Amount > 1000 ? "High" : "Low"`; `SignFlag <-
Amount < 0 ? -1 : 1`, deliberately using only already-supported building blocks, not the real
package's own SUBSTRING/DATEDIFF-dependent branches) -> OLE DB Destination. **Generated, built
against a copy of `Etl.Core` (0 warnings/0 errors), and actually RUN against
`.\SQLFORPOC_2022`**: 4 seed rows (below/above/at-the-boundary Amount, plus one negative) --
read back afterward, not just trusted from the exit code: `500.00 -> Low/1`, `1500.00 ->
High/1`, `-250.00 -> Low/-1` (the negate branch genuinely exercised), `1000.00 -> Low/1` (the
exact boundary, `>` is strictly-greater). Backing tables:
`tests/Ssis.Extract.Tests/Fixtures/synthetic-ternary-tables.sql`.

Also added: three `ExpressionTranslatorTests` (ternary of string literals; ternary+negate, the
exact real-case shape; a ternary branch degrading to a gap rather than guessing, using DATEDIFF
as the deliberately-still-unsupported function). Regression: 89/89 in
`Ssis.Extract.Codegen.Tests` (was 86), same 3 pre-existing unrelated failures elsewhere.

### Numeric `+` dispatch and a computed SUBSTRING start argument (2026-08-27) -- closes `EmailDomain` fully

The immediate next pick after ternary/negate, same session: the SUBSTRING-with-a-non-literal-
start gap named (but deliberately left untouched) in that round's own writeup. Same
"check whether it's already oracle-verified before doing new derivation work" discipline as
every fix this session -- it was: `Ssis.Runtime.Expressions.Evaluator.Add` already branches on
operand type (string+string concats, numeric+numeric adds with type promotion), oracle-verified
(`1 + NULL(DT_I4)`, `(DT_WSTR,20)(1 + 2.5) => "3.5"` are both in the gate-2 corpus) -- so again,
pure plumbing.

**What was added:** `ExpressionTranslator.TranslateValue`'s dispatcher used to treat EVERY `+`
as string concatenation unconditionally -- correct for every case evidenced before this round
(all string operands) but flatly wrong for `FINDSTRING(...) + 1` (would have silently produced
`$"{SsisFn.FindString(...)}1"`, string concatenation of the count and the literal "1", instead of
integer addition -- a real semantic bug if guessed rather than designed). Fixed by extending
`TryResolveOperandType` (already used by `TranslateComparison` for string-vs-numeric comparison
dispatch) with a `KnownFunctionReturnTypes` lookup (UPPER/TRIM/SUBSTRING -> WStr, FINDSTRING ->
I4 -- deliberately NOT `Ssis.Runtime.Expressions.Functions.ReturnTypes`, which covers functions
this translator can't emit code for yet, like DATEDIFF; reusing it here would claim a type with
no way to produce the value), then having `TranslateValue` decide concat-vs-arithmetic by
whichever operand's type resolves, same pattern `TranslateComparison` already uses. `SUBSTRING`'s
own switch case was generalized from requiring literal `IntLiteral` start/length to translating
all three args recursively via `TranslateValue` (mirroring FINDSTRING's own earlier
generalization) -- a plain literal start/length still renders byte-identical text (confirmed by
a dedicated regression test), so no existing golden output moved.

**A genuine, positive side effect, not just a side effect of the main fix:** `TryResolveOperandType`
now resolving function-call return types means `TranslateCondition` can resolve MORE comparisons
than before -- `UPPER(Name) == UPPER(Other)` (previously `NotTranslatable`, "no operand with a
resolvable type", since neither side was a bare Reference/literal) now correctly translates to a
string comparison. Caught this by running the full suite: an existing test
(`TranslateCondition_ReportsAGap_WhenNeitherOperandHasAResolvableType`) started failing because
its own premise ("neither operand resolvable") stopped being true. Not a regression -- the test's
INTENT (prove a genuinely unresolvable comparison degrades to a gap) still needed proving, so it
was re-pointed at a shape that's still actually unresolvable (`(DT_WSTR,10)A == (DT_WSTR,10)B` --
a `Cast` node translates fine as a value but `TryResolveOperandType` has no `Cast` case), and a
new test was added specifically asserting the `UPPER(...)==UPPER(...)` case now succeeds.

**Verified against the real motivating case, regenerating `Package_Transforms.dtsx` a third
time:** `CustomerEnriched.EmailDomain` is now GONE from the gap list entirely -- confirmed by
reading the emitted `CustomerEnrichedValidTransform.cs` directly:
`EmailDomain = WidthGuard.Wstr(((SsisFn.FindString(SsisFn.Trim(row.Email), "@", 1) > 0) ?
SsisFn.Substring(SsisFn.Trim(row.Email), (SsisFn.FindString(SsisFn.Trim(row.Email), "@", 1) + 1),
100) : "(none)"), 200, ...)` -- every piece (nested FINDSTRING/TRIM, ternary, numeric `+`, a
computed SUBSTRING start) composing correctly in one column. `TenureDays` remains gapped,
correctly, cleanly isolated on `DATEDIFF` alone (unchanged from the ternary/negate round -- still
needs new Windows-only oracle work, out of scope here too).

**Own dedicated fixture -- and unlike every other fixture this session, this one reproduces the
REAL package's own expression VERBATIM, not a simplified stand-in, because by this point every
piece it needs is actually supported:** `tests/Ssis.Extract.Tests/Fixtures/SyntheticEmailDomain.dtsx`
(`Ssis.Extract.FixtureBuilder`'s new `email-domain` mode) -- OLE DB Source (SqlCommand) -> Derived
Column (`EmailDomain <- FINDSTRING(TRIM(Email),"@",1) > 0 ? SUBSTRING(TRIM(Email),
FINDSTRING(TRIM(Email),"@",1) + 1,100) : "(none)"`, copied verbatim from RBC_Demo_ETL's own
DER_Enrich) -> OLE DB Destination. **Generated, built against a copy of `Etl.Core` (0
warnings/0 errors), and actually RUN against `.\SQLFORPOC_2022`**: 3 seed rows covering all three
logical paths (an ordinary `@`, no `@` at all, and an `@` only found after trimming
leading/trailing whitespace) -- read back afterward, not just trusted from the exit code:
`alice@example.com -> example.com`, `bobexample.com -> (none)`,
`  carol@sample.org   -> sample.org` (the trimmed value, not the padded one -- proving TRIM
matters to the RESULT, not just routing, the same distinction `SyntheticFindStringTrim.dtsx`
made for its own condition-only usage). Backing tables:
`tests/Ssis.Extract.Tests/Fixtures/synthetic-email-domain-tables.sql`.

Also added: four `ExpressionTranslatorTests` (the new `UPPER(...)==UPPER(...)` capability;
numeric `+` producing arithmetic addition instead of concat; SUBSTRING with a computed start
argument, the exact real-case shape; a regression guard proving the pre-existing literal-args
SUBSTRING shape renders identical text). Regression: 93/93 in `Ssis.Extract.Codegen.Tests` (was
89), same 3 pre-existing unrelated failures elsewhere.

**Remaining known gaps, unchanged:** Flat File Destination, Excel Source, ForEach Loop
containers, Conditional Split -> Sort -> Merge, `DATEDIFF` (needs new oracle work), Data
Conversion component support (needs new row-type-generation modeling).

### `DATEDIFF("dd", ...)` support -- new oracle work, plus a genuine new gap found (2026-08-27)

The immediate next pick after numeric `+`, same session -- the one remaining piece of
`TenureDays` (`ISNULL(SignupDate_dt) ? -1 : DATEDIFF("dd",SignupDate_dt,GETDATE())`), and the
first gap this session that genuinely needed NEW semantics derivation rather than wiring up
something already oracle-verified (`DATEDIFF` was entirely absent from `Functions.cs`'s
`ReturnTypes` table before this).

**New oracle corpus block, `src/Ssis.Expression.Oracle/Program.cs`** (8 cases, run against the
real SSIS 22 evaluator on this machine): resolved three real open questions rather than
guessing by T-SQL analogy the way `DatePart`'s own older, explicitly-disclaimed code does for
every part except `"yy"`. Measured: sign convention is `end - start` (swapping the two args
negates the result); `"dd"` counts calendar-**DAY-BOUNDARY** crossings, exactly like T-SQL's
own `DATEDIFF("dd",...)`, NOT elapsed 24-hour periods (23:00 -> next day 01:00, only 2 hours
elapsed, measured as `1`, not `0`); `DT_DBDATE`/`DT_DBTIMESTAMP` mix freely with no explicit
cast needed (matches `SignupDate_dt`/`GETDATE()`'s own real shapes). Corpus rows added at
`tests/Ssis.Runtime.Expressions.Tests/Fixtures/ssis-expression-oracle.tsv` lines 123-130 (a
byte-preserving PowerShell insert, not a hand edit -- see trap 18), `Functions.DateDiff`
implements exactly this (only `"dd"` -- any other part is a hard error, deliberately NOT
extrapolated the way `DatePart` was, since day-boundary counting is genuinely easy to get wrong
for other parts and none of them are measured). 149/149 in
`Ssis.Runtime.Expressions.Tests` (was 141).

**`ExpressionTranslator` additions:** a `DATEDIFF` case (`TranslateDateDiff`, rejecting any
datepart literal other than `"dd"` rather than guessing) mapping to a new
`SsisFn.DateDiffDays`; and -- a real, previously-latent gap this closed along the way, not
scope creep -- `GETUTCDATE()`/`GETDATE()` used to be recognized ONLY at a column's own root
(`TranslateColumn`'s own top-level check), never when NESTED inside another call's argument.
The real `TenureDays` expression needs exactly that (`GETDATE()` as `DATEDIFF`'s own third
argument), so the identical case was added to `TranslateScalar`'s switch too.

**Verified against the real motivating case, regenerating `Package_Transforms.dtsx` a fourth
time:** `CustomerEnriched.TenureDays` is now GONE from the gap list entirely -- confirmed by
reading the emitted transform: `TenureDays = ((row.SignupDate_dt is null) ? -(1) :
SsisFn.DateDiffDays(row.SignupDate_dt, ctx.LoadedAtUtc))`. Combined with the numeric-`+`
round's `EmailDomain` fix, `DFT_DerivedAndSplit`'s entire EXPRESSION-TRANSLATION story is now
complete -- every computed column in that flow generates correctly. What still blocks that flow
from reaching a runnable `Program.cs` is purely the separate, already-documented Data
Conversion row-type gap (`CustomerID_i4`/`SignupDate_dt` don't exist on the generated CSV row
type) -- unrelated to expression translation, unchanged by this round.

**A second, genuinely new, previously-invisible gap was found building this round's own
fixture, not shipped as a guess:** `SqlRowReaderEmitter`/`EntityEmitter`/`SqlRowEmitter` never
track column NULLABILITY at all -- confirmed by grepping every one of them for
"Nullable"/"IsNullable" and finding nothing. A SQL `DATE NULL` column still generates as a
non-nullable C# `DateOnly`, so `ISNULL(x)`'s own `(x is null)` translation (already correct at
the text level, unit-tested and proven in isolation) fails to COMPILE (`CS0037: Cannot convert
null to 'DateOnly'`) once real code is actually built against it -- caught only by actually
building the generated project, exactly the discipline this whole "verify end-to-end, not just
unit tests" approach exists to catch. **Not specific to dates or to DATEDIFF** -- the identical
failure would hit ANY nullable value-typed column (a nullable `int`, `decimal`, `bool`, ...)
wrapped in `ISNULL(...)` anywhere in this generator's output; strings are unaffected only
because a reference type is nullable regardless. A real architectural question (should EVERY
generated property become nullable, or only ones actually referenced via `ISNULL`/compared to
NULL -- and if the latter, from what evidence, since nullability isn't tracked in the pipeline
model at all today) -- deliberately left unfixed and named here rather than folded into this
round, same "don't let one fix become two" discipline as Conditional Split's own
Lookup-before-split ordering fix.

**A related, DIRECTLY-in-scope type mismatch WAS fixed, unlike the nullability gap above:**
`SsisFn.DateDiffDays` needed a second overload, `DateDiffDays(DateOnly start, DateTime end)`
(`start.ToDateTime(TimeOnly.MinValue)` then the same day-boundary math), because a `DATE`
column generates as `DateOnly` while `GETDATE()`/`ctx.LoadedAtUtc` is always `DateTime` -- this
is inherent to DATEDIFF itself (it always needs to bridge whatever concrete types its two date
arguments resolve to), not a symptom of the nullability gap, so fixing it here was correctly in
scope where the nullability gap was not.

**Own dedicated fixture, redesigned once the nullability gap above was discovered:**
`tests/Ssis.Extract.Tests/Fixtures/SyntheticDateDiff.dtsx` (`Ssis.Extract.FixtureBuilder`'s new
`datediff` mode) -- deliberately NOT the full `ISNULL(...) ? -1 : DATEDIFF(...)` expression
(that would hit the nullability gap and fail to compile, for reasons unrelated to DATEDIFF
itself); instead `TenureDays <- DATEDIFF("dd",SignupDate,GETDATE())` against a `NOT NULL DATE`
column, proving DATEDIFF's own arithmetic in isolation. The full real expression is still
proven correct at the TEXT level by a dedicated `ExpressionTranslatorTests` case
(`TranslateColumn_TranslatesTheRealTenureDaysExpression_...`). **Generated, built against a
copy of `Etl.Core` (0 warnings/0 errors -- the `DateOnly` overload fix confirmed necessary and
sufficient), and actually RUN against `.\SQLFORPOC_2022`**: 3 seed rows with dates computed
relative to `SYSUTCDATETIME()` at seed time (not hardcoded literals, so the fixture stays valid
regardless of what day it's run on) -- read back afterward: `2026-08-17 -> TenureDays=10`
(10 days ago), `2026-08-27 -> TenureDays=0` (today), `2026-09-01 -> TenureDays=-5` (5 days in
the FUTURE, specifically included to prove the negative-sign convention end to end, not just
the positive case). Backing tables:
`tests/Ssis.Extract.Tests/Fixtures/synthetic-datediff-tables.sql`.

Also added: two `ExpressionTranslatorTests` (the full real `TenureDays` expression now
translating, replacing what used to be this test suite's own "deliberately unsupported"
example; a still-genuinely-unsupported non-`"dd"` datepart degrading to a gap, preserving that
coverage) and one `SsisFnEmitterTests` (both `DateDiffDays` overloads emitted together).
Regression: 95/95 in `Ssis.Extract.Codegen.Tests` (was 94), 149/149 in
`Ssis.Runtime.Expressions.Tests` (was 141), same 3 pre-existing unrelated failures elsewhere.

**Remaining known gaps, updated:** Flat File Destination, Excel Source, ForEach Loop
containers, Conditional Split -> Sort -> Merge, Data Conversion component support (row-type
generation), and the newly-named nullable-value-typed-column gap above. `DATEDIFF` itself is
closed for the one evidenced datepart (`"dd"`) -- any other datepart remains a deliberate gap.

### Nullable value-typed columns -- the minimal, evidence-based version (2026-08-27)

Picked up immediately after DATEDIFF, same session, after sizing it live with the user first:
the first pass at "just make the property nullable" turned out to need a real design decision
once the actual evidenced usage pattern was traced through -- `ISNULL(x) ? fallback : f(x)`
only works if `f(x)`'s own reference to `x` is unwrapped to a non-nullable value, and nothing
in a naive translation would express "the ternary's OWN condition already ruled out null" to
the type system. Framed the tradeoff to the user before building it (AskUserQuestion) rather
than quietly shipping a guess; they picked the minimal version below.

**The core design decision, stated as a real tradeoff, not hidden:** a column becomes
"nullable-inferred" ONLY from direct evidence -- a bare `ISNULL(x)` check somewhere in the same
Derived Column's own expressions (a `.dtsx` carries no nullability concept at all, confirmed the
same way `PrimaryKeyInference`'s own doc comment already established for primary keys). Once
inferred nullable, EVERY OTHER reference to that column (i.e. everywhere except inside the
`ISNULL` check itself) is unwrapped with `.Value`, trusting that any code reaching it already
passed through the same guard. **This is not proven null-safety** -- a column referenced with
no `ISNULL` guard anywhere would still translate normally (non-nullable), but a HYPOTHETICAL
future expression that referenced a nullable-inferred column WITHOUT going through its own
guard would throw a `InvalidOperationException` at runtime (`.Value` on a null) rather than
degrade to a `NotTranslatable` gap at generation time. Accepted deliberately: full null-flow
analysis (proving the value truly can't be null at each specific use site) is a much larger
undertaking, and the one real evidenced pattern (guard-then-use, in a ternary) is exactly what
this handles correctly.

**What was added:**
- `NullabilityInference.cs` (new) -- walks a Derived Column's own output expressions (not just
  the root; the real case has `ISNULL(x)` as a ternary's own *condition*, so the AST walk
  recurses through `BinaryExpr`/`UnaryExpr`/`Cast`/`Conditional`/other `FunctionCall` args too)
  and collects the NAME of any bare `Reference` passed directly to `ISNULL(...)`. Deliberately
  scoped to Derived Column expressions only (not Conditional Split conditions -- RouterEmitter's
  own, separate reference-resolution path) and SQL sources only (not CSV -- CsvRowEmitter/
  ClassMapEmitter untouched) -- the one real evidenced need is a Derived Column over a SQL
  source; RBC_Demo_ETL's own CSV-sourced equivalent is separately blocked on the (also unfixed)
  Data Conversion gap anyway, so a CSV-sourced version of this fix couldn't be end-to-end
  verified against it regardless. Both are named, explicit follow-ups, not silently skipped.
- `ExpressionTranslator.cs`: a new `ColumnReference` record (`CSharpExpression, Type,
  IsNullable = false`) replaces the raw `(string, SsisType)` tuple everywhere (19 call sites,
  mechanical) as the `references` dictionary's value type. `TranslateScalar`'s `Reference` case
  appends `.Value` when `IsNullable`; `TranslateIsNull` was changed to resolve a bare-Reference
  argument directly against the RAW (still-nullable) expression instead of routing through
  `TranslateScalar` -- routing through it would have double-applied the `.Value` unwrap and
  broken the null check the function exists to perform in the first place.
- `SqlRowEmitter`/`EntityEmitter`: append `?` to a nullable-inferred column's CLR type
  (skipped for `string`, a reference type that needs no type-level change to hold null).
  `EntityEmitter` matches by the DESTINATION column's own buffer name (`PipelineColumnName`),
  since a passthrough column's entity property must mirror its source row property's own
  nullability or the plain assignment between them won't compile.
- `SqlRowReaderEmitter`: guards the read with `reader.IsDBNull(ordinal) ? null :
  reader.GetFieldValue<T>(ordinal)` -- this one applies EVEN TO a nullable string column
  (unlike the type-name change above), since `GetFieldValue<T>` throws on a genuinely-null
  value regardless of the C# property's own nullable annotation; ADO.NET only ever checks
  `IsDBNull`, never the CLR type.
- `PackageGenerator.cs`: computes the nullable-name set once per single-destination flow (from
  `flow.DerivedColumn`, before `ResolveFlowSource`/`EntityEmitter` are even called) and threads
  it through; the Conditional Split flow path passes nothing (an empty set), matching the
  RouterEmitter scope decision above.

**Verified two ways:** a new `NullabilityInferenceTests.cs` (4 tests: the ternary-condition
shape, an `&&`-condition shape, no-`ISNULL` returns empty, `ISNULL(UPPER(x))` does NOT flag
`x` -- only a bare Reference argument counts) plus a new `ExpressionTranslatorTests` case
proving the exact real TenureDays expression now translates to
`((row.SignupDate is null) ? -(1) : SsisFn.DateDiffDays(row.SignupDate.Value, ctx.LoadedAtUtc))`
when `SignupDate` is nullable-inferred (the *existing* non-nullable-case test, added last
round, is untouched and still passes -- `IsNullable` defaults `false`, so nothing about
previously-working translation changed). **Then closed the loop for real**: `SyntheticDateDiff.dtsx`
(the fixture built last round specifically WITHOUT the `ISNULL(...)` wrapper to sidestep this
exact gap) was updated back to the full, verbatim RBC_Demo_ETL expression, its backing
`SignupDate` column made nullable again with a genuine `NULL` seed row. **Generated, built
against a copy of `Etl.Core` (0 warnings/0 errors -- confirming `DateOnly?`/`SqlRowReaderEmitter`'s
null-guard/the `.Value` unwrap all compose correctly), and actually RUN against
`.\SQLFORPOC_2022`**: 4 rows, read back afterward -- `2026-08-18 -> 10`, `2026-08-28 -> 0`,
`2026-09-02 -> -5`, `NULL -> -1` (the ISNULL branch, genuinely exercised with a real NULL row
for the first time this session). Regenerating the real `Package_Transforms.dtsx` showed NO
change (expected -- it's CSV-sourced, outside this round's scope, still separately blocked on
Data Conversion). Regression: 100/100 in `Ssis.Extract.Codegen.Tests` (was 95), same 3
pre-existing unrelated failures elsewhere.

**Remaining known gaps, updated:** Flat File Destination, Excel Source, ForEach Loop
containers, Conditional Split -> Sort -> Merge, Data Conversion component support, Conditional
Split condition nullability (RouterEmitter), and CSV-source nullability (CsvRowEmitter/
ClassMapEmitter) -- the last two are this round's own named follow-ups.

### Data Conversion component (2026-08-28) -- closes the last `SSIS_From_Sandeep` gap named above

Picked up as the next round after nullable value-typed columns, same "run a real probe before
guessing" discipline that built every other empirically-derived piece of this tool. Unlike
DATEDIFF (a new SSIS *expression* function, needing `Ssis.Expression.Oracle`), Data Conversion
is a pipeline *component* whose behavior (specifically: what `IgnoreFailure` actually does on a
genuine conversion/truncation failure) is not documented anywhere trustworthy enough to guess
from -- so the very first step was building a real fixture and running it through the real SSIS
engine, before any reader/codegen code existed.

**The probe, run for real via `dtexec` against `.\SQLFORPOC_2022`/`SsisPoC`** (backing tables:
`tests/Ssis.Extract.Tests/Fixtures/synthetic-data-conversion-tables.sql`; fixture: `tests/
Ssis.Extract.Tests/Fixtures/SyntheticDataConversion.dtsx`, `Ssis.Extract.FixtureBuilder`'s new
`data-conversion` mode -- reproduces RBC_Demo_ETL's own `Package_Transforms.dtsx` shape exactly:
`OLE DB Source` (SqlCommand: `ID, CustomerIdText, SignupDateText`, all plain strings) ->
`Data Conversion` (`DCONV_Types`: `CustomerIdText` -> `CustomerId_i4` DT_I4,
`SignupDateText` -> `SignupDate_dt` DT_DBDATE, both dispositions IgnoreFailure) ->
`OLE DB Destination`) -- seeded five rows: a valid pair, a valid-but-whitespace-padded pair, an
invalid pair (`"not-a-number"`/`"not-a-date"`), an empty-string pair, and a NULL pair. **Measured
result, read back from the real target table after a real `DTSER_SUCCESS` run:** the row is
**never dropped** -- all 5 rows landed at the destination -- and a failing/empty/NULL conversion
simply yields `NULL` in that ONE output column, independent of every other column on the same
row (`ID=3` -> `NULL/NULL`, `ID=4` -> `NULL/NULL`, `ID=5` -> `NULL/NULL`; `ID=2`'s
whitespace-padded values parsed correctly -- confirms locale-neutral trimming). This is exactly
what `ssisx generate`'s new `SsisFn.ToNullableI4`/`SsisFn.ToNullableDate` helpers reproduce, and
what a later end-to-end run of the *generated* code was diffed against (exact match, see below).

**Two real object-model facts nailed down building the fixture, neither guessed from
Microsoft's own (evidently outdated) Data Conversion Transformation SDK sample, which shows a
different API surface than this GAC's real `IDTSDesigntimeComponent100` uses**: `MapOutputColumn`
is NOT how a converted output column is created -- it's the OUTPUT-side mirror of
`MapInputColumn`, for wiring an EXISTING output column to an external metadata column, unrelated
to creating one. The real mechanism is the same `InsertOutputColumnAt` every other bespoke
Derived-Column-shaped fixture in this file already uses, plus `SetOutputColumnProperty(output.ID,
newCol.ID, "SourceInputColumnLineageID", vcol.LineageID)` to point the new column back at its
source. `IDTSOutputColumn100.ErrorRowDisposition`/`.TruncationRowDisposition` are ordinary
settable properties (`DTSRowDisposition` enum, confirmed via reflection), not custom properties.

**A real, unrelated destination-wiring bug was caught and fixed building this fixture, not
shipped as a workaround.** The first version of the fixture failed dtexec's own runtime
validation with `VS_NEEDSNEWMETADATA` (0xC004706B) on the OLE DB Destination -- root-caused
(not guessed) by dumping every column's actual `DataType`/`Length`/`Precision`/`Scale` via a
throwaway PowerShell object-model probe and finding they matched perfectly; the real cause was
`Ssis.Extract.FixtureBuilder`'s own shared `ResolveOleDbMetadata` helper marking EVERY virtual
input column reaching a destination as `UT_READONLY`, including the two untouched RAW string
columns (`CustomerIdText`/`SignupDateText`) that flow through a synchronous Data Conversion
component but were never wanted at the destination at all -- the first fixture in this file
where a component's own raw input columns are genuinely superfluous at the destination (every
earlier fixture's virtual input columns happened to already be a subset of what the destination
wanted). Fixed generically in the shared helper (filter to only the destination's own external
column names before marking `UT_READONLY`) -- a no-op for every prior fixture, verified by the
full suite staying green.

**Extraction needed almost no new machinery** -- confirmed by reading the code before writing
any: every converted column's type/length/precision/scale/dispositions are already captured
generically by the pre-existing `PipelineOutputColumnSpec` every other component's output columns
go through. The only genuinely bespoke piece is `DataConversionColumnSpec.SourceColumnLineageId`:
unlike Derived Column (whose output column carries an `Expression` property with embedded
`#{lineageId}` references inside a larger expression string), a Data Conversion output column
instead carries a single-purpose `SourceInputColumnLineageID` custom property whose ENTIRE value
is one `#{lineageId}` reference -- `PipelineReader.StripLineageRef` strips the wrapper, nothing
more.

**The real codegen gap, exactly as this file's own "remaining gaps" list already named it, was
in `LineageBuilder`, not any row-type emitter.** `LineageBuilder.Build` only ever derived an edge
from a column's own `Expression` (Derived Column's mechanism) -- a Data Conversion output column
has no `Expression` at all, so it was neither an edge NOR a `ConstantColumns` entry: invisible to
every lineage-based consumer, which is exactly the `CS1061 does not contain a definition for
CustomerID_i4` failure this file's own earlier `SSIS_From_Sandeep` note already predicted.
`LineageBuilder` now also walks each component's own `DataConvertPayload.Columns`, emitting a new
`"DataConversion"`-kind edge from the resolved raw source column straight to the converted output
column -- confirmed correct by reading the extracted `spec.json`'s own lineage edges directly,
not just trusting the code.

**`TransformEmitter` needed a genuinely new resolution path, not a tweak to the existing one.** A
destination column matching a Data Conversion's own output column name is now resolved via
`TranslateDataConversion`: finds the `"DataConversion"` lineage edge, wraps the raw source
column reference in `SsisFn.ToNullableI4`/`SsisFn.ToNullableDate` (the only two evidenced target
types -- anything else is a named gap, never guessed) -- e.g. `CustomerId_i4 =
SsisFn.ToNullableI4(row.CustomerIdText)`. Both helpers take `string?` and return a nullable value
type, matching the measured IgnoreFailure semantics exactly (`.NET`'s own `int.TryParse`/
`DateOnly.TryParse` already trim whitespace, so no extra locale-neutral-parsing logic was
needed). **A converted column's destination-entity property is nullable BY CONSTRUCTION, not by
ISNULL evidence** the way `NullabilityInference`'s other entries are -- `IgnoreFailure` can
always null out the value regardless of whether anything downstream guards it, so
`PackageGenerator` unconditionally adds every `DataConvertPayload` column name (both sides: the
converted output name for `EntityEmitter`, and the raw source name -- resolved via the same new
lineage edge -- for `SqlRowEmitter`/`SqlRowReaderEmitter`) to the shared nullable-column-names set
that already existed for ISNULL-based inference. The pre-existing "no Derived Column found ->
direct-copy pipeline" gate in `PackageGenerator` was widened to `flow.DerivedColumn is null &&
flow.DataConversion is null` -- this fixture has NO Derived Column anywhere, proving the widened
gate rather than assuming it.

**A second real, previously-latent bug was caught building the Gate 3 run, not shipped --
found by actually building the generated project, not by unit tests.** `SqlRowReaderEmitter`
already had an `IsDBNull` guard mechanism (added for the earlier nullable-value-typed-column
round), correctly gated on the same nullable-column-names set -- so the reader itself came out
right immediately. But `SqlRowEmitter`'s row-type property emission had an explicit `column.
Type.ClrTypeName != "string"` exclusion ("a reference type needs no type-level change to hold
null" -- true in general C#, but this generated project builds with `<Nullable>enable</Nullable>`
+ `TreatWarningsAsErrors=true`), so a `string` property assigned a genuinely-null value
(`CustomerIdText`/`SignupDateText`, from the real seeded NULL row) was a **build ERROR**
(`CS8601`), not just a runtime risk -- the row type still said non-nullable `string` even though
the reader now correctly assigned it `null`. Caught only by actually building the generated
project against seeded NULL data and reading the compiler's own error, exactly the "verify by
running it" discipline this whole file is built on. Fixed by dropping the same exclusion from
`SqlRowEmitter` (string columns get the same nullable-set-driven `?` treatment as every other
type now) -- verified as a no-op for every prior fixture (none had ever had a nullable-inferred
STRING column reach `SqlRowEmitter` before this round) by the full suite staying green.
`EntityEmitter` still carries the identical-looking exclusion, now known to be equally wrong
under the same conditions -- deliberately NOT fixed this round (nothing in this fixture exercises
a nullable destination-entity string column), named here as a real, precise follow-up rather than
left implicit.

**Golden files needed a mechanical update, patched byte-preservingly, not regenerated.** Every
`PipelineComponentSpec` now serializes a `"DataConvert": null` field (alphabetically after
`ContactInfo`) for every pipeline component in both real PoC packages -- confirmed via a
byte-preserving PowerShell regex insert (trap 18's own discipline: `git diff --stat` showed
exactly 3 lines changed in `LoadEmployees.spec.json`, 6 in `LoadReferenceData.spec.json`, nothing
else), not `SSISX_REGENERATE_GOLDEN=1` (which would have silently absorbed the unrelated
in-progress SSDT edits already sitting in this working tree into the baseline).

**Verified end to end, the same real-corpus discipline as every other round:** `ssisx generate`
against the fixture, built against a copy of `Etl.Core` (0 warnings/0 errors once both bugs above
were fixed), truncated the real target table, and actually RUN against `.\SQLFORPOC_2022` --
read back afterward, not trusted from the exit code: `123/2026-01-15`, `456/2026-02-20`,
`NULL/NULL`, `NULL/NULL`, `NULL/NULL` -- an **exact row-for-row match against the real dtexec
probe run above**, the strongest evidence this tool's whole "gaps not guesses" philosophy can
produce: the generated C# reproduces genuine SSIS runtime behavior, not an assumption about it.
Two new unit tests added (`PackagePlannerTests.Plan_FindsTheDataConversionComponent_...`,
`PackageGeneratorTests.Generate_TranslatesADataConversion_...`, the latter asserting the emitted
`SsisFn.ToNullableI4`/`ToNullableDate` calls, the `string?`/`int?`/`DateOnly?` types, and the
`IsDBNull` reader guard directly). Regression: 102/102 in `Ssis.Extract.Codegen.Tests` (was 100),
same 3 pre-existing unrelated failures elsewhere (someone else's in-progress SSDT edits to
`SSIS/LoadReferenceData.dtsx`/`SSIS/SSIS.dtproj`, not this session's or the extractor's).

**Remaining known gaps, updated:** Flat File Destination, Excel Source, ForEach Loop containers,
Conditional Split -> Sort -> Merge, Conditional Split condition nullability (RouterEmitter),
CSV-source nullability (CsvRowEmitter/ClassMapEmitter), `EntityEmitter`'s own identical
string-nullability exclusion (named above, same fix shape as `SqlRowEmitter`'s), and Data
Conversion target types other than DT_I4/DT_DBDATE (e.g. DT_R8, DT_WSTR) -- all named, none
guessed at.

### Data Conversion + Conditional Split cross-reference (2026-08-28, same day) -- the round above did NOT actually close DFT_DerivedAndSplit

The section above's own closing claim ("closes the last `SSIS_From_Sandeep` gap") was premature,
caught immediately by re-running `ssisx generate` against the real `Package_Transforms.dtsx`:
`DFT_DerivedAndSplit`'s own gap was still there, unchanged. The SQL-source/single-destination
work above is real and correct as far as it goes, but the real flow's Data Conversion output
columns are referenced by TWO other components that round never touched: `CSPLIT_Validity`'s own
condition (`!ISNULL(CustomerID_i4) && FINDSTRING(...)`) and `DER_Enrich`'s own `TenureDays`
expression (`ISNULL(SignupDate_dt) ? -1 : DATEDIFF(...)`) -- both reference a Data Conversion
column from OUTSIDE the direct-to-destination path `TranslateDataConversion` alone covers. Sized
live with the user (`AskUserQuestion`) before starting, since "just wire up the cross-reference"
turned out to also need fixing two independent, pre-existing bugs neither visible until an actual
full build/run was attempted.

**What was added, mirroring the direct-to-destination path's own resolution rather than
duplicating it:**
- `TransformEmitter.TranslateDerivedColumn` (the mechanism `DER_Enrich.TenureDays` itself goes
  through) now checks whether a referenced column is a Data Conversion output BEFORE falling back
  to a plain `row.{name}` reference -- if so, it calls the same `TranslateDataConversion` the
  direct-to-destination case uses and marks the resulting `ColumnReference` `IsNullable: true`
  unconditionally (structural, not ISNULL-usage-based, same reasoning as the direct case).
- `RouterEmitter.Emit` gained two new optional parameters (`PipelineSpec? pipeline`,
  `PipelineComponentSpec? dataConversion`) and the identical recognition logic for a Conditional
  Split's own input columns -- reusing `TransformEmitter.TranslateDataConversion` directly
  (promoted from `private` to `internal`, same sharing precedent as `CollectSsisFunctions`/
  `MapPipelineTypeToSsisType`) rather than re-deriving the SsisFn wrapping a second time. This
  closes the "Conditional Split condition nullability" gap named above -- and closes it more
  cheaply than expected: because a Data Conversion column's nullability is STRUCTURAL (not
  ISNULL-usage-inferred), no new `NullabilityInference`-style evidence-gathering pass was needed,
  just the same recognition-and-delegate pattern already proven for Derived Column.
- `PackageGenerator`'s nullable-column-names computation (Data Conversion output names + their
  raw source column names) was factored out into a shared `ResolveNullableColumnNames(flow)`
  helper, called from BOTH the single-destination path and `GenerateConditionalSplitFlow` --
  threaded into `RouterEmitter.Emit`, `ResolveFlowSource`, `EntityEmitter.Emit`, and every
  branch's own `TransformEmitter.Emit` call in the split path, none of which received it before.

**This also revealed that "CSV-source nullability" -- named as a separate follow-up gap in the
original nullable-value-typed-columns round -- doesn't actually exist as a real, separate gap for
Data Conversion's own sake.** A Data Conversion's converted value is never materialized as a row
property at all (it's computed inline via `SsisFn.ToNullable*` wherever referenced); the CSV/SQL
row type only ever needs its RAW source column, which is already a plain `string`/`string?` --
no CLR type change needed regardless of source kind. Confirmed directly: regenerating the REAL
CSV-sourced `Package_Transforms.dtsx` after these changes produced a correct
`CSPLIT_ValidityRouter.cs` (`!((SsisFn.ToNullableI4(row.CustomerID) is null))`) and
`CustomerEnrichedValidTransform.cs` (`TenureDays = ((SsisFn.ToNullableDate(row.SignupDate) is
null) ? -(1) : SsisFn.DateDiffDays(SsisFn.ToNullableDate(row.SignupDate)!.Value,
ctx.LoadedAtUtc))`) with **zero** `CsvRowEmitter`/`ClassMapEmitter` changes -- CSV-source
nullability is retired as a named gap, not deferred further.

**Three real, previously-latent bugs were caught getting this far, each found by actually
building/running generated code, none by unit tests alone:**
1. **`EntityEmitter.Emit` inside `GenerateConditionalSplitFlow` was never passed
   `nullableColumnNames` at all** (a pre-existing gap in the Conditional Split path specifically,
   unrelated to Data Conversion -- it would have equally affected an ISNULL-inferred nullable
   column in ANY Conditional Split branch, just never observed before this was the first
   Conditional Split fixture with one). Symptom: the real package's own `CustomerEnriched.cs`
   entity generated `public int CustomerID` (non-nullable) while the transform assigned it
   `SsisFn.ToNullableI4(...)` (`int?`) -- `CS0266`, caught by actually building the regenerated
   real package's whole solution, not by the gap-count report (which showed 0 gaps for this flow
   and looked like success). Fixed by passing the same `nullableColumnNames` already computed for
   the router into this call.
2. **A genuine Roslyn nullable-flow-analysis limitation, not a logic bug**: `TranslateScalar`'s
   Reference case appends `.Value` unconditionally when `IsNullable`, which is correct for a
   plain row-property reference (`row.SignupDate.Value` -- Roslyn narrows a repeated
   member-access chain across an `is null` check on the identical expression) but produces
   `CS8629` ("nullable value type may be null") for a CALL-expression reference
   (`SsisFn.ToNullableDate(row.SignupDate).Value` -- Roslyn never assumes a method call is pure,
   so it cannot narrow across two separate invocations even of the exact same call). Both
   `CustomerEnrichedValidTransform.cs`/`...InvalidTransform.cs` failed to build with this exact
   error. Fixed with a targeted null-forgiving `!` inserted only when
   `ColumnReference.CSharpExpression` ends in `)` (the one syntactic signal distinguishing a call
   from a plain property chain in this codebase) -- semantically justified, not a blind
   suppression: the ISNULL guard that put this reference in the non-null branch calls the exact
   same pure, deterministic function with the exact same input, so it provably cannot be null
   here. Verified as a no-op for the pre-existing plain-row-property nullable case (its own exact
   golden string, asserted since the 2026-08-27 round, is unchanged).
3. **A real, previously-SILENT gap in `PackagePlanner.PlanDataFlow`, discovered by building the
   REAL package's whole regenerated solution for the first time (every earlier verification round
   trusted the gap-count report alone).** `DFT_SortAndMergeJoin` (a Merge Join over TWO Flat File
   Sources, `FF_SRC_Contacts`/`FF_SRC_Customers3`) used to report ZERO gaps and generate a
   `CustomerWithContactTransform.cs` that referenced `row.Email` -- a column that doesn't exist on
   the generated row type at all, since `PlanDataFlow` silently picks `pipeline.Components.
   FirstOrDefault(...)` for its ONE source, discarding the second entirely. This flow used to fail
   for an unrelated, ALREADY-gapped reason (no way to translate `CustomerID_i4` before Data
   Conversion support existed) -- Data Conversion's own widened acceptance criteria is what
   newly, silently let it through. Fixed with a generic, up-front check in `PlanDataFlow`: more
   than one source component (`Microsoft.FlatFileSource` or `SourceInfo.IsSqlSource`) in one Data
   Flow Task is now an explicit gap ("a merge/join across multiple sources... is not supported
   yet") rather than silently picking the first and generating broken code. Also correctly (and
   harmlessly) catches `SyntheticParallelShapes.dtsx`'s own `DFT_DirectCopy` (two INDEPENDENT
   source/destination pairs) one step earlier than its pre-existing "no Derived Column found"
   gate -- still gapped either way, just with a clearer reason now.

**Verified two ways, same discipline as every other round:**
1. **The real motivating case** -- regenerating `Package_Transforms.dtsx` after all three fixes:
   `DFT_DerivedAndSplit`'s gap is GONE entirely (confirmed by reading the emitted router/transform
   files directly, quoted above); `DFT_SortAndMergeJoin` is now a clean, explicit gap instead of
   silently-broken code; the WHOLE regenerated solution (all 3 successfully-wired flows plus a
   copy of `Etl.Core`) builds with **0 warnings/0 errors** -- the first time this real package's
   full generated solution was actually built end to end in one shot, not just spot-read.
2. **A new dedicated fixture**, `tests/Ssis.Extract.Tests/Fixtures/SyntheticDataConversionSplit.dtsx`
   (`Ssis.Extract.FixtureBuilder`'s new `data-conversion-split` mode): OLE DB Source -> Data
   Conversion (`CustomerIdText`/`SignupDateText` -> `CustomerId_i4`/`SignupDate_dt`) ->
   Conditional Split (`!ISNULL(CustomerId_i4)` -> Valid; default -> Invalid, isolating the
   ISNULL-cross-reference from FINDSTRING/TRIM, already proven separately) -> each branch's own
   Derived Column (`TenureDays <- ISNULL(SignupDate_dt) ? -1 : DATEDIFF(...)`) -> Union All -> one
   destination -- deliberately simpler than the real package's own condition so it isolates the
   STRUCTURAL cross-reference, the same "prove the minimal case first" discipline as every earlier
   fixture in this file. **Generated, built against a copy of `Etl.Core` (0 warnings/0 errors --
   confirming the null-forgiving fix was necessary AND sufficient), and actually RUN against
   `.\SQLFORPOC_2022`**, seeded with a valid pair (ID=1), a valid-CustomerID/NULL-date pair (ID=2,
   exercising the raw-source-column-nullability ordering fix directly -- this row crashed with
   `SqlNullValueException` before that fix, confirmed by reproducing the crash first), and an
   invalid-CustomerID pair (ID=3): read back afterward, not trusted from the exit code --
   `1: 123/2026-08-18/10`, `2: 456/NULL/-1` (routed Valid, ISNULL branch genuinely exercised),
   `3: NULL/2026-01-01/239` (routed Invalid, `SignupDate_dt` still converts fine independent of
   the split decision) -- every row correct, including both boundary conditions. Four new unit
   tests (`PackagePlannerTests.Plan_ReportsAnExplicitGap_ForADataFlowTaskWithMultipleSourceComponents`,
   `PackageGeneratorTests.Generate_ResolvesADataConversionColumn_...`,
   `RouterEmitterTests.Emit_ResolvesAConditionReferencingADataConversionColumn_...`,
   `ExpressionTranslatorTests.TranslateColumn_AppendsNullForgiving_...`). Regression: 106/106 in
   `Ssis.Extract.Codegen.Tests` (was 102), same 3 pre-existing unrelated failures elsewhere.

**Remaining known gaps, updated again:** Flat File Destination, Excel Source, ForEach Loop
containers, Conditional Split -> Sort -> Merge, multi-source merge/join flows (e.g.
`DFT_SortAndMergeJoin`'s own Merge Join -- now correctly gapped rather than silently broken, but
still not GENERATED), `EntityEmitter`'s own string-nullability exclusion (still not fixed --
nothing has yet exercised a nullable destination-entity string column), and Data Conversion
target types other than DT_I4/DT_DBDATE. Both "Conditional Split condition nullability" and
"CSV-source nullability", named as follow-ups above, are now closed/retired respectively.

### `EntityEmitter`'s string-nullability exclusion, fixed (2026-08-28, same day)

The smallest of the remaining named gaps, picked up immediately after the Data Conversion round
above: `EntityEmitter` had the exact same `column.Type.ClrTypeName != "string"` exclusion
`SqlRowEmitter` carried until it was fixed earlier the same day for a real, evidenced reason (a
`string` property assigned a genuinely-null value is a build ERROR, `CS8601`, under this
project's `<Nullable>enable</Nullable>` + `TreatWarningsAsErrors=true` -- not just a runtime
risk). Nothing in this session's own fixtures had exercised a nullable destination-entity
string column yet, so the mirrored bug in `EntityEmitter` was still live, just unobserved.

Fixed identically: dropped the `!= "string"` guard from the `isNullable` computation, matching
`SqlRowEmitter`'s own already-fixed logic and doc comment exactly. No behavior changes for any
existing column -- only a column both (a) genuinely a `string` and (b) present in
`nullableColumnNames` is affected, and no prior fixture had that combination.

**Verified with a unit test, not a new end-to-end fixture** -- this is pure mechanical parity
with an already-proven fix, not new semantics, so the same evidence bar didn't apply.
`EntityEmitterTests.Emit_MakesAStringPropertyNullable_WhenItsOwnColumnIsNullableInferred` reuses
the real `LoadEmployees` destination, passing `EmployeeKey` (a real string column) as
nullable-inferred: asserts `public string? EmployeeKey { get; set; }` with no `= "";`
initializer, while every other string column (`FullName`) is untouched. Regression: 107/107 in
`Ssis.Extract.Codegen.Tests` (was 106), same 3 pre-existing unrelated failures elsewhere.

**Remaining known gaps, updated once more:** Flat File Destination, Excel Source, ForEach Loop
containers, Conditional Split -> Sort -> Merge, multi-source merge/join flows, and Data
Conversion target types other than DT_I4/DT_DBDATE. `EntityEmitter`'s string-nullability
exclusion, named above, is now closed.

### Flat File Destination (2026-08-28) -- closes RBC_Demo_ETL's own `DFT_ExportDelimited`/`DFT_ExportFixedWidth`

Sized live with the user first (`AskUserQuestion`): a first look made this look like a small
codegen addition, but it turned out to need a brand-new `Etl.Core` write-side abstraction (no
CSV-writing sink existed at all, only SQL bulk-copy), a codegen bypass for the whole EF Core/
DbContext/PrimaryKeyInference machinery (a flat file has no table), AND -- to actually close the
real evidenced flows, both of which are pure column copies -- a narrow, deliberately-scoped
exemption from the pre-existing "no Derived Column -> direct-copy pipeline is not generated"
gate. Framed as three options (small/full/skip); the user picked full.

**Extraction needed one new bespoke payload, `FlatFileDestinationPayload`** (`Overwrite`/
`Header`/`EscapeQualifier` plus the usual generic `ColumnMappings`), wired into `PipelineReader`
exactly like `Microsoft.OLEDBDestination`. Confirmed real from RBC_Demo_ETL's own
`Package_Exports.dtsx` (`DFT_ExportDelimited_DST`/`DFT_ExportFixedWidth_DST`) before writing any
reader code, same discipline as every other component type in this file. **A genuinely
surprising, real-evidenced quirk, recorded rather than re-derived later:** everything about the
file's PHYSICAL layout (delimited vs fixed-width, delimiters, column widths) lives on the FLAT
FILE CONNECTION MANAGER, already modeled generically by `FlatFileFormatSpec` for the source
side -- the destination component itself carries none of that. And SSIS's own FixedWidth wizard
adds an extra trailing pipeline column (`RowEnd` in the real package) with NO fixed width of its
own -- `ColumnType="Delimited"` with the ROW delimiter as its own `ColumnDelimiter` -- purely to
carry the CRLF row terminator; the real package's own author literally selects `'' AS RowEnd` to
feed it. This needs no special generator handling at all: it's an ordinary mapped column, and the
row-terminator behavior falls out of walking the connection manager's own column list in order.

**New `Etl.Core` abstraction** (`Data/FlatFileColumnFormat.cs`, `Data/FlatFileBulkSink.cs`):
`FlatFileBulkSink<TEntity> : IBulkSink<TEntity>` never touches its own `IUnitOfWork` parameter (a
flat file write has no transactional participant, the same separation `SqlRowSource<TRow>`
already has on the read side) -- it reads properties BY NAME (never ordinal, same rule
`SqlBulkSink<TEntity>` follows) against a `FlatFileColumnFormat` list resolved once at
construction, in the CONNECTION MANAGER's own column order (the real physical file layout, not
the entity's C# property order). A null `FixedWidth` means delimited (write the raw value, then
the delimiter); a non-null one means pad-right-with-spaces-or-truncate, never erroring -- the
component carries no `usesDispositions`/truncation-disposition attribute at all, unlike an OLE DB
Destination's own input columns.

**Codegen changes, `Ssis.Extract.Codegen`:**
- `PackagePlanner.PlanDataFlow`'s destination resolution now also accepts
  `Microsoft.FlatFileDestination` (previously only OLE DB/ADO NET Destination).
- `PackageGenerator.Generate`'s per-flow loop gained an `isFlatFileDestination` branch that: (1)
  derives the entity/row-type name from the Data Flow Task's own name (`SanitizeIdentifier`,
  reused from the Lookup-cache naming) instead of an OpenRowset table name, since a flat file has
  none; (2) skips the fast-load gate entirely (no such concept for a file); (3) is EXEMPTED from
  the "no Derived Column found" direct-copy gate -- deliberately narrow, only for this
  destination type, since `TransformEmitter` already handles an empty `DerivedColumns` list
  correctly (every column becomes a plain `row.ColumnName` passthrough) and there's nothing
  SQL-specific in that code path left unproven; the OLE DB/ADO NET direct-copy restriction is
  UNCHANGED, still a separate, still-out-of-scope gap; (4) resolves a new `ResolveFlatFileSink`
  helper instead of adding a `DbContextEmitter.TableSpec` -- reads the destination's own
  connection manager's `FlatFileFormatSpec`, matches each connection-manager column (in ITS OWN
  order -- the real physical layout) against the component's `ColumnMappings` by name to find
  the row/entity PROPERTY name, and reuses the existing `BuildFileSourceEntry` helper for the
  file path (a destination is just another named folder/filename pair, exactly like a CSV
  source's own `FileSourceOptions` entry -- no new configuration shape needed). A literal
  (non-empty) `Header` property, or a FixedWidth connection manager combined with
  `ColumnNamesInFirstDataRow`, are both named gaps rather than guessed -- neither is evidenced
  in either real instance.
- `ProgramEmitter` gained a `FlowSinkSpec` abstraction (`SqlFlowSink`/`FlatFileFlowSink`,
  parallel to the existing `FlowSourceSpec`) threaded onto `ProgramFlowSpec`. A flat-file sink's
  `IBulkSink<TEntity>` is constructed directly inside a factory lambda (`AddScoped<IBulkSink<T>>(sp
  => new FlatFileBulkSink<T>(...))`) rather than through the generic `AddBulkSink<T>()` (which
  would resolve to a `SqlBulkSink<T>` -- wrong for a file).
- `ProgramEmitter.CSharpStringLiteral` was hardened to escape `\r`/`\n`/`\t`, not just `\`/`"` --
  a flat file's own `ColumnDelimiterDecoded`/header line can contain a literal CR/LF (SSIS's
  DECODED row delimiter, not its escape-sequence text), and embedding a raw newline inside a
  non-verbatim C# string literal is a compile error (CS1010), not just cosmetically wrong. A
  strict superset of the old escaping, so this can only fix a latent bug elsewhere (e.g. a
  multi-line Execute SQL Task statement), never introduce a new one.

**Two real, previously-latent bugs found getting the REAL package's own generated output to
actually build, neither visible from a hand-built fixture that happened to dodge them:**
1. `BuildSqlFlowSource`'s own server/database match check (built for the OLE DB/ADO NET
   destination case, to catch a source and destination silently needing two untracked
   connections) unconditionally required a DESTINATION connection manager to compare against --
   a Flat File Destination has no server/database at all, so every flat-file flow's SQL source
   was rejected as "targets a different server/database" regardless of anything about the
   source itself. Fixed by skipping that comparison specifically when the destination is a Flat
   File Destination.
2. **A real "zero SQL tables in the whole package" case, discovered ONLY by generating the REAL
   package, not the hand-built fixture** (which was deliberately given a third, ordinary SQL
   destination flow once this was understood -- see below): RBC_Demo_ETL's own
   `Package_Exports.dtsx` has three flows -- the two Flat File Destinations, plus
   `DFT_AdoNetRoundTrip` (an ADO NET Destination) -- but `DFT_AdoNetRoundTrip` fails on its OWN,
   separate, pre-existing "no Derived Column" gap (that destination type's own direct-copy
   restriction is unchanged), leaving `tables.Count == 0` for the WHOLE package. `Program.cs`
   still unconditionally emitted `AddEtlDbContext<{Package}DbContext>()` referencing a type that
   `DbContextEmitter` refused to generate for zero tables (`CS0246`) -- caught only by actually
   building the real package's generated project, not the report's own gap count (which showed
   nothing wrong). This is a real, general fact about the architecture, not specific to Flat File
   Destination: `IUnitOfWork`'s whole-package transaction (`UnitOfWork.cs`) is backed by a live
   `DbContext` regardless of whether anything maps to a table -- `PackageRunner`/`ExecuteSqlStep`/
   `FlatFileBulkSink` all still need SOMETHING to resolve. Fixed by letting `DbContextEmitter.Emit`
   succeed with an EMPTY `tables` list (a bare `DbContext` subclass, no `DbSet`s, empty
   `OnModelCreating`) while still failing loudly if tables WERE passed in but every one
   individually failed `EmitOneTable` (a real per-table problem, unchanged); `PackageGenerator`
   now calls it whenever ANY flow got wired, not just when `tables.Count > 0`.

**Verified two ways, same discipline as every other round:**
1. **The real motivating case** -- `ssisx generate` against RBC_Demo_ETL's actual
   `Package_Exports.dtsx`: both `DFT_ExportDelimited`/`DFT_ExportFixedWidth` now generate with
   only the expected non-blocking SqlCommand-column-naming advisory (the same one every
   AccessMode=2 OLE DB Source gets), matching the real connection managers' own column widths
   (10/30/40) exactly. The whole regenerated solution (both flows plus a copy of `Etl.Core`)
   builds with **0 warnings/0 errors**. A full RUN was not attempted against this real package --
   its own source table (`dbo.StagingCustomers` in a `SSISDemo` database) is the third party's
   own data, not available in this environment -- named honestly rather than glossed over, same
   as the ADO NET Destination round's own unresolved connection-manager quirk.
2. **A new dedicated fixture**, `tests/Ssis.Extract.Tests/Fixtures/SyntheticFlatFileDestination.dtsx`
   (`Ssis.Extract.FixtureBuilder`'s new `flat-file-destination` mode): OLE DB Source -> Flat File
   Destination, twice -- once Delimited (with a `ColumnNamesInFirstDataRow` header, the one real
   evidenced instance that sets it), once FixedWidth (with the real evidenced synthetic trailing
   `RowEnd` column) -- plus a third, ordinary OLE DB Source -> Derived Column -> OLE DB
   Destination flow (`DFT_Log`), added once the "zero SQL tables" bug above was understood, so
   this fixture proves the SAME shape the real package actually has, not an easier one. The
   backing table's `FullName` seed value is deliberately 8 characters longer than the
   FixedWidth connection manager's own column width, specifically to exercise real
   truncation/padding rather than assume it. **Generated, built against a copy of `Etl.Core` (0
   warnings/0 errors), and actually RUN against `.\SQLFORPOC_2022`**: read back the real output
   afterward, not trusted from the exit code --
   `customers-export.csv` (delimited): a header row plus 3 comma-separated rows, CRLF-terminated,
   exactly as configured;
   `customers-fixed.txt` (fixed-width): `"C001      "`/`"Alice Smith    "`/`"alice@example.com   "`
   (short values RIGHT-PADDED with spaces to their column's exact width) and
   `"C003      "`/`"Charlotte Fitzg"`/`"charlotte@example.co"` (the over-length `FullName`/
   `CleanEmail` values SILENTLY TRUNCATED to fit, no error) -- confirming the documented SSIS
   behavior for real, not by assumption; `dbo.SyntheticFlatFileDestinationLog` got its 1 row via
   the ordinary `SqlBulkSink` path, unaffected by anything in this round.

Two new unit tests: `PackagePlannerTests.Plan_ResolvesAFlatFileDestination_...` (both flows
resolve as valid destinations, no Derived Column needed) and
`PackageGeneratorTests.Generate_WiresTwoFlatFileDestinationFlows_...` (asserts the exact
`FlatFileColumnFormat`/header-line/file-path-key wiring in the emitted `Program.cs`, and that the
DbContext carries only `SyntheticFlatFileDestinationLog`, never either flat-file flow's own
entity). Regression: 109/109 in `Ssis.Extract.Codegen.Tests` (was 107), same 3 pre-existing
unrelated failures elsewhere.

**Remaining known gaps, updated:** Excel Source, ForEach Loop containers, Conditional Split ->
Sort -> Merge, multi-source merge/join flows, and Data Conversion target types other than
DT_I4/DT_DBDATE. Flat File Destination, named above, is now closed.

### Sort + Merge Join (2026-08-28) -- closes RBC_Demo_ETL's own `DFT_SortAndMergeJoin`

Sized live with the user first (`AskUserQuestion`, three options): the largest remaining real
gap, closing a genuine two-independent-source flow the "multi-source" gate had blocked since it
was first discovered. Unlike Lookup, SSIS DOES persist a Sort's own sort key and a Merge Join's
own column mappings in the saved XML -- so this was buildable from real evidence, not a guess,
but needed a brand-new Etl.Core abstraction (today's `DataFlowStep`/`IRowSource` are strictly
one-source) plus a real design call on where the join algorithm lives.

**The key design decision: Merge Join is an `IRowSource<TRow>`, not a new step type.**
`MergeJoinRowSource<TLeft,TRight,TKey,TRow> : IRowSource<TRow>` reads BOTH underlying sources
fully into memory, sorts each by the join key (SSIS's own Sort component has no other evidenced
consumer than feeding a Merge Join, so its only real job -- order rows before a multi-input step
reads them -- is folded in here rather than modeled as a separate reusable "SortStep"), then
performs a genuine sort-merge-join walk, yielding combined rows via a caller-supplied
`Func<TLeft?,TRight?,TRow>` projection. This slots directly into the EXISTING
`DataFlowStep<TRow,TEntity>` (Source -> Transform -> Sink) pipeline with ZERO changes to that
type: everything downstream (the destination's own transform) just sees an ordinary `TRow`,
exactly like a SQL or CSV row -- a much smaller footprint than a parallel step-type would have
needed. Duplicate keys on either side are handled as a genuine cross product (the standard
sort-merge-join algorithm for a non-unique key), not a naive 1:1 zip.

**`MergeJoinType.LeftOuter`'s raw value is inferred, not independently run-verified against
live SSIS -- stated honestly, not glossed over.** The one real evidenced raw value (JoinType=2)
is mapped to LeftOuter from the real destination's own column shape (every customer kept, only
the contact fields left nullable, a table literally named `CustomerWithContact`) and the
property's own description text ("using the FULL, LEFT, or INNER join"). A hand-authored `.dtsx`
fixture built specifically to confirm this via a real dtexec run hit an unresolved SSIS
object-model quirk (below) before it could execute. What IS independently verified, via a plain
C# unit test with no SSIS involved at all
(`D:\PoC\SSIS_Rewrite\tests\Etl.Core.Tests\Data\MergeJoinRowSourceTests.cs`, 5 tests): once a
join type is selected, `MergeJoinRowSource`'s own algorithm implements it correctly -- LeftOuter
keeps an unmatched left row (nulled right side) and drops an unmatched right row entirely, Inner
keeps only matches, FullOuter keeps both unmatched sides, duplicate keys cross-product correctly,
and unsorted input is sorted internally before joining. `Inner`/`FullOuter`'s own raw-value
mapping remains unevidenced, same "don't hand-guess an enum" rule as `OleDbDestinationPayload.AccessMode`.

**Extraction: `SortPayload`/`MergeJoinPayload`, both requiring real evidence to get right --
one initial wrong reading, corrected before shipping.** `Microsoft.Sort`'s own key designation
was FIRST assumed to live on its OUTPUT columns (a `NewSortKeyPosition` custom property observed
there) -- extracted, tested, and found to always come back EMPTY against the real package. Root
cause, found by re-reading the raw XML byte-for-byte rather than trusting the first skim:
`NewSortKeyPosition`/`NewComparisonFlags` actually live on Sort's own INPUT columns; each OUTPUT
column instead carries a `SortColumnId` custom property (a `#{...}` reference to the matching
INPUT column's own LineageId) that is purely a passthrough pointer, unrelated to key-ness.
`Ssis.Extract.Dtsx.PipelineReader` now correctly builds a `Dictionary<LineageId, Position>` from
Sort's own input columns first, then resolves each output column's key status by matching its
own `SortColumnId` against that dictionary. `Microsoft.MergeJoin`'s own output columns carry an
`InputColumnID` custom property (NOT a lineageId reference like Data Conversion's
`SourceInputColumnLineageID` -- it's a reference to one of the component's OWN Left/Right INPUT
columns' refId), resolved once at read time to that input column's own LineageId, so codegen can
continue tracing back to the true origin exactly like any other column reference.

**Codegen's own resolution deliberately does NOT reuse `LineageBuilder`'s global, name-only edge
search.** Two Data Conversion components in the same pipeline can produce a column with the
identical name (confirmed real: `DFT_SortAndMergeJoin`'s own `DCONV_CustId_L`/`DCONV_CustId_R`
both produce `CustomerID_i4`) -- `LineageBuilder`'s edges are matched by bare column name only,
with no per-component scoping, so reusing it here would be genuinely ambiguous. A new
`PackagePlanner.PlanMergeJoin`/`ResolveMergeJoinSide` walks BACKWARD by hand instead: from each
of Merge Join's own Left/Right inputs, find the feeding Sort (matching a representative column's
LineageId against every component's own output columns -- the generic "who produced this value"
lookup every backward walk needs), then check ALL of that Sort's own input columns (not just the
first -- an earlier version that only checked the first column MISSED the Data Conversion
entirely, since the real fixture's own first column happens to be a plain passthrough one) for
either a real source or exactly one Data Conversion feeding it. `MergeJoinEmitter`'s own
`ResolveSortOutputValue` then resolves a Sort output column's true value the same way for BOTH
every Merge Join output column (via `SourceColumnLineageId`) and the join key itself (via
`SortKeySpec.ColumnName`) -- one shared resolution, not duplicated.

**A Merge Join's own output has NO external-metadata columns at all** (confirmed real:
`MRG_CustomerContacts.Outputs[Merge Join Output]` has an empty `<externalMetadataColumns/>` --
unlike a source reading a file/table, a pure pipeline transform has no design-time external
schema to join against) -- so `PipelineResolver.Resolve` (built around exactly that join) cannot
build the combined row type; `MergeJoinEmitter` resolves column type straight from the output
column's own dataType/length/precision/scale/codePage instead, via a NEW `MergeJoinEmitter.cs`
(combined row type class + a static mapper class with `LeftKey`/`RightKey`/`Map`).

**Nullability needed a THIRD access mode beyond the existing "key selector, non-nullable
parameter" vs "output projection, right side may be absent" split -- found only by actually
building the generated project, not by unit tests.** A `Map(TLeft? left, TRight? right)`
projection's LEFT-side parameter is STILL a nullable TYPE even though it's semantically
guaranteed present for LeftOuter -- Roslyn's nullable analysis doesn't know that, so an unguarded
`left.Email` is a build ERROR (CS8601/CS8629) under this project's `<Nullable>enable</Nullable>`
+ `TreatWarningsAsErrors=true`, not just a runtime risk. Fixed with a targeted null-forgiving
`left!` at the point of access, semantically justified because LeftOuter's own contract
guarantees it (the same "provably safe, not blindly suppressed" reasoning as the earlier
Data-Conversion-cross-reference round's own CS8629 fix) -- `ResolveSortOutputValue` takes both
`sourceParamIsNullable` (false only for a key selector) and `mayBeAbsent` (true only for the
Right side of a Map projection) so the three real access shapes (plain, null-forgiving,
null-conditional) are each produced exactly where needed. `MergeJoinEmitResult` also exposes the
resulting nullable-column-name set so `EntityEmitter`/`TransformEmitter` make the DESTINATION
entity's own properties nullable to match -- otherwise a plain passthrough assignment
(`CustomerID = row.CustomerID_i4`) fails to compile the moment only one side is nullable.

**`ProgramEmitter` gained a `MergeJoinFlowSource`** (wrapping two ordinary
`CsvFlowSource`/`SqlFlowSource` values, resolved exactly like any single-source flow's own),
emitting each side's own source construction as a LOCAL FUNCTION inside the outer
`AddScoped<IRowSource<TRow>>` factory (not a second top-level DI registration -- neither side's
own `IRowSource` is independently useful, it only exists to feed the one `MergeJoinRowSource`).
Also fixed two, real, general `usesSql`/`usesFlatFileSink` detection gaps along the way: the
combined row type always lands in `.Sql` regardless of what the two SIDES are sourced from (both
were CSV in the real evidenced case), so `usesSql` now also fires on `MergeJoinFlowSource`'s mere
presence; and the mapper's own `using` lines are computed per-side (left/right can independently
be CSV- or SQL-sourced) rather than assuming one shared namespace.

**Verified two ways, though the SECOND is a genuine, honestly-named partial result, not a full
Gate-3-style run:**
1. **The real motivating case** -- `ssisx generate` against RBC_Demo_ETL's actual
   `Package_Transforms.dtsx`: `DFT_SortAndMergeJoin` is GONE from the gap list entirely (was
   previously blocked by the multi-source gate). The whole regenerated solution (all wired flows
   plus a copy of `Etl.Core`) builds with **0 warnings/0 errors** -- confirmed only after fixing
   three real bugs found purely by attempting this build: the per-side namespace mixup above, a
   missing `NullableColumnNames` thread into `EntityEmitter`/`TransformEmitter`, and the
   `usesSql` detection gap. A full RUN was not attempted -- the real package's own source data
   (two CSV files under the third party's own `D:\OneDrive\...` path) is not available in this
   environment, same honest limitation as the Flat File Destination round's own real-package
   check.
2. **`MergeJoinRowSource`'s own join algorithm** -- proven via 5 genuine unit tests (no SSIS
   involved), listed above. A DEDICATED, hand-authored-via-the-SSIS-object-model `.dtsx` fixture
   (which would have let this be dtexec-verified end to end, matching every other round's own
   discipline) was attempted but NOT completed: `Ssis.Extract.FixtureBuilder.BuildMergeJoinFixture`
   gets as far as a real Sort (confirmed correct: `SetInputColumnProperty` on the input side) and
   a real two-input Merge Join (confirmed correct: `InsertInput(IP_AFTER, 0)` adds the second
   input; the designtime `InsertOutputColumnAt` throws `COMException 0xC0208019` unconditionally
   for this component -- unlike Conditional Split, where the equivalent call works -- so the raw
   `output.OutputColumnCollection.New()` + setting `Name`/`SetDataTypeProperties`/`MappedColumnID`
   directly works instead, and `MappedColumnID` is confirmed to be what persists as the saved
   XML's own `InputColumnID` custom property). But after `SetComponentProperty` for
   `JoinType`/`NumKeyColumns`/`TreatNullsAsEqual` runs, the component appears to auto-generate
   ADDITIONAL output columns beyond the ones added manually, and the two inputs' own identities
   don't line up with a simple index-0/index-1 assumption the way expected -- confirmed by
   reading the saved XML back (six output columns where three were intended), not root-caused.
   Left unresolved and clearly documented (in `BuildMergeJoinFixture`'s own doc comment) for
   whoever continues this, rather than papered over -- the same "a gap must be visible" rule
   this whole file follows for every other unresolved quirk (the ADO NET Destination
   connection-manager issue, the Union-All-remerge dtexec quirk). The backing SQL tables
   (`synthetic-merge-join-tables.sql`, seeded with a left-only key and a right-only key
   specifically to distinguish LeftOuter from FullOuter by behavior) are kept as a documented
   starting point for a future attempt.

Regression: 109/109 in `Ssis.Extract.Codegen.Tests` (unchanged -- no new dedicated fixture-based
test was added there this round, for the reason above), same 3 pre-existing unrelated failures
elsewhere; 29/29 in `Etl.Core.Tests` (5 new tests, `MergeJoinRowSourceTests`).

**Remaining known gaps, updated once more:** Excel Source, ForEach Loop containers, Conditional
Split -> Sort -> Merge (a DIFFERENT shape from this round's own multi-source Merge Join -- single
source, Conditional-Split-then-remerge, needs `ResolveBranch`'s own chain-walker to understand
Sort/Merge as pass-through/convergence points, a distinct follow-up), and Data Conversion target
types other than DT_I4/DT_DBDATE. Multi-source Merge Join, named above, is now closed for the
one evidenced shape (two sources, each optionally through one Data Conversion, then Sort, then
directly into a destination) -- Sort/Merge as part of a Conditional Split's own branch chain is
explicitly NOT covered by this round.

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
   extra escaped-quote-wrapping shown in README. **`dtexec` itself now runs
   fine** — `DTExec.exe /Project SSIS.ispac /Package LoadReferenceData.dtsx`
   returned `DTSER_SUCCESS (0)` on 2026-08-21, so the COM blocker that made
   this moot on the old box is gone. `/ISServer` still has not been tried.
   The `/Par` restrictions above are unchanged and are the real reason to
   prefer `catalog.start_execution` from T-SQL.
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
    connection manager's `CM.<name>.Password`, which never passes through
    package code. Marking `Sensitive=1` also makes the build strip the value
    from the `.ispac` entirely (`design_default_value` = NULL). **The
    connection manager does NOT need to be project-scoped for this** — a
    package-scoped OLEDB connection manager auto-splits a literal `Password=`
    in its `ConnectionString` into a package-level (`object_type = 30`)
    `CM.<name>.Password` parameter just the same; verified empirically 2026-
    08-13 (see "Sensitive credential" below) after this note originally
    assumed otherwise.
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
18. **Never `sed -i` a `.dtsx` or `Project.params`.** Git-bash `sed -i`
    rewrites the file with LF endings, and unlike `.sql`/`.ps1`/`.cs` (which
    git normalizes, so the damage is invisible in `git diff`) the SSIS XML
    files are **not** EOL-normalized here. Two consequences, and the second is
    the one that wastes an hour:
    - every line shows as changed — a one-word edit becomes a 955-insertion /
      955-deletion diff that buries the real change and, on a file with
      uncommitted work in it, makes the real change unreviewable.
    - it silently changes the file's **byte length and SHA-256**, which are
      fields in the extractor's own `spec.json`. Golden tests then fail on
      `FileSizeBytes`/`Sha256` and read like an extractor regression rather
      than an editing accident. (Observed exactly: 55140 → 54190 = 955 CR
      bytes dropped, plus 5 bytes of genuine edit.)

    Use a byte-preserving replace instead — read all bytes, decode UTF-8,
    regex-replace, write all bytes back:
    ```powershell
    $b=[IO.File]::ReadAllBytes($p); $t=[Text.Encoding]::UTF8.GetString($b)
    [IO.File]::WriteAllBytes($p,[Text.Encoding]::UTF8.GetBytes($t -replace 'old','new'))
    ```
    And when a golden file needs the same edit, patch it the same way rather
    than regenerating with `SSISX_REGENERATE_GOLDEN=1` — a full regeneration
    also absorbs any unrelated uncommitted package edits into the baseline,
    which is how someone else's in-progress SSDT work gets silently blessed as
    the reference output.

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
- A genuinely protected credential (not just encryption-at-rest with no
  consumer) is now demonstrated for `LoadEmployees` only — see "Sensitive
  credential" below. `LoadReferenceData` still uses Integrated Security
  throughout and was deliberately left untouched.
- SSISDB retention is still at the 365-day default and the
  `SSIS Server Maintenance Job` has not been checked. A verbose retry
  generated **769 messages vs 34** for one small package, so this matters
  sooner than it looks.
- ~~The root cause of local `dtexec.exe`'s COM registration failure~~ —
  **closed 2026-08-21 by the machine change, not by a fix.** It never
  reproduced on the new box. Its root cause on the old box remains unknown and
  is now unknowable; if it ever reappears, note that the "missing Integration
  Services feature" theory was disproven *there* (feature was installed,
  uninstall/reinstall + reboots did not help) — but on the NEW box a genuinely
  missing Integration Services feature caused a *different* and much clearer
  failure ("you must install Standard Edition (64-bit) of Integration Services
  or higher"), so don't conflate the two.
