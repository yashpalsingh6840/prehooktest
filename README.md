# Tools

Everything a client site needs to extract, assess, and generate C# from its own SSIS
packages -- self-contained, with no dependency on any other repo. Two things live here, for
two different reasons:

| Folder | What it is | Why it's here |
|---|---|---|
| [`SsisExtractor/`](SsisExtractor/README.md) | `ssisx` -- the extractor/report/conformance/generator CLI (its own solution, `SsisExtractor.slnx`) | The tool itself: parses raw `.dtsx` XML (no GAC, no SSISDB, no live SSIS install needed) and turns it into inventory, findings, conformance rules, and generated C#. |
| `Etl.Core/` | The hand-written runtime library every `ssisx generate` output targets (`SqlBulkSink`, `CsvRowSource`, `PackageRunner`, the notification/hosting plumbing, etc.) | `ssisx generate` deliberately does not produce this -- it's written once, not derived from any `.dtsx` (see `GenerateCommand.WriteFixedFiles`'s own doc comment). A generated package needs a copy of it alongside its own project to build at all. |

## Using this on a client's own packages

Two ways to invoke it: `dotnet run --project ...` (rebuilds if needed, slower per call, no
build step to remember) or build once and call the `.exe` directly (faster for repeated calls
against a real portfolio -- the normal case). Both work identically from `cmd.exe` or
PowerShell; only step 3's copy command differs between shells (both variants given below).

### One-time setup (either shell)

```
cd Tools\SsisExtractor
dotnet build SsisExtractor.slnx -c Debug
cd ..
```

The built CLI is now at `SsisExtractor\src\Ssis.Extract.Cli\bin\Debug\net8.0\ssisx.exe`, relative
to this `Tools\` folder. The commands below assume you're running from `Tools\` and use that
path directly -- adjust it if you `cd` elsewhere. (`dotnet run --project
SsisExtractor\src\Ssis.Extract.Cli -- <args>` works too, without the separate build step, if you
prefer -- e.g. `dotnet run --project SsisExtractor\src\Ssis.Extract.Cli -- report --input
<client .dtsx folder> --out out --recursive`.)

### Sample commands -- `cmd.exe` (no `.\` prefix needed; each line stands alone)

```bat
set SSISX=SsisExtractor\src\Ssis.Extract.Cli\bin\Debug\net8.0\ssisx.exe

REM 1. See what's there and how ready it is (no client SSISDB/SSIS install access needed)
%SSISX% report --input C:\client-packages --out out --recursive

REM 2. Generate C# -- for ONE package (the normal case on a real portfolio). --etl-core
REM    copies the runtime library into out\generate\Etl.Core AS PART OF THIS COMMAND, and
REM    --fills fills-library points every Tier-1/2 answer at the DURABLE fills library
REM    (a folder at the Tools\ root, tracked in git -- NOT out\fills, which is disposable).
REM    Pass BOTH flags every time, or the generated solution won't build (--etl-core) and any
REM    hand-ported Tier-2 work you write later will be lost the next time out\ is regenerated
REM    (--fills) -- see "If a work packet or a fill seems missing" below.
%SSISX% generate --input C:\client-packages --out out --recursive --package LoadEmployees --etl-core Etl.Core --fills fills-library

REM 2b. ...or a named handful in one call (comma-separated, no spaces around the commas)
%SSISX% generate --input C:\client-packages --out out --recursive --package LoadEmployees,LoadReferenceData --etl-core Etl.Core --fills fills-library

REM 2c. ...or literally everything, only when that's actually what's wanted
%SSISX% generate --input C:\client-packages --out out --recursive --etl-core Etl.Core --fills fills-library

REM 3. Build the generated solution to confirm it compiles
dotnet build out\generate\Generated.slnx
```

Replace `C:\client-packages` with the real folder holding the client's `.dtsx` files, and
`LoadEmployees`/`LoadReferenceData` with real package names from that portfolio (run step 1
first if you don't know them -- they're listed in `out\inventory.csv` and the console output).
`Etl.Core`/`fills-library` above are relative paths from wherever you're running the command
(this example assumes you're in `Tools\`, same folder as both `Etl.Core\` and `fills-library\`,
per the one-time setup above).

**A `cmd.exe`-specific gotcha, confirmed by actually running these commands, not assumed:**
each line above must stay on its own line (or its own line in a `.bat` file) -- a
`set X=Y && %X%` chained onto ONE line silently fails, because `cmd.exe` expands `%X%` at
parse time, before `set` has run. Typed as separate lines (as above), it works correctly.

### Sample commands -- PowerShell (equivalent to the above)

```powershell
$ssisx = "SsisExtractor\src\Ssis.Extract.Cli\bin\Debug\net8.0\ssisx.exe"

