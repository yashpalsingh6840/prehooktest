# SSIS PoC — Architecture & Execution Overview

**Purpose:** briefing document for discussing this PoC's design, hosting, and
execution model. Everything here has been verified by actually running it on
`.\SQLFORPOC` (SQL Server 2025 Developer Edition), not just designed on
paper. Deep operational detail (exact commands, every trap hit) lives in
`CLAUDE.md`/`README.md` in the repo — this doc is the summary for a design
conversation, not a runbook.

**TL;DR:** Two SSIS packages, deployed to an SSISDB catalog, driven by a
thin custom T-SQL API (`ctl`) so an application never has to speak SSIS —
just "run this package, here's a flat bag of name/value parameters."
Credentials are protected end-to-end via SSISDB's built-in sensitive-value
mechanism, proven with a live SQL login, not just encryption with nothing
consuming it. The open items are the usual PoC→production gaps: signed
procedures instead of relying on sysadmin, a second (Prod) environment, and
a concurrency/timeout limiter — not fundamental design problems.

---

## 1. What packages we have

| Package | Loads | Source | Parameter scope |
|---|---|---|---|
| `LoadEmployees.dtsx` | `dbo.Employee` | one CSV | **project** parameters only |
| `LoadReferenceData.dtsx` | `dbo.Department`, `dbo.Designation` | two CSVs (different folders) | **package** parameters + one shared project parameter |

Both follow the same shape: `Flat File Source → Derived Column → OLE DB
Destination`, each writing to a table it truncates first so it's safely
re-runnable.

- **`LoadEmployees`**: truncates `dbo.Employee`, then loads 12 sample rows.
  Derived Column adds `FullName` (first+last), `Location` (city+state),
  `EmployeeKey` (`DEPT-ID`), and a `LoadedAtUtc` audit timestamp.
- **`LoadReferenceData`**: same pattern, twice — one dataflow for
  Departments (8 rows), one for Designations (10 rows), each with its own
  derived key + timestamp columns.

**Why two different parameter scopes, deliberately:** it forces the control
framework (`ctl`, see §4) to *resolve* whether a parameter is project- or
package-scoped from catalog metadata rather than being told — which is
exactly what lets an application pass one flat name/value bag regardless of
which package it's calling. If every package only ever used project
parameters, that generalization would never have been tested.

## 2. Parameters each package takes

**Project parameters** (`SSIS/Project.params` — shared across the project,
so `LoadEmployees` sees these too even though it only actually uses one):

| Parameter | Default | Used by |
|---|---|---|
| `SourceFolder` | `D:\PoC\SSIS\Data\Input` | `LoadEmployees` (builds CSV path) |
| `SourceFileName` | `Employees.csv` | `LoadEmployees` (builds CSV path) |
| `TargetConnectionString` | `Data Source=.\SQLFORPOC;...;Integrated Security=SSPI;...` | `LoadReferenceData`'s destinations only (see §8 for why `LoadEmployees` stopped using this) |

**`LoadReferenceData`'s own package parameters** (package-scoped, not
visible to `LoadEmployees`):

| Parameter | Default |
|---|---|
| `DepartmentFolder` | `D:\PoC\SSIS\Data\Input\Department` |
| `DepartmentFileName` | `Departments.csv` |
| `DesignationFolder` | `D:\PoC\SSIS\Data\Input\Designation` |
| `DesignationFileName` | `Designations.csv` |

**`LoadEmployees`' destination connection** is *not* driven by
`TargetConnectionString` — its `CM_TargetDb` connection manager carries its
own `ServerName`/`InitialCatalog`/`UserName`/`Password` properties instead,
because it authenticates with a real SQL login rather than Windows
Integrated Security (§8 explains why and how).

**Stable vs. volatile values — different mechanisms, on purpose:**

| Kind of value | Example | Held in |
|---|---|---|
| Stable per environment | connection info, base folder | SSISDB **Environment variable**, bound once |
| Volatile, changes every run | file name, business date, batch id | **Per-execution parameter override**, passed at call time |

