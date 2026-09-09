# Tools

Everything a client site needs to extract, assess, and generate C# from its own SSIS
packages -- self-contained, with no dependency on any other repo. Three things live here, for
three different reasons:

| Folder | What it is | Why it's here |
|---|---|---|
| [`SsisExtractor/`](SsisExtractor/README.md) | `ssisx` -- the extractor/report/conformance/generator CLI (its own solution, `SsisExtractor.slnx`) | The tool itself: parses raw `.dtsx` XML (no GAC, no SSISDB, no live SSIS install needed) and turns it into inventory, findings, conformance rules, and generated C#. |
| `Etl.Core/` | The hand-written runtime library every `ssisx generate` output targets (`SqlBulkSink`, `CsvRowSource`, `ExecuteSqlStep`/`DataFlowStep`/every other step type, the notification/hosting plumbing, etc.) | `ssisx generate` deliberately does not produce this -- it's written once, not derived from any `.dtsx` (see `GenerateCommand.WriteFixedFiles`'s own doc comment). A generated package needs a copy of it alongside its own project to build at all. |
| [`SsisValidationKit/`](SsisValidationKit/README.md) | `svk` -- a **separate, standalone** review/sample-data tool (its own solution, `SsisValidationKit.slnx`) that only ever *reads* `ssisx extract`'s output; it never edits anything under `SsisExtractor/` and never generates C# | Two commands: `svk walkthrough` (a human-readable execution-order review doc + a claims file per package) and `svk sampledata` (deterministic, schema-correct sample CSVs/SQL/DDL). Both optional, both read-only against `ssisx`'s own spec output -- see "Reviewing before/alongside generation with `svk`" below. |

## Project map -- what each project/file actually does

Full narrative detail lives in each tool's own README
([`SsisExtractor/README.md`](SsisExtractor/README.md),
[`SsisValidationKit/README.md`](SsisValidationKit/README.md)) and, for `ssisx`, its `docs/`
folder (`spec-schema.md`/`report-schema.md`/`conformance-schema.md`/`gate2-schema.md`). This
section is a quick reference for orienting inside either solution -- what each `.csproj` is for
and its handful of most important files.

### `ssisx` (`SsisExtractor/`, `SsisExtractor.slnx`)

Reads raw `.dtsx`/`.dtproj`/`.ispac` XML directly (no SSIS install, no GAC, no SSISDB on the
main path) and turns it into inventory, findings, conformance obligations, generated tests, and
generated C#. Nine projects, roughly in dependency order:

