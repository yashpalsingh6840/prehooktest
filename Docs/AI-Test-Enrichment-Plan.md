# AI-written unit test enrichment: what README.md should carry, and a new `apply-tests` command

## Context

Every package in the tracked portfolio is now buildable and green
(`Docs/Generated-Tests-Plan.md`'s own 4 phases, closed 2026-09-06). The next question the user
raised is a genuinely different one from anything that plan covers: once a package is already
green, can AI be pointed at it again to **add more unit tests** -- raise coverage past the
deterministic starter baseline -- using `generate/<Package>/README.md` as the context source
instead of the raw generated `.cs` files, to keep this cheap in a GitHub-Copilot-only, token-
constrained client environment. Three concrete sub-questions were asked:

1. Can `README.md` be fed to AI and asked to add more unit tests?
2. Can the generated markdown be enriched further to make that read cheaper?
3. Should there be a new command, `apply-unit-test`-shaped, mirroring `apply-fills`?

**Answer to all three: yes**, with one correction to how the second question was framed and one
firm boundary on how this must NOT be wired, both explained below.

### Why this is a genuinely different activity from everything `gaps.json` already covers

Every existing AI touchpoint in this tool -- `SCRIPT-COLUMN`, `SCRIPT-TASK`, `TEST-ORACLE`,
`LOCAL-DATA`, `ENCRYPTED-SECRET` -- exists because the deterministic generator is **missing
information it needs to proceed at all**: a package won't build, or won't pass its own baseline
suite, without that answer. That is why they're gated through `GapKind`/`GapTier`, block the
"generatable" count when appropriate, and get a work packet with a stable `GapId`.

"Add more unit tests to an already-green package" is not that. Nothing is missing -- the package
compiles, the deterministic starter suite passes, `Category=Integration` (when pointed at a real
server) passes too. This is voluntary enrichment of an already-complete artifact. It must **never**
look like a gap: it must never appear in `gaps.json`, never affect `PortfolioDigest`'s
"generatable" count, and never block anything. Get this wrong and the readiness metric this whole
tool exists to protect (see `CLAUDE.md`'s own "Portfolio survey now reports generation readiness"
section) starts lying by omission the same way the `.Notification`/SqlCommand-advisory gaps once
did before they were fixed.

## Decision 1 -- one `README.md` per package, not a second one under `.Tests/`

**Extend the existing `generate/<Package>/README.md`. Do not create
`generate/<Package>.Tests/README.md` and combine the two at read time.**

Reasoning:
- The entire point of feeding a README instead of raw code is handing AI **one** compact, complete
  picture. Two files means every consumer -- a human, `/ssisx-rewrite`, this new workflow -- opens
  both and merges them mentally. That is the token/complexity cost this design exists to avoid, not
  a neutral split.
- The `.Tests` project has no identity independent of the package it tests. A standalone
  `.Tests/README.md` would either duplicate the package's own data-flow table (now two places to
  keep in sync -- exactly the drift class `PackageReadmeEmitter`'s own doc comment already warns
  about for Seq numbers) or read as a bare coverage table with no context for what each method
  does.
- There is already direct precedent for exactly this extension, not a new pattern: `README.md`'s
  existing "Control flow" table (`PackageReadmeEmitter.cs:70`) already carries a **Test** column
  per Seq-numbered method (`FindTest`, `PackageReadmeEmitter.cs:162`), populated from the exact
  same `TestFiles` list `PackageGenerator` already assembles. Today that column only answers "does
  a test file exist" (a filename or `_none_`). This plan deepens that column -- same table, same
  Seq numbering, no new file.

## Decision 2 -- this is NOT a gap; no new `GapKind`, no `gaps.json` entry, ever

Reinforcing the framing above with the concrete mechanical consequence: nothing in this plan adds
a `GapKind` value, writes to `gaps.json`, or is read by `PortfolioDigest.IsBlockingGap`/
`GenerationGapKinds`. The new artifacts this plan adds (the enriched README section, the new fill
folder, the new command) exist entirely alongside the gap/fill machinery, never inside it.

## Decision 3 -- correcting the "enrich the markdown" question

The user's second question conflated two different things worth separating explicitly:

- **A live coverage PERCENTAGE** (line/branch %, per package) -- this already exists,
  fully built and verified (`Tools/SsisExtractor/scripts/Run-Coverage.ps1`,
  `coverage-report.md`). It requires actually *running* the test suite with
  `--collect:"XPlat Code Coverage"`. This plan does not touch it and does not duplicate it.