This split matters under concurrency: environment variables are global
mutable state — two overlapping runs writing to the same variable would
silently stomp each other with no error. Execution overrides are scoped to
one `execution_id`, so they're safe for concurrent runs. `SourceFileName`,
for instance, is deliberately left unbound at the environment level so every
run can (and must) supply its own.

## 3. How we've hosted this on SQL Server

- **Instance:** SQL Server 2025 Developer Edition, named instance
  `.\SQLFORPOC`, on the same box as everything else for this PoC.
- **SSISDB catalog** exists on that instance (`CLR enabled` was a
  prerequisite — SSIS catalog stored procedures are CLR-based).
- **Folder:** `SSIS PoC` → **Project:** `SSIS` (both packages) →
  **Environment:** `Dev` (one environment, deliberately — see §6).
- **Deployment mechanism:** `SSISDB.catalog.deploy_project`, called with
  the built `.ispac` as a `varbinary(max)` parameter. This is a stock
  Microsoft stored procedure taking raw bytes over TDS — no Visual Studio,
  no deployment wizard, no Windows dependency for this specific step. That
  makes it viable from any CI/CD runner (including Linux) that can open a
  SQL connection and read a file. **Building** the `.ispac` still requires
  Windows + SSDT/Visual Studio; only *deploying* it is platform-agnostic.
- **Parameter/environment binding** is a one-time setup script
  (`Sql/02_SetupCatalogEnvironment.sql`) — creates the `Dev` environment,
  creates its variables, and binds them to project/package parameters via
  `catalog.set_object_parameter_value`.

## 4. How we're executing them today

Three execution paths exist; only the first is the one intended for
production-style use — the other two are development conveniences.

### Primary path: the `ctl` control framework + `SsisRunner`

Custom T-SQL API in database `SsisPoC`, schema `ctl`, purpose-built so an
application never has to know SSIS vocabulary (`SSISDB`, `object_type`,
execution status codes) — just a logical package name and a flat parameter
bag.

| Procedure | What it does |
|---|---|
| `ctl.usp_StartPackage` | start **one** package, fire-and-forget, 1 round trip |
| `ctl.usp_StartRun` | start **N** packages from a manifest, 1 round trip regardless of N |
| `ctl.usp_PollRun` | status + error text for every item in one run, 1 round trip |
| `ctl.usp_PollCorrelation` | status across **every run** sharing a caller-supplied `CorrelationId` |

```
Application (SsisRunner / any .NET client)
   │  ctl.usp_StartPackage @PackageKey='LOAD_EMPLOYEES', @Parameters=...
   ▼
SsisPoC.ctl  ──resolves PackageRegistry, validates params, logs RunItem──▶  SSISDB.catalog.start_execution
                                                                                   │  (async, no SYNCHRONIZED)
                                                                                   ▼
                                                                     ISServerExec.exe (spawned by the
                                                                     SQL Server Database Engine service)
                                                                                   │
                                                                                   ▼
                                                                     package runs, writes to target table
```

Executions are started **asynchronously** (no `SYNCHRONIZED`) — nothing
holds a connection open for the package's duration, and a killed caller can
resume by polling the RunId later; SQL Server is the system of record, not
the caller. A same-`PackageKey` concurrency guard rejects starting a
package that's already `Running` unless the caller opts in
(`@AllowConcurrent=1`). There's no separate retry procedure — calling
`usp_StartPackage`/`usp_StartRun` again *is* the retry
(`@RetryOfRunId` for lineage, `@LoggingLevel=3` for verbose diagnostics on
that one attempt).

`Scripts/SsisRunner/` (.NET 8 console app) is the reference client:

```powershell
SsisRunner.exe --package LOAD_EMPLOYEES --set SourceFileName=Employees.csv --wait
SsisRunner.exe --manifest manifests\nightly.json --wait
SsisRunner.exe --status 12
```

### Development-only path: local in-process execution

