---
mode: agent
description: Add more unit tests to an already-green, already-generatable package, reading only its README.md. NOT part of the gap-fill workflow -- see Docs/AI-Test-Enrichment-Plan.md.
---

# ssisx: add more unit tests to an already-green package

- **Package:** `${input:package:Package name, e.g. the folder name under out/generate/}`

**This is not a gap-fill prompt.** The package this is pointed at must already build clean and
pass `dotnet test --filter Category!=Integration` with zero fills applied. Nothing here ever
touches `gaps.json`, `GapKind`, or the "generatable" count -- see this prompt's own description
and `Docs/AI-Test-Enrichment-Plan.md`'s Decision 2. If the package is not already green, run
`/ssisx-rewrite` (or the individual `/ssisx-generate` + `/ssisx-fill` steps) first.

## Step 0 -- check for existing more-tests FIRST

List `fills-library/<package>/MoreTests/`. If files already exist, **read and verify them
against the package's current README.md -- never overwrite them.** Prior, already-reviewed work
may be sitting there.

## Step 1 -- read ONLY the README's "Test coverage notes" section

Open `out/generate/<package>/README.md` and read its **"Test coverage notes"** section only --
**do not open any generated `.cs` file, and do not scan the whole `.Tests` project.** That
section already says, per existing test, what it asserts and (by what it does not say) what it
doesn't -- re-reading the actual test source would duplicate work the README exists to save.

Pick a small, named set of candidates:

- A row in the package's own **"Control flow"** table (just above) whose Test column reads
  `_none_` -- genuinely zero coverage.
- A "Test coverage notes" row whose own summary states a real, named limitation (e.g. "does not
  exercise NULL/boundary inputs", "asserts `.Name` only", "a single representative literal per
  condition").

Two or three candidates is plenty for one pass -- this has no natural stopping point, so don't
try to exhaustively cover everything at once.

## Step 2 -- open only what each candidate names

For each candidate, open **only the one file the README row itself names** (the transform/
router/statement class under `generate/<package>/Mapping|Sql|...`) to see its real signature and
column/branch shapes. This is still far cheaper than reading the whole `.Tests` project cold.

## Step 3 -- write the tests

**File convention:** one file per class or small batch, under
`fills-library/<package>/MoreTests/<Name>MoreTests.cs` -- **not** `fills-library/<package>/Tests/`
(that folder belongs to `apply-fills`' own `TEST-ORACLE` mechanism and is matched against
`gaps.json`; a file dropped there for this purpose would be reported `Orphaned`).

Put a provenance comment as the file's own first line, for audit only -- there is no `GapId` to
check it against and no staleness is possible, so this is never used to decide whether the file
is applied, only to record who wrote it:

```
// ssisx-more-test: Author=<name-or-email> Date=<yyyy-mm-dd> Targets=<Class>.<Method>
```

Name each test class distinctly (a `MoreTests` suffix on the file's own type, not reusing an
existing generated test class name) to avoid a `CS0101` duplicate-type collision with the
deterministic starter tests already in the same project.

Generated projects build with `Nullable=enable` **and `TreatWarningsAsErrors=true`** -- an unused
`using` or an unreachable warning fails the build here, same as everywhere else in this tool.

If a real limitation genuinely can't be tested against the abstractions available (e.g. it
needs a real server the `PackageHarness` fake doesn't provide), **say so plainly** -- tag the
test `[Trait("Category", "Integration")]` rather than inventing a fake that doesn't prove
anything.

## Step 4 -- stop and report before applying

**Stop here and report what you wrote before running `apply-tests`.** This is a style choice,
not a correctness gate -- there is no risk of silently-wrong generated code the way a bad Lookup
key would be -- but reviewing a handful of new test files before they land is cheap, and this
tool's whole habit is not skipping the review step just because the risk here is lower.

## Step 5 -- apply, build, and test

Once confirmed, resolve `$ssisx`/`$dotnet` via the bootstrap block in `ssisx-extract.prompt.md`
(Step 1), then:

```powershell
& $ssisx apply-tests --out out --fills fills-library
& $dotnet build out/generate/Generated.slnx --nologo
& $dotnet test out/generate/Generated.slnx --filter Category!=Integration --nologo
```

`apply-tests` copies every file unconditionally -- there is no `Stale`/`Orphaned` concept here
(see the command's own `--help` text). If a test references a method/column that got renamed or
removed since the README was read, the build will fail with an ordinary compile error naming
exactly what's wrong -- that failure IS the staleness check for this feature; fix or remove the
test rather than treating it as a tool bug.

## Step 6 -- report

- Confirm the build result and the `dotnet test` summary (passed/failed/skipped).
- Say plainly which README rows now have stronger coverage and which limitations, if any, were
  left untested and why.
- Remind me to `git add fills-library && git commit` -- an uncommitted test can still be lost.

## Logging

Append to `session-logs/<yyyy-MM-dd>-session.md`: this prompt, the commands, their output, and a
bump of the counters.