- **A static, generate-time inventory of what EACH existing test asserts, and what obviously isn't
  exercised yet** -- this does NOT exist today. `README.md`'s "Test" column says a file exists; it
  says nothing about whether that file's own assertions are a bare `.Name` check, a boundary case,
  or a full happy-path run. This is the actual gap AI needs closed before it can usefully add tests
  without either duplicating existing coverage or guessing at what's missing by re-reading every
  `.cs` file top to bottom -- which is the exact cost this whole exercise is meant to avoid.

This plan builds the second one. It is cheap because every fact it needs is already known at
`ssisx generate` time -- no test run required, no coverage tool involved.

## Design

### 1. A `TestCoverageNote` per generated test file

A new, small record, carried alongside a `GeneratedFile` from the exact point each test emitter
already constructs one -- each emitter already knows precisely what it wrote, so this is a
one-line addition per call site, not new analysis:

```csharp
public sealed record TestCoverageNote(
    string MethodName,       // matches ComponentMethodEntry.MethodName, same join key FindTest already uses
    string Kind,             // "NameOnly" | "HappyPath" | "BoundaryCase" | "Integration" | "StatementText" | ...
    string Summary);         // one short sentence, e.g. "asserts BuildStatement() text only, no execution"
```

Threaded exactly like `testFiles` is today: a new `List<TestCoverageNote> testCoverageNotes` in
`PackageGenerator.Generate`, appended alongside every one of the ~20 existing
`testFiles.Add(...)` call sites (`ComponentTestEmitter.Emit*`, `RunAsyncFailureTestEmitter.Emit`,
`TransformTestEmitter.Emit`, `SqlStatementTestEmitter.Emit*`, the Merge Join mapper test, etc.).
This is real, mechanical, multi-site work -- honestly sized at ~20 call sites, not a one-line
change -- but each addition is a hardcoded one-line string literal per emitter, not new logic; no
emitter needs to introspect its own generated text.

**Verified, not assumed:** `TestCoverageNote.MethodName` can safely join against
`ComponentMethodEntry.MethodName` (`PackageClassEmitter.cs:46`,
`record ComponentMethodEntry(string Kind, string SsisName, string MethodName, string Signature, string? RowTypeName = null)`)
because that field is populated from the exact same local variable used to emit the method body
at each call site (e.g. `PackageClassEmitter.cs:409/432/587/646`) -- never re-derived, so there is
no risk of the two lists drifting apart on a name collision, the same guarantee
`PackageReadmeEmitter.FindTest` already relies on today.

Each emitter's own `Summary` is authored once, by hand, to state plainly what its starter test
does and does NOT cover -- e.g.:
- Execute SQL Task statement test: *"asserts the built SQL text only; never executes it."*
- Transform test: *"asserts each output column for one representative row; does not exercise
  NULL/boundary inputs beyond what `RepresentativeLiteral`/`RepresentativeOracleValue` already
  choose."*
- Router (Conditional Split) test: *"asserts one case per branch plus the default, using a
  single representative literal per condition; does not test values straddling a numeric
  boundary unless the condition itself is `>`/`<` against that exact literal."*
- Source (Excel/SQL) test: *"asserts `.Name` only by default; the real read is
  `[Trait("Category","Integration")]`, run only against a reachable server."*
- `RunAsync` happy path: *"asserts step count/order and per-branch transactions for the wired
  steps; does not vary input data beyond what the fixed sample file/rows contain."*

### 2. `README.md`'s "Control flow" table gets a richer Test column, plus one new subsection

`PackageReadmeEmitter.FindTest` changes from "filename or `_none_`" to rendering the matched
`TestCoverageNote`'s `Kind` + `Summary` alongside the filename it already shows -- still one cell,
still one row per Seq, no new table. Example rendered cell:

> `SyntheticRemergeTargetHighTransformTests.cs` -- HappyPath: asserts each output column for one
> representative row; does not exercise NULL/boundary inputs.

