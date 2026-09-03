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

REM 2. Generate C# -- for ONE package (the normal case on a real portfolio)
%SSISX% generate --input C:\client-packages --out out --recursive --package LoadEmployees

REM 2b. ...or a named handful in one call (comma-separated, no spaces around the commas)
%SSISX% generate --input C:\client-packages --out out --recursive --package LoadEmployees,LoadReferenceData

REM 2c. ...or literally everything, only when that's actually what's wanted
%SSISX% generate --input C:\client-packages --out out --recursive

REM 3. Copy the runtime library alongside the generated output (robocopy works the same in
REM    cmd.exe and PowerShell -- /E copies subfolders including empty ones, /XD excludes any
REM    bin/obj that may exist under the source copy)
robocopy Etl.Core out\generate\Etl.Core /E /XD bin obj

REM 4. Build the generated solution to confirm it compiles
dotnet build out\generate\Generated.slnx
```

Replace `C:\client-packages` with the real folder holding the client's `.dtsx` files, and
`LoadEmployees`/`LoadReferenceData` with real package names from that portfolio (run step 1
first if you don't know them -- they're listed in `out\inventory.csv` and the console output).

**Two `cmd.exe`-specific gotchas, both confirmed by actually running these commands, not
assumed:** each line above must stay on its own line (or its own line in a `.bat` file) -- a
`set X=Y && %X%` chained onto ONE line silently fails, because `cmd.exe` expands `%X%` at
parse time, before `set` has run; typed as separate lines (as above), it works correctly.
Separately, `robocopy`'s own success exit code is `1` ("files copied"), not `0` -- harmless as
written above (nothing here chains it with `&&`), but don't wire it into a script that checks
`if errorlevel 0` expecting the usual convention.

### Sample commands -- PowerShell (equivalent to the above)

```powershell
$ssisx = "SsisExtractor\src\Ssis.Extract.Cli\bin\Debug\net8.0\ssisx.exe"

& $ssisx report --input C:\client-packages --out out --recursive
& $ssisx generate --input C:\client-packages --out out --recursive --package LoadEmployees
& $ssisx generate --input C:\client-packages --out out --recursive --package LoadEmployees,LoadReferenceData
& $ssisx generate --input C:\client-packages --out out --recursive

Copy-Item Etl.Core out\generate\Etl.Core -Recurse -Exclude bin,obj
dotnet build out\generate\Generated.slnx
```

`SsisExtractor\scripts\Verify-GeneratedBuild.ps1` does the generate + Etl.Core-copy + build
steps in one command (`-EtlCorePath ..\..\Etl.Core` from inside `SsisExtractor\scripts\`), for
whichever packages are already under a given `--input` -- PowerShell only, no `cmd.exe` twin
exists for it today (ask if one's needed).

Every `generate` call above exits `3` when it wrote code but there are gaps to review (normal,
not a failure -- read `out\generate-report.md`), `0` when everything generated with zero gaps,
and `2` on a usage error, including a `--package` name that matched nothing.

**`generate`'s job ends at writing buildable C# source.** There is no client database or real
source data available in this environment, so nothing here builds, runs, or verifies the
generated code against real behavior -- that comparison is a separate, dev-only concern (see
`Validation/` at this repo's own root, which is *not* shipped here) with its own captured data
corpus this environment doesn't have. Building (`dotnet build`, step 3 above) to confirm the
code compiles is the expected extent of checking; running it against invented sample data is
not the goal and should not be attempted.

## Running this with GitHub Copilot

Two files exist for this, both under `Tools\` (this folder), neither at the repo root above it:

| File | What it is | How it reaches Copilot |
|---|---|---|
| [`.github/copilot-instructions.md`](.github/copilot-instructions.md) | Short hard rules (don't re-implement the tool, don't generate the whole portfolio unless asked, don't invent a verification step, don't guess at a gap, don't read this repo's own dev-history docs) | **Auto-loaded** by GitHub Copilot Chat for every message, but only when the IDE's open workspace/folder ROOT is `Tools\` itself -- see "Where to open it" below. |
| [`COPILOT_GUIDE.md`](COPILOT_GUIDE.md) | Full command reference, the `--package` workflow, and a library of ready-to-paste prompts | **Not** auto-loaded -- Copilot reads it when told to, or when a prompt below references it. |

### Where to open it (this is the part that actually matters)

`.github/copilot-instructions.md` only auto-loads when it sits at the root of whatever folder
your IDE has open as its workspace. **Open `Tools\` itself as the workspace/folder** --
in VS Code: `File > Open Folder... > D:\PoC\SSIS\Tools` (or wherever this folder ends up on the
client machine) -- not a parent folder containing `Tools\` as a subfolder. If a parent folder
is opened instead, Copilot looks for `.github/copilot-instructions.md` at THAT root, won't find
it, and silently skips it -- no error, it just won't know the rules above.

If you can't change what's open as the workspace root (e.g. Copilot is already running against
the whole client repo, with `Tools\` as one subfolder among others), tell Copilot to read the
file explicitly instead -- either paste its content, drag the file into the chat panel, or (VS
Code Copilot Chat) reference it by typing `#file:Tools/.github/copilot-instructions.md` in your
first message. Either way, **say so in your very first message of the session** -- it does not
carry over from an earlier chat/session automatically the way auto-loaded instructions do.

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
