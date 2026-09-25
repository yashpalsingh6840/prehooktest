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
   to **run the CLI** (`ssisx extract`, `ssisx generate`, `ssisx apply-fills`, ...) and to
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
   The most you should do is `dotnet build` the generated solution to confirm it compiles, and
   `dotnet test --filter Category!=Integration` (see rule 10 below) to confirm the generated
   tests pass with no server reachable. Never invent a connection string, or a database, to make
   something "runnable" -- that is out of scope here. **The one deliberate exception:** a
   `LocalFileSourceData` work packet is explicitly asking you to supply a realistic SAMPLE FILE
   under `fills-library\<Package>\TestData\<exact name from the packet>` -- that is not "inventing
   input to fake a run," it is the one gap kind whose entire job is exactly that; do it when asked.
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
8a. **Do not report a CLI step as complete from intent alone.** Capture `$LASTEXITCODE` (or
the equivalent process exit code), print it, and verify the command's expected output directory
or file exists before summarizing success. If PowerShell is left at a continuation prompt or
the output was not captured, rerun the command in a fresh non-interactive invocation. For
multi-step read-only workflows, verify each step separately before moving to the next one.
9. **Budget your own reads.** Do not open `CLAUDE.md`, anything under `Docs\` (e.g.
   `Docs\Emitter-Rewrite-Plan.md`, `Docs\Phase0-Extractor-Plan.md`,
   `Docs\Migration-Validation-Plan.md`, `Docs\SsisValidationKit-Plan.md`,
   `Docs\Generated-Tests-Plan.md`, `Docs\AI-Test-Enrichment-Plan.md`), or
   `SsisExtractor\docs\report-schema.md` -- they are this
   project's own multi-hundred-KB development history and internal design plans, not written for
   you, and reading them will burn your context for no benefit. None of this exists at all in a
   client-only copy of this `Tools\` folder -- it's only reachable here because this is the full
   dev repo. Everything you need to operate this tool is in `COPILOT_GUIDE.md` and
   `ssisx --help`. If a command's own `--help` output and the guide disagree, trust `--help` --
   it is generated from the same code that runs.
10. **Before writing or reviewing a generated test, read `<Package>\README.md` first** -- never
    re-derive the component-to-method map from the `.dtsx` or by reading `<Package>.cs` top to
    bottom; the README's own component table is generated fresh every run specifically so you
    never have to. A generated test needing something no fake can provide (a real database, a
    real `.xlsx` file, a real secondary-connection server) carries
    `[Trait("Category", "Integration")]` -- the always-green baseline for everything else is
    `dotnet test --filter Category!=Integration`, which should pass with zero fills applied right
    after a fresh `ssisx generate`.
11. **A `TEST-ORACLE` work packet is self-contained -- do not open `TestDoubles\PackageHarness.cs`
    or `COPILOT_TESTING_GUIDE.md` to confirm its pinned testing-API block.** The packet already
    embeds everything you need (the same block, copied verbatim). Reading those files anyway
    burns tokens for no benefit -- they exist for the rarer case the packet's own template
    doesn't cover.
12. **`svk` is a separate, standalone, optional tool -- never a gate.** `SsisValidationKit\`
    ships alongside `ssisx` (built by the same one-time setup, see `COPILOT_GUIDE.md`'s "Invoking
    ssisx" section) but only has two commands, `svk walkthrough` and `svk sampledata`, both
    deterministic and read-only against `ssisx extract`'s own output. It never edits anything
    under `SsisExtractor\`, never generates C#, and its own review docs/claims files are advisory
    -- a package with no walkthrough/sampledata run against it generates and builds exactly the
    same as one that does. Do not invent a third `svk` command or assume it participates in
    `generate`/`apply-fills` in any way.
13. **`.github/prompts/ssisx-rewrite.prompt.md` (`/ssisx-rewrite`) sequences the whole pipeline
    for one package, and it is DESIGNED to stop twice for a human** -- once after writing Tier-1/2
    fills, once after writing test-oracle/local-data fills. Do not "helpfully" continue past
    either stop on your own judgement, even if every fill you wrote looks obviously correct: an
    AI's own confidence is never sign-off, and continuing anyway defeats the one property this
    whole gap taxonomy exists to protect. Re-running it for the same package is always safe -- it
    detects what already exists on disk and resumes rather than redoing finished work.
14. **Never delete any TestData, fill, or generated output -- ever -- unless explicitly told
    to.** This applies to a client's own supplied real data just as much as to this tool's own
    disposable `<out>/generate/` tree. If a genuinely clean run is needed, that is its own
    explicit request from the human -- never something to do on your own judgement as part of
    "running the tool" or "cleaning up."
15. **`.github/prompts/ssisx-rewrite.prompt.md` (`/ssisx-rewrite`) is the STANDARD thing to run
    whenever asked to "run the tool" / "generate this package" / "process this folder", in THIS
    chat session** -- it now covers one package, a named few, or a whole folder (leave `package`
    blank), through extract -> `svk` walkthrough/sampledata -> generate -> Tier-1/2 fills (AI
    boundary) -> apply+build -> test-oracle/local-data fills (AI boundary) -> apply+build+test ->
    code coverage. It is agent-driven ON PURPOSE: you take each step as your own tool call, so you
    can actually read a gap's own work packet and draft a fill, then stop for the human's
    confirmation -- something no plain script can do. `Tools/SsisExtractor/scripts/
    Run-Pipeline.ps1` runs the SAME 5 mechanical steps as a single script call, but ONLY for when
    no Claude/Copilot chat session is available at all (CI, an ops person with no AI session) --
    it can apply fills already on disk, never write one, so do not reach for it here. Coverage
    (`Run-Coverage.ps1`, a purely mechanical sub-step even within `/ssisx-rewrite`) is measured on
    the package's OWN generated code separately from the shared `Etl.Core` library -- see
    `Tools/COPILOT_TESTING_GUIDE.md`'s coverage section for why the blended total reads lower and
    is not the number to quote.
16. **`ssisx apply-tests` (and `/ssisx-more-tests`) are a separate, optional, NOT-a-gap workflow
    -- never confuse them with `apply-fills`.** Only run this once a package already builds clean
    and passes `dotnet test --filter Category!=Integration` with zero fills applied; it raises
    coverage past the deterministic starter tests, never affects `gaps.json`/the "generatable"
    count, and reads only `generate\<Package>\README.md`'s own "Test coverage notes" section --
    never the whole `.Tests` project. Its own file convention
    (`fills[-library]\<Package>\MoreTests\*.cs`) is deliberately a different folder from
    `apply-fills`' own `Tests\`/`TestData\` -- do not write a "more test" into either of those, and
    do not run `apply-tests` as part of `/ssisx-rewrite` (that pipeline's own "done" signal is
    about closing gaps, not about this).

17. **Read work packets and run build/test commands economically -- this directly affects how
    much of a task fits in your own context/token budget, not just politeness.**
    - A packet's own "SSIS semantics you must preserve" and "Respond with" sections are
      IDENTICAL boilerplate repeated verbatim across every packet from the same run (rule 11
      already says this for `TEST-ORACLE`; it applies to every packet kind). Never open/read a
      work packet whole -- extract only its "Evidence"/"What you need to produce" sections,
      e.g. `sed -n '/## Evidence/,/## Respond with/p' out/gaps/<Package>/<file>.md`, or a
      targeted file read using an offset/limit that skips the boilerplate block.
    - Resolve `$ssisx`/`$dotnet` to a BUILT exe once per task (the bootstrap block in
      `ssisx-extract.prompt.md` already does this) and reuse that same path for every later
      command -- `apply-fills`, `build`, `test`. Never `dotnet run --project ...` for the CLI
      on a repeat call; that pays a full restore/build check every single invocation for no
      benefit once a built exe already exists.
    - Before writing a fill that references a generated type, grep that generated file's own
      `namespace`/class declaration first rather than guessing the `using` it needs -- a wrong
      guess is only caught by a full build, costing an entire extra solution build+test cycle
      to fix and re-verify.
    - Pipe every `dotnet build`/`dotnet test` through `--nologo -v minimal` and read only the
      final outcome line (`Build succeeded`/`Build FAILED`, the test pass/fail summary) plus
      any `error`/`FAILED` lines -- never tail or dump the full restore/compile transcript into
      the conversation.

18. **Exit code 98 or 99 from `ssisx`/`svk` means a genuine bug in the TOOL itself, not a problem
    with this client's packages.** Do not try to fix, patch, or work around it yourself (this
    repeats rule 1 for a different failure shape). Both codes write a compact
    `ssisx-diagnostic-<timestamp>.txt` (or `svk-diagnostic-...`) to the `--out` directory (or the
    current folder) -- point the user at that exact file and tell them plainly: it contains no
    package/column/file names or other client data, only the exception type and a stack trace
    filtered to this tool's own source, so it is safe to screenshot and share back for the tool to
    be fixed. Read the file yourself first if you can (it is short) so you can summarize what it
    says, but never try to guess a fix from it.

19. **`generate`/`apply-fills` each also write a durable log** -- `<out>/generate.log` and
    `<out>/apply-fills.log`, overwritten fresh every run. Read one of these BEFORE assuming a
    run "did nothing" or asking the user to re-run with more output -- they already contain
    everything that scrolled by live, plus (for `apply-fills`) a full per-file trace: every
    `fills/<Package>/` folder and file scanned, every seam pattern match attempted, and an
    explicit note when a file has no recognizable `partial` method/class at all. Both logs are
    safe to read/share -- they contain the same package/column/file names already visible in
    `generate-report.md`/`fills-applied.json`, nothing more sensitive.

## The one-sentence workflow

**Survey once (cheap, whole portfolio, read-only) -> generate one package (or a named few) at
a time -> read its gaps -> either wait for a human decision/fill, or move to the next package.**

For the common case -- one package or a folder, start to finish, with nothing deleted along the
way -- reach for `/ssisx-rewrite` (rule 15) instead of the manual command sequence below; it runs
every step as your own tool calls, stopping at the two AI boundaries for a human's confirmation.
The sequence below is the detailed, step-by-step reference for when finer control is needed.

`ssisx` is not on PATH -- build it once (`cd SsisExtractor && dotnet build SsisExtractor.slnx -c
Debug && cd ..`, from this `Tools\` folder), then call the built exe directly:
`SsisExtractor\src\Ssis.Extract.Cli\bin\Debug\net8.0\ssisx.exe`. Full detail in
`COPILOT_GUIDE.md`'s own "Invoking ssisx" section -- read that before running anything if this
isn't already clear.

```powershell
$ssisx = "SsisExtractor\src\Ssis.Extract.Cli\bin\Debug\net8.0\ssisx.exe"

