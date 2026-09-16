# SSIS PoC — agent context

Project purpose, layout, and full usage docs are in [README.md](README.md) —
read that first. This file is operational notes for an agent picking this
project back up in a fresh session: environment specifics, exact commands
known to work, and traps already hit once (don't re-discover them).

**Below this point is a long, deliberately-preserved chronological build log**
(see the note at the top of the "READ THIS FIRST" section for why it is kept
rather than rewritten). Treat everything in it as *history*, not as a live
description of this repo's current file layout — verify a claimed path
exists before acting on it. In particular:

## Orientation — what actually lives in this repo right now (2026-09-09)

As of commit `2f212bf` ("Move SSIS/Sql/Data/SSIS_From_Sandeep/Scripts/Build
out of this repo"), **the original PoC packages, SQL scripts, sample data,
the third-party `SSIS_From_Sandeep` portfolio, and the `.NET` runner/build
scripts described throughout the rest of this file (`SSIS/`, `Sql/`,
`Data/`, `Scripts/`, `SSIS.sln`) are no longer in this repository** — they
were moved to a separate location. `README.md` at the repo root still
describes that old layout and is now stale for the same reason; don't trust
its file tree either without checking.

What's actually here today:

| Folder | What it is |
|---|---|
| `Tools/SsisExtractor/` | `ssisx` — the portable, GAC-free SSIS extractor/analyzer/C#-code-generator CLI. This is the actively-developed core of the project now. Its own `README.md` and `docs/*-schema.md` are current; see `CLAUDE.md`'s own extensive "Phase 0 extractor" section below for the full build history. |
| `Tools/SsisValidationKit/` | `svk` — companion CLI producing human-readable walkthroughs/sample data from an extracted spec; ships in the same solution as `ssisx` (`SsisExtractor.slnx`) but must never depend on `Ssis.Extract.Codegen`/`Ssis.Extract.Cli` (see its own `SvkCore.csproj` doc comment). |
| `Tools/Etl.Core/` | A portable copy of the hand-written `Etl.Core` runtime library that `ssisx generate`'s output targets — see "Tools reorganized for real client portability" below for why this copy exists and how to refresh it. |
| `Tools/COPILOT_GUIDE.md`, `Tools/COPILOT_TESTING_GUIDE.md`, `Tools/.github/` | Client-facing docs and prompt files for running this tool via GitHub Copilot on a client site with no access to this repo's own history. |
| `Docs/` | Design/plan documents for major features (`Migration-Validation-Plan.md`, `Phase0-Extractor-Plan.md`, `Emitter-Rewrite-Plan.md`, `Generated-Tests-Plan.md`, review prompts, etc.) — referenced by name throughout the log below. |
| `Validation/` | The gate-3 golden-corpus comparison harness (`ssis-validate`), dev-only, not shipped to a client — see its own section below for why it's separate from `Tools/`. |
| `ClientMigration/` | Scanning/checklist tooling for surveying a client's own SSIS portfolio ahead of migration. |
| `Artifacts/` | Reference screenshots. |

**Practical implications for picking up work here:**
- Commands referencing `SSIS.sln`, `devenv`, `sqlcmd -i Sql\...`, or paths
  under `SSIS/`/`Data/`/`Scripts/` in the sections below describe a *former*
  state of this repo (or a sibling environment) — confirm the target still
  exists before running them.
- `Tools/SsisExtractor/scripts/Run-Tests.ps1` (or `dotnet test SsisExtractor.slnx`
  from `Tools/SsisExtractor/`) is the test entry point that still applies today.
- The AI-assisted-generation workflow (`ssisx generate` → `svk` → fill gaps →
  `ssisx apply-fills`/`apply-tests` → build/test) is the current focus; the
  `/ssisx-rewrite` skill (and its GitHub Copilot equivalent under
  `Tools/.github/prompts/`) is the standard way to drive it end to end.

## READ THIS FIRST — corrections from the 2026-09-02/03 gap audit

An audit was run deliberately from primary evidence only (source code, the
real SSIS component set, and behaviour measured via dtexec), specifically
**without** reading this file first, because this file has repeatedly
described the project's own work as more verified than the code showed. It
found six real defects under a fully green test suite. All are now fixed, and
the corrections below **supersede anything later in this file that
contradicts them.** Later sections were accurate when written; several are
now stale, and they are left in place rather than rewritten because they are
a historical record.

**Current test counts** (via the new `Tools/SsisExtractor/scripts/Run-Tests.ps1`,
which is the only thing that runs everything and fails on any failure):

| Suite | Count |
|---|---|
| `Ssis.Extract.Codegen.Tests` | 302 |
| `Ssis.Extract.Tests` | 156 |
| `Ssis.Runtime.Expressions.Tests` | 149 |
| `Etl.Core.Tests` (sibling repo) | 81 |
| `LoadEmployees.Tests` / `LoadReferenceData.Tests` | 11 / 7 |

**The refrain "the same 3 pre-existing unrelated failures" appears 27 times
below. It is obsolete â€” every suite is green.** Those three were NOT
"someone's in-progress SSDT edits", as this file repeatedly said: the tree was
clean and `SSIS/LoadReferenceData.dtsx` was committed, so they were permanent
committed failures â€” two stale goldens plus an `.ispac` test pinning a package
count the SSIS project had legitimately outgrown. They survived because
**there is no CI in this repo at all** (no workflow, no pipeline, and until
now nothing that ran `dotnet test`), not because an exit code was swallowed.

**What was actually wrong, and what it means for the deliverable:**

1. **Merge Join ignored `JoinType`.** `ProgramEmitter` hardcoded
   `MergeJoinType.LeftOuter` for every Merge Join. Measured via dtexec:
   **raw 0 = FULL OUTER, 1 = LEFT OUTER, 2 = INNER** â€” the exact reverse of
   the C# enum's ordinal order. The real portfolio's only Merge Join declares
   2, so the shipped deliverable was emitting unmatched left rows that SSIS
   drops. Any earlier claim here that raw 2 means LeftOuter is wrong, and the
   "verified via a real generated run" that backed it was circular â€” that run
   exercised code with LeftOuter compiled in.
2. **In-place column modification was invisible.** A Derived Column in
   "Replace &lt;column&gt;" mode (and Character Map's in-place mode) persists
   its expression on its own readWrite INPUT column and declares no output
   column. Every output-column-based mechanism missed it at once: codegen
   emitted the RAW value, and conformance, testgen, `expressions.csv` and the
   non-determinism manifest all recorded nothing â€” with 0 blocking gaps and
   100% coverage. Latent for the tracked corpus (grep for
   `usageType="readWrite"` finds none), severe for the next portfolio.
3. **Union All/Merge per-input column mapping was never read.** This file and
   `UnionEmitter` claimed "real SSIS enforces matching schemas across every
   Merge/UnionAll input". **False, measured:** one output column named
   `UnifiedName` accepted `CustName` from input 1 and `ClientName` from input
   2 and validated VS_ISVALID. The mapping is explicit in each input column's
   `OutputColumnLineageID`, which nothing read.
4. **Lookup duplicate keys resolved to the wrong row.** The generated cache
   used last-wins; **measured SSIS keeps the FIRST duplicate.** This changed
   the real deliverable (`Package_Transforms`' own `LKP_CountryCache`).
   `NoMatchBehavior` was also never read at all.
5. **Event handlers were omitted in complete silence.** Extracted all along,
   referenced nowhere in codegen. The third-party `Package_Advanced.dtsx` has
   an `OnError` handler containing an Execute SQL Task, so SSIS logs on error
   and the generated job does not. Reporting it moves that package out of the
   generatable set: **4/5 â†’ 3/5.** The count was wrong, not the package.
   **Closed in part 2026-09-03** — see "Event handler translation" below: the
   one real evidenced shape (a package-root OnError handler with a single
   Execute SQL Task, under the schema-default MaximumErrorCount everywhere)
   is now translated, verified against the real package, restoring it to the
   generatable set. Everything else about an event handler is still reported,
   not guessed.
6. **A guard could silently skip its step.** Conditional-precedence guards read
   variables via `PackageVariables.Get<T>`, which returns `default(T)` on a
   type mismatch â€” so a guard reading `int` against a `long` silently saw 0 and
   skipped. Guards now use the new `GetRequired<T>`, which throws.

**Three audit items dissolved on inspection and were deliberately NOT
"fixed"** â€” recorded so nobody re-opens them: `DT_UI8 â†’ long` is already
documented with its tradeoff reasoned out; a renamed Merge Join output column
is already safe (`MergeJoinEmitter` resolves via `SourceColumnLineageId`, not
by name); and `LegacyComponentIds`' three bare CLSIDs already carry their
provenance in the file. One audit claim was also simply **wrong**: unmodelled
control-flow TASK types were said to be omitted silently, but `WalkContainer`
has always ended with a generic fall-through gap. Only the event-handler half
was real.

**Two long-standing "unresolved"/"unverified" caveats are now closed with
evidence, so ignore the sections below that still say otherwise:**
- The Merge Join fixture's "phantom output columns" quirk is **root-caused**.
  Two wrong premises: `Microsoft.MergeJoin` starts with **TWO** inputs after
  `ProvideComponentProperties()`, not one (so the old `InsertInput` created a
  third), and it **auto-creates one output column per marked-used input
  column** (so the manual `AddMergeJoinOutputColumn` calls were duplicates).
  `SyntheticMergeJoin.dtsx` now exists, validates VS_ISVALID and runs under
  dtexec â€” Merge Join codegen had never been verified end to end before.
- Event handlers were "genuinely unverified against a real example". They are
  now verified against `Package_Advanced.dtsx`. Note the real element carries
  **no `DTS:ObjectName`**, so the event name comes from the FALLBACK read â€”
  the primary read would have failed on the first real package it met.

**The pattern worth internalising, because it has now produced four wrong
claims in this file:** a negative conclusion ("SSIS never persists X", "SSIS
enforces Y") drawn from a synthetic fixture is worth very little, because the
fixture may simply never have exercised X. That is how the Lookup join key,
the Script Component source shape, the Union All schema claim, and the Merge
Join quirk all became wrong. Measure against a real SSDT-authored package or
a dtexec run before writing a negative down.

Also: commit `4f679b0` ("Close the SsisExtractor gap-audit plan (Phases
1-3.6)") landed real work that was never written into this file. The sections
below jump from 2026-09-02's `DFT_LookupAndAggregate` round straight past it.

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

## Gate 3 prep (`Validation/`, added 2026-08-18 — golden corpus + comparison harness, built before a C# rewrite exists; first real run against the actual rewrite 2026-08-20)

A separate tool from the extractor, `ssis-validate` (`Validation/Ssis.Validate.Harness/`),
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
(`Validation/selftest/Ssis.Validate.SelfTestJob/`, explicitly NOT a rewrite of
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
`Validation/docs/gate3-harness.md` and `Validation/README.md`.

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

**Pointing `Validation`'s gate 3 harness at it, for real, produced two genuine PASSes**
against a freshly created container — full account and exact commands in
`Validation/docs/gate3-harness.md`'s own "first real gate 3 run" section, one fact worth
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

## Phase 0 extractor (`Tools/SsisExtractor/`, started 2026-08-13 — slice 5 complete, minus `pull`; synthetic component-coverage pass + slice 6's object-model oracle added 2026-08-14; Script Task AND Script Component source extraction — plan §4.5's both halves — plus `ssisx conformance` (migration-validation gate 1) added 2026-08-17; `Ssis.Runtime.Expressions` + `ssisx testgen` (migration-validation gate 2) added 2026-08-18; primary-key candidate detection added 2026-08-19; parallel-control-flow/direct-copy/error-routing synthetic fixture + client-facing portfolio survey added 2026-08-25; `ssisx generate` — SSIS-to-C# code generator, Phases 0-4 — built 2026-08-25, Gate 3 verified against its own output + nested-container support added 2026-08-26; portfolio survey now reports `ssisx generate` readiness too, added 2026-08-26; non-preload Execute SQL Task, OLE DB Source, and full-cache Lookup support added 2026-08-26, each Gate-3-or-equivalent verified; Conditional Split multi-destination routing — a new Etl.Core row-router abstraction plus condition translation — added 2026-08-26, Gate 3 verified; tested against a real 5-package/~30-component third-party portfolio (`SSIS_From_Sandeep`) 2026-08-27, one generation-gap classification bug fixed, Conditional Split multi-hop Union-All-remerge support added and end-to-end verified, File System Task pre-load/post-flow support added -- touching a new Etl.Core abstraction in the sibling SSIS_Rewrite repo -- and end-to-end verified; ADO NET Source/Destination support added, verified against the real package though not via a dedicated fixture -- an SSIS-object-model ADO.NET connection-manager creation issue in this environment blocked one, documented rather than resolved; FINDSTRING/TRIM support added to ExpressionTranslator 2026-08-27, closing a real router-level SsisFn-tracking bug along the way, verified against the real package and a dedicated fixture end-to-end; ternary and unary-negate support added to ExpressionTranslator 2026-08-27, same session, both already oracle-verified so no new semantics work needed, verified against the real package (gap reasons sharpened across two re-generations) and a dedicated fixture end-to-end; numeric '+' dispatch plus a computed SUBSTRING start argument added 2026-08-27, closing CustomerEnriched.EmailDomain fully -- verified with a fixture that reproduces the real package's own expression verbatim, run end-to-end; DATEDIFF("dd",...) support added 2026-08-27 with a new Ssis.Expression.Oracle corpus block (the first genuinely new semantics derivation this session, not just plumbing), closing CustomerEnriched.TenureDays fully -- found and named a new, separate, unfixed gap along the way (no SQL row/entity emitter tracks column nullability, so ISNULL on a nullable value-typed column fails to compile); nullable value-typed column support added 2026-08-27, same session -- an evidence-based minimal version (infer nullable from ISNULL(x) usage, unwrap with .Value everywhere else, a real named tradeoff vs. full null-flow analysis) scoped to SQL sources/Derived Columns only, verified end-to-end with a genuine NULL row against .\SQLFORPOC_2022; Data Conversion component support added 2026-08-28, first for SQL-source/single-destination flows only (a real dtexec probe run first to measure IgnoreFailure's actual behavior -- never drops a row, nulls the one failing column -- then LineageBuilder extended with a new "DataConversion" edge kind, TransformEmitter wired to emit SsisFn.ToNullableI4/ToNullableDate against the raw source column, and a real, previously-latent SqlRowEmitter/SqlRowReaderEmitter bug fixed along the way), then same day extended to the Conditional-Split-cross-reference case that round's own closing claim had missed -- RouterEmitter and a Derived-Column-referencing-a-Data-Conversion-column path both wired to the same TranslateDataConversion resolution, retiring both "Conditional Split condition nullability" and "CSV-source nullability" as gaps (the latter turned out not to exist at all for this feature), fixing two more real bugs found only by building/running the real package's whole regenerated solution for the first time (a missing nullableColumnNames thread into EntityEmitter, and a Roslyn nullable-flow-analysis CS8629 needing a targeted null-forgiving `!`), and closing a third, previously-SILENT PackagePlanner gap (a multi-source Merge Join flow used to generate broken code with zero reported gaps) -- `DFT_DerivedAndSplit` now closes fully end-to-end, verified against both the real package (0 warnings/0 errors across its whole generated solution) and a new dedicated fixture run against `.\SQLFORPOC_2022`; `EntityEmitter`'s own string-nullability exclusion fixed 2026-08-28, mirroring `SqlRowEmitter`'s earlier fix, verified with a unit test rather than a new fixture since it's mechanical parity with an already-proven fix; Flat File Destination support added 2026-08-28 -- a new Etl.Core FlatFileBulkSink write-side abstraction, a codegen bypass for the whole EF Core/DbContext path (a flat file has no table), and a narrow direct-copy exemption scoped to this destination type only -- closing RBC_Demo_ETL's own DFT_ExportDelimited/DFT_ExportFixedWidth fully (0 warnings/0 errors building the real package's whole regenerated solution) plus a dedicated fixture run end-to-end against `.\SQLFORPOC_2022` that empirically confirmed real fixed-width padding/truncation behavior, fixing two more real bugs along the way (BuildSqlFlowSource's server/database check wrongly assumed every destination has one, and a "zero SQL tables in the whole package" case that left Program.cs referencing a DbContext type that was never generated); Sort + Merge Join support added 2026-08-28 -- a new Etl.Core MergeJoinRowSource abstraction (Merge Join modeled as an IRowSource, not a new step type, so it needed zero changes to DataFlowStep), a corrected-after-one-wrong-reading SortPayload (key designation lives on Sort's own INPUT columns, not output), and a hand-walked (not LineageBuilder-based, which would be ambiguous here) backward resolution from Merge Join's own output through Sort to each side's real source -- closing RBC_Demo_ETL's own DFT_SortAndMergeJoin fully (0 warnings/0 errors building the real package's whole regenerated solution), with the join algorithm itself verified via 5 genuine unit tests since a dedicated dtexec-runnable fixture hit an unresolved SSIS object-model quirk (Microsoft.MergeJoin auto-generating extra output columns, documented rather than resolved); Conditional Split -> Sort -> Merge support added 2026-08-28, same day -- a different, single-source shape than the Merge Join round, closed with two surgical fixes and zero new Etl.Core code (Sort/Merge are genuine name-preserving pass-throughs, so the pre-existing destination-column resolution needed no changes) -- closing RBC_Demo_ETL's own DFT_MergeSortedBranches fully (0 warnings/0 errors building the real package's whole regenerated solution), this time WITH a working dedicated fixture run end-to-end against .\SQLFORPOC_2022 (unlike the Merge Join round), after discovering Microsoft.Merge needs a third, different "add a second input" recipe from both Union All's and Merge Join's own (it already has two inputs from construction)); ForEach Loop container support added 2026-08-28, same day, sized against Excel Source first (picked as the smaller lift) -- closing RBC_Demo_ETL's own FEL_SampleFiles by recognizing its one evidenced body shape (a single Execute SQL Task whose SqlStatementSource is entirely PropertyExpression-driven) via a new, deliberately narrow Etl.Core.Pipeline.ForEachLoopStep and a small standalone expression translator (not a reuse of the pipeline-column-based ExpressionTranslator, since there is no buffer here), verified against the real package's own zero-gap planner resolution AND a genuinely working dedicated fixture run end-to-end against real files and .\SQLFORPOC_2022 (unlike the Merge Join round's own unresolved fixture attempt); Excel Source support added 2026-08-28, same day -- a new ExcelDataReader-backed Etl.Core.Excel.ExcelRowSource abstraction (explicitly not the real, Windows-only ACE OLEDB provider, to keep generated output portable), plus lifting the pre-existing OLE DB/ADO NET "no Derived Column found -> direct-copy pipeline" gate that was separately blocking the real motivating flow, and a DbContextEmitter.HasNoKey() fix for tables with no EF-convention-shaped key column at all -- closing RBC_Demo_ETL's own DFT_ExcelImport down to one honestly-named, non-crashing numeric-type-mismatch gap (r8-vs-i4 on a plain passthrough column, a new, precisely-scoped gap rather than a guessed cast), verified against the real package (0 warnings/0 errors on everything except that one column) and a dedicated fixture built via the real SSIS object model against a genuine, checked-in copy of the real DripEligibility.xlsx, run end-to-end against .\SQLFORPOC_2022, landing all 9 real rows correctly); OLE DB Command and
Multicast support added 2026-08-28, same day -- a new Etl.Core.Pipeline.OleDbCommandStep (a
genuinely new, destination-less flow shape, running one parameterized SQL statement per row via a
new IUnitOfWork.ExecuteSqlAsync overload) and a new MulticastStep reusing the existing
ConditionalSplitBranch abstraction completely unchanged -- closing RBC_Demo_ETL's own
DFT_FlagCustomers and DFT_FixedWidthImport fully (0 warnings/0 errors building each real package's
whole regenerated solution) plus two dedicated fixtures run end-to-end against .\SQLFORPOC_2022,
fixing a real Flat-File-Destination numeric-passthrough gap (ToString() widening, not a type-
mismatch gap) and discovering a genuine Microsoft.Multicast object-model quirk (it auto-provisions
a fresh, permanently-dangling spare output the instant an existing one gets a path attached) along
the way); numeric-passthrough-coercion for the one evidenced OLE DB/ADO NET r8-to-i4 pairing added
2026-08-28, same day -- a real dtexec PROBE run first (a new Ssis.Extract.FixtureBuilder
numeric-coercion fixture) measured round-to-nearest-ties-to-even as SSIS's own real rounding rule,
then a new SsisFn.NarrowR8ToI4 helper reproduces it exactly (checked, so overflow throws rather
than silently corrupting data -- a deliberate, unmeasured-against-real-SSIS conservative choice),
closing RBC_Demo_ETL's own DFT_ExcelImport fully and verified end-to-end with the same fixture
used for the probe, an exact row-for-row match against the real dtexec run; Data Conversion
DT_R8/DT_WSTR/DT_BOOL support added 2026-08-28, same day, built SPECULATIVELY with the user's
explicit sign-off after confirming no real package in the portfolio needs any of the three (every
real Microsoft.DataConvert instance targets only the already-supported i4/dbdate) -- another real
dtexec probe measured DT_WSTR's own genuinely different failure mode (truncates rather than
nulling out, the first Data Conversion target this tool has measured that isn't "NULL on
failure"), verified end-to-end with an exact row-for-row match against that probe and zero
regression on the one real package that does use Data Conversion; portfolio re-survey run
2026-08-30 (2/5 -> confirmed-still-2/5 with a changed blocker set, then 3/5 after the round
below), confirming Aggregate/Row Count really is moot (DFT_LookupAndAggregate has BOTH a Lookup
and an Aggregate downstream of its Multicast, so the Lookup gate wins regardless) and surfacing
one genuinely new gap; string-to-int passthrough coercion (SsisFn.ParseWstrToI4) added
2026-08-30, same day, closing RBC_Demo_ETL's own Package_Exports/DFT_AdoNetRoundTrip fully (0
warnings/0 errors) and taking the portfolio to 3/5 -- another real dtexec probe measured the same
round-to-nearest-ties-to-even rule as NarrowR8ToI4 but a genuinely different, also-measured
FAILURE mode (fails hard on non-numeric/empty/out-of-range text, unlike r8-to-i4 which never
fails and unlike Data Conversion's own IgnoreFailure which never fails either), verified
end-to-end with an exact row-for-row match on both the success and failure paths against that
probe, and along the way found (but deliberately did not fix, out of scope that round) a
pre-existing, unrelated CS1061 build bug in the `Package` project's own StagingCustomers/
SalesSummary transforms; that bug root-caused and fixed 2026-08-30, same day -- a real,
previously-silent TransformEmitter gap (a Script Component synthesizing brand-new columns was
wrongly assumed to be a genuine passthrough, since "not computed, not converted" had always
meant "safe to guess" until this flow broke that assumption) closed via a new allowlist of
recognized "safe to guess" pipeline component types, NOT a lineage walk (confirmed empirically
that Sort/Merge/UnionAll assign fresh lineageIds to their own output columns, so tracing lineage
back to "the true producer" would have wrongly broken every already-working Sort/Merge remerge
flow) -- found and fixed one real false positive along the way (Merge Join's own combined row
type is resolved independently by MergeJoinEmitter itself, needing a new opt-out flag), verified
against the real `Package.dtsx` (now builds 0 warnings/0 errors, closing two real instances of
the same bug) and confirmed to introduce zero new false positives across the extractor's entire
existing fixture corpus; a follow-up portfolio re-survey the same session held steady at 3/5 with
no new real gaps, so Excel Source SqlCommand mode was picked next and built SPECULATIVELY 2026-08-30
(the user was told first that this option, initially mis-framed as "real", actually has zero
evidence in the tracked portfolio, and chose to proceed anyway) -- a raw System.Data.OleDb probe
against the real checked-in workbook confirmed a bare "SELECT * FROM [SheetName$]" query returns
identical rows to OpenRowset mode through the real ACE provider, so that one shape (and only that
shape, since Etl.Core.Excel.ExcelRowSource has no query engine at all) is now translated;
verified end-to-end via a byte-preserving-derived fixture (not a fresh object-model build -- a
real, confirmed, left-unresolved SSIS object-model quirk blocks attaching an OLE DB Destination
downstream of a SqlCommand-mode Excel Source in this environment, documented rather than solved)
run against .\SQLFORPOC_2022 and the real workbook, landing byte-for-byte the same 9 rows as the
OpenRowset-mode fixture; a ForEach Loop whose body is a whole Data Flow Task (re-run once per
enumerated file, not just a single SQL statement) built speculatively 2026-08-30 after confirming
the real portfolio's only ForEach Loop uses the SQL-body shape instead -- a new Etl.Core
ForEachFileDataFlowStep (constructing a fresh IRowSource per iteration and running it through a
shared transform/sink via a fresh DataFlowStep instance each time) plus PackagePlanner/
PackageGenerator/ProgramEmitter changes to resolve and wire a plain single-destination flow
sourced from an expression-driven Flat File Source, verified end-to-end (generated, built 0
warnings/0 errors, run against .\SQLFORPOC_2022 and two real checked-in CSV files, all 3 rows
across both files landing correctly); Aggregate + Row Count support (GroupBy + Count only) built
2026-08-30 despite confirmed zero real payoff (the one real Aggregate instance is blocked by its
own upstream Lookup regardless), on the user's own explicit request -- Row Count needed nothing
at all (already a recognized passthrough component), Aggregate got a new AggregatePayload/
LineageBuilder "Aggregate" edge kind, a new AggregateRowEmitter (resolving its row type straight
off raw output columns, an Aggregate having no external metadata columns of its own, same reason
MergeJoinEmitter already established), and a new Etl.Core.Data.AggregateRowSource (blocking on
its input side, ordinary streaming IRowSource on its output side), verified end-to-end with an
exact row-for-row match against a real dtexec probe that also measured AggregationType=1's own
NULL-exclusion rule (SQL COUNT(column), not COUNT(*)); three more numeric-passthrough-coercion
pairings (i8/numeric/r4 -> i4) built speculatively 2026-08-30, same session, via a real dtexec
probe that measured both float-shaped pairings round to the same nearest-ties-to-even rule as
NarrowR8ToI4/ParseWstrToI4, with i8->i4 needing only a plain checked narrowing cast -- verified
end-to-end with an exact row-for-row match across all 9 seeded rows, midpoints included; three
more Data Conversion target types (DT_I2/DT_I8/DT_DBTIMESTAMP) built speculatively the same
session, closing this session's second "build both" round -- a real dtexec probe confirmed all
three follow the same "NULL on any failure" convention as the already-closed DT_I4/DT_DBDATE/
DT_R8/DT_BOOL, verified end-to-end with an exact match across all 5 seeded rows; AI-assisted gap
filling started 2026-08-31 -- chunk 1 of 4 (gap identity + work packets) adds a GapKind/GapTier
taxonomy routing each gap to who can actually close it (Tier 1 datum / Tier 2 script port / Tier 3
emitter feature, the last deliberately NOT AI-fillable), stable conformance-style GapIds, one
self-contained markdown work packet per Tier-1/2 gap under <out>/gaps/, and gaps.json -- changing
nothing about generated C# (verified byte-identical), and correcting a documented assumption along
the way: a Script Component's SourceCode array is (name, encoding, content) TRIPLES, not opaque
blobs, with main.cs (not ScriptMain.*) as its entry point; chunk 2 (2026-08-31) added the Tier-1
decisions mechanism (fills/<Package>.decisions.json, never written by ssisx, applied only when a
human confirmed it and the evidence still matches) -- and, investigating its first consumer,
CORRECTED this file's own long-standing claim that SSIS never persists a Lookup's join key: it does,
on the joining input column as a JoinToReferenceColumn property (already extracted all along), so a
Lookup flow now generates with NO human in the loop and the decisions path is only a fallback for a
Lookup that genuinely declares none; control-flow parallelism is now REPORTED rather than silently
flattened (2026-08-31) -- a non-blocking advisory naming how many executables SSIS ran concurrently
and what the package declared, closing an honesty hole rather than adding a capability; Tier-2 seams
and `ssisx apply-fills` (chunk 3) added 2026-08-31 -- a Script-Component-produced column becomes a
`private partial` Fill_ method so an unfilled gap is a COMPILE ERROR (CS8795) instead of a silently
dropped column, taking RBC_Demo_ETL's own Package from "builds clean while missing 6 columns" to
"builds clean with all 6 genuinely ported"; a disabled executable is now SKIPPED rather than
generated as if live (2026-08-31) -- a real correctness bug whose fix also removed a live
false-positive parallelism advisory, both measured against a real dtexec run rather than reasoned
about; the Script Task half of Tier 2 added 2026-08-31 -- a Script Task
becomes a whole ILoadTask with a RunScriptAsync seam plus a shared PackageVariables, and
investigating it surfaced TWO more silently-ignored attributes (conditional precedence
constraints), which keeps the portfolio honestly at 3/5 rather than a wrong 4/5; the EXPRESSION half of those
conditional constraints closed 2026-08-31 -- a real dtexec probe measured all seven edge kinds and
showed every outcome-based form running its successor on the FAILURE path while the package failed
anyway, so only `EvalOp=3` (Success AND expression) is reproducible under the single whole-package
transaction and is now generated as an Etl.Core ConditionalStep decorator, the rest reduced to one
named emitter feature (a failure-handler position outside that transaction) and the portfolio still
honestly 3/5; that failure-handler position BUILT 2026-08-31 -- an EF probe measured that a
unit of work autocommits after its own rollback, so a Failure constraint's successor becomes an
Etl.Core FailureHandlerAction run post-rollback (and is EXCLUDED from the step list, which was the
actual bug) with the package still failing, matching dtexec exactly bar the deliberate whole-package
rollback, leaving `Package` with 8 blocking gaps that are ALL Script seams and no tool gap at all

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

- **Gate 3 (`Validation/Ssis.Validate.Harness`) has been run against `ssisx generate`'s
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
`Validation/docs/gate3-harness.md` ("`KeyColumn` is hand-filled, not detected"), not
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
`Validation`'s `ConfigGenerator` (new `--primary-keys-json` flag) reproduced the two PoC
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

### Sort + Merge -- Conditional Split remerge (2026-08-28) -- closes RBC_Demo_ETL's own `DFT_MergeSortedBranches`

The follow-up named immediately above, picked up the same session: a DIFFERENT shape from the
multi-source Merge Join round -- single source, `Conditional Split -> Sort -> Sort -> Merge ->
one shared destination`, no per-branch transform at all
(`CSPLIT_ByCountry -> {SORT_Canada, SORT_RestOfWorld} -> MRG_SortedUnion -> OLEDST_CustomerMerged`,
confirmed real from `Package_Transforms.dtsx`'s own XML, including a
Data-Conversion-derived column, `CustomerID_i4`, referenced directly at the destination with NO
Derived Column anywhere in the flow). Traced through by hand before writing any code (paths,
input/output column names end to end) rather than assumed from the multi-source round's own
shape, and the result was smaller than expected either way: **two surgical fixes, no new
`Etl.Core` runtime code at all.**

**Why no new runtime abstraction was needed, unlike Merge Join.** `Microsoft.Sort`'s own output
columns keep the EXACT SAME NAMES as its input (confirmed from the real evidenced XML: input
`CustomerID` in, output `CustomerID` out), and `Microsoft.Merge`'s own single output likewise
keeps the same names as BOTH of its inputs. Neither transforms a VALUE -- they only reorder/
combine rows. So the destination-column resolution that already exists (`TransformEmitter`'s
plain `row.{PipelineColumnName}` fallback, `EntityEmitter`'s nullable-set lookup by the same
name) needed zero changes: it already resolves any buffer column purely by NAME, with no
awareness that a Sort or Merge sat somewhere upstream. The only thing missing was letting
`PackagePlanner.ResolveBranch`'s own forward chain-walk (built for the Union-All-remerge round,
2026-08-27) recognize `Microsoft.Sort`/`Microsoft.Merge` as pass-throughs at all -- before this,
either one in the chain was a fatal "not supported" gap.

**The two fixes:**
- `PackagePlanner.ResolveBranch`: the pass-through match widened from
  `"Microsoft.DerivedColumn" or "Microsoft.UnionAll"` to also include `"Microsoft.Sort" or
  "Microsoft.Merge"` (only `DerivedColumn` still contributes to the branch's own
  `DerivedColumns` list). Two branches converging on the SAME `Microsoft.Merge` instance (one
  via each of its two named inputs) naturally resolve to the same downstream destination here,
  with no special-casing -- identical to how two branches sharing a Union All already worked.
- `PackageGenerator.GenerateConditionalSplitFlow`'s own "no Derived Column found -> direct-copy
  pipeline" gate was widened from `flow.DerivedColumn is null` to
  `flow.DerivedColumn is null && flow.DataConversion is null` -- the exact same widening the
  single-destination path already got in the Data Conversion round (2026-08-28, earlier the same
  day). Without it, this flow (genuinely zero Derived Columns anywhere, its only "transform" being
  a Data-Conversion cross-reference) was wrongly rejected as a pure direct copy, even though
  `TransformEmitter`'s own `convertedByName` resolution (already threaded into every branch's
  call regardless of `DerivedColumns.Count`, confirmed by reading `PackageGenerator.cs` before
  touching anything) was fully capable of handling it once the gate got out of the way.

**Verified two ways, same discipline as every other round:**
1. **The real motivating case** -- regenerating `Package_Transforms.dtsx`:
   `DFT_MergeSortedBranches`'s own gap is GONE entirely (confirmed by reading the emitted
   `CustomerMergedCanadaTransform.cs`/`...RestOfWorldTransform.cs` directly:
   `CustomerID = SsisFn.ToNullableI4(row.CustomerID), Email = row.Email,` -- the Data Conversion
   cross-reference resolving correctly with no Derived Column in sight). The whole regenerated
   solution (all wired flows plus a copy of `Etl.Core`) builds with **0 warnings/0 errors**.
2. **A new dedicated fixture**, `tests/Ssis.Extract.Tests/Fixtures/SyntheticSortMergeRemerge.dtsx`
   (`Ssis.Extract.FixtureBuilder`'s new `sort-merge-remerge` mode) -- deliberately EXCLUDES Data
   Conversion (already proven separately) to isolate exactly what's new here: OLE DB Source ->
   Derived Column (`LoadedAtUtc`, shared, needed only to avoid re-tripping the pre-existing
   direct-copy gate, since this fixture has no Data Conversion to satisfy it instead) ->
   Conditional Split (`Category == "Canada"` -> Canada; default -> RestOfWorld) -> Sort_Canada/
   Sort_RestOfWorld (each by `ID`) -> `Microsoft.Merge` -> one destination. **Generated, built
   against a copy of `Etl.Core` (0 warnings/0 errors), and actually RUN against
   `.\SQLFORPOC_2022`**: 4 seed rows, deliberately interleaved by ID and category (not
   pre-grouped, and each branch's own two rows seeded in DESCENDING ID order so Sort's own
   reordering is genuinely exercised, not coincidentally already correct) -- read back
   afterward, not trusted from the exit code: all 4 rows (`Alice`/`Bob`/`Carol`/`Dave`) landed in
   `dbo.SyntheticSortMergeTarget` with the correct `Name`, regardless of which branch or Sort
   pass they came through. This is genuinely STRONGER evidence than the multi-source Merge Join
   round could produce -- that one never got a working dedicated fixture at all.

**Three real object-model facts nailed down building the fixture, none guessed** (same "ask the
runtime, don't guess" discipline as trap 12) -- worth keeping since `Microsoft.Merge` turned out
to need a THIRD, different recipe from both components already documented in this file for
"how do you add a second input to a pipeline component":
- `Microsoft.Merge` already has **TWO inputs** immediately after `ProvideComponentProperties()`
  -- unlike `Microsoft.UnionAll`/`Microsoft.MergeJoin` (both start with exactly ONE, needing
  `InputCollection.New()`/`InsertInput` respectively to add a second, per this file's own
  Union-All-remerge and Merge Join sections). Confirmed only after BOTH those other recipes
  failed against Merge specifically: `InputCollection.New()` (Union All's own approach) created
  a malformed THIRD input and crashed the native `ReinitializeMetaData()` with an
  `AccessViolationException` (not a catchable `COMException` -- a genuine native crash);
  `InsertInput` (Merge Join's own approach) threw `COMException 0xC020800E` immediately, even
  BEFORE any path was attached (ruling out the "first input already has a live connection" cause
  UnionAll's own doc comment names for that same HRESULT). The fix needed neither call: both of
  Merge's own inputs already exist from construction, so both paths attach directly to
  `InputCollection[0]`/`[1]`.
- Once past that, Merge behaves like Union All in every other respect: it auto-populates its own
  output columns from the FIRST input via `ReinitializeMetaData()`, but the SECOND input's own
  columns are NOT auto-mapped -- each needs `SetInputColumnProperty(..., "OutputColumnLineageID",
  ...)` pointed at the matching (by name) output column the first input already created.
- Unlike `Microsoft.MergeJoin`'s own still-unresolved quirk, `Microsoft.Merge` did NOT
  auto-generate any extra output columns after `ReinitializeMetaData()` -- confirmed by reading
  the saved XML back, exactly the 3 intended columns (ID/Name/Category).

Two new unit tests, both reading the checked-in fixture directly rather than hand-building a
`PipelineSpec` (`PackagePlannerTests.Plan_WalksThroughSortAndMerge_...`,
`PackageGeneratorTests.Generate_WiresAConditionalSplitFlowWithNoPerBranchTransform_...`).
Regression: 111/111 in `Ssis.Extract.Codegen.Tests` (was 109), same 3 pre-existing unrelated
failures elsewhere. No `Etl.Core` changes at all this round -- nothing to commit in the
`SSIS_Rewrite` repo.

**Remaining known gaps, unchanged except for this round's own closure:** Excel Source, ForEach
Loop containers, Data Conversion target types other than DT_I4/DT_DBDATE. Conditional Split ->
Sort -> Merge, named above, is now closed for the one evidenced shape.

### ForEach Loop containers (2026-08-28) -- closes RBC_Demo_ETL's own `FEL_SampleFiles`

Picked up the same session, sized live with the user first (`AskUserQuestion`, three options:
ForEach Loop / Excel Source / both) against the two remaining named gaps -- both appear exactly
once, both only in `Package_Advanced.dtsx`. ForEach Loop was the smaller lift: its extraction was
already done (`ForEachLoopPayload`, since 2026-08-14's synthetic component-coverage pass) and
explicitly, deliberately NOT code-generated (`PackagePlanner`'s own doc comment: "Etl.Core has no
loop abstraction to generate against"); Excel Source needs a brand-new third-party NuGet
dependency and extraction from scratch, deferred.

**The real evidenced body shape, traced by hand before writing any code:** `FEL_SampleFiles`
uses `Microsoft.ForEachFileEnumerator` (`Folder`, `FileSpec="*.csv"`, `Recurse="0"`,
`FileNameRetrievalType="1"`), maps its current value to `User::CurrentFile`, and its ENTIRE body
is one Execute SQL Task (`SQL_LogFileName`) whose `SqlStatementSource` is not a static attribute
at all but a `<DTS:PropertyExpression Name="SqlStatementSource">` string-concatenating literal
text with `@[User::CurrentFile]` -- confirmed real, not assumed, by reading the raw XML directly.
This shape -- "a loop that re-runs one parameterized SQL statement per file" -- is dramatically
smaller than the more commonly imagined "re-run a whole Data Flow Task per file" ForEach pattern,
and is exactly what `Etl.Core` needed to support: nothing else.

**Extraction needed ZERO new work.** `ExecutableSpec.PropertyExpressions` (a generic
`List<PropertyExpressionSpec>` on every executable, not ForEach-Loop-specific) already captures
`<DTS:PropertyExpression>` elements verbatim; `ForEachLoopPayload`/`ForEachFileEnumeratorSpec`
already capture Folder/FileSpec/Recurse/FileNameRetrievalType/VariableMappings. Confirmed by
grepping before writing anything -- this round is entirely a `Ssis.Extract.Codegen`/`Etl.Core`
change.

**New Etl.Core abstractions, deliberately the smallest shape that covers the real evidence, not
a general loop-over-anything construct:** `ForEachFileLoopAction` (`Folder, FileSpec, Recurse,
NameMode`) plus `ForEachFileNameMode` (`FullyQualified=0, NameAndExtension=1, NameOnly=2` --
values from Microsoft's own public `IDTSForEachFileEnumerator` documentation, the one raw-enum
mapping in this whole file NOT independently probed against a live object model, since it's
long-stable public API rather than an inferred custom property) in `Abstractions/`; a new
`Pipeline/ForEachLoopStep.cs` (`ILoadTask`, primary-ctor `(name, action, Func<string,string>
buildSql, ILogger<ForEachLoopStep> logger)`, mirroring `ExecuteSqlStep`'s exact shape) that
enumerates files in ordinal-sorted order (SSIS's own enumeration order is filesystem-dependent
and unreproducible, so a stable order was chosen deliberately), rebuilds the SQL text via
`buildSql` per file, and runs each through the SAME `IUnitOfWork.ExecuteSqlAsync` every other
step already uses -- one failing iteration rolls back everything, same all-or-nothing semantics
as the rest of a package. **Deliberately scoped to exactly this one body shape** -- a loop
re-running a Data Flow Task, a File System Task, or anything with more than one child task is
still a generation gap, not modeled here; inventing a general per-iteration-flow abstraction with
no real evidence would be exactly the guessed scaffolding this tool refuses to produce.

**Codegen additions in `Ssis.Extract.Codegen`:** `PackagePlanner` gained `ForEachFileLoopPlan`
(the resolved SSIS-side facts: folder/mask/variable/raw expression text) and a new
`ForEachFileLoopStep : PackageStep`, always a `Steps` entry regardless of its own position
relative to a Data Flow Task -- unlike a one-shot SQL/File System task, a repeating loop has no
pre-load-list equivalent to be hoisted into, the same reasoning `FlowStep` itself is never
hoisted either. `PlanForEachFileLoop` validates the ONE supported shape (enumerator type, exactly
one child, that child an Execute SQL Task, its own `SqlStatementSource` driven by a
PropertyExpression) and reports a precise gap for anything else -- including, deliberately, a
STATIC `SqlStatementSource` with no per-iteration expression at all: technically runnable via the
ordinary Execute SQL Task path, but running the identical statement N times is almost certainly
not the intended semantics of putting it in a loop, so it's called out rather than silently
"working." A new `ForEachLoopEmitter.cs` translates the raw expression text into C#: parses via
the ALREADY-BUILT `Ssis.Runtime.Expressions.Parser` (gate 2, 2026-08-18) and walks the AST with a
translator deliberately much narrower than `ExpressionTranslator` -- only `StringLiteral`, the
loop's own `Reference` (matched by name against the loop's own mapped variable, resolved to a bare
C# identifier), and `+` (string concatenation) are supported, since there is no pipeline/buffer
here at all for the existing column-resolution machinery to key off of. `ProgramEmitter` gained
`ProgramForEachFileLoopStep` and a step-emission case emitting
`new ForEachLoopStep("Name", new ForEachFileLoopAction(...), currentFile => {expr}, sp.GetRequiredService<ILogger<ForEachLoopStep>>())`;
its own `CSharpStringLiteral` helper was promoted from `private` to `internal` so
`ForEachLoopEmitter` could reuse the exact same escaping (same sharing precedent as
`TransformEmitter.CollectSsisFunctions`).

**Verified two ways, same discipline as every other round:**
1. **The real motivating case** -- regenerating `Package_Advanced.dtsx`: `FEL_SampleFiles`'s own
   gap is GONE entirely (confirmed both from the gap report and directly via
   `PackagePlanner.Plan`, which resolves `Folder`, `FileSpec="*.csv"`,
   `NameRetrievalTypeRaw=1`, `VariableName="User::CurrentFile"`, and the exact real SQL template
   with zero gaps of its own). The package as a WHOLE still produces no files, correctly and for
   unrelated, pre-existing reasons -- `DFT_ExcelImport` (Excel Source, still deferred) and
   `DFT_FlagCustomers` (no destination) are its only other flows, so
   `ProgramEmitter.Emit`'s own "no Data Flow Task could be planned" gate still fires; this
   round's own fix is independently confirmed correct via `PackagePlanner.Plan` directly, not
   masked by that unrelated gate.
2. **A new dedicated fixture**, `tests/Ssis.Extract.Tests/Fixtures/SyntheticForEachFileLoop.dtsx`
   (`Ssis.Extract.FixtureBuilder`'s new `foreach-file-loop` mode): an ordinary OLE DB Source ->
   Derived Column -> OLE DB Destination flow (`DFT_Load`, included ONLY so the whole package has
   a real Data Flow Task and clears the gate named above -- mirroring the real package's own
   shape, where `FEL_SampleFiles` sits ALONGSIDE `DFT_ExcelImport`, not instead of it) plus a
   sibling, precedence-independent `FEL_SampleFiles` loop reproducing the real shape exactly
   (`Microsoft.ForEachFileEnumerator`, `FileNameRetrievalType=1`, one Execute SQL Task with a
   PropertyExpression). **Generated, built against a copy of `Etl.Core` (0 warnings/0 errors),
   and actually RUN against `.\SQLFORPOC_2022` and three real sample `.txt` files** (checked in
   under `tests/Ssis.Extract.Tests/Fixtures/synthetic-foreach-file-loop-files/`) -- read back
   afterward, not trusted from the exit code: `dbo.SyntheticForEachFileLoopLog` got exactly 3
   rows (`alpha.txt`, `beta.txt`, `gamma.txt`, in that sorted order, confirming the step's own
   ordinal sort), and `dbo.SyntheticForEachFileLoopTarget` got its 2 rows via the ordinary
   `DFT_Load` path, unaffected by anything in this round. This is a genuinely WORKING dedicated
   fixture, unlike the Merge Join round's own unresolved one.

**Four real object-model facts nailed down building the fixture, none guessed** (same "ask the
runtime, don't guess" discipline as trap 12) -- worth keeping since a `ForEachLoop`'s own runtime
shape turned out to differ from every other component type built via the object model so far in
this file:
- `Application.ForEachEnumeratorInfos` (NOT `EnumeratorInfos`), keyed by the enumerator's own
  DISPLAY name ("Foreach File Enumerator"), not its `CreationName`
  ("DTS.ForEachFileEnumerator.8") -- confirmed by reflecting `Application` directly rather than
  guessing from the pipeline object model's own, differently-shaped
  `PipelineComponentInfos`/`CreationName` convention.
- `ForEachEnumeratorInfo.CreateNew()` returns a `ForEachEnumeratorHost` whose `InnerObject` is a
  raw, untyped COM object (no strongly-typed `ForEachFileEnumerator` wrapper class exists in this
  GAC) -- its own settable config lives as plain dynamic `DtsProperty` entries on the HOST itself
  (`feHost.Properties["Directory"].SetValue(feHost, ...)`), reflected directly rather than
  guessed at.
- Two of those property NAMES differ from their own SAVED XML attribute names -- `"Directory"`
  (not `"Folder"`, despite `<ForEachFileEnumeratorProperties Folder="...">` in the saved XML) and
  `"FileNameRetrieval"` (not `"FileNameRetrievalType"`) -- yet another instance of the "runtime
  property name diverges from the persisted XML attribute name" lesson trap 12 already
  established for other components, confirmed by reading the saved XML back after setting them.
- `ForEachLoop.VariableMappings.Add()` takes ZERO arguments (an initial guessed
  `Add(variableName, valueIndex)` signature failed to compile) -- it returns a
  `ForEachVariableMapping` whose own `VariableName`/`ValueIndex` are ordinary settable properties
  set afterward, the same "`Add()` then set properties on the result" shape `InsertOutput`
  already uses elsewhere in this file for a Conditional Split's own case output.

Two new `PackagePlannerTests`/`PackageGeneratorTests` reading the checked-in fixture directly,
four `ForEachLoopEmitterTests` (the real `FEL_SampleFiles` expression translating exactly;
a different-variable reference degrading to a gap; an unsupported function-call shape degrading
to a gap; the whole-expression-is-the-variable case), and four `Etl.Core.Tests` (`ForEachLoopStepTests`,
using a REAL temp directory with real files -- sorted-order execution, each of the three
`ForEachFileNameMode` values, and the zero-matching-files case). Regression: 117/117 in
`Ssis.Extract.Codegen.Tests` (was 111), same 3 pre-existing unrelated failures elsewhere; 33/33
in `Etl.Core.Tests` (was 29).

**Remaining known gaps, updated once more:** Excel Source, Data Conversion target types other
than DT_I4/DT_DBDATE. ForEach Loop containers, named above, is now closed for the one evidenced
body shape (a single expression-driven Execute SQL Task) -- a loop re-running a Data Flow Task
per file remains unsupported and unevidenced.

### Excel Source (2026-08-28) -- closes RBC_Demo_ETL's own `DFT_ExcelImport`, plus lifting the
### OLE DB/ADO NET "direct-copy" gate that was blocking it for an unrelated reason

The last named gap from the `SSIS_From_Sandeep` sweep, picked up same session after ForEach Loop,
scoped live with the user first (`AskUserQuestion`): Excel Source needed a new third-party NuGet
dependency (`ExcelDataReader`, since the real ACE OLEDB provider path is Windows-only and this
generator's own output must stay portable), so it got sign-off before adding anything. Network
access to nuget.org was confirmed working first (a real risk in this environment, not assumed).

**The real evidenced shape, read from `Package_Advanced.dtsx`'s own `EXCEL_SRC_Drip` before
writing any code:** `componentClassID="Microsoft.ExcelSource"` -- NOT the `ManagedComponentHost`
discriminator ADO NET/Script Component use, a plain, distinct component type, and structurally
IDENTICAL to `Microsoft.OLEDBSource` (same `OpenRowset`/`AccessMode`/`SqlCommand`/`CommandTimeout`
property names, even the same connection role name, `"OleDbConnection"`). The one evidenced
`AccessMode` is `0` (OpenRowset), with `OpenRowset="DripEligibility$"` -- the trailing `$` is the
ADO/Jet convention marking "this is a worksheet, not a named range". Every column comes back
`r8`(double)/`wstr`(string) -- Excel/ACE OLEDB has no narrower numeric type at all, confirmed
from the real worksheet's own actual data (`CustomerID` is business-meaningful but still r8). The
EXCEL connection manager's own `ConnectionString` is a full ACE OLEDB provider string
(`Provider=Microsoft.ACE.OLEDB.12.0;Data Source=<path>.xlsx;Extended Properties="EXCEL 12.0
XML;HDR=YES";`), not a bare path the way FLATFILE/FILE connection managers store one.

**Extraction: `ExcelSourcePayload`** (`Ssis.Extract.Model.Pipeline.ComponentPayloads.cs`), wired
into `PipelineReader.cs` on `componentClassId == "Microsoft.ExcelSource"` -- a straight mirror of
`OleDbSourcePayload`, kept as its own type (not reused) since it's read via a different provider
with its own real-world quirks (the `$` suffix, the rigid r8/wstr typing) and, more importantly,
needs a completely different RUNTIME read path in codegen. `DtsxPackageReader.ParseConnectionString`
gained a third parameter, `isExcel`, alongside the pre-existing `isFlatFileLike`: an EXCEL
connection manager's own `"Data Source"` key is ALWAYS the workbook file path (unlike an ordinary
OLEDB connection string, where `"Data Source"` names a SQL server host) -- so it's still parsed
via the generic key=value path (Extended Properties/Provider are real signals worth keeping), but
`ParsedConnectionString.FilePath` is ADDITIONALLY populated from the same `"Data Source"` value,
letting codegen's existing `BuildFileSourceEntry` helper (built for CSV/FILE connection managers)
work UNCHANGED for Excel too -- no separate Excel-specific path-resolution code needed anywhere.

**Etl.Core: a new `Excel/` read-side abstraction**, deliberately NOT using the real ACE OLEDB
provider (Windows-only, an install this rewrite must not depend on) -- `ExcelRowSource<TRow>`
(`Etl.Core.Excel`) wraps the `ExcelDataReader` NuGet package instead, reading each cell already
typed (double/string/DateTime/bool/null) from the worksheet's own cell format. Two real API facts
nailed down building it, neither guessed:
- `ExcelReaderFactory.CreateReader(stream)`'s own default configuration calls
  `Encoding.GetEncoding(1252)` even for a modern `.xlsx` workbook -- throws
  `NotSupportedException` without `CodePagesEncodingProvider` registered, the exact same gap
  `CsvRowSource<TRow>` already had a fix for (`Etl.Core.Csv.CodePageSupport`, `internal`, so
  reachable from `Etl.Core.Excel` in the same assembly without duplicating it).
- The raw `IExcelDataReader` (no `.AsDataSet()` header-aware binding) has **no working column
  NAME resolution at all** -- confirmed real, not assumed, via a direct probe: `GetName(i)` throws
  `NotSupportedException` unconditionally. It is a plain positional grid, the same constraint a
  Flat File Source already has (columns identified by POSITION, never name) -- unlike a SQL
  SELECT's own named result columns. `ExcelSourceOptions.WorksheetName` itself IS still resolved
  by name (`reader.Name`, which DOES work -- the sheet-selection API is separate from the
  per-column API), looping `reader.NextResult()` until it matches (with the `$` suffix stripped).

**Codegen: a new `ExcelRowEmitter`/`ExcelRowReaderEmitter` pair**, kept as their own files rather
than reusing `SqlRowEmitter`/`SqlRowReaderEmitter` even though the ROW-TYPE shape is identical --
`SqlRowReaderEmitter` reads via `DbDataReader.GetFieldValue<T>(reader.GetOrdinal(name))`, a
`System.Data.Common.DbDataReader`-only convenience method with no `IDataRecord` equivalent, and
(per the finding above) name-based ordinal lookup doesn't work here anyway. `ExcelRowReaderEmitter`
reads by ORDINAL POSITION instead -- each column's index within `PipelineResolver.Resolve(output)`'s
own `Columns` list, computed BEFORE any null-typed column is skipped, so a later column's true
worksheet position is never shifted -- via a small `ClrTypeName -> IDataRecord method name`
table (`GetDouble`/`GetString`/`GetInt32`/... ); an unmapped CLR type (DateOnly, Guid, byte[], ...)
is a named gap, not guessed, since `IDataRecord` has no generic fallback. `PackageGenerator`
gained `BuildExcelFlowSource` (AccessMode must be 0 -- a SqlCommand-mode Excel Source has no
obvious SQL dialect to assume, per `ExcelSourcePayload`'s own doc comment -- and the worksheet's
own HDR=YES/NO is read straight off `ParsedConnectionString.Extras["HDR"]`, a real, benign parser
quirk: the generic `;`-split has no quote awareness, so `Extended Properties="EXCEL 12.0
XML;HDR=YES"` splits ACROSS its own embedded `;`, landing `HDR` as its own extras key with a
trailing stray `"` -- convenient, not a bug, since it means HDR is directly readable with no
Excel-specific parsing at all). `ProgramEmitter` gained `ExcelFlowSource` (reusing the exact same
`FileSourceOptions`/`FileSourceKey` mechanism a CSV source already uses -- a workbook is just
another named folder/filename pair) and `usesExcel`-gated `using Etl.Core.Excel;`/`using
{Root}.Excel;` lines, mirroring `usesCsv`/`usesSql` exactly. `GenerateCommand.cs`'s own fixed
`Directory.Packages.props` template gained `ExcelDataReader`'s `PackageVersion` entry alongside
`CsvHelper`'s.

**Two real, previously-latent bugs found getting a genuine end-to-end run, neither visible from
just building the real package's own structural resolution:**

1. **The pre-existing "no Derived Column found -> direct-copy pipeline is not generated" gate for
   OLE DB/ADO NET destinations was still standing** -- Flat File Destination got exempted from it
   in an earlier round (2026-08-28, same day), but OLE DB never did, purely because nothing had
   ever built+run one. `DFT_ExcelImport` (Excel Source straight to an OLE DB Destination, no
   Derived Column at all) is exactly that shape, so closing Excel Source alone would have swapped
   "unsupported source type" for this OTHER, unrelated pre-existing gap. Sized live with the user
   again (`AskUserQuestion`) before touching it, since lifting a "deliberately conservative,
   unproven" restriction is a real scope decision, not a mechanical follow-on -- confirmed first
   that `TransformEmitter.Emit` already handles an empty `DerivedColumns`/`DataConversions` list
   correctly for ANY destination shape (same reasoning Flat File Destination's own exemption
   already established), then removed the gate entirely for the single-destination path (the
   Conditional Split variant's own separate gate is untouched, out of scope this round). Proven,
   not just argued: this is what actually let `DFT_ExcelImport` fully generate.
2. **A genuinely new type-coercion gap, found only by actually BUILDING the real package's
   generated output**: `DFT_ExcelImport`'s own `CustomerID` is buffered as `double` (Excel's own
   rigid typing) but the real destination table's column is `int` -- SSIS's own OLE DB provider
   coerces this implicitly at INSERT time, but a plain `row.CustomerID` passthrough in generated
   C# hit a real `CS0266` (`double` -> `int` is not an implicit C# conversion), confirmed by
   actually building a fixture reproducing this exact real pairing, not assumed. Fixed in
   `TransformEmitter.Emit`: the destination's own input column CACHES the SOURCE's buffer type
   (`PipelineInputColumnSpec.CachedDataType`, e.g. `"r8"`) independent of the destination's own
   EXTERNAL (real table) column type (`"i4"`) -- these normally agree, and Excel Source is the
   first real case where they don't. When they disagree, the column is reported as an explicit,
   precise gap (naming both CLR types) and DROPPED from the assignments -- the same "per-column
   gap, rest of the flow still generates" shape an untranslatable Derived Column expression
   already uses -- rather than guessing which numeric coercion (truncate/round/checked) the real
   OLE DB provider performs, since this tool has no oracle evidence for that rule.

**A third, genuinely more consequential bug was found building this round's own dedicated
fixture, not the real package** -- a real, previously-latent limitation of `DbContextEmitter`
itself, unrelated to Excel Source specifically but only ever exposed by it: **its own doc comment
claimed "EF works fine with a keyless-by-convention entity" whenever no `PrimaryKeyInference`
candidate exists, but that was only ever true by ACCIDENT.** Every real table examined before this
round happened to already carry a column matching EF Core's OWN separate, bare "Id"/"{Type}Id"
key-discovery convention (e.g. `EmployeeID` satisfies `"{Employee}Id"` case-insensitively) even
when `PrimaryKeyInference`'s own stricter integer-typed `"<table>ID"/"ID"` naming rule didn't
confidently confirm it. This round's own fixture table (`CustomerID`/`DripEligible`/`ReviewedBy`,
none named `Id`/`...Id`, and `CustomerID` is FLOAT so it couldn't match `PrimaryKeyInference`'s
own integer-type requirement even if renamed) is genuinely the first with NEITHER -- confirmed by
actually RUNNING the generated exe and hitting EF's own `InvalidOperationException: "... requires
a primary key to be defined"` at FIRST USE, invisible to `dotnet build`, breaking the WHOLE
package (not just this table) only at runtime. Fixed by emitting an explicit `entity.HasNoKey();`
(exactly EF's own suggested fix, from that same exception's message) whenever neither
`PrimaryKeyInference` NOR EF's own convention finds anything -- safe unconditionally, since
`Etl.Core.Data.SqlBulkSink<TEntity>` never uses EF's change tracking at all, only
`EntityTableMap`'s own schema-metadata read, which a keyless entity type still provides fine.

**Verified two ways, same discipline as every other round:**
1. **The real motivating case** -- regenerating `Package_Advanced.dtsx`: `DFT_ExcelImport` went
   from "no bespoke payload, unsupported source" all the way to fully wired EXCEPT one honestly-
   named, non-crashing gap (`CustomerID`'s own r8-vs-i4 mismatch) -- every other piece (row type,
   reader, entity, transform, DbContext, `Program.cs`) generates correctly. The package's other
   pre-existing gaps (`DFT_FlagCustomers`, no destination; the non-blocking `.Notification` gap)
   are unrelated and unchanged.
2. **A new dedicated fixture**, `tests/Ssis.Extract.Tests/Fixtures/SyntheticExcelSource.dtsx`
   (`Ssis.Extract.FixtureBuilder`'s new `excel-source` mode) -- built via the REAL SSIS object
   model against a genuine, checked-in copy of RBC_Demo_ETL's own `sample-data/
   DripEligibility.xlsx` (`tests/Ssis.Extract.Tests/Fixtures/synthetic-excel-source.xlsx` --
   reused verbatim rather than hand-built, since this project has no `.xlsx` WRITER anywhere and
   the real file already IS real evidence), confirming `AddOleDbComponent`/`ResolveOleDbMetadata`
   (this fixture-builder's existing shared helpers) work UNCHANGED for `Microsoft.ExcelSource` --
   `ReinitializeMetaData()` genuinely reads the real worksheet schema through the real, confirmed-
   installed ACE OLEDB provider. The backing table's `CustomerID` is deliberately `FLOAT`, not
   `INT` (matching the worksheet's own real r8 type exactly), so this fixture proves the CORE
   feature cleanly, without the separately-gapped r8-vs-i4 case masking it. **Generated, built
   against a copy of `Etl.Core` (0 warnings/0 errors), and actually RUN against
   `.\SQLFORPOC_2022`**: all 9 real rows from the real workbook landed correctly -- read back
   afterward, not trusted from the exit code, and byte-for-byte matching an independent
   `ExcelRowSource<TRow>` unit-test read of the same file (`Etl.Core.Tests.Excel.
   ExcelRowSourceTests`, 4 tests, the real workbook copied in as a checked-in test asset).

New tests: 4 `Etl.Core.Tests` (`ExcelRowSourceTests`, against the real workbook), 7
`Ssis.Extract.Codegen.Tests` (`ExcelRowEmitterTests` x2, `ExcelRowReaderEmitterTests`,
`PackagePlannerTests`/`PackageGeneratorTests` wiring tests, `TransformEmitterTests`' own
type-mismatch-gap test, `DbContextEmitterTests`' own `HasNoKey()` test). Regression: 124/124 in
`Ssis.Extract.Codegen.Tests` (was 117), same 3 pre-existing unrelated failures elsewhere; 37/37 in
`Etl.Core.Tests` (was 33).

**Remaining known gaps, updated once more:** Data Conversion target types other than
DT_I4/DT_DBDATE, and the newly-named numeric-passthrough-coercion gap (a source/destination CLR
type mismatch on a plain passthrough column, e.g. Excel's own r8-vs-i4). Excel Source and the
OLE DB/ADO NET direct-copy gate, named above, are now closed.

### OLE DB Command + Multicast (2026-08-28) -- closes RBC_Demo_ETL's own `DFT_FlagCustomers` and `DFT_FixedWidthImport`

Picked up the same session, after checking exactly where every remaining unsupported component
type in `SSIS_From_Sandeep` actually sits: OLE DB Command and Multicast each close a real,
currently-blocked flow outright; Aggregate/Row Count only appear inside `Package_Transforms.dtsx`'s
own `DFT_LookupAndAggregate`, which is already blocked by that flow's own Lookup regardless (the
established "never guess a Lookup's join key" gate fires before any destination/transform
resolution runs) -- so building them wouldn't newly generate anything this round. Sized live with
the user (`AskUserQuestion`) before starting; they picked the two closeable real gaps.

**OLE DB Command -- a genuinely new flow shape, no destination/entity/table at all.** The real
evidenced case (`Package_Advanced.dtsx`'s `DFT_FlagCustomers`: `OLESRC_CustomerIds` ->
`OLECMD_SetFlag`, `EXEC dbo.usp_SetCustomerFlag ?, N'flagged'`, one parameter, both components on
the same connection manager) has no destination component whatsoever -- the command itself is the
flow's sink, run once per source row. `Etl.Core.Pipeline.OleDbCommandStep<TRow>` (new) reads every
row from an `IRowSource<TRow>` and calls a new `IUnitOfWork.ExecuteSqlAsync(string sql, object?[]
parameters, CancellationToken ct)` overload per row -- implemented via EF Core's own
`Database.ExecuteSqlRawAsync(sql, parameters, ct)`, which converts `"{0}"`-style placeholders to
real `DbParameter`s (injection-safe, no new ADO.NET plumbing) and runs on the exact same
connection/transaction as everything else in the package. No buffering needed (unlike Conditional
Split/Multicast) -- each row's command runs immediately, so this genuinely streams.

`PackagePlanner.PlanDataFlow`'s existing destination search (OLEDBDestination/FlatFileDestination/
ADONETDestination) now falls back to checking for exactly one `Microsoft.OLEDBCommand` component
before reporting "no destination found" -- resolved into a new `OleDbCommandPlan(Component,
SqlTemplate, ParameterColumnNames)`, with every `?` rewritten to EF Core's own `{0}`, `{1}`, ...
positionally against the component's own declared `<inputColumns>` order (the well-documented
default when `ParameterMapping` is empty -- confirmed empty in the one evidenced instance).
A non-empty `ParameterMapping`, or a `?` count that doesn't match the declared column count, is a
named gap, never guessed. `PackageGenerator.GenerateOleDbCommandFlow` (new) resolves the flow's own
source via the existing `ResolveFlowSource`/`BuildSqlFlowSource` (widened to accept a **nullable**
`destinationComponent` -- when null, the "does the source target the same server/database as the
destination" check is skipped entirely, the same reasoning already established for a Flat File
Destination, since there's no second connection to compare against), validates the command's own
connection manager matches the source's (parsed server/database, not raw name, so two
differently-named CMs pointing at the same physical DB still pass), then emits a new
`ProgramOleDbCommandStep` bypassing EF/table/transform machinery entirely -- the same bypass shape
the Lookup path already uses. This flow contributes NO table, but `IUnitOfWork`'s own
DbContext-backed transaction still needs *something* to wrap, so the "does this package need a
DbContext at all" gate (already widened for a table-less Flat File Destination package) was
widened again to include an OLE DB Command-only package.

**Multicast -- reuses the Conditional Split branch abstraction completely unchanged.** Structurally
an unconditional Conditional Split: every row goes to every branch, no routing decision at all.
`Etl.Core.Pipeline.ConditionalSplitBranch<TRow,TEntity>`/`Abstractions.IConditionalSplitBranch<TRow>`
already buffer-then-flush per branch and are deliberately non-generic-in-`TEntity` so heterogeneous
branches can share one list -- exactly the real evidenced shape (`Package_Legacy.dtsx`'s own
`DFT_FixedWidthImport`: one Multicast fanning to BOTH a Flat File Destination audit copy and an OLE
DB Destination table load, two genuinely DIFFERENT sink *kinds* from the same component, something
no evidenced Conditional Split had ever needed). So **zero new Etl.Core sink/branch types** --
only a new `Etl.Core.Pipeline.MulticastStep<TRow>`, a near-copy of `ConditionalSplitStep<TRow>`
minus the router (`foreach branch: branch.Add(row)`, no `IRowRouter` at all).

`PackagePlanner` gained `PlanMulticast`/`MulticastPlan`, reusing `ResolveBranch` (Conditional
Split's own forward chain-walker) verbatim, just called once per output with no condition attached
-- `ResolveBranch`'s own destination-acceptance check was widened to include
`Microsoft.FlatFileDestination` (previously OLE DB/ADO NET only; a strict widening, safe for
Conditional Split too even though no evidenced case there needs it) and its gap-message wording was
parameterized (`componentKind`, defaulting to `"Conditional Split"`) so a Multicast-specific failure
reads correctly rather than mentioning "Conditional Split" by name.
`PackageGenerator.GenerateMulticastFlow` mirrors `GenerateConditionalSplitFlow`'s own Phase A/B
shape (resolve every branch's entity name up front, commit only if everything succeeds) with two
real differences: no `RouterEmitter` call at all, and each branch is independently checked for
whether it targets a Flat File Destination -- a Flat-File branch's entity name is derived from the
**destination component's own name** (not the branch's output name), deliberately mirroring the
SQL branch's own "identify by destination, not by branch" convention so two branches that
converge to the SAME Flat File Destination (a hypothetical, unevidenced Union-All case) would
naturally compute the identical entity name -- `ProgramEmitter`'s own `DistinctBy(EntityName)`
before both the `AddBulkSink`/`EmitFlatFileSinkRegistration` loops is what actually prevents a
duplicate DI registration in that case.

**Two real object-model facts nailed down building the Multicast fixture, one a genuine
correction of the plan's own first attempt** (same "ask the runtime, don't guess" discipline as
trap 12, and the same shape of surprise Merge/Merge Join/Union-All each already produced in this
file -- every pipeline component that adds a second input/output seems to need ITS OWN distinct
recipe): the initial attempt copied Conditional Split's own `InsertOutput(DTSInsertPlacement.
IP_AFTER, 0)` recipe to manually add a second output before attaching any paths -- this produced
FOUR outputs in the saved XML (two orphaned/dangling extras), confirmed via a debug probe rather
than guessed: **`Microsoft.Multicast` auto-provisions a fresh "spare" output the INSTANT an
existing one gets a path attached** (matching real SSDT designer behavior -- dragging a new path
off a Multicast in the UI needs no manual "add output" step first). The correct recipe needs no
`InsertOutput` call at all: after `ReinitializeMetaData()` there is exactly one output; attaching a
path to it grows the collection to two; attaching a path to THAT new one grows it to three, the
last one left permanently unconnected. This exactly matches the real evidenced XML
(`Package_Legacy.dtsx`'s own `MCAST_FixedRows`: two real, path-connected outputs plus a third
marked `dangling="true"`) -- confirmed by re-reading the real package's raw XML directly, not
assumed. A new `PipelineOutputSpec.Dangling` field (mirroring the existing
`DeleteOutputOnPathDetached`) was added so `PlanMulticast` can skip this trailing spare rather than
trying (and correctly failing) to resolve it as a real branch.

**A second real, previously-latent bug was caught building this fixture, not shipped:** the
Excel-Source-round's own numeric-passthrough-coercion check (comparing a column's SOURCE buffer
CLR type against its DESTINATION's resolved CLR type, gapping out on a mismatch) fired on this
fixture's own `ID` column (buffered as `int`, feeding a Flat File Destination) -- WRONGLY, since a
Flat File Destination has no real narrowing risk at all: every column it writes is fundamentally
text (its own connection manager types EVERY column `DT_WSTR` regardless of what feeds it,
confirmed real from that destination type's own connection-manager builder), unlike an OLE DB
Destination's binary/typed columns where a genuine coercion decision (round/truncate/checked) is
actually needed. First "fixed" by just suppressing the gap for a Flat File Destination -- caught as
still-wrong immediately by actually building the generated project: a bare `row.ID` passthrough
assignment still failed `CS0029` (`int` -> `string`) once the gap stopped blocking it. The real fix
widens the source to a real value, not text: `TransformEmitter.Emit` now tracks
`flatFileStringConversionColumns` separately from the OLE DB `mismatchedTypeColumns` gap set, and
a Flat-File-destined mismatched column gets `row.{col}.ToString()` (or `row.{col}?.ToString() ??
""` when nullable-inferred) instead of either a raw passthrough or a gap.

**Verified two ways, same discipline as every other round:**
1. **The real motivating cases** -- regenerating both `Package_Advanced.dtsx` and
   `Package_Legacy.dtsx`: `DFT_FlagCustomers`'s own "no destination found" gap is gone entirely
   (only the pre-existing, unrelated Excel Source r8-vs-i4 gap remains in that package); `Package_
   Legacy` drops to its own single, unavoidable Notification gap -- `DFT_FixedWidthImport`'s
   Multicast gap is gone completely. Both packages' WHOLE regenerated solutions (each plus a copy
   of `Etl.Core`) build with **0 warnings/0 errors**.
2. **Two new dedicated fixtures**, both via `Ssis.Extract.FixtureBuilder`'s new `oledb-command`/
   `multicast` modes: `SyntheticOleDbCommand.dtsx` (OLE DB Source -> OLE DB Command, `UPDATE
   dbo.SyntheticOleDbCommandTarget SET Flagged = 1 WHERE CustomerID = ?`, no destination at all)
   and `SyntheticMulticast.dtsx` (OLE DB Source -> Multicast -> {OLE DB Destination, Flat File
   Destination}, deliberately reproducing the real package's own two-different-destination-kinds
   shape rather than an easier one). **Both generated, built against a copy of `Etl.Core` (0
   warnings/0 errors), and actually RUN against `.\SQLFORPOC_2022`**: the OLE DB Command run
   flagged all 3 seeded `CustomerID`s (`Flagged` 0 -> 1, confirmed by reading the table back); the
   Multicast run landed all 3 seeded rows in BOTH `dbo.SyntheticMulticastTarget` (the SQL branch)
   AND `audit-trail.csv` (the Flat File branch, confirmed byte-for-byte correct), the strongest
   proof this tool's whole "gaps not guesses" philosophy can produce for a genuinely new dual-sink
   flow shape.

Backing tables: `tests/Ssis.Extract.Tests/Fixtures/synthetic-oledb-command-tables.sql`,
`synthetic-multicast-tables.sql`. Regression: 128/128 in `Ssis.Extract.Codegen.Tests` (was 124),
same 3 pre-existing unrelated failures elsewhere; 41/41 in `Etl.Core.Tests` (was 37, 4 new tests --
`MulticastStepTests`, `OleDbCommandStepTests`).

**Remaining known gaps, updated once more:** Aggregate and Row Count (both real, but only
evidenced inside a flow already blocked by Lookup, so building them wouldn't close anything new
without a further, currently-nonexistent real flow to prove them against), Excel Source in
SqlCommand mode, a ForEach Loop re-running a whole Data Flow Task per file, Data Conversion target
types other than DT_I4/DT_DBDATE, and the numeric-passthrough-coercion gap (now correctly scoped
to OLE DB/ADO NET destinations only -- Flat File Destination's own version of this is closed,
above). OLE DB Command and Multicast, named above, are now closed for their one evidenced shape
each.

### Numeric-passthrough-coercion for OLE DB/ADO NET destinations (2026-08-28) -- closes the r8-to-i4 case, e.g. RBC_Demo_ETL's own `DFT_ExcelImport`

The next round after OLE DB Command + Multicast, picked from the "remaining gaps" list by explicit
user choice (`AskUserQuestion`) over Data Conversion's other target types, Excel Source's
SqlCommand mode, or a ForEach Loop re-running a whole Data Flow Task. This is the gap
`TransformEmitter`'s own doc comment named from the Excel Source round: a plain passthrough
column whose source buffer type doesn't match its destination's external metadata type (r8 from
Excel/ACE OLEDB vs. i4 at a real table) was reported as a generation gap rather than guessed,
since SSIS's own OLE DB provider performs the narrowing implicitly at insert time and this tool
had no evidence for which rounding/truncation rule to reproduce.

**Real evidence gathered first, via a new dtexec PROBE, before writing any generator code** --
same "ask the runtime, don't guess" discipline as trap 12, extended here from object-model
properties to a live INSERT's own numeric coercion. `Ssis.Extract.FixtureBuilder`'s new
`numeric-coercion` mode (`BuildNumericCoercionFixture`) builds a minimal OLE DB Source (SqlCommand
returning a `float` "Val" column) -> OLE DB Destination (an `int` "Val" column) passthrough, with
NO Data Conversion and NO Derived Column in between -- reproducing the real mismatch shape in
isolation. Backing tables: `tests/Ssis.Extract.Tests/Fixtures/synthetic-numeric-coercion-tables.sql`,
seeded with values chosen specifically to distinguish rounding rules: exact integers, ordinary
non-midpoint values in both directions, FOUR midpoint values (3.5/-3.5 with an odd target, 2.5/4.5
with an even target either side), and a NULL. **Run for real via `DTExec.exe /File
SyntheticNumericCoercion.dtsx` against `.\SQLFORPOC_2022`/`SsisPoC`** (`DTSER_SUCCESS`), then read
back: `3.0->3, 3.2->3, 3.5->4, 3.7->4, -3.2->-3, -3.5->-4, -3.7->-4, 2.5->2, 4.5->4, NULL->NULL`.
This is **round-to-nearest, ties-to-EVEN** -- exactly matching .NET's own default `Math.Round(double)`
midpoint rule (2.5 and 4.5 both round toward the nearest EVEN integer, not away from zero), and
notably the SAME convention CLAUDE.md's own trap 12 already documented for the SSIS *expression
language's* own `(DT_I4)` cast on a float -- both apparently share the same underlying OLE
Automation-style numeric coercion, though that's an inference from the matching behavior, not
independently confirmed from source. An overflow-inducing value was deliberately EXCLUDED from
this same probe run: OLE DB Destination fast-loads in one transaction by default
(`FastLoadMaxInsertCommitSize=0`), so a single failing row would roll back every other row in the
same batch and contaminate the rounding-rule measurement -- overflow behavior is therefore left
UNMEASURED, stated honestly rather than guessed (see below).

**What was built:** `SsisFn.NarrowR8ToI4` (two overloads, `double`/`double?`, in
`SsisFnEmitter.cs`) -- `checked((int)Math.Round(value))`, reproducing the measured rounding rule
exactly. `checked` is a deliberate, conservative choice for the untested overflow case: it throws
`OverflowException` rather than silently wrapping to a garbage value, which is the same "never
guess a coercion that could silently corrupt data" principle as everywhere else in this tool --
worst case this generated code fails loudly somewhere real SSIS might have succeeded (or itself
failed) differently, never the reverse. `TransformEmitter.Emit`'s existing mismatch-detection loop
gained one new branch, checked BEFORE the general `mismatchedTypeColumns` gap (mirroring the
Flat File Destination round's own `isFlatFileDestination` branch immediately above it in the same
loop): a `sourceType.ClrTypeName == "double" && column.Type.ClrTypeName == "int"` pairing routes to
a new `narrowedR8ToI4Columns` set instead of gapping. **Every other numeric pairing (i8->i4,
numeric->i4, r4->i4, etc.) still gaps** -- none of them were measured, and this round deliberately
does not extrapolate the same rule to them.

**Verified two ways, same discipline as every other round:**
1. **The real motivating case** -- regenerating `SSIS_From_Sandeep`'s actual `Package_Advanced.dtsx`:
   `DFT_ExcelImport`'s own `CustomerID` gap (previously the one named, honest, non-crashing gap
   left on that flow) is GONE entirely -- confirmed by reading the emitted
   `DripEligibilityTransform.cs` directly: `CustomerID = SsisFn.NarrowR8ToI4(row.CustomerID)`. The
   whole regenerated solution (this flow plus a copy of `Etl.Core`) builds with **0 warnings/0
   errors**. Only the pre-existing, unrelated `Package_Advanced` gaps remain (a SqlCommand-mode
   OLE DB Source advisory on a different flow, and the always-present Notification gap).
2. **A new dedicated fixture**, the same `SyntheticNumericCoercion.dtsx` built for the probe above
   (kept and checked in, doing double duty as both the ground-truth probe and the Gate-3-style
   verification fixture -- no second fixture needed). **Generated, built against a copy of
   `Etl.Core` (0 warnings/0 errors), and actually RUN against `.\SQLFORPOC_2022`**: all 9
   non-midpoint-excluded rows loaded, read back afterward and diffed directly against the real
   dtexec probe's own captured values above -- **exact row-for-row match**
   (`3,3,4,4,-3,-4,-4,2,4`), the strongest evidence tier this tool's "gaps not guesses" philosophy
   can produce: the generated C# reproduces genuine SSIS runtime behavior bit-for-bit, not an
   assumption about it. The probe's own NULL row (ID 10) was excluded from this generated-code run
   -- a plain SQL source column with no `ISNULL`/Data Conversion evidence is never nullable-inferred
   regardless of this round's own fix (a separate, pre-existing, out-of-scope limitation, unrelated
   to numeric coercion), so including it would have crashed on an unrelated gap and masked this
   round's own result; SSIS's real NULL-passthrough behavior for this pairing (NULL -> NULL) was
   still captured by the earlier raw dtexec probe run, which is sufficient ground truth for it.

One test was rewritten, not just extended: the pre-existing
`Emit_ReportsAGap_ForAPlainPassthroughColumn_WhoseSourceAndDestinationClrTypesDiffer` asserted the
OLD gap-reporting behavior for exactly this r8-to-i4 pairing -- renamed to
`Emit_ResolvesAPlainPassthroughColumn_WhenSourceIsDoubleAndDestinationIsInt` and rewritten to
assert the new `SsisFn.NarrowR8ToI4` call instead. A new test,
`Emit_ReportsAGap_ForAPlainPassthroughColumn_WithAnUnevidencedNumericMismatch` (i8->i4), preserves
coverage that an unevidenced pairing still gaps rather than guessing the same rule applies.
Regression: 129/129 in `Ssis.Extract.Codegen.Tests` (was 128 before this round's net +1 from
splitting that one test into two), same 3 pre-existing unrelated failures elsewhere.

**Remaining known gaps, updated once more:** Aggregate/Row Count (only evidenced inside a flow
already blocked by Lookup), Excel Source in SqlCommand mode, a ForEach Loop re-running a whole
Data Flow Task per file, and the numeric-passthrough-coercion gap for every OTHER numeric pairing
(i8->i4, numeric->i4, r4->i4, etc. -- none measured, all still gap). The one evidenced r8-to-i4 OLE
DB/ADO NET pairing, named above, is now closed. Overflow behavior for r8-to-i4 itself remains
unmeasured against real SSIS (the generated code throws `OverflowException`, a deliberate
conservative choice, not a confirmed match to real SSIS behavior). Data Conversion target types
other than DT_I4/DT_DBDATE, previously listed here, are now closed for DT_R8/DT_WSTR/DT_BOOL --
see the next section.

### Data Conversion — DT_R8/DT_WSTR/DT_BOOL, built speculatively (2026-08-28)

Picked next after numeric-passthrough-coercion, by explicit user choice. **Before writing any
code, checked how much real evidence actually exists for this round** — across the ENTIRE
`SSIS_From_Sandeep` portfolio (all 5 packages, including every `.bak_*` backup), every single
`Microsoft.DataConvert` instance (`DCONV_Types`, `DCONV_CustId_L`, `DCONV_CustId_R`,
`DCONV_MergeId`) targets only `i4`/`dbdate`, both already supported since the original Data
Conversion round. There is currently **no real package anywhere in this tool's own portfolio**
that needs any other target type. Surfaced this to the user directly (`AskUserQuestion`) rather
than silently building ahead of it or silently skipping it — they chose to proceed speculatively
anyway, an explicit, informed deviation from this session's usual "don't build without a real
evidenced need" discipline (the same discipline that closed `AccessMode`'s own resolver as a
non-feature back in the original `ssisx generate` plan).

**Real dtexec measurement still came first, even without a real caller** — the same "ask the
runtime, don't guess" rule applies regardless of whether the caller is real or speculative. New
`Ssis.Extract.FixtureBuilder` mode `data-conversion-types` builds an OLE DB Source (SqlCommand: 3
raw string columns) -> Data Conversion (R8Text -> DT_R8, WstrText -> DT_WSTR,10, BoolText ->
DT_BOOL, all dispositions IgnoreFailure, mirroring the real evidenced `DCONV_Types` shape exactly)
-> OLE DB Destination. Backing tables:
`tests/Ssis.Extract.Tests/Fixtures/synthetic-data-conversion-types-tables.sql`, 6 rows covering
ordinary valid values, whitespace-padded values, invalid text, empty strings, NULL, and — for
DT_WSTR specifically — a value exactly at the target width and one 5 characters OVER it. **Run for
real via `DTExec.exe /File` against `.\SQLFORPOC_2022`** (`DTSER_SUCCESS`), then read back:

- **DT_R8** follows the exact same "NULL on any failure" convention as DT_I4/DT_DBDATE — `"abc"`
  and `""` both yield NULL, `" 2.5 "` parses to `2.5` (locale-neutral trim, same as I4/DATE), and
  `"1e3"` parses to `1000.0` (scientific notation accepted).
- **DT_BOOL** also yields NULL on failure (`"yes"` -> NULL), but accepts a WIDER input set than a
  bare `bool.TryParse` would: `"True"`/`"False"` (case-insensitive-by-construction) AND the
  numeric strings `"1"`/`"0"` (`"1"` -> true, `"0"` -> false) — neither of which `bool.TryParse`
  recognizes on its own.
- **DT_WSTR is a genuinely different failure mode, the first this tool has measured that ISN'T
  "NULL on failure"**: `"HelloWorldExtra"` (15 chars) against a 10-char target TRUNCATES to
  `"HelloWorld"` (the first 10 characters) rather than nulling out — there's no real numeric/date/
  bool-style "failure" for a string-to-string conversion, so `TruncationRowDisposition=
  IgnoreFailure`'s only real job is truncation. An empty source string passes through as an EMPTY
  string, not NULL (unlike every numeric/date/bool target measured so far) — confirmed via
  `DATALENGTH(...)/2`, not T-SQL's own `LEN()`, which silently trims trailing spaces and would
  have under-reported the width-exactly-fits case (10 chars, including 2 trailing spaces) as 8.

**What was built:** three new `SsisFn` helpers (`ToNullableR8(string?)`, `ToNullableBool(string?)`
— `bool.TryParse` first, then falls back to `"0"`/`"1"`, matching the measured wider acceptance
set — and `ToWstr(string?, int maxLength)`, truncating via `value[..maxLength]` only when
`value.Length > maxLength`, never nulling out). `TransformEmitter.WrapDataConversion`'s switch
gained `"r8"`, `"bool"`, and `"wstr"` cases — the WSTR case reads `conversion.Length` (already a
generic field on `DataConversionColumnSpec`, no model change needed) for the target width, and
gaps (rather than guessing a default) if no length was declared, which real SSIS never omits for
a DT_WSTR conversion. `MapPipelineTypeToSsisType`/`CollectSsisFunctions` already had entries for
`r8`/`bool`/`wstr` from earlier rounds (the expression-language type map and the general
`SsisFn.*`-detection switch respectively) — no changes needed there.

**Verified two ways, adapted for a speculative round with no real motivating package:**
1. **No regression on the one real package that DOES use Data Conversion** — regenerating
   `SSIS_From_Sandeep`'s actual `Package_Transforms.dtsx` produces the exact same 2 gaps as
   before (`DFT_LookupAndAggregate`'s own pre-existing Aggregate gap, plus Notification), and its
   whole regenerated solution still builds with **0 warnings/0 errors** — confirming the new
   switch cases are additive and don't disturb the existing i4/dbdate paths.
2. **A new dedicated fixture**, the same `SyntheticDataConversionTypes.dtsx` built for the probe
   above (kept and checked in, same double-duty pattern as the numeric-coercion round).
   **Generated, built against a copy of `Etl.Core` (0 warnings/0 errors), and actually RUN
   against `.\SQLFORPOC_2022`**: all 6 rows loaded, read back afterward and diffed directly
   against the real dtexec probe's own captured values — **exact row-for-row match** across all
   three columns, including the DT_WSTR truncation case (`"HelloWorldExtra"` -> `"HelloWorld"`,
   confirmed 10 characters via `DATALENGTH`) and the empty-string-stays-empty case.

Two new tests: `SsisFnEmitterTests.Emit_ProducesTheThreeSpeculativeDataConversionTargets_R8BoolWstr`
and `PackageGeneratorTests.Generate_TranslatesTheThreeSpeculativeDataConversionTargets_R8BoolWstr`
(the latter reading the real fixture end to end, same pattern as the original Data Conversion
round's own `Generate_TranslatesADataConversion_...` test). Regression: 131/131 in
`Ssis.Extract.Codegen.Tests` (was 129), same 3 pre-existing unrelated failures elsewhere.

**Remaining known gaps, updated once more:** Aggregate/Row Count (only evidenced inside a flow
already blocked by Lookup), Excel Source in SqlCommand mode, a ForEach Loop re-running a whole
Data Flow Task per file, and the numeric-passthrough-coercion gap for every numeric pairing other
than r8-to-i4. Data Conversion target types other than DT_I4/DT_DBDATE/DT_R8/DT_WSTR/DT_BOOL
remain unsupported (e.g. DT_I2/DT_I8/DT_NUMERIC/DT_DBTIMESTAMP) — none evidenced or measured yet.

### Portfolio re-survey + string-to-int passthrough coercion (2026-08-30) — closes `Package_Exports`

Re-ran `ssisx report`/`ssisx generate` against the full `SSIS_From_Sandeep` portfolio from a
clean rebuild, specifically to get a fresh generatable-count now that nearly every prior gap was
closed — the user picked this over jumping straight to another named gap. Result: **2/5 →
confirmed-still-2/5 at survey time**, but with the exact blocker set changed since the last
count: `Package_Advanced`/`Package_Legacy` are now BOTH fully generatable (closed by this
session's earlier rounds, not previously re-verified together), `Package` (the orchestrator) is
still blocked on its 2 `Microsoft.ScriptTask`s (unchanged, out of scope), and `Package_Transforms`
is still blocked by `DFT_LookupAndAggregate` — confirmed, by reading the real XML directly, that
this flow has BOTH a `Microsoft.Lookup` and a `Microsoft.Aggregate` downstream of its Multicast,
so building Aggregate/Row Count support would not close it anyway (the Lookup gate, hoisted to
the top of `PackageGenerator`'s per-flow loop, still wins regardless of what Aggregate support
would add — verified this is NOT an ordering bug: `PackagePlanner.PlanMulticast` fails first here
because Aggregate is unsupported, short-circuiting before the `DataFlowPlan.Lookup` field is ever
populated, but if Aggregate becomes supported the constructed `DataFlowPlan` would still carry
`Lookup: lookup` and `PackageGenerator`'s existing Lookup-first check would still catch it before
ever reaching `flow.Multicast` — the safety net holds for a future round too, just wasn't the
gap-message the report showed today). Confirms the pre-existing "remaining known gaps" list was
accurate on Aggregate/Row Count being moot.

**One genuinely NEW, real, evidenced gap surfaced, not previously named**:
`Package_Exports`/`DFT_AdoNetRoundTrip`'s `CustomerID` — an ADO NET Source reads it as `wstr,20`
(the real `dbo.StagingCustomers.CustomerID` column is text-typed in the third party's own staging
table), but `CustomerExportLog.CustomerID` is `i4`. A DIFFERENT case from the r8-to-i4 rounding
fix (2026-08-28 above) — this needs PARSING text, not just rounding a float — and newly reachable
only because the OLE DB/ADO NET direct-copy gate was lifted in the Excel Source round (this flow
has no Derived Column at all). Picked as the next round over the other three named gaps
(Excel SqlCommand mode, ForEach-over-a-Data-Flow-Task, other numeric/Data-Conversion targets) —
real and evidenced beats speculative every time, per this project's own discipline.

**Real dtexec probe measured first, same discipline as every other coercion this project has
built** (`Ssis.Extract.FixtureBuilder`'s new `string-to-int-coercion` mode, OLE DB Source/
Destination standing in for the real ADO NET components — this environment still cannot
construct an ADO.NET connection manager via the object model, see the "ADO NET Source/
Destination" section above; the coercion itself is source-agnostic, keyed only off buffered type
`wstr` vs. destination `i4`, same as the r8-to-i4 round). Backing tables:
`tests/Ssis.Extract.Tests/Fixtures/synthetic-string-to-int-coercion-tables.sql`. **Two real
findings, neither guessed:**
- The **success** path: `"123"`, `"  456  "` (whitespace-padded), `"-42"`, `"007"`, `"+15"`,
  `"0"`, and a literal `NULL` all landed correctly (`123, 456, -42, 7, 15, 0, NULL`) — locale-
  neutral, whitespace/sign-tolerant. Decimal-looking (`"12.5"`) and scientific-notation
  (`"1e3"`) strings ALSO succeed (`12`, `1000`) — the string is parsed as a NUMBER first, then
  rounded. Midpoint probes (`"12.5"→12`, `"13.5"→14`, `"-13.5"→-14`) confirm the **exact same
  round-to-nearest-ties-to-even rule as `NarrowR8ToI4`**.
- The **failure** path, measured separately (one pathological row at a time, truncating between
  runs — a single failing row would otherwise roll back the whole fast-load batch and
  contaminate the success-path measurement, same reasoning as the r8-to-i4 probe's own overflow
  exclusion): non-numeric text (`"abc"`), an empty string, a whitespace-only string, and an
  out-of-range value (`"99999999999999"`) all made the real probe **FAIL HARD** —
  `DTSER_FAILURE`, zero rows landed, real OLE DB error *"Invalid character value for cast
  specification"*. This is a genuinely different failure mode from `NarrowR8ToI4` (which never
  fails — a float is already numeric) and from every Data Conversion target (`IgnoreFailure`
  always nulls out, never fails hard) — the destination input column's disposition here defaults
  to `FailComponent`, and this pairing has no Data Conversion component at all to configure
  `IgnoreFailure` on in the first place.

**Built `SsisFn.ParseWstrToI4(string?)`** (`checked((int)Math.Round(double.Parse(value,
NumberStyles.Float, CultureInfo.InvariantCulture)))`, `null` passes through as `null` unchanged)
— throws `FormatException`/`OverflowException` on the measured failure inputs, deliberately NOT
swallowed, to match the real hard-failure behavior instead of guessing a null-out convention that
was measured NOT to apply here. `TransformEmitter`'s mismatch-detection loop gained a
`sourceType == "string" && destType == "int"` branch alongside the existing r8→i4 one.

**A real design wrinkle, absent from the r8→i4 case, that needed a genuine fix, not just a new
branch:** `string`/`string?` are the SAME runtime type in .NET (unlike `double`/`double?`), so
there is no non-nullable overload of `ParseWstrToI4` the way `NarrowR8ToI4(double)` vs.
`NarrowR8ToI4(double?)` lets the SOURCE column's own declared nullability pick the right one —
`ParseWstrToI4` always returns `int?`. That means the destination entity property MUST be
nullable-inferred structurally (the same "nullable BY CONSTRUCTION" reasoning Data Conversion's
own converted columns already use), and — because `PackageGenerator` calls `EntityEmitter` BEFORE
`TransformEmitter` — that inference has to happen up front in `ResolveNullableColumnNames`, not
inside `TransformEmitter.Emit`'s own later mismatch loop (too late; `EntityEmitter` would already
have emitted a non-nullable `int` property). Extracted the detection into a new, shared
`TransformEmitter.DetectParsedWstrToI4Columns(destinationComponent)` (internal, same sharing
precedent as `CollectSsisFunctions`/`MapPipelineTypeToSsisType`), called from BOTH
`ResolveNullableColumnNames` (seeds the nullable set before `EntityEmitter` runs) and
`TransformEmitter.Emit` itself (reuses the same answer instead of re-deriving it, so the two can
never drift apart). This also correctly makes the RAW source row property nullable too (same
name, no rename on a plain passthrough), which is actually MORE correct than before — the real
data can genuinely contain NULL (row ID=7 in the probe), so `SqlRowReaderEmitter`'s `IsDBNull`
guard is now warranted here for a reason unrelated to any Data Conversion.

**Verified two ways, same discipline as every other round:**
1. **A new dedicated fixture**, `tests/Ssis.Extract.Tests/Fixtures/SyntheticStringToIntCoercion.dtsx`
   (`Ssis.Extract.FixtureBuilder`'s new `string-to-int-coercion` mode — the same probe fixture,
   doing double duty). **Generated, built against a copy of `Etl.Core` (0 warnings/0 errors), and
   actually RUN against `.\SQLFORPOC_2022`**: the 7-row success batch produced an **exact
   row-for-row match** against the real dtexec probe (`123, 456, -42, 7, 0, 15, NULL`).
   Re-running the pathological cases through the GENERATED exe (not just dtexec) confirmed the
   same class of outcome both ways: `"abc"` → `FormatException`, transaction rolled back, 0 rows
   landed (dtexec: `DTSER_FAILURE`, 0 rows); `"99999999999999"` → `OverflowException`, 0 rows
   landed (dtexec: `DTSER_FAILURE`, 0 rows); `"12.5"` → succeeded, landed `12` (dtexec: same).
   The exact exception type necessarily differs from SSIS's own OLE DB error, but the essential
   property — fail hard, land nothing, never silently succeed with garbage — matches exactly.
2. **The real motivating case** — regenerating `SSIS_From_Sandeep`'s actual `Package_Exports.dtsx`:
   the `CustomerExportLog.CustomerID` gap is GONE entirely; `Package_Exports` moved from 1
   blocking gap to fully generatable, taking the portfolio from **2/5 to 3/5**. `Package_Exports`
   alone builds with **0 warnings/0 errors** against a copy of `Etl.Core`. (A full RUN against
   this real package was not attempted — its own source data lives in the third party's own
   `SSISDemo` database, unavailable here, same honest limitation as every other real-package
   check in this file that can't be executed end to end.)

**A genuinely new, unrelated, PRE-EXISTING bug was found while building the whole regenerated
solution, not caused by this round — found, named, and deliberately NOT fixed here (out of
scope; confirmed by checking today's OWN "before" generation output, saved before this round's
code change, and finding the identical failure already there).** The `Package` project (the
orchestrator, already blocked on its 2 Script Tasks, never previously claimed to build) fails
with 6 `CS1061` errors: `Mapping/StagingCustomersTransform.cs`/`SalesSummaryTransform.cs`
reference row-type properties (`FullName`, `CleanEmail`, `IsValidRow`, `LoadDateTime`,
`CustomerTier`, `ProcessedDateTime`) that don't exist on their own generated
`StagingCustomersCsvRow`/`SalesSummarySqlRow` types. Not root-caused further this round — named
here as a real, precise follow-up rather than silently left for someone to rediscover from
scratch. Since `Package` is already excluded from the "generatable" count for an unrelated
reason (Script Task), this doesn't affect today's 3/5 result, but it does mean `Package`'s
OTHER, individually-gap-free flows have a real, previously-unnoticed generation bug of their own.

Two new `TransformEmitterTests` (the resolved-pairing case, mirroring the r8→i4 test exactly;
`DetectParsedWstrToI4Columns` in isolation), one new `SsisFnEmitterTests`
(`Emit_ProducesParseWstrToI4_...`), and one new `PackageGeneratorTests`
(`Generate_ResolvesAStringSourceIntDestinationPassthroughColumn_ViaParseWstrToI4`, reading the
real fixture end to end and asserting the nullable entity property, the `IsDBNull`-guarded
reader, and the `ParseWstrToI4` call all compose correctly). Regression: 135/135 in
`Ssis.Extract.Codegen.Tests` (was 131), same 3 pre-existing unrelated failures elsewhere. No
`Etl.Core` changes needed this round (same as the r8-to-i4 round — the coercion lives entirely
in per-package generated code, not shared plumbing), so `D:\PoC\SSIS_Rewrite` has nothing to
commit for this round.

**Remaining known gaps, updated:** Aggregate/Row Count (still moot — confirmed again this round,
now with the exact reason why building it wouldn't help), Excel Source in SqlCommand mode, a
ForEach Loop re-running a whole Data Flow Task per file, the numeric-passthrough-coercion gap for
every pairing other than r8-to-i4/wstr-to-i4 (i8→i4, numeric→i4, r4→i4, etc. — still unmeasured),
Data Conversion target types other than DT_I4/DT_DBDATE/DT_R8/DT_WSTR/DT_BOOL, and the newly-found
`Package` CS1061 row-type/transform mismatch bug named above.

### Script Component silent passthrough — a real, previously-invisible bug, fixed (2026-08-30)

Root-caused, same session, immediately after the string-to-int round: the `Package` CS1061 bug
named above (found but deliberately not fixed at the time) turned out to be a real, general
`TransformEmitter` gap, not something specific to that one flow. The user picked "investigate
it" over the other three named speculative gaps when asked.

**Root cause, confirmed from the real XML, not guessed:** `TransformEmitter`'s own passthrough
fallback assumes ANY destination column not matched by `computedByName` (Derived Column) or
`convertedByName` (Data Conversion) is a genuine, unchanged passthrough with a real `row.X`
property — true for every flow this tool had been tested against, because in all of them the
only components capable of adding a NEW column were exactly those two, already handled
explicitly. RBC_Demo_ETL's own `Package.dtsx`/`DFT_LoadCustomers` breaks that assumption: a
**Script Component** (`SCR_CleanseCustomerRow`) synthesizes four brand-new columns (`FullName`,
`CleanEmail`, `IsValidRow`, `LoadDateTime`) that exist nowhere on the raw CSV row type — `ssisx
generate`'s own report showed **zero gaps** for this flow; the CS1061s were only ever visible by
actually building the generated project, exactly the "report says success, code doesn't compile"
failure class this tool has hit before (`DFT_SortAndMergeJoin`'s own silent multi-source bug,
`portfolio-digest.json`'s tuple-serialization bug).

**Why lineage-tracing back to "the true original producer" is the WRONG fix, confirmed
empirically before writing any code:** the obvious-looking approach — walk each destination
column's lineage backward and check whether its ultimate origin is a real source — breaks
already-working flows. Read `Package_Transforms.dtsx`'s own `MRG_SortedUnion` directly:
`Microsoft.Merge`'s (and, by the same shape, `Microsoft.Sort`'s) own OUTPUT columns get a
**FRESH lineageId**, distinct from their input's (confirmed via the `OutputColumnLineageID`
custom property on the Merge's own input columns, pointing FORWARD to a *new* output lineageId)
— so a column passing straight through Sort/Merge/Union All would have its "true producer"
resolve to Sort/Merge ITSELF, not the flow's real source. Tracing lineage would have wrongly
flagged every already-verified Sort/Merge/Union-All remerge flow (2026-08-28's own rounds) as
broken.

**The actual fix: an ALLOWLIST of recognized "safe to guess" component types, not a lineage
walk.** A new `TransformEmitter.RecognizedPassthroughComponentClassIds` (Flat File/Excel/SQL
sources, `Sort`, `Merge`, `UnionAll`, `RowCount`, `ConditionalSplit`, `Multicast`, `MergeJoin`,
`OLEDBCommand`, both destination types) plus `IsRecognizedPassthroughComponent`. Any component in
the flow's own pipeline NOT on this list — Script Component being the concrete evidenced case,
but this also generically covers Aggregate, Lookup, Row Count if ever removed from the list, and
any future unmodeled type — has its own output columns collected into a
`unrecognizedComponentColumns` map; a destination column matching one of those names is now a
named `GenerationGap` ("column 'X' is produced by 'Y' (ComponentClassId), a component type this
tool does not translate... if this is a Script Component, its transform logic must be ported by
hand, the same limitation as a Script Task") instead of a guessed `row.X`. `Microsoft.RowCount`
was deliberately included even though nothing in the currently-tracked portfolio exercises it
outside an already-Lookup-blocked flow — it structurally never adds/changes a column (its only
job is writing a count into a variable), so recognizing it costs nothing and avoids a future
false positive.

**A real false positive was caught testing this against the WHOLE real portfolio, not just the
motivating flow — fixed before shipping, not after.** `Package_Transforms.dtsx`'s own
`DFT_SortAndMergeJoin` (the 2026-08-28 Merge Join round's own closed flow) has a Data Conversion
on one side (`DCONV_CustId_L`). `GenerateMergeJoinFlow` calls `TransformEmitter.Emit` with an
EMPTY `DataConversions` list (`MergeJoinEmitter` resolves every one of its own combined row
type's columns itself — including through a Data Conversion, via its own `SourceColumnLineageId`
walk — independently of `TransformEmitter`'s usual `convertedByName` threading), so the new gate
had no way to know `CustomerID_i4` was already safely resolved and wrongly gapped it. Fixed with
a new `TransformRequest.TrustAllPassthroughColumns` flag (default `false`), set `true` only in
`GenerateMergeJoinFlow`'s own call — the only caller whose row type is built by a DIFFERENT
emitter with its own independent resolution, not the flow's raw source buffer.

**Verified two ways, same discipline as every other round:**
1. **The real motivating case** — regenerating RBC_Demo_ETL's actual `Package.dtsx`: the 4
   Script-Component-produced columns now correctly report as gaps (`StagingCustomers.FullName`/
   `.CleanEmail`/`.IsValidRow`/`.LoadDateTime`, and the same shape on `SalesSummary.CustomerTier`/
   `.ProcessedDateTime` from a second Script Component, `SCR_ComputeCustomerTier` — a SECOND real
   instance of the exact same bug, closed by the same one generic fix). Confirmed the flow still
   WIRES into `Program.cs` with the 4 columns simply omitted (same "per-column gap, rest still
   generates" shape an untranslatable Derived Column expression already uses) — not dropped
   entirely. **The whole regenerated `Package` project now builds with 0 warnings/0 errors**
   (previously 6 `CS1061`s). Re-ran the WHOLE portfolio afterward to confirm the Merge Join fix:
   `Package_Transforms` is back to its correct 2 gaps (`DFT_LookupAndAggregate`, Notification),
   and the entire regenerated solution (all 5 packages plus a copy of `Etl.Core`) builds with
   **0 warnings/0 errors** — genuinely the first time `Package` itself has ever built clean, even
   though it's still excluded from the "generatable" count for its own, unrelated, unchanged
   reason (2 `Microsoft.ScriptTask`s).
2. **Regenerated the extractor's own full `tests/Ssis.Extract.Tests/Fixtures/` directory (24
   packages) and this PoC's own real `SSIS/` folder** and grepped every gap reason for `"produced
   by"` — zero matches in either, confirming the new gate introduces no false positive anywhere
   in the existing tracked corpus, not just the two flows it was built against.
3. Two new `TransformEmitterTests` (`Emit_ReportsAGap_ForAColumnProducedByAnUnrecognizedComponent_...`,
   asserting the exact gap message and that the OTHER column in the same flow still generates;
   `Emit_TrustsEveryPassthroughColumn_WhenTrustAllPassthroughColumnsIsSet`, proving the opt-out
   suppresses that same gap). No dedicated dtexec-runnable fixture was built this round — unlike
   a numeric-coercion round, this fix has no new SSIS *runtime* semantics to measure; it's a
   detection/reporting correctness fix, verified by the real package's own build outcome plus
   unit tests, the same evidence tier the earlier `DFT_SortAndMergeJoin` silent-bug fix used.
   Regression: 137/137 in `Ssis.Extract.Codegen.Tests` (was 135), same 3 pre-existing unrelated
   failures elsewhere. No `Etl.Core` changes this round.

**Remaining known gaps, unchanged except for this round's own closure:** Aggregate/Row Count
(still moot), Excel Source in SqlCommand mode, a ForEach Loop re-running a whole Data Flow Task
per file, the numeric-passthrough-coercion gap for every pairing other than r8-to-i4/wstr-to-i4,
and Data Conversion target types other than DT_I4/DT_DBDATE/DT_R8/DT_WSTR/DT_BOOL. The `Package`
CS1061 bug, named above, is now closed — and Script Component (and any other genuinely
unmodeled pipeline component type) is now a safe, named gap everywhere in this tool, not just in
the one flow that happened to surface it.

### Excel Source SqlCommand mode, built speculatively (2026-08-30)

A re-survey the same session confirmed the portfolio held steady at 3/5 with no new real gaps,
so the next round had no free evidenced win the way the string-to-int round did. Before starting
this one, the initial framing ("Excel SqlCommand mode is real, not speculative") was checked
against the actual XML and found WRONG: the one real Excel Source in the whole tracked portfolio
(`Package_Advanced`'s own `EXCEL_SRC_Drip`) is `AccessMode=0`/OpenRowset with an empty
`SqlCommand` — there is genuinely **zero** real evidence for SqlCommand mode anywhere. Surfaced
this correction to the user directly (`AskUserQuestion`, per the established
`ssis-poc-speculative-work-signoff` discipline) rather than quietly building ahead of demand;
they chose to proceed speculatively anyway.

**What's actually possible, confirmed via a raw probe before any codegen work**: this
generator's own `Etl.Core.Excel.ExcelRowSource` (built for the OpenRowset-mode round) is an
`ExcelDataReader`-backed grid reader with **no query engine at all** — it can only select a
worksheet by name. So a real Excel Source SqlCommand can only ever be translated when it's
semantically equivalent to naming a worksheet. Confirmed empirically, not assumed: a raw
`System.Data.OleDb.OleDbConnection` probe against the real checked-in workbook
(`synthetic-excel-source.xlsx`) ran `SELECT * FROM [DripEligibility$]` directly through the real
ACE OLEDB provider and got back the identical 9 rows/3 columns as the OpenRowset-mode fixture —
proof that a **bare** `SELECT * FROM [SheetName$]` really is nothing more than the worksheet
reference wrapped in SQL syntax, for this provider, in this environment.

**Built:** a new `PackageGenerator.BareSelectStarFromSheetPattern` regex
(`^SELECT\s+\*\s+FROM\s+\[?([^\[\]\s]+\$)\]?\s*;?\s*$`, case-insensitive) recognizing ONLY that
exact shape. `BuildExcelFlowSource` now branches on `AccessMode`: `0`/`null` resolves
`OpenRowset` exactly as before (unchanged, zero regression risk for the one real portfolio
instance); `2` requires the `SqlCommand` text to match the pattern, extracting the captured
`SheetName$` and feeding it through the IDENTICAL downstream code as OpenRowset mode (same
`ExcelFlowSource`, same `ExcelRowSource<TRow>` at runtime — there is no second implementation to
drift out of sync with); anything else (a WHERE clause, column list, JOIN, range address, or any
other `AccessMode`) is a named gap stating plainly that this generator's reader has no query
engine, never a guess at what a more complex query would do.

**A real SSIS object-model quirk was hit and documented rather than papered over, matching this
project's own precedent (the ADO NET connection-manager and MergeJoin-extra-columns quirks
elsewhere in this file) for something confirmed-real-but-left-unsolved.** Building a genuine
`.dtsx` fixture via the SSIS object model (`Ssis.Extract.FixtureBuilder`, the usual technique) —
an Excel Source with `AccessMode=2`/`SqlCommand="SELECT * FROM [DripEligibility$]"` wired
straight to an OLE DB Destination — consistently failed with `COMException 0xC0202040` when the
**destination's own** `ReinitializeMetaData()` ran, never the source's. Isolated with a
dedicated, throwaway object-model probe (outside the normal fixture-builder, same "ask the
runtime" discipline as everywhere else): the Excel Source's OWN metadata resolves perfectly in
isolation (confirmed identical output columns/types to OpenRowset mode); the failure appears only
once a downstream OLE DB Destination tries to resolve ITS OWN input metadata against that
upstream. Re-acquiring the source's connection before the destination's own resolution call did
not help. Left unresolved after real, genuine attempts — not guessed around.

**Verification therefore used a different, still-rigorous tier, the same one the MergeJoin
round's own unresolved dtexec-fixture quirk fell back on:**
1. **A hand-derived fixture, not a fresh object-model build.** `SyntheticExcelSourceSqlCommand.dtsx`
   is a byte-preserving edit (trap 18's own discipline: read all bytes, regex-replace only the
   Excel Source's own three properties — `OpenRowset` cleared, `SqlCommand` set to `SELECT * FROM
   [DripEligibility$]`, `AccessMode` 0→2 — scoped to the text BEFORE the destination component's
   own `componentClassID` marker so the destination is untouched) of the already-proven
   `SyntheticExcelSource.dtsx`. `ssisx` parses raw XML directly (plan §2.1) and never invokes the
   SSIS object model at all, so this fixture is exactly as valid an input to `ssisx generate` as
   one built through SSDT would be — it just could never be dtexec-executed, which is not
   something this generator's own test tier needs.
2. **Generated, built against a copy of `Etl.Core` (0 warnings/0 errors), and actually RUN
   against `.\SQLFORPOC_2022` and the real checked-in workbook**: 9 rows landed, read back
   afterward — **byte-for-byte identical** to the OpenRowset-mode fixture's own already-verified
   9 rows (same `CustomerID`/`DripEligible`/`ReviewedBy` values). The strongest evidence tier this
   project's philosophy asks for, just reached via a hand-derived `.dtsx` instead of a fresh
   object-model one.
3. **The gap path verified the same way**: a second fixture,
   `SyntheticExcelSourceSqlCommandUnsupported.dtsx` (same byte-preserving technique, SqlCommand
   changed to add `WHERE CustomerID > 5`), correctly produces the named gap and generates no
   `Program.cs` at all — confirming the pattern match is neither too loose nor silently guessing
   past an unsupported shape.
4. **Regenerated the real portfolio afterward**: `Package_Advanced`'s own real, OpenRowset-mode
   `EXCEL_SRC_Drip` is completely unaffected — same file, same gap count, still fully
   generatable. Two new `PackageGeneratorTests`
   (`Generate_WiresAnExcelSourceInSqlCommandMode_...`,
   `Generate_ReportsAGap_ForAnExcelSourceSqlCommand_ThatIsNotABareSelectStarFromSheet`).
   Regression: 139/139 in `Ssis.Extract.Codegen.Tests` (was 137), same 3 pre-existing unrelated
   failures elsewhere. No `Etl.Core` changes this round (the runtime read path is unchanged;
   only the design-time worksheet-name extraction is new), so `D:\PoC\SSIS_Rewrite` has nothing
   to commit.

**Remaining known gaps, updated:** Aggregate/Row Count (still moot), a ForEach Loop re-running a
whole Data Flow Task per file, the numeric-passthrough-coercion gap for every pairing other than
r8-to-i4/wstr-to-i4, and Data Conversion target types other than DT_I4/DT_DBDATE/DT_R8/DT_WSTR/
DT_BOOL. Excel Source in SqlCommand mode, named above, is now closed for the one shape that can
ever be translated given `ExcelRowSource`'s own no-query-engine design (a bare worksheet
reference) — anything more expressive remains, correctly, a permanent gap rather than a future
target, since there is no way to execute it against this generator's own portable reader.

### ForEach Loop over a Data Flow Task, built speculatively (2026-08-30)

Picked from the "remaining known gaps" list by explicit user choice. **Checked the real
portfolio first, same discipline as every other round**: the only `STOCK:FOREACHLOOP` anywhere
in `SSIS_From_Sandeep` (`Package_Advanced.dtsx`'s own `FEL_SampleFiles`) has a single Execute SQL
Task body — the shape already closed 2026-08-28 — not a Data Flow Task. Surfaced this directly to
the user (`AskUserQuestion`) before writing any code, per the established
`ssis-poc-speculative-work-signoff` discipline; they chose to build it anyway.

**The scoped shape, decided up front rather than discovered mid-build:** a ForEach File
Enumerator loop whose single child is a Data Flow Task, itself a plain single-destination flow
(Flat File Source → optional Derived Column → SQL destination) whose Flat File Source's own
connection manager has its `ConnectionString` entirely driven by a `PropertyExpression`
referencing the loop's own mapped variable — the natural, most-minimal real-world pattern
(`@[User::CurrentFile]` as a bare reference, needing no folder-concatenation translation).
Lookup/Conditional Split/Merge Join/Multicast/OLE DB Command/Flat File Destination/Excel/ADO NET
inside a loop body are each a named gap, not attempted — stacking one unevidenced per-iteration
shape on top of another was ruled out deliberately, the same "don't compound speculation"
discipline every earlier speculative round in this file has followed.

**Confirmed via a real isolated object-model probe before writing any FixtureBuilder code**
(same "ask the runtime, don't guess" discipline as trap 12): a connection manager's own
`Properties["ConnectionString"]` supports `.SetExpression(cm, expr)` exactly like any ordinary
task property, producing the identical `<DTS:PropertyExpression DTS:Name="ConnectionString">`
shape `ConnectionManagerSpec.PropertyExpressions` already reads for a real package's own
expression-driven CSV connection managers (e.g. `LoadReferenceData`'s own `CM_DepartmentCsv`) —
not a guess extrapolated from task-property behavior.

**`Etl.Core` (`D:\PoC\SSIS_Rewrite`): one new type, `Pipeline/ForEachFileDataFlowStep.cs`.**
Deliberately NOT a new low-level abstraction — each iteration constructs a fresh
`IRowSource<TRow>` for that file's own path (via a caller-supplied `Func<string,IRowSource<TRow>>`,
the per-iteration "current file" value computed the identical way `ForEachLoopStep` already
does) and runs it through the SAME `transform`/`sink` every other iteration uses via a fresh
`DataFlowStep<TRow,TEntity>` instance per file — reusing that type's own streaming
Source→Transform→Sink loop and its own "zero rows read is an error" guard rather than
duplicating either. Same ordinal-sorted file enumeration and same single-shared-transaction
all-or-nothing semantics as `ForEachLoopStep`. `ForEachFileLoopAction`'s own doc comment (which
previously stated flatly "there is no per-iteration re-run capability here for anything beyond a
single SQL statement") was updated to describe both shapes now, not just the SQL one.

**`Ssis.Extract.Codegen`: the biggest share of the work, three files.**
- `PackagePlanner.cs`: new `ForEachFileDataFlowPlan` record (folder/mask/variable/raw
  ConnectionString-expression text, plus a fully-resolved `DataFlowPlan` for the loop's own inner
  flow — reusing `PlanDataFlow` verbatim, since a per-iteration Data Flow Task is structurally
  identical to an ordinary one; only its SOURCE FILE PATH varies per iteration) and a new
  `ForEachDataFlowLoopStep : PackageStep`. `PlanForEachFileLoop` was restructured to return a
  `PackageStep?` directly (either shape) instead of a single plan type, branching on whether the
  loop's one child is `inner.DataFlowTask` or `inner.ExecuteSqlTask` — a new
  `PlanForEachDataFlowLoop` helper resolves the Data Flow Task via `PlanDataFlow`, then requires
  its source be a Flat File Source whose own connection manager carries a `ConnectionString`
  PropertyExpression, gapping with the same "would read the identical file every iteration, very
  likely not intended" wording the SQL-body case already uses for a static `SqlStatementSource`.
  **A ForEach-Data-Flow-Loop's own inner flow is deliberately NOT duplicated into
  `PackagePlan.Flows`** (that list is purely a convenience projection of ordinary `FlowStep`s,
  per its own doc comment) — which surfaced a real interaction to fix, not just a new field:
  `PackageGenerator.Generate`'s own early-exit gate (`plan.Flows.Count == 0` → "nothing to
  generate") would otherwise silently swallow a package whose ONLY real content is this kind of
  loop, so it was widened to also check `plan.Steps.Any(s => s is ForEachDataFlowLoopStep)`.
- `PackageGenerator.cs`: a new `GenerateForEachDataFlowLoop`, mirroring the ordinary
  single-destination flow's own generation (EntityEmitter/TransformEmitter/
  `DbContextEmitter.TableSpec` all resolve identically) but explicitly gapping on
  Lookup/Conditional Split/Merge Join/Multicast/OLE DB Command/Flat File Destination rather than
  attempting any of them, and skipping `ResolveFlowSource`/`BuildFileSourceEntry` entirely — this
  source's file path is never a static `appsettings.json` value, so only `CsvRowEmitter`/
  `ClassMapEmitter` are called (they only depend on the connection manager's own column FORMAT,
  never its path). The `ForEachFileNameMode` raw-enum resolution was factored out of
  `ResolveForEachFileLoop` into a small shared `ResolveForEachFileNameMode` helper, used by both
  loop-body shapes now.
- `ProgramEmitter.cs`: new `ProgramForEachDataFlowLoopStep` record and wiring. **Deliberately no
  top-level `IRowSource<TRow>` DI registration for this step at all** — the whole point is a
  fresh source per file, so `EmitRowSourceRegistration` is never called for it; only its own
  `IRowTransform<TRow,TEntity>` is a DI service (registered the same way an ordinary flow's own
  transform is). The step-construction switch emits
  `new ForEachFileDataFlowStep<TRow,TEntity>("Name", new ForEachFileLoopAction(...),
  currentFile => new CsvRowSource<TRow>(...), sp.GetRequiredService<IRowTransform<...>>(),
  sp.GetRequiredService<IBulkSink<...>>(), sp.GetRequiredService<ILogger<DataFlowStep<...>>>())`
  — the file-path expression (already translated to C# by `PackageGenerator`, reusing
  `ForEachLoopEmitter.TranslateSqlTemplate` verbatim, since a connection manager's
  `ConnectionString` expression and an Execute SQL Task's `SqlStatementSource` expression are
  both just "build a string from `@[Namespace::Variable]` references") is spliced directly into
  the `CsvSourceOptions { FilePath = ... }` initializer inside that lambda.

**New fixture, `SyntheticForEachDataFlowLoop.dtsx`** (`Ssis.Extract.FixtureBuilder`'s new
`foreach-data-flow-loop` mode): a `STOCK:FOREACHLOOP` over `*.csv` files (`FileNameRetrieval`
left at its schema default, 0/FullyQualified, so `@[User::CurrentFile]` resolves to each file's
own FULL path per iteration) whose single child, `DFT_Load`, is Flat File Source → Derived
Column (`LoadedAtUtc`) → OLE DB Destination. **No companion top-level flow was added** (unlike
`SyntheticForEachFileLoop.dtsx`, which needed one purely to dodge the pre-existing early-exit
gate) — this fixture's package has NOTHING else in it, deliberately, so it directly proves this
round's own gate-widening fix rather than sidestepping the question. **Generated, built against
a copy of `Etl.Core` (0 warnings/0 errors), and actually RUN against `.\SQLFORPOC_2022` and two
real checked-in `.csv` files** (`sales-jan.csv`: Alice/Bob; `sales-feb.csv`: Carol) — read back
afterward, not trusted from the exit code: all 3 rows landed correctly across both files
(`sales-feb.csv` processed before `sales-jan.csv`, confirming the ordinal — not date — file sort
order), each with its own real `LoadedAtUtc` stamp. Backing table:
`tests/Ssis.Extract.Tests/Fixtures/synthetic-foreach-data-flow-loop-tables.sql`. A second,
byte-preserving derived fixture, `SyntheticForEachDataFlowLoopStaticPath.dtsx` (the
`PropertyExpression` stripped via a regex replace, trap 18's own discipline — never `sed`/text-
replace a `.dtsx`), proves the gap path: no per-iteration expression → an explicit, named gap,
not a silent guess.

Two new `PackagePlannerTests` (the resolved-plan shape; the static-ConnectionString gap) and one
new `PackageGeneratorTests` (asserting the exact `Program.cs` wiring — no top-level
`IRowSource<TRow>` registration, the `ForEachFileDataFlowStep<...>` construction, the spliced
`CsvSourceOptions { FilePath = currentFile }`). Regression: 142/142 in
`Ssis.Extract.Codegen.Tests` (was 139), same 3 pre-existing unrelated failures elsewhere
(someone else's in-progress SSDT edits to `SSIS/LoadReferenceData.dtsx`/`SSIS/SSIS.dtproj`, not
this session's or the extractor's).

**Remaining known gaps, updated:** Aggregate/Row Count (still moot), the numeric-passthrough-
coercion gap for every pairing other than r8-to-i4/wstr-to-i4, and Data Conversion target types
other than DT_I4/DT_DBDATE/DT_R8/DT_WSTR/DT_BOOL. A ForEach Loop re-running a Data Flow Task per
file, named above, is now closed for the one scoped shape (a plain single-destination SQL flow
sourced from an expression-driven Flat File Source) — every other loop-body combination remains
a named gap, not a future target, since combining any of them with this already-speculative
feature would stack one unevidenced shape on top of another.

### Aggregate + Row Count, built anyway despite confirmed zero payoff (2026-08-30)

Picked from the "remaining known gaps" list by explicit user choice, with the user told directly
first that Aggregate/Row Count support closes nothing real: `DFT_LookupAndAggregate` (the one
real evidenced `Microsoft.Aggregate` instance, RBC_Demo_ETL's own `AGG_ByRegion`) sits downstream
of a `Microsoft.Lookup` that already blocks the whole flow regardless — confirmed twice by
re-surveying the portfolio. The user chose to build it anyway, for completeness.

**Row Count needed nothing at all** — `Microsoft.RowCount` was already in
`TransformEmitter.RecognizedPassthroughComponentClassIds` (added during the Script Component
silent-passthrough fix, since RowCount structurally never adds/changes a column — its only real
job is writing a count into an SSIS variable, a side effect this generator has no consumer for
regardless). So this round is entirely Aggregate.

**A real, previously-unnoticed environment issue was hit and root-caused before any real work
started**: an ad hoc PowerShell probe script (`LoadWithPartialName` for the three usual GAC
assemblies) failed `ProvideComponentProperties()` on `Microsoft.Aggregate` with "Element not
found" — NOT because Aggregate itself has some special requirement, but because that script's
own assembly resolution differs from this project's own `Build/Ssis.Gac.props`-driven references.
The identical construction sequence worked cleanly once run through a temporary probe mode added
to `Ssis.Extract.FixtureBuilder` itself (which already references the GAC correctly) — a reminder
that this project's own established pattern (probe through a properly-referenced project, not a
bare ad hoc script) exists for a real reason, not just style.

**Two real object-model facts nailed down via that properly-referenced probe, neither guessed:**
`AggregationType`/`KeyScale` are native COM type converters with NO managed CLR enum backing them
anywhere (confirmed by reflecting every loaded assembly and finding nothing named either) — unlike
some other raw-enum properties in this codebase, there is no public Microsoft SDK reference for
this one either. And an Aggregate output column's own data type is derived AUTOMATICALLY from
whichever input column `AggregationColumnId` references — calling `SetOutputColumnDataTypeProperties`
on a freshly-inserted output column throws `COMException 0xC020401A`; setting only
`AggregationColumnId`/`AggregationType` and reading the column back afterward showed Region
resolving to `DT_WSTR`/50 (matching its source column) and CustomerCount resolving to `DT_UI8`/0
on its own (matching the real evidenced XML's own `ui8` Count-column type) — this is also why
`AggregateRowEmitter` (new, see below) resolves its row type straight off the Aggregate's own raw
output columns rather than through `PipelineResolver`, the same reason `MergeJoinEmitter` already
established (a pure pipeline transform has no external metadata columns of its own to join
against).

**A real dtexec probe measured `AggregationType=1`'s (Count) own NULL-handling rule, not
guessed**: seed data (`synthetic-aggregate-tables.sql`) deliberately includes one row with a NULL
in the counted column. Real result: the group with the NULL row landed `CustomerCount=2`, not 3 —
confirming AggregationType=1 excludes NULLs, matching ordinary SQL `COUNT(column)` semantics,
never `COUNT(*)`. `SsisPipelineTypeMap` gained a new `"ui8"` entry (`DT_UI8` → `long`, not the
more literally-accurate `ulong` — EF Core's SQL Server provider has no native mapping for any
unsigned integer wider than byte, so a `ulong` property would fail model creation; `long`/`bigint`
is what a real destination column for this value is in practice anyway).

**Extraction: new `AggregatePayload`/`AggregateColumnSpec`**, wired into `PipelineReader.cs`
exactly like every other bespoke payload (a column's own `AggregationColumnId` custom property is
a `#{lineageId}` reference, stripped the same way `DataConvertPayload`'s own
`SourceInputColumnLineageID` already is). `LineageBuilder` gained a new `"Aggregate"` edge kind,
mirroring its own `"DataConversion"` edge exactly — resolving each output column (GroupBy key or
Count) back to its TRUE source column by name, the same lineage-based resolution every other
bespoke payload in this tool already relies on. Golden files needed the usual mechanical update
(a new `"Aggregate": null,` field on every `PipelineComponentSpec`) — patched byte-preservingly
(trap 18's own discipline), and this round's own byte-level surprise worth recording: this
project's JSON writer serializes properties in ALPHABETICAL order, not C# declaration order (an
initial golden-file edit inserted the new field in declaration-order position and the test still
failed — the true anchor turned out to be `"AdoNetSource": null,`, immediately before
`"Aggregate"` alphabetically, not `"OleDbCommand": null,` where declaration order would have
placed it). Worth remembering before the next round adds a new `PipelineComponentSpec` field.

**Codegen: new `AggregateRowEmitter.cs`** (the Aggregate's own OUTPUT row type, resolved straight
off raw output columns per the object-model finding above) plus `PackagePlanner.AggregatePlan`/
`AggregateCountSpec` (one GroupBy column + one-or-more Count columns, each resolved via the new
lineage edge kind; any other AggregationType, zero/multiple GroupBy columns, or an unresolvable
source reference is a named gap) and `PackageGenerator.GenerateAggregateFlow`, hoisted right after
the Lookup check in the per-flow loop (an Aggregate's own output is a structurally different row
shape from the flow's raw source — fewer rows, a new column set — so it needs its own resolution
path, the same "must win first" convention Lookup's own doc comment already established).
Deliberately narrow: only a SQL-sourced flow (OLE DB/ADO NET Source) is supported, and the
destination must be SQL (not Flat File) — neither is evidenced for an Aggregate flow, and
combining either with this already-speculative feature was ruled out up front. The resulting
`ProgramFlowSpec` slots into the EXISTING `wiredFlows` dictionary unchanged (no new dictionary,
no new `ProgramStep` case) — an Aggregate flow is still, from `ProgramEmitter`'s point of view,
just an ordinary flow with an unusual `FlowSourceSpec`.

**`Etl.Core` (`D:\PoC\SSIS_Rewrite`): one new type, `Data/AggregateRowSource.cs`.** Deliberately
minimal: blocking on its INPUT side (must read every row from the wrapped source before producing
its first output row — a group's count isn't known until every row belonging to it has been seen)
but an ordinary streaming `IRowSource<TRow>` on its OUTPUT side, so it slots into the existing
`DataFlowStep<TRow,TEntity>` pipeline with zero changes to that type. It has no built-in notion of
"Count" or any other aggregation function at all — the generated `(key, rows) => new {Row} { ...
rows.Count(r => r.X != null) ... }` projection is where the actual aggregation happens, matching
this project's own long-standing preference for generated code to carry the semantics rather than
a runtime helper guessing at them.

**New fixture, `SyntheticAggregate.dtsx`** (`Ssis.Extract.FixtureBuilder`'s new `aggregate` mode):
OLE DB Source (SqlCommand: Region, CustomerID) → Aggregate (GroupBy Region, Count(CustomerID) AS
CustomerCount — the real evidenced AggregationType values) → OLE DB Destination. **Generated,
built against a copy of `Etl.Core` (0 warnings/0 errors), and actually RUN against
`.\SQLFORPOC_2022`**: East=2/West=1 — an **exact row-for-row match** against the real dtexec probe
run of the same fixture, confirming the generated C# reproduces genuine SSIS Aggregate behavior
bit-for-bit, not an assumption about it. A second, byte-preserving derived fixture,
`SyntheticAggregateUnsupportedType.dtsx` (CustomerCount's own `AggregationType` changed from 1 to
3 via a targeted regex replace, Region's own GroupBy value left untouched), proves the gap path.

Two new `PackagePlannerTests` (the resolved-plan shape; the unsupported-AggregationType gap) and
one new `PackageGeneratorTests` (asserting the exact `Program.cs` wiring — no top-level
`IRowSource<TRow>` registration for the raw source, the `AggregateRowSource<...>` construction,
the nullable-by-construction `CustomerID` property, the `rows.Count(r => r.CustomerID != null)`
projection). Regression: 145/145 in `Ssis.Extract.Codegen.Tests` (was 142), same 3 pre-existing
unrelated failures elsewhere.

**Remaining known gaps, updated:** the numeric-passthrough-coercion gap for every pairing other
than r8-to-i4/wstr-to-i4, and Data Conversion target types other than DT_I4/DT_DBDATE/DT_R8/
DT_WSTR/DT_BOOL. Aggregate/Row Count, named above, is now closed for the one evidenced shape
(GroupBy + Count) — Sum/Average/Minimum/Maximum/CountDistinct/CountAll remain unsupported,
correctly, since none is evidenced anywhere in the tracked portfolio and none was measured.

### Numeric-passthrough-coercion: three more pairings (i8/numeric/r4 → i4), built speculatively (2026-08-30)

Picked next by explicit user choice ("build both" remaining speculative rounds). No real
evidenced instance of any of the three exists anywhere in the tracked portfolio — named purely as
candidates in the "remaining known gaps" list, same speculative-work-signoff discipline as every
other round in this file.

**A real dtexec probe measured all three at once**, same discipline as the original r8-to-i4/
wstr-to-i4 rounds: `SyntheticIntNumericCoercion.dtsx` (`Ssis.Extract.FixtureBuilder`'s new
`int-numeric-coercion` mode) — one OLE DB Source (SqlCommand: `I8Val bigint`, `NumericVal
decimal(18,2)`, `R4Val real`) → OLE DB Destination (all three narrowed to `int`), seeded with
midpoint values chosen to distinguish rounding rules (`synthetic-int-numeric-coercion-tables.sql`).
**Confirmed real, not guessed:** both float-shaped pairings (`numeric`, `r4`) round to the
NEAREST integer, ties-to-EVEN — the exact same rule as `NarrowR8ToI4`/`ParseWstrToI4` (2.5→2,
3.5→4, 4.5→4, -3.5→-4, matching .NET's own default `Math.Round` midpoint rule exactly).
`i8`→`i4` has no rounding question at all (both sides are already integers) — trivially safe to
narrow with `checked((int)value)`.

**Three new `SsisFn` helpers** (`NarrowI8ToI4`, `NarrowNumericToI4`, `NarrowR4ToI4`), each with a
nullable-source overload mirroring `NarrowR8ToI4`'s own pair exactly — since `long`/`decimal`/
`float` each have a genuinely separate nullable variant (unlike `ParseWstrToI4`'s own
`string`/`string?` runtime-type collision), no `ResolveNullableColumnNames`-level structural
nullability was needed; the same call expression works whether or not the column happens to be
nullable-inferred for some other reason. `TransformEmitter`'s mismatch-detection loop gained three
new `HashSet<string>` branches (`narrowedI8ToI4Columns`/`narrowedNumericToI4Columns`/
`narrowedR4ToI4Columns`), mirroring `narrowedR8ToI4Columns`'s own shape exactly — checked BEFORE
the general `mismatchedTypeColumns` gap, same ordering as every other resolved pairing.
`SsisPipelineTypeMap` needed no new entries — `i8`/`numeric`/`r4` were already mapped from earlier
rounds (`long`/`decimal`/`float` respectively).

**A pre-existing test's own premise changed, caught immediately by the suite, not shipped
silently**: `TransformEmitterTests.Emit_ReportsAGap_ForAPlainPassthroughColumn_
WithAnUnevidencedNumericMismatch` used `i8`→`i4` as its own "still genuinely unsupported"
example — now resolved, so that test would have asserted a gap that no longer exists. Repointed
at `i2`(smallint)→`i4`, still genuinely unmeasured, preserving the same coverage (an unevidenced
pairing still gaps) without asserting something now false.

**Verified end to end, same discipline as every prior coercion round**: `ssisx generate` against
the fixture (only the two unavoidable/non-blocking gaps — SqlCommand column-naming advisory,
Notification), built against a copy of `Etl.Core` (0 warnings/0 errors), and actually RUN against
`.\SQLFORPOC_2022` — all 9 rows, midpoints included, an **exact row-for-row match** against the
real dtexec probe run above. New tests: a 3-case `[Theory]` in `TransformEmitterTests` (one per
pairing, each asserting the correct `SsisFn.*` call and zero gaps), one `SsisFnEmitterTests` case
(asserting all six emitted method signatures — three functions × two overloads each), and one
`PackageGeneratorTests` case reading the real fixture end to end. Regression: 150/150 in
`Ssis.Extract.Codegen.Tests` (was 145), same 3 pre-existing unrelated failures elsewhere. No
`Etl.Core` changes this round (the coercion lives entirely in per-package generated code, same as
every prior numeric-coercion round), so `D:\PoC\SSIS_Rewrite` has nothing to commit.

**Remaining known gaps, updated:** Data Conversion target types other than DT_I4/DT_DBDATE/DT_R8/
DT_WSTR/DT_BOOL, and the numeric-passthrough-coercion gap for every OTHER pairing beyond the five
now closed (r8/i8/numeric/r4/wstr → i4) — e.g. i2→i4, i4→i2, any pairing not ending in i4 at all.
None of those has been measured or evidenced; none is guessed at.

### Data Conversion: three more target types (DT_I2/DT_I8/DT_DBTIMESTAMP), built speculatively (2026-08-30)

The last of the two "build both" speculative rounds picked this session. No real evidenced
instance of any of the three exists anywhere in the tracked portfolio — DT_I4/DT_DBDATE/DT_R8/
DT_WSTR/DT_BOOL are the only target types any real or prior-speculative package has ever needed.

**A real dtexec probe measured all three at once**, same shape and seed pattern as the original
DT_R8/DT_WSTR/DT_BOOL round: `SyntheticDataConversionTypes2.dtsx` (`Ssis.Extract.FixtureBuilder`'s
new `data-conversion-types2` mode, reusing the existing `AddConvertedColumn` helper unchanged) —
OLE DB Source (three raw text columns) → Data Conversion (→ DT_I2/DT_I8/DT_DBTIMESTAMP, all
dispositions IgnoreFailure) → OLE DB Destination, seeded with a valid value, a whitespace-padded
valid value, invalid text, an empty string, and NULL
(`synthetic-data-conversion-types2-tables.sql`). **Confirmed real, not guessed:** all three follow
the exact same "NULL on any failure" convention as DT_I4/DT_DBDATE/DT_R8/DT_BOOL — none of them
reproduces DT_WSTR's own genuinely different truncation behavior.

**Three new `SsisFn` helpers** (`ToNullableI2`, `ToNullableI8`, `ToNullableDateTime`), each
following the identical `!string.IsNullOrEmpty(value) && T.TryParse(...) ? result : null` shape
`ToNullableI4`/`ToNullableR8`/`ToNullableDate` already use — `ToNullableDateTime` returns
`DateTime?` (date + time), a genuinely different .NET type from `ToNullableDate`'s own `DateOnly?`
(date only), not a renamed copy. `TransformEmitter.WrapDataConversion`'s switch gained three new
cases (`"i2"`/`"i8"`/`"dbtimestamp"`); `SsisPipelineTypeMap` needed NO new entries at all — all
three pipeline type codes were already mapped from earlier, unrelated rounds (`i2`→`short`,
`i8`→`long`, `dbtimestamp`→`DateTime`).

**Verified end to end, same discipline as every prior Data Conversion round**: `ssisx generate`
against the fixture (only the two unavoidable/non-blocking gaps), built against a copy of
`Etl.Core` (0 warnings/0 errors), and actually RUN against `.\SQLFORPOC_2022` — all 5 rows an
**exact match** against the real dtexec probe run above (rows 1-2 parse correctly including
whitespace-padding; rows 3-5, invalid/empty/NULL, all null out). New tests: one
`PackageGeneratorTests` case (mirroring the original DT_R8/WSTR/BOOL test exactly) and one
`SsisFnEmitterTests` case (asserting all three emitted method signatures). Regression: 152/152 in
`Ssis.Extract.Codegen.Tests` (was 150), same 3 pre-existing unrelated failures elsewhere. No
`Etl.Core` changes this round (Data Conversion support lives entirely in per-package generated
code), so `D:\PoC\SSIS_Rewrite` has nothing to commit.

**Remaining known gaps, updated:** Data Conversion target types other than DT_I4/DT_DBDATE/DT_R8/
DT_WSTR/DT_BOOL/DT_I2/DT_I8/DT_DBTIMESTAMP (e.g. DT_NUMERIC, DT_DBTIMESTAMPOFFSET — neither
evidenced nor measured), and the numeric-passthrough-coercion gap for every pairing beyond the
five already closed. Both of this session's "build both" speculative rounds are now done.

### AI-assisted gap filling, chunk 1 — gap identity + work packets (2026-08-31)

The first chunk of a planned 4-chunk feature (plan preserved at
`C:\Users\yashpal.singh01\.claude\plans\peaceful-percolating-pebble.md`) letting a human working
with AI close the gaps `ssisx generate` refuses to guess at, **without weakening the property that
makes this tool worth anything** — that a gap is loud and visible and nothing which looks complete
is silently wrong. Motivated by the real portfolio: `SSIS_From_Sandeep` sits at 3/5 generatable,
blocked by `Package`'s 2 Script Tasks + 6 Script-Component-synthesized columns, and
`Package_Transforms`' own `DFT_LookupAndAggregate`.

**The load-bearing idea is a TIER TAXONOMY, not "let AI fill gaps".** Reading the real blocked
flows showed they are three genuinely different problems that must be routed differently:

| Tier | Nature | Real example | Closed by | Enters the build as |
|---|---|---|---|---|
| 1 `MissingDatum` | Info absent from the `.dtsx`; code fully derivable once supplied | Lookup join key | AI proposes value + rationale + confidence, **human confirms** | **Nothing AI-authored** — a datum; the deterministic emitter still writes every line |
| 2 `MissingLogic` | Real code IS in the `.dtsx`, just not mechanically translatable | Script Task, Script Component | AI translates into a defined seam, human reviews, gate 3 verifies | Provenance-stamped C# in a `fills/` file |
| 3 `MissingToolSupport` | This tool cannot model the shape at all | Multicast→Aggregate routing | **Nobody, via AI** | Nothing — it becomes an emitter feature with a fixture and a test |

**The Tier-3 refusal is the most important rule, not a footnote.** Patching a Tier-3 gap
per-package with AI would hide ONE systemic emitter gap behind N one-off hand-written patches —
precisely inverting what `generation-readiness.md` exists to surface. Tier 3 gets no work packet
at all, deliberately.

**What chunk 1 built** (nothing about generated C# changed — verified byte-identical, see below):
`GapKind`/`GapTier`/`GapSpec` (`Ssis.Extract.Model/Analysis/GapSpec.cs`), `GapIdentity`
(stable ids), `AiPacketEmitter` (one self-contained markdown packet per Tier-1/2 gap), and
`GenerateCommand` writing `<out>/gaps/<Package>/<GapId>.md` + `<out>/gaps.json`.
`GenerationGap` gained two **optional** trailing parameters (`Kind`, `EvidenceRefId`) specifically
so all ~143 existing `new GenerationGap(...)` call sites compile untouched — only **3** are
classified (Script Task in `PackagePlanner`, Script Component column in `TransformEmitter`, Lookup
in `PackageGenerator`), and tier is DERIVED (`Unclassified` + blocking = Tier 3, + non-blocking =
Advisory) rather than stored twice.

**Gap ids deliberately mirror `ConformanceRuleSpec.RuleId`'s `<CATEGORY>:<location>` shape and its
stability contract** (`SCRIPT-COLUMN:Package:StagingCustomers.FullName`) rather than inventing a
second convention — fill files will be keyed by these and hand-maintained across a migration, so
an id that moved when someone nudged a package in the designer would silently orphan the work
behind it. Ids derive only from meaning-bearing identifiers; a genuine collision disambiguates by
a hash of the REASON, never list position. `EvidenceRefId` is carried structurally rather than
parsed back out of `Reason` text, so evidence resolves unambiguously even when two components
share a display name (RBC_Demo_ETL genuinely has two Data Conversions both producing
`CustomerID_i4`).

**A real, previously-unknown fact about the SSIS format was discovered building this, and it
CORRECTS an earlier documented assumption.** `ScriptComponentPayload.SourceCodeItems` was modelled
as "an ordered list of opaque text blobs" with `ssisx report` writing them as
`source-<index>.cs`, because — as this file's own Script Component section states — no real
SSDT-authored Script Component existed on this machine to read for ground truth. `SSIS_From_Sandeep`
provides one, and the array is nothing of the sort: it is a flat sequence of repeating
**(file name, encoding, content)** triples, one per VSTA project file (30 elements = 10 files;
element 0 `ComponentWrapper.cs`, element 1 `UTF8`, element 2 the actual C#). Confirmed against the
raw XML, not inferred from the parsed output. Two consequences: the entry point is **`main.cs`**,
NOT the `ScriptMain.*` a Script *Task* uses (one more place these two features diverge despite
looking identical in the SSIS UI), and half of what `ssisx report` currently writes as "source"
files is a bare file name or the literal string `UTF8`. Now parsed into
`ScriptComponentPayload.SourceFiles`; a count that is not a clean multiple of 3 yields an empty
list rather than a guessed alignment, with the raw array still carried. **Without this the packets
would have been near-useless** — dumping 30 blobs of mostly VSTA-generated plumbing with the one
authored file buried among them.

**Two more real defects were caught by reading the generated output rather than trusting it**, the
same discipline that caught `portfolio-digest.json`'s silently-empty tuple serialization:
- A Lookup's reference columns carry the `DT_`-prefixed type spelling (`DT_WSTR`) while
  `SsisPipelineTypeMap` is keyed by the buffer short form (`wstr`), so every reference column
  rendered as `(unmapped)`. Normalized at the call site rather than widening the shared map's
  accepted vocabulary, which every other consumer relies on.
- `GapKind`/`GapTier` serialized to `gaps.json` as bare integers (`"Kind": 1`). Unreadable in a
  file whose whole purpose is being a human-and-tool-readable index, and fragile: renumbering the
  enum would silently change what a stored value means. Fixed with
  `[JsonConverter(typeof(JsonStringEnumConverter))]` on both enums.

**Verified, per this project's usual bar:**
- **Generated C# is byte-identical to before this change** — `diff -r` across all 5
  `SSIS_From_Sandeep` package trees plus every shared file, zero differences. This chunk cannot
  regress generation because it does not touch it.
- Run against the real portfolio: **8 work packets** (2 Script Tasks + 6 Script Component
  columns), and the whole gap picture now classifies as 8 `MissingLogic` / 1
  `MissingToolSupport` (the Multicast→Aggregate one) / 10 `Advisory` — the taxonomy landing
  correctly on real data, not a fixture.
- A real packet was read cold and answered to confirm it is self-sufficient. It is — and that
  exercise produced **a concrete design correction for chunk 3**: `IsValidRow`'s port needs a
  static `Regex` field, not just a method body, so the Tier-2 seam contract must be *a partial
  class part*, not a bare method body as the plan assumed.
- 167/167 in `Ssis.Extract.Codegen.Tests` (was 152 — 15 new across `GapIdentityTests`/
  `AiPacketEmitterTests`), 149/149 in `Ssis.Runtime.Expressions.Tests`, same 3 pre-existing
  unrelated failures elsewhere (someone else's in-progress SSDT edits to
  `SSIS/LoadReferenceData.dtsx`/`SSIS/SSIS.dtproj`).

**Not built yet, by design** (chunks 2-4): `fills/` consumption + the Lookup `decisions.json`
(Tier 1) and the Multicast→Aggregate planner fix that actually unblocks `DFT_LookupAndAggregate`;
`--seams` + `ssisx apply-fills` (Tier 2); provenance/staleness checking against
`GapSpec.EvidenceSha256`, which is recorded from this chunk onward specifically so chunk 4 has a
baseline to compare against.


### AI-assisted gap filling, chunk 2 — and a Lookup join key correction that redirected it (2026-08-31)

Chunk 2 was planned as "Tier-1 decisions mechanism + consume a human-confirmed Lookup join key,
taking the portfolio to 4/5". Two things changed that, both found by reading real evidence before
building rather than after.

**First, the plan's own premise about `DFT_LookupAndAggregate` was wrong.** Its real shape is
`FF_SRC_Customers2 → LKP_Country →(Match)→ MCAST_Matched → {Output 1 → AGG_ByRegion → dest,
Output 2 → dest}` plus a routed No-Match branch — **three** composed features (Lookup wiring,
Aggregate inside a Multicast branch, a two-output Lookup), not the one the plan assumed. Surfaced to
the user before starting; chunk 2 was rescoped to the mechanism plus a simple Lookup shape, with the
other two left as their own round. The portfolio stays 3/5.

**Second, and much more consequential: SSIS DOES persist a Lookup's join key.** This file and
`LookupCacheEmitter`'s own doc comment have asserted the opposite for months — "SSIS does not
persist a Lookup's join key anywhere in the saved .dtsx (confirmed via a real object-model
round-trip)". Reading RBC_Demo_ETL's own `LKP_Country` directly:

```
<inputColumn cachedName="Country" ...>
  <property name="JoinToReferenceColumn">CountryName</property>
```

The join key is recorded on the joining INPUT column, and each copied column's source is on its own
output column's `CopyFromReferenceColumn` (`CountryCode`, `Region`) — with the reference query
(`SELECT CountryName, CountryCode, Region FROM dbo.CountryLookup`) and reference columns already
extracted too. **The extractor was capturing all of it the whole time** via the generic
`PipelineInputColumnSpec.Properties`/`PipelineOutputColumnSpec.Properties` — zero reader changes
were needed to read it back.

**Why the wrong conclusion survived so long, and why it is the same mistake as chunk 1's:** it was
drawn from `SyntheticLookupSplit.dtsx`, a fixture built through the object model whose Lookup input
columns were never mapped, so it genuinely had no join key to persist. Absence in one synthetic
fixture was read as absence in the format. That is exactly the error chunk 1 hit hours earlier with
Script Component `SourceCodeItems` ("opaque blobs", concluded when no real SSDT-authored Script
Component was available). **Two instances in one session of the same failure mode: a fixture that
never exercised the real path, treated as evidence about the format.** Worth naming as a pattern —
when a fixture is the ONLY evidence for a negative claim ("SSIS doesn't store X"), that claim is
worth far less than it looks, because the fixture may simply never have set X.

**What this means for the taxonomy:** the Lookup join key is NOT a Tier-1 missing datum. It is a
deterministic feature that was never built. So a Lookup flow now generates with **no human in the
loop at all** — strictly better than the plan. The Tier-1 machinery still ships, because it has a
real reachable consumer: a Lookup that genuinely declares no `JoinToReferenceColumn` (that same
synthetic fixture, and plausibly a programmatically-generated client package). Kept as a documented
FALLBACK on the user's explicit call, after being told it no longer has an evidenced primary use.

**What was built:**
- `GapDecisionSpec`/`GapDecisionStatus` (`Ssis.Extract.Model/Analysis/`) + `GapDecisions`
  (`Ssis.Extract.Codegen/`): confirmed Tier-1 answers read from
  `fills/<Package>.decisions.json`, keyed by `GapSpec.GapId`, **never written by `ssisx`** — the
  same lifecycle, and the same reasoning, as `ConformanceCommand`'s claims files. `--fills`
  overrides the default location (a sibling of `generate/`, not inside it, so it survives the
  "regenerate `generate/` freely" instruction the tool itself gives).
- **Refusing is the important half.** A decision applies only when a human confirmed it
  (`ConfirmedBy`), it is complete for its gap kind, and it still matches the evidence hash it was
  made against. `Unconfirmed`/`Incomplete`/`Stale`/`Orphaned` are each reported in the generate
  report and NOT used. `Confidence` is recorded for the audit trail and deliberately is NOT a gate —
  an AI's own confidence is not sign-off.
- `PackageGenerator.TryDeriveLookupJoinKey` (the primary, deterministic path) +
  `GenerateLookupFlow`, deliberately narrow: single routed output, SQL destination, fast load. A
  Lookup routing BOTH its match and no-match outputs onward (as `DFT_LookupAndAggregate` and
  `SyntheticLookupSplit` both do) is a named gap, not half-generated output.
- `TransformEmitter.LookupJoinSpec`: a Lookup-added column resolves to
  `lookup[row.{input}].{referenceColumn}`, and the transform gains a primary-constructor parameter
  for the cache. Indexer rather than `TryGetValue` on purpose — a full-cache Lookup with no routed
  no-match output fails the component on a miss, so throwing is the faithful translation.
- `ProgramEmitter.LookupPreload`: the reference table is loaded **once at Program.cs top level**
  (`await {Cache}.LoadAsync<TKey>(...)`) before any registration. `IRowTransform.Map` is
  synchronous, so a full cache must be materialized up front anyway; top-level statements can just
  `await`, which needs **no new Etl.Core abstraction and no sync-over-async**, and keeps the load
  visible in the generated file instead of buried in a lambda that happens to run once.

**Verified, per this project's usual bar:**
- **New fixture, `SyntheticLookupSingle.dtsx`** (`Ssis.Extract.FixtureBuilder`'s new
  `lookup-single` mode): OLE DB Source → Lookup (full cache, fail-on-no-match, ONE routed output) →
  OLE DB Destination. Its reference key column is deliberately named **differently** from the input
  column (`CountryName` vs `Country`) so the fixture fails if the key is ever guessed by
  name-matching — which a naive heuristic would have gotten right by accident.
  **Generated, built against a copy of `Etl.Core` (0 warnings/0 errors), and actually RUN against
  `.\SQLFORPOC_2022`** — rows read back, not trusted from the exit code: `Canada→CA/North America`,
  `India→IN/Asia`, and a second `Canada` row resolving identically. Backing tables:
  `tests/Ssis.Extract.Tests/Fixtures/synthetic-lookup-single-tables.sql`.
- **One real object-model fact nailed down:** `SetOutputColumnDataTypeProperties` throws
  `COMException 0xC020401A` on a Lookup output column — the component derives a copied column's type
  from its reference column, so setting `CopyFromReferenceColumn` is both necessary and sufficient.
  Same HRESULT and same cause as the `Microsoft.Aggregate` round.
- **The fallback path proven end to end too**, on `SyntheticLookupSplit.dtsx`: with no decision it
  gaps; with a confirmed one it applies and generation advances to that flow's NEXT real limitation
  ("routes 2 outputs onward") rather than repeating a reason that no longer applies; an unconfirmed
  entry reports `Unconfirmed` and is refused; an entry naming a nonexistent gap reports `Orphaned`.
- **Zero portfolio regression**: all 5 `SSIS_From_Sandeep` package trees regenerate
  **byte-identical** to the chunk-1 baseline. `Package_Transforms` still reports the same 2 gaps —
  its Lookup gate is never even reached, because `PackagePlanner` gaps that flow earlier on the
  Multicast→Aggregate path.
- 175/175 in `Ssis.Extract.Codegen.Tests` (was 167), 149/149 in
  `Ssis.Runtime.Expressions.Tests`, same 3 pre-existing unrelated failures elsewhere. One
  pre-existing test was updated rather than deleted: it asserted the OLD gap wording; its intent (a
  Lookup with no resolvable key still gets a cache scaffold and still is not wired) is unchanged.

**Corrected in place, so the wrong claim does not outlive this round:** `LookupCacheEmitter`'s doc
comment, the Lookup gap message, and `AiPacketEmitter`'s own Tier-1 contract text (which now
explains that seeing this packet at all means the package lacks the property SSIS normally writes).

**Still not built** (chunks 3-4 plus the newly-named round): Aggregate inside a Multicast branch and
a two-output Lookup, which together are what `DFT_LookupAndAggregate` actually needs; `--seams` +
`ssisx apply-fills` for Tier 2; provenance/staleness reporting built out (the hash check exists and
is tested, but nothing records `EvidenceSha256` into a decisions file automatically yet).

### Control-flow parallelism is now reported, not silently flattened (2026-08-31)

Prompted by a real client-shaped question rather than the gap list: a screenshot of an unfamiliar
package (`MFDBUpdate.dtsx`) with **four independent top-level branches**, each
`Execute SQL Task (create temp table) -> Data Flow (text file -> temp) -> Data Flow (temp -> main)
-> Execute SQL Task (UpdateDates ...)`, and the question of whether this tool handles it. Checking
rather than answering from memory turned up a real, previously-unnamed defect.

**The control flow itself was already handled correctly**, and specifically so — this is the exact
shape `PackagePlanner`'s precedence-constraint reachability was built for (see the "three new
component types" section's own account of the naive global-flag bug it replaced). Each branch's own
`create temp table` correctly stays a pre-load statement and its own `UpdateDates` correctly becomes
a post-flow `SqlStep`, with no cross-branch contamination.

**What was wrong is that nothing said the parallelism was being dropped.** SSIS runs executables
with no ordering constraint between them concurrently (up to `MaxConcurrentExecutables`);
`PackageRunner` runs every step strictly in sequence. The generated code produces the same DATA — a
topological order always respects every precedence constraint — so this is not a correctness
failure. But a package whose four branches SSIS ran in parallel generated code that looked complete,
reported zero gaps about it, and took four times as long. That silence is the same failure class as
`DFT_SortAndMergeJoin`'s own silent multi-source bug and the Script Component passthrough bug: the
report said success and the difference was invisible.

**Built:** `PackagePlanner.ReportParallelismFlattening`, one non-blocking `GenerationGap` per
container that has any concurrency, located `{Package}.Parallelism` /
`{Package}.{Container}.Parallelism`, with real evidence in the reason — how many executables were
concurrent, how many waves, and what the package itself declared. Needed **zero** new extraction:
`ControlFlowDagSpec.ParallelLevels` (built in slice 2, never consumed by codegen until now) and
`PackageSpec.ExecutionSemantics.MaxConcurrentExecutables` were both already there. Nested containers
are walked via a new `EnumerateContainers`, so a parallel Sequence Container reports at its own
location rather than the package root's.

**One genuinely nice result worth keeping, because `ControlFlowDagSpec`'s own doc comment warns
against exactly the assumption this relies on.** That type states `ParallelLevels` is a SOUND BUT
INCOMPLETE accounting of parallelism — two nodes in *different* levels can also be unordered, and it
won't surface that pair. True for enumerating WHICH pairs run concurrently, but **not** for the only
question asked here, whether ANY pair does: if every level were a singleton then level 0's single
node is the unique root and each level n+1 node has a predecessor at level n, so the container is a
strict chain with no concurrency at all. So `ParallelLevels.Any(l => l.Count > 1)` is sound AND
complete for existence. Reasoned out and recorded in the method's doc comment rather than assumed
either way, since the surrounding warning invites the opposite conclusion.

**Deliberately non-blocking**, for the same reason as the `.Notification` and SqlCommand advisories:
counting it as blocking would have knocked `Package_Advanced`/`Package_Exports`/`Package_Legacy` out
of the "generatable" count for something that generates and runs correctly — exactly the miscount
`PortfolioDigest.IsBlockingGap` exists to prevent. It also gets **no AI work packet**
(`GapIdentity.HasWorkPacket` is false for Advisory), which is correct: this is a missing runtime
capability, not a missing datum or a script to port. Asserted directly rather than left implicit.

**Verified:**
- **Generated C# is byte-identical** across all 5 real `SSIS_From_Sandeep` packages (`diff -r`
  against the persisted `D:\PoC\SSIS_From_Sandeep_Generated\generate`, only `bin`/`obj` differ).
  This round adds a gap and touches no emitter, so it cannot regress generation.
- **The generatable count is unchanged at 3/5**, and the advisory still shows per-package in
  `generation-readiness.md` tagged `_(non-blocking -- informational)_`.
- **Exactly 4 new gaps on the real portfolio** (20 -> 24; `gaps.json` GapId sets diffed directly,
  nothing else moved): `Package` (2 concurrent), `Package_Advanced` (3), `Package_Exports` (3),
  `Package_Legacy` (2). **`Package_Transforms` correctly reports none** — it is fully constrained,
  which is what proves the detector discriminates instead of firing on everything.
- **The negative case is the one that actually matters**, and it was tested as a `[Theory]` over
  three genuinely sequential packages (`SyntheticPostFlowSql`, `SyntheticNestedContainer` — chained
  at BOTH levels, so it also covers the nested walk not false-positiving — and the real
  `SSIS/LoadEmployees.dtsx`). A false positive here would attach a permanent advisory to every
  sequential package in a client portfolio, which is the noise that trains people to ignore the gap
  list.
- New `tests/Ssis.Extract.Codegen.Tests/ParallelismAdvisoryTests.cs`. **Four pre-existing tests
  legitimately changed**, all gap-COUNT assertions on two fixtures whose root executables carry
  **zero** precedence constraints (`SyntheticForEachFileLoop` — CLAUDE.md already described its loop
  as a "sibling, precedence-independent" task; `SyntheticFlatFileDestination` — three independent
  root flows). Confirmed true positives by reading each fixture's constraint count before touching
  any assertion, not by assuming the new code was right. 179/179 in `Ssis.Extract.Codegen.Tests`
  (was 175), 149/149 in `Ssis.Runtime.Expressions.Tests`, same 3 pre-existing unrelated failures
  elsewhere.

**A second, real, still-open gap found by the same investigation and deliberately NOT fixed here:**
`ExecutableSpec.Disabled` is extracted and `RulesEngine` reports it as a finding, but
**`Ssis.Extract.Codegen` never reads it at all** (confirmed by grep — zero references anywhere in
that project). A disabled task is therefore generated as if enabled. That is a genuine correctness
bug, not an advisory like this round's, and it wants its own round with a fixture that actually
proves a disabled task is skipped.

**Not built, deliberately:** actual parallel execution. That needs `IEtlPackage.Steps` to become
ordered GROUPS, `PackageRunner` to `Task.WhenAll` within a group, and `IUnitOfWork` to become a
per-branch factory — because a `SqlTransaction` is bound to one `SqlConnection` and one connection
serializes commands, so parallel branches must either give up whole-package rollback (closer to what
SSIS itself did, auto-committing per task) or escalate to MSDTC. `PreLoadStatements` being one flat
global list is a third real restructure. Sized but not started: the honest next step is to time a
sequential generated run against a hand-parallelized copy before committing to that change, since
four branches hitting the same database may just move the bottleneck to SQL Server I/O.

**Partially superseded 2026-09-04, and worth flagging so this passage is not misread as still fully
current:** the "Make generated Program.cs a flat, readable script" round (below) added VISUAL/
STRUCTURAL flow grouping — each independent SSIS control-flow branch gets its own `// Flow N:
<name> → <name>` heading in the generated `Program.cs`, computed via `PackagePlanner`'s new
`FlowGroup` (weakly-connected components over a container's own precedence constraints). That is
readability only: every step, in every flow group, still runs strictly sequentially, one
`await theStep.RunAsync(...)` at a time. Everything else in this passage is unchanged and still
true — true CONCURRENT execution (`Task.WhenAll` across branches, a `SqlTransaction`/`IUnitOfWork`
per branch, `PreLoadStatements` no longer one flat global list) remains unbuilt, for exactly the
reasons already given above.

### AI-assisted gap filling, chunk 3 — Tier-2 seams + `ssisx apply-fills` (2026-08-31)

Chunk 3 of the plan at `C:\Users\yashpal.singh01\.claude\plans\peaceful-percolating-pebble.md`.
Closes the gap between "it builds" and "it works": before this round `Package` built **clean**
while silently missing 6 columns two Script Components synthesize. A build succeeding said almost
nothing. Now it refuses to build until those columns are genuinely accounted for.

**Verified the compiler's own behaviour with a throwaway probe BEFORE designing against it** --
same "ask the runtime, don't guess" discipline, applied to Roslyn rather than the SSIS object
model. Both halves confirmed, neither assumed:
- An unimplemented `private partial` method with a return type is `error CS8795`, **one per
  seam**, naming the exact method. That is the whole mechanism: a clear, per-column, compile-time
  refusal instead of a silently dropped column.
- A second `partial` part supplying the implementation compiles clean under
  `<Nullable>enable</Nullable>` + `TreatWarningsAsErrors=true`, **including a
  `static readonly Regex` field** and matching `in RowContext ctx` exactly.

**That second probe is what settles chunk 1's design correction**, and it turned out to be
load-bearing rather than pedantic: the real `SCR_CleanseCustomerRow` port needs a compiled `Regex`
field, which a bare method body cannot declare. So the Tier-2 contract is **a partial class
PART**, not "a method body to paste in" as the original plan assumed.

**What was built:**
- `TransformRequest.EmitSeams` (optional, default false -- all 7 `TransformEmitter.Emit` call
  sites keep compiling) plus `--seams` on `ssisx generate`. Opt-in specifically because it
  deliberately BREAKS the build; nobody's existing pipeline should start failing on an upgrade.
- `TransformEmitter`: a `ScriptComponentColumn` column becomes
  `{Col} = Fill_{Col}(row, ctx),` plus `private partial {Type} Fill_{Col}({Row} row, in RowContext ctx);`,
  and the class gains `partial`. The seam's return type is resolved by **exactly** the rule
  `EntityEmitter` uses for the property it feeds (nullable-set included) -- a disagreement there
  would turn a clear CS8795 into a confusing CS0029.
- `partial` is added **only when a seam actually exists**, so every flow without a Script
  Component stays byte-identical to before this round.
- **The gap stays, and stays BLOCKING.** A seam is outstanding work, not a resolution -- its
  reason changes to name the exact signature and point at `apply-fills`, but
  `generation-readiness.md` must keep saying the package is not generatable, which is now
  literally true: it will not compile.
- New `ssisx apply-fills --out <dir> [--fills <dir>]`, copying `fills/<Package>/*.cs` into
  `generate/<Package>/Fills/` -- its own folder, so a reviewer can see at a glance which files
  are not machine-generated. Reads `fills/` and never writes to it, same lifecycle as conformance
  claims. Exits 3 while any seam is unfilled, 1 on an orphan.

**Refusing is the important half of `apply-fills`, exactly as it was for chunk 2's decisions.** A
fill implementing a `Fill_*` no gap asked for is reported as ORPHANED and **not copied** --
verified for real, not just designed: copying it produces a confusing "no defining declaration"
error two steps later, whereas skipping it keeps the message where the problem is. Unfilled seams
are listed with their GapIds as "the next build will report one CS8795 each".

**Scope rule, and it is the same one the whole taxonomy rests on: seams are offered ONLY for a
Script Component.** Its source IS in the `.dtsx`, so porting it is Tier-2 work a human can do.
Any other unrecognized producer keeps its plain gap, because that is missing TOOL support, and a
per-package hand-patch there would hide one systemic emitter gap behind N patches. Pinned by its
own test rather than left as a comment.

**Verified four ways:**
1. **The real motivating package.** `Package.dtsx` with `--seams`: all 6 real columns become
   seams with correct CLR types (`string`/`bool`/`DateTime`), and the project fails with **exactly
   6 CS8795 errors**, one per seam. Ported both Script Components from their own work packets,
   ran `apply-fills`, rebuilt: **0 warnings / 0 errors**, with the 6 columns genuinely populated.
   This also **exercised the chunk-1 packet format for real** -- each packet carried the complete
   authored `main.cs` (not buried among the 9 VSTA-generated files, thanks to chunk 1's triple-
   parsing correction), which is what made the port unambiguous.
2. **The whole 5-package deliverable**, `D:\PoC\SSIS_From_Sandeep_Generated`, regenerated with
   `--seams` + fills applied: `Generated.slnx` builds **0 warnings / 0 errors**. The fills live at
   `fills/Package/` there as a worked example.
3. **A new dedicated fixture, run for real.** `SyntheticScriptComponentSeams.dtsx`
   (`Ssis.Extract.FixtureBuilder`'s new `script-component-seams` mode): OLE DB Source -> Script
   Component synthesizing `FullName`/`IsValid` -> OLE DB Destination. **Generated, built against a
   copy of `Etl.Core` (0/0), and actually RUN against `.\SQLFORPOC_2022`** -- rows read back, not
   trusted from the exit code: `Alice/Smith -> 'Alice Smith'/1`, `'  Bob  '/'  Jones  ' -> 'Bob
   Jones'/1` (9 chars, no doubled space), `Carol/'   ' -> 'Carol'/0` (LEN=5, **no trailing
   space**). Both boundaries the ported logic turns on, confirmed by real row content.
4. **Byte-identical without `--seams`**: all 5 portfolio package trees `diff -r` clean against the
   pre-round baseline. 182/182 in `Ssis.Extract.Codegen.Tests` (was 179), 149/149 in
   `Ssis.Runtime.Expressions.Tests`, same 3 pre-existing unrelated failures.

**A new fixture was genuinely necessary, and the reason is this session's own recurring lesson.**
`SyntheticScriptComponent.dtsx` already exists -- and is useless here: its Script Component is a
pure PASSTHROUGH that declares no output columns of its own, so it cannot exercise the seam path
at all. Reusing it and concluding "seams work" would have repeated exactly the mistake that
produced both of today's format corrections (a fixture that never sets X, treated as evidence
about X). Checked before building, not after.

**Two real facts worth not re-deriving:**
- A fixture's OLE DB **Destination** `ReinitializeMetaData()` throws `COMException 0xC0202009` if
  the target table does not exist yet -- external metadata is resolved against the live database
  at build time. Create the backing tables BEFORE running the fixture builder. (Every earlier
  fixture's tables already existed, which is why this had not been hit.)
- `ssis-fixture-builder`'s argument order is `<output-path> <fixture-name>`, not the reverse.

**Also corrected, since it directly contradicted chunk 2:** `ssisx --help`'s generate section
still claimed "nothing here reads or writes hand-maintained files the way 'conformance' claims
do". That stopped being true when `fills/<Package>.decisions.json` landed. Now states plainly
that `generate/` is disposable while `fills/` is hand-maintained and only ever read.

**Still not built:** the Script Task half of Tier 2 (`ProgramScriptTaskStep` -- a whole
`ILoadTask`, a different shape from a method seam), so `Package`'s own 2 Script Tasks remain
gaps; chunk 4 (provenance stamping and staleness reporting -- the hash check exists and is
tested, but nothing writes `EvidenceSha256` into a fill yet); and Aggregate-inside-a-Multicast-
branch + a two-output Lookup, which together are what `DFT_LookupAndAggregate` needs to take the
portfolio to 4/5.


### A disabled executable is now skipped, not generated as if live (2026-08-31)

The correctness bug found (and deliberately left) during the parallelism-advisory round.
`ExecutableSpec.Disabled` was extracted, `RulesEngine` reported it as a `disabled-task` finding,
and `PackageDiffer`/`ConformanceRulesBuilder` both used it -- but **`Ssis.Extract.Codegen` had
zero references to it anywhere** (grep-confirmed before doing anything else). A task the package
author had switched off was therefore generated as though it were live.

**This was not hypothetical -- it was live in shipped output.** RBC_Demo_ETL's own
`Package_Legacy.dtsx` carries `SQL_LegacyStep_DISABLED` (`DTS:Disabled="True"`, described by its
own author as *"Deliberately disabled - mirrors the disabled UTF-8 converter in N600ToSalesForce"*
-- i.e. a real client pattern the third party put in on purpose as a KT case), and the persisted
deliverable's own `generate/Package_Legacy/Program.cs` really did contain:

```csharp
PreLoadStatements: ["...TRUNCATE TABLE dbo.CustomerFixedImport;...",
    "INSERT INTO dbo.EtlRunLog (PackageName, EventType, MessageText) VALUES (N'Package_Legacy', N'Legacy', N'this step is disabled');"],
```

The generated job wrote a row SSIS never wrote, and the row's own message text said *"this step is
disabled"*. Unlike the parallelism advisory (same data, longer wall clock), this is a genuine
correctness difference. `Package_Legacy` was one of the 3/5 packages counted as **fully
generatable**, so nothing anywhere flagged it.

**What "skip" has to mean was MEASURED, not reasoned about** -- the one question the fix depends
on is whether a disabled task's own SUCCESSORS still run, because if they do not then skipping
just that node would be wrong and the whole downstream chain would have to go with it. A real
`dtexec` run against `.\SQLFORPOC_2022` settled it (fixture below):

| Measured | Result |
|---|---|
| the disabled task's own statement | **never ran** (`disabled-ran` marker absent) |
| an ENABLED child of a DISABLED Sequence Container | **never ran** (`inside-disabled-seq` absent) |
| the Data Flow Task ordered AFTER the disabled task | **ran** -- all 3 rows loaded |
| the Execute SQL Task after that | **ran** (`post` marker present) |

So SSIS treats a disabled node as satisfied for precedence purposes: the ordering it imposes on
everything else is unchanged, and skipping exactly that node and nothing else is the faithful
translation.

**The fix is one `continue` in `PackagePlanner.WalkContainer`**, placed before every payload
branch -- which is why it covers a disabled CONTAINER too, by never reaching the `STOCK:SEQUENCE`
branch that would have descended into its children. Two deliberate details:
- The skipped node is **not** added to `flowOrAfter`. A disabled Data Flow Task never ran, so
  nothing ordered after it is "after a flow", and a following Execute SQL Task correctly stays a
  pre-load statement rather than becoming a post-flow `SqlStep`.
- `PackagePlanner` is the only codegen consumer of the executable tree (confirmed by grepping
  every `.Executables`/`.Children` reference in that project), so there was no second place to
  fix and no risk of a half-applied skip.

Also added, for completeness rather than from evidence: a ForEach Loop whose single body task is
disabled now skips the whole loop instead of emitting a loop that runs a statement SSIS never ran.
Not evidenced in any real package -- one check, stated as such.

**A SECOND real bug fell out of the same root cause and was fixed in the same round, because it
is the same fact being ignored.** The parallelism advisory added earlier the same day counted
`ParallelLevels` membership without regard to whether a node ran -- and `Package_Legacy`'s only
concurrent wave is `SQL_AtomicSwap -> {DFT_FixedWidthImport, SQL_LegacyStep_DISABLED}`. SSIS never
ran two things at once in that package, yet the advisory said it did: **a live false positive on a
real package**, exactly the noise that (by that round's own stated reasoning) trains people to
ignore the gap list. `ReportParallelismFlattening` now counts only non-disabled nodes per level,
and `EnumerateContainers` no longer descends into a disabled container at all.

**That filter gives something up, and the trade is stated rather than glossed over.** The previous
round established that `ParallelLevels.Any(l => l.Count > 1)` is sound AND complete for the
existence question, even though `ControlFlowDagSpec`'s own doc comment warns the levels are
sound-but-incomplete for enumerating which pairs are concurrent. Filtering is safe in the sense
that disabling a node does not change the ordering it imposes, so nobody else's depth moves -- but
it does give the completeness back up: with a disabled node as the sole bridge of a diamond, two
enabled siblings can both land in singleton levels and go unreported. Accepted deliberately: an
advisory that fires when nothing ran concurrently is worse than a missed report in that one
contrived shape. Recorded in the method's own doc comment too, not just here.

**The advisory design, which is deliberately unlike every other gap in this tool:** a skip is
reported as one **non-blocking** `GenerationGap` per skipped executable (`{Name}.Disabled`), and
skipping is *correct* -- it is what SSIS itself did -- so it does not block generation, is
`GapTier.Advisory`, and produces **no AI work packet** (asserted directly by a test, not left
implicit: there is nothing for anyone to fill in). It is reported at all because silence about a
real difference between the `.dtsx` and the generated output is this tool's own recurring failure
mode: a reader diffing the two would otherwise find a task in the package with no counterpart in
the code and no way to tell deliberate handling from a bug. The container's advisory also says how
many children went with it, so "one task skipped" is never confused with "a whole branch skipped".

**Verified four ways:**
1. **The real motivating package.** Regenerating `Package_Legacy.dtsx`: the diff against the
   persisted baseline is **exactly one line** -- the disabled task's `INSERT` removed from
   `PreLoadStatements`, nothing else. Diffing the whole portfolio's `gaps.json` GapId sets showed
   **exactly one swap**: the false-positive `Package_Legacy.Parallelism` advisory replaced by the
   accurate `SQL_LegacyStep_DISABLED.Disabled` one, count unchanged at 23, nothing else moved.
2. **A new dedicated fixture, run twice -- once under real SSIS, once as generated code.**
   `tests/Ssis.Extract.Tests/Fixtures/SyntheticDisabledTask.dtsx`
   (`Ssis.Extract.FixtureBuilder`'s new `disabled-task` mode): a single fully-ordered chain
   `SQL_MarkPre -> SQL_Disabled_MarkNever -> DFT_Load -> SQL_MarkPost -> SEQ_Disabled`, with the
   second link disabled and `SEQ_Disabled` a disabled container holding one enabled child.
   Deliberately with **no** parallelism at all (every root node ordered), so the only thing the
   fixture varies is disabled-ness -- and deliberately putting the disabled task in PRE-LOAD
   position and the disabled container in POST-FLOW position, so one fixture covers both of
   `PackagePlanner`'s placement paths. **Generated, built against a copy of `Etl.Core` (0
   warnings/0 errors), and actually RUN against `.\SQLFORPOC_2022`** -- rows read back, not
   trusted from the exit code: markers `pre`, `post`, and 3 target rows; `disabled-ran` and
   `inside-disabled-seq` both absent. An **exact row-for-row match against the real dtexec probe
   run of the same fixture**, which is the strongest evidence tier this tool's own philosophy asks
   for. Backing tables:
   `tests/Ssis.Extract.Tests/Fixtures/synthetic-disabled-task-tables.sql`.
3. **A derived fixture for the parallelism half**, since the fixture above has no parallelism by
   design: `SyntheticFlatFileDestinationDisabled.dtsx` is `SyntheticFlatFileDestination.dtsx`
   (three precedence-independent root flows) with `DTS:Disabled="True"` added to one of them via a
   byte-preserving PowerShell read/replace/write -- 27 bytes longer, exactly the one attribute
   (trap 18's discipline; never a text-replace on a `.dtsx`). Its advisory correctly reads "up to
   2 executables" where the original reads "up to 3", and the disabled flow is gone from
   `plan.Flows` entirely rather than merely uncounted.
4. **No collateral effect anywhere else.** Regenerating the whole 35-package fixture corpus shows
   exactly the two new `.Disabled` advisories and no others; this PoC's own 4 packages are
   unchanged; the refreshed deliverable at `D:\PoC\SSIS_From_Sandeep_Generated` (regenerated with
   `--seams`, fills re-applied) builds **0 warnings/0 errors** with the bogus statement gone.
   186/186 in `Ssis.Extract.Codegen.Tests` (was 182 -- 4 new in
   `tests/Ssis.Extract.Codegen.Tests/DisabledExecutableTests.cs`), 149/149 in
   `Ssis.Runtime.Expressions.Tests`, same 3 pre-existing unrelated failures elsewhere. **No
   existing test needed changing**, which is itself the point: nothing had ever asserted the buggy
   behaviour, because nothing had ever looked.

**One object-model fact worth not re-deriving:** the runtime property is `DtsContainer.Disable`
(no "d"), while the persisted attribute is `DTS:Disabled` on every executable in the tree and
`DTS:Disable` on the package root -- so the runtime name matches the ROOT's attribute spelling,
not the one it actually writes for the node you set it on. `ExecutableSpec`'s own doc comment
already recorded the root-vs-child attribute difference; the runtime-property spelling is the new
half.

**Remaining known gaps, unchanged by this round:** the Script Task half of Tier 2
(`ProgramScriptTaskStep`), chunk 4 (provenance + staleness), Aggregate-inside-a-Multicast-branch +
a two-output Lookup (together what `DFT_LookupAndAggregate` needs to reach 4/5), Data Conversion
target types beyond the eight closed, and the numeric-passthrough-coercion gap for every pairing
beyond the five closed. Codegen ignoring `ExecutableSpec.Disabled`, named as a real open bug by
the parallelism round, is now closed.


### AI-assisted gap filling — the Script Task half of Tier 2, and two more silently-ignored attributes (2026-08-31)

Chunk 3 closed Script Component COLUMNS. This closes the other half: a Script Task, which is a
whole `ILoadTask` rather than one method on a transform, so it needed its own emitter, its own
place in the step order, and — this was the genuinely new part — a way for two ported tasks to
share a variable.

**The plan's own open question is answered, and the answer made it moot.** It flagged "whether a
`partial` method may implement an interface member under net10" as something to resolve rather
than assume. Probed against Roslyn first (same discipline as chunk 3's own probe): the question
never needs answering, because the better shape avoids it. The generated part implements
`ILoadTask` FULLY — `Name`, `RunAsync`, the `StepResult`, the timing — and delegates to one
`private partial Task RunScriptAsync(ScriptTaskContext ctx, CancellationToken ct)`. Confirmed both
halves: unimplemented, that is exactly one `CS8795` naming the method; a second part supplying
`private partial async Task RunScriptAsync(...)` compiles clean under `Nullable` +
`TreatWarningsAsErrors`, static fields included. Keeping the plumbing generated also means a fill
cannot get the row counts or timing subtly wrong, and there is only one seam signature to learn.

**PackageVariables is required by real evidence, not added for symmetry.** `Package`'s two Script
Tasks are `SCR_ValidateAndLogStart` (C#) stamping `User::BatchStartTime` and
`SCR_NotifyAndLogProgress` (**VB.NET** — the first VB script this tool has had to port) reading it
back to compute elapsed time. Nothing in `Etl.Core` carried state between steps: `LoadContext` is
immutable run identity and `IUnitOfWork` is a database connection. So without a shared bag one of
those two tasks could not be ported faithfully at all. New `Etl.Core.Abstractions.PackageVariables`
(a deliberately plain string→object? dictionary — no scoping, no expression evaluation, no declared
types; anything richer would be inventing semantics no `.dtsx` asked for) plus
`ScriptTaskContext(Uow, Load, Variables, Services)`.

**One parameter, not four, and the reason is the lifecycle:** fills are hand-written,
human-reviewed files with provenance. Adding a capability later must not force every already-signed-off
Script Task fill to be edited. `ScriptTaskContext` absorbs that.

**`Services` is the loose one, and deliberately so.** A Script Task can reach any connection
manager in the package, and which ones it touches is decided inside script text this tool reads but
does not parse. Rather than guess a narrower shape, the port gets the app's own `IServiceProvider`
and resolves what it needs (`IOptions<FileSourceOptions>` for a CSV path, `ILoggerFactory` for
`FireInformation`). Defensible for a hand-written reviewed fill; it would not be defensible for
generated code.

**Placement: a Script Task is always a step at its true position, never hoisted.** No pre-load/
post-flow branching of the kind an Execute SQL Task gets — there is no third "pre-load script" list
on `IEtlPackage`, and inventing one would compound the ordering problem the two existing hoisted
lists already have. Which surfaced a real hazard worth naming:

**`ReportHoistingInversion` — a new BLOCKING gap for a hazard this round itself introduces.**
`PreLoadStatements`/`PreLoadFileActions` are hoisted ahead of every step. Harmless while everything
pre-flow was itself hoisted — true until a Script Task became a step. Now a hoisted statement the
package ordered AFTER a Script Task would silently run BEFORE it (imagine "validate, then
TRUNCATE, then load" quietly becoming "TRUNCATE, then validate"). Tested against transitive
ancestors, not direct predecessors (a chain of any length counts) and not walk position (two
UNORDERED pre-flow tasks may legitimately run either way — SSIS would have run them concurrently —
and must not be reported). `Package`'s own `SQL_TruncateTargets -> SCR_ValidateAndLogStart` is
correctly ordered and correctly silent.

**Two MORE silently-ignored attributes found, and they are the same bug shape as `Disabled`.**
Reading `Package`'s control flow closely: `DTS:Value="1"` on `DFT_LoadCustomers -> SQL_LogLoadFailure`
and `DTS:EvalOp="3"` + `DTS:Expression="@[User::RowsLoaded] > 0"` on
`SCR_NotifyAndLogProgress -> DFT_BuildSalesSummary`. Both have ALWAYS been extracted
(`PrecedenceConstraintSpec.Value`/`.EvalOp`/`.Expression`) and read by **nothing** in
`Ssis.Extract.Codegen` — grep-confirmed, exactly as `Disabled` was. Meaning generated code would,
on a SUCCESSFUL run, write a failure-log row SSIS only writes on failure, and build the sales
summary regardless of whether anything loaded.

The raw enum values were read from the real GAC enums by reflection rather than guessed, which
mattered: **the two enums do not share a numbering base** (`DTSExecResult`: Success=0, Failure=1,
Completion=2; `DTSPrecedenceEvalOp`: Expression=1, Constraint=2, ExpressionAndConstraint=3,
ExpressionOrConstraint=4), so "3" means completely different things depending on which attribute
it is on. New `ReportConditionalConstraints` reports each one, **blocking** — unlike the parallelism
and disabled advisories, which produce code that does the right work (just sequentially, or minus
something SSIS also skipped), this one produces code that does the WRONG work. Only `Package` has
any conditional constraint anywhere in the tracked portfolio or the fixture corpus (measured, 1 of
each), so nothing else moved.

**So the honest outcome is that the portfolio stays 3/5, not 4/5.** Closing the Script Task gaps
was supposed to take `Package` to fully-generatable; investigating it turned up a further real
correctness problem in the same package. Reporting that instead of letting `Package` read as
generatable is the whole point of the tool — a wrong 4/5 would have been worse than an honest 3/5.
Conditional execution is a real feature for its own round (`IEtlPackage.Steps` is a flat
unconditional list, and the expression form additionally needs a variable that only exists once the
Script Task setting it has been ported — which, as of this round, it now can be).

**Verified four ways:**
1. **The real motivating package.** `Package.dtsx --seams`: 8 seams (6 columns + 2 Script Tasks),
   build fails with exactly those 8 `CS8795`. Ported BOTH Script Tasks from their own work packets
   — including the VB one — ran `apply-fills`, rebuilt: **0 warnings / 0 errors**. The whole
   5-package deliverable at `D:\PoC\SSIS_From_Sandeep_Generated` likewise builds 0/0 with all four
   fills applied; they are checked in there at `fills/Package/` as the worked example.
2. **A new dedicated fixture, actually run.** `SyntheticScriptTaskSeams.dtsx`
   (`Ssis.Extract.FixtureBuilder`'s `script-task-seams`): `SQL_Truncate -> SCR_Start -> DFT_Load ->
   SCR_Finish`, deliberately putting one Script Task pre-flow and one post-flow so one fixture
   covers both placements, with the Execute SQL Task ordered first so the hoisting guard's NEGATIVE
   case is covered too. **Generated, built against a copy of `Etl.Core` (0/0), and actually RUN
   against `.\SQLFORPOC_2022`** — read back, not trusted from the exit code:
   `Marker='started-by-SyntheticScriptTaskSeams', RowsSeen=2`. Both halves matter. The marker
   proves the shared `PackageVariables` genuinely carried a value between two independently-written
   fills; `RowsSeen=2` proves a Script Task's own SQL sees rows the flow inserted but has NOT
   committed — i.e. that it really does run inside the package's single transaction.
3. **The hoisting guard's positive case**, via a second fixture
   (`SyntheticScriptTaskHoistInversion.dtsx`, `SCR_First -> SQL_Truncate -> DFT_Load`): fires with
   the right message. A guard nothing else in the corpus can trigger is worth a fixture of its own.
4. **No collateral effect.** Default (non-`--seams`) generation is byte-identical across all 5
   real packages; the portfolio's gap set gains exactly the 2 new constraint gaps and nothing else.
   191/191 in `Ssis.Extract.Codegen.Tests` (was 186), 149/149 in `Ssis.Runtime.Expressions.Tests`,
   47/47 in `Etl.Core.Tests` (was 41), same 3 pre-existing unrelated failures elsewhere.

**A real bug in this round's own tooling, caught by the mechanism built to catch exactly this.**
`apply-fills` recognized neither Script Task fill: its seam regex allowed exactly one token between
`partial` and the method name, and the very first real fill written was
`private partial async Task RunScriptAsync(...)`. It surfaced not as a wrong build but as
`apply-fills` reporting both files ORPHANED and refusing to copy them — the refusal path from
chunk 3 doing its job on its author. Both regexes now allow an optional `async`.

**Two facts worth not re-deriving:**
- **A Script Task fill is matched by its CLASS name, not its method name.** Every Script Task seam
  is `RunScriptAsync`, so keying on the method would let one fill silently appear to answer both of
  `Package`'s two gaps, and one task's logic would go missing with nothing reported.
- **`TaskHost.ScriptLanguage` does not take the value that gets persisted.** Setting it to
  `"CSharp"` — exactly what the saved XML's own `Language` attribute contains, read straight out of
  the real `Package.dtsx` — throws `UnrecognizedVSTAScriptLanguageException`, because the setter
  wants a VSTA DISPLAY name ("Microsoft Visual C# 2022"). Same runtime-name-vs-XML-attribute
  divergence as the ForEach File Enumerator's own `Directory`/`FileNameRetrieval`. The fixture
  leaves it unset; nothing under test needs it.

Also updated so the contract cannot drift: `AiPacketEmitter`'s Tier-2 Script Task section, which
still told a porter to write a whole `ILoadTask` class. It now describes the partial-part contract,
the `ctx` mapping table, and the `using Microsoft.Extensions.DependencyInjection;` a porter needs
to resolve anything from `ctx.Services` — a papercut hit while writing the first real fill.

**Still not built:** chunk 4 (provenance + staleness — the `EvidenceSha256` check exists and is
tested, but nothing writes it into a fill yet); conditional precedence constraints, newly named
above and now what actually blocks `Package`; and Aggregate-inside-a-Multicast-branch + a two-output
Lookup, which together are what `DFT_LookupAndAggregate` needs. The Script Task half of Tier 2,
named as open since chunk 3, is now closed.

### Conditional precedence constraints -- one of seven forms, chosen by measurement (2026-08-31)

The gap the Script Task round newly surfaced, and the thing that then actually blocked `Package`.
`DTS:Value` (outcome) and `DTS:EvalOp`+`DTS:Expression` (guard expression) had always been
extracted and read by nothing in `Ssis.Extract.Codegen`, so generated code would write
`Package`'s failure-log row on every SUCCESSFUL run and build its sales summary regardless of
whether anything loaded.

**A real dtexec probe came first, before any generator code -- and it is the whole reason the
scope is what it is.** New fixture `tests/Ssis.Extract.Tests/Fixtures/SyntheticCondConstraint.dtsx`
(`Ssis.Extract.FixtureBuilder`'s new `cond-constraint` mode): ONE node, `SQL_Body`, with SEVEN
successors hanging off it -- one per edge kind -- each writing its own marker row so what ran is
read back from a table rather than inferred from console output. Every successor has exactly one
incoming constraint, which keeps `DTS:LogicalAnd` (the AND/OR combination of MULTIPLE incoming
constraints) entirely out of the measurement, as does the real package. `SQL_Body` raises an error
only when a control row says so, so the two runs are the same fixture with one bit flipped and
nothing else varying. `User::ProbeRows` is a literal `0`, so `== 0` is always true and `> 0`
always false -- both expression outcomes measured in one run with no mechanism needed to POPULATE
a variable (which would have been a second unmeasured thing in the same probe).

| Edge | predecessor succeeded | predecessor FAILED |
|---|---|---|
| `Value=Success` / absent | ran | skipped |
| `Value=Failure` (1) | skipped | **ran** |
| `Value=Completion` (2) | ran | ran |
| `EvalOp=1` Expression-only, true | ran | **ran** -- the outcome constraint really is ignored |
| `EvalOp=1` Expression-only, false | skipped | skipped |
| `EvalOp=3` Success AND true | ran | skipped |
| `EvalOp=4` Failure OR false | skipped | ran |
| **package result** | `DTSER_SUCCESS` | **`DTSER_FAILURE`** |

**Two findings drove the design.** A Failure handler running does NOT rescue the package --
`MaximumErrorCount` was still reached and dtexec returned failure -- and the failing task's own
`INSERT` PERSISTED, i.e. SSIS committed it. So faithfully translating any outcome-based form needs
work to run AFTER a failure AND be committed, while `PackageRunner` wraps the whole package in ONE
transaction and rolls it back on any failure. That is not a small gap to close; it is a
failure-handler position outside the transaction, and it would trade away the whole-package
rollback this rewrite deliberately chose over SSIS's per-task auto-commit.

**So exactly one form is generated: `EvalOp=3` (ExpressionAndConstraint) with the constraint half
at Success** -- "run only if the predecessor succeeded AND this expression is true". It is the only
form that never asks for work after a failure, and it is the real evidenced case
(`SCR_NotifyAndLogProgress -> DFT_BuildSalesSummary`, gated on `@[User::RowsLoaded] > 0`). Every
other form stays a BLOCKING gap whose reason now names the measured behaviour rather than a
general "not supported" -- including, per-form, that the successor was observed running on the
failure path. Worth recording: `EvalOp=3` with the constraint half at Success persists as
`DTS:EvalOp="3"` with NO `DTS:Value` at all (Success = 0 is the omitted schema default), confirmed
from both the real package and the probe's own saved XML -- so the absence of `DTS:Value` is not
evidence of an unconditional edge.

**`Etl.Core`:** one new `Pipeline/ConditionalStep.cs`, a DECORATOR rather than a new step kind --
SSIS puts the condition on the EDGE, not the task, so wrapping keeps every existing `ILoadTask`
(data flow, Execute SQL, Script Task, ForEach loop, Conditional Split, ...) gateable with no change
to any of them. `StepResult` gained `bool Skipped { get; init; }` (additive; every existing
construction site unchanged): a skipped step and a step that ran and moved zero rows are different
facts, and `(0, 0)` alone cannot tell them apart. It reads the variables LIVE on each run, which is
what lets a guard see what an earlier ported Script Task wrote.

**Codegen.** `PackageStep`/`ProgramStep` each gained a `Guard` init property on the ABSTRACT record
-- not a wrapper record, not a per-kind field -- so all ~15 existing construction sites and every
per-kind emission case stay untouched. Guards are resolved BEFORE the walk (attaching one
afterwards would mean matching steps back to executables, which only `FlowStep`/`ScriptTaskStep`
retain enough identity to do) and applied through one `Gated(refId, step)` local in
`WalkContainer`. Emission wraps the step's own local in a second local
(`salesSummaryFlowGated`) rather than inline in the `Steps` list, which keeps the generated file
readable and works uniformly for every step kind.

**The expression translator is REUSED, not rewritten.** `ExpressionTranslator.TranslateCondition`
already carries the whole comparison surface a constraint needs, oracle-verified against measured
SSIS behaviour (ordinal `==`/`!=` versus culture-aware `<`/`>`, numeric-vs-string operand dispatch,
`&&`/`||`/`!`); the only difference is what a `Reference` resolves to -- a package VARIABLE lookup
(`v.Get<int>("User::RowsLoaded")`) instead of a pipeline buffer column. This is the opposite call
from `ForEachLoopEmitter`, which genuinely needed its own tiny translator because its expression is
a STRING-building one with no boolean surface at all. A translation that reaches for an `SsisFn.*`
helper or a null test is REFUSED rather than emitted: neither is evidenced in any real constraint
expression, and emitting either would need wiring this round does not do -- an `SsisFn` call needs
`SsisFnEmitter` to emit that helper and `Program.cs` to carry the using, which is exactly the
latent half-wired hazard that bit `RouterEmitter` once already.

**Variable seeding is not cosmetic.** `Program.cs` now emits
`packageVariables.Set("User::RowsLoaded", 0); // design-time default from the .dtsx` for every
variable a guard reads. Without it a guard reading a variable nothing has assigned yet would get
`Get<T>`'s fallback instead of the declared default -- identical by luck for a `0` default, wrong
for any other. Only the variant types whose literal form is unambiguous are mapped
(Int16/Int32/Int64/String/Boolean); a DateTime default would need a parse and a round-trip format
decision nothing evidenced asks for, so such a variable is simply absent from the table and a guard
referencing it degrades to a named gap through the ordinary unresolved-reference path.

**A safety net was built for a hazard this round introduces, and it immediately caught a real
case I had not constructed for it.** A guard that translates successfully but never reaches a step
would silently run the gated work unconditionally while reporting nothing -- the same failure class
as the disabled-executable and Script-Component-passthrough bugs. `ReportUnappliedGuards` turns
that into a blocking gap. Running the whole fixture corpus afterwards showed the probe fixture
itself tripping it: `SyntheticCondConstraint` has no Data Flow Task at all, so its own
`SQL_ExprAndSuccess` is an Execute SQL Task in PRE-LOAD position, hoisted into a flat
`IReadOnlyList<string>` that has nowhere to carry a condition. Correctly reported rather than
silently dropped.

**Verified four ways:**
1. **The real motivating package.** `Package.dtsx` regenerated: the expression-constraint gap is
   GONE, only the Failure one remains, and the emitted `Program.cs` carries
   `v => (v.Get<int>("User::RowsLoaded") > 0)` wrapping `DFT_BuildSalesSummary`. The whole
   5-package deliverable at `D:\PoC\SSIS_From_Sandeep_Generated`, regenerated with `--seams` and
   all four fills re-applied, builds **0 warnings / 0 errors**.
2. **A byte-level baseline diff, not an argument.** Built the CLI from a `git worktree` at HEAD,
   generated the 5 real packages both with and without `--seams`, and diffed the trees: the ONLY
   difference in either mode is `Package/Program.cs`, exactly the three intended edits (the seed
   line, the `ConditionalStep` local, the `Steps` entry). All four other packages byte-identical.
3. **A new dedicated fixture, run under BOTH real SSIS and as generated code, matching exactly.**
   `SyntheticCondGuard.dtsx` (`cond-guard` mode): `SQL_Reset -> SCR_SetRows -> DFT_Load` with
   THREE gated Execute SQL Tasks -- one whose condition is true at the design-time default, one
   that must NOT run (a fixture where every gate fires would pass just as well with the condition
   ignored), and one reading `User::RowsLoaded`, which the Script Task WRITES. That last one is the
   real package's own composition and the reason `PackageVariables` had to land first. Generated,
   built against a copy of `Etl.Core` (0/0), and RUN against `.\SQLFORPOC_2022` -- markers read back
   from the table, not trusted from the exit code: `gate-true` ran, `gate-false` skipped,
   `gate-from-script` ran (the fill set 7), 2 target rows, package succeeded despite a skipped step.
   Then `dtexec` on the same fixture: `DTSER_SUCCESS`, `gate-true` only, 2 rows -- correct, because
   under real SSIS the stub Script Task leaves `RowsLoaded` at 0. Setting the fill to 0 to match the
   stub and re-running the generated exe produced an **exact match: `gate-true` only, 2 rows**, all
   three gates agreeing with real SSIS.
4. **A second derived fixture for the safety net.** `SyntheticCondGuardUnapplied.dtsx` is
   `SyntheticScriptTaskHoistInversion.dtsx` with `EvalOp="3"` plus an expression added to its
   `SCR_First -> SQL_Truncate` edge, via a byte-preserving read/replace/write (trap 18; never a
   text-replace on a `.dtsx`) -- +74 bytes, exactly the one attribute pair plus the package rename.
   `SQL_Truncate` is pre-flow, so it hoists and the guard cannot apply: reported.

**A pre-existing, unrelated bug found and fixed in passing:** `Ssis.Extract.FixtureBuilder`'s
dispatcher had the `data-conversion-types2` arm copy-pasted with `lookup-single`'s condition, so
that arm was dead code and `data-conversion-types2` fell through to "unknown fixture" -- that
fixture had been unbuildable since the round that added it. One-line fix.

**One object-model sharp edge worth not re-deriving:** `DTSExecResult` and `DTSPrecedenceEvalOp`
each exist in BOTH `Microsoft.SqlServer.Dts.Runtime` and `...Runtime.Wrapper`, so an unqualified
reference is `CS0104: ambiguous` in any project importing both (as every object-model project here
does). Fully qualify. The settable surface on `PrecedenceConstraint` is
`Value`/`EvalOp`/`Expression`/`LogicalAnd`, all plain properties -- no `Set*Property` indirection,
unlike the pipeline components.

**Test coverage:** new `tests/Ssis.Extract.Codegen.Tests/ConditionalConstraintTests.cs` (10 cases:
the translated form plus its seeds and the untouched unconditional steps, the `Program.cs` wiring,
the unapplied guard, a 5-case `[Theory]` over every unsupported form asserting its measured reason,
the four unconditional-default spellings staying SILENT, and a supported-form expression that
cannot be translated). New `D:\PoC\SSIS_Rewrite\tests\Etl.Core.Tests\Pipeline\ConditionalStepTests.cs`
(3 cases, including that the predicate reads variables live rather than as they were at
construction). One test was RELOCATED, not deleted: `ScriptTaskSeamTests`'
`Plan_ReportsAConditionalPrecedenceConstraint_...` lived there because constraints were DISCOVERED
while building Script Task seams; it covered two forms where the new file covers all five, so a
narrower copy would only be a second thing to update -- a comment in its place says so.
Regression: 200/200 in `Ssis.Extract.Codegen.Tests` (was 191), 149/149 in
`Ssis.Runtime.Expressions.Tests`, 50/50 in `Etl.Core.Tests` (was 47), same 3 pre-existing unrelated
failures in `Ssis.Extract.Tests`.

**The portfolio stays at 3/5, and `Package`'s blocking-gap count drops 10 -> 9.** Closing the
expression form does not make `Package` generatable, because its Failure edge is still there and
still real. That is the honest number.

**Remaining known gaps, updated:** the outcome-based conditional forms (Failure/Completion,
Expression-only, ExpressionOrConstraint) -- which now all reduce to ONE emitter feature, a
failure-handler position outside the package transaction, sized by the measurements above; chunk 4
(provenance + staleness); Aggregate-inside-a-Multicast-branch plus a two-output Lookup, together
what `DFT_LookupAndAggregate` needs; and the pre-existing per-pairing coercion and Data Conversion
target-type gaps. The expression form of conditional execution, named as the blocker by the Script
Task round, is now closed.

### Failure handlers -- the outcome half of conditional constraints (2026-08-31)

The round the conditional-constraint measurement specified: a `DTS:Value="1"` (Failure) precedence
constraint. This was the last remaining TOOL gap on `Package` -- generated code ran its
failure-log task on every SUCCESSFUL run, because the task was an ordinary step.

**The load-bearing design question was answered by measurement, and it collapsed the work.** A
failure handler must run after the package transaction is rolled back AND commit its own work, so
the obvious reading is "it needs its own connection" -- a scope factory, or a connection string
`PackageRunner` does not have (and cannot reliably recover: SqlClient strips the password from
`SqlConnection.ConnectionString` once opened, unless `Persist Security Info=true`). A throwaway EF
probe against `.\SQLFORPOC_2022` measured the actual behaviour instead:

```
after rollback: CurrentTransaction is null = True
after-rollback INSERT: succeeded
rows persisted: [after-rollback]          <- the in-transaction row did NOT persist
```

So after EF Core's own `RollbackAsync` the DbContext has no ambient transaction and a subsequent
raw command autocommits. **No second connection, no scope factory, no connection-string handling.**
That is also exactly SSIS's own shape -- each task auto-committed separately.

**The contract is made explicit anyway, and that is deliberate.** New
`IUnitOfWork.ExecuteSqlWithoutTransactionAsync` THROWS if a transaction is still active, rather
than quietly executing. Relying on implicit post-rollback behaviour is the kind of dependency that
breaks silently on a framework upgrade; here a future EF that stopped clearing the transaction
fails loudly instead of enlisting the handler in a transaction about to be discarded -- which would
write nothing, the one outcome this position exists to prevent.

**`Etl.Core`:** `FailureHandlerAction(TaskName, Sql)` -- plain data, matching
`FileSystemPreLoadAction`, because the one evidenced handler is a single terminal Execute SQL Task
(RBC_Demo_ETL's `SQL_LogLoadFailure`, whose author even documented it as *"Runs only when
DFT_LoadCustomers fails (Failure constraint)"*). `IEtlPackage.FailureHandlers` defaults to empty, so
every existing implementer is unchanged. `PackageRunner.RunFailureHandlersAsync` runs them in the
catch block AFTER the rollback, each isolated in its own try/catch so a failing handler cannot
replace the original exception -- the thing anyone diagnosing the run actually needs, and SSIS would
not have erased it either -- and with `CancellationToken.None` throughout, the same choice the
rollback above already makes. `PackageResult.FailureHandlersRun` is additive, so a notifier can say
"it failed AND the failure was logged", which is the entire point of a handler.

**Codegen: the critical part is the EXCLUSION, not the emission.** `ResolveConditionalConstraints`
now returns handlers alongside guards, keyed by refId, and `WalkContainer` skips those refIds
outright -- placed right beside the disabled-executable skip, and for the same reason. Generating
the task in both positions would have kept the original bug while adding a handler. Five shapes are
REFUSED rather than taken, each because it would generate something wrong rather than merely
incomplete: a target that is not an Execute SQL Task with a statement; one with more than one
incoming constraint (also reachable on the success path -- excluding it would silently DROP work
SSIS runs); one with its own successors (a failure-path chain is not modelled); a disabled one; and
a second Failure edge into the same task. `FailureHandlers` is emitted in the generated
`EtlPackage` only when there IS one, since it is an optional parameter defaulting to empty --
adding `FailureHandlers: []` everywhere would have changed every existing package's text for no
behavioural difference (caught by 7 golden/exact-string tests failing, then fixed properly rather
than by regenerating goldens).

**The stale reason text was rewritten, not left.** `UnsupportedFormReason`'s shared tail used to say
these forms need "a failure-handler position outside that transaction" -- true before this round,
misleading after it, since that position now exists. Completion / Expression-only /
ExpressionOrConstraint were all measured to fire on BOTH paths, so what they actually need is the
same task in two positions at once (an ordinary step AND a handler) -- a separate feature, and now
the reason says so. The unreachable `Value="1"` arm was deleted rather than left as dead code.

**Verified four ways:**
1. **Real SSIS ground truth, both directions.** New fixture `SyntheticFailureHandler.dtsx`
   (`failure-handler` mode): `SQL_Truncate -> DFT_Load -> SQL_Body -(Failure)-> SQL_Handler`, where
   `SQL_Body` raises an error only when a control row says so -- so the success and failure runs are
   the same fixture with one bit flipped. `DFT_Load` loads real rows BEFORE the failure, which is
   what makes the deliberate divergence visible. dtexec: success -> `DTSER_SUCCESS`, 2 target rows,
   log `(none)`; failure -> `DTSER_FAILURE`, **2 target rows still committed**, log `[handled]`.
2. **The generated code, run both ways, matching.** Generated, built against a copy of `Etl.Core`
   (0 warnings/0 errors), run against `.\SQLFORPOC_2022`, rows read back: success -> exit 0, 2 rows,
   log `(none)`; failure -> exit 1, **0 rows**, log `[handled]`. Identical to SSIS on every
   dimension except the target row count on the failure run -- SSIS auto-commits per task so its
   rows survive, while this rewrite rolls the whole load back. That is the trade `PackageRunner`
   already makes for any failure, stated rather than hidden.
3. **A byte-level baseline diff via a `git worktree` at HEAD.** Generating the 5 real packages both
   with and without `--seams`: the ONLY difference in either mode is `Package/Program.cs`, and it is
   exactly the intended move -- the `ExecuteSqlStep` local gone, the task out of `Steps`, and into
   `FailureHandlers`. All four other packages byte-identical.
4. **A corpus sweep that confirmed itself on a fixture built for something else.** The
   conditional-constraint PROBE fixture's own `SQL_Body -> SQL_OnFailure` gap disappeared, resolving
   as a handler -- independent confirmation on a package not built for this round. Corpus constraint
   gaps 7 -> 6; this PoC's own 4 packages still zero.

**Result: `Package` drops from 9 blocking gaps to 8, and every one of the 8 is now a Script seam --
Tier-2 human work, with no tool gap left in that package at all.** The portfolio still reads 3/5,
because seams are genuinely unfilled work and the readiness metric correctly counts them; filling
them (as the worked example in `D:\PoC\SSIS_From_Sandeep_Generated\fills\Package\` does) is what
makes it generatable, and the whole 5-package deliverable regenerated with `--seams` and those
fills applied builds **0 warnings / 0 errors**.

**Test coverage:** 4 new cases in `ConditionalConstraintTests` (the handler resolved AND excluded
from `Steps`, the `Program.cs` emission, and a 4-case `[Theory]` over the refused shapes). 3 new in
`PackageRunnerTests` (handlers run after rollback and the package still fails; handlers do NOT run
on success -- the case that makes the feature correct rather than merely present; a throwing handler
keeps the original error and does not stop the rest). `FakeUnitOfWork` mirrors the real
no-active-transaction precondition rather than just recording the call, which is what lets the
AFTER-rollback ordering be pinned with no database at all. One `[InlineData]` row was removed from
the unsupported-forms `[Theory]` (`Value="1"` is no longer an unsupported form) and that file's own
doc comment updated from "exactly one of the seven forms" to two. Regression: 205/205 in
`Ssis.Extract.Codegen.Tests` (was 200), 53/53 in `Etl.Core.Tests` (was 50), 149/149 in
`Ssis.Runtime.Expressions.Tests`, same 3 pre-existing unrelated failures in `Ssis.Extract.Tests`.

**Remaining known gaps, updated:** the conditional forms that fire on BOTH paths (Completion,
Expression-only, ExpressionOrConstraint) -- now precisely characterised as needing one task in two
positions, and unevidenced in the tracked portfolio; chunk 4 (provenance + staleness);
Aggregate-inside-a-Multicast-branch plus a two-output Lookup, together what
`DFT_LookupAndAggregate` needs and the only remaining tool gap in the whole portfolio; and the
pre-existing per-pairing coercion and Data Conversion target-type gaps. Failure handlers, named as
the next round by the conditional-constraint measurement, are now closed for the one evidenced
shape.

### Running the real SSIS packages and the generated rewrite side by side, and fixing three real bugs found doing it (2026-08-31/2026-09-01)

Everything above this point had been VERIFIED against real dtexec probes and real generated-code
runs on a fixture-by-fixture basis -- but the real SSIS packages (`SSIS_From_Sandeep`) and the
generated rewrite (`D:\PoC\SSIS_From_Sandeep_Generated`) had never actually been run side by
side, end to end, on the same machine. Both existed; neither had been exercised together. This
round did that for real, and it surfaced two genuine, previously-unknown bugs the fixture-level
verification had never hit (both fixtures happen to avoid the exact shapes that trigger them),
plus a third (already-flagged) real gap that was fixed as a deliberate follow-up.

**Standing up the environment.** The third party's own `docker-compose.yml`
(`SSIS_From_Sandeep/docker-compose.yml`) was used to bring up a real
`mcr.microsoft.com/mssql/server:2022-latest` container (`ssisdemo-sqlserver`, port 1433), seeded
with `SSISDemo`/`SSISDemoCache` via the repo's own `sql/*.sql` scripts. All 5 real packages were
then run for real via `DTExec.exe`, using `.dtsConfig` override files (SSIS's own external
connection-string-override mechanism -- the `DTS:Password` nodes in these third-party `.dtsx`
files are encrypted with `EncryptSensitiveWithUserKey`, tied to the ORIGINAL author's Windows
account, and are undecryptable here; a `.dtsConfig` sidesteps that entirely rather than editing
the `.dtsx`). One environment gap was found and fixed along the way, unrelated to any code:
`Package_Advanced`'s own `dbo.DripEligibility` table was never created by any of the third
party's 6 SQL setup scripts -- derived its schema directly from the package's own destination
`<inputColumn>` metadata and created it by hand, a one-time environment fix. All 5 real packages
then ran successfully, matching the README's own documented expected-results table and the third
party's own committed sample output files. A full golden snapshot of every target table/file was
captured before touching the generated code at all.

**Then the generated rewrite was pointed at the exact same, freshly-seeded databases.** 3 of 5
packages matched the SSIS baseline byte-for-byte on the first try. 2 failed, both with genuine,
previously-unknown bugs -- invisible in every earlier round's own dedicated fixture, because no
fixture built so far happened to combine a RaggedRight flat file with no delimiters (Bug 2) or a
multi-flow package whose second flow's SQL source reads a table the FIRST flow just wrote in the
same still-open transaction (Bug 1).

**Bug 2 -- `Package_Legacy`'s own `CustomerFixedImport` load, a fixed-width/no-delimiter read
CsvHelper cannot do at all.** `ClassMapEmitter` always emits CsvHelper's `.Name(...)` mapping,
which needs delimiters to split a line on -- but `CustomersFixed.txt` is genuinely fixed-width
(`Format="RaggedRight"`, `HeaderRowsToSkip="1"`, no real column-name header at all). Fixed with a
new, non-CsvHelper reader: `Etl.Core.Csv.FixedWidthRowSource<TRow>` (reads by character
position, honors `SkipRows`) plus a new codegen path,
`Ssis.Extract.Codegen.FlatFileRuntimeShape`/`FixedWidthRowReaderEmitter`, that detects this shape
(`MaximumWidth` set, `ColumnDelimiterDecoded` empty) and routes to it instead of the ordinary CSV
path. `HeaderRowsToSkip` was also wired generically into the ORDINARY delimited path
(`CsvSourceOptions.SkipRows`/`CsvRowSource`) along the way -- it had been extracted all along but
never consumed anywhere. **Verified against the real motivating package**: regenerated, built
0 warnings/0 errors, run against the real container -- `CustomerFixedImport` landed
**byte-for-byte identical** to real SSIS's own padded values (including the padding itself, not
just the trimmed content), and `legacy-audit-trail.txt` matched byte-for-byte too. 212/212 in
`Ssis.Extract.Codegen.Tests` (was 205, 6 new).

**Bug 1 -- `Package`'s own multi-flow load, a genuine, reproduced self-deadlock.** `Package.dtsx`
loads `StagingCustomers` then `SalesSummary`, the second flow's own `SqlRowSource` reading a
table the FIRST flow just bulk-inserted, all still inside the package's single open
transaction (`PackageRunner`'s own deliberate whole-package-transaction design, the improvement
over SSIS's own separately-auto-committed TRUNCATE). `SqlRowSource` owns an INDEPENDENT
connection (by design -- the same separation a real OLE DB Source's read connection has from a
destination's transactional one), so this second connection can never see the first connection's
uncommitted write: the read blocked for the full command timeout and failed with "Execution
Timeout Expired", every time, deterministically.

Two things had to be measured, not guessed, to fix this correctly:
1. **READ UNCOMMITTED alone does not work here.** A live 2-session SQL Server probe proved a
   pre-load `TRUNCATE`'s schema-stability (Sch-M) lock blocks even a NOLOCK/READ UNCOMMITTED
   reader for its own transaction's full duration -- NOLOCK only ever skips row/page locks, never
   a schema-stability one. (A plain `DELETE`+`INSERT`'s row locks WOULD have been bypassable this
   way; `TRUNCATE` specifically is what defeats it.)
2. **`sp_bindsession`/`sp_getbindtoken`** -- SQL Server's own mechanism for joining a SECOND
   physical connection to a FIRST one's exact transaction/lock context (not a snapshot, not a
   dirty read -- literally the same transaction, confirmed live and reusable across multiple
   independent follower connections) -- is what actually works, and is now wired into
   `Etl.Core.Data.SqlRowSource<TRow>` via a new, optional `IUnitOfWork? uow` constructor
   parameter (defaulting to `null`, so a Lookup preload's own already-working, transaction-free
   read is untouched) and a new `IUnitOfWork.GetBindTokenAsync`/`UnitOfWork.GetBindTokenAsync`
   (issues `EXEC sp_getbindtoken @token OUTPUT`, caches the result for the life of the
   transaction).

**A second, subtler deadlock was found wiring this in naively, and it is the one actually worth
remembering.** Fetching the bind token LAZILY, from inside `SqlRowSource.ReadAsync` itself,
still hung -- root-caused (after ruling out several isolated repro attempts and confirming via
live DMV inspection that no server-side request was even active) to a SELF-INFLICTED
connection-contention deadlock: this second flow's own `SqlRowSource` is read from WHILE the
FIRST flow's `SqlBulkCopy.WriteToServerAsync` is still actively streaming on the shared writer
connection (a destination pulls from its source as it writes) -- so a lazy token fetch issues a
SECOND command on a connection `SqlBulkCopy` is already mid-operation on, which can never
complete without MARS. Fixed by having `PackageRunner.RunAsync` pre-fetch the token ONCE, right
after `BeginAsync`, before any pre-load statement or step runs -- every later step's own lazy
call becomes a cache hit that never touches the connection again. **Verified against the real
motivating package**: `Package` now succeeds end to end, `SalesSummary` landed byte-for-byte
identical to the real SSIS baseline (7 rows); rerunning the other 4 packages afterward confirmed
no regression. 54/54 in `Etl.Core.Tests` (was 53, 2 new -- including one specifically pinning
that two steps each calling `GetBindTokenAsync` still net exactly ONE real fetch).

**Bug 3 -- found the same session, deliberately deferred at the time, then fixed as its own
round on the user's own explicit request.** `Package_Legacy`'s own final step,
`SQL_CacheSet_SecondDb`, runs `EXEC dbo.Cache_Set N'LegacyImport', N'fixed-width import
completed';` against `CM_SQL_SSISDemoCache` -- a genuinely SECOND database
(`sql/06_legacy_objects.sql`'s own comment: *"a SECOND DATABASE, so one package can span two of
them the way production's FXRate_Import ... and RORPackage do"*) -- while every other connection
manager in that package targets `CM_SQL_SSISDemo`. `ExecuteSqlTaskPayload.ConnectionName` was
already extracted (resolved from the task's own DTSID reference, slice 2); the gap was entirely
in codegen, which routed EVERY `SqlStep` through the package's one shared `IUnitOfWork`
connection unconditionally -- so this task silently ran against the wrong database and failed
there (`dbo.Cache_Set` only exists in `SSISDemoCache`).

**Framed as a design question before being built, not assumed.** The user asked whether the LLM
should take a larger role in emitting packages generally, specifically to avoid issues like this
one. Answered directly: Bug 3 is not a Tier-1/2 gap in this project's own AI-assisted-gap-filling
taxonomy (see the chunks above) -- it is Tier-3, a deterministic tool-modeling gap ("an Execute
SQL Task can target a connection manager other than the flow's primary one"), the exact category
that taxonomy deliberately routes AWAY from AI, precisely so a systemic gap gets fixed once for
the whole portfolio rather than hidden behind N one-off per-package translations. The user then
asked for, and got, the deterministic emitter fix instead.

**What real SSIS actually does here was measured before building anything, the same discipline
as every other round: no `DTS:TransactionOption` appears anywhere in `Package_Legacy.dtsx`**,
meaning SSIS runs `SQL_CacheSet_SecondDb` on its own default, autocommitted, physical connection,
completely uncoordinated with the package's other connection managers (no MSDTC escalation). So
the faithful translation is a SECOND, independent, autocommitted connection -- not an attempt to
enlist a different database in the package's own shared `SqlTransaction`, which isn't even
possible without a distributed transaction, a design this rewrite has deliberately not taken on
elsewhere (see the "Not built" parallel-execution note).

**What was built:**
- `Etl.Core.Pipeline.SecondaryConnectionSqlStep` (new): an `ILoadTask` that deliberately does
  NOT use the `IUnitOfWork` passed to `RunAsync` at all -- opens its own `SqlConnection`, runs
  autocommitted, no explicit transaction. Same independent-connection precedent
  `Etl.Core.Data.SqlRowSource<TRow>` already established on the read side, applied here to a
  write.
- `Ssis.Extract.Codegen.PackagePlanner.SqlStep` gained an optional `ConnectionManagerName` (from
  `ExecuteSqlTaskPayload.ConnectionName`, carried through unconditionally -- `PackagePlanner`
  itself doesn't know the package's own resolved primary database yet at plan time, since that's
  only resolved later, per-flow, in `PackageGenerator`).
- `PackageGenerator.ResolveSqlStep` (new): compares the task's own connection manager's parsed
  (Server, Database) against the package's already-resolved primary (same plain ordinal
  comparison `BuildSqlFlowSource` already uses for its own source-vs-destination check). A
  mismatch routes to a new `ProgramSecondaryConnectionSqlStep` instead of the ordinary
  `ProgramSqlStep`; falls back to the unchanged, ordinary path whenever there's no evidence to
  say otherwise (a dangling connection reference, or no flow ever resolved a primary
  server/database at all). Deduped by connection manager name, so two tasks against the same
  secondary database would still produce one config entry and one connection string.
- `ProjectEmitter.SecondaryConnectionRequest`/`ProjectRequest.SecondaryConnections`: a new
  per-package `"SecondaryConnections"` section in THIS package's own `appsettings.json`
  (Server/Database/AuthMode/UserId/Encrypt/TrustServerCertificate/ConnectTimeoutSeconds -- never
  a password, same rule as `TargetDatabase`; supplied via
  `SecondaryConnections:<CM name>:Password` in User Secrets/an environment variable, symmetric
  with `TargetDatabase:Password`). Unlike `TargetDatabase`, this lives in the PACKAGE's own
  config, not the portfolio-wide `Shared/appsettings.Shared.json` -- a secondary connection is
  inherently package-specific.
- `ProgramEmitter.ProgramSecondaryConnectionSqlStep`: builds one connection-string local per
  DISTINCT secondary connection manager at `Program.cs` TOP LEVEL (same reason as the Lookup
  preload's own `lookupConnectionString` -- no `ServiceProvider` exists yet), captured by the
  later per-step factory lambda.

**Verified two ways, same discipline as every other round:**
1. **The real motivating package** -- regenerated `Package_Legacy.dtsx`: its own
   `appsettings.json` now carries a `SecondaryConnections.CM_SQL_SSISDemoCache` entry
   (`Server: "localhost,1433"`, `Database: "SSISDemoCache"`), `Program.cs` constructs
   `SecondaryConnectionSqlStep("SQL_CacheSet_SecondDb", "CM_SQL_SSISDemoCache", ...)`, and the
   whole 6-project regenerated solution (5 packages + a copy of `Etl.Core`) builds
   **0 warnings/0 errors**. Run for real against the Docker container: `Package_Legacy` now
   succeeds end to end (previously the one remaining failure), `CustomerFixedImport` still landed
   8 rows correctly, and -- the actual proof -- `SSISDemoCache.dbo.CacheEntry` genuinely gained
   the row `SQL_CacheSet_SecondDb` writes (`LegacyImport` / `fixed-width import completed`),
   confirmed by querying `SSISDemoCache` directly, not `SSISDemo` (where the table doesn't exist
   at all -- confirmed via `OBJECT_ID('dbo.SyntheticSecondConnectionLog')` on the dedicated
   fixture's own equivalent table, returning `NULL`, i.e. the statement could only have succeeded
   by genuinely reaching the second database). The other 4 real packages' own `appsettings.json`
   files correctly gained NO `SecondaryConnections` section at all (grepped directly) -- the fix
   is scoped to the one evidenced case, not applied speculatively everywhere.
2. **A new dedicated fixture**, `SyntheticSecondConnectionSql.dtsx`
   (`Ssis.Extract.FixtureBuilder`'s new `second-connection-sql` mode): `SQL_PreLoad` (TRUNCATE,
   primary CM) -> `DFT_Load` (CSV -> Derived Column `LoadedAtUtc` -> OLE DB Destination, primary
   CM) -> `SQL_CacheSet_SecondDb` (INSERT, a genuinely different SECOND database on the same
   `.\SQLFORPOC_2022` instance, `SsisPoC_Secondary` -- see
   `synthetic-second-connection-sql-tables.sql`). **Generated, built against a copy of
   `Etl.Core` (0 warnings/0 errors), and actually RUN against `.\SQLFORPOC_2022`**: read back
   afterward, not trusted from the exit code -- the primary database's own
   `SyntheticSecondConnectionTarget` got its 3 CSV rows, and the SEPARATE `SsisPoC_Secondary`
   database's own `SyntheticSecondConnectionLog` genuinely gained the row the secondary step
   writes.

Two new `PackagePlannerTests` (the resolved `ConnectionManagerName`, both on the ordinary
pre-load task and the secondary one) and one new `PackageGeneratorTests`
(`Generate_RoutesAPostFlowExecuteSqlTask_ToItsOwnSecondaryConnection_WhenItTargetsADifferentDatabase`,
asserting the exact `Program.cs`/`appsettings.json` wiring and that no plain `ExecuteSqlStep` is
ever emitted for this task) plus two new `Etl.Core.Tests`
(`SecondaryConnectionSqlStepTests` -- `Name` returns the constructor value; a connection failure
propagates as a real `SqlException` and never silently falls back to running the SQL through
`IUnitOfWork` instead). Regression: 214/214 in `Ssis.Extract.Codegen.Tests` (was 212), 56/56 in
`Etl.Core.Tests` (was 54), same 3 pre-existing unrelated failures in `Ssis.Extract.Tests`.

**Remaining known gaps, unchanged by this round except for its own closure:** the conditional
forms that fire on both paths, chunk 4, Aggregate-inside-a-Multicast-branch plus a two-output
Lookup (`DFT_LookupAndAggregate`), and the pre-existing per-pairing coercion/Data-Conversion
target-type gaps. Cross-database Execute SQL Task routing, found and deferred earlier this
session as "Bug 3", is now closed for the one evidenced shape (a task on a genuinely different
database, with no `TransactionOption="Required"` anywhere to say otherwise).

### Aggregate-inside-a-Multicast-branch + two-output Lookup routing -- the last real tool gap in the portfolio, closed (2026-09-02)

`DFT_LookupAndAggregate` was the one remaining flow blocked by a genuine `ssisx generate`
limitation rather than a hand-portable Script seam. Real topology, read directly from
`Package_Transforms.dtsx` rather than assumed: `FF_SRC_Customers2 -> LKP_Country [CacheType=0;
NoMatchBehavior=1 REDIRECT; join Country->CountryName; copies Region] --(Match)--> MCAST_Matched
--(Output 1)--> AGG_ByRegion [GroupBy Region, Count(CustomerID)] --> OLEDST_RegionSummary`, with
TWO dead branches alongside the one real path: `MCAST_Matched`'s own Output 2 and `LKP_Country`'s
own No-Match output each feed a `Microsoft.RowCount` writing to a package variable
(`User::MatchedRows`/`User::UnmatchedRows`) that nothing else in the whole 5614-line package ever
reads (grepped directly, not assumed). **So only one branch in the entire flow is ever
observably live** -- confirmed via `MCAST_Matched`'s own saved XML (`synchronousInputId` set, no
separate `<outputColumns>` at all -- a pure fan-out that never assigns a fresh lineageId), which
is also why `PackagePlanner.PlanAggregate`'s existing global-lineageId-based column resolution
(built 2026-08-30) already resolved `AGG_ByRegion`'s own GroupBy/Count columns correctly straight
through the Multicast -- the ONLY thing that ever blocked this flow was `PlanMulticast`/
`ResolveBranch` treating a dead-end RowCount and an Aggregate mid-chain as unconditional gaps.

**The design that fell out of this evidence was much smaller than "N-way Multicast fan-out with
per-branch Aggregate buffering"**: recognize a dead-end `Microsoft.RowCount` (no outgoing path of
its own) as a valid, non-fatal branch terminus -- discarded, with a non-blocking advisory gap
naming the variable being dropped, never silent -- and recognize `Microsoft.Aggregate` as a valid
structural pass-through hop for `ResolveBranch`'s own forward walk (purely structural, deliberately
NOT added to `derivedColumns`, since value semantics stay entirely with the already-existing
`PlanAggregate`/`AggregateRowEmitter`/`GenerateAggregateFlow` machinery). Both gated to
`componentKind == "Multicast"` only -- Conditional Split's own "fatal for the whole flow if ANY
branch fails" invariant is untouched, since this shape is unevidenced there.

**`ConditionalSplitBranchPlan.Destination` became nullable** (`Discarded: bool = false` alongside
it), which rippled into `GenerateConditionalSplitFlow`/`GenerateMulticastFlow` needing a
null-forgiving `!` at each existing dereference (Conditional Split branches are never discarded,
by construction) and `GenerateMulticastFlow` gaining a real, generically useful defensive addition:
skip `Discarded` branches in its own per-branch loop. This is NOT the real motivating case --
`GenerateAggregateFlow`/the new composed path always wins dispatch first whenever an Aggregate
exists anywhere in the pipeline (`PackageGenerator.Generate`'s per-flow dispatch checks
`flow.Aggregate` before Multicast) -- but it IS a direct, foreseeable consequence of relaxing
`ResolveBranch`: a plain Multicast with one live branch and one discarded RowCount branch, no
Lookup, no Aggregate, would otherwise reach `GenerateMulticastFlow` and crash dereferencing a null
`Destination`. `PlanDataFlow`'s own destination selection (`multicast.Branches[^1].Destination`)
also had to change to the first NON-null destination among branches -- for the one real evidenced
shape, the discarded branch IS the last one Multicast declares, so `[^1]` alone would have picked
the wrong, null one.

**`GenerateLookupFlow`'s own classification was rewritten, not reused via `ResolveBranch`.** A
Multicast is a genuine fan-out; teaching `ResolveBranch`'s single-linear-chain-per-hop walk to
recurse into multiple branches mid-walk was unnecessary, because `flow.Multicast` is already
fully resolved by planning time. Instead: classify each of the Lookup's own routed (non-error)
outputs by a single explicit hop (discardable RowCount dead end -> advisory, continue; otherwise
the one candidate LIVE output -- more than one is still a hard gap, the composed-shape boundary
this round draws). The live output's own next hop then decides the shape: straight to the
destination (today's original, unchanged path) or into `flow.Multicast` with exactly one live
branch feeding `flow.Aggregate` (the new composed path, `GenerateLookupThenAggregateFlow`) --
anything else (Multicast leading straight to a destination with no Aggregate, or more than one
live Multicast branch) is unevidenced and gapped rather than guessed, a deliberate scope trim
from this round's own original plan once building the "straight to destination" bonus case turned
out to need its own new `FlowSourceSpec` variant rather than being free.

**One new, small, generic `Etl.Core` primitive**: `Data/FilteringRowSource.cs`
(`FilteringRowSource<TRow> : IRowSource<TRow>`, wraps another source, yields only rows matching a
predicate) -- this is what expresses `NoMatchBehavior=1`: a Lookup miss is excluded from the row
stream entirely, before anything downstream (the transform's own join indexer, or
`AggregateRowSource<TSourceRow,TKey,TRow>`, which requires `TKey : notnull` and so can never
safely group a miss under a null/sentinel key) ever sees it. No new push/flush Multicast-branch
abstraction was needed at all -- a Multicast with only one live consumer needs no fan-out
machinery; it degenerates to a plain 1:1 pass-through at runtime, so the composed flow is just
`Lookup cache -> filter misses -> AggregateRowSource -> entity -> bulk insert`, no different in
kind from the already-existing standalone Lookup and Aggregate flows, just composed.

**A real, load-bearing design correction made mid-round: filtering and the GroupBy key selector
are independent axes, not the same field.** A first draft coupled "wrap the source in
`FilteringRowSource`" to "the GroupBy key reads through the lookup cache" (`AggregateLookupKeySpec`
alone) -- wrong, because a plain PASSTHROUGH GroupBy column downstream of a redirect-on-miss
Lookup would still need the miss filtered out even when the key itself needs no lookup at all.
Fixed before it ever reached a fixture: `AggregateFlowSource` carries `LookupKey` (drives the key
SELECTOR text) and a separately-settable `LookupFilter` (drives whether the source gets wrapped at
all) as two independent optional fields, set independently by the caller -- the one real evidenced
flow happens to set both, but nothing requires that.

**`AggregateRowEmitter`'s reused `GenerateAggregateFlow` core was factored out, not
duplicated** -- a new `EmitAggregateRowEntityAndTransform` helper (row type + entity + transform +
table + auth resolution) takes an already-resolved `groupByClrType`/`innerSource` and an optional
`lookupKey`/`lookupFilter`, called by both the standalone Aggregate path (both null) and the new
composed path. `GenerateLookupThenAggregateFlow`'s own GroupBy-type resolution tries the Lookup's
own reference-column type first (via `outputToReference`, the exact same dictionary the plain
single-output Lookup path already builds from `CopyFromReferenceColumn`), falling back to the
standalone path's raw-source-column resolution when the GroupBy value isn't Lookup-derived at all
-- so a plain passthrough GroupBy downstream of this same Lookup would still resolve correctly.

**Three real bugs were caught building this, all before a fixture ever ran, and one only by
actually building the generated project** -- none guessed past:
1. **`ProgramFlowSpec.Lookup` being non-null used to mean, unconditionally, "pass the cache to the
   transform's constructor"** (`new {TransformClassName}({cacheVariable})`) -- true for the plain
   single-output Lookup shape, where the transform's own `Map()` does the join itself, but WRONG
   for the composed shape, whose transform is a plain parameterless `TrustAllPassthroughColumns`
   mapping (the cache is already fully consumed upstream, inside `AggregateFlowSource`'s own key
   selector/filter). Caught by actually building the generated project (`CS7036`/constructor
   mismatch), not by a unit test. Fixed with a new `ProgramFlowSpec.TransformNeedsLookupCache`
   flag (default `true`, so every existing Lookup flow is untouched), set `false` only by the new
   composed path.
2. **The emitted `FilteringRowSource<T>(...)` call was missing its own required `name` argument**
   -- caught the same way, a real `CS7036` on the constructor. `FilteringRowSource`'s own
   component-name literal was simply forgotten in the first emission pass.
3. **A real, previously-unencountered `Microsoft.Aggregate` validation rule, found only via a real
   `dtexec` probe, not guessed:** unlike a plain destination, Aggregate demands every one of its
   OWN mapped input columns actually be referenced by some output's `AggregationColumnId` --
   mapping every virtual input column as `UT_READONLY` (the pattern every earlier Aggregate/
   Multicast/OLE DB Destination fixture in this file already uses safely, since their own upstream
   components happen to expose only the columns actually needed) left "Country" declared-but-
   unused this time (the Multicast passes through the Lookup's ENTIRE Match output, Country
   included, and nothing downstream of the Aggregate needs it) -- `dtexec` failed validation with
   `0xC0208218` ("input column ... is not referenced")/`VS_ISBROKEN`. Fixed in the fixture builder
   by mapping only the two columns (`Region`, `CustomerID`) the Aggregate's own output columns
   actually reference -- a real, generalizable fact about `Microsoft.Aggregate`'s own object-model
   contract, not specific to this fixture.

**Verified two ways, the strongest evidence tier this project's own philosophy asks for:**
1. **A real `dtexec` probe against `.\SQLFORPOC_2022`, then the generated code run against the
   exact same seed data, diffed row-for-row.** New fixture,
   `tests/Ssis.Extract.Tests/Fixtures/SyntheticLookupThenAggregate.dtsx`
   (`Ssis.Extract.FixtureBuilder`'s new `lookup-then-aggregate` mode): the real evidenced shape in
   miniature -- OLE DB Source (Country, CustomerID) -> Lookup (CacheType=0, NoMatchBehavior=1,
   Country->CountryName, copies Region) -> Multicast -> {Output 1 -> Aggregate (GroupBy Region,
   Count CustomerID) -> OLE DB Destination, Output 2 -> dead-end RowCount}, Lookup's own No-Match
   output -> a second dead-end RowCount. Seed data: Canada x2 + India x1, all matching the
   reference table, plus one deliberately UNMATCHED row (`Atlantis`) to exercise the filter for
   real. **`dtexec` result (real SSIS, `DTSER_SUCCESS`):** `Asia=1, North America=2` -- Atlantis's
   row genuinely excluded, not grouped under a null Region and not counted anywhere.
   **Generated, built against a copy of `Etl.Core` (0 warnings/0 errors), and actually RUN against
   `.\SQLFORPOC_2022`:** **exact match**, `Asia=1, North America=2`, read back from the table
   directly, not trusted from the exit code. A second, minimal fixture,
   `SyntheticMulticastDiscard.dtsx` (`multicast-discard` mode: a plain Multicast, one live branch,
   one discarded RowCount branch, no Lookup, no Aggregate at all), proves
   `GenerateMulticastFlow`'s own defensive discard-skip independently -- generated, built (0/0),
   and run against `.\SQLFORPOC_2022`, landing both real rows correctly with the discarded branch
   contributing nothing.
2. **The real motivating package.** Regenerating `Package_Transforms.dtsx`:
   `DFT_LookupAndAggregate`'s own blocking gap is GONE entirely -- its only remaining gaps are the
   two discarded-RowCount advisories named above plus the unavoidable Notification advisory, all
   non-blocking. The WHOLE regenerated 5-package portfolio (`Package`, `Package_Advanced`,
   `Package_Exports`, `Package_Legacy`, `Package_Transforms`, plus a copy of `Etl.Core`) builds with
   **0 warnings/0 errors** in one shot. A full RUN against this real package was not attempted --
   its own source data lives in the third party's own OneDrive path
   (`D:\OneDrive\...\KT\RBC\SSIS\sample-data\Customers.csv`), genuinely unavailable in this
   environment, the same honest limitation as the Merge Join/Flat File Destination rounds' own
   real-package checks; the `ssisdemo-sqlserver` Docker container from the 2026-09-01 round is
   still up and healthy, but this flow's own SOURCE is a Flat File, not the SQL data that container
   holds, so it wouldn't have helped here regardless.

Two new `PackagePlannerTests` (discard + Aggregate-passthrough resolution for the composed
fixture, including the destination-selection fix; the plain Multicast-discard-only fixture, no
Lookup/Aggregate) and two new `PackageGeneratorTests` (the full composed wiring --
`FilteringRowSource`, the lookup-cache key selector, the parameterless transform registration --
and the Multicast-only discard-skip). One pre-existing test's own wording assertion was updated,
not its substance: `LookupFlowTests.Generate_UsesAConfirmedDecision_WhenThePackageDeclaresNoJoinKey`
asserted the OLD flat "routes 2 outputs onward" gap text for `SyntheticLookupSplit.dtsx` (a
DIFFERENT fixture whose No-Match output leads to a REAL destination, not a discardable RowCount
dead end, so it's still correctly a hard gap -- just reworded to "routes more than one output to
real downstream processing"). Regression: 218/218 in `Ssis.Extract.Codegen.Tests` (was 214), 149/149
in `Ssis.Runtime.Expressions.Tests`, 61/61 in `Etl.Core.Tests` (was 56, 5 new --
`FilteringRowSourceTests`), same 3 pre-existing unrelated failures in `Ssis.Extract.Tests`
(someone else's in-progress SSDT edits to `SSIS/LoadReferenceData.dtsx`/`SSIS/SSIS.dtproj`, not
this round's).

**Remaining known gaps, updated -- this closes the last one this file has tracked since the
2026-08-30 Aggregate/Row-Count round:** the conditional forms that fire on both paths (Completion,
Expression-only, ExpressionOrConstraint -- unevidenced anywhere), chunk 4 (provenance/staleness for
AI-assisted gap filling), and the pre-existing per-pairing numeric-coercion/Data-Conversion
target-type gaps. `DFT_LookupAndAggregate` -- Aggregate-inside-a-Multicast-branch plus two-output
Lookup routing -- is now closed. Every real, evidenced flow in the tracked `SSIS_From_Sandeep`
portfolio either generates cleanly or is blocked only on `Package`'s own 8 hand-portable Script
seams (Tier 2, already fillable via `--seams`/`ssisx apply-fills`, unrelated to this round).

### Event handler translation -- the one gap the 2026-09-02/03 audit itself left open, closed (2026-09-03)

Picked up as the direct next round after that audit's own six fixes were verified end to end
(Docker back up, real dtexec run vs. the fixed generator's own output, byte-for-byte match across
all five real packages -- see the audit's own commit history). Finding 5 explicitly left this
open: event handlers were reported once codegen started reading them, but deliberately NOT
translated, because "an SSIS event fires per event occurrence and propagates up the container
tree, which is not the same as the Failure-constraint handler this tool does generate, and the
difference has not been measured." This round measured it.

**A real dtexec probe came first, before any codegen** (`Ssis.Extract.FixtureBuilder`'s new
`event-handler-probe` mode, `SyntheticEventHandlerProbe.dtsx` -- built via the real object model,
reflected first to confirm the `Package.EventHandlers.Add("OnError")` API shape since no prior
fixture in this repo had ever built one): a package-root `OnError` handler (one Execute SQL Task,
matching `Package_Advanced.dtsx`'s own real shape exactly) plus a Sequence Container with NO
handler of its own holding a failing task, plus a second, independent root-level failing task, with
`Package.MaximumErrorCount` deliberately raised to 10 so a second, distinct failure got the chance
to actually happen instead of the run stopping after the first. **Measured, not assumed:**
- The handler fired **twice** -- once per failing task, not once per run. Raising
  `MaximumErrorCount` is exactly what let a second, independent failure occur at all.
- It propagates from a child container that declares no handler of its own up to the nearest
  ancestor that does (the Sequence's own failure, with no handler on the Sequence itself, fired
  the package-level one).
- A task ordered "on success" after a failing task correctly did not run.
- The overall package still returned **`DTSER_SUCCESS`** despite two real task failures underneath
  it -- a genuinely surprising, separately-confirmed fact: reflecting the live object model showed
  `FailParentOnFailure`/`FailPackageOnFailure` both default to `false` and `MaximumErrorCount`
  defaults to exactly `1` on a Package, a Sequence, and a plain Task alike, so a failing task's own
  container hits *its own* threshold and becomes Failure without that automatically failing an
  ancestor. Only because the PACKAGE's own threshold had also been deliberately raised (to let
  branch B run at all) did the package as a whole tolerate two real failures underneath it.

**The precondition this makes possible, and why it is not a narrowing for convenience:** at the
schema DEFAULT of `MaximumErrorCount=1` everywhere -- the case for every package in the tracked
portfolio, unmeasured elsewhere -- the FIRST failure anywhere in the tree immediately exhausts its
own container's threshold, and because this rewrite's own `PackageRunner` already aborts the WHOLE
run the instant any ONE step throws (no per-branch tolerance exists here at all), "some task
failed" and "the whole package failed" collapse into the exact same event. That is also why scoping
to the package root is not a special case: under that same default, a handler on some deeper
container could only ever fire on a run where nothing above it also failed -- which this rewrite's
whole-package-abort model already treats as "the run succeeded" -- so a non-root handler is a
genuinely different, unevidenced shape, not a generalization of this one.

**What was built, in `Ssis.Extract.Codegen/PackagePlanner.cs`:** a new `ResolveErrorEventHandler`,
resolved separately from precedence-constraint failure handlers (an event handler lives in a wholly
different part of the model, `package.EventHandlers`, never inside the ordinary executable tree)
but landing in the exact SAME `PackagePlan.FailureHandlers` list and reusing `Etl.Core`'s existing
`FailureHandlerAction`/`PackageRunner.RunFailureHandlersAsync` completely unchanged -- that runtime
already runs every entry unconditionally on ANY exception, which turns out not to be a coincidence
given the measured precondition above. Requires: exactly one live `OnError` handler at the package
root, `MaximumErrorCount` left at its default everywhere in the whole executable tree (a new check
over `ExecutableSpec.MaximumErrorCount` plus `PackageSpec.ExecutionSemantics.MaximumErrorCount`,
both already extracted, read by nothing before this), and its one live child being an Execute SQL
Task with a literal (non-expression) statement -- the exact shape `ResolveFailureHandler` already
requires for a Failure precedence constraint, reused verbatim. Anything else (more than one live
handler, a non-root owner, a non-OnError event, a raised `MaximumErrorCount`, a non-SQL child) stays
a reported gap, not guessed -- `ReportEventHandlers` now takes the set of package-root OnError
handlers `ResolveErrorEventHandler` already examined (whether it succeeded or rejected) and skips
them, so a rejected one gets exactly one precise gap instead of two conflicting ones.

**Verified two ways:**
1. **The real motivating package.** Regenerating `Package_Advanced.dtsx`: the
   `EventHandlers[OnError]` gap is gone entirely, and the whole regenerated 5-package portfolio
   (plus a copy of `Etl.Core`) still builds **0 warnings/0 errors**. Reading the emitted
   `Program.cs` directly confirms `FailureHandlers: [new FailureHandlerAction("SQL_LogOnError",
   "INSERT INTO dbo.EtlRunLog (PackageName, SourceName, EventType, MessageText) VALUES
   (N'Package_Advanced', N'(event handler)', N'OnError', N'An error was raised.');")]` -- the
   real SQL text, verbatim, from the real `.dtsx`.
2. **A real forced failure, run against the generated exe, not just built.** Pointed the Excel
   Source's own configured file at a nonexistent path so `DFT_ExcelImport` genuinely throws;
   `Package_Advanced.exe` exited 1 (the load rolled back, matching this rewrite's own established
   trade against SSIS's per-task auto-commit) and `dbo.EtlRunLog` gained EXACTLY the row real SSIS's
   own handler would write: `Package_Advanced | (event handler) | OnError | An error was raised.`
   -- read back from the table directly, not trusted from the exit code. The earlier, unmodified
   happy-path portfolio run (nothing failed) correctly logged nothing, matching real SSIS's own
   equivalent run.

Two new unit tests in `EventHandlerReportingTests.cs` (the real `Package_Advanced` shape now
resolving to a `FailureHandlerAction` instead of a gap; a raised `MaximumErrorCount` correctly
still gapping) plus one pinning the non-root boundary explicitly. One pre-existing test
(`Plan_IgnoresADisabledTaskInsideAnOtherwiseLiveHandler`) needed a one-line fix inside
`ResolveErrorEventHandler` itself: a handler whose only child is disabled must return null
SILENTLY (matching `ReportEventHandlers`'s own long-standing "declares nothing that runs" rule),
not raise a spurious rejection gap. Regression: 304/304 in `Ssis.Extract.Codegen.Tests` (was 302),
708 tests total across both repos via `scripts/Run-Tests.ps1`, all green.

**Remaining, deliberately unaddressed:** `MaximumErrorCount` raised anywhere in the tree (no real
package in the tracked portfolio does this, so it stays a named, evidence-gated gap rather than a
guessed multi-firing translation); an event handler on a non-root container; any event other than
`OnError`; a handler whose one live child is anything other than a single Execute SQL Task. None of
these is guessed at -- each was named, not silently dropped, exactly like every other gap in this
tool.

### AI-assisted gap filling, chunk 4 -- Tier-2 provenance/staleness, and Tier-1 catching up (2026-09-03)

Closes the plan's own last chunk (`peaceful-percolating-pebble.md`): chunk 2 built the staleness
concept for Tier-1 `decisions.json` months ago and this file has said ever since that "the hash
check exists and is tested, but nothing writes `EvidenceSha256` into a fill yet" -- true for BOTH
tiers. This closes it for Tier 2 (the harder half, since a Tier-2 fill is free-form `.cs`, not a
JSON dictionary keyed by GapId) and, cheaply, for Tier 1 (whose machinery only ever needed its own
packet to actually ask for the hash).

**Tier 1, closed almost for free.** `GapDecisions.ResolveLookupJoinKey`/
`ResolveEncryptedSecretAcknowledgment` already refuse a decision whose `EvidenceSha256` no longer
matches -- built and tested in chunk 2. The gap was entirely upstream: `AiPacketEmitter`'s own
`LookupResponseFormat` JSON template never asked for `evidenceSha256` at all, and
`EncryptedSecretContract`'s own step 3 ("record an acknowledgment... with `ConfirmedBy` set") never
mentioned it either. Both now instruct including it, copied verbatim from the packet's own heading
-- the machinery was always there; it just never got fed.

**Tier 2 needed a real design, since a fill file has no JSON dictionary to carry a hash in.** A
fill MAY carry a one-line comment immediately above the seam it answers:

```
// ssisx-fill: GapId=<id> Author=<name-or-email> Date=<yyyy-mm-dd> EvidenceSha256=<hash>
```

`AiPacketEmitter.CodeResponseFormat` (and the `ScriptTaskContract`/`ScriptComponentContract`
example code blocks) now instruct including it, copied verbatim from the packet's own heading.
**Per-seam, not per-file, and that distinction is load-bearing, discovered by reading a real
already-authored fill before designing anything**: `StagingCustomersTransform.Fills.cs` (this
session's own worked example, `D:\PoC\SSIS_From_Sandeep_Generated\fills\Package\`) answers FOUR
separate GapIds in one class -- a single file-level header could not carry four different hashes.

**`ApplyFillsCommand` (`Ssis.Extract.Cli`) gained provenance parsing, a staleness check, and a new
`fills-applied.json` manifest** -- written fresh every run via `StableJsonWriter`, one row
(`FillRecordSpec`: Package/FileName/Seam/GapId/Status/Author/Date/RecordedEvidenceSha256/
CurrentEvidenceSha256) per seam found in every `fills/<Package>/*.cs` file, regardless of outcome.
`FillStatus` mirrors `GapDecisionStatus`'s own shape one tier over: `Applied` (recorded hash
matches current), `Unattributed` (no provenance comment at all -- every fill written before this
feature, or a human who skipped it; NOT a refusal, since most fills predate this), `Stale`
(recorded hash no longer matches), `Orphaned` (unchanged from before -- a seam no gap asks for).

**Deliberately does NOT synthesize a header into an unattributed fill's own copy.** It would be
easy to fabricate one from `gaps.json`'s current hash, but that value was never actually checked
against by a human -- stamping it in would make an unattributed fill LOOK reviewed rather than
actually being reviewed, the exact failure this whole design exists to prevent. The honest picture
goes into the manifest instead; the source fill itself is still never written to, same lifecycle
as `decisions.json` and conformance claims.

**A real, load-bearing bug was caught verifying this against the actual `Package` fills, not
shipped from the design alone.** The first cut of `ExtractProvenance` located the seam's own
regex-match `Index` and walked `content[..declaration.Index]` backward looking for the nearest
non-blank line -- but that substring's OWN LAST "line" is a mid-line FRAGMENT (`"    private "`,
cut off right before the word `partial`), which is neither blank nor a provenance comment, so the
search stopped there and returned null on every real fill, misreporting every genuinely-provenanced
seam as `Unattributed`. Caught immediately by the new `Ssis.Extract.Cli.Tests` project's own tests
(0/6 passing on the first run), not shipped. Fixed by finding which whole LINE the declaration
starts on first, then searching only COMPLETE preceding lines.

**A second, more consequential design bug was caught only by testing against a real multi-seam
file with real content, not a synthetic one-seam fixture.** The first cut computed each seam's
`FillStatus` independently and copied the WHOLE FILE whenever `recognized.Count > 0` (i.e. at
least one seam was fine) -- so a file with three current seams and ONE deliberately-corrupted-hash
seam still got copied in full, `Fill_FullName`'s real (if stale) implementation included, and the
generated project built with **zero errors**, silently defeating the entire staleness check for
that seam. Caught by actually stamping a real fill from `D:\PoC\SSIS_From_Sandeep_Generated\fills\`
with a wrong hash and building the regenerated `Package.csproj` -- not by unit tests alone, which
had been written to expect the (wrong) partial-copy behavior. **Fixed to block the WHOLE file
whenever ANY of its seams is stale** -- there is no safe way to strip one method out of an
otherwise-fine file without risking a subtly wrong edit, and a file with even one stale seam is
not more trustworthy for containing three fine ones alongside it. The file's own fine seams then
correctly show up as still-unfilled too (the honest state of the actual generated output, not a
false "these three are done"), and rebuilding confirmed **exactly four CS8795 errors**, matching
the manifest's "1 stale + 3 still-unfilled" count precisely -- re-verified end to end after the
fix, not assumed correct from re-reading the diff.

**`PortfolioDigest` gained `GenerationGapKinds`** -- a portfolio-wide (not per-package, same
footing as the existing `GenerationGapReasons`) tally of blocking gaps by `GapKind`, computed
directly from the `GenerationGap.Kind` values `PackageGenerator.Generate` already returns in
memory (no new file I/O, no `--fills` flag added to `ssisx report` -- `Kind` was already being
carried on every gap, just never tallied). This is the plan's own "component type recurring in
Tier 2/3 is the signal to promote it into the deterministic emitter" metric. Deliberately scoped
down from the plan's literal "report AI-authored fill count per package" wording: that would need
`ssisx report` to read an arbitrary `fills/` directory of hand-authored C# off disk, a genuinely
different responsibility crossover between the survey command and the generate/apply-fills
workflow, for a benefit `GenerationGapKinds` already delivers more cheaply and with zero new
attack surface on the digest's own allow-list safety design. Run for real against
`SSIS_From_Sandeep`: **`EncryptedConnectionManagerSecret: 8, ScriptComponentColumn: 6,
ScriptTask: 2`** -- confirming the signal actually works, not just compiles: the encrypted-secret
gap alone recurs across enough connection managers in this one real portfolio to be a strong
candidate the next time this project looks for what to promote out of the AI-fill workflow.

**No `ApplyFillsCommand` tests existed at all before this round** (confirmed by the research pass
that scoped this chunk) -- `Ssis.Extract.Cli` had no test project and no `InternalsVisibleTo`.
Added both: a new `tests/Ssis.Extract.Cli.Tests/` project (mirroring `Ssis.Extract.Codegen.Tests`'s
own shape) and `InternalsVisibleTo` on `Ssis.Extract.Cli.csproj`, registered in
`SsisExtractor.slnx`. Six real-filesystem integration tests (a temp `--out` directory, real
`gaps.json`/`fills/` files -- this command's whole job is file I/O, so faking it away would test
less than doing it): a matching-hash fill applies; a stale one is refused and not copied; a
headerless one applies as Unattributed; an orphan is refused exactly as before; a mixed file (one
current seam, one stale) blocks entirely, both ending up reported unfilled; and a Script Task fill
is recognized by its own class-level provenance comment. All six caught both real bugs above
before either reached the real portfolio.

**Verified end to end, the same discipline as every other round:**
1. **The existing, already-authored real fills** (`D:\PoC\SSIS_From_Sandeep_Generated\fills\
   Package\`, written last session with no provenance comments at all) still apply cleanly,
   unmodified, as `Unattributed` -- confirmed via a fresh `ssisx generate --seams` +
   `ssisx apply-fills` run into a new temp `--out`, and the whole regenerated 5-package portfolio
   (`Generated.slnx`, plus a copy of `Etl.Core`) still builds **0 warnings/0 errors** with them
   applied -- zero regression from adding this feature.
2. **A real fill, genuinely stamped and genuinely corrupted**, not a synthetic fixture: copied
   `StagingCustomersTransform.Fills.cs` into a scratch `--out`, added a correct
   `// ssisx-fill:` comment above `Fill_FullName` (confirmed `Applied` on the next `apply-fills`
   run), then corrupted only its recorded hash (confirmed `Stale`, the whole file NOT copied, and
   -- the real proof -- rebuilding `Package.csproj` produced exactly the 4 expected `CS8795`
   errors, one per seam in that file, where a build had succeeded moments before).
3. **`ssisx report`'s new digest section**, run against the same real portfolio, produced the
   `EncryptedConnectionManagerSecret: 8, ScriptComponentColumn: 6, ScriptTask: 2` breakdown quoted
   above, serialized correctly into `portfolio-digest.json` (confirmed by reading the file, not
   assuming the console renderer's own correctness proves the export format too -- this project's
   own `KeyCount`-record fix earlier in this file exists precisely because that assumption once
   failed).

Regression: 149+156+6+304 = 615 in the `ssisx` solution (was 149+156+304 = 609 -- the 6 new tests
in the new `Ssis.Extract.Cli.Tests` project), 7+11+81 = 99 in `Etl.Core` unaffected, all green via
`scripts/Run-Tests.ps1`. No `Etl.Core` changes this round -- this is entirely a codegen/CLI-side
feature.

**Remaining, deliberately unaddressed at the time:** the plan's own "optionally link a fill to the
conformance rule(s) it satisfies" -- explicitly optional there, closed in the very next round below;
and Tier 1's `decisions.json` files still are not automatically re-stamped with a fresh hash when a
human edits one by hand (the packet now asks for it at authoring time, but nothing enforces it
afterward -- same trust model as `ConfirmedBy` itself, a human field this tool reads and never
writes).

### Linking a fill to the conformance rule it answers (2026-09-03, same day)

The plan's own explicitly-optional last item, picked as the next round when asked what to do next
-- the AI-gap-filling plan's 4 chunks were otherwise fully done, and this was the one concretely
scoped, non-speculative piece left.

**The two commands share nothing today, not even data, despite being about the exact same real-
world obligation.** A Script Task/Component's `ScriptCode` conformance rule (gate 1,
`ssisx conformance`) and its own Tier-2 gap (`ssisx generate --seams`/`apply-fills`) both ask "read
this script and port it deliberately" -- but `ConformanceCommand.cs` never references anything in
`Ssis.Extract.Codegen`'s gap machinery, and `GenerateCommand.cs`/`ApplyFillsCommand.cs` never
reference `ConformanceRuleSpec`/`ImplementationClaimSpec`. Confirmed by reading both, not assumed:
their persisted `RuleId`/`GapId` strings use genuinely DIFFERENT location conventions --
`ConformanceRulesBuilder.ScriptCodeRules` keys a `ScriptCode` rule as `SCRIPT-CODE:{RefId}` (SSIS's
own backslash-delimited executable/component path, package carried as a separate JSON field), while
a Tier-2 `GapId` is `<KIND>:<Package>:<Location>` where `Location` is an `ObjectName`- or
`{Entity}.{Column}`-shaped string. **Neither is a string-transform of the other.**

**But there IS an exact shared key, just dropped before it reaches disk.** `GenerationGap` already
carries `EvidenceRefId` (`ScriptTaskEmitter.cs`'s `task.RefId`, `TransformEmitter.cs`'s
`producer.RefId`) -- the literal same RefId value `ConformanceRulesBuilder` uses to build the rule.
`AiPacketEmitter.NewSpec` (the `GenerationGap` -> `GapSpec` conversion, chunk 1) simply never
forwarded it into the persisted `gaps.json`. Fixed with one additive field,
`GapSpec.EvidenceRefId`, threaded straight through -- and a new, single-source-of-truth
`ConformanceRulesBuilder.ScriptCodeRuleId(refId)` (both existing `RuleId = $"SCRIPT-CODE:{...}"`
call sites now call it) so `ApplyFillsCommand` computes the exact same string the rule builder
does, rather than duplicating the literal and risking the two drifting apart later.

**What `ApplyFillsCommand` does with it, and -- more importantly -- what it deliberately does
NOT do.** Every `FillRecordSpec` now carries a `ConformanceRuleId` (null except for
`ScriptTask`/`ScriptComponentColumn`). Separately, after the main seam loop, every EXPECTED seam
(not just ones a fill answered) is grouped by `(Package, RuleId)` -- several `ScriptComponentColumn`
gaps share one RuleId (same component, several columns), and the real obligation is "port the WHOLE
component," not one per column, so a group counts as answered only once every seam in it has
genuinely landed in `generate/` (i.e. is in `filledMethods`, never stale/orphaned). For each fully-
answered group, it reads that package's own claims file (read-only, default
`<out>/conformance/claims/`, same default `conformance` itself uses -- silently absent, never an
error, when nobody has run `conformance` for this `--out` at all) and reports the RuleId alongside
its CURRENT claim status. **It never writes to the claims file.** Only a human puts a
`ConfirmedBy`-equivalent sign-off on a conformance claim -- this command's entire job here is
making the connection VISIBLE, the same division of labor as every other piece of this feature
(decisions.json, fill provenance, all human-authored, `ssisx` only ever reads them).

**Verified against the real portfolio, not just unit tests.** `ssisx generate --seams` +
`ssisx conformance` (fresh claim stubs, all `Pending`) + `ssisx apply-fills` with the real,
already-authored `Package` fills (`D:\PoC\SSIS_From_Sandeep_Generated\fills\Package\`) against
`SSIS_From_Sandeep`: correctly named all **4** real `ScriptCode` rules this package's fills fully
answer --
```
SCRIPT-CODE:Package\DFT_BuildSalesSummary\SCR_ComputeCustomerTier  (2 seams)
SCRIPT-CODE:Package\DFT_LoadCustomers\SCR_CleanseCustomerRow        (4 seams)
SCRIPT-CODE:Package\SCR_NotifyAndLogProgress                       (1 seam)
SCRIPT-CODE:Package\SCR_ValidateAndLogStart                        (1 seam)
```
all correctly `Pending` on the first run. Then hand-edited one claim to `Implemented` in the real
claims file and re-ran `apply-fills`: that one rule's own report line correctly flipped to
"claim status: Implemented -- already marked Implemented" while the other three stayed `Pending`
-- and the claims file itself was confirmed untouched by `apply-fills` (the hand edit survived).
The whole regenerated 5-package portfolio (plus a copy of `Etl.Core`) still builds
**0 warnings/0 errors** -- this round touches only reporting, never generated code.

New tests (`ApplyFillsCommandTests.cs`, +3): the fully-answered case reports the RuleId and its
claim status; a PARTIALLY-filled Script Component (one of two columns) correctly reports nothing
for the group (while the one filled seam still carries its own `ConformanceRuleId` in the
manifest, a true fact independent of the group's completeness); and no claims file at all reports
`(no claims file found)` rather than a false `Unclaimed`. Regression: 149+9+156+304 = 618 in the
`ssisx` solution (was 615), Etl.Core's 99 unaffected, all green. No `Etl.Core` changes.

This closes the AI-assisted gap-filling plan's own last item -- all 4 chunks plus this optional
extra are now done.

### `Tools` reorganized for real client portability (2026-09-03)

Prompted directly by the user asking whether the extractor/generator could actually be sent to a
client and run there today. The honest answer at the time: extraction/survey/report/conformance
already could (raw-XML parsing, no GAC/SSISDB dependency, and `Run-Survey.ps1`/`Publish-Survey.ps1`
already package it as a self-contained exe) -- but `ssisx generate`'s own output could not build
anywhere except this dev machine, because every path to its one hand-written dependency,
`Etl.Core`, pointed at the sibling dev repo `D:\PoC\SSIS_Rewrite`, which will never exist on a
client site. This closes that gap and, per the user's own follow-up, tidies `Tools/` down to only
what a client actually needs.

**`Tools/Etl.Core` now exists** -- a portable copy of the runtime library (robocopy'd from
`D:\PoC\SSIS_Rewrite\src\Etl.Core`, excluding `bin`/`obj`; deliberately NOT the test project or the
hand-written `LoadEmployees`/`LoadReferenceData` reference apps that live alongside it there, since
a client needs the library `ssisx generate`'s output targets, not this PoC's own dev-time tests).
Etl.Core.csproj itself is untouched -- it has no `TargetFramework` and no `PackageVersion`-pinned
`PackageReference`s by design, since it's meant to be dropped into a tree that already supplies
`Directory.Build.props`/`Directory.Packages.props` (either the dev repo's own root files, or
`ssisx generate`'s own fixed emission via `GenerateCommand.WriteFixedFiles`). To make the *portable
copy* buildable on its own too, it got its own scoped `Directory.Build.props`/`Directory.Packages.props`
placed directly alongside `Etl.Core.csproj` (not at the `Tools/` root -- `Tools/SsisExtractor`'s own
~15 projects each set their own `TargetFramework`/`PlatformTarget` explicitly, several `net48`/`x64`
for GAC interop, and never use central package management, so a `Tools`-root-level
`Directory.Packages.props` with `ManagePackageVersionsCentrally=true` would have broken every one of
them with `NU1008`). Content is a deliberate byte-for-byte mirror of what `GenerateCommand.WriteFixedFiles`
already emits (and of `D:\PoC\SSIS_Rewrite`'s own root files) -- all three must be kept in sync by
hand; there is no single source of truth for these version numbers yet, a known, named risk rather
than a silently assumed one.

**One real trap hit writing the props files, worth recording alongside trap 18's own XML lesson**:
an XML comment containing a literal `--` (used as a plain prose dash, e.g. "net48/x64 for GAC interop
-- see Build/Ssis.Gac.props") is **invalid XML** -- `--` may only appear at the very end of a comment,
immediately before `-->`. MSBuild does not error clearly on this: `dotnet build` just reported
`error NETSDK1013: The TargetFramework value '' was not recognized`, as if the props file were
simply absent. Root-caused only via `-v:diag`, which reported "was not imported ... due to the file
being invalid" -- the generic XML parse failure, not a pointer to the actual `--` substring. Fixed
by rewording the comments to avoid double-hyphens.

**Verified two ways, not just "the file parses now":**
1. `dotnet build Tools\Etl.Core\Etl.Core.csproj -c Release` standalone -- 0 warnings/0 errors.
2. The real end-to-end path this whole change exists for:
   `Verify-GeneratedBuild.ps1 -PackageFolder ..\..\..\SSIS -EtlCorePath ..\..\Etl.Core` (run from
   `Tools\SsisExtractor\scripts\`) -- `ssisx generate` against this PoC's own real packages, Etl.Core
   staged from the new portable copy (including its own `Directory.Build.props`/`Directory.Packages.props`,
   which travel along with the copy and simply become the CLOSER props file MSBuild resolves for
   `Etl.Core.csproj` specifically inside the generated tree -- confirmed harmless precisely because
   their content matches `generate/Directory.Packages.props`'s own emission): **PASS -- every
   generated package compiled clean against the supplied Etl.Core**, 0 warnings/0 errors.

**Two forward-looking references fixed, not just this narrative note.** `GenerateCommand.cs`'s own
`Etl.Core` gap message -- text a CLIENT sees when running `ssisx generate` -- used to hardcode
`D:\PoC\SSIS_Rewrite\src\Etl.Core`, a path that will never exist off this machine; it and the
adjacent `WriteFixedFiles` doc comment now point at "the Etl.Core folder shipped alongside this tool
(Tools/Etl.Core, a sibling of Tools/SsisExtractor)" instead -- self-relative, not another
dev-machine absolute path. `GeneratedSolutionBuildTests.cs`'s own `EtlCoreProjectDir` (a TEST, not
prose) hardcoded the same absolute path and consequently silently skipped on every machine but this
one (its own class comment already warned "a skipped run proves nothing"); it now resolves relative
to `[CallerFilePath]`, matching the pattern `ExtractorRoot` already used, so the test actually runs
on any clone. `Verify-GeneratedBuild.ps1`'s own doc comment/example were updated the same way.
`Run-Tests.ps1`'s doc comment and its "Etl.Core repo not found" warning were reworded to distinguish
the two things clearly: the LIBRARY (portable, ships at `Tools\Etl.Core`, unaffected by whether
`SSIS_Rewrite` exists) from the TEST SUITE (`Etl.Core.Tests`, still only in the dev repo, since a
client has no reason to run this repo's own regression tests) -- its default `-RewritePath` is
unchanged, since the suite genuinely still only lives there.

**`Tools/Validation` moved to the repo root (`D:\PoC\SSIS\Validation`, sibling of
`Scripts`/`Sql`/`Data`)**, at the user's own suggestion after being told what it actually is: the
gate-3 golden-corpus comparison harness, built and verified against THIS PoC's own captured SSISDB
runs (`CorpusCapture` is explicitly Windows-only, drives `SsisRunner.exe` against local SSISDB) --
real substance (~400 files, its own solution and test project) but a dev-time proof that the
generator is correct, not a tool a client ever runs themselves. It didn't belong in a folder meant
to be handed to a client. Every relative path inside it needed correcting for the new depth (one
level shallower than before) -- traced by actually reading `Program.cs`'s own `ResolveRelativeToConfig`
first rather than guessing from `PackageConfig.cs`'s own doc comment (which loosely says "relative to
the repo root," but the real resolution code resolves every config path relative to the CONFIG FILE's
own directory): both `configs/*.config.json`'s `SchemaScript`/`AdditionalSchemaScripts` values, and
every `..\..\X`-style example command in `README.md`/`docs/gate3-harness.md`, needed exactly one
fewer `../` level. One exception traced through carefully and left UNCHANGED: `README.md`'s own
`ssisx report --input ..\SSIS` example assumed a DIFFERENT working directory than the rest of that
same code block already (a pre-existing inconsistency, not introduced by this move) -- verified it
was not something this move broke, so left alone rather than half-fixed.

**`Tools/README.md` (new)** documents the resulting two-folder layout (`SsisExtractor/` = the CLI,
`Etl.Core/` = the runtime dependency), a copy-paste "point this at a client's own packages" recipe,
and why `Validation/` isn't here. **Deliberately does NOT name `D:\PoC\SSIS_Rewrite` or any other
dev-machine path** -- caught by the user reviewing it: that file lives inside the exact folder this
whole change exists to make client-shippable, so it must not leak this project's own internal repo
layout. The "how to refresh the copy when the library changes" instruction moved here instead,
where it belongs (this file is this repo's own dev-ops notes, never sent anywhere):

```powershell
robocopy D:\PoC\SSIS_Rewrite\src\Etl.Core Tools\Etl.Core /E /XD bin obj
```

Run from this repo's root. Never touches `Tools\Etl.Core\Directory.Build.props`/
`Directory.Packages.props` (robocopy only pulls from `SSIS_Rewrite\src\Etl.Core`, which doesn't
have them) -- if `GenerateCommand.WriteFixedFiles`'s own emitted versions ever change, update
those two files by hand to match, since all three are independent copies with no shared source
of truth yet.

**Bash tool was completely non-functional this entire session** -- every invocation (even `echo`,
`pwd`) returned exit 1 with no output at all, from the very first call. Diagnosed as far as
possible without being able to restart anything: Git Bash itself works fine when invoked directly
from PowerShell (`& "C:\Program Files\Git\bin\bash.exe" -c "echo hello"` succeeds), and no
`bash`/`shell` override exists in any settings file (`~/.claude.json`, `~/.claude/settings.json`,
this repo's `.claude/settings*.json`) -- so the failure is in the harness's own process-spawning for
this tool, this session, not a config file this file could fix. All work this session went through
the PowerShell tool instead, prefixing `$env:PATH` with `C:\Program Files\Git\cmd;C:\Program
Files\Git\usr\bin` whenever `git` was needed.

### `--package` -- running one package (or a named few), not the whole portfolio (2026-09-03, same day)

Prompted directly by the client-env question this whole `Tools` reorg was answering: a real
portfolio is dozens of packages, `ssisx generate` should never default to running against all
of them, and the client site drives this with GitHub Copilot (not Claude) on a limited token
budget, so it needs to be told exactly what to run rather than left to explore.

**`--input` already accepted a single `.dtsx` file directly** (confirmed by reading
`PackageLoader.LoadInto` before assuming a gap existed) -- so "run just one package" was never
actually blocked, only inconvenient: it required knowing and typing the package's own full file
path rather than just its name, and there was no way to name a SMALL BATCH (2-3 packages) in one
call without either multiple invocations or falling back to the whole-portfolio sweep.

**What was added:** `PackageLoader.Load` gained an optional `IReadOnlyList<string>? packageNames`
parameter, applied via a new `FilterByName` AFTER the existing directory sweep and duplicate-name
resolution -- deliberately not resolved by turning names into file paths up front, so the exact
same load-failure/duplicate-name/build-output handling `PackageLoader` already has applies
whether or not a filter is in play. Matched against the package's own `ObjectName` (the
`.dtsx`'s own `DTS:ObjectName`, not necessarily its filename -- confirmed these usually but not
always agree while reading `PackageLoader`'s own existing duplicate-name comment). A requested
name matching nothing is recorded as a `LoadFailure` (kind `package-not-found`) and printed as a
warning -- never silently ignored, since a typo'd name silently generating zero packages would
look like "ran clean" for a package that was never touched. **Every command that loads packages
now takes `--package <name>` (repeatable, and comma-splittable in one value: `--package A,B` ==
`--package A --package B`)** -- `extract`, `graph`, `report`, `conformance`, `testgen`,
`generate`. Each command now also hard-fails (exit 2) when `--package` was given but matched
zero packages, rather than silently "succeeding" with an empty run -- none of the six had this
guard before, because the all-packages-or-nothing shape never let an empty result happen
silently until a filter could produce one.

**Verified against both real portfolios in this repo, not just this PoC's own 4 packages** --
this was pointed at deliberately, since the whole reason to add this is a real, larger, messier
portfolio (`SSIS_From_Sandeep`), not the two straight-line PoC packages:
- `ssisx report --input SSIS --recursive` (no filter): unchanged, all 4 packages.
- `ssisx generate --input SSIS --recursive --package LoadEmployees`: exactly 1 package folder
  written (`generate/LoadEmployees/`), not all 4.
- `ssisx generate --input SSIS_From_Sandeep\RBC_Demo_ETL\RBC_Demo_ETL --recursive --package
  Package_Legacy,Package_Exports`: exactly those 2 (of 5) package folders written, comma-
  separated form confirmed working on the real multi-package portfolio, not just a synthetic
  test.
- `--package NoSuchPackage`: exit 2, `warning: --package 'NoSuchPackage' matched no package...`
  followed by a hard `error:`, never a quiet zero-package "success".
- `ssisx generate --input SSIS --recursive` with no `--package` at all: unchanged, still
  generates every package (7 gaps, 4 packages processed, matching pre-existing behavior) --
  confirming the new parameter is additive and changes nothing for an existing caller that
  never passes `--package`.

Full suite still green afterward: 149+156+9+304 = 618 across `Ssis.Runtime.Expressions.Tests`/
`Ssis.Extract.Tests`/`Ssis.Extract.Cli.Tests`/`Ssis.Extract.Codegen.Tests`, same 3 pre-existing
unrelated failures elsewhere (unaffected -- this round touched none of their fixtures).

**A second, unrelated leak was caught and fixed while updating the CLI's own `--help` text for
this**: the `generate` help section's prose named `D:\PoC\SSIS_Migration` as where the
hand-written reference packages live -- literal console output a client (or Copilot) sees by
running `ssisx --help`, a much more directly-exposed leak than the earlier `Tools\README.md`/
`Directory.*.props` ones, since this text ships INSIDE the compiled `ssisx.exe` itself, not just
a doc file beside it. Rewritten to point at `Tools/Etl.Core` (this tool's own shipped copy) and
to state explicitly, in the help text itself, that generation does not attempt any verification
against real data -- see the client-facing docs below for why that needed saying plainly.

### GitHub Copilot instructions + guide, and "no verification is attempted" made explicit (2026-09-03, same day)

The other half of the same client-env conversation: the client site has GitHub Copilot, not
Claude, with a limited token budget -- meaning whatever guides it must be short enough to
actually fit in that budget and precise enough that Copilot never has to guess or re-derive
something this project already decided. `CLAUDE.md` (this file) is explicitly the wrong document
for that job -- it is this project's own multi-hundred-KB development history, addressed to a
future Claude session picking this PoC back up, not to an assistant operating the shipped `Tools`
folder on a client machine that has never seen any of this narrative.

**Two new files, both under `Tools/` (not this repo's own root -- they ship with the tool, they
are not this repo's dev notes):**
- `Tools/.github/copilot-instructions.md` -- the file GitHub Copilot auto-loads as repository
  custom instructions for any workspace opened at (or under) this folder. Short, and entirely
  rules plus a one-sentence workflow: never re-implement `ssisx`/`Etl.Core`, never run `generate`
  against the whole portfolio unless explicitly asked (use `--package`, see above), never attempt
  to verify generated code against a real database/data/SSIS run (none exist in this
  environment -- inventing one is explicitly forbidden, not just undiscussed), never hand-edit
  anything under `<out>/generate/` (disposable, regenerated freely -- Tier-2 work goes in
  `<out>/fills/` instead), never guess at a value a `GenerationGap` work packet says is missing,
  and never open `CLAUDE.md`/the plan docs/`docs/report-schema.md` (this project's own history,
  not written for it, and a token-budget trap for exactly the audience this file serves).
- `Tools/COPILOT_GUIDE.md` -- the longer reference the short file points to: what `generate`
  does and explicitly does NOT do, the full `--package` command reference, how to build what was
  generated, and a "Prompts you can paste into Copilot Chat" section (survey a portfolio, generate
  one package, generate a named batch, confirm a build, work a Tier-1/Tier-2 gap, recognize and
  correctly refuse a Tier-3 gap, and a sanity-check prompt before ever running `generate` without
  `--package`).

**The explicit "no verification" framing is a direct answer to the user's own stated constraint,
not a restatement of something already true everywhere.** `ssisx generate` never built or ran
anything on its own -- that was already true -- but nothing had ever said so IN THE CLIENT-FACING
DOCS, because every session that used this tool so far was this project's own dev environment,
which DOES have a real database and a real captured corpus (`Validation/`) to check against. On
a client site, neither exists, and without saying so explicitly, the natural (wrong) move for an
agent under-informed about that gap is to invent sample data or a throwaway database to "prove" the
generated code works -- exactly the failure this documentation update exists to head off. Stated
in three places now, deliberately redundant: `Tools/README.md`, `Tools/COPILOT_GUIDE.md`, and the
`generate` command's own `--help` text (see above) -- so it survives whichever one an agent
actually reads.

`Tools/README.md` also gained a "Running this with GitHub Copilot" section pointing at both new
files, and its own usage example was updated to show the `--package`-scoped form as the normal
case, with the whole-portfolio form kept as a clearly-labeled alternative ("only when that's
actually what's wanted").

### `--etl-core` and `HOW-TO-FILL-GAPS.md` — closing two real gaps found running this through GitHub Copilot for real (2026-09-03, later the same day)

Prompted directly by the user actually running the `--package`-scoped workflow above through
GitHub Copilot in Visual Studio and hitting two real problems, neither hypothetical: Solution
Explorer showed `Etl.Core (not found)`, and the user suspected (a suspicion that turned out to
be correct about the ROOT CAUSE, though not about what was actually happening) that Copilot
wasn't picking up gap-filling guidance at all.

**Bug 1 — `Etl.Core` genuinely wasn't copied, because the copy was a separate manual step, and
a separate manual step is exactly the kind of thing that gets silently skipped moving fast
through a checklist.** `ssisx generate` already correctly reported the gap in
`generate-report.md` ("this generator does not produce Etl.Core... copy it in by hand"), but
Copilot ran `generate` and moved on without doing the follow-up copy. Fixed by folding the copy
INTO the command itself: a new `--etl-core <path>` flag on `ssisx generate`
(`GenerateCommand.cs`), which copies the given folder (excluding `bin`/`obj`, the same exclusion
`robocopy /XD bin obj` gives, reimplemented in pure .NET rather than shelling out) into
`<out>/generate/Etl.Core/` as part of the same command that reports the gap if it's missing. A
failed copy still surfaces as a `GenerationGap`, never silently — the alternative is a
`Generated.slnx` referencing a directory nothing put there, discovered only much later as
exactly the Solution-Explorer symptom that motivated this fix.

**Bug 2 — NOT actually a missing-work-packets bug** (reproducing the exact scenario confirmed
`ssisx generate` DOES write all of them under `gaps/<Package>/`, every run) **but a real
structural gap in how Copilot ever finds out this mechanism exists.** `.github/copilot-
instructions.md` only auto-loads when the IDE's open workspace ROOT is `Tools\` itself — the
user had the GENERATED solution open directly (the natural thing to do once code exists to
build), and `gaps\`/`fills\` aren't part of any `.csproj`, so they don't even appear in Solution
Explorer to be noticed by accident. Fixed with a new self-contained `HOW-TO-FILL-GAPS.md`,
written by `ssisx generate` itself directly into `<out>` root on every run (`GenerateCommand.
WriteAiAssistantGuide`) — it travels WITH the generated output regardless of which workspace
root an IDE happens to be pointed at, filtered to only the gaps that are actually fillable
(`Tier.MissingDatum`/`MissingLogic` — Tier 3 gets no packet, by the taxonomy's own design), with
the full `// ssisx-fill:` comment format and the `apply-fills` procedure spelled out inline
rather than assumed known.

**Verified as genuinely fixed, not just plausible, from the user's own real environment** (`D:\
PoC\Tools\out`, run through GitHub Copilot, not a scripted repro here): `Etl.Core.csproj` present
after `--etl-core`; 4 real `.cs` fill files written by Copilot, each carrying a correct
`// ssisx-fill:` provenance comment; `fills-applied.json` showing all 8 seams `Applied`; the
whole generated solution building **0 warnings / 0 errors**. On the user's explicit follow-up
ask to check "to be 200% sure" the ported LOGIC (not just that it compiled) was faithful, all 4
fill files were independently read against their own work packets' original SSIS script source
and confirmed correct — including the one genuinely tricky substitution (`SCR_NotifyAndLogProgress`,
ported from VB.NET, switching the original's raw `SqlConnection` row-count query to
`ctx.Uow.Context.Set<T>().CountAsync(...)` — the SAME shared `DbContext`/transaction already used
everywhere else, which is actually SAFER than a second connection would have been, sidestepping
the exact cross-connection-visibility class of bug this project hit for real in the
`sp_bindsession` round above).

### `--framework net8.0` — supporting a client stuck on .NET 8 without a second copy of this tool (2026-09-03, later still)

Asked directly, before any code was touched: could the generator (net10.0 throughout, per the
"Rewrite skeleton decisions" memory) also target net8.0. Checked live against the real published
NuGet packages before answering "yes" — the honest constraint turned out to be narrower than a
TFM string, and worth recording exactly:

- **`Microsoft.EntityFrameworkCore.SqlServer` 10.0.11 targets `net10.0` ONLY** (confirmed via
  the live nuget.org package page, not assumed by analogy). So net8.0 support cannot be "the same
  packages, different TargetFramework" — it needs a DIFFERENT EF Core MAJOR VERSION. **9.0.15**
  (the newest 9.x, confirmed to target `net8.0` only) is what a net8.0 leg actually needs.
- Every OTHER pinned package — `Microsoft.Extensions.Hosting`/`Configuration.UserSecrets`
  (both 10.0.11), `System.Text.Encoding.CodePages` (10.0.11) — already multi-targets net8.0 for
  real (checked the same way, not assumed just because EF Core's provider does), so those stay
  unchanged regardless of which framework is chosen.

Presented this as a real design decision (pick-one-per-generate-run via a flag, vs. true
`<TargetFrameworks>net8.0;net10.0</TargetFrameworks>` dual multi-targeting on every project,
always building both) before implementing either — the user picked the flag. **New `--framework
net8.0|net10.0` on `ssisx generate`** (default `net10.0`, so omitting it changes nothing for an
existing caller). `GenerateCommand.cs` gained `EfCoreSqlServerVersionFor(framework)` (the one
version lookup that actually depends on the flag) plus `BuildDirectoryBuildProps`/
`BuildDirectoryPackagesProps` helpers, used to write BOTH the generated output's own
`Directory.Build.props`/`Directory.Packages.props` AND — after `--etl-core` copies the library
in — to OVERWRITE the copied `Etl.Core`'s own two props files with the exact same
framework-matched content, unconditionally. That last part closes a standing, previously
manually-enforced risk: `Tools/Etl.Core`'s own committed props files and `GenerateCommand`'s
emission have always needed to be kept in sync BY HAND (each side's own doc comment says so) —
now the OUTPUT side of that promise is enforced in code every run, regardless of framework or of
whether the committed source copy has drifted.

**Verified end to end, not just "the flag compiles" — three separate real generations, each
actually built, not just reported as gap-free:**
1. `LoadEmployees` (`SSIS/`) with `--framework net8.0 --etl-core Tools/Etl.Core`: emitted
   `Directory.Build.props`/`Directory.Packages.props` correctly showed `net8.0`/EF Core `9.0.15`
   in BOTH the generated output and the copied `Etl.Core`; `dotnet build Generated.slnx` — **0
   warnings / 0 errors**.
2. `LoadReferenceData` with no `--framework` at all (regression check): unchanged, still
   `net10.0`/EF Core `10.0.11`, still **0 warnings / 0 errors** — confirms the new flag changes
   nothing for the existing default path.
3. Three real `SSIS_From_Sandeep` packages together (`Package_Advanced`, `Package_Legacy`,
   `Package_Exports` — Excel Source, fixed-width flat file read, ADO NET, a secondary database
   connection, event handlers) with `--framework net8.0`: **0 warnings / 0 errors** across all
   three plus `Etl.Core`, confirming `Etl.Core`'s ENTIRE surface (not just the two-package PoC
   subset) has no net9/net10-exclusive BCL API dependency anywhere in it.

Full suite re-run after: 149+156+9+304 = 618, all green, no regression. `--help` (`Program.cs`),
`Tools/README.md`, and `Tools/COPILOT_GUIDE.md` all updated with the flag and the one-sentence
"why EF Core changes version, not just TFM" explanation, so an agent (Copilot or otherwise)
reads the real constraint rather than assuming a plain recompile would work.

**Real end-to-end verification followed immediately, on the user's own explicit "let's not
assume" push** — build success alone (above) proves the code compiles, not that it produces the
same data. Reset `dbo.Employee`/`Department`/`Designation` to a genuinely clean, verified state
(`Sql/01_CreateTarget.sql` + `Sql/04_CreateLoaderCredential.sql`, confirmed via a fresh row-count
query before touching anything — not assumed clean from a prior session's memory), then ran the
FULL real cycle twice: generate `LoadEmployees`+`LoadReferenceData` → `dotnet build` → actually
execute the built `.exe`s against `.\SQLFORPOC_2022` (via `TargetDatabase__Password` as an env
var, confirmed as the right mechanism by reading `EtlHost.cs`'s own `AddUserSecrets` call rather
than assumed from memory of an unrelated project's config precedence) → read the landed rows back
— once for `net10.0` (12/8/10 rows, captured to a file), once for `net8.0` (12/8/10 rows) after
resetting the tables again in between. **`Compare-Object` on every non-timestamp column across
all three tables came back with zero differences — the two frameworks produce byte-for-byte
identical output data, not just the same row counts.** This is the strongest evidence tier this
project's own philosophy asks for, now extended to prove `--framework net8.0` isn't just
"compiles" but is a genuinely faithful alternate target.

Same session, on the user's own explicit ask: `Tools/.github/copilot-instructions.md` and
`Tools/COPILOT_GUIDE.md` both updated so Copilot itself knows to ask which .NET version a client
runs rather than defaulting silently — a new rule 3a in the instructions file (pass
`--framework net8.0` for every `generate` call on such an engagement, not just the first one; ask
if unsure, don't guess net10.0), a new "Choosing a .NET target framework" section in the guide
with both example commands, and a new ready-to-paste prompt for the .NET-8-client scenario.

### Program.cs made a flat, readable script — `IPackageRunner`/`IEtlPackage` deleted (2026-09-04)

Prompted by a real client package this project only just looked at closely enough to notice the
problem in: `MFDBUpdate.dtsx` has **four independent top-level branches**, each its own
`Execute SQL Task (create temp table) -> Data Flow (text file -> temp) -> Data Flow (temp -> main)
-> Execute SQL Task (UpdateDates ...)`. Every one of those 16 steps used to land in the exact same
flat `IEtlPackage.Steps` list, in topological order, with nothing in the generated code — and
nothing in `generate-report.md` — showing that they were four separate, unrelated stories rather
than one long undifferentiated sequence. Reading that generated `Program.cs` cold, a human (or an
AI assistant working from it, per the `Tools/COPILOT_GUIDE.md` workflow) had no way to tell where
one branch ended and the next began without re-deriving the precedence graph by hand from the
original `.dtsx` — exactly the kind of silent information loss this project's whole "gaps not
guesses" discipline exists to prevent, just one level up from a missing column or a missing script.

**What changed:** generated `Program.cs` is no longer a data structure (`new EtlPackage(Steps:
[...])`) handed to a generic `IPackageRunner`/`PackageRunner` that lived in `Etl.Core` — it is now
a flat, sequential script that opens the transaction, constructs each step as a local variable,
and calls `await theStep.RunAsync(uow, load, ct)` on each in execution order directly, with
rollback/failure-handler logic written out in the `catch` block inline rather than delegated to a
shared runner method. Steps belonging to the same independent SSIS control-flow branch (computed
via a new `FlowGroup` — weakly-connected components over each container's own precedence
constraints, on `PackagePlanner.PackageStep`/`ProgramEmitter.ProgramStep`) are now visually
grouped under their own `// Flow N: <name> → <name>` comment heading, so `MFDBUpdate.dtsx`'s own
four branches now read as four clearly separated blocks in the generated file instead of one
16-step wall. `IPackageRunner`, `IEtlPackage`, `EtlPackage`, and `ConditionalStep` are gone from
`Etl.Core` entirely — every step type (`ExecuteSqlStep`, `DataFlowStep`, `ScriptTaskStep`, a
conditional gate, ...) is still there and unchanged in what it does; only the orchestration layer
that used to sit above them was removed.

**This is a readability/structure change, not a concurrency one — worth being explicit about
since it is easy to misread `FlowGroup` as the parallel-execution work the "Not built,
deliberately" passage above still says is unbuilt.** It is not: every step across every flow
group still runs strictly sequentially, one `await` at a time, in the same single whole-package
transaction as before. `FlowGroup` only decides where a `// Flow N:` heading goes in the emitted
text; it changes nothing about execution order or about the one-`SqlConnection`-per-`IUnitOfWork`
constraint that passage explains.

**Fully verified, not just designed.** Full suites green in both repos —
`Ssis.Extract.Tests` 156/156, `Ssis.Runtime.Expressions.Tests` 149/149, `Ssis.Extract.Cli.Tests`
9/9, `Ssis.Extract.Codegen.Tests` 319/319, `Etl.Core.Tests` 90/90, `LoadEmployees.Tests` 11/11,
`LoadReferenceData.Tests` 7/7 — plus three real end-to-end checks a green suite alone wouldn't
catch: a real run against `.\SQLFORPOC_2022` (12/8/10 rows, unchanged from before this refactor);
a real multi-branch package from the third-party `SSIS_From_Sandeep` portfolio
(`Package_Exports`) regenerated and confirmed to show 3 separate `// Flow N:` groups, not one
undifferentiated list; and a full AI-gap-fill round trip (8 seams filled, `apply-fills` → 0
warnings/0 errors, `fills-applied.json` all `Applied`) proving the seam mechanism survived the
rewrite untouched. A real FORCED failure was also run against the generated exe, not just the
happy path: the transaction rolled back and the failure handler's own row landed independently,
confirming the inlined catch-block logic reproduces `PackageRunner`'s old behaviour exactly.

### The remaining three dual-path conditional-constraint forms — Completion, Expression-only, Failure-OR-expression (2026-09-04)

The three forms the 2026-08-31 "Failure handlers" round left as gaps, closed the same way that
round's own Failure-only handler was: reusing `ResolveFailureHandler`'s existing safety scoping
(single incoming constraint, a single Execute SQL Task with a literal statement, terminal, not
disabled), now with two new fields on `FailureHandlerPlan` (`Guard`, `IsDualPosition`) so the SAME
task can be BOTH an ordinary step (success path) AND a failure-handler entry (failure path) — the
measured behaviour these three forms all share (`Value=Completion` runs on both paths
unconditionally; `EvalOp=Expression` ignores the outcome constraint entirely, same expression both
sides; `EvalOp=ExpressionOrConstraint`, scoped to its one evidenced Value — Failure — reduces to
the expression alone on success and is unconditionally true on failure).

**The real risk a pure Failure-only handler never has to worry about: double execution.** A
Failure-only task never runs in-line at all, so it can only ever fire once (in the catch block). A
dual-position task DOES run in-line on success — so if it ran there and a LATER, unrelated step
then fails, naively re-running every failure handler unconditionally (the pre-existing behaviour)
would run it a second time. Fixed with a per-dual-position-task runtime "reached" flag
(`ProgramEmitter`): declared `false` above the `try`, set `true` unconditionally the instant
execution reaches that step's own line (before its guard is even evaluated — reaching the line at
all already means everything before it succeeded, independent of what the guard itself then
decides), and checked (`if (!xReached)`) before the catch block's copy runs. This is what actually
answers "did THIS task's own predecessor fail, specifically" without tracking predecessor identity
at runtime at all.

**Same known imprecision as the Failure-only round, inherited rather than introduced:** any
failure handler in this generator (dual-position or not) fires on ANY exception anywhere in the
package, not scoped to its own true predecessor specifically — accepted there already, and the
`IsDualPosition` reached-flag only closes the ONE new failure mode dual-position adds (re-running
work that already ran in-line), not this pre-existing one.

**Verified:** 18/18 in `ConditionalConstraintTests.cs` (was 15 — 4 new, 2 rewritten since Value=2/
EvalOp=1 moved from the "unsupported" theory to positive coverage), 322/322 in
`Ssis.Extract.Codegen.Tests` overall. The three new forms have no real evidenced instance anywhere
in the tracked portfolio (`SyntheticCondConstraint.dtsx`'s own dtexec probe, from the original
2026-08-31 measurement, remains the only ground truth for all seven forms) — this PoC's own two
packages regenerate byte-identical (4 packages, 7 gaps, unchanged) as a smoke check.

**The end-to-end gap was closed the same day, in a follow-up pass.** The checked-in
`SyntheticCondConstraint.dtsx` probe deliberately has no Data Flow Task (it exists purely as a
dtexec ground-truth probe), so `ssisx generate` refuses it outright ("no Data Flow Task could be
planned") — it can never itself prove the GENERATED code, only the SSIS side. A derivative
fixture (`SyntheticCondConstraintDual.dtsx`, built via the object model, same `SQL_Body` plus the
three dual-position successors, anchored by a trivial `DFT_Load` so it actually generates) closed
that gap: generated (0 `.Constraint` gaps — only the pre-existing non-blocking `Parallelism`/
`Notification` advisories), built against a copy of `Etl.Core` (0 warnings/0 errors), and RUN
twice against `.\SQLFORPOC_2022` — once with the predecessor succeeding, once forced to fail.
**Exact match against the dtexec table above, both runs, all three forms**: on success,
Completion/Expression-only(true) ran and Failure-OR(false) correctly skipped; on failure (target
rows unaffected, confirming the whole-package rollback), all three fired via the post-rollback
`ExecuteSqlWithoutTransactionAsync` path, with Expression-only's own guard genuinely
re-evaluating true rather than assuming it. The generated `Program.cs`'s own `sQL_XStepReached`
flags were inspected directly and confirmed present, one per dual-position task.

**Remaining unsupported:** `EvalOp=ExpressionOrConstraint` paired with any `Value` other than
Failure (e.g. Completion OR expression, Success OR expression) — only the one combination measured
against real SSIS is generated; the others are plausible by analogy but not guessed at.

### The method-per-task emitter rewrite, all 8 phases (2026-09-04/05) — `Program.cs` is no longer a flat script

Motivated by a real readability failure, not a stylistic preference: reading
`Out/walkthrough/walkthrough/Package_Advanced.walkthrough.md` (svk's own human-readable
walkthrough, see the SsisValidationKit section below) beside the OLD generated `Program.cs` for
the same package, there was no way to map one to the other — `SEQ_Prepare` left no trace at all
(hoisted into a package-global pre-load list), the three components that ran CONCURRENTLY under
real SSIS were three indistinguishable `results.Add(await ...)` lines, and every source/sink was
built inside a `builder.Services.AddScoped<IRowSource<T>>(sp => {...})` DI lambda far from where
it actually ran. Full design in `Docs/Emitter-Rewrite-Plan.md`; user-confirmed acceptance bar was
"the smallest possible unit of work per SSIS component, each one independently testable and
verifiable end to end" — driving every design choice below.

**What changed, per phase:**
1. **`CodeWriter`** (indent-aware `Line`/`OpenBlock`/`CloseBlock`/`Indent`/`Dedent`) replaces
   `ProgramEmitter`'s old hand-tracked `"    "` indent strings. `PipelineFlowOrder`/
   `ComponentInsight` moved from `Tools/SsisValidationKit/src/SvkCore` into `Ssis.Extract.Dtsx` so
   the new emitter and svk's own walkthrough builder share the identical component numbering —
   a walkthrough row and a generated method are the same number by construction, not by
   convention.
2. **`PackagePlanner`**: `PackageStep` gained `ContainerPath` (a Sequence Container is no longer
   silently flattened) and `Wave` (0-based topological level within a container, from
   `ControlFlowDagSpec.ParallelLevels` — two steps sharing a Wave had no precedence constraint
   under real SSIS). Pre-load hoisting is GONE entirely — every step, including a pre-flow
   Execute SQL/File System Task, is now an ordinary step at its true position. The now-redundant
   `ReportParallelismFlattening` advisory (and its whole test file) is deleted, since parallelism
   is generated for real now rather than merely noted.
3. **`PackageClassEmitter`** (new, replacing most of the old 1200+-line `ProgramEmitter`): one
   `internal` method per SSIS task/pipeline component, named and ordered exactly like the
   package's own control flow — a Sequence Container becomes a method that calls its own children
   in order (`SEQ_Prepare`), every source/sink is constructed DIRECTLY inside the method that uses
   it, no DI-lambda indirection. `Program.cs` itself shrinks to ~10 lines of bootstrap
   (`EtlHost.Create` → construct the package class → `RunAsync`).
4. **Per-component methods, `internal` not `private`** — deliberately, so the generated
   `{Package}.Tests` project (`InternalsVisibleTo`) can call any one of them in isolation. A
   component folded into a transform (Derived Column, Data Conversion, Union All, Sort, Row Count)
   gets no method of its own — named in the owning method's doc comment instead, honest rather
   than pretending finer granularity than actually exists.
5. **`TestProjectEmitter`/`TransformTestEmitter`/`SqlStatementBuilderEmitter`/
   `SqlStatementTestEmitter`/`ColumnExpressionBuilders`** (new): starter `[Fact]`s per component —
   an Execute SQL Task's own `{Task}Statement.BuildStatement()` gets a text-assertion test with no
   database needed, a transform gets one assertion per output column (unchanged from before this
   round, just now emitted via the shared `ColumnExpressionBuilders` rather than duplicated logic).
6. **File paths from `appsettings.json`, not absolute literals** — a File System Task/ForEach
   folder now resolves through `File(key)` (`FileSourceOptions`) the same way a CSV source already
   did, closing a real portability gap (`Package_Advanced`'s old `Program.cs` had the third
   party's own `D:\OneDrive\...` path baked in three times).
7. **Real concurrency** — the biggest behavioral change of the eight. Contiguous same-Wave root
   steps of width 1 stay sequential inside the one ambient transaction, exactly as before; a Wave
   of width > 1 becomes a genuine `Task.WhenAll`, each branch in its OWN transaction via a new
   `RunBranchAsync` helper (fresh `AsyncServiceScope` → fresh `IUnitOfWork` → its own
   `BeginAsync`/bind-token/commit-or-rollback). The ambient transaction is committed immediately
   before a concurrent wave and reopened immediately after (a `uowActive` flag, declared OUTSIDE
   the `try` so the `catch` can see it, guards the ambient rollback for the case where the ambient
   transaction isn't active when a branch fails). This surfaced one real, previously-latent
   `Etl.Core.Data.UnitOfWork` bug, found only by an actual live run, not a unit test:
   `CommitAsync`/`RollbackAsync` never cleared `_tx`/`_bindToken`, so re-`BeginAsync`ing the SAME
   instance (needed to reopen the ambient transaction after a wave) threw "A transaction is
   already active." Fixed by clearing both fields after commit/rollback, mirrored into
   `D:\PoC\SSIS_Rewrite`. `PackageVariables` is now `ConcurrentDictionary`-backed (single-operation
   safety only, not cross-branch read-modify-write — no tracked package needs that). `_results`
   becomes a `ConcurrentBag<StepResult>` only for packages that actually have concurrency.
8. **Task-level seams** — needed ZERO code changes: a Script Task already generates as its own
   class with a `partial RunScriptAsync` seam, constructed inline from its owning task method, a
   shape entirely independent of how `Program.cs` itself is structured. Verified rather than
   assumed: regenerated `Package.dtsx` with `--seams`/no fills → exactly 8 `CS8795`s (one per
   unfilled seam); applied the existing `fills-library` fills → 0 warnings/0 errors.

**Verified against the plan's own bar, not just "it builds":**
- Regenerated all 5 real `SSIS_From_Sandeep` packages + this PoC's own 2 with `--seams
  --etl-core --fills` → **0 warnings/0 errors**, `fills-applied.json` shows all 4 existing
  `Package` fills still applying cleanly (Script Task seam signature unaffected by the rewrite).
- **Concurrency proven real, not cosmetic**, against `.\SQLFORPOC_2022` with debug+timestamped
  logging on `SyntheticFlatFileDestination.dtsx` (3 independent root flows): 5 separate
  `BeginAsync` calls total (ambient-before/3 branches/ambient-after), two branch transactions
  began within 1.3ms of each other, and one branch's own work genuinely overlapped another's
  completion in wall-clock time.
- **Partial-failure semantics proven live**: broke one branch's own output path via
  `appsettings.json` only (no code/`.dtsx` change) and reran — exit code 1, the failing branch's
  file never written, the TWO SIBLING branches' already-committed work stood (their own CSV file
  and SQL row both present, read back from the database, not trusted from the exit code), and the
  generated code's own `if (uowActive)` guard confirmed the ambient rollback was correctly
  skipped since the ambient transaction wasn't active at the failure point. One honest gap: no
  fixture combines real concurrency with an `OnError` event handler, so that specific combination
  (does the handler still fire when a concurrent sibling fails) remains unevidenced.
- **Live no-regression run**: `LoadEmployees`+`LoadReferenceData` against `.\SQLFORPOC_2022`
  (both sequential, no concurrency) — still 12/8/10 rows, byte-identical golden files confirm the
  rewrite changes nothing observable for a package with no parallel waves.

**Two real bugs found AFTER all 8 phases were "done," by actually reading the generated output**
(the user's own review, not a test) — worth recording as a reminder that a green suite proves the
emitter didn't regress its OWN prior behavior, never that newly-generated code is actually clean:
1. **A Sequence Container's own method body was indented at the WRONG level** (4 spaces instead
   of 8, matching the class-body level rather than one level deeper) — `PackageClassEmitter`'s
   `ContainerNode` case built its own nested `CodeWriter` (`body`) with only ONE `Indent()` call,
   then spliced its lines into `methods` via `RawLine`, which bypasses `methods`'s OWN current
   indent entirely (by design, for splicing an already-fully-indented writer's output — see
   `CodeWriter.RawLine`'s own doc comment) rather than adding to it. Every OTHER top-level writer
   in this file (`run`, `methods` itself) already bakes in its correct ABSOLUTE indent up front for
   exactly this reason; the container case's `body` writer just had the wrong number of levels
   baked in. Fixed with a second `body.Indent()` call (mirroring `run`'s own two-`Indent()`
   pattern, same comment shape), so `body`'s baseline now correctly matches wherever `methods`
   will be sitting (level 2: "inside the class" + "inside this specific method's body") at the
   point it gets spliced in.
2. **Every source method declared an unused `IUnitOfWork uow` parameter**, even a file-based
   source (Excel/CSV/fixed-width) that never touches it — `EmitSourceMethod` hardcoded
   `(IUnitOfWork uow)` unconditionally, reasonable for a SQL source (which binds its own
   connection to the package's active transaction, see `SqlRowSource`'s own doc comment) but a
   real, visible wart on every other source in code meant to be read and reviewed. Fixed with a
   new `SourceNeedsUow(FlowSourceSpec)` (recursive — a Merge Join/Union/Aggregate needs `uow` only
   if ONE of its nested sides does) driving both the method's own declared signature and every
   call site's argument list (`SourceCallArgs`), so `EXCEL_SRC_Drip()` and `OLESRC_CustomerIds(uow)`
   each declare exactly what they use, never more.

Both fixed, verified against the real motivating case (`Package_Advanced.dtsx`, whose `SEQ_Prepare`
and `EXCEL_SRC_Drip` were the exact two examples that surfaced this): regenerated, both defects
gone, `dotnet build` still 0 warnings/0 errors. Two golden files (`LoadEmployees.cs.txt`/
`LoadReferenceData.cs.txt`, both flat-file-sourced, so both had the same unused-`uow` wart) patched
to match; two `PackageGeneratorTests` (the Excel-source assertions) updated the same way. Full
suite green afterward: 315/315 in `Ssis.Extract.Codegen.Tests`.

**Also fixed the same session, unrelated to the emitter itself but caught reviewing what to
commit:** moving `Migration-Validation-Plan.md`/`Phase0-Extractor-Plan.md`/
`PoC-Architecture-Overview.md` into a new `Docs/` folder (alongside the new
`Emitter-Rewrite-Plan.md`) broke 7 relative Markdown links across `Tools/SsisExtractor/README.md`,
`Tools/SsisExtractor/docs/{conformance,gate2,report,spec}-schema.md`, `Validation/README.md`, and
`Validation/docs/gate3-harness.md` — each needed one more `../Docs/` segment. Fixed, not left as a
silent regression from an otherwise-unrelated repo tidy-up.

**Not built:** an `OnError` handler firing correctly when a CONCURRENT sibling branch fails (no
fixture combines both), and true concurrency across `Etl.Core.Data.SqlRowSource` connections that
share a bind token beyond the two known-safe shapes already measured (`sp_bindsession`, see the
2026-09-01 round above) — nothing new needed this round, but nobody has stress-tested a wider wave
count than 3.

### Generated-tests plan (`Docs/Generated-Tests-Plan.md`) — Phase 2 closed, Phase 3 built (2026-09-06)

Phases 1/2 (foundation + per-component deterministic tests — `TestDoublesEmitter`/
`ComponentTestEmitter`/`SampleDataEmitter`/`PackageReadmeEmitter`, every taxonomy row generating
a starter test) were built in earlier sessions; see the plan doc itself for that history. This
session closed the one row Phase 2 had left deferred, then built the whole of Phase 3.

**Phase 2's last row, "Secondary-connection SQL," closed.** The taxonomy asks for "assert it
never touches the passed `uow`," mirroring `Etl.Core.Tests`' own `SecondaryConnectionSqlStepTests`
— a comment in `SqlStatementTestEmitter.EmitStepTest` had deferred this to "an Integration-tagged
test, a later phase," assuming it needed a real server. It doesn't: `TestDoublesEmitter`'s
`PackageHarness` now wires a deliberately UNREACHABLE, fast-failing `SecondaryConnections:<CM>`
config entry (`(local)\NonexistentInstance12345`, 1s timeout — the identical value the Etl.Core
test already uses), so `SqlStatementTestEmitter.EmitSecondaryConnectionStepTest` asserts a real
`SqlException` propagates and `uow.ExecutedSql`/`BeginCalled` stay untouched, with **no
`[Trait("Category", "Integration")]` needed at all** — stronger than the deferred plan. A real
regression was caught before it shipped: the first pass added `using
Microsoft.Extensions.Configuration;` unconditionally, breaking the golden files for every package
with NO secondary connection (an unused using) — fixed by gating the `using` the same way the
config body itself is gated. Verified end to end: generated `SyntheticSecondConnectionSql.dtsx`,
built (0/0), ran green, deliberately corrupted the assertion to red, reverted to green; also
regenerated this PoC's own `LoadEmployees`/`LoadReferenceData` (no secondary connection) to
confirm zero stray `using`. **A second, genuinely pre-existing, unrelated drift was found and
fixed the same session**, caught only by reviewing a routine `SSISX_REGENERATE_GOLDEN=1` diff by
eye (the standing rule, not skipped): `LoadEmployees.cs.txt`/`LoadReferenceData.cs.txt`'s own
golden files were stale relative to the ALREADY-CURRENT `PackageClassEmitter.cs` (missing the
`uowActive` try-block pattern from the emitter rewrite's own concurrent-wave handling) and their
own `.csproj.txt` goldens were missing the `TestData\**` item groups from Phase 1's own Tier-B
wiring — neither caused by this session's changes, confirmed by grepping the current emitter
source directly before trusting the regeneration. Final confirmed state:
**347/347** (337 fast unit tests + all 10 real-build `GeneratedSolutionBuildTests` cycles), zero
regressions.

**Phase 3 — the AI tier.** `GapKind.TestOracle`/`LocalFileSourceData` added to
`Ssis.Extract.Model.Analysis.GapSpec` (both reuse `GapTier.MissingDatum` per the plan's own
explicit "no new tier" decision — that tier already grants a work packet while staying
non-blocking, with zero changes to `GapTier` itself). `GapSpec` also gained
`ExpectedFileName` — the REAL `TestData/` file name a `LocalFileSourceData` fill must use,
resolved from the connection manager's own configured `SourceFileName`
(`appsettings.Development.json` only ever overrides `SourceFolder`), which is frequently
DIFFERENT from the Tier-A synthetic sample's own `{FileSourceKey}.csv` name — this distinction
is exactly why `ExpectedFileName` had to be its own field rather than reusing `Location`.

**Where the two new kinds get produced, three call sites, all non-blocking:**
- `RouterTestEmitter`'s own two pre-existing fallback gaps (a Conditional Split case the
  oracle-based evaluator can't resolve to boolean for a representative row) are now classified
  `GapKind.TestOracle` with `EvidenceRefId` pointing at the split component — this needed NO new
  fallback logic, just classifying what already existed.
- `ScriptTaskEmitter`/`TransformEmitter`'s Script Task/Script Component seam gaps each gained a
  COMPANION `TestOracle` gap at the SAME `Location` (a different `GapId` KIND prefix keeps the
  two distinct with no collision) — "a filled seam is human logic; a test for it is a
  test-oracle packet," per the taxonomy table's own wording, is a literal second work item, not
  folded into the seam's own packet.
- `PackageGenerator.Generate`'s existing CSV/fixed-width Tier-A-sample loop and Excel-source
  loop each gained a `LocalFileSourceData` gap alongside what they already emit — always
  reported, regardless of whether Tier-A synthesis itself succeeded (CSV/fixed-width) or is
  simply unavailable (Excel has no `.xlsx` writer at all, so this is its ONLY path to real
  Integration-tested data, not an upgrade).

**`AiPacketEmitter` gained the two contracts/evidence builders/response formats**, plus ONE new
shared constant, `PinnedTestingApiBlock` (`FakeUnitOfWork`'s public surface, `PackageHarness`
usage, the `Category=Integration` convention, "read the package README for signatures") —
embedded verbatim in the `TestOracleContract` and copied by hand into
`Tools/COPILOT_TESTING_GUIDE.md` (a markdown file has no way to literally share a C# constant;
kept in sync by discipline, the same risk class as every other doc in this project).
`TestOracleEvidence` dispatches to one of three shapes purely from what `EvidenceRefId` resolves
to (a Script Task, a Script Component, or a Conditional Split) — no new gap-shape field needed.
`LocalFileSourceDataEvidence` resolves two ways: by connection-manager `RefId` for CSV/fixed-width
(straight from `FlatFileFormatSpec`), or by COMPONENT NAME for Excel (no per-generation
connection-manager thread exists for it, but `BuildExcelFlowSource` already derives its own
`FileSourceKey` — the gap's `Location` — directly from the Excel Source component's own `Name`,
so searching for a component with that exact name is sufficient). A real formatting bug (a
doubled-underscore Markdown artifact, `,__not`, from a naive string-concatenation split across an
italic span) was caught reading the actual rendered packet, not assumed correct from the code —
fixed, not left in a shipped packet.

**`ApplyFillsCommand` gained routing for two new subfolders**, structurally different from the
existing seam mechanism (a whole new file, never a splice into one that already exists):
`fills/<Package>/Tests/*.cs` (TEST-ORACLE) is copied to `generate/<Package>.Tests/Fills/`,
matched ONLY by its own `// ssisx-fill:` comment — expected as the file's FIRST non-blank line
(a new `ExtractLeadingProvenance`, distinct from the seam-relative `ExtractProvenance`) — since
there is no seam NAME to fall back on the way a Script Component/Task fill has; a file with no
comment, or a `GapId` this package doesn't currently have, is `Orphaned`, not `Unattributed` —
a real, deliberate asymmetry from the seam mechanism, stated in the response-format contract
text. `fills/<Package>/TestData/*` (LOCAL-DATA) is copied to `generate/<Package>/TestData/`,
matched purely by FILENAME against `GapSpec.ExpectedFileName` — a raw data file has no universal
comment syntax, so there is genuinely no staleness check possible here, stated plainly in the
packet's own contract rather than silently missing; `Applied`/`Orphaned` only. A stale
TEST-ORACLE fill still bumps the exit code to 3 (same as a stale seam) and is correctly excluded
from "still open" (mirroring the pre-existing `stillUnfilled`/`staleMethods` exclusion) — caught
live during end-to-end verification, not designed up front: a first pass double-counted a stale
gap in both sections.

**Verified end to end, the real acceptance bar** (`SyntheticScriptTaskSeams.dtsx --seams`, a real
package with 2 Script Task seams + 1 CSV source): both TEST-ORACLE companion gaps and 1 LOCAL-DATA
gap appeared in `gaps.json` with real evidence and packets; wrote a real `.cs` fill under
`fills/<Package>/Tests/` (matching provenance) and a real `.csv` fill under
`fills/<Package>/TestData/` (matching `ExpectedFileName`); `apply-fills` reported both `Applied`
and copied them to the right place; corrupting the TEST-ORACLE fill's `EvidenceSha256` reported
`Stale` and did NOT copy it (confirmed the file was genuinely absent from `Fills/`); a
mismatched-name `TestData/` file reported `Orphaned` and was NOT copied. Unit-test coverage added
alongside: `GapIdentityTests` (tier mapping for both kinds, a companion-gap-shares-a-Location
collision check), `AiPacketEmitterTests` (real router/CSV-schema evidence from real fixtures,
`ExpectedFileName` resolution), and 7 new `ApplyFillsCommandTests` (Applied/Stale/Orphaned for
both new subfolder kinds, including a gap with no derivable `ExpectedFileName` at all being
permanently unmatchable, by design). Regression: 344 in `Ssis.Extract.Codegen.Tests` (was 337),
16 in `Ssis.Extract.Cli.Tests` (was 9), `Ssis.Extract.Tests`/`Ssis.Runtime.Expressions.Tests`
unaffected (156/149); `GeneratedSolutionBuildTests`' own real generate+build cycle across 10
fixtures still green — nothing about production-code SHAPE changed this round, only gap
classification and reporting.

**Docs updated to match**: `Tools/.github/copilot-instructions.md` gained rules 10/11 (read the
package README before writing a test; a TEST-ORACLE packet is self-contained) and rule 4 gained
an explicit carve-out for `LocalFileSourceData` (supplying a realistic sample file is NOT the
"inventing input to fake a run" rule 4 otherwise forbids — a real, necessary clarification, since
the literal old wording would have told Copilot to refuse the one gap kind whose entire job is
exactly that). "Working a gap"'s Tier-1 section now branches by the THREE different sub-shapes
(decisions.json / `Tests/` / `TestData/`) instead of assuming one. `Tools/COPILOT_GUIDE.md`
gained a "Testing the generated code" section and two new ready-to-paste prompts, and its own
stale "only a plain SQL-destination flow with a Derived Column gets tests" sentence (true when
first written, false since Phase 2 widened coverage to every taxonomy row) was corrected.
`GenerateCommand.WriteAiAssistantGuide` (`HOW-TO-FILL-GAPS.md`) gained a `Kind` column and the
same three-way Tier-1 split. New `Tools/COPILOT_TESTING_GUIDE.md` — the deeper reference, meant
to be read rarely, since a `TEST-ORACLE` packet already embeds its own pinned API block verbatim.

**`--skip-tests` (the plan's own "Opt-out," flagged as missing, then built the same session).**
Grepped first, confirmed genuinely never implemented in any earlier session despite being in
Phase 2's own plan scope. Threaded as a new optional `PackageGenerator.Generate(..., bool
skipTests = false)` parameter, deliberately implemented as ONE cut point rather than guarding
dozens of individual `testFiles.Add(...)`/emitter call sites scattered through the ~2800-line
method: right after the Notification gap is added (after every other gap for the package already
exists) and right before the README/final result are built, `if (skipTests) { testFiles.Clear();
gaps.RemoveAll(g => g.Kind is GapKind.TestOracle or GapKind.LocalFileSourceData); }` — this is
what let the README (which reads the same `testFiles`/`gaps` lists) and the final
`SiblingFiles`/`Gaps` both correctly reflect "no tests" with a single change, rather than two
separately-maintained cut points that could drift apart. `appsettings.Development.json` is
deliberately UNCHANGED either way — it also serves a real, non-test `dotnet run --environment
Development`, which `--skip-tests` has no opinion about. `Generated.slnx`'s own existing
`.Where(r => r.SiblingFiles.Any(f => f.RelativePath == "{Package}.Tests/{Package}.Tests.csproj"))`
filter (already conditional, built in an earlier session) needed NO changes at all — an empty
`SiblingFiles` list naturally produces no `<Project>` entry, confirmed by reading the emitted
`.slnx` directly rather than assumed. Verified end to end on a real fixture with both a Script
Task AND a CSV source (so both companion-gap-removal paths are exercised, not just one):
`--skip-tests` genuinely omits the whole `{Package}.Tests` directory and its own
`TestOracle`/`LocalFileSourceData` gaps while leaving the UNRELATED `ScriptTask` seam gaps
completely untouched; the generated solution (a clean package, `LoadEmployees`) builds
**0 warnings/0 errors** with `--skip-tests`. `ssisx --help`'s own `generate` section was
corrected in the same pass — it had the SAME stale "only a plain SQL-destination flow with a
Derived Column gets tests" sentence `COPILOT_GUIDE.md` had, now describing the real, full
taxonomy. One new unit test (`Generate_OmitsTheWholeTestsProject_AndItsOwnTestOracleAndLocalDataGaps_WhenSkipTestsIsSet`),
asserting the sanity check (tests ON genuinely produces both gap kinds — the test isn't vacuous),
the omission, the untouched `ScriptTask` count, and the specific README content difference (a
real generated test file name present/absent, not a loose ".Tests" substring check — the first
version of this assertion was wrong, since README's own static prose mentions "`{Package}.Tests/`"
regardless of whether any test was actually generated for that specific package). Regression:
345 in `Ssis.Extract.Codegen.Tests` (was 344 immediately after Phase 3's own gap-kind work, +1
here for this round's own test).

**Not built this round, deliberately out of scope for Phase 3 itself:** Phase 4 (shipping `svk`,
the `/ssisx-walkthrough`/`/ssisx-test`/`/ssisx-rewrite` prompt files, the one-command pipeline).

**Known, deliberately-deferred anomaly, noted rather than chased mid-phase (2026-09-06):**
`GeneratedSolutionBuildTests` (10 real `ssisx generate` + `dotnet build` cycles) took 1.46 minutes
run in isolation, but **2h31m** twice in a row when run as part of the whole
`Ssis.Extract.Codegen.Tests` project alongside 337+ other fast unit tests, in the same session,
same code, no failures either way (always 10/10). Process inspection mid-run showed near-zero CPU
time despite high wall-clock time across every spawned `dotnet.exe`/`testhost.exe` -- pointing at
each fixture's own implicit NuGet restore stalling on something slow (a network call to a package
feed, most likely) rather than the build itself being expensive; not root-caused further. The
user asked to note this and investigate once every `Docs/Generated-Tests-Plan.md` phase is done,
not mid-phase -- see memory `ssis_generatedsolutionbuildtests_slow_runs` for the full diagnostic
detail and where to pick it up.

**Anomaly investigated and ACTUALLY fixed 2026-09-06, next session, before Phase 4 started (per
the user's own explicit request) -- in three real, independent rounds, the first two of which
looked conclusive and were not.** Worth recording the false starts, not just the ending, since
each one was believed at the time and each one turned out to be a partial answer:

1. **First theory, believed and later shown incomplete: "concurrent dotnet contention."**
   Isolating the full 355-test project with nothing else running reproduced the fast baseline
   (1m21s). Deliberately running 3 background loops of continuous `ssisx generate`+`dotnet build`
   alongside it reproduced a real, severe slowdown (14+ minutes and climbing). This looked like
   the whole story and was written up as root-caused. **It was not**: a LATER supposedly-isolated
   run (confirmed zero other dotnet processes beforehand) also hung, for 27+ minutes, disproving
   "concurrent EXTERNAL activity" as a complete explanation.
2. **Two real bugs found and fixed from that contradiction, both genuine, both necessary but
   NOT sufficient on their own:**
   - `GeneratedSolutionBuildTests.Run()`'s helper called `process.StandardOutput.ReadToEnd()`
     then `process.StandardError.ReadToEnd()` SEQUENTIALLY -- the textbook `Process`
     stdout/stderr deadlock/backpressure risk (Microsoft's own docs warn against exactly this
     pattern): if the child fills the OS pipe buffer on the stream not yet being drained (a real
     trigger was caught live: ~2.5KB from 10 MSB3026 locked-file retry warnings alone), the child
     blocks on that write while the parent is still blocked reading the other stream. Fixed with
     concurrent `ReadToEndAsync()` on both streams.
   - `WaitForExit(int)` does **not** kill the process when its timeout elapses -- confirmed this
     was leaving stale, lock-holding `testhost`/`dotnet build` processes alive for 25+ minutes
     past when they should have exited, causing a LATER, unrelated build to fail with
     MSB3026/MSB3027 (a real build failure was caught live from this). Fixed: `Kill(entireProcessTree:
     true)` on timeout.
   - Also added a `-p:RestoreSources="<local global-packages folder>"` override scoped to these
     spawned `dotnet build` calls ONLY (never the shipped `ssisx generate` output or any
     client-facing doc/config) -- every package version these 10+1 fixtures need was already
     cached locally, so this removes any NuGet-restore network dependency from the test entirely,
     regardless of whether that was ever the actual cause.
   - **Isolated `GeneratedSolutionBuildTests` alone was now reliably fast (1m28s, 10/10) -- but
     the full 355-test project was STILL slow (1h50m+ before being stopped) even with these two
     real fixes in place.** Genuinely surprising, and reported honestly as still-unsolved rather
     than declared fixed a second time on partial evidence.
3. **The actual mechanism, found via live process/file forensics, not further guessing:** a
   verbose run showed the FIRST fixture's own build had visibly FINISHED (every output file's
   mtime static for 90+ seconds, `bin`/`obj` fully populated) while the outer test method was
   still blocked. `Get-CimInstance Win32_Process` on the live, stuck run showed real
   `/nodemode:1 /nodeReuse:true` MSBuild server-node processes -- MSBuild's own PERSISTENT
   background build-server feature, which can inherit the redirected stdout/stderr PIPE HANDLES
   from the `dotnet build` process that spawned it and then **outlive** that process, still
   running as a detached background service. If that persistent node held the write end of the
   pipe open, `ReadToEndAsync()` would never see EOF and would block indefinitely -- exactly
   matching the observed symptom (near-zero CPU, high wall-clock, on a build that had already
   finished writing its own output) -- and exactly explains why the concurrent-dotnet-loops
   experiment "reproduced" it (more concurrent builds meant more chances to spin up/reuse such a
   node) without that being the true mechanism. **Fix:** added `-nodeReuse:false` to both spawned
   `dotnet build` calls, forcing a transient, non-reused MSBuild node for each one specifically so
   its own process lifetime -- and its own pipe handles -- can never outlive it.

**Correction (2026-09-06, found by an independent review commissioned later the same day -- see
"Independent three-part review" below): this write-up is itself incomplete.** The shipped fix
carries a FOURTH mechanism this list never mentions:
`[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]` at the top of
`GeneratedSolutionBuildTests.cs`, with its own comment stating THIS -- not `-nodeReuse:false`
alone -- is what actually closes the gap between the isolated-run speed and the full-project
speed: xUnit's default per-class test-collection parallelism was letting this one class's 10+
real subprocess spawns run at the same wall-clock time as the other ~335 fast unit tests in the
same test run, which is what caused the MSBuild-node/NuGet-lock contention in the first place.
The code is correct and is genuinely what keeps runs fast (re-verified independently, see below);
only this narrative was missing a piece of it. Left the three points above as originally written
(a true, still-useful account of the false starts) rather than rewritten, and recorded the
correction here instead, per this file's own long-standing convention for a wrong or incomplete
claim once evidence contradicts it.

**Verified properly this time: three consecutive clean full-suite runs (355 tests each,
`dotnet build-server shutdown` before each), not one lucky sample** -- 1m4s, 1m2s, 1m3s, 355/355
every time. Then the WHOLE `SsisExtractor.slnx` (`dotnet test SsisExtractor.slnx`, all four test
projects together): 149+156+16+355 = 676/676 green, the codegen project's own 355 still landing
at 57s inside that combined run. `Docs/Generated-Tests-Plan.md`'s own memory
(`ssis_generatedsolutionbuildtests_slow_runs`) and this section were both wrong once already this
session in the direction of overclaiming "fixed" -- the three fixes above, together, are what
finally made every run land consistently fast; no further anomaly observed across 5 consecutive
full runs total (2 during the diagnosis, 3 for confirmation) after all three were in place.

### Generated-tests plan — Phase 4, shipping `svk` + the one-command pipeline (2026-09-06)

Closes `Docs/Generated-Tests-Plan.md` entirely -- all four phases now done. `svk`
(`SsisValidationKit/`) ships alongside `ssisx` for real (one build produces both exes), and the
per-step Copilot prompts gained a `/ssisx-rewrite` orchestrator plus the two missing per-step
prompts (`ssisx-walkthrough`, `ssisx-test`) the pipeline needed.

**What was built:**
- `SsisExtractor.slnx` gained a new `/svk/` folder with `SvkCore.csproj`/`SvkCli.csproj` as
  project entries (relative paths into `../SsisValidationKit/src/...`) -- `SsisValidationKit/`
  itself is untouched, its own `SsisValidationKit.slnx` still builds standalone. `SvkCore.csproj`
  got the governance boundary pinned as a doc comment on its own `ProjectReference` block (the
  real enforcement point, not just a README claim): it may reference ONLY `Ssis.Extract.Model`/
  `Ssis.Extract.Dtsx`, never `Ssis.Extract.Codegen`/`Ssis.Extract.Cli`, regardless of now sharing
  a solution file with them.
- **Hit the exact same XML-comment double-hyphen trap this file's own "Tools reorganized for
  real client portability" section already documents, a second time, in a second file type.**
  The first draft of both the new `SsisExtractor.slnx` comment AND the `SvkCore.csproj` comment
  used `--`
  as a plain prose dash -- `dotnet build` failed with `MSB4025`/`XmlException: An XML comment
  cannot contain '--'`, the exact same failure mode (a `.slnx`, like a `.csproj`, is parsed as
  plain XML) the earlier `Directory.Build.props` incident already hit, just not cross-referenced
  at the time it was written since it hadn't happened in a `.slnx`/`.csproj` yet. Fixed by
  rewording both comments to avoid `--` entirely, the same fix shape as before. Worth
  remembering as a general rule now, not just a props-file-specific one: **never use `--` as a
  prose dash in ANY XML-based project file** (`.csproj`, `.slnx`, `.props`, `.targets`, `.dtsx`).
- Three new prompt files under `Tools/.github/prompts/`: `ssisx-walkthrough.prompt.md` (runs
  `svk walkthrough` + `svk sampledata`, both fully deterministic, advisory/never-blocking),
  `ssisx-test.prompt.md` (the AI half for `TEST-ORACLE`/`LOCAL-DATA` gaps, mirroring
  `ssisx-fill.prompt.md`'s own shape but for the two gap kinds Phase 3 added), and
  `ssisx-rewrite.prompt.md` (the orchestrator, sequencing the other five prompts by REFERENCE
  rather than restating their command lines, per the plan's own anti-duplication discipline).
  `ssisx-extract.prompt.md`'s bootstrap block gained a `$svk` resolution alongside `$ssisx`/
  `$dotnet` (one `dotnet build SsisExtractor.slnx` now satisfies both).
- `Tools/README.md`, `Tools/COPILOT_GUIDE.md`, `Tools/.github/copilot-instructions.md` all
  updated: the "two things live here" table became three (`SsisValidationKit/` added), a new
  "Reviewing with `svk`"/"Reviewing before/alongside generation with `svk`" section in each,
  `--skip-tests` documented in `COPILOT_GUIDE.md` (built in the Phase 3 round but never written
  up in the client-facing docs until now), the one-command pipeline table, and two new numbered
  rules in `copilot-instructions.md` (12: `svk` is optional/read-only/never a gate; 13:
  `/ssisx-rewrite` is DESIGNED to stop at both AI boundaries, do not continue past either on your
  own judgement even if a fill looks obviously correct).

**A real logic bug was caught and fixed in `ssisx-rewrite.prompt.md`'s own Step 0 (resume
detection), by actually tracing a real generated state through it rather than just reading it
back.** The first draft's five checks were written as independent bullets rather than a strict
decision tree -- concretely, "is `fills-library/<package>/` non-empty?" and "does
`out/gaps/<package>/` still show unfilled `TEST-ORACLE`/`LOCAL-DATA`?" had no stated ELSE branch,
so a package that had been generated (gaps exist) but had NOTHING written to `fills-library/` yet
(the exact, normal state right after Step 3) matched none of the affirmative checks and would
have fallen through to check 5's "everything is already done" -- exactly backwards. Rewritten as
an explicit top-to-bottom decision tree with a stated resume point on every branch, verified by
literally producing that state with a real fixture and confirming the corrected tree resumes at
Step 4, not "done."

**Verified end to end against a real fixture, not just read back** (`SyntheticScriptComponentSeams.dtsx`,
chosen over `SyntheticScriptTaskSeams.dtsx` after that one turned out to have SOURCE-STRIPPED
Script Tasks -- a legitimate, correctly-unfillable gap per its own work packet, "say so; do not
invent an implementation," but not useful for proving a full round trip to zero gaps):
1. `ssisx extract` -> `svk walkthrough` + `svk sampledata` (the new Phase 4 command sequence,
   run for real) -> `ssisx generate --seams` -- all in a scratch `D:\tmp` folder, never
   `D:\PoC\Tools\out`. Produced 2 `SCRIPT-COLUMN` (Tier 2) + 2 `TEST-ORACLE` (Tier 1) gaps, build
   failing `CS8795` as expected.
2. Wrote a real Tier-2 fill porting the Script Component's actual C# source (`FullName`/
   `IsValid`) into `fills-library/<Package>/<Class>.Fills.cs`. **Caught a real mistake the same
   way a first-time Copilot session would**: the first attempt omitted `using Etl.Core.Abstractions;`/
   `using <Package>.Sql;` (the packet's own text warns "the generated part's usings do not carry
   over to yours" -- easy to read and still forget), producing `CS0246`/`CS0759`. Fixed, re-applied,
   rebuilt clean (0 warnings/0 errors), `dotnet test --filter Category!=Integration` green (3/3).
3. Wrote a real `TEST-ORACLE` fill (a new xUnit test file for the `FullName` seam). **Caught a
   second real mistake**: the packet's own "what to produce" text offers `Fill_<Column>(row, ctx)`
   as the primary call shape with `.Map(row, ctx)` only as a fallback "if the seam is only
   reachable through the whole transform" -- but the generated seam is ALWAYS `private`
   (confirmed by reading the emitted transform directly), so `Fill_FullName` is never callable
   from a separate `.Tests` project at all; only the `.Map()` fallback ever compiles. Fixed by
   asserting through `.Map(row, ctx).FullName` instead; applied, rebuilt clean, `dotnet test`
   green (5/5, the 2 new assertions included). **Worth a possible future fix in
   `AiPacketEmitter`'s own `TEST-ORACLE` contract text** -- its phrasing reads as though direct
   `Fill_<Column>` calls sometimes work, when for a Script Component column seam specifically
   they never do; not changed this round (out of this round's own scope), named here so it isn't
   silently rediscovered the same way next time.
4. Re-checked the (now-corrected) Step 0 decision tree against each real state produced above and
   confirmed it resolves correctly at every point: before any fill (resume at Step 4), after the
   Tier-2 fill with `fills-library/Tests/`+`TestData/` still empty (resume at Step 6), and after
   the `TEST-ORACLE` fill with one gap (`IsValid`) still deliberately left open (matches the
   "has content, re-apply/re-test" branch, not "everything done" -- correctly distinguishes a
   partially-worked package from a finished one).

**Verified the build/governance side too:** `dotnet build SsisExtractor.slnx -c Debug` produces
both `ssisx.exe` and `svk.exe` in one shot (0 warnings/0 errors); `SvkCore.csproj`'s own
`ProjectReference`s grepped afterward and confirmed still exactly the two allowed ones; every
client-facing doc (`Tools/README.md`, `COPILOT_GUIDE.md`, `COPILOT_TESTING_GUIDE.md`,
`copilot-instructions.md`, the new prompt files, `SsisValidationKit/README.md`) grepped for
dev-machine absolute paths (`D:\PoC\SSIS_Rewrite`, `D:\PoC\SSIS_Migration`, `OneDrive`) -- none
found, same leak class this project has caught and fixed twice before.

**Not built, deliberately out of scope:** an automated/non-interactive test of `/ssisx-rewrite`
itself as a literal script (it is a Copilot Chat prompt, not something this project's own test
suite invokes) -- verified instead by manually executing its exact command sequence end to end,
as described above, which is the strongest verification tier available for a prompt file. A
non-interactive/CI mode for the pipeline remains out of scope per the plan's own "Out of scope,
deliberately" list (decision 9's AI-boundary gating is by design).

### Post-Phase-4 review round: closing the one real coverage gap the review found (2026-09-06, same day)

Immediately after Phase 4 shipped, the user asked for a review of the implementation against
`Docs/Generated-Tests-Plan.md` plus a quality/robustness pass -- done via `EnterPlanMode` with 2
`Explore` agents gathering the exact facts before any code changed. It found the plan's own
promise ("`TransformTestEmitter`... extended to every flow path, not just single-destination")
oversold what shipped: `TransformTestEmitter.Emit` was called from exactly one place (the
single-destination path), never for a Conditional Split branch's own Derived Column, a Multicast
branch, a Merge Join flow, or a Lookup flow, even when those DID have one -- reasoned honestly in
the emitter's own doc comment, but silent at the level a user actually reads (`generate-report.md`
never said so).

**Closed for Conditional Split + Multicast (identical code shape, one fix covers both), advisory-
only for Merge Join/Lookup, Aggregate deliberately left alone:**
- `TransformTestRequest.DerivedColumn` (singular) -> `DerivedColumns` (plural) -- a branch can
  carry both a shared upstream one and its own (`SyntheticConditionalSplitRemerge.dtsx`'s own
  real shape).
- The emitted file/class name now keys off `TransformClassName`, not `EntityName` -- a REAL
  collision existed: two branches sharing an entity via a Union-All remerge keep DISTINCT,
  already-disambiguated `TransformClassName`s (`...HighTransform`/`...LowTransform`), so naming by
  `EntityName` alone would have made both branches try to emit the identical file/type.
- Wired a real per-branch `TransformTestEmitter.Emit` call into both
  `GenerateConditionalSplitFlow` and `GenerateMulticastFlow` in `PackageGenerator.cs`, mirroring
  the single-destination path's own call exactly, buffered/committed with the same
  all-or-nothing discipline `branchFiles`/`branchTables` already use.
- Merge Join/Lookup CANNOT safely get real coverage the same way: Lookup's own reference-column
  mapping and Merge Join's own `TrustAllPassthroughColumns` bypass are both invisible to
  `TransformTestEmitter`, and a naive call would risk emitting an assignment to a row property
  that doesn't exist (the same CS0117 class its own doc comment already warns about for a
  Data-Conversion cross-reference). Each now emits a non-blocking `GapKind.TestOracle` advisory
  instead -- ONLY when a Derived Column/Data Conversion is actually present, never unconditionally
  (an always-firing gap is exactly the miscounting shape `PortfolioDigest.IsBlockingGap`'s own
  design already exists to prevent).
- **Aggregate deliberately left with no gap at all**, and the reason is real: its own row type has
  only group/aggregated columns, with no clean analog of "the flow's own Derived Column" to gap on
  the way there is for the other four shapes (a pre-aggregation Derived Column, if any, belongs to
  the SOURCE side, a different, already-out-of-scope question). Naming this correctly needs its
  own design work, not a mechanical one-liner -- left as a named follow-up, not guessed at.

**Verified for real, not just unit-tested**, in a `D:\tmp` scratch dir: regenerated
`SyntheticConditionalSplitRemerge.dtsx` -- confirmed TWO DISTINCT test files
(`SyntheticRemergeTargetHighTransformTests.cs`/`...LowTransformTests.cs`, proving the collision
fix), built against a copy of `Etl.Core` (0 warnings/0 errors), `dotnet test --filter
Category!=Integration` green (4/4), then **deliberately corrupted one real assertion**
(`Assert.Equal("High", result.Segment)` -> `"DELIBERATELY-WRONG-VALUE"`) and confirmed it goes red
-- the same "a real failure is genuinely caught" discipline this project's own testing plan
requires, applied to the new coverage specifically. `SyntheticMulticast.dtsx` (no Derived Column
at all) confirmed as the true no-regression case: correctly zero new test files/gaps, build/test
still green. One pre-existing test needed updating, not just re-passed:
`Generate_ResolvesADataConversionColumn_ReferencedByAConditionalSplitConditionAndAPostSplitDerivedColumn`
now correctly expects 5 gaps (was 3) for `SyntheticDataConversionSplit.dtsx` -- its own two
branches EACH now report the pre-existing "Data-Conversion cross-reference" limitation
independently (both branches reference `SignupDate_dt`), asserted by count-of-matching-gaps, not
just a raw total. Full suite green afterward (see the slowness section above for the final,
correct 355/355 timing).

**Disclosed limitation, not glossed over:** the Merge Join/Lookup advisory's own POSITIVE case
(does it actually fire when it should) was verified by code review only -- confirmed it correctly
stays SILENT on the two real fixtures that have nothing to report (the more dangerous case to get
right, since a false positive would be noise), but no existing fixture combines either shape with
a downstream Derived Column, and building one from scratch via the object model was judged not
worth the effort for a purely informational, zero-functional-risk gap this round. Named as a real,
bounded follow-up rather than silently left unverified.

### Re-running the plan's own Verification items 2/4/5/6 for real (2026-09-06, later the same day)

These four had never been (re-)run after Phase 4 + the review round both landed, and the plan's
own philosophy ("every prior round shipped a real bug a green suite alone did not catch") means
that gap doesn't close itself. Each was run for real -- a `git worktree`, a live database, a
hand-edited generated test harness -- not re-derived from the code. Two genuine, previously
undocumented findings came out of it; one was fixed and regression-tested the same session, the
other is disclosed as a real, still-open gap.

**Item 2 (byte-diff proof) -- confirmed, with one real, disclosed finding.** Built `ssisx` from a
`git worktree` at HEAD (`4c92a52`, i.e. before this whole plan's own uncommitted work) and from the
current working tree, generated all 9 real packages (this PoC's own 4 plus `SSIS_From_Sandeep`'s 5)
both ways into separate `D:\tmp` output trees, and diffed them file-for-file. Every file that is
"only in new" is exactly an expected addition (`README.md`, `appsettings.Development.json`, the
whole new `.Tests` taxonomy) -- nothing was silently replaced or removed. **But every package's own
`{Package}.cs` DOES differ**, not byte-identical as the plan's own wording assumed -- traced to one
single, deliberate, already-tested fix that landed in this session's own uncommitted work
(`PackageClassEmitter`'s `uowActive` flag went from `if (hasConcurrency)`-conditional to
unconditional, and a Lookup's own reference-table preload moved INSIDE the `try` block, ahead of
`BeginAsync`): before this fix, a Lookup preload failure on a non-concurrent package would
propagate out of `RunAsync` unhandled, skipping rollback/failure-handlers/notifier entirely, since
`uowActive`/the guard around `RollbackAsync` only existed for concurrent-wave packages. Confirmed
this is deliberate and tested, not an accidental regression: `RunAsyncFailureTestEmitter`'s own doc
comment and its `usesLookupPreload`-gated test both exist specifically to exercise this exact
scenario (a Lookup preload failing before `BeginAsync` ever runs, so there is nothing to roll back).
Verified the diff is this ONE fix and nothing else, on every one of the 9 packages plus the two
fixtures the review round's own claim rests on (`SyntheticConditionalSplitRemerge.dtsx`,
`SyntheticMulticast.dtsx` -- neither's Mapping/Model/Sql production files differ at all, only the
package-root `.cs`, and only by this one pattern) -- confirming the review round's own claim
("only NEW test files were added for Conditional Split/Multicast, no production .cs changed
byte-for-byte") independently, on both the real-portfolio case (`Package_Transforms`'s own
`DFT_MergeSortedBranches`) and the two dedicated fixtures.

**Item 4 (whole-portfolio combined pass) -- confirmed for the intended scope, plus a genuinely
useful contrast case.** `ssisx generate --input SSIS --recursive --etl-core Tools/Etl.Core --fills
fills-library` (a nonexistent `fills-library`, so nothing is filled) into a fresh `D:\tmp` dir,
`dotnet build Generated.slnx` -> **0 warnings/0 errors**, `dotnet test --filter Category!=Integration`
-> **27/27 green** (LoadEmployees 7, LoadReferenceData 10, SyntheticParallelShapes 10), with no
database reachable and nothing filled. One thing worth recording precisely: **`--seams` no longer
exists as a flag** -- seams are ON BY DEFAULT now (`--unsafe-skip-seams` is the new opt-OUT,
another real, previously-undocumented change that landed in this session's own uncommitted work,
alongside the `uowActive` fix above), so the literal `--seams` flag from the plan's own wording
would now error; omitting it is the correct call. None of this PoC's own 4 packages has a real
Script Task/Component seam (`ScriptTest.dtsx` degrades to a Tier-3 "no Data Flow Task" gap;
`SyntheticParallelShapes.dtsx`'s own gap is an unrelated multi-source one), so seams-on-by-default
introduces no CS8795 risk for this literal scope. **Extended the same run to the full 9-package
portfolio (adding `SSIS_From_Sandeep`) as a stronger version of the same check**: this correctly
FAILS to build (8 `CS8795`s, exactly `Package`'s own 8 hand-portable Script seams named throughout
the AI-assisted-gap-filling sections above) until those are filled -- not a bug, the seam mechanism
working exactly as designed, and consistent with the taxonomy's own explicit carve-out ("Script
Task / Script Component seam: none generated... not something `ssisx` can derive").

**Item 5 (Integration tier for real) -- LoadEmployees/LoadReferenceData confirmed end to end; a
real, previously-unexercised gap found in the `Category=Integration` scaffold itself, closed the
same session (see the follow-up subsection immediately below).** Reset
`dbo.Employee`/`Department`/`Designation` to 0/0/0 via `Sql/01_CreateTarget.sql` +
`Sql/04_CreateLoaderCredential.sql`, ran the built exes for real against `.\SQLFORPOC_2022`
(`TargetDatabase__Password` env var for `ssis_loader`), and read the rows back directly: **12/8/10,
exit 0 both**, matching the counts this project has verified every prior time it ran these two
packages. Then, per the extra ask, ran `dotnet test --filter Category=Integration` against
`LoadEmployees.Tests`/`LoadReferenceData.Tests` -- **"No test matches the given testcase filter"**
for both, correctly: neither package has a SQL source, Excel source, or secondary connection, so
per the taxonomy neither generates any `Integration`-tagged test at all. To actually exercise the
filter, generated a real SQL-sourced fixture with backing tables already present in `SsisPoC`
(`SyntheticOleDbSourceTransform.dtsx`), built it, and ran its one `Integration` test. It failed
against `PackageHarness`'s own documented fake server (`"(fake)"`) as expected; per
`Tools/COPILOT_TESTING_GUIDE.md`'s own explicit "this is by design, edit that one line for a local
run" instruction, hand-edited the generated (human-editable) `TestDoubles/PackageHarness.cs` to
point `DatabaseOptions` at `.\SQLFORPOC_2022`/`SsisPoC` and reran. **It still failed, with a
different, more specific error: `"Session binding token is invalid"`.** Root-caused, not
guessed: `SqlRowSource<TRow>`, given a non-null `uow`, unconditionally attempts `sp_bindsession`
using `uow.GetBindTokenAsync()`'s own result -- and `FakeUnitOfWork.GetBindTokenAsync` always
returns the literal hardcoded string `"fake-bind-token"`, which the real server correctly rejects,
regardless of what real server `DatabaseOptions` points at. Confirmed the underlying mechanism
itself is sound and this is specifically a test-double limitation: a raw `SqlRowSource` constructed
with `uow: null` (bypassing bind-session, falling back to the documented READ UNCOMMITTED path)
read real rows from the same real table successfully. A secondary-connection Integration test is
unaffected -- that taxonomy row is deliberately NOT bind-session-based at all (`PackageHarness`
wires an unreachable `SecondaryConnections` entry specifically so the connection attempt itself,
not a bind token, is what proves `uow` was untouched).

**Closed the same session: `PackageHarness.NewRealUnitOfWorkAsync()`.** Rather than the
`sourceNeedsUow`-shaped alternative (skip binding statically for a single-flow package), which would
change PRODUCTION behavior for no good reason -- the conservative "always bind" wiring is already
correct there; a REAL `uow`'s `GetBindTokenAsync()` genuinely returns a valid token from
`sp_getbindtoken` on the actual open connection, so `sp_bindsession` succeeds fine in production. The
bug was entirely test-side, so the fix is too: `TestDoublesEmitter`'s own `PackageHarness.cs`
template now captures its `DatabaseOptions` into a named field (`_databaseOptions`, read by the
existing fake-DI registration AND the new method, so a human's edit to that one line is the single
source of truth for both) and gained `NewRealUnitOfWorkAsync(CancellationToken ct = default)`: builds
a real `{Package}DbContext` from `SqlConnectionStringFactory.Build(_databaseOptions)`, constructs a
genuine `Etl.Core.Data.UnitOfWork` (`SqlBulkCopyFactory`, `NullLogger<UnitOfWork>.Instance`), and
calls `BeginAsync(IsolationLevel.ReadCommitted, ct)` before handing it back -- begun here, not left
to the caller, because production code never calls `GetBindTokenAsync` before `BeginAsync` either
(see `UnitOfWork`'s own precondition on that method). `ComponentTestEmitter.EmitSqlSourceTest`'s own
Integration `[Fact]` now does `await using var uow = await harness.NewRealUnitOfWorkAsync();` instead
of `harness.NewUnitOfWork()` -- the `.Name`-only `[Fact]` is untouched (still the Fake, still needs no
server at all). Never committed, so the transaction rolls back on disposal; a plain `SELECT` read
leaves nothing to roll back anyway. `EmitExcelSourceTest` needed no change -- an Excel source never
takes `uow` at all (confirmed earlier the same session), so it was never affected.

**Verified end to end, the same discipline as every other round in this file:** rebuilt `ssisx`,
regenerated `SyntheticOleDbSourceTransform.dtsx` fresh (0 warnings/0 errors), edited the freshly
generated `PackageHarness.cs`'s own `_databaseOptions` line to `.\SQLFORPOC_2022`/`SsisPoC` exactly
as a human following `COPILOT_TESTING_GUIDE.md` would, and ran `dotnet test --filter
Category=Integration` -- **it now genuinely PASSES against the real server**, where it used to fail
with "Session binding token is invalid" even after that same edit. Confirmed the always-green
baseline is untouched too: a SEPARATE, unedited fresh generation (still pointing at the fake server
by default) still passes `Category!=Integration` clean (4/4) -- the new method is opt-in, reached only
by the Integration test itself. Two new unit tests added
(`TestDoublesEmitterTests.Emit_ProducesAPackageHarness_WithARealUnitOfWorkFactory`, a new file since
none existed for `TestDoublesEmitter` before; `ComponentTestEmitterTests.EmitSqlSourceTest_...`
extended with the new assertions) and the two stale `PackageHarness.cs.txt`/`LoadEmployees.cs.txt`/
`LoadReferenceData.cs.txt`-family goldens regenerated and reviewed by eye (`git diff`) before
trusting them -- confirmed each diff was exactly the pre-existing, already-verified `uowActive`/
TestData-wiring changes from earlier in this same session, not anything new. Full suite green after:
149+156+16+**357**+94+11+7 = **790** (was 789, +1 for the new `TestDoublesEmitterTests` file).

**Item 6 (walkthrough correspondence) -- one real, confirmed, and FIXED emitter gap.** `Package_Advanced`
doesn't exist as a walkthrough artifact in this environment, so per instruction substituted
`SyntheticConditionalSplitRemerge.dtsx` (this session's own newest fixture, never checked this way
before). `ssisx extract` + `svk walkthrough` produced a 6-row component table (Seq 1 OLE DB Source,
2 DER_LoadedAtUtc, 3 CSPLIT_Amount, 4 DER_TagHigh/DER_TagLow, 5 UNION_Recombine, 6 OLE DB
Destination) that matches the generated `README.md`'s own "Data flow detail" table row-for-row,
same Seq numbers, same detail text -- confirming `PipelineFlowOrder`/`ComponentInsight` really do
back both artifacts with one shared numbering, as designed. Checking each row against an actual
test method surfaced a real gap: **Seq 1 (the OLE DB Source) had NO test file at all** --
`CSPLIT_AmountRouterTests.cs` covers Seq 3, the two branch transform test files cover Seq 2/4 (the
shared and per-branch Derived Columns, folded in as the README's own doc comment says they should
be), but nothing covered the source. The structurally identical `SyntheticMulticast.dtsx` DOES get
`OLEDBSourceSourceTests.cs` for the same shape of source -- proving this wasn't "by design" but a
real asymmetry. **Root-caused and fixed**: `PackageGenerator`'s own "Source -- SQL / Excel"
starter-test loop built its candidate list (`allWiredSources`) from `wiredFlows`/`wiredMulticasts`/
`wiredOleDbCommands` only -- `wiredSplits` (a real, separate dictionary `GenerateConditionalSplitFlow`
already populated, just never read by this loop) was missing from the concat entirely, and
`GenerateConditionalSplitFlow`'s own signature never even accepted a `csvSampleCandidates` list (so
a CSV-sourced Conditional Split flow has the identical gap, for the identical reason -- this fixture
just happens to be SQL-sourced). Fixed both: added `wiredSplits` to the concat, and threaded
`csvSampleCandidates` from the call site through into `GenerateConditionalSplitFlow`'s own
`ResolveFlowSource` call, mirroring `GenerateMulticastFlow`'s already-correct wiring exactly. New
`Generate_EmitsAStarterSourceTest_ForAConditionalSplitFlowsSqlSource` test added to
`PackageGeneratorTests.cs`, pinning `OLEDBSourceSourceTests.cs`'s presence and its `Integration`
trait. **Verified end to end, not just re-passed**: regenerated the fixture with the fix, confirmed
`OLEDBSourceSourceTests.cs` now appears, built (0/0), `dotnet test --filter Category!=Integration`
green (5/5, was 4/4), then the FULL suite across both repos: 149+156+16+**356**
(`Ssis.Extract.Codegen.Tests`, was 355, +1 for the new regression test) + 94+11+7 = **789 total**
(was 788), zero regressions elsewhere.

### Independent three-part review, commissioned after committing the plan (2026-09-06, later the same day)

With all 10 Verification items checked and the work committed (`cc3d923`), the user asked for a
genuinely independent review before moving on -- not a self-check, one written as a standalone
prompt (`Docs/Generated-Tests-Plan-Review-Prompt.md`, kept in the repo for reuse) and handed to
three fresh general-purpose agents in parallel, each told explicitly not to trust any prior
"done"/"fixed" claim in this file and to back every finding with something actually run or read.

**Part B (non-regression: `ssisx extract`/`conformance`/`testgen`/`generate`, `svk walkthrough`/
`sampledata`) -- clean.** Every count matched this file exactly (100% coverage, 31/49 conformance
rules, 5-covered/3-skipped testgen expressions), `conformance` was confirmed idempotent (byte-
identical `conformance-report.json` across two runs), the walkthrough's Rule column cross-checked
exactly against a fresh `conformance` run, and `SvkCore`'s governance boundary held by direct
`ProjectReference` read, not just its own doc comment. One byte-diff nuance surfaced independently,
already known: `LoadEmployees.cs` isn't byte-identical to pre-plan output because of the
`uowActive`/Lookup-preload fix (see Item 2 above) riding in the same commit -- the agent re-derived
the identical root cause from `git diff` on `PackageClassEmitter.cs` on its own, landing on the
same explanation as this file's own account without being told it.

**Part C (slowness fix robustness) -- confirmed ROBUST, plus the doc-completeness correction
recorded in that section above.** Three isolated runs (68-70s), three full-suite runs (73-82s),
and a genuine adversarial run (3 concurrent background loops, one deliberately spinning up
persistent MSBuild nodes with node reuse left ON) all landed in a tight, consistent range with
zero failures -- the adversarial run cost only ~20-30% (89s), nowhere near the original
multi-hour stalls, and live process inspection during cleanup directly reconfirmed real leftover
`/nodemode:1 /nodeReuse:true` MSBuild/VBCSCompiler processes, the exact mechanism the fix targets.
All three previously-documented code components (`-nodeReuse:false`, concurrent `ReadToEndAsync`,
timeout-driven `Kill(entireProcessTree: true)`) confirmed present by direct line-level read. The
one real finding -- the missing `CollectionBehavior(DisableTestParallelization = true)` mechanism
-- is now folded into that section's own write-up above.

**Part A (plan completeness) -- two real, previously-silent gaps found, both closed the same
session; two minor items left as-is.** A third and fourth instance of the "wired for shape X, not
shape Y" bug class this file has already named twice (Conditional Split's own missing source test,
the post-Phase-4 review round's Merge Join/Multicast transform-test gap):

1. **`GenerateLookupThenAggregateFlow` (the real `DFT_LookupAndAggregate` composed shape) reached
   its own return with NO "Aggregate source" starter test AND no gap either** -- unlike every
   other flow shape, which degrades to an honest advisory when it can't produce real coverage.
   Root cause: the standalone `GenerateAggregateFlow`'s own `testCandidates` parameter (feeding
   `EmitAggregateSourceTest`) was never threaded through `GenerateLookupFlow`/
   `GenerateLookupThenAggregateFlow` at all. **Fixed, and a second, real bug was caught fixing
   it, not shipped**: a first attempt threaded `testCandidates` through unconditionally and
   naively reused the standalone template's own `row => row.{GroupByOutputColumnName}` key
   selector -- caught only by actually BUILDING the generated test (not by the gap-count report,
   which showed nothing wrong): `CS0117`, `row.Region` doesn't exist, because in this composed
   shape "Region" is a Lookup-copied reference column that only resolves via
   `lKP_CountryCache[row.Country].Region`, never a bare row property (confirmed by reading the
   real production `AGG_ByRegion()` method directly). Fixed properly by gating the real test on
   `lookupKey is null` (the GroupBy value is a plain passthrough column -- safe, matches the
   standalone template's own assumption) and falling back to the same honest
   `GapKind.TestOracle` advisory `GenerateLookupFlow`'s own downstream-transform gap already uses
   for the `lookupKey is not null` case (the one real evidenced shape,
   `SyntheticLookupThenAggregate.dtsx`'s own "GroupBy Region"). Verified end to end: regenerated
   the real fixture, confirmed the broken test is GONE and the honest advisory fires instead
   (`TEST-ORACLE:SyntheticLookupThenAggregate:SyntheticLookupThenAggregateTarget`), 0 warnings/0
   errors, 1/1 (`RunAsyncTests.cs` only) green.
2. **`GenerateForEachDataFlowLoop` (a ForEach Loop whose body is a whole Data Flow Task) had zero
   test coverage and zero gap, silently** -- the taxonomy's own "ForEach loop" row only covers the
   SQL-statement-per-iteration shape; this Data-Flow-per-iteration shape has no dedicated test
   shape at all (its own CSV source has no static Tier-A sample file to point a test at -- the
   path is computed per iteration, not a fixed location), so building real coverage was correctly
   judged out of scope for a same-session fix. Closed instead with the same honest
   `GapKind.TestOracle` advisory pattern, naming exactly what has no coverage (the per-iteration
   source, the transform, the sink) and why. Verified: regenerated `SyntheticForEachDataFlowLoop.dtsx`,
   confirmed the new gap fires (`TEST-ORACLE:SyntheticForEachDataFlowLoop:FEL_SampleFiles`),
   0 warnings/0 errors, 1/1 green.

Both fixes threaded a real, existing precedent (the Lookup flow's own downstream-transform
advisory) rather than inventing a new shape -- consistent with the plan's own "a component
outside this pilot's own scope is a named, non-blocking gap, not a silent gap" design. Two
existing tests updated to pin both fixes as regressions
(`LookupFlowTests.Generate_WiresALookupThenAggregateFlow_ThroughAMulticast`,
`PackageGeneratorTests.Generate_WiresAForEachDataFlowLoop_ConstructingAFreshCsvRowSourcePerIteration`)
-- no new test FILES this round, so the suite count is unchanged: 149+156+16+357+94+11+7 = **790**,
all green.

**Two minor items surfaced, left as named follow-ups, not fixed:** `generate-report.md`'s "Starter
tests" column counts every `testFiles` entry (including non-`[Fact]` infrastructure like
`TestDoubles/*.cs`), not actual test methods -- a labeling inaccuracy, not a functional bug. And
`/ssisx-rewrite`'s own Step-0 resume decision tree has no terminal state for a seam whose source
is genuinely stripped (an unfillable `TEST-ORACLE`/`LOCAL-DATA` gap that will never close) -- it
resumes at the same step forever, which is harmless (idempotent) but not a clean "done" state.

### A second independent review, specifically hunting the two just-shipped fixes (2026-09-06, later the same day)

After committing the two Part-A fixes (`6b9f532`), a SECOND standalone review prompt
(`Docs/Generated-Tests-Plan-Review-Prompt-2.md`) was written and handed to a fresh agent,
explicitly told not to trust the commit, `CLAUDE.md`, or a green suite -- this exact area had
already shipped one real bug earlier the same day that only building the generated output
caught, not the gap count. The brief: re-derive both fixes from the raw diff, specifically
stress the untested `lookupKey is null` branch of the Lookup+Aggregate fix, and hunt for a
FIFTH instance of the "wired for shape X, not shape Y" bug class before accepting there were
only two.

**Both shipped fixes held up.** The `lookupKey is null` branch (never exercised by the one real
fixture, which always takes the `is not null` path) was independently built and proven for real:
a disposable fixture with a plain-passthrough GroupBy column produced the real "Aggregate
source" test, and the whole solution built 0/0 and the test passed. `GenerateForEachDataFlowLoop`'s
advisory was confirmed to fire exactly once with a correct `EvidenceRefId`.

**But the hunt for a fifth instance found a SIXTH -- and this one is a production correctness
bug, not a missing test.** `PackagePlanner.PlanDataFlow` resolves `flow.Aggregate` and
`flow.Multicast` entirely independently (unlike ConditionalSplit/Multicast, which genuinely are
mutually exclusive there) -- but `PackageGenerator.Generate`'s per-flow dispatch checked
`flow.Aggregate` before ever looking at `flow.Multicast`, and called `GenerateAggregateFlow`
unconditionally once Aggregate was set. `GenerateAggregateFlow` never reads `flow.Multicast` at
all. So a plain Multicast (NO Lookup anywhere -- the one real evidenced Aggregate-behind-a-
Multicast shape, `DFT_LookupAndAggregate`, always has exactly one live branch through a Lookup,
which `GenerateLookupThenAggregateFlow` already handles correctly) with one branch straight to a
destination and a second branch through an Aggregate to a DIFFERENT destination used to: drop
the straight branch silently (no file, no gap), wire the Aggregate's own row against whichever
destination `PackagePlanner` happened to resolve first (not necessarily the Aggregate's own real
target), and report the package as **fully generatable (0 blocking gaps)** while the generated
project **fails to even compile** (a real `CS1061`, confirmed against a disposable fixture built
specifically to reproduce this exact shape). Worse than either of today's earlier two fixes,
which were silent but not WRONG.

**Fixed the same session.** `PackageGenerator.Generate`'s dispatch now checks
`flow.Aggregate is not null && flow.Multicast is not null` (by the time this runs, `flow.Lookup`
is already guaranteed null -- every Lookup branch above it unconditionally `continue`s) and
reports a named, BLOCKING gap instead of proceeding -- this shape is unevidenced anywhere in the
tracked portfolio, so this generator makes no attempt to support it, only to stop silently
mis-generating it. New permanent fixture, `SyntheticMulticastAggregateSibling.dtsx`
(`Ssis.Extract.FixtureBuilder`'s new `multicast-aggregate-sibling` mode: OLE DB Source ->
Multicast -> {straight to Destination A, through Aggregate to Destination B}, no Lookup) --
verified end to end: `ssisx generate` now reports **0 files written, 1 blocking gap** naming
both component names and the reason, instead of silently succeeding into broken code. New
regression test, `LookupFlowTests.Generate_ReportsABlockingGap_ForAMulticastAndAggregateWithNoLookup`.

**A second, real gap was also found and closed the same round**: `GenerateUnionFlow` (a
multi-source Merge/UnionAll flow) reaches its own return with **zero test coverage and zero
gap** -- its own `UnionFlowSource` matches neither the generic SQL/Excel source-test loop's type
checks nor `csvSampleCandidates`, and `ComponentTestEmitter` has no Union-specific test method
the way Merge Join gets its own mapper test. Closed with the same honest `GapKind.TestOracle`
advisory pattern used for the two earlier gaps this session, rather than inventing a new test
shape under time pressure. Verified against the real `SyntheticUnionTwoSources.dtsx` fixture:
the new gap fires exactly once, 0 warnings/0 errors, 1/1 green. Two pre-existing tests needed
their own gap-count assertions bumped by one (`Generate_WrapsBothSidesInConcatenatingRowSource_
ForAGenuinelyMultiSourceUnionAll`, `Generate_WrapsBothSidesInMergeInterleaveRowSource_
ForAGenuinelyMultiSourceMerge` -- `Microsoft.Merge` goes through the identical `GenerateUnionFlow`
path as `Microsoft.UnionAll`, so both needed the same update).

**One item from the review deliberately left as a minor follow-up, not fixed:**
`GenerateMergeJoinFlow`'s own downstream-transform gap check (`flow.DerivedColumn is not null ||
flow.DataConversion is not null`) is dead code -- `PlanMergeJoin` always constructs its
`DataFlowPlan` with both fields null, and the planner rejects any non-direct-to-destination Merge
Join earlier anyway, so this branch can never fire. Not a correctness bug (Merge Join itself IS
correctly tested, via its own dedicated mapper test) and not touched this round -- purely a stale
comment/dead branch, named here rather than silently left for someone to rediscover.

Full suite green after both fixes: 149+156+16+**358**+94+11+7 = **791** (was 790, +1 for the new
`Generate_ReportsABlockingGap_ForAMulticastAndAggregateWithNoLookup` test).

### A third independent review -- a fourth bug in the same class, arguably worse than the third (2026-09-06, later still)

After committing the Multicast+Aggregate correctness fix (`a2fc2a6`), a THIRD standalone review
prompt (`Docs/Generated-Tests-Plan-Review-Prompt-3.md`) was written and handed to a fresh agent,
explicitly told two reviews in a row had each found something real and its job was to find a
third instance of the underlying CLASS of bug -- planner fields resolved independently, generator
dispatch not checking for both -- not merely to re-confirm the two just-shipped fixes.

**Both prior fixes held up under adversarial re-verification.** The previously-untested
`lookupKey is null` branch of the Lookup+Aggregate fix was independently built and proven
correct; the Multicast+Aggregate gap was re-confirmed to fire exactly as committed, placed
correctly in the dispatch order, and reasoned (not built, but directly) to generalize correctly
to a 3-branch Multicast and to an Aggregate reached via a discarded-RowCount/Sort/Union hop,
since the check is pure component co-presence, not shape-specific.

**But the hunt for a third instance found a fourth -- confirmed by building, and arguably worse:
`PlanDataFlow`'s own MergeJoin/Union early-return happens BEFORE the generic "more than one
source component" gate ever runs, so a genuinely-resolved Merge Join or Union said nothing at
all about a THIRD, completely unrelated source/destination pair sitting in the SAME Data Flow
Task -- that whole pair (a real table's worth of production logic) vanished with no file and no
gap, while the flow it WAS wired for reported a clean, complete-looking generation.** Confirmed
against a disposable fixture built specifically to reproduce it (a genuine 2-source UnionAll plus
a wholly separate, unconnected OLE DB Source -> OLE DB Destination pair reusing existing backing
tables): `ssisx generate` reported the union flow's own gaps and nothing else -- the extra pair
never appeared in `generate-report.md`, `gaps.json`, or the generated `DbContext`. Reasoned
(directly, from the code, not built) that `MergeJoin` has the identical defect and is if anything
MORE exposed, since it lacks even the partial guard (`hasSplitOrMulticastUpstream`) Union already
had for an unrelated reason.

**Fixed the same session, symmetrically for both.** `PlanDataFlow` now computes the full
`sourceComponents` list BEFORE either carve-out (previously computed only afterward, for the
generic gate both carve-outs used to bypass entirely): a Merge Join is always exactly two-sided,
so `sourceComponents.Count > 2` is gapped explicitly before `PlanMergeJoin` ever runs; a Union's
own resolved `Sides.Count` is compared against `sourceComponents.Count` AFTER `PlanUnion`
succeeds, gapping (and discarding the otherwise-valid plan) if any source went unaccounted for.
New permanent fixture, `SyntheticUnionPlusExtraSource.dtsx` (`Ssis.Extract.FixtureBuilder`'s new
`union-plus-extra-source` mode, extending the existing `union-two-sources` recipe with a third,
wholly unconnected source/destination pair) -- verified end to end: `ssisx generate` now reports
**0 files written, 1 blocking gap** naming all three source components and explaining exactly
why, instead of silently succeeding with a table's worth of logic missing. New regression test,
`PackagePlannerTests.Plan_ReportsAGap_WhenAUnionAllCoexistsWithAWhollyUnrelatedExtraSource`.

**A second, real but currently-non-live finding, deliberately left as a named follow-up, not
fixed:** `flow.Aggregate` and `flow.OleDbCommand` CAN coexist on one `DataFlowPlan`
(`PackagePlanner.cs`, the OLE DB Command resolution branch has no `aggregate is null` guard), and
`PackageGenerator`'s dispatch checks `Aggregate` before `OleDbCommand` -- but this currently fails
SAFELY rather than silently: `DestinationInfo.IsFastLoadConfigured` happens to always return
`false` for an OLE DB Command component (it has no destination side at all), so
`GenerateAggregateFlow` gaps out with a confusingly-worded but non-broken message ("destination
'X' is not configured for fast load"). Confirmed by building a disposable fixture reproducing
this exact shape. The safety here is INCIDENTAL, not a deliberate guard the way the two just-added
checks are -- if that unrelated fast-load check were ever loosened or bypassed, this composition
would silently mis-generate the same way the two now-fixed bugs did.

**Two other checks came back clean, verified by reading code (not built, but directly):**
`ConditionalSplit`+`MergeJoin` is safely refused (`ResolveMergeJoinSide` requires each input be
fed directly by a `Microsoft.Sort`, so a split-fed input reports a precise gap rather than
mis-generating); `ConditionalSplit`+`Union` and two-of-three `{Aggregate, MergeJoin, Union}` are
each either already-handled by an existing mechanism or structurally impossible (both
`PlanMergeJoin`/`PlanUnion` return before `Aggregate` is ever resolved, and hardcode it to `null`
in their own `DataFlowPlan`). The `GenerateMergeJoinFlow` dead-code assessment from the prior
round was independently re-confirmed correct (`PlanMergeJoin` always hardcodes
`DerivedColumn`/`DataConversion` to `null`, so that branch genuinely can never fire).

Full suite green after this fix: 149+156+16+**359**+94+11+7 = **792** (was 791, +1 for the new
planner test).

**Hardened the same session, immediately after, rather than leave a "safe by accident" gap:**
`flow.Aggregate`/`flow.OleDbCommand` co-presence now gets the identical treatment as the Merge
Join/Union fix above, not just a documented note. `PlanDataFlow`'s OLE DB Command carve-out
(the branch that resolves a destination-less flow whose only sink is an OLE DB Command) gained an
explicit `aggregate is null` condition, mirroring the `conditionalSplit is null && multicast is
null` guards already there -- when Aggregate IS present, the flow now falls through to the
generic "no OLE DB Destination, ADO NET Destination, Flat File Destination, or OLE DB Command
found" gap instead of ever building a `DataFlowPlan` carrying both fields, so the refusal no
longer depends on `DestinationInfo.IsFastLoadConfigured` happening to return `false` for an OLE
DB Command component. New permanent fixture, `SyntheticAggregateThenOleDbCommand.dtsx`
(`Ssis.Extract.FixtureBuilder`'s new `aggregate-then-oledb-command` mode: OLE DB Source ->
Aggregate -> OLE DB Command, no destination anywhere) -- verified end to end: the gap text
changed from the old incidental "destination 'X' is not configured for fast load" (raised deep
inside `GenerateAggregateFlow`) to the new, deliberate, planner-level "no ... destination found"
message, confirming the refusal now happens for the right reason, at the right layer, not by
accident. New regression test,
`PackagePlannerTests.Plan_ReportsAGap_WhenAnAggregateCoexistsWithADestinationLessOleDbCommand`.

Full suite green after this hardening: 149+156+16+**360**+94+11+7 = **793** (was 792, +1 for the
new planner test).

### Every file-based source's real read is now `Category=Integration`, not just Excel — a deliberate, user-requested regression (2026-09-06, later still)

Prompted by a real conversation, not a review finding: a hand-run session pointed out that
`PackageHarness`'s Excel entry got a manual hand-edit (per the earlier "why does D:\tmp's
generated PackageHarness.cs point Excel at a fake path" exchange) while CSV/fixed-width sources
already worked, via a DIFFERENT mechanism — a tool-synthesized Tier-A sample (`SampleData/*.csv`).
Asked directly whether every package's test data should instead be sourced from
`appsettings.Development.json` (the file that already, unconditionally, points every
`FileSource:Files:<Key>:SourceFolder` at a package-relative `TestData` folder). Investigated
first, not assumed: confirmed the test project never reads either `appsettings.json` file at all
— `PackageHarness.cs` builds its own hand-written `ServiceCollection` in code, deliberately
decoupled from `IConfiguration` so a test behaves identically on any machine regardless of what
config files or `DOTNET_ENVIRONMENT` happen to be set. Framed the real tradeoff before touching
anything (`AskUserQuestion`): redirecting ALL sources (not just Excel) to `TestData/` means every
CSV/fixed-width source's own real-read test — previously always-green on synthetic data — now
needs its own `LOCAL-DATA` fill first, same as SQL (needs a real server) and Excel (needs a real
workbook) already did. The user explicitly chose this, informed of the consequence: **"All
sources, everywhere."**

**What shipped, in `Ssis.Extract.Codegen`:**
- `TestDoublesEmitter.cs`: the `sampleDataFiles`/`placeholderFileKeys` split (CSV/fixed-width
  pointed at a tool-synthesized `SampleData/` folder; Excel pointed at a deliberately-missing
  placeholder) is now ONE unified `testDataFiles` list — every file-based source, uniformly,
  points at `Path.Combine(AppContext.BaseDirectory, "TestData")` + its own REAL declared file
  name (never the Tier-A sample's own `{key}.csv` name, which is frequently different).
- `ComponentTestEmitter.cs`: `EmitCsvSourceReadTest` (row-count/column assertions against a
  known-synthetic sample) is retired; a new `EmitFileSourceTest` takes over, mirroring
  `EmitSqlSourceTest`/`EmitExcelSourceTest`'s own two-fact shape exactly — a `.Name`-only `[Fact]`
  (neither `CsvRowSource<TRow>` nor `FixedWidthRowSource<TRow>` opens its file at construction, so
  this still needs nothing real) plus a `[Trait("Category", "Integration")]`-tagged real-read
  `[Fact]` asserting only `Assert.NotNull(rows)` — real data content is as unknowable here as a
  real SQL query's result, so no stronger assertion is honest. The generated test file is now
  named `{sourceMethodName}SourceTests.cs`, not `{rowTypeName}SourceTests.cs` — a real, previously
  cosmetic-only risk this also closes: two different flows' own Flat File Sources routinely share
  the SSIS-default component name, but `sourceMethodName` (from `methodInventory`, C#-unique by
  construction) never collides the way a bare row-type-derived name theoretically could.
- `PackageGenerator.cs`: `PlanDataFlow`'s per-flow dispatch gained one unconditional `allWiredSources`
  branch for `CsvFlowSource`/`FixedWidthFlowSource` (previously a SEPARATE, Tier-A-gated loop that
  silently emitted NOTHING if `SampleDataEmitter` failed to synthesize a sample for an unusual
  format — now unconditional, matching SQL/Excel's own always-emit behavior). The real `TestData/`
  file name is resolved directly from `candidate.ConnectionManager.Parsed?.FilePath` (CSV/fixed-
  width) or the already-built `fileSourceEntries` list (Excel) — the SAME resolution
  `BuildFileSourceEntry` already uses for `appsettings.json`, reused rather than re-derived, so the
  two can never disagree. `SampleDataEmitter`/`SampleData/*.csv` generation is UNCHANGED and still
  runs — kept deliberately, now serving purely as a schema-correct reference for writing the real
  `LOCAL-DATA` fill (the same role `svk sampledata` already plays for a package with no
  `ssisx generate` output at all), consumed by nothing at runtime any more.

**A real, previously-shipped bug was caught and fixed as a direct consequence of this change, not
a rewrite for its own sake.** The old `LocalFileSourceData` gap message named the WRONG file for a
human to save their realistic data as: it used the Tier-A sample's own invented name
(`sample.FileName`, e.g. `Employee.csv`) as a fallback, which for `LoadEmployees`' real connection
manager is `Employees.csv` (plural) — a human following the old gap message's instructions
verbatim would have saved their file under the wrong name and the fill would silently never be
found. Caught by reviewing the regenerated golden `README.md.txt` diff by eye (the standing rule),
not assumed correct. The gap message now names the SAME real file name `PackageHarness`/
`appsettings.Development.json` actually resolve, computed identically in both places.

**Verified four ways:**
1. **Golden-file regression, reviewed by eye per the standing rule, not trusted blindly.** Exactly
   the expected 7-file diff across both PoC packages (`EmployeeCsvRowSourceTests.cs.txt`/
   `Department`/`DesignationCsvRowSourceTests.cs.txt` deleted, `FF_SRC_{Entity}SourceTests.cs.txt`
   added under the new naming, `PackageHarness.cs.txt`/`README.md.txt` updated) — confirmed the
   `PackageHarness.cs` diff shows exactly `SourceFolder = ... "SampleData" → "TestData"` and
   `SourceFileName = "Employee.csv" → "Employees.csv"` (the real name), nothing else moved.
2. **Full suite green**: 149+156+16+360 = 681 across all four `ssisx` test projects, zero
   regressions beyond the expected golden diff.
3. **A real end-to-end round trip against the live `SSIS_From_Sandeep` portfolio**
   (`Package_Advanced`/`Package_Exports`/`Package_Legacy`, regenerated fresh into a scratch
   `D:\tmp` dir): confirmed `Category!=Integration` stays 100% green (10+9+7 = 26 tests, the
   `.Name`-only ones, unaffected) while `Category=Integration` now correctly FAILS with a clean
   `FileNotFoundException` naming `TestData/CustomersFixed.txt` for the fixed-width source with no
   fill supplied — then supplied a real fixed-width file matching the exact declared schema
   (`CustomerID`/`FullName`/`Email`/`CountryCode`, fixed widths, no header) and reran: **passes**.
   Did the identical round trip for Excel (a real `.xlsx` via `openpyxl`, dropped under
   `TestData/DripEligibility.xlsx`) and confirmed it now works through the SAME unified mechanism
   with ZERO manual `PackageHarness.cs` hand-editing needed — the tool's own default output is
   correct out of the box, closing the gap that originally required a manual fix earlier this
   session.
4. **Docs corrected in the same pass**, not left stale: `Tools/COPILOT_TESTING_GUIDE.md`'s own
   "two sample-data tiers" section (previously stated Tier A "is what makes the suite green
   immediately after generate" — no longer true), `Tools/.github/copilot-instructions.md`'s
   `LOCAL-DATA` rule wording, and `Docs/Generated-Tests-Plan.md`'s own Tier-A section (a dated
   correction note, not a rewrite of the historical record, matching this file's own convention).
   `ssisx --help`'s own "should pass with ZERO fills applied right after a fresh generate" claim
   was checked and left UNCHANGED — it is still literally true, since `Category!=Integration`
   still passes with zero fills; only WHICH tests carry the `Integration` tag changed.

### The standard 5-step pipeline, `Run-Pipeline.ps1`/`/ssisx-pipeline`, code coverage, and a real happy-path regression found running it for real (2026-09-06, later still)

Prompted directly: the user wanted one durable, always-do-these-5-steps pipeline for running
this tool against a package or a folder — extract, `svk` walkthrough/sampledata, generate,
apply-fills, then build + test + a code-coverage report — invokable from either a Claude session
or GitHub Copilot chat, with one hard rule threaded through everything: **never delete any
TestData, fill, or generated output unless explicitly told to**, so a human always has the
flexibility to manually review or hand-supply data between runs.

**What shipped:**
- `coverlet.collector` wired into every generated `{Package}.Tests.csproj` by default
  (`TestProjectEmitter.cs`/`GenerateCommand.BuildDirectoryPackagesProps`) — closes a real,
  previously-manual step (this session's own earlier coverage measurement needed hand-patching
  each `.csproj` with `coverlet.collector` before `--collect:"XPlat Code Coverage"` would work at
  all). Golden files regenerated and reviewed; the diff is exactly the intended
  `<PackageReference Include="coverlet.collector">` block.
- `Tools/SsisExtractor/scripts/Run-Coverage.ps1` (new): runs `dotnet test --filter
  Category!=Integration --collect:"XPlat Code Coverage"` per `{Package}.Tests` project, parses
  each `coverage.cobertura.xml` (plain `[xml]` cast, no external tool), and reports line/branch
  coverage split into "own generated code" vs. "shared `Etl.Core`" — the same split done by hand
  earlier this session, now a real, reusable script. Never deletes a prior `TestResults\` run;
  picks the most-recently-written cobertura file instead.
- `Tools/SsisExtractor/scripts/Run-Pipeline.ps1` (new): the actual 5-step orchestrator, callable
  for one package (`-Package`) or a whole folder. Resolves `ssisx.exe`/`svk.exe` next to itself
  if published there (the client-deployment convention `Run-Survey.ps1` already established),
  falling back to `dotnet run --project` against source otherwise (works in this dev repo too).
  `-OutputPath`/`-FillsPath` are created if missing, NEVER recreated/wiped if present — no
  `Remove-Item` anywhere in either new script.
- `Tools/.github/prompts/ssisx-pipeline.prompt.md` (new): the Copilot-invocable `/ssisx-pipeline`
  wrapper, calling `Run-Pipeline.ps1` rather than re-deriving its steps in prose (same
  anti-duplication discipline `ssisx-rewrite.prompt.md` already established). Deliberately a
  DIFFERENT, lighter-weight command than `/ssisx-rewrite`: this one only ever APPLIES fills
  already on disk and reports where things stand; it never writes a Tier-1/2/test-oracle/
  local-data fill itself, and never stops at an AI boundary the way `/ssisx-rewrite` does.
- `Tools/.github/copilot-instructions.md` gained two new numbered rules (14: never delete
  TestData/fills/generated output; 15: `/ssisx-pipeline` is the default for "run the tool") plus
  a pointer from the existing manual command sequence. `Tools/COPILOT_GUIDE.md`/`Tools/README.md`
  gained the new pipeline's own section/table, a coverage subsection, and a new top-of-list
  ready-to-paste prompt.
- **A real, pre-existing contradiction was caught and fixed while updating the docs, not
  introduced by this round**: `COPILOT_GUIDE.md`'s own "What's disposable and what isn't" table
  said `out\` was "100% disposable, delete freely" — true when written, but no longer true since
  `out\generate\<Package>\TestData\` can now hold a real, hand-supplied `LOCAL-DATA` file (the
  2026-09-06 "every source, everywhere" round, earlier the same day). Fixed the table and its own
  "if you ever want a clean regenerate" paragraph to match the new rule.

**Two real bugs found actually RUNNING the new pipeline end to end, neither found by the
existing test suite (`GeneratedSolutionBuildTests` only checks that the generated solution
BUILDS, never that its own generated tests PASS — a real, named limitation in that suite's own
verification depth, left as a follow-up rather than fixed this round):**

1. **A classic PowerShell gotcha in `Run-Pipeline.ps1` itself**: `Invoke-Tool`'s helper function
   did `& $tool.Cmd @allArgs; return $LASTEXITCODE` with the external command's own stdout left
   unsuppressed — PowerShell merges an external command's passthrough stdout with a function's
   own `return` value into ONE combined output array when both are captured by the caller,
   corrupting `$extractExit`/`$generateExit` into a multi-line string instead of a plain integer.
   Fixed by piping every native-tool invocation through `| ForEach-Object { Write-Host $_ }`
   (streams to the console in real time without entering the function's own output stream) —
   confirmed necessary by reproducing the exact corrupted `Write-Error` message before the fix,
   not assumed. A related, doubly-real gotcha: `$ErrorActionPreference = 'Stop'` (set globally in
   both new scripts) upgrades ANY native command's stderr line into a terminating exception —
   including `dotnet test`'s own legitimate `[FAIL]` output, which aborted the whole pipeline on
   the first real test failure instead of gracefully reporting it. Fixed by using `'Continue'`
   globally and `-ErrorAction Stop` explicitly on each genuinely-fatal `Write-Error` call instead.
2. **A real, previously-undiscovered regression from the SAME-DAY "every source, everywhere"
   TestData round** (see that section above): the happy-path `RunAsync_Succeeds_
   AgainstFakesWithNoFaultInjected` starter test — supposed to be always-green, fakes-only, no
   real data needed — actually runs the WHOLE pipeline end to end, including a real CSV/fixed-
   width read through the SAME `PackageHarness`-registered `FileSourceOptions` the isolated
   source-read test uses. Once that read started requiring a real `TestData/` file (the earlier
   round's own change), the happy-path test broke too — invisible to every unit test in this
   repo's own suite, only found by literally running `Run-Pipeline.ps1` against this PoC's real
   packages and reading the failure. Framed the fix as a real design fork before touching
   anything (`AskUserQuestion`): exclude CSV/fixed-width from the happy-path eligibility check too
   (simple, consistent, but loses the happy-path test for most real packages), or have
   `PackageHarness` synthesize a throwaway placeholder file for JUST this one test. The user chose
   the second. Implemented via a new `PackageHarness(bool useSampleDataForFileSources = false)`
   constructor parameter: `false` (every existing caller, including the isolated Integration
   read test) keeps pointing at the real `TestData/<real file name>`, unchanged; `true` (used ONLY
   by the happy-path test, via `RunAsyncFailureTestEmitter`) points the SAME CSV/fixed-width keys
   at the Tier-A synthetic sample (`SampleData/`, already generated unconditionally and kept
   specifically as a reference after the earlier round) instead — reusing existing, already-
   verified content rather than inventing new placeholder-generation logic. Threaded through via a
   new `sampleDataFallbackEntries` list in `PackageGenerator.cs`, built from the SAME
   `sampleDataByKey` dictionary already populated for the Tier-A reference files. Deliberately does
   NOT cover Excel (no Tier-A sample exists for it at all) — harmless by construction, since
   `PackageClassEmitter.SupportsFakeHappyPath` already excludes any Excel-sourced package from the
   happy-path test regardless, for an unrelated, pre-existing reason.

**Verified four ways:**
1. **Golden-file regression, reviewed by eye** — exactly the intended diff:
   `PackageHarness.cs.txt` gained the `useSampleDataForFileSources` parameter and per-key ternary,
   `RunAsyncTests.cs.txt` shows the happy-path test's own harness construction changed to
   `new PackageHarness(useSampleDataForFileSources: true)`, nothing else moved.
2. **Full suite green**: 149+156+16+360 = 681, zero regressions.
3. **A real, live end-to-end run of `Run-Pipeline.ps1`** against this PoC's own real `SSIS/`
   folder (all 4 packages, `net10.0`) — republished a genuinely STALE `ssisx.exe` that had been
   sitting next to `Tools/SsisExtractor/scripts/` since 2026-08-25 (a disposable, gitignored,
   safe-to-rebuild-any-time build artifact per `Publish-Survey.ps1`'s own doc comment, not user
   data — confirmed via `git status` showing it untracked before republishing it) — first run
   surfaced bug 2 above for real (`Assert.Equal() Failure: Expected: 0, Actual: 1` on all 3
   CSV-sourced packages); after both fixes, a clean rerun: exit code 0, all 3 packages' own
   `dotnet test` green, `coverage-report.md` written with real line/branch/own-code percentages
   per package (27.9–30.3% blended, 90.7–93.5% own-code — the Etl.Core-diluted blended figure
   this file's own coverage-measurement round already explained).
4. **Confirmed additive-only in practice, not just by reading the script**: reran the pipeline
   twice in a row against the same `-OutputPath` and confirmed nothing under it was touched
   except regenerated/overwritten-in-place files — no folder disappeared, no prior `TestResults\`
   run was removed.

**Not done, deliberately out of scope for this round:** actually strengthening
`GeneratedSolutionBuildTests` to run the generated tests themselves (not just build), which is
what would have caught bug 2 automatically instead of needing a live pipeline run to surface it —
named here as a real, precise follow-up rather than silently left for someone to rediscover.

### Correction, same day: `/ssisx-pipeline` retired — `/ssisx-rewrite` is the standard command, not the script

The section above framed `Run-Pipeline.ps1`/`/ssisx-pipeline` as "the standard thing to run from
chat." The user pushed back with the right question: a PowerShell script **cannot invoke AI at
all** — once called, it runs to completion as a closed box, with no point where it could pause
and hand a gap's own work packet to an LLM. That's exactly why Step 4 of that script could only
ever *apply* fills already on disk, never *write* one — a structural mismatch between "wrap
everything in one script" and "some steps need a human/AI in the loop," not a detail to work
around later.

**The fix, once framed this way, was obvious: when a Claude/Copilot chat session is available,
the AGENT should take each step as its own tool call — extract, walkthrough, generate, read the
gap report, draft a fill, stop for confirmation, apply, build, test, coverage — exactly the shape
`/ssisx-rewrite` already had for steps 1-4/6-7. It only needed two things it was missing: a
coverage step, and whole-folder scope (it was single-package only).** Both added:
`ssisx-rewrite.prompt.md` gained a new "Scope" section (blank `package` = every package in
`inputPath`, looped one at a time, each completing its own AI boundaries before the next starts)
and a new Step 8 (`Run-Coverage.ps1`, called directly — a purely mechanical sub-step, fine to
invoke without agent judgement even inside an otherwise agent-driven pipeline).

**`ssisx-pipeline.prompt.md` was deleted outright** — a `.prompt.md` file only makes sense as a
chat-invocable wrapper, and the whole point of `Run-Pipeline.ps1` is the OPPOSITE case: no chat
session available at all (CI, an ops person with no AI session). Keeping a slash command for
that would have kept inviting exactly the confusion this correction fixes. `Run-Pipeline.ps1`
and `Run-Coverage.ps1` themselves are UNCHANGED in behavior — still real, tested, additive-only
scripts — only their own doc comments were rewritten to state plainly: use `/ssisx-rewrite` when
a chat session exists; reach for these scripts only when one genuinely doesn't.

Updated to match: `Tools/.github/copilot-instructions.md` (rules 14/15 — rule 15 now names
`/ssisx-rewrite` as the standard, describes the whole-folder+coverage extension, and states the
"why agent-driven" reasoning explicitly rather than asserting it), `Tools/COPILOT_GUIDE.md` (the
`/ssisx-rewrite` section merged with the former standalone pipeline section, its own 8-row table
gaining the coverage step, its "Prompts to paste" entry rewritten to invoke `/ssisx-rewrite`
rather than the script), and `Tools/README.md` (same merge). No `Ssis.Extract.Codegen`/CLI code
changed this round — this was entirely a prompt/doc-level correction, so the full suite (681
tests) is unaffected and was not re-run for this specific change.

### `/ssisx-batch` run against 9 real third-party GitHub SSIS portfolios, two real generator bugs
### fixed, and a large prior uncommitted feature pile verified and committed (2026-09-16)

A new orchestration layer, `/ssisx-batch` + two agent types (`ssisx-batch-phase-a`,
`ssisx-batch-phase-b`), was pointed at `D:\PoC\SSIS_Packages_From_GitHub\` — 10 real,
independently-cloned GitHub SSIS repos, none of them this project's own PoC packages or the
already-exercised `SSIS_From_Sandeep` portfolio. Phase A runs the mechanical half (extract → svk
walkthrough/sampledata → generate → build → test → coverage) with no AI judgment; Phase B (not
run this round, held explicitly per the user's own instruction) would write AI-assisted Tier-1/2
fills. `ETL-SSIS-Real-Scenarios` (~40 unrelated tutorial folders, not a real portfolio) was
excluded by explicit user choice; two deep, mostly-unrelated repos
(`mssql-ssis-community-samples`, `sql-server-samples`) had their own `InputPath` narrowed to just
the inner SSIS folder rather than walking the whole clone. Full per-project results are in
`D:\PoC\SSIS_Packages_From_GitHub\Output\BATCH-SUMMARY.md`.

**Two real, root-caused, previously-unknown generator bugs surfaced from this run, both fixed and
verified end-to-end this session:**

1. **`Run-Pipeline.ps1` treated a benign duplicate-package-`ObjectName` skip as fatal.** SSDT's
   own designer default (`ObjectName="Package1"`, never renamed by the package's author) collided
   across multiple `.dtsx` files in 4 of the 9 portfolios — `PackageLoader.DropDuplicateNames`
   correctly keeps the first-loaded package and records the rest as a benign `"duplicate-name"`
   skip in `load-failures.md`, but `ReportCommand.Run` (and so `ssisx extract`'s own exit code)
   returns exit 2 for ANY non-empty `Failures` list, benign or not — and `Run-Pipeline.ps1:141-142`
   treated any non-zero `ssisx extract` exit as an immediate, hard `Write-Error -ErrorAction Stop`,
   aborting Steps 2-5 entirely. Two of the 9 batch portfolios
   (`SSIS-Data-Warehouse-ETL-Pipeline`, `Sales-DataWarehouse-with-Incremental-Load-SSIS-ETL-Pipeline`)
   never got past extraction because of this alone. Fixed by having `Run-Pipeline.ps1` parse
   `<extractOut>\load-failures.md`'s own `Kind` column (already the exact discriminator on disk —
   `"duplicate-name"`/`"package-not-found"` are benign; `"package"`/`"project"`/`"ispac"` carry a
   real exception message) and only abort on a genuine hard failure, warning and continuing
   otherwise. **Verified for real**: both previously-halted portfolios now proceed cleanly through
   generate/build (each correctly still produces zero runnable packages, since every flow in both
   is independently blocked on its own Tier-3 gaps — Lookup redirect-no-match, unresolved OLE DB
   connections, SCD, a cross-database source — but that is now visible instead of masked by a
   Step-1 abort).
2. **A Flat File Source component's own output-column type override was invisible to
   `CsvRowEmitter`/`SampleDataEmitter`, producing a real `CS0029` build failure.** Confirmed real
   in `E-Commerce-Datawarehouse-implementation`'s own `Source_to_Stage_Sales_all_dimensions.dtsx`:
   the Flat File connection manager declares `BirthYear` as `DT_STR` (raw text), but the Flat File
   **Source component**'s own output column for `BirthYear` independently overrides it to `DT_I4`
   (`ErrorOrTruncationOperation="Conversion"`, the source's Advanced Editor performing the
   conversion inline while reading, failing the component on bad data per its own
   `TruncationRowDisposition`) — with **no** Data Conversion/Derived Column anywhere downstream.
   `TransformEmitter`'s own mismatch/coercion gate was never at fault here — it already correctly
   reads the DESTINATION side's `i4`, so from its own view nothing disagreed. The bug was entirely
   in two OTHER emitters that resolve a flat-file column's type purely from the connection
   manager's own declared type, with no visibility into the source component's own override:
   `CsvRowEmitter.Emit` (generating `string BirthYear` while the Model/Transform correctly expect
   `int`) and — a second, previously-latent instance of the *identical* bug, found only because
   fixing the first one made a starter test start FAILING for a new reason (a synthesized "Sample"
   placeholder value no longer parses as the now-correctly-typed `int`) —
   `SampleDataEmitter.EmitCsv`'s own representative-value synthesis, which resolved a column's
   type the same CM-only way. Fixed by threading the Flat File Source's own `PipelineComponentSpec`
   into both (`CsvRowEmitter.Emit`'s new optional `sourceComponent` parameter, shared via a new
   internal `ResolveSourceOutputTypesByName` helper reused by `SampleDataEmitter.EmitCsv` through
   a new `CsvSampleCandidate.SourceComponent` field) and preferring the source's own output-column
   type whenever it disagrees with the CM's declared type for that column name — an unmapped
   override type is a named `GenerationGap`, never a silent fallback to the CM's type. Two new
   regression tests in `CsvRowEmitterTests.cs` pin both the override-applied and
   override-unmapped-degrades-to-gap cases against a hand-built `PipelineComponentSpec` (not a new
   `.dtsx` fixture — a full object-model-built fixture was judged unnecessary for this fix's own
   scope). `FixedWidthRowReaderEmitter`'s own sibling `SampleDataEmitter.EmitFixedWidth` needed no
   change — a fixed-width column is ALWAYS read as a plain string regardless of its declared type
   (already documented, evidenced against a real dtexec run in an earlier round), a genuinely
   different, already-correct case. **Verified end to end, the strongest evidence tier this
   project's own philosophy asks for**: regenerated `E-Commerce-Datawarehouse-implementation`'s
   actual `Source_to_Stage_Sales_all_dimensions` package, confirmed `BirthYear` now generates as
   `int` throughout (row/model/transform all agree) and the synthesized sample value is `7`, not
   `"Sample"`; the whole regenerated portfolio (all 7 packages, plus a copy of `Tools/Etl.Core`)
   builds **0 warnings/0 errors** and its one wired package's 7 generated tests all pass
   (90.6% own-code coverage) — where before this fix, `dotnet build` failed outright.

**A large body of prior work — `Microsoft.SCD` (Slowly Changing Dimension), ForLoop container,
Expression Task, PCT Sampling router, XML Source, Copy Map, and TransferSqlServerObjectsTask
component support — was discovered sitting UNCOMMITTED in the working tree while investigating
why SCD (the batch run's own dominant Tier-3 blocker, appearing across 3 of the 9 portfolios) had
no support at all.** It did — fully, evidenced against a real `.dtsx` (`ScdPayload`'s own doc
comment cites a genuine SCD component's real saved-XML shape: exactly 6 fixed outputs across 2
exclusion groups — `Unchanged`/`New`/`Fixed Attribute`/`Changing Attribute Updates` in one group,
`Historical Attribute Inserts` deliberately its own — matching real evidence, not guessed) — it
was simply never committed, and `CLAUDE.md` carried no history entry for any of it. Rather than
trust or discard this pile blindly, it was verified this session, per the plan's own explicit
gate, before touching it further:
- `dotnet build`/`dotnet test` on `Tools/SsisExtractor/SsisExtractor.slnx`: builds 0/0, and
  passes **415/417** — the 2 failures are pre-existing, unrelated to this pile or either fix above
  (`GenerateGoldenFileTests.Generate_MatchesGoldenFiles` for both PoC packages, a stale golden
  string in `FakeUnitOfWork.cs.txt` — "ported from\nD:\\PoC\\SSIS_Rewrite\..." — that no longer
  matches `TestDoublesEmitter.cs`'s own current, already-committed emission; confirmed neither
  file involved is touched by this pile or this session's two fixes, left unfixed as a separate,
  named, pre-existing issue rather than folded into this round).
- The sibling `D:\PoC\SSIS_Rewrite` repo (the hand-written `Etl.Core` this pile's own new
  abstractions — `IScdBranch`, `ScdColumnRole`, `ScdBranch`, `ScdClassifier`, plus `ForLoopStep`,
  `ExpressionTaskStep`, several new `IRowSource` variants — target) also carried the matching
  uncommitted mirror of these changes: `dotnet build`/`dotnet test` there too, **139/139** in
  `Etl.Core.Tests` plus `LoadEmployees.Tests`/`LoadReferenceData.Tests` unaffected, all green.
- **The real motivating case**: regenerated `E-Commerce-Datawarehouse-implementation` (14 real
  `Microsoft.SCD` instances across its packages, per the batch run's own earlier count) with this
  code in place — confirmed SCD is recognized and its component correctly reasoned about (its own
  unconnected `Unchanged`/`Fixed Attribute`/`Changing Attribute Updates`/`Inferred Member Updates`
  outputs each report a precise, non-blocking advisory naming exactly which output SSIS itself
  would silently discard rows into — not a crash, not a silent guess).

Given the pile passed every check, it was committed together with the two fixes above as one
round, per the user's own explicit choice once shown the verification results — Fix B's own call
sites in `PackageGenerator.cs` were already interleaved with this pile's own edits to that same
file before this session started, so a clean separate commit for "just the 2 fixes" was not
practical without risky patch surgery.

**Not built or investigated further this round** (named, not silently dropped): a
`ssisx-batch`/Phase B run against any of these 9 portfolios (explicitly held by the user); a
dedicated dtexec-verifiable `.dtsx` fixture for the Flat File Source override bug (the hand-built
`PipelineComponentSpec` unit test was judged sufficient for this fix's own scope); the 2
pre-existing unrelated `GenerateGoldenFileTests` golden-string failures named above; and a full
narrative write-up of the SCD/ForLoop/ExpressionTask/PctSampling/XmlSource/CopyMap/
TransferSqlServerObjectsTask pile's own internal design decisions — this entry documents that the
pile was found, verified, and committed, not a re-derivation of how each of those seven component
types was actually built (that work predates this session and its author/history is unknown).

### Step D — re-running the 3 affected portfolios for real, `-Quiet`, and a third real bug found (2026-09-16, same day)

The round above fixed and committed Fix A/Fix B/the SCD pile but never actually re-ran the 3
portfolios those fixes were meant to unblock, and `BATCH-SUMMARY.md` (referenced above as if it
already existed) had in fact never been written. Closed the same day: built a `-Quiet` switch on
`Run-Pipeline.ps1` first (needed because re-running a portfolio through an agent captures the
*entire* `dotnet restore`/`build`/`test` transcript as tool-call context regardless of what the
agent is told to read afterward — tens of thousands of tokens per re-run otherwise), then ran it
for real against all 3 portfolios and wrote `D:\PoC\SSIS_Packages_From_GitHub\Output\
BATCH-SUMMARY.md` (a first-time write, not an update) with the results. Full detail in that file;
the two headline confirmations:

- **Fix A confirmed on two independent real cases**: both `SSIS-Data-Warehouse-ETL-Pipeline` and
  `Sales-DataWarehouse-with-Incremental-Load-SSIS-ETL-Pipeline` previously never reached `generate`
  at all (a benign duplicate-`ObjectName` skip made `ssisx extract` exit 2, which the old script
  treated as fatal) — both now correctly warn and proceed through `generate`/`build`.
- **Fix B confirmed on its real motivating case, to the strongest evidence tier this project asks
  for**: `E-Commerce-Datawarehouse-implementation`'s whole regenerated solution now builds 0
  warnings/0 errors (previously `CS0029` in `stgCustomerDemographicsTransform.cs`), and the exact
  flow the fix targeted (`Source_to_Stage_Sales_all_dimensions`, 12 files/11 starter tests) not
  only builds but its own `dotnet test` genuinely **PASSes**, 90.6% own-generated-code coverage.
- **The SCD pile confirmed on real (not synthetic) packages for the first time**: across all 3
  portfolios, every real `Microsoft.SCD` instance correctly produced either a non-blocking Advisory
  gap (an unconnected output) or a precisely-named Tier-3 gap (a composite business key — a real,
  previously-unexercised SCD shape, distinct from the unconnected-output case, correctly reported
  as "unevidenced and unmeasured" rather than guessed at) — never a crash, never a silent guess, on
  a portfolio the pile's own author never saw.

**A third, real, pre-existing bug was found running this — not fixed this round, named instead.**
`Run-Coverage.ps1`'s own `Write-Error "No {Package}.Tests.csproj found..." -ErrorAction Stop` is a
*terminating* error that, called via `& $coverageScript ...` from inside `Run-Pipeline.ps1`,
unwinds all the way out and kills the ENTIRE calling script the instant a portfolio has zero fully-
generatable packages (so no `{Package}.Tests.csproj` exists anywhere) — before `Run-Pipeline.ps1`
ever reaches its own final summary/gap-by-tier block. Hit on 2 of the 3 portfolios re-run this
round (both landed at 0/N generatable, for reasons unrelated to either fix). Independent of
`-Quiet` and of Fix A/B/the SCD pile — it would have happened on the old script too, for any
portfolio with 0 generatable packages; this round's own gap-tier numbers for those two portfolios
were read directly from `gaps.json`/`generate-report.md` instead of the crashed script's own
summary. Left unfixed and stated plainly in `BATCH-SUMMARY.md` rather than silently worked around
— Step D's own scope was "run and report," not "fix a third bug."

`ClientMigration/gap_report.py`/`gap_report_csv.py` (plus their own output,
`gap_report.txt`/`unsupported_components_report.csv`, and the underlying `github_scan.csv`) were
also committed this round — real component-type-gap analysis tooling against the 9 GitHub
portfolios, found sitting untracked in the working tree alongside the rest of this round's work,
reviewed and kept on the user's explicit instruction rather than assumed disposable.

**Not done, deliberately out of scope**: re-running the other 6 of the original 9 portfolios (Step
D's own scope was specifically the 3 portfolios Fix A/B were built to unblock); fixing the
`Run-Coverage.ps1` termination bug just found; Phase B (AI-assisted gap filling) against any of the
3 — still explicitly held per the user's own standing instruction.

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
