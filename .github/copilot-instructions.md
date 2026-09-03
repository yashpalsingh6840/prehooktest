# Repo custom instructions -- read this before doing anything

This workspace contains **`ssisx`**, a working, already-built SSIS-to-C# extractor and
generator, plus **`Etl.Core`**, the hand-written runtime library generated code depends on.
Full reference: [`COPILOT_GUIDE.md`](../COPILOT_GUIDE.md) (same folder as this file's parent).
Read that before starting any non-trivial task -- it has the full command reference, workflow,
and a library of ready-to-use prompts. This file is the short list of hard rules.

## Hard rules -- do not violate these

1. **Never re-implement, patch, or "improve" `ssisx` or `Etl.Core` to do the actual
   extraction/generation work differently.** These are already built and tested. Your job is
   to **run the CLI** (`ssisx report`, `ssisx generate`, `ssisx apply-fills`, ...) and to
   **fill in human-facing gaps it explicitly asks for** (Tier-1 decisions, Tier-2 seams -- see
   below). If a command errors, read the error message and the linked doc section first --
   do not guess a fix by editing the tool's own source.
2. **Never run `ssisx generate` (or `report`/`conformance`/`testgen`) against the WHOLE
   portfolio unless explicitly asked.** Use `--package <name>` (repeatable, or comma-separated:
   `--package A,B,C`) to scope to exactly the package(s) you were asked about. `extract` and
   `report` are cheap and read-only, so running them across everything once is fine and useful
   (it's how you find out what package names exist); `generate` is the one to keep scoped,
   since it is what a real engagement runs one-package-at-a-time.
3. **Do not attempt to verify generated code against a live database, real input files, or a
   real SSIS run.** This environment has none of those -- no client SQL Server, no client
   source files, no client SSISDB. `ssisx generate`'s job ends at writing buildable C# source.
   The most you should do is `dotnet build` the generated solution to confirm it compiles.
   Never invent connection strings, seed data, or "sample" input files to make something
   "runnable" -- that is out of scope here, and doing so risks shipping code nobody has
   actually checked against real behavior.
4. **A `GenerationGap` is not a bug to silently work around.** Every gap in
   `generate-report.md`/`gaps.json` has a stable `GapId`, a tier, and (for Tier 1/2) a work
   packet under `gaps/<Package>/`. Read the packet. Do not invent a value or logic that the
   packet says is missing -- that is exactly the "gap not guess" property this tool exists to
   protect. See "Working a gap" below for the actual, correct procedure per tier.
5. **Never hand-edit anything under `<out>/generate/`.** That whole tree is disposable and
   regenerated on every `ssisx generate` run -- any edit there is silently lost the next time
   someone runs the command. Tier-2 work goes in `<out>/fills/<Package>/*.cs` instead (see
   below), which `ssisx` only ever reads.
6. **Never invent an internal/example path.** Always resolve `--input`/`--out`/`--fills` from
   what the user actually gave you (a real client folder path), never a placeholder like
   `D:\Something\SSIS` copied from a doc. If you are not sure what folder to point at, ask.
7. **Budget your own reads.** Do not open `CLAUDE.md`, `Phase0-Extractor-Plan.md`,
   `Migration-Validation-Plan.md`, or `docs/report-schema.md` -- they are this project's own
   multi-hundred-KB development history, not written for you, and reading them will burn your
   context for no benefit. Everything you need to operate this tool is in `COPILOT_GUIDE.md`
   and `ssisx --help`. If a command's own `--help` output and the guide disagree, trust
   `--help` -- it is generated from the same code that runs.

## The one-sentence workflow

**Survey once (cheap, whole portfolio, read-only) -> generate one package (or a named few) at
a time -> read its gaps -> either wait for a human decision/fill, or move to the next package.**

`ssisx` is not on PATH -- build it once (`cd SsisExtractor && dotnet build SsisExtractor.slnx -c
Debug && cd ..`, from this `Tools\` folder), then call the built exe directly:
`SsisExtractor\src\Ssis.Extract.Cli\bin\Debug\net8.0\ssisx.exe`. Full detail in
`COPILOT_GUIDE.md`'s own "Invoking ssisx" section -- read that before running anything if this
isn't already clear.

```powershell
$ssisx = "SsisExtractor\src\Ssis.Extract.Cli\bin\Debug\net8.0\ssisx.exe"

# 1. Survey the whole portfolio once -- lists every package name, complexity, and how many
#    would generate cleanly today. Safe to run against everything; writes nothing but reports.
& $ssisx report --input <client-folder> --out out --recursive

# 2. Generate ONE named package (the normal case)
& $ssisx generate --input <client-folder> --out out --recursive --package LoadEmployees

# 2b. Or a named few in one call
& $ssisx generate --input <client-folder> --out out --recursive --package LoadEmployees,LoadReferenceData
```

Exit code `3` from `generate` means "wrote code, but there are gaps" -- that is normal and
expected, not a failure. Read `out\generate-report.md`.

## Working a gap (Tier 1 / 2 / 3 -- do the right thing for each)

- **Tier 3 (`MissingToolSupport`)** -- the tool itself doesn't model this SSIS component/shape
  yet. **There is nothing for you to fill in.** Do not attempt a workaround. Report it back to
  the human as "this package needs a tool enhancement," naming the `GapId`.
- **Tier 1 (`MissingDatum`)** -- a fact is missing from the `.dtsx` (e.g. an unresolvable Lookup
  join key) that a human must confirm. Read the work packet under `gaps/<Package>/`, answer its
  exact question in `out\fills\<Package>.decisions.json` in the format the packet shows, with a
  real `ConfirmedBy`. **Do not fabricate a confident-sounding answer yourself** -- if you are
  not certain, present the packet's question to the human instead of guessing at a value that
  looks plausible.
- **Tier 2 (`MissingLogic`)** -- a Script Task/Script Component's real source IS in the `.dtsx`;
  it just needs porting to C#. Read the work packet (it includes the actual script text), write
  the `.cs` file it describes under `out\fills\<Package>\`, following the exact partial-class
  seam signature the packet gives you (do not change the signature). Then run
  `ssisx apply-fills --out out` to copy it in and see what, if anything, is still outstanding.
  Add the `// ssisx-fill: GapId=... Author=... Date=... EvidenceSha256=...` comment the packet
  asks for -- copy the `EvidenceSha256` value from the packet verbatim.

## Getting unstuck

Run the failing command with no other changes and read its own error text -- this tool is built
to fail with a specific, actionable message rather than a stack trace. `ssisx --help` is always
accurate (it is printed from the same binary you just ran). If you are still stuck, stop and ask
the human rather than working around it by editing generated code or the tool's own source.