# 1. Survey the whole portfolio once -- lists every package name, complexity, and how many
#    would generate cleanly today. Safe to run against everything; writes nothing but reports.
& $ssisx extract --input <client-folder> --out out --recursive

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
- **Tier 1 (`MissingDatum`)** -- one workflow, but check the `GapId`'s own prefix (or the gap's
  `Kind` in `gaps.json`) before writing anything: the three sub-shapes below answer to
  DIFFERENT places and formats, and are not interchangeable.
  - **`LOOKUP-JOIN-KEY:...` / `ENCRYPTED-SECRET:...`** -- a fact is missing from the `.dtsx`
    (e.g. an unresolvable Lookup join key) that a human must confirm. Read the work packet under
    `out\gaps\<Package>\`, answer its exact question in `fills-library\<Package>.decisions.json`
    (NOT `out\fills\...` -- see rule 7a) in the format the packet shows, with a real
    `ConfirmedBy`. **Do not fabricate a confident-sounding answer yourself** -- if you are not
    certain, present the packet's question to the human instead of guessing at a value that
    looks plausible.
  - **`TEST-ORACLE:...`** -- the deterministic emitter could not itself derive a test assertion
    (a Conditional Split case, or a test for a Script Task/Component seam once filled). The
    packet asks for a WHOLE new xUnit test file (its own pinned testing-API block is embedded in
    the packet -- see rule 11). Write it under `fills-library\<Package>\Tests\<Name>Tests.cs`,
    with a `// ssisx-fill: GapId=... Author=... Date=... EvidenceSha256=...` comment as the
    file's own FIRST line (there is no seam to place it above -- this is a brand-new file).
  - **`LOCAL-DATA:...`** -- a realistic sample file this source's own Integration-tagged read
    test actually needs (see rule 4's own exception) -- CSV/fixed-width/Excel alike, all resolve
    through the same `TestData/` folder; there is no separate synthetic fallback any of them read
    instead. The packet names the EXACT file name in its own "Save as" line -- use that name
    verbatim. Write it under `fills-library\<Package>\TestData\<that exact name>`, with NO
    comment of any kind (a data file has no provenance convention).
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
