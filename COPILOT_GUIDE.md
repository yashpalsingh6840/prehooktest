# Using `ssisx` with GitHub Copilot

This is the detailed reference behind [`.github/copilot-instructions.md`](.github/copilot-instructions.md)
(that file is what Copilot auto-loads for every chat; this one is what a human -- or Copilot,
when it needs more detail -- reads for the full picture). If you only read one section, read
["Prompts you can paste into Copilot Chat"](#prompts-you-can-paste-into-copilot-chat) below.

## What's actually in this folder

| Folder | What it is |
|---|---|
| `SsisExtractor/` | `ssisx` -- the CLI itself (extract, report, conformance, testgen, diff, generate, apply-fills). Already built and tested; you run it, you don't modify it. |
| `Etl.Core/` | The hand-written C# runtime library every generated project depends on (SQL bulk-copy, CSV reading, the package-run host, email notification). `ssisx generate` does not produce this from a `.dtsx` -- pass `--etl-core Etl.Core` on every `generate` call and the command copies it in for you (see below); skip that flag and the generated solution will not build. |
| `SsisValidationKit/` | `svk` -- a separate, standalone, **optional** tool, built by the same one-time build as `ssisx` (see below). Two read-only, deterministic commands: `svk walkthrough` (a human-readable execution-order review doc + a claims file per package) and `svk sampledata` (schema-correct sample CSV/SQL/DDL). Never edits `SsisExtractor/`, never generates C#, never gates `generate`/`apply-fills` -- see "Reviewing with `svk`" below. |
| `.github/copilot-instructions.md` | Hard rules Copilot auto-loads for this workspace. |
| `COPILOT_GUIDE.md` | This file. |
| `COPILOT_TESTING_GUIDE.md` | The deeper "how testing works here" reference -- `PackageHarness`'s full API, the two sample-data tiers, the `Category=Integration` convention. Not usually needed: a `TEST-ORACLE` work packet already embeds the one pinned block you need. |

**Nothing here needs SSIS installed, SQL Server installed, or the original SSIS project open.**
`ssisx` parses the raw `.dtsx`/`.dtproj` XML directly. It does not need, and will never ask for,
a live database connection, a deployed SSISDB catalog, or the packages' real source data files.

## Invoking `ssisx` -- do this once, first

`ssisx` is not on PATH and there is no globally-installed command called `ssisx` -- it's a CLI
project inside `SsisExtractor/` that needs building once per machine. Build it, then call the
built `.exe` directly for every command below (faster than `dotnet run` on repeated calls,
which is the normal usage pattern here):

```
cd SsisExtractor
dotnet build SsisExtractor.slnx -c Debug
cd ..
```

**This one build also produces `svk`** (`SsisValidationKit\src\SvkCli\bin\Debug\net8.0\svk.exe`)
-- `SvkCore`/`SvkCli` are project entries inside `SsisExtractor.slnx` too, so there is no second
solution to build. `svk` is entirely optional (see "Reviewing with `svk`" below); skip it if you
never call `svk <command>`.

That produces `SsisExtractor\src\Ssis.Extract.Cli\bin\Debug\net8.0\ssisx.exe`, relative to this
`Tools\` folder. **Everywhere this guide says `ssisx <command> ...` below, substitute that real
path** (or `dotnet run --project SsisExtractor\src\Ssis.Extract.Cli -- <command> ...` if you'd
rather skip the separate build step). Do not guess at a different path, and do not assume
`ssisx` resolves on its own -- if a command fails with "not recognized", the build step above
was skipped or `Tools\` isn't the current directory.

## Reviewing with `svk` (optional, read-only, never a gate)

`SsisValidationKit\` is a **separate, standalone tool** from `ssisx` -- its own solution
(`SsisValidationKit.slnx`), its own README. It only ever reads what `ssisx extract` already
wrote; it never edits anything under `SsisExtractor\`, and it never generates C#. Two commands,
both fully deterministic (same input/`--seed` -> byte-identical output every time -- neither is
an AI step, there is nothing here to triage):

```powershell
$svk = "SsisValidationKit\src\SvkCli\bin\Debug\net8.0\svk.exe"

# A human-readable execution-order review doc per package (real SSIS order, concurrency
# callouts, linked conformance rule IDs) + a claims file to record "I checked this component".
& $svk walkthrough --spec out --out out\walkthrough --claims out\walkthrough\claims --package LoadEmployees

# Deterministic sample CSV/SQL/DDL, type/length-correct against the package's own declared
# schema, including a Lookup's own reference table where a join key is resolvable.
& $svk sampledata --spec out --out out\sampledata --package LoadEmployees --rows 20 --seed 42
```

`--spec` points at the SAME `--out` folder a prior `ssisx extract` call wrote to (it reads
`packages\<Package>.spec.json` from there) -- not a separate spec location. Neither command is
required before `ssisx generate`; running `generate` with no prior `svk` step at all works
exactly the same. Full reference: `SsisValidationKit\README.md`.

## What `ssisx generate` does and does NOT do

**Does:** reads one or more `.dtsx` packages and writes a runnable C# console-app project per
package under `<out>\generate\<PackageName>\` -- entity classes, a DbContext, row-reader/row-
writer code, translated Derived Column expressions, `Program.cs`, `.csproj`/`appsettings.json`.
Also writes `Directory.Build.props`/`Directory.Packages.props`/`Generated.slnx` once per `--out`
run, a `generate-report.md` naming every gap, and (new) a self-contained
**`<out>\HOW-TO-FILL-GAPS.md`** at the output root every run -- if you (or a later Copilot
session) end up working from the generated solution directly with no other doc in view, open
that file first; it's written specifically to not assume anything else here is visible.

`generate` ALSO writes a starter xUnit test project at `<out>\generate\<PackageName>.Tests\` -- a
sibling directory of the main project, listed in `Generated.slnx` under its own `/tests/` folder
-- for every generated method, not just a narrow pilot shape: `RunAsync`'s own failure/happy
paths, every Execute SQL Task/File System Task/Sequence Container, every source and sink (CSV,
fixed-width, Excel, SQL, SQL/flat-file destinations), a Conditional Split's router, transforms,
Merge Join mappers, OLE DB Command, ForEach loops, Multicast, and Aggregate. Every expected value
is computed (not guessed) -- transform/router assertions go through the same oracle-verified
expression evaluator `ssisx testgen` uses. **`dotnet test --filter Category!=Integration` on this
project is a real, legitimate verification step you can and should run, right after a fresh
`generate` with zero fills applied** (unlike anything touching a real database, see below): it
needs no client data or connection at all. Full detail, including the two-tier sample-data
system and the `Category=Integration` convention, is in **`COPILOT_TESTING_GUIDE.md`** -- read
that before writing or reviewing a generated test. A component outside this pilot's own scope is
a named, non-blocking gap in `generate-report.md` (often a `TEST-ORACLE`/`LOCAL-DATA` one -- see
"Working a gap"), not silently skipped.

**Does NOT do, and should never be asked to do in this environment:**
- Does not build the generated code (you, or Copilot, run `dotnet build` separately to check
  it compiles).
- Does not run the generated code against a real database. There is no client database
  connection available here, and none should be invented.
- Does not compare its own output against the original package's real behavior. That
  comparison (this project's own internal "Gate 3" validation harness) depends on a captured
  data corpus this engagement's client site does not have. **Do not attempt to build an
  equivalent verification step** -- it is explicitly out of scope for a client engagement with
  no source data/database access. The deliverable here is *correct-looking, buildable C# source
  plus an honest list of what's unresolved* -- not a proven-identical replacement.
- Does not silently guess at anything it can't derive from the `.dtsx` file. See "Working a gap"
  in the instructions file.

## What the generated code actually looks like

Each package generates as one class (`{PackageName}Package`, in `{PackageName}.cs`), not a flat
script -- **one `internal` method per SSIS task or pipeline component**, named after the task/
component itself and ordered exactly like that package's own control flow, so a method name in
the code is the same name you'd see in the original `.dtsx`/SSDT designer. `Program.cs` itself is
just a few lines of bootstrap that construct the class and call its `RunAsync`. Every method is
`internal` (not `private`) specifically so the generated `{Package}.Tests` project can call it
directly and test one task/component in isolation -- this is a deliberate design choice, not an
accident of the generator's own internals, so don't "clean it up" into `private` if asked to
review the generated code.

**Concurrency is real, not simulated.** If the original package ran two or more executables with
no ordering constraint between them (SSIS itself ran those concurrently), the generated code runs
them concurrently too, via `Task.WhenAll` -- each branch gets its **own** database connection/
transaction (opened fresh for that branch, committed or rolled back independently). This means
rollback is scoped to a branch, not the whole package, for exactly the executables that ran
concurrently under SSIS -- if one concurrent branch fails after a sibling branch already
committed, that sibling's work stands, matching what the original SSIS package itself did (none
of these packages declares `DTS:TransactionOption="Required"`, so SSIS never wrapped concurrent
branches in one shared transaction either). Everything that ran sequentially in the original
package still shares one transaction end to end, same as before. This is stated in the generated
class's own doc comment and in `generate-report.md` -- read those rather than assuming.

**A source/sink method's own parameter list is not the same for every one -- don't assume they
all look alike when writing a test or a fill against one.** A source that reads from SQL (an OLE
DB/ADO NET Source, or a Merge Join/Union/Aggregate that nests one) takes `(IUnitOfWork uow)`,
because it binds its own connection to the package's active transaction -- a later flow reading a
table an earlier one just wrote (still uncommitted) would otherwise deadlock. A source that reads
from a file (CSV, fixed-width, Excel) or writes to a sink takes **no `uow` parameter at all**,
since it never touches the transaction. Called sites match: `EXCEL_SRC_Drip()`, but
`OLESRC_CustomerIds(uow)`. If you're calling one of these methods directly from a hand-written
test, check its own declared signature rather than copying a `(uow)` call from a different
component.

## Running against one package vs. many (the flexibility this exists for)

Every command that reads packages (`extract`, `graph`, `report`, `conformance`, `testgen`,
`generate`) accepts `--input <folder> --recursive` to sweep an entire portfolio, AND
`--package <name>` (repeatable, or comma-separated in one value) to narrow the run to just the
package(s) you name. `<name>` is matched against the package's own internal name (its
`DTS:ObjectName`, which is *usually* the filename without `.dtsx` -- run `report` once without
`--package` to see the real names if you're unsure).

**Practical rule of thumb, because a real portfolio can be dozens of packages:**
- `extract`/`report`/`conformance` are read-only and cheap -- fine to run across the whole
  portfolio in one call, and a good first step so you (and Copilot) know what's actually there.
- `generate` is the one to always scope with `--package` unless a human explicitly asks for
  "generate everything." One package (or a small named batch) at a time is the normal,
  expected usage -- not a workaround.

A `--package` name that doesn't match anything is a hard error (exit 2) with a clear message --
it will never silently generate zero packages and look like success.

## Choosing a .NET target framework: `--framework net8.0` or `net10.0`

`ssisx generate` targets **`net10.0` by default** -- omit `--framework` entirely and that's what
you get, unchanged from before this flag existed. If the client's own environment runs **.NET
8** instead, add `--framework net8.0` to **every** `generate` call for that engagement (it is
not remembered between calls -- pass it every time, the same way you pass `--etl-core` every
time).

**This is not a cosmetic TargetFramework swap.** `Etl.Core`'s EF Core SqlServer provider
(10.0.11) only runs on .NET 10 -- confirmed against the real published NuGet package, not
assumed -- so `--framework net8.0` also pins a genuinely different EF Core major version
(9.0.15, the newest that still supports .NET 8) inside the generated `Directory.Packages.props`.
`--etl-core` automatically rewrites the copied `Etl.Core`'s own props files to match whichever
`--framework` you asked for, so the two always agree -- never hand-edit either props file to try
to reconcile them yourself.

```powershell
# Default -- targets net10.0, EF Core 10.0.11
ssisx generate --input <folder> --out out --recursive --package LoadEmployees --etl-core Etl.Core --fills fills-library

# For a client on .NET 8 -- targets net8.0, EF Core 9.0.15
ssisx generate --input <folder> --out out --recursive --package LoadEmployees --etl-core Etl.Core --fills fills-library --framework net8.0
```

**If you don't know which .NET version the client actually runs, ask before generating** --
don't default to net10.0 and hope, since discovering the mismatch only at `dotnet build` (or
worse, at deployment) is a much more expensive failure than asking up front. Both legs have been
verified end-to-end in this project's own dev environment (build AND run, byte-for-byte
identical output data across both), so either one is safe to hand a client once you've confirmed
which they need.

## Command reference (see `ssisx --help` for the authoritative, always-current version)

```powershell
# See every package name + how complex/ready each one is. Read-only. Safe on everything.
ssisx report --input <folder> --out out --recursive

# Generate exactly one package. --etl-core copies the runtime library into
# out\generate\Etl.Core as PART OF this command; --fills fills-library points every
# Tier-1/2 answer at the DURABLE library outside out\, never the disposable out\fills\
# default -- BOTH flags are required on every generate call, no exceptions (see "What's
# disposable and what isn't" below for why this is non-negotiable).
ssisx generate --input <folder> --out out --recursive --package LoadEmployees --etl-core Etl.Core --fills fills-library

# Generate a named handful
ssisx generate --input <folder> --out out --recursive --package LoadEmployees,LoadReferenceData --etl-core Etl.Core --fills fills-library

# Generate literally everything (only when asked to)
ssisx generate --input <folder> --out out --recursive --etl-core Etl.Core --fills fills-library

# Generate for .NET 8 instead of .NET 10 (default is net10.0) -- add --framework net8.0
# to ANY generate call above. This also pins a different EF Core major version (9.0.15,
# not 10.0.11) for that run, and --etl-core copies the matching props files in too.
ssisx generate --input <folder> --out out --recursive --package LoadEmployees --etl-core Etl.Core --fills fills-library --framework net8.0

# See gate-1 obligations (what a rewrite owes) for one package
ssisx conformance --input <folder> --out out --package LoadEmployees

# After writing a Tier-2 fill file under fills-library\<Package>\*.cs (NOT out\fills\ --
# see "What's disposable and what isn't" below):
ssisx apply-fills --out out --fills fills-library

# After writing a hand-picked extra test under fills-library\<Package>\MoreTests\*.cs --
# NOT part of the gap-fill workflow above, see "Adding more unit tests" below:
ssisx apply-tests --out out --fills fills-library
```

`ssisx generate` exits:
- `0` -- every requested package generated with zero gaps.
- `3` -- code was written, but one or more gaps exist (**normal, expected** -- read the report).
- `2` -- usage error (bad flag, unreadable input, `--package` name matched nothing).

If `--etl-core` is omitted, or points at a path that doesn't exist, that's reported as an
`Etl.Core` gap in `generate-report.md`/`gaps.json` rather than failing silently -- if a build
later says something like "project Etl.Core not found", that gap report is where to look, not
a reason to start guessing at what went wrong.

## Building what was generated

```powershell
dotnet build out\generate\Generated.slnx
```

That's it -- `Etl.Core` is already in place from `--etl-core` above, no separate copy step
needed. A clean build with zero warnings/errors is the expected outcome for a package with
zero gaps. A package with open gaps will fail to build with `CS8795` (an unimplemented seam)
until its Tier-2 fills are written and applied -- **that failure is intentional**, not
something to patch around; see "Working a gap".

## Testing the generated code

```powershell
dotnet test out\generate\Generated.slnx --filter Category!=Integration
```

That is the always-green baseline: every generated test that needs a real database, a real
`.xlsx` file, or a real secondary-connection server is tagged `[Trait("Category", "Integration")]`
and excluded by this filter -- what's left should pass with **zero fills applied**, right after a
fresh `generate`, on any machine. Running WITHOUT the filter (`dotnet test` alone) will attempt
those Integration-tagged tests too, which need real data/a real server this environment does not
have -- do not do that here unless a human has explicitly pointed you at one.

Two things a generated test needs that a gap can't derive on its own become work packets, same
as any other gap -- see "Working a gap" in the instructions file and the two new prompts below:
a **`TEST-ORACLE`** gap (a Conditional Split case, or a test for a Script Task/Component seam
once it's filled) asks for a whole new test file under `fills-library\<Package>\Tests\`; a
**`LOCAL-DATA`** gap asks for a realistic sample file under `fills-library\<Package>\TestData\`
-- every file-based source's own real-read test (CSV, fixed-width, Excel alike) needs this
before it passes; only the SEPARATE, always-green "happy path" `RunAsync` test still falls back
to a synthetic Tier-A sample, and only for that one test. Full depth --
`PackageHarness`'s complete API, the two sample-data tiers, why a source/sink method's own
`uow` parameter varies -- is in **`COPILOT_TESTING_GUIDE.md`**; you should not usually need it,
since a `TEST-ORACLE` packet already embeds everything its own answer needs.

**Opting out: `--skip-tests`.** Add it to any `generate` call to omit the whole
`<Package>.Tests` project (and its own `TestOracle`/`LocalFileSourceData` gaps) for that
package -- named plainly, unlike `--unsafe-skip-seams`, because skipping tests loses coverage
but can never produce silently wrong code. `appsettings.Development.json`/`TestData\` wiring is
unaffected either way -- that also serves a real, non-test `dotnet run --environment
Development`, which `--skip-tests` has no opinion about.

```powershell
ssisx generate --input <folder> --out out --recursive --package LoadEmployees --etl-core Etl.Core --fills fills-library --skip-tests
```

**Code coverage.** Every generated `{Package}.Tests.csproj` already references
`coverlet.collector` (wired in by `ssisx generate` itself -- no manual patching), so
`--collect:"XPlat Code Coverage"` works out of the box:

```powershell
.\Tools\SsisExtractor\scripts\Run-Coverage.ps1 -GeneratedRoot out\generate
```

Runs `dotnet test --filter Category!=Integration --collect:"XPlat Code Coverage"` per package,
parses each `coverage.cobertura.xml`, and writes `out\generate\coverage-report.md` splitting the
result into "this package's own generated code" vs. "the shared `Etl.Core` library" -- the
own-code figure is the honest one for a starter-test baseline; the blended total is dragged down
by `Etl.Core`'s own infrastructure (SQL bulk-copy, Excel/fixed-width readers, ...) that a starter
suite was never meant to fully exercise. Never deletes a prior `TestResults\` folder -- each run
adds a fresh, GUID-named result folder alongside whatever was already there. `/ssisx-rewrite`
(further down) calls this as its own Step 8.

## Adding more unit tests to an already-green package: `ssisx apply-tests`

**This is not part of the gap-fill workflow above -- see `Docs/AI-Test-Enrichment-Plan.md` (a
dev-repo-only doc, do not open it from a client-only copy of this folder) for the full design.**
Only worth reaching for once a package already builds clean and passes `dotnet test --filter
Category!=Integration` with zero fills applied -- it raises coverage past the deterministic
starter baseline, never appears in `gaps.json`, and never affects whether a package counts as
generatable.

Every generated `README.md` carries its own "Test coverage notes" section -- what each existing
test already asserts, and (by what it doesn't say) what it doesn't -- so an assistant can pick a
handful of real gaps in coverage without opening a single generated `.cs` test file. Write one
`.cs` file per candidate under `fills-library\<Package>\MoreTests\` (**not**
`fills-library\<Package>\Tests\` -- that folder is `apply-fills`' own `TEST-ORACLE` mechanism and
is matched against `gaps.json`; a file dropped there for this purpose is reported `Orphaned`), an
optional `// ssisx-more-test: Author=... Date=... Targets=...` first-line comment for audit only,
then:

```powershell
ssisx apply-tests --out out --fills fills-library
dotnet build out\generate\Generated.slnx --nologo
dotnet test out\generate\Generated.slnx --filter Category!=Integration --nologo
```

Every file present is copied unconditionally -- there is no `GapId` to check a "more test"
against, so unlike a Tier-1/2 fill there is nothing to be `Stale`/`Orphaned` about. If a test
references a method/column that got renamed since the README was read, the next build simply
fails with an ordinary compile error naming exactly what's wrong -- that IS the staleness check
for this feature, not a tool bug to work around. Its own audit trail lands in
`out\tests-applied.json`, a separate manifest from `fills-applied.json` on purpose. The
`/ssisx-more-tests` prompt (`.github/prompts/ssisx-more-tests.prompt.md`) walks this end to end,
including the "stop and report before applying" step every AI-touching workflow in this tool
follows.

## What's disposable and what isn't -- fills live OUTSIDE `out\`, permanently

**The short version: Tier-1/Tier-2 work never lives inside `out\` at all.** It lives at
`fills-library\<Package>\` -- a folder at the `Tools\` root, a sibling of `SsisExtractor\`/
`Etl.Core\`/`out\`, already tracked in git. Every `generate` and `apply-fills` call in this guide
now includes `--fills fills-library` for exactly this reason -- **never omit that flag, and
never write a fill anywhere under `out\`.**

| Path | Disposable? | What it is |
|---|---|---|
| `out\generate\<Package>\` source files (`.cs`, `.csproj`, ...) | **Regenerable, but do not delete on your own judgement** | Everything `ssisx generate` writes is reproducible by re-running it. Regenerating overwrites these in place, which is fine -- but see rule 14 in `.github/copilot-instructions.md`: never delete first, just regenerate. |
| `out\generate\<Package>\TestData\` and `out\generate\<Package>.Tests\SampleData\` | **No -- never delete this** | `TestData\` may hold a real, hand-supplied `LOCAL-DATA` file (copied in by `apply-fills` from `fills-library\<Package>\TestData\`, the durable source -- but the copy under `out\` is what every test/local run actually reads). `SampleData\` is the Tier-A synthetic reference. Neither is safe to nuke as part of "cleaning up `out\`". |
| `out\fills\` (an old-style location from before `fills-library\` existed) | **No -- never delete this either** | If you see this from an old run, treat it exactly like `fills-library\` below -- check its contents before assuming it's disposable just because it's under `out\`. |
| `fills-library\` (at the `Tools\` root, NOT under `out\`) | **No -- never delete this. Commit it to git.** | Hand-ported Tier-2 Script Task/Component logic, and Tier-1 `*.decisions.json` answers. `ssisx` never writes to it, and it survives every `out` deletion/regeneration because it was never inside `out` to begin with. |
| `out\conformance\claims\` | **No -- never delete this** | Human sign-off on gate-1 obligations. Unlike fills, this one genuinely does default to living under `--out` (via `--claims`) -- if you want it durable too, point `--claims` at a folder outside `out\` the same way, e.g. `--claims claims-library`. |

**The short version, per rule 14: never delete anything under `out\` (or anywhere else) on your
own judgement.** Re-running `generate`/`apply-fills` overwrites the regenerable parts in place --
that's the normal, safe way to refresh output, not deleting first. A genuinely clean wipe is the
human's call to make explicitly, never something to do as part of "running the tool" or amid
troubleshooting a build.

**Why this exists, and why it's not optional:** this already happened for real, more than once.
An earlier `out\fills\Package\` held 4 already-verified, already-working fill files. Something
outside `ssisx` (most likely a manual "clean start" -- deleting or recreating the whole `out`
folder between sessions; `ssisx` itself has no delete logic anywhere in its source, checked
directly) removed them. Nothing crashed at that moment -- `ssisx apply-fills` just quietly
reported "0 fills applied" the next time it ran, since there was genuinely nothing there to
apply. The real symptom only showed up much later, as **8 unrelated-looking `CS8795` build
errors** in Visual Studio, with no obvious link back to a missing folder. It happened again on a
completely fresh `out` a session later, for the exact same reason -- relying on `out\fills\` as
the storage location meant every "regenerate from scratch" silently discarded real work. Moving
the fills to `fills-library\`, outside `out\` and tracked in git, is the actual, permanent fix --
not a reminder to be more careful next time.

**If you ever want a clean regenerate:** re-run `generate` (it overwrites the regenerable parts
of `out\` in place) rather than deleting `out\` first -- a `TestData\`/`SampleData\` folder under
it may hold real, hand-supplied data (see the table above), which a blanket delete would take
with it. A genuinely clean wipe of `out\` is a separate, explicit decision for the human to make,
not something to do on your own judgement while troubleshooting. Just make sure your
next `generate`/`apply-fills` call still includes `--fills fills-library`. If a rebuild shows
`CS8795` errors for seams you already filled, the first thing to check is whether
`fills-library\<Package>\*.cs` still exists on disk and whether the command you ran actually
included `--fills fills-library` -- not whether the port itself was wrong. See the "CS8795"
prompt below for the exact recovery steps. **After writing or editing anything under
`fills-library\`, commit it** (`git add fills-library && git commit`) -- an uncommitted fill is
still one accidental folder-delete away from being lost, same as before this fix, just one step
removed.

## The one-command pipeline, agent-driven: `/ssisx-rewrite`

Rewriting one package by hand means remembering several separate commands in the right order,
twice interrupted by AI work. `.github/prompts/ssisx-rewrite.prompt.md` (`/ssisx-rewrite` in
Copilot Chat) sequences the existing per-step prompts instead of restating their command lines,
so each step stays independently runnable and this file doesn't drift out of sync with them.
**This is the DEFAULT thing to run whenever asked to "run the tool" / "generate this package" /
"process this folder", in a chat session** -- one package, a named few, or a whole folder (leave
`package` blank), start to finish:

| # | Step | Prompt | Kind |
|---|---|---|---|
| 1 | Extract + survey | `ssisx-extract.prompt.md` | deterministic |
| 2 | Walkthrough (`svk`, advisory) | `ssisx-walkthrough.prompt.md` | deterministic |
| 3 | Generate | `ssisx-generate.prompt.md` | deterministic |
| 4 | Tier-1/2 gap fills | `ssisx-fill.prompt.md` | **AI -- stops for review** |
| 5 | Apply + build | `ssisx-fill.prompt.md`'s own closing step | deterministic |
| 6 | Test-oracle/local-data fills | `ssisx-test.prompt.md` | **AI -- stops for review** |
| 7 | Apply + build + test | `ssisx-test.prompt.md`'s own closing step | deterministic |
| 8 | Code coverage | `Run-Coverage.ps1`, called directly | deterministic |

**It always stops at both AI boundaries and reports** rather than continuing on its own
judgement -- a Tier-1 answer or a test's expected value is only used once a human has actually
reviewed it, never on an AI's own confidence. **Resuming is filesystem-detected, not tracked in
a separate state file:** re-running `/ssisx-rewrite` checks what already exists on disk (an
extracted spec, a generated project, non-empty `fills-library\<Package>\`) and picks up from
there, so it's always safe to run again after a partial stop. `--package <Package>` is threaded
through every step -- it never touches any package not in scope.

**Why agent-driven, not one script call:** steps 4 and 6 need a human/AI actually reading a work
packet and writing something, which a plain script cannot do -- it can only run deterministic
commands and report an exit code. Running `/ssisx-rewrite` means the agent takes each step as
its own tool call, so it can react mid-flight: read the real gap report, draft a fill, and stop
for the human's confirmation, instead of a script silently continuing past an unfilled gap into
a build failure.

**`Run-Pipeline.ps1` runs the same 5 mechanical steps as ONE script call, but only for when no
Claude/Copilot chat session is available at all** (CI, a scripted regression check, an ops
person with no AI session at hand):

```powershell
.\Tools\SsisExtractor\scripts\Run-Pipeline.ps1 -InputPath <client-folder> -OutputPath <out> -Framework net10.0
# One or a few packages:
.\Tools\SsisExtractor\scripts\Run-Pipeline.ps1 -InputPath <client-folder> -OutputPath <out> -Package LoadEmployees,LoadReferenceData -Framework net10.0
```

It can run `ssisx apply-fills` to pick up fills already on disk, but it can never write one --
if it hits a Tier-1/2/test-oracle/local-data gap with nothing supplied yet, it just reports the
failure and stops, rather than pausing to ask. **If a chat session is available, use
`/ssisx-rewrite` instead** -- do not reach for this script from within one. Additive-only, same
as `/ssisx-rewrite`: `-OutputPath`/`-FillsPath` (default `<OutputPath>\fills`) are created if
missing, reused as-is if present, never deleted. If `ssisx.exe`/`svk.exe` are published next to
the script it uses those directly; otherwise it falls back to `dotnet run --project` against
the source, so it works in this dev repo too, not just a client handoff.

## Prompts you can paste into Copilot Chat

These assume Copilot has this workspace open and has already read
`.github/copilot-instructions.md` (automatic). Replace `<client-folder>` with the real path to
the client's `.dtsx` files, and `<out>` with wherever you want output written (e.g. `out`).

**Run the standard pipeline against one package or a whole folder (the usual first thing to try):**
> Follow `/ssisx-rewrite` for `<client-folder>` (leave the package blank to cover every package
> there, or name one/a few) targeting `<framework>`. Take every step as your own tool call so you
> can actually stop and ask me at the two AI boundaries rather than guessing past them. Never
> delete anything already under `<out>` or the fills folder along the way. Give me one combined
> summary at the end: per package, build status, test pass/fail counts, coverage percentage, and
> anything still waiting on my confirmation.

**First look at a portfolio:**
> Run `ssisx report --input <client-folder> --out <out> --recursive`. Don't generate anything
> yet. Summarize: how many packages, which ones look simplest, and how many would generate with
> zero blocking gaps today according to `generation-readiness.md`.

**Generate one specific package:**
> Run `ssisx generate --input <client-folder> --out <out> --recursive --package <PackageName>
> --etl-core Etl.Core --fills fills-library`. Then read `<out>\generate-report.md` for that
> package and tell me, in plain terms, exactly what gaps (if any) it has and what tier each one
> is.

**Generate a named batch, not everything:**
> Run `ssisx generate` scoped with `--package` to just these packages: <list>. Always include
> `--etl-core Etl.Core --fills fills-library`. Do not run it against the rest of the portfolio.

**Generate for a client on .NET 8 instead of .NET 10:**
> This client's environment runs .NET 8, not .NET 10. Run `ssisx generate` with
> `--framework net8.0` in addition to `--etl-core Etl.Core --fills fills-library` for every
> package. Confirm the generated `Directory.Build.props` under `<out>\generate\` shows `net8.0`
> before telling me it's done.

**Check a package actually compiles:**
> Run `dotnet build <out>\generate\Generated.slnx` and show me the result. Do not attempt to
> run the built executable against any database. (If this fails with something like "project
> Etl.Core not found," the earlier `generate` call was missing `--etl-core` -- re-run it with
> that flag rather than copying the folder in by hand.)

**Build suddenly fails with `CS8795` for seams that were already filled:**
> The generated solution fails with `CS8795` ("must have an implementation part") for seams I
> already ported. Do NOT re-port anything yet. First run
> `ssisx apply-fills --out <out> --fills fills-library` and show me its output. If it says "0
> fills applied," check TWO things before assuming the fill content is wrong: (1) does
> `fills-library\<PackageName>\` still have files in it at all, and (2) did the command actually
> include `--fills fills-library`. **Never delete `fills-library\`, `<out>\generate\Etl.Core\`,
> `<out>\generate\*\TestData\`, or `<out>` itself to "start clean"** -- re-running `generate`/
> `apply-fills` overwrites what needs overwriting on its own; deleting first only risks losing a
> hand-supplied `LOCAL-DATA` file or a fill nobody re-typed a copy of.

**Work a Tier-2 gap (Script Task/Component port):**
> Open the work packet at `<out>\gaps\<PackageName>\<GapId>.md`. Write the fill it describes
> under `fills-library\<PackageName>\<File>.cs` (NOT under `<out>\`), matching its exact seam
> signature, with the `// ssisx-fill:` provenance comment filled in from the packet. Then run
> `ssisx apply-fills --out <out> --fills fills-library` and tell me what it reports. Once it
> applies cleanly, remind me to `git add fills-library && git commit`.

**Work a Tier-1 gap (a missing datum, e.g. a Lookup join key):**
> Open the work packet at `<out>\gaps\<PackageName>\<GapId>.md`. Do not guess the answer
> yourself. Show me its exact question so I can confirm it, then write my answer into
> `fills-library\<PackageName>.decisions.json` (NOT `<out>\fills\...`) in the format the packet
> specifies, and remind me to commit `fills-library` afterward.

**Work a TEST-ORACLE gap (a starter test the emitter couldn't derive on its own):**
> Open the work packet at `<out>\gaps\<PackageName>\<GapId>.md` -- it embeds everything you need
> (the pinned testing-API block, and either the Conditional Split's own cases or the Script
> Task/Component source this test exercises). Write the whole test file it describes under
> `fills-library\<PackageName>\Tests\<Name>Tests.cs` (NOT under `<out>\`), with a
> `// ssisx-fill:` comment as the file's own FIRST line (there is no seam to place it above --
> this is a brand-new file, unlike a Tier-2 port). Then run
> `ssisx apply-fills --out <out> --fills fills-library` and tell me what it reports.

**Work a LOCAL-DATA gap (supply a realistic sample file):**
> Open the work packet at `<out>\gaps\<PackageName>\<GapId>.md` -- its own "Save as" line names
> the EXACT file name to use, and its own column table gives the exact schema. Write a realistic
> file (same columns, order, delimiter/widths, header) matching it under
> `fills-library\<PackageName>\TestData\<that exact name>`, with NO comment of any kind (a data
> file has no provenance convention). Then run `ssisx apply-fills --out <out> --fills
> fills-library` and confirm it reports `Applied`.

**A Tier-3 gap (tool doesn't support this shape):**
> This gap is Tier 3 (`MissingToolSupport`). Don't try to work around it -- summarize what SSIS
> feature it is (from the gap's reason text) so I can decide whether it's worth asking for a
> tool enhancement.

**Add more unit tests to an already-green package (not a gap):**
> First confirm `<PackageName>` already builds clean and passes `dotnet test --filter
> Category!=Integration` with zero fills applied -- this is not the gap-fill workflow above. Read
> `<out>\generate\<PackageName>\README.md`'s own "Test coverage notes" section only (not the whole
> `.Tests` project), pick 2-3 rows that are genuinely undercovered, and write one small test file
> per candidate under `fills-library\<PackageName>\MoreTests\<Name>MoreTests.cs` (NOT
> `fills-library\<PackageName>\Tests\` -- that's a different mechanism). Stop and show me what you
> wrote before applying. Once I confirm, run `ssisx apply-tests --out <out> --fills
> fills-library`, then `dotnet build`/`dotnet test --filter Category!=Integration` on
> `<out>\generate\Generated.slnx`, and tell me what changed.

**Sanity-check before running the generator on everything:**
> Before running `generate` without `--package`, tell me how many packages are in
> `<client-folder>` and confirm with me that I actually want to generate all of them right now.

**"Where are the gap/fill files? I don't see them" -- read this before assuming something's
broken:**
> Two different folders in two different places, easy to mix up: `<out>\gaps\<Package>\*.md` are
> auto-generated by `ssisx generate` itself, one file per Tier-1/2 gap, INSIDE the disposable
> `<out>` folder -- these should always exist if `generate-report.md` lists any Tier-1/2 gaps for
> that package. `fills-library\<Package>\*.cs` and `fills-library\<Package>.decisions.json` are
> the OPPOSITE, and live at the `Tools\` root, OUTSIDE `<out>` entirely: `ssisx` never writes
> these, ever -- they start empty and stay empty until a human or Copilot writes an answer into
> them based on a `gaps\` packet. `ssisx apply-fills --out <out> --fills fills-library` reporting
> "0 fills applied" the first time is therefore correct, not a bug -- it means nobody has
> answered a packet yet, not that packets are missing. List `<out>\gaps\<Package>\` AND
> `fills-library\<Package>\` for me and tell me which of these two situations we're actually in
> before concluding anything is broken. (If you see an OLD `out\fills\` folder from before this
> convention existed, that's leftover state -- move anything real in it into `fills-library\`.)

**Review with `svk` before generating (optional):**
> Run `svk walkthrough --spec <out> --out <out>\walkthrough --claims <out>\walkthrough\claims
> --package <PackageName>`, then `svk sampledata --spec <out> --out <out>\sampledata --package
> <PackageName> --rows 20 --seed 42`. Summarize the walkthrough's component count and whether it
> flagged any SSIS-side concurrency, and list which sample-data files were written vs. skipped
> (with the reason). Don't run `generate` yet.

**Run the whole pipeline for one package:**
> Run `/ssisx-rewrite` for package `<PackageName>` against `<client-folder>`, targeting
> `<net8.0 or net10.0>`. Stop and report at both AI boundaries rather than continuing past them
> yourself.