`Scripts/Run-Package.ps1` / `Scripts/PkgRun.Console.exe` run a package
in-process via the SSIS managed API (`Project.OpenProject()` +
`Package.Execute()`), entirely outside SSISDB — useful for a fast local
iteration loop while editing a package, not a production path. **As of the
credential change in §8, this only works for `LoadReferenceData`** —
`LoadEmployees` needs a password that now exists solely inside SSISDB.

### Not used: raw `dtexec`

Local `dtexec.exe` on this dev box can't launch anything at all (`Could not
create DTS.Application`, COM registration issue — the "Integration
Services" *server* feature was never installed here, only the SSDT design
runtime). This is a dev-box quirk, not a production blocker: catalog
execution (`catalog.start_execution`) never goes through that COM layer at
all — confirmed empirically, both paths were tested independently.

## 5. What's needed to build, deploy, and run this on SQL Server

| Stage | Requirement |
|---|---|
| **Build** (produce the `.ispac`) | Windows + Visual Studio 2022 (or SSDT) with the Integration Services project extension. Not portable — Microsoft doesn't ship a cross-platform SSIS project compiler. |
| **Deploy** (`.ispac` → SSISDB) | Just a SQL connection — `catalog.deploy_project` takes raw bytes over TDS. Works from any platform, including a Linux CI runner. |
| **Host** (the SQL Server instance) | SSISDB catalog present (Integration Services **server** feature installed at SQL Server setup time — a checkbox, distinct from the client/design tools); `clr enabled = 1` (required before `Catalog.Create()`/catalog stored procs will run). |
| **Execute** | Nothing beyond the catalog itself — execution spawns `ISServerExec.exe` as a child of the Database Engine service; the classic `DTS.Application` COM object (what local `dtexec` needs) is never touched. |
| **Client / caller** | .NET 8 runtime for `SsisRunner`; `Microsoft.Data.SqlClient` for the DB connection. No SSIS-specific client dependency at all — that's the point of the `ctl` abstraction. |

Nothing about *hosting and executing* packages requires the SSIS "server"
Windows service to be running, or `dtexec` to work locally — Project
Deployment Model execution through SSISDB is self-contained inside the SQL
Server process itself.

## 6. Caveats and what's left over (PoC → production gaps)

These are the honest, currently-open items — nothing here is a design
dead-end, just work not yet done because this is a PoC:

- **`ctl` procedures are unsigned and rely on the caller being sysadmin.**
  They live in `SsisPoC` and call across into `SSISDB`; ownership chaining
  doesn't cross databases. Production needs the procedures **signed with a
  certificate** whose SSISDB-side user holds the actual permissions — not
  `TRUSTWORTHY ON`. This is the single biggest gap before this is
  deployable as-is.
- **No cross-package concurrency limiter or per-package timeout.**
  SSISDB provides neither natively. A same-package concurrency *guard*
  exists (reject a second start of an already-running `PackageKey`) but
  that's a correctness check, not a throughput limiter — 50 packages
  kicked off at once today would all start.
- **Only one environment (`Dev`) exists.** The Dev→QA→Prod promotion story
  (same project, different environment, different bound values) is
  architecturally supported but only demonstrated with one environment so
  far.
- **SSISDB retention is still the 365-day default**, and the built-in `SSIS
  Server Maintenance Job` hasn't been reviewed. This matters more than it
  looks: a single verbose retry on one small package produced ~769
  `operation_messages` rows vs. ~34 at the default logging level — at any
  real nightly volume this needs a retention/verbosity policy, not the
  out-of-the-box default.
- **The SSISDB database master key password is the disaster-recovery key
  for every sensitive value in the catalog** (it's what lets you
  re-open SSISDB's encryption if it's ever restored onto a different SQL
  Server instance, whose own Service Master Key can't open it). It
  currently lives in plaintext in a PoC doc — fine for a throwaway local
  box, **must move to a real secrets store before this goes anywhere near
  production.**
- **`dtexec /ISServer` (remote-triggering a catalog execution from the
  command line) has never actually been tried** — `catalog.start_execution`
  from T-SQL is the proven path and is what `ctl` uses; this just hasn't
  been exercised as an alternative.
- Local `dtexec.exe`/`DTS.Application` COM registration is broken on this
  dev box specifically — confirmed not a missing-feature problem (feature
  shows installed; reinstall + reboots didn't fix it). Irrelevant to the
  production path (§4), flagged here only so it doesn't get mistaken for
  something wrong with the packages themselves.

## 7. Anything else worth raising

- **Certificate signing for `ctl`** (§6's top item) is the natural next
  PoC milestone if this direction is approved — it's the one piece that
  currently only works because of a dev-box sysadmin shortcut. No design
  work has started on it yet; flagging it here so it's a deliberate next
  step rather than a gap discovered later.
- **Execution identity for production** (see §9): consider up front whether
  package execution should run as the SQL Server Database Engine's own
  service account, or be routed through a SQL Agent job + Proxy/Credential
  so different packages can run under different least-privilege Windows
  identities. This is a one-time architecture decision, cheap now,
  expensive to change once packages are in production with fixed
  assumptions baked into file-share ACLs.
- **Message/log volume at real scale** — the verbose-logging cost
  (§6) suggests logging level and retention policy should be sized against
  actual nightly package count before go-live, not left at defaults.
- **This PoC deliberately proves generality, not scale**: two packages
  with two different parameter scopes were chosen specifically so the `ctl`
  abstraction had to *resolve* metadata rather than being hardcoded per
  package — the same framework, unmodified, is what would onboard package
  #3 through #50 as registry rows, not code changes.

## 8. What we can secure — sensitive parameter values

**What SSISDB's sensitive-value mechanism actually protects, proven with a
live credential, not just encryption with nothing consuming it:**

`LoadEmployees`' destination connection (`CM_TargetDb`) was switched from
Windows Integrated Security to **SQL Server Authentication**, specifically
so there'd be a real secret for the catalog to protect end-to-end.
`LoadReferenceData` was deliberately left untouched (still Integrated
Security) — this is a one-package proof, not a project-wide rework.

| Piece | What it is |
|---|---|
| SQL login | `ssis_loader` — `SELECT, INSERT, ALTER` on `dbo.Employee`, nothing else |
| Connection manager | `CM_TargetDb`, static `ConnectionString` containing `User ID=ssis_loader;Password=;` (no expression — see below for why that matters) |
| Auto-split parameter | `CM.CM_TargetDb.Password` — created automatically at deploy time, `sensitive=1` |
| Environment variable | `TargetDbPassword`, `sensitive=1`, encrypted at rest in SSISDB |
| Binding | The environment variable is bound to the auto-split parameter via `catalog.set_object_parameter_value @value_type='R'` |

**Verified, not assumed:** package ran successfully authenticating as
`ssis_loader`, 12 rows loaded, and the real password string is absent from
every row of `catalog.operation_messages` for that execution — only
ciphertext exists in the internal tables.

### The four-stage flow — who's responsible for each part

Only one of these four stages is project-authored code; the rest is
Microsoft/SSISDB behavior we're relying on, not reimplementing:

1. **Deploy-time split** *(Microsoft's code)* — `catalog.deploy_project`
   inspects every OLE DB/ADO.NET connection manager and unconditionally
   decomposes it into `CM.<name>.ConnectionString`, `.ServerName`,
   `.UserName`, `.Password`, etc. — for every connection manager, used or
   not.
2. **Binding a value** *(our one-time setup code)* — the single
   `set_object_parameter_value @value_type='R'` call above. The only stage
   that's actually project-specific.
3. **Per-execution resolution** *(SSISDB's code)* — `create_execution`,
   given the package's environment reference, resolves every bound
   parameter automatically. `ctl` has zero `CM.*`-specific logic anywhere.
4. **Runtime application** *(the SSIS execution engine)* — recognizes the
   `CM.<ConnectionManagerName>.<Property>` naming convention and sets that
   property directly on the live connection manager object before
   `AcquireConnection`. `ConnectionString` and `Password` are applied as
   independent properties and never merged into one visible string.

### The hard limits of this mechanism (important for the architect conversation)

- **A sensitive value's only legal consumer is a connection manager
  property.** It cannot be read by a property expression or by an Execute
  SQL Task parameter mapping — SSIS refuses both outright at design/run
  time. So this mechanism protects **connection credentials specifically**,
  not arbitrary application secrets (API keys, tokens meant for business
  logic). `AuditToken` in this PoC demonstrates exactly that boundary: it's
  encrypted at rest but deliberately unbound, because nothing in package
  logic is *allowed* to consume it.
- **The connection manager needs a static `ConnectionString`** — one not
  fully owned by a whole-string property expression — for the auto-split
  to trigger at all. That's a one-time modeling choice per connection
  manager, not a per-run concern.
- Once bound, the value is a **one-way trip**: nobody (not us, not package
  logic) can read it back out afterward; only the runtime applies it. This
  is by design, not a limitation to work around.
- **Recipe for protecting another credential later** (condensed — full
  version in `CLAUDE.md` → "Sensitive credential"): give the connection
  manager a static `ConnectionString` with a literal `Password=` → rebuild
  → redeploy → confirm `CM.<name>.Password` now exists as `sensitive=1` in
  `catalog.object_parameters` → create a sensitive environment variable →
  bind it with `set_object_parameter_value`. No `ctl`/`SsisRunner` code
  changes needed — both already resolve parameters generically by name.

## 9. File path access for CSVs in a production environment

Today, `SourceFolder` points at a local drive on the SQL Server box
(`D:\PoC\SSIS\Data\Input`) — fine for a single-box PoC, not realistic in
production, where the people/systems producing the files won't have
filesystem access to the SQL Server machine.

**No package or `ctl` change is needed to move to a network path** —
`SourceFolder`/`SourceFileName` (and `LoadReferenceData`'s equivalents) are
already parameters, bound the same way whether the value is a local path or
`\\fileserver\share\drops\`. The only new work is **who can actually open
the file**, which is an AD/NTFS question, not an SSIS one.

**Who reads the file:** when `ctl`/`SsisRunner` calls
`catalog.start_execution` directly (today's path, no SQL Agent involved),
SQL Server spawns `ISServerExec.exe`, which inherits the **SQL Server
Database Engine service account's** own Windows identity — not the SQL
login used to call the stored procedure, and not the identity of whoever
triggered the run. Those are separate security boundaries entirely.

**What to configure, in order:**

1. **Run the SQL Server Database Engine under a dedicated domain service
   account** (not a virtual/local account like the current dev box's
   `NT Service\MSSQL$SQLFORPOC`) — a virtual account authenticates to a
   remote file share as the machine's computer account, which isn't
   something you can scope cleanly if other services share that box.
2. Grant that account **Read** at both the SMB share layer and the NTFS
   layer on the drop folder (List folder/read data, read attributes). Add
   Modify+Delete on an archive subfolder only if a package moves/renames
   files after loading.
3. **Grant to an AD group, not the named account**, so rotating the
   service account or adding a second SSIS box doesn't mean re-touching
   share ACLs.
4. **Verify** with `SELECT servicename, service_account FROM
   sys.dm_server_services;` — confirms exactly which identity to grant, and
   is the fastest way to prove/disprove a permission problem.

**If broader access for the whole SQL instance is unacceptable**, the
alternative is routing execution through a **SQL Agent job step (subsystem:
SSIS Package Execution) with a Proxy tied to a Credential** — a real,
lower-privilege Windows account scoped just to that share, independent of
the Database Engine's own account. That's a genuine architecture change to
`ctl` (calling `sp_start_job` instead of `catalog.start_execution`
directly), not a config tweak — worth deciding on purpose rather than
defaulting into it, and a good candidate topic for this conversation if
per-package identity isolation matters to the target environment.

**Not a double-hop/Kerberos-delegation problem** — the file read is a
single hop (the SQL Server machine's own identity talking to the file
server directly), not a relay of any end-user's credentials.
