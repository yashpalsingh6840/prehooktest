# Repo custom instructions -- read this before doing anything

This workspace contains **`ssisx`**, a working, already-built SSIS-to-C# extractor and
generator, plus **`Etl.Core`**, the hand-written runtime library generated code depends on.
Full reference: [`COPILOT_GUIDE.md`](../COPILOT_GUIDE.md) (same folder as this file's parent).
Read that before starting any non-trivial task -- it has the full command reference, workflow,
and a library of ready-to-use prompts. This file is the short list of hard rules.

**If you're reading this from inside a GENERATED output folder instead** (e.g. someone opened
`Generated.slnx` directly and this file happened to be reachable) -- stop, and read
`HOW-TO-FILL-GAPS.md` at that output folder's own root instead. It is self-contained and
written specifically for that situation; this file assumes `Tools\` itself is what's open.

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
3. **Always pass `--etl-core Etl.Core` on every `ssisx generate` call, no exceptions.** Without
   it, the generated solution references a project (`Etl.Core.csproj`) that was never copied in
   and will not build -- an IDE opening it will show "Etl.Core (not found)". This is not
   hypothetical: it is the exact mistake that happened running this tool through Copilot before
   this rule was written down. `--etl-core` copies the folder in as part of the same command, so
   there is no separate manual step to remember or skip.
3a. **The generated code targets .NET 10.0 by default. If the client's own runtime is .NET
   8.0, add `--framework net8.0` to every `generate` call for that engagement** (not just the
   first one -- it is not sticky across calls). This is not a cosmetic flag: it also pins a
   different major version of EF Core (9.0.15 instead of 10.0.11) inside the generated
   `Directory.Packages.props`, because EF Core's SqlServer provider for 10.0.11 only runs on
   .NET 10 at all -- `--etl-core` automatically copies the matching props into `Etl.Core` too, so
   never try to reconcile the two by hand. If you don't know which .NET version the client
   targets, ask before generating -- don't guess net10.0 as a safe default, since a build that
   doesn't run there is a much worse failure to discover late than asking up front.
4. **Do not attempt to verify generated code against a live database, real input files, or a
   real SSIS run.** This environment has none of those -- no client SQL Server, no client
   source files, no client SSISDB. `ssisx generate`'s job ends at writing buildable C# source.
   The most you should do is `dotnet build` the generated solution to confirm it compiles.
   Never invent connection strings, seed data, or "sample" input files to make something
   "runnable" -- that is out of scope here, and doing so risks shipping code nobody has
   actually checked against real behavior.
5. **A `GenerationGap` is not a bug to silently work around.** Every gap in
   `generate-report.md`/`gaps.json` has a stable `GapId`, a tier, and (for Tier 1/2) a work
   packet under `gaps/<Package>/`. Read the packet. Do not invent a value or logic that the
   packet says is missing -- that is exactly the "gap not guess" property this tool exists to
   protect. See "Working a gap" below for the actual, correct procedure per tier.
6. **`gaps/<Package>/*.md` and the fills library are opposite things -- do not confuse them.**
   `<out>/gaps/` is auto-written by `ssisx generate` itself (questions/work packets) and lives
   INSIDE the disposable `<out>` folder. The fills library is the OPPOSITE: never written by
   `ssisx`, and it lives OUTSIDE `<out>` on purpose (see rule 7a). `ssisx apply-fills` reporting
   "0 fills applied" on a brand-new `<out>` is the correct, expected result of nobody having
   answered a packet yet, not a sign anything is broken. Check `<out>/gaps/<Package>/` for the
   actual work packets before concluding they're missing.
7. **Never hand-edit anything under `<out>/generate/`.** That whole tree is disposable and
   regenerated on every `ssisx generate` run -- any edit there is silently lost the next time
   someone runs the command. Tier-2 work goes in the fills library instead (see rule 7a), which
   `ssisx` only ever reads.
7a. **ALL Tier-1/Tier-2 fill work lives in `fills-library\<Package>\` at the `Tools\` ROOT --
   never inside `<out>\fills\`, and `--out out` alone is NEVER enough.** Every `generate` and
   `apply-fills` call MUST include `--fills fills-library` explicitly (a path relative to
   `Tools\`, a sibling of `SsisExtractor\`/`Etl.Core\`/`out\`, already git-tracked -- see the
   one-sentence workflow below for the exact commands). This is not a style preference, it is a
   fix for a real, repeated incident: relying on the DEFAULT fills location
   (`<out>/fills/`, inside the `out` folder) caused real, already-verified Tier-2 work to be
   silently lost **more than once**, because `<out>` is gitignored, disposable-LOOKING, and gets
   deleted/recreated across sessions/iterations whenever anyone (including you) starts a "clean"
   `report`/`generate` run against a fresh `--out`. Each time that happened, nothing crashed
   immediately -- `ssisx apply-fills` just quietly reported "0 fills applied," and the real
   symptom only surfaced much later as confusing `CS8795` build errors with no obvious link back
   to a missing folder. Storing fills at `fills-library\` (outside `out\`, committed to git)
   makes them survive EVERY `out` deletion/regeneration automatically -- there is no longer a
   "did I remember to preserve this" step for you to skip. Concretely:
   - Writing a Tier-2 fill: create it at `fills-library\<Package>\<File>.cs`, never
     `out\fills\<Package>\<File>.cs`.
   - Answering a Tier-1 decision: write it to `fills-library\<Package>.decisions.json`, never
     `out\fills\<Package>.decisions.json`.
   - Every `generate` call: add `--fills fills-library`.
   - Every `apply-fills` call: add `--fills fills-library`.
   - After creating or editing anything under `fills-library\`, tell the human it needs
     `git add fills-library && git commit` -- an uncommitted fill is still one bad `git clean`/
     folder-reset away from being lost again, and only a commit makes it truly permanent.
   - If you ever find real work already sitting under `<out>\fills\` (from before this rule, or
     from a session that didn't follow it), move it into `fills-library\` and re-run
     `apply-fills --out out --fills fills-library` to confirm it still applies -- do not leave it
     stranded in the disposable location.
   - Deleting `<out>` entirely (or just `<out>\generate\`) for a clean regenerate is always safe
     now, precisely because the fills no longer live inside it.
8. **Never invent an internal/example path.** Always resolve `--input`/`--out`/`--fills`/
   `--etl-core` from what the user actually gave you (a real client folder path), never a
   placeholder like `D:\Something\SSIS` copied from a doc. If you are not sure what folder to
   point at, ask.
9. **Budget your own reads.** Do not open `CLAUDE.md`, `Phase0-Extractor-Plan.md`,
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

# 2. Generate ONE named package (the normal case) -- --etl-core and --fills are BOTH required
#    every time, no exceptions: omit --etl-core and it will not build; omit --fills and any
#    Tier-2 work you write later goes into the disposable <out> folder instead of the durable
#    library and WILL be lost the next time <out> is regenerated. net10.0 is the default, add
#    --framework net8.0 if that's what the client runs.
& $ssisx generate --input <client-folder> --out out --recursive --package LoadEmployees --etl-core Etl.Core --fills fills-library

# 2b. Or a named few in one call, targeting .NET 8 instead of the net10.0 default
& $ssisx generate --input <client-folder> --out out --recursive --package LoadEmployees,LoadReferenceData --etl-core Etl.Core --fills fills-library --framework net8.0

# 3. After writing/editing any Tier-2 fill under fills-library\<Package>\*.cs (never
#    out\fills\<Package>\*.cs -- see rule 7a), apply it with the SAME --fills path:
& $ssisx apply-fills --out out --fills fills-library
```

Exit code `3` from `generate` means "wrote code, but there are gaps" -- that is normal and
expected, not a failure. Read `out\generate-report.md`.

## Working a gap (Tier 1 / 2 / 3 -- do the right thing for each)

- **Tier 3 (`MissingToolSupport`)** -- the tool itself doesn't model this SSIS component/shape
  yet. **There is nothing for you to fill in.** Do not attempt a workaround. Report it back to
  the human as "this package needs a tool enhancement," naming the `GapId`.
- **Tier 1 (`MissingDatum`)** -- a fact is missing from the `.dtsx` (e.g. an unresolvable Lookup
  join key) that a human must confirm. Read the work packet under `out\gaps\<Package>\`, answer
  its exact question in `fills-library\<Package>.decisions.json` (NOT `out\fills\...` -- see
  rule 7a) in the format the packet shows, with a real `ConfirmedBy`. **Do not fabricate a
  confident-sounding answer yourself** -- if you are not certain, present the packet's question
  to the human instead of guessing at a value that looks plausible.
- **Tier 2 (`MissingLogic`)** -- a Script Task/Script Component's real source IS in the `.dtsx`;
  it just needs porting to C#. Read the work packet (it includes the actual script text), write
  the `.cs` file it describes under `fills-library\<Package>\` (NOT `out\fills\...` -- see rule
  7a), following the exact partial-class seam signature the packet gives you (do not change the
  signature). Then run `ssisx apply-fills --out out --fills fills-library` to copy it in and see
  what, if anything, is still outstanding. Add the
  `// ssisx-fill: GapId=... Author=... Date=... EvidenceSha256=...` comment the packet asks for
  -- copy the `EvidenceSha256` value from the packet verbatim. Remind the human to
  `git add fills-library && git commit` once the fill is confirmed working.

## Getting unstuck

Run the failing command with no other changes and read its own error text -- this tool is built
to fail with a specific, actionable message rather than a stack trace. `ssisx --help` is always
accurate (it is printed from the same binary you just ran). If you are still stuck, stop and ask
the human rather than working around it by editing generated code or the tool's own source.

**Build fails with `CS8795` ("must have an implementation part")?** That is an unfilled Tier-2
seam -- working as designed, not a tool bug. Before writing anything: run
`ssisx apply-fills --out out --fills fills-library` and read what it reports. If it says "0
fills applied" for a package you already ported, **check whether `fills-library\<Package>\*.cs`
still exists on disk** (see rule 7a above) before assuming the port itself needs redoing -- it
may just need copying back in (or, if it's genuinely gone, restoring it via
`git checkout -- fills-library` if it was ever committed).