A genuinely uncovered method (today's `_none_`) is left exactly as `_none_` -- already the
strongest, cheapest signal AI needs ("nothing here at all yet"), requiring no new work.

New, small, unconditional subsection (mirrors the existing "Data flow sources and sinks" and
"Gaps reported" sections' own always-render-when-nonempty pattern):

```
## Test coverage notes

Read this before adding a test -- it says what each existing test already asserts, so a new test
adds real coverage instead of duplicating what's here. Additional tests go under
`fills/<Package>/MoreTests/` and are applied with `ssisx apply-tests` -- see
`HOW-TO-FILL-GAPS.md` at the `--out` root. This is NOT a gap: the package is already fully
generatable without it.

| Seq | Method | Kind | What's already asserted |
|---|---|---|---|
```

(re-using the exact same Seq numbers as the "Control flow" table above it, so a row here and a
row there are the same method by construction -- no second numbering scheme.)

### 3. Fill folder: `fills[-library]/<Package>/MoreTests/*.cs` -- deliberately NOT `Tests/`

`fills/<Package>/Tests/*.cs` already exists and means something specific: a `TEST-ORACLE` gap
answer, matched against `gaps.json` by its own leading `// ssisx-fill: GapId=...` comment
(`ApplyFillsCommand.ExtractLeadingProvenance`, matched against `knownOracles`). Reusing that
folder for a non-gap-driven "more tests" file would make `apply-fills` try to resolve a `GapId`
that does not exist and report a false `Orphaned`. A new, sibling folder,
`fills[-library]/<Package>/MoreTests/`, keeps the two mechanisms from ever colliding, by
construction, not by convention someone has to remember.

**Provenance comment, for audit only, never for matching:**

```
// ssisx-more-test: Author=<name-or-email> Date=<yyyy-mm-dd> Targets=<Class>.<Method>
```

Unlike a Tier-2 seam or a `TEST-ORACLE` fill, there is no `GapId` to check this against and no
staleness hash is possible -- stated as plainly here as `LocalFileSourceData`'s own doc comment
states the identical limitation for `TestData/` files. The self-check this design leans on
instead is real and arguably *stronger*: a "more test" file references the actual generated
row/entity/transform types by name, so if the package is regenerated and a referenced method or
column is renamed or removed, the stale test fails to **compile** -- an immediate, unambiguous
signal, not a silent drift the way a Tier-1 datum could go stale unnoticed.

### 4. `ssisx apply-tests --out <dir> [--fills <dir>]` -- a new, separate command

Mirrors `apply-fills`'s shape closely enough to need no new mental model, deliberately narrower.

**Verified, not assumed:** `Program.cs` has no CLI-parsing library at all -- it's a plain `switch`
on `args[0]` (e.g. `"apply-fills" => ApplyFillsCommand.Run(rest),`), so adding this command is one
new arm (`"apply-tests" => ApplyTestsCommand.Run(rest),`) plus one new `--help` block, mirroring
`ApplyFillsCommand`'s own `public static int Run(string[] args)` entry point exactly. And
`TestProjectEmitter.cs`'s emitted `.csproj` has **no `<Compile Include>` at all** -- plain
SDK-style implicit glob -- so `MoreTests/*.cs` is picked up by `dotnet build`/`dotnet test`
automatically the moment the files exist; `apply-tests` never needs to touch the `.csproj`.

- Copies every `fills[-library]/<Package>/MoreTests/*.cs` into
  `generate/<Package>.Tests/MoreTests/` (a new, clearly-separate subfolder inside the existing
  test project -- distinguishable at a glance from the deterministic starter tests, and trivially
  deletable independent of them).
- No `GapId` lookup, no staleness check (none is possible, per above) -- every file present is
  copied, unconditionally. A file missing the `// ssisx-more-test:` comment is still copied
  (best-effort, matching `Unattributed`'s own precedent) but flagged in the report so a human
  notices an undocumented addition.
- Writes its own **separate** manifest, `tests-applied.json` (NOT folded into
  `fills-applied.json`) -- keeping the gap-workflow audit trail (which gates readiness) and the
  enrichment audit trail (which never does) visibly, structurally distinct.
- **Never touches `gaps.json`, `GapKind`, or `PortfolioDigest`.** This is the one invariant this
  whole plan cannot compromise on -- see Decision 2.
- Nice-to-have, not required for v1: a cheap `class \w+` regex scan warning (not blocking) if a
  new file's own type name collides with something already in `{Package}.Tests`'s existing
  namespace -- the real, foreseeable failure mode being a `CS0101` duplicate-type build error.
  The prompt-level instruction below (name the class distinctly, e.g. a `MoreTests` suffix)
  covers this cheaply enough that the regex check can be deferred.

### 5. A new, separate, optional prompt: `/ssisx-more-tests`

**Not folded into `/ssisx-rewrite`.** That pipeline's whole contract is "close every gap, reach a
green, buildable state" -- a fixed, bounded amount of work with two named AI-boundary stops. This
activity only makes sense *after* that's already true, has no natural stopping point (there is
always one more test that could be added), and is explicitly optional per package. Folding it in
would blur `/ssisx-rewrite`'s own "done" signal.

`Tools/.github/prompts/ssisx-more-tests.prompt.md`, mirroring `ssisx-fill.prompt.md`'s shape:

1. Read `generate/<Package>/README.md`'s new "Test coverage notes" section only -- explicitly
   NOT the whole package source, which is the entire point.
2. Pick a small, named set of candidates (rows marked `_none_`, or a `Kind` whose own `Summary`
   states a real, named gap -- e.g. "does not exercise NULL/boundary inputs").
3. For each, open only the ONE file it targets (named directly in the README row) to see the real
   signature/types -- still far cheaper than scanning the whole `.Tests` project.
4. Write one `.cs` file per candidate (or a small batch) under
   `fills-library/<Package>/MoreTests/`, each carrying its own `// ssisx-more-test:` comment.
5. **Stop and report** before running `apply-tests` -- same discipline as every other AI boundary
   in this tool: a human confirms before it's wired in, even though nothing here is a "gap"
   requiring `ConfirmedBy` sign-off the way a Tier-1 decision does. This is a style choice, not a
   correctness gate (there is no risk of silently-wrong generated code the way a bad Lookup key
   would be) -- kept anyway because reviewing a handful of new test files before they land is
   cheap and this project's whole habit is not skipping the review step just because the risk is
   lower.
6. Run `ssisx apply-tests`, then `dotnet build` + `dotnet test` on the `.Tests` project, report
   pass/fail plainly.

## Verification plan

Run against one real, already-green package (`Package_Legacy` or `Package_Advanced` -- both
already fully generatable with all Tier-1 gaps resolved per this session's own work), not a
synthetic fixture, since the whole point is proving the token-economy claim on a real README:

1. Regenerate that package with the enriched `PackageReadmeEmitter` and confirm the new "Test
   coverage notes" section renders correctly -- cross-check every row's `Kind`/`Summary` against
   the actual test file's own assertions by hand, at least once, to confirm the hardcoded
   per-emitter summaries are honest and not just plausible-sounding.
2. **Prove the token-savings claim, not just assert it**: have AI attempt the SAME "add two more
   tests" task twice -- once given only the README's new section, once given the whole `.Tests`
   folder to explore cold -- and compare tokens actually spent and whether the README-only run
   produced non-duplicate, correctly-targeted tests. This is the one part of the whole plan that
   is a claim rather than a mechanical fact until measured.
3. Write a real `MoreTests/*.cs` file (by hand, if AI isn't run for this step) targeting a
   genuine `_none_` row, run `ssisx apply-tests`, confirm `tests-applied.json` reports it Applied
   and `gaps.json`/`generate-report.md`/`PortfolioDigest` output are byte-identical to before
   (proving Decision 2's invariant holds in practice, not just in design).
4. Confirm the self-check property: deliberately rename a method the new test references,
   regenerate, re-run `dotnet build` on `.Tests`, and confirm it fails to compile with a clear
   error rather than silently passing stale assertions.
5. Full existing suite (`Ssis.Extract.Codegen.Tests` et al.) stays green -- the `TestCoverageNote`
   threading touches ~20 call sites in `PackageGenerator.cs`; every one needs its own golden-file
   diff reviewed by eye (this project's standing rule) to confirm nothing else moved.

## Explicitly out of scope

- Any change to `GapKind`, `GapTier`, `gaps.json`, or `PortfolioDigest`.
- A live coverage percentage inside `README.md` -- `Run-Coverage.ps1`/`coverage-report.md` already
  own that, unchanged.
- Any staleness/hash check for a `MoreTests` fill -- stated as impossible up front, not deferred
  as a TODO.
- Folding this into `/ssisx-rewrite` or into `apply-fills`.
- Automatically deleting or pruning a `MoreTests` file that no longer compiles -- a build failure
  there is a signal for a human to fix or remove it, not something `ssisx` cleans up unasked
  (consistent with rule 14 in `Tools/.github/copilot-instructions.md`: never delete fills/
  TestData/generated output without being told to).