| Project | Purpose | Key files |
|---|---|---|
| `Ssis.Extract.Model` | Plain-POCO data model (the "spec" shape) every other project reads or writes -- no I/O, no logic. | `Package/PackageSpec.cs` (per-package spec root), `Pipeline/PipelineSpec.cs` (a Data Flow Task's full component/path model), `Analysis/ConformanceSpec.cs` (gate-1 obligation shape), `Analysis/GapSpec.cs` (Tier 1/2/3 gap taxonomy), `Serialization/StableJsonWriter.cs` (deterministic, sorted-key JSON), `Shared/SsisPipelineTypeMap.cs` (SSIS buffer-type -> CLR-type lookup). |
| `Ssis.Extract.Dtsx` | Reads raw XML into `PackageSpec`/`PipelineSpec` and runs every derived/cross-package analysis -- the extraction engine itself. | `DtsxPackageReader.cs` (main entry: one `.dtsx` -> one `PackageSpec`), `PipelineReader.cs` (parses `<pipeline>` into `PipelineSpec`, incl. bespoke payloads for ~20 component types), `LineageBuilder.cs` (column-level lineage via lineageId, not `<path>` walking), `RulesEngine.cs` (23 findings rules), `ConformanceRulesBuilder.cs`/`ConformanceChecker.cs` (gate-1 rule generation + claim reconciliation), `SqlHarvester.cs`, `DependencyGraphBuilder.cs`, `PackageDiffer.cs` (semantic diff), `IspacReader.cs`/`DtprojReader.cs`/`ProjectParamsReader.cs`. |
| `Ssis.Extract.Sql` | Parses harvested SQL text with Microsoft's real T-SQL parser, kept isolated so the core readers stay dependency-free. | `SqlAnalyzer.cs` (the whole project -- `TSql170Parser`, flags TRUNCATE/DELETE/MERGE/dynamic SQL, read/write disambiguation). |
| `Ssis.Runtime.Expressions` | Standalone parser + evaluator for the SSIS expression language, implementing MEASURED (not documented) semantics -- gate 2's foundation. | `SsisExpression.cs` (public parse+evaluate entry point), `Lexer.cs`/`Parser.cs` (tokenizer + recursive-descent parser -> `Ast.cs` nodes), `Evaluator.cs` (tree-walking evaluator, no implicit coercion, three-valued NULL logic), `Functions.cs` (built-in functions: `SUBSTRING`/`FINDSTRING`/`TRIM`/`DATEDIFF`/etc.), `SsisValue.cs`/`SsisType.cs`. |
| `Ssis.Expression.Oracle` (net48, Windows-only) | Ground-truth generator: evaluates a fixed 130+-expression corpus through the REAL SSIS evaluator and writes a TSV fixture `Ssis.Runtime.Expressions.Tests` asserts against. | `Program.cs` (the whole project -- one isolated `Application`/`Package`/`Variable` per expression, `EvaluateAsExpression=true`). |
| `Ssis.Extract.ObjectModel` (net48, Windows-only) | A second, independent way to gain confidence: loads a package via the real SSIS object model and cross-checks structural counts against the XML-derived spec. | `PackageOracle.cs` (walks executables/connections/variables/pipeline via `Application.LoadPackage`), `PackageOracleFacts.cs` (the comparison DTO). |
| `Ssis.Extract.Codegen` | The largest project: plans and emits the generated C# rewrite -- package classes, transforms, entities, tests, sample data, and AI work packets. | `PackagePlanner.cs` (resolves each Data Flow Task into a `DataFlowPlan`: source/transform/destination/gaps), `PackageGenerator.cs` (top-level per-package orchestrator, ~2800 lines, unit-testable independent of file I/O), `PackageClassEmitter.cs` (emits `{Package}.cs`, one method per SSIS task/component, real concurrency for parallel SSIS branches), `ExpressionTranslator.cs` (SSIS expression AST -> C# text, or a named `NotTranslatable` gap), `TransformEmitter.cs`/`EntityEmitter.cs`/`RouterEmitter.cs`/`MergeJoinEmitter.cs`/`AggregateRowEmitter.cs`/`LookupCacheEmitter.cs` (per-component-shape emitters), `AiPacketEmitter.cs` (gap -> self-contained markdown work packet), `GapDecisions.cs` (applies only human-confirmed Tier-1 decisions), `ComponentTestEmitter.cs`/`TransformTestEmitter.cs`/`SqlStatementTestEmitter.cs`/`TestProjectEmitter.cs` (starter xUnit tests per component), `SampleDataEmitter.cs`/`SqlRowEmitter.cs`/`CsvRowEmitter.cs`/`ExcelRowEmitter.cs`/`FixedWidthRowReaderEmitter.cs` (per-source-type read/sample code). |
| `Ssis.Extract.FixtureBuilder` (net48, Windows-only) | Dev-only console tool: builds synthetic `.dtsx` test fixtures via the real SSIS object model (not hand-written XML), for shapes no real tracked package demonstrates. | `Program.cs` (the whole project -- dozens of named fixture modes, e.g. `SyntheticParallelShapes.dtsx`, `SyntheticLookupSplit.dtsx`). |
| `Ssis.Extract.Cli` | `ssisx.exe` itself -- thin command shells over the libraries above, handling arg parsing, file I/O, and console/markdown/JSON rendering. | `Program.cs` (dispatches `extract`/`graph`/`report`/`conformance`/`testgen`/`diff`/`generate`/`apply-fills`/`apply-tests`), `PackageLoader.cs` (shared `--input`/`--package` resolver for every command), `ExtractCommand.cs`, `GenerateCommand.cs` (thin shell over `PackageGenerator`, plus `--etl-core`/`--fills`/`--framework`/`--skip-tests` wiring), `ApplyFillsCommand.cs` (copies hand-authored fills in, reports what's outstanding), `ConformanceCommand.cs`, `GraphCommand.cs`, `TestGenCommand.cs`, `DiffCommand.cs`, `ReportCommand.cs`, `PortfolioDigest.cs` (the client-transferable, sanitized digest), `Rendering/MermaidRenderer.cs`/`DotRenderer.cs`. |

`tests/` mirrors this layout one-for-one (`Ssis.Extract.Tests`, `Ssis.Runtime.Expressions.Tests`,
`Ssis.Extract.Cli.Tests`, `Ssis.Extract.Codegen.Tests`, `Ssis.Extract.ObjectModel.Tests`) --
see `SsisExtractor/README.md`'s own "Test" section for what's golden-file-pinned vs. synthetic.

### `svk` (`SsisValidationKit/`, its own `SsisValidationKit.slnx`, also referenced from `SsisExtractor.slnx`)

A separate, standalone, **read-only** review/sample-data tool -- it only ever reads `ssisx
extract`'s own spec output, never edits anything under `SsisExtractor/`, and never generates C#.
Two projects:

| Project | Purpose | Key files |
|---|---|---|
| `SvkCore` | Builds human-readable walkthroughs and synthetic sample data from an already-extracted `PackageSpec`. Only allowed to reference `Ssis.Extract.Model`/`Ssis.Extract.Dtsx` -- never `Ssis.Extract.Codegen`/`Ssis.Extract.Cli` (a pinned governance boundary, checked by a doc comment on its own `ProjectReference`). | `WalkthroughBuilder.cs` (renders one package's execution-order review markdown, incl. per-component lineage + linked conformance rule ID), `ExecutionOrderBuilder.cs` (walks the control-flow tree in real SSIS execution order, flagging parallel branches), `SampleDataPlanner.cs` (discovers every source/destination/Lookup by scanning pipeline payloads), `SampleDataWriter.cs` (writes CSV/fixed-width/`.xlsx`/SQL/schema/Lookup-reference sample data), `ValueSynthesizer.cs` (deterministic, seeded, type-correct synthetic values), `SharedKeyPool.cs` (keeps a Lookup's reference table and main-flow rows in the intended ~70/30 match/no-match split), `ControlFlowDiagramBuilder.cs`/`DataFlowDiagramBuilder.cs` (Mermaid diagrams), `SchemaEmitter.cs` (runnable `CREATE TABLE`, PK lines marked `INFERRED`). |
| `SvkCli` | `svk.exe` -- thin command shell over `SvkCore`. | `Program.cs` (dispatches `sampledata`/`walkthrough`), `WalkthroughCommand.cs`, `SampleDataCommand.cs`, `CliArgs.cs`. |

See `SsisValidationKit/README.md`'s "Known limitations, stated rather than hidden" section
before relying on `svk`'s output for anything beyond a quick sanity read.

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

**This one build produces both tools.** `SvkCore`/`SvkCli` (the `svk` sources under
`SsisValidationKit\`) are added as project entries inside `SsisExtractor.slnx` too -- a
`.csproj` can belong to more than one solution file with zero physical move, so
`SsisValidationKit\` stays exactly where it is and its own `SsisValidationKit.slnx` stays valid
for anyone iterating on `svk` in isolation. There is no second build step to remember.

The built CLI is now at `SsisExtractor\src\Ssis.Extract.Cli\bin\Debug\net8.0\ssisx.exe`, and
`svk` at `SsisValidationKit\src\SvkCli\bin\Debug\net8.0\svk.exe`, both relative
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

**Raising coverage past the starter tests, once a package is already green, is a separate,
optional, NOT-a-gap workflow** -- `ssisx apply-tests` reads a hand-written test under
`fills-library\<Package>\MoreTests\*.cs` (never `...\Tests\`, which is `apply-fills`' own
`TEST-ORACLE` mechanism) and copies it in unconditionally; it never touches `gaps.json` or the
"generatable" count. See `COPILOT_GUIDE.md`'s "Adding more unit tests to an already-green
package" section, or `Docs/AI-Test-Enrichment-Plan.md` for the full design.

**Generated code is one class per package, one method per SSIS task/component (not a flat
script), and real concurrency where SSIS itself ran things concurrently -- each concurrent
branch gets its own transaction, so rollback there is per-branch, not whole-package.** See
`COPILOT_GUIDE.md`'s "What the generated code actually looks like" for the full explanation
before reviewing or editing generated output.

**`generate`'s job ends at writing buildable C# source.** There is no client database or real
source data available in this environment, so nothing here builds, runs, or verifies the
generated code against real behavior -- that comparison is a separate, dev-only concern (see
`Validation/` at this repo's own root, which is *not* shipped here) with its own captured data
corpus this environment doesn't have. Building (`dotnet build`, step 3 above) to confirm the
code compiles is the expected extent of checking; running it against invented sample data is
not the goal and should not be attempted.

## Reviewing before/alongside generation with `svk`

`svk` (`SsisValidationKit\`) is a **separate, standalone** tool from `ssisx` -- its own
`SsisValidationKit.slnx`, built by the same one-time setup above. It only ever *reads* what
`ssisx extract` already wrote; it never edits anything under `SsisExtractor\` and never
generates C#. Two commands, both deterministic (same input/`--seed` -> byte-identical output,
nothing here is an AI step):

```powershell
$svk = "SsisValidationKit\src\SvkCli\bin\Debug\net8.0\svk.exe"

# A human-readable execution-order review doc per package, plus a claims file to record
# "I checked this component" -- reads ssisx extract's own --out folder, never SSISDB/a live DB.
& $svk walkthrough --spec out --out out\walkthrough --claims out\walkthrough\claims --package LoadEmployees

# Deterministic sample CSV/SQL/DDL, type/length-correct against the package's own declared
# schema -- useful before generation for a quick sanity read, and as the seed a human/AI then
# makes realistic for a LOCAL-DATA gap (see COPILOT_TESTING_GUIDE.md).
& $svk sampledata --spec out --out out\sampledata --package LoadEmployees --rows 20 --seed 42
```

Full command reference, output layout, and known limitations: `SsisValidationKit\README.md`.

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

**The standard pipeline for "run the tool" -- one package, a named few, or a whole folder, in a
chat session:** `.github/prompts/ssisx-rewrite.prompt.md` (`/ssisx-rewrite` in Copilot Chat)
sequences every step below -- extract, an optional `svk` walkthrough, generate, apply-fills,
build, test, and a code-coverage report -- stopping at each of the two places a human decision is
actually required (Tier-1/2 gaps, then test-oracle/local-data gaps) rather than guessing past
them. It is agent-driven on purpose: each step is the agent's own tool call, so it can actually
read a gap's own work packet and draft a fill, then stop for a human's confirmation -- something
a plain script cannot do. Each step is still independently runnable via its own prompt if you'd
rather go one at a time:

| # | Step | Prompt |
|---|---|---|
| 1 | Extract + survey | `ssisx-extract.prompt.md` |
| 2 | Walkthrough (advisory) | `ssisx-walkthrough.prompt.md` |
| 3 | Generate | `ssisx-generate.prompt.md` |
| 4 | Tier-1/2 gap fills -- **AI, stops for review** | `ssisx-fill.prompt.md` |
| 5 | Apply + build | `ssisx-fill.prompt.md` (its own closing step) |
| 6 | Test-oracle/local-data fills -- **AI, stops for review** | `ssisx-test.prompt.md` |
| 7 | Apply + build + test | `ssisx-test.prompt.md` (its own closing step) |
| 8 | Code coverage | `SsisExtractor/scripts/Run-Coverage.ps1`, called directly |

Re-running `/ssisx-rewrite` is always safe -- it detects what already exists on disk (an
extracted spec, a generated project, filled-in fills) per package and resumes from there rather
than redoing finished work. **Never deletes any TestData, fill, or generated output along the
way** -- a human may want to review or hand-supply data at any point.

**No Claude/Copilot chat session available at all?** `SsisExtractor/scripts/Run-Pipeline.ps1`
runs the same 5 mechanical steps (everything except the two AI-fill steps) as one script call --
CI, a scripted regression check, an ops person with no AI session at hand. It can apply fills
already on disk, but it can never write one (a script has no way to read a work packet and
reason about an answer); if a chat session is available, use `/ssisx-rewrite` instead:

```powershell
.\SsisExtractor\scripts\Run-Pipeline.ps1 -InputPath <client-folder> -OutputPath out -Framework net10.0
```

`Etl.Core/` here is a **portable copy** -- see this project's own internal dev notes for where
it's actually developed and how to refresh it; that's not this file's concern, since this
folder is meant to be handed to a client as-is.

## What's deliberately not here

The gate-3 golden-corpus comparison harness lives at the repo root's own `Validation/` folder,
not here -- it's a dev-time tool that proves *this project's own* generator output matches
*this project's own* real SSIS runs, tied to a corpus captured from a specific development
box's SSISDB. A client doesn't run it; it's not part of what this folder exists to ship.
