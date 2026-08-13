---
name: ssis-poc
description: Use when resuming work on the SSIS PoC project (d:\PoC\SSIS) in a fresh session — orients quickly by summarizing the full build timeline, current verified state, and exactly where to look (CLAUDE.md / README.md / which SQL script) for any given topic, so nothing gets re-derived or re-broken.
---

# Resuming the SSIS PoC

This is the fast-orientation entry point for this project, not a replacement
for its two real reference documents:

- **[CLAUDE.md](../../../CLAUDE.md)** — the operational reference. Exact
  commands, machine-specific facts, 17 numbered traps already hit once, and
  the load-bearing design decisions. Auto-loads into every session's context
  already (it's project instructions, not just a doc) — read it before
  changing anything.
- **[README.md](../../../README.md)** — the usage-facing docs. What the
  project does, how to run it, how it was verified.

This file's job is different: give a **chronological map** of everything
built so a fresh session (or a human skimming) knows what exists and in what
order, without re-reading the whole conversation history or re-deriving
mechanisms that were already worked out — then point at the right place for
depth. If this file and CLAUDE.md/README ever disagree, **they win** —
update this file to match, not the other way around.

## What this project actually is, in one paragraph

Two SSIS Project-Deployment-Model packages (`LoadEmployees.dtsx`,
`LoadReferenceData.dtsx`) that read CSVs and load SQL Server tables, fully
parameterized. On top of that sits an SSISDB catalog deployment, a custom
`ctl` control framework (`SsisPoC.ctl`) that lets an application start any
package by name + a flat parameter bag and poll it by RunId, a .NET 8 CLI
(`SsisRunner`) driving that framework, and (most recently) a real
end-to-end demonstration of SSISDB's sensitive-value/credential protection.

## Build timeline

**2026-08-12 — base project + local execution**
- Both packages built (Flat File → Derived Column → OLE DB Destination),
  parameters externalized (`SSIS/Project.params`), target tables created
  (`Sql/01_CreateTarget.sql`).
- Two in-process local runners built on shared logic (`Scripts/PkgRun.cs`):
  `Scripts/Run-Package.ps1` and `Scripts/PkgRun.Console/`. Needed because
  local `dtexec.exe` is COM-broken on this machine (still true, still not
  worth chasing — see CLAUDE.md trap list intro).
- SSISDB catalog created via SMO (four-part workaround needed — assembly
  probing, `Microsoft.Data.SqlClient`, `TrustServerCertificate=True`, `clr
  enabled`), project deployed, `Dev` environment created and parameters
  bound (`Sql/02_SetupCatalogEnvironment.sql`).
- Verified end to end against the real `.\SQLFORPOC` instance.

**2026-08-12 → 2026-08-13 — the `ctl` control framework**
- v1: manifest/batch-only (`usp_StartRun` + a retry proc), built to answer
  "run N packages with different parameters from an application in as few
  round trips as possible" (`Sql/03_CreateControlFramework.sql`).
- **Reworked 2026-08-13** around a single-package primitive instead:
  `usp_StartPackage` (thin wrapper over `usp_StartRun`), fire-and-forget
  execution (no `SYNCHRONIZED`), poll by RunId (`usp_PollRun`) or by a
  caller-supplied `CorrelationId` across independent runs
  (`usp_PollCorrelation`). `usp_RetryRun` was deleted outright — calling
  `usp_StartPackage`/`usp_StartRun` again *is* the retry
  (`@RetryOfRunId` for lineage, `@LoggingLevel` for verbosity). A
  same-package concurrency guard was added (refresh-before-check, so a
  finished-but-unpolled run doesn't permanently block reuse).
- `Scripts/SsisRunner/` (.NET 8 CLI) built as the client, then **added to
  `SSIS.sln`** (it was deliberately excluded at first) — needed the
  `Release|Any CPU` solution-platform mapping trick (trap 6) like
  `PkgRun.Console` before it.
- Full detail: CLAUDE.md → "The `ctl` control framework" section.

**2026-08-13 — git baseline**
- `.gitignore` added (build output, VS/SSDT local state, deploy-wizard
  reports; `.claude/` was originally blanket-excluded here — narrowed since,
  see "Housekeeping" below).
- Root commit `46eab7f`: 24 files — both SQL scripts existing at the time,
  SSIS project source, all three .NET client entry points, sample CSVs,
  `SSIS.sln`, both docs.

**2026-08-13 — a genuinely protected credential**
- Every other connection in the project uses Integrated Security (nothing
  to protect); `AuditToken` had demonstrated encryption-at-rest with no live
  consumer since trap 13 (sensitive parameters can't be read by package
  logic — not via expression, not via Execute SQL Task mapping).
- `LoadEmployees`' `CM_TargetDb` was switched to SQL Server Authentication
  (new login `ssis_loader`, `Sql/04_CreateLoaderCredential.sql`) specifically
  to exercise the one consumer SSIS does allow: a connection manager's
  `Password` property. New sensitive environment variable
  `TargetDbPassword`, bound to the catalog's auto-generated
  `CM.CM_TargetDb.Password` parameter.
- Verified end to end via `ctl.usp_StartPackage`: 12 rows loaded, real
  password confirmed absent from every log/message table, ciphertext
  confirmed present internally.
- Two stale/wrong comments fixed along the way (`01_CreateTarget.sql`,
  `02_SetupCatalogEnvironment.sql` both wrongly claimed `AuditToken` was
  consumed via an Execute SQL Task — it never was and can't be).
- **Known, deliberate consequence:** this broke local (non-catalog)
  execution of `LoadEmployees` specifically — `Run-Package.ps1` /
  `PkgRun.Console.exe` both default to it and now fail with *"Login failed
  for user 'ssis_loader'"*, because the password only exists inside
  SSISDB's encrypted store and those entry points never touch SSISDB. Not a
  bug — pass `--package LoadReferenceData.dtsx` for a local run instead, or
  drive `LoadEmployees` through the catalog.
- Full mechanism (who splits `ConnectionString`, who maps the password onto
  the connection manager, and a numbered recipe for protecting another
  credential later without re-deriving any of this): CLAUDE.md →
  "Sensitive credential" section.

**2026-08-13 — this file**
- Added so a fresh session can reconstruct "what exists and why" in under a
  minute instead of re-reading transcript history.

## Current state, at a glance

- **Working and verified:** both packages via SSISDB catalog execution;
  the full `ctl` framework (start/poll/correlate/concurrency-guard/retry-by-
  reinvocation); `SsisRunner` CLI (`--package`, `--manifest`, `--status`,
  `--wait`); the `LoadEmployees` SQL-auth credential path end to end.
- **Known broken, not worth fixing:** local `dtexec.exe` (COM registration —
  confirmed not a missing-feature issue); local in-process execution of
  `LoadEmployees` specifically (see above — inherent to the credential fix,
  not a regression to chase).
- **Not committed yet** as of this file's creation: the sensitive-credential
  work (`CLAUDE.md`, `README.md`, `SSIS/LoadEmployees.dtsx`,
  `Sql/01_CreateTarget.sql`, `Sql/02_SetupCatalogEnvironment.sql` modified;
  `Sql/04_CreateLoaderCredential.sql` new). Run `git status` to check
  current state before assuming either way.
- **Not done / deferred** (only pursue if asked again — full list in
  CLAUDE.md): `ctl` procedures are unsigned, rely on caller being sysadmin;
  no cross-package concurrency limiter or per-package timeout; only one
  environment (`Dev`) exists; SSISDB retention still at the 365-day
  default; `dtexec /ISServer` never actually tried against the catalog.

## "I need to..." — where to look

| Need | Where |
|---|---|
| Run a package quickly | CLAUDE.md → "Commands known to work" |
| Add a new package to the `ctl` registry | CLAUDE.md → `ctl` framework section, "Adding a package is a registry row" |
| Protect a new credential (another connection manager password) | CLAUDE.md → "Sensitive credential" → the numbered recipe |
| Understand `CorrelationId` | CLAUDE.md → `ctl` framework section, "CorrelationId" subsection |
| Debug a failed/stuck run | CLAUDE.md → `ctl` framework section, "Debugging a run" |
| Rebuild + redeploy after a `.dtsx` edit | CLAUDE.md → "Commands known to work" (devenv build) + "SSISDB catalog" (deploy_project) |
| Something in SSIS/SSISDB is behaving weirdly | CLAUDE.md → "Traps already hit once" (17 numbered) — check here before spending time on it |
| Full usage docs / how to invoke anything | README.md |

## Housekeeping note

`.gitignore`'s `.claude/` entry was originally a blanket exclude (written
when only `settings.json` — pure session-local state — lived there). Since
this skill file is meant to be tracked and picked up in future sessions, the
rule needs to be narrowed to exclude just the session-local files, not the
whole directory, or this file won't actually make it into the repo. Check
`.gitignore` before assuming this file is committed.