& $ssisx report --input C:\client-packages --out out --recursive
& $ssisx generate --input C:\client-packages --out out --recursive --package LoadEmployees --etl-core Etl.Core --fills fills-library
& $ssisx generate --input C:\client-packages --out out --recursive --package LoadEmployees,LoadReferenceData --etl-core Etl.Core --fills fills-library
& $ssisx generate --input C:\client-packages --out out --recursive --etl-core Etl.Core --fills fills-library

dotnet build out\generate\Generated.slnx

# After writing/editing a Tier-2 fill under fills-library\<Package>\*.cs (never out\fills\):
& $ssisx apply-fills --out out --fills fills-library
```

`SsisExtractor\scripts\Verify-GeneratedBuild.ps1` does the generate + build steps in one
command (`-EtlCorePath ..\..\Etl.Core` from inside `SsisExtractor\scripts\`), for whichever
packages are already under a given `--input` -- PowerShell only, no `cmd.exe` twin exists for
it today (ask if one's needed).

**Targeting .NET 8 instead of .NET 10:** add `--framework net8.0` to any `generate` call above.
This is not just a TargetFramework swap -- Etl.Core's EF Core SqlServer provider (10.0.11)
only targets net10.0, so `--framework net8.0` also pins a different EF Core MAJOR version
(9.0.15, the newest that still targets net8.0) for that run. `--etl-core` still copies the
runtime library in, and now also rewrites its two props files to match whichever `--framework`
you asked for, so the copied `Etl.Core` and the generated packages always agree. Verified
end-to-end (not just "it compiles the flag"): generated + built real packages against
net8.0/EF Core 9.0.15 -- including Excel Source, fixed-width flat files, and a secondary
database connection -- 0 warnings/0 errors, same as the net10.0 default. Omitting `--framework`
changes nothing; net10.0 stays the default.

```
%SSISX% generate --input C:\client-packages --out out --recursive --package LoadEmployees --etl-core Etl.Core --fills fills-library --framework net8.0
```

Every `generate` call above exits `3` when it wrote code but there are gaps to review (normal,
not a failure -- read `out\generate-report.md`), `0` when everything generated with zero gaps,
and `2` on a usage error, including a `--package` name that matched nothing. **If `--etl-core`
itself is missing or wrong**, that's also reported as a gap in `generate-report.md` (search for
`Etl.Core`) rather than failing silently -- if you ever see "Etl.Core (not found)" in an IDE's
Solution Explorer after generating, it means either `--etl-core` was omitted or its path was
wrong; check the report, not the IDE, to find out which.

### If a work packet or a fill seems missing -- two different folders, in two different places

- **`out\gaps\<Package>\*.md`** -- auto-generated, every `generate` run, one file per Tier-1/2
  gap, INSIDE the disposable `out\` folder. If these are missing for a package that has gaps,
  something is actually wrong (an old `ssisx.exe` build, or a `--package` name that didn't
  match) -- check `out\gaps.json` and `out\generate-report.md` first.
- **`fills-library\<Package>\*.cs`** and **`fills-library\<Package>.decisions.json`** -- at the
  `Tools\` root, a sibling of `out\`, tracked in git. These are **never** written by `ssisx`.
  They start out genuinely empty, and stay empty until a human (or Copilot, working from a
  `gaps\` work packet) writes into them. `ssisx apply-fills --out out --fills fills-library`
  reporting "0 fills applied" the first time you run it is the **expected, correct** result of
  nobody having answered a packet yet -- not a bug, and not something `--etl-core`-style
  automation should try to paper over, since a Tier-1/2 gap is real work someone has to do.

**Why `fills-library\` is a separate top-level folder and not `out\fills\`:** it used to be
`out\fills\`, and that caused real, already-verified Tier-2 work to be silently lost more than
once, because `out\` is gitignored and gets deleted/regenerated freely across sessions -- there
was no durable trace of the fills left once that happened, and the only symptom was a confusing
`CS8795` build error much later with no obvious link back to a missing folder. Moving fills
outside `out\` means the entire `out\` folder is now genuinely, unconditionally disposable --
delete it and regenerate freely, as long as every `generate`/`apply-fills` call still includes
`--fills fills-library`. **Commit `fills-library\` to git** once a fill is confirmed working
(`git add fills-library && git commit`) -- that is what makes it permanent, not just "outside
`out\`."

**`generate`'s job ends at writing buildable C# source.** There is no client database or real
source data available in this environment, so nothing here builds, runs, or verifies the
generated code against real behavior -- that comparison is a separate, dev-only concern (see
`Validation/` at this repo's own root, which is *not* shipped here) with its own captured data
corpus this environment doesn't have. Building (`dotnet build`, step 3 above) to confirm the
code compiles is the expected extent of checking; running it against invented sample data is
not the goal and should not be attempted.

## Running this with GitHub Copilot

Three files exist for this -- two under `Tools\` itself, and one written FRESH into every
`--out` folder by `generate`, specifically so it's visible from wherever the generated output
actually gets opened later, not just from `Tools\`:

| File | What it is | How it reaches Copilot |
|---|---|---|
| [`.github/copilot-instructions.md`](.github/copilot-instructions.md) | Short hard rules (don't re-implement the tool, don't generate the whole portfolio unless asked, always pass `--etl-core`, don't invent a verification step, don't confuse `gaps/` with `fills/`, don't read this repo's own dev-history docs) | **Auto-loaded**, but only when the IDE's open workspace root is `Tools\` itself -- see "Where to open it" below. Least reliable of the three in practice. |
| [`COPILOT_GUIDE.md`](COPILOT_GUIDE.md) | Full command reference, the `--package`/`--etl-core` workflow, and a library of ready-to-paste prompts | **Not** auto-loaded -- Copilot reads it when told to. |
| **`<out>\HOW-TO-FILL-GAPS.md`** | Self-contained -- lists every open Tier-1/2 gap with its exact `GapId` and packet path, and the exact procedure to answer each, with nothing assumed about what else is in view | Written by every `generate` run, at the OUTPUT ROOT -- sits right next to `generate\`, `gaps\`, `fills\`. **This is the one to point Copilot at when working from the generated solution itself, e.g. in Visual Studio.** |

### If you're working from Visual Studio (or opened the generated `.slnx` directly)

**This is very likely what actually happened if gap-filling "isn't working": the generated
solution (`Generated.slnx`) doesn't contain `Tools\.github\copilot-instructions.md` at all** --
it's a separate folder tree, and a `.slnx`/Solution Explorer only shows `.csproj`-referenced
files, so `gaps\` and `fills\` (plain folders, not part of any project) don't even appear in
Solution Explorer by default. Auto-loaded instructions never had a chance to apply here, and
Copilot has no reason to know those folders exist unless told.

**Fix: open `<out>\HOW-TO-FILL-GAPS.md` yourself (File > Open > File, or drag it into the Copilot
Chat panel) and tell Copilot to work from it.** It names every open gap, exactly where its work
packet is, and exactly what to write and where -- self-contained, no dependency on `Tools\`
being in view at all. A good first message in that chat:

> Read `HOW-TO-FILL-GAPS.md` (in this same output folder). Pick the first gap in its table, open
> its work packet, and tell me what it's asking for before writing anything.

### Where to open it, for the other two files (this is the part that actually matters there)

`.github/copilot-instructions.md` only auto-loads when it sits at the root of whatever folder
your IDE has open as its workspace. **Open `Tools\` itself as the workspace/folder** --
in VS Code: `File > Open Folder... > D:\PoC\SSIS\Tools` (or wherever this folder ends up on the
client machine) -- not a parent folder containing `Tools\` as a subfolder, and not the generated
solution either (see above). If a parent folder is opened instead, Copilot looks for
`.github/copilot-instructions.md` at THAT root, won't find it, and silently skips it -- no
error, it just won't know the rules above.

If you can't change what's open as the workspace root, tell Copilot to read the file explicitly
instead -- either paste its content, drag the file into the chat panel, or (VS Code Copilot
Chat) reference it by typing `#file:Tools/.github/copilot-instructions.md` in your first
message. Either way, **say so in your very first message of the session** -- it does not carry
over from an earlier chat/session automatically the way auto-loaded instructions do.

### Getting started

Once the instructions file is in play (auto-loaded or explicitly referenced), open Copilot
Chat and start with:

> Read `COPILOT_GUIDE.md` in this folder, then run `ssisx report --input <client .dtsx folder>
> --out out --recursive` and summarize what's there -- package count, complexity, and how many
> would generate cleanly today.

From there, [`COPILOT_GUIDE.md`](COPILOT_GUIDE.md)'s own "Prompts you can paste into Copilot
Chat" section has the rest (generate one package, generate a named batch, work a gap, confirm a
build) -- copy them as-is, filling in the real client folder path and package name(s).

`Etl.Core/` here is a **portable copy** -- see this project's own internal dev notes for where
it's actually developed and how to refresh it; that's not this file's concern, since this
folder is meant to be handed to a client as-is.

## What's deliberately not here

The gate-3 golden-corpus comparison harness lives at the repo root's own `Validation/` folder,
not here -- it's a dev-time tool that proves *this project's own* generator output matches
*this project's own* real SSIS runs, tied to a corpus captured from a specific development
box's SSISDB. A client doesn't run it; it's not part of what this folder exists to ship.
