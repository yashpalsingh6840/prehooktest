# Using `ssisx` with GitHub Copilot

This is the detailed reference behind [`.github/copilot-instructions.md`](.github/copilot-instructions.md)
(that file is what Copilot auto-loads for every chat; this one is what a human -- or Copilot,
when it needs more detail -- reads for the full picture). If you only read one section, read
["Prompts you can paste into Copilot Chat"](#prompts-you-can-paste-into-copilot-chat) below.

## What's actually in this folder

| Folder | What it is |
|---|---|
| `SsisExtractor/` | `ssisx` -- the CLI itself (extract, report, conformance, testgen, diff, generate, apply-fills). Already built and tested; you run it, you don't modify it. |
| `Etl.Core/` | The hand-written C# runtime library every generated project depends on (SQL bulk-copy, CSV reading, the package-run host, email notification). `ssisx generate` does not produce this -- copy it alongside generated output before building. |
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
run, and a `generate-report.md` naming every gap.

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

## Command reference (see `ssisx --help` for the authoritative, always-current version)

```powershell
# See every package name + how complex/ready each one is. Read-only. Safe on everything.
ssisx report --input <folder> --out out --recursive

# Generate exactly one package
ssisx generate --input <folder> --out out --recursive --package LoadEmployees

# Generate a named handful
ssisx generate --input <folder> --out out --recursive --package LoadEmployees,LoadReferenceData

# Generate literally everything (only when asked to)
ssisx generate --input <folder> --out out --recursive

# See gate-1 obligations (what a rewrite owes) for one package
ssisx conformance --input <folder> --out out --package LoadEmployees

# After writing a Tier-2 fill file under out\fills\<Package>\*.cs:
ssisx apply-fills --out out
```

`ssisx generate` exits:
- `0` -- every requested package generated with zero gaps.
- `3` -- code was written, but one or more gaps exist (**normal, expected** -- read the report).
- `2` -- usage error (bad flag, unreadable input, `--package` name matched nothing).

## Building what was generated

```powershell
Copy-Item Etl.Core out\generate\Etl.Core -Recurse -Exclude bin,obj
dotnet build out\generate\Generated.slnx
```

A clean build with zero warnings/errors is the expected outcome for a package with zero gaps.
A package with open gaps will fail to build with `CS8795` (an unimplemented seam) until its
Tier-2 fills are written and applied -- **that failure is intentional**, not something to patch
around; see "Working a gap".

## Prompts you can paste into Copilot Chat

These assume Copilot has this workspace open and has already read
`.github/copilot-instructions.md` (automatic). Replace `<client-folder>` with the real path to
the client's `.dtsx` files, and `<out>` with wherever you want output written (e.g. `out`).

**First look at a portfolio:**
> Run `ssisx report --input <client-folder> --out <out> --recursive`. Don't generate anything
> yet. Summarize: how many packages, which ones look simplest, and how many would generate with
> zero blocking gaps today according to `generation-readiness.md`.

**Generate one specific package:**
> Run `ssisx generate --input <client-folder> --out <out> --recursive --package <PackageName>`.
> Then read `<out>\generate-report.md` for that package and tell me, in plain terms, exactly
> what gaps (if any) it has and what tier each one is.

**Generate a named batch, not everything:**
> Run `ssisx generate` scoped with `--package` to just these packages: <list>. Do not run it
> against the rest of the portfolio.

**Check a package actually compiles:**
> Copy `Etl.Core` into `<out>\generate\Etl.Core`, then `dotnet build <out>\generate\Generated.slnx`
> and show me the result. Do not attempt to run the built executable against any database.

**Work a Tier-2 gap (Script Task/Component port):**
> Open the work packet at `<out>\gaps\<PackageName>\<GapId>.md`. Write the fill it describes
> under `<out>\fills\<PackageName>\<File>.cs`, matching its exact seam signature, with the
> `// ssisx-fill:` provenance comment filled in from the packet. Then run
> `ssisx apply-fills --out <out>` and tell me what it reports.

**Work a Tier-1 gap (a missing datum, e.g. a Lookup join key):**
> Open the work packet at `<out>\gaps\<PackageName>\<GapId>.md`. Do not guess the answer
> yourself. Show me its exact question so I can confirm it, then write my answer into
> `<out>\fills\<PackageName>.decisions.json` in the format the packet specifies.

**A Tier-3 gap (tool doesn't support this shape):**
> This gap is Tier 3 (`MissingToolSupport`). Don't try to work around it -- summarize what SSIS
> feature it is (from the gap's reason text) so I can decide whether it's worth asking for a
> tool enhancement.

**Sanity-check before running the generator on everything:**
> Before running `generate` without `--package`, tell me how many packages are in
> `<client-folder>` and confirm with me that I actually want to generate all of them right now.
