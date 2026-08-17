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

## Phase 0 extractor (`Tools/SsisExtractor/`, started 2026-08-13 — slice 5 complete, minus `pull`; synthetic component-coverage pass + slice 6's object-model oracle added 2026-08-14; Script Task AND Script Component source extraction — plan §4.5's both halves — plus `ssisx conformance` (migration-validation gate 1) added 2026-08-17)

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
- The root cause of local `dtexec.exe`'s COM registration failure is still
  unresolved: the "missing Integration Services feature" theory was
  **disproven** (feature is installed; uninstall/reinstall + reboots did
  not help). Not worth more effort — catalog execution works without it.
