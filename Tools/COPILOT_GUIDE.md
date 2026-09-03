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
| `.github/copilot-instructions.md` | Hard rules Copilot auto-loads for this workspace. |
| `COPILOT_GUIDE.md` | This file. |

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

That produces `SsisExtractor\src\Ssis.Extract.Cli\bin\Debug\net8.0\ssisx.exe`, relative to this
`Tools\` folder. **Everywhere this guide says `ssisx <command> ...` below, substitute that real
path** (or `dotnet run --project SsisExtractor\src\Ssis.Extract.Cli -- <command> ...` if you'd
rather skip the separate build step). Do not guess at a different path, and do not assume
`ssisx` resolves on its own -- if a command fails with "not recognized", the build step above
was skipped or `Tools\` isn't the current directory.

## What `ssisx generate` does and does NOT do

**Does:** reads one or more `.dtsx` packages and writes a runnable C# console-app project per
package under `<out>\generate\<PackageName>\` -- entity classes, a DbContext, row-reader/row-
writer code, translated Derived Column expressions, `Program.cs`, `.csproj`/`appsettings.json`.
Also writes `Directory.Build.props`/`Directory.Packages.props`/`Generated.slnx` once per `--out`
run, a `generate-report.md` naming every gap, and (new) a self-contained
**`<out>\HOW-TO-FILL-GAPS.md`** at the output root every run -- if you (or a later Copilot
session) end up working from the generated solution directly with no other doc in view, open
that file first; it's written specifically to not assume anything else here is visible.

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

## What's disposable and what isn't -- fills live OUTSIDE `out\`, permanently

**The short version: Tier-1/Tier-2 work never lives inside `out\` at all.** It lives at
`fills-library\<Package>\` -- a folder at the `Tools\` root, a sibling of `SsisExtractor\`/
`Etl.Core\`/`out\`, already tracked in git. Every `generate` and `apply-fills` call in this guide
now includes `--fills fills-library` for exactly this reason -- **never omit that flag, and
never write a fill anywhere under `out\`.**

| Path | Disposable? | What it is |
|---|---|---|
| `out\` (all of it, including any `out\fills\` you might see from an old run) | **Yes -- delete freely, including the whole folder** | Everything under `--out` is regenerated fresh by `ssisx report`/`generate`. Nothing here is hand-maintained once `--fills fills-library` is used consistently. |
| `fills-library\` (at the `Tools\` root, NOT under `out\`) | **No -- never delete this. Commit it to git.** | Hand-ported Tier-2 Script Task/Component logic, and Tier-1 `*.decisions.json` answers. `ssisx` never writes to it, and it survives every `out` deletion/regeneration because it was never inside `out` to begin with. |
| `out\conformance\claims\` | **No -- never delete this** | Human sign-off on gate-1 obligations. Unlike fills, this one genuinely does default to living under `--out` (via `--claims`) -- if you want it durable too, point `--claims` at a folder outside `out\` the same way, e.g. `--claims claims-library`. |

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

**If you ever want a clean regenerate:** delete the whole `out\` folder freely -- it's now
genuinely 100% disposable, since nothing durable lives inside it anymore. Just make sure your
next `generate`/`apply-fills` call still includes `--fills fills-library`. If a rebuild shows
`CS8795` errors for seams you already filled, the first thing to check is whether
`fills-library\<Package>\*.cs` still exists on disk and whether the command you ran actually
included `--fills fills-library` -- not whether the port itself was wrong. See the "CS8795"
prompt below for the exact recovery steps. **After writing or editing anything under
`fills-library\`, commit it** (`git add fills-library && git commit`) -- an uncommitted fill is
still one accidental folder-delete away from being lost, same as before this fix, just one step
removed.

## Prompts you can paste into Copilot Chat

These assume Copilot has this workspace open and has already read
`.github/copilot-instructions.md` (automatic). Replace `<client-folder>` with the real path to
the client's `.dtsx` files, and `<out>` with wherever you want output written (e.g. `out`).

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
> include `--fills fills-library`. Never delete `fills-library\` to "start clean" -- deleting
> `<out>` entirely is fine and expected, deleting `fills-library\` is not.

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

**A Tier-3 gap (tool doesn't support this shape):**
> This gap is Tier 3 (`MissingToolSupport`). Don't try to work around it -- summarize what SSIS
> feature it is (from the gap's reason text) so I can decide whether it's worth asking for a
> tool enhancement.

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
